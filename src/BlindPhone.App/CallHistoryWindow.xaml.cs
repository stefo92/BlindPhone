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
    /// <summary>Quante voci si mostrano per pannello.</summary>
    /// <remarks>
    /// Cinquanta: oltre, scorrere un elenco a voce costa più che richiamare a memoria,
    /// e le chiamate più vecchie di così si cercano per nome in rubrica.
    /// </remarks>
    private const int MaxRows = 50;

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

        // Il pannello delle perse ha il fuoco all'apertura: e' quello per cui la
        // finestra si apre.
        MissedGrid.Focus();
        if (_missed.Count > 0) GridNavigation.FocusCell(MissedGrid, 0);

        var perse = _missed.Count switch
        {
            0 => "nessuna chiamata persa",
            1 => "una chiamata persa",
            _ => $"{_missed.Count} chiamate perse",
        };

        _announcer.Announce(Announcement.CommandResponse(
            $"Ultime chiamate: {perse}. Control Tab per passare a ricevute ed effettuate",
            DateTimeOffset.Now));
    }

    private async Task FillAsync(CallHistoryKind kind, ObservableCollection<CallHistoryRow> target)
    {
        target.Clear();

        try
        {
            var entries = await _history.GetAsync(kind, MaxRows, CancellationToken.None).ConfigureAwait(true);

            foreach (var entry in entries) target.Add(ToRow(entry));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lettura dell'elenco {Kind} non riuscita", kind);
        }
    }

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

    private void UpdateSummary()
        => LiveRegion.Update(
            SummaryText,
            $"Perse: {_missed.Count}. Ricevute: {_incoming.Count}. Effettuate: {_outgoing.Count}.");

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
        if (!IsLoaded) return;

        var grid = CurrentGrid;
        if (grid.Items.Count > 0 && grid.SelectedIndex < 0) grid.SelectedIndex = 0;
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
