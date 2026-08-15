using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Contacts;

/// <summary>
/// Archivio delle ultime chiamate su SQLite.
/// </summary>
/// <remarks>
/// <para>
/// File separato da quello della rubrica. La rubrica è un elenco che si conserva; il
/// registro delle chiamate contiene dati di traffico, con un periodo di conservazione
/// che l'operatore sceglie e che l'ente deve poter dimostrare. Due file distinti
/// significano che svuotare l'uno non tocca l'altro, e che chi verifica la conformità
/// ne guarda uno solo.
/// </para>
/// <para>
/// Le operazioni sono serializzate come nella rubrica: l'archivio viene scritto dal
/// servizio che registra le chiamate e letto dalla finestra dell'elenco, e SQLite non
/// gradisce due scritture concorrenti sullo stesso file.
/// </para>
/// <para>
/// <b>Il file è cifrato</b> (vedi <see cref="EncryptedSqlite"/>): sono dati di
/// traffico telefonico, e su una postazione smarrita direbbero a chiunque chi ha
/// chiamato l'ente e a che ora.
/// </para>
/// </remarks>
public sealed class SqliteCallHistoryRepository : ICallHistoryRepository, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteCallHistoryRepository> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Crea l'archivio e ne assicura lo schema.</summary>
    /// <param name="databasePath">Percorso del file di archivio.</param>
    /// <param name="encryptionKey">Chiave di cifratura dell'archivio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SqliteCallHistoryRepository(
        string databasePath,
        string encryptionKey,
        ILogger<SqliteCallHistoryRepository>? logger = null)
    {
        _log = logger ?? NullLogger<SqliteCallHistoryRepository>.Instance;

        (_connection, IsEncrypted, WasReset) = EncryptedSqlite.Open(databasePath, encryptionKey, _log);

        EnsureSchema();
    }

    /// <summary>Dice se l'archivio su disco è cifrato.</summary>
    public bool IsEncrypted { get; }

    /// <summary>Dice se l'archivio è ripartito vuoto perché la chiave non lo apriva più.</summary>
    public bool WasReset { get; }

    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS call_history (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                kind          INTEGER NOT NULL,
                number        TEXT NOT NULL,
                display_name  TEXT,
                started_at    TEXT NOT NULL,
                duration_ms   INTEGER NOT NULL,
                queue_name    TEXT,
                end_reason    INTEGER
            );

            CREATE INDEX IF NOT EXISTS ix_history_kind ON call_history(kind, started_at DESC);
            CREATE INDEX IF NOT EXISTS ix_history_started ON call_history(started_at);
            """;

        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public async Task AddAsync(CallHistoryEntry entry, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO call_history (kind, number, display_name, started_at, duration_ms, queue_name, end_reason)
                VALUES ($kind, $number, $name, $started, $duration, $queue, $reason);
                """;

            command.Parameters.AddWithValue("$kind", (int)entry.Kind);
            command.Parameters.AddWithValue("$number", entry.Number);
            command.Parameters.AddWithValue("$name", (object?)entry.DisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("$started", entry.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$duration", (long)entry.Duration.TotalMilliseconds);
            command.Parameters.AddWithValue("$queue", (object?)entry.QueueName ?? DBNull.Value);
            command.Parameters.AddWithValue("$reason", entry.EndReason is null ? DBNull.Value : (int)entry.EndReason);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Un registro che non si riesce a scrivere non deve far cadere la chiamata
            // che stava registrando: si perde una riga di storico, non il servizio.
            _log.LogWarning(ex, "Registrazione della chiamata nell'elenco non riuscita");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CallHistoryEntry>> GetAsync(
        CallHistoryKind kind,
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, kind, number, display_name, started_at, duration_ms, queue_name, end_reason
                FROM call_history
                WHERE kind = $kind
                ORDER BY started_at DESC
                LIMIT $limit;
                """;

            command.Parameters.AddWithValue("$kind", (int)kind);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));

            var results = new List<CallHistoryEntry>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(Read(reader));
            }

            return results;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lettura dell'elenco delle chiamate non riuscita");
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CallHistoryKind? kind, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = _connection.CreateCommand();

            if (kind is { } tipo)
            {
                command.CommandText = "SELECT COUNT(*) FROM call_history WHERE kind = $kind;";
                command.Parameters.AddWithValue("$kind", (int)tipo);
            }
            else
            {
                command.CommandText = "SELECT COUNT(*) FROM call_history;";
            }

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(result);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Conteggio delle chiamate non riuscito");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> ClearAsync(CallHistoryKind? kind, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = _connection.CreateCommand();

            if (kind is null)
            {
                command.CommandText = "DELETE FROM call_history;";
            }
            else
            {
                command.CommandText = "DELETE FROM call_history WHERE kind = $kind;";
                command.Parameters.AddWithValue("$kind", (int)kind);
            }

            var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _log.LogInformation("Elenco delle chiamate svuotato: {Count} voci eliminate", removed);
            return removed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Svuotamento dell'elenco delle chiamate non riuscito");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> PurgeAsync(TimeSpan retention, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Conservazione illimitata: e' una scelta legittima quando l'ente ha una
        // propria procedura, e va rispettata alla lettera. Non si cancella nulla.
        if (retention <= TimeSpan.Zero) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM call_history WHERE started_at < $limite;";
            command.Parameters.AddWithValue("$limite", (now - retention).ToString("O"));

            var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (removed > 0)
            {
                _log.LogInformation(
                    "Elenco delle chiamate: {Count} voci oltre i {Giorni} giorni di conservazione eliminate",
                    removed, retention.TotalDays);
            }

            return removed;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pulizia dell'elenco delle chiamate non riuscita");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CallHistoryEntry Read(SqliteDataReader reader)
    {
        var reasonOrdinal = reader.GetOrdinal("end_reason");

        return new CallHistoryEntry(
            reader.GetInt64(reader.GetOrdinal("id")),
            (CallHistoryKind)reader.GetInt32(reader.GetOrdinal("kind")),
            reader.GetString(reader.GetOrdinal("number")),
            reader.IsDBNull(reader.GetOrdinal("display_name")) ? null : reader.GetString(reader.GetOrdinal("display_name")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
            TimeSpan.FromMilliseconds(reader.GetInt64(reader.GetOrdinal("duration_ms"))),
            reader.IsDBNull(reader.GetOrdinal("queue_name")) ? null : reader.GetString(reader.GetOrdinal("queue_name")),
            reader.IsDBNull(reasonOrdinal) ? null : (CallEndReason)reader.GetInt32(reasonOrdinal));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}
