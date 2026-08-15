using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la raccolta dei rapporti di errore.
/// </summary>
/// <remarks>
/// <para>
/// La proprietà che conta più di tutte è che questa classe <b>non sollevi mai
/// eccezioni</b>: viene invocata dai gestori di ultima istanza, dove un errore non
/// ha più nessuno che lo raccolga. Fallire lì trasformerebbe il raccoglitore di
/// guasti nella causa del guasto peggiore, cioè la chiusura improvvisa con le
/// chiamate in corso.
/// </para>
/// <para>
/// La seconda è che il rapporto <b>non contenga dati personali</b>. È la differenza
/// fra un file che si può inviare subito e uno che richiede una valutazione sul
/// trattamento prima di uscire dall'ente.
/// </para>
/// </remarks>
public sealed class CrashReporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "BlindPhone.Test", $"crash-{Guid.NewGuid():N}");

    private CrashReporter Reporter() => new(_directory);

    [Fact]
    public void UnErroreProduceUnRapportoLeggibile()
    {
        var reporter = Reporter();

        var rapporto = reporter.Record(
            new InvalidOperationException("qualcosa e' andato storto"), "interfaccia", fatal: false);

        Assert.NotNull(rapporto);
        Assert.True(File.Exists(rapporto!.Path));

        var testo = File.ReadAllText(rapporto.Path);

        Assert.Contains("InvalidOperationException", testo, StringComparison.Ordinal);
        Assert.Contains("qualcosa e' andato storto", testo, StringComparison.Ordinal);
        Assert.Contains("interfaccia", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LaPrimaRigaBastaARiconoscereIlGuasto()
    {
        // E' quella che viene letta all'operatore e mostrata nell'elenco: deve
        // bastare a capire se e' lo stesso guasto gia' segnalato o uno nuovo.
        var rapporto = Reporter().Record(new TimeoutException("centralino non risponde"), "processo", fatal: true);

        Assert.NotNull(rapporto);
        Assert.Contains("TimeoutException", rapporto!.Headline, StringComparison.Ordinal);
        Assert.Contains("centralino non risponde", rapporto.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void LaCausaInternaCompareNelRapporto()
    {
        // Le eccezioni interne sono spesso la causa vera, e ToString() le annida in
        // fondo: chi legge un rapporto cerca quella riga.
        var errore = new InvalidOperationException(
            "apertura del canale non riuscita",
            new IOException("porta 5061 gia' in uso"));

        var rapporto = Reporter().Record(errore, "processo", fatal: false);

        var testo = File.ReadAllText(rapporto!.Path);

        Assert.Contains("Causa interna 1", testo, StringComparison.Ordinal);
        Assert.Contains("porta 5061 gia' in uso", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void IRapportiSiRitrovanoDalPiuRecente()
    {
        var reporter = Reporter();

        reporter.Record(new Exception("primo"), "processo", fatal: false);
        Thread.Sleep(1100);
        reporter.Record(new Exception("secondo"), "processo", fatal: false);

        var pendenti = reporter.Pending();

        Assert.Equal(2, pendenti.Count);
        Assert.Contains("secondo", pendenti[0].Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void DueErroriNelloStessoIstanteRestanoDue()
    {
        // Gli errori gravi arrivano a grappolo: un guasto che ne provoca altri due
        // nello stesso istante. Con i nomi dei file distinti al solo secondo, il
        // secondo rapporto sovrascriveva il primo — cioe' proprio la causa iniziale.
        var reporter = Reporter();

        reporter.Record(new Exception("causa"), "processo", fatal: false);
        reporter.Record(new Exception("conseguenza"), "processo", fatal: false);
        reporter.Record(new Exception("altra conseguenza"), "processo", fatal: false);

        Assert.Equal(3, reporter.Pending().Count);
    }

    [Fact]
    public void SenzaRapportiLElencoEVuotoENonSolleva()
    {
        // Il caso normale, e quello dell'avvio: la cartella non esiste nemmeno.
        Assert.Empty(Reporter().Pending());
    }

    [Fact]
    public void EliminareIRapportiLiTogliedalDisco()
    {
        var reporter = Reporter();

        reporter.Record(new Exception("uno"), "processo", fatal: false);
        reporter.Record(new Exception("due"), "processo", fatal: false);

        Assert.Equal(2, reporter.Clear());
        Assert.Empty(reporter.Pending());
    }

    [Fact]
    public void UnaCartellaNonScrivibileNonFaCadereIlProgramma()
    {
        // E' la proprieta' piu' importante: qui non c'e' piu' nessuno a raccogliere
        // un'eccezione.
        var reporter = new CrashReporter(Path.Combine("Z:", "cartella", "che", "non", "esiste"));

        var rapporto = reporter.Record(new Exception("prova"), "processo", fatal: true);

        Assert.Null(rapporto);
        Assert.Empty(reporter.Pending());
        Assert.Equal(0, reporter.Clear());
    }

    [Fact]
    public void IlRapportoNonContieneNumeriDiTelefono()
    {
        // Contiene tipo, messaggio, traccia e versione: nessun dato di traffico. E'
        // cio' che permette di inviarlo senza una valutazione preliminare.
        var rapporto = Reporter().Record(new Exception("errore generico"), "processo", fatal: false);
        var testo = File.ReadAllText(rapporto!.Path);

        Assert.Contains("Versione", testo, StringComparison.Ordinal);
        Assert.Contains("Sistema", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("password", testo, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Cartella temporanea trattenuta: non e' il soggetto della prova.
        }
    }
}
