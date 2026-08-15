using BlindPhone.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BlindPhone.Contacts.SqlBackup;

/// <summary>
/// Importa contatti da una tabella scelta dentro un backup di banca dati.
/// </summary>
/// <remarks>
/// <para>
/// <b>La tabella la sceglie l'operatore, e non c'è alternativa.</b> Un backup di
/// gestionale contiene decine di tabelle e nessuna convenzione dice quale sia la
/// rubrica: indovinarla significherebbe importare a caso lo storico delle modifiche o
/// una tabella di servizio. La procedura è la stessa del CSV con un passo in più —
/// file, <i>tabella</i>, colonne, anteprima — e l'anteprima resta obbligatoria.
/// </para>
/// <para>
/// La mappatura riusa <see cref="CsvColumnMapping"/> e non un tipo parallelo: i campi
/// da riempire sono gli stessi, e due tipi identici che divergono nel tempo sono il
/// modo più sicuro per far comparire una funzione in una sola delle due procedure.
/// </para>
/// </remarks>
public sealed class SqlBackupContactSource : TabularContactSource
{
    private readonly string _path;
    private readonly ILogger<SqlBackupContactSource> _log;

    /// <summary>Crea la sorgente.</summary>
    /// <param name="path">Percorso del backup.</param>
    /// <param name="table">Tabella da importare.</param>
    /// <param name="mapping">Corrispondenza fra colonne e campi del contatto.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SqlBackupContactSource(
        string path,
        string table,
        CsvColumnMapping mapping,
        ILogger<SqlBackupContactSource>? logger = null)
        : base(table, mapping, "sql", logger)
    {
        _path = path;
        _log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlBackupContactSource>.Instance;
    }

    /// <inheritdoc />
    public override string Name => $"Backup SQL: {Path.GetFileName(_path)} ({Table})";

    /// <inheritdoc />
    protected override ISqlBackupReader OpenReader() => SqlBackupFile.Open(_path);

    /// <inheritdoc />
    public override Task<SourceTestResult> TestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return Task.FromResult(new SourceTestResult(false, "File di backup non trovato"));

        try
        {
            using var lettore = SqlBackupFile.Open(_path);

            var tabella = lettore.Tables.FirstOrDefault(
                t => string.Equals(t.Name, Table, StringComparison.OrdinalIgnoreCase));

            if (tabella is null)
            {
                return Task.FromResult(new SourceTestResult(
                    false, $"La tabella {Table} non esiste in questo backup"));
            }

            var contatti = Read(limit: null, cancellationToken);

            return Task.FromResult(new SourceTestResult(
                true,
                $"{lettore.FormatDescription}: {contatti.Count} contatti nella tabella {Table}",
                contatti.Count));
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(new SourceTestResult(false, ex.Message));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lettura del backup {Percorso} non riuscita", _path);
            return Task.FromResult(new SourceTestResult(false, ContactFileErrors.Describe(ex)));
        }
    }
}
