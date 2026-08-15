using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che il testo dei suggerimenti si legga, in tutti i temi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto non era un suggerimento vuoto: era testo bianco su bianco.</b>
/// Nessuno dichiarava l'aspetto del <c>ToolTip</c>, quindi il fondo restava quello
/// predefinito di Windows — un grigio chiarissimo, <c>#F1F2F7</c> — in tutti e tre i
/// temi, mentre il testo dentro prendeva lo stile implicito del <c>TextBlock</c>,
/// cioè il colore del tema. Nel tema scuro quel colore è <c>#F2F2F2</c>: rapporto di
/// contrasto circa 1 a 1, riquadro apparentemente vuoto.
/// </para>
/// <para>
/// È il tipo di difetto che nessun collaudo testuale nota, perché non c'è nulla di
/// sbagliato scritto da nessuna parte: c'è una dichiarazione che manca. Il test
/// verifica quindi due cose diverse — che lo stile esista, e che i due colori che
/// mette uno sull'altro stacchino davvero.
/// </para>
/// <para>
/// La soglia è 7 a 1, il livello AAA delle linee guida, ed è quella che il prodotto
/// applica altrove: la postazione è di un operatore ipovedente o non vedente, e i
/// colleghi vedenti che gli siedono accanto leggono lo stesso schermo.
/// </para>
/// </remarks>
public sealed class SuggerimentiTests
{
    private static string Tema(string nome)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(directory.FullName, "src", "BlindPhone.App", "Temi", nome);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Temi/{nome} non trovato");
    }

    /// <summary>Blocco dello stile del suggerimento.</summary>
    private static string Stile()
    {
        var stili = Tema("Stili.xaml");

        var inizio = stili.IndexOf("<Style TargetType=\"ToolTip\">", StringComparison.Ordinal);
        Assert.True(inizio > 0, "Manca lo stile del suggerimento: senza, il fondo resta quello di Windows.");

        var fine = stili.IndexOf("</Style>", inizio, StringComparison.Ordinal);
        return stili[inizio..fine];
    }

    /// <summary>Colore dichiarato per una chiave, quando è scritto per esteso.</summary>
    private static string? Colore(string tema, string chiave)
    {
        var m = Regex.Match(
            Tema(tema),
            $@"x:Key=""{Regex.Escape(chiave)}""\s+Color=""(#[0-9A-Fa-f]{{6,8}})""");

        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Rapporto di contrasto secondo le linee guida WCAG.</summary>
    private static double Contrasto(string primo, string secondo)
    {
        var a = Luminanza(primo);
        var b = Luminanza(secondo);

        var chiaro = Math.Max(a, b);
        var scuro = Math.Min(a, b);

        return (chiaro + 0.05) / (scuro + 0.05);
    }

    private static double Luminanza(string esadecimale)
    {
        var cifre = esadecimale.TrimStart('#');
        if (cifre.Length == 8) cifre = cifre[2..];

        double Canale(int posizione)
        {
            var valore = int.Parse(
                cifre.Substring(posizione, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

            return valore <= 0.03928 ? valore / 12.92 : Math.Pow((valore + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Canale(0)) + (0.7152 * Canale(2)) + (0.0722 * Canale(4));
    }

    [Fact]
    public void IlSuggerimentoDichiaraFondoETesto()
    {
        // Il fondo e' la meta' che mancava: senza dichiararlo restava quello di
        // Windows, uguale in tutti i temi, mentre il testo seguiva il tema.
        var stile = Stile();

        Assert.Contains("Property=\"Background\"", stile, StringComparison.Ordinal);
        Assert.Contains("Property=\"Foreground\"", stile, StringComparison.Ordinal);
    }

    [Fact]
    public void IlColoreDelTestoEDichiaratoAncheDentroIlModello()
    {
        // Uno stile implicito con un Foreground esplicito vince sull'ereditarieta':
        // dichiarare il solo Foreground sul suggerimento non arriverebbe al TextBlock
        // che il contenuto genera, ed e' esattamente cio' che rendeva invisibile il
        // testo. La ridichiarazione dentro il modello non e' ridondante.
        var stile = Stile();

        var modello = stile.IndexOf("<ControlTemplate", StringComparison.Ordinal);
        Assert.True(modello > 0, "Lo stile del suggerimento deve portare il proprio modello.");

        Assert.Contains("<Style TargetType=\"TextBlock\">", stile[modello..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Chiaro.xaml")]
    [InlineData("Scuro.xaml")]
    public void IlTestoStaccaDalFondoDelSuggerimento(string tema)
    {
        // Il tema di sistema non compare: i suoi colori sono quelli di Windows e non
        // si possono leggere da qui. Li' comanda chi ha configurato il contrasto per
        // la propria vista, ed e' giusto cosi'.
        var stile = Stile();

        string Chiave(string proprieta)
        {
            var m = Regex.Match(
                stile,
                $@"Property=""{proprieta}""\s+Value=""\{{DynamicResource ([A-Za-z]+)\}}""");

            Assert.True(m.Success, $"Lo stile del suggerimento non aggancia {proprieta} al tema.");
            return m.Groups[1].Value;
        }

        var fondo = Colore(tema, Chiave("Background"));
        var testo = Colore(tema, Chiave("Foreground"));

        Assert.NotNull(fondo);
        Assert.NotNull(testo);

        var contrasto = Contrasto(fondo!, testo!);

        Assert.True(contrasto >= 7.0,
            $"Nel tema {tema} il testo del suggerimento e' {testo} su fondo {fondo}: "
            + $"rapporto {contrasto:0.0} a 1, sotto il 7 a 1 richiesto. "
            + "E' il difetto per cui il riquadro sembrava vuoto.");
    }

    [Fact]
    public void IlRiquadroNonCresceOltreLoSchermo()
    {
        // I suggerimenti dei sei comandi portano spiegazione e scorciatoia su due
        // righe, e chi ingrandisce il testo di sistema le vede crescere: senza un
        // limite il riquadro si allunga invece di andare a capo.
        Assert.Contains("Property=\"MaxWidth\"", Stile(), StringComparison.Ordinal);
    }
}
