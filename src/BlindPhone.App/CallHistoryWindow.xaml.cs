using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>Una riga dell'elenco delle ultime chiamate.</summary>
/// <param name="When">Momento, in forma compatta.</param>
/// <param name="SpokenWhen">Momento in forma discorsiva, per la sintesi.</param>
/// <param name="Who">Nominativo, oppure il numero quando il nome non si conosce.</param>
/// <param name="Number">Numero della controparte.</param>
/// <param name="Duration">Durata della conversazione.</param>
/// <param name="Outcome">Esito, per le chiamate effettuate.</param>
/// <param name="Queue">Coda di provenienza.</param>
/// <param name="Entry">Voce di origine, per le azioni.</param>
public sealed record CallHistoryRow(
    string When,
    string SpokenWhen,
    string Who,
    string Number,
    string Duration,
    string Outcome,
    string Queue,
    CallHistoryEntry Entry)
{
    /// <summary>Numero nella forma che lo screen reader legge cifra per cifra.</summary>
    public string SpokenNumber => SpeechFormatter.SpacedDigits(Number);

    /// <inheritdoc />
    public override string ToString() => Who;
}

/// <summary>
/// Elenco delle ultime chiamate, diviso per tipo.
/// </summary>
/// <remarks>
/// <para>
/// Risponde alla domanda che un centralinista si fa più volte al giorno: «chi mi ha
/// cercato mentre ero occupato?». Per chi non vede non esiste l'icona lampeggiante che
/// la ricorda, quindi senza questo elenco una chiamata persa sparisce del tutto.
/// </para>
/// <para>
/// Le chiamate perse sono il <b>primo</b> pannello, non l'ultimo: sono la ragione
/// principale per cui questa finestra si apre, e trovarle senza cercarle è la
/// differenza fra un elenco utile e uno da consultare.
/// </para>
/// </remarks>
public partial class CallHistoryWindow : Window
{
    /// <summary>Quante voci si caricano per volta in un pannello.</summary>
    /// <remarks>
    /// <para>
    /// <b>Era un tetto, adesso è una pagina.</b> Cinquanta voci restano quante se ne
    /// mostrano all'apertura — oltre, scorrere un elenco a voce costa più che
    /// richiamare a memoria — ma le altre non sono più irraggiungibili: il pulsante
    /// «Mostra le precedenti» ne aggiunge altrettante, e il riepilogo dice sempre
    /// quante se ne stanno vedendo su quante ce ne sono.
    /// </para>
    /// <para>
    /// Il difetto che questo corregge non era nell'archivio, che ha sempre registrato
    /// tutto: era che l'elenco si fermava a cinquanta <b>senza dirlo</b>. Per chi non
    /// vede non c'è una barra di scorrimento che suggerisca che sotto c'è dell'altro,
    /// e un registro che smette di crescere sembra un registro che ha smesso di
    /// registrare. È esattamente la conclusione a cui si arriva, ed è sbagliata.
    /// </para>
    /// <para>
    /// Si carica a pagine e non tutto insieme perché queste tabelle non virtualizzano
    /// le righe, scelta presa per la stabilità con lo screen reader: con novanta
    /// giorni di conservazione su un centralino ospedaliero le voci sono migliaia, e
    /// costruirle tutte a ogni apertura si pagherebbe in attesa a ogni apertura.
    /// </para>
    /// </remarks>
    private const int PageSize = 50;

    /// <summary>Quante voci sono caricate adesso, per pannello.</summary>
    private readonly Dictionary<CallHistoryKind, int> _caricate = new()
    {
        [CallHistoryKind.Missed] = 0,
        [CallHistoryKind.Incoming] = 0,
        [CallHistoryKind.Outgoing] = 0,
    };

    /// <summary>Quante ce ne sono in archivio, per pannello.</summary>
    private readonly Dictionary<CallHistoryKind, int> _totali = new()
    {
        [CallHistoryKind.Missed] = 0,
        [CallHistoryKind.Incoming] = 0,
        [CallHistoryKind.Outgoing] = 0,
    };

    private readonly ICallHistoryRepository _history;
    private readonly CallSessionManager _calls;
    private readonly TransferOrchestrator _transfer;
    private readonly IAnnouncer _announcer;
    private readonly HotkeyProfile _hotkeys;
    private readonly ILogger<CallHistoryWindow> _log;

    private readonly ObservableCollection<CallHistoryRow> _missed = [];
    private readonly ObservableCollection<CallHistoryRow> _incoming = [];
    private readonly ObservableCollection<CallHistoryRow> _outgoing = [];

    /// <summary>Crea la finestra.</summary>
    /// <param name="history">Archivio delle chiamate.</param>
    /// <param name="calls">Gestore delle chiamate, per richiamare.</param>
    /// <param name="transfer">Orchestratore del trasferimento.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="hotkeys">Profilo di scorciatoie attivo, per la guida.</param>
    /// <param name="logger">Log diagnostico.</param>
    public CallHistoryWindow(
        ICallHistoryRepository history,
        CallSessionManager calls,
        TransferOrchestrator transfer,
        IAnnouncer announcer,
        HotkeyProfile hotkeys,
        ILogger<CallHistoryWindow> logger)
    {
        _history = history;
        _calls = calls;
        _transfer = transfer;
        _announcer = announcer;
        _hotkeys = hotkeys;
        _log = logger;

        InitializeComponent();

        MissedGrid.ItemsSource = _missed;
        IncomingGrid.ItemsSource = _incoming;
        OutgoingGrid.ItemsSource = _outgoing;

        Loaded += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    // -----------------------------------------------------------------------
    // Caricamento
    // -----------------------------------------------------------------------

    private async Task LoadAsync()
    {
        await FillAsync(CallHistoryKind.Missed, _missed).ConfigureAwait(true);
        await FillAsync(CallHistoryKind.Incoming, _incoming).ConfigureAwait(true);
        await FillAsync(CallHistoryKind.Outgoing, _outgoing).ConfigureAwait(true);

        UpdateSummary();
        UpdateShowMore();

        // Il pannello delle perse ha il fuoco all'apertura: e' quello per cui la
        // finestra si apre.
        MissedGrid.Focus();
        if (_missed.Count > 0) GridNavigation.FocusCell(MissedGrid, 0);

        var totale = _totali[CallHistoryKind.Missed];

        var perse = totale switch
        {
            0 => "nessuna chiamata persa",
            1 => "una chiamata persa",
            _ when totale > _missed.Count
                => $"{_missed.Count} chiamate perse mostrate su {totale}",
            _ => $"{totale} chiamate perse",
        };

        _announcer.Announce(Announcement.CommandResponse(
            $"Ultime chiamate: {perse}. Control Tab per passare a ricevute ed effettuate",
            DateTimeOffset.Now));
    }

    private Task FillAsync(CallHistoryKind kind, ObservableCollection<CallHistoryRow> target)
        => FillAsync(kind, target, PageSize);

    /// <summary>
    /// Ricarica un pannello con il numero di voci indicato, dalla più recente.
    /// </summary>
    /// <remarks>
    /// Si rilegge dall'inizio invece di chiedere solo il blocco successivo: nel
    /// frattempo possono essere arrivate altre chiamate, e un blocco chiesto a partire
    /// da una posizione salterebbe proprio quelle appena registrate.
    /// </remarks>
    private async Task FillAsync(CallHistoryKind kind, ObservableCollection<CallHistoryRow> target, int quante)
    {
        try
        {
            var entries = await _history.GetAsync(kind, quante, CancellationToken.None).ConfigureAwait(true);
            var totale = await _history.CountAsync(kind, CancellationToken.None).ConfigureAwait(true);

            target.Clear();
            foreach (var entry in entries) target.Add(ToRow(entry));

            _caricate[kind] = target.Count;
            _totali[kind] = totale;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lettura dell'elenco {Kind} non riuscita", kind);
        }
    }

    /// <summary>Elenco che alimenta un pannello.</summary>
    private ObservableCollection<CallHistoryRow> RowsFor(CallHistoryKind kind) => kind switch
    {
        CallHistoryKind.Incoming => _incoming,
        CallHistoryKind.Outgoing => _outgoing,
        _ => _missed,
    };

    /// <summary>Indica se nel pannello aperto ci sono voci non ancora caricate.</summary>
    private bool CeNeSonoAltre => _totali[CurrentKind] > _caricate[CurrentKind];

    private async void OnShowMoreClick(object sender, RoutedEventArgs e)
    {
        var kind = CurrentKind;

        if (!CeNeSonoAltre)
        {
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            Report("Sono gia' mostrate tutte");
            return;
        }

        var griglia = CurrentGrid;
        var posizione = griglia.SelectedIndex;

        await FillAsync(kind, RowsFor(kind), _caricate[kind] + PageSize).ConfigureAwait(true);

        UpdateSummary();
        UpdateShowMore();

        // Il cursore resta dov'era: le voci si aggiungono in fondo, e riportarlo in
        // cima costringerebbe a riscendere ogni volta.
        if (posizione >= 0 && posizione < griglia.Items.Count)
        {
            GridNavigation.FocusCell(griglia, posizione);
        }

        Report(_caricate[kind] >= _totali[kind]
            ? $"Mostrate tutte: {_caricate[kind]}"
            : $"Adesso {_caricate[kind]} di {_totali[kind]}");
    }

    private void UpdateShowMore()
    {
        if (ShowMoreButton is null) return;

        ShowMoreButton.IsEnabled = CeNeSonoAltre;

        System.Windows.Automation.AutomationProperties.SetHelpText(
            ShowMoreButton,
            CeNeSonoAltre
                ? $"Ne restano {_totali[CurrentKind] - _caricate[CurrentKind]} da mostrare in questo pannello."
                : "In questo pannello sono gia' mostrate tutte.");
    }

    private void Report(string testo)
        => _announcer.Announce(Announcement.CommandResponse(testo, DateTimeOffset.Now));

    private static CallHistoryRow ToRow(CallHistoryEntry entry) => new(
        FormatWhen(entry.StartedAt),
        SpeakWhen(entry.StartedAt),
        entry.EffectiveName,
        entry.Number,
        entry.WasAnswered ? FormatDuration(entry.Duration) : string.Empty,
        DescribeOutcome(entry),
        entry.QueueName ?? string.Empty,
        entry);

    /// <summary>Momento in forma compatta, per lo schermo.</summary>
    private static string FormatWhen(DateTimeOffset when)
    {
        var italiano = CultureInfo.GetCultureInfo("it-IT");
        var oggi = DateTimeOffset.Now.Date;

        if (when.Date == oggi) return when.ToString("HH:mm", italiano);
        if (when.Date == oggi.AddDays(-1)) return "ieri " + when.ToString("HH:mm", italiano);

        return when.ToString("dd/MM HH:mm", italiano);
    }

    /// <summary>
    /// Momento in forma discorsiva, per la sintesi.
    /// </summary>
    /// <remarks>
    /// «Oggi alle 9 e 40» si capisce al primo ascolto; «09:40» viene letto in modi
    /// diversi da voci diverse, e su una data compatta come 05/08 alcune sintesi
    /// leggono i numeri come una frazione.
    /// </remarks>
    private static string SpeakWhen(DateTimeOffset when)
    {
        var italiano = CultureInfo.GetCultureInfo("it-IT");
        var oggi = DateTimeOffset.Now.Date;

        var ora = when.Minute == 0
            ? $"alle {when.Hour}"
            : $"alle {when.Hour} e {when.Minute:00}";

        if (when.Date == oggi) return $"oggi {ora}";
        if (when.Date == oggi.AddDays(-1)) return $"ieri {ora}";

        return $"{when.Day} {italiano.DateTimeFormat.GetMonthName(when.Month)} {ora}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1)) return $"{(int)duration.TotalSeconds} secondi";

        var minuti = (int)duration.TotalMinutes;
        var secondi = duration.Seconds;

        return secondi == 0 ? $"{minuti} minuti" : $"{minuti} minuti e {secondi} secondi";
    }

    /// <summary>Esito di una chiamata, in parole.</summary>
    private static string DescribeOutcome(CallHistoryEntry entry)
    {
        if (entry.WasAnswered) return FormatDuration(entry.Duration);

        return entry.EndReason switch
        {
            CallEndReason.Busy => "occupato",
            CallEndReason.NoAnswer => "nessuna risposta",
            CallEndReason.Rejected => "rifiutata",
            CallEndReason.NotFound => "numero inesistente",
            CallEndReason.DoNotDisturb => "non disturbare",
            CallEndReason.NetworkFailure => "guasto di rete",
            CallEndReason.Transferred => "trasferita",
            _ => "senza risposta",
        };
    }

    /// <summary>
    /// Riepilogo dei tre pannelli, con quante se ne vedono su quante ce ne sono.
    /// </summary>
    /// <remarks>
    /// La forma «50 di 312» esiste per una ragione sola: senza, un pannello fermo a
    /// cinquanta non si distingue da un registro che ha smesso di registrare.
    /// </remarks>
    private void UpdateSummary()
        => LiveRegion.Update(
            SummaryText,
            $"Perse: {Quante(CallHistoryKind.Missed)}. "
            + $"Ricevute: {Quante(CallHistoryKind.Incoming)}. "
            + $"Effettuate: {Quante(CallHistoryKind.Outgoing)}.");

    private string Quante(CallHistoryKind kind)
        => _caricate[kind] >= _totali[kind]
            ? _totali[kind].ToString(CultureInfo.InvariantCulture)
            : $"{_caricate[kind]} di {_totali[kind]}";

    // -----------------------------------------------------------------------
    // Navigazione e azioni
    // -----------------------------------------------------------------------

    private DataGrid CurrentGrid => Tabs.SelectedItem switch
    {
        var t when ReferenceEquals(t, IncomingTab) => IncomingGrid,
        var t when ReferenceEquals(t, OutgoingTab) => OutgoingGrid,
        _ => MissedGrid,
    };

    private CallHistoryKind CurrentKind => Tabs.SelectedItem switch
    {
        var t when ReferenceEquals(t, IncomingTab) => CallHistoryKind.Incoming,
        var t when ReferenceEquals(t, OutgoingTab) => CallHistoryKind.Outgoing,
        _ => CallHistoryKind.Missed,
    };

    private CallHistoryRow? SelectedRow => CurrentGrid.SelectedItem as CallHistoryRow;

    /// <remarks>
    /// Nessun annuncio del conteggio a ogni cambio scheda: il controllo a schede è
    /// standard e lo screen reader dice già «scheda 2 di 3» con il suo nome. Il
    /// riepilogo resta a schermo per chi lo rilegge con il cursore virtuale.
    /// </remarks>
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Il cambio di selezione risale anche dalle tabelle contenute nelle schede:
        // senza questo controllo il pulsante verrebbe ricalcolato a ogni riga scorsa.
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, Tabs)) return;

        var grid = CurrentGrid;
        if (grid.Items.Count > 0 && grid.SelectedIndex < 0) grid.SelectedIndex = 0;

        // Quante ne restano dipende dal pannello aperto.
        UpdateShowMore();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1) return;

        e.Handled = true;

        try
        {
            new HelpWindow(HelpBook.Build(_hotkeys, DateTimeOffset.Now), _announcer, HelpBook.CallHistoryTopicId)
            {
                Owner = this,
            }.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della guida non riuscita");
        }
    }

    private async void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SelectedRow is not { } row) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CallBackAsync(row).ConfigureAwait(true);
            return;
        }

        // F9 usa il numero come destinazione del trasferimento in corso: e' la
        // sequenza che serve quando il chiamante chiede di essere passato alla persona
        // che lo aveva cercato poco prima.
        if (e.Key == Key.F9)
        {
            e.Handled = true;
            await UseForTransferAsync(row).ConfigureAwait(true);
        }
    }

    private async void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow is { } row) await CallBackAsync(row).ConfigureAwait(true);
    }

    private async void OnCallBackClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row) await CallBackAsync(row).ConfigureAwait(true);
        else Announce("Scegli prima una chiamata dall'elenco");
    }

    private async Task CallBackAsync(CallHistoryRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Number))
        {
            Announce("Questa chiamata non ha un numero da richiamare");
            return;
        }

        // STESSA REGOLA DELLA RUBRICA: con qualcuno in linea non si compone.
        //
        // Qui la richiamata partiva mentre il chiamante era ancora in conversazione,
        // aprendo una seconda linea in parallelo. Anche questo elenco ha il suo tasto
        // per il trasferimento, quindi Invio non serve mentre si sta parlando.
        //
        // La regola sta in CallSessionManager.IsTalking.
        if (_calls.IsTalking)
        {
            _log.LogDebug(
                "Richiamata ignorata: c'e' una conversazione in corso. "
                + "Per passare il chiamante si usa il tasto di trasferimento");

            return;
        }

        _log.LogInformation("Richiamata dall'elenco: {Numero}", row.Number);

        Close();
        await _calls.DialAsync(row.Number).ConfigureAwait(true);
    }

    private async Task UseForTransferAsync(CallHistoryRow row)
    {
        if (_transfer.Phase != TransferPhase.EnteringDestination)
        {
            Announce("Nessun trasferimento in corso: Invio richiama il numero");
            return;
        }

        Close();
        await _transfer.SetDestinationAsync(row.Number).ConfigureAwait(true);
    }

    // -----------------------------------------------------------------------
    // Svuotamento
    // -----------------------------------------------------------------------

    /// <summary>
    /// Svuota il pannello aperto oppure tutto l'elenco.
    /// </summary>
    /// <remarks>
    /// La domanda distingue i due casi perché sono decisioni diverse: togliere le
    /// perse già gestite è ordinaria amministrazione, cancellare tutto è
    /// un'operazione di conservazione dei dati. La conferma dice quante voci sta per
    /// eliminare, perché l'elenco non si vede tutto insieme.
    /// </remarks>
    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        var totale = _missed.Count + _incoming.Count + _outgoing.Count;

        if (totale == 0)
        {
            Announce("L'elenco e' gia' vuoto");
            return;
        }

        var pannello = CurrentKind switch
        {
            CallHistoryKind.Incoming => "ricevute",
            CallHistoryKind.Outgoing => "effettuate",
            _ => "perse",
        };

        var nelPannello = CurrentGrid.Items.Count;

        var answer = MessageBox.Show(
            this,
            $"Vuoi eliminare solo le chiamate {pannello} ({nelPannello} voci)?\n\n"
            + $"Si: elimina solo quelle.\n"
            + $"No: elimina tutto l'elenco ({totale} voci fra perse, ricevute ed effettuate).\n"
            + "Annulla: non elimina nulla.\n\n"
            + "L'operazione non si puo' annullare.",
            "Svuota l'elenco delle chiamate",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer == MessageBoxResult.Cancel)
        {
            Announce("Operazione annullata");
            return;
        }

        var kind = answer == MessageBoxResult.Yes ? CurrentKind : (CallHistoryKind?)null;
        var removed = await _history.ClearAsync(kind, CancellationToken.None).ConfigureAwait(true);

        await LoadAsync().ConfigureAwait(true);

        Announce(kind is null
            ? $"Elenco svuotato: {removed} chiamate eliminate"
            : $"Chiamate {pannello} eliminate: {removed} voci");
    }

    private void Announce(string message)
        => _announcer.Announce(Announcement.CommandResponse(message, DateTimeOffset.Now));

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
