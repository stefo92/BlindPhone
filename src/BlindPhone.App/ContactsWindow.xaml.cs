using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>Riga della tabella dei risultati di ricerca in rubrica.</summary>
/// <param name="Name">Nominativo.</param>
/// <param name="Number">Numero predefinito.</param>
/// <param name="Description">Descrizione della voce: che cosa e' e chi risponde.</param>
/// <param name="Department">Reparto o unita' operativa.</param>
/// <param name="Location">Collocazione fisica in forma breve.</param>
/// <param name="RelatedTo">Interno di riferimento a cui la voce fa capo.</param>
/// <param name="Presence">Stato di presenza dell'interno.</param>
/// <param name="Contact">Contatto completo, per le azioni.</param>
public sealed record ContactRow(
    string Name,
    string Number,
    string Description,
    string Department,
    string Location,
    string RelatedTo,
    string Presence,
    Contact Contact)
{
    /// <summary>
    /// Numero nella forma che lo screen reader legge cifra per cifra.
    /// </summary>
    /// <remarks>
    /// È il nome accessibile della sola cella del numero. Il contenuto letterale
    /// "4021" verrebbe pronunciato "quattromilaventuno", che è inutilizzabile per
    /// trascriverlo o ricomporlo; le cifre separate da spazi vengono lette una per
    /// una e sulla riga braille restano quattro caratteri.
    /// </remarks>
    public string SpokenNumber => SpeechFormatter.SpacedDigits(Number);

    /// <summary>Rappresentazione testuale breve della riga.</summary>
    /// <remarks>
    /// Non è un dettaglio estetico. Quando lo screen reader non trova un nome
    /// accessibile sulla riga ricade su questa stringa, e la resa predefinita di un
    /// <c>record</c> è l'elenco di tutte le proprietà — compreso il contatto
    /// annidato per intero. Sarebbe una lettura di decine di secondi al posto di un
    /// nome.
    /// </remarks>
    public override string ToString() => Name;
}

/// <summary>
/// Rubrica: ricerca e azioni sui contatti.
/// </summary>
/// <remarks>
/// <para>
/// Il modello di interazione è quello che un operatore non vedente usa davvero:
/// si apre, si digita, si scende con la freccia, si preme Invio. Non c'è un
/// pulsante "cerca", perché aggiungerebbe un passaggio a un'operazione che si
/// ripete centinaia di volte al giorno.
/// </para>
/// <para>
/// <b>Il ritardo di ricerca è una scelta di accessibilità.</b> Cercare a ogni
/// tasto premuto produrrebbe un annuncio del conteggio per ogni lettera, che si
/// sovrapporrebbe all'eco delle lettere stesse rendendo illeggibile ciò che si sta
/// digitando. Trecento millisecondi di pausa sono abbastanza per distinguere la
/// fine di una parola dalla battitura in corso.
/// </para>
/// </remarks>
public partial class ContactsWindow : Window
{
    /// <summary>Numero massimo di risultati mostrati.</summary>
    /// <remarks>
    /// Oltre questa soglia l'elenco smette di essere navigabile a orecchio: si
    /// annuncia il totale e si invita a restringere la ricerca, che è più veloce
    /// che scorrere duecento voci.
    /// </remarks>
    private const int MaxResults = 50;

    private readonly IContactRepository _contacts;
    private readonly CallSessionManager _calls;
    private readonly TransferOrchestrator _transfer;
    private readonly IAnnouncer _announcer;
    private readonly HotkeyProfile _hotkeys;
    private readonly PresenceMonitorService _presence;
    private readonly ILogger<ContactsWindow> _log;

    private readonly ObservableCollection<ContactRow> _results = [];
    private readonly DispatcherTimer _searchTimer;

    private CancellationTokenSource? _searchCancellation;

    /// <summary>Crea il pannello rubrica.</summary>
    /// <param name="contacts">Archivio dei contatti.</param>
    /// <param name="calls">Gestore delle chiamate.</param>
    /// <param name="transfer">Orchestratore del trasferimento.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="hotkeys">Profilo di scorciatoie attivo, per la guida in linea.</param>
    /// <param name="presence">Stato degli interni: libero, occupato, fuori servizio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public ContactsWindow(
        IContactRepository contacts,
        CallSessionManager calls,
        TransferOrchestrator transfer,
        IAnnouncer announcer,
        HotkeyProfile hotkeys,
        PresenceMonitorService presence,
        ILogger<ContactsWindow> logger)
    {
        _contacts = contacts;
        _calls = calls;
        _transfer = transfer;
        _announcer = announcer;
        _hotkeys = hotkeys;
        _presence = presence;
        _log = logger;

        // Lo stato di un interno cambia mentre l'operatore sta guardando l'elenco:
        // un elenco che mostra lo stato di dieci minuti prima e' peggio di uno che
        // non lo mostra affatto.
        _presence.PresenceChanged += OnPresenceChanged;
        Closed += (_, _) => _presence.PresenceChanged -= OnPresenceChanged;

        InitializeComponent();

        ResultsGrid.ItemsSource = _results;
        ResultsGrid.SelectionChanged += OnSelectionChanged;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await RunSearchAsync().ConfigureAwait(true);
        };

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Imposta il testo di ricerca iniziale, prima che la finestra si apra.
    /// </summary>
    /// <param name="query">Testo da cercare.</param>
    /// <remarks>
    /// Serve a non far perdere all'operatore quello che aveva già digitato nel
    /// campo di composizione prima di premere il tasto della rubrica.
    /// </remarks>
    public void SetInitialQuery(string query) => SearchBox.Text = query;

    /// <summary>
    /// Apre la guida in linea sull'argomento della rubrica.
    /// </summary>
    /// <remarks>
    /// La rubrica ha un modello di navigazione tutto suo — la tabella che si legge
    /// cella per cella — ed è il punto in cui F1 viene premuto più spesso da chi sta
    /// imparando.
    /// </remarks>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1) return;

        e.Handled = true;

        try
        {
            new HelpWindow(
                HelpBook.Build(_hotkeys, DateTimeOffset.Now), _announcer, HelpBook.ContactsTopicId)
            {
                Owner = this,
            }.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della guida non riuscita");
            _announcer.Announce(Announcement.CommandResponse("Impossibile aprire la guida", DateTimeOffset.Now));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();

        var total = await SafeCountAsync().ConfigureAwait(true);

        // L'annuncio di apertura dice due cose: quanti contatti ci sono, e cosa
        // fare adesso. Senza la seconda, l'operatore che apre la rubrica per la
        // prima volta non sa se deve digitare o navigare.
        _announcer.Announce(Announcement.CommandResponse(
            total == 0
                ? "Rubrica vuota. Importa un file dalle impostazioni"
                : $"Rubrica, {total} contatti. Digita per cercare, freccia giu' per scendere nei risultati",
            DateTimeOffset.Now));

        // Una colonna Stato sempre vuota sembra un guasto. Detto una volta
        // all'apertura, e' invece un limite noto: la presenza degli interni arriva
        // dall'API del centralino, e senza quel collegamento non esiste.
        if (total > 0 && !_presence.IsAvailable)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Lo stato degli interni non e' disponibile: richiede il collegamento all'API del centralino. "
                + "La colonna Stato restera' vuota",
                DateTimeOffset.Now));
        }

        // Se c'e' un trasferimento in corso lo si ricorda: la rubrica aperta in
        // quel contesto serve a scegliere la destinazione, non a chiamare.
        if (_transfer.Phase == TransferPhase.EnteringDestination)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Trasferimento in corso: F9 su un contatto lo usa come destinazione",
                DateTimeOffset.Now));
        }
    }

    // -----------------------------------------------------------------------
    // Ricerca
    // -----------------------------------------------------------------------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Ogni battitura riavvia il conto alla rovescia: la ricerca parte quando
        // l'operatore smette di digitare, non mentre digita.
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task RunSearchAsync()
    {
        var query = SearchBox.Text.Trim();

        if (query.Length < 2)
        {
            _results.Clear();
            LiveRegion.Update(ResultCountText, query.Length == 0
                ? string.Empty
                : "Digita almeno due caratteri");
            return;
        }

        // Una ricerca precedente ancora in corso viene annullata: su un archivio
        // grande, i risultati di una query obsoleta arriverebbero dopo quelli
        // buoni e sovrascriverebbero l'elenco con qualcosa che l'operatore non ha
        // piu' chiesto.
        if (_searchCancellation is not null) await _searchCancellation.CancelAsync().ConfigureAwait(true);

        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        try
        {
            var found = await _contacts.SearchAsync(query, MaxResults + 1, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;

            _results.Clear();

            foreach (var contact in found.Take(MaxResults))
            {
                _results.Add(ToRow(contact));
            }

            AnnounceResultCount(found.Count, query);

            // Si dichiara quali interni interessano adesso: senza API del centralino
            // lo stato arriva per sottoscrizione SIP, e ogni interno guardato ne costa
            // una. Quelli della ricerca precedente vengono lasciati andare.
            _ = _presence.WatchAsync(
                [.. _results.Select(r => r.Contact.ExtensionNumber ?? r.Contact.PrimaryNumber ?? string.Empty)],
                CancellationToken.None);

            // Si seleziona senza spostare il focus: l'operatore sta ancora
            // digitando, e portargli via il cursore dal campo di ricerca gli
            // farebbe finire le lettere successive dentro la tabella.
            if (_results.Count > 0) ResultsGrid.SelectedIndex = 0;
            else DetailText.Text = "Nessun contatto trovato.";
        }
        catch (OperationCanceledException)
        {
            // Ricerca superata da una piu' recente: nulla da fare.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ricerca in rubrica non riuscita");
            LiveRegion.Update(ResultCountText, "Ricerca non riuscita");
            _announcer.Announce(Announcement.CommandResponse("Ricerca non riuscita", DateTimeOffset.Now));
        }
    }

    /// <summary>
    /// Annuncia quanti risultati sono stati trovati.
    /// </summary>
    /// <remarks>
    /// Il conteggio è l'informazione che permette di decidere se scendere
    /// nell'elenco o restringere la ricerca, ed è invisibile a chi non vede finché
    /// non lo si dice. Quando c'è un solo risultato lo si nomina direttamente: è il
    /// caso più frequente e risparmia all'operatore un passaggio.
    /// </remarks>
    private void AnnounceResultCount(int found, string query)
    {
        string message;

        if (found == 0)
        {
            message = $"Nessun risultato per {query}";
        }
        else if (found == 1)
        {
            message = $"Un risultato: {_results[0].Contact.FullSpeechDescription}";
        }
        else if (found > MaxResults)
        {
            message = $"Piu' di {MaxResults} risultati, restringi la ricerca";
        }
        else
        {
            message = $"{found} risultati";
        }

        // Doppio canale, con scopi diversi: la regione dinamica lascia il testo a
        // schermo perche' l'operatore possa rileggerlo col cursore virtuale,
        // l'annuncio lo pronuncia subito anche se il focus e' altrove.
        LiveRegion.Update(ResultCountText, message);
        _announcer.Announce(Announcement.CommandResponse(message, DateTimeOffset.Now));
    }

    /// <summary>
    /// Aggiorna la riga dell'interno che ha cambiato stato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si ricostruisce la sola riga interessata, e solo se è fra i risultati che
    /// l'operatore ha davanti. Ricostruire l'elenco intero farebbe perdere il posto
    /// del cursore: chi sta percorrendo venti contatti si ritroverebbe riportato in
    /// cima ogni volta che un interno qualunque squilla.
    /// </para>
    /// <para>
    /// Il cambiamento non viene annunciato. L'operatore sta cercando un contatto, non
    /// sorvegliando il centralino: una voce che interrompe per dire che un interno
    /// estraneo si è liberato coprirebbe proprio la lettura in corso. Lo stato nuovo
    /// si sente arrivando sulla cella, che è il momento in cui serve.
    /// </para>
    /// </remarks>
    private void OnPresenceChanged(object? sender, PresenceChangedEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            for (var i = 0; i < _results.Count; i++)
            {
                var contact = _results[i].Contact;
                var numero = contact.ExtensionNumber ?? contact.PrimaryNumber;

                if (!SameExtension(numero, e.Extension)) continue;

                var selezionata = ResultsGrid.SelectedIndex == i;
                _results[i] = ToRow(contact);

                if (selezionata) ResultsGrid.SelectedIndex = i;
                return;
            }
        });

    /// <summary>Confronta due numeri guardando le sole cifre.</summary>
    private static bool SameExtension(string? left, string? right)
    {
        static string Cifre(string? value) => value is null ? string.Empty : new string([.. value.Where(char.IsAsciiDigit)]);

        var a = Cifre(left);
        return a.Length > 0 && a == Cifre(right);
    }

    private ContactRow ToRow(Contact contact) => new(
        contact.EffectiveDisplayName,
        contact.PrimaryNumber ?? string.Empty,
        contact.Category ?? string.Empty,
        contact.Department ?? string.Empty,
        contact.LocationSpeech ?? string.Empty,
        contact.RelatedTo ?? string.Empty,
        DescribePresence(contact),
        contact);

    /// <summary>
    /// Rende lo stato di presenza in forma testuale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lo stato viene dal servizio che ascolta il centralino, non dal campo salvato
    /// nell'archivio. Il campo salvato è vecchio per definizione: al primo avvio del
    /// mattino direbbe "libero" perché così era la sera prima, e l'operatore
    /// trasferirebbe una chiamata a un telefono spento fidandosi di un dato scritto.
    /// </para>
    /// <para>
    /// Testo e non colore: nessuna informazione va veicolata dal solo colore, e uno
    /// stato mostrato con un pallino verde sarebbe muto per lo screen reader e
    /// invisibile in contrasto elevato.
    /// </para>
    /// </remarks>
    private string DescribePresence(Contact contact)
    {
        var numero = contact.ExtensionNumber ?? contact.PrimaryNumber;
        return PresenceMonitorService.Describe(_presence.Get(numero));
    }

    // -----------------------------------------------------------------------
    // Navigazione
    // -----------------------------------------------------------------------

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        // Freccia giu' dal campo di ricerca scende nei risultati senza toccare il
        // mouse: e' il gesto centrale di tutta la finestra.
        if (e.Key == Key.Down && _results.Count > 0)
        {
            e.Handled = true;
            FocusResult(ResultsGrid.SelectedIndex < 0 ? 0 : ResultsGrid.SelectedIndex);
            return;
        }

        if (e.Key == Key.Enter && _results.Count > 0)
        {
            // Invio dal campo di ricerca chiama il primo risultato: con una ricerca
            // che ha prodotto una sola voce, e' cio' che l'operatore si aspetta.
            ResultsGrid.SelectedIndex = 0;
            e.Handled = true;
            _ = CallSelectedAsync();
        }
    }

    /// <summary>
    /// Comandi della tabella dei risultati.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si intercetta la fase di anteprima, non quella di risalita, perché la
    /// <see cref="DataGrid"/> tratta questi tasti prima di qualunque gestore
    /// registrato sull'evento normale: Invio sposta il cursore alla riga
    /// successiva, e la freccia su viene consumata anche quando non c'è nulla sopra.
    /// In anteprima i tasti arrivano qui per primi, e ciò che non serve prosegue.
    /// </para>
    /// <para>
    /// Le frecce orizzontali non compaiono: sono il movimento fra le celle, cioè la
    /// lettura campo per campo, e vanno lasciate alla tabella.
    /// </para>
    /// </remarks>
    private void OnResultsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                AnnounceAllNumbers();
                break;

            case Key.Enter:
                e.Handled = true;
                _ = CallSelectedAsync();
                break;

            case Key.F9:
                e.Handled = true;
                _ = UseAsTransferDestinationAsync();
                break;

            case Key.D when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                AnnounceFullDetails();
                break;

            case Key.Up when GridNavigation.CurrentRowIndex(ResultsGrid) == 0:
                // Dalla prima riga si torna al campo di ricerca: chiudere il ciclo
                // evita di dover cercare il Tab all'indietro.
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
        }
    }

    /// <summary>
    /// Seleziona una riga della tabella e le porta il focus della tastiera.
    /// </summary>
    /// <param name="index">Indice della riga.</param>
    private void FocusResult(int index) => GridNavigation.FocusCell(ResultsGrid, index);

    private void OnResultsDoubleClick(object sender, MouseButtonEventArgs e) => _ = CallSelectedAsync();

    /// <summary>
    /// Aggiorna la scheda del contatto selezionato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il testo si scrive senza notificare nulla, e la scheda non è più una regione
    /// dinamica. Notificarla farebbe pronunciare la descrizione completa del
    /// contatto a ogni pressione della freccia, subito dopo il contenuto della
    /// cella: sarebbe la lettura lunga che la navigazione per celle serve appunto a
    /// evitare.
    /// </para>
    /// <para>
    /// Resta a schermo perché l'operatore possa rileggerla col cursore virtuale
    /// senza perdere la posizione nella tabella, e Control D la pronuncia quando
    /// serve davvero.
    /// </para>
    /// </remarks>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var contact = SelectedContact();
        DetailText.Text = contact?.FullSpeechDescription ?? string.Empty;
    }

    // -----------------------------------------------------------------------
    // Azioni
    // -----------------------------------------------------------------------

    private Contact? SelectedContact() => (ResultsGrid.SelectedItem as ContactRow)?.Contact;

    private async void OnCallClick(object sender, RoutedEventArgs e) => await CallSelectedAsync().ConfigureAwait(true);

    private async void OnTransferClick(object sender, RoutedEventArgs e)
        => await UseAsTransferDestinationAsync().ConfigureAwait(true);

    private async Task CallSelectedAsync()
    {
        var contact = SelectedContact();

        if (contact?.PrimaryNumber is not { Length: > 0 } number)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Il contatto selezionato non ha un numero", DateTimeOffset.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return;
        }

        // Se un trasferimento e' in corso, chiamare creerebbe una terza linea e
        // confonderebbe lo stato: si usa il contatto come destinazione, che e'
        // quello che serve in quel momento.
        if (_transfer.Phase == TransferPhase.EnteringDestination)
        {
            await UseAsTransferDestinationAsync().ConfigureAwait(true);
            return;
        }

        // CON QUALCUNO IN LINEA INVIO NON FA NIENTE.
        //
        // Il controllo qui sopra copriva il trasferimento gia' avviato. Restava
        // scoperto il caso piu' comune: il chiamante in linea e nessun trasferimento
        // iniziato. Li' Invio componeva, e siccome comporre non mette in attesa
        // nessuno partiva una seconda chiamata in parallelo mentre il chiamante
        // restava in conversazione; per giunta questa finestra si chiudeva, quindi
        // l'operatore perdeva anche il contatto che stava guardando.
        //
        // Su questo elenco il tasto del trasferimento c'e' gia' ed e' F9: Invio non
        // serve a niente mentre si sta parlando con qualcuno, e non deve fare danni.
        // Fuori da una conversazione resta quello di sempre e chiama il contatto.
        //
        // La regola sta in CallSessionManager.IsTalking.
        if (_calls.IsTalking)
        {
            _log.LogDebug(
                "Chiamata dalla rubrica ignorata: c'e' una conversazione in corso. "
                + "Per passare il chiamante si usa F9");

            return;
        }

        try
        {
            await _calls.DialAsync(number).ConfigureAwait(true);
            Close();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chiamata dalla rubrica non riuscita");
            _announcer.Announce(Announcement.CommandResponse("Chiamata non riuscita", DateTimeOffset.Now));
        }
    }

    private async Task UseAsTransferDestinationAsync()
    {
        var contact = SelectedContact();

        if (contact?.PrimaryNumber is not { Length: > 0 } number)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Il contatto selezionato non ha un numero", DateTimeOffset.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return;
        }

        if (_transfer.Phase != TransferPhase.EnteringDestination)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Nessun trasferimento in corso", DateTimeOffset.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return;
        }

        await _transfer.SetDestinationAsync(number).ConfigureAwait(true);

        _announcer.Announce(Announcement.CommandResponse(
            $"Destinazione: {contact.SpeechName}, {SpeechFormatter.Digits(number)}. F9 per procedere",
            DateTimeOffset.Now));

        // Si chiude per riportare il focus alla finestra principale, dove il tasto
        // di trasferimento continua la procedura.
        Close();
    }

    /// <summary>
    /// Legge tutti i recapiti del contatto selezionato.
    /// </summary>
    /// <remarks>
    /// L'elenco compare come annuncio invece che come finestra di scelta: aprire
    /// un'altra finestra per due numeri costringerebbe a navigarla e chiuderla,
    /// mentre nella pratica l'operatore ascolta e ricompone.
    /// </remarks>
    private void AnnounceAllNumbers()
    {
        var contact = SelectedContact();
        if (contact is null) return;

        if (contact.Numbers.Count == 0)
        {
            _announcer.Announce(Announcement.CommandResponse("Nessun recapito", DateTimeOffset.Now));
            return;
        }

        var lines = contact.Numbers.Select(n =>
            $"{DescribeNumberKind(n.Kind)}: {SpeechFormatter.Digits(n.Number)}");

        _announcer.Announce(Announcement.CommandResponse(
            $"{contact.SpeechName}. {string.Join(". ", lines)}", DateTimeOffset.Now));
    }

    private static string DescribeNumberKind(PhoneNumberKind kind) => kind switch
    {
        PhoneNumberKind.Extension => "interno",
        PhoneNumberKind.Work => "ufficio",
        PhoneNumberKind.Mobile => "cellulare",
        PhoneNumberKind.Home => "casa",
        PhoneNumberKind.Fax => "fax",
        _ => "altro",
    };

    private void AnnounceFullDetails()
    {
        var contact = SelectedContact();
        if (contact is null) return;

        var parts = new List<string> { contact.FullSpeechDescription };

        if (contact.PrimaryNumber is { Length: > 0 } number)
            parts.Add($"numero {SpeechFormatter.Digits(number)}");

        if (DescribePresence(contact) is { Length: > 0 } stato) parts.Add(stato);

        if (contact.DoNotTransfer) parts.Add("non riceve trasferimenti diretti");
        if (contact.IsVip) parts.Add("contatto VIP");

        _announcer.Announce(Announcement.CommandResponse(string.Join(". ", parts), DateTimeOffset.Now));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task<int> SafeCountAsync()
    {
        try
        {
            return await _contacts.CountAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Conteggio dei contatti non riuscito");
            return 0;
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _searchTimer.Stop();
        _searchCancellation?.Dispose();
        ResultsGrid.SelectionChanged -= OnSelectionChanged;
        base.OnClosed(e);
    }
}
