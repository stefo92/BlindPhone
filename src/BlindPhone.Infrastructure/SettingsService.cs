using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Deposito protetto per i segreti: password SIP e Client Secret dell'API.
/// </summary>
/// <remarks>
/// <para>
/// Usa DPAPI con ambito utente: i valori cifrati sono leggibili solo dall'account
/// Windows che li ha scritti, sulla stessa macchina. È sufficiente per una
/// postazione di lavoro e non richiede infrastruttura aggiuntiva, il che conta
/// quando il prodotto va installato su decine di postazioni di enti diversi.
/// </para>
/// <para>
/// Il punto fermo è che nessun segreto finisce nel file JSON delle impostazioni,
/// che è esportabile, condivisibile e finisce nei backup. Un collaudo di sicurezza
/// presso una PA controlla esattamente questo.
/// </para>
/// </remarks>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BlindPhone.SecretStore.v1");

    private readonly string _path;
    private readonly ILogger<SecretStore> _log;
    private Dictionary<string, string> _secrets = [];

    /// <summary>Crea il deposito.</summary>
    /// <param name="directory">Cartella dati dell'applicazione.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SecretStore(string directory, ILogger<SecretStore>? logger = null)
    {
        _path = Path.Combine(directory, "secrets.dat");
        _log = logger ?? NullLogger<SecretStore>.Instance;
        Load();
    }

    /// <summary>Legge un segreto.</summary>
    /// <param name="key">Chiave del segreto.</param>
    /// <returns>Il valore, oppure stringa vuota se assente.</returns>
    public string Get(string key) => _secrets.GetValueOrDefault(key, string.Empty);

    /// <summary>Scrive un segreto e persiste il deposito.</summary>
    /// <param name="key">Chiave del segreto.</param>
    /// <param name="value">Valore da conservare.</param>
    public void Set(string key, string value)
    {
        _secrets[key] = value;
        Save();
    }

    /// <summary>Rimuove un segreto.</summary>
    /// <param name="key">Chiave del segreto.</param>
    public void Remove(string key)
    {
        if (_secrets.Remove(key)) Save();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            _secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
        }
        catch (Exception ex)
        {
            // Il deposito e' legato all'utente e alla macchina: un profilo copiato
            // altrove non lo puo' decifrare. Si riparte vuoti e si chiedono di nuovo
            // le credenziali, invece di impedire l'avvio dell'applicazione.
            _log.LogWarning(ex, "Deposito dei segreti non leggibile: sara' necessario reinserire le credenziali");
            _secrets = [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var plain = JsonSerializer.SerializeToUtf8Bytes(_secrets);
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path, protectedBytes);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Salvataggio del deposito dei segreti non riuscito");
        }
    }

    /// <summary>Chiave del segreto contenente la password SIP.</summary>
    public const string SipPasswordKey = "sip.password";

    /// <summary>Chiave del segreto contenente il Client Secret dell'API Yeastar.</summary>
    public const string PbxClientSecretKey = "pbx.clientSecret";

    /// <summary>Chiave del segreto contenente la chiave di cifratura degli archivi locali.</summary>
    public const string DatabaseKeyKey = "db.key";

    /// <summary>
    /// Restituisce la chiave con cui sono cifrati rubrica e registro delle chiamate,
    /// generandola alla prima esecuzione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La chiave è casuale a 256 bit e non deriva da nulla di indovinabile: una
    /// passphrase costruita dal nome utente o dal nome della macchina si ricava dal
    /// disco stesso, e proteggerebbe soltanto da chi non ci prova.
    /// </para>
    /// <para>
    /// Vive qui dentro, cioè in <c>secrets.dat</c> protetto da DPAPI con ambito
    /// utente: leggerla richiede l'account Windows dell'operatore su quella macchina.
    /// Un disco estratto e montato altrove non la restituisce, ed è esattamente lo
    /// scenario per cui gli archivi vengono cifrati.
    /// </para>
    /// <para>
    /// <b>Il rovescio va detto:</b> se il profilo Windows viene ricreato o
    /// <c>secrets.dat</c> va perso, la chiave sparisce e gli archivi non sono più
    /// leggibili da nessuno. La rubrica si reimporta dal centralino o dal CSV; il
    /// registro delle chiamate no. È il prezzo della cifratura locale senza un server
    /// che custodisca le chiavi, e per una postazione di lavoro è il compromesso
    /// giusto.
    /// </para>
    /// </remarks>
    public string GetOrCreateDatabaseKey()
    {
        var existing = Get(DatabaseKeyKey);
        if (!string.IsNullOrEmpty(existing)) return existing;

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Set(DatabaseKeyKey, key);

        _log.LogInformation("Generata la chiave di cifratura degli archivi locali");

        return key;
    }
}

/// <summary>
/// Carica e salva la configurazione dell'applicazione.
/// </summary>
/// <remarks>
/// <para>
/// Il file vive in <c>%APPDATA%\BlindPhone</c>, quindi per utente e non per
/// macchina: su una postazione condivisa a turni ogni operatore ritrova le proprie
/// preferenze, incluse le scorciatoie, che sono la cosa più personale di questo
/// prodotto.
/// </para>
/// <para>
/// Il salvataggio è atomico (scrittura su file temporaneo e sostituzione): una
/// configurazione troncata da un'interruzione di corrente lascerebbe l'operatore
/// senza scorciatoie all'avvio successivo, cioè senza modo di usare il telefono.
/// </para>
/// </remarks>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<SettingsService> _log;

    /// <summary>Crea il servizio.</summary>
    /// <param name="directory">
    /// Cartella dati; se omessa si usa <c>%APPDATA%\BlindPhone</c>.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    public SettingsService(string? directory = null, ILogger<SettingsService>? logger = null)
    {
        _log = logger ?? NullLogger<SettingsService>.Instance;

        DataDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlindPhone");

        Directory.CreateDirectory(DataDirectory);
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        Secrets = new SecretStore(DataDirectory);
    }

    /// <summary>Cartella in cui vivono configurazione, segreti e archivi locali.</summary>
    public string DataDirectory { get; }

    /// <summary>Percorso del file di configurazione.</summary>
    public string SettingsPath { get; }

    /// <summary>Deposito protetto dei segreti.</summary>
    public SecretStore Secrets { get; }

    /// <summary>Configurazione corrente.</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>
    /// Crea un servizio isolato in una cartella temporanea, per le prove.
    /// </summary>
    /// <param name="label">Etichetta della cartella, per riconoscerla.</param>
    /// <remarks>
    /// <para>
    /// Esiste perché verificare i valori predefiniti richiede un'installazione
    /// pulita, e la tentazione è cancellare il file dell'utente per ottenerla.
    /// Quel file contiene l'account SIP: cancellarlo significa mettere la
    /// postazione fuori servizio, e il danno si scopre solo quando l'operatore
    /// prova a chiamare.
    /// </para>
    /// <para>
    /// Ogni prova che riguarda la configurazione va fatta qui, mai su
    /// <c>%APPDATA%</c>.
    /// </para>
    /// </remarks>
    public static SettingsService CreateTemporary(string label = "prova")
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "BlindPhone.Test",
            $"{label}-{Guid.NewGuid():N}");

        return new SettingsService(directory);
    }

    /// <summary>Sollevato dopo ogni salvataggio, perché i servizi si riconfigurino a caldo.</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>
    /// Carica la configurazione dal disco, o restituisce quella predefinita se il
    /// file non esiste.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            // Al primo avvio il file viene scritto subito con i valori predefiniti.
            // Senza, l'operatore non avrebbe nulla da modificare ne' da interfaccia
            // ne' da file, e l'amministratore che prepara la postazione non saprebbe
            // quali chiavi esistono.
            Current = new AppSettings().Migrate();
            Save(Current);
            return Current;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            var caricata = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            // La migrazione avviene in lettura e non in scrittura, di proposito: una
            // postazione che si aggiorna deve funzionare al primo avvio, prima che
            // qualcuno apra le impostazioni e salvi. Il file su disco resta com'e'
            // finche' non c'e' un motivo per riscriverlo.
            Current = caricata.Migrate();

            if (caricata.SchemaVersion != Current.SchemaVersion)
            {
                _log.LogInformation(
                    "Configurazione migrata dallo schema {Vecchio} al {Nuovo}: "
                    + "l'account SIP e' diventato il primo di un elenco",
                    caricata.SchemaVersion, Current.SchemaVersion);
            }
        }
        catch (Exception ex)
        {
            // Un file corrotto non deve impedire l'avvio: si riparte dai valori
            // predefiniti e si conserva l'originale, cosi' l'assistenza puo'
            // recuperare la configurazione precedente.
            _log.LogError(ex, "Configurazione non leggibile: si usano i valori predefiniti");
            TryBackupCorruptedFile();
            Current = new AppSettings().Migrate();
        }

        return Current;
    }

    /// <summary>Salva la configurazione sul disco in modo atomico.</summary>
    /// <param name="settings">Configurazione da salvare.</param>
    /// <returns><c>true</c> se il salvataggio è riuscito.</returns>
    public bool Save(AppSettings settings)
    {
        try
        {
            // Due normalizzazioni prima di scrivere. La migrazione copre il caso di
            // una configurazione costruita a mano nei test o importata da una versione
            // precedente. L'allineamento del campo storico serve invece a chi legge:
            // una configurazione esportata da qui resta utilizzabile da una versione
            // di BlindPhone che gli account multipli non li conosce, perche' trova in
            // "sip" l'account predefinito e non un oggetto vuoto.
            settings = settings.Migrate();
            settings = settings with { Sip = settings.ActiveSipAccount };

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temporary = SettingsPath + ".tmp";

            File.WriteAllText(temporary, json, Encoding.UTF8);

            if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, null);
            else File.Move(temporary, SettingsPath);

            Current = settings;
            SettingsChanged?.Invoke(this, settings);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Salvataggio della configurazione non riuscito");
            return false;
        }
    }

    /// <summary>
    /// Esporta la configurazione, senza segreti, in un file scelto dall'operatore.
    /// </summary>
    /// <param name="path">Percorso di destinazione.</param>
    /// <returns><c>true</c> se l'esportazione è riuscita.</returns>
    /// <remarks>
    /// Serve a due scopi: consentire all'ufficio IT di preparare una postazione e
    /// replicarla, e permettere all'operatore di portare le proprie scorciatoie su
    /// un'altra macchina. L'assenza dei segreti è deliberata e va detta all'utente:
    /// dopo l'importazione le credenziali vanno reinserite.
    /// </remarks>
    public bool Export(string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Esportazione della configurazione non riuscita");
            return false;
        }
    }

    /// <summary>Importa una configurazione da file.</summary>
    /// <param name="path">Percorso del file.</param>
    /// <returns>La configurazione importata, oppure <c>null</c> se il file non è valido.</returns>
    public AppSettings? Import(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

            if (settings is null) return null;

            Save(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importazione della configurazione non riuscita");
            return null;
        }
    }

    private void TryBackupCorruptedFile()
    {
        try
        {
            var backup = SettingsPath + $".corrotto-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(SettingsPath, backup, overwrite: true);
            _log.LogInformation("Configurazione precedente conservata in {Path}", backup);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Copia di sicurezza della configurazione non riuscita");
        }
    }
}
