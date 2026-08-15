using BlindPhone.Contacts.SqlBackup;
using Microsoft.Data.SqlClient;

namespace BlindPhone.Contacts.SqlServer;

/// <summary>
/// Legge tabelle e viste da Microsoft SQL Server, in sola lettura.
/// </summary>
/// <remarks>
/// <para>
/// <b>Espone la stessa interfaccia del lettore dei backup</b>, e non è una comodità:
/// significa che la procedura guidata — elenco delle tabelle, scelta, abbinamento
/// delle colonne, anteprima — è letteralmente la stessa. Chi ha imparato a importare
/// un backup sa già collegare un server, e una funzione che compare in una sola delle
/// due procedure non può esistere per costruzione.
/// </para>
/// <para>
/// <b>BlindPhone non scrive mai nel database dell'ente.</b> Le uniche istruzioni che
/// invia sono <c>SELECT</c> sulla vista di sistema delle tabelle e sulla tabella
/// scelta. Il nome della tabella non viene mai concatenato senza essere prima
/// verificato contro l'elenco letto dal server e virgolettato.
/// </para>
/// </remarks>
public sealed class SqlServerReader : ISqlBackupReader
{
    private readonly SqlConnection _connection;

    /// <summary>Apre il collegamento e legge l'elenco delle tabelle.</summary>
    /// <param name="options">Parametri del collegamento.</param>
    /// <param name="password">Password dal deposito protetto, se serve.</param>
    public SqlServerReader(SqlServerOptions options, string? password)
    {
        _connection = new SqlConnection(options.BuildConnectionString(password));
        _connection.Open();

        Tables = LeggiTabelle();
    }

    /// <inheritdoc />
    public string FormatDescription => "Microsoft SQL Server";

    /// <inheritdoc />
    public IReadOnlyList<SqlTableInfo> Tables { get; }

    /// <inheritdoc />
    public IEnumerable<IReadOnlyDictionary<string, string?>> ReadRows(
        string table,
        int? limit,
        CancellationToken cancellationToken)
    {
        // La tabella dev'essere una di quelle lette dal server: non si costruisce una
        // query su un nome arrivato da fuori, nemmeno virgolettato.
        var scelta = Tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        if (scelta is null) yield break;

        using var comando = _connection.CreateCommand();
        comando.CommandText = limit is { } n
            ? $"SELECT TOP ({n}) * FROM {QuoteName(scelta.Name)}"
            : $"SELECT * FROM {QuoteName(scelta.Name)}";

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
            // Tabelle e viste insieme: negli enti la rubrica pubblicata ai sistemi
            // esterni e' quasi sempre una vista, perche' il gestionale non espone le
            // tabelle vere.
            comando.CommandText = """
                SELECT TABLE_SCHEMA, TABLE_NAME
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
                ORDER BY TABLE_SCHEMA, TABLE_NAME
                """;

            using var lettore = comando.ExecuteReader();
            while (lettore.Read()) nomi.Add($"{lettore.GetString(0)}.{lettore.GetString(1)}");
        }

        var colonne = LeggiColonne();

        return [.. nomi
            .Where(colonne.ContainsKey)
            .Select(nome => new SqlTableInfo(nome, colonne[nome], ContaRighe(nome)))];
    }

    private Dictionary<string, List<string>> LeggiColonne()
    {
        var risultato = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        using var comando = _connection.CreateCommand();
        comando.CommandText = """
            SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION
            """;

        using var lettore = comando.ExecuteReader();

        while (lettore.Read())
        {
            var chiave = $"{lettore.GetString(0)}.{lettore.GetString(1)}";

            if (!risultato.TryGetValue(chiave, out var elenco))
            {
                elenco = [];
                risultato[chiave] = elenco;
            }

            elenco.Add(lettore.GetString(2));
        }

        return risultato;
    }

    /// <summary>
    /// Conta le righe, senza pretendere di riuscirci.
    /// </summary>
    /// <remarks>
    /// Il conteggio serve solo a riconoscere la rubrica nell'elenco. Una vista
    /// costosa o una tabella su cui l'utenza non ha permesso non devono impedire di
    /// vedere tutte le altre, quindi il fallimento vale «non so quante righe».
    /// </remarks>
    private int? ContaRighe(string table)
    {
        try
        {
            using var comando = _connection.CreateCommand();
            comando.CommandText = $"SELECT COUNT_BIG(*) FROM {QuoteName(table)}";
            comando.CommandTimeout = 10;

            return Convert.ToInt32(comando.ExecuteScalar());
        }
        catch (SqlException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>Virgoletta un nome qualificato secondo le regole di SQL Server.</summary>
    /// <param name="qualified">Nome nella forma <c>schema.tabella</c>.</param>
    private static string QuoteName(string qualified)
    {
        var punto = qualified.IndexOf('.');

        if (punto < 0) return Bracket(qualified);

        return $"{Bracket(qualified[..punto])}.{Bracket(qualified[(punto + 1)..])}";

        static string Bracket(string part) => $"[{part.Replace("]", "]]")}]";
    }

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();
}
