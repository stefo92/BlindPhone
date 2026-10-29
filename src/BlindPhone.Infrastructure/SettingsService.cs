using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Un segreto non restituito perché l'host configurato non è quello per cui era
/// stato inserito.
/// </summary>
/// <param name="Key">Chiave del segreto.</param>
/// <param name="What">Che cosa va reinserito, in parole che l'operatore riconosce.</param>
/// <param name="BoundHost">L'host per cui il segreto era stato inserito.</param>
/// <param name="RequestedHost">L'host che la configurazione indica adesso.</param>
public sealed record RejectedSecret(string Key, string What, string BoundHost, string RequestedHost)
{
    /// <summary>Frase pronunciabile che spiega all'operatore cosa è successo.</summary>
    /// <remarks>
    /// Senza questa frase l'operatore vedrebbe soltanto un errore di autenticazione, e
    /// cercherebbe il guasto nella password invece che nel cambio di server — che è
    /// l'informazione che gli serve per capire se qualcuno ha toccato la
    /// configurazione.
    /// </remarks>
    public string Speech =>
        $"Il server configurato e' cambiato: era {BoundHost}, ora e' {RequestedHost}. "
        + $"Per sicurezza {What} non viene inviato al server nuovo: va reinserito dalle impostazioni.";
}

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
/// <para>
/// <b>Ogni credenziale è legata all'host per cui è stata inserita.</b> Prima non lo
/// era, e la conseguenza era questa: bastava cambiare l'indirizzo del centralino —
/// in <c>settings.json</c>, che l'operatore può aprire con un editor di testo, o con
/// un pacchetto di configurazione distribuito — perché il Client Secret dell'API
/// partisse verso il nuovo indirizzo senza che nessuno venisse avvisato. Nel caso
/// Yeastar quel segreto viaggia nel corpo JSON di <c>get_token</c>: il nuovo host lo
/// riceve per intero, e da quel momento ha le credenziali dell'API del centralino
/// vero. Vale lo stesso per la password SIP verso un registrar sostituito.
/// </para>
/// <para>
/// Il legame non è una cifratura aggiuntiva: DPAPI protegge il file, questo protegge
/// il <i>destinatario</i>. Sono due problemi diversi — «chi può leggere il segreto
/// sul disco» e «a chi il programma lo manda» — e il secondo non era coperto.
/// </para>
/// </remarks>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BlindPhone.SecretStore.v1");

    /// <summary>Un segreto e l'host a cui appartiene.</summary>
    /// <param name="Value">Il valore del segreto.</param>
    /// <param name="BoundHost">
    /// L'host per cui è stato inserito, già normalizzato. <c>null</c> per le voci
    /// scritte dalle versioni precedenti alla 1.0.17, che non lo registravano.
    /// </param>
    private sealed record SecretEntry(string Value, string? BoundHost);

    private readonly string _path;
    private readonly ILogger<SecretStore> _log;
    private readonly List<RejectedSecret> _rejected = [];

    private Dictionary<string, SecretEntry> _secrets = new(StringComparer.Ordinal);

    /// <summary>Crea il deposito.</summary>
    /// <param name="directory">Cartella dati dell'applicazione.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SecretStore(string directory, ILogger<SecretStore>? logger = null)
    {
        _path = Path.Combine(directory, "secrets.dat");
        _log = logger ?? NullLogger<SecretStore>.Instance;
        Load();
    }

    /// <summary>
    /// I segreti che sono stati negati perché l'host configurato non corrisponde.
    /// </summary>
    /// <remarks>
    /// Si accumulano invece di essere annunciati sul posto: la lettura dei segreti
    /// avviene durante la costruzione dei servizi, prima che esista una voce con cui
    /// parlare. L'applicazione li annuncia quando la pipeline è pronta.
    /// </remarks>
    public IReadOnlyList<RejectedSecret> Rejected => _rejected;

    /// <summary>
    /// Legge un segreto senza guardare a quale host appartenga.
    /// </summary>
    /// <param name="key">Chiave del segreto.</param>
    /// <returns>Il valore, oppure stringa vuota se assente.</returns>
    /// <remarks>
    /// Vale per i segreti che non vengono inviati a nessuno — la chiave di cifratura
    /// degli archivi locali è l'unico caso. Per una credenziale che parte verso un
    /// server si usa <see cref="GetForHost"/>: un segreto letto da qui finisce
    /// dovunque la configurazione dica di mandarlo.
    /// </remarks>
    public string Get(string key)
        => _secrets.TryGetValue(key, out var voce) ? voce.Value : string.Empty;

    /// <summary>Scrive un segreto senza legarlo a un host, e persiste il deposito.</summary>
    /// <param name="key">Chiave del segreto.</param>
    /// <param name="value">Valore da conservare.</param>
    /// <remarks>Come <see cref="Get"/>: solo per i segreti che non vengono inviati.</remarks>
    public void Set(string key, string value)
    {
        _secrets[key] = new SecretEntry(value, null);
        Save();
    }

    /// <summary>
    /// Legge un segreto solo se è stato inserito per l'host indicato.
    /// </summary>
    /// <param name="key">Chiave del segreto.</param>
    /// <param name="host">
    /// Host o indirizzo a cui il segreto sta per essere inviato. Un indirizzo
    /// completo viene ridotto al suo nome host.
    /// </param>
    /// <param name="what">
    /// Che cosa va reinserito, in parole che l'operatore riconosce: finisce
    /// nell'annuncio quando il segreto viene negato.
    /// </param>
    /// <returns>
    /// Il valore, oppure stringa vuota se il segreto non c'è o appartiene a un altro
    /// host.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Le voci scritte dalle versioni precedenti vengono legate qui</b>, al primo
    /// utilizzo, all'host che la configurazione indica in quel momento. È quello che
    /// permette a una postazione aggiornata dalla 1.0.16 di continuare a funzionare
    /// senza reinserire niente, e il costo è noto: se l'host era già stato cambiato
    /// prima del primo avvio della versione nuova, il legame si forma su quello. È
    /// l'unico momento in cui la vecchia falla resta aperta, e dura un avvio.
    /// </para>
    /// <para>
    /// Un segreto negato <b>non viene cancellato</b>: rimettendo l'host di prima
    /// torna a funzionare. Cancellarlo renderebbe definitivo un cambio di indirizzo
    /// fatto per sbaglio.
    /// </para>
    /// </remarks>
    public string GetForHost(string key, string? host, string what)
    {
        if (!_secrets.TryGetValue(key, out var voce) || voce.Value.Length == 0) return string.Empty;

        var richiesto = NormalizeHost(host);

        if (voce.BoundHost is null)
        {
            if (richiesto.Length == 0)
            {
                // Nessun host configurato: non c'e' niente a cui legare il segreto, e
                // non c'e' nemmeno niente a cui inviarlo. Si legherà al primo
                // utilizzo vero.
                return voce.Value;
            }

            _secrets[key] = voce with { BoundHost = richiesto };
            Save();

            _log.LogInformation(
                "Segreto {Chiave} legato all'host {Host}: da ora un cambio di host richiede il reinserimento",
                key, richiesto);

            return voce.Value;
        }

        if (string.Equals(voce.BoundHost, richiesto, StringComparison.Ordinal)) return voce.Value;

        _log.LogWarning(
            "Segreto {Chiave} non restituito: inserito per {Atteso}, la configurazione indica {Richiesto}",
            key, voce.BoundHost, richiesto.Length == 0 ? "nessun host" : richiesto);

        var rifiuto = new RejectedSecret(
            key, what, voce.BoundHost, richiesto.Length == 0 ? "nessuno" : richiesto);

        // Una chiave sola nell'elenco, anche se la si legge piu' volte durante
        // l'avvio: l'operatore deve sentirlo una volta, non una per servizio.
        if (!_rejected.Any(r => string.Equals(r.Key, key, StringComparison.Ordinal)))
        {
            _rejected.Add(rifiuto);
        }

        return string.Empty;
    }

    /// <summary>
    /// Scrive un segreto legandolo all'host per cui è stato inserito.
    /// </summary>
    /// <param name="key">Chiave del segreto.</param>
    /// <param name="value">Valore da conservare. Vuoto rimuove la voce.</param>
    /// <param name="host">Host per cui il segreto vale.</param>
    /// <remarks>
    /// <para>
    /// Si chiama dal pannello delle impostazioni, dove l'operatore ha davanti il campo
    /// dell'indirizzo e quello della password: lì il legame si forma o si rifà, ed è
    /// una scelta presa da una persona. Quello che non deve poter cambiare il
    /// destinatario di un segreto è una modifica silenziosa della configurazione — un
    /// file di testo, un pacchetto distribuito — e quella passa da
    /// <see cref="GetForHost"/>, che non rilega niente.
    /// </para>
    /// <para>
    /// Un valore vuoto rimuove la voce invece di conservare una stringa vuota legata a
    /// un host: «nessuna password» e «password vuota per questo server» si
    /// comporterebbero allo stesso modo, e la prima è quella che si intende. Con
    /// un'eccezione, spiegata nel corpo: non rimuove una credenziale che vale per un
    /// altro server.
    /// </para>
    /// </remarks>
    public void SetForHost(string key, string value, string? host)
    {
        if (string.IsNullOrEmpty(value))
        {
            // UN CAMPO VUOTO NON SIGNIFICA SEMPRE «CANCELLA».
            //
            // Quando la credenziale e' legata a un altro server, il pannello la mostra
            // vuota di proposito: non e' stata cancellata, e' stata nascosta perche'
            // non vale per il server configurato. Salvare la scheda senza ritoccare
            // quel campo la distruggerebbe — e distruggerebbe anche la via d'uscita,
            // perche' rimettere l'indirizzo di prima deve far tornare le cose a
            // posto. Chi vuole davvero cancellarla svuota il campo con il server
            // giusto configurato, e allora il ramo sotto fa il suo lavoro.
            if (_secrets.TryGetValue(key, out var esistente)
                && esistente.BoundHost is { } legato
                && !string.Equals(legato, NormalizeHost(host), StringComparison.Ordinal))
            {
                _log.LogInformation(
                    "Credenziale {Chiave} conservata: vale per {Legato}, non per il server configurato",
                    key, legato);

                return;
            }

            Remove(key);
            return;
        }

        var legato = NormalizeHost(host);
        _secrets[key] = new SecretEntry(value, legato.Length == 0 ? null : legato);

        _rejected.RemoveAll(r => string.Equals(r.Key, key, StringComparison.Ordinal));

        Save();
    }

    /// <summary>L'host a cui un segreto è legato, se è legato a uno.</summary>
    /// <param name="key">Chiave del segreto.</param>
    /// <returns>L'host normalizzato, oppure <c>null</c>.</returns>
    /// <remarks>
    /// Serve al pannello per dire all'operatore che una credenziale stava su un altro
    /// server, prima di rilegarla al nuovo.
    /// </remarks>
    public string? BoundHostOf(string key)
        => _secrets.TryGetValue(key, out var voce) ? voce.BoundHost : null;

    /// <summary>Rimuove un segreto.</summary>
    /// <param name="key">Chiave del segreto.</param>
    public void Remove(string key)
    {
        _rejected.RemoveAll(r => string.Equals(r.Key, key, StringComparison.Ordinal));

        if (_secrets.Remove(key)) Save();
    }

    /// <summary>
    /// Riduce un host o un indirizzo alla forma con cui si confrontano.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Un indirizzo completo si riduce al suo nome host: <c>ftps://server/blindphone/</c>
    /// e <c>ftps://server/altra-cartella/</c> sono lo stesso server, e cambiare
    /// cartella non cambia a chi si manda la password.
    /// </para>
    /// <para>
    /// Il confronto è fra due valori passati da questa stessa funzione, quindi una
    /// forma che non viene riconosciuta — un host con la porta attaccata, per dirne
    /// una — resta confrontata per intero. Il peggio che accade è un reinserimento in
    /// più quando si aggiunge o si toglie una porta, che è l'errore dalla parte
    /// giusta.
    /// </para>
    /// </remarks>
    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;

        var pulito = host.Trim().TrimEnd('.').ToLowerInvariant();

        if (Uri.TryCreate(pulito, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host.TrimEnd('.').ToLowerInvariant();
        }

        return pulito;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);

            _secrets = ParseSecrets(plain);
        }
        catch (Exception ex)
        {
            // Il deposito e' legato all'utente e alla macchina: un profilo copiato
            // altrove non lo puo' decifrare. Si riparte vuoti e si chiedono di nuovo
            // le credenziali, invece di impedire l'avvio dell'applicazione.
            _log.LogWarning(ex, "Deposito dei segreti non leggibile: sara' necessario reinserire le credenziali");
            _secrets = new Dictionary<string, SecretEntry>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Interpreta il contenuto del deposito, nel formato attuale e in quello
    /// precedente.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fino alla 1.0.16 il deposito era <c>{"chiave": "valore"}</c>; adesso è
    /// <c>{"chiave": {"Value": "...", "BoundHost": "..."}}</c>. Si distinguono dal
    /// tipo del valore, voce per voce, e non dal formato del file intero: una voce
    /// nuova accanto a una vecchia si legge comunque, che è la condizione in cui si
    /// trova il deposito subito dopo l'aggiornamento.
    /// </para>
    /// <para>
    /// Un deposito vuoto o illeggibile costa all'operatore il reinserimento delle
    /// credenziali: per questo si preferisce leggere quel che si può invece di
    /// scartare tutto alla prima voce che non torna.
    /// </para>
    /// </remarks>
    private Dictionary<string, SecretEntry> ParseSecrets(byte[] plain)
    {
        var letti = new Dictionary<string, SecretEntry>(StringComparer.Ordinal);

        using var documento = JsonDocument.Parse(plain);

        if (documento.RootElement.ValueKind != JsonValueKind.Object) return letti;

        foreach (var voce in documento.RootElement.EnumerateObject())
        {
            switch (voce.Value.ValueKind)
            {
                // Formato precedente: il valore nudo, senza host a cui sia legato.
                case JsonValueKind.String:
                    letti[voce.Name] = new SecretEntry(voce.Value.GetString() ?? string.Empty, null);
                    break;

                case JsonValueKind.Object when ReadProperty(voce.Value, "value") is { } valore:
                    letti[voce.Name] = new SecretEntry(valore, ReadProperty(voce.Value, "boundHost"));
                    break;

                default:
                    _log.LogWarning("Voce {Chiave} del deposito dei segreti non interpretabile: ignorata", voce.Name);
                    break;
            }
        }

        return letti;
    }

    /// <summary>
    /// Legge una proprietà di stringa senza badare alle maiuscole.
    /// </summary>
    /// <remarks>
    /// Il deposito viene scritto con i nomi dei membri del record, quindi
    /// <c>Value</c> e <c>BoundHost</c>. La lettura tollera anche la forma con
    /// l'iniziale minuscola, così un deposito scritto da una versione che usasse
    /// l'altra convenzione resta leggibile: il prezzo di sbagliare qui è
    /// l'operatore che reinserisce tutte le password.
    /// </remarks>
    private static string? ReadProperty(JsonElement oggetto, string name)
    {
        foreach (var proprieta in oggetto.EnumerateObject())
        {
            if (!string.Equals(proprieta.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

            return proprieta.Value.ValueKind == JsonValueKind.String ? proprieta.Value.GetString() : null;
        }

        return null;
    }

    /// <remarks>
    /// Il formato scritto è quello nuovo, con l'host accanto al valore. Una versione
    /// di BlindPhone precedente alla 1.0.17 non lo sa leggere: ripartirebbe con il
    /// deposito vuoto e chiederebbe di reinserire le credenziali. È il prezzo di un
    /// ritorno indietro, non di un aggiornamento, ed è recuperabile — a differenza di
    /// un segreto spedito a un host sbagliato.
    /// </remarks>
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
