using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BlindPhone.Contacts;

/// <summary>
/// Apre un archivio SQLite cifrato con SQLCipher, migrando quelli nati in chiaro.
/// </summary>
/// <remarks>
/// <para>
/// I due archivi locali di BlindPhone — la rubrica dell'ente e il registro delle
/// chiamate — stanno su una postazione che può essere rubata, prestata o dismessa.
/// Il registro in particolare è composto da dati di traffico telefonico: chi ha
/// chiamato chi, e quando. In chiaro sono leggibili da chiunque apra il file con un
/// visualizzatore SQLite, senza password e senza lasciare traccia.
/// </para>
/// <para>
/// La cifratura è dell'intero file, non dei singoli campi: anche i nomi delle
/// tabelle e degli indici sono illeggibili, e non resta un'intestazione da cui
/// dedurre di che archivio si tratti.
/// </para>
/// </remarks>
public static class EncryptedSqlite
{
    /// <summary>Intestazione di un file SQLite non cifrato.</summary>
    /// <remarks>
    /// Un file SQLCipher comincia invece con il sale di derivazione della chiave,
    /// cioè con byte casuali: la distinzione fra i due casi è certa e costa una
    /// lettura di sedici byte.
    /// </remarks>
    private static readonly byte[] PlaintextHeader = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// Apre l'archivio, cifrandolo se era rimasto in chiaro da una versione precedente.
    /// </summary>
    /// <param name="databasePath">Percorso del file di archivio.</param>
    /// <param name="key">Chiave di cifratura; vuota apre in chiaro (solo per i test).</param>
    /// <param name="log">Log diagnostico.</param>
    /// <returns>La connessione aperta, se l'archivio è cifrato e se è stato azzerato.</returns>
    /// <remarks>
    /// I due valori restituiti non sono decorativi: descrivono i due modi in cui
    /// l'apertura può riuscire lo stesso ma non essere andata bene — l'archivio
    /// rimasto in chiaro perché la conversione non è riuscita, e l'archivio ripartito
    /// vuoto perché la chiave non lo apriva più. In entrambi i casi il posto operatore
    /// continua a funzionare, perché un centralinista senza telefono non lavora, ma il
    /// fatto deve essere <b>visibile</b> — nel log e nel pannello di diagnostica —
    /// invece di somigliare a un avvio normale.
    /// </remarks>
    public static (SqliteConnection Connection, bool Encrypted, bool Reset) Open(
        string databasePath,
        string key,
        ILogger log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        if (string.IsNullOrEmpty(key))
        {
            var plain = new SqliteConnection(PlainConnectionString(databasePath));
            plain.Open();

            return (plain, false, false);
        }

        if (IsPlaintext(databasePath))
        {
            if (!TryEncryptInPlace(databasePath, key, log))
            {
                log.LogError(
                    "Archivio {File} rimasto IN CHIARO: la cifratura non e' riuscita e verra' ritentata al prossimo avvio",
                    Path.GetFileName(databasePath));

                var degradato = new SqliteConnection(PlainConnectionString(databasePath));
                degradato.Open();

                return (degradato, false, false);
            }
        }

        if (TryOpenWithKey(databasePath, key, out var connection)) return (connection, true, false);

        // LA CHIAVE NON APRE PIU' L'ARCHIVIO.
        //
        // Succede quando secrets.dat va perso o il profilo Windows viene ricreato:
        // DPAPI e' legato all'utente e alla macchina, quindi la chiave sparisce e con
        // essa il contenuto, che non e' recuperabile da nessuno.
        //
        // Qui non si puo' fermare l'avvio. Questo e' il telefono di un centralinista
        // che non vede: una finestra di errore al posto della schermata di lavoro lo
        // lascerebbe senza modo di rispondere, e senza modo di capire perche'. Si
        // riparte quindi da un archivio vuoto — la rubrica si reimporta dal
        // centralino, il registro delle chiamate purtroppo no.
        //
        // Il file illeggibile viene spostato, non cancellato: se domani secrets.dat
        // torna da un backup, quei dati tornano con lui.
        var scartato = $"{databasePath}.non-leggibile-{DateTime.Now:yyyyMMdd-HHmmss}";

        ReleaseLeakedHandles();
        File.Move(databasePath, scartato);

        log.LogError(
            "Archivio {File} non apribile con la chiave di questa postazione: spostato in {Scartato} e ricominciato vuoto. "
            + "Il contenuto e' recuperabile solo ripristinando il deposito dei segreti che lo ha cifrato",
            Path.GetFileName(databasePath), Path.GetFileName(scartato));

        var nuovo = new SqliteConnection(ConnectionString(databasePath, key));
        nuovo.Open();

        return (nuovo, true, true);
    }

    /// <summary>
    /// Libera gli handle di file lasciati aperti da un tentativo di apertura fallito.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non è una precauzione generica ed è stato verificato in laboratorio: quando
    /// SQLCipher rifiuta la chiave, la connessione resta agganciata al file anche dopo
    /// <c>Dispose</c> e <c>Close</c>, e a rilasciarlo è solo il finalizzatore. Senza
    /// questa chiamata lo spostamento del file fallisce ogni volta.
    /// </para>
    /// <para>
    /// Forzare la garbage collection è una cosa che di norma non si fa. Qui il codice
    /// passa una volta sola, all'avvio, e soltanto quando l'archivio è già
    /// irrecuperabile: l'alternativa è lasciare il posto operatore senza rubrica a ogni
    /// avvio successivo, perché il file bloccato non si sposta mai.
    /// </para>
    /// </remarks>
    private static void ReleaseLeakedHandles()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Prova ad aprire l'archivio con la chiave, verificando che decifri davvero.</summary>
    private static bool TryOpenWithKey(string databasePath, string key, out SqliteConnection connection)
    {
        connection = new SqliteConnection(ConnectionString(databasePath, key));

        try
        {
            connection.Open();

            // Open() non decifra nulla: SQLCipher verifica la chiave alla prima
            // lettura vera. Senza questa interrogazione una chiave sbagliata
            // passerebbe inosservata fino alla prima ricerca in rubrica.
            using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master;";
            probe.ExecuteScalar();

            return true;
        }
        catch (SqliteException)
        {
            connection.Dispose();
            connection = null!;

            return false;
        }
    }

    /// <summary>Stringa di connessione con la chiave di cifratura.</summary>
    /// <param name="databasePath">Percorso del file.</param>
    /// <param name="key">Chiave di cifratura.</param>
    public static string ConnectionString(string databasePath, string key)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Password = key,
            Pooling = false,
        }.ToString();

    /// <summary>Stringa di connessione senza cifratura, per gli archivi ancora in chiaro.</summary>
    /// <remarks>
    /// <b>Senza pool anche qui.</b> Ogni repository tiene una sola connessione aperta
    /// per tutta la sessione, quindi il pool non fa risparmiare nulla; in compenso
    /// trattiene il file aperto dopo la Dispose, e la conversione — che deve spostare
    /// e sostituire quel file — fallirebbe a intermittenza per un handle che nessuno
    /// sta piu' usando.
    /// </remarks>
    private static string PlainConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

    /// <summary>Dice se il file esiste ed è un archivio SQLite non cifrato.</summary>
    /// <param name="databasePath">Percorso del file.</param>
    public static bool IsPlaintext(string databasePath)
    {
        if (!File.Exists(databasePath)) return false;

        try
        {
            // Condivisione piena in lettura e scrittura: SQLite tiene il file aperto
            // in scrittura anche fra una connessione e l'altra, per via del pool.
            // Con la condivisione predefinita la lettura fallirebbe, e un archivio in
            // chiaro verrebbe scambiato per uno gia' cifrato — cioe' non convertito.
            using var stream = new FileStream(
                databasePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var header = new byte[PlaintextHeader.Length];
            if (stream.Read(header, 0, header.Length) < header.Length) return false;

            return header.SequenceEqual(PlaintextHeader);
        }
        catch (IOException)
        {
            // File occupato: si tratta come non migrabile adesso e si ritenta al
            // prossimo avvio, invece di rischiare una conversione a metà.
            return false;
        }
    }

    /// <summary>
    /// Converte in cifrato un archivio nato in chiaro, conservandone il contenuto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si scrive prima una copia cifrata a fianco, la si verifica riaprendola con la
    /// chiave, e solo allora si sostituisce l'originale. L'ordine conta: convertire
    /// sul posto e interrompersi a metà lascerebbe un operatore senza rubrica, cioè
    /// senza il modo in cui trasferisce le chiamate.
    /// </para>
    /// <para>
    /// L'originale in chiaro viene <b>eliminato</b> a conversione riuscita. Tenerne
    /// una copia di sicurezza sarebbe prudente per i dati e insensato per la
    /// riservatezza: resterebbe sul disco esattamente il file che si voleva togliere.
    /// </para>
    /// </remarks>
    private static bool TryEncryptInPlace(string databasePath, string key, ILogger log)
    {
        var temporaneo = databasePath + ".cifratura";

        try
        {
            foreach (var residuo in new[] { temporaneo, temporaneo + "-wal", temporaneo + "-shm" })
            {
                if (File.Exists(residuo)) File.Delete(residuo);
            }

            using (var origine = new SqliteConnection(PlainConnectionString(databasePath)))
            {
                origine.Open();

                // sqlcipher_export copia schema e dati dentro l'archivio collegato,
                // che essendo stato aperto con una chiave nasce gia' cifrato.
                using var command = origine.CreateCommand();
                command.CommandText = "ATTACH DATABASE $file AS cifrato KEY $chiave; SELECT sqlcipher_export('cifrato'); DETACH DATABASE cifrato;";
                command.Parameters.AddWithValue("$file", temporaneo);
                command.Parameters.AddWithValue("$chiave", key);
                command.ExecuteNonQuery();
            }

            var tabelle = CountTables(ConnectionString(temporaneo, key));

            if (tabelle == 0)
            {
                log.LogError("Copia cifrata di {File} vuota: conversione annullata", Path.GetFileName(databasePath));
                File.Delete(temporaneo);

                return false;
            }

            foreach (var vecchio in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(vecchio)) File.Delete(vecchio);
            }

            File.Move(temporaneo, databasePath);

            log.LogInformation(
                "Archivio {File} convertito in cifrato: {Tabelle} tabelle trasferite",
                Path.GetFileName(databasePath), tabelle);

            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Cifratura dell'archivio {File} non riuscita", Path.GetFileName(databasePath));

            try
            {
                if (File.Exists(temporaneo)) File.Delete(temporaneo);
            }
            catch (IOException)
            {
                // Il file di lavoro rimasto verra' rimosso al tentativo successivo.
            }

            return false;
        }
    }

    private static int CountTables(string connectionString)
    {
        using var verifica = new SqliteConnection(connectionString);
        verifica.Open();

        using var command = verifica.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table';";

        return Convert.ToInt32(command.ExecuteScalar());
    }
}
