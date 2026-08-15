using BlindPhone.Contacts;
using BlindPhone.Contacts.SqlBackup;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Lettura di una rubrica da un backup di banca dati.
/// </summary>
/// <remarks>
/// <para>
/// I dump usati qui sono scritti nei tre dialetti che si incontrano davvero:
/// MySQL con gli apici inversi, PostgreSQL con le virgolette doppie, SQL Server con
/// le parentesi quadre. Chi consegna il backup non sa quale ci aspettiamo, quindi
/// devono funzionare tutti e tre.
/// </para>
/// </remarks>
public sealed class BackupSqlTests : IDisposable
{
    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "blindphone-test-" + Guid.NewGuid().ToString("N"));

    public BackupSqlTests() => Directory.CreateDirectory(_cartella);

    public void Dispose()
    {
        try { Directory.Delete(_cartella, recursive: true); } catch (IOException) { }
    }

    private string Scrivi(string nome, string contenuto)
    {
        var percorso = Path.Combine(_cartella, nome);
        File.WriteAllText(percorso, contenuto);
        return percorso;
    }

    // -----------------------------------------------------------------------
    // Dump MySQL
    // -----------------------------------------------------------------------

    private const string DumpMySql = """
        -- Esportazione del gestionale, 10/08/2026
        /*!40101 SET NAMES utf8mb4 */;

        DROP TABLE IF EXISTS `anagrafica`;
        CREATE TABLE `anagrafica` (
          `id` int(11) NOT NULL AUTO_INCREMENT,
          `nome` varchar(60) DEFAULT NULL,
          `cognome` varchar(60) DEFAULT NULL,
          `numero` varchar(20) DEFAULT NULL,
          `reparto` varchar(80) DEFAULT NULL,
          `padiglione` varchar(10) DEFAULT NULL,
          `piano` varchar(10) DEFAULT NULL,
          PRIMARY KEY (`id`),
          KEY `idx_cognome` (`cognome`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        INSERT INTO `anagrafica` (`id`,`nome`,`cognome`,`numero`,`reparto`,`padiglione`,`piano`) VALUES
        (1,'Mario','Rossi','4021','Radiologia','B','2'),
        (2,'Anna','D\'Amico','4022','Cardiologia; Ambulatorio','A','1'),
        (3,'Luca','Verdi',NULL,'Direzione','A','3');

        CREATE TABLE `anagrafica_log` (
          `id` int(11) NOT NULL,
          `azione` varchar(20) DEFAULT NULL
        );

        INSERT INTO `anagrafica_log` (`id`,`azione`) VALUES (1,'modifica');
        """;

    [Fact]
    public void UnDumpMySqlElencaLeTabelleConColonneERighe()
    {
        using var lettore = SqlBackupFile.Open(Scrivi("backup.sql", DumpMySql));

        Assert.Equal(2, lettore.Tables.Count);

        var rubrica = lettore.Tables.Single(t => t.Name == "anagrafica");

        Assert.Equal(3, rubrica.RowCount);
        Assert.Equal(
            ["id", "nome", "cognome", "numero", "reparto", "padiglione", "piano"],
            rubrica.Columns);
    }

    [Fact]
    public void IVincoliNonDiventanoColonne()
    {
        // PRIMARY KEY e KEY stanno nella stessa parentesi delle colonne: prenderli
        // produrrebbe due voci fantasma nell'elenco da mappare, e l'operatore che non
        // vede lo schema non avrebbe modo di capire che non sono campi.
        using var lettore = SqlBackupFile.Open(Scrivi("backup.sql", DumpMySql));

        var rubrica = lettore.Tables.Single(t => t.Name == "anagrafica");

        Assert.DoesNotContain("PRIMARY", rubrica.Columns);
        Assert.DoesNotContain("KEY", rubrica.Columns);
        Assert.DoesNotContain("idx_cognome", rubrica.Columns);
    }

    [Fact]
    public void IValoriVengonoLettiConGliApiciEIPuntoEVirgolaDentroLeStringhe()
    {
        using var lettore = SqlBackupFile.Open(Scrivi("backup.sql", DumpMySql));

        var righe = lettore.ReadRows("anagrafica", null, CancellationToken.None).ToList();

        Assert.Equal(3, righe.Count);
        Assert.Equal("Rossi", righe[0]["cognome"]);

        // L'apice protetto dalla barra rovesciata e' un apice, non un terminatore.
        Assert.Equal("D'Amico", righe[1]["cognome"]);

        // Il punto e virgola dentro la stringa non divide l'istruzione: se lo facesse,
        // questa riga sarebbe tagliata a meta' e le successive andrebbero perse.
        Assert.Equal("Cardiologia; Ambulatorio", righe[1]["reparto"]);

        // NULL non e' la stringa "NULL" e non e' la stringa vuota.
        Assert.Null(righe[2]["numero"]);
    }

    [Fact]
    public void LeAltreTabelleRestanoSeparate()
    {
        using var lettore = SqlBackupFile.Open(Scrivi("backup.sql", DumpMySql));

        var log = lettore.ReadRows("anagrafica_log", null, CancellationToken.None).ToList();

        Assert.Single(log);
        Assert.Equal("modifica", log[0]["azione"]);
    }

    // -----------------------------------------------------------------------
    // Altri dialetti
    // -----------------------------------------------------------------------

    [Fact]
    public void UnDumpPostgreSqlVieneLettoUgualmente()
    {
        const string dump = """
            CREATE TABLE public."contatti" (
                "id" integer NOT NULL,
                "nominativo" character varying(120),
                "interno" character varying(10)
            );

            INSERT INTO public."contatti" ("id", "nominativo", "interno") VALUES (1, 'Rossi Mario', '4021');
            INSERT INTO public."contatti" ("id", "nominativo", "interno") VALUES (2, 'Bianchi Ada', '4022');
            """;

        using var lettore = SqlBackupFile.Open(Scrivi("pg.sql", dump));

        var tabella = Assert.Single(lettore.Tables);

        // Il nome e' la tabella, non lo schema: qui uno schema non esiste.
        Assert.Equal("contatti", tabella.Name);
        Assert.Equal(2, tabella.RowCount);

        var righe = lettore.ReadRows("contatti", null, CancellationToken.None).ToList();
        Assert.Equal("Rossi Mario", righe[0]["nominativo"]);
    }

    [Fact]
    public void UnDumpSqlServerVieneLettoUgualmente()
    {
        const string dump = """
            CREATE TABLE [dbo].[Rubrica](
                [Id] [int] NOT NULL,
                [Nominativo] [nvarchar](100) NULL,
                [Interno] [nvarchar](10) NULL,
                CONSTRAINT [PK_Rubrica] PRIMARY KEY CLUSTERED ([Id] ASC)
            );

            INSERT [dbo].[Rubrica] ([Id], [Nominativo], [Interno]) VALUES (1, N'Verdi Luca', N'4030');
            """;

        using var lettore = SqlBackupFile.Open(Scrivi("mssql.sql", dump));

        var tabella = Assert.Single(lettore.Tables);

        Assert.Equal("Rubrica", tabella.Name);
        Assert.Equal(["Id", "Nominativo", "Interno"], tabella.Columns);
    }

    // -----------------------------------------------------------------------
    // SQLite
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaBancaDatiSqliteVieneApertaDirettamente()
    {
        var percorso = Path.Combine(_cartella, "rubrica.db");

        using (var connessione = new SqliteConnection($"Data Source={percorso}"))
        {
            connessione.Open();

            using var comando = connessione.CreateCommand();
            comando.CommandText =
                "CREATE TABLE contatti (id INTEGER, nome TEXT, cognome TEXT, interno TEXT);"
                + "INSERT INTO contatti VALUES (1,'Mario','Rossi','4021');"
                + "CREATE TABLE impostazioni (chiave TEXT, valore TEXT);";
            comando.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var lettore = SqlBackupFile.Open(percorso);

        Assert.Equal(SqlBackupFormat.Sqlite, SqlBackupFile.Detect(percorso));
        Assert.Equal(2, lettore.Tables.Count);

        var contatti = lettore.Tables.Single(t => t.Name == "contatti");
        Assert.Equal(1, contatti.RowCount);

        var righe = lettore.ReadRows("contatti", null, CancellationToken.None).ToList();
        Assert.Equal("Rossi", righe[0]["cognome"]);
    }

    // -----------------------------------------------------------------------
    // Formati che non si possono leggere
    // -----------------------------------------------------------------------

    [Fact]
    public void UnBackupNativoDiSqlServerVieneRifiutatoSpiegandoPerche()
    {
        // I backup nativi di SQL Server cominciano con "TAPE" e sono binari: nessuno
        // li sa aprire fuori da SQL Server. Il messaggio finisce in un annuncio
        // vocale, quindi deve dire cosa chiedere a chi ha prodotto il file.
        var percorso = Path.Combine(_cartella, "db.bak");
        File.WriteAllBytes(percorso, [.. "TAPE"u8.ToArray(), 0x00, 0x01, 0x02, 0x00]);

        Assert.Equal(SqlBackupFormat.SqlServerBackup, SqlBackupFile.Detect(percorso));

        var errore = Assert.Throws<NotSupportedException>(() => SqlBackupFile.Open(percorso));

        Assert.Contains("SQL Server", errore.Message);
        Assert.Contains("CSV", errore.Message);
    }

    [Fact]
    public void IlFormatoSiRiconosceDalContenutoNonDallEstensione()
    {
        // Un mysqldump rinominato .bak e' il caso piu' comune in assoluto negli
        // uffici: fidarsi dell'estensione lo farebbe rifiutare per sbaglio.
        var percorso = Scrivi("esportazione.bak", DumpMySql);

        Assert.Equal(SqlBackupFormat.TextDump, SqlBackupFile.Detect(percorso));
    }

    /// <summary>
    /// Un dump con un'estensione qualsiasi viene riconosciuto lo stesso.
    /// </summary>
    /// <remarks>
    /// Nasce da un caso reale: il backup di un cliente si chiamava <c>.bkp</c>. Un
    /// elenco chiuso di estensioni ammesse rifiuta file validi finché qualcuno non
    /// segnala il nome mancante, cioè al primo cliente che ne ha uno.
    /// </remarks>
    [Theory]
    [InlineData("rubrica.bkp")]
    [InlineData("rubrica.dmp")]
    [InlineData("rubrica.backup")]
    [InlineData("rubrica.qualunquecosa")]
    [InlineData("rubrica")]
    public void UnDumpConUnEstensioneQualsiasiVieneRiconosciuto(string nome)
    {
        var percorso = Scrivi(nome, DumpMySql);

        Assert.True(SqlBackupFile.ContainsSqlStatements(percorso));

        using var lettore = SqlBackupFile.Open(percorso);
        Assert.Equal(2, lettore.Tables.Count);
    }

    [Fact]
    public void UnCsvNonVieneScambiatoPerUnDump()
    {
        // La contromisura non deve dirottare i file CSV verso il lettore dei backup.
        var percorso = Scrivi(
            "rubrica.csv",
            "numero,nome,cognome,reparto\n4021,Mario,Rossi,Radiologia\n4022,Anna,Bianchi,Cardiologia\n");

        Assert.False(SqlBackupFile.ContainsSqlStatements(percorso));
    }

    [Fact]
    public void UnFileVuotoNonVieneScambiatoPerUnDump()
    {
        Assert.False(SqlBackupFile.ContainsSqlStatements(Scrivi("vuoto.bkp", string.Empty)));
    }

    // -----------------------------------------------------------------------
    // Costruzione dei contatti
    // -----------------------------------------------------------------------

    private static CsvColumnMapping MappaturaAnagrafica => new()
    {
        FirstName = "nome",
        LastName = "cognome",
        Extension = "numero",
        Department = "reparto",
        Building = "padiglione",
        Floor = "piano",
    };

    [Fact]
    public async Task LaTabellaSceltaDiventaUnaRubrica()
    {
        var percorso = Scrivi("backup.sql", DumpMySql);
        var sorgente = new SqlBackupContactSource(percorso, "anagrafica", MappaturaAnagrafica);

        var contatti = await sorgente.LoadAsync(CancellationToken.None);

        // Tre righe, ma quella senza numero non entra: a un centralino non serve, e
        // ogni voce inutile costa tempo di ascolto.
        Assert.Equal(2, contatti.Count);

        var mario = contatti[0];
        Assert.Equal("Mario", mario.FirstName);
        Assert.Equal("Rossi", mario.LastName);
        Assert.Equal("4021", mario.PrimaryNumber);
        Assert.Equal("Radiologia", mario.Department);

        // Le due fusioni che la rubrica mostra a schermo.
        Assert.Equal("Mario Rossi", mario.EffectiveDisplayName);
        Assert.Equal("padiglione B, piano 2", mario.LocationSpeech);
    }

    [Fact]
    public async Task ReimportareLoStessoBackupNonDuplicaINominativi()
    {
        // L'identificativo e' deterministico: senza, ogni reimportazione creerebbe
        // contatti nuovi e i preferiti e i numeri rapidi resterebbero attaccati a
        // voci morte.
        var percorso = Scrivi("backup.sql", DumpMySql);
        var sorgente = new SqlBackupContactSource(percorso, "anagrafica", MappaturaAnagrafica);

        var prima = await sorgente.LoadAsync(CancellationToken.None);
        var seconda = await sorgente.LoadAsync(CancellationToken.None);

        Assert.Equal(prima.Select(c => c.Id), seconda.Select(c => c.Id));
    }

    [Fact]
    public async Task LAnteprimaSiFermaAlleRigheChieste()
    {
        var percorso = Scrivi("backup.sql", DumpMySql);
        var sorgente = new SqlBackupContactSource(percorso, "anagrafica", MappaturaAnagrafica);

        var anteprima = await sorgente.PreviewAsync(1, CancellationToken.None);

        Assert.Single(anteprima);
    }

    [Fact]
    public async Task UnaTabellaInesistenteLoDiceInItaliano()
    {
        var percorso = Scrivi("backup.sql", DumpMySql);
        var sorgente = new SqlBackupContactSource(percorso, "inesistente", MappaturaAnagrafica);

        var esito = await sorgente.TestAsync(CancellationToken.None);

        Assert.False(esito.Success);
        Assert.Contains("non esiste", esito.Message);
    }
}
