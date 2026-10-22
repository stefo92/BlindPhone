using System.IO;
using System.Windows;
using System.Windows.Threading;
using BlindPhone.Accessibility;
using BlindPhone.Audio;
using BlindPhone.Contacts;
using BlindPhone.Contacts.SqlServer;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using BlindPhone.Infrastructure;
using BlindPhone.Pbx.Yeastar;
using BlindPhone.Telephony;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Punto di composizione dell'applicazione.
/// </summary>
/// <remarks>
/// <para>
/// Qui si decide quale implementazione concreta serve ciascuna astrazione del core.
/// È l'unico punto in cui il prodotto sa contemporaneamente di SIPSorcery, di
/// Yeastar, di NAudio e di WPF: tutto il resto lavora sulle interfacce.
/// </para>
/// <para>
/// <b>Il ciclo di pump.</b> L'applicazione fa avanzare a intervalli regolari la
/// pipeline di annuncio, l'orchestratore del trasferimento e il gestore delle
/// chiamate. È una scelta deliberata: i servizi del core non possiedono timer
/// propri, così il loro comportamento temporale resta deterministico nei test. Il
/// prezzo è questo timer, che vive qui e non altrove.
/// </para>
/// </remarks>
public partial class App : Application
{
    /// <summary>
    /// Se mostrare l'avviso di versione beta all'avvio. <b>Oggi è spento.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// L'avviso è la finestra che compariva prima della finestra principale e chiedeva
    /// un Invio per proseguire. È stato spento su richiesta: su una postazione che si
    /// avvia più volte al giorno una conferma obbligatoria a ogni avvio è attrito, e
    /// pesa di più proprio su chi non vede, perché va ascoltata per sapere che c'è.
    /// </para>
    /// <para>
    /// <b>Che cosa non viene più detto.</b> Quella finestra era l'unico punto in cui il
    /// programma dichiarava di essere una versione di prova, di non offrire garanzie, e
    /// di non dover essere l'unico mezzo per le chiamate di emergenza. Spegnendola quel
    /// testo non compare più da nessuna parte a tempo di esecuzione: se deve restare
    /// raggiungibile, il posto naturale è un argomento della guida in linea.
    /// </para>
    /// <para>
    /// <b>La finestra resta al suo posto.</b> Non è codice morto per distrazione:
    /// <see cref="BetaNoticeWindow"/> è completa e funzionante — porta anche la
    /// correzione per cui il suo testo arriva davvero agli screen reader — e riaccenderla
    /// costa la sola modifica di questa riga. Cancellarla obbligherebbe a riscriverla al
    /// primo collaudo esterno.
    /// </para>
    /// <para>
    /// È <c>static readonly</c> e non <c>const</c> di proposito: una costante renderebbe
    /// irraggiungibile il ramo sottostante, e la compilazione avvisa. La soluzione
    /// compila a zero avvisi ed è un livello che vale la pena difendere.
    /// </para>
    /// </remarks>
    private static readonly bool AvvisoBetaAllAvvio = false;

    private ServiceProvider? _services;
    private DispatcherTimer? _pumpTimer;
    private SingleInstanceGuard? _singleInstance;

    /// <summary>
    /// Riferimenti catturati all'avvio, per il gestore degli errori.
    /// </summary>
    /// <remarks>
    /// Il gestore non deve risolverli dal contenitore: in chiusura il contenitore è
    /// già smontato e la risoluzione solleva un'eccezione <b>dentro il gestore delle
    /// eccezioni</b>, dove non c'è più nessuno a raccoglierla. Il processo termina
    /// con una segnalazione di errore di Windows, ed è quello che succedeva.
    /// </remarks>
    private ILogger<App>? _logger;
    private IAnnouncer? _announcer;

    /// <summary>
    /// Raccoglitore dei rapporti di errore, catturato all'avvio per lo stesso motivo
    /// degli altri due.
    /// </summary>
    /// <remarks>
    /// Anzi, a maggior ragione: i gestori di ultima istanza scattano quando il
    /// processo sta morendo, cioè quando risolvere qualcosa dal contenitore è
    /// l'operazione con meno probabilità di riuscire.
    /// </remarks>
    private CrashReporter? _crashes;

    /// <summary>Servizio che applica il tema dei colori. Vive quanto l'applicazione.</summary>
    private ThemeService? _theme;

    /// <summary>Se raccogliere i rapporti, secondo la configurazione.</summary>
    private bool _collectCrashReports = true;

    /// <summary>Indica che la chiusura è iniziata.</summary>
    /// <remarks>
    /// Durante la chiusura i callback in ritardo trovano oggetti già smaltiti: è
    /// atteso e non va annunciato come guasto a chi ha appena chiuso il programma.
    /// </remarks>
    private volatile bool _shuttingDown;

    /// <summary>Servizi disponibili all'applicazione.</summary>
    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("Servizi non ancora inizializzati");

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Prima di ogni altra cosa: se una copia e' gia' in esecuzione le si chiede di
        // mostrarsi e si esce subito.
        //
        // Va fatto prima di costruire i servizi, e non dopo: costruirli significa
        // aprire il file di registro, il deposito dei segreti e l'archivio della
        // rubrica, cioe' toccare risorse che appartengono gia' alla copia attiva.
        _singleInstance = new SingleInstanceGuard();

        // La segnalazione alla copia attiva l'ha gia' inviata la guardia, che poi ha
        // atteso il tempo di cortesia per il caso "chiusa e riaperta subito".
        if (!_singleInstance.IsFirstInstance)
        {
            Shutdown();
            return;
        }

        // Un'eccezione non gestita su un posto operatore significa perdere tutte le
        // chiamate in corso. Si intercetta, si registra e si informa l'operatore,
        // invece di lasciare terminare il processo in silenzio.
        //
        // TRE SORGENTI, PERCHE' TRE SONO I MODI IN CUI UN ERRORE SFUGGE.
        //
        // Il dispatcher raccoglie quelli dell'interfaccia, ed e' l'unico che si
        // possa assorbire: dopo, il programma continua. Gli altri due arrivano da
        // thread di sfondo e da attivita' i cui errori nessuno ha atteso, e li'
        // l'unica cosa che si puo' ancora fare e' lasciarne traccia prima che il
        // processo termini. Senza quella traccia, di un guasto che si presenta una
        // volta ogni tre giorni non resta niente da guardare.
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var settingsService = new SettingsService();
        var settings = settingsService.Load();

        _services = BuildServices(settingsService, settings);

        // Si catturano ora, finche' il contenitore e' vivo: il gestore degli errori
        // deve poter parlare anche quando il contenitore non c'e' piu'.
        _logger = _services.GetService<ILogger<App>>();
        _announcer = _services.GetService<IAnnouncer>();
        _crashes = _services.GetService<CrashReporter>();
        _collectCrashReports = settings.Support.CollectCrashReports;

        // Prima di costruire qualunque finestra: un pacchetto di configurazione
        // applicato dopo lascerebbe la postazione a lavorare per una sessione con
        // impostazioni che l'amministratore ha gia' sostituito, e il primo avvio e'
        // proprio quello che deve trovarla configurata.
        settings = ApplyProvisioning(settings);
        LoadConfigurationPolicy(settings);

        // IL TEMA PRIMA DELLA PRIMA FINESTRA.
        //
        // Applicarlo dopo farebbe comparire l'avviso di versione con i colori di
        // partenza e poi ridipingerlo sotto gli occhi di chi lo sta leggendo. Va anche
        // dopo il pacchetto di configurazione, perche' l'amministratore puo' aver
        // deciso il tema per tutte le postazioni dell'ente.
        _theme = new ThemeService(Resources, _services.GetService<ILogger<ThemeService>>());
        _theme.Apply(settings.Accessibility.Theme);

        // Il tema segue l'impostazione senza riavviare: cambiarlo mentre si e' in
        // linea non deve costare la chiamata in corso.
        settingsService.SettingsChanged += (_, aggiornate) => _theme?.Apply(aggiornate.Accessibility.Theme);

        // Prima riga del registro: dice dove sta il registro stesso. Serve a chi
        // dovra' andarlo a prendere dopo una segnalazione.
        _logger?.LogInformation(
            "BlindPhone avviato. Registro diagnostico in {Path}, livello {Level}, conservato {Days} giorni, "
            + "modalita' debug {Debug}",
            _services.GetRequiredService<FileLoggerProvider>().CurrentFilePath,
            _services.GetRequiredService<LogLevelSwitch>().Minimum,
            settings.DiagnosticLogRetentionDays,
            settings.DebugModeEnabled ? "attiva" : "spenta");

        StartPumpLoop();

        // LA CHIUSURA DELL'AVVISO NON DEVE SPEGNERE IL PROGRAMMA.
        //
        // Con il comportamento predefinito di WPF l'applicazione termina quando si
        // chiude l'ultima finestra aperta. Durante l'avvio l'unica finestra aperta e'
        // l'avviso di versione beta: premendo "Ho capito" quella finestra si chiude,
        // WPF conclude che non ne resta nessuna e spegne tutto prima che la finestra
        // principale venga creata.
        //
        // E' successo davvero: il registro mostrava avvio, servizi pronti e chiusura a
        // un secondo e mezzo di distanza, senza una sola riga della finestra
        // principale.
        //
        // Con l'avviso spento non si apre piu' nessuna finestra prima della principale,
        // quindi in questo momento la riga non protegge da niente. Resta per due
        // ragioni: torna necessaria appena si riaccende l'avviso, e vale per qualunque
        // altra finestra che un domani venga aperta durante l'avvio — dove il difetto
        // si ripresenterebbe identico e ugualmente difficile da leggere.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // L'avviso di versione beta viene prima della finestra principale, e dopo
        // l'avvio del ciclo di pump: senza quel ciclo l'annuncio resterebbe in coda e
        // chi non vede si troverebbe davanti una finestra muta in attesa di una
        // conferma che non sa di dover dare.
        //
        // Oggi non viene mostrato: vedi AvvisoBetaAllAvvio.
        if (!AvvisoBetaAllAvvio)
        {
            // Va registrato. Davanti a "l'avviso non compare piu'" la prima domanda e'
            // se sia stato spento di proposito o se la finestra non sia riuscita ad
            // aprirsi, e le due cose si distinguono solo da qui: il secondo caso ha una
            // riga di errore, questo ha questa.
            _logger?.LogInformation("Avviso di versione beta disattivato: non viene mostrato");
        }
        else if (!ShowBetaNotice())
        {
            _logger?.LogInformation("Avvio interrotto: avviso di versione beta non accettato");
            Shutdown();
            return;
        }

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        AnnouncePendingCrashReports();

        // Da qui in avanti vale di nuovo la regola normale: chiudere l'ultima finestra
        // spegne il programma. La finestra principale, quando l'area di notifica e'
        // attiva, si nasconde invece di chiudersi, quindi resta aperta e il posto
        // operatore continua a ricevere chiamate.
        ShutdownMode = ShutdownMode.OnLastWindowClose;
    }

    /// <summary>
    /// Applica il pacchetto di configurazione distribuito, se ne trova uno nuovo.
    /// </summary>
    /// <returns>La configurazione risultante.</returns>
    /// <remarks>
    /// <para>
    /// È il passaggio che rende utile la preconfigurazione su più postazioni: si
    /// installa il programma, si lascia il pacchetto in
    /// <c>%ProgramData%\BlindPhone</c>, e al primo avvio la postazione è configurata.
    /// </para>
    /// <para>
    /// L'esito viene <b>annunciato</b>. Una configurazione che cambia da sola senza
    /// dire nulla è indistinguibile da un guasto per chi non vede lo schermo:
    /// l'operatore troverebbe dispositivi audio diversi e tasti spostati senza sapere
    /// perché.
    /// </para>
    /// </remarks>
    private AppSettings ApplyProvisioning(AppSettings settings)
    {
        try
        {
            var servizio = _services?.GetService<ConfigurationBundleService>();
            var esito = servizio?.ApplyProvisioningIfNewer();

            if (esito is null) return settings;

            _logger?.LogInformation("Provisioning all'avvio: {Messaggio}", esito.Message);

            _announcer?.Announce(Announcement.High(
                esito.Success
                    ? $"Configurazione aggiornata dall'amministratore. {esito.Message}"
                    : $"Configurazione distribuita non applicata. {esito.Message}",
                DateTimeOffset.Now,
                coalesceKey: "provisioning"));

            return esito.Settings ?? settings;
        }
        catch (Exception ex)
        {
            // Un pacchetto malformato non deve impedire l'avvio: la postazione parte
            // con la configurazione che aveva, che e' peggio di quella nuova ma
            // infinitamente meglio di un posto operatore che non parte.
            _logger?.LogError(ex, "Applicazione del pacchetto di configurazione all'avvio non riuscita");
            return settings;
        }
    }

    /// <summary>
    /// Carica il criterio che limita ciò che l'operatore può modificare.
    /// </summary>
    /// <remarks>
    /// I due livelli si leggono qui e si combinano nel servizio: quello di postazione
    /// dal registro di Windows, quello dell'utente dalla configurazione. Il primo
    /// vince, e la differenza fra i due — uno è un vincolo, l'altro una protezione
    /// dall'errore — è documentata in <see cref="Core.Domain.ConfigurationPolicy"/>.
    /// </remarks>
    private void LoadConfigurationPolicy(AppSettings settings)
    {
        try
        {
            var servizio = _services?.GetService<ConfigurationPolicyService>();
            if (servizio is null) return;

            var postazione = _services?.GetService<MachinePolicyStore>()?.Read();

            servizio.Load(
                postazione,
                settings.Policy with
                {
                    Origin = settings.Policy.IsRestrictive
                        ? Core.Domain.PolicyOrigin.User
                        : Core.Domain.PolicyOrigin.None,
                });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Lettura del criterio di configurazione non riuscita: le impostazioni restano aperte");
        }
    }

    /// <summary>
    /// Segnala all'avvio gli errori registrati e non ancora inviati.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È la parte che rende utile la raccolta automatica. Un errore avvenuto ieri
    /// pomeriggio, su una postazione dove nessuno guarda lo schermo, non lascia
    /// altrimenti alcuna traccia percepibile: il programma si riapre e sembra tutto
    /// a posto, e la segnalazione all'assistenza — se arriva — arriva senza dati.
    /// </para>
    /// <para>
    /// L'annuncio dice <b>quanti</b>, <b>quando</b> e <b>con quale tasto</b>
    /// mandarli. Dire soltanto che qualcosa è andato storto lascerebbe l'operatore
    /// con un'informazione che non può usare.
    /// </para>
    /// </remarks>
    private void AnnouncePendingCrashReports()
    {
        try
        {
            if (_crashes is null || !_collectCrashReports) return;

            var pendenti = _crashes.Pending();
            if (pendenti.Count == 0) return;

            var profilo = _services?.GetService<HotkeyProfileStore>()?.Active;
            var tasto = profilo?.Get(
                            Core.Domain.HotkeyCommand.SendSupportPackage,
                            Core.Domain.HotkeyScope.Local)?.SpeechText
                        ?? "Control Alt I";

            var ultimo = pendenti[0];

            _logger?.LogWarning(
                "Rapporti di errore in attesa di invio: {Count}, il piu' recente del {Data}",
                pendenti.Count, ultimo.OccurredAt);

            var quando = ultimo.OccurredAt.Date == DateTimeOffset.Now.Date
                ? $"oggi alle {ultimo.OccurredAt:HH} e {ultimo.OccurredAt:mm}"
                : $"il {ultimo.OccurredAt:d MMMM} alle {ultimo.OccurredAt:HH} e {ultimo.OccurredAt:mm}";

            var testo = pendenti.Count == 1
                ? $"Nella sessione precedente si e' verificato un errore, {quando}. "
                  + $"Premi {tasto} per segnalarlo all'assistenza"
                : $"Nelle sessioni precedenti si sono verificati {pendenti.Count} errori, "
                  + $"l'ultimo {quando}. Premi {tasto} per segnalarli all'assistenza";

            _announcer?.Announce(Announcement.High(
                testo, DateTimeOffset.Now, EarconKind.PbxLinkDegraded, coalesceKey: "crash:pending"));
        }
        catch (Exception ex)
        {
            // Un problema qui non deve impedire l'avvio: al massimo l'operatore non
            // sapra' che c'e' un rapporto da mandare, e lo scoprira' aprendo la
            // diagnostica.
            _logger?.LogWarning(ex, "Segnalazione dei rapporti di errore in attesa non riuscita");
        }
    }

    /// <summary>
    /// Mostra l'avviso di versione beta e attende la conferma.
    /// </summary>
    /// <returns><c>true</c> se l'operatore ha accettato e l'avvio può proseguire.</returns>
    /// <remarks>
    /// Se l'avviso non riesce ad aprirsi si prosegue comunque: un posto operatore che
    /// non parte perché non è riuscito a mostrare una finestra informativa sarebbe un
    /// guasto peggiore del problema che l'avviso vuole prevenire. Il fatto viene
    /// registrato.
    /// </remarks>
    private bool ShowBetaNotice()
    {
        try
        {
            var notice = new BetaNoticeWindow(_services!.GetRequiredService<AnnouncementService>());

            return notice.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Avviso di versione beta non mostrato");
            return true;
        }
    }

    private static ServiceProvider BuildServices(SettingsService settingsService, AppSettings settings)
    {
        var services = new ServiceCollection();

        // Registro su file, accanto alla configurazione. Serve a rendere leggibile
        // cio' che finora si poteva solo dedurre: senza, di un guasto segnalato dal
        // campo non resta alcuna traccia dopo la chiusura del programma.
        var logProvider = new FileLoggerProvider(
            Path.Combine(settingsService.DataDirectory, "log"),
            settings.DiagnosticLogRetentionDays);

        services.AddSingleton(logProvider);

        // Il livello vive in un interruttore condiviso invece che nel filtro fisso di
        // Microsoft.Extensions.Logging: e' cio' che permette al pannello di
        // diagnostica di alzare il dettaglio mentre il guasto si sta manifestando,
        // senza riavviare e senza far cadere le chiamate in corso.
        //
        // Finche' il prodotto e' in beta il predefinito e' "tutto", modalita' debug
        // compresa: una beta viene installata per trovare difetti, e un difetto che si
        // presenta una volta ogni tre giorni si indaga soltanto se il registro c'era
        // gia' quando e' successo. Vedi AppSettings.DiagnosticLogLevel, dove sta anche
        // la nota su cosa rimettere a posto quando la beta finisce.
        var logSwitch = new LogLevelSwitch(settings.DebugModeEnabled
            ? LogLevelSwitch.Parse(settings.DiagnosticLogLevel, LogLevel.Debug)
            : LogLevelSwitch.Parse(settings.DiagnosticLogLevel));

        services.AddSingleton(logSwitch);

        var loggerFactory = LoggerFactory.Create(builder => builder
            .AddDebug()
            .AddProvider(logProvider)
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter((_, level) => logSwitch.IsEnabled(level)));

        services.AddSingleton(loggerFactory);
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // LO STACK SIP SCRIVE NELLO STESSO REGISTRO, E FINORA NON LO FACEVA.
        //
        // SIPSorcery ha un proprio LoggerFactory che, se nessuno gliene assegna uno,
        // resta quello nullo: i messaggi SIP scambiati con il centralino non
        // comparivano da nessuna parte, a nessun livello di dettaglio. Su un registro
        // dichiarato "tutto" era un'omissione che rendeva la parola falsa, e
        // soprattutto toglieva l'unica evidenza che distingue un difetto di BlindPhone
        // da un comportamento del centralino.
        //
        // Il filtro e' lo stesso: a livello normale non si vede nulla di piu' di
        // prima, e il dettaglio compare solo quando l'operatore lo alza.
        SIPSorcery.LogFactory.Set(loggerFactory);

        services.AddSingleton(settingsService);
        services.AddSingleton(settings);
        services.AddSingleton<IClock>(SystemClock.Instance);

        // ------------------------------------------------------------------
        // Accessibilita'
        // ------------------------------------------------------------------

        services.AddSingleton<NvdaScreenReaderBridge>();
        services.AddSingleton<JawsScreenReaderBridge>();

        services.AddSingleton<IEnumerable<IScreenReaderBridge>>(sp =>
        [
            // L'ordine e' quello di preferenza. NVDA e' primo perche' il suo client
            // controller offre anche la riga braille, che l'interfaccia COM di JAWS
            // non espone.
            sp.GetRequiredService<NvdaScreenReaderBridge>(),
            sp.GetRequiredService<JawsScreenReaderBridge>(),
        ]);

        services.AddSingleton(sp => new CompositeSpeechOutput(
            sp.GetRequiredService<IEnumerable<IScreenReaderBridge>>(),
            sp.GetRequiredService<ILogger<CompositeSpeechOutput>>())
        {
            Mode = Enum.TryParse<SpeechChannelMode>(settings.Accessibility.SpeechChannel, out var mode)
                ? mode
                : SpeechChannelMode.Automatic,
            InternalVoiceEnabled = settings.Accessibility.InternalVoiceEnabled,
            InternalVoiceName = settings.Accessibility.InternalVoiceName,
            InternalVoiceRate = settings.Accessibility.InternalVoiceRate,
        });

        services.AddSingleton<ISpeechOutput>(sp => sp.GetRequiredService<CompositeSpeechOutput>());

        // I dispositivi scelti dall'operatore vanno assegnati QUI, alla costruzione:
        // senza, il motore ricade sul dispositivo di comunicazione predefinito per
        // ogni via, e tutto — suoneria, annunci, conversazione — esce dallo stesso
        // posto qualunque cosa dica il pannello. Vedi AudioSettingsApplier.
        services.AddSingleton(sp =>
        {
            var engine = ActivatorUtilities.CreateInstance<NAudioEngine>(sp);
            AudioSettingsApplier.Apply(engine, settings, sp.GetRequiredService<ILogger<App>>());
            return engine;
        });
        services.AddSingleton<IAudioEngine>(sp => sp.GetRequiredService<NAudioEngine>());
        services.AddSingleton<IAudioDucker>(sp => sp.GetRequiredService<NAudioEngine>());

        services.AddSingleton(sp =>
        {
            var player = new ToneEarconPlayer(
                sp.GetRequiredService<NAudioEngine>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<ToneEarconPlayer>>())
            {
                Volume = settings.Accessibility.EarconVolume,
                UseSpatialPanning = settings.Accessibility.EarconSpatialPanning,
            };

            ApplyRingtone(player, settings, sp.GetRequiredService<ILogger<App>>());
            return player;
        });

        services.AddSingleton<IEarconPlayer>(sp => sp.GetRequiredService<ToneEarconPlayer>());

        services.AddSingleton(new AnnouncementContext());

        services.AddSingleton(BuildAnnouncementOptions(settings));

        services.AddSingleton(sp => new AnnouncementService(
            sp.GetRequiredService<ISpeechOutput>(),
            sp.GetRequiredService<IEarconPlayer>(),
            sp.GetRequiredService<AnnouncementContext>(),
            sp.GetRequiredService<AnnouncementOptions>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IAudioDucker>(),
            sp.GetRequiredService<ILogger<AnnouncementService>>()));

        services.AddSingleton<IAnnouncer>(sp => sp.GetRequiredService<AnnouncementService>());

        // ------------------------------------------------------------------
        // Rubrica
        // ------------------------------------------------------------------

        // La stessa chiave per i due archivi: sono dati della stessa postazione e
        // dello stesso operatore, e due chiavi distinte raddoppierebbero i modi di
        // perderne una senza proteggere da nulla di piu'.
        var chiaveArchivi = settingsService.Secrets.GetOrCreateDatabaseKey();

        services.AddSingleton<IContactRepository>(sp => new SqliteContactRepository(
            Path.Combine(settingsService.DataDirectory, "contacts.db"),
            chiaveArchivi,
            sp.GetRequiredService<ILogger<SqliteContactRepository>>()));

        // RIALLINEAMENTO DALLA RUBRICA DEL GESTIONALE.
        //
        // L'archivio locale resta quello sopra anche con il collegamento attivo: il
        // server e' la fonte, non il posto da cui si legge mentre il telefono squilla.
        services.AddSingleton(sp => new ContactsSyncService(
            sp.GetRequiredService<IContactRepository>(),
            settingsService.Secrets,
            () => settingsService.Current,
            () =>
            {
                var sessioni = sp.GetRequiredService<CallSessionManager>();
                return sessioni.ActiveCall is not null || sessioni.RingingCall is not null;
            },
            sp.GetRequiredService<ILogger<ContactsSyncService>>(),
            sp.GetRequiredService<ILogger<SqlServerContactSource>>()));

        // Archivio separato da quello della rubrica: il registro delle chiamate
        // contiene dati di traffico con un periodo di conservazione, la rubrica no.
        // Due file distinti significano che svuotare l'uno non tocca l'altro.
        services.AddSingleton<ICallHistoryRepository>(sp => new SqliteCallHistoryRepository(
            Path.Combine(settingsService.DataDirectory, "history.db"),
            chiaveArchivi,
            sp.GetRequiredService<ILogger<SqliteCallHistoryRepository>>()));

        // ------------------------------------------------------------------
        // Telefonia
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // Configurazione distribuita e blocco del pannello
        // ------------------------------------------------------------------

        services.AddSingleton(sp => new MachinePolicyStore(
            sp.GetRequiredService<ILogger<MachinePolicyStore>>()));

        services.AddSingleton(sp => new ConfigurationPolicyService(
            sp.GetRequiredService<ILogger<ConfigurationPolicyService>>()));

        services.AddSingleton(sp => new ConfigurationBundleService(
            settingsService,
            sp.GetRequiredService<HotkeyProfileStore>(),
            sp.GetRequiredService<ILogger<ConfigurationBundleService>>()));

        // ------------------------------------------------------------------
        // Fiducia sui certificati, condivisa fra telefono e centralino
        // ------------------------------------------------------------------

        services.AddSingleton<ICertificateTrustStore>(sp => new CertificateTrustStore(
            settingsService.DataDirectory,
            sp.GetRequiredService<ILogger<CertificateTrustStore>>()));

        // La richiesta di fiducia arriva su un thread qualsiasi e va portata
        // sull'interfaccia: l'annunciatore si risolve al momento dell'uso perche' il
        // servizio di fiducia nasce prima della pipeline di annuncio.
        services.AddSingleton<ICertificateTrustPrompt>(sp => new DispatcherCertificateTrustPrompt(
            () => sp.GetService<IAnnouncer>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<DispatcherCertificateTrustPrompt>>()));

        services.AddSingleton(sp => new CertificateTrustService(
            sp.GetRequiredService<ICertificateTrustStore>(),
            sp.GetRequiredService<ICertificateTrustPrompt>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<CertificateTrustService>>())
        {
            AcceptAnyCertificate = settings.Pbx.AcceptAnyCertificate,
        });

        // ------------------------------------------------------------------
        // Segnalazione all'assistenza
        // ------------------------------------------------------------------

        services.AddSingleton(sp => new CrashReporter(
            settingsService.DataDirectory,
            sp.GetRequiredService<ILogger<CrashReporter>>()));

        services.AddSingleton(sp => new SupportPackageService(
            settingsService,
            sp.GetRequiredService<FileLoggerProvider>(),
            sp.GetRequiredService<CrashReporter>(),
            sp.GetRequiredService<ILogger<SupportPackageService>>()));

        services.AddSingleton(sp => CreateTelephonyEngine(sp, settingsService, settings));
        services.AddSingleton<ITelephonyEngine>(sp => sp.GetRequiredService<SwitchableTelephonyEngine>());

        services.AddSingleton(sp => new SipAccountSwitcher(
            settingsService,
            sp.GetRequiredService<SwitchableTelephonyEngine>(),
            sp.GetRequiredService<CallSessionManager>(),
            sp.GetRequiredService<IAnnouncer>(),
            sp.GetRequiredService<ILogger<SipAccountSwitcher>>()));

        services.AddSingleton(new CallAnnouncementMapper(
            settings.Accessibility.DigitGrouping,
            settings.Accessibility.Verbosity));

        // I nomi dei tasti citati dagli annunci della conferenza vengono dal profilo
        // attivo, come quelli del trasferimento e per lo stesso motivo.
        services.AddSingleton(sp => BuildConferenceOptions(
            sp.GetRequiredService<HotkeyProfileStore>().Active));

        services.AddSingleton(sp => new CallSessionManager(
            sp.GetRequiredService<ITelephonyEngine>(),
            sp.GetRequiredService<IAnnouncer>(),
            sp.GetRequiredService<CallAnnouncementMapper>(),
            sp.GetRequiredService<AnnouncementContext>(),
            sp.GetRequiredService<IContactRepository>(),
            sp.GetRequiredService<AnnouncementOptions>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<CallSessionManager>>(),
            sp.GetRequiredService<IEarconPlayer>(),
            sp.GetRequiredService<ConferenceOptions>()));

        // Il nome del tasto di trasferimento che compare negli annunci viene dal
        // profilo attivo: se restasse fissato al predefinito, chi rimappa il tasto si
        // sentirebbe dire di premerne un altro.
        // Registra le chiamate concluse nell'elenco delle ultime chiamate.
        services.AddSingleton(sp => new CallHistoryService(
            sp.GetRequiredService<CallSessionManager>(),
            sp.GetRequiredService<ICallHistoryRepository>(),
            sp.GetRequiredService<ILogger<CallHistoryService>>()));

        services.AddSingleton(sp => BuildTransferOptions(
            settings, sp.GetRequiredService<HotkeyProfileStore>().Active));

        // Il sorvegliante del segnale di ascolto: senza di lui l'orchestratore
        // dichiara l'inclusione non disponibile, che e' il comportamento voluto.
        services.AddSingleton(sp => new IntrusionMonitor(
            sp.GetRequiredService<ITelephonyEngine>(),
            sp.GetRequiredService<CallSessionManager>(),
            sp.GetRequiredService<IAnnouncer>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<IntrusionMonitor>>()));

        services.AddSingleton(sp => new TransferOrchestrator(
            sp.GetRequiredService<ITelephonyEngine>(),
            sp.GetRequiredService<CallSessionManager>(),
            sp.GetRequiredService<IAnnouncer>(),
            sp.GetRequiredService<IContactRepository>(),
            sp.GetRequiredService<TransferOptions>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<TransferOrchestrator>>(),
            sp.GetRequiredService<PresenceMonitorService>(),
            sp.GetRequiredService<IntrusionMonitor>()));

        // ------------------------------------------------------------------
        // Centralino
        // ------------------------------------------------------------------

        services.AddSingleton(BuildYeastarOptions(settingsService, settings));

        services.AddSingleton(sp => new YeastarApiClient(
            sp.GetRequiredService<YeastarOptions>(),
            httpClient: null,
            sp.GetRequiredService<ILogger<YeastarApiClient>>(),
            sp.GetRequiredService<CertificateTrustService>()));

        services.AddSingleton<YeastarEventStream>();
        services.AddSingleton<YeastarQueueProvider>();
        services.AddSingleton<IQueueProvider>(sp => CreateQueueProvider(sp, settings));

        // Stato degli interni: libero, occupato, fuori servizio. Vive in memoria e
        // non nell'archivio, perche' una presenza conservata fra un avvio e l'altro
        // sarebbe una presenza vecchia — vedi PresenceMonitorService.
        services.AddSingleton(sp => new PresenceMonitorService(
            sp.GetRequiredService<IQueueProvider>(),
            sp.GetRequiredService<ITelephonyEngine>(),
            sp.GetRequiredService<ILogger<PresenceMonitorService>>()));

        // QUADRO DEI COLLEGHI IN TURNO.
        //
        // Prende lo stato dalla strada migliore che c'e': l'API del centralino o le
        // sottoscrizioni SIP quando esistono, altrimenti il sondaggio di
        // raggiungibilita', che e' l'unica strada che funzioni su un impianto che
        // rifiuta le sottoscrizioni. Vedi ShiftBoardService.
        //
        // Il sondaggio si prende dal motore telefonico al momento dell'uso e non qui:
        // prima dell'avvio del motore non esiste un trasporto SIP su cui chiedere, e
        // catturarlo adesso significherebbe catturare null per sempre.
        services.AddSingleton(sp => new ShiftBoardService(
            () => sp.GetRequiredService<ITelephonyEngine>().ReachabilityProbe,
            sp.GetRequiredService<PresenceMonitorService>(),
            sp.GetRequiredService<IContactRepository>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<ShiftBoardService>>()));

        // Orologio del turno e stato di pausa della postazione. Vive nel contenitore e
        // non nella finestra perche' e' stato di dominio, non di interfaccia: la
        // finestra lo mostra, ma chi decide se la postazione accetta chiamate e'
        // altrove.
        services.AddSingleton(sp => new DutyStatusService(sp.GetRequiredService<IClock>()));

        // Voce degli avvisi destinati a chi e' in linea. Distinta da quella degli
        // annunci: quella parla all'operatore e obbedisce alla sua configurazione,
        // questa parla all'interlocutore e non ha nulla da configurare.
        services.AddSingleton(sp => new RemoteVoice(sp.GetRequiredService<ILogger<RemoteVoice>>()));

        services.AddSingleton(sp => new CallRecordingController(
            sp.GetRequiredService<CallSessionManager>(),
            sp.GetRequiredService<ITelephonyEngine>(),
            sp.GetRequiredService<IAnnouncer>(),
            sp.GetRequiredService<RemoteVoice>(),
            settingsService,
            sp.GetRequiredService<ILogger<CallRecordingController>>()));

        services.AddSingleton(sp => new QueueMonitorService(
            sp.GetRequiredService<IQueueProvider>(),
            sp.GetRequiredService<IAnnouncer>(),
            sp.GetRequiredService<AnnouncementOptions>(),
            settings.Accessibility.DigitGrouping,
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<QueueMonitorService>>()));

        // ------------------------------------------------------------------
        // Scorciatoie e interfaccia
        // ------------------------------------------------------------------

        // Il deposito dei profili e' il proprietario della mappa dei tasti; il profilo
        // attivo che si registra qui e' l'istanza viva che tutti condividono, e che
        // cambia contenuto senza essere sostituita quando l'operatore salva una
        // configurazione diversa. Vedi HotkeyProfileStore.
        services.AddSingleton(sp => new HotkeyProfileStore(
            settingsService.DataDirectory,
            sp.GetRequiredService<ILogger<HotkeyProfileStore>>()));

        services.AddSingleton(sp => sp.GetRequiredService<HotkeyProfileStore>().Active);
        services.AddSingleton<HotkeyConflictDetector>();
        services.AddSingleton<GlobalHotkeyRegistrar>();

        services.AddSingleton<MainWindow>();

        // Transitorie: il pannello impostazioni rilegge la configurazione a ogni
        // apertura, cosi' una modifica fatta a mano nel file JSON si vede senza
        // dover riavviare l'applicazione; la rubrica riparte pulita a ogni apertura
        // invece di conservare la ricerca precedente, che a distanza di ore sarebbe
        // solo un risultato vecchio da cancellare.
        services.AddTransient<SettingsWindow>();
        services.AddTransient<ContactsWindow>();
        services.AddTransient<CallHistoryWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Sceglie il motore telefonico: quello reale se l'account SIP è configurato,
    /// altrimenti quello simulato.
    /// </summary>
    /// <remarks>
    /// Il ripiego sul motore simulato non è una scorciatoia di sviluppo: consente di
    /// avviare l'applicazione su una postazione non ancora configurata e di
    /// esercitare tutte le scorciatoie, che è esattamente ciò che serve nella
    /// formazione di un operatore appena assunto. Lo stato viene comunque annunciato,
    /// perché nessuno deve credere di essere in servizio quando non lo è.
    /// </remarks>
    /// <summary>
    /// Costruisce il motore telefonico commutabile, con l'account predefinito.
    /// </summary>
    /// <remarks>
    /// Restituisce sempre l'involucro commutabile e non il motore concreto: è quello
    /// che permette di cambiare account senza riavviare, tenendo ferma la superficie
    /// su cui mezzo prodotto si è agganciato. Vedi
    /// <see cref="SwitchableTelephonyEngine"/>.
    /// </remarks>
    private static SwitchableTelephonyEngine CreateTelephonyEngine(
        IServiceProvider sp,
        SettingsService settingsService,
        AppSettings settings)
    {
        var iniziale = settings.ActiveSipAccount;

        return new SwitchableTelephonyEngine(
            account => BuildEngine(sp, settingsService, settings, account),
            iniziale.IsUsable ? BuildAccountOptions(iniziale, settingsService) : null,
            sp.GetRequiredService<ILogger<SwitchableTelephonyEngine>>());
    }

    /// <summary>Costruisce il motore concreto per un account, o quello simulato.</summary>
    private static ITelephonyEngine BuildEngine(
        IServiceProvider sp,
        SettingsService settingsService,
        AppSettings settings,
        SipAccountOptions? account)
    {
        if (account is null)
        {
            sp.GetRequiredService<ILogger<App>>()
                .LogWarning("Account SIP non configurato: avvio in modalita' simulata");

            return new FakeTelephonyEngine(sp.GetRequiredService<IClock>());
        }

        // Il motore audio va passato: senza, la chiamata si instaura ma non passa
        // audio in nessuna direzione, e il centralino la chiude dopo pochi secondi
        // per assenza di flusso RTP.
        return new SipTelephonyEngine(
            account,
            sp.GetRequiredService<NAudioEngine>(),
            BuildAudioTuning(settingsService.Current),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<SipTelephonyEngine>>(),
            sp.GetRequiredService<ILogger<SipAudioEndPoint>>(),
            sp.GetRequiredService<ILogger<SipPresenceSource>>(),
            sp.GetRequiredService<ILogger<SipOptionsProbe>>(),

            // La politica di fiducia sui certificati e' la stessa del connettore
            // dell'API: un certificato gia' approvato per il centralino non va
            // riapprovato quando la stessa macchina si presenta sulla porta SIP.
            sp.GetRequiredService<CertificateTrustService>());
    }

    /// <summary>
    /// Traduce un account configurato nei parametri del motore telefonico.
    /// </summary>
    /// <param name="sip">Account come sta nella configurazione.</param>
    /// <param name="settingsService">Servizio di configurazione, per il deposito dei segreti.</param>
    /// <remarks>
    /// La password si legge con la chiave dell'account e non con quella storica: è
    /// ciò che permette a più account di coesistere senza sovrascriversi la
    /// credenziale. Vedi <see cref="SipSettings.PasswordKey"/>.
    /// </remarks>
    internal static SipAccountOptions BuildAccountOptions(SipSettings sip, SettingsService settingsService) => new()
    {
        Name = sip.Name,
        DisplayName = sip.DisplayName,
        OutboundCallerId = sip.OutboundCallerId,
        Srtp = Enum.TryParse<Core.Domain.SrtpPolicy>(sip.Srtp, out var srtp)
            ? srtp
            : Core.Domain.SrtpPolicy.Disabled,
        Username = sip.Username,
        AuthId = sip.AuthId,
        Password = settingsService.Secrets.Get(sip.PasswordKey),
        Domain = sip.Domain,
        Registrar = sip.Registrar,
        OutboundProxy = sip.OutboundProxy,
        Transport = Enum.TryParse<SipTransportKind>(sip.Transport, out var transport)
            ? transport
            : SipTransportKind.Udp,
        LocalPort = sip.LocalPort,
        RegistrationExpiry = sip.RegistrationExpiry,
        CodecPreference = sip.CodecPreference,
    };

    /// <summary>
    /// Sceglie il connettore di coda: quello Yeastar se il collegamento è attivo e
    /// configurato, altrimenti quello inerte.
    /// </summary>
    /// <remarks>
    /// Il connettore inerte non è un ripiego di emergenza ma una configurazione
    /// legittima: su un centralino che non espone l'API non c'è nulla da collegare,
    /// e insistere produce soltanto un avviso di guasto a ogni interrogazione della
    /// coda. Il posto operatore resta pienamente utilizzabile per chiamare,
    /// rispondere, trasferire e consultare la rubrica.
    /// </remarks>
    private static IQueueProvider CreateQueueProvider(IServiceProvider sp, AppSettings settings)
    {
        var pbx = settings.Pbx;
        var configured = !string.IsNullOrWhiteSpace(pbx.Host) && !string.IsNullOrWhiteSpace(pbx.ClientId);

        if (!pbx.Enabled || !configured)
        {
            sp.GetRequiredService<ILogger<App>>().LogInformation(
                pbx.Enabled
                    ? "Centralino non configurato: le chiamate in coda non saranno visibili"
                    : "Collegamento al centralino disattivato nelle impostazioni");

            return new DisabledQueueProvider(sp.GetRequiredService<IClock>());
        }

        return sp.GetRequiredService<YeastarQueueProvider>();
    }

    /// <summary>
    /// Applica la suoneria scelta dall'operatore, se il file esiste ancora.
    /// </summary>
    /// <remarks>
    /// Un percorso non più valido non deve lasciare muta la suoneria: si ricade sul
    /// segnale sintetizzato e lo si registra. Su un posto operatore una chiamata che
    /// entra senza farsi sentire è il guasto peggiore possibile.
    /// </remarks>
    private static void ApplyRingtone(ToneEarconPlayer player, AppSettings settings, ILogger logger)
    {
        var path = settings.RingtonePath;

        if (string.IsNullOrWhiteSpace(path)) return;

        if (!File.Exists(path))
        {
            logger.LogWarning(
                "Suoneria personalizzata non trovata in {Path}: si usa il segnale predefinito", path);
            return;
        }

        player.CustomSounds[EarconKind.IncomingCall] = path;
        logger.LogInformation("Suoneria personalizzata attiva: {Path}", path);
    }

    /// <summary>Traduce le impostazioni di regolazione audio nei parametri del motore.</summary>
    private static AudioTuningOptions BuildAudioTuning(AppSettings settings) => new()
    {
        JitterBufferMilliseconds = settings.Audio.JitterBufferMs,
        MaxBufferMilliseconds = settings.Audio.MaxBufferMs,
        DeviceLatencyMilliseconds = settings.Audio.DeviceLatencyMs,
        PacketMilliseconds = settings.Audio.PacketMs,
    };

    private static AnnouncementOptions BuildAnnouncementOptions(AppSettings settings) => new()
    {
        Verbosity = settings.Accessibility.Verbosity,
        QueueMode = settings.Accessibility.QueueMode,
        SilenceDuringConversation = settings.Accessibility.SilenceDuringConversation,
        QueueWaitThreshold = TimeSpan.FromSeconds(settings.Accessibility.QueueWaitThresholdSeconds),
        HoldReminderInterval = TimeSpan.FromSeconds(settings.Accessibility.HoldReminderSeconds),
    };

    private static TransferOptions BuildTransferOptions(AppSettings settings, HotkeyProfile hotkeys) => new()
    {
        Mode = Enum.TryParse<TransferInteractionMode>(settings.Calls.TransferMode, out var mode)
            ? mode
            : TransferInteractionMode.ProgressiveDoubleTap,
        DoubleTapWindow = TimeSpan.FromMilliseconds(settings.Accessibility.DoubleTapWindowMs),
        ConsultationTimeout = TimeSpan.FromSeconds(settings.Calls.ConsultationTimeoutSeconds),
        ConfirmExternalDestinations = settings.Calls.ConfirmExternalTransfers,
        WarnOnUnavailableDestination = settings.Calls.WarnOnUnavailableDestination,
        MaxInternalNumberLength = settings.Calls.MaxInternalNumberLength,
        DigitGrouping = settings.Accessibility.DigitGrouping,
        // In modalita' semplificata gli annunci nominano Invio, che e' il tasto che
        // quella modalita' insegna: vedi SimplifiedKeypad.TransferKeyName.
        TransferKeyName = SimplifiedKeypad.TransferKeyName(
            settings.Interaction == Core.Domain.InteractionMode.Simplified,
            hotkeys.Get(Core.Domain.HotkeyCommand.Transfer, Core.Domain.HotkeyScope.Local)?.SpeechText ?? "F9"),
    };

    private static ConferenceOptions BuildConferenceOptions(HotkeyProfile hotkeys) => new()
    {
        ConferenceKeyName = KeyName(hotkeys, Core.Domain.HotkeyCommand.Conference, "F7"),
        HoldKeyName = KeyName(hotkeys, Core.Domain.HotkeyCommand.ToggleHold, "F4"),
        DialerKeyName = KeyName(hotkeys, Core.Domain.HotkeyCommand.FocusDialer, "F8"),
    };

    /// <summary>Nome pronunciabile della combinazione locale assegnata a un comando.</summary>
    private static string KeyName(HotkeyProfile hotkeys, Core.Domain.HotkeyCommand command, string fallback)
        => hotkeys.Get(command, Core.Domain.HotkeyScope.Local)?.SpeechText ?? fallback;

    private static YeastarOptions BuildYeastarOptions(SettingsService settingsService, AppSettings settings) => new()
    {
        Host = settings.Pbx.Host,
        Port = settings.Pbx.Port,
        UseTls = settings.Pbx.UseTls,
        ClientId = settings.Pbx.ClientId,
        ClientSecret = settingsService.Secrets.Get(SecretStore.PbxClientSecretKey),
        Extension = settings.Sip.Username,
        QueueIds = settings.Pbx.QueueIds,
        WaitingListIsOrdered = settings.Pbx.WaitingListIsOrdered,
        EnableExperimentalQueuePick = settings.Pbx.EnableExperimentalQueuePick,
    };

    /// <summary>
    /// Avvia il ciclo che fa avanzare i servizi con comportamento temporizzato.
    /// </summary>
    /// <remarks>
    /// L'intervallo di 100 millisecondi è un compromesso misurato: più lungo si
    /// somma alla latenza percepita degli annunci, che ha un budget di 150
    /// millisecondi dalla pressione del tasto; più corto non serve, perché le
    /// finestre di accorpamento sono nell'ordine delle centinaia di millisecondi.
    /// </remarks>
    private void StartPumpLoop()
    {
        var announcements = Services.GetRequiredService<AnnouncementService>();
        var calls = Services.GetRequiredService<CallSessionManager>();
        var transfer = Services.GetRequiredService<TransferOrchestrator>();
        var intrusion = Services.GetRequiredService<IntrusionMonitor>();
        var queue = Services.GetRequiredService<QueueMonitorService>();
        var shift = Services.GetRequiredService<ShiftBoardService>();
        var telephony = Services.GetRequiredService<ITelephonyEngine>();
        var logSwitch = Services.GetRequiredService<LogLevelSwitch>();

        var lastCounterDump = DateTimeOffset.MinValue;

        _pumpTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };

        _pumpTimer.Tick += (_, _) =>
        {
            // Un tick puo' essere gia' in coda quando il timer viene fermato: senza
            // questo controllo girerebbe su servizi appena smaltiti.
            if (_shuttingDown) return;

            var now = DateTimeOffset.Now;

            // L'ordine conta: il trasferimento puo' generare annunci che devono
            // essere consegnati nello stesso ciclo, non nel successivo.
            transfer.Pump(now);
            calls.Pump(now);

            // Il segnale che dichiara l'ascolto durante un'inclusione. Sta nello
            // stesso ciclo di tutto cio' che nel prodotto si ripete: una sorgente di
            // tempo sola, e nessun timer che possa restare acceso per conto suo.
            intrusion.Pump(now);
            queue.Pump(now);

            // Il giro di verifica sui colleghi in turno passa dallo stesso ciclo di
            // tutto il resto: una sorgente di tempo sola, e nessun timer che possa
            // restare acceso per conto suo. Decide da se' quando e' ora.
            shift.Pump(now);
            announcements.Pump(now);

            DumpCountersIfDebugging(now);
        };

        void DumpCountersIfDebugging(DateTimeOffset now)
        {
            // Con il registro in modalita' dettagliata, i contatori del percorso audio
            // vengono annotati ogni dieci secondi mentre una chiamata e' in corso.
            //
            // Serve a vedere l'ANDAMENTO, che il riepilogo di fine chiamata nasconde:
            // un buffer che si degrada dal quarto minuto e un buffer disturbato fin
            // dall'inizio danno gli stessi totali finali e sono due guasti diversi. La
            // condizione guarda il livello del registro e non l'impostazione salvata,
            // cosi' segue l'interruttore che il pannello di diagnostica muove a caldo.
            if (logSwitch.Minimum > LogLevel.Debug) return;

            // La conferenza va inclusa, ed e' anzi il caso in cui i contatori servono
            // di piu': sono i soli che dicono se il missaggio sta davvero partendo o
            // se un partecipante sta sentendo solo l'operatore.
            if (calls.ActiveCall is null && !calls.IsConferenceActive) return;

            if (now - lastCounterDump < TimeSpan.FromSeconds(10)) return;

            lastCounterDump = now;

            _logger?.LogDebug(
                "Contatori del percorso audio: {Counters}",
                string.Join(" | ", telephony.DiagnosticCounters.Select(c => $"{c.Label} {c.Value}")));
        }

        _pumpTimer.Start();
    }

    /// <summary>
    /// Raccoglie le eccezioni non gestite dell'interfaccia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Questo metodo non può sollevare eccezioni.</b> Un errore sollevato qui
    /// dentro non ha più nessuno che lo raccolga e termina il processo, con l'effetto
    /// paradossale che il gestore degli errori diventa la causa del guasto peggiore
    /// possibile su un posto operatore: la chiusura improvvisa con le chiamate in
    /// corso.
    /// </para>
    /// <para>
    /// È successo davvero. Il metodo risolveva il registro e l'annunciatore dal
    /// contenitore di dipendenze, che durante la chiusura è già smontato: la
    /// risoluzione sollevava <c>ObjectDisposedException</c>, il processo terminava e
    /// Windows apriva una segnalazione di errore. Ora i due riferimenti sono
    /// catturati all'avvio e tutto il corpo è protetto.
    /// </para>
    /// </remarks>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Prima cosa, prima di qualunque altra: l'errore e' assorbito comunque vada.
        e.Handled = true;

        try
        {
            if (_shuttingDown)
            {
                // Un callback in ritardo che trova oggetti gia' smaltiti mentre il
                // programma sta chiudendo non e' un guasto, e annunciarlo a chi ha
                // appena premuto "chiudi" e' solo allarmismo.
                _logger?.LogDebug(e.Exception, "Errore durante la chiusura, ignorato");
                return;
            }

            _logger?.LogError(e.Exception, "Errore non gestito nell'interfaccia");

            var registrato = Record(e.Exception, "interfaccia", fatal: false);

            // UN ERRORE RIPETUTO SI ANNUNCIA UNA VOLTA SOLA.
            //
            // Un'eccezione dentro il calcolo del layout non capita una volta: capita a
            // ogni tentativo di ridisegno, e il ridisegno riparte subito. Sul difetto
            // dell'elenco degli account SIP il registro ne ha contate milleduecentoventi
            // in pochi secondi, e ciascuna faceva partire un annuncio con il suo segnale
            // acustico: quello che l'operatore sentiva era una sirena, e sotto quella
            // sirena la frase che spiegava il guasto era inascoltabile.
            //
            // La chiave di accorpamento fa collassare le ripetizioni ravvicinate in un
            // annuncio solo. Non nasconde nulla: nel registro restano tutte.
            if (!ShouldAnnounceFailure()) return;

            // L'annuncio e' l'unico modo in cui un operatore non vedente puo' sapere
            // che qualcosa e' andato storto: una finestra di errore che non ruba il
            // focus resterebbe invisibile.
            //
            // Quando l'errore e' stato registrato lo si dice, e si dice come mandarlo:
            // e' la differenza fra una segnalazione che parte oggi e una che parte fra
            // tre giorni, quando del guasto non e' rimasto niente da guardare.
            _announcer?.Announce(Announcement.Critical(
                registrato
                    ? "Si e' verificato un errore. E' stato registrato: usa il comando di segnalazione "
                      + "all'assistenza per inviarlo. La chiamata in corso potrebbe non essere piu' "
                      + "gestita correttamente"
                    : "Si e' verificato un errore. La chiamata in corso potrebbe non essere piu' gestita correttamente",
                DateTimeOffset.Now,
                EarconKind.CommandRejected,
                coalesceKey: "errore:interfaccia"));
        }
        catch (Exception failure)
        {
            // Ultima rete. Si perde l'avviso, non il posto operatore.
            System.Diagnostics.Debug.WriteLine($"Gestore degli errori non riuscito: {failure}");
        }
    }

    /// <summary>
    /// Raccoglie un errore che ha già terminato, o sta per terminare, il processo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Qui non si può assorbire nulla e non si può parlare: il tempo che resta prima
    /// che il processo muoia basta per scrivere un file e non per far pronunciare una
    /// frase. È esattamente il caso in cui la raccolta automatica vale di più — di
    /// questo guasto, senza, non resterebbe alcuna traccia.
    /// </para>
    /// </remarks>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is not Exception exception) return;

            _logger?.LogCritical(exception, "Errore non gestito nel processo");
            Record(exception, "processo", e.IsTerminating);
        }
        catch (Exception failure)
        {
            System.Diagnostics.Debug.WriteLine($"Registrazione dell'errore di processo non riuscita: {failure}");
        }
    }

    /// <summary>
    /// Raccoglie l'errore di un'attività di sfondo che nessuno ha atteso.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Su questo prodotto è una categoria concreta e non teorica: la riconnessione al
    /// centralino, il rinnovo dei token, il flusso eventi e le operazioni audio girano
    /// tutte su attività lanciate e non attese. Un errore lì non si vede da nessuna
    /// parte — l'unico sintomo è una funzione che smette di aggiornarsi.
    /// </para>
    /// <para>
    /// Si marca come osservato: senza, in alcune configurazioni l'errore verrebbe
    /// risollevato dal raccoglitore di memoria e chiuderebbe il processo. Un guasto
    /// nel collegamento al centralino non deve far cadere le chiamate in corso.
    /// </para>
    /// </remarks>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            e.SetObserved();

            if (_shuttingDown) return;

            _logger?.LogError(e.Exception, "Errore in un'attivita' di sfondo");
            Record(e.Exception, "attivita' di sfondo", fatal: false);
        }
        catch (Exception failure)
        {
            System.Diagnostics.Debug.WriteLine($"Registrazione dell'errore di sfondo non riuscita: {failure}");
        }
    }

    /// <summary>Istante dell'ultimo annuncio di errore pronunciato.</summary>
    private DateTimeOffset _lastFailureAnnouncedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Decide se questo errore merita un annuncio, o se è la replica di uno appena
    /// detto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cinque secondi. La finestra è larga di proposito: un errore che si ripete a
    /// ogni ridisegno ne produce centinaia al secondo, e l'accorpamento della pipeline
    /// di annuncio — pensato per raggruppare eventi vicini, non per arginare una
    /// valanga — non basta da solo a tenere la voce utilizzabile.
    /// </para>
    /// <para>
    /// Il freno vale solo sulla <b>voce</b>: registro e rapporti di errore continuano a
    /// raccogliere tutto, perché è li' che si va a guardare dopo.
    /// </para>
    /// </remarks>
    private bool ShouldAnnounceFailure()
    {
        var now = DateTimeOffset.Now;
        if (now - _lastFailureAnnouncedAt < TimeSpan.FromSeconds(5)) return false;

        _lastFailureAnnouncedAt = now;
        return true;
    }

    /// <summary>
    /// Scrive il rapporto, se la raccolta è attiva e il raccoglitore è ancora vivo.
    /// </summary>
    /// <returns><c>true</c> se il rapporto è stato scritto.</returns>
    private bool Record(Exception exception, string origin, bool fatal)
    {
        var reporter = _crashes;
        if (reporter is null || !_collectCrashReports) return false;

        return reporter.Record(exception, origin, fatal) is not null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// L'ordine conta: prima si smette di generare lavoro, poi si smonta. Invertirlo
    /// lascia il ciclo di pump a girare su servizi già smaltiti, che è il modo in cui
    /// nascono le eccezioni in chiusura.
    /// </remarks>
    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;

        _pumpTimer?.Stop();
        _pumpTimer = null;

        // Va scritta prima dello smontaggio: dopo, il registro non esiste piu'. Una
        // chiusura registrata distingue "chiuso dall'operatore" da "terminato di
        // colpo", che e' la prima domanda davanti a una segnalazione.
        _logger?.LogInformation("BlindPhone chiuso");

        try
        {
            // SMONTAGGIO ASINCRONO, E NON E' UN DETTAGLIO DI STILE.
            //
            // Diversi servizi registrati qui — la sorveglianza della coda, il gestore
            // delle chiamate, il motore telefonico — implementano IAsyncDisposable e
            // non IDisposable. Il contenitore, chiamato con Dispose(), rifiuta di
            // smontarli e solleva:
            //
            //   'QueueMonitorService' type only implements IAsyncDisposable.
            //   Use DisposeAsync to dispose the container.
            //
            // L'eccezione veniva raccolta e annotata come "smontaggio non riuscito", e
            // il risultato era che ALLA CHIUSURA NON VENIVA SMONTATO NULLA: la
            // registrazione SIP restava aperta presso il centralino finche' non scadeva
            // da sola, e le risorse audio finivano al raccoglitore di memoria.
            //
            // Si attende davvero: la chiusura di un posto operatore deve arrivare in
            // fondo, e i pochi millisecondi che costa sono meno del tempo che il
            // centralino impiegherebbe ad accorgersi dell'interno sparito.
            if (_services is not null) _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Smontaggio dei servizi non riuscito");
        }

        // La sottoscrizione agli avvisi di Windows e' statica e sopravvivrebbe al
        // processo che l'ha registrata: lasciarla appesa trattiene in vita l'oggetto e,
        // su un riavvio a caldo, fa arrivare l'avviso a un servizio gia' smontato.
        _theme?.Dispose();
        _theme = null;

        // Il posto viene liberato per ultimo: finche' il mutex e' tenuto, un secondo
        // avvio si limiterebbe a segnalare a un processo che sta gia' morendo.
        _singleInstance?.Dispose();
        _singleInstance = null;

        // Dopo lo smontaggio anche il registro appartiene a un contenitore morto:
        // si lasciano andare i riferimenti invece di rischiare di usarli.
        _services = null;
        _logger = null;
        _announcer = null;

        base.OnExit(e);
    }
}
