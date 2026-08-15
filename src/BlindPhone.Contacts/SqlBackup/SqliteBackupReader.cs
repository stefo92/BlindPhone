using Microsoft.Data.Sqlite;

namespace BlindPhone.Contacts.SqlBackup;

/// <summary>
/// Legge una banca dati SQLite aperta in sola lettura.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il file viene aperto in sola lettura e non viene mai modificato.</b> È un
/// backup: se l'apertura lo toccasse — anche solo per il giornale delle transazioni
/// — la copia non sarebbe più identica all'originale, e chi l'ha consegnata avrebbe
/// ragione a non fidarsi più di quello che le abbiamo fatto.
/// </para>
/// <para>
/// <b>I nomi di tabella arrivano dal file, quindi vanno virgolettati.</b> Una tabella
/// che si chiama <c>x"; DROP TABLE y; --</c> è legale in SQLite, e concatenarla in una
/// query la trasformerebbe in un comando. Qui ogni nome passa da
/// <see cref="QuoteIdentifier"/> prima di entrare in una istruzione.
/// </para>
/// </remarks>
public sealed class SqliteBackupReader : ISqlBackupReader
{
    private readonly SqliteConnection _connection;

    /// <summary>Apre la banca dati.</summary>
    /// <param name="path">Percorso del file SQLite.</param>
    public SqliteBackupReader(string path)
    {
        var stringa = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        _connection = new SqliteConnection(stringa);
        _connection.Open();

        Tables = LeggiTabelle();
    }

    /// <inheritdoc />
    public string FormatDescription => "Banca dati SQLite";

    /// <inheritdoc />
    public IReadOnlyList<SqlTableInfo> Tables { get; }

    /// <inheritdoc />
    public IEnumerable<IReadOnlyDictionary<string, string?>> ReadRows(
        string table,
        int? limit,
        CancellationToken cancellationToken)
    {
        // La tabella deve essere una di quelle trovate: non si costruisce una query
        // su un nome arbitrario arrivato da fuori, nemmeno virgolettato.
        if (!Tables.Any(t => string.Equals(t.Name, table, StringComparison.Ordinal)))
            yield break;

        using var comando = _connection.CreateCommand();
        comando.CommandText = limit is { } n
            ? $"SELECT * FROM {QuoteIdentifier(table)} LIMIT {n}"
            : $"SELECT * FROM {QuoteIdentifier(table)}";

        using var lettore = comando.ExecuteReader();

        while (lettore.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var riga = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < lettore.FieldCount; i++)
            {
                riga[lettore.GetName(i)] = lettore.IsDBNull(i) ? null : lettore.GetValue(i)?.ToString();
            }

            yield return riga;
        }
    }

    private List<SqlTableInfo> LeggiTabelle()
    {
        var nomi = new List<string>();

        using (var comando = _connection.CreateCommand())
        {
            // Si escludono le tabelle di servizio di SQLite: non contengono dati
            // dell'ente e nell'elenco da scegliere sarebbero solo rumore.
            comando.CommandText =
                "SELECT name FROM sqlite_master WHERE type IN ('table','view') "
                + "AND name NOT LIKE 'sqlite_%' ORDER BY name";

            using var lettore = comando.ExecuteReader();
            while (lettore.Read()) nomi.Add(lettore.GetString(0));
        }

        var tabelle = new List<SqlTableInfo>(nomi.Count);

        foreach (var nome in nomi)
        {
            tabelle.Add(new SqlTableInfo(nome, LeggiColonne(nome), ContaRighe(nome)));
        }

        return tabelle;
    }

    private List<string> LeggiColonne(string tabella)
    {
        var colonne = new List<string>();

        using var comando = _connection.CreateCommand();
        comando.CommandText = $"SELECT * FROM {QuoteIdentifier(tabella)} LIMIT 0";

        using var lettore = comando.ExecuteReader();
        for (var i = 0; i < lettore.FieldCount; i++) colonne.Add(lettore.GetName(i));

        return colonne;
    }

    private int? ContaRighe(string tabella)
    {
        try
        {
            using var comando = _connection.CreateCommand();
            comando.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(tabella)}";

            return Convert.ToInt32(comando.ExecuteScalar());
        }
        catch (SqliteException)
        {
            // Una vista può appoggiarsi a una tabella assente in un backup parziale:
            // non poterla contare non è un motivo per nascondere tutte le altre.
            return null;
        }
    }

    /// <summary>Virgoletta un identificativo secondo le regole di SQLite.</summary>
    /// <param name="name">Nome di tabella o colonna.</param>
    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();
}
