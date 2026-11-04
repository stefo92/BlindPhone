using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la scheda Rubrica come procedura di importazione: quali passi ci sono,
/// come si numerano e dove si sceglie che cosa compare nella ricerca rapida.
/// </summary>
/// <remarks>
/// <para>
/// <b>I campi si scelgono una volta sola.</b> C'era un secondo elenco, «Campi mostrati
/// nella ricerca dal campo di composizione», che chiedeva di nuovo quello che
/// l'abbinamento delle colonne aveva già deciso: un reparto non abbinato non arriva in
/// rubrica, e la sua colonna sarebbe stata vuota comunque. Ora le colonne seguono la
/// rubrica, e l'abbinamento lo dice.
/// </para>
/// <para>
/// <b>Ogni passo che non serve è una serie di tabulazioni.</b> Chi ascolta attraversa
/// la scheda con Tab: nove caselle di abbinamento vuote prima di aver scelto un file,
/// o con la directory che i campi li ha già per nome, erano nove fermate per niente.
/// </para>
/// </remarks>
public sealed class ImportazioneRubricaTests
{
    private static string Pannello()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(
                directory.FullName, "src", "BlindPhone.App", "SettingsWindow.xaml");

            if (File.Exists(candidato)) return File.ReadAllText(candidato);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("SettingsWindow.xaml non trovato");
    }

    private static string Scheda(string xaml)
    {
        var scheda = xaml.IndexOf("x:Name=\"ContactsTab\"", StringComparison.Ordinal);
        Assert.True(scheda > 0, "Scheda Rubrica non trovata");

        var successiva = xaml.IndexOf("<TabItem", scheda, StringComparison.Ordinal);
        return xaml[scheda..(successiva < 0 ? xaml.Length : successiva)];
    }

    [Fact]
    public void NonCEUnSecondoElencoDeiCampiDellaRicercaRapida()
    {
        // La scelta e' quella dell'abbinamento: un elenco a parte andava tenuto
        // allineato a mano, e a ogni importazione diversa smetteva di esserlo.
        var scheda = Scheda(Pannello());

        Assert.DoesNotContain("DialerField", scheda, StringComparison.Ordinal);
        Assert.DoesNotContain("ricerca rapida, in ordine di lettura", scheda, StringComparison.Ordinal);
    }

    [Fact]
    public void LAbbinamentoDiceCheDecideAncheLaRicercaRapida()
    {
        // Senza questa frase la scelta si sposterebbe in silenzio: chi cercava il
        // vecchio elenco non saprebbe dove sia finita.
        var scheda = Scheda(Pannello());

        var passo = scheda.IndexOf("x:Name=\"MappingStep\"", StringComparison.Ordinal);
        var griglia = scheda.IndexOf("x:Name=\"MappingGrid\"", StringComparison.Ordinal);

        Assert.True(passo > 0 && griglia > passo, "Passo dell'abbinamento non trovato");
        Assert.Contains("campo di composizione", scheda[passo..griglia], StringComparison.Ordinal);
    }

    [Fact]
    public void IPassiSiNumeranoDaCodice()
    {
        // L'abbinamento compare e scompare: con i numeri scritti fissi la sequenza
        // salterebbe da 1 a 3 per chi la ascolta.
        var scheda = Scheda(Pannello());

        foreach (var nome in new[] { "StepFileHeading", "StepMappingHeading", "StepPreviewHeading", "StepImportHeading" })
        {
            Assert.Contains($"x:Name=\"{nome}\"", scheda, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LAbbinamentoParteNascostoEINumeriSonoQuelliSenzaDiLui()
    {
        // Prima di aver scelto un file non c'e' niente da abbinare. I numeri scritti
        // nel file devono essere quelli di quello stato, che e' il primo che si vede.
        var scheda = Scheda(Pannello());

        Assert.Matches(@"x:Name=""MappingStep""\s+Visibility=""Collapsed""", scheda);
        Assert.Contains("Text=\"2. Anteprima\"", scheda, StringComparison.Ordinal);
        Assert.Contains("Text=\"3. Importazione\"", scheda, StringComparison.Ordinal);
    }

    [Fact]
    public void IlPassoDelFileRaccoglieSoloCioCheServeAlFile()
    {
        // Il passo si nasconde con la rubrica collegata a server, directory o Outlook.
        // La scelta della tabella deve restarne fuori: serve anche al server, e
        // nascosta con il file lascerebbe il server senza tabella da scegliere.
        var scheda = Scheda(Pannello());

        var inizio = scheda.IndexOf("x:Name=\"FileStep\"", StringComparison.Ordinal);
        Assert.True(inizio > 0, "Passo del file non trovato");

        var tabella = scheda.IndexOf("x:Name=\"BackupTablePanel\"", StringComparison.Ordinal);
        Assert.True(tabella > inizio, "Scelta della tabella non trovata dopo il passo del file");

        var passo = scheda[inizio..tabella];
        var fine = passo.LastIndexOf("</StackPanel>", StringComparison.Ordinal);
        Assert.True(fine > 0, "Il passo del file non si chiude prima della scelta della tabella");

        foreach (var nome in new[] { "StepFileHeading", "CsvPath", "BrowseCsvButton", "CsvFormatText", "CsvNoHeader" })
        {
            Assert.Contains($"x:Name=\"{nome}\"", passo[..fine], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NonCEPiuIlPulsanteAnalizza()
    {
        // Dopo Sfoglia l'analisi partiva gia' da sola; a chi scrive il percorso basta
        // Invio, che prima salvava e chiudeva le impostazioni.
        var scheda = Scheda(Pannello());

        Assert.DoesNotContain("AnalyzeCsvButton", scheda, StringComparison.Ordinal);

        var casella = Regex.Match(scheda, @"x:Name=""CsvPath""[^>]*>", RegexOptions.Singleline);
        Assert.True(casella.Success, "Casella del percorso non trovata");
        Assert.Contains("PreviewKeyDown=\"OnCsvPathPreviewKeyDown\"", casella.Value, StringComparison.Ordinal);
        Assert.Contains("LostFocus=\"OnCsvPathLostFocus\"", casella.Value, StringComparison.Ordinal);
        Assert.Contains("Invio", casella.Value, StringComparison.Ordinal);
    }
}
