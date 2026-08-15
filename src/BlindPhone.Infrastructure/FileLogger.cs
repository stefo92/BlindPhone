using System.Text;
using Microsoft.Extensions.Logging;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Registro su file, a rotazione giornaliera, senza dipendenze esterne.
/// </summary>
/// <remarks>
/// <para>
/// Esiste per una ragione precisa: fino a ieri l'applicazione scriveva soltanto
/// verso il debugger, quindi di una segnalazione dal campo non restava alcuna
/// traccia. Ogni indagine partiva da una descrizione a voce e proseguiva per
/// deduzione — ed è così che un difetto è rimasto invisibile finché non ha fatto
/// terminare il processo.
/// </para>
/// <para>
/// <b>Contiene dati personali.</b> Numeri chiamati e chiamanti compaiono nei
/// messaggi, quindi il file è materiale soggetto alla disciplina sulla protezione
/// dei dati: da qui la cancellazione automatica dopo un numero di giorni
/// configurabile, che va allineato alla policy di conservazione dell'ente.
/// </para>
/// <para>
/// Non si usa una libreria di terze parti di proposito. Su un prodotto destinato
/// alla Pubblica Amministrazione ogni dipendenza è una voce in più da giustificare
/// in collaudo e da sorvegliare per vulnerabilità, e quello che serve qui sta in
/// poche decine di righe.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly int _retentionDays;

    private StreamWriter? _writer;
    private DateOnly _openedFor;
    private bool _disposed;

    /// <summary>Crea il provider e prepara la cartella.</summary>
    /// <param name="directory">Cartella in cui scrivere i file di registro.</param>
    /// <param name="retentionDays">Giorni di conservazione; oltre, i file vengono eliminati.</param>
    public FileLoggerProvider(string directory, int retentionDays = 14)
    {
        _directory = directory;
        _retentionDays = Math.Clamp(retentionDays, 1, 365);

        try
        {
            Directory.CreateDirectory(_directory);
            PurgeOldFiles();
        }
        catch (Exception)
        {
            // Un registro che non si riesce ad aprire non deve impedire l'avvio del
            // posto operatore: si perde la diagnostica, non il servizio.
        }
    }

    /// <summary>Percorso del file di oggi.</summary>
    public string CurrentFilePath => Path.Combine(_directory, $"blindphone-{DateTime.Now:yyyyMMdd}.log");

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch (Exception)
            {
                // In chiusura non c'e' piu' nessuno a cui segnalarlo.
            }

            _writer = null;
        }
    }

    /// <summary>Scrive una riga, aprendo o ruotando il file se serve.</summary>
    /// <remarks>
    /// Ogni riga viene scaricata su disco immediatamente. È il contrario di quello
    /// che si farebbe per prestazioni, ed è deliberato: il caso in cui il registro
    /// serve davvero è quello in cui il processo termina di colpo, e le righe rimaste
    /// in memoria sarebbero proprio quelle che raccontano il perché.
    /// </remarks>
    internal void Write(string line)
    {
        lock (_gate)
        {
            if (_disposed) return;

            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);

                if (_writer is null || _openedFor != today)
                {
                    _writer?.Flush();
                    _writer?.Dispose();

                    _writer = new StreamWriter(CurrentFilePath, append: true, Encoding.UTF8)
                    {
                        AutoFlush = true,
                    };

                    _openedFor = today;
                    PurgeOldFiles();
                }

                _writer.WriteLine(line);
            }
            catch (Exception)
            {
                // Il registro non deve mai far cadere cio' che sta registrando.
            }
        }
    }

    private void PurgeOldFiles()
    {
        try
        {
            var limit = DateTime.Now.AddDays(-_retentionDays);

            foreach (var file in Directory.EnumerateFiles(_directory, "blindphone-*.log"))
            {
                if (File.GetLastWriteTime(file) < limit) File.Delete(file);
            }
        }
        catch (Exception)
        {
            // Un file bloccato o un permesso mancante non giustificano un errore.
        }
    }

    /// <summary>Registro di una singola categoria.</summary>
    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var text = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ')
                .Append(Abbreviate(logLevel))
                .Append(' ')
                .Append(Shorten(category))
                .Append("  ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                // L'eccezione si scrive per intero, pila compresa: senza, il registro
                // dice che qualcosa e' andato storto ma non da dove.
                text.AppendLine().Append(exception);
            }

            provider.Write(text.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "AVV",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRI",
            _ => "???",
        };

        /// <summary>Tiene solo l'ultimo segmento del nome della categoria.</summary>
        /// <remarks>
        /// Uno spazio dei nomi completo occupa mezza riga e non aggiunge nulla: chi
        /// legge un registro di BlindPhone sa gia' che i tipi sono di BlindPhone.
        /// </remarks>
        private static string Shorten(string name)
        {
            var last = name.LastIndexOf('.');
            return last >= 0 && last < name.Length - 1 ? name[(last + 1)..] : name;
        }
    }
}
