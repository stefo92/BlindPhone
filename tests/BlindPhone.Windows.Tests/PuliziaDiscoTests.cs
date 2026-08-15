using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la pulizia dei file temporanei.
/// </summary>
/// <remarks>
/// <para>
/// Qui la proprietà che conta non è che i file spariscano: è che spariscano
/// <b>solo</b> quelli dichiarati. Nella stessa cartella vivono l'account SIP, il
/// deposito protetto delle password, la rubrica cifrata e l'elenco delle chiamate:
/// un pulsante che libera spazio e porta via la configurazione mette la postazione
/// fuori servizio, e su un centralino ospedaliero il danno si scopre alla prima
/// chiamata che non parte.
/// </para>
/// <para>
/// Per questo la maggior parte di queste prove verifica <b>cosa resta</b>.
/// </para>
/// </remarks>
public sealed class PuliziaDiscoTests : IDisposable
{
    private readonly string _radice = Path.Combine(
        Path.GetTempPath(), "BlindPhone.Test", $"pulizia-{Guid.NewGuid():N}");

    private string Dati => Path.Combine(_radice, "dati");

    private string Registri => Path.Combine(Dati, "log");

    private string Aggiornamenti => Path.Combine(_radice, "temp");

    private string RegistroDiOggi => Path.Combine(Registri, "blindphone-20260812.log");

    private DiskCleanupService Servizio()
        => new(Dati, Registri, () => RegistroDiOggi, Aggiornamenti);

    /// <summary>Prepara una cartella dati come quella di una postazione in esercizio.</summary>
    private void PreparaPostazione()
    {
        Directory.CreateDirectory(Dati);
        Directory.CreateDirectory(Registri);
        Directory.CreateDirectory(Path.Combine(Registri, "anonimo"));
        Directory.CreateDirectory(Path.Combine(Dati, "errori"));
        Directory.CreateDirectory(Aggiornamenti);

        // Da eliminare.
        Scrivi(RegistroDiOggi, 3000);
        Scrivi(Path.Combine(Registri, "blindphone-20260811.log"), 2000);
        Scrivi(Path.Combine(Registri, "blindphone-20260810.log"), 1000);
        Scrivi(Path.Combine(Registri, "anonimo", "blindphone-20260811-anonimo.log"), 1500);
        Scrivi(Path.Combine(Dati, "errori", "errore-20260810-120000-000-1.txt"), 500);
        Scrivi(Path.Combine(Aggiornamenti, "BlindPhone-1.0.8-x64.msi"), 4000);
        Scrivi(Path.Combine(Aggiornamenti, "BlindPhone-1.0.9-x64.msi.part"), 700);

        // Da NON eliminare, mai.
        Scrivi(Path.Combine(Dati, "settings.json"), 100);
        Scrivi(Path.Combine(Dati, "secrets.dat"), 100);
        Scrivi(Path.Combine(Dati, "history.db"), 100);
        Scrivi(Path.Combine(Dati, "hotkeys.json"), 100);
        Scrivi(Path.Combine(Dati, "contatti.db"), 100);
    }

    private static void Scrivi(string percorso, int byteCount)
        => File.WriteAllBytes(percorso, new byte[byteCount]);

    [Fact]
    public void IlRegistroDiOggiNonVieneMaiEliminato()
    {
        // E' aperto in scrittura dal programma che sta girando, ed e' quello che
        // racconta il turno in corso: e' il file che serve se il guasto capita fra
        // dieci minuti.
        PreparaPostazione();

        Servizio().Clean();

        Assert.True(File.Exists(RegistroDiOggi));
        Assert.False(File.Exists(Path.Combine(Registri, "blindphone-20260811.log")));
        Assert.False(File.Exists(Path.Combine(Registri, "blindphone-20260810.log")));
    }

    [Fact]
    public void ConfigurazioneRubricaEChiamateRestanoDoveSono()
    {
        PreparaPostazione();

        Servizio().Clean();

        Assert.True(File.Exists(Path.Combine(Dati, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Dati, "secrets.dat")));
        Assert.True(File.Exists(Path.Combine(Dati, "history.db")));
        Assert.True(File.Exists(Path.Combine(Dati, "hotkeys.json")));
        Assert.True(File.Exists(Path.Combine(Dati, "contatti.db")));
    }

    [Fact]
    public void UnaSottocartellaNonDichiarataNonVieneToccata()
    {
        // La difesa contro l'allargamento silenzioso: il giorno in cui qualcuno
        // deposita qualcosa in una sottocartella nuova, la pulizia non deve
        // scoprirla da sola.
        PreparaPostazione();

        var altrove = Path.Combine(Dati, "registrazioni");
        Directory.CreateDirectory(altrove);
        Scrivi(Path.Combine(altrove, "chiamata-20260810.wav"), 9000);

        Servizio().Clean();

        Assert.True(File.Exists(Path.Combine(altrove, "chiamata-20260810.wav")));
    }

    [Fact]
    public void LeCartelleRestanoInPiedi()
    {
        // Una cartella che sparisce e viene ricreata al volo e' un modo di scoprire
        // un problema di permessi mentre si sta scrivendo un registro.
        PreparaPostazione();

        Servizio().Clean();

        Assert.True(Directory.Exists(Registri));
        Assert.True(Directory.Exists(Path.Combine(Registri, "anonimo")));
        Assert.True(Directory.Exists(Path.Combine(Dati, "errori")));
    }

    [Fact]
    public void LAnteprimaDiceEsattamenteQuelloCheVerraEliminato()
    {
        PreparaPostazione();

        var anteprima = Servizio().Inspect();
        var esito = Servizio().Clean();

        // Sei file: due registri vecchi, una copia anonima, un rapporto di errore,
        // un pacchetto e uno scaricamento a meta'.
        Assert.Equal(6, anteprima.TotalFiles);
        Assert.Equal(anteprima.TotalFiles, esito.DeletedFiles);
        Assert.Equal(anteprima.TotalBytes, esito.BytesFreed);
        Assert.Equal(0, esito.SkippedFiles);
    }

    [Fact]
    public void LAnteprimaElencaLeFamiglieConNomiLeggibili()
    {
        // L'elenco viene letto ad alta voce nella conferma: chi non vede lo schermo
        // non ha altro modo di sapere che cosa sta per sparire.
        PreparaPostazione();

        var anteprima = Servizio().Inspect();

        Assert.Equal(4, anteprima.Categories.Count);
        Assert.All(anteprima.Categories, c => Assert.False(string.IsNullOrWhiteSpace(c.Explanation)));
        Assert.Contains(anteprima.Categories, c => c.Name.Contains("Registri", StringComparison.Ordinal));
        Assert.Contains(anteprima.Categories, c => c.Describe().Contains("file", StringComparison.Ordinal));
    }

    [Fact]
    public void SuUnaPostazioneAppenaInstallataNonCEnienteDaEliminare()
    {
        Directory.CreateDirectory(Dati);

        var anteprima = Servizio().Inspect();

        Assert.True(anteprima.IsEmpty);
        Assert.Empty(anteprima.Categories);
    }

    [Fact]
    public void CartelleInesistentiNonSollevano()
    {
        // Il pannello puo' essere aperto prima che il registro abbia scritto la sua
        // prima riga: leggere una cartella che non c'e' non e' un errore.
        var esito = Servizio().Clean();

        Assert.Equal(0, esito.DeletedFiles);
        Assert.Equal(0, esito.SkippedFiles);
    }

    [Fact]
    public void UnFileInUsoVieneContatoMaNonFermaLaPulizia()
    {
        PreparaPostazione();

        var bloccato = Path.Combine(Registri, "blindphone-20260811.log");
        using (var _ = new FileStream(bloccato, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var esito = Servizio().Clean();

            Assert.Equal(1, esito.SkippedFiles);
            Assert.Equal(5, esito.DeletedFiles);
            Assert.True(File.Exists(bloccato));
        }

        // E gli altri sono spariti lo stesso.
        Assert.False(File.Exists(Path.Combine(Registri, "blindphone-20260810.log")));
    }

    [Theory]
    [InlineData(0, "0 byte")]
    [InlineData(1, "1 byte")]
    [InlineData(2048, "2 kilobyte")]
    public void LeDimensioniSiLeggonoSenzaSigle(long byteCount, string atteso)
    {
        // "MB" viene letto "emme bi" da alcune sintesi e "megabyte" da altre: su un
        // pannello che si usa a orecchio la differenza si sente.
        Assert.Equal(atteso, DiskCleanupService.DescribeSize(byteCount));
    }

    [Fact]
    public void IMegabyteSiDiconoPerEsteso()
        => Assert.EndsWith("megabyte", DiskCleanupService.DescribeSize(5 * 1024 * 1024), StringComparison.Ordinal);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_radice)) Directory.Delete(_radice, recursive: true);
        }
        catch (IOException)
        {
            // Una prova che non riesce a pulire la propria cartella temporanea non e'
            // un difetto del prodotto.
        }
    }
}
