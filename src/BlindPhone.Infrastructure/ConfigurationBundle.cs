using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Pacchetto di configurazione: quello che un amministratore prepara una volta e
/// applica su tutte le postazioni.
/// </summary>
/// <remarks>
/// <para>
/// <b>Che cosa risolve.</b> Configurare una postazione a mano significa percorrere
/// sette schede, una cinquantina di campi e un profilo di tasti rapidi. Su venti
/// postazioni è una giornata, e su venti postazioni fatte a mano almeno una avrà un
/// parametro diverso dalle altre — di solito quello che poi produce la segnalazione
/// che nessuno riesce a riprodurre altrove.
/// </para>
/// <para>
/// <b>Che cosa NON contiene, e non è una limitazione.</b> Nessuna password, nessun
/// Client Secret, nessuna chiave degli archivi. I segreti vivono sotto DPAPI, che è
/// legato all'utente e alla macchina: portarli in un file li renderebbe leggibili
/// altrove, ed è esattamente ciò da cui DPAPI protegge. Restano da inserire su ogni
/// postazione, ed è l'unico passaggio manuale che questo pacchetto non toglie.
/// </para>
/// <para>
/// <b>Le sezioni si scelgono.</b> È la parte che rende il pacchetto usabile davvero:
/// l'ufficio IT esporta audio, accessibilità, tasti rapidi e criterio di blocco, e
/// lascia fuori l'account SIP — che è l'unica cosa diversa fra una postazione e
/// l'altra. Il pacchetto si applica allora anche a postazioni già configurate, senza
/// cancellare il loro interno.
/// </para>
/// </remarks>
public sealed record ConfigurationBundle
{
    /// <summary>Marcatore del formato, per riconoscere il file.</summary>
    public const string FormatMarker = "BlindPhone.Configurazione";

    /// <summary>Estensione dei pacchetti di configurazione.</summary>
    public const string FileExtension = ".bpcfg";

    /// <summary>Nome con cui il pacchetto viene cercato al primo avvio.</summary>
    public const string ProvisioningFileName = "configurazione-predefinita" + FileExtension;

    /// <summary>Marcatore del formato.</summary>
    /// <remarks>
    /// <b>Nasce vuoto e non con il marcatore.</b> Sembrerebbe comodo il contrario, ed
    /// è la comodità che rompe il controllo: con un valore predefinito, un JSON
    /// qualunque deserializzato in questo tipo eredita il marcatore e risulta un
    /// pacchetto valido. Il valore lo scrive <see cref="ConfigurationBundleService"/>
    /// al momento di comporre, così l'unico modo di averlo è essere stati scritti da
    /// lui.
    /// </remarks>
    public string Formato { get; init; } = string.Empty;

    /// <summary>Versione del formato del pacchetto.</summary>
    public int Versione { get; init; } = 1;

    /// <summary>
    /// Revisione della configurazione, decisa dall'amministratore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È il numero che permette di aggiornare una configurazione già distribuita. Le
    /// postazioni ricordano l'ultima revisione applicata: un pacchetto con revisione
    /// più alta viene applicato da solo al primo avvio, uno con revisione uguale o
    /// più bassa viene ignorato.
    /// </para>
    /// <para>
    /// Senza, l'unica alternativa sarebbe riapplicare il pacchetto a ogni avvio —
    /// che cancellerebbe ogni regolazione fatta dall'operatore nel frattempo, comprese
    /// quelle nelle sezioni che l'amministratore aveva deliberatamente lasciato
    /// libere.
    /// </para>
    /// </remarks>
    public int Revisione { get; init; } = 1;

    /// <summary>Chi lo ha preparato, e quando.</summary>
    public string? Descrizione { get; init; }

    /// <summary>Istante di creazione.</summary>
    public DateTimeOffset CreatoIl { get; init; } = DateTimeOffset.Now;

    /// <summary>Postazione su cui è stato preparato, per la tracciabilità.</summary>
    public string? CreatoSu { get; init; }

    /// <summary>Sezioni contenute nel pacchetto.</summary>
    /// <remarks>
    /// Solo queste vengono sovrascritte all'applicazione: le altre restano come sono
    /// sulla postazione di destinazione.
    /// </remarks>
    public IReadOnlyList<SettingsSection> Sezioni { get; init; } = [];

    /// <summary>Configurazione da cui prendere le sezioni incluse.</summary>
    public AppSettings? Impostazioni { get; init; }

    /// <summary>Profili di tasti rapidi, in forma serializzata.</summary>
    /// <remarks>
    /// Si trasporta il testo già serializzato invece degli oggetti: il formato dei
    /// profili ha un serializzatore suo, con la sua tolleranza alle voci non
    /// riconosciute, e farlo passare da qui significherebbe mantenerne due.
    /// </remarks>
    public string? ProfiliTasti { get; init; }

    /// <summary>Nome del profilo da rendere attivo.</summary>
    public string? ProfiloTastiAttivo { get; init; }

    /// <summary>Criterio di blocco da applicare.</summary>
    public ConfigurationPolicy? Criterio { get; init; }

    /// <summary>Descrizione pronunciabile del contenuto.</summary>
    public string Describe()
    {
        var parti = new List<string>();

        if (Sezioni.Count > 0)
        {
            parti.Add("sezioni: " + string.Join(", ", Sezioni.Select(ConfigurationPolicy.DescribeSection)));
        }

        if (!string.IsNullOrWhiteSpace(ProfiliTasti)) parti.Add("profili dei tasti rapidi");
        if (Criterio is { IsRestrictive: true }) parti.Add("criterio di blocco delle impostazioni");

        if (parti.Count == 0) return "Il pacchetto non contiene nulla da applicare.";

        var testo = $"Revisione {Revisione}, del {CreatoIl:d MMMM yyyy}. Contiene {string.Join("; ", parti)}.";

        if (!string.IsNullOrWhiteSpace(Descrizione)) testo += $" {Descrizione.Trim()}";

        return testo;
    }
}

/// <summary>Esito dell'applicazione di un pacchetto.</summary>
/// <param name="Success">Se il pacchetto è stato applicato.</param>
/// <param name="Message">Messaggio in linguaggio naturale, già pronunciabile.</param>
/// <param name="Settings">Configurazione risultante, quando l'applicazione è riuscita.</param>
public sealed record BundleResult(bool Success, string Message, AppSettings? Settings = null);

/// <summary>
/// Costruisce, legge e applica i pacchetti di configurazione.
/// </summary>
public sealed class ConfigurationBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SettingsService _settings;
    private readonly HotkeyProfileStore _hotkeys;
    private readonly ILogger<ConfigurationBundleService> _log;
    private readonly IReadOnlyList<string> _provisioningFolders;

    /// <summary>Crea il servizio.</summary>
    /// <param name="settings">Configurazione dell'applicazione.</param>
    /// <param name="hotkeys">Deposito dei profili di tasti rapidi.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="provisioningFolders">
    /// Cartelle in cui cercare il pacchetto da applicare all'avvio. Omesse si usano
    /// quelle di <see cref="ProvisioningFolders"/>.
    /// </param>
    /// <remarks>
    /// Le cartelle si possono indicare perché la verifica dei permessi che protegge
    /// il provisioning va collaudata, e collaudarla su <c>%ProgramData%</c> vorrebbe
    /// dire cambiare i permessi di una cartella di sistema sulla macchina di chi
    /// esegue i test.
    /// </remarks>
    public ConfigurationBundleService(
        SettingsService settings,
        HotkeyProfileStore hotkeys,
        ILogger<ConfigurationBundleService>? logger = null,
        IReadOnlyList<string>? provisioningFolders = null)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _log = logger ?? NullLogger<ConfigurationBundleService>.Instance;
        _provisioningFolders = provisioningFolders ?? ProvisioningFolders;
    }

    /// <summary>Tutte le sezioni, per chi vuole esportare la configurazione intera.</summary>
    public static IReadOnlyList<SettingsSection> AllSections { get; } =
        [.. Enum.GetValues<SettingsSection>()];

    /// <summary>
    /// Cartelle in cui viene cercato il pacchetto da applicare all'avvio, in ordine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La prima è quella giusta per una distribuzione: <c>%ProgramData%\BlindPhone</c>
    /// è scrivibile dall'installatore, leggibile da tutti gli utenti della postazione
    /// e sopravvive agli aggiornamenti. La seconda, accanto all'eseguibile, serve a
    /// chi prepara una chiavetta o una cartella di rete da cui installare: pacchetto e
    /// programma viaggiano insieme.
    /// </para>
    /// <para>
    /// L'ordine conta: un pacchetto messo in <c>ProgramData</c> da un criterio di
    /// gruppo deve vincere su quello rimasto accanto all'eseguibile dall'installazione
    /// di sei mesi prima.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ProvisioningFolders { get; } =
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BlindPhone"),
        AppContext.BaseDirectory,
    ];

    /// <summary>
    /// Cerca un pacchetto da applicare e lo applica se è più recente di quello già
    /// applicato.
    /// </summary>
    /// <returns>
    /// L'esito, oppure <c>null</c> se non c'era nulla da fare — che è il caso
    /// normale su una postazione già configurata.
    /// </returns>
    /// <remarks>
    /// <para>
    /// È la funzione che rende il pacchetto utile su venti postazioni invece che su
    /// una: si installa il programma, si lascia il file in <c>%ProgramData%\BlindPhone</c>
    /// e al primo avvio la postazione è configurata. Restano da inserire le sole
    /// password, che non viaggiano mai in un file.
    /// </para>
    /// <para>
    /// Il confronto è sulla <b>revisione</b> e non sulla data del file: copiare un
    /// pacchetto ne cambia la data di modifica, e una postazione riconfigurata a ogni
    /// avvio perderebbe le regolazioni fatte dall'operatore nelle sezioni che
    /// l'amministratore aveva lasciato libere.
    /// </para>
    /// <para>
    /// <b>Un pacchetto che non arriva da un amministratore non viene applicato.</b> Un
    /// pacchetto distribuito vale per tutti i profili della postazione: sposta
    /// l'indirizzo del centralino, cambia il registrar SIP, dirotta l'invio dei
    /// pacchetti di assistenza e può installare un criterio con password che
    /// impedisce all'operatore di rimettere le cose a posto. Con i permessi
    /// predefiniti di <c>%ProgramData%</c> un utente standard può creare quella
    /// sottocartella e scriverci dentro, quindi la sola posizione del file non
    /// dimostra niente: si guarda chi può modificarlo. Vedi
    /// <see cref="ProtectedLocation"/>.
    /// </para>
    /// <para>
    /// Un pacchetto rifiutato <b>non interrompe la ricerca</b>: si passa alla cartella
    /// successiva. Fermarsi lì permetterebbe di mettere fuori gioco una
    /// configurazione legittima, accanto all'eseguibile, lasciando un file qualunque
    /// in una cartella non protetta.
    /// </para>
    /// </remarks>
    public BundleResult? ApplyProvisioningIfNewer()
    {
        var applicata = _settings.Current.AppliedConfigurationRevision;

        // Il primo rifiuto si conserva: se non si trova nessun pacchetto valido,
        // l'operatore deve comunque sentire che ce n'era uno e perche' e' stato
        // ignorato. Un pacchetto scartato in silenzio e' indistinguibile da un
        // pacchetto mai arrivato, e manderebbe chi amministra a cercare il guasto
        // sul server di distribuzione.
        BundleResult? rifiuto = null;

        foreach (var cartella in _provisioningFolders)
        {
            var percorso = Path.Combine(cartella, ConfigurationBundle.ProvisioningFileName);

            if (!File.Exists(percorso)) continue;

            // LA VERIFICA DEI PERMESSI VIENE PRIMA DELLA LETTURA.
            //
            // Non per ordine formale: leggere e interpretare un JSON scritto da chi
            // non doveva poterlo scrivere e' comunque dare in pasto al programma
            // dati di origine non verificata, e qui c'e' tutto il tempo di non
            // farlo.
            if (!ProtectedLocation.IsAdminOnly(percorso, out var motivo))
            {
                _log.LogError(
                    "Pacchetto di configurazione in {Path} ignorato: {Motivo}. "
                    + "Un pacchetto distribuito deve stare in una cartella modificabile solo dagli "
                    + "amministratori, e appartenere a loro. Come sistemarla: {Rimedio}",
                    percorso, motivo, DescribeFix(cartella));

                rifiuto ??= new BundleResult(
                    false,
                    "Configurazione distribuita ignorata: la cartella che la contiene non e' protetta, "
                    + "quindi il pacchetto potrebbe non venire dall'amministratore. Avvisa chi gestisce la postazione");

                continue;
            }

            var bundle = Read(percorso);

            if (bundle is null) continue;

            if (bundle.Revisione <= applicata)
            {
                _log.LogInformation(
                    "Pacchetto di configurazione in {Path} gia' applicato (revisione {Revisione})",
                    percorso, bundle.Revisione);

                return null;
            }

            _log.LogInformation(
                "Pacchetto di configurazione trovato in {Path}: revisione {Nuova}, applicata {Applicata}",
                percorso, bundle.Revisione, applicata);

            return Apply(bundle);
        }

        return rifiuto;
    }

    /// <summary>
    /// I due comandi che rimettono a posto i permessi di una cartella di
    /// distribuzione.
    /// </summary>
    /// <param name="folder">Cartella da sistemare.</param>
    /// <remarks>
    /// <para>
    /// Stanno nel registro, per esteso e pronti da incollare, perché chi li leggerà è
    /// un amministratore che sta cercando di capire perché una configurazione
    /// distribuita non viene applicata: dirgli «la cartella non è protetta» senza
    /// dirgli come proteggerla sposta il problema invece di risolverlo.
    /// </para>
    /// <para>
    /// Si usano gli identificativi numerici e non i nomi dei gruppi: i nomi integrati
    /// cambiano con la lingua di Windows, e un comando che funziona solo su
    /// un'installazione italiana è un comando che fallirà senza spiegare perché.
    /// </para>
    /// </remarks>
    private static string DescribeFix(string folder)
        => $"icacls \"{folder}\" /inheritance:r /grant \"*S-1-5-18:(OI)(CI)F\" "
           + "\"*S-1-5-32-544:(OI)(CI)F\" \"*S-1-5-32-545:(OI)(CI)RX\"  —  poi  "
           + $"icacls \"{folder}\" /setowner \"*S-1-5-32-544\" /t";

    /// <summary>Compone un pacchetto dalla configurazione corrente.</summary>
    /// <param name="sections">Sezioni da includere.</param>
    /// <param name="includeHotkeys">Se includere i profili dei tasti rapidi.</param>
    /// <param name="policy">Criterio di blocco da includere, oppure <c>null</c>.</param>
    /// <param name="revision">Revisione della configurazione.</param>
    /// <param name="description">Nota dell'amministratore.</param>
    public ConfigurationBundle Compose(
        IReadOnlyList<SettingsSection> sections,
        bool includeHotkeys,
        ConfigurationPolicy? policy,
        int revision,
        string? description)
    {
        return new ConfigurationBundle
        {
            Formato = ConfigurationBundle.FormatMarker,
            Revisione = Math.Max(1, revision),
            Descrizione = description,
            CreatoSu = Environment.MachineName,
            Sezioni = [.. sections],

            // Si porta la configurazione intera e si sceglie all'applicazione: cosi'
            // un pacchetto resta leggibile per intero da chi lo ispeziona, e le
            // sezioni dichiarate dicono che cosa verra' effettivamente scritto.
            Impostazioni = SanitizeForExport(_settings.Current),

            ProfiliTasti = includeHotkeys ? _hotkeys.SerializeAll() : null,
            ProfiloTastiAttivo = includeHotkeys ? _hotkeys.Active.Name : null,
            Criterio = policy,
        };
    }

    /// <summary>Scrive un pacchetto su file.</summary>
    /// <param name="bundle">Pacchetto da scrivere.</param>
    /// <param name="path">Percorso di destinazione.</param>
    public BundleResult Save(ConfigurationBundle bundle, string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(bundle, JsonOptions), Encoding.UTF8);

            _log.LogInformation(
                "Pacchetto di configurazione scritto in {Path}: revisione {Revisione}, {Sezioni} sezioni",
                path, bundle.Revisione, bundle.Sezioni.Count);

            return new BundleResult(
                true,
                $"Configurazione esportata in {Path.GetFileName(path)}. "
                + "Non contiene password: sulle altre postazioni vanno reinserite");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scrittura del pacchetto di configurazione non riuscita");
            return new BundleResult(false, "Esportazione non riuscita. Verifica i permessi sulla cartella scelta");
        }
    }

    /// <summary>Legge un pacchetto da file.</summary>
    /// <param name="path">Percorso del file.</param>
    /// <returns>Il pacchetto, oppure <c>null</c> se il file non è un pacchetto valido.</returns>
    public ConfigurationBundle? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var bundle = JsonSerializer.Deserialize<ConfigurationBundle>(
                File.ReadAllText(path, Encoding.UTF8), JsonOptions);

            if (bundle is null) return null;

            // Il marcatore distingue un pacchetto da un settings.json esportato con la
            // vecchia funzione: i due sono entrambi JSON e finiscono nella stessa
            // cartella, e applicare il secondo come pacchetto scriverebbe sezioni che
            // nessuno ha scelto.
            if (!string.Equals(bundle.Formato, ConfigurationBundle.FormatMarker, StringComparison.Ordinal))
            {
                _log.LogWarning("Il file {Path} non e' un pacchetto di configurazione di BlindPhone", path);
                return null;
            }

            return bundle;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lettura del pacchetto {Path} non riuscita", path);
            return null;
        }
    }

    /// <summary>
    /// Applica un pacchetto alla configurazione corrente.
    /// </summary>
    /// <param name="bundle">Pacchetto da applicare.</param>
    /// <returns>Esito, con la configurazione risultante.</returns>
    /// <remarks>
    /// Vengono sovrascritte <b>solo</b> le sezioni dichiarate nel pacchetto. Il resto
    /// resta com'è sulla postazione: è quello che permette di distribuire una
    /// configurazione comune senza cancellare l'account SIP di ciascuna.
    /// </remarks>
    public BundleResult Apply(ConfigurationBundle bundle)
    {
        if (bundle.Impostazioni is null && bundle.ProfiliTasti is null && bundle.Criterio is null)
        {
            return new BundleResult(false, "Il pacchetto non contiene nulla da applicare");
        }

        try
        {
            // La configurazione di partenza si cattura adesso: dopo il salvataggio
            // _settings.Current e' quella nuova, e il confronto che dice all'operatore
            // che cosa e' stato abbassato non avrebbe piu' niente con cui confrontare.
            var prima = _settings.Current;
            var risultato = Merge(prima, bundle);
            var declassamenti = DescribeSecurityDowngrades(prima, risultato);

            if (!_settings.Save(risultato))
            {
                return new BundleResult(false, "Applicazione non riuscita: la configurazione non si e' salvata");
            }

            var profili = ApplyHotkeys(bundle);

            _log.LogInformation(
                "Pacchetto di configurazione applicato: revisione {Revisione}, sezioni {Sezioni}, "
                + "profili tasti {Profili}",
                bundle.Revisione,
                string.Join(", ", bundle.Sezioni),
                profili);

            var parti = new List<string>();

            if (bundle.Sezioni.Count > 0)
            {
                parti.Add(string.Join(", ", bundle.Sezioni.Select(ConfigurationPolicy.DescribeSection)));
            }

            if (profili > 0) parti.Add($"{profili} profili di tasti rapidi");

            var messaggio = parti.Count == 0
                ? "Configurazione applicata"
                : $"Configurazione applicata: {string.Join("; ", parti)}. "
                  + "Le password vanno reinserite: non viaggiano nel pacchetto";

            if (declassamenti.Count > 0)
            {
                foreach (var avviso in declassamenti)
                {
                    _log.LogWarning("Pacchetto di configurazione, sicurezza abbassata: {Avviso}", avviso);
                }

                // SI DICE, E AD ALTA VOCE.
                //
                // Un operatore vedente avrebbe davanti l'icona del lucchetto o la
                // casella spuntata nel pannello. Chi non vede ha questo annuncio e
                // nient'altro, e sono proprio le modifiche che un pacchetto
                // malintenzionato farebbe per prime.
                messaggio += $". ATTENZIONE: {string.Join("; ", declassamenti)}";
            }

            return new BundleResult(true, messaggio, risultato);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applicazione del pacchetto di configurazione non riuscita");
            return new BundleResult(false, "Applicazione non riuscita. Il registro ne ha conservato il dettaglio");
        }
    }

    /// <summary>
    /// Compone la configurazione risultante prendendo dal pacchetto le sole sezioni
    /// dichiarate.
    /// </summary>
    private AppSettings Merge(AppSettings current, ConfigurationBundle bundle)
    {
        var origine = bundle.Impostazioni;
        if (origine is null) return ApplyPolicy(current, bundle);

        var risultato = current;

        foreach (var sezione in bundle.Sezioni)
        {
            risultato = sezione switch
            {
                // Gli account arrivano senza password: quelle stanno nel deposito
                // protetto della postazione di destinazione, e restano da inserire.
                SettingsSection.SipAccounts => risultato with
                {
                    SipAccounts = origine.SipAccounts,
                    DefaultSipAccountId = origine.DefaultSipAccountId,
                    Sip = origine.Sip,
                },

                SettingsSection.Pbx => risultato with { Pbx = origine.Pbx },

                SettingsSection.Audio => risultato with
                {
                    Microphone = origine.Microphone,
                    Conversation = origine.Conversation,
                    Ringer = origine.Ringer,
                    Announcements = origine.Announcements,

                    // La suoneria e' un percorso che il programma apre da se'
                    // all'avvio: un valore di rete non e' un file che potrebbe non
                    // esserci, e' un'autenticazione verso un host scelto da chi ha
                    // scritto il pacchetto.
                    RingtonePath = AcceptLocalRingtone(origine.RingtonePath, risultato.RingtonePath),
                    Audio = origine.Audio,

                    // Fa parte della sezione Audio come il resto: un reparto che ha
                    // trovato il livello di riduzione del rumore adatto ai propri
                    // uffici lo distribuisce insieme ai dispositivi.
                    MicrophoneProcessing = origine.MicrophoneProcessing,
                },

                SettingsSection.Accessibility => risultato with { Accessibility = origine.Accessibility },
                SettingsSection.Calls => risultato with { Calls = origine.Calls },
                SettingsSection.Tray => risultato with { Tray = origine.Tray },

                SettingsSection.Contacts => risultato with
                {
                    CallHistoryRetentionDays = origine.CallHistoryRetentionDays,
                },

                SettingsSection.Diagnostics => risultato with
                {
                    Support = origine.Support,
                    DiagnosticLogRetentionDays = origine.DiagnosticLogRetentionDays,
                    DiagnosticLogLevel = origine.DiagnosticLogLevel,
                    DebugModeEnabled = origine.DebugModeEnabled,
                },

                // I tasti rapidi non stanno in AppSettings: vivono nel loro deposito
                // e si applicano a parte.
                _ => risultato,
            };
        }

        return ApplyPolicy(risultato with { AppliedConfigurationRevision = bundle.Revisione }, bundle);
    }

    private static AppSettings ApplyPolicy(AppSettings settings, ConfigurationBundle bundle)
        => bundle.Criterio is null
            ? settings with { AppliedConfigurationRevision = bundle.Revisione }
            : settings with
            {
                Policy = bundle.Criterio with { Origin = PolicyOrigin.User },
                AppliedConfigurationRevision = bundle.Revisione,
            };

    /// <summary>
    /// Accetta la suoneria proposta dal pacchetto solo se è un file locale.
    /// </summary>
    /// <param name="proposto">Percorso che arriva dal pacchetto.</param>
    /// <param name="attuale">Percorso in uso sulla postazione, che resta se il primo viene scartato.</param>
    /// <remarks>
    /// <para>
    /// Un valore come <c>\\host\share\suoneria.wav</c> fa partire un'autenticazione
    /// verso <c>host</c> appena la suoneria viene caricata, e chi controlla quell'host
    /// raccoglie un tentativo di accesso con l'identità dell'operatore. Gli è bastato
    /// scrivere una riga in un file di configurazione.
    /// </para>
    /// <para>
    /// Si scarta il valore e si tiene quello che c'era, invece di rifiutare l'intero
    /// pacchetto: le altre sezioni possono essere perfettamente legittime, e una
    /// postazione che non si configura è un costo certo contro un rischio che qui è
    /// già stato rimosso.
    /// </para>
    /// </remarks>
    private string? AcceptLocalRingtone(string? proposto, string? attuale)
    {
        if (ProtectedLocation.IsLocalPath(proposto)) return proposto;

        _log.LogWarning(
            "Suoneria {Path} del pacchetto di configurazione scartata: non e' un percorso locale. Resta {Attuale}",
            proposto, attuale ?? "il segnale predefinito");

        return attuale;
    }

    /// <summary>
    /// Elenca, in frasi pronunciabili, le protezioni che il pacchetto abbassa.
    /// </summary>
    /// <param name="prima">Configurazione in uso sulla postazione.</param>
    /// <param name="dopo">Configurazione che il pacchetto produrrebbe.</param>
    /// <remarks>
    /// <para>
    /// <b>Perché esiste anche con la cartella protetta.</b> Un pacchetto che arriva da
    /// un amministratore può comunque abbassare la sicurezza per errore — un modello
    /// preparato durante un collaudo, con la verifica dei certificati spenta, e poi
    /// distribuito. Qui non si rifiuta niente: si dice che è accaduto, perché la
    /// differenza fra una scelta e una dimenticanza la vede solo chi se ne accorge.
    /// </para>
    /// <para>
    /// Sono elencate anche le cose che non sono un «abbassamento» ma uno
    /// <i>spostamento</i>: il centralino, il registrar, il server degli aggiornamenti
    /// e l'indirizzo dell'assistenza. Un pacchetto che li muove manda le credenziali
    /// e i dati di traffico verso una macchina diversa da prima, e il legame fra
    /// segreto e host (vedi <see cref="SecretStore.GetForHost"/>) impedisce che le
    /// password partano, ma non che l'operatore debba saperlo.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> DescribeSecurityDowngrades(AppSettings prima, AppSettings dopo)
    {
        var avvisi = new List<string>();

        if (!prima.Pbx.AcceptAnyCertificate && dopo.Pbx.AcceptAnyCertificate)
        {
            avvisi.Add("la verifica dei certificati del centralino e' stata disattivata");
        }

        if (!prima.Updates.AllowUnverifiedPackages && dopo.Updates.AllowUnverifiedPackages)
        {
            avvisi.Add("sono stati ammessi aggiornamenti senza impronta dichiarata");
        }

        if (Spostato(prima.Pbx.Host, dopo.Pbx.Host))
        {
            avvisi.Add($"il centralino e' cambiato: ora e' {dopo.Pbx.Host}");
        }

        if (Spostato(prima.Updates.ServerUrl, dopo.Updates.ServerUrl))
        {
            avvisi.Add("il server degli aggiornamenti e' cambiato");
        }

        if (Spostato(prima.Support.UploadUrl, dopo.Support.UploadUrl))
        {
            avvisi.Add("l'indirizzo a cui si inviano i pacchetti di assistenza e' cambiato");
        }

        avvisi.AddRange(DescribeAccountDowngrades(prima, dopo));

        return avvisi;

        // Un valore che viene svuotato non e' uno spostamento: non manda niente da
        // nessuna parte, e segnalarlo sarebbe rumore su ogni pacchetto che lascia
        // fuori una sezione.
        static bool Spostato(string? vecchio, string? nuovo)
            => !string.IsNullOrWhiteSpace(nuovo)
               && !string.Equals(vecchio, nuovo, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gli abbassamenti che riguardano i singoli account SIP.
    /// </summary>
    /// <remarks>
    /// Il confronto è per identificativo e non per posizione nell'elenco: un pacchetto
    /// può riordinare gli account, e confrontare il primo con il primo produrrebbe
    /// avvisi inventati.
    /// </remarks>
    private static IEnumerable<string> DescribeAccountDowngrades(AppSettings prima, AppSettings dopo)
    {
        foreach (var nuovo in dopo.SipAccounts)
        {
            var vecchio = prima.SipAccounts.FirstOrDefault(
                a => string.Equals(a.Id, nuovo.Id, StringComparison.Ordinal));

            if (vecchio is null) continue;

            if (TransportRank(nuovo.Transport) < TransportRank(vecchio.Transport))
            {
                yield return $"l'account {nuovo.Name} passa da {vecchio.Transport} a {nuovo.Transport}: "
                             + "la segnalazione non e' piu' cifrata";
            }

            if (SrtpRank(nuovo.Srtp) < SrtpRank(vecchio.Srtp))
            {
                yield return $"l'account {nuovo.Name} passa da cifratura {vecchio.Srtp} a {nuovo.Srtp}: "
                             + "la voce puo' viaggiare in chiaro";
            }

            if (!string.Equals(
                    string.IsNullOrWhiteSpace(vecchio.Registrar) ? vecchio.Domain : vecchio.Registrar,
                    string.IsNullOrWhiteSpace(nuovo.Registrar) ? nuovo.Domain : nuovo.Registrar,
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return $"il registrar dell'account {nuovo.Name} e' cambiato";
            }
        }
    }

    /// <summary>Quanto protegge un trasporto, dal meno al più.</summary>
    private static int TransportRank(string? transport) => transport?.Trim().ToLowerInvariant() switch
    {
        "tls" => 2,
        "tcp" => 1,
        _ => 0,
    };

    /// <summary>Quanto protegge una politica di cifratura della voce, dalla meno alla più.</summary>
    private static int SrtpRank(string? srtp) => srtp?.Trim().ToLowerInvariant() switch
    {
        "required" => 2,
        "preferred" => 1,
        _ => 0,
    };

    private int ApplyHotkeys(ConfigurationBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.ProfiliTasti)) return 0;

        var applicati = _hotkeys.ImportAll(bundle.ProfiliTasti, bundle.ProfiloTastiAttivo);

        if (applicati == 0)
        {
            _log.LogWarning("Il pacchetto conteneva profili di tasti rapidi ma nessuno era interpretabile");
        }

        return applicati;
    }

    /// <summary>
    /// Toglie dalla configurazione ciò che non deve uscire dalla postazione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// I segreti non ci sono già: <see cref="AppSettings"/> non li contiene per
    /// costruzione. Quello che va tolto sono i valori che descrivono <b>questa</b>
    /// postazione e non la configurazione: gli identificativi dei dispositivi audio,
    /// che su un'altra macchina puntano a schede diverse o a niente.
    /// </para>
    /// <para>
    /// I <i>nomi</i> dei dispositivi restano. Servono a chi riceve il pacchetto per
    /// sapere che cosa era stato scelto — «Altoparlanti (EPOS PC 7 USB)» — e la
    /// risoluzione per nome è già prevista dal motore audio quando l'identificativo
    /// non si trova.
    /// </para>
    /// </remarks>
    private static AppSettings SanitizeForExport(AppSettings settings) => settings with
    {
        Microphone = settings.Microphone with { DeviceId = null },
        Conversation = settings.Conversation with { DeviceId = null },
        Ringer = settings.Ringer with { DeviceId = null },
        Announcements = settings.Announcements with { DeviceId = null },
    };
}
