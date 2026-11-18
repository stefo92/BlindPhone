using BlindPhone.Contacts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifiche sulla cifratura degli archivi locali.
/// </summary>
/// <remarks>
/// <para>
/// Sono le prove che un collaudo di sicurezza presso una PA rifà a mano: il file non
/// si apre senza chiave, non si apre con la chiave sbagliata, e non contiene niente
/// di leggibile.
/// </para>
/// <para>
/// La più importante è però quella sulla conversione: una postazione già in servizio
/// ha una rubrica in chiaro costruita in mesi di lavoro, e un aggiornamento che la
/// perde per strada è peggio del problema che risolve.
/// </para>
/// </remarks>
public sealed class EncryptedSqliteTests : IDisposable
{
    private const string Chiave = "chiave-di-prova-256";

    private readonly string _cartella;

    /// <summary>Prepara una cartella isolata.</summary>
    public EncryptedSqliteTests()
    {
        _cartella = Path.Combine(Path.GetTempPath(), "BlindPhone.Test", $"cifratura-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cartella);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_cartella)) Directory.Delete(_cartella, recursive: true);
        }
        catch (IOException)
        {
            // Una cartella temporanea rimasta non e' un fallimento della prova.
        }
    }

    [Fact]
    public void UnArchivioNuovoNasceCifrato()
    {
        var file = Percorso("nuovo.db");

        using (var connessione = Apri(file, Chiave))
        {
            Scrivi(connessione, "il segreto");
        }

        Assert.False(EncryptedSqlite.IsPlaintext(file));
    }

    [Fact]
    public void SenzaChiaveL_archivioNonSiApre()
    {
        var file = Percorso("protetto.db");

        using (var connessione = Apri(file, Chiave))
        {
            Scrivi(connessione, "il segreto");
        }

        SqliteConnection.ClearAllPools();

        using var tentativo = new SqliteConnection($"Data Source={file}");
        tentativo.Open();

        using var comando = tentativo.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM sqlite_master;";

        // Aprire la connessione riesce sempre: SQLite non tocca il file finche' non
        // deve leggerlo. Il rifiuto arriva alla prima lettura vera, ed e' li' che va
        // verificato — altrimenti la prova passerebbe anche senza cifratura.
        Assert.Throws<SqliteException>(() => comando.ExecuteScalar());
    }

    [Fact]
    public void ConLaChiaveSbagliataSiRicominciaVuotiSenzaButtareIlFile()
    {
        var file = Percorso("protetto.db");

        using (var connessione = Apri(file, Chiave))
        {
            Scrivi(connessione, "il segreto");
        }

        SqliteConnection.ClearAllPools();

        // Il caso vero: secrets.dat perso o profilo Windows ricreato. Il contenuto non
        // e' recuperabile, ma il telefono di un centralinista che non vede non puo'
        // rifiutarsi di partire — resterebbe senza modo di rispondere e senza modo di
        // capire perche'.
        var (connessione2, cifrato, azzerato) = EncryptedSqlite.Open(
            file, "chiave-diversa", NullLogger.Instance);

        using (connessione2)
        {
            Assert.True(cifrato);
            Assert.True(azzerato);

            Scrivi(connessione2, "ricominciato");
            Assert.Equal(["ricominciato"], Leggi(connessione2));
        }

        // Il file illeggibile viene spostato e non cancellato: se domani il deposito
        // dei segreti torna da un backup, quei dati tornano con lui.
        var scartati = Directory.GetFiles(_cartella, "protetto.db.non-leggibile-*");
        Assert.Single(scartati);
        Assert.True(new FileInfo(scartati[0]).Length > 0);
    }

    [Fact]
    public void UnArchivioGiaInChiaroVieneConvertitoSenzaPerdereNulla()
    {
        var file = Percorso("vecchio.db");

        // La postazione in servizio prima di questo aggiornamento: archivio in chiaro,
        // con dentro il lavoro di mesi.
        using (var vecchio = new SqliteConnection($"Data Source={file}"))
        {
            vecchio.Open();
            Scrivi(vecchio, "Rossi Mario", "Bianchi Anna", "Verdi Luigi");
        }

        Assert.True(EncryptedSqlite.IsPlaintext(file));

        SqliteConnection.ClearAllPools();

        var (connessione, cifrato, azzerato) = EncryptedSqlite.Open(file, Chiave, NullLogger.Instance);

        using (connessione)
        {
            Assert.True(cifrato);
            Assert.False(azzerato);
            Assert.Equal(["Bianchi Anna", "Rossi Mario", "Verdi Luigi"], Leggi(connessione));
        }

        Assert.False(EncryptedSqlite.IsPlaintext(file));

        // Il file in chiaro non deve sopravvivere alla conversione, ne' come copia di
        // sicurezza: resterebbe sul disco esattamente quello che si voleva togliere.
        Assert.False(File.Exists(file + ".cifratura"));
        Assert.Empty(Directory.GetFiles(_cartella, "*.bak"));
    }

    [Fact]
    public void UnArchivioGiaCifratoNonVieneRiconvertito()
    {
        var file = Percorso("stabile.db");

        using (var connessione = Apri(file, Chiave))
        {
            Scrivi(connessione, "Rossi Mario");
        }

        SqliteConnection.ClearAllPools();

        // Riaprire non deve rimettere in moto la conversione: farlo a ogni avvio
        // riscriverebbe l'intero archivio senza motivo.
        var (riaperto, cifrato, azzerato) = EncryptedSqlite.Open(file, Chiave, NullLogger.Instance);

        using (riaperto)
        {
            Assert.True(cifrato);
            Assert.False(azzerato);
            Assert.Equal(["Rossi Mario"], Leggi(riaperto));
        }
    }

    [Fact]
    public void SenzaChiaveSiApreInChiaro()
    {
        // Il caso serve solo alle prove e agli strumenti diagnostici, ma deve
        // dichiararsi per quello che e': l'archivio risulta non cifrato.
        var file = Percorso("aperto.db");

        var (connessione, cifrato, azzerato) = EncryptedSqlite.Open(file, string.Empty, NullLogger.Instance);

        using (connessione)
        {
            Scrivi(connessione, "in chiaro");
            Assert.False(cifrato);
            Assert.False(azzerato);
        }

        Assert.True(EncryptedSqlite.IsPlaintext(file));
    }

    /// <summary>Chiave degli archivi in <c>ArchiviSqlCipher4</c>.</summary>
    /// <remarks>
    /// Ha la forma delle chiavi vere, 32 byte casuali in Base64 (vedi
    /// <c>SettingsService.GetOrCreateDatabaseKey</c>), con dentro <c>+</c>, <c>/</c>
    /// e <c>=</c>: se la chiave arrivasse al motore con byte diversi da quelli di
    /// allora, gli archivi non si aprirebbero.
    /// </remarks>
    private const string ChiaveArchiviDellaVersionePrecedente = "0McDTnkjn+63p/pMQzONSkgAW2L/fMy79s4AaOZPLLc=";

    /// <summary>
    /// Gli archivi cifrati dalla 1.0.20 si aprono con tutto il contenuto, e ci si
    /// continua a scrivere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È la prova che il cambio di motore - da <c>SQLitePCLRaw.bundle_e_sqlcipher</c>
    /// 2.1.11 (SQLite 3.39.2, SQLCipher 4.5.2) a SQLite3 Multiple Ciphers - non costa a
    /// nessuna postazione la rubrica o il registro delle chiamate. Se fallisse,
    /// <see cref="EncryptedSqlite.Open"/> scambierebbe ogni archivio esistente per uno
    /// con la chiave sbagliata, lo sposterebbe da parte e ripartirebbe vuoto.
    /// </para>
    /// <para>
    /// I due file sono stati scritti dall'<c>EncryptedSqlite</c> della 1.0.20 con il
    /// suo motore, una volta sola, e coprono le due strade per cui un archivio è
    /// diventato cifrato: nato cifrato, oppure nato in chiaro e convertito al primo
    /// avvio da <c>sqlcipher_export</c>. Contengono la tabella <c>contatti</c> con 300
    /// righe inventate, «Contatto di prova NNN - Niccolò Città» e «+39 06 0NNN», e un
    /// indice sul nome: tredici pagine, quindi non solo la prima da decifrare.
    /// </para>
    /// <para>
    /// <b>Non vanno rigenerati.</b> Riscritti con il motore di adesso, la prova
    /// passerebbe sempre e non direbbe più niente sugli archivi che stanno sulle
    /// postazioni.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("nato-cifrato-1.0.20.db")]
    [InlineData("convertito-da-chiaro-1.0.20.db")]
    public void UnArchivioCifratoDallaVersionePrecedenteSiApreConTuttoIlContenuto(string nome)
    {
        var file = Percorso(nome);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ArchiviSqlCipher4", nome), file);

        var (connessione, cifrato, azzerato) = EncryptedSqlite.Open(
            file, ChiaveArchiviDellaVersionePrecedente, NullLogger.Instance);

        using (connessione)
        {
            Assert.True(cifrato);
            Assert.False(azzerato);

            // integrity_check legge e verifica ogni pagina, non solo l'intestazione.
            Assert.Equal("ok", Scalare(connessione, "PRAGMA integrity_check;"));
            Assert.Equal(300L, Scalare(connessione, "SELECT count(*) FROM contatti;"));
            Assert.Equal(
                "Contatto di prova 001 - Niccolò Città | +39 06 0001",
                Scalare(connessione, "SELECT nome || ' | ' || numero FROM contatti WHERE id = 1;"));
            Assert.Equal(
                "Contatto di prova 300 - Niccolò Città | +39 06 0300",
                Scalare(connessione, "SELECT nome || ' | ' || numero FROM contatti WHERE id = 300;"));

            using var inserisci = connessione.CreateCommand();
            inserisci.CommandText = "INSERT INTO contatti (nome, numero) VALUES ('Contatto scritto dopo il cambio di motore', '+39 06 0301');";
            inserisci.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        var (riaperto, cifrato2, azzerato2) = EncryptedSqlite.Open(
            file, ChiaveArchiviDellaVersionePrecedente, NullLogger.Instance);

        using (riaperto)
        {
            Assert.True(cifrato2);
            Assert.False(azzerato2);
            Assert.Equal("ok", Scalare(riaperto, "PRAGMA integrity_check;"));
            Assert.Equal(301L, Scalare(riaperto, "SELECT count(*) FROM contatti;"));
        }

        Assert.False(EncryptedSqlite.IsPlaintext(file));
        Assert.Empty(Directory.GetFiles(_cartella, "*.non-leggibile-*"));
    }

    [Fact]
    public void UnArchivioInChiaroConPagineDiAltraMisuraVieneConvertitoLoStesso()
    {
        var file = Percorso("pagine-piccole.db");

        using (var vecchio = new SqliteConnection($"Data Source={file}"))
        {
            vecchio.Open();

            using var pagine = vecchio.CreateCommand();
            pagine.CommandText = "PRAGMA page_size = 1024;";
            pagine.ExecuteNonQuery();

            Scrivi(vecchio, "Rossi Mario");
        }

        SqliteConnection.ClearAllPools();

        // Il formato SQLCipher 4 vuole pagine da 4096 byte: una copia con le pagine
        // dell'originale verrebbe cifrata e poi non si riaprirebbe.
        var (connessione, cifrato, azzerato) = EncryptedSqlite.Open(file, Chiave, NullLogger.Instance);

        using (connessione)
        {
            Assert.True(cifrato);
            Assert.False(azzerato);
            Assert.Equal(["Rossi Mario"], Leggi(connessione));
            Assert.Equal(4096L, Scalare(connessione, "PRAGMA page_size;"));
        }
    }

    [Fact]
    public void IlMotoreNonEPiuIlSqliteDel2022()
    {
        using var connessione = Apri(Percorso("motore.db"), Chiave);

        // sqlite3mc_version esiste solo in SQLite3 Multiple Ciphers: se un altro
        // pacchetto riportasse dentro un motore diverso, lo si vedrebbe qui.
        Assert.StartsWith("SQLite3 Multiple Ciphers", (string)Scalare(connessione, "SELECT sqlite3mc_version();")!);

        // CVE-2025-6965 riguarda tutte le versioni prima della 3.50.2. Il motore
        // della 1.0.20 era la 3.39.2.
        var versione = Version.Parse((string)Scalare(connessione, "SELECT sqlite_version();")!);
        Assert.True(versione >= new Version(3, 50, 2), $"SQLite {versione} e' anteriore alla 3.50.2.");
    }

    // -----------------------------------------------------------------------

    private string Percorso(string nome) => Path.Combine(_cartella, nome);

    private static SqliteConnection Apri(string file, string chiave)
    {
        var (connessione, cifrato, azzerato) = EncryptedSqlite.Open(file, chiave, NullLogger.Instance);

        Assert.True(cifrato);
        Assert.False(azzerato);

        return connessione;
    }

    private static void Scrivi(SqliteConnection connessione, params string[] valori)
    {
        using var crea = connessione.CreateCommand();
        crea.CommandText = "CREATE TABLE IF NOT EXISTS prova (nome TEXT);";
        crea.ExecuteNonQuery();

        foreach (var valore in valori)
        {
            using var inserisci = connessione.CreateCommand();
            inserisci.CommandText = "INSERT INTO prova (nome) VALUES ($nome);";
            inserisci.Parameters.AddWithValue("$nome", valore);
            inserisci.ExecuteNonQuery();
        }
    }

    private static object? Scalare(SqliteConnection connessione, string sql)
    {
        using var comando = connessione.CreateCommand();
        comando.CommandText = sql;

        return comando.ExecuteScalar();
    }

    private static List<string> Leggi(SqliteConnection connessione)
    {
        using var comando = connessione.CreateCommand();
        comando.CommandText = "SELECT nome FROM prova ORDER BY nome;";

        var risultati = new List<string>();

        using var reader = comando.ExecuteReader();
        while (reader.Read()) risultati.Add(reader.GetString(0));

        return risultati;
    }
}
