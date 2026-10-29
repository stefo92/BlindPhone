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
/// <b>FTP in chiaro viene rifiutato, e prima veniva soltanto segnalato.</b> Su
/// <c>ftp://</c> la password e il pacchetto attraversano la rete in chiaro: chi sta
/// sul percorso vede la prima e può sostituire il secondo con un altro programma, che
/// l'operatore installerebbe con i privilegi di amministratore. Un avviso nel
/// registro non è una difesa — nessuno lo legge prima di premere il pulsante — e la
/// scelta non è davvero di chi amministra la postazione, perché l'esito lo paga
/// l'operatore. Serve <c>ftps://</c> o <c>https://</c>.
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
            // Prima era un avviso nel registro e si proseguiva. Il percorso che si
            // apriva — sostituire il pacchetto a chi sta in mezzo alla rete — porta
            // all'esecuzione di codice arbitrario come amministratore, e non e' una
            // cosa su cui lasciare una scelta implicita a chi non l'ha fatta.
            _log.LogError(
                "Server degli aggiornamenti su canale non cifrato ({Server}): controllo rifiutato. "
                + "Serve ftps:// oppure https://",
                _settings.ServerUrl);

            return new UpdateCheckResult(null, UpdateNotes.Empty,
                "Il server degli aggiornamenti usa un canale non cifrato: "
                + "serve un indirizzo ftps:// oppure https://. Avvisa chi amministra la postazione");
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
        if (!IsSecureChannel)
        {
            throw new InvalidOperationException(
                "Il server degli aggiornamenti usa un canale non cifrato: "
                + "serve un indirizzo ftps:// oppure https://. Non e' stato scaricato nulla.");
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
            await DownloadToFileAsync(update.InstallerFileName, parziale, progress, cancellationToken)
                .ConfigureAwait(false);

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebException ex)
            when (ex.Response is FtpWebResponse { StatusCode: FtpStatusCode.ActionNotTakenFileUnavailable })
        {
            // NOTE ASSENTI E NOTE NON LEGGIBILI SONO DUE COSE DIVERSE, E PRIMA NO.
            //
            // Qui il server risponde "550 file non disponibile": il file non e' stato
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
        var richiesta = CreateRequest(fileName, WebRequestMethods.Ftp.DownloadFile);

        using var risposta = (FtpWebResponse)await richiesta.GetResponseAsync().ConfigureAwait(false);
        await using var origine = risposta.GetResponseStream();

        using var memoria = new MemoryStream();
        var buffer = new byte[1024];

        while (true)
        {
            var letti = await origine.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
