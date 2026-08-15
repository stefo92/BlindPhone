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

    /// <summary>Crea il servizio.</summary>
    /// <param name="settings">Configurazione dell'applicazione.</param>
    /// <param name="hotkeys">Deposito dei profili di tasti rapidi.</param>
    /// <param name="logger">Log diagnostico.</param>
    public ConfigurationBundleService(
        SettingsService settings,
        HotkeyProfileStore hotkeys,
        ILogger<ConfigurationBundleService>? logger = null)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _log = logger ?? NullLogger<ConfigurationBundleService>.Instance;
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
    /// </remarks>
    public BundleResult? ApplyProvisioningIfNewer()
    {
        var applicata = _settings.Current.AppliedConfigurationRevision;

        foreach (var cartella in ProvisioningFolders)
        {
            var percorso = Path.Combine(cartella, ConfigurationBundle.ProvisioningFileName);
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

        return null;
    }

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
            var risultato = Merge(_settings.Current, bundle);

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

            return new BundleResult(
                true,
                parti.Count == 0
                    ? "Configurazione applicata"
                    : $"Configurazione applicata: {string.Join("; ", parti)}. "
                      + "Le password vanno reinserite: non viaggiano nel pacchetto",
                risultato);
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
    private static AppSettings Merge(AppSettings current, ConfigurationBundle bundle)
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
                    RingtonePath = origine.RingtonePath,
                    Audio = origine.Audio,
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
