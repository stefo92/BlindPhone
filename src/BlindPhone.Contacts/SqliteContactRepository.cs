using System.Text.Json;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Contacts;

/// <summary>
/// Archivio locale dei contatti su SQLite.
/// </summary>
/// <remarks>
/// <para>
/// L'archivio locale non è una cache opzionale: è ciò che permette al posto
/// operatore di continuare a funzionare quando il centralino o il server LDAP non
/// rispondono. Un operatore che perde la rubrica perde la capacità di trasferire
/// per nome, cioè il modo in cui lavora davvero: i colleghi si conoscono per
/// cognome, non per numero di interno.
/// </para>
/// <para>
/// <b>Ricerca.</b> Oltre alla corrispondenza testuale c'è la ricerca numerica in
/// stile T9, che consente di digitare "7677" sul tastierino e trovare "Rossi". Su
/// una postazione dove le dita stanno già sul tastierino numerico per comporre i
/// numeri, è più veloce che spostarsi sulla tastiera alfabetica.
/// </para>
/// <para>
/// <b>Il file è cifrato</b> (vedi <see cref="EncryptedSqlite"/>): la rubrica di un
/// ente è un elenco di nomi, ruoli e collocazioni interne, e in chiaro basta copiare
/// il file per portarselo via tutto.
/// </para>
/// </remarks>
public sealed class SqliteContactRepository : IContactRepository, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteContactRepository> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Quante parole della ricerca vengono considerate.
    /// </summary>
    /// <remarks>
    /// Un limite serve perché ogni parola aggiunge una condizione alla query: sei
    /// bastano per qualunque descrizione ricordata a memoria, e chi ne scrive venti
    /// non sta cercando, sta incollando.
    /// </remarks>
    private const int MaxSearchTerms = 6;

    /// <summary>Crea l'archivio e ne assicura lo schema.</summary>
    /// <param name="databasePath">Percorso del file di archivio.</param>
    /// <param name="encryptionKey">Chiave di cifratura dell'archivio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SqliteContactRepository(
        string databasePath,
        string encryptionKey,
        ILogger<SqliteContactRepository>? logger = null)
    {
        _log = logger ?? NullLogger<SqliteContactRepository>.Instance;

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
            CREATE TABLE IF NOT EXISTS contacts (
                id            TEXT PRIMARY KEY,
                first_name    TEXT,
                last_name     TEXT,
                display_name  TEXT,
                phonetic_name TEXT,
                organization  TEXT,
                department    TEXT,
                job_title     TEXT,
                category      TEXT,
                related_to    TEXT,
                building      TEXT,
                floor_name    TEXT,
                email         TEXT,
                notes         TEXT,
                numbers_json  TEXT NOT NULL,
                is_favorite   INTEGER NOT NULL DEFAULT 0,
                speed_dial    INTEGER,
                is_vip        INTEGER NOT NULL DEFAULT 0,
                no_transfer   INTEGER NOT NULL DEFAULT 0,
                source        TEXT NOT NULL,
                presence      INTEGER NOT NULL DEFAULT 0,
                updated_at    TEXT NOT NULL,
                search_text   TEXT NOT NULL,
                search_t9     TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_contacts_search ON contacts(search_text);
            CREATE INDEX IF NOT EXISTS ix_contacts_t9 ON contacts(search_t9);
            CREATE INDEX IF NOT EXISTS ix_contacts_speed ON contacts(speed_dial);

            CREATE TABLE IF NOT EXISTS contact_numbers (
                contact_id  TEXT NOT NULL,
                normalized  TEXT NOT NULL,
                PRIMARY KEY (contact_id, normalized)
            );

            CREATE INDEX IF NOT EXISTS ix_numbers_normalized ON contact_numbers(normalized);
            """;

        command.ExecuteNonQuery();

        MigrateAddColumns();
    }

    /// <summary>
    /// Aggiunge le colonne introdotte dopo la prima versione dello schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite non supporta <c>ADD COLUMN IF NOT EXISTS</c>, quindi si interroga
    /// l'elenco delle colonne esistenti e si aggiungono solo quelle mancanti. È
    /// deliberatamente più prudente di una migrazione basata sul numero di versione:
    /// un archivio creato da una versione intermedia, o riportato da un backup, non
    /// deve impedire l'avvio.
    /// </para>
    /// <para>
    /// Perdere la rubrica significa, per l'operatore, perdere la capacità di
    /// trasferire per nome, cioè il modo in cui lavora davvero.
    /// </para>
    /// </remarks>
    private void MigrateAddColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var query = _connection.CreateCommand())
        {
            query.CommandText = "PRAGMA table_info(contacts)";
            using var reader = query.ExecuteReader();

            while (reader.Read()) existing.Add(reader.GetString(1));
        }

        (string Column, string Type)[] required =
        [
            ("category", "TEXT"),
            ("related_to", "TEXT"),
            ("building", "TEXT"),
            ("floor_name", "TEXT"),
        ];

        foreach (var (column, type) in required)
        {
            if (existing.Contains(column)) continue;

            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE contacts ADD COLUMN {column} {type}";
            alter.ExecuteNonQuery();

            _log.LogInformation("Rubrica: aggiunta la colonna {Column}", column);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Contact>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // OGNI PAROLA E' UNA CONDIZIONE A SE', E DEVONO VALERE TUTTE.
            //
            // Con un solo LIKE sull'intera frase le parole devono essere contigue e
            // nell'ordine esatto: chi cerca "ambulatorio cardiologia" non troverebbe
            // "Ambulatorio di Cardiologia", ne' "Cardiologia - ambulatorio". In una
            // rubrica ospedaliera le voci senza nome si cercano proprio cosi', a
            // pezzi di descrizione ricordati, e l'ordine in cui vengono in mente non
            // e' quello in cui sono scritti.
            //
            // Le parole possono stare in campi diversi — una nella descrizione, una
            // nel reparto — perche' il testo di ricerca li contiene tutti concatenati.
            var termini = query
                .Trim()
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(MaxSearchTerms)
                .ToArray();

            if (termini.Length == 0) return [];

            using var command = _connection.CreateCommand();

            var condizioni = new List<string>(termini.Length);

            for (var i = 0; i < termini.Length; i++)
            {
                // Con un termine tutto numerico si cerca anche in T9: l'operatore che
                // digita sul tastierino sta cercando un nome, non solo un numero.
                condizioni.Add(termini[i].All(char.IsDigit)
                    ? $"(search_text LIKE $p{i} OR search_t9 LIKE $p{i})"
                    : $"search_text LIKE $p{i}");
            }

            command.CommandText = $"""
                SELECT * FROM contacts
                WHERE {string.Join(" AND ", condizioni)}
                ORDER BY is_favorite DESC, last_name, first_name
                LIMIT $limit
                """;

            for (var i = 0; i < termini.Length; i++)
            {
                command.Parameters.AddWithValue($"$p{i}", $"%{termini[i]}%");
            }

            command.Parameters.AddWithValue("$limit", limit);

            return await ReadContactsAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Contact?> ResolveByNumberAsync(string number, CancellationToken cancellationToken)
    {
        var normalized = NormalizeNumber(number);
        if (normalized is null) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT c.* FROM contacts c
                JOIN contact_numbers n ON n.contact_id = c.id
                WHERE n.normalized = $number
                LIMIT 1
                """;

            command.Parameters.AddWithValue("$number", normalized);

            var results = await ReadContactsAsync(command, cancellationToken).ConfigureAwait(false);
            return results.FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Contact?> GetBySpeedDialAsync(int slot, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM contacts WHERE speed_dial = $slot LIMIT 1";
            command.Parameters.AddWithValue("$slot", slot);

            var results = await ReadContactsAsync(command, cancellationToken).ConfigureAwait(false);
            return results.FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> UpsertAsync(
        IEnumerable<Contact> contacts,
        string sourceName,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();
            var written = 0;

            foreach (var contact in contacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteContact(contact, sourceName, transaction);
                written++;
            }

            transaction.Commit();
            _log.LogInformation("Rubrica: {Count} contatti scritti dalla sorgente {Source}", written, sourceName);
            return written;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void WriteContact(Contact contact, string sourceName, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO contacts (
                id, first_name, last_name, display_name, phonetic_name, organization,
                department, job_title, category, related_to, building, floor_name,
                email, notes, numbers_json, is_favorite,
                speed_dial, is_vip, no_transfer, source, presence, updated_at,
                search_text, search_t9)
            VALUES (
                $id, $first, $last, $display, $phonetic, $org,
                $dept, $title, $category, $relatedTo, $building, $floor,
                $email, $notes, $numbers, $fav,
                $speed, $vip, $noTransfer, $source, $presence, $updated,
                $search, $t9)
            ON CONFLICT(id) DO UPDATE SET
                first_name = $first, last_name = $last, display_name = $display,
                phonetic_name = COALESCE(contacts.phonetic_name, $phonetic),
                organization = $org, department = $dept, job_title = $title,
                category = $category, related_to = $relatedTo,
                building = $building, floor_name = $floor,
                email = $email, numbers_json = $numbers, source = $source,
                updated_at = $updated, search_text = $search, search_t9 = $t9
            """;

        // Nota sul COALESCE del nome fonetico: la resa fonetica e' inserita a mano
        // dall'operatore e non arriva da nessuna sorgente esterna. Una
        // sincronizzazione che la sovrascrivesse cancellerebbe un lavoro fatto a
        // mano, contatto per contatto, che nessuno rifarebbe.
        var searchText = BuildSearchText(contact);

        command.Parameters.AddWithValue("$id", contact.Id.ToString());
        command.Parameters.AddWithValue("$first", (object?)contact.FirstName ?? DBNull.Value);
        command.Parameters.AddWithValue("$last", (object?)contact.LastName ?? DBNull.Value);
        command.Parameters.AddWithValue("$display", (object?)contact.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$phonetic", (object?)contact.PhoneticName ?? DBNull.Value);
        command.Parameters.AddWithValue("$org", (object?)contact.Organization ?? DBNull.Value);
        command.Parameters.AddWithValue("$dept", (object?)contact.Department ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)contact.JobTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)contact.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$relatedTo", (object?)contact.RelatedTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$building", (object?)contact.Building ?? DBNull.Value);
        command.Parameters.AddWithValue("$floor", (object?)contact.Floor ?? DBNull.Value);
        command.Parameters.AddWithValue("$email", (object?)contact.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)contact.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$numbers", JsonSerializer.Serialize(contact.Numbers));
        command.Parameters.AddWithValue("$fav", contact.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$speed", (object?)contact.SpeedDialSlot ?? DBNull.Value);
        command.Parameters.AddWithValue("$vip", contact.IsVip ? 1 : 0);
        command.Parameters.AddWithValue("$noTransfer", contact.DoNotTransfer ? 1 : 0);
        command.Parameters.AddWithValue("$source", sourceName);
        command.Parameters.AddWithValue("$presence", (int)contact.Presence);
        command.Parameters.AddWithValue("$updated", contact.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$search", searchText);
        command.Parameters.AddWithValue("$t9", ToT9(searchText));

        command.ExecuteNonQuery();

        WriteNumbers(contact, transaction);
    }

    private void WriteNumbers(Contact contact, SqliteTransaction transaction)
    {
        using var delete = _connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM contact_numbers WHERE contact_id = $id";
        delete.Parameters.AddWithValue("$id", contact.Id.ToString());
        delete.ExecuteNonQuery();

        foreach (var number in contact.Numbers)
        {
            var normalized = NormalizeNumber(number.Number);
            if (normalized is null) continue;

            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO contact_numbers (contact_id, normalized)
                VALUES ($id, $number)
                """;

            insert.Parameters.AddWithValue("$id", contact.Id.ToString());
            insert.Parameters.AddWithValue("$number", normalized);
            insert.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public async Task UpdatePresenceAsync(
        string extension,
        PresenceState presence,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeNumber(extension);
        if (normalized is null) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE contacts SET presence = $presence
                WHERE id IN (SELECT contact_id FROM contact_numbers WHERE normalized = $number)
                """;

            command.Parameters.AddWithValue("$presence", (int)presence);
            command.Parameters.AddWithValue("$number", normalized);
            command.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> ClearAsync(string? sourceName, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();

            // I numeri vanno rimossi per primi: sono la tabella che alimenta la
            // risoluzione del chiamante, e lasciarne di orfani farebbe annunciare
            // nomi di contatti non piu' esistenti.
            using (var deleteNumbers = _connection.CreateCommand())
            {
                deleteNumbers.Transaction = transaction;
                deleteNumbers.CommandText = sourceName is null
                    ? "DELETE FROM contact_numbers"
                    : """
                      DELETE FROM contact_numbers
                      WHERE contact_id IN (SELECT id FROM contacts WHERE source = $source)
                      """;

                if (sourceName is not null) deleteNumbers.Parameters.AddWithValue("$source", sourceName);
                deleteNumbers.ExecuteNonQuery();
            }

            int removed;

            using (var deleteContacts = _connection.CreateCommand())
            {
                deleteContacts.Transaction = transaction;
                deleteContacts.CommandText = sourceName is null
                    ? "DELETE FROM contacts"
                    : "DELETE FROM contacts WHERE source = $source";

                if (sourceName is not null) deleteContacts.Parameters.AddWithValue("$source", sourceName);
                removed = deleteContacts.ExecuteNonQuery();
            }

            transaction.Commit();

            _log.LogInformation(
                "Rubrica: rimossi {Count} contatti{Source}",
                removed, sourceName is null ? string.Empty : $" della sorgente {sourceName}");

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM contacts";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<IReadOnlyList<Contact>> ReadContactsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<Contact>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var numbersJson = reader.GetString(reader.GetOrdinal("numbers_json"));
            var numbers = JsonSerializer.Deserialize<List<PhoneNumber>>(numbersJson) ?? [];

            results.Add(new Contact
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                FirstName = GetNullableString(reader, "first_name"),
                LastName = GetNullableString(reader, "last_name"),
                DisplayName = GetNullableString(reader, "display_name"),
                PhoneticName = GetNullableString(reader, "phonetic_name"),
                Organization = GetNullableString(reader, "organization"),
                Department = GetNullableString(reader, "department"),
                JobTitle = GetNullableString(reader, "job_title"),
                Category = GetNullableString(reader, "category"),
                RelatedTo = GetNullableString(reader, "related_to"),
                Building = GetNullableString(reader, "building"),
                Floor = GetNullableString(reader, "floor_name"),
                Email = GetNullableString(reader, "email"),
                Notes = GetNullableString(reader, "notes"),
                Numbers = numbers,
                IsFavorite = reader.GetInt32(reader.GetOrdinal("is_favorite")) == 1,
                SpeedDialSlot = reader.IsDBNull(reader.GetOrdinal("speed_dial"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("speed_dial")),
                IsVip = reader.GetInt32(reader.GetOrdinal("is_vip")) == 1,
                DoNotTransfer = reader.GetInt32(reader.GetOrdinal("no_transfer")) == 1,
                Source = reader.GetString(reader.GetOrdinal("source")),
                Presence = (PresenceState)reader.GetInt32(reader.GetOrdinal("presence")),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            });
        }

        return results;
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>
    /// Compone il testo su cui opera la ricerca incrementale.
    /// </summary>
    /// <remarks>
    /// Include anche edificio e piano. In una struttura complessa l'operatore
    /// riceve di continuo domande di collocazione — "chi c'è al padiglione B?" — e
    /// poterle risolvere digitando "padiglione b" evita di dover ricordare a memoria
    /// la mappa dell'ente.
    /// </remarks>
    private static string BuildSearchText(Contact contact)
    {
        var parts = new[]
        {
            contact.FirstName, contact.LastName, contact.DisplayName,
            contact.Organization, contact.Department, contact.JobTitle,
            contact.Category, contact.RelatedTo, contact.Building, contact.Floor,
            string.Join(' ', contact.Numbers.Select(n => n.Number)),
        };

        return string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p))).ToLowerInvariant();
    }

    /// <summary>
    /// Converte un testo nella sequenza di tasti T9 corrispondente.
    /// </summary>
    /// <remarks>
    /// Permette di cercare un nome digitando sul tastierino numerico: "7677" trova
    /// "Rossi". Su una postazione dove le dita sono già sui numeri per comporre, è
    /// più rapido che spostarsi sulla tastiera alfabetica e tornare indietro.
    /// </remarks>
    private static string ToT9(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
        {
            var digit = char.ToLowerInvariant(c) switch
            {
                >= 'a' and <= 'c' => '2',
                >= 'd' and <= 'f' => '3',
                >= 'g' and <= 'i' => '4',
                >= 'j' and <= 'l' => '5',
                >= 'm' and <= 'o' => '6',
                >= 'p' and <= 's' => '7',
                >= 't' and <= 'v' => '8',
                >= 'w' and <= 'z' => '9',
                >= '0' and <= '9' => c,
                _ => '\0',
            };

            if (digit != '\0') result.Append(digit);
        }

        return result.ToString();
    }

    /// <summary>
    /// Riduce un numero alla forma confrontabile: sole cifre, ultime nove.
    /// </summary>
    /// <remarks>
    /// Il confronto sulle ultime cifre permette di riconoscere lo stesso contatto
    /// indipendentemente dal prefisso con cui si presenta: interno, numero diretto
    /// o formato internazionale. Nove cifre rendono improbabile una collisione senza
    /// far fallire il confronto sugli interni brevi.
    /// </remarks>
    private static string? NormalizeNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;

        var digits = new string([.. number.Where(char.IsDigit)]);
        if (digits.Length == 0) return null;

        return digits.Length <= 9 ? digits : digits[^9..];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        _connection.Dispose();
    }
}
