using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace BlindPhone.Contacts;

/// <summary>
/// Apre un archivio SQLite cifrato nel formato SQLCipher 4, migrando quelli nati in chiaro.
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
/// <para>
/// <b>Il motore è SQLite3 Multiple Ciphers, il formato resta SQLCipher 4.</b> Fino
/// alla 1.0.20 cifrava il motore SQLCipher di <c>SQLitePCLRaw.bundle_e_sqlcipher</c>,
/// che non è più mantenuto. SQLite3MC sa scrivere molti formati e di suo ne
/// sceglierebbe un altro (ChaCha20): qui viene impostato su SQLCipher 4 a ogni
/// apertura, vedi <see cref="ApplyKey"/>, ed è l'unico modo in cui gli archivi già
/// sulle postazioni restano leggibili.
/// </para>
/// </remarks>
public static class EncryptedSqlite
{
    /// <summary>Dimensione delle pagine del formato SQLCipher 4.</summary>
    /// <remarks>
    /// Nel formato SQLCipher anche la dimensione della pagina è cifrata, quindi il
    /// motore non la può leggere dal file e la deve sapere in anticipo: un archivio
    /// con pagine di un'altra misura non si aprirebbe.
    /// </remarks>
    private const int SqlCipherPageSize = 4096;

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
            var plain = new SqliteConnection(ConnectionString(databasePath));
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

                var degradato = new SqliteConnection(ConnectionString(databasePath));
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

        return (OpenWithKey(databasePath, key), true, true);
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
    /// Quella verifica è stata fatta con il motore della 1.0.20. Con SQLite3MC 2.7.1
    /// la prova sulla chiave sbagliata passa anche senza la chiamata, che resta lo
    /// stesso: costa niente, e se il comportamento tornasse il prezzo sarebbe la
    /// rubrica di una postazione.
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
        SqliteConnection? candidata = null;

        try
        {
            candidata = OpenWithKey(databasePath, key);

            // L'apertura non decifra nulla: la chiave si verifica alla prima
            // lettura vera. Senza questa interrogazione una chiave sbagliata
            // passerebbe inosservata fino alla prima ricerca in rubrica.
            using var probe = candidata.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master;";
            probe.ExecuteScalar();

            connection = candidata;

            return true;
        }
        catch (SqliteException)
        {
            candidata?.Dispose();
            connection = null!;

            return false;
        }
    }

    /// <summary>Apre l'archivio e gli applica formato e chiave di cifratura.</summary>
    /// <param name="databasePath">Percorso del file.</param>
    /// <param name="key">Chiave di cifratura.</param>
    private static SqliteConnection OpenWithKey(string databasePath, string key)
    {
        var connection = new SqliteConnection(ConnectionString(databasePath));

        try
        {
            connection.Open();
            ApplyKey(connection, key);

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Imposta il formato SQLCipher 4 e consegna la chiave al motore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Non si passa la chiave nella stringa di connessione</b>, come si faceva con
    /// <c>Password=</c> fino alla 1.0.20. Microsoft.Data.Sqlite la consegnerebbe al
    /// motore appena aperto il file, prima di qualunque altra istruzione, e SQLite3MC
    /// la userebbe con il suo formato predefinito: gli archivi esistenti
    /// risulterebbero illeggibili e, peggio, <see cref="Open"/> li scambierebbe per
    /// archivi con la chiave sbagliata, spostandoli da parte e ripartendo vuoto.
    /// </para>
    /// <para>
    /// L'ordine quindi conta: prima <c>cipher</c> e <c>legacy</c>, poi la chiave.
    /// <c>legacy = 4</c> vuol dire SQLCipher 4 così com'è, con tutti i suoi
    /// parametri: PBKDF2-HMAC-SHA512 a 256000 iterazioni, HMAC-SHA512 su ogni pagina,
    /// pagine da 4096 byte e intestazione cifrata anche lei. Lo stesso formato vale
    /// per gli archivi nuovi: una sola forma sul disco, qualunque sia l'anno in cui
    /// l'archivio è nato, e una postazione riportata alla 1.0.20 continua ad aprirli.
    /// </para>
    /// <para>
    /// La chiave passa da <c>sqlite3_key</c>, la stessa funzione che
    /// <c>PRAGMA key</c> chiama dentro il motore, con gli stessi byte UTF-8: così
    /// non entra mai nel testo di un'istruzione e non c'è niente da virgolettare.
    /// </para>
    /// </remarks>
    private static void ApplyKey(SqliteConnection connection, string key)
    {
        SetSqlCipher4(connection);

        var rc = raw.sqlite3_key(connection.Handle, Encoding.UTF8.GetBytes(key));
        SqliteException.ThrowExceptionForRC(rc, connection.Handle);
    }

    /// <summary>Sceglie il formato SQLCipher 4 per le cifrature che seguono.</summary>
    /// <remarks>
    /// Va eseguita prima della chiave e fuori da ogni transazione: queste istruzioni
    /// non si annullano con un rollback.
    /// </remarks>
    private static void SetSqlCipher4(SqliteConnection connection)
    {
        using var formato = connection.CreateCommand();
        formato.CommandText = "PRAGMA cipher = 'sqlcipher'; PRAGMA legacy = 4;";
        formato.ExecuteNonQuery();
    }

    /// <summary>Stringa di connessione, senza chiave: la chiave la applica <see cref="ApplyKey"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Senza pool.</b> Ogni repository tiene una sola connessione aperta per tutta
    /// la sessione, quindi il pool non fa risparmiare nulla; in compenso trattiene il
    /// file aperto dopo la Dispose, e la conversione — che deve spostare e sostituire
    /// quel file — fallirebbe a intermittenza per un handle che nessuno sta piu'
    /// usando.
    /// </para>
    /// <para>
    /// C'è una ragione in più, adesso che la chiave si applica dopo l'apertura: una
    /// connessione tornata nel pool la porterebbe con sé, e la successiva apertura
    /// dello stesso file - anche senza chiave - la ritroverebbe già applicata.
    /// </para>
    /// </remarks>
    private static string ConnectionString(string databasePath)
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
    /// Si scrive prima una copia a fianco, la si cifra, la si verifica riaprendola con
    /// la chiave, e solo allora si sostituisce l'originale. L'ordine conta: convertire
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
            // Anche il giornale: e' quello della cifratura della copia, e se un
            // tentativo precedente si e' interrotto a meta' verrebbe riapplicato
            // alla copia nuova.
            foreach (var residuo in new[] { temporaneo, temporaneo + "-journal", temporaneo + "-wal", temporaneo + "-shm" })
            {
                if (File.Exists(residuo)) File.Delete(residuo);
            }

            using (var origine = new SqliteConnection(ConnectionString(databasePath)))
            {
                origine.Open();

                // VACUUM INTO scrive a fianco una copia coerente di schema e dati,
                // passando dal motore e non dal file system. La copia ha le pagine
                // della misura che il formato SQLCipher 4 pretende, qualunque fosse
                // quella dell'originale.
                using var copia = origine.CreateCommand();
                copia.CommandText = $"PRAGMA page_size = {SqlCipherPageSize}; VACUUM INTO $file;";
                copia.Parameters.AddWithValue("$file", temporaneo);
                copia.ExecuteNonQuery();
            }

            using (var lavoro = new SqliteConnection(ConnectionString(temporaneo)))
            {
                lavoro.Open();

                // La copia e' ancora in chiaro, e la si cifra sul posto: rekey su un
                // archivio senza chiave lo cifra. Prende il posto di sqlcipher_export,
                // che SQLite3MC non ha.
                SetSqlCipher4(lavoro);

                var rc = raw.sqlite3_rekey(lavoro.Handle, Encoding.UTF8.GetBytes(key));
                SqliteException.ThrowExceptionForRC(rc, lavoro.Handle);
            }

            var tabelle = CountTables(temporaneo, key);

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

    private static int CountTables(string databasePath, string key)
    {
        using var verifica = OpenWithKey(databasePath, key);

        using var command = verifica.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table';";

        return Convert.ToInt32(command.ExecuteScalar());
    }
}
