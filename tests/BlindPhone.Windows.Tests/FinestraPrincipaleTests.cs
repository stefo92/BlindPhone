using System.Xml.Linq;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// La finestra principale dopo la riprogettazione: fascia di stato fissa, due barre dei
/// comandi, una o due colonne, elenchi che vanno a capo.
/// </summary>
/// <remarks>
/// <para>
/// Quasi tutto quello che queste prove difendono non si vede: l'ordine in cui il
/// tabulatore attraversa la finestra, il nome con cui lo screen reader presenta una
/// barra, il fatto che una fascia colorata non parli da sola. Sono le cose che si
/// rimettono «in ordine» guardando lo schermo senza accorgersi del danno.
/// </para>
/// <para>
/// Come le altre prove sui file di interfaccia, leggono il XAML e non avviano niente.
/// </para>
/// </remarks>
public sealed class FinestraPrincipaleTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string Percorso(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(directory.FullName, "src", "BlindPhone.App", file);
            if (File.Exists(candidato)) return candidato;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{file} non trovato");
    }

    private static XDocument Finestra() => XDocument.Load(Percorso("MainWindow.xaml"));

    private static XElement Nominato(XDocument documento, string nome)
        => documento.Descendants().Single(e => (string?)e.Attribute(X + "Name") == nome);

    private static bool InModello(XElement e)
        => e.Ancestors().Any(a => a.Name.LocalName is "ControlTemplate" or "Style" or "DataTemplate");

    /// <summary>
    /// Gli elementi che il tabulatore attraversa, nell'ordine del documento: è l'ordine
    /// che WPF segue quando nessuno scrive un indice di tabulazione.
    /// </summary>
    private static List<string> TappeDiTabulazione(XDocument documento)
    {
        string[] tappe = ["Button", "TextBox", "ListView", "ItemsControl", "DataGrid"];

        return documento.Descendants()
            .Where(e => e.Name.Namespace == P && tappe.Contains(e.Name.LocalName) && !InModello(e))
            .Select(e => (string?)e.Attribute(X + "Name"))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
    }

    // -----------------------------------------------------------------------
    // La fascia di stato
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("PauseBanner")]
    [InlineData("IncomingCallBanner")]
    [InlineData("ActiveCallBanner")]
    [InlineData("ActiveCallText")]
    public void GliAvvisiATuttaLarghezzaNonCiSonoPiu(string nome)
    {
        // Comparivano in cima a tutto e spingevano giu' la finestra a ogni chiamata: il
        // punto che chi lavora con l'ingrandimento stava guardando se ne andava proprio
        // quando succedeva qualcosa.
        Assert.DoesNotContain(
            Finestra().Descendants(),
            e => (string?)e.Attribute(X + "Name") == nome);
    }

    [Fact]
    public void LaFasciaStaInCimaAllaSchedaDellaChiamata()
    {
        var documento = Finestra();
        var fascia = Nominato(documento, "StatusBand");
        var scheda = Nominato(documento, "CallCard");

        Assert.Contains(scheda, fascia.Ancestors());
        Assert.Equal("0", (string?)fascia.Attribute("Grid.Row"));
    }

    [Fact]
    public void LaFasciaNonEUnaRegioneDinamica()
    {
        // Lo screen reader riceve gia' l'annuncio di ogni cambiamento: dichiararla
        // farebbe dire due volte la stessa cosa, e il cronometro farebbe parlare ogni
        // secondo.
        var fascia = Nominato(Finestra(), "StatusBand");

        Assert.DoesNotContain(
            fascia.DescendantsAndSelf(),
            e => e.Attribute("AutomationProperties.LiveSetting") is not null);
    }

    [Fact]
    public void LaFasciaNonVaACapoEQuindiNonCambiaAltezza()
    {
        var fascia = Nominato(Finestra(), "StatusBand");

        foreach (var nome in new[] { "StatusBandIcon", "StatusBandWord", "StatusBandTimer" })
        {
            Assert.Contains(fascia.Descendants(), e => (string?)e.Attribute(X + "Name") == nome);
        }

        // Lo stile dei testi della fascia vieta l'a capo: una parola che andasse su due
        // righe allungherebbe la fascia e sposterebbe tutto quello che sta sotto.
        var stile = fascia.Descendants(P + "Style").Single();
        Assert.Contains(
            stile.Elements(P + "Setter"),
            s => (string?)s.Attribute("Property") == "TextWrapping" && (string?)s.Attribute("Value") == "NoWrap");
    }

    [Fact]
    public void IlPittogrammaDellaFasciaEMuto()
    {
        var icona = Nominato(Finestra(), "StatusBandIcon");

        Assert.Equal(string.Empty, (string?)icona.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void ColoriDellaFasciaStannoInTuttiETreITemi()
    {
        string[] chiavi =
        [
            "LineAlertTextBrush", "LineRingingBrush", "LineTalkingBrush", "LineHoldBrush", "LineOutOfServiceBrush",
        ];

        foreach (var tema in new[] { "Chiaro", "Scuro", "Sistema" })
        {
            var testo = File.ReadAllText(Percorso(Path.Combine("Temi", tema + ".xaml")));

            Assert.All(chiavi, k => Assert.Contains($"x:Key=\"{k}\"", testo, StringComparison.Ordinal));
        }

        // E il code-behind li richiama per nome, senza scriverli: un colore scritto nel
        // codice sopravviverebbe al cambio di tema e al contrasto elevato.
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));
        Assert.All(chiavi.Skip(1), k => Assert.Contains($"\"{k}\"", codice, StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Le due barre dei comandi
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("StationCommandBar", "Comandi della postazione")]
    [InlineData("CallCommandBar", "Comandi della chiamata")]
    public void OgniBarraEUnaTappaSolaConUnNomeSuo(string nome, string nomeAccessibile)
    {
        var documento = Finestra();
        var barra = Nominato(documento, nome);

        Assert.Equal(nomeAccessibile, (string?)barra.Attribute("AutomationProperties.Name"));
        Assert.False(string.IsNullOrWhiteSpace((string?)barra.Attribute("AutomationProperties.HelpText")));

        // La regola della tappa sola sta nello stile comune delle due barre.
        Assert.Equal("{StaticResource CommandBar}", (string?)barra.Attribute("Style"));

        var stile = documento.Descendants(P + "Style").Single(s => (string?)s.Attribute(X + "Key") == "CommandBar");
        var setter = stile.Elements(P + "Setter").ToDictionary(
            s => (string?)s.Attribute("Property") ?? string.Empty,
            s => (string?)s.Attribute("Value"));

        Assert.Equal("Once", setter["KeyboardNavigation.TabNavigation"]);
        Assert.Equal("Cycle", setter["KeyboardNavigation.DirectionalNavigation"]);
    }

    [Fact]
    public void LaVecchiaBarraUnicaNonCEPiu()
        => Assert.DoesNotContain(Finestra().Descendants(), e => (string?)e.Attribute(X + "Name") == "CommandBar");

    [Fact]
    public void LaBarraDellaPostazioneStaNellaSchedaDellaPostazione()
    {
        var documento = Finestra();

        Assert.Contains(Nominato(documento, "StationCard"), Nominato(documento, "StationCommandBar").Ancestors());
        Assert.Contains(Nominato(documento, "StationCard"), Nominato(documento, "ServicePauseButton").Ancestors());
        Assert.Contains(Nominato(documento, "CallCard"), Nominato(documento, "CallCommandBar").Ancestors());
    }

    [Fact]
    public void IlFocusTornaSullaBarraQuandoVieneRicostruita()
    {
        // Ricostruita sotto il cursore, la barra se lo porterebbe via: chi si muove a
        // tastiera ricomincerebbe il giro da capo proprio quando la situazione cambia.
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));

        var inizio = codice.IndexOf("private void RebuildBar(", StringComparison.Ordinal);
        Assert.True(inizio > 0, "RebuildBar non esiste piu'");

        var corpo = codice[inizio..Math.Min(codice.Length, inizio + 3500)];

        Assert.Contains("IsKeyboardFocusWithin", corpo, StringComparison.Ordinal);
        Assert.Contains("primo.Focus();", corpo, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Le due caselle e l'ordine di tabulazione
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("ConnectedToBox", "Chiamata in conversazione")]
    [InlineData("OnHoldBox", "Chiamate in attesa")]
    public void LeCaselleRestanoCaselleDiSolaLetturaConIlLoroNome(string nome, string nomeAccessibile)
    {
        // Si percorrono con le frecce, si seleziona il numero e lo si copia altrove: da
        // un'etichetta non si potrebbe.
        var casella = Nominato(Finestra(), nome);

        Assert.Equal(P + "TextBox", casella.Name);
        Assert.Equal("True", (string?)casella.Attribute("IsReadOnly"));
        Assert.Equal("True", (string?)casella.Attribute("IsReadOnlyCaretVisible"));
        Assert.Equal(nomeAccessibile, (string?)casella.Attribute("AutomationProperties.Name"));
        Assert.Null(casella.Attribute("AutomationProperties.LiveSetting"));
    }

    [Fact]
    public void LOrdineDiTabulazioneSegueLOrdineVisivo()
    {
        // Postazione, chiamata, coda, colleghi, composizione: lo stesso ordine in cui
        // l'occhio legge la finestra, larga o stretta. La casella del nome resta la terza
        // tappa, dopo la pausa e i comandi, come quando la barra era una sola.
        var tappe = TappeDiTabulazione(Finestra());

        string[] attese =
        [
            "ServicePauseButton", "StationCommandBar",
            "ConnectedToBox", "CallCommandBar", "OnHoldBox",
            "QueueList", "ColleaguesList",
            "DialerBox", "DialButton", "SuggestionsGrid",
        ];

        Assert.Equal(attese, tappe);
    }

    [Fact]
    public void LaDisposizioneCambiaSoloRigaEColonna()
    {
        // Nessun elemento cambia contenitore: e' cio' che tiene fermo l'ordine di
        // tabulazione quando la finestra passa da una a due colonne.
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));

        var inizio = codice.IndexOf("private void ApplyWorkAreaLayout()", StringComparison.Ordinal);
        Assert.True(inizio > 0, "ApplyWorkAreaLayout non esiste piu'");

        var fine = codice.IndexOf("\n    }\n", inizio, StringComparison.Ordinal);
        if (fine < 0) fine = codice.IndexOf("\r\n    }\r\n", inizio, StringComparison.Ordinal);
        var corpo = codice[inizio..fine];

        Assert.Contains("MainWindowLayout.ArrangeWorkArea", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("Children.Remove", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("Children.Add", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.SetRowSpan", corpo, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Elenchi
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("QueueList")]
    [InlineData("ColleaguesList")]
    public void LeVociSiLeggonoComePrima(string nome)
    {
        // Cambia come si vedono, non come si sentono: il nome accessibile della voce
        // resta il testo composto, letto tutto in una volta.
        var documento = Finestra();
        var elenco = Nominato(documento, nome);

        Assert.Equal("{StaticResource WatchedItem}", (string?)elenco.Attribute("ItemContainerStyle"));

        var stile = documento.Descendants(P + "Style").Single(s => (string?)s.Attribute(X + "Key") == "WatchedItem");

        Assert.Contains(
            stile.Elements(P + "Setter"),
            s => (string?)s.Attribute("Property") == "AutomationProperties.Name"
                 && (string?)s.Attribute("Value") == "{Binding AccessibleText}");
    }

    [Fact]
    public void LoStatoDeiColleghiEUnaParolaPiuUnSegnoMuto()
    {
        var modello = Finestra().Descendants(P + "DataTemplate")
            .Single(t => (string?)t.Attribute(X + "Key") == "ColleagueItemTemplate");

        var testi = modello.Descendants(P + "TextBlock").ToList();

        Assert.Contains(testi, t => (string?)t.Attribute("Text") == "{Binding Status}");

        var segno = Assert.Single(testi, t => (string?)t.Attribute("Text") == "{Binding Icon}");
        Assert.Equal(string.Empty, (string?)segno.Attribute("AutomationProperties.Name"));

        // Lo stato viene prima del nome: e' l'informazione per cui si guarda l'elenco.
        Assert.True(
            testi.FindIndex(t => (string?)t.Attribute("Text") == "{Binding Status}")
            < testi.FindIndex(t => (string?)t.Attribute("Text") == "{Binding Name}"));
    }

    [Fact]
    public void IlColoreDellAccentoNonEDichiaratoAMano()
    {
        // Un solo pulsante colorato, quello che chiama: la fascia ha colori suoi ma li
        // prende dai temi, e nessun altro pulsante della finestra ne dichiara uno.
        var documento = Finestra();

        var colorati = documento.Descendants(P + "Setter")
            .Where(s => (string?)s.Attribute("Property") == "Background"
                        && ((string?)s.Attribute("Value"))?.StartsWith('#') == true)
            .Select(s => (string?)s.Parent?.Attribute(X + "Key"))
            .ToList();

        Assert.Equal(new[] { "PrimaryButton" }, colorati);
    }
}
