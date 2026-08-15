using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>Un errore grave registrato su disco, in attesa di essere segnalato.</summary>
/// <param name="Path">File che lo contiene.</param>
/// <param name="OccurredAt">Quando è successo.</param>
/// <param name="Headline">Prima riga, in linguaggio naturale.</param>
public sealed record CrashReport(string Path, DateTimeOffset OccurredAt, string Headline);

/// <summary>
/// Registra su disco gli errori gravi e li ritrova all'avvio successivo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il problema che risolve.</b> Un guasto che si manifesta una volta ogni tre
/// giorni, su una postazione dove l'operatore non vede lo schermo, è
/// irriproducibile per definizione: quando l'assistenza arriva, la finestra di
/// errore è stata chiusa, il registro è stato sovrascritto, e l'unica descrizione
/// disponibile è «a un certo punto non funzionava più». Da qui in avanti resta un
/// file con la traccia completa, scritto nell'istante in cui l'errore accade.
/// </para>
/// <para>
/// <b>Perché un file separato dal registro.</b> Il registro esiste già e contiene
/// l'errore, ma contiene anche tutto il resto: per ritrovarlo bisogna sapere quando
/// è successo, che è precisamente l'informazione che manca. Il file di crash è
/// invece un elenco corto di eventi rari, e la sua sola esistenza è già la
/// segnalazione.
/// </para>
/// <para>
/// <b>Cosa NON contiene.</b> Nessun numero di telefono, nessun nome di contatto,
/// nessuna credenziale: solo il tipo di errore, il messaggio, la traccia dello stack
/// e la versione. È la differenza fra un file che si può inviare subito e uno che
/// richiede una valutazione sul trattamento dei dati personali prima di uscire
/// dall'ente. I numeri restano nel registro, che si allega separatamente e con la
/// consapevolezza di cosa contiene.
/// </para>
/// </remarks>
public sealed class CrashReporter
{
    private readonly string _directory;
    private readonly ILogger<CrashReporter> _log;
    private readonly Lock _gate = new();

    /// <summary>Progressivo che rende univoco il nome del file.</summary>
    private int _counter;

    /// <summary>Quanti rapporti conservare prima di eliminare i più vecchi.</summary>
    /// <remarks>
    /// Venti bastano: se ce ne sono di più il problema non è la conservazione, è che
    /// qualcosa va storto in continuazione, e i venti più recenti lo raccontano
    /// altrettanto bene.
    /// </remarks>
    private const int MaxReports = 20;

    /// <summary>Crea il raccoglitore.</summary>
    /// <param name="dataDirectory">Cartella dati dell'applicazione.</param>
    /// <param name="logger">Log diagnostico.</param>
    public CrashReporter(string dataDirectory, ILogger<CrashReporter>? logger = null)
    {
        _directory = Path.Combine(dataDirectory, "errori");
        _log = logger ?? NullLogger<CrashReporter>.Instance;
    }

    /// <summary>Cartella in cui vivono i rapporti.</summary>
    public string Directory_ => _directory;

    /// <summary>
    /// Scrive un rapporto per un errore grave.
    /// </summary>
    /// <param name="exception">Errore da registrare.</param>
    /// <param name="origin">Da dove arriva: interfaccia, attività di sfondo, processo.</param>
    /// <param name="fatal">Se l'errore ha terminato l'applicazione.</param>
    /// <returns>Il rapporto scritto, oppure <c>null</c> se non è stato possibile scriverlo.</returns>
    /// <remarks>
    /// <b>Non solleva mai eccezioni.</b> Viene invocato dai gestori di ultima istanza,
    /// dove un errore non ha più nessuno che lo raccolga: fallire qui trasformerebbe
    /// il raccoglitore di guasti nella causa del guasto peggiore, cioè la chiusura
    /// improvvisa con le chiamate in corso.
    /// </remarks>
    public CrashReport? Record(Exception exception, string origin, bool fatal)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var now = DateTimeOffset.Now;

            // Millisecondi e contatore, non solo i secondi: gli errori gravi arrivano
            // spesso a grappolo — un guasto che ne provoca altri due nello stesso
            // istante — e con la sola precisione al secondo il secondo rapporto
            // sovrascriveva il primo. Si perdeva proprio la causa iniziale, che e'
            // quella che serve.
            var progressivo = Interlocked.Increment(ref _counter);

            var path = Path.Combine(
                _directory,
                $"errore-{now:yyyyMMdd-HHmmss-fff}-{progressivo}.txt");

            var testo = Compose(exception, origin, fatal, now);

            lock (_gate)
            {
                File.WriteAllText(path, testo, Encoding.UTF8);
                Prune();
            }

            _log.LogError(exception, "Errore grave registrato in {Path}", path);

            return new CrashReport(path, now, Headline(exception, origin));
        }
        catch (Exception failure)
        {
            // Ultima rete: si perde il rapporto, non il posto operatore.
            System.Diagnostics.Debug.WriteLine($"Registrazione dell'errore non riuscita: {failure}");
            return null;
        }
    }

    /// <summary>
    /// Rapporti presenti sul disco, dal più recente.
    /// </summary>
    /// <remarks>
    /// Letto all'avvio: la loro presenza è ciò che permette a BlindPhone di dire
    /// «l'ultima volta qualcosa è andato storto» invece di ripartire come se nulla
    /// fosse. Un operatore che non vede lo schermo non ha altro modo di saperlo.
    /// </remarks>
    public IReadOnlyList<CrashReport> Pending()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return [];

            return
            [
                .. System.IO.Directory.GetFiles(_directory, "errore-*.txt")
                    .Select(Read)
                    .OfType<CrashReport>()
                    .OrderByDescending(r => r.OccurredAt)
            ];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lettura dei rapporti di errore non riuscita");
            return [];
        }
    }

    /// <summary>
    /// Elimina i rapporti dopo che sono stati inviati o dopo che l'operatore li ha
    /// congedati.
    /// </summary>
    /// <returns>Quanti ne sono stati eliminati.</returns>
    public int Clear()
    {
        var eliminati = 0;

        try
        {
            if (!System.IO.Directory.Exists(_directory)) return 0;

            lock (_gate)
            {
                foreach (var file in System.IO.Directory.GetFiles(_directory, "errore-*.txt"))
                {
                    try
                    {
                        File.Delete(file);
                        eliminati++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Rapporto {Path} non eliminato", file);
                    }
                }
            }

            _log.LogInformation("Rapporti di errore eliminati: {Count}", eliminati);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pulizia dei rapporti di errore non riuscita");
        }

        return eliminati;
    }

    private CrashReport? Read(string path)
    {
        try
        {
            var prima = File.ReadLines(path, Encoding.UTF8).FirstOrDefault() ?? "Errore";
            return new CrashReport(path, File.GetLastWriteTime(path), prima);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Elimina i rapporti più vecchi oltre il tetto. Va invocato dentro al lock.</summary>
    private void Prune()
    {
        var files = System.IO.Directory.GetFiles(_directory, "errore-*.txt")
            .OrderByDescending(File.GetLastWriteTime)
            .Skip(MaxReports);

        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Rapporto vecchio {Path} non eliminato", file);
            }
        }
    }

    private static string Headline(Exception exception, string origin)
        => $"{exception.GetType().Name} in {origin}: {FirstLine(exception.Message)}";

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "nessun dettaglio";

        var riga = text.Split('\n')[0].Trim();
        return riga.Length <= 160 ? riga : riga[..160];
    }

    /// <summary>Compone il testo del rapporto.</summary>
    /// <remarks>
    /// La prima riga è deliberatamente autosufficiente: è quella che viene letta
    /// all'operatore e mostrata nell'elenco, e deve bastare a capire se si tratta
    /// dello stesso guasto già segnalato o di uno nuovo.
    /// </remarks>
    private static string Compose(Exception exception, string origin, bool fatal, DateTimeOffset now)
    {
        var sb = new StringBuilder();

        sb.AppendLine(Headline(exception, origin));
        sb.AppendLine();
        sb.AppendLine(CultureInfo.CurrentCulture, $"Quando: {now:dddd d MMMM yyyy, HH:mm:ss}");
        sb.AppendLine(CultureInfo.CurrentCulture, $"Origine: {origin}");
        sb.AppendLine(CultureInfo.CurrentCulture, $"Ha terminato l'applicazione: {(fatal ? "si" : "no")}");
        sb.AppendLine(CultureInfo.CurrentCulture, $"Versione: {DiagnosticEnvironment.ProductVersion}");
        sb.AppendLine(CultureInfo.CurrentCulture, $"Sistema: {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.CurrentCulture, $"Runtime: {Environment.Version}");
        sb.AppendLine(CultureInfo.CurrentCulture, $"Processore: {Environment.ProcessorCount} core");
        sb.AppendLine();
        sb.AppendLine("--- Dettaglio ---");
        sb.AppendLine(exception.ToString());

        // Le eccezioni interne sono spesso la causa vera: ToString() le include, ma
        // annidate e in fondo. Si ripetono in testa alla parte di dettaglio perche'
        // chi legge un rapporto cerca quella riga.
        var inner = exception.InnerException;
        var livello = 1;

        while (inner is not null && livello <= 5)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.CurrentCulture, $"--- Causa interna {livello} ---");
            sb.AppendLine(CultureInfo.CurrentCulture, $"{inner.GetType().FullName}: {inner.Message}");

            inner = inner.InnerException;
            livello++;
        }

        return sb.ToString();
    }
}

/// <summary>Dati d'ambiente riportati nei rapporti e nel riepilogo diagnostico.</summary>
public static class DiagnosticEnvironment
{
    /// <summary>Versione leggibile del prodotto.</summary>
    /// <remarks>
    /// È quella che porta l'etichetta «beta», e va riportata su ogni segnalazione:
    /// senza, una traccia dello stack non si sa a quale codice appartenga.
    /// </remarks>
    public static string ProductVersion =>
        System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                System.Reflection.Assembly.GetEntryAssembly()!)
            ?.InformationalVersion
        ?? "sconosciuta";
}
