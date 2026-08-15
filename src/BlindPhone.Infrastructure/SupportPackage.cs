using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>Esito della creazione o dell'invio di un pacchetto per l'assistenza.</summary>
/// <param name="Success">Se l'operazione è riuscita.</param>
/// <param name="Message">
/// Messaggio in linguaggio naturale, già pronunciabile: è quello che l'operatore
/// sente, e deve dire cosa è successo e cosa fare adesso.
/// </param>
/// <param name="Path">Percorso del pacchetto, quando è stato prodotto.</param>
public sealed record SupportResult(bool Success, string Message, string? Path = null);

/// <summary>Come recapitare il pacchetto all'assistenza.</summary>
public enum SupportDeliveryMode
{
    /// <summary>Salva soltanto, e apre la cartella che lo contiene.</summary>
    /// <remarks>
    /// È il ripiego che funziona sempre, anche su una postazione senza posta
    /// configurata e senza accesso a Internet: il file esiste, e l'operatore può
    /// dirlo a chi glielo chiede.
    /// </remarks>
    SaveOnly = 0,

    /// <summary>Allega a un messaggio di posta nel programma predefinito.</summary>
    /// <remarks>
    /// Il messaggio viene <b>preparato e non inviato</b>: parte solo quando
    /// l'operatore lo manda. Un allegato con dati di traffico che parte da solo
    /// sarebbe una comunicazione di dati personali senza che nessuno l'abbia
    /// autorizzata.
    /// </remarks>
    Email = 1,

    /// <summary>Carica su un indirizzo HTTPS configurato dall'ente.</summary>
    Upload = 2,
}

/// <summary>
/// Costruisce e recapita il pacchetto diagnostico per l'assistenza.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché esiste come servizio e non come pulsante.</b> Il pacchetto serve
/// proprio quando le cose vanno male, e in quel momento chiedere all'operatore di
/// navigare fino a una scheda delle impostazioni, premere un pulsante, scegliere una
/// cartella in una finestra di sistema e poi ritrovare il file per allegarlo a una
/// mail è una sequenza che non regge. Qui è un'operazione sola, raggiungibile da una
/// combinazione di tasti, che finisce con il file pronto e l'operatore informato di
/// dove sia.
/// </para>
/// <para>
/// <b>Cosa contiene, e perché va detto.</b> Riepilogo diagnostico, rapporti di
/// errore e file di registro recenti. I registri riportano numeri chiamati e
/// chiamanti: sono dati di traffico, cioè dati personali, e chi li invia deve
/// saperlo prima e non dopo. La conferma preventiva non è una formalità.
/// </para>
/// </remarks>
public sealed class SupportPackageService
{
    private readonly SettingsService _settings;
    private readonly FileLoggerProvider _logProvider;
    private readonly CrashReporter _crashes;
    private readonly ILogger<SupportPackageService> _log;

    /// <summary>Quanti file di registro allegare, dal più recente.</summary>
    /// <remarks>
    /// Sette coprono la settimana in cui il problema è stato notato e tengono
    /// l'allegato in una dimensione che la posta di un ente accetta senza rifiutarlo.
    /// </remarks>
    private const int LogFilesToInclude = 7;

    /// <summary>Crea il servizio.</summary>
    /// <param name="settings">Configurazione, per la cartella dati e i recapiti.</param>
    /// <param name="logProvider">Registro su file, per sapere dove stanno i log.</param>
    /// <param name="crashes">Raccoglitore dei rapporti di errore.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SupportPackageService(
        SettingsService settings,
        FileLoggerProvider logProvider,
        CrashReporter crashes,
        ILogger<SupportPackageService>? logger = null)
    {
        _settings = settings;
        _logProvider = logProvider;
        _crashes = crashes;
        _log = logger ?? NullLogger<SupportPackageService>.Instance;
    }

    /// <summary>Cartella in cui finiscono i pacchetti quando non se ne sceglie una.</summary>
    /// <remarks>
    /// Il desktop e non la cartella dati: un file che l'operatore deve poter
    /// nominare al telefono va dove sa di trovarlo, non dentro <c>%APPDATA%</c>, che
    /// per raggiungerlo richiede di sapere che esiste.
    /// </remarks>
    public static string DefaultFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Nome predefinito del pacchetto.</summary>
    public static string SuggestedFileName =>
        $"BlindPhone-assistenza-{DateTime.Now:yyyyMMdd-HHmm}.zip";

    /// <summary>
    /// Costruisce il pacchetto.
    /// </summary>
    /// <param name="path">Percorso di destinazione.</param>
    /// <param name="diagnosticSummary">Riepilogo diagnostico già composto.</param>
    /// <param name="note">Descrizione del problema scritta dall'operatore, se presente.</param>
    /// <returns>Esito, con il messaggio da pronunciare.</returns>
    public SupportResult Build(string path, string diagnosticSummary, string? note = null)
    {
        try
        {
            var cartella = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(cartella)) Directory.CreateDirectory(cartella);

            if (File.Exists(path)) File.Delete(path);

            using var archivio = ZipFile.Open(path, ZipArchiveMode.Create);

            Write(archivio, "riepilogo-diagnostico.txt", diagnosticSummary);

            // La descrizione dell'operatore vale piu' di tutto il resto messo insieme:
            // "succede quando trasferisco durante una seconda chiamata" indirizza la
            // ricerca in un modo che nessun contatore puo' fare.
            if (!string.IsNullOrWhiteSpace(note))
            {
                Write(archivio, "descrizione-del-problema.txt", note);
            }

            var errori = AddCrashReports(archivio);
            var registri = AddLogFiles(archivio);

            var dimensione = new FileInfo(path).Length / 1024;

            _log.LogInformation(
                "Pacchetto per l'assistenza creato in {Path}: {Crashes} rapporti di errore, "
                + "{Logs} registri, {Size} KB",
                path, errori, registri, dimensione);

            var messaggio = errori > 0
                ? $"Pacchetto pronto: {errori} rapporti di errore e {registri} registri, {dimensione} kilobyte"
                : $"Pacchetto pronto: riepilogo e {registri} registri, {dimensione} kilobyte";

            return new SupportResult(true, messaggio, path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Creazione del pacchetto per l'assistenza non riuscita");

            return new SupportResult(
                false,
                "Creazione del pacchetto non riuscita. Verifica lo spazio su disco e i permessi sulla cartella");
        }
    }

    /// <summary>
    /// Carica il pacchetto sull'indirizzo configurato dall'ente.
    /// </summary>
    /// <param name="path">Pacchetto da inviare.</param>
    /// <param name="endpoint">Indirizzo HTTPS di destinazione.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// <para>
    /// L'indirizzo è configurato dall'ente e non fornito con il prodotto: non esiste
    /// un server dell'assistenza a cui mandare i dati per impostazione predefinita, e
    /// non deve esistere. Un softphone che spedisce da solo i registri di traffico a
    /// un indirizzo scelto dal fornitore è un trasferimento di dati personali che
    /// nessuno ha autorizzato.
    /// </para>
    /// <para>
    /// Solo HTTPS: su HTTP il pacchetto viaggerebbe in chiaro con dentro i numeri
    /// chiamati, e l'indirizzo viene rifiutato prima di provarci.
    /// </para>
    /// </remarks>
    public async Task<SupportResult> UploadAsync(
        string path,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new SupportResult(
                false,
                "Indirizzo di invio non valido: serve un indirizzo HTTPS. Il pacchetto resta salvato sul disco",
                path);
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var contenuto = new MultipartFormDataContent();
            using var flusso = File.OpenRead(path);
            using var file = new StreamContent(flusso);

            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            contenuto.Add(file, "pacchetto", Path.GetFileName(path));
            contenuto.Add(new StringContent(DiagnosticEnvironment.ProductVersion), "versione");
            contenuto.Add(new StringContent(Environment.MachineName), "postazione");

            var risposta = await http.PostAsync(uri, contenuto, cancellationToken).ConfigureAwait(false);

            if (!risposta.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "Invio del pacchetto rifiutato dal server: {Status}", risposta.StatusCode);

                return new SupportResult(
                    false,
                    $"Il server dell'assistenza ha rifiutato l'invio, codice {(int)risposta.StatusCode}. "
                    + "Il pacchetto resta salvato sul disco",
                    path);
            }

            _log.LogInformation("Pacchetto inviato a {Uri}", uri);

            return new SupportResult(true, "Pacchetto inviato all'assistenza", path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Invio del pacchetto non riuscito");

            return new SupportResult(
                false,
                "Invio non riuscito: il centralino dell'assistenza non risponde. "
                + "Il pacchetto resta salvato sul disco e si puo' allegare a una mail",
                path);
        }
    }

    /// <summary>
    /// Prepara un messaggio di posta con il pacchetto già allegato.
    /// </summary>
    /// <param name="path">Pacchetto da allegare.</param>
    /// <param name="address">Indirizzo dell'assistenza.</param>
    /// <param name="summary">Riga di descrizione, usata come oggetto.</param>
    /// <remarks>
    /// <para>
    /// Il messaggio viene <b>preparato, non inviato</b>: si apre nel programma di
    /// posta predefinito con destinatario, oggetto, testo e allegato già pronti, e
    /// parte solo quando l'operatore lo manda. È deliberato — un allegato con dati
    /// di traffico che esce dall'ente senza che nessuno prema «invia» sarebbe una
    /// comunicazione di dati personali fatta dal software.
    /// </para>
    /// <para>
    /// L'allegato automatico richiede l'interfaccia MAPI del programma di posta, che
    /// Outlook e i client aziendali forniscono. Dove non c'è si ripiega su
    /// <c>mailto:</c>, che apre il messaggio senza allegato: in quel caso il testo
    /// dice all'operatore dove trovare il file e la cartella viene aperta.
    /// </para>
    /// </remarks>
    public SupportResult PrepareEmail(string path, string address, string summary)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new SupportResult(
                false,
                "Indirizzo dell'assistenza non configurato. Il pacchetto resta salvato sul disco",
                path);
        }

        var oggetto = $"BlindPhone {DiagnosticEnvironment.ProductVersion} - segnalazione da {Environment.MachineName}";

        var corpo = new StringBuilder()
            .AppendLine("Segnalazione da BlindPhone.")
            .AppendLine()
            .AppendLine(summary)
            .AppendLine()
            .AppendLine(CultureInfo.CurrentCulture, $"Pacchetto diagnostico allegato: {Path.GetFileName(path)}")
            .AppendLine(CultureInfo.CurrentCulture, $"Se l'allegato manca, si trova in: {path}")
            .AppendLine()
            .AppendLine("Il pacchetto contiene i registri di BlindPhone, che riportano i numeri")
            .AppendLine("chiamati e chiamanti: sono dati personali e vanno trattati come tali.")
            .ToString();

        if (MapiMail.TrySend(address, oggetto, corpo, path, out var errore))
        {
            _log.LogInformation("Messaggio di segnalazione preparato per {Address}", address);

            return new SupportResult(
                true,
                "Messaggio di posta preparato con il pacchetto allegato: controllalo e premi invia",
                path);
        }

        _log.LogWarning("Preparazione del messaggio con allegato non riuscita: {Errore}", errore);

        // Ripiego senza allegato. Meglio un messaggio da completare a mano che
        // nessun messaggio: l'operatore ha comunque il file e sa dov'e'.
        try
        {
            var url = $"mailto:{Uri.EscapeDataString(address)}"
                      + $"?subject={Uri.EscapeDataString(oggetto)}"
                      + $"&body={Uri.EscapeDataString(corpo)}";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            RevealInExplorer(path);

            return new SupportResult(
                true,
                "Messaggio di posta aperto senza allegato: il pacchetto e' nella cartella appena aperta, "
                + "trascinalo nel messaggio",
                path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura del programma di posta non riuscita");
            RevealInExplorer(path);

            return new SupportResult(
                false,
                $"Programma di posta non disponibile. Il pacchetto e' pronto in {path}",
                path);
        }
    }

    /// <summary>Apre Esplora risorse con il file già selezionato.</summary>
    /// <remarks>
    /// Selezionato e non solo «cartella aperta»: chi usa uno screen reader arriva sul
    /// file senza doverlo cercare in un elenco che può contenerne centinaia.
    /// </remarks>
    public void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Apertura di Esplora risorse non riuscita");
        }
    }

    /// <summary>
    /// Anonimizzatore condiviso da registri e rapporti di errore.
    /// </summary>
    /// <remarks>
    /// Uno solo per pacchetto, e non uno per file: le etichette devono essere le
    /// stesse in tutto lo zip, altrimenti lo stesso recapito comparirebbe come
    /// "ESTERNO-03" in un registro e "ESTERNO-07" in un altro, e correlare due file
    /// diventerebbe impossibile proprio quando serve.
    /// </remarks>
    private readonly LogAnonymizer _anonimizzatore = new();

    private int AddCrashReports(ZipArchive archivio)
    {
        var aggiunti = 0;

        foreach (var rapporto in _crashes.Pending())
        {
            try
            {
                // I rapporti di errore portano il testo completo delle eccezioni, e
                // li' dentro finiscono URI SIP e numeri: passano dallo stesso
                // trattamento dei registri.
                AddAnonymizedFile(archivio, rapporto.Path, $"errori/{Path.GetFileName(rapporto.Path)}");
                aggiunti++;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Rapporto di errore {Path} non allegato", rapporto.Path);
            }
        }

        return aggiunti;
    }

    /// <summary>
    /// Copia un file nel pacchetto dopo averne tolto i recapiti.
    /// </summary>
    /// <param name="archivio">Pacchetto in costruzione.</param>
    /// <param name="percorso">File da allegare.</param>
    /// <param name="voce">Nome della voce dentro il pacchetto.</param>
    /// <remarks>
    /// <para>
    /// <b>Si legge con condivisione in scrittura</b> perché il registro di oggi è
    /// aperto dal registratore stesso, e la lettura esclusiva fallirebbe proprio sul
    /// file più utile: quello che contiene il guasto appena osservato.
    /// </para>
    /// <para>
    /// <b>Un file che non si riesce ad anonimizzare non entra nel pacchetto.</b> È la
    /// scelta scomoda ma unica possibile: allegarlo in chiaro perché
    /// l'anonimizzazione è fallita significherebbe far uscire dall'ente esattamente
    /// i dati che questa funzione esiste per trattenere, e chi invia il pacchetto lo
    /// farebbe credendolo pulito.
    /// </para>
    /// </remarks>
    private void AddAnonymizedFile(ZipArchive archivio, string percorso, string voce)
    {
        List<string> righe = [];

        using (var sorgente = new FileStream(percorso, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var lettore = new StreamReader(sorgente, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            while (lettore.ReadLine() is { } riga) righe.Add(riga);
        }

        var anonime = _anonimizzatore.Anonymize(righe);

        using var destinazione = archivio.CreateEntry(voce).Open();
        using var scrittore = new StreamWriter(destinazione, new UTF8Encoding(false));

        foreach (var riga in anonime) scrittore.WriteLine(riga);
    }

    private int AddLogFiles(ZipArchive archivio)
    {
        var cartella = Path.GetDirectoryName(_logProvider.CurrentFilePath);
        if (string.IsNullOrWhiteSpace(cartella) || !Directory.Exists(cartella)) return 0;

        var aggiunti = 0;

        var registri = Directory.GetFiles(cartella, "blindphone-*.log")
            .OrderByDescending(File.GetLastWriteTime)
            .Take(LogFilesToInclude);

        foreach (var registro in registri)
        {
            try
            {
                AddAnonymizedFile(archivio, registro, $"log/{Path.GetFileName(registro)}");
                aggiunti++;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Registro {Path} non allegato", registro);
            }
        }

        return aggiunti;
    }

    private static void Write(ZipArchive archivio, string nome, string contenuto)
    {
        var voce = archivio.CreateEntry(nome);
        using var scrittore = new StreamWriter(voce.Open(), Encoding.UTF8);
        scrittore.Write(contenuto);
    }

    /// <summary>Cartella dati, per comporre i percorsi predefiniti.</summary>
    public string DataDirectory => _settings.DataDirectory;
}
