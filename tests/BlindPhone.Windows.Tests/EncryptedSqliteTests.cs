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
