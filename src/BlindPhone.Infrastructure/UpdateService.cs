using System.Net;
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
/// <b>FTP in chiaro è accettato e segnalato.</b> Su <c>ftp://</c> la password e il
/// pacchetto attraversano la rete in chiaro: chi sta sul percorso vede la prima e può
/// sostituire il secondo con un altro programma, che l'operatore installerebbe con i
/// privilegi di amministratore. Con <c>ftps://</c> il canale è cifrato e il problema
/// non si pone. L'avviso resta nel registro e nella finestra dell'aggiornamento,
/// perché la scelta è di chi amministra la postazione e va fatta sapendo.
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
    private readonly UpdateSettings _settings;
    private readonly string _password;
    private readonly Version _installed;
    private readonly ILogger<UpdateService> _log;

    /// <summary>Crea il servizio.</summary>
    /// <param name="settings">Indirizzo del server e credenziali.</param>
    /// <param name="password">Password del server, letta dal deposito protetto.</param>
    /// <param name="installed">Versione installata su questa postazione.</param>
    /// <param name="logger">Log diagnostico.</param>
    public UpdateService(
        UpdateSettings settings,
        string password,
        Version installed,
        ILogger<UpdateService>? logger = null)
    {
        _settings = settings;
        _password = password;
        _installed = installed;
        _log = logger ?? NullLogger<UpdateService>.Instance;
    }

    /// <summary>Se la postazione è configurata per cercare aggiornamenti.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.ServerUrl);

    /// <summary>Se il canale verso il server è cifrato.</summary>
    public bool IsSecureChannel
        => _settings.ServerUrl.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase)
           || _settings.ServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

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

        if (!IsSecureChannel)
        {
            _log.LogWarning(
                "Aggiornamenti su canale non cifrato: password e pacchetto viaggiano in chiaro. "
                + "Usare ftps:// o https:// se il server lo consente");
        }

        try
        {
            var files = await ListDirectoryAsync(cancellationToken).ConfigureAwait(false);
            _log.LogInformation("Cartella degli aggiornamenti letta: {Conteggio} file", files.Count);

            var update = UpdateCatalog.FindNewer(files, _installed);

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
    /// L'impronta del file scaricato non corrisponde a quella dichiarata, oppure non è
    /// dichiarata e la configurazione non ammette pacchetti non verificati.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Il pacchetto scaricato non è ancora un pacchetto attendibile.</b> Fra il
    /// server e la postazione può esserci una rete che non si controlla, e l'oggetto
    /// che sta arrivando verrà eseguito con i privilegi di amministratore: è il
    /// momento più delicato dell'intera funzione. L'impronta dichiarata nelle note è
    /// il controllo che si può fare senza infrastruttura — non protegge da chi può
    /// cambiare <b>entrambi</b> i file sul server, ma coglie il pacchetto arrivato a
    /// metà, quello sovrascritto durante il caricamento e la manomissione fatta su un
    /// file solo.
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
        var cartella = Path.Combine(Path.GetTempPath(), "BlindPhone-aggiornamenti");
        Directory.CreateDirectory(cartella);

        var parziale = Path.Combine(cartella, update.InstallerFileName + ".part");
        var definitivo = Path.Combine(cartella, update.InstallerFileName);

        _log.LogInformation("Scaricamento di {File} in corso", update.InstallerFileName);

        await DownloadToFileAsync(update.InstallerFileName, parziale, progress, cancellationToken)
            .ConfigureAwait(false);

        var impronta = await ComputeSha256Async(parziale, cancellationToken).ConfigureAwait(false);

        if (notes.Sha256 is { } attesa)
        {
            if (!string.Equals(impronta, attesa.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(parziale);

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
            File.Delete(parziale);

            throw new InvalidOperationException(
                "Il pacchetto non dichiara la propria impronta e la configurazione non ammette "
                + "pacchetti non verificati.");
        }
        else
        {
            _log.LogWarning(
                "Il pacchetto {File} non dichiara l'impronta SHA-256: impossibile verificarne l'integrita'",
                update.InstallerFileName);
        }

        if (File.Exists(definitivo)) File.Delete(definitivo);
        File.Move(parziale, definitivo);

        _log.LogInformation("Pacchetto pronto in {Percorso}", definitivo);
        return definitivo;
    }

    // -----------------------------------------------------------------------
    // Trasporto
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<string>> ListDirectoryAsync(CancellationToken cancellationToken)
    {
        if (IsHttp)
        {
            // Su HTTP non esiste un elenco: si chiede direttamente il file delle
            // novita' della versione che il server dichiara corrente.
            throw new NotSupportedException(
                "Su HTTPS l'elenco della cartella non e' disponibile: usare un indirizzo ftp:// o ftps://");
        }

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
            var richiesta = CreateRequest(update.NotesFileName, WebRequestMethods.Ftp.DownloadFile);

            using var risposta = (FtpWebResponse)await richiesta.GetResponseAsync().ConfigureAwait(false);
            await using var flusso = risposta.GetResponseStream();
            using var lettore = new StreamReader(flusso, Encoding.UTF8);

            var testo = await lettore.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return UpdateNotes.Parse(testo);
        }
        catch (Exception ex)
        {
            // Le novita' mancanti non impediscono l'aggiornamento: si propone lo
            // stesso, dicendo che non sono state pubblicate. Rifiutarsi di aggiornare
            // per un file di testo assente sarebbe sproporzionato.
            _log.LogWarning(ex, "File delle novita' {File} non leggibile", update.NotesFileName);
            return UpdateNotes.Empty;
        }
    }

    private async Task DownloadToFileAsync(
        string fileName,
        string destination,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var richiesta = CreateRequest(fileName, WebRequestMethods.Ftp.DownloadFile);

        using var risposta = (FtpWebResponse)await richiesta.GetResponseAsync().ConfigureAwait(false);
        var totale = risposta.ContentLength;

        await using var origine = risposta.GetResponseStream();
        await using var destinazione = File.Create(destination);

        var buffer = new byte[81920];
        long scritti = 0;
        var ultimaPercentuale = -1;

        while (true)
        {
            var letti = await origine.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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

    private bool IsHttp => _settings.ServerUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

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

        UriFormatException => "L'indirizzo del server degli aggiornamenti non e' valido",

        _ => "Controllo degli aggiornamenti non riuscito",
    };
}
