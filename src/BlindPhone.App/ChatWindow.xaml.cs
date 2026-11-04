using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>Una riga dello storico della conversazione.</summary>
/// <param name="Message">Messaggio da mostrare.</param>
/// <param name="Who">Chi l'ha scritto, a schermo: «Tu» oppure il nome del collega.</param>
/// <param name="AccessibleText">La riga intera, come la legge lo screen reader.</param>
public sealed record ChatLine(InstantMessage Message, string Who, string AccessibleText)
{
    /// <summary>Testo del messaggio.</summary>
    public string Text => Message.Text;

    /// <summary>Ora del messaggio, a schermo.</summary>
    public string Time => Message.At.ToLocalTime().ToString("HH:mm");

    /// <summary>Se l'ha scritto l'operatore: decide il filo colorato a sinistra.</summary>
    public bool IsOutgoing => Message.IsOutgoing;

    /// <summary>Stato della consegna, vuoto quando è quello normale.</summary>
    public string Delivery => InstantMessageTexts.DeliveryShown(Message);

    /// <summary>La riga dello stato sparisce quando non ha niente da dire.</summary>
    public Visibility DeliveryVisibility => Delivery.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Evita che lo screen reader, senza un nome, legga il record per intero.</summary>
    public override string ToString() => AccessibleText;
}

/// <summary>
/// La finestra di una conversazione a messaggi con un collega.
/// </summary>
/// <remarks>
/// <para>
/// <b>Si apre per scrivere</b>, con il cursore nella casella del messaggio. Invio manda,
/// Maiusc Invio va a capo: è la convenzione di quasi ogni programma di messaggi, e chi
/// ne usa uno la porta con sé.
/// </para>
/// <para>
/// <b>Un messaggio in arrivo non sposta il cursore</b>, nemmeno qui: si aggiunge in fondo
/// allo storico e lo annuncia il servizio. Chi sta scrivendo la risposta continua a
/// scriverla; chi sta rileggendo un messaggio precedente resta su quello.
/// </para>
/// <para>
/// <b>Avere la finestra davanti vuol dire leggere.</b> Quando diventa attiva, i messaggi
/// della conversazione smettono di contare fra quelli da leggere: non c'è un comando
/// «segna come letto» da ricordare.
/// </para>
/// </remarks>
public partial class ChatWindow : Window
{
    private readonly InstantMessagingService _messaging;
    private readonly MessageConversation _conversation;
    private readonly IAnnouncer _announcer;
    private readonly HotkeyProfile _hotkeys;
    private readonly Func<DigitGrouping> _grouping;
    private readonly Func<string, Task> _dial;
    private readonly ILogger _log;

    private readonly ObservableCollection<ChatLine> _lines = [];

    /// <summary>Crea la finestra della conversazione.</summary>
    /// <param name="messaging">Servizio dei messaggi.</param>
    /// <param name="conversation">Conversazione da mostrare.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="hotkeys">Profilo dei tasti, per la guida.</param>
    /// <param name="grouping">Raggruppamento delle cifre, letto al momento.</param>
    /// <param name="dial">Come chiamare il collega.</param>
    /// <param name="logger">Log diagnostico.</param>
    internal ChatWindow(
        InstantMessagingService messaging,
        MessageConversation conversation,
        IAnnouncer announcer,
        HotkeyProfile hotkeys,
        Func<DigitGrouping> grouping,
        Func<string, Task> dial,
        ILogger logger)
    {
        _messaging = messaging;
        _conversation = conversation;
        _announcer = announcer;
        _hotkeys = hotkeys;
        _grouping = grouping;
        _dial = dial;
        _log = logger;

        InitializeComponent();

        HistoryList.ItemsSource = _lines;
        UpdateTitle();

        foreach (var messaggio in conversation.Messages) _lines.Add(Line(messaggio));

        _messaging.MessageAdded += OnMessageAdded;
        _messaging.MessageUpdated += OnMessageUpdated;
        _messaging.ConversationsChanged += OnConversationsChanged;

        Loaded += (_, _) =>
        {
            ScrollToEnd();
            FocusCompose();
        };

        Activated += (_, _) => _messaging.MarkRead(_conversation.Peer);

        Closed += (_, _) =>
        {
            _messaging.MessageAdded -= OnMessageAdded;
            _messaging.MessageUpdated -= OnMessageUpdated;
            _messaging.ConversationsChanged -= OnConversationsChanged;
        };
    }

    /// <summary>Interno del collega con cui si sta parlando.</summary>
    public string Peer => _conversation.Peer;

    /// <summary>Porta il cursore nella casella del messaggio.</summary>
    public void FocusCompose()
    {
        ComposeBox.Focus();
        Keyboard.Focus(ComposeBox);
    }

    // -----------------------------------------------------------------------
    // Scrittura
    // -----------------------------------------------------------------------

    /// <summary>
    /// Invio manda, Maiusc Invio va a capo.
    /// </summary>
    /// <remarks>
    /// La casella non accetta Invio da sé (<c>AcceptsReturn</c> è spento): se lo
    /// accettasse, Invio andrebbe a capo prima di arrivare qui. L'a capo con Maiusc si
    /// inserisce a mano, nel punto in cui si trova il cursore.
    /// </remarks>
    private async void OnComposePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;

        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ComposeBox.SelectedText = Environment.NewLine;
            ComposeBox.Select(ComposeBox.SelectionStart + ComposeBox.SelectionLength, 0);
            return;
        }

        await SendAsync().ConfigureAwait(true);
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        await SendAsync().ConfigureAwait(true);
        FocusCompose();
    }

    /// <summary>
    /// Manda il testo della casella, e la svuota subito.
    /// </summary>
    /// <remarks>
    /// La casella si svuota prima della risposta del centralino, non dopo: il messaggio
    /// è già nello storico come «invio in corso», e chi vuole scrivere il successivo non
    /// deve aspettare. Se la consegna non riesce, il testo resta nello storico con il
    /// motivo, e si può copiare da lì per riprovare.
    /// </remarks>
    private async Task SendAsync()
    {
        var testo = ComposeBox.Text;

        if (string.IsNullOrWhiteSpace(testo))
        {
            _announcer.Announce(Announcement.CommandResponse(InstantMessageTexts.EmptyMessage, DateTimeOffset.Now));
            return;
        }

        ComposeBox.Clear();

        try
        {
            await _messaging.SendAsync(_conversation.Peer, testo).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Invio del messaggio a {Interno} non riuscito", _conversation.Peer);
            _announcer.Announce(Announcement.CommandResponse(
                InstantMessageTexts.NotDelivered(InstantMessageTexts.NotSent), DateTimeOffset.Now));
        }
    }

    private async void OnCallClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _dial(_conversation.Peer).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chiamata dalla conversazione con {Interno} non riuscita", _conversation.Peer);
            _announcer.Announce(Announcement.CommandResponse("Chiamata non riuscita", DateTimeOffset.Now));
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // -----------------------------------------------------------------------
    // Storico
    // -----------------------------------------------------------------------

    /// <summary>Control C copia il messaggio selezionato, senza chi e quando.</summary>
    /// <remarks>
    /// Il caso tipico è un numero dettato in un messaggio — «richiama il 3491234567» — da
    /// incollare nel campo di composizione. Copiare la riga intera porterebbe con sé il
    /// nome e l'ora, da cancellare a mano.
    /// </remarks>
    private void OnHistoryPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (HistoryList.SelectedItem is not ChatLine riga) return;

        e.Handled = true;

        try
        {
            Clipboard.SetText(riga.Text);
            _announcer.Announce(Announcement.CommandResponse("Messaggio copiato", DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            // Gli appunti possono essere occupati da un altro programma per un istante.
            _log.LogWarning(ex, "Copia del messaggio negli appunti non riuscita");
            _announcer.Announce(Announcement.CommandResponse("Copia non riuscita", DateTimeOffset.Now));
        }
    }

    /// <summary>F1 apre la guida sull'argomento dei messaggi.</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1) return;

        e.Handled = true;

        try
        {
            FinestreNonModali.MostraOAttiva(
                () => new HelpWindow(
                    HelpBook.Build(_hotkeys, DateTimeOffset.Now), _announcer, HelpBook.MessagesTopicId),
                Owner ?? this,
                finestra => finestra.ShowTopic(HelpBook.MessagesTopicId));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della guida non riuscita");
            _announcer.Announce(Announcement.CommandResponse("Impossibile aprire la guida", DateTimeOffset.Now));
        }
    }

    // -----------------------------------------------------------------------
    // Aggiornamenti dal servizio
    // -----------------------------------------------------------------------

    private void OnMessageAdded(object? sender, InstantMessageEventArgs e)
    {
        if (!ReferenceEquals(e.Conversation, _conversation)) return;

        Dispatcher.BeginInvoke(() =>
        {
            // Lo storico scorre in fondo solo se il cursore non ci sta dentro: chi sta
            // rileggendo un messaggio precedente non deve vedersi spostare l'elenco sotto
            // gli occhi.
            var stavaLeggendo = HistoryList.IsKeyboardFocusWithin;

            _lines.Add(Line(e.Message));

            if (!stavaLeggendo) ScrollToEnd();
        });
    }

    private void OnMessageUpdated(object? sender, InstantMessageEventArgs e)
    {
        if (!ReferenceEquals(e.Conversation, _conversation)) return;

        Dispatcher.BeginInvoke(() =>
        {
            for (var i = _lines.Count - 1; i >= 0; i--)
            {
                if (_lines[i].Message.Id != e.Message.Id) continue;

                // Si sostituisce la riga e non la si rifà da capo: se il cursore c'era
                // sopra, la selezione si riporta sulla nuova.
                var selezionata = ReferenceEquals(HistoryList.SelectedItem, _lines[i]);
                _lines[i] = Line(e.Message);
                if (selezionata) HistoryList.SelectedIndex = i;

                break;
            }
        });
    }

    private void OnConversationsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() =>
        {
            // La rubrica puo' aver trovato il nome del collega dopo l'apertura: titolo e
            // righe gia' scritte si aggiornano, cosi' lo storico non resta intestato al
            // numero.
            var titolo = InstantMessageTexts.ChatTitle(_conversation.Name, _conversation.Peer);
            if (titolo == Title) return;

            UpdateTitle();

            for (var i = 0; i < _lines.Count; i++)
            {
                if (!_lines[i].IsOutgoing) _lines[i] = Line(_lines[i].Message);
            }
        });

    private ChatLine Line(InstantMessage messaggio)
    {
        var nome = _conversation.Name;
        var chi = messaggio.IsOutgoing ? "Tu" : InstantMessageTexts.WhoShown(nome, messaggio.Peer);

        return new ChatLine(messaggio, chi, InstantMessageTexts.Accessible(messaggio, nome, _grouping()));
    }

    private void UpdateTitle()
    {
        Title = InstantMessageTexts.ChatTitle(_conversation.Name, _conversation.Peer);
        HeaderText.Text = InstantMessageTexts.WhoShown(_conversation.Name, _conversation.Peer);
    }

    private void ScrollToEnd()
    {
        if (_lines.Count > 0) HistoryList.ScrollIntoView(_lines[^1]);
    }
}
