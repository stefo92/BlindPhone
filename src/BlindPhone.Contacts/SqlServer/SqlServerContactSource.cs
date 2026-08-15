using BlindPhone.Contacts.SqlBackup;
using BlindPhone.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Contacts.SqlServer;

/// <summary>
/// Legge la rubrica da una tabella o vista di Microsoft SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il server è la fonte, non l'archivio di lavoro.</b> I contatti letti finiscono
/// nella copia locale cifrata, che resta ciò che l'operatore interroga durante il
/// turno. La ragione è operativa: il nome del chiamante viene cercato mentre il
/// telefono squilla, e un giro di rete in quel momento — o una rete che non c'è —
/// lascerebbe l'operatore senza sapere chi sta chiamando proprio quando deve
/// decidere. Preferiti, numeri rapidi e stato degli interni restano locali e
/// personali per lo stesso motivo.
/// </para>
/// </remarks>
public sealed class SqlServerContactSource : TabularContactSource
{
    private readonly SqlServerOptions _options;
    private readonly string? _password;
    private readonly ILogger<SqlServerContactSource> _log;

    /// <summary>Crea la sorgente.</summary>
    /// <param name="options">Parametri del collegamento.</param>
    /// <param name="password">Password dal deposito protetto, se serve.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SqlServerContactSource(
        SqlServerOptions options,
        string? password,
        ILogger<SqlServerContactSource>? logger = null)
        : base(options.Table, options.Mapping, "mssql", logger)
    {
        _options = options;
        _password = password;
        _log = logger ?? NullLogger<SqlServerContactSource>.Instance;
    }

    /// <inheritdoc />
    public override string Name => $"SQL Server: {_options.Database} ({_options.Table})";

    /// <inheritdoc />
    protected override ISqlBackupReader OpenReader() => new SqlServerReader(_options, _password);

    /// <inheritdoc />
    public override Task<SourceTestResult> TestAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
            return Task.FromResult(new SourceTestResult(false, "Server e banca dati non sono ancora indicati"));

        try
        {
            using var lettore = new SqlServerReader(_options, _password);

            if (string.IsNullOrWhiteSpace(_options.Table))
            {
                return Task.FromResult(new SourceTestResult(
                    true,
                    $"Collegamento riuscito: {lettore.Tables.Count} fra tabelle e viste. Scegli quella della rubrica",
                    null));
            }

            var tabella = lettore.Tables.FirstOrDefault(
                t => string.Equals(t.Name, _options.Table, StringComparison.OrdinalIgnoreCase));

            if (tabella is null)
            {
                return Task.FromResult(new SourceTestResult(
                    false, $"La tabella {_options.Table} non esiste su questo server"));
            }

            var contatti = Read(limit: null, cancellationToken);

            return Task.FromResult(new SourceTestResult(
                true, $"Collegamento riuscito: {contatti.Count} contatti in {_options.Table}", contatti.Count));
        }
        catch (SqlException ex)
        {
            _log.LogWarning(ex, "Collegamento a {Server} non riuscito", _options.Describe());
            return Task.FromResult(new SourceTestResult(false, DescribeSqlFailure(ex)));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Collegamento a {Server} non riuscito", _options.Describe());
            return Task.FromResult(new SourceTestResult(false, "Collegamento al server non riuscito"));
        }
    }

    /// <summary>
    /// Traduce l'errore di SQL Server in una frase che dice cosa fare.
    /// </summary>
    /// <param name="exception">Errore restituito dal server.</param>
    /// <remarks>
    /// I numeri di errore di SQL Server sono stabili e documentati, ed è quello che
    /// li rende traducibili. «Login failed for user» non dice niente a chi lo ascolta
    /// una volta sola; «l'utenza è stata rifiutata dal server» gli permette di
    /// chiamare l'amministratore sapendo cosa riferire.
    /// </remarks>
    public static string DescribeSqlFailure(SqlException exception) => DescribeSqlFailure(exception.Number);

    /// <summary>
    /// Traduce il numero di errore di SQL Server in una frase che dice cosa fare.
    /// </summary>
    /// <param name="number">Numero di errore restituito dal server.</param>
    /// <remarks>
    /// Prende il numero e non l'eccezione perché <see cref="SqlException"/> non ha
    /// costruttori pubblici: senza questo passaggio la traduzione non sarebbe
    /// verificabile con un test, e un test che ne riscrivesse la tabella
    /// verificherebbe la propria copia invece del programma.
    /// </remarks>
    public static string DescribeSqlFailure(int number) => number switch
    {
        18456 => "Il server ha rifiutato l'utenza: nome o password non validi",
        4060 => "L'utenza non ha accesso a questa banca dati",
        229 or 230 => "L'utenza non ha il permesso di leggere questa tabella",
        2 or 53 or 11001 => "Il server non risponde: verifica il nome e che sia raggiungibile dalla rete",
        -2 => "Il server non ha risposto entro il tempo previsto",

        // Il certificato non verificabile e' il caso piu' comune sulle installazioni
        // interne, ed e' l'unico in cui la mossa successiva sta nelle impostazioni.
        -2146893019 or 20 => "Il certificato del server non e' verificabile. Se la rete e' quella "
                             + "dell'ente, attiva l'opzione che accetta il certificato del server",

        _ => $"Il server ha risposto con l'errore {number}",
    };
}
