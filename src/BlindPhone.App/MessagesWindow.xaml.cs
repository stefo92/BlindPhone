using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using BlindPhone.Infrastructure;

namespace BlindPhone.App;

/// <summary>Una riga dell'elenco delle conversazioni.</summary>
/// <param name="Peer">Interno del collega.</param>
/// <param name="Name">Nome del collega, se noto.</param>
/// <param name="Who">Chi, a schermo.</param>
/// <param name="UnreadText">Messaggi da leggere, a schermo; vuoto se non ce ne sono.</param>
/// <param name="Last">Ultimo messaggio, a schermo.</param>
/// <param name="AccessibleText">La riga come la legge lo screen reader.</param>
public sealed record ConversationRow(
    string Peer,
    string? Name,
    string Who,
    string UnreadText,
    string Last,
    string AccessibleText)
{
    /// <summary>Evita che lo screen reader, senza un nome, legga il record per intero.</summary>
    public override string ToString() => AccessibleText;
}

/// <summary>
/// L'elenco delle conversazioni a messaggi, e il punto da cui se ne apre una nuova.
/// </summary>
/// <remarks>
/// <para>
/// <b>Si apre sull'elenco se c'è qualcosa da leggere, sul campo altrimenti.</b> Chi
/// preme il tasto dei messaggi con un messaggio in attesa vuole leggerlo, e lo trova
/// sotto il cursore; chi lo preme a elenco vuoto vuole scrivere a qualcuno, e il campo è
/// già pronto.
/// </para>
/// <para>
/// <b>L'elenco si aggiorna da sé</b> mentre è aperto — un messaggio nuovo porta la sua
/// conversazione in cima — senza togliere la selezione a chi lo sta scorrendo.
/// </para>
/// </remarks>
public partial class MessagesWindow : Window
{
    private readonly InstantMessagingService _messaging;
    private readonly FinestreDeiMessaggi _windows;
    private readonly IContactRepository _contacts;
    private readonly IAnnouncer _announcer;
    private readonly HotkeyProfile _hotkeys;
    private readonly SettingsService _settings;

    private readonly ObservableCollection<ConversationRow> _rows = [];

    internal MessagesWindow(
        InstantMessagingService messaging,
        FinestreDeiMessaggi windows,
        IContactRepository contacts,
        IAnnouncer announcer,
        HotkeyProfile hotkeys,
        SettingsService settings)
    {
        _messaging = messaging;
        _windows = windows;
        _contacts = contacts;
        _announcer = announcer;
        _hotkeys = hotkeys;
        _settings = settings;

        InitializeComponent();

        ConversationsList.ItemsSource = _rows;
        Refresh();

        MenuDellInterno.Collega(
            ConversationsList,
            () => ConversationsList.SelectedItem is ConversationRow r ? new Interno(r.Peer, r.Name) : null,
            interno => _windows.ChiamaDallaFinestraAsync(interno.Number),
            interno => _windows.Show(interno.Number, interno.Name),
            tastoChiama: null,
            () => "Invio");

        _messaging.ConversationsChanged += OnConversationsChanged;
        Closed += (_, _) => _messaging.ConversationsChanged -= OnConversationsChanged;

        Loaded += (_, _) =>
        {
            if (_rows.Count > 0 && _messaging.UnreadTotal > 0)
            {
                ConversationsList.SelectedIndex = 0;
                ConversationsList.UpdateLayout();

                if (ConversationsList.ItemContainerGenerator.ContainerFromIndex(0) is System.Windows.Controls.ListViewItem voce)
                {
                    voce.Focus();
                    return;
                }
            }

            NewPeerBox.Focus();
        };
    }

    // -----------------------------------------------------------------------
    // Conversazione nuova
    // -----------------------------------------------------------------------

    private async void OnNewPeerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await OpenTypedAsync().ConfigureAwait(true);
    }

    private async void OnNewConversationClick(object sender, RoutedEventArgs e)
        => await OpenTypedAsync().ConfigureAwait(true);

    /// <summary>
    /// Apre la conversazione con quello che è scritto nel campo: un interno, o un nome.
    /// </summary>
    /// <remarks>
    /// Un nome si cerca in rubrica e si accetta solo se porta a una persona sola: con due
    /// «Rossi» scegliere il primo vorrebbe dire scrivere, forse, alla persona sbagliata.
    /// In quel caso si dice quanti sono e dove sceglierli.
    /// </remarks>
    private async Task OpenTypedAsync()
    {
        var scritto = NewPeerBox.Text.Trim();

        if (scritto.Length == 0)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Scrivi l'interno del collega, poi premi Invio", DateTimeOffset.Now));
            return;
        }

        if (!scritto.Any(char.IsLetter))
        {
            NewPeerBox.Clear();
            _windows.Show(scritto);
            return;
        }

        IReadOnlyList<Core.Domain.Contact> trovati;

        try
        {
            trovati = await _contacts.SearchAsync(scritto, 5, CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            trovati = [];
        }

        var conNumero = trovati
            .Where(c => !string.IsNullOrWhiteSpace(c.ExtensionNumber ?? c.PrimaryNumber))
            .ToList();

        switch (conNumero.Count)
        {
            case 0:
                _announcer.Announce(Announcement.CommandResponse(
                    $"Nessun contatto con un numero per {scritto}", DateTimeOffset.Now));
                break;

            case 1:
                var contatto = conNumero[0];
                NewPeerBox.Clear();
                _windows.Show(contatto.ExtensionNumber ?? contatto.PrimaryNumber!, contatto.NameOrDescription);
                break;

            default:
                _announcer.Announce(Announcement.CommandResponse(
                    $"{conNumero.Count} contatti per {scritto}: scrivi l'interno, oppure cerca in rubrica e usa il menu contestuale",
                    DateTimeOffset.Now));
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Elenco
    // -----------------------------------------------------------------------

    private void OnConversationsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        OpenSelected();
    }

    private void OnConversationsDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (ConversationsList.SelectedItem is ConversationRow riga) _windows.Show(riga.Peer, riga.Name);
    }

    private void OnConversationsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

    /// <summary>
    /// Riscrive l'elenco, tenendo la selezione sul collega su cui era.
    /// </summary>
    private void Refresh()
    {
        var selezionato = (ConversationsList.SelectedItem as ConversationRow)?.Peer;
        var avevaIlCursore = ConversationsList.IsKeyboardFocusWithin;
        var raggruppamento = _settings.Current.Accessibility.DigitGrouping;

        var righe = _messaging.Conversations
            .OrderByDescending(c => c.UnreadCount > 0)
            .ThenByDescending(c => c.LastActivity)
            .Select(c =>
            {
                var ultimo = c.Last;
                var nome = c.Name;

                return new ConversationRow(
                    c.Peer,
                    nome,
                    InstantMessageTexts.WhoShown(nome, c.Peer),
                    c.UnreadCount > 0 ? InstantMessageTexts.Unread(c.UnreadCount) : string.Empty,
                    ultimo is null
                        ? "Nessun messaggio"
                        : (ultimo.IsOutgoing ? "Tu: " : string.Empty) + InstantMessageTexts.Shorten(ultimo.Text, 120, "…"),
                    InstantMessageTexts.ConversationAccessible(nome, c.Peer, c.UnreadCount, ultimo, raggruppamento));
            })
            .ToList();

        if (!righe.SequenceEqual(_rows))
        {
            _rows.Clear();
            foreach (var riga in righe) _rows.Add(riga);
        }

        var unread = _messaging.UnreadTotal;
        ListHeaderText.Text = unread > 0
            ? $"Conversazioni: {_rows.Count}, {InstantMessageTexts.Unread(unread)}"
            : $"Conversazioni: {_rows.Count}";

        EmptyText.Text = InstantMessageTexts.NoConversations;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (selezionato is null) return;

        var daSelezionare = _rows.FirstOrDefault(r => r.Peer == selezionato);
        if (daSelezionare is null) return;

        ConversationsList.SelectedItem = daSelezionare;

        if (!avevaIlCursore) return;

        ConversationsList.UpdateLayout();
        if (ConversationsList.ItemContainerGenerator.ContainerFromItem(daSelezionare) is System.Windows.Controls.ListViewItem voce) voce.Focus();
    }

    // -----------------------------------------------------------------------
    // Finestra
    // -----------------------------------------------------------------------

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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
        catch
        {
            _announcer.Announce(Announcement.CommandResponse("Impossibile aprire la guida", DateTimeOffset.Now));
        }
    }
}
