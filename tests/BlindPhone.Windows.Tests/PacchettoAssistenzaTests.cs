using System.IO.Compression;
using System.Text;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica il pacchetto per l'assistenza dall'inizio alla fine.
/// </summary>
/// <remarks>
/// <para>
/// Le prove di <see cref="AnonimizzazioneRegistriTests"/> verificano l'anonimizzatore
/// riga per riga. Queste verificano il <b>file che esce davvero dall'ente</b>: si
/// costruisce il pacchetto, lo si riapre e si cerca dentro il numero di telefono.
/// È la sola prova che copra anche il modo in cui l'anonimizzatore viene invocato,
/// e non solo quello che fa quando lo si invoca.
/// </para>
/// <para>
/// Nasce da un difetto vero, trovato l'11 agosto 2026: il pannello delle impostazioni
/// aveva una <i>seconda</i> costruzione dell'archivio, scritta prima e mai
/// riallineata, che copiava i registri così com'erano. Le note della 1.0.8
/// dichiaravano che i registri allegati escono anonimi; da quel pulsante non era
/// vero, e nessuna prova se ne accorgeva perché tutte guardavano la classe giusta.
/// </para>
/// </remarks>
public sealed class PacchettoAssistenzaTests : IDisposable
{
    private const string RecapitoEsterno = "3351234567";
    private const string Interno = "4021";

    private readonly string _radice = Path.Combine(
        Path.GetTempPath(), "BlindPhone.Test", $"pacchetto-{Guid.NewGuid():N}");

    private string Dati => Path.Combine(_radice, "dati");

    private string Registri => Path.Combine(Dati, "log");

    private SupportPackageService Servizio()
    {
        var impostazioni = new SettingsService(Dati);
        var registro = new FileLoggerProvider(Registri);
        var errori = new CrashReporter(Dati);

        return new SupportPackageService(impostazioni, registro, errori);
    }

    /// <summary>Scrive un registro con dentro un recapito vero, come quelli in esercizio.</summary>
    private void ScriviRegistro()
    {
        Directory.CreateDirectory(Registri);

        File.WriteAllLines(
            Path.Combine(Registri, $"blindphone-{DateTime.Now:yyyyMMdd}.log"),
            [
                $"2026-08-11 09:12:03.100 INF SipTelephonyEngine  Chiamata entrante call-1: da +39{RecapitoEsterno} a sip:centralino.example.com",
                $"From: \"Mario Rossi\" <sip:+39{RecapitoEsterno}@centralino.example.com>;tag=ABC",
                $"Refer-To: sip:{Interno}@centralino.example.com",
                "2026-08-11 09:12:41.900 INF SipTelephonyEngine  CSeq: 3351 INVITE, in ascolto sulla porta 5061",
            ],
            Encoding.UTF8);
    }

    private static string Leggi(ZipArchive archivio, string voce)
    {
        var entry = archivio.GetEntry(voce);
        Assert.NotNull(entry);

        using var lettore = new StreamReader(entry!.Open(), Encoding.UTF8);
        return lettore.ReadToEnd();
    }

    [Fact]
    public void NelPacchettoNonSopravviveNessunNumeroDiTelefono()
    {
        ScriviRegistro();

        var destinazione = Path.Combine(_radice, "pacchetto.zip");
        var esito = Servizio().Build(destinazione, "Riepilogo diagnostico di prova");

        Assert.True(esito.Success, esito.Message);

        using var archivio = ZipFile.OpenRead(destinazione);
        var registro = Leggi(archivio, $"log/blindphone-{DateTime.Now:yyyyMMdd}.log");

        Assert.DoesNotContain(RecapitoEsterno, registro, StringComparison.Ordinal);
        Assert.DoesNotContain("Mario Rossi", registro, StringComparison.Ordinal);
        Assert.Contains("ESTERNO-", registro, StringComparison.Ordinal);
        Assert.Contains("INTERNO-", registro, StringComparison.Ordinal);
        Assert.Contains("NOMINATIVO-", registro, StringComparison.Ordinal);
    }

    [Fact]
    public void IlRegistroRestaLeggibileDopoIlPassaggio()
    {
        // La seconda meta' della promessa: un pacchetto senza recapiti ma illeggibile
        // non serve alla diagnosi per cui viene creato.
        ScriviRegistro();

        var destinazione = Path.Combine(_radice, "pacchetto.zip");
        Servizio().Build(destinazione, "Riepilogo diagnostico di prova");

        using var archivio = ZipFile.OpenRead(destinazione);
        var registro = Leggi(archivio, $"log/blindphone-{DateTime.Now:yyyyMMdd}.log");

        Assert.Contains("Chiamata entrante call-1", registro, StringComparison.Ordinal);
        Assert.Contains("centralino.example.com", registro, StringComparison.Ordinal);
        Assert.Contains("2026-08-11 09:12:03.100", registro, StringComparison.Ordinal);

        // Il contatore di transazione e la porta non sono recapiti e devono restare
        // interi: senza, la sequenza dei messaggi SIP non si ricostruisce piu'.
        Assert.Contains("CSeq: 3351 INVITE", registro, StringComparison.Ordinal);
        Assert.Contains("porta 5061", registro, StringComparison.Ordinal);
    }

    [Fact]
    public void IlRiepilogoDiagnosticoEntraNelPacchetto()
    {
        ScriviRegistro();

        var destinazione = Path.Combine(_radice, "pacchetto.zip");
        Servizio().Build(destinazione, "BlindPhone 1.0.9 beta, postazione di prova");

        using var archivio = ZipFile.OpenRead(destinazione);

        Assert.Contains(
            "BlindPhone 1.0.9 beta",
            Leggi(archivio, "riepilogo-diagnostico.txt"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaRegistriIlPacchettoSiCreaLoStesso()
    {
        // Il pannello puo' essere aperto su una postazione appena installata: un
        // riepilogo da solo e' comunque meglio di un errore.
        Directory.CreateDirectory(Dati);

        var destinazione = Path.Combine(_radice, "pacchetto.zip");
        var esito = Servizio().Build(destinazione, "Riepilogo");

        Assert.True(esito.Success, esito.Message);
        Assert.True(File.Exists(destinazione));
    }

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
