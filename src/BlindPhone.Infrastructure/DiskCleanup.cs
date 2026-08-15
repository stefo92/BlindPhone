using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Una famiglia di file eliminabili, con quanti sono e quanto occupano.
/// </summary>
/// <param name="Name">Nome per l'operatore, non per il tecnico.</param>
/// <param name="Explanation">Che cosa si perde eliminandoli.</param>
/// <param name="FileCount">Quanti file.</param>
/// <param name="Bytes">Spazio occupato.</param>
public sealed record CleanupCategory(string Name, string Explanation, int FileCount, long Bytes)
{
    /// <summary>Riga leggibile, per l'elenco della conferma.</summary>
    public string Describe() => $"{Name}: {FileCount} file, {DiskCleanupService.DescribeSize(Bytes)}";
}

/// <summary>
/// Che cosa si libererebbe, prima di liberarlo.
/// </summary>
/// <param name="Categories">Famiglie con almeno un file.</param>
public sealed record CleanupPreview(IReadOnlyList<CleanupCategory> Categories)
{
    /// <summary>File eliminabili in tutto.</summary>
    public int TotalFiles => Categories.Sum(c => c.FileCount);

    /// <summary>Spazio recuperabile in tutto.</summary>
    public long TotalBytes => Categories.Sum(c => c.Bytes);

    /// <summary>Vero quando non c'è nulla da eliminare.</summary>
    public bool IsEmpty => TotalFiles == 0;
}

/// <summary>
/// Esito della pulizia.
/// </summary>
/// <param name="DeletedFiles">File eliminati davvero.</param>
/// <param name="SkippedFiles">File non eliminabili, quasi sempre perché in uso.</param>
/// <param name="BytesFreed">Spazio liberato.</param>
public sealed record CleanupResult(int DeletedFiles, int SkippedFiles, long BytesFreed);

/// <summary>
/// Elimina i file che BlindPhone produce e non gli servono più.
/// </summary>
/// <remarks>
/// <para>
/// <b>Una postazione di centralino riempie il disco da sola.</b> Il registro
/// diagnostico in modalità debug cresce di parecchi megabyte per turno — nella
/// giornata dell'11 agosto 2026 ne ha prodotti sedici in un giorno solo — e nessuno
/// se ne accorge finché il disco non è pieno e il programma non riesce più a
/// scrivere. Da lì in avanti si perde proprio quello che serve a capire un guasto.
/// </para>
/// <para>
/// <b>Quello che questa classe NON tocca conta più di quello che elimina.</b> Nella
/// stessa cartella dei registri vivono la configurazione con l'account SIP, il
/// deposito protetto delle password, la rubrica cifrata, l'elenco delle chiamate e i
/// profili delle scorciatoie. Cancellarli metterebbe la postazione fuori servizio, e
/// su un centralino ospedaliero il danno si scoprirebbe alla prima chiamata che non
/// parte. Qui si elencano i file uno per uno, per estensione e per cartella: non
/// esiste nessuna cancellazione ricorsiva e nessun carattere jolly che possa
/// allargarsi da solo il giorno in cui qualcuno aggiunge un file nuovo.
/// </para>
/// <para>
/// <b>Il registro di oggi resta.</b> È aperto in scrittura dal programma che sta
/// girando, ed è quello che racconta il turno in corso: è il file che serve se il
/// guasto capita fra dieci minuti. Anche le registrazioni delle chiamate restano
/// fuori — non sono file temporanei ma documenti, e vivono in Documenti proprio per
/// non finire in mezzo a una pulizia.
/// </para>
/// </remarks>
public sealed class DiskCleanupService
{
    private readonly string _dataDirectory;
    private readonly string _logDirectory;
    private readonly Func<string?> _currentLogFile;
    private readonly string _updateDirectory;
    private readonly ILogger<DiskCleanupService> _log;

    /// <summary>Cartella in cui l'aggiornamento scarica i pacchetti.</summary>
    /// <remarks>
    /// Sta fra i file temporanei di Windows e non nella cartella dati: è materiale di
    /// passaggio, e un pacchetto di installazione pesa quanto tutto il resto messo
    /// insieme.
    /// </remarks>
    public static string DefaultUpdateDirectory =>
        Path.Combine(Path.GetTempPath(), "BlindPhone-aggiornamenti");

    /// <summary>Crea il servizio a partire dai servizi vivi.</summary>
    /// <param name="settings">Servizio di configurazione, per la cartella dati.</param>
    /// <param name="logProvider">Registro su file, per la cartella e il file di oggi.</param>
    /// <param name="logger">Log diagnostico.</param>
    public DiskCleanupService(
        SettingsService settings,
        FileLoggerProvider logProvider,
        ILogger<DiskCleanupService>? logger = null)
        : this(
            settings.DataDirectory,
            Path.GetDirectoryName(logProvider.CurrentFilePath) ?? settings.DataDirectory,
            // Si legge al momento della pulizia e non adesso: una postazione accesa a
            // cavallo della mezzanotte cambia file, e il "registro di oggi" da
            // risparmiare non e' piu' quello di quando il pannello e' stato aperto.
            () => logProvider.CurrentFilePath,
            DefaultUpdateDirectory,
            logger)
    {
    }

    /// <summary>Crea il servizio su cartelle indicate, per le prove.</summary>
    /// <param name="dataDirectory">Cartella dati dell'applicazione.</param>
    /// <param name="logDirectory">Cartella dei registri.</param>
    /// <param name="currentLogFile">Registro attualmente in scrittura, da risparmiare.</param>
    /// <param name="updateDirectory">Cartella degli scaricamenti di aggiornamento.</param>
    /// <param name="logger">Log diagnostico.</param>
    public DiskCleanupService(
        string dataDirectory,
        string logDirectory,
        Func<string?> currentLogFile,
        string updateDirectory,
        ILogger<DiskCleanupService>? logger = null)
    {
        _dataDirectory = dataDirectory;
        _logDirectory = logDirectory;
        _currentLogFile = currentLogFile;
        _updateDirectory = updateDirectory;
        _log = logger ?? NullLogger<DiskCleanupService>.Instance;
    }

    /// <summary>
    /// Elenca che cosa si può eliminare, senza eliminare niente.
    /// </summary>
    /// <remarks>
    /// <b>La conferma deve poter essere letta ad alta voce.</b> Chi non vede lo
    /// schermo non può aprire la cartella e guardare: l'unico modo di sapere che cosa
    /// sta per sparire è sentirselo dire prima, famiglia per famiglia, con quanti file
    /// sono e quanto pesano.
    /// </remarks>
    public CleanupPreview Inspect()
    {
        var categorie = new List<CleanupCategory>();

        foreach (var (nome, spiegazione, file) in Collect())
        {
            if (file.Count == 0) continue;
            categorie.Add(new CleanupCategory(nome, spiegazione, file.Count, TotalSize(file)));
        }

        return new CleanupPreview(categorie);
    }

    /// <summary>
    /// Elimina i file elencati da <see cref="Inspect"/>.
    /// </summary>
    /// <returns>Quanti ne sono spariti, quanti sono rimasti e quanto spazio si è liberato.</returns>
    /// <remarks>
    /// <para>
    /// <b>Un file che non si lascia eliminare non ferma la pulizia.</b> Su Windows un
    /// file può essere aperto da un antivirus, da un editor lasciato aperto o dal
    /// programma stesso: interrompersi al primo intoppo lascerebbe metà lavoro fatto e
    /// nessuna spiegazione. Si conta e si va avanti, e alla fine si dice quanti sono
    /// rimasti indietro.
    /// </para>
    /// <para>
    /// Le cartelle non si eliminano, solo il loro contenuto: una cartella che sparisce
    /// e viene ricreata al volo è un modo di scoprire un problema di permessi nel
    /// momento sbagliato, cioè mentre si sta scrivendo un registro.
    /// </para>
    /// </remarks>
    public CleanupResult Clean()
    {
        var eliminati = 0;
        var saltati = 0;
        long liberati = 0;

        foreach (var (nome, _, file) in Collect())
        {
            foreach (var percorso in file)
            {
                long dimensione;

                try
                {
                    dimensione = new FileInfo(percorso).Length;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Dimensione di {File} non leggibile", percorso);
                    dimensione = 0;
                }

                try
                {
                    File.Delete(percorso);
                    eliminati++;
                    liberati += dimensione;
                }
                catch (Exception ex)
                {
                    saltati++;
                    _log.LogWarning(
                        ex, "Eliminazione di {File} non riuscita ({Categoria})", percorso, nome);
                }
            }
        }

        _log.LogInformation(
            "Pulizia del disco: {Eliminati} file eliminati, {Saltati} non eliminabili, "
            + "{Liberati} liberati",
            eliminati, saltati, DescribeSize(liberati));

        return new CleanupResult(eliminati, saltati, liberati);
    }

    /// <summary>
    /// Le famiglie di file eliminabili, nell'ordine in cui vanno dette.
    /// </summary>
    /// <remarks>
    /// Un elenco solo, usato sia dall'anteprima sia dalla pulizia: se fossero due,
    /// il giorno in cui se ne aggiunge una il pannello annuncerebbe una cosa e il
    /// pulsante ne farebbe un'altra.
    /// </remarks>
    private List<(string Name, string Explanation, IReadOnlyList<string> Files)> Collect()
    {
        var corrente = SafeFullPath(_currentLogFile());

        return
        [
            (
                "Registri dei giorni scorsi",
                "Servono a indagare una segnalazione gia' chiusa. Il registro di oggi non viene toccato",
                Files(_logDirectory, "blindphone-*.log")
                    .Where(f => !string.Equals(SafeFullPath(f), corrente, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            ),
            (
                "Copie anonimizzate dei registri",
                "Copie senza numeri di telefono, prodotte per l'assistenza: si rifanno in qualunque momento dai registri",
                Files(Path.Combine(_logDirectory, "anonimo"), "*-anonimo.log")
            ),
            (
                "Rapporti di errore",
                "Il dettaglio degli errori gravi gia' segnalati all'avvio",
                Files(Path.Combine(_dataDirectory, "errori"), "errore-*.txt")
            ),
            (
                "Pacchetti di aggiornamento scaricati",
                "Installatori di versioni gia' installate o scartate, lasciati fra i file temporanei di Windows",
                [.. Files(_updateDirectory, "*.msi"), .. Files(_updateDirectory, "*.part")]
            ),
        ];
    }

    private IReadOnlyList<string> Files(string directory, string pattern)
    {
        try
        {
            // SearchOption.TopDirectoryOnly e' il valore predefinito ed e' quello che
            // serve: una discesa ricorsiva raccoglierebbe qualunque cosa qualcuno
            // depositi in una sottocartella, e questa classe deve poter dire con
            // esattezza che cosa elimina.
            return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : [];
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Lettura di {Cartella} non riuscita", directory);
            return [];
        }
    }

    private long TotalSize(IReadOnlyList<string> files)
    {
        long totale = 0;

        foreach (var file in files)
        {
            try
            {
                totale += new FileInfo(file).Length;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Dimensione di {File} non leggibile", file);
            }
        }

        return totale;
    }

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>
    /// Dice una dimensione in una forma che la sintesi vocale legge bene.
    /// </summary>
    /// <param name="bytes">Dimensione in byte.</param>
    /// <remarks>
    /// Niente sigle: «MB» viene letto «emme bi» da alcune sintesi e «megabyte» da
    /// altre, e su un pannello che si usa a orecchio la differenza si sente.
    /// </remarks>
    public static string DescribeSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F1} gigabyte";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F1} megabyte";
        if (bytes >= 1024) return $"{bytes / 1024.0:F0} kilobyte";

        return bytes == 1 ? "1 byte" : $"{bytes} byte";
    }
}
