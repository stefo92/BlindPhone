using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>Esito del controllo degli aggiornamenti.</summary>
/// <param name="Update">L'aggiornamento trovato, se ce n'è uno più recente.</param>
/// <param name="Notes">Le novità che porta.</param>
/// <param name="Error">Perché il controllo non è riuscito, se non è riuscito.</param>
public sealed record UpdateCheckResult(AvailableUpdate? Update, UpdateNotes Notes, string? Error = null)
{
    /// <summary>C'è un aggiornamento da proporre.</summary>
    public bool HasUpdate => Update is not null;

    /// <summary>Il controllo non è riuscito.</summary>
    public bool Failed => Error is not null;
}

/// <summary>
/// Cerca, scarica e verifica gli aggiornamenti pubblicati sul server dell'ente.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il server è una cartella con dentro dei file, e nient'altro.</b> Non c'è un
/// servizio da installare né un formato da rispettare: si copiano il pacchetto e il
/// file di testo delle novità nella cartella, e le postazioni se ne accorgono. Chi
/// pubblica non ha bisogno di sapere come funziona questa classe, e questa classe non
/// ha bisogno che il server sappia di lei.
/// </para>
/// <para>
/// <b>Non installa niente da sé.</b> Il controllo legge l'elenco della cartella —
/// poche centinaia di byte — e si ferma lì. Lo scaricamento parte quando l'operatore
/// lo chiede, e l'installazione è un programma esterno che chiede a sua volta il
/// consenso di Windows. Un posto operatore che si riavvia da solo a metà turno
/// sarebbe un guasto, non una comodità.
/// </para>
/// <para>
/// <b>Due trasporti, e la stessa cartella.</b> Su <c>ftps://</c> si legge l'elenco dei
/// file e si sceglie il più recente. Su <c>https://</c> un elenco non esiste, e la
/// cartella lo sostituisce con una domanda: <c>?per=x64</c> (o <c>?per=arm64</c>)
/// risponde con un rimando al pacchetto più recente per quel processore. È ciò che fa
/// il portale <c>https://blindphone.curti.cloud/download/</c>, ed è anche il link che
/// le persone seguono dalla pagina per scaricare: il programma e il sito leggono la
/// stessa risposta. Firma e novità stanno accanto al pacchetto, con lo stesso nome.
/// </para>
/// <para>
/// <b>Del rimando si tiene il solo nome del file.</b> Pacchetto, firma e novità si
/// scaricano sempre dalla cartella scritta nelle impostazioni, anche se il rimando
/// indicasse un altro server: chi controlla la risposta può al più indicare un altro
/// nome, che deve comunque essere un pacchetto di BlindPhone con la sua firma.
/// </para>
/// <para>
/// <b>FTP e HTTP in chiaro vengono rifiutati, e prima <c>ftp://</c> veniva soltanto
/// segnalato.</b> Su un canale in chiaro la password e il pacchetto attraversano la
/// rete leggibili: chi sta sul percorso vede la prima e può sostituire il secondo. La
/// firma ferma il pacchetto sostituito, ma la password resta letta, e un avviso nel
/// registro non è una difesa — nessuno lo legge prima di premere il pulsante. Serve
/// <c>ftps://</c> o <c>https://</c>.
/// </para>
/// <para>
/// <b>Due verifiche, e la prima è obbligatoria.</b> La firma applicativa
/// (<see cref="UpdateSignature"/>) dice <i>chi</i> ha prodotto il pacchetto, e senza
/// di essa non si installa niente: nessuna impostazione la disattiva. L'impronta
/// SHA-256 dichiarata nelle note resta come controllo d'integrità del trasferimento,
/// cioè per il file arrivato a metà o sovrascritto durante il caricamento, e non
/// come prova di provenienza: sta sullo stesso server e sullo stesso canale del
/// pacchetto.
/// </para>
/// <para>
/// <b>Sull'obsolescenza di <c>FtpWebRequest</c>.</b> È marcato obsoleto in .NET, e non
/// esiste un sostituto nella libreria standard: la strada indicata è una libreria di
/// terze parti. Per una funzione che elenca una cartella e scarica un file,
/// aggiungere una dipendenza esterna a un prodotto destinato alla pubblica
/// amministrazione — con la sua catena di aggiornamenti e di verifiche di licenza —
/// costa più di quanto renda. Se un domani la classe venisse rimossa, il punto da
/// sostituire è solo questo file.
/// </para>
/// </remarks>
public sealed class UpdateService
{
    /// <summary>Quanto si aspetta che il server cominci a rispondere.</summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Quanto si aspetta il blocco successivo di un file che sta arrivando.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Il gestore HTTP di tutte le postazioni, uno per processo.
    /// </summary>
    /// <remarks>
    /// <b>I rimandi non si seguono da soli.</b> Sulla cartella il rimando è la risposta
    /// che si vuole leggere, non un passaggio da attraversare; sui file un rimando
    /// porterebbe lo scaricamento fuori dalla cartella configurata, cosa che il nome
    /// del file soltanto — vedi il commento della classe — esiste per impedire. Il
    /// proxy è quello di sistema, come per il browser della postazione.
    /// </remarks>
    private static readonly Lazy<HttpMessageHandler> SharedHandler = new(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = ResponseTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    private readonly UpdateSettings _settings;
    private readonly string _password;
    private readonly Version _installed;
    private readonly string _architecture;
    private readonly HttpClient _http;
    private readonly ILogger<UpdateService> _log;

    /// <summary>Crea il servizio.</summary>
    /// <param name="settings">Indirizzo del server e credenziali.</param>
    /// <param name="password">Password del server, letta dal deposito protetto.</param>
    /// <param name="installed">Versione installata su questa postazione.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="httpHandler">
    /// Gestore HTTP da usare al posto di quello condiviso. Serve alle prove, per fidarsi
    /// del certificato di un server finto; non viene chiuso dal servizio.
    /// </param>
    /// <param name="architecture">
    /// Processore del pacchetto da cercare. Se omesso è quello del processo, cioè
    /// quello del pacchetto installato: un BlindPhone x64 su Windows on ARM resta x64.
    /// </param>
    public UpdateService(
        UpdateSettings settings,
        string password,
        Version installed,
        ILogger<UpdateService>? logger = null,
        HttpMessageHandler? httpHandler = null,
        string? architecture = null)
    {
        // Un indirizzo vuoto vale come la cartella del portale, e lo si traduce qui una
        // volta per tutte: elenco, note, firma e pacchetto si leggono tutti dalla stessa
        // cartella. Vedi UpdateSettings.EffectiveServerUrl.
        _settings = settings with { ServerUrl = settings.EffectiveServerUrl };
        _password = password;
        _installed = installed;
        _architecture = string.IsNullOrWhiteSpace(architecture) ? CurrentArchitecture() : architecture.Trim();
        _http = new HttpClient(httpHandler ?? SharedHandler.Value, disposeHandler: false)
        {
            // I tempi li governano ResponseTimeout e ReadTimeout: un tetto unico su
            // tutta la richiesta interromperebbe a meta' un pacchetto da sessanta
            // megabyte su una linea lenta che sta ancora trasferendo.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _log = logger ?? NullLogger<UpdateService>.Instance;
    }

    /// <summary>Processore per cui si cercano i pacchetti: <c>x64</c> o <c>arm64</c>.</summary>
    public string Architecture => _architecture;

    /// <summary>Se c'è una cartella a cui chiedere gli aggiornamenti.</summary>
    /// <remarks>
    /// Con un indirizzo vuoto nelle impostazioni c'è sempre: vale la cartella del
    /// portale. Resta falso solo in una copia compilata senza indirizzo predefinito.
    /// </remarks>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.ServerUrl);

    /// <summary>La cartella a cui il servizio si collega.</summary>
    public string ServerUrl => _settings.ServerUrl;

    /// <summary>Se il canale verso il server è cifrato.</summary>
    public bool IsSecureChannel
        => _settings.ServerUrl.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase)
           || _settings.ServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dice perché un indirizzo non serve come server degli aggiornamenti.
    /// </summary>
    /// <param name="serverUrl">Indirizzo della cartella, come è scritto nelle impostazioni.</param>
    /// <returns>
    /// La frase da dire, che comincia in minuscolo per stare dopo un'introduzione,
    /// oppure <c>null</c> se l'indirizzo è utilizzabile. Anche un indirizzo vuoto
    /// restituisce <c>null</c>: vale come la cartella del portale.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Una frase per ogni schema, perché i motivi sono diversi.</b> Prima tutto
    /// quello che non era <c>ftps://</c> o <c>https://</c> diventava «canale non
    /// cifrato». Per <c>ftp://</c> è vero. Per <c>sftp://</c> è falso: SFTP è
    /// cifrato, ma è il trasferimento di file di SSH, un protocollo diverso da FTP che
    /// questa applicazione non parla. Chi leggeva «non cifrato» andava a cercare la
    /// cifratura sul server, che c'era già, invece di attivare FTPS.
    /// </para>
    /// <para>
    /// <c>https://</c> va bene dalla 1.0.21: la cartella risponde a <c>?per=</c> con il
    /// pacchetto più recente (vedi il commento della classe). <c>http://</c> no, per la
    /// stessa ragione di <c>ftp://</c>.
    /// </para>
    /// </remarks>
    public static string? DescribeAddressProblem(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;

        var indirizzo = serverUrl.Trim();
        var separatore = indirizzo.IndexOf("://", StringComparison.Ordinal);
        var schema = separatore > 0 ? indirizzo[..separatore].ToLowerInvariant() : string.Empty;

        return schema switch
        {
            "ftps" or "https" => null,

            "sftp" or "ssh" or "scp" =>
                $"l'indirizzo comincia con {schema}://, ma BlindPhone non sa collegarsi a un server SFTP. "
                + "SFTP e' il trasferimento di file di SSH, un protocollo diverso da FTP: serve un server FTPS, "
                + "cioe' FTP cifrato con TLS, e un indirizzo che comincia con ftps://, oppure una cartella "
                + "web con un indirizzo che comincia con https://",

            "ftp" =>
                "l'indirizzo comincia con ftp://, cioe' FTP in chiaro: la password e il pacchetto viaggerebbero "
                + "leggibili sulla rete, e chi sta sul percorso potrebbe sostituire il pacchetto. Serve lo stesso "
                + "server con la cifratura TLS attiva, e un indirizzo che comincia con ftps://",

            "http" =>
                "l'indirizzo comincia con http://, cioe' HTTP in chiaro: la password e il pacchetto viaggerebbero "
                + "leggibili sulla rete, e chi sta sul percorso potrebbe sostituire il pacchetto. Serve lo stesso "
                + "indirizzo con https://",

            "" =>
                "l'indirizzo deve cominciare con https:// oppure ftps://, per esempio "
                + "https://aggiornamenti.esempio.it/blindphone/",

            _ =>
                $"BlindPhone non sa collegarsi a un indirizzo {schema}://: serve un indirizzo che comincia "
                + "con https:// oppure ftps://",
        };
    }

    /// <summary>
    /// Chiede al server se c'è una versione più recente, e con quali novità.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// Non solleva eccezioni: un server irraggiungibile non è un errore
    /// dell'operatore e non deve comparire come tale. Il motivo finisce nel risultato
    /// e nel registro, e chi ha chiesto il controllo a mano lo vede scritto.
    /// </remarks>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new UpdateCheckResult(null, UpdateNotes.Empty, "Nessun server degli aggiornamenti configurato");
        }

        if (DescribeAddressProblem(_settings.ServerUrl) is { } problema)
        {
            // Su ftp:// prima era un avviso nel registro e si proseguiva. Il percorso
            // che si apriva — sostituire il pacchetto a chi sta in mezzo alla rete —
            // porta all'esecuzione di codice arbitrario come amministratore, e non e'
            // una cosa su cui lasciare una scelta implicita a chi non l'ha fatta. Gli
            // altri schemi — sftp:// per primo — non sono insicuri: sono protocolli che
            // questa applicazione non parla, e la frase lo dice.
            _log.LogError(
                "Indirizzo del server degli aggiornamenti non utilizzabile ({Server}): {Motivo}. Controllo rifiutato",
                _settings.ServerUrl,
                problema);

            return new UpdateCheckResult(null, UpdateNotes.Empty,
                $"Indirizzo del server degli aggiornamenti non utilizzabile: {problema}. "
                + "Avvisa chi amministra la postazione");
        }

        if (!UpdateSignature.IsConfigured)
        {
            // Senza chiave pubblica incorporata non c'e' modo di distinguere un
            // pacchetto di BlindPhone da uno di chiunque altro. Si dice adesso,
            // mentre non e' ancora stato scaricato niente, e non dopo il download.
            _log.LogError(
                "Questa copia di BlindPhone non porta la chiave pubblica di firma degli aggiornamenti: "
                + "il controllo automatico e' disattivato");

            return new UpdateCheckResult(null, UpdateNotes.Empty,
                "Questa versione di BlindPhone non puo' verificare la firma degli aggiornamenti, "
                + "quindi non li installa da sola. Chiedi a chi amministra la postazione di aggiornarla a mano");
        }

        try
        {
            var files = await ListDirectoryAsync(cancellationToken).ConfigureAwait(false);
            _log.LogInformation(
                "Cartella degli aggiornamenti letta: {Conteggio} file, processore {Processore}",
                files.Count, _architecture);

            var update = UpdateCatalog.FindNewer(files, _installed, _architecture);

            if (update is null)
            {
                _log.LogInformation("Nessun aggiornamento: la versione {Versione} e' la piu' recente", _installed);
                return new UpdateCheckResult(null, UpdateNotes.Empty);
            }

            _log.LogInformation(
                "Aggiornamento disponibile: {Nuova} (installata {Installata}), pacchetto {File}",
                update.DisplayVersion, _installed, update.InstallerFileName);

            var notes = await ReadNotesAsync(update, cancellationToken).ConfigureAwait(false);
            return new UpdateCheckResult(update, notes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Controllo degli aggiornamenti non riuscito");
            return new UpdateCheckResult(null, UpdateNotes.Empty, DescribeFailure(ex));
        }
    }

    /// <summary>
    /// Scarica il pacchetto in una cartella temporanea e ne verifica l'impronta.
    /// </summary>
    /// <param name="update">Aggiornamento scelto.</param>
    /// <param name="notes">Note, da cui si legge l'impronta attesa.</param>
    /// <param name="progress">Percentuale di avanzamento, da 0 a 100.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Il percorso del pacchetto scaricato.</returns>
    /// <exception cref="InvalidOperationException">
    /// Il pacchetto non porta la firma di BlindPhone, la firma non è valida,
    /// l'impronta del file scaricato non corrisponde a quella dichiarata, oppure non è
    /// dichiarata e la configurazione non ammette pacchetti non verificati.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Il pacchetto scaricato non è ancora un pacchetto attendibile.</b> Fra il
    /// server e la postazione può esserci una rete che non si controlla, e l'oggetto
    /// che sta arrivando verrà eseguito con i privilegi di amministratore: è il
    /// momento più delicato dell'intera funzione.
    /// </para>
    /// <para>
    /// <b>I controlli sono tre, in ordine di quanto dimostrano.</b> La firma
    /// applicativa <c>.sig</c> dimostra la <i>provenienza</i> e non si può aggirare
    /// controllando il server, perché richiede una chiave privata che sul server non
    /// c'è: è obbligatoria e nessuna impostazione la disattiva. L'impronta dichiarata
    /// nelle note dimostra l'<i>integrità del trasferimento</i> — coglie il pacchetto
    /// arrivato a metà, quello sovrascritto durante il caricamento — e nulla di più,
    /// perché sta sullo stesso server e sullo stesso canale. La firma Authenticode,
    /// quando c'è un certificato, è ciò che rende verificabile l'editore anche nella
    /// richiesta di consenso di Windows, dove l'operatore prende l'ultima decisione.
    /// </para>
    /// <para>
    /// <b>La firma si scarica prima del pacchetto</b>, e sono settanta byte: se non
    /// c'è, non vale la pena di scaricare trenta megabyte per poi buttarli.
    /// </para>
    /// <para>
    /// Il file scaricato ha estensione <c>.part</c> fino a verifica avvenuta, e prende
    /// il nome definitivo soltanto dopo: un pacchetto scartato non deve restare in
    /// giro con un nome che invita a farci doppio clic.
    /// </para>
    /// </remarks>
    public async Task<string> DownloadAsync(
        AvailableUpdate update,
        UpdateNotes notes,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (DescribeAddressProblem(_settings.ServerUrl) is { } problema)
        {
            throw new InvalidOperationException(
                $"Indirizzo del server degli aggiornamenti non utilizzabile: {problema}. "
                + "Non e' stato scaricato nulla.");
        }

        if (!UpdateSignature.IsConfigured)
        {
            throw new InvalidOperationException(
                "Questa versione di BlindPhone non puo' verificare la firma degli aggiornamenti, "
                + "quindi non li installa da sola. L'aggiornamento va fatto a mano.");
        }

        var cartella = Path.Combine(Path.GetTempPath(), "BlindPhone-aggiornamenti");
        Directory.CreateDirectory(cartella);

        var parziale = Path.Combine(cartella, update.InstallerFileName + ".part");
        var definitivo = Path.Combine(cartella, update.InstallerFileName);
        var fileFirma = definitivo + UpdateSignature.SignatureExtension;

        var firma = await DownloadSignatureAsync(update, cancellationToken).ConfigureAwait(false);

        _log.LogInformation("Scaricamento di {File} in corso", update.InstallerFileName);

        try
        {
            try
            {
                await DownloadToFileAsync(update.InstallerFileName, parziale, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebException or HttpRequestException or TimeoutException or FileNotFoundException)
            {
                // La finestra legge all'operatore il messaggio dell'eccezione: quello
                // grezzo di .NET, in inglese e con i codici del protocollo, non gli
                // dice cosa riferire.
                _log.LogError(ex, "Scaricamento di {File} non riuscito", update.InstallerFileName);

                throw new InvalidOperationException(
                    $"Scaricamento del pacchetto non riuscito. {DescribeFailure(ex)}. Non e' stato installato nulla.",
                    ex);
            }

            // 1. PROVENIENZA. Il controllo che l'attaccante non puo' superare
            //    sostituendo i file sul server, perche' richiede la chiave privata.
            if (!UpdateSignature.Verify(parziale, firma))
            {
                _log.LogError(
                    "Firma del pacchetto {File} non valida: {Byte} byte di firma rifiutati",
                    update.InstallerFileName, firma.Length);

                throw new InvalidOperationException(
                    "Il pacchetto scaricato non porta la firma di BlindPhone. "
                    + "Non e' stato installato nulla: avvisa chi gestisce il server degli aggiornamenti.");
            }

            _log.LogInformation("Firma del pacchetto {File} verificata", update.InstallerFileName);

            // 2. INTEGRITA' DEL TRASFERIMENTO. Le note mancanti non fanno piu'
            //    ricadere su "non verificato": vedi UpdateSettings.AllowUnverifiedPackages.
            var impronta = await ComputeSha256Async(parziale, cancellationToken).ConfigureAwait(false);

            if (notes.Sha256 is { } attesa)
            {
                if (!string.Equals(impronta, attesa.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogError(
                        "Impronta del pacchetto non corrispondente: attesa {Attesa}, calcolata {Calcolata}",
                        attesa, impronta);

                    throw new InvalidOperationException(
                        "Il pacchetto scaricato non corrisponde a quello pubblicato. "
                        + "Non e' stato installato nulla: riprova, e se il problema resta avvisa chi gestisce il server.");
                }

                _log.LogInformation("Impronta del pacchetto verificata: {Impronta}", impronta);
            }
            else if (!_settings.AllowUnverifiedPackages)
            {
                throw new InvalidOperationException(
                    "Il file delle novita' non dichiara l'impronta del pacchetto e la configurazione non "
                    + "ammette pacchetti non verificati. Chiedi a chi pubblica di aggiungere la riga SHA256.");
            }
            else
            {
                _log.LogWarning(
                    "Il pacchetto {File} non dichiara l'impronta SHA-256: verificata la sola firma",
                    update.InstallerFileName);
            }
        }
        catch
        {
            TryDelete(parziale);
            throw;
        }

        if (File.Exists(definitivo)) File.Delete(definitivo);
        File.Move(parziale, definitivo);

        try
        {
            // 3. EDITORE VERIFICATO. Si controlla sul nome definitivo e non sul
            //    .part: Windows sceglie come leggere la firma di un file anche
            //    dall'estensione, e su un ".part" non riconoscerebbe un MSI.
            VerifyPublisher(definitivo);

            // La firma viaggia accanto al pacchetto perche' va riverificata al
            // momento dell'installazione, sul file che msiexec stara' per aprire.
            await File.WriteAllBytesAsync(fileFirma, firma, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(definitivo);
            TryDelete(fileFirma);
            throw;
        }

        _log.LogInformation("Pacchetto pronto in {Percorso}", definitivo);
        return definitivo;
    }

    /// <summary>
    /// Impone la firma Authenticode, quando questa copia del programma sa quale
    /// editore attendersi.
    /// </summary>
    /// <remarks>
    /// Finché non esiste un certificato di firma del codice non c'è niente da
    /// confrontare, e il controllo non si applica: la provenienza l'ha già dimostrata
    /// la firma applicativa, che è il controllo che chiude l'attacco. Il giorno in cui
    /// l'impronta viene dichiarata in <see cref="AuthenticodeVerifier"/>, questo
    /// diventa obbligatorio insieme all'altra.
    /// </remarks>
    private void VerifyPublisher(string packagePath)
    {
        if (!AuthenticodeVerifier.IsConfigured)
        {
            _log.LogInformation(
                "Firma Authenticode non verificata: nessuna impronta di editore dichiarata in questa versione");
            return;
        }

        if (!AuthenticodeVerifier.IsSignedBy(
                packagePath, AuthenticodeVerifier.ExpectedPublisherThumbprints, out var motivo))
        {
            _log.LogError("Firma Authenticode del pacchetto rifiutata: {Motivo}", motivo);

            throw new InvalidOperationException(
                "Il pacchetto non e' firmato dall'editore atteso. Non e' stato installato nulla: "
                + "avvisa chi gestisce il server degli aggiornamenti.");
        }

        _log.LogInformation("Firma Authenticode del pacchetto verificata");
    }

    /// <summary>
    /// Scarica la firma che accompagna un pacchetto.
    /// </summary>
    /// <remarks>
    /// Una firma assente non è un caso da tollerare come il file delle novità: senza,
    /// non c'è niente da verificare, e l'intero scopo di questo passaggio è non
    /// installare ciò che non si riesce a verificare.
    /// </remarks>
    private async Task<byte[]> DownloadSignatureAsync(
        AvailableUpdate update, CancellationToken cancellationToken)
    {
        var nome = UpdateSignature.SignatureFileNameFor(update.InstallerFileName);
        byte[] firma;

        // Il blocco protetto copre il solo scaricamento, e non i controlli che
        // seguono. Escludere le proprie eccezioni con un filtro «non e' una
        // InvalidOperationException» qui non funzionerebbe: WebException deriva
        // proprio da quella, quindi un .sig assente — un 550 del server — non
        // verrebbe tradotto e l'operatore sentirebbe il messaggio grezzo di .NET.
        try
        {
            firma = await DownloadBytesAsync(nome, UpdateSignature.MaxSignatureBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            // Il tetto sulla dimensione: la frase la porta gia' chi l'ha alzato, ed e'
            // piu' precisa di «manca la firma».
            _log.LogError(ex, "File di firma {File} rifiutato", nome);
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "File di firma {File} non scaricabile", nome);

            throw new InvalidOperationException(
                $"Accanto al pacchetto manca la sua firma ({nome}), oppure non si riesce a leggerla. "
                + "L'aggiornamento non viene installato: avvisa chi gestisce il server.",
                ex);
        }

        if (firma.Length == 0)
        {
            throw new InvalidOperationException(
                $"Il file di firma {nome} e' vuoto: l'aggiornamento non viene installato.");
        }

        _log.LogInformation("Firma del pacchetto scaricata: {Byte} byte da {File}", firma.Length, nome);
        return firma;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // Un file temporaneo che resta non e' un guasto da propagare: coprirebbe
            // il motivo vero per cui si e' arrivati qui, che e' il rifiuto del
            // pacchetto.
            _log.LogDebug(ex, "Rimozione di {Path} non riuscita", path);
        }
    }

    // -----------------------------------------------------------------------
    // Trasporto
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<string>> ListDirectoryAsync(CancellationToken cancellationToken)
    {
        if (IsHttps) return [await FindLatestOverHttpsAsync(cancellationToken).ConfigureAwait(false)];

        var richiesta = CreateRequest(string.Empty, WebRequestMethods.Ftp.ListDirectory);

        using var risposta = (FtpWebResponse)await richiesta.GetResponseAsync().ConfigureAwait(false);
        await using var flusso = risposta.GetResponseStream();
        using var lettore = new StreamReader(flusso, Encoding.UTF8);

        var testo = await lettore.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return [.. testo.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private async Task<UpdateNotes> ReadNotesAsync(AvailableUpdate update, CancellationToken cancellationToken)
    {
        try
        {
            await using var remoto = await OpenAsync(update.NotesFileName, cancellationToken).ConfigureAwait(false);
            using var lettore = new StreamReader(remoto.Content, Encoding.UTF8);

            var testo = await lettore.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return UpdateNotes.Parse(testo);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            // NOTE ASSENTI E NOTE NON LEGGIBILI SONO DUE COSE DIVERSE, E PRIMA NO.
            //
            // Qui il server risponde "550 file non disponibile" su FTPS, o "404" su
            // HTTPS: il file non e' stato
            // pubblicato. Non impedisce di proporre l'aggiornamento — si dice che le
            // novita' non ci sono — e l'impronta mancante viene gestita al momento
            // dello scaricamento, che la rifiuta salvo configurazione contraria.
            //
            // Ogni ALTRO guasto si propaga, e il motivo e' un attacco concreto: un
            // tempo bastava far fallire la lettura di questo file di testo per
            // ottenere "impronta non dichiarata", che con il vecchio valore
            // predefinito di AllowUnverifiedPackages significava installare senza
            // verificare niente. Un errore di lettura non e' un'assenza, e
            // confonderli e' stato il difetto.
            _log.LogWarning("File delle novita' {File} non pubblicato sul server", update.NotesFileName);
            return UpdateNotes.Empty;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "File delle novita' {File} non leggibile", update.NotesFileName);

            throw new InvalidOperationException(
                $"Il file delle novita' {update.NotesFileName} esiste ma non si riesce a leggerlo, "
                + "quindi l'impronta del pacchetto non si puo' confrontare. Riprova, e se il problema "
                + "resta avvisa chi gestisce il server.",
                ex);
        }
    }

    /// <summary>
    /// Scarica un file piccolo tenendolo in memoria, con un tetto sulla dimensione.
    /// </summary>
    /// <param name="fileName">Nome del file nella cartella remota.</param>
    /// <param name="maxBytes">Dimensione massima accettata.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// Il tetto non è una cautela formale. Il file arriva da un server che potrebbe
    /// non essere quello giusto, e senza un limite «scarica la firma» diventa un modo
    /// per far riempire la memoria della postazione con un file grande quanto
    /// l'attaccante decide. Non si legge nemmeno <c>ContentLength</c> per decidere:
    /// lo dichiara la stessa parte che manda i byte.
    /// </remarks>
    private async Task<byte[]> DownloadBytesAsync(
        string fileName, int maxBytes, CancellationToken cancellationToken)
    {
        await using var remoto = await OpenAsync(fileName, cancellationToken).ConfigureAwait(false);
        using var inattivita = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using var memoria = new MemoryStream();
        var buffer = new byte[1024];

        while (true)
        {
            var letti = await ReadChunkAsync(remoto.Content, buffer, inattivita, cancellationToken)
                .ConfigureAwait(false);
            if (letti == 0) break;

            if (memoria.Length + letti > maxBytes)
            {
                // InvalidDataException e non InvalidOperationException: la seconda e'
                // la base di WebException, quindi un guasto di rete e un file troppo
                // grande non si distinguerebbero piu' da chi chiama.
                throw new InvalidDataException(
                    $"Il file {fileName} supera i {maxBytes} byte attesi: non viene letto.");
            }

            memoria.Write(buffer, 0, letti);
        }

        return memoria.ToArray();
    }

    private async Task DownloadToFileAsync(
        string fileName,
        string destination,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await using var remoto = await OpenAsync(fileName, cancellationToken).ConfigureAwait(false);
        using var inattivita = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var totale = remoto.Length;

        await using var destinazione = File.Create(destination);

        var buffer = new byte[81920];
        long scritti = 0;
        var ultimaPercentuale = -1;

        while (true)
        {
            var letti = await ReadChunkAsync(remoto.Content, buffer, inattivita, cancellationToken)
                .ConfigureAwait(false);
            if (letti == 0) break;

            await destinazione.WriteAsync(buffer.AsMemory(0, letti), cancellationToken).ConfigureAwait(false);
            scritti += letti;

            if (totale <= 0 || progress is null) continue;

            // Si segnala solo al cambio di punto percentuale: un evento per ogni
            // blocco da ottanta chilobyte farebbe lavorare l'interfaccia piu' della
            // rete.
            var percentuale = (int)(scritti * 100 / totale);
            if (percentuale == ultimaPercentuale) continue;

            ultimaPercentuale = percentuale;
            progress.Report(percentuale);
        }
    }

    private bool IsHttps => _settings.ServerUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Chiede alla cartella HTTPS il pacchetto più recente per questo processore.
    /// </summary>
    /// <returns>Il nome del pacchetto, nella forma in cui sta nella cartella.</returns>
    /// <remarks>
    /// <para>
    /// La domanda è <c>&lt;cartella&gt;?per=x64</c>, e la risposta attesa è un rimando
    /// — 302 o simili — verso <c>BlindPhone-&lt;versione&gt;-x64.msi</c>. È lo stesso
    /// link che le persone seguono dal portale per scaricare, quindi il programma e
    /// il sito non possono indicare due versioni diverse.
    /// </para>
    /// <para>
    /// <b>Ogni altra risposta è un guasto da dire, non «nessun aggiornamento».</b> Una
    /// pagina al posto del rimando, un rimando che non porta a un pacchetto o porta al
    /// pacchetto di un altro processore significano che la cartella non è quella
    /// giusta o non sa rispondere: tacere lascerebbe la postazione indietro, senza
    /// che nessuno se ne accorga.
    /// </para>
    /// </remarks>
    private async Task<string> FindLatestOverHttpsAsync(CancellationToken cancellationToken)
    {
        var domanda = new Uri(FolderUri(), "?per=" + Uri.EscapeDataString(_architecture));

        using var risposta = await SendHttpAsync(domanda, cancellationToken).ConfigureAwait(false);
        var codice = (int)risposta.StatusCode;

        if (codice is >= 300 and < 400 && risposta.Headers.Location is { } rimando)
        {
            var destinazione = rimando.IsAbsoluteUri ? rimando : new Uri(domanda, rimando);
            var nome = Uri.UnescapeDataString(Path.GetFileName(destinazione.AbsolutePath));

            if (UpdateCatalog.ParseVersion(nome) is null)
            {
                throw new InvalidOperationException(
                    $"Il server degli aggiornamenti non indica un pacchetto per i processori {_architecture}: "
                    + "la cartella non lo pubblica, oppure l'indirizzo non e' quello della cartella dei download");
            }

            if (!UpdateCatalog.IsForArchitecture(nome, _architecture))
            {
                throw new InvalidOperationException(
                    $"Il server degli aggiornamenti indica {nome}, che non e' un pacchetto per i processori "
                    + $"{_architecture}: non viene proposto. Avvisa chi gestisce il server");
            }

            _log.LogInformation("La cartella indica come piu' recente {File}", nome);
            return nome;
        }

        EnsureHttpSuccess(risposta);

        throw new InvalidOperationException(
            $"Il server degli aggiornamenti ha risposto con una pagina invece che con il pacchetto piu' recente "
            + $"(codice {codice}): l'indirizzo deve essere quello della cartella dei download, che a ?per= "
            + "risponde con il nome del pacchetto");
    }

    /// <summary>Un file remoto aperto in lettura, su FTPS o su HTTPS.</summary>
    private sealed class RemoteFile(Stream content, long length, IDisposable owner) : IAsyncDisposable
    {
        /// <summary>Il contenuto, da leggere fino alla fine.</summary>
        public Stream Content => content;

        /// <summary>La dimensione dichiarata dal server, o un numero negativo.</summary>
        public long Length => length;

        public async ValueTask DisposeAsync()
        {
            await content.DisposeAsync().ConfigureAwait(false);
            owner.Dispose();
        }
    }

    /// <summary>
    /// Apre un file della cartella remota.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// Il server dice che il file non c'è: 550 su FTPS, 404 o 410 su HTTPS. È l'unico
    /// caso che chi chiama può voler distinguere da un guasto, e su due trasporti si
    /// presenta nello stesso modo.
    /// </exception>
    private async Task<RemoteFile> OpenAsync(string fileName, CancellationToken cancellationToken)
    {
        if (IsHttps)
        {
            var risposta = await SendHttpAsync(new Uri(FolderUri(), Uri.EscapeDataString(fileName)), cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (risposta.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    throw new FileNotFoundException($"Il file {fileName} non e' pubblicato sul server", fileName);
                }

                EnsureHttpSuccess(risposta);

                var flusso = await risposta.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return new RemoteFile(flusso, risposta.Content.Headers.ContentLength ?? -1, risposta);
            }
            catch
            {
                risposta.Dispose();
                throw;
            }
        }

        var richiesta = CreateRequest(fileName, WebRequestMethods.Ftp.DownloadFile);
        FtpWebResponse ftp;

        try
        {
            ftp = (FtpWebResponse)await richiesta.GetResponseAsync().ConfigureAwait(false);
        }
        catch (WebException ex)
            when (ex.Response is FtpWebResponse { StatusCode: FtpStatusCode.ActionNotTakenFileUnavailable })
        {
            throw new FileNotFoundException($"Il file {fileName} non e' pubblicato sul server", fileName, ex);
        }

        return new RemoteFile(ftp.GetResponseStream(), ftp.ContentLength, ftp);
    }

    /// <summary>
    /// Manda una richiesta GET e aspetta le intestazioni della risposta, non il corpo.
    /// </summary>
    /// <exception cref="TimeoutException">Il server non comincia a rispondere in tempo.</exception>
    private async Task<HttpResponseMessage> SendHttpAsync(Uri indirizzo, CancellationToken cancellationToken)
    {
        using var richiesta = new HttpRequestMessage(HttpMethod.Get, indirizzo);

        // Il nome del prodotto e la versione, come fa ogni programma che scarica: al
        // server serve per distinguere le postazioni dai browser nei registri, e non
        // dice niente della persona o dell'ente.
        richiesta.Headers.UserAgent.Add(new ProductInfoHeaderValue("BlindPhone", UpdateCatalog.Describe(_installed)));

        if (!string.IsNullOrWhiteSpace(_settings.Username))
        {
            // Sempre su HTTPS: http:// e' rifiutato prima di arrivare qui.
            var credenziali = Encoding.UTF8.GetBytes($"{_settings.Username}:{_password}");
            richiesta.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credenziali));
        }

        using var attesa = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attesa.CancelAfter(ResponseTimeout);

        try
        {
            return await _http.SendAsync(richiesta, HttpCompletionOption.ResponseHeadersRead, attesa.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Il server degli aggiornamenti non risponde", ex);
        }
    }

    /// <summary>
    /// Trasforma una risposta HTTP di errore in un'eccezione che porta il codice.
    /// </summary>
    private static void EnsureHttpSuccess(HttpResponseMessage risposta)
    {
        if (risposta.IsSuccessStatusCode) return;

        var codice = (int)risposta.StatusCode;

        var frase = risposta.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Il server ha rifiutato le credenziali degli aggiornamenti",
            HttpStatusCode.NotFound => "La cartella degli aggiornamenti non esiste sul server",
            _ when codice is >= 300 and < 400 =>
                $"Il server degli aggiornamenti rimanda altrove (codice {codice}) invece di consegnare il file: "
                + "pacchetto, firma e novita' devono stare nella cartella indicata",
            _ => $"Il server degli aggiornamenti ha risposto con il codice {codice}",
        };

        throw new HttpRequestException(frase, inner: null, risposta.StatusCode);
    }

    /// <summary>
    /// Legge il blocco successivo di un file, con un tetto sull'attesa.
    /// </summary>
    /// <param name="flusso">Il contenuto in arrivo.</param>
    /// <param name="buffer">Dove scriverlo.</param>
    /// <param name="inattivita">
    /// Annullamento collegato a quello di chi chiama, riarmato a ogni blocco: il tetto
    /// vale per il silenzio fra un blocco e l'altro, non per l'intero file.
    /// </param>
    /// <param name="cancellationToken">L'annullamento chiesto da chi chiama.</param>
    /// <exception cref="TimeoutException">Il server ha smesso di mandare dati.</exception>
    private static async Task<int> ReadChunkAsync(
        Stream flusso, Memory<byte> buffer, CancellationTokenSource inattivita, CancellationToken cancellationToken)
    {
        inattivita.CancelAfter(ReadTimeout);

        try
        {
            return await flusso.ReadAsync(buffer, inattivita.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Il server degli aggiornamenti ha smesso di mandare dati", ex);
        }
    }

    /// <summary>L'indirizzo della cartella, sempre con la barra in fondo.</summary>
    /// <remarks>
    /// Senza la barra, <c>…/download?per=x64</c> chiederebbe un file che si chiama
    /// <c>download</c>, e i nomi relativi si risolverebbero nella cartella sopra.
    /// </remarks>
    private Uri FolderUri()
    {
        var radice = _settings.ServerUrl.Trim();
        return new Uri(radice.EndsWith('/') ? radice : radice + "/");
    }

    /// <summary>Il processore di questo processo, come lo scrive l'installatore.</summary>
    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        var altro => altro.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Prepara una richiesta verso la cartella remota.
    /// </summary>
    /// <remarks>
    /// <c>ftps://</c> non è uno schema che <see cref="FtpWebRequest"/> conosca: si
    /// traduce in <c>ftp://</c> con la cifratura accesa, che è ciò che quello schema
    /// significa — FTP con AUTH TLS, non un protocollo diverso.
    /// </remarks>
    private FtpWebRequest CreateRequest(string fileName, string method)
    {
        var radice = _settings.ServerUrl.Trim();
        var cifrato = radice.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase);

        if (cifrato) radice = string.Concat("ftp://", radice.AsSpan("ftps://".Length));
        if (!radice.EndsWith('/')) radice += "/";

        var uri = new Uri(radice + fileName);

#pragma warning disable SYSLIB0014 // Nessun sostituto FTP nella libreria standard: vedi il commento della classe.
        var richiesta = (FtpWebRequest)WebRequest.Create(uri);
#pragma warning restore SYSLIB0014

        richiesta.Method = method;
        richiesta.EnableSsl = cifrato;
        richiesta.UseBinary = true;
        richiesta.UsePassive = true;
        richiesta.KeepAlive = false;
        richiesta.Timeout = 30_000;
        richiesta.ReadWriteTimeout = 60_000;

        richiesta.Credentials = string.IsNullOrWhiteSpace(_settings.Username)
            ? new NetworkCredential("anonymous", "blindphone@localhost")
            : new NetworkCredential(_settings.Username, _password);

        return richiesta;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var flusso = File.OpenRead(path);
        var impronta = await SHA256.HashDataAsync(flusso, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(impronta);
    }

    /// <summary>
    /// Traduce un guasto di rete in una frase che l'operatore può riferire.
    /// </summary>
    /// <remarks>
    /// «530 Not logged in» non dice niente a chi lo legge e dice tutto a chi
    /// amministra: la frase serve al primo per chiamare il secondo sapendo cosa
    /// riferire.
    /// </remarks>
    private static string DescribeFailure(Exception ex) => ex switch
    {
        WebException { Response: FtpWebResponse { StatusCode: FtpStatusCode.NotLoggedIn } }
            => "Il server ha rifiutato le credenziali degli aggiornamenti",

        WebException { Response: FtpWebResponse { StatusCode: FtpStatusCode.ActionNotTakenFileUnavailable } }
            => "La cartella degli aggiornamenti non esiste sul server",

        WebException { Status: WebExceptionStatus.NameResolutionFailure }
            => "Il nome del server degli aggiornamenti non si risolve",

        WebException { Status: WebExceptionStatus.Timeout }
            => "Il server degli aggiornamenti non risponde",

        WebException { Status: WebExceptionStatus.TrustFailure }
            => "Il certificato del server degli aggiornamenti non e' attendibile",

        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }
            => "Il nome del server degli aggiornamenti non si risolve",

        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }
            => "Il certificato del server degli aggiornamenti non e' attendibile, oppure la connessione "
               + "cifrata non si stabilisce",

        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }
            => "Il server degli aggiornamenti non accetta il collegamento",

        // Le risposte con un codice le traduce gia' EnsureHttpSuccess, con la frase giusta.
        HttpRequestException { StatusCode: not null } => ex.Message,

        TimeoutException => ex.Message,

        // «Il file X non e' pubblicato sul server»: la frase e' di OpenAsync.
        FileNotFoundException => ex.Message,

        UriFormatException => "L'indirizzo del server degli aggiornamenti non e' valido",

        // Un rifiuto deciso qui dentro porta già la frase giusta, scritta per chi la
        // dovrà sentire: tradurla una seconda volta la sostituirebbe con una generica.
        //
        // VA DOPO I CASI DI RETE, E NON PRIMA. <see cref="WebException"/> deriva da
        // <see cref="InvalidOperationException"/> — una scelta di libreria che non si
        // indovina leggendo il codice — quindi un ramo messo in cima si prenderebbe
        // anche tutti i guasti di rete, sostituendo «il nome del server non si
        // risolve» con il messaggio grezzo dell'eccezione. Il compilatore lo dice
        // (CS8510), ed è il motivo per cui l'ordine qui non va cambiato.
        InvalidOperationException => ex.Message,

        _ => "Controllo degli aggiornamenti non riuscito",
    };
}
