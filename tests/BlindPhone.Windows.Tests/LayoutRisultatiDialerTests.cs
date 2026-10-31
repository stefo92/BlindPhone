using System.Xml.Linq;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// I contatti proposti dal campo di composizione si vedono anche su uno schermo basso.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto.</b> L'elenco dei contatti aveva una riga sua, sotto il campo di
/// composizione, che a sua volta sta in fondo alla finestra. Con la finestra non a
/// tutto schermo su un monitor piccolo non c'era posto: l'elenco compariva tagliato, o
/// non compariva, mentre la voce annunciava «tre contatti». La finestra si allarga da
/// sé quando il contenuto non ci sta, ma solo fino al bordo dello schermo, e le due
/// liste che potevano cedere spazio hanno già un'altezza minima.
/// </para>
/// <para>
/// <b>Misurato su una finestra vera</b>, con coda e una sola lista: a 689 pixel di area
/// utile — uno schermo da 768 righe — l'elenco con tre contatti lasciava fuori metà
/// delle righe e faceva sparire l'ultimo annuncio. Con l'elenco sovrapposto
/// l'altezza che serve alla finestra non dipende più dai risultati: restano 640 pixel
/// con una lista, 460 senza liste, qualunque sia il numero dei contatti.
/// </para>
/// <para>
/// Queste prove non possono misurare l'impaginazione, che richiede una finestra viva e
/// uno schermo: guardano la <b>struttura</b> da cui l'impaginazione discende, che è la
/// cosa che qualcuno potrebbe rimettere a posto «per ordine» senza vedere il danno.
/// Come le altre prove sui file di interfaccia, non avviano niente.
/// </para>
/// </remarks>
public sealed class LayoutRisultatiDialerTests
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

    // -----------------------------------------------------------------------
    // L'elenco e' sovrapposto: non ha una riga
    // -----------------------------------------------------------------------

    [Fact]
    public void LElencoNonHaUnaRigaDellaGriglia()
    {
        var elenco = Nominato(Finestra(), "SuggestionsGrid");

        // Una riga della griglia significa altezza che la finestra deve trovare: ed e'
        // esattamente cio' che mancava su uno schermo basso.
        Assert.Null(elenco.Attribute("Grid.Row"));
    }

    [Fact]
    public void LElencoStaInUnaTelaCheNonOccupaAltezza()
    {
        var elenco = Nominato(Finestra(), "SuggestionsGrid");

        var tela = elenco.Parent;
        Assert.NotNull(tela);
        Assert.Equal(P + "Canvas", tela!.Name);

        // Altezza zero: la tela non concorre alla misura della riga in cui sta, e cio'
        // che contiene si disegna fuori dai suoi confini.
        Assert.Equal("0", (string?)tela.Attribute("Height"));
    }

    [Fact]
    public void LElencoSiApreVersoLAltoAPartireDalCampo()
    {
        var elenco = Nominato(Finestra(), "SuggestionsGrid");

        // Ancorato al bordo basso della tela, cioe' al bordo alto del campo: si apre
        // verso l'alto, dove c'e' sempre posto, e non verso il fondo della finestra,
        // dove non ce n'e'. Con un ancoraggio in alto l'elenco scenderebbe sotto il
        // campo e tornerebbe a uscire dalla finestra.
        Assert.NotNull(elenco.Attribute("Canvas.Bottom"));
        Assert.Null(elenco.Attribute("Canvas.Top"));
    }

    [Fact]
    public void LElencoRestaNellOrdineDiLetturaDiPrima()
    {
        var documento = Finestra();
        var elenco = Nominato(documento, "SuggestionsGrid");
        var campo = Nominato(documento, "DialerBox");
        var pulsante = Nominato(documento, "DialButton");

        // Stesso contenitore del campo e dopo il pulsante: l'ordine di tabulazione e
        // quello con cui lo screen reader legge sono campo, pulsante, elenco, come
        // prima dello spostamento. Messo in fondo alla finestra, o prima del campo,
        // l'elenco cambierebbe posto nel giro dei tasti senza che nessuno lo dica.
        var contenitore = campo.Parent!;
        Assert.Same(contenitore, pulsante.Parent);

        var tela = elenco.Parent!;
        Assert.Same(contenitore, tela.Parent);

        var figli = contenitore.Elements().ToList();
        Assert.True(figli.IndexOf(campo) < figli.IndexOf(pulsante), "Il campo deve precedere il pulsante");
        Assert.True(figli.IndexOf(pulsante) < figli.IndexOf(tela), "L'elenco deve venire dopo il pulsante");
    }

    [Fact]
    public void LElencoHaUnContornoChiaroSopraLeListe()
    {
        var elenco = Nominato(Finestra(), "SuggestionsGrid");

        // Sovrapposto a una lista, un elenco senza un contorno proprio sembra
        // appartenere a quella: il filo di un pixel dei riquadri non basta a
        // distinguerlo, e il colore di evidenziazione e' lo stesso che la finestra usa
        // per dire «questa e' l'area attiva».
        Assert.Contains("AccentBrush", (string?)elenco.Attribute("BorderBrush"), StringComparison.Ordinal);
        Assert.True(double.Parse((string?)elenco.Attribute("BorderThickness") ?? "0",
            System.Globalization.CultureInfo.InvariantCulture) >= 2);
    }

    // -----------------------------------------------------------------------
    // Coerenza della griglia principale
    // -----------------------------------------------------------------------

    [Fact]
    public void OgniElementoDellaGrigliaPrincipaleStaInUnaRigaCheEsiste()
    {
        var griglia = Nominato(Finestra(), "RootGrid");

        var righe = griglia.Element(P + "Grid.RowDefinitions")!.Elements(P + "RowDefinition").Count();

        var fuori = griglia.Elements()
            .Where(e => e.Name != P + "Grid.RowDefinitions")
            .Select(e => (Elemento: e, Riga: int.Parse((string?)e.Attribute("Grid.Row") ?? "0")))
            .Where(x => x.Riga >= righe)
            .Select(x => $"{x.Elemento.Name.LocalName} in riga {x.Riga}")
            .ToList();

        // Le righe fuori dalla griglia non producono un errore: WPF le mette
        // nell'ultima, e due elementi si sovrappongono senza che nessuno se ne accorga.
        Assert.True(fuori.Count == 0, $"Elementi in righe che non esistono ({righe} righe): " + string.Join("; ", fuori));
    }

    [Fact]
    public void NessunaRigaDellaGrigliaPrincipaleRestaVuota()
    {
        var griglia = Nominato(Finestra(), "RootGrid");

        var righe = griglia.Element(P + "Grid.RowDefinitions")!.Elements(P + "RowDefinition").Count();

        var occupate = griglia.Elements()
            .Where(e => e.Name != P + "Grid.RowDefinitions")
            .Select(e => int.Parse((string?)e.Attribute("Grid.Row") ?? "0"))
            .ToHashSet();

        var vuote = Enumerable.Range(0, righe).Where(r => !occupate.Contains(r)).ToList();

        // Una riga senza niente dentro e' quella che l'elenco aveva e non ha piu': a
        // altezza automatica non pesa, ma lascia credere che li' ci sia qualcosa.
        Assert.True(vuote.Count == 0, "Righe senza nessun elemento: " + string.Join(", ", vuote));
    }

    // -----------------------------------------------------------------------
    // Elenchi senza colonne fisse
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("QueueList")]
    [InlineData("ColleaguesList")]
    public void GliElenchiDaSorvegliareNonHannoColonneAdAltezzaFissa(string nome)
    {
        var elenco = Nominato(Finestra(), nome);

        // Cinque colonne a larghezza fissa facevano 760 pixel: in una colonna laterale,
        // o con lo schermo ingrandito, compariva la barra di scorrimento orizzontale e
        // l'attesa del chiamante restava oltre il bordo. La colonna «Posizione», a 90
        // pixel, andava perfino a capo nella propria intestazione.
        Assert.Empty(elenco.Descendants(P + "GridView"));
        Assert.Empty(elenco.Descendants(P + "GridViewColumn"));

        // Senza scorrimento orizzontale la voce riceve la larghezza dell'elenco e va a
        // capo; con lo scorrimento attivo riceverebbe una larghezza infinita.
        Assert.Equal("Disabled", (string?)elenco.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.NotNull(elenco.Attribute("ItemTemplate"));
    }

    // -----------------------------------------------------------------------
    // La finestra nasce dentro lo schermo
    // -----------------------------------------------------------------------

    [Fact]
    public void LaFinestraSiAdattaAllAreaDiLavoroPrimaDiMostrarsi()
    {
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));

        // La finestra nasce a 1000 per 700 qualunque sia lo schermo. Su un monitor piu'
        // basso nasceva con il fondo sotto la barra delle applicazioni, e con il fondo
        // il campo di composizione.
        var costruttore = codice.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        var adatta = codice.IndexOf("FitToWorkArea();", costruttore, StringComparison.Ordinal);
        Assert.True(adatta > costruttore, "FitToWorkArea() non e' piu' chiamata dopo InitializeComponent()");

        var inizio = codice.IndexOf("private void FitToWorkArea()", StringComparison.Ordinal);
        Assert.True(inizio > 0, "FitToWorkArea() non esiste piu'");

        var corpo = codice[inizio..Math.Min(codice.Length, inizio + 400)];

        Assert.Contains("SystemParameters.WorkArea", corpo, StringComparison.Ordinal);

        // Mai sotto le misure minime: sotto, la finestra non e' piu' utilizzabile.
        Assert.Contains("Math.Max(MinHeight", corpo, StringComparison.Ordinal);
        Assert.Contains("Math.Max(MinWidth", corpo, StringComparison.Ordinal);
    }
}
