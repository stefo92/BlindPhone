using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// All'avvio BlindPhone dice quale cuffia ha trovato e che cosa fa il suo pulsante.
/// </summary>
/// <remarks>
/// <para>
/// La ricerca della cuffia all'avvio c'era già, e la provano <c>CuffiaTests</c>: si
/// ascoltano le cuffie con i pulsanti telefonici, oppure solo quella scelta nelle
/// impostazioni. Mancava il riscontro, e l'operatore scopriva se il pulsante rispondeva
/// solo premendolo con il telefono che squillava.
/// </para>
/// <para>
/// Nessuna prova apre un dispositivo: le frasi sono funzioni pure, e una prova che
/// aprisse la cuffia vera dipenderebbe da che cosa è attaccato alla macchina.
/// </para>
/// </remarks>
public sealed class CuffiaAllAvvioTests
{
    [Fact]
    public void CuffiaTrovata_SiDiceCheIlPulsanteRisponde()
    {
        var testo = AnnouncementTexts.HeadsetAtStartup(
            "Plantronics Blackwire 5220 Series", canAnswer: true, chosenName: null);

        Assert.Equal(
            "Cuffia Plantronics Blackwire 5220 Series pronta: il pulsante della cuffia risponde e chiude le chiamate",
            testo);
    }

    [Fact]
    public void LaCuffiaSceltaCollegata_SiDiceComeLeAltre()
    {
        var testo = AnnouncementTexts.HeadsetAtStartup(
            "Plantronics Blackwire 5220 Series", canAnswer: true,
            chosenName: "Plantronics Blackwire 5220 Series");

        Assert.StartsWith("Cuffia Plantronics Blackwire 5220 Series pronta", testo);
    }

    [Fact]
    public void CuffiaConIlSoloMicrofono_SiDiceDiRispondereDallaTastiera()
    {
        var testo = AnnouncementTexts.HeadsetAtStartup("Cuffia semplice", canAnswer: false, chosenName: null);

        Assert.Equal(
            "Cuffia Cuffia semplice pronta: dalla cuffia funziona il tasto del microfono, per rispondere usa la tastiera",
            testo);
    }

    [Fact]
    public void CuffiaSceltaNonCollegata_SiDiceDiUsareLaTastiera()
    {
        // La cuffia passa a un collega e torna diversa: senza questa frase i pulsanti
        // della cuffia collegata non rispondono, e niente lo dice.
        var testo = AnnouncementTexts.HeadsetAtStartup(
            null, canAnswer: false, chosenName: "Plantronics Blackwire 5220 Series");

        Assert.Equal("Cuffia Plantronics Blackwire 5220 Series non collegata: per rispondere usa la tastiera", testo);
    }

    [Fact]
    public void NessunaCuffiaENessunaScelta_SiTace()
    {
        // Vivavoce o cuffia semplice: una frase a ogni avvio che non la riguarda.
        Assert.Null(AnnouncementTexts.HeadsetAtStartup(null, canAnswer: false, chosenName: null));
    }

    [Fact]
    public void LAvvioDellaCuffiaPortaIlRiscontro()
    {
        // La chiamata con atStartup e' cio' che fa parlare all'avvio: senza, la ricerca
        // c'e' ma l'operatore non sente nulla. Un cambio delle impostazioni invece non
        // la ripete: la cuffia appena scelta si prova premendone il pulsante.
        var sorgente = File.ReadAllText(Sorgente("HeadsetController.cs"));

        Assert.Contains("Apply(_settingsService.Current.Headset, atStartup: true)", sorgente);
        Assert.Contains("if (atStartup) AnnounceStartupState(settings)", sorgente);
        Assert.Contains("_dispatcher?.InvokeAsync(() => Apply(settings.Headset));", sorgente);
    }

    private static string Sorgente(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(directory.FullName, "src", "BlindPhone.App", file);
            if (File.Exists(candidato)) return candidato;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(file);
    }
}
