using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BlindPhone.Accessibility;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>Riga dell'elenco delle chiamate in coda.</summary>
/// <param name="Position">Posizione, oppure stringa vuota se non garantita.</param>
/// <param name="Caller">Chiamante, nome o numero.</param>
/// <param name="Wait">Tempo di attesa in forma leggibile.</param>
/// <param name="Queue">Nome della coda.</param>
/// <param name="Flags">Contrassegni: VIP, emergenza.</param>
/// <param name="Id">Identificativo della chiamata lato centralino.</param>
/// <param name="SpokenCaller">Chiamante nella forma pronunciabile, con il numero letto cifra per cifra.</param>
public sealed record QueueRow(
    string Position,
    string Caller,
    string Wait,
    string Queue,
    string Flags,
    string Id,
    string SpokenCaller)
{
    /// <summary>
    /// Testo che la sintesi legge quando il focus arriva sulla voce.
    /// </summary>
    /// <remarks>
    /// I contrassegni vengono per primi: se una chiamata è di emergenza o VIP,
    /// l'operatore deve saperlo prima ancora di sapere chi è, perché è
    /// l'informazione che cambia l'ordine in cui risponde.
    /// </remarks>
    public string AccessibleText
    {
        get
        {
            var parts = new List<string>(5);

            if (!string.IsNullOrWhiteSpace(Flags)) parts.Add(Flags);
            if (!string.IsNullOrWhiteSpace(Position)) parts.Add($"posizione {Position}");

            parts.Add(SpokenCaller);
            parts.Add($"in attesa da {Wait}");

            if (!string.IsNullOrWhiteSpace(Queue)) parts.Add($"coda {Queue}");

            return string.Join(", ", parts);
        }
    }
}

/// <summary>Riga dell'elenco dei colleghi in turno.</summary>
/// <param name="Status">Stato in parole, per la colonna.</param>
/// <param name="Name">Nominativo, oppure stringa vuota se non se ne conosce nessuno.</param>
/// <param name="Number">Interno, in cifre.</param>
/// <param name="Since">Ora in cui lo stato è diventato quello, oppure stringa vuota.</param>
/// <param name="AccessibleText">
/// Quello che lo screen reader legge arrivando sulla riga: lo stato per primo, poi chi
/// è, con il numero letto cifra per cifra.
/// </param>
public sealed record ColleagueRow(
    string Status,
    string Name,
    string Number,
    string Since,
    string AccessibleText);

/// <summary>Contatto proposto sopra il campo di composizione.</summary>
/// <param name="Name">Nominativo.</param>
/// <param name="Number">Interno da comporre.</param>
/// <param name="Presence">
/// Stato dell'interno in una parola, oppure stringa vuota se non è noto.
/// </param>
/// <param name="Contact">Contatto di provenienza.</param>
/// <remarks>
/// Due soli campi, e non è una semplificazione grafica: qui all'operatore serve
/// riconoscere il contatto giusto e chiamarlo, non consultarne la scheda. Tipo,
/// collocazione e struttura allungherebbero la lettura di ogni riga in un momento
/// in cui c'è quasi sempre qualcuno in linea che aspetta. La scheda completa resta
/// in rubrica, a un tasto di distanza.
/// </remarks>
public sealed record DialerSuggestion(string Name, string Number, string Presence, Contact Contact)
{
    /// <summary>
    /// Valore di un campo facoltativo della rubrica, per nome.
    /// </summary>
    /// <param name="id">Chiave del campo, come in <c>DialerFieldCatalog</c>.</param>
    /// <remarks>
    /// L'indicizzatore esiste per legare le colonne facoltative senza una proprietà
    /// per ciascuna: quali colonne ci sono lo decide l'operatore dal pannello, e un
    /// modello con un campo fisso per ognuna andrebbe riscritto ogni volta che se ne
    /// aggiunge uno al catalogo.
    /// </remarks>
    public string? this[string id] => string.Equals(id, "Presence", StringComparison.Ordinal)
        ? Presence
        : DialerFieldCatalog.Find(id)?.Value(Contact);

    /// <summary>Interno nella forma che lo screen reader legge cifra per cifra.</summary>
    public string SpokenNumber => SpeechFormatter.SpacedDigits(Number);

    /// <summary>Rappresentazione testuale breve della riga.</summary>
    /// <remarks>
    /// Impedisce che lo screen reader, non trovando un nome accessibile sulla riga,
    /// ricada sulla resa predefinita del <c>record</c>, che elencherebbe ogni
    /// proprietà compreso il contatto annidato per intero.
    /// </remarks>
    public override string ToString() => Name;
}

/// <summary>
/// Finestra principale del posto operatore.
/// </summary>
/// <remarks>
/// <para>
/// Il code-behind fa tre cose e nient'altro: instrada i tasti verso i servizi del
/// core, riflette lo stato dei servizi nell'interfaccia, e inoltra gli annunci a UI
/// Automation. Nessuna logica telefonica vive qui, il che è ciò che rende
/// collaudabile senza interfaccia tutto il comportamento che conta.
/// </para>
/// <para>
/// <b>Il principio che governa la gestione del focus.</b> Il focus si sposta solo
/// per azione esplicita dell'operatore. Una chiamata in arrivo annuncia ma non
/// sposta il cursore <b>dentro</b> la finestra: se lo facesse, l'operatore che sta
/// scrivendo una nota si ritroverebbe a digitare in un campo diverso senza
/// accorgersene, e per chi non vede il testo finirebbe nel posto sbagliato senza
/// alcun indizio.
/// </para>
/// <para>
/// <b>L'unica deroga</b> riguarda la finestra intera, non il focus interno: una
/// chiamata entrante che inizia a squillare la riporta in primo piano, perché con la
/// finestra riposta nell'area di notifica non ci sarebbe nulla su cui agire e i
/// comandi locali non arriverebbero. Si disattiva dalle impostazioni per chi lavora
/// stabilmente in un altro programma e preferisce rispondere con le scorciatoie
/// globali.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>Numero massimo di contatti proposti sopra il campo di composizione.</summary>
    /// <remarks>
    /// Oltre questa soglia scegliere a orecchio costa più che restringere: si dice
    /// che sono troppi e si invita a digitare ancora, invece di far scorrere un
    /// elenco che non finisce.
    /// </remarks>
    private const int MaxSuggestions = 8;

    private readonly CallSessionManager _calls;
    private readonly TransferOrchestrator _transfer;
    private readonly QueueMonitorService _queue;
    private readonly IQueueProvider _queueProvider;
    private readonly DutyStatusService _duty;
    private readonly CallRecordingController _recording;
    private readonly PresenceMonitorService _presence;
    private readonly ShiftBoardService _shift;
    private readonly CallHistoryService _history;
    private readonly ITelephonyEngine _telephony;
    private readonly IAnnouncer _announcer;
    private readonly AnnouncementService _announcements;
    private readonly AnnouncementContext _context;
    private readonly HotkeyProfile _hotkeys;
    private readonly HotkeyProfileStore _hotkeyStore;
    private readonly TransferOptions _transferOptions;
    private readonly ConferenceOptions _conferenceOptions;
    private readonly GlobalHotkeyRegistrar _globalHotkeys;

    /// <summary>Pulsanti e spie della cuffia, collegati quando la finestra è pronta.</summary>
    private HeadsetController? _headset;

    private readonly CompositeSpeechOutput _speech;
    private readonly IContactRepository _contacts;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MainWindow> _log;
    private readonly ILogger<TrayIcon> _trayLog;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;

    private readonly ObservableCollection<QueueRow> _queueRows = [];
    private readonly ObservableCollection<ColleagueRow> _colleagueRows = [];
    private readonly ObservableCollection<DialerSuggestion> _suggestions = [];
    private readonly DispatcherTimer _suggestionTimer;

    /// <summary>
    /// Orologio al secondo: tempo in servizio, durata della pausa, tempo di attesa.
    /// </summary>
    /// <remarks>
    /// Uno solo per tutti e tre, e sempre acceso. Il tempo in servizio scorre da
    /// quando la postazione è aperta, quindi non c'è un momento in cui ci sia nulla da
    /// aggiornare, e tre timer separati costerebbero tre risvegli al secondo per
    /// scrivere in tre etichette che stanno nella stessa finestra.
    /// </remarks>
    private readonly DispatcherTimer _clockTimer;

    private CancellationTokenSource? _suggestionSearch;
    private DateTimeOffset _lastQueueCountKeyPress;
    private string? _lastDialedNumber;

    /// <summary>
    /// Testo di aiuto originale del campo di composizione.
    /// </summary>
    /// <remarks>
    /// Si cattura all'avvio perché in pausa viene sostituito con la spiegazione del
    /// blocco: senza una copia, tornando in servizio bisognerebbe riscriverlo a mano
    /// qui, e le due versioni divergerebbero alla prima modifica del XAML.
    /// </remarks>
    private readonly string _dialerHelpText;

    /// <summary>
    /// Se la sezione della coda è a schermo. <c>null</c> finché non è stata decisa
    /// la prima volta.
    /// </summary>
    /// <remarks>
    /// Il terzo valore non è pignoleria: distingue "non ancora applicata" da
    /// "applicata e risultata nascosta", e serve a non annunciare all'avvio una
    /// configurazione che l'operatore non ha appena cambiato.
    /// </remarks>

    /// <summary>
    /// Altezza minima di una sezione a elenco, in pixel indipendenti dal dispositivo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Misurata, non scelta a occhio.</b> Serve a contenere il titolo della scheda,
    /// la riga delle intestazioni di colonna e almeno tre voci: sotto, la sezione mostra
    /// i nomi delle colonne e nient'altro, cioè occupa spazio nella finestra e sul
    /// percorso di navigazione senza dire niente. A centoventi pixel, provato sul banco
    /// del layout, si vedeva esattamente questo.
    /// </para>
    /// <para>
    /// Le righe elastiche sono le prime a cedere spazio quando qualcosa sopra cresce.
    /// Con un minimo dichiarato lo spazio non si prende più da qui: il contenuto sfonda
    /// la finestra e <see cref="GrowToFitContent"/> la allarga.
    /// </para>
    /// <para>
    /// <b>È il valore preferito, non sempre quello in vigore.</b> Dove nemmeno
    /// allargandosi fino al bordo dello schermo la finestra riesce a contenere le liste a
    /// questa altezza, ciascuna cede quanto serve a lasciare in vista il campo di
    /// composizione: vedi <see cref="MainWindowLayout.ListMinimum"/> e
    /// <see cref="FitListMinimums"/>.
    /// </para>
    /// </remarks>
    private const double MinimumListHeight = MainWindowLayout.PreferredListHeight;

    private bool? _queueSectionVisible;

    /// <summary>Icona nell'area di notifica, se attiva.</summary>
    private TrayIcon? _tray;

    /// <summary>
    /// Indica che la chiusura è definitiva e non va convertita in "metti via".
    /// </summary>
    private bool _exiting;

    /// <summary>Crea la finestra principale.</summary>
    /// <param name="calls">Gestore delle chiamate.</param>
    /// <param name="transfer">Orchestratore del trasferimento.</param>
    /// <param name="queue">Sorveglianza della coda.</param>
    /// <param name="queueProvider">Connettore verso il centralino.</param>
    /// <param name="duty">Orologio del turno e stato di pausa della postazione.</param>
    /// <param name="recording">Registrazione della conversazione.</param>
    /// <param name="shift">Quadro dei colleghi in turno.</param>
    /// <param name="presence">Stato degli interni: libero, occupato, fuori servizio.</param>
    /// <param name="history">Registrazione delle chiamate nell'elenco delle ultime.</param>
    /// <param name="telephony">Motore telefonico.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="announcements">Servizio di annuncio, per gli eventi di emissione.</param>
    /// <param name="context">Contesto operativo condiviso.</param>
    /// <param name="hotkeys">Profilo di scorciatoie attivo.</param>
    /// <param name="hotkeyStore">Deposito dei profili, per seguirne i cambiamenti.</param>
    /// <param name="transferOptions">
    /// Opzioni del trasferimento, di cui va tenuto aggiornato il nome del tasto.
    /// </param>
    /// <param name="conferenceOptions">
    /// Opzioni della conferenza, di cui vanno tenuti aggiornati i nomi dei tasti.
    /// </param>
    /// <param name="globalHotkeys">Registratore delle scorciatoie globali.</param>
    /// <param name="speech">Destinazione della voce.</param>
    /// <param name="contacts">Rubrica.</param>
    /// <param name="settings">Configurazione.</param>
    /// <param name="settingsService">
    /// Servizio di configurazione, letto al momento dell'uso per le impostazioni che
    /// cambiano a caldo.
    /// </param>
    /// <param name="serviceProvider">Contenitore, per aprire le finestre secondarie.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="trayLogger">Log dell'icona nell'area di notifica.</param>
    public MainWindow(
        CallSessionManager calls,
        TransferOrchestrator transfer,
        QueueMonitorService queue,
        IQueueProvider queueProvider,
        DutyStatusService duty,
        CallRecordingController recording,
        PresenceMonitorService presence,
        ShiftBoardService shift,
        CallHistoryService history,
        ITelephonyEngine telephony,
        IAnnouncer announcer,
        AnnouncementService announcements,
        AnnouncementContext context,
        HotkeyProfile hotkeys,
        HotkeyProfileStore hotkeyStore,
        TransferOptions transferOptions,
        ConferenceOptions conferenceOptions,
        GlobalHotkeyRegistrar globalHotkeys,
        CompositeSpeechOutput speech,
        IContactRepository contacts,
        AppSettings settings,
        SettingsService settingsService,
        IServiceProvider serviceProvider,
        ILogger<MainWindow> logger,
        ILogger<TrayIcon> trayLogger)
    {
        _calls = calls;
        _transfer = transfer;
        _queue = queue;
        _queueProvider = queueProvider;
        _duty = duty;
        _recording = recording;
        _presence = presence;
        _shift = shift;
        _history = history;
        _telephony = telephony;
        _announcer = announcer;
        _announcements = announcements;
        _context = context;
        _hotkeys = hotkeys;
        _hotkeyStore = hotkeyStore;
        _transferOptions = transferOptions;
        _conferenceOptions = conferenceOptions;
        _globalHotkeys = globalHotkeys;
        _speech = speech;
        _contacts = contacts;
        _settings = settings;
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        _log = logger;
        _trayLog = trayLogger;

        InitializeComponent();

        FitToWorkArea();

        QueueList.ItemsSource = _queueRows;
        ColleaguesList.ItemsSource = _colleagueRows;
        SuggestionsGrid.ItemsSource = _suggestions;

        _dialerHelpText = AutomationProperties.GetHelpText(DialerBox);

        // Il ritardo è lo stesso della rubrica, e per lo stesso motivo: cercare a
        // ogni tasto premuto produrrebbe un annuncio per ogni lettera, che si
        // sovrapporrebbe all'eco delle lettere stesse.
        _suggestionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _suggestionTimer.Tick += async (_, _) =>
        {
            _suggestionTimer.Stop();
            await RunSuggestionSearchAsync().ConfigureAwait(true);
        };

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TickClocks();

        _announcements.Spoken += OnAnnouncementSpoken;
        _queueProvider.SnapshotUpdated += OnQueueSnapshotUpdated;
        _calls.CallStateChanged += OnCallStateChanged;
        _shift.Changed += OnShiftBoardChanged;
        _transfer.PhaseChanged += OnTransferPhaseChanged;
        _telephony.RegistrationStateChanged += OnRegistrationChanged;
        _globalHotkeys.HotkeyPressed += OnGlobalHotkeyPressed;
        _hotkeyStore.ActiveProfileChanged += OnHotkeyProfileChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;

        // La gestione a livello di finestra intercetta i tasti prima dei controlli:
        // e' cio' che permette a F2 di rispondere anche mentre il focus e' nel campo
        // di composizione, senza costringere l'operatore a spostarsi.
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp += OnWindowPreviewKeyUp;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;

        // La finestra si allarga quando il contenuto non ci sta: vedi GrowToFitContent.
        RootGrid.LayoutUpdated += OnRootLayoutUpdated;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Le scorciatoie globali richiedono l'handle della finestra, che esiste solo
        // dopo il caricamento.
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        _globalHotkeys.Attach(source.Handle);
        source.AddHook(WndProcHook);

        // La cuffia passa dagli stessi comandi delle scorciatoie: vedi HeadsetController.
        _headset = _serviceProvider.GetRequiredService<HeadsetController>();
        _headset.Start(source, CurrentActionContext, ExecuteCommandAsync);

        RegisterGlobalHotkeys();
        ApplyTraySettings(_settingsService.Current);
        ApplyQueueVisibility(_settingsService.Current);
        BuildSuggestionColumns(_settingsService.Current);

        // Il turno comincia quando la finestra e' pronta, non quando il processo parte:
        // fra i due momenti c'e' l'avviso di versione, che puo' restare aperto per il
        // tempo che l'operatore impiega a leggerlo.
        _duty.Restart(DateTimeOffset.Now);
        _clockTimer.Start();

        await StartServicesAsync().ConfigureAwait(true);

        // L'ELENCO DEI COLLEGHI VIENE DOPO L'AVVIO DEI SERVIZI, e l'ordine conta.
        //
        // Il sondaggio di raggiungibilita' nasce con il trasporto SIP: prima non c'e'
        // nulla a cui chiedere, e la riga di registro che dice da quale strada arriva
        // lo stato riporterebbe «nessuna» a ogni avvio, cioe' l'informazione sbagliata
        // proprio nel punto in cui la si va a cercare.
        await ApplyColleaguesSettingsAsync(_settingsService.Current).ConfigureAwait(true);

        UpdateRegistrationText();
        UpdateStatus();
        DialerBox.Focus();

        // Il controllo degli aggiornamenti non si attende: e' una lettura su una rete
        // che puo' non rispondere, e l'avvio della postazione non deve dipenderne. Se
        // trova qualcosa lo dice quando ha finito.
        _ = CheckForUpdatesAsync();
    }

    // -----------------------------------------------------------------------
    // Modalità semplificata: tre tasti del tastierino
    // -----------------------------------------------------------------------

    /// <summary>Se la postazione è configurata per il tastierino.</summary>
    private bool IsSimplifiedMode
        => _settingsService.Current.Interaction == InteractionMode.Simplified;

    /// <summary>
    /// Intercetta i tasti del tastierino, quando la modalità semplificata è attiva.
    /// </summary>
    /// <returns><c>true</c> se il tasto è stato consumato qui.</returns>
    /// <remarks>
    /// <para>
    /// <b>Il tasto viene consumato anche quando non fa niente.</b> Se in questo stato
    /// «+» non ha significato, non deve per questo finire nel campo di composizione:
    /// il chiamante che detta un numero non si aspetta di trovarci dentro un più. È
    /// la funzione che nel requisito «verifica che il tasto + non venga passato alla
    /// composizione», e vale anche per il tasto che non produce alcuna azione.
    /// </para>
    /// <para>
    /// <b>Invio fa eccezione</b>, ed è deliberato: fuori da una chiamata deve tornare
    /// a essere l'Invio di sempre, quello che compone il numero scritto nel campo. Là
    /// il tasto non si consuma e prosegue verso il gestore del campo, che fa il resto.
    /// </para>
    /// </remarks>
    private bool TryHandleSimplifiedKeypad(KeyEventArgs e)
    {
        if (!IsSimplifiedMode) return false;

        // Solo i tasti del tastierino numerico: il "+" della tastiera principale si
        // ottiene con Maiusc e finisce nel testo, e il "/" della riga dei numeri e'
        // un carattere come un altro. La modalita' parla al tastierino, e solo a
        // quello.
        var tasto = e.Key switch
        {
            Key.Add => KeypadKey.Answer,
            Key.Divide => KeypadKey.HangUp,
            Key.Enter => KeypadKey.Confirm,
            Key.Subtract => KeypadKey.Release,
            _ => (KeypadKey?)null,
        };

        if (tasto is not { } premuto) return false;

        var azione = SimplifiedKeypad.Resolve(premuto, CurrentActionContext());

        // Fuori da una chiamata Invio torna a comporre: si lascia proseguire.
        if (azione == KeypadAction.Dial) return false;

        e.Handled = true;

        _log.LogInformation(
            "Tastierino semplificato: tasto {Tasto} -> azione {Azione}", premuto, azione);

        if (azione == KeypadAction.None)
        {
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return true;
        }

        _ = ExecuteKeypadActionAsync(azione);
        return true;
    }

    /// <summary>Esegue l'azione decisa dal tastierino.</summary>
    /// <remarks>
    /// <para>
    /// Ogni azione passa per la stessa strada della tastiera completa o dei pulsanti:
    /// stesso effetto, stesso annuncio, stessa riga di registro. La modalità
    /// semplificata è una <b>disposizione di tasti</b>, non un secondo modo di
    /// gestire le chiamate da tenere allineato al primo.
    /// </para>
    /// <para>
    /// Le due varianti del trasferimento avviano prima la procedura sulla chiamata —
    /// in pausa o ancora in linea, <c>BeginTransferAsync</c> la ripesca da sé — e poi
    /// scelgono la strada: è la stessa coppia di metodi che usano i due pulsanti della
    /// barra.
    /// </para>
    /// </remarks>
    private async Task ExecuteKeypadActionAsync(KeypadAction azione)
    {
        switch (azione)
        {
            case KeypadAction.Answer:
                await ExecuteCommandAsync(HotkeyCommand.Answer).ConfigureAwait(true);
                break;

            case KeypadAction.HangUp:
                await ExecuteCommandAsync(HotkeyCommand.HangUp).ConfigureAwait(true);
                break;

            case KeypadAction.Hold:
            case KeypadAction.Resume:
                await ExecuteCommandAsync(HotkeyCommand.ToggleHold).ConfigureAwait(true);
                break;

            case KeypadAction.StartConsultation:
                await KeypadStartConsultationAsync().ConfigureAwait(true);
                break;

            case KeypadAction.CompleteTransfer:
                await ExecuteCommandAsync(HotkeyCommand.Transfer).ConfigureAwait(true);
                break;

            // NON E' UN COMANDO RIFIUTATO, E' UNA RISPOSTA.
            //
            // L'operatore ha premuto Invio con l'interno gia' in linea, cioe' il gesto
            // che fino a ieri completava il trasferimento. Il segnale di comando
            // rifiutato lo lascerebbe a chiedersi se il programma funzioni ancora;
            // dirgli quale tasto serve glielo insegna nel momento in cui gli serve.
            case KeypadAction.AnnounceReleaseKey:
                _announcer.Announce(Announcement.CommandResponse(
                    AnnouncementTexts.ReleaseKeyReminder(SimplifiedKeypad.ReleaseKeyName),
                    DateTimeOffset.Now));
                break;
        }

        UpdateCommandBar();
    }

    /// <summary>
    /// Chiama l'interno composto, perché l'operatore senta cosa risponde.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La procedura va aperta prima: <c>AttendedTransferNowAsync</c> pretende una
    /// sessione già avviata, perché è lì che il chiamante viene messo in attesa e la
    /// destinazione risolta sulla rubrica.
    /// </para>
    /// <para>
    /// <b>Qui il trasferimento non si compie.</b> Si apre soltanto la linea con
    /// l'interno; il chiamante resta in attesa finché l'operatore non preme il tasto
    /// che lo svincola, o finché non chiude la consultazione per riprenderlo.
    /// </para>
    /// </remarks>
    private async Task KeypadStartConsultationAsync()
    {
        await FlushPendingSuggestionSearchAsync().ConfigureAwait(true);

        var destinazione = CurrentDestination();
        if (string.IsNullOrWhiteSpace(destinazione)) return;

        // La digitazione e' finita nell'istante in cui si preme Invio: dichiararlo
        // prima di chiamare e' cio' che permette alla conferma di essere pronunciata
        // invece di finire scartata come annuncio durante la composizione.
        _context.StopTyping();

        if (_transfer.Phase == TransferPhase.Idle)
        {
            await _transfer.HandleTransferKeyAsync(destinazione).ConfigureAwait(true);
        }

        await _transfer.AttendedTransferNowAsync(destinazione).ConfigureAwait(true);
    }

    /// <summary>Fotografia dello stato, condivisa con la barra dei pulsanti.</summary>
    private CallActionContext CurrentActionContext() => new(
        HasRingingCall: _calls.RingingCall is not null,
        HasActiveCall: _calls.ActiveCall is not null,
        HasOutgoingCall: _calls.OutgoingCall is not null,
        HeldCount: _calls.HeldCalls.Count,
        IsConferenceActive: _calls.IsConferenceActive,
        TransferPhase: _transfer.Phase,
        HasTypedDestination: !string.IsNullOrWhiteSpace(CurrentDestination()),
        IsRecording: _recording.IsRecording,
        IsDtmfMode: _dtmfMode,
        IsMuted: _telephony.IsMuted,
        IsServicePaused: _duty.IsPaused,
        HasUpdateAvailable: _updateFound is { HasUpdate: true },
        CanBargeIn: _transfer.CanBargeIn);

    /// <summary>
    /// Blocca il campo di composizione mentre una chiamata squilla, in modalità
    /// semplificata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È il primo passo dello schema: mentre squilla, il campo non accetta testo, così
    /// il <c>+</c> con cui si risponde non può finire scritto da nessuna parte.
    /// Riprende ad accettare testo appena si è risposto — è lì che serve, per comporre
    /// l'interno a cui passare la chiamata.
    /// </para>
    /// <para>
    /// Il blocco della pausa di servizio ha comunque la precedenza: una postazione
    /// fuori servizio ha il campo bloccato per un'altra ragione, e riaprirlo perché la
    /// chiamata è finita sarebbe sbagliato.
    /// </para>
    /// </remarks>
    private void UpdateDialerAvailability()
    {
        var bloccatoPerPausa = _duty.IsPaused;
        var bloccatoPerSquillo = IsSimplifiedMode && _calls.RingingCall is not null;

        DialerBox.IsReadOnly = bloccatoPerPausa || bloccatoPerSquillo;
    }

    // -----------------------------------------------------------------------
    // Aggiornamenti
    // -----------------------------------------------------------------------

    /// <summary>Aggiornamento trovato sul server, se ce n'è uno.</summary>
    private UpdateCheckResult? _updateFound;

    /// <summary>
    /// Chiede al server se c'è una versione più recente.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Silenzioso quando non trova niente</b>, che è il caso normale: un annuncio
    /// «nessun aggiornamento» a ogni avvio sarebbe una frase in più da ascoltare tutti
    /// i giorni per non sapere nulla di nuovo. Anche il fallimento resta nel registro:
    /// un server irraggiungibile all'avvio non è un problema dell'operatore, e non c'è
    /// niente che possa farci.
    /// </para>
    /// <para>
    /// Quando invece trova qualcosa lo dice a voce e fa comparire il pulsante nella
    /// barra. Non apre la finestra da sé: aprirla di prepotenza sopra una chiamata
    /// entrante sarebbe il modo peggiore di dare una notizia che può aspettare.
    /// </para>
    /// </remarks>
    private async Task CheckForUpdatesAsync()
    {
        var settings = _settingsService.Current.Updates;
        if (!settings.CheckOnStartup) return;

        var servizio = CreateUpdateService();
        if (!servizio.IsConfigured) return;

        try
        {
            var esito = await servizio.CheckAsync().ConfigureAwait(true);
            if (!esito.HasUpdate) return;

            _updateFound = esito;
            UpdateCommandBar();

            _announcer.Announce(Announcement.Normal(
                $"Aggiornamento disponibile: versione {esito.Update!.DisplayVersion}. "
                + "Il pulsante per installarlo e' nella barra dei comandi.",
                DateTimeOffset.Now,
                coalesceKey: "update:available"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Controllo degli aggiornamenti non riuscito");
        }
    }

    /// <summary>Costruisce il servizio degli aggiornamenti con la configurazione corrente.</summary>
    /// <remarks>
    /// Si costruisce al bisogno e non si conserva: l'indirizzo del server e le
    /// credenziali si possono cambiare dalle impostazioni mentre il programma gira, e
    /// un servizio costruito all'avvio continuerebbe a puntare al server vecchio.
    /// </remarks>
    private UpdateService CreateUpdateService()
    {
        var settings = _settingsService.Current;

        return new UpdateService(
            settings.Updates,

            // Legata al server per cui e' stata inserita: cambiare l'indirizzo degli
            // aggiornamenti in settings.json non deve mandare la password del server
            // dell'ente a un indirizzo che l'ente non ha scelto.
            _settingsService.Secrets.GetForHost(
                settings.Updates.PasswordKey,
                settings.Updates.ServerUrl,
                "la password del server degli aggiornamenti"),

            InstalledVersion(),
            _serviceProvider.GetRequiredService<ILogger<UpdateService>>());
    }

    /// <summary>Versione installata su questa postazione.</summary>
    private static Version InstalledVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Apre la finestra dell'aggiornamento trovato.</summary>
    private void ShowUpdateWindow()
    {
        if (_updateFound is not { HasUpdate: true } esito) return;

        try
        {
            var finestra = new UpdateWindow(
                CreateUpdateService(),
                esito,
                InstalledVersion().ToString(3),
                _announcer,
                _serviceProvider.GetRequiredService<ILogger<UpdateWindow>>())
            {
                Owner = this,
            };

            finestra.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della finestra dell'aggiornamento non riuscita");

            _announcer.Announce(Announcement.CommandResponse(
                "Non e' stato possibile aprire la finestra dell'aggiornamento", DateTimeOffset.Now));
        }
    }

    // -----------------------------------------------------------------------
    // Area di notifica e ciclo di vita della finestra
    // -----------------------------------------------------------------------

    /// <summary>
    /// Crea o rimuove l'icona nell'area di notifica secondo la configurazione.
    /// </summary>
    /// <param name="settings">Configurazione corrente.</param>
    /// <remarks>
    /// Si applica a caldo, come i tasti rapidi e la voce: l'operatore che disattiva
    /// l'icona vuole vedere l'effetto adesso. Se la finestra è riposta quando l'icona
    /// viene tolta, viene prima riaperta: togliere l'icona lasciando la finestra
    /// nascosta significherebbe un programma in esecuzione senza alcun modo visibile
    /// di richiamarlo.
    /// </remarks>
    private void ApplyTraySettings(AppSettings settings)
    {
        if (settings.Tray.Enabled && _tray is null)
        {
            _tray = new TrayIcon(_trayLog);
            _tray.OpenRequested += (_, _) => Dispatcher.Invoke(() => RestoreWindow("Riaperto dall'area di notifica"));
            _tray.ExitRequested += (_, _) => Dispatcher.Invoke(QuitApplication);

            UpdateRegistrationText();
            _log.LogInformation("Icona nell'area di notifica attiva");
            return;
        }

        if (!settings.Tray.Enabled && _tray is not null)
        {
            if (!IsVisible) RestoreWindow(null);

            _tray.Dispose();
            _tray = null;
            _log.LogInformation("Icona nell'area di notifica disattivata");
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
        => Dispatcher.Invoke(() =>
        {
            ApplyTraySettings(settings);
            ApplyQueueVisibility(settings);

            // L'elenco dei colleghi cambia piu' spesso di tutto il resto di questa
            // funzione, perche' cambia quando cambia il personale: chiedere un riavvio
            // per una riga aggiunta sarebbe sproporzionato.
            _ = ApplyColleaguesSettingsAsync(settings);
            BuildSuggestionColumns(settings);

            // La modalita' di interazione si applica subito, e con essa il nome del
            // tasto che gli annunci del trasferimento pronunciano: chi passa al
            // tastierino semplificato deve sentirsi nominare Invio dalla prima
            // chiamata, non dal riavvio successivo.
            ApplyTransferKeyName(settings, _hotkeys);
        });

    /// <summary>
    /// Allinea il nome del tasto pronunciato dagli annunci del trasferimento.
    /// </summary>
    /// <param name="settings">Configurazione corrente, per la modalità di interazione.</param>
    /// <param name="profile">Profilo dei tasti attivo.</param>
    /// <remarks>
    /// Dipende da due cose che cambiano a caldo e in momenti diversi — il profilo dei
    /// tasti e la modalità di interazione — e per questo sta in un metodo solo invece
    /// che ripetuto nei due gestori.
    /// </remarks>
    private void ApplyTransferKeyName(AppSettings settings, HotkeyProfile profile)
        => _transferOptions.TransferKeyName = SimplifiedKeypad.TransferKeyName(
            settings.Interaction == InteractionMode.Simplified,
            profile.Get(HotkeyCommand.Transfer, HotkeyScope.Local)?.SpeechText ?? "F9");

    /// <summary>
    /// Costruisce le colonne dei contatti proposti secondo la scelta dell'operatore.
    /// </summary>
    /// <param name="settings">Configurazione corrente.</param>
    /// <remarks>
    /// <para>
    /// <b>Nominativo e interno ci sono sempre.</b> Una riga senza nome non si
    /// riconosce e una senza numero non si chiama: non sono una scelta, sono la riga.
    /// Le altre le sceglie l'operatore in Impostazioni, scheda Rubrica, perché
    /// dipendono da come è fatta la rubrica dell'ente — dove i nominativi si
    /// assomigliano serve la descrizione, dove bisogna dire al chiamante dove
    /// presentarsi serve la collocazione.
    /// </para>
    /// <para>
    /// <b>Anche l'ordine è dell'operatore</b>, e per lo stesso motivo: chi legge la
    /// riga con lo screen reader la percorre da sinistra a destra e si ferma quando
    /// ha saputo quello che gli serve, quindi il campo che decide deve venire subito
    /// dopo il numero. Le colonne escono nell'ordine in cui il catalogo le risolve,
    /// che è quello configurato.
    /// </para>
    /// <para>
    /// <b>Il nome accessibile dell'interno resta dichiarato in due punti</b>, cella e
    /// testo, con lo stesso valore: è ciò che fa leggere «4 0 2 1» invece di
    /// «quattromilaventuno» senza farlo dire due volte. Ricostruendo le colonne da
    /// codice quella dichiarazione va rifatta, e dimenticarla non produce nessun
    /// errore — solo un numero che nessuno può trascrivere.
    /// </para>
    /// </remarks>
    private void BuildSuggestionColumns(AppSettings settings)
    {
        SuggestionsGrid.Columns.Clear();

        SuggestionsGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = "Nominativo",
            Binding = new System.Windows.Data.Binding(nameof(DialerSuggestion.Name)),
            Width = new System.Windows.Controls.DataGridLength(1, System.Windows.Controls.DataGridLengthUnitType.Star),
        });

        SuggestionsGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = "Interno",
            Binding = new System.Windows.Data.Binding(nameof(DialerSuggestion.Number)),
            Width = new System.Windows.Controls.DataGridLength(160),
            CellStyle = (Style)FindResource("SpokenNumberCell"),
            ElementStyle = (Style)FindResource("SpokenNumberText"),
        });

        var scelti = settings.Contacts.DialerFields ?? DialerFieldCatalog.Default;

        foreach (var campo in DialerFieldCatalog.Resolve(scelti))
        {
            SuggestionsGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
            {
                Header = campo.Label,

                // L'indicizzatore del modello: una colonna per chiave, senza una
                // proprieta' dedicata per ciascun campo del catalogo.
                Binding = new System.Windows.Data.Binding($"[{campo.Id}]"),
                Width = new System.Windows.Controls.DataGridLength(campo.Id == "Presence" ? 130 : 200),
            });
        }

        _log.LogDebug(
            "Contatti proposti: colonne {Colonne}",
            string.Join(", ", SuggestionsGrid.Columns.Select(c => c.Header)));
    }


    // -----------------------------------------------------------------------
    // Colleghi in turno
    // -----------------------------------------------------------------------

    /// <summary>Se la sezione dei colleghi è a schermo, oppure <c>null</c> prima del primo giro.</summary>
    private bool? _colleaguesSectionVisible;


    /// <summary>
    /// Riscrive l'elenco dei colleghi quando il quadro cambia.
    /// </summary>
    /// <remarks>
    /// L'evento arriva dal giro di verifica, che gira fuori dal filo dell'interfaccia:
    /// il passaggio dal dispatcher non è una formalità, è ciò che evita di toccare la
    /// tabella da un filo che non è il suo.
    /// </remarks>
    private void OnShiftBoardChanged(object? sender, EventArgs e)
        => Dispatcher.Invoke(() =>
        {
            ApplyColleaguesVisibility();
            RefreshColleagues();
        });

    /// <summary>
    /// Mostra o toglie la sezione dei colleghi secondo quello che l'operatore ha
    /// configurato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stessa regola della sezione della coda, e per la stessa ragione: senza interni
    /// da sorvegliare l'elenco resterebbe vuoto per sempre, e un elenco permanentemente
    /// vuoto sul percorso di navigazione costa una tabulazione e un annuncio a ogni
    /// giro, tutti i giorni, per non dire mai nulla.
    /// </para>
    /// <para>
    /// Il minimo di altezza c'è per lo stesso motivo per cui c'è su quella della coda:
    /// è una riga elastica, quindi è la prima a cedere spazio quando qualcosa sopra
    /// cresce, e ridotta a una fessura mostrerebbe una sezione che c'è e non si legge.
    /// A zero quando è nascosta, altrimenti resterebbe una fascia vuota in mezzo alla
    /// finestra.
    /// </para>
    /// </remarks>
    private void ApplyColleaguesVisibility()
    {
        var visible = _shift.IsConfigured;

        // Va guardato PRIMA di nascondere: quando un elemento diventa Collapsed WPF gli
        // toglie il fuoco da se', e da quel momento la domanda "il cursore era li'
        // dentro?" risponde sempre di no.
        var hadFocus = ColleaguesSection.IsKeyboardFocusWithin;

        ColleaguesSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ColleaguesRow.Height = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColleaguesRow.MinHeight = visible ? MinimumListHeight : 0;

        var previous = _colleaguesSectionVisible;
        _colleaguesSectionVisible = visible;

        if (!visible && hadFocus) DialerBox.Focus();

        if (previous is null || previous == visible) return;

        _announcer.Announce(Announcement.CommandResponse(
            visible
                ? $"Elenco dei colleghi attivo. {LocalKeyName(_hotkeys, HotkeyCommand.FocusColleagues, "Control Alt C")} per consultarlo"
                : "Elenco dei colleghi tolto dalla finestra: nessun interno da sorvegliare",
            DateTimeOffset.Now));
    }

    /// <summary>Riscrive l'elenco dei colleghi con quello che si sa adesso.</summary>
    /// <remarks>
    /// <para>
    /// <b>La riga selezionata si conserva.</b> L'elenco si riscrive a ogni giro di
    /// verifica, e senza questo accorgimento il cursore dell'operatore tornerebbe in
    /// cima ogni minuto — cioè proprio mentre sta scorrendo per trovare qualcuno.
    /// </para>
    /// <para>
    /// <b>Non parla da sé.</b> Un collega che si alza dalla scrivania non è un fatto
    /// che debba interrompere una conversazione in corso, e su una squadra di venti
    /// persone gli annunci sarebbero decine al giorno. Chi vuole sapere come stanno le
    /// cose lo chiede con il tasto rapido.
    /// </para>
    /// </remarks>
    private void RefreshColleagues()
    {
        var selezionato = (ColleaguesList.SelectedItem as ColleagueRow)?.Number;
        var quadro = _shift.Snapshot();
        var raggruppamento = _settingsService.Current.Accessibility.DigitGrouping;

        _colleagueRows.Clear();

        foreach (var voce in quadro)
        {
            _colleagueRows.Add(new ColleagueRow(
                ShiftBoardTexts.Status(voce),
                voce.Name,
                voce.Number,
                voce.Since is { } da ? $"dalle {da:HH:mm}" : string.Empty,
                ShiftBoardTexts.Accessible(voce, raggruppamento)));
        }

        ColleaguesHeaderText.Text = _shift.CanTrustResults
            ? ShiftBoardTexts.Header(quadro, _shift.LastUpdatedAt)
            : ShiftBoardTexts.Header(quadro, _shift.LastUpdatedAt, canTrust: false);

        AutomationProperties.SetHelpText(ColleaguesList, DescribeColleaguesSource());

        if (selezionato is null) return;

        var riga = _colleagueRows.FirstOrDefault(r => r.Number == selezionato);
        if (riga is not null) ColleaguesList.SelectedItem = riga;
    }

    /// <summary>Spiega da dove arriva lo stato, e quando non arriva perché.</summary>
    private string DescribeColleaguesSource()
    {
        if (_shift.MethodRefused) return ShiftBoardTexts.ProbeRefused;
        if (_shift.AnswersForAnyone) return ShiftBoardTexts.CannotDistinguish;

        return "Usa le frecce per scorrere. Invio chiama il collega selezionato. "
               + $"Lo stato arriva da: {_shift.SourceName}.";
    }

    /// <summary>
    /// Porta il cursore sull'elenco dei colleghi e dice come sono messi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Chiede una verifica prima di parlare.</b> Il quadro si aggiorna a intervalli,
    /// e l'operatore preme questo tasto proprio quando sta decidendo a chi passare una
    /// chiamata: un dato di cinquanta secondi fa è vecchio nel momento in cui conta di
    /// più. La verifica non blocca — si annuncia quello che si sa adesso e l'elenco si
    /// riscrive da sé quando le risposte arrivano.
    /// </para>
    /// <para>
    /// <b>Con la sezione assente il comando parla lo stesso.</b> Un tasto muto è
    /// indistinguibile da un tasto che non funziona, e porta a premerlo di nuovo: qui
    /// dice dove si configurano gli interni da sorvegliare.
    /// </para>
    /// </remarks>
    private void FocusColleaguesList()
    {
        if (!_shift.IsConfigured)
        {
            _announcer.Announce(Announcement.CommandResponse(
                ShiftBoardTexts.NotConfigured, DateTimeOffset.Now));

            return;
        }

        ColleaguesList.Focus();

        if (_colleagueRows.Count > 0 && ColleaguesList.SelectedIndex < 0)
        {
            ColleaguesList.SelectedIndex = 0;
        }

        _announcer.Announce(Announcement.CommandResponse(
            ShiftBoardTexts.Summary(_shift.Snapshot(), _shift.CanTrustResults),
            DateTimeOffset.Now));

        // La verifica parte adesso e finisce quando finisce: l'annuncio e' gia' andato
        // con quello che si sapeva, e l'elenco si riallinea da se' all'arrivo delle
        // risposte. Attenderla qui bloccherebbe il cursore per qualche secondo.
        _ = _shift.RefreshAsync();
    }

    private async void OnColleaguesListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await CallSelectedColleagueAsync().ConfigureAwait(true);
    }

    private async void OnColleaguesDoubleClick(object sender, MouseButtonEventArgs e)
        => await CallSelectedColleagueAsync().ConfigureAwait(true);

    /// <summary>
    /// Chiama il collega selezionato, o lo usa come destinazione del trasferimento.
    /// </summary>
    /// <remarks>
    /// <b>Durante un trasferimento Invio non compone: sceglie.</b> È il gesto per cui
    /// questo elenco viene consultato più spesso — il chiamante è già in attesa e si sta
    /// cercando chi c'è — e comporre una seconda chiamata da qui lascerebbe due linee
    /// aperte al posto di una destinazione scelta.
    /// </remarks>
    private async Task CallSelectedColleagueAsync()
    {
        if (ColleaguesList.SelectedItem is not ColleagueRow riga) return;
        if (string.IsNullOrWhiteSpace(riga.Number)) return;

        if (_transfer.Phase == TransferPhase.EnteringDestination)
        {
            await _transfer.SetDestinationAsync(riga.Number).ConfigureAwait(true);
            await _transfer.AttendedTransferNowAsync(riga.Number).ConfigureAwait(true);
            return;
        }

        DialerBox.Text = riga.Number;
        await DialAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Rilegge dalle impostazioni chi sorvegliare e ogni quanto.
    /// </summary>
    /// <param name="settings">Configurazione corrente.</param>
    /// <remarks>
    /// Va richiamato all'avvio e a ogni salvataggio delle impostazioni: l'elenco dei
    /// colleghi è la cosa che si tocca più spesso di tutta questa funzione — cambia
    /// quando cambia il personale — e chiedere un riavvio per una riga aggiunta sarebbe
    /// sproporzionato.
    /// </remarks>
    private async Task ApplyColleaguesSettingsAsync(AppSettings settings)
    {
        var elenco = ColleagueRoster.Parse(settings.Colleagues.Monitored);
        var intervallo = TimeSpan.FromSeconds(Math.Max(1, settings.Colleagues.RefreshSeconds));

        await _shift.ConfigureAsync(elenco, intervallo).ConfigureAwait(true);

        ApplyColleaguesVisibility();
        RefreshColleagues();
    }

    /// <summary>
    /// Mostra o nasconde la sezione della coda secondo il collegamento all'API del
    /// centralino.
    /// </summary>
    /// <param name="settings">Configurazione corrente.</param>
    /// <remarks>
    /// <para>
    /// <b>Perché una sezione che sparisce.</b> Senza collegamento all'API del
    /// centralino non esiste alcuna coda da mostrare: l'elenco resta vuoto per sempre
    /// e il suo contatore fermo a zero. Tenerlo a schermo non è neutro — è un elemento
    /// sul percorso di navigazione da attraversare a ogni giro di tabulazione, che lo
    /// screen reader annuncia ogni volta come "elenco delle chiamate in attesa in
    /// coda, vuoto" per non dire mai nulla di diverso. Su una postazione senza API è
    /// puro attrito, tutti i giorni.
    /// </para>
    /// <para>
    /// La riga della griglia si azzera insieme alla sezione. Nasconderne il solo
    /// contenuto lascerebbe un vuoto verticale in mezzo alla finestra, con il campo di
    /// composizione spinto a metà schermo: chi vede lo troverebbe fuori posto, e chi
    /// ingrandisce lo schermo dovrebbe percorrere quel vuoto per arrivarci.
    /// </para>
    /// <para>
    /// Si applica a caldo come le altre impostazioni, e a caldo va anche detto: chi ha
    /// appena tolto la spunta deve sentire che cosa è cambiato nella finestra, perché
    /// una sezione che sparisce in silenzio è indistinguibile da un guasto. All'avvio
    /// invece non si annuncia nulla: nessuno ha cambiato niente, e la prima cosa che
    /// l'operatore sente accendendo il posto operatore non deve essere una nota di
    /// configurazione.
    /// </para>
    /// </remarks>
    private void ApplyQueueVisibility(AppSettings settings)
    {
        var visible = settings.Pbx.Enabled;

        // Va guardato PRIMA di nascondere. Quando un elemento diventa Collapsed WPF
        // gli toglie il fuoco da se', e da quel momento la domanda "il cursore era
        // li' dentro?" risponde sempre di no: chiedendolo dopo, il cursore finirebbe
        // nel nulla e l'operatore non avrebbe modo di sapere dove si trova.
        var hadFocus = QueueSection.IsKeyboardFocusWithin;

        QueueSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        QueueRow.Height = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        // L'ELENCO NON PUO' RIDURSI A UNA FESSURA.
        //
        // E' l'unica riga elastica della finestra, quindi e' la prima a cedere spazio
        // quando qualcosa sopra cresce: con un nome di chiamante lungo, che manda il
        // riquadro dell'avviso su due righe, restava alta sette pixel. La sezione
        // risultava presente e vuota, che e' il modo peggiore di mostrarla — chi guarda
        // non ha modo di sapere se la coda e' vuota o se l'elenco e' stato schiacciato.
        //
        // Con un minimo dichiarato lo spazio non si prende piu' da qui: il contenuto
        // sfonda la finestra, GrowToFitContent se ne accorge e la allarga.
        //
        // A zero quando la sezione e' nascosta, altrimenti resterebbe una fascia vuota
        // di centoventi pixel in mezzo alla finestra.
        QueueRow.MinHeight = visible ? MinimumListHeight : 0;

        var previous = _queueSectionVisible;
        if (previous == visible) return;

        _queueSectionVisible = visible;

        _log.LogInformation(
            "Sezione della coda {Stato}: collegamento all'API del centralino {Flag}",
            visible ? "visibile" : "nascosta",
            visible ? "attivo" : "disattivo");

        // Il cursore non puo' restare dentro una sezione che non c'e' piu'.
        if (!visible && hadFocus) DialerBox.Focus();

        // La riga di stato nomina la coda: va rifatta, altrimenti continuerebbe a
        // riportare un conteggio che non ha piu' una sezione a cui riferirsi.
        UpdateStatus();

        // All'avvio non si annuncia: previous vale null solo la prima volta.
        if (previous is null) return;

        // Quando si riattiva si dice anche che il collegamento vero parte al riavvio:
        // il connettore viene scelto all'avvio, quindi la sezione ricompare subito ma
        // resterebbe vuota, e senza dirlo sembrerebbe una coda ferma invece di un
        // collegamento non ancora aperto. E' la stessa cosa che dice il pannello delle
        // impostazioni quando propone di riavviare.
        _announcer.Announce(Announcement.CommandResponse(
            visible
                ? "Collegamento al centralino attivo: la sezione delle chiamate in coda torna nella "
                  + "finestra. Il collegamento si apre al riavvio di BlindPhone"
                : "Collegamento al centralino disattivato: la sezione delle chiamate in coda "
                  + "e' stata tolta dalla finestra",
            DateTimeOffset.Now));
    }

    /// <summary>
    /// Converte la chiusura della finestra in un "metti via", quando richiesto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chiudere la finestra di un softphone significa smettere di ricevere chiamate.
    /// Su un centralino il risultato è che i chiamanti squillano a vuoto e nessuno lo
    /// sa: non c'è nulla a schermo che lo mostri e, per chi non vede, nemmeno il
    /// silenzio è un indizio, perché il silenzio è anche il comportamento normale di
    /// un momento senza traffico.
    /// </para>
    /// <para>
    /// La conversione avviene solo se l'icona nell'area di notifica esiste davvero.
    /// Nascondere la finestra senza icona lascerebbe il programma in esecuzione senza
    /// alcun modo di richiamarlo se non la scorciatoia globale, che un altro
    /// programma può avere occupato.
    /// </para>
    /// <para>
    /// <b>Alt+F4 non arriva qui.</b> Viene intercettato prima, in
    /// <see cref="OnWindowPreviewKeyDown"/>, e chiude davvero. Quello che resta a
    /// questo gestore è la X del titolo e la voce Chiudi del menu di sistema: gesti
    /// da mouse, che è il modo in cui la chiusura viene chiesta per sbaglio.
    /// </para>
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;

        var tray = _settingsService.Current.Tray;
        if (!tray.MinimizeToTrayOnClose || _tray is null) return;

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();

        var restore = _hotkeys.Get(HotkeyCommand.ShowWindow, HotkeyScope.Global)?.SpeechText;
        var quit = _hotkeys.Get(HotkeyCommand.QuitApplication, HotkeyScope.Local)?.SpeechText;

        // Si dice che il telefono resta in servizio, e come tornare indietro. Windows
        // B raggiunge l'area di notifica ed e' la via che funziona sempre, anche se la
        // scorciatoia globale risulta occupata da un altro programma.
        var reopen = restore is null
            ? "Per riaprirlo raggiungi l'icona nell'area di notifica con Windows B."
            : $"Premi {restore} per riaprirlo, oppure raggiungi l'icona con Windows B.";

        var exit = quit is null
            ? "Per uscire davvero usa la voce Esci del menu dell'icona."
            : $"Per uscire davvero riapri la finestra e premi {quit}, oppure usa la voce Esci del menu dell'icona.";

        _announcer.Announce(Announcement.High(
            $"BlindPhone resta in servizio nell'area di notifica: le chiamate continuano ad arrivare. "
            + $"{reopen} {exit}",
            DateTimeOffset.Now));

        _log.LogInformation("Finestra riposta nell'area di notifica");
    }

    /// <summary>
    /// Riporta la finestra davanti a tutto e le fa dare la tastiera.
    /// </summary>
    /// <param name="reason">Motivo da annunciare, oppure <c>null</c> per non annunciare.</param>
    /// <returns>
    /// <c>true</c> se al termine i tasti arrivano alla finestra; <c>false</c> se
    /// Windows ha concesso solo di comparire davanti.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Comparire davanti e ricevere i tasti sono due cose diverse</b>, ed è la
    /// distinzione che qui era andata perduta. Il passaggio momentaneo per
    /// <c>Topmost</c> cambia l'ordine di sovrapposizione, che Windows concede a
    /// chiunque; <c>Activate</c> chiede il primo piano, che Windows nega a chi non ha
    /// ricevuto l'ultimo evento di input — e una chiamata che arriva dalla rete non è
    /// un evento di input. Il valore restituito da quella chiamata non veniva
    /// guardato, così la finestra compariva e i tasti restavano al programma di prima.
    /// </para>
    /// <para>
    /// Per un operatore che non vede il difetto era invisibile e pericoloso: F2
    /// premuto dentro Esplora file rinomina il file selezionato. Il primo piano vero
    /// lo prende <see cref="ForegroundActivator"/>, e quando non ci riesce lo dice.
    /// </para>
    /// <para>
    /// <c>Focus</c> viene dopo, e serve all'altra metà del problema: il primo piano
    /// porta i tasti alla finestra, ma se dentro la finestra non ha il fuoco nessun
    /// elemento i comandi non vengono instradati.
    /// </para>
    /// </remarks>
    private bool RestoreWindow(string? reason)
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();

        Topmost = true;
        Topmost = false;

        var foreground = ForegroundActivator.TryTake(this);

        Focus();

        if (!foreground)
        {
            _log.LogWarning(
                "Finestra portata davanti senza il primo piano di sistema: i comandi locali non arrivano");
        }

        if (reason is not null)
        {
            _announcer.Announce(Announcement.CommandResponse(reason, DateTimeOffset.Now));
        }

        return foreground;
    }

    /// <summary>
    /// Chiude davvero l'applicazione.
    /// </summary>
    /// <remarks>
    /// Con una chiamata in corso si chiede conferma, perché uscire la farebbe cadere e
    /// la richiesta arriva quasi sempre da una combinazione premuta per sbaglio. La
    /// finestra viene prima riaperta: una domanda posta da una finestra nascosta non
    /// verrebbe letta da nessuno screen reader, e l'operatore sentirebbe una richiesta
    /// di conferma senza sapere di cosa.
    /// </remarks>
    private void QuitApplication()
    {
        // La conferenza viene per prima: li' le chiamate attive sono due e
        // ActiveCall vale null, quindi guardando solo quella si uscirebbe senza
        // chiedere nulla proprio nel caso in cui cadono due linee invece di una.
        var conference = _calls.ConferencedCalls;

        var busy = conference.Count > 0
            ? conference[0]
            : _calls.ActiveCall ?? _calls.RingingCall ?? _calls.OutgoingCall;

        if (busy is not null)
        {
            if (!IsVisible) RestoreWindow(null);

            var chi = conference.Count > 0
                ? "una conferenza a tre con " + string.Join(" e ", conference.Select(c => c.RemoteParty.SpeechName))
                : $"una chiamata in corso con {busy.RemoteParty.SpeechName}";

            var cosaCade = conference.Count > 0 ? "le chiamate cadono" : "la chiamata cade";

            var answer = MessageBox.Show(
                this,
                $"C'e' {chi}.\n\n"
                + $"Uscendo da BlindPhone {cosaCade}.\n\nUscire lo stesso?",
                "Esci da BlindPhone",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                _announcer.Announce(Announcement.CommandResponse(
                    conference.Count > 0
                        ? "Uscita annullata: la conferenza continua"
                        : "Uscita annullata: la chiamata continua",
                    DateTimeOffset.Now));
                return;
            }
        }

        _exiting = true;
        _log.LogInformation("Uscita richiesta dall'operatore");

        _announcer.Announce(Announcement.CommandResponse("Chiusura di BlindPhone", DateTimeOffset.Now));
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Avvia i servizi di rete, tollerando che non siano configurati.
    /// </summary>
    /// <remarks>
    /// Un errore di configurazione non deve impedire l'avvio: l'operatore deve poter
    /// entrare nelle impostazioni e sistemare, e per farlo l'applicazione deve
    /// essere in esecuzione. Ogni fallimento viene comunque annunciato, perché
    /// nessuno deve credere di essere in servizio quando non lo è.
    /// </remarks>
    private async Task StartServicesAsync()
    {
        // La modalita' simulata va detta subito, con priorita' critica e senza
        // ambiguita': e' l'unico caso in cui l'applicazione funziona perfettamente
        // ma non serve a nulla, e l'operatore non ha modo di accorgersene da solo.
        if (_telephony.IsSimulated)
        {
            _announcer.Announce(Announcement.Critical(
                "Attenzione: account SIP non configurato. Le chiamate sono simulate e nessuna telefonata "
                + "parte davvero. Premi Control virgola per aprire le impostazioni e configurare l'interno.",
                DateTimeOffset.Now,
                EarconKind.RegistrationLost,
                braille: "SIMULATO"));
        }

        try
        {
            await _telephony.StartAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Avvio del motore telefonico non riuscito");
            _announcer.Announce(Announcement.Critical(
                $"Telefono non avviato. {ex.Message}", DateTimeOffset.Now, EarconKind.RegistrationLost));
        }

        // Prima dell'avvio del connettore: gli eventi di presenza arrivano appena il
        // canale si apre, e chi non fosse ancora in ascolto li perderebbe.
        _presence.Start();

        // Registrazione delle chiamate nell'elenco, e pulizia di quelle scadute: la
        // conservazione si applica all'avvio, che e' il momento in cui il posto
        // operatore ha tempo per farlo senza rubarlo a una conversazione.
        _history.Start();

        // Riallineamento della rubrica dal gestionale, se configurato. Parte dopo la
        // presenza e dopo l'elenco chiamate perche' e' la meno urgente delle tre: una
        // rubrica vecchia di un'ora funziona, un posto operatore senza presenza no.
        ((App)Application.Current).Services.GetRequiredService<ContactsSyncService>().Start();

        var conservazione = CallHistoryRetention.ToTimeSpan(
            CallHistoryRetention.Normalize(_settingsService.Current.CallHistoryRetentionDays));

        _ = _history.PurgeAsync(conservazione, DateTimeOffset.Now);

        try
        {
            await _queueProvider.StartAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Collegamento al centralino non riuscito");
            _announcer.Announce(Announcement.High(
                $"Centralino non collegato. {ex.Message}", DateTimeOffset.Now, EarconKind.PbxLinkDegraded));
        }
    }

    /// <summary>
    /// Registra presso Windows tutte le scorciatoie globali del profilo attivo.
    /// </summary>
    /// <returns>Le combinazioni che il sistema non ha concesso.</returns>
    private List<string> RegisterGlobalHotkeys()
    {
        var refused = new List<string>();

        foreach (var binding in _hotkeys.Bindings.Where(b => b.Scope == HotkeyScope.Global))
        {
            var virtualKey = ToVirtualKey(binding.Key);
            if (virtualKey == 0) continue;

            var conflict = _globalHotkeys.Register(binding, virtualKey);

            if (!conflict.IsClear)
            {
                // Va detto, non nascosto: una scorciatoia globale che non funziona
                // senza spiegazione porta l'operatore a credere che il prodotto sia
                // inaffidabile.
                _log.LogWarning("Scorciatoia globale non registrata: {Message}", conflict.Message);
                refused.Add(binding.SpeechText);
            }
        }

        return refused;
    }

    /// <summary>
    /// Riallinea il posto operatore a una nuova configurazione dei tasti.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Le combinazioni locali seguono da sole: il profilo attivo è un'istanza
    /// condivisa e questa finestra lo interroga a ogni tasto premuto. Le globali no,
    /// perché Windows conosce solo quelle che gli sono state dichiarate: vanno
    /// disdette e registrate di nuovo, altrimenti resterebbero attive le vecchie e le
    /// nuove non funzionerebbero, cioè il caso peggiore per chi sta verificando la
    /// modifica appena fatta.
    /// </para>
    /// <para>
    /// L'esito viene annunciato. Una scorciatoia globale che il sistema rifiuta perché
    /// un altro programma l'ha già registrata è la sola parte della configurazione che
    /// non si può verificare in anticipo, e senza annuncio l'operatore la scoprirebbe
    /// premendo un tasto che non fa niente.
    /// </para>
    /// </remarks>
    private void OnHotkeyProfileChanged(object? sender, HotkeyProfile profile)
        => Dispatcher.Invoke(() =>
        {
            _log.LogInformation(
                "Profilo di scorciatoie cambiato: {Name}, {Count} assegnazioni",
                profile.Name, profile.Bindings.Count);

            _globalHotkeys.UnregisterAll();
            var refused = RegisterGlobalHotkeys();

            // Gli annunci del trasferimento nominano il tasto configurato: senza
            // questo aggiornamento manderebbero a premerne uno non piu' assegnato. In
            // modalita' semplificata nominano Invio, che e' il tasto di quella
            // modalita', qualunque combinazione porti il profilo.
            ApplyTransferKeyName(_settingsService.Current, profile);

            // Stessa cosa per la conferenza, che nomina tre tasti: quello che la apre
            // e la scioglie, e i due che servono a procurarsi la seconda chiamata
            // quando manca.
            _conferenceOptions.ConferenceKeyName = LocalKeyName(profile, HotkeyCommand.Conference, "F7");
            _conferenceOptions.HoldKeyName = LocalKeyName(profile, HotkeyCommand.ToggleHold, "F4");
            _conferenceOptions.DialerKeyName = LocalKeyName(profile, HotkeyCommand.FocusDialer, "F8");

            var message = refused.Count == 0
                ? $"Tasti rapidi aggiornati: profilo {profile.Name} attivo"
                : $"Tasti rapidi aggiornati: profilo {profile.Name} attivo. "
                  + $"Attenzione, {refused.Count} combinazioni globali sono gia' usate da altri programmi "
                  + $"e non funzioneranno fuori da BlindPhone: {string.Join(", ", refused)}";

            _announcer.Announce(Announcement.CommandResponse(message, DateTimeOffset.Now));
        });

    /// <summary>Combinazione locale assegnata a un comando, in forma pronunciabile.</summary>
    private static string LocalKeyName(HotkeyProfile profile, HotkeyCommand command, string fallback)
        => profile.Get(command, HotkeyScope.Local)?.SpeechText ?? fallback;

    /// <summary>
    /// Intercetta i messaggi di sistema diretti a questa finestra.
    /// </summary>
    /// <remarks>
    /// Due mittenti, entrambi esterni all'applicazione. Windows, per le scorciatoie
    /// globali registrate presso di lui. E una seconda copia di BlindPhone appena
    /// avviata, che prima di togliersi di mezzo chiede a questa di farsi vedere:
    /// succede quando l'operatore, non trovando la finestra perché è riposta
    /// nell'area di notifica, riapre il collegamento sul desktop.
    /// </remarks>
    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_globalHotkeys.HandleMessage(msg, wParam))
        {
            handled = true;
            return nint.Zero;
        }

        if (msg == SingleInstanceGuard.ShowWindowMessage)
        {
            handled = true;

            _log.LogInformation("Secondo avvio rilevato: finestra riportata in primo piano");

            // Va detto, altrimenti chi ha appena riaperto il collegamento non capisce
            // perche' non e' successo niente di nuovo: la finestra che torna davanti
            // senza spiegazioni sembra un avvio riuscito, e l'operatore non sa di
            // avere gia' il programma acceso.
            RestoreWindow("BlindPhone era gia' in esecuzione: finestra riportata in primo piano");
        }

        return nint.Zero;
    }

    // -----------------------------------------------------------------------
    // Instradamento dei tasti
    // -----------------------------------------------------------------------

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ALT+F4 CHIUDE DAVVERO, E NON VIENE DEVIATO NELL'AREA DI NOTIFICA.
        //
        // E' il gesto con cui si chiude qualunque finestra di Windows, e per chi non
        // vede e' l'unico: la X del titolo va cercata con il mouse. Finche' arrivava
        // al gestore di chiusura veniva convertito in un "metti via" esattamente come
        // il clic sulla X, e il risultato era il peggiore dei due possibili — il
        // programma restava acceso e chi l'aveva premuto credeva di averlo chiuso.
        //
        // Da qui passa invece per la stessa via del comando Esci, conferma compresa:
        // con una chiamata in corso viene chiesto prima di farla cadere.
        //
        // Sta davanti a tutto il resto perche' deve funzionare sempre — in modalita'
        // toni, in modalita' semplificata, con la postazione in pausa e con il focus
        // dentro qualunque campo.
        if (IsCloseWindowCombination(e))
        {
            // Senza questa riga Windows chiude comunque per conto suo e la chiusura
            // torna a finire nell'area di notifica, oltre a portarsi via anche il
            // caso in cui la conferma viene annullata.
            e.Handled = true;
            QuitApplication();
            return;
        }

        // ESC con il focus dentro l'elenco dei contatti proposti chiude l'elenco e
        // basta. Se passasse al comando di annullamento svuoterebbe la
        // composizione, e il cursore resterebbe su una riga che non esiste piu':
        // l'operatore non avrebbe modo di sapere dove si trova.
        if (e.Key == Key.Escape && SuggestionsGrid.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            HideSuggestions();
            return;
        }

        // I TONI HANNO LA PRECEDENZA SU TUTTO IL RESTO, MA SOLO IN MODALITA' TONI.
        //
        // E' l'unico punto in cui una cifra puo' significare due cose, e la modalita'
        // decide quale: dentro va in linea, fuori va nel campo di composizione. Il
        // controllo sta prima della risoluzione dei comandi perche' in modalita' toni
        // nemmeno un comando assegnato a una cifra deve rubare il tasto: chi sta
        // rispondendo a un menu automatico ha bisogno che il 4 sia un 4.
        if (_dtmfMode && TryHandleDtmfKey(e)) return;

        // IL TASTIERINO VIENE PRIMA DELLE SCORCIATOIE, e per una ragione sola: in
        // modalita' semplificata "+", "/" e "-" non devono poter essere rubati da un
        // comando che qualcuno ci ha assegnato sopra, ne' finire nel campo di
        // composizione. Sono i tasti su cui si regge l'intera modalita', e devono
        // fare sempre la stessa cosa.
        if (TryHandleSimplifiedKeypad(e)) return;

        var modifiers = ToHotkeyModifiers(Keyboard.Modifiers);

        // I TASTI DI SISTEMA ARRIVANO SOTTO FALSO NOME.
        //
        // Quando Windows tratta un tasto come "di sistema" — F10 da solo, e qualunque
        // combinazione con Alt — WPF non lo consegna in e.Key, dove mette il valore
        // segnaposto System, ma in e.SystemKey. Leggendo il solo e.Key si cercava nel
        // profilo un tasto chiamato "System", che non esiste in nessuna mappa: il
        // comando non veniva riconosciuto e il tasto proseguiva verso il gestore
        // predefinito, che per F10 attiva la barra dei menu e porta il cursore sul
        // titolo della finestra.
        //
        // E' il difetto per cui F10 non apriva la rubrica. Riguardava allo stesso modo
        // ogni combinazione con Alt assegnata dal pannello dei tasti rapidi.
        // La normalizzazione serve al tasto Pausa, che con Control premuto arriva sotto
        // il nome Cancel: vedi HotkeyKeyNames.Normalize.
        var keyName = HotkeyKeyNames.Normalize(
            HotkeyKeyNames.ResolveSystemKey(e.Key.ToString(), e.SystemKey.ToString()));

        var command = _hotkeys.Resolve(keyName, modifiers, HotkeyScope.Local);

        // POSTAZIONE IN PAUSA: chi prova a digitare va avvisato, non lasciato a vuoto.
        //
        // Il campo di composizione e' bloccato, quindi le lettere e le cifre non
        // finiscono da nessuna parte. Senza questo ramo il risultato sarebbe una
        // tastiera che non scrive e non dice niente, che per chi non vede lo schermo e'
        // indistinguibile da un guasto: si continuerebbe a digitare un numero intero
        // prima di accorgersi che non e' successo nulla.
        //
        // Sta prima del rientro per comando nullo, cosi' intercetta anche le lettere a
        // cui l'operatore ha assegnato un comando. Tutto il resto — tasti funzione,
        // Esc, le combinazioni con Control o Alt, e ovviamente il tasto che toglie la
        // pausa — continua a funzionare.
        if (_duty.IsPaused && IsTypingKey(keyName, modifiers))
        {
            e.Handled = true;
            AnnouncePausedTyping();
            return;
        }

        if (command is null) return;

        // Il campo di composizione deve poter ricevere le cifre: se il comando e'
        // un tasto singolo senza modificatori e il focus e' nel dialer, si lascia
        // passare solo se non e' un tasto funzione.
        //
        // Il controllo passa da HotkeyKeyNames e non dall'iniziale del nome: anche la
        // lettera F comincia per F, e prima di questa correzione assegnarle un comando
        // le impediva di essere digitata. Con la mappa dei tasti configurabile il caso
        // ha smesso di essere teorico.
        if (modifiers == HotkeyModifiers.None
            && DialerBox.IsKeyboardFocusWithin
            && !HotkeyKeyNames.IsFunctionKey(keyName)
            && e.Key != Key.Escape)
        {
            // Va registrato: e' l'unico caso in cui un tasto riconosciuto NON
            // esegue il comando, e davanti a un "ho premuto e non e' successo
            // niente" e' la prima cosa da escludere.
            _log.LogDebug("Tasto {Key} lasciato al campo di composizione, comando {Command} non eseguito",
                keyName, command.Value);
            return;
        }

        e.Handled = true;
        _ = ExecuteCommandAsync(command.Value);
    }

    /// <summary>
    /// Manda in linea il tasto premuto, se è una cifra valida per un menu automatico.
    /// </summary>
    /// <param name="e">Tasto premuto.</param>
    /// <returns><c>true</c> se il tasto è stato consumato dalla modalità toni.</returns>
    /// <remarks>
    /// <para>
    /// Esc esce dalla modalità: è la via d'uscita che si prova per istinto quando si
    /// sospetta di essere nel posto sbagliato, e trovarla funzionante vale più di
    /// qualunque annuncio.
    /// </para>
    /// <para>
    /// I tasti che non sono cifre passano oltre senza essere consumati: in modalità
    /// toni si deve poter continuare a riagganciare, mettere in attesa e chiedere lo
    /// stato, altrimenti l'operatore si troverebbe chiuso dentro un menu automatico
    /// senza comandi.
    /// </para>
    /// </remarks>
    private bool TryHandleDtmfKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            LeaveDtmfMode("Modalita' toni disattivata: le cifre tornano nel campo di composizione");
            return true;
        }

        var cifra = DtmfDigitFor(e.Key);
        if (cifra is null) return false;

        e.Handled = true;
        _ = _calls.SendDtmfAsync(cifra.Value);

        return true;
    }

    /// <summary>
    /// Traduce un tasto nella cifra corrispondente, riga superiore e tastierino.
    /// </summary>
    /// <remarks>
    /// Il tastierino numerico va compreso: è quello che un operatore usa per i menu
    /// automatici, perché ha le dita già lì e non deve cercare i numeri sulla riga
    /// superiore. Asterisco e cancelletto sono i due tasti con cui i menu confermano e
    /// annullano, e senza di essi la modalità sarebbe monca.
    /// </remarks>
    private static char? DtmfDigitFor(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 when Keyboard.Modifiers == ModifierKeys.None => (char)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
        Key.Multiply => '*',

        // Maiusc 3 su tastiera italiana produce il cancelletto, e il tastierino non ne
        // ha uno: si accetta anche la forma con il modificatore.
        Key.D3 when Keyboard.Modifiers == ModifierKeys.Shift => '#',
        Key.Oem2 or Key.OemQuestion when Keyboard.Modifiers == ModifierKeys.Shift => '#',
        _ => null,
    };

    /// <summary>
    /// Riconosce Alt+F4, la combinazione con cui Windows chiude una finestra.
    /// </summary>
    /// <param name="e">Tasto premuto o rilasciato.</param>
    /// <remarks>
    /// <para>
    /// Arriva sotto falso nome, come ogni combinazione con Alt: WPF mette il
    /// segnaposto <c>System</c> in <c>e.Key</c> e il tasto vero in
    /// <c>e.SystemKey</c>. Leggendo il solo <c>e.Key</c> non si troverebbe mai.
    /// </para>
    /// <para>
    /// Il modificatore si legge da <see cref="Keyboard.Modifiers"/> e non si dà per
    /// scontato: F4 può risultare di sistema anche senza Alt, e chiudere la
    /// postazione per un F4 premuto da solo sarebbe il difetto peggiore di quello
    /// che si sta correggendo.
    /// </para>
    /// </remarks>
    private static bool IsCloseWindowCombination(KeyEventArgs e)
        => e.Key == Key.System
           && e.SystemKey == Key.F4
           && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

    /// <summary>
    /// Impedisce che il rilascio di un tasto di sistema assegnato a un comando apra
    /// la modalità menu di Windows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non basta gestire la pressione. Per F10 e per le combinazioni con Alt, quello
    /// che fa entrare Windows in modalità menu è il <b>rilascio</b>: il comando viene
    /// eseguito correttamente e subito dopo il cursore salta sul titolo della
    /// finestra, che è il sintomo osservato con F10 assegnato alla rubrica.
    /// </para>
    /// <para>
    /// Si sopprime solo il rilascio dei tasti che risultano assegnati a un comando: un
    /// F10 non assegnato continua a comportarsi come in qualunque altro programma
    /// Windows, e non si toglie all'operatore un comportamento di sistema che potrebbe
    /// aspettarsi.
    /// </para>
    /// </remarks>
    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // Alt+F4 non passa dal profilo — non e' assegnabile — quindi il controllo qui
        // sotto non la riconoscerebbe, e il suo rilascio e' esattamente il caso
        // descritto: se la conferma di uscita viene annullata la finestra resta
        // aperta, e senza questa riga il cursore finirebbe sulla barra dei menu.
        if (IsCloseWindowCombination(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.System) return;

        var keyName = HotkeyKeyNames.ResolveSystemKey(e.Key.ToString(), e.SystemKey.ToString());
        var modifiers = ToHotkeyModifiers(Keyboard.Modifiers);

        if (_hotkeys.Resolve(keyName, modifiers, HotkeyScope.Local) is null) return;

        e.Handled = true;
    }

    private void OnGlobalHotkeyPressed(object? sender, HotkeyCommand command)
        => Dispatcher.Invoke(() =>
        {
            _log.LogInformation("Scorciatoia globale {Command}", command);
            _ = ExecuteCommandAsync(command);
        });

    /// <summary>
    /// Esegue un comando dell'operatore.
    /// </summary>
    /// <param name="command">Comando da eseguire.</param>
    /// <remarks>
    /// Ogni ramo produce un effetto <b>e</b> un riscontro: non esiste comando muto.
    /// Un comando che non risponde è indistinguibile da un comando che non ha
    /// funzionato, e porta l'operatore a premere di nuovo, spesso peggiorando la
    /// situazione.
    /// </remarks>
    private async Task ExecuteCommandAsync(HotkeyCommand command)
    {
        // UNA RIGA PER OGNI COMANDO, con lo stato al momento in cui arriva.
        //
        // Risponde alla domanda che davanti a una segnalazione non aveva risposta:
        // il tasto e' arrivato? E se e' arrivato, su quale stato ha agito? Senza,
        // "ESC non funziona" e "ESC funziona ma non c'era niente da annullare" sono
        // indistinguibili, e si finisce a correggere per ipotesi.
        _log.LogInformation(
            "Comando {Command}. Trasferimento {Phase}, attiva {Active}, entrante {Ringing}, "
            + "uscente {Outgoing}, in trasferimento {Transferring}, in attesa {Held}",
            command,
            _transfer.Phase,
            _calls.ActiveCall?.Id ?? "-",
            _calls.RingingCall?.Id ?? "-",
            _calls.OutgoingCall?.Id ?? "-",
            _calls.TransferringCall?.Id ?? "-",
            _calls.HeldCalls.Count);

        try
        {
            switch (command)
            {
                // Rispondere e chiudere sono i due momenti in cui l'operatore si
                // prepara a digitare: dopo la risposta per annunciare la chiamata a un
                // interno, dopo la chiusura per la telefonata seguente. Il cursore ci
                // va da solo, con l'eventuale residuo gia' selezionato.
                case HotkeyCommand.Answer:
                    await _calls.AnswerAsync().ConfigureAwait(true);
                    PrepareDialerForNextDestination();
                    break;

                case HotkeyCommand.HangUp:
                    await HandleHangUpKeyAsync().ConfigureAwait(true);
                    PrepareDialerForNextDestination();
                    break;

                case HotkeyCommand.ToggleHold:
                    await _calls.ToggleHoldAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.Swap:
                    await HandleSwapKeyAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.Conference:
                    await _calls.ToggleConferenceAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.Transfer:
                    await HandleTransferKeyAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.Cancel:
                    await HandleCancelAsync().ConfigureAwait(true);
                    break;

                // INCLUSIONE SU INTERNO OCCUPATO.
                //
                // L'orchestratore rifiuta da se' quando le condizioni non ci sono, e
                // lo dice: qui non si duplica il controllo, perche' due risposte
                // diverse alla stessa domanda sono il modo in cui un pulsante finisce
                // per esistere e non funzionare.
                case HotkeyCommand.BargeIn:
                    await _transfer.BargeInAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.ToggleMute:
                    _telephony.SetMuted(!_telephony.IsMuted);
                    _announcer.Announce(Announcement.CommandResponse(
                        _telephony.IsMuted ? "Microfono silenziato" : "Microfono attivo",
                        DateTimeOffset.Now));
                    break;

                case HotkeyCommand.FocusDialer:
                    DialerBox.Focus();
                    DialerBox.SelectAll();
                    _announcer.Announce(Announcement.CommandResponse("Campo di composizione", DateTimeOffset.Now));
                    break;

                case HotkeyCommand.FocusColleagues:
                    FocusColleaguesList();
                    break;

                case HotkeyCommand.FocusQueue:
                    FocusQueueList();
                    break;

                case HotkeyCommand.FocusActiveCall:
                    AnnounceActiveCall();
                    break;

                case HotkeyCommand.AnnounceQueueCount:
                    HandleQueueCountKey();
                    break;

                case HotkeyCommand.AnnounceQueueList:
                    _queue.AnnounceWaitingList();
                    break;

                case HotkeyCommand.AnnounceStatusSummary:
                    AnnounceStatusSummary();
                    break;

                case HotkeyCommand.AnnounceCallDuration:
                    AnnounceCallDuration();
                    break;

                case HotkeyCommand.AnnounceLineQuality:
                    AnnounceLineQuality();
                    break;

                case HotkeyCommand.AnnounceAgentState:
                    _queue.AnnounceAgentState();
                    break;

                case HotkeyCommand.AnnounceExtensionState:
                    await AnnounceExtensionStateAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.RepeatLastAnnouncement:
                    _announcer.RepeatLast();
                    break;

                case HotkeyCommand.SilenceSpeech:
                    _announcer.Silence();
                    break;

                case HotkeyCommand.RedialLast:
                    await RedialAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.JoinMeeting:
                    await JoinMeetingAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.ToggleDtmfMode:
                    ToggleDtmfMode();
                    break;

                case HotkeyCommand.SendDialerAsDtmf:
                    await SendDialerAsDtmfAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.ToggleAgentLogin:
                    await ToggleAgentLoginAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.ToggleAgentPause:
                    await ToggleAgentPauseAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.ToggleServicePause:
                    await ToggleServicePauseAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.ToggleCallRecording:
                    await _recording
                        .ToggleAsync(LocalKeyName(_hotkeys, HotkeyCommand.ToggleCallRecording, "Alt R"))
                        .ConfigureAwait(true);
                    break;

                case HotkeyCommand.PickSelectedQueueCall:
                    await PickSelectedQueueCallAsync().ConfigureAwait(true);
                    break;

                case HotkeyCommand.AnnounceHotkeyList:
                    AnnounceHotkeyList();
                    break;

                case HotkeyCommand.OpenSettings:
                    OpenSettings();
                    break;

                case HotkeyCommand.OpenContacts:
                    OpenContacts();
                    break;

                case HotkeyCommand.OpenCallHistory:
                    OpenCallHistory();
                    break;

                case HotkeyCommand.OpenHelp:
                    OpenHelp();
                    break;

                case HotkeyCommand.OpenDiagnostics:
                    OpenSettings(diagnostics: true);
                    break;

                case HotkeyCommand.SendSupportPackage:
                    OpenSupportReport();
                    break;

                case HotkeyCommand.OpenSipAccounts:
                    OpenSipAccounts();
                    break;

                case HotkeyCommand.ShowWindow:
                    RestoreWindow(IsVisible ? "Finestra di BlindPhone in primo piano" : "BlindPhone riaperto");
                    break;

                case HotkeyCommand.QuitApplication:
                    QuitApplication();
                    break;

                default:
                    _announcer.Announce(Announcement.CommandResponse(
                        "Funzione non ancora disponibile", DateTimeOffset.Now));
                    _announcer.PlayEarcon(EarconKind.CommandRejected);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Esecuzione del comando {Command} non riuscita", command);
            _announcer.Announce(Announcement.CommandResponse(
                "Il comando non e' riuscito", DateTimeOffset.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
        }
        finally
        {
            // Qualunque comando puo' aver cambiato quello che si puo' fare adesso:
            // silenziare il microfono, entrare in modalita' toni, avviare una
            // registrazione. Riallineare qui la barra evita di doversi ricordare di
            // farlo in ciascuno dei trenta rami dello switch — cioe' evita l'unico
            // modo in cui la barra puo' mentire.
            UpdateCommandBar();
        }
    }

    // -----------------------------------------------------------------------
    // La barra dei comandi, per chi usa il mouse
    // -----------------------------------------------------------------------

    /// <summary>Firma dei pulsanti mostrati, per non ricostruire la barra a vuoto.</summary>
    private string _commandBarSignature = string.Empty;

    /// <summary>
    /// Rifà la barra dei pulsanti se le azioni disponibili sono cambiate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Si ricostruisce solo quando cambia davvero qualcosa.</b> Questo metodo viene
    /// richiamato dopo ogni comando, a ogni transizione di chiamata e a ogni tasto
    /// battuto nel campo di composizione: ricostruire ogni volta toglierebbe il focus
    /// dal pulsante su cui si trova l'operatore e farebbe rileggere la barra alla
    /// tecnologia assistiva a ogni cifra digitata.
    /// </para>
    /// <para>
    /// Le regole di quali pulsanti mostrare non stanno qui ma in
    /// <see cref="CallActionCatalog"/>, dove si collaudano stato per stato. Qui resta
    /// il solo atto di costruirli.
    /// </para>
    /// </remarks>
    private void UpdateCommandBar()
    {
        var contesto = CurrentActionContext();

        // Le spie della cuffia si aggiornano qui, prima del confronto con la firma che
        // fa uscire presto: la barra puo' restare uguale mentre la cuffia deve cambiare,
        // come quando si silenzia il microfono durante una consultazione.
        _headset?.Reflect(contesto);

        var azioni = CallActionCatalog.Available(contesto);

        // Nella firma entra anche l'acceso e spento: «Registra» disponibile e
        // «Registra» spento sono lo stesso pulsante ma non lo stesso pulsante, e senza
        // questo la barra resterebbe ferma mentre la chiamata va in attesa.
        var signature = string.Join('|', azioni.Select(a => $"{a.Action}:{a.IsEnabled}"));
        if (signature == _commandBarSignature) return;

        _commandBarSignature = signature;

        // Se il cursore era su un pulsante, i pulsanti che sta per sostituire se lo
        // porterebbero via: il focus tornerebbe alla finestra, e chi si muove a
        // tastiera dovrebbe ricominciare il giro di tabulazione da capo proprio nel
        // momento in cui la situazione e' cambiata.
        var focusEraNellaBarra = CommandBar.IsKeyboardFocusWithin;

        CommandBar.Items.Clear();

        CallActionGroup? gruppoPrecedente = null;

        foreach (var azione in azioni)
        {
            // Un filo verticale fra una famiglia e l'altra: separa i comandi della
            // chiamata da quelli della conversazione e da quelli della postazione.
            // Senza, dodici pulsanti in fila sono un muro in cui si cerca a occhio
            // invece di andare a colpo sicuro.
            if (gruppoPrecedente is { } precedente && precedente != azione.Group)
            {
                CommandBar.Items.Add(CreateGroupSeparator());
            }

            gruppoPrecedente = azione.Group;
            CommandBar.Items.Add(CreateCommandButton(azione));
        }

        if (!focusEraNellaBarra) return;

        // I contenitori esistono solo dopo il passaggio di disposizione: chiedere il
        // focus adesso non troverebbe nulla da mettere a fuoco.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                // Il primo elemento e' sempre un pulsante, ma i separatori stanno
                // nella stessa raccolta: si cerca il primo che si possa mettere a
                // fuoco invece di dare per scontata la posizione.
                foreach (var elemento in CommandBar.Items)
                {
                    if (elemento is not System.Windows.Controls.Button { IsEnabled: true } primo) continue;

                    primo.Focus();
                    return;
                }
            });
    }

    /// <summary>Costruisce un pulsante della barra.</summary>
    /// <param name="descriptor">Azione, pittogramma, etichetta e descrizione.</param>
    /// <remarks>
    /// <para>
    /// <b>Sul pulsante c'è il nome della funzione, nel fumetto tutto il resto.</b> Il
    /// tasto rapido scritto accanto all'etichetta raddoppiava la larghezza di ogni
    /// pulsante — «Interrompi registrazione (Alt R)» — e in una barra da dodici voci
    /// quella lunghezza si paga tutta insieme. Nel fumetto ci sta comodo, insieme
    /// alla frase che dice cosa succede premendo.
    /// </para>
    /// <para>
    /// Il nome accessibile resta la sola etichetta, senza pittogramma: gli screen
    /// reader leggono i pittogrammi per nome — «telefono nero», «tazza calda» — e
    /// davanti a ogni comando sarebbe rumore. Quello che serve sapere sta nel testo di
    /// aiuto, che è dove la tecnologia assistiva lo cerca.
    /// </para>
    /// </remarks>
    private System.Windows.Controls.Button CreateCommandButton(CallActionButton descriptor)
    {
        var tasto = HotkeyTextFor(descriptor.Action);
        var spiegazione = tasto is null
            ? descriptor.Description
            : $"{descriptor.Description}.\nScorciatoia: {tasto}";

        var button = new System.Windows.Controls.Button
        {
            Content = BuildCommandContent(descriptor),
            Style = (Style)FindResource("CommandButton"),
            Tag = descriptor.Action,
            IsEnabled = descriptor.IsEnabled,
            ToolTip = spiegazione,
        };

        AutomationProperties.SetName(button, descriptor.Label);
        AutomationProperties.SetHelpText(
            button,
            tasto is null
                ? descriptor.Description
                : $"{descriptor.Description}. Scorciatoia da tastiera: {tasto}");

        button.Click += OnCommandButtonClick;
        return button;
    }

    /// <summary>
    /// Compone pittogramma ed etichetta di un pulsante della barra.
    /// </summary>
    /// <param name="descriptor">Azione, pittogramma ed etichetta.</param>
    /// <remarks>
    /// <para>
    /// <b>Due caselle di testo e non una stringa sola</b>, perché il pittogramma e la
    /// parola vogliono famiglie di caratteri diverse: il glifo vive in Segoe Fluent
    /// Icons e la parola nel carattere di sistema. Concatenandoli in un'unica stringa
    /// il glifo finirebbe reso dal carattere del testo, che non lo conosce, e al suo
    /// posto comparirebbe un rettangolo vuoto.
    /// </para>
    /// <para>
    /// <b>Il pittogramma è decorativo e va dichiarato tale.</b> Il nome accessibile
    /// vuoto lo rende muto per lo screen reader: senza, il lettore annuncerebbe un
    /// carattere sconosciuto davanti a ogni comando. Il nome accessibile del pulsante
    /// resta la sola etichetta, come prima.
    /// </para>
    /// <para>
    /// La famiglia porta due nomi: il secondo è il font di icone di Windows 10, che
    /// serve se la postazione non è ancora stata aggiornata a Windows 11 — i punti di
    /// codice usati qui esistono in entrambi.
    /// </para>
    /// </remarks>
    private static object BuildCommandContent(CallActionButton descriptor)
    {
        if (string.IsNullOrEmpty(descriptor.Icon)) return descriptor.Label;

        var riga = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };

        var glifo = new System.Windows.Controls.TextBlock
        {
            Text = descriptor.Icon,
            FontFamily = IconFont,
            FontSize = 15,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        AutomationProperties.SetName(glifo, string.Empty);

        var etichetta = new System.Windows.Controls.TextBlock
        {
            Text = descriptor.Label,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // IL COLORE SI PRENDE DAL PULSANTE, non dallo stile comune delle caselle di
        // testo. Quello stile assegna il colore del tema, che qui vincerebbe
        // sull'ereditarieta': su un pulsante colorato il testo diventerebbe
        // illeggibile, e su un pulsante disattivato resterebbe acceso mentre il
        // contorno sbiadisce — cioe' il contrario di quello che deve dire.
        FollowButtonForeground(glifo);
        FollowButtonForeground(etichetta);

        riga.Children.Add(glifo);
        riga.Children.Add(etichetta);

        return riga;
    }

    /// <summary>Lega il colore di una casella di testo a quello del pulsante che la contiene.</summary>
    private static void FollowButtonForeground(System.Windows.Controls.TextBlock text)
        => text.SetBinding(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor,
                    typeof(System.Windows.Controls.Button),
                    1),
            });

    /// <summary>Famiglia dei pittogrammi, con il ripiego per Windows 10.</summary>
    private static readonly System.Windows.Media.FontFamily IconFont =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>
    /// Filo verticale che separa due famiglie di pulsanti.
    /// </summary>
    /// <remarks>
    /// Decorazione e basta: non è raggiungibile, non ha nome accessibile e non compare
    /// nel giro di tabulazione. Chi non vede non perde nulla — per lui il
    /// raggruppamento non esiste — e chi guarda trova i comandi divisi per famiglia
    /// invece che in un elenco unico.
    /// </remarks>
    private static System.Windows.Controls.Border CreateGroupSeparator() => new()
    {
        Width = 1,
        Height = 24,
        Margin = new Thickness(4, 0, 12, 8),
        VerticalAlignment = VerticalAlignment.Center,
        Background = System.Windows.SystemColors.ControlDarkBrush,
        Opacity = 0.6,
        Focusable = false,
        IsHitTestVisible = false,
    };

    /// <summary>Scorciatoia da tastiera dell'azione, come la legge l'operatore.</summary>
    private string? HotkeyTextFor(CallAction action)
    {
        var command = HotkeyFor(action);
        return command is null ? null : _hotkeys.Get(command.Value, HotkeyScope.Local)?.SpeechText;
    }

    /// <summary>
    /// Comando di tastiera corrispondente a un'azione della barra, se ne ha uno.
    /// </summary>
    /// <remarks>
    /// Le tre voci del trasferimento condividono un tasto solo, che cambia significato
    /// con la fase: sul pulsante si scrive quello, perché è il tasto che in quel
    /// momento fa la stessa cosa. Il trasferimento cieco fa eccezione — da tastiera lo
    /// si manda con Invio dal campo di composizione — e per questo non dichiara
    /// scorciatoia: scrivere il tasto del trasferimento su un pulsante che trasferisce
    /// alla cieca insegnerebbe una cosa falsa.
    /// </remarks>
    private static HotkeyCommand? HotkeyFor(CallAction action) => action switch
    {
        CallAction.Answer => HotkeyCommand.Answer,
        CallAction.Reject or CallAction.HangUp => HotkeyCommand.HangUp,
        CallAction.Hold or CallAction.Resume => HotkeyCommand.ToggleHold,
        CallAction.Swap => HotkeyCommand.Swap,
        CallAction.ConferenceStart or CallAction.ConferenceEnd => HotkeyCommand.Conference,
        CallAction.TransferStart or CallAction.TransferAttended or CallAction.TransferComplete
            => HotkeyCommand.Transfer,
        CallAction.TransferCancel => HotkeyCommand.Cancel,
        CallAction.BargeIn => HotkeyCommand.BargeIn,
        CallAction.RecordStart or CallAction.RecordStop => HotkeyCommand.ToggleCallRecording,
        CallAction.DtmfOn or CallAction.DtmfOff => HotkeyCommand.ToggleDtmfMode,
        CallAction.MuteOn or CallAction.MuteOff => HotkeyCommand.ToggleMute,
        CallAction.ServicePauseOn or CallAction.ServicePauseOff => HotkeyCommand.ToggleServicePause,
        CallAction.OpenContacts => HotkeyCommand.OpenContacts,
        CallAction.OpenCallHistory => HotkeyCommand.OpenCallHistory,
        CallAction.OpenSettings => HotkeyCommand.OpenSettings,
        CallAction.OpenSipAccounts => HotkeyCommand.OpenSipAccounts,
        _ => null,
    };

    private async void OnCommandButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: CallAction action }) return;

        _log.LogInformation("Comando dal pulsante {Action}", action);
        await ExecuteCallActionAsync(action).ConfigureAwait(true);
    }

    /// <summary>
    /// Esegue l'azione di un pulsante.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quasi tutte passano per <see cref="ExecuteCommandAsync"/>, cioè per lo stesso
    /// percorso della tastiera: stesso effetto, stesso annuncio, stessa riga di
    /// registro, e nessun secondo comportamento da tenere allineato al primo.
    /// </para>
    /// <para>
    /// Fanno eccezione le due modalità di trasferimento, che sulla tastiera si
    /// distinguono per la doppia pressione e sul pulsante non possono: vedi
    /// <see cref="TransferOrchestrator.AttendedTransferNowAsync"/>.
    /// </para>
    /// </remarks>
    private async Task ExecuteCallActionAsync(CallAction action)
    {
        switch (action)
        {
            case CallAction.TransferBlind:
                await TransferFromButtonAsync(blind: true).ConfigureAwait(true);
                break;

            case CallAction.TransferAttended:
                await TransferFromButtonAsync(blind: false).ConfigureAwait(true);
                break;

            case CallAction.OpenUpdate:
                ShowUpdateWindow();
                break;

            default:
                if (HotkeyFor(action) is { } command)
                {
                    await ExecuteCommandAsync(command).ConfigureAwait(true);
                }

                break;
        }

        UpdateCommandBar();
    }

    /// <summary>Manda il trasferimento nella modalità scelta con il pulsante.</summary>
    /// <param name="blind">
    /// <c>true</c> per mandare il chiamante senza annunciarlo, <c>false</c> per
    /// chiamare prima l'interno.
    /// </param>
    /// <remarks>
    /// La digitazione si dichiara conclusa <b>prima</b> di trasferire, come sul
    /// percorso da tastiera: il campo contiene ancora la destinazione mentre il
    /// trasferimento parte, e con la digitazione ancora aperta la conferma — che su
    /// questo percorso è l'unico riscontro — verrebbe soppressa.
    /// </remarks>
    private async Task TransferFromButtonAsync(bool blind)
    {
        await FlushPendingSuggestionSearchAsync().ConfigureAwait(true);

        var destination = CurrentDestination();
        _context.StopTyping();

        if (blind)
        {
            await _transfer.BlindTransferNowAsync(destination).ConfigureAwait(true);
            DialerBox.Clear();
            return;
        }

        await _transfer.AttendedTransferNowAsync(destination).ConfigureAwait(true);
    }

    /// <summary>
    /// Gestisce il tasto del trasferimento, passando la destinazione dal campo di
    /// composizione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il numero già digitato non viene buttato via.</b> È il punto che rendeva
    /// scomoda la funzione più usata del posto operatore: il chiamante detta il
    /// numero mentre parla, l'operatore lo scrive nel campo in quel momento, e prima
    /// il tasto di trasferimento svuotava il campo. L'unica sequenza praticabile
    /// diventava annotare il numero su un foglio, premere il tasto e ridigitarlo —
    /// con il chiamante già in attesa e nessuno a cui chiedere conferma.
    /// </para>
    /// <para>
    /// Ora se il campo contiene già qualcosa quel valore diventa la destinazione, e
    /// l'annuncio lo rilegge cifra per cifra prima che parta qualunque cosa.
    /// </para>
    /// </remarks>
    private async Task HandleTransferKeyAsync()
    {
        // Vale anche qui: se nel campo c'e' un nome e la ricerca non e' ancora
        // partita, la destinazione sarebbe le lettere digitate.
        await FlushPendingSuggestionSearchAsync().ConfigureAwait(true);

        if (_transfer.Phase == TransferPhase.EnteringDestination)
        {
            var destination = CurrentDestination();
            if (!string.IsNullOrWhiteSpace(destination))
            {
                await _transfer.SetDestinationAsync(destination).ConfigureAwait(true);
            }
        }

        var wasIdle = _transfer.Phase == TransferPhase.Idle;
        var alreadyTyped = wasIdle ? CurrentDestination() : string.Empty;

        // PREMERE IL TASTO DI TRASFERIMENTO SIGNIFICA AVER FINITO DI DIGITARE, E VALE
        // DA QUALUNQUE FASE.
        //
        // Senza dirlo il campo resta pieno, la soppressione degli annunci durante la
        // digitazione continua a valere e si perde "chiamata a 3491234567, in corso",
        // cioe' il solo modo di sapere che l'interno sta squillando.
        //
        // Stava dentro il ramo della fase di inserimento, dove bastava finche' la
        // consultazione partiva dalla seconda pressione. Adesso parte dalla prima,
        // che avviene a trasferimento fermo: li' quel ramo non passa, e l'annuncio
        // sarebbe stato scartato proprio nel caso piu' frequente.
        _context.StopTyping();

        if (!string.IsNullOrWhiteSpace(alreadyTyped))
        {
            await _transfer.HandleTransferKeyAsync(alreadyTyped).ConfigureAwait(true);
        }
        else
        {
            await _transfer.HandleTransferKeyAsync().ConfigureAwait(true);
        }

        if (wasIdle && _transfer.Phase == TransferPhase.EnteringDestination)
        {
            // Il campo si svuota solo se non c'era nulla da conservare. Il focus ci
            // va comunque: sia per digitare la destinazione, sia per correggere
            // quella appena riletta se una cifra e' sbagliata.
            if (string.IsNullOrWhiteSpace(alreadyTyped)) DialerBox.Clear();

            DialerBox.Focus();
            DialerBox.CaretIndex = DialerBox.Text.Length;
        }
    }

    /// <summary>
    /// Gestisce il tasto di riaggancio, che durante la consultazione ha un
    /// significato diverso.
    /// </summary>
    /// <remarks>
    /// Se l'interno a cui si sta annunciando la chiamata non la vuole, riagganciare
    /// significa chiudere la linea con lui e tornare al chiamante — non chiudere una
    /// delle due linee e lasciare l'altra in sospeso. Fuori dalla consultazione il
    /// tasto mantiene il comportamento di sempre.
    /// </remarks>
    private async Task HandleHangUpKeyAsync()
    {
        if (await _transfer.HandleHangUpKeyAsync().ConfigureAwait(true))
        {
            _log.LogInformation("Riaggancio assorbito dal trasferimento: chiusa la linea con l'interno");
            return;
        }

        var closed = await _calls.HangUpAsync().ConfigureAwait(true);
        _log.LogInformation("Riaggancio sulla chiamata corrente: esito {Closed}", closed);
    }

    /// <summary>
    /// Gestisce il tasto di alternanza, che durante un trasferimento agisce su una
    /// coppia di linee diversa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fuori dal trasferimento le due linee sono una in conversazione e una in attesa,
    /// e le alterna il gestore delle chiamate. Durante un trasferimento con annuncio
    /// sono il chiamante parcheggiato dentro la procedura e l'interno che si sta
    /// consultando: il chiamante non risulta «in attesa», quindi l'alternanza
    /// ordinaria non lo trovava e rispondeva che non c'era nulla da alternare.
    /// </para>
    /// <para>
    /// L'orchestratore assorbe il tasto solo quando ha davvero una procedura aperta:
    /// altrimenti restituisce <c>false</c> e si prosegue con il comportamento di
    /// sempre. È la stessa divisione del tasto di riaggancio, che pure cambia
    /// significato durante la consultazione.
    /// </para>
    /// </remarks>
    private async Task HandleSwapKeyAsync()
    {
        if (await _transfer.HandleSwapKeyAsync().ConfigureAwait(true))
        {
            _log.LogInformation("Alternanza assorbita dal trasferimento in corso");
            return;
        }

        await _calls.SwapAsync().ConfigureAwait(true);
    }

    private async Task HandleCancelAsync()
    {
        if (_transfer.Phase != TransferPhase.Idle)
        {
            var cancelled = await _transfer.CancelAsync().ConfigureAwait(true);
            _log.LogInformation("Annullamento del trasferimento: esito {Cancelled}", cancelled);
            return;
        }

        if (!string.IsNullOrEmpty(DialerBox.Text))
        {
            DialerBox.Clear();
            _announcer.Announce(Announcement.CommandResponse("Composizione annullata", DateTimeOffset.Now));
            return;
        }

        _announcer.Silence();
    }

    /// <summary>
    /// Distingue la pressione singola dalla doppia sul tasto del conteggio coda.
    /// </summary>
    /// <remarks>
    /// La doppia pressione legge l'elenco completo. Si usa lo stesso tasto perché
    /// l'operatore lo percepisce come un unico gesto: prima "quante sono", poi
    /// "chi sono".
    /// </remarks>
    private void HandleQueueCountKey()
    {
        var now = DateTimeOffset.Now;
        var window = TimeSpan.FromMilliseconds(_settings.Accessibility.DoubleTapWindowMs);

        if (now - _lastQueueCountKeyPress <= window)
        {
            _lastQueueCountKeyPress = DateTimeOffset.MinValue;
            _queue.AnnounceWaitingList();
            return;
        }

        _lastQueueCountKeyPress = now;
        _queue.AnnounceCount();
    }

    private void FocusQueueList()
    {
        // Con la sezione nascosta non c'e' nulla su cui portare il cursore: WPF
        // rifiuterebbe il focus in silenzio e il comando resterebbe muto, che e'
        // indistinguibile da un tasto che non funziona.
        if (_queueSectionVisible == false)
        {
            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.QueueLinkDisabled, DateTimeOffset.Now));
            return;
        }

        QueueList.Focus();

        if (_queueRows.Count == 0)
        {
            _announcer.Announce(Announcement.CommandResponse("Elenco coda vuoto", DateTimeOffset.Now));
            return;
        }

        if (QueueList.SelectedIndex < 0) QueueList.SelectedIndex = 0;

        _announcer.Announce(Announcement.CommandResponse(
            $"Elenco coda, {SpeechFormatter.Count(_queueRows.Count, "chiamata", "chiamate")}",
            DateTimeOffset.Now));
    }

    /// <summary>
    /// Chiamate di cui parlano i comandi informativi.
    /// </summary>
    /// <remarks>
    /// In conferenza non esiste una chiamata attiva: ce ne sono due, e i comandi che
    /// chiedono «con chi sono» o «da quanto» devono rispondere di entrambe. Prima che
    /// esistesse la conferenza bastava <c>ActiveCall</c>; oggi usarla da sola
    /// risponderebbe «nessuna chiamata attiva» con due persone in linea, che è il tipo
    /// di bugia che l'operatore non ha modo di verificare.
    /// </remarks>
    private IReadOnlyList<Call> CallsInEar()
    {
        var conference = _calls.ConferencedCalls;
        if (conference.Count > 0) return conference;

        return _calls.ActiveCall is { } active ? [active] : [];
    }

    private void AnnounceActiveCall()
    {
        var calls = CallsInEar();

        if (calls.Count == 0)
        {
            _announcer.Announce(Announcement.CommandResponse("Nessuna chiamata attiva", DateTimeOffset.Now));
            return;
        }

        var now = DateTimeOffset.Now;
        var who = string.Join(" e ", calls.Select(c => c.RemoteParty.SpeechName));
        var since = calls.Max(c => c.TalkDuration(now));

        _announcer.Announce(Announcement.CommandResponse(
            $"{who}, da {SpeechFormatter.Duration(since)}",
            now));
    }

    private void AnnounceStatusSummary()
    {
        // Come per le caselle di riepilogo: chi aspetta, non chi si trova nello stato
        // «in attesa». Il riepilogo si chiede quando si e' persa la misura di quante
        // linee ci sono aperte, ed e' proprio a meta' di un trasferimento che serve.
        var text = AnnouncementTexts.StatusSummary(
            _calls.ActiveCall,
            _calls.WaitingCalls,
            _queueProvider.CurrentSnapshot,
            _queueProvider.AgentState,
            _telephony.RegistrationState,
            DateTimeOffset.Now,
            _calls.ConferencedCalls);

        _announcer.Announce(Announcement.CommandResponse(text, DateTimeOffset.Now));
    }

    /// <summary>
    /// Risponde alla domanda «da quanto», che cambia oggetto secondo la situazione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>In linea è la chiamata, fuori è il turno.</b> Non è un tasto con due
    /// significati: è la stessa domanda — da quanto tempo — applicata a ciò che
    /// l'operatore ha davanti in quel momento. Durante una conversazione la durata che
    /// interessa è quella della conversazione; senza nessuno in linea, l'unica durata
    /// che esista è quella del turno.
    /// </para>
    /// <para>
    /// Prima, senza chiamate, il comando rispondeva «nessuna chiamata attiva»: una
    /// risposta corretta e inutile, che diceva all'operatore una cosa che sapeva già e
    /// nessuna di quelle che stava chiedendo.
    /// </para>
    /// <para>
    /// In pausa la risposta parla della pausa. È il tempo che conta lì, e ricordarlo
    /// insieme al modo di uscirne è quello che impedisce alle pause di allungarsi da
    /// sole.
    /// </para>
    /// </remarks>
    private void AnnounceCallDuration()
    {
        var calls = CallsInEar();
        var now = DateTimeOffset.Now;

        // TRE SITUAZIONI, TRE CRONOMETRI, IN QUEST'ORDINE.
        //
        // 1. Una chiamata che squilla ha la precedenza su tutto: e' l'unico tempo che
        //    scade, perche' il chiamante sta per rinunciare o il centralino sta per
        //    ritirargli la chiamata. Chiedere «da quanto» mentre il telefono suona
        //    significa chiedere quello, non da quanto si e' in servizio.
        // 2. Una conversazione in corso: il tempo contato dalla risposta.
        // 3. Postazione libera: il tempo di servizio.
        //
        // LA PAUSA NON HA UN RAMO SUO, ed e' deliberato: in pausa qualunque tasto fa
        // gia' annunciare da quanto dura, con un annuncio accorpato che non si ripete.
        // Un quarto ramo qui direbbe la stessa cosa una seconda volta. Il tempo di
        // servizio che si sente premendo in pausa resta vero: conta il turno al netto
        // delle pause, e dice quanta pausa c'e' stata.
        //
        // Ogni tempo ha il suo verbo davanti. Con lo squillo che ha la precedenza si
        // possono sentire due tempi di fila, e le parole sono l'unica cosa che li
        // distingue.
        var talkTime = calls.Count > 0
            ? AnnouncementTexts.InCallFor(calls.Max(c => c.TalkDuration(now)))
            : null;

        if (_calls.RingingCall is { } ringing)
        {
            var squillo = AnnouncementTexts.RingingFor(now - ringing.CreatedAt);

            _announcer.Announce(Announcement.CommandResponse(
                talkTime is null ? squillo : $"{talkTime}. {squillo}", now));

            return;
        }

        if (talkTime is not null)
        {
            _announcer.Announce(Announcement.CommandResponse(talkTime, now));
            return;
        }

        _announcer.Announce(Announcement.CommandResponse(
            AnnouncementTexts.ServiceTime(_duty.InService(now), _duty.TotalPaused(now)), now));
    }

    /// <summary>
    /// Annuncia la qualità della linea.
    /// </summary>
    /// <remarks>
    /// In conferenza le linee sono due e possono aver negoziato codec diversi: se ne
    /// legge una per volta, nominando l'interlocutore. Dare un unico verdetto
    /// nasconderebbe proprio il caso che interessa, cioè quello in cui una delle due
    /// è a banda stretta e l'altra no.
    /// </remarks>
    private void AnnounceLineQuality()
    {
        var calls = CallsInEar();

        if (calls.Count == 0)
        {
            _announcer.Announce(Announcement.CommandResponse("Nessuna chiamata attiva", DateTimeOffset.Now));
            return;
        }

        var cifrata = _telephony.IsSignallingEncrypted;

        var text = calls.Count == 1
            ? AnnouncementTexts.LineQuality(calls[0], cifrata)
            : string.Join(". ", calls.Select(c =>
                $"{c.RemoteParty.SpeechName}: {AnnouncementTexts.LineQuality(c, cifrata)}"));

        _announcer.Announce(Announcement.CommandResponse(text, DateTimeOffset.Now));
    }

    /// <summary>
    /// Legge l'elenco completo delle scorciatoie.
    /// </summary>
    /// <remarks>
    /// Sostituisce il foglietto attaccato al monitor che userebbe un operatore
    /// vedente. È la funzione a cui si ricorre nelle prime settimane e quella che
    /// rende superflua l'assistenza di un collega.
    /// </remarks>
    private void AnnounceHotkeyList()
    {
        // L'ordine e' quello del catalogo, cioe' per gruppo di funzione: chi ascolta
        // trenta voci di seguito ha bisogno che quelle della gestione chiamata
        // arrivino insieme. L'ordine alfabetico dei nomi interni, che si usava prima,
        // le sparpagliava.
        var lines = HotkeyCommandCatalog.All
            .Select(info => (info, binding: _hotkeys.Get(info.Command, HotkeyScope.Local)))
            .Where(entry => entry.binding is not null)
            .Select(entry => $"{entry.info.Label}: {entry.binding!.SpeechText}");

        _announcer.Announce(Announcement.CommandResponse(
            $"Profilo {_hotkeys.Name}. {string.Join(". ", lines)}", DateTimeOffset.Now));
    }

    // -----------------------------------------------------------------------
    // Toni per i menu automatici
    // -----------------------------------------------------------------------

    /// <summary>
    /// Indica che le cifre digitate vanno in linea come toni invece che nel campo di
    /// composizione.
    /// </summary>
    private bool _dtmfMode;

    /// <summary>
    /// Attiva o disattiva la modalità toni.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il conflitto che risolve.</b> Durante una conversazione la stessa tastiera
    /// serve a due cose incompatibili: scrivere il numero che il chiamante sta
    /// dettando — senza che gli arrivi in linea — e rispondere al menu automatico
    /// dell'ente che si è appena chiamato. Le cifre non possono andare in tutti e due
    /// i posti, e indovinare la destinazione sarebbe peggio di sbagliarla sempre: un
    /// tasto che a volte scrive e a volte suona in linea non è usabile da nessuno, e
    /// meno che mai da chi non vede lo schermo.
    /// </para>
    /// <para>
    /// La modalità esplicita toglie l'ambiguità: dentro, ogni cifra parte come tono e
    /// viene detta; fuori, ogni cifra finisce nel campo di composizione e viene detta
    /// lo stesso. In entrambi i casi l'operatore sente cosa è successo, e l'ingresso e
    /// l'uscita sono annunciati.
    /// </para>
    /// <para>
    /// <b>Si spegne da sola alla fine della chiamata.</b> È la protezione contro
    /// l'errore peggiore: restare in modalità toni senza saperlo e ritrovarsi le cifre
    /// del numero successivo mandate in linea invece che nel campo.
    /// </para>
    /// </remarks>
    private void ToggleDtmfMode()
    {
        // In conferenza i toni non partono, perche' andrebbero a una sola delle due
        // linee senza che si possa dire quale. Farci entrare lo stesso significherebbe
        // lasciare l'operatore a premere cifre che vengono rifiutate una per una.
        if (!_dtmfMode && _calls.IsConferenceActive)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "In conferenza i toni non si inviano: sciogli prima la conferenza con "
                + LocalKeyName(_hotkeys, HotkeyCommand.Conference, "F7"),
                DateTimeOffset.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return;
        }

        if (!_dtmfMode && _calls.ActiveCall is null)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Nessuna chiamata in corso: la modalita' toni serve durante una conversazione",
                DateTimeOffset.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return;
        }

        _dtmfMode = !_dtmfMode;
        _log.LogInformation("Modalita' toni {Stato}", _dtmfMode ? "attivata" : "disattivata");

        var uscita = _hotkeys.Get(HotkeyCommand.ToggleDtmfMode, HotkeyScope.Local)?.SpeechText ?? "la stessa combinazione";

        _announcer.Announce(Announcement.CommandResponse(
            _dtmfMode
                ? $"Modalita' toni attiva: le cifre che digiti vanno in linea. {uscita} oppure Esc per uscire"
                : "Modalita' toni disattivata: le cifre tornano nel campo di composizione",
            DateTimeOffset.Now));
    }

    /// <summary>Esce dalla modalità toni, se attiva, e lo dice.</summary>
    /// <param name="reason">Motivo dell'uscita, da annunciare.</param>
    private void LeaveDtmfMode(string reason)
    {
        if (!_dtmfMode) return;

        _dtmfMode = false;
        _log.LogInformation("Modalita' toni disattivata: {Motivo}", reason);

        _announcer.Announce(Announcement.CommandResponse(reason, DateTimeOffset.Now));
    }

    /// <summary>
    /// Invia in linea i toni corrispondenti a quanto scritto nel campo di composizione.
    /// </summary>
    /// <remarks>
    /// La via comoda quando la sequenza si conosce già: si scrive con calma, ogni cifra
    /// viene riletta e si può correggere, e parte in un gesto solo. Il campo non viene
    /// svuotato: se il menu automatico chiede di ripetere, la sequenza è ancora lì.
    /// </remarks>
    private async Task SendDialerAsDtmfAsync()
    {
        var sequenza = DialerBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(sequenza))
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Il campo di composizione e' vuoto: scrivi la sequenza da inviare", DateTimeOffset.Now));
            return;
        }

        await _calls.SendDtmfSequenceAsync(sequenza).ConfigureAwait(true);
    }

    /// <summary>
    /// Dice se l'interno che si sta per chiamare è libero, occupato o fuori servizio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È la domanda che l'operatore si fa prima di passare una chiamata, e finora
    /// aveva risposta solo guardando una colonna di un elenco: chi ha già digitato il
    /// numero non ha alcun elenco davanti.
    /// </para>
    /// <para>
    /// Se lo stato non è ancora noto — sulla via SIP lo si conosce solo dopo aver
    /// sottoscritto quell'interno — si sottoscrive e si attende un attimo la risposta
    /// del centralino. Un secondo e mezzo è il compromesso: abbastanza perché una
    /// risposta di rete locale arrivi, poco abbastanza da non sembrare un blocco.
    /// </para>
    /// <para>
    /// Quando non si sa, si dice <b>perché</b> non si sa. «Sconosciuto» e basta
    /// lascerebbe l'operatore a chiedersi se ha sbagliato il numero, se il programma è
    /// rotto o se l'interno è davvero irraggiungibile: tre ipotesi con tre rimedi
    /// diversi.
    /// </para>
    /// </remarks>
    private async Task AnnounceExtensionStateAsync()
    {
        var interno = CurrentDestination();

        if (string.IsNullOrWhiteSpace(interno))
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Nessun interno indicato. Digita il numero nel campo di composizione, "
                + "oppure evidenzia un contatto, e riprova",
                DateTimeOffset.Now));

            return;
        }

        if (!_presence.IsAvailable)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Lo stato degli interni non e' disponibile: richiede il collegamento all'API del centralino "
                + "oppure un account SIP configurato",
                DateTimeOffset.Now));

            return;
        }

        var parlato = SpeechFormatter.Digits(interno, _settings.Accessibility.DigitGrouping);

        var stato = await _presence
            .QueryAsync(interno, TimeSpan.FromMilliseconds(1500), CancellationToken.None)
            .ConfigureAwait(true);

        var descrizione = PresenceMonitorService.Describe(stato);

        _log.LogInformation("Stato richiesto per l'interno {Extension}: {Stato}", interno, stato);

        if (descrizione.Length == 0)
        {
            _announcer.Announce(Announcement.CommandResponse(
                $"Interno {parlato}: stato non ancora noto. Il centralino non ha risposto, "
                + $"oppure quell'interno non e' sorvegliabile. Lo stato arriva tramite {_presence.SourceName}",
                DateTimeOffset.Now));

            return;
        }

        // Il consiglio pratico si aggiunge solo quando cambia la decisione: dire
        // "libero, puoi passare la chiamata" a ogni interrogazione allungherebbe la
        // risposta senza aggiungere nulla.
        var consiglio = PresenceMonitorService.DiscouragesTransfer(stato)
            ? ". Meglio non passargli una chiamata adesso"
            : string.Empty;

        _announcer.Announce(Announcement.CommandResponse(
            $"Interno {parlato}: {descrizione}{consiglio}", DateTimeOffset.Now));
    }

    /// <summary>
    /// Apre la guida sull'argomento che riguarda il punto in cui si trova il cursore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chi preme F1 sta chiedendo aiuto adesso, su quello che ha davanti. Aprire
    /// sempre l'indice lo costringerebbe a percorrere quattordici argomenti per
    /// arrivare a quello che il contesto diceva già.
    /// </para>
    /// <para>
    /// La guida viene costruita a ogni apertura dal profilo attivo, così le
    /// combinazioni che cita sono quelle assegnate in questo momento.
    /// </para>
    /// </remarks>
    private void OpenHelp()
    {
        // Con una conferenza aperta l'argomento pertinente e' quello, prima di
        // qualunque considerazione sul punto in cui si trova il cursore: chi chiede
        // aiuto mentre due persone sono in linea ha una domanda su quella situazione,
        // non sul pannello che ha davanti.
        var topic = _calls.IsConferenceActive
            ? HelpBook.ConferenceTopicId
            : DialerBox.IsKeyboardFocusWithin || SuggestionsGrid.IsKeyboardFocusWithin
                ? HelpBook.DialerTopicId
                : QueueList.IsKeyboardFocusWithin
                    ? HelpBook.QueueTopicId
                    : HelpBook.DefaultTopicId;

        try
        {
            // Una guida che impedisce di usare il programma di cui spiega l'uso serve
            // a poco: resta aperta accanto alla postazione, e il tasto di aiuto premuto
            // di nuovo la porta sull'argomento del punto in cui ci si trova.
            FinestreNonModali.MostraOAttiva(
                () => new HelpWindow(HelpBook.Build(_hotkeys, DateTimeOffset.Now), _announcer, topic),
                this,
                finestra => finestra.ShowTopic(topic));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della guida non riuscita");
            _announcer.Announce(Announcement.CommandResponse(
                "Impossibile aprire la guida", DateTimeOffset.Now));
        }
    }

    /// <summary>
    /// Apre l'elenco delle ultime chiamate.
    /// </summary>
    /// <remarks>
    /// La finestra si apre sulle chiamate perse: è la domanda per cui viene aperta
    /// quasi sempre. Come la rubrica, è modale rispetto alla principale ma non ferma
    /// la telefonia: una chiamata in arrivo viene comunque annunciata.
    /// </remarks>
    private void OpenCallHistory()
    {
        try
        {
            // Non modale: i comandi di questo elenco - richiama, usa per il
            // trasferimento - hanno senso proprio con una chiamata in corso, e da
            // finestra modale non erano nemmeno raggiungibili.
            FinestreNonModali.MostraOAttiva(
                _serviceProvider.GetRequiredService<CallHistoryWindow>, this);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura dell'elenco delle chiamate non riuscita");
            _announcer.Announce(Announcement.CommandResponse(
                "Impossibile aprire l'elenco delle chiamate", DateTimeOffset.Now));
        }
    }

    /// <summary>
    /// Apre il pannello delle impostazioni.
    /// </summary>
    /// <remarks>
    /// La finestra è modale rispetto alla principale ma <b>non blocca le
    /// chiamate</b>: il motore telefonico e il connettore del centralino continuano
    /// a girare, e un annuncio di chiamata entrante arriva comunque. Un pannello di
    /// configurazione che rendesse sordo il posto operatore sarebbe inutilizzabile
    /// durante un turno, cioè nell'unico momento in cui l'operatore si accorge che
    /// una impostazione va corretta.
    /// </remarks>
    /// <param name="diagnostics">
    /// Se aprire direttamente sulla scheda della diagnostica. Chi preme il comando
    /// dedicato sta cercando quella, non le impostazioni in generale.
    /// </param>
    private void OpenSettings(bool diagnostics = false)
    {
        try
        {
            // Il pannello chiuso chiede la password prima di aprirsi. Se l'operatore
            // rinuncia o sbaglia, non si apre: aprirlo in sola lettura sarebbe una
            // terza modalita' che nessuno ha configurato, e chi ha scelto "chiuso"
            // intendeva chiuso.
            var criterio = _serviceProvider.GetRequiredService<ConfigurationPolicyService>();

            if (!criterio.CanOpenPanel)
            {
                var sblocco = new UnlockSettingsWindow(criterio, _announcer) { Owner = this };
                sblocco.ShowDialog();

                if (!sblocco.Unlocked)
                {
                    _log.LogInformation("Apertura delle impostazioni rifiutata dal criterio di configurazione");
                    return;
                }
            }

            var window = _serviceProvider.GetRequiredService<SettingsWindow>();
            window.Owner = this;

            if (diagnostics) window.ShowDiagnostics();

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura delle impostazioni non riuscita");
            _announcer.Announce(Announcement.CommandResponse(
                "Impossibile aprire le impostazioni", DateTimeOffset.Now));
        }
    }

    /// <summary>
    /// Apre la segnalazione all'assistenza.
    /// </summary>
    /// <remarks>
    /// Il riepilogo diagnostico si costruisce qui e viene passato come funzione:
    /// così viene composto nel momento in cui il pacchetto si crea, e fotografa lo
    /// stato del guasto invece di quello di quando la finestra si è aperta.
    /// </remarks>
    private void OpenSupportReport()
    {
        try
        {
            var window = new SupportReportWindow(
                _serviceProvider.GetRequiredService<SupportPackageService>(),
                _settingsService,
                _serviceProvider.GetRequiredService<CrashReporter>(),
                BuildDiagnosticSummary,
                _announcer,
                _serviceProvider.GetRequiredService<ILogger<SupportReportWindow>>())
            {
                Owner = this,
            };

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della segnalazione all'assistenza non riuscita");
            _announcer.Announce(Announcement.CommandResponse(
                "Impossibile aprire la segnalazione. Il riepilogo resta disponibile dalla diagnostica",
                DateTimeOffset.Now));
        }
    }

    /// <summary>
    /// Compone il riepilogo diagnostico da allegare alla segnalazione.
    /// </summary>
    /// <remarks>
    /// Stessa sorgente del pannello di diagnostica, e non una seconda versione: due
    /// riepiloghi che dicessero cose diverse sulla stessa postazione sarebbero
    /// peggio di uno solo.
    /// </remarks>
    private string BuildDiagnosticSummary()
    {
        try
        {
            return DiagnosticReport.Build(DiagnosticSnapshotFactory.Build(_serviceProvider));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Composizione del riepilogo diagnostico non riuscita");
            return $"Riepilogo diagnostico non disponibile: {ex.Message}";
        }
    }

    /// <summary>
    /// Apre l'elenco degli account SIP.
    /// </summary>
    /// <remarks>
    /// Se da lì l'operatore chiede di modificarli, si passa alle impostazioni: sono
    /// due finestre diverse perché servono a due gesti diversi, guardare e cambiare,
    /// ma il passaggio dall'una all'altra deve costare una pressione.
    /// </remarks>
    private void OpenSipAccounts()
    {
        try
        {
            var window = new SipAccountsWindow(
                _serviceProvider.GetRequiredService<SipAccountSwitcher>(),
                _announcer,
                _serviceProvider.GetRequiredService<ILogger<SipAccountsWindow>>())
            {
                Owner = this,
            };

            window.ShowDialog();

            if (window.OpenSettingsRequested) OpenSettings();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura dell'elenco degli account non riuscita");
            _announcer.Announce(Announcement.CommandResponse(
                "Impossibile aprire l'elenco degli account", DateTimeOffset.Now));
        }
    }

    /// <summary>
    /// Apre la rubrica.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il testo digitato nel campo di composizione viene portato dentro come
    /// ricerca iniziale. È il comportamento che serve nel caso reale: l'operatore
    /// inizia a scrivere un nome nel dialer, si accorge che non ricorda l'interno e
    /// preme F10. Perdere quello che aveva già digitato lo costringerebbe a
    /// ribatterlo.
    /// </para>
    /// <para>
    /// La finestra è modale rispetto alla principale ma non ferma la telefonia:
    /// una chiamata in arrivo viene comunque annunciata, e i comandi globali
    /// continuano a funzionare.
    /// </para>
    /// </remarks>
    private void OpenContacts()
    {
        try
        {
            var typed = DialerBox.Text.Trim();

            // La ricerca digitata arriva alla rubrica anche quando era gia' aperta: chi
            // scrive un nome e preme il tasto della rubrica sta chiedendo quel nome, e
            // trovarsi davanti la ricerca di prima sarebbe una risposta a un'altra
            // domanda.
            FinestreNonModali.MostraOAttiva(
                _serviceProvider.GetRequiredService<ContactsWindow>,
                this,
                finestra =>
                {
                    if (typed.Length >= 2) finestra.SetInitialQuery(typed);
                });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della rubrica non riuscita");
            _announcer.Announce(Announcement.CommandResponse(
                "Impossibile aprire la rubrica", DateTimeOffset.Now));
        }
    }

    private async Task RedialAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastDialedNumber))
        {
            _announcer.Announce(Announcement.CommandResponse("Nessun numero da richiamare", DateTimeOffset.Now));
            return;
        }

        await _calls.DialAsync(_lastDialedNumber).ConfigureAwait(true);
    }

    /// <summary>
    /// Entra in una riunione Zoom componendo quello che c'è nel campo di composizione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Che cosa fa davvero, e perché vale la pena.</b> Alla stessa riunione si
    /// arriva anche componendo il numero telefonico dell'invito: si chiama, si ascolta
    /// un messaggio registrato, si digita l'identificativo, cancelletto, si aspetta, si
    /// digita il codice, cancelletto. È un percorso a ostacoli <b>a tempo e tutto in
    /// ascolto</b>, dove un tono premuto troppo presto si perde e non c'è modo di
    /// sapere a che punto si è. Di qui si entra con una composizione sola.
    /// </para>
    /// <para>
    /// <b>Non introduce una modalità.</b> Appena la riunione risponde è una chiamata
    /// come le altre: attesa, conferenza, toni e riaggancio funzionano con i tasti che
    /// l'operatore già conosce. È anche il motivo per cui il caso più utile — mettere
    /// in conferenza un chiamante telefonico e la riunione, cioè fare da ponte fra la
    /// rete telefonica e Zoom — non richiede niente di nuovo.
    /// </para>
    /// <para>
    /// <b>Il codice d'accesso non resta qui.</b> Viene tolto da ciò che si pronuncia e
    /// si conserva un passo più in basso, in <c>CallSessionManager.DialAsync</c>.
    /// </para>
    /// </remarks>
    private async Task JoinMeetingAsync()
    {
        var digitato = DialerBox.Text.Trim();

        if (digitato.Length == 0)
        {
            // Il comando non puo' indovinare la riunione: dire che cosa manca e in
            // quale campo e' l'unica risposta utile.
            _announcer.Announce(Announcement.CommandResponse(
                "Scrivi prima l'identificativo della riunione nel campo di composizione, "
                + "e se serve il codice dopo un punto",
                DateTimeOffset.Now));
            return;
        }

        string destinazione;

        if (digitato.Contains('@', StringComparison.Ordinal))
        {
            // Indirizzo gia' intero: arriva cosi' quando la riunione e' un contatto in
            // rubrica, che e' il modo piu' comodo di tenere quelle ricorrenti.
            destinazione = digitato;
        }
        else
        {
            var (identificativo, codice) = ZoomRoomConnector.Split(digitato);

            if (!ZoomRoomConnector.TryBuild(
                    identificativo, codice, null, out var composizione, out var errore))
            {
                _announcer.Announce(Announcement.High(
                    errore!, DateTimeOffset.Now, EarconKind.CommandRejected));
                return;
            }

            destinazione = composizione.Dial;
        }

        // Stessa regola della composizione ordinaria: con qualcuno in linea non si
        // compone. Qui pero' si dice, invece di fermarsi in silenzio: chi ha appena
        // scritto un identificativo di undici cifre e preme il tasto ha bisogno di
        // sapere perche' non e' successo niente.
        if (!CurrentActionContext().CanDial)
        {
            _announcer.Announce(Announcement.High(
                "C'e' una conversazione in corso: metti in attesa prima di entrare in riunione",
                DateTimeOffset.Now,
                EarconKind.CommandRejected));
            return;
        }

        // La digitazione e' finita: va dichiarato PRIMA di comporre, altrimenti il
        // riscontro dell'avvio viene scartato insieme all'eco delle cifre. Stessa
        // ragione spiegata per esteso in DialAsync.
        _context.StopTyping();

        _lastDialedNumber = destinazione;
        await _calls.DialAsync(destinazione).ConfigureAwait(true);
        DialerBox.Clear();
    }

    private async Task ToggleAgentLoginAsync()
    {
        var loggedIn = _queueProvider.AgentState.Status != AgentStatus.LoggedOut;
        await _queueProvider.SetAgentLoginAsync(!loggedIn, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task ToggleAgentPauseAsync()
    {
        var paused = _queueProvider.AgentState.Status == AgentStatus.Paused;
        await _queueProvider.SetAgentPausedAsync(!paused, null, CancellationToken.None).ConfigureAwait(true);
    }

    // -----------------------------------------------------------------------
    // Postazione fuori servizio
    // -----------------------------------------------------------------------

    /// <summary>
    /// Mette la postazione fuori servizio, o la riporta in servizio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Due strade verso il centralino, e servono entrambe.</b> Il rifiuto SIP degli
    /// INVITE è quella che funziona sempre, anche dove l'API non c'è: il centralino
    /// riceve un «non disponibile» in millisecondi e passa il chiamante al collega
    /// successivo. La pausa dell'agente in coda, quando l'API c'è, aggiunge quello che
    /// il rifiuto da solo non può dare — il centralino smette di <b>assegnare</b>
    /// chiamate a questa postazione, invece di assegnarle e vederle rifiutate, e lo
    /// stato compare sul pannello di chi supervisiona il servizio.
    /// </para>
    /// <para>
    /// <b>Se la seconda strada fallisce, la pausa vale lo stesso.</b> Il rifiuto SIP è
    /// già attivo e le chiamate non arrivano più: annullare tutto perché il centralino
    /// non ha risposto lascerebbe l'operatore convinto di essere in pausa mentre il
    /// telefono continua a squillare, che è il modo peggiore di sbagliare qui.
    /// </para>
    /// <para>
    /// <b>Le chiamate in corso non si toccano.</b> Mettersi in pausa significa non
    /// prenderne di nuove, non far cadere quella che si ha in mano: chi preme il tasto
    /// mentre sta parlando sta preparando la fine del turno, non chiudendo la
    /// conversazione.
    /// </para>
    /// </remarks>
    private async Task ToggleServicePauseAsync()
    {
        var now = DateTimeOffset.Now;
        var entrando = !_duty.IsPaused;

        // La durata si legge PRIMA di uscire dalla pausa: dopo, l'orologio e' gia'
        // azzerato e l'annuncio direbbe "pausa durata zero secondi".
        var durata = entrando ? TimeSpan.Zero : _duty.CurrentPause(now);

        if (entrando) _duty.Pause(now);
        else _duty.Resume(now);

        _telephony.SetDoNotDisturb(entrando);
        ApplyPausedState(entrando);

        _log.LogInformation(
            "Postazione {Stato} dall'operatore. Chiamate attive {Attive}, in attesa {Attesa}",
            entrando ? "fuori servizio" : "in servizio",
            _calls.LiveCalls.Count,
            _calls.HeldCalls.Count);

        var tasto = LocalKeyName(_hotkeys, HotkeyCommand.ToggleServicePause, "Control Alt Pausa");

        _announcer.Announce(Announcement.Critical(
            entrando
                ? AnnouncementTexts.ServicePauseStarted(tasto)
                : AnnouncementTexts.ServicePauseEnded(durata),
            now,
            entrando ? EarconKind.RegistrationLost : EarconKind.RegistrationRestored,
            coalesceKey: "duty:pause",
            braille: entrando ? "FUORI SERVIZIO" : "in servizio"));

        // La coda per ultima: il rifiuto SIP e' gia' attivo e l'operatore e' gia' stato
        // avvisato, quindi un centralino lento non ritarda ne' l'effetto ne' l'annuncio.
        await SyncQueueAgentPauseAsync(entrando).ConfigureAwait(true);
    }

    /// <summary>
    /// Allinea allo stato della postazione anche la pausa dell'agente in coda.
    /// </summary>
    /// <param name="paused">Se la postazione è andata in pausa.</param>
    /// <remarks>
    /// Silenzioso quando il collegamento al centralino non c'è: il connettore inerte
    /// accetta la richiesta e non fa nulla, ed è il comportamento giusto — la pausa
    /// della postazione non dipende dall'API, e segnalare un errore su una funzione
    /// che non è stata chiesta trasformerebbe una configurazione legittima in un
    /// guasto apparente.
    /// </remarks>
    private async Task SyncQueueAgentPauseAsync(bool paused)
    {
        try
        {
            await _queueProvider
                .SetAgentPausedAsync(paused, null, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Allineamento della pausa agente sul centralino non riuscito");

            _announcer.Announce(Announcement.High(
                "Il centralino non ha registrato la pausa in coda. Le chiamate entranti vengono comunque "
                + "rifiutate da BlindPhone",
                DateTimeOffset.Now,
                coalesceKey: "duty:pause:queue"));
        }
    }

    /// <summary>
    /// Riflette nella finestra lo stato di postazione fuori servizio.
    /// </summary>
    /// <param name="paused">Se la postazione è in pausa.</param>
    /// <remarks>
    /// <para>
    /// <b>Il campo di composizione si blocca invece di sparire.</b> Un campo che
    /// scompare toglie un punto di riferimento a chi naviga a tastiera e cambia il
    /// numero di tabulazioni per arrivare altrove; un campo che resta e non accetta
    /// testo si trova dove è sempre stato e può spiegare da sé perché non scrive.
    /// </para>
    /// <para>
    /// Il pulsante di chiamata si disabilita davvero, e non solo per coerenza: è
    /// l'unico modo per cui uno screen reader lo annunci come non disponibile invece di
    /// lasciar premere qualcosa che non farà nulla.
    /// </para>
    /// </remarks>
    private void ApplyPausedState(bool paused)
    {
        PauseBanner.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;

        UpdateDialerAvailability();
        DialButton.IsEnabled = !paused;

        // Il testo di aiuto del campo cambia con lo stato: chi ci arriva con il cursore
        // deve sentire perche' non scrive, senza dover premere un tasto per scoprirlo.
        AutomationProperties.SetHelpText(DialerBox, paused
            ? "Campo bloccato: la postazione e' fuori servizio e non accetta chiamate. "
              + $"Premi {LocalKeyName(_hotkeys, HotkeyCommand.ToggleServicePause, "Control Alt Pausa")} "
              + "per tornare in servizio."
            : _dialerHelpText);

        if (paused) StartPauseBannerFlash();
        else StopPauseBannerFlash();

        // IL PULSANTE VA RISCRITTO QUI, e prima non succedeva.
        //
        // Si aggiornava soltanto dentro UpdateStatus, che gira alle transizioni di
        // chiamata e agli aggiornamenti di coda: su una postazione ferma, con l'API
        // del centralino disattivata, non girava affatto. L'operatore premeva la
        // pausa, sentiva l'annuncio, vedeva comparire il riquadro rosso — e sul
        // pulsante restava la tazza, come se il comando non fosse arrivato.
        UpdateServicePauseButton();

        UpdateServiceClock(DateTimeOffset.Now);

        // Porta la pausa sul suggerimento dell'icona nell'area di notifica.
        UpdateRegistrationText();
    }

    /// <summary>Avvia il lampeggio dell'avviso.</summary>
    /// <remarks>
    /// La cadenza e la scelta di animare l'opacità sono spiegate accanto allo
    /// storyboard, in MainWindow.xaml: restano sotto i tre lampeggi al secondo che le
    /// linee guida sulle crisi fotosensibili fissano come tetto.
    /// </remarks>
    private void StartPauseBannerFlash()
    {
        if (TryFindResource("BannerFlash") is not Storyboard storyboard) return;

        _pauseFlash = storyboard;
        storyboard.Begin(PauseBanner, isControllable: true);
    }

    /// <summary>Ferma il lampeggio e riporta l'avviso a piena opacità.</summary>
    /// <remarks>
    /// Senza il ripristino esplicito il riquadro resterebbe all'opacità in cui
    /// l'animazione è stata fermata: invisibile a schermo perché nascosto, ma
    /// sbiadito alla pausa successiva.
    /// </remarks>
    private void StopPauseBannerFlash()
    {
        _pauseFlash?.Stop(PauseBanner);
        _pauseFlash = null;

        PauseBanner.Opacity = 1.0;
    }

    /// <summary>Storyboard del lampeggio, tenuto per poterlo fermare.</summary>
    private Storyboard? _pauseFlash;

    /// <summary>Storyboard del lampeggio dell'avviso di chiamata entrante.</summary>
    private Storyboard? _incomingFlash;

    /// <summary>
    /// Mostra, aggiorna o spegne l'avviso di chiamata entrante.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Segue lo stato di squillo, non l'evento di arrivo.</b> Un riquadro acceso
    /// all'arrivo e spento da qualcun altro avrebbe cinque modi di restare acceso a
    /// vuoto — risposta, rifiuto, rinuncia del chiamante, caduta, cambio di chiamata
    /// in squillo — e un avviso che resta acceso su una chiamata che non c'è più è
    /// peggio di nessun avviso. Sta accanto alla suoneria, che segue la stessa regola:
    /// <c>CallSessionManager.SyncRinger</c>.
    /// </para>
    /// <para>
    /// Il lampeggio parte solo quando il riquadro si accende. Questo metodo viene
    /// richiamato anche a ogni aggiornamento della coda, e far ripartire l'animazione
    /// ogni volta la bloccherebbe sul primo fotogramma.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Mostra o spegne l'avviso verde di conversazione in corso.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Segue la conversazione e non la chiamata: mettendo in attesa sparisce, perché
    /// «in linea con» smetterebbe di essere vero e questo riquadro lo legge di sfuggita
    /// chi passa dalla postazione, senza il contesto per correggerlo.
    /// </para>
    /// <para>
    /// Il testo si riscrive a ogni passaggio anche quando il riquadro è già acceso:
    /// alternando fra due linee l'interlocutore cambia mentre lo stato «in
    /// conversazione» resta, e un nome fermo sul precedente sarebbe la peggiore delle
    /// informazioni. Il lampeggio invece parte una volta sola, quando il riquadro si
    /// accende.
    /// </para>
    /// </remarks>
    private void UpdateActiveCallBanner()
    {
        var testo = CallBoardTexts.InCall(_calls.ActiveCall, _calls.ConferencedCalls);
        var acceso = ActiveCallBanner.Visibility == Visibility.Visible;

        if (string.IsNullOrEmpty(testo))
        {
            if (!acceso) return;

            ActiveCallBanner.Visibility = Visibility.Collapsed;

            _activeCallFlash?.Stop(ActiveCallBanner);
            _activeCallFlash = null;
            ActiveCallBanner.Opacity = 1.0;

            return;
        }

        ActiveCallBannerText.Text = testo;

        if (acceso) return;

        ActiveCallBanner.Visibility = Visibility.Visible;

        if (TryFindResource("BannerFlash") is not Storyboard storyboard) return;

        _activeCallFlash = storyboard;
        storyboard.Begin(ActiveCallBanner, isControllable: true);
    }

    /// <summary>Storyboard del lampeggio dell'avviso di conversazione in corso.</summary>
    private Storyboard? _activeCallFlash;

    /// <summary>
    /// Riflette sul pulsante della pausa lo stato del turno.
    /// </summary>
    /// <remarks>
    /// Il pulsante dice l'azione che si può fare adesso e non lo stato in cui ci si
    /// trova — «Pausa» quando si è in servizio, «Torna in servizio» quando si è
    /// fuori — come tutti gli altri pulsanti del prodotto. Lo stato lo raccontano
    /// l'orologio accanto, che in pausa cambia forma, e il riquadro rosso.
    /// </remarks>
    private void UpdateServicePauseButton()
    {
        var descrittore = CallActionCatalog.ServicePauseButton(_duty.IsPaused);
        var tasto = LocalKeyName(_hotkeys, HotkeyCommand.ToggleServicePause, "Control Alt Pausa");

        // Stessa composizione dei pulsanti della barra: il pittogramma vuole la
        // famiglia dei caratteri di icone, la parola quella del testo.
        ServicePauseButton.Content = BuildCommandContent(descrittore);
        ServicePauseButton.ToolTip = $"{descrittore.Description}.\nScorciatoia: {tasto}";

        AutomationProperties.SetName(ServicePauseButton, descrittore.Label);
        AutomationProperties.SetHelpText(
            ServicePauseButton, $"{descrittore.Description}. Scorciatoia da tastiera: {tasto}");
    }

    private async void OnServicePauseButtonClick(object sender, RoutedEventArgs e)
    {
        _log.LogInformation("Comando dal pulsante {Action}", CallAction.ServicePauseOn);
        await ExecuteCommandAsync(HotkeyCommand.ToggleServicePause).ConfigureAwait(true);
    }

    private void UpdateIncomingBanner()
    {
        var ringing = _calls.RingingCall;
        var acceso = IncomingCallBanner.Visibility == Visibility.Visible;

        if (ringing is null)
        {
            if (!acceso) return;

            IncomingCallBanner.Visibility = Visibility.Collapsed;

            _incomingFlash?.Stop(IncomingCallBanner);
            _incomingFlash = null;
            IncomingCallBanner.Opacity = 1.0;

            return;
        }

        // Il testo si riscrive comunque: una seconda chiamata che comincia a squillare
        // mentre la prima e' ancora li' prende il posto della prima nel riquadro appena
        // quella viene risposta, e il nome deve seguirla.
        IncomingCallBannerText.Text = CallBoardTexts.IncomingCall(ringing.RemoteParty, ringing.QueueName);
        UpdateIncomingTimer(DateTimeOffset.Now);

        if (acceso) return;

        IncomingCallBanner.Visibility = Visibility.Visible;

        if (TryFindResource("BannerFlash") is not Storyboard storyboard) return;

        _incomingFlash = storyboard;
        storyboard.Begin(IncomingCallBanner, isControllable: true);
    }

    /// <summary>
    /// Indica se un tasto, con questi modificatori, sarebbe finito nel campo di
    /// composizione.
    /// </summary>
    /// <param name="keyName">Nome tecnico del tasto.</param>
    /// <param name="modifiers">Modificatori premuti.</param>
    /// <remarks>
    /// Lettere e cifre, riga superiore e tastierino. Maiusc è ammesso perché fa parte
    /// del digitare; Control e Alt no, perché con quelli si compongono i comandi, che
    /// in pausa devono continuare a funzionare.
    /// </remarks>
    private static bool IsTypingKey(string keyName, HotkeyModifiers modifiers)
    {
        if (modifiers is not (HotkeyModifiers.None or HotkeyModifiers.Shift)) return false;
        if (keyName.Length == 1 && char.IsAsciiLetter(keyName[0])) return true;
        if (keyName.Length == 2 && keyName[0] == 'D' && char.IsAsciiDigit(keyName[1])) return true;

        return keyName.StartsWith("NumPad", StringComparison.Ordinal) && keyName.Length == 7;
    }

    /// <summary>Spiega perché la tastiera non scrive.</summary>
    /// <remarks>
    /// Si accorpa su una chiave propria: chi non se ne accorge digita un numero intero
    /// prima di fermarsi, e senza accorpamento sentirebbe la stessa frase dieci volte
    /// di seguito, sovrapposta a se stessa.
    /// </remarks>
    private void AnnouncePausedTyping()
    {
        var now = DateTimeOffset.Now;
        var tasto = LocalKeyName(_hotkeys, HotkeyCommand.ToggleServicePause, "Control Alt Pausa");

        _announcer.Announce(Announcement.High(
            AnnouncementTexts.TypingWhilePaused(_duty.CurrentPause(now), tasto),
            now,
            EarconKind.CommandRejected,
            coalesceKey: "duty:typing"));
    }

    /// <summary>
    /// Invio sull'elenco della coda: preleva la chiamata su cui si trova il cursore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È un gesto <b>contestuale</b> e non una scorciatoia del profilo, perché Invio
    /// ha già un significato altrove: nel campo di composizione compone il numero.
    /// Assegnarlo nel profilo lo renderebbe un tasto solo per tutta la finestra, e
    /// uno dei due usi dovrebbe sparire. Legato alla lista, ciascuno vale dove il
    /// cursore si trova, che è anche il modo in cui la scheda dei tasti lo descrive.
    /// </para>
    /// <para>
    /// Basta il <c>KeyDown</c> che risale dalla riga: a differenza della
    /// <see cref="System.Windows.Controls.DataGrid"/> dei contatti proposti, la
    /// <see cref="System.Windows.Controls.ListView"/> non consuma Invio per conto
    /// suo, quindi non serve intercettare la fase di anteprima.
    /// </para>
    /// </remarks>
    private void OnQueueListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;

        // Si passa da ExecuteCommandAsync invece di chiamare il metodo direttamente:
        // il comando lascia cosi' la stessa riga di registro e ricade nella stessa
        // rete di protezione degli errori qualunque sia il tasto che lo ha invocato.
        // Chiamarlo qui di lato significherebbe che un'eccezione del connettore, che
        // per la combinazione assegnata diventa "il comando non e' riuscito", da
        // questa strada chiuderebbe l'applicazione.
        _ = ExecuteCommandAsync(HotkeyCommand.PickSelectedQueueCall);
    }

    private async Task PickSelectedQueueCallAsync()
    {
        if (!_queueProvider.Capabilities.CanPickSpecificCall)
        {
            // Si dice perche' non si puo', invece di lasciare un comando muto: e' la
            // differenza fra un limite noto e un difetto apparente.
            _announcer.Announce(Announcement.CommandResponse(
                "Il centralino non consente di prelevare una chiamata specifica dalla coda",
                DateTimeOffset.Now));
            return;
        }

        if (QueueList.SelectedItem is not QueueRow row)
        {
            // Stessa regola: senza riscontro, "non c'e' niente da prelevare" e
            // "il prelievo e' fallito in silenzio" sarebbero la stessa cosa, e la
            // reazione sarebbe premere di nuovo. Le due frasi si distinguono perche'
            // le due situazioni si risolvono in modo diverso: una si aspetta, l'altra
            // si risolve scendendo con le frecce su una riga.
            _announcer.Announce(Announcement.CommandResponse(
                _queueRows.Count == 0 ? "Elenco coda vuoto" : "Nessuna chiamata selezionata",
                DateTimeOffset.Now));
            return;
        }

        // Si annuncia la richiesta, non solo il suo esito. Il prelievo e' un
        // trasferimento cieco verso il proprio interno: fra la pressione del tasto e
        // lo squillo passa un giro di rete, e in quel silenzio l'operatore non
        // saprebbe se il tasto e' stato raccolto. La frase e' breve di proposito,
        // perche' l'annuncio della chiamata entrante le arriva sopra subito dopo.
        _announcer.Announce(Announcement.CommandResponse("Prelievo in corso", DateTimeOffset.Now));

        var picked = await _queueProvider.PickCallAsync(row.Id, CancellationToken.None).ConfigureAwait(true);

        if (!picked)
        {
            _announcer.Announce(Announcement.CommandResponse("Prelievo non riuscito", DateTimeOffset.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
        }
    }

    // -----------------------------------------------------------------------
    // Composizione
    // -----------------------------------------------------------------------

    /// <summary>
    /// Freccia giù dal campo di composizione: scende fra i contatti proposti.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Va intercettata in anteprima, e la ragione è che altrimenti non arriva.</b>
    /// Una casella di testo tratta le frecce verticali come spostamento del cursore e
    /// dichiara l'evento gestito nel proprio gestore di classe, che WPF esegue
    /// <i>prima</i> di quelli agganciati alla singola casella. Il ramo che stava in
    /// <c>KeyDown</c> non veniva mai eseguito: misurato in laboratorio, su una casella
    /// di testo <c>PreviewKeyDown</c> riceve Giù e Invio, <c>KeyDown</c> riceve solo
    /// Invio.
    /// </para>
    /// <para>
    /// Il sintomo per l'operatore era doversi spostare con due tabulazioni — una per
    /// il pulsante Chiama, una per l'elenco — invece che con la freccia. Invio
    /// continua a vivere in <c>KeyDown</c> perché lì arriva, e spostarlo qui non
    /// servirebbe a niente.
    /// </para>
    /// <para>
    /// <b>Se la ricerca è ancora in attesa, prima si completa.</b> L'elenco compare
    /// dopo un terzo di secondo dall'ultima lettera digitata: chi scrive in fretta e
    /// preme subito la freccia troverebbe l'elenco vuoto e resterebbe fermo. È lo
    /// stesso motivo per cui Invio fa la stessa cosa.
    /// </para>
    /// </remarks>
    private async void OnDialerPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down) return;

        e.Handled = true;

        await FlushPendingSuggestionSearchAsync().ConfigureAwait(true);

        if (_suggestions.Count == 0) return;

        GridNavigation.FocusCell(SuggestionsGrid, Math.Max(SuggestionsGrid.SelectedIndex, 0));
    }

    private async void OnDialerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;

        await FlushPendingSuggestionSearchAsync().ConfigureAwait(true);
        await DialAsync().ConfigureAwait(true);
    }

    private async void OnDialClick(object sender, RoutedEventArgs e)
    {
        await FlushPendingSuggestionSearchAsync().ConfigureAwait(true);
        await DialAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Completa subito una ricerca del nominativo ancora in attesa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si compone prima che sia scaduto il conto alla rovescia: senza questo si
    /// comporrebbero le lettere digitate. Chi scrive un nome in fretta e preme
    /// subito Invio deve ottenere lo stesso risultato di chi aspetta, altrimenti la
    /// funzione è affidabile solo per chi digita piano.
    /// </para>
    /// <para>
    /// Senza annuncio: il conteggio dei risultati sarebbe seguito immediatamente da
    /// "chiamata a ...", e la prima frase non farebbe che ritardare la seconda.
    /// </para>
    /// </remarks>
    private async Task FlushPendingSuggestionSearchAsync()
    {
        if (!_suggestionTimer.IsEnabled) return;

        _suggestionTimer.Stop();
        await RunSuggestionSearchAsync(announce: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Annulla la ricerca del nominativo o dell'interno, in attesa o già avviata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serve nell'istante in cui si compone o si trasferisce: da quel momento la
    /// risposta della ricerca non interessa più a nessuno, e l'annuncio «Mario Rossi»
    /// arriverebbe sopra «Chiamata a…», che è il solo riscontro dell'avvio.
    /// </para>
    /// <para>
    /// Con i nomi non c'era il problema, perché il campo si riempie a tasti e
    /// Invio completa la ricerca prima di comporre. Con le cifre il campo può essere
    /// riempito dal programma — la ricomposizione di un numero dalle ultime chiamate —
    /// e comporre subito: il conto alla rovescia restava acceso mentre la chiamata
    /// partiva, e allo scadere cercava e parlava.
    /// </para>
    /// </remarks>
    private void CancelSuggestionSearch()
    {
        _suggestionTimer.Stop();
        _suggestionSearch?.Cancel();
    }

    private async Task DialAsync()
    {
        var destination = CurrentDestination();
        if (string.IsNullOrWhiteSpace(destination)) return;

        // In modalita' trasferimento Invio TRASFERISCE SUBITO, alla cieca, senza
        // attese e senza conferme.
        //
        // E' la scelta che rende praticabile lo smaltimento di una coda: digita,
        // Invio, avanti il prossimo. I due gesti restano distinti e non ambigui —
        // Invio manda la chiamata, il tasto di trasferimento chiama prima l'interno
        // per annunciarla — e nessuno dei due dipende piu' dalla velocita' con cui
        // si preme due volte.
        if (_transfer.Phase == TransferPhase.EnteringDestination)
        {
            // Si dichiara conclusa la digitazione PRIMA di trasferire, e non e' un
            // dettaglio di ordine.
            //
            // Durante la digitazione gli annunci sotto la priorita' critica vengono
            // soppressi, perche' l'eco delle cifre ha la precedenza. Ma il campo
            // contiene ancora il numero appena scritto nel momento in cui il
            // trasferimento parte, quindi la conferma "trasferimento cieco a ...
            // inviato" finiva scartata proprio li'. Il registro l'ha mostrato:
            //
            //   DBG AnnouncementService  Annuncio scartato (OperatorTyping):
            //       Trasferimento cieco a tre, quattro, nove, ... inviato
            //
            // Su questo percorso quella frase e' l'UNICO riscontro che esiste: si
            // sono tolte tutte le conferme preventive per guadagnare velocita', e
            // perdere anche quella lascia l'operatore senza sapere se e dove la
            // chiamata sia andata.
            _context.StopTyping();
            CancelSuggestionSearch();

            await _transfer.BlindTransferNowAsync(destination).ConfigureAwait(true);
            DialerBox.Clear();
            return;
        }

        // CON QUALCUNO IN LINEA NON SI COMPONE, E INVIO NON FA NIENTE.
        //
        // E' il caso dell'operatore che ha il chiamante in linea, digita l'interno a
        // cui vuole passarlo e preme Invio invece del tasto di trasferimento. In
        // modalita' standard Invio non e' il tasto del trasferimento — lo e' in
        // modalita' semplificata — quindi finiva qui, e qui si componeva: partiva una
        // seconda chiamata in parallelo mentre il chiamante restava in linea, perche'
        // comporre non mette in attesa nessuno.
        //
        // Il danno non e' la seconda linea, e' che non si sente. L'unico riscontro
        // era "Chiamata a 4021", che a chi si aspettava un trasferimento verso il 4021
        // suona come una conferma: l'operatore lo scopriva dal chiamante che
        // protestava, non dal programma.
        //
        // Il tasto viene consumato dal gestore che porta qui, quindi la pressione non
        // prosegue altrove: si ferma, in silenzio, come chiesto. La riga di registro
        // resta perche' "ho premuto e non e' successo niente" dev'essere una domanda
        // con risposta.
        //
        // Con la chiamata IN ATTESA si compone eccome: e' la strada per procurarsi la
        // seconda chiamata di una conferenza. La regola sta in CallActionContext.CanDial.
        if (!CurrentActionContext().CanDial)
        {
            _log.LogDebug(
                "Composizione ignorata: c'e' una conversazione in corso. "
                + "Per passare il chiamante si usa il tasto di trasferimento");
            return;
        }

        // Stessa ragione del ramo del trasferimento, che sta poche righe sopra: la
        // digitazione e' finita nell'istante in cui si preme Invio, e va dichiarato
        // PRIMA di comporre. Il campo contiene ancora il testo mentre la chiamata
        // parte, quindi "chiamata a ..." — che e' il solo riscontro dell'avvio —
        // finirebbe scartato per OperatorTyping insieme a tutto il resto.
        _context.StopTyping();
        CancelSuggestionSearch();

        _lastDialedNumber = destination;
        await _calls.DialAsync(destination).ConfigureAwait(true);
        DialerBox.Clear();

        // Si ripete a ogni composizione, non solo all'avvio: senza, l'operatore
        // sente "chiamata a duecento" e resta ad aspettare una risposta che non
        // puo' arrivare, senza capire perche'.
        if (_telephony.IsSimulated)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Chiamata simulata: nessuna telefonata e' partita. Configura l'account SIP.",
                DateTimeOffset.Now));
        }
    }

    /// <summary>
    /// Segnala alla pipeline che l'operatore sta digitando, per sospendere gli
    /// annunci non critici.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Durante la composizione l'eco delle cifre ha la precedenza: un annuncio di
    /// coda che si sovrappone alla lettura delle cifre fa perdere il filo e produce
    /// numeri sbagliati.
    /// </para>
    /// <para>
    /// <b>Si dichiara la battitura, non il contenuto del campo.</b> Prima qui si
    /// accendeva un interruttore quando la casella conteneva del testo, e restava
    /// acceso finché qualcuno non la svuotava: il numero rimasto dal trasferimento
    /// precedente zittiva la postazione per minuti interi. La battitura scade da sé,
    /// il contenuto no.
    /// </para>
    /// </remarks>
    private void OnDialerTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Il campo svuotato e' un gesto esplicito e vale come fine della composizione:
        // non c'e' nessuna cifra da proteggere, e aspettare la scadenza ritarderebbe
        // il primo annuncio senza motivo.
        if (string.IsNullOrEmpty(DialerBox.Text)) _context.StopTyping();
        else _context.NoteTyping(DateTimeOffset.Now);

        if (_transfer.Phase == TransferPhase.EnteringDestination)
        {
            _ = _transfer.SetDestinationAsync(DialerBox.Text.Trim());
        }

        // I due pulsanti del trasferimento esistono solo quando c'e' una destinazione
        // verso cui trasferire: compaiono alla prima cifra e spariscono se il campo
        // viene svuotato.
        UpdateCommandBar();

        // Ogni battitura riavvia il conto alla rovescia: la ricerca parte quando
        // l'operatore smette di digitare, non mentre digita.
        _suggestionTimer.Stop();

        if (!LooksLikeName(DialerBox.Text) && !ExtensionSearch.IsExtensionQuery(DialerBox.Text))
        {
            HideSuggestions();
            return;
        }

        _suggestionTimer.Start();
    }

    // -----------------------------------------------------------------------
    // Ricerca del nominativo dal campo di composizione
    // -----------------------------------------------------------------------

    /// <summary>
    /// Distingue un nome da cercare da un numero da comporre.
    /// </summary>
    /// <param name="text">Testo del campo di composizione.</param>
    /// <remarks>
    /// <para>
    /// La regola è la più semplice che si possa tenere a mente senza guardare lo
    /// schermo: se ci sono lettere è un nome, se ci sono solo cifre è un numero.
    /// </para>
    /// <para>
    /// <b>Un numero si cerca comunque, ma per uguaglianza.</b> Un nome si cerca per
    /// frammenti; un numero di sole cifre viene confrontato per intero con gli
    /// interni in rubrica — vedi <see cref="ExtensionSearch"/> — e l'elenco compare
    /// solo se qualcuno ha proprio quell'interno. Per questo non intralcia chi digita
    /// una destinazione di trasferimento: finché le cifre non formano un interno
    /// esistente il campo si comporta come sempre.
    /// </para>
    /// </remarks>
    private static bool LooksLikeName(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed.Any(char.IsLetter);
    }

    /// <summary>
    /// Cerca in rubrica il nome o l'interno digitato e aggiorna l'elenco dei contatti proposti.
    /// </summary>
    /// <param name="announce">Se annunciare l'esito della ricerca.</param>
    /// <remarks>
    /// Le due ricerche hanno regole opposte e non vanno confuse: il nome si cerca per
    /// frammenti, l'interno per uguaglianza esatta. Qui sta solo la scelta di quale
    /// usare; la regola dell'interno è in <see cref="ExtensionSearch"/>.
    /// </remarks>
    private async Task RunSuggestionSearchAsync(bool announce = true)
    {
        var query = DialerBox.Text.Trim();
        var byExtension = ExtensionSearch.IsExtensionQuery(query);

        if (!byExtension && !LooksLikeName(query))
        {
            HideSuggestions();
            return;
        }

        // Una ricerca precedente ancora in corso viene annullata: i risultati di una
        // query obsoleta arriverebbero dopo quelli buoni e proporrebbero contatti
        // che l'operatore non sta piu' cercando.
        if (_suggestionSearch is not null) await _suggestionSearch.CancelAsync().ConfigureAwait(true);

        _suggestionSearch?.Dispose();
        _suggestionSearch = new CancellationTokenSource();
        var token = _suggestionSearch.Token;

        // Ogni voce porta il numero con cui verra' proposta. Per il nome e' quello
        // scelto da DialableNumber; per l'interno e' l'interno digitato, e non un
        // altro recapito della stessa voce: chi ha scritto 4021 e scende nell'elenco
        // deve trovare 4021 sotto ogni riga, non il cellulare della persona.
        List<(Contact Contact, string? Number)> found;

        try
        {
            if (byExtension)
            {
                var matches = await ExtensionSearch
                    .FindAsync(_contacts, query, MaxSuggestions + 1, token)
                    .ConfigureAwait(true);

                found = [.. matches.Select(m => (m.Contact, (string?)m.Number))];
            }
            else
            {
                var contacts = await _contacts
                    .SearchAsync(query, MaxSuggestions + 1, token)
                    .ConfigureAwait(true);

                found = [.. contacts.Select(c => (c, DialableNumber(c)))];
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ricerca in rubrica dal campo di composizione non riuscita");
            HideSuggestions();
            return;
        }

        if (token.IsCancellationRequested) return;

        // Il campo puo' essere cambiato mentre la ricerca era in corso.
        if (!string.Equals(query, DialerBox.Text.Trim(), StringComparison.Ordinal)) return;

        _suggestions.Clear();

        foreach (var (contact, dialable) in found)
        {
            // Un contatto senza recapito non e' chiamabile: proporlo costringerebbe
            // l'operatore a scoprirlo premendo Invio.
            if (dialable is not { Length: > 0 } number) continue;
            if (_suggestions.Count == MaxSuggestions) break;

            _suggestions.Add(new DialerSuggestion(
                contact.EffectiveDisplayName,
                number,
                PresenceMonitorService.Describe(_presence.Get(number)),
                contact));
        }

        SuggestionsGrid.Visibility = _suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_suggestions.Count > 0) SuggestionsGrid.SelectedIndex = 0;

        // Si dichiara al servizio cosa l'operatore ha davanti: senza API del
        // centralino lo stato arriva per sottoscrizione SIP, e ogni interno guardato
        // ne costa una. Lo stato arrivera' fra qualche decimo di secondo e la riga si
        // aggiornera' da sola.
        _ = _presence.WatchAsync([.. _suggestions.Select(s => s.Number)], CancellationToken.None);

        if (announce) AnnounceSuggestions(found.Count > MaxSuggestions, query);
    }

    /// <summary>
    /// Numero con cui si raggiunge un contatto dal campo di composizione.
    /// </summary>
    /// <remarks>
    /// L'interno viene prima del recapito predefinito: chi cerca un nome dal posto
    /// operatore vuole quasi sempre passare la chiamata a una scrivania, e un
    /// cellulare contrassegnato come primario manderebbe il chiamante da un'altra
    /// parte senza che nessuno se ne accorga.
    /// </remarks>
    private static string? DialableNumber(Contact contact) => contact.ExtensionNumber ?? contact.PrimaryNumber;

    /// <summary>
    /// Annuncia quanti contatti corrispondono al nome digitato.
    /// </summary>
    /// <param name="truncated">Se i contatti trovati erano più di quelli mostrati.</param>
    /// <param name="query">Testo cercato.</param>
    /// <remarks>
    /// <para>
    /// Senza questo annuncio l'elenco è invisibile: comparirebbe a schermo e nessuno
    /// saprebbe che c'è. È l'informazione che decide il gesto successivo — scendere
    /// con la freccia, continuare a digitare, o premere Invio senz'altro.
    /// </para>
    /// <para>
    /// Si usa <see cref="Announcement.CommandResponse"/> e non un annuncio normale
    /// perché durante la digitazione la pipeline sopprime tutto il resto: qui la
    /// deroga è corretta, perché la ricerca parte solo quando l'operatore ha smesso
    /// di digitare.
    /// </para>
    /// <para>
    /// <b>Con le cifre la deroga costa di più, e si paga solo quando serve.</b> Sulle
    /// lettere l'annuncio arriva dopo un nome che l'operatore ha finito di scrivere;
    /// sulle cifre può arrivare a metà di un numero più lungo, sopra l'eco delle cifre
    /// successive. Per questo con un interno si parla solo se c'è una voce che lo ha —
    /// una conferma che vale la pena sentire, perché dice a chi si sta per passare la
    /// chiamata — e si tace in tutti gli altri casi.
    /// </para>
    /// </remarks>
    private void AnnounceSuggestions(bool truncated, string query)
    {
        string message;

        var byExtension = ExtensionSearch.IsExtensionQuery(query);

        if (_suggestions.Count == 0)
        {
            // Digitando cifre il silenzio e' la risposta giusta. La maggior parte dei
            // numeri che si compongono non e' un interno in rubrica, e a ogni pausa
            // nel digitare — anche a meta' di un numero — si sentirebbe "nessun
            // contatto" sopra l'eco delle cifre, per un'informazione che non serve:
            // l'operatore voleva comporre, e puo' farlo.
            if (byExtension) return;

            message = $"Nessun contatto per {query}";
        }
        else if (_suggestions.Count == 1)
        {
            // Il caso piu' frequente: si dice chi e' e con che interno, cosi' non
            // serve nemmeno scendere nell'elenco per verificare.
            //
            // Lo stato si aggiunge solo quando e' noto e quando sconsiglia di passare
            // la chiamata: sapere che l'interno e' occupato PRIMA di premere Invio
            // cambia la decisione, mentre sentirsi dire "libero" a ogni ricerca
            // allungherebbe l'annuncio piu' frequente del prodotto senza aggiungere
            // nulla. Chi vuole comunque la conferma la trova nella colonna Stato.
            var only = _suggestions[0];
            var state = _presence.Get(only.Number);

            var warning = PresenceMonitorService.DiscouragesTransfer(state)
                ? $", {PresenceMonitorService.Describe(state)}"
                : string.Empty;

            // Se l'interno l'ha appena digitato l'operatore, rileggerglielo e' tempo
            // speso per niente: quello che non sa, e che il campo non dice, e' di chi
            // sia. Con un nome cercato invece l'interno e' la risposta, e si dice.
            message = byExtension
                ? $"{only.Contact.SpeechName}{warning}"
                : $"{only.Contact.SpeechName}, interno "
                  + $"{SpeechFormatter.Digits(only.Number, _settings.Accessibility.DigitGrouping)}{warning}";
        }
        else if (truncated)
        {
            // Continuare a digitare non restringe un interno: o le cifre sono quelle
            // o non lo sono. Qui non c'e' niente da affinare, c'e' solo da sapere che
            // l'interno e' condiviso da molte voci.
            message = byExtension
                ? $"Piu' di {MaxSuggestions} voci su questo interno"
                : $"Piu' di {MaxSuggestions} contatti, continua a digitare";
        }
        else
        {
            message = byExtension
                ? $"{_suggestions.Count} voci su questo interno, freccia giu' per scegliere"
                : $"{_suggestions.Count} contatti, freccia giu' per scegliere";
        }

        _announcer.Announce(Announcement.CommandResponse(message, DateTimeOffset.Now));
    }

    /// <summary>
    /// Destinazione su cui agiscono Invio e il tasto di trasferimento.
    /// </summary>
    /// <remarks>
    /// Se l'elenco dei contatti è aperto e nel campo c'è un nome, la destinazione è
    /// l'interno del contatto evidenziato e non le lettere digitate. È il punto in
    /// cui la ricerca per nome diventa una chiamata, e vale per ogni percorso —
    /// composizione, trasferimento cieco, trasferimento con annuncio — proprio
    /// perché sta qui e non dentro a uno di essi.
    /// </remarks>
    private string CurrentDestination()
    {
        var typed = DialerBox.Text.Trim();

        if (SuggestionsGrid.Visibility != Visibility.Visible) return typed;

        // Con le cifre la destinazione sono le cifre, anche se l'elenco e' aperto:
        // l'elenco di un interno contiene solo voci che hanno quell'interno, quindi
        // scegliere una riga o non sceglierla porta allo stesso numero.
        if (!LooksLikeName(typed)) return typed;

        var chosen = SuggestionsGrid.SelectedItem as DialerSuggestion ?? _suggestions.FirstOrDefault();
        return chosen?.Number ?? typed;
    }

    /// <summary>Chiude l'elenco dei contatti proposti.</summary>
    private void HideSuggestions()
    {
        if (SuggestionsGrid.Visibility == Visibility.Collapsed && _suggestions.Count == 0) return;

        // Se il focus e' dentro l'elenco che sta per sparire va riportato al campo:
        // altrimenti resta su una riga che non esiste piu', e chi non vede lo
        // schermo non ha modo di capire dove sia finito il cursore.
        var hadFocus = SuggestionsGrid.IsKeyboardFocusWithin;

        _suggestions.Clear();
        SuggestionsGrid.Visibility = Visibility.Collapsed;

        if (hadFocus) ReturnToDialer();
    }

    private void ReturnToDialer()
    {
        DialerBox.Focus();

        // Il cursore va in fondo e non seleziona tutto: si torna al campo per
        // correggere o continuare il nome, non per sostituirlo.
        DialerBox.CaretIndex = DialerBox.Text.Length;
    }

    /// <summary>
    /// Riporta il cursore nel campo di composizione e seleziona quello che c'è già.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il campo non si svuota: si seleziona.</b> Sono due cose diverse e la
    /// differenza è tutta per chi non vede lo schermo. Svuotarlo perderebbe il numero
    /// che l'operatore aveva appena digitato, e quando un trasferimento non riesce
    /// quel numero serve ancora: rifarlo a memoria con il chiamante in linea è la
    /// scomodità che il campo persistente era nato per evitare. Selezionarlo lascia
    /// tutto dov'è e insieme rende inutile cancellare: la prima cifra del nuovo
    /// interno sostituisce quello vecchio.
    /// </para>
    /// <para>
    /// Senza questo, dopo una risposta o un trasferimento il campo restava con dentro
    /// il numero di prima e il cursore chissà dove. Chi non vede non ha modo di
    /// accorgersene: digitava il nuovo interno e otteneva le cifre attaccate a quelle
    /// vecchie, oppure doveva premere Canc alla cieca finché il campo non taceva.
    /// </para>
    /// <para>
    /// <b>Solo a finestra in primo piano.</b> Il comando può arrivare da una
    /// scorciatoia globale mentre l'operatore sta lavorando nella rubrica o nelle
    /// impostazioni: spostargli il cursore in un'altra finestra sarebbe un salto che
    /// non ha chiesto, e con il lettore di schermo diventa un annuncio che non
    /// spiega dove sia finito.
    /// </para>
    /// </remarks>
    private void PrepareDialerForNextDestination()
    {
        if (!IsActive) return;

        DialerBox.Focus();
        DialerBox.SelectAll();
    }

    /// <summary>
    /// Alla fine di una procedura di trasferimento il campo torna pronto.
    /// </summary>
    /// <remarks>
    /// Vale a trasferimento riuscito e a trasferimento annullato, perché in entrambi
    /// i casi il gesto successivo dell'operatore è digitare una destinazione: la
    /// prossima, oppure la stessa corretta di una cifra.
    /// </remarks>
    private void OnTransferPhaseChanged(object? sender, TransferPhase phase)
        => Dispatcher.Invoke(() =>
        {
            if (phase != TransferPhase.Idle) return;
            PrepareDialerForNextDestination();
        });

    /// <summary>
    /// Comandi dell'elenco dei contatti proposti.
    /// </summary>
    /// <remarks>
    /// Si intercetta la fase di anteprima perché la
    /// <see cref="System.Windows.Controls.DataGrid"/> tratta Invio e le frecce
    /// prima di qualunque gestore registrato sull'evento normale.
    /// Le frecce orizzontali non compaiono: sono il movimento fra le celle, cioè la
    /// lettura di nominativo e interno, e restano alla tabella.
    /// </remarks>
    private async void OnSuggestionsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await DialAsync().ConfigureAwait(true);
                break;

            case Key.Up when GridNavigation.CurrentRowIndex(SuggestionsGrid) == 0:
                // Dal primo contatto si torna al campo: chiudere il ciclo evita di
                // dover cercare il Tab all'indietro.
                e.Handled = true;
                ReturnToDialer();
                break;
        }
    }

    private async void OnSuggestionsDoubleClick(object sender, MouseButtonEventArgs e)
        => await DialAsync().ConfigureAwait(true);

    // -----------------------------------------------------------------------
    // Riflesso dello stato nell'interfaccia
    // -----------------------------------------------------------------------

    private void OnQueueSnapshotUpdated(object? sender, QueueSnapshot snapshot)
        => Dispatcher.Invoke(() => RefreshQueue(snapshot));

    private void RefreshQueue(QueueSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        var selectedId = (QueueList.SelectedItem as QueueRow)?.Id;

        _queueRows.Clear();

        foreach (var caller in snapshot.Waiting)
        {
            _queueRows.Add(new QueueRow(
                caller.Position?.ToString() ?? string.Empty,
                caller.SpeechIdentity ?? "sconosciuto",
                SpeechFormatter.Duration(caller.WaitDuration(now)),
                caller.QueueName,
                caller switch
                {
                    { IsEmergency: true } => "Emergenza",
                    { IsVip: true } => "VIP",
                    _ => string.Empty,
                },
                caller.Id,
                // Il numero va letto cifra per cifra: pronunciato come intero e'
                // inutilizzabile per trascriverlo o richiamarlo.
                SpeechFormatter.Party(
                    caller.CallerName,
                    caller.CallerNumber,
                    _settings.Accessibility.DigitGrouping)));
        }

        // Il ripristino della selezione evita che il cursore dell'operatore salti a
        // ogni aggiornamento: su una lista che si ricostruisce ogni tre secondi,
        // perdere la posizione rende impossibile scorrerla.
        if (selectedId is not null)
        {
            var restored = _queueRows.FirstOrDefault(r => r.Id == selectedId);
            if (restored is not null) QueueList.SelectedItem = restored;
        }

        LiveRegion.Update(QueueHeaderText, snapshot.WaitingCount == 0
            ? "Chiamate in coda: nessuna"
            : $"Chiamate in coda: {snapshot.WaitingCount}");

        UpdateStatus();
    }

    private void OnQueueSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Nessun annuncio esplicito: la DataGrid e' un controllo standard e lo
        // screen reader legge gia' la riga selezionata attraverso UI Automation.
        // Aggiungere una voce nostra produrrebbe una lettura doppia.
    }

    /// <summary>
    /// Riflette il cambio di stato e, se richiesto, riporta davanti la finestra.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È l'unica deroga alla regola per cui il focus si sposta solo su azione
    /// dell'operatore, ed è necessaria: con la finestra riposta nell'area di notifica
    /// non c'è nulla su cui agire, i comandi locali non arrivano, e l'operatore
    /// dovrebbe cercare l'icona mentre il telefono squilla.
    /// </para>
    /// <para>
    /// Vale solo per le chiamate <b>entranti che iniziano a squillare</b>. Ogni altro
    /// cambio di stato — risposta, attesa, trasferimento, chiusura — non tocca il
    /// primo piano: sono conseguenze di azioni già decise dall'operatore, e portare
    /// avanti la finestra a ogni transizione la renderebbe una finestra che salta
    /// addosso.
    /// </para>
    /// <para>
    /// Chi prende appunti in un altro programma può disattivare la deroga dalle
    /// impostazioni: gli restano le scorciatoie globali, che rispondono senza
    /// cambiare finestra.
    /// </para>
    /// </remarks>
    private void OnCallStateChanged(object? sender, CallStateChangedEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            UpdateStatus();

            // La registrazione non sopravvive alla chiamata che stava registrando: se
            // e' finita, la combinazione deve tornare a significare "avvia".
            _recording.OnCallsChanged();

            // La modalita' toni non sopravvive alla chiamata: restarci senza saperlo
            // significherebbe mandare in linea le cifre del numero successivo invece
            // di scriverle nel campo di composizione.
            // In conferenza la chiamata attiva non c'e', ma la conversazione si':
            // uscire dalla modalita' toni li' sarebbe corretto solo per caso, e
            // rientrarci dopo lo scioglimento costerebbe una pressione in piu' in un
            // menu automatico dove ogni cifra fuori posto porta nel ramo sbagliato.
            if (_dtmfMode && _calls.ActiveCall is null && !_calls.IsConferenceActive)
            {
                LeaveDtmfMode("Chiamata terminata: modalita' toni disattivata");
            }

            if (e.Transition.To != CallState.Alerting) return;
            if (e.Call.Direction != CallDirection.Inbound) return;
            if (!_settingsService.Current.Tray.RestoreOnIncomingCall) return;

            _log.LogInformation("Chiamata entrante: finestra riportata in primo piano");

            // Nessun annuncio proprio: la chiamata entrante ne produce gia' uno con
            // priorita' critica, e dire anche "finestra riaperta" ritarderebbe il nome
            // del chiamante, che e' l'informazione per cui l'operatore sta aspettando.
            //
            // L'eccezione e' il primo piano negato, ed e' l'unico caso in cui vale la
            // pena rubare qualche decimo di secondo al nome del chiamante: senza,
            // l'operatore preme F2 dentro un altro programma e non capisce perche' il
            // telefono continui a squillare.
            if (!RestoreWindow(null)) AnnounceForegroundDenied();
        });

    /// <summary>
    /// Avverte che i tasti locali non arrivano e dice con che cosa rispondere.
    /// </summary>
    /// <remarks>
    /// Priorità alta e non critica: l'annuncio della chiamata entrante deve restare
    /// il primo: chi chiama e da dove viene prima di come rispondere.
    /// </remarks>
    private void AnnounceForegroundDenied()
        => _announcer.Announce(Announcement.High(
            ForegroundDeniedNotice.Build(_hotkeys), DateTimeOffset.Now));

    private void OnRegistrationChanged(object? sender, SipRegistrationState state)
        => Dispatcher.Invoke(UpdateRegistrationText);

    /// <summary>
    /// Aggiorna la riga di stato della registrazione.
    /// </summary>
    /// <remarks>
    /// La modalità simulata ha la precedenza su qualunque altro stato: è
    /// l'informazione che cambia il significato di tutto il resto, e mostrarla come
    /// un semplice "non registrato" la farebbe confondere con un problema di rete
    /// transitorio.
    /// </remarks>
    private void UpdateRegistrationText()
    {
        var state = _telephony.RegistrationState;

        var text = _telephony.IsSimulated
            ? "MODALITA' SIMULATA - nessuna telefonata reale. Configura l'account SIP con Control virgola."
            : state.IsReachable
                ? $"Telefono registrato ({state.AccountName})"
                : $"Telefono NON registrato. {state.FailureReason ?? string.Empty}".Trim();

        LiveRegion.Update(RegistrationText, text);

        // Lo stesso stato finisce sull'icona dell'area di notifica: con la finestra
        // riposta e' l'unico posto in cui si puo' verificare che il telefono sia
        // ancora in servizio.
        //
        // La pausa viene prima di tutto il resto, ed e' il caso che conta di piu':
        // e' proprio con la finestra riposta che si rischia di restare fuori servizio
        // senza ricordarsene, e il suggerimento dell'icona e' l'unico posto in cui
        // guardare per accorgersene.
        _tray?.UpdateStatus(_duty.IsPaused
            ? "BlindPhone - FUORI SERVIZIO, non riceve chiamate"
            : _telephony.IsSimulated
                ? "BlindPhone - modalita' simulata"
                : state.IsReachable
                    ? $"BlindPhone - registrato ({state.AccountName})"
                    : "BlindPhone - NON registrato");
    }

    private void UpdateStatus()
    {
        UpdateIncomingBanner();
        UpdateActiveCallBanner();
        UpdateServicePauseButton();
        UpdateCommandBar();
        UpdateDialerAvailability();

        var conference = _calls.ConferencedCalls;

        // CHI ASPETTA, NON CHI E' NELLO STATO "IN ATTESA".
        //
        // Sono due insiemi diversi per una sola riga della macchina a stati: durante
        // un trasferimento con annuncio il chiamante e' parcheggiato dentro la
        // procedura, non «in attesa», e con le sole HeldCalls le tre righe che parlano
        // di lui — il riquadro della chiamata, il conteggio delle attese e la casella
        // di riepilogo — dichiaravano tutte insieme che non stava aspettando nessuno.
        // A chi aspetta la distinzione non arriva: sente la stessa musica e conta gli
        // stessi minuti.
        var held = _calls.WaitingCalls;

        if (conference.Count > 0)
        {
            ActiveCallText.Text = CallBoardTexts.InCall(null, conference);

            UpdateSecondaryStatus(held);
            return;
        }

        var active = _calls.ActiveCall;

        // QUESTA RIGA NON PARLA PIU', E LA SCRITTURA E' DIRETTA PER QUESTO.
        //
        // Toglierle LiveSetting dal XAML non sarebbe bastato: LiveRegion.Update
        // solleva comunque l'evento di regione cambiata, ed e' quello che faceva
        // parlare lo screen reader. Chi racconta cosa succede e' la pipeline degli
        // annunci, una volta sola; questa riga risponde a "come sono messo adesso" a
        // chi ci passa sopra con il cursore virtuale.
        //
        // Il testo dice anche la pausa. Con una chiamata in attesa "nessuna chiamata
        // attiva" e' vero alla lettera e falso nella sostanza: chi guarda lo schermo
        // legge che non sta succedendo niente mentre qualcuno aspetta in linea.
        ActiveCallText.Text = active is not null
            ? CallBoardTexts.InCall(active)
            : held.Count > 0
                ? $"Chiamata in pausa: {CallBoardTexts.Identity(held[0].RemoteParty)}"
                  + (held.Count > 1 ? $"   (+{held.Count - 1} in attesa)" : string.Empty)
                : "Nessuna chiamata attiva";

        UpdateSecondaryStatus(held);
    }

    /// <summary>Riga di stato secondaria: coda, attese, stato agente.</summary>
    /// <remarks>
    /// <para>
    /// Il conteggio della coda compare solo se la coda esiste. Senza collegamento
    /// all'API del centralino varrebbe zero per sempre, e comparirebbe a schermo per
    /// non dire mai nulla.
    /// </para>
    /// <para>
    /// Come la riga della chiamata, non parla da sola: si scrive senza sollevare
    /// l'evento di regione dinamica. Un riepilogo che si legge da sé a ogni chiamata
    /// che entra, che si risponde e che si chiude ripete cose che gli annunci hanno
    /// già detto meglio.
    /// </para>
    /// </remarks>
    private void UpdateSecondaryStatus(IReadOnlyList<Call> held)
    {
        var parts = new List<string>(3);

        if (_queueSectionVisible != false)
        {
            parts.Add($"Coda: {_queueProvider.CurrentSnapshot.WaitingCount}");
        }

        parts.Add($"In attesa: {held.Count}");
        parts.Add($"Agente: {DescribeAgent(_queueProvider.AgentState.Status)}");

        StatusText.Text = string.Join("  |  ", parts);

        UpdateCallBoards();
    }

    // -----------------------------------------------------------------------
    // Le due caselle di riepilogo: con chi sono, chi sta aspettando
    // -----------------------------------------------------------------------

    /// <summary>
    /// Riscrive le due caselle di sola lettura e regola l'orologio dell'attesa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Le caselle sono l'equivalente visivo delle spie di linea di un centralino
    /// tradizionale, e servono a chi guarda lo schermo: il collega che affianca
    /// l'operatore, il responsabile che passa dalla postazione, l'operatore ipovedente
    /// che lavora con l'ingrandimento. Gli annunci raccontano ciò che <b>è appena
    /// successo</b> e poi svaniscono; queste due righe dicono <b>come si è messi
    /// adesso</b> e restano.
    /// </para>
    /// <para>
    /// Deliberatamente <b>non</b> sono regioni dinamiche e non passano da
    /// <see cref="LiveRegion"/>: il tempo di attesa cambia ogni secondo, e notificarlo
    /// alla tecnologia assistiva significherebbe far parlare lo screen reader
    /// ininterrottamente, sopra la conversazione in corso. Chi non vede ha già
    /// l'annuncio della messa in attesa e il promemoria periodico, che sono dosati per
    /// non coprire la voce del chiamante.
    /// </para>
    /// </remarks>
    private void UpdateCallBoards()
    {
        var now = DateTimeOffset.Now;

        SetBoardText(ConnectedToBox, CallBoardTexts.Connected(_calls.ActiveCall, _calls.ConferencedCalls));
        SetBoardText(OnHoldBox, CallBoardTexts.OnHold(_calls.WaitingCalls, now));
        UpdateServiceClock(now);
    }

    /// <summary>Fa avanzare di un secondo i tempi mostrati.</summary>
    /// <remarks>
    /// Mentre il cursore è dentro la casella delle attese non si riscrive nulla:
    /// l'operatore sta selezionando il numero per copiarlo altrove, e riscrivere il
    /// testo gli cancellerebbe la selezione una volta al secondo, cioè renderebbe
    /// impossibile proprio il gesto per cui la casella è una casella di testo e non
    /// un'etichetta. Alla prima variazione di stato il testo si riallinea da solo.
    /// </remarks>
    private void TickClocks()
    {
        var now = DateTimeOffset.Now;

        UpdateServiceClock(now);
        UpdateIncomingTimer(now);

        if (OnHoldBox.IsKeyboardFocusWithin) return;

        SetBoardText(OnHoldBox, CallBoardTexts.OnHold(_calls.WaitingCalls, now));
    }

    /// <summary>
    /// Riscrive il cronometro dello squillo nel riquadro della chiamata entrante.
    /// </summary>
    /// <param name="now">Istante corrente.</param>
    /// <remarks>
    /// Si misura dalla creazione della chiamata, che per una entrante è l'istante in
    /// cui ha cominciato a squillare. Quando non c'è nessuna chiamata in squillo non
    /// si scrive niente: il riquadro è nascosto, e riscriverlo a vuoto una volta al
    /// secondo sarebbe lavoro per nessuno.
    /// </remarks>
    private void UpdateIncomingTimer(DateTimeOffset now)
    {
        if (_calls.RingingCall is not { } ringing) return;

        IncomingCallTimerText.Text = CallBoardTexts.ShortDuration(now - ringing.CreatedAt);
    }

    /// <summary>Riscrive l'orologio del turno nell'intestazione della finestra.</summary>
    private void UpdateServiceClock(DateTimeOffset now)
        => ServiceClockText.Text = CallBoardTexts.ServiceClock(_duty.InService(now), _duty.CurrentPause(now));

    // -----------------------------------------------------------------------
    // La finestra si allarga per contenere quello che c'è scritto
    // -----------------------------------------------------------------------

    /// <summary>
    /// Porta la misura iniziale della finestra dentro l'area di lavoro dello schermo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>La finestra nasceva sempre a 1000 per 700, qualunque fosse lo schermo.</b> Su
    /// un monitor basso — un portatile da 768 righe, o un 1080 con la scala al 150 per
    /// cento, che in unità indipendenti dal dispositivo dà 720 — l'area di lavoro è più
    /// piccola della finestra: il bordo basso finisce sotto la barra delle applicazioni
    /// e con lui il campo di composizione, cioè la cosa per cui si apre il programma.
    /// Lo stesso vale in larghezza sugli schermi stretti.
    /// </para>
    /// <para>
    /// Si riduce soltanto, e mai sotto le misure minime dichiarate: la finestra non si
    /// ingrandisce da sé oltre quello che il codice XAML dice, e su uno schermo che non
    /// la contiene nemmeno al minimo non c'è una scelta giusta da fare qui.
    /// </para>
    /// <para>
    /// Va fatto prima della prima visualizzazione: con il centraggio automatico la
    /// posizione si calcola sulla misura che la finestra ha in quel momento, e una
    /// riduzione successiva la lascerebbe decentrata.
    /// </para>
    /// </remarks>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;

        Height = Math.Max(MinHeight, Math.Min(Height, area.Height));
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width));
    }

    /// <summary>Ridimensionamento già in coda, per non chiederne uno per fotogramma.</summary>
    private bool _resizePending;

    /// <summary>
    /// Altezza mancante a cui si è già dovuto rinunciare perché lo schermo finisce.
    /// </summary>
    /// <remarks>
    /// Senza, ogni calcolo di impaginazione rimetterebbe in coda un ridimensionamento
    /// che non può riuscire, per tutto il tempo in cui la finestra resta alta quanto lo
    /// schermo. Il valore si supera da sé: se il contenuto cresce ancora, la mancanza
    /// diventa maggiore di quella a cui si era rinunciato e si riprova.
    /// </remarks>
    private double _unreachableGrowth;

    /// <summary>
    /// Si accorge che il contenuto non ci sta più in altezza.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il difetto che chiude</b>: con un nome di chiamante lungo il riquadro
    /// dell'avviso va a capo e cresce, e il contenuto non ci sta più. WPF non lo segnala
    /// in alcun modo — non c'è una barra di scorrimento, non c'è un segno che manchi
    /// qualcosa: la finestra taglia, e a sparire è quello che sta in fondo. Su questa
    /// schermata «in fondo» vuol dire il campo di composizione.
    /// </para>
    /// <para>
    /// <b>Perché di qui e non dai punti che riscrivono i testi</b>: i modi di cambiare
    /// altezza sono molti — l'avviso di chiamata, la barra dei comandi che cambia
    /// numero di pulsanti, il riquadro della pausa — e ricordarsi di chiamare il
    /// controllo in ognuno è il genere di cosa che si dimentica proprio nel caso nuovo.
    /// Qui il controllo è una sottrazione e finisce subito quando non c'è niente da
    /// fare.
    /// </para>
    /// <para>
    /// <b>L'elenco dei contatti proposti non è fra questi</b>: è sovrapposto alle altre
    /// sezioni e non occupa altezza, quindi comparire non richiede nessun allargamento.
    /// Prima richiedeva spazio sotto il campo di composizione, e dove la finestra non
    /// poteva più crescere — schermi bassi — l'elenco restava tagliato.
    /// </para>
    /// </remarks>
    private void OnRootLayoutUpdated(object? sender, EventArgs e)
    {
        // Prima di tutto il resto, e a finestra ingrandita come a finestra normale: un
        // elenco fuori dalla finestra e una lista che toglie il campo di composizione
        // si presentano in entrambi i casi.
        if (IsLoaded && WindowState != WindowState.Minimized)
        {
            PositionSuggestions();
            ScheduleListFit();
        }

        if (_resizePending) return;
        if (!IsLoaded || WindowState != WindowState.Normal) return;

        // Un pixel di tolleranza: gli arrotondamenti del calcolo di impaginazione
        // produrrebbero altrimenti un ridimensionamento continuo da frazioni di pixel.
        var missing = MissingHeight();
        if (missing < 1.0) return;
        if (missing <= _unreachableGrowth + 0.5) return;

        // Il ridimensionamento non si fa qui dentro: cambiare l'altezza della finestra
        // durante il calcolo dell'impaginazione ne fa partire un altro dall'interno del
        // primo. Si rimanda al giro successivo del dispatcher, quando questo e' chiuso.
        _resizePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(GrowToFitContent));
    }

    // -----------------------------------------------------------------------
    // Elenco dei contatti proposti e liste: stare dentro la finestra
    // -----------------------------------------------------------------------

    /// <summary>Adeguamento dell'altezza delle liste già in coda.</summary>
    private bool _listFitPending;

    /// <summary>
    /// Porta l'elenco dei contatti proposti dentro la finestra.
    /// </summary>
    /// <remarks>
    /// <para>
    /// L'elenco è ancorato al bordo alto del campo di composizione. Quando il campo sta
    /// sotto il bordo della finestra — con coda e colleghi su uno schermo basso — così
    /// ancorato scendeva con lui, e fuori dalla finestra non restava che la fascia
    /// delle intestazioni: i risultati si sentivano e non si vedevano.
    /// </para>
    /// <para>
    /// Qui, a ogni impaginazione, si riporta il bordo basso dell'elenco al più in basso
    /// che la finestra consente, e se ne limita l'altezza allo spazio che sta sopra. Il
    /// calcolo è in <see cref="MainWindowLayout.PlaceSuggestions"/>. Non cambia altezza
    /// a nessun'altra sezione, quindi non innesca altro: la seconda impaginazione che
    /// questa produce ritrova gli stessi valori e si ferma.
    /// </para>
    /// </remarks>
    private void PositionSuggestions()
    {
        if (SuggestionsGrid.Visibility != Visibility.Visible) return;
        if (!SuggestionsCanvas.IsLoaded) return;
        if (VisualTreeHelper.GetParent(RootGrid) is not FrameworkElement host || host.ActualHeight <= 0) return;

        var canvasTop = SuggestionsCanvas.TranslatePoint(new System.Windows.Point(0, 0), host).Y;

        var placement = MainWindowLayout.PlaceSuggestions(
            canvasTop,
            visibleTop: RootGrid.Margin.Top,
            visibleBottom: host.ActualHeight - RootGrid.Margin.Bottom);

        var bottom = System.Windows.Controls.Canvas.GetBottom(SuggestionsGrid);

        if (double.IsNaN(bottom) || Math.Abs(bottom - placement.Bottom) >= 1)
        {
            System.Windows.Controls.Canvas.SetBottom(SuggestionsGrid, placement.Bottom);
        }

        if (Math.Abs(SuggestionsGrid.MaxHeight - placement.MaxHeight) >= 1)
        {
            SuggestionsGrid.MaxHeight = placement.MaxHeight;
        }
    }

    /// <summary>
    /// Altezza utile massima che la finestra può dare al proprio contenuto.
    /// </summary>
    /// <param name="host">Il contenitore della griglia principale, cioè l'area utile.</param>
    /// <remarks>
    /// A finestra normale è quella che la finestra raggiungerebbe allargandosi fino al
    /// bordo dello schermo, perché è fin lì che <see cref="GrowToFitContent"/> la porta;
    /// a finestra ingrandita è quella che ha. Lo scarto fra l'altezza della finestra e
    /// quella dell'area utile è la cornice — titolo e bordi — che il sistema aggiunge.
    /// </remarks>
    private double ContentCapacity(FrameworkElement host)
    {
        if (WindowState != WindowState.Normal) return host.ActualHeight;

        var frame = ActualHeight - host.ActualHeight;

        return Math.Max(host.ActualHeight, SystemParameters.WorkArea.Height - frame);
    }

    /// <summary>
    /// Stabilisce quale altezza minima dare alle liste visibili, e a quali.
    /// </summary>
    /// <returns>Le righe delle liste visibili e l'altezza voluta, oppure <c>null</c> se non c'è niente da decidere.</returns>
    private (List<System.Windows.Controls.RowDefinition> Rows, double Wanted)? PlanListMinimum()
    {
        List<System.Windows.Controls.RowDefinition> rows = [];

        if (QueueSection.Visibility == Visibility.Visible) rows.Add(QueueRow);
        if (ColleaguesSection.Visibility == Visibility.Visible) rows.Add(ColleaguesRow);

        if (rows.Count == 0) return null;
        if (VisualTreeHelper.GetParent(RootGrid) is not FrameworkElement host || host.ActualHeight <= 0) return null;

        var capacity = ContentCapacity(host) - RootGrid.Margin.Top - RootGrid.Margin.Bottom;

        // Le altre righe hanno altezza propria: nessuna si allunga se una lista cede.
        var fixedHeight = RootGrid.RowDefinitions.Where(r => !rows.Contains(r)).Sum(r => r.ActualHeight);

        return (rows, MainWindowLayout.ListMinimum(capacity, fixedHeight, rows.Count));
    }

    /// <summary>
    /// Chiede l'adeguamento dell'altezza minima delle liste, se serve.
    /// </summary>
    /// <remarks>
    /// Come per l'allargamento della finestra, non si agisce dentro il calcolo
    /// dell'impaginazione ma al giro successivo del dispatcher. Costa un confronto fra
    /// due numeri a ogni impaginazione, e finisce lì quando le liste hanno già
    /// l'altezza giusta.
    /// </remarks>
    private void ScheduleListFit()
    {
        if (_listFitPending) return;
        if (PlanListMinimum() is not { } plan) return;
        if (!plan.Rows.Any(r => Math.Abs(r.MinHeight - plan.Wanted) >= 1)) return;

        _listFitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FitListMinimums));
    }

    /// <summary>
    /// Lascia alle liste l'altezza minima che lo spazio consente.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il difetto che chiude.</b> Le due liste hanno un minimo di 180 pixel ciascuna,
    /// scelto perché sotto mostrano solo i nomi delle colonne. Su uno schermo basso
    /// quei 360 pixel più il resto non entrano, la finestra non può crescere oltre lo
    /// schermo e a restare fuori è il campo di composizione, che sta in fondo.
    /// </para>
    /// <para>
    /// La scelta è di priorità: fra una lista più corta e un campo di composizione che
    /// non si vede, vince il campo. La lista resta percorribile con la tastiera e letta
    /// per intero dallo screen reader, il campo no. Dove lo spazio basta nulla cambia: il
    /// valore resta 180 e la finestra si allarga, come prima, per darglielo.
    /// </para>
    /// </remarks>
    private void FitListMinimums()
    {
        _listFitPending = false;

        if (!IsLoaded || PlanListMinimum() is not { } plan) return;

        foreach (var row in plan.Rows)
        {
            if (Math.Abs(row.MinHeight - plan.Wanted) >= 1) row.MinHeight = plan.Wanted;
        }

        // Con un minimo diverso la mancanza di prima non vale piu': si riprova a
        // contenere il contenuto.
        _unreachableGrowth = 0;

        _log.LogDebug("Altezza minima delle liste portata a {Altezza} pixel", (int)plan.Wanted);
    }

    /// <summary>
    /// Quanto contenuto è rimasto fuori dalla finestra, in altezza.
    /// </summary>
    /// <returns>Pixel indipendenti dal dispositivo, zero o negativo se ci sta tutto.</returns>
    /// <remarks>
    /// <para>
    /// <b>Misurato, non dedotto</b>, e le due strade più naturali sono state provate e
    /// scartate sul banco di prova del layout.
    /// </para>
    /// <para>
    /// <b>Sommare le altezze delle righe</b> e confrontarle con quella della griglia non
    /// rileva niente: quando le righe non ci stanno <b>la griglia si allarga con loro</b>
    /// e sfonda la finestra invece di comprimersi. Con la finestra a 480 la griglia
    /// stava a 622, e i due numeri coincidevano sempre.
    /// </para>
    /// <para>
    /// <b>Confrontare <c>ActualHeight</c> con <c>DesiredSize</c></b> funziona quando il
    /// contenuto è troppo e sbaglia quando è poco: i due numeri si scambiano i ruoli, e
    /// la differenza resta positiva in tutti e due i casi. Con la sezione della coda
    /// nascosta la finestra cresceva a vuoto, e ogni allargamento ne chiedeva un altro.
    /// </para>
    /// <para>
    /// <b>Il numero giusto ce l'ha chi contiene la griglia</b>: la sua altezza è l'area
    /// utile della finestra, e non segue il contenuto. Nelle tre misure di collaudo —
    /// contenuto in eccesso, contenuto giusto, finestra alla misura minima — il conto
    /// dà rispettivamente 113, 0 e 333, e l'allargamento converge sempre alla prima
    /// correzione, senza scatti.
    /// </para>
    /// </remarks>
    private double MissingHeight()
    {
        if (VisualTreeHelper.GetParent(RootGrid) is not FrameworkElement host) return 0;

        var granted = host.ActualHeight - RootGrid.Margin.Top - RootGrid.Margin.Bottom;

        // Prima del primo calcolo di impaginazione non c'e' ancora niente da confrontare.
        return granted <= 0 ? 0 : RootGrid.ActualHeight - granted;
    }

    /// <summary>
    /// Allarga la finestra di quanto serve, senza uscire dallo schermo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cresce e basta: non rimpicciolisce mai da sé.</b> Una finestra che si allarga
    /// alla chiamata e si stringe quando finisce cambierebbe misura decine di volte
    /// per turno, e chi lavora con l'ingrandimento perderebbe ogni volta il punto in
    /// cui stava guardando. Allargarsi è il rimedio a un contenuto tagliato; stringersi
    /// non rimedia a niente.
    /// </para>
    /// <para>
    /// <b>Se cresce oltre il bordo dello schermo, la finestra risale.</b> Crescere verso
    /// il basso oltre l'area di lavoro nasconderebbe proprio la parte che si stava
    /// cercando di mostrare. L'area di lavoro è quella dello schermo principale: su più
    /// schermi il calcolo è approssimato, e il limite serve comunque a non produrre una
    /// finestra più alta di qualunque schermo.
    /// </para>
    /// </remarks>
    private void GrowToFitContent()
    {
        _resizePending = false;

        if (!IsLoaded || WindowState != WindowState.Normal) return;

        // Si rimisura adesso: fra la rilevazione e questo momento e' passato un giro di
        // dispatcher, e il contenuto puo' essere gia' cambiato.
        var missing = MissingHeight();
        if (missing < 1.0) return;

        var area = SystemParameters.WorkArea;
        var target = Math.Min(ActualHeight + missing, area.Height);

        if (target <= ActualHeight + 0.5)
        {
            // Lo schermo finisce qui. Si annota per non riprovare a ogni impaginazione.
            _unreachableGrowth = missing;
            _log.LogDebug(
                "Contenuto piu' alto dello schermo: restano fuori {Mancanti} pixel", (int)missing);

            return;
        }

        _log.LogDebug(
            "Finestra allargata da {Prima} a {Dopo} pixel: al contenuto ne mancavano {Mancanti}",
            (int)ActualHeight, (int)target, (int)missing);

        _unreachableGrowth = 0;
        Height = target;

        if (Top + target > area.Bottom) Top = Math.Max(area.Top, area.Bottom - target);
    }

    /// <summary>Scrive una casella solo se il testo è cambiato davvero.</summary>
    /// <remarks>
    /// Riassegnare lo stesso testo azzererebbe la selezione dell'operatore e sposterebbe
    /// il punto di inserimento: su una casella riscritta ogni secondo è la differenza
    /// fra poterci lavorare e non poterci lavorare.
    /// </remarks>
    private static void SetBoardText(System.Windows.Controls.TextBox box, string text)
    {
        if (box.Text == text) return;
        box.Text = text;
    }

    /// <summary>
    /// Riflette l'annuncio pronunciato nella regione dinamica UI Automation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serve a due categorie di utenti. Chi usa il cursore virtuale dello screen
    /// reader ritrova a schermo ciò che ha appena sentito e può rileggerlo con
    /// calma. Chi ha configurato la modalità solo UIA non riceve alcuna voce
    /// dall'applicazione, e questa notifica è l'unico canale.
    /// </para>
    /// <para>
    /// Si usa <c>RaiseNotificationEvent</c> e non lo spostamento del focus: l'evento
    /// fa parlare lo screen reader senza muovere il cursore dell'operatore, che è
    /// esattamente il comportamento richiesto dal principio per cui il focus si
    /// sposta solo su azione esplicita.
    /// </para>
    /// </remarks>
    private void OnAnnouncementSpoken(object? sender, Announcement announcement)
        => Dispatcher.Invoke(() =>
        {
            // Qui si usa la notifica e non la regione dinamica: l'annuncio e' un
            // evento puntuale, e la notifica lo fa pronunciare senza spostare il
            // focus. La regione dinamica serve invece ai valori che permangono e
            // che l'operatore puo' rileggere.
            LastAnnouncementText.Text = announcement.Text;

            var peer = UIElementAutomationPeer.FromElement(LastAnnouncementText)
                       ?? UIElementAutomationPeer.CreatePeerForElement(LastAnnouncementText);

            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                announcement.Priority >= AnnouncementPriority.High
                    ? AutomationNotificationProcessing.ImportantMostRecent
                    : AutomationNotificationProcessing.MostRecent,
                announcement.Text,
                Guid.NewGuid().ToString());
        });

    // -----------------------------------------------------------------------
    // Ausiliari
    // -----------------------------------------------------------------------

    private static string DescribeAgent(AgentStatus status) => status switch
    {
        AgentStatus.Available => "disponibile",
        AgentStatus.Paused => "in pausa",
        AgentStatus.LoggedOut => "disconnesso",
        AgentStatus.Busy => "occupato",
        AgentStatus.WrapUp => "post chiamata",
        _ => "sconosciuto",
    };

    private static HotkeyModifiers ToHotkeyModifiers(ModifierKeys keys)
    {
        var result = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (keys.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (keys.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Windows;
        return result;
    }

    /// <summary>Converte il nome di un tasto nel codice virtuale di Windows.</summary>
    private static uint ToVirtualKey(string keyName)
        => Enum.TryParse<Key>(keyName, ignoreCase: true, out var key)
            ? (uint)KeyInterop.VirtualKeyFromKey(key)
            : 0;

    private async void OnClosed(object? sender, EventArgs e)
    {
        _suggestionTimer.Stop();
        _clockTimer.Stop();
        _suggestionSearch?.Dispose();

        _announcements.Spoken -= OnAnnouncementSpoken;
        _queueProvider.SnapshotUpdated -= OnQueueSnapshotUpdated;
        _calls.CallStateChanged -= OnCallStateChanged;
        _transfer.PhaseChanged -= OnTransferPhaseChanged;
        _telephony.RegistrationStateChanged -= OnRegistrationChanged;
        _globalHotkeys.HotkeyPressed -= OnGlobalHotkeyPressed;
        _hotkeyStore.ActiveProfileChanged -= OnHotkeyProfileChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;

        _globalHotkeys.Dispose();
        _headset?.Dispose();

        // Senza questa riga l'icona resta nell'area di notifica dopo la chiusura,
        // finche' qualcuno non ci passa sopra il mouse: su una postazione dove
        // l'operatore non vede, resterebbe li' per sempre.
        _tray?.Dispose();
        _tray = null;

        try
        {
            await _queueProvider.StopAsync(CancellationToken.None).ConfigureAwait(true);
            await _telephony.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Arresto dei servizi non riuscito");
        }
    }
}
