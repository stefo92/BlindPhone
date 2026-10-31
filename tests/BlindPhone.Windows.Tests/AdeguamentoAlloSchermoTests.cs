using System.Xml.Linq;
using BlindPhone.App;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// L'elenco dei contatti proposti resta dentro la finestra e il campo di composizione
/// resta in vista, anche su uno schermo basso.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il sintomo, come lo ha riferito chi lo usava.</b> Cercando un interno compariva
/// «la riga delle intestazioni, molto larga», e i risultati non si vedevano: si
/// sentivano dallo screen reader e basta. Con coda e colleghi visibili e la finestra
/// non a tutto schermo il campo di composizione — che sta in fondo — era già sotto il
/// bordo della finestra, e l'elenco, ancorato al suo bordo alto, ci scendeva insieme:
/// della sua parte alta restava la fascia delle intestazioni, e le righe cadevano fuori.
/// </para>
/// <para>
/// Misurato su una finestra vera, a 600 pixel utili con coda e colleghi: prima solo
/// l'intestazione, poi tutte e tre le righe dei risultati e il campo di composizione
/// con il pulsante Chiama.
/// </para>
/// <para>
/// <b>Cosa provano queste prove.</b> La parte aritmetica — dove mettere l'elenco, che
/// altezza lasciare alle liste — è in <see cref="MainWindowLayout"/> proprio per poterla
/// provare qui. Il collegamento con la finestra, che richiede lo schermo, lo si protegge
/// leggendo il sorgente, come le altre prove sui file di interfaccia.
/// </para>
/// </remarks>
public sealed class AdeguamentoAlloSchermoTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    // -----------------------------------------------------------------------
    // Una o due colonne
    // -----------------------------------------------------------------------

    [Fact]
    public void ConLaFinestraLargaChiamataEColonnaLateraleStannoAffiancate()
    {
        var disposizione = MainWindowLayout.ArrangeWorkArea(1232, hasSideSections: true);

        Assert.True(disposizione.TwoColumns);
        Assert.Equal(new GridCell(MainWindowLayout.WorkRow, 0, 1), disposizione.Call);
        Assert.Equal(new GridCell(MainWindowLayout.WorkRow, MainWindowLayout.SideColumn, 1), disposizione.Side);
    }

    [Fact]
    public void ConLaFinestraStrettaLaColonnaLateraleScendeSottoLaChiamata()
    {
        var disposizione = MainWindowLayout.ArrangeWorkArea(568, hasSideSections: true);

        Assert.False(disposizione.TwoColumns);
        Assert.Equal(new GridCell(MainWindowLayout.CallRow, 0, MainWindowLayout.ColumnCount), disposizione.Call);
        Assert.Equal(new GridCell(MainWindowLayout.WorkRow, 0, MainWindowLayout.ColumnCount), disposizione.Side);
    }

    [Fact]
    public void LaSogliaEInclusa()
    {
        Assert.True(MainWindowLayout.ArrangeWorkArea(MainWindowLayout.TwoColumnMinWidth, true).TwoColumns);
        Assert.False(MainWindowLayout.ArrangeWorkArea(MainWindowLayout.TwoColumnMinWidth - 1, true).TwoColumns);
    }

    [Theory]
    [InlineData(568)]
    [InlineData(1000)]
    [InlineData(1888)]
    public void SenzaCodaNeColleghiLaChiamataPrendeTuttaLaLarghezza(double larghezza)
    {
        // Una colonna vuota a destra sarebbe spazio tolto al nome di chi chiama per
        // non mostrare niente.
        var disposizione = MainWindowLayout.ArrangeWorkArea(larghezza, hasSideSections: false);

        Assert.False(disposizione.TwoColumns);
        Assert.Equal(new GridCell(MainWindowLayout.WorkRow, 0, MainWindowLayout.ColumnCount), disposizione.Call);
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(999, true)]
    [InlineData(1000, true)]
    [InlineData(1800, true)]
    [InlineData(800, false)]
    [InlineData(1800, false)]
    public void LaChiamataVienePrimaDellaColonnaLateraleAncheAGuardarla(double larghezza, bool laterale)
    {
        // L'ordine di tabulazione e' quello del XAML: chiamata, poi coda e colleghi. Chi
        // guarda legge dall'alto in basso e da sinistra a destra, e deve trovare lo
        // stesso ordine: la chiamata sta sopra, oppure sulla stessa riga e a sinistra.
        var d = MainWindowLayout.ArrangeWorkArea(larghezza, laterale);

        var primaSopra = d.Call.Row < d.Side.Row;
        var stessaRigaASinistra = d.Call.Row == d.Side.Row && d.Call.Column + d.Call.ColumnSpan <= d.Side.Column;

        Assert.True(!laterale || primaSopra || stessaRigaASinistra, $"A {larghezza} pixel la chiamata non precede la colonna laterale");
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(1400, true)]
    [InlineData(1400, false)]
    public void NessunaCellaEsceDallaGriglia(double larghezza, bool laterale)
    {
        // Righe e colonne fuori dalla griglia non producono errori: WPF mette
        // l'elemento nell'ultima, e due aree si sovrappongono senza che nessuno se ne
        // accorga.
        var d = MainWindowLayout.ArrangeWorkArea(larghezza, laterale);

        foreach (var cella in new[] { d.Call, d.Side })
        {
            Assert.InRange(cella.Row, MainWindowLayout.StationRow, MainWindowLayout.AnnouncementRow);
            Assert.InRange(cella.Column + cella.ColumnSpan, 1, MainWindowLayout.ColumnCount);
        }
    }

    [Fact]
    public void LeRigheDellaDisposizioneSonoQuelleDelXaml()
    {
        // Le costanti di MainWindowLayout e la griglia del XAML devono raccontare la
        // stessa griglia: cinque righe, tre colonne, la terza riga elastica.
        var griglia = XDocument.Load(Percorso("MainWindow.xaml")).Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == "RootGrid");

        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var righe = griglia.Element(p + "Grid.RowDefinitions")!.Elements(p + "RowDefinition").ToList();
        var colonne = griglia.Element(p + "Grid.ColumnDefinitions")!.Elements(p + "ColumnDefinition").ToList();

        Assert.Equal(MainWindowLayout.AnnouncementRow + 1, righe.Count);
        Assert.Equal(MainWindowLayout.ColumnCount, colonne.Count);
        Assert.Equal("*", (string?)righe[MainWindowLayout.WorkRow].Attribute("Height"));
        Assert.Equal("Auto", (string?)righe[MainWindowLayout.CallRow].Attribute("Height"));
    }

    [Fact]
    public void LaFinestraNasceLargaAbbastanzaPerDueColonne()
    {
        // Con la larghezza di prima, mille pixel, la finestra sarebbe nata in una
        // colonna sola: tolti cornice e margini resta meno della soglia.
        var finestra = XDocument.Load(Percorso("MainWindow.xaml")).Root!;
        var larghezza = double.Parse((string?)finestra.Attribute("Width") ?? "0", System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(larghezza - 16 - 32 >= MainWindowLayout.TwoColumnMinWidth);
    }

    // -----------------------------------------------------------------------
    // Altezza minima dell'area di lavoro
    // -----------------------------------------------------------------------

    [Fact]
    public void ConLeColonneAffiancateVinceIlPiuAltoFraChiamataEListe()
    {
        var larga = MainWindowLayout.ArrangeWorkArea(1232, true);

        Assert.Equal(420, MainWindowLayout.WorkRowMinimum(larga, callHeight: 420, visibleLists: 2));
        Assert.Equal(2 * MainWindowLayout.ListFloor, MainWindowLayout.WorkRowMinimum(larga, callHeight: 200, visibleLists: 2));
    }

    [Fact]
    public void ConLaFinestraStrettaLaChiamataNonContaPercheStaSopra()
    {
        // In una colonna la chiamata sta nella riga ad altezza automatica, che non la
        // taglia comunque: l'area di lavoro deve tenere soltanto le liste.
        var stretta = MainWindowLayout.ArrangeWorkArea(568, true);

        Assert.Equal(MainWindowLayout.ListFloor, MainWindowLayout.WorkRowMinimum(stretta, callHeight: 600, visibleLists: 1));
    }

    [Fact]
    public void SenzaListeContaSoloLaChiamata()
    {
        var sola = MainWindowLayout.ArrangeWorkArea(1232, false);

        Assert.Equal(380, MainWindowLayout.WorkRowMinimum(sola, callHeight: 380, visibleLists: 0));
    }

    [Fact]
    public void IlPavimentoDelleListeLasciaVedereIlTitoloEUnaVoce()
    {
        // Riempimento della scheda 32, titolo 30, margine 16, una voce su due righe 52.
        // Sotto, la sezione mostrerebbe il titolo e nient'altro.
        Assert.True(MainWindowLayout.ListFloor >= 32 + 30 + 16 + 52);

        // E non e' piu' la misura preferita di prima: le liste cedono prima che la
        // finestra cresca.
        Assert.True(MainWindowLayout.ListFloor < 180);
    }

    // -----------------------------------------------------------------------
    // Dove sta l'elenco
    // -----------------------------------------------------------------------

    [Fact]
    public void ConIlCampoInVistaLElencoStaSopraIlCampo()
    {
        // Il caso di sempre: campo a 560, finestra che arriva a 689.
        var posizione = MainWindowLayout.PlaceSuggestions(canvasTop: 560, visibleTop: 16, visibleBottom: 673);

        Assert.Equal(MainWindowLayout.SuggestionsGap, posizione.Bottom);
        Assert.Equal(MainWindowLayout.SuggestionsMaxHeight, posizione.MaxHeight);
    }

    [Fact]
    public void ConIlCampoSottoIlBordoDellaFinestraLElencoSaleEStaDentro()
    {
        // Il caso del difetto: coda e colleghi, finestra di 600 pixel utili, campo a 676.
        // Ancorato al campo, l'elenco avrebbe avuto il bordo basso a 668 e l'alto a 530:
        // visibile soltanto l'intestazione. Deve stare dentro i 584 che la finestra offre.
        var posizione = MainWindowLayout.PlaceSuggestions(canvasTop: 676, visibleTop: 16, visibleBottom: 600 - 16);

        var bordoBasso = 676 - posizione.Bottom;

        Assert.Equal(584, bordoBasso);
        Assert.True(posizione.Bottom > MainWindowLayout.SuggestionsGap);
    }

    [Theory]
    [InlineData(560, 689)]
    [InlineData(676, 600)]
    [InlineData(676, 520)]
    [InlineData(300, 400)]
    [InlineData(900, 500)]
    public void IlBordoBassoDellElencoNonEsceMaiDallaFinestra(double campo, double finestra)
    {
        var visibleBottom = finestra - 16;

        var posizione = MainWindowLayout.PlaceSuggestions(campo, 16, visibleBottom);

        Assert.True(
            campo - posizione.Bottom <= visibleBottom,
            $"Campo a {campo}, finestra di {finestra}: l'elenco finisce a {campo - posizione.Bottom}, oltre {visibleBottom}");

        // E non si avvicina al campo piu' dello stacco dichiarato.
        Assert.True(posizione.Bottom >= MainWindowLayout.SuggestionsGap);
    }

    [Theory]
    [InlineData(100, 16)]
    [InlineData(60, 16)]
    [InlineData(40, 16)]
    public void InUnaFinestraBassissimaRestanoAlmenoLIntestazioneEUnaRiga(double campo, double alto)
    {
        var posizione = MainWindowLayout.PlaceSuggestions(campo, alto, 600);

        Assert.True(posizione.MaxHeight >= MainWindowLayout.SuggestionsMinHeight);
    }

    [Fact]
    public void LAltezzaSiLimitaAlloSpazioCheStaSopra()
    {
        // Campo a 150, parte utile da 16: 126 pixel sopra, meno 8 di stacco.
        var posizione = MainWindowLayout.PlaceSuggestions(canvasTop: 150, visibleTop: 16, visibleBottom: 700);

        Assert.Equal(126, posizione.MaxHeight);
    }

    // -----------------------------------------------------------------------
    // Il collegamento con la finestra
    // -----------------------------------------------------------------------

    [Fact]
    public void LaTelaDellElencoHaUnNomePerPotersiMisurare()
    {
        var tela = XDocument.Load(Percorso("MainWindow.xaml")).Descendants()
            .SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "SuggestionsCanvas");

        Assert.NotNull(tela);
        Assert.Equal("Canvas", tela!.Name.LocalName);
    }

    [Fact]
    public void LImpaginazioneRiposizionaLElencoEAdeguaLAreaDiLavoroAncheAFinestraIngrandita()
    {
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));

        var inizio = codice.IndexOf("private void OnRootLayoutUpdated", StringComparison.Ordinal);
        Assert.True(inizio > 0, "OnRootLayoutUpdated non esiste piu'");

        var corpo = codice[inizio..Math.Min(codice.Length, inizio + 1400)];

        var posiziona = corpo.IndexOf("PositionSuggestions();", StringComparison.Ordinal);
        var adegua = corpo.IndexOf("ScheduleWorkRowFit();", StringComparison.Ordinal);
        var allarga = corpo.IndexOf("if (_resizePending) return;", StringComparison.Ordinal);

        Assert.True(posiziona > 0, "L'elenco non viene piu' riposizionato a ogni impaginazione");
        Assert.True(adegua > 0, "L'area di lavoro non viene piu' adeguata al contenuto");

        // Prima del ritorno anticipato sullo stato della finestra: a finestra ingrandita
        // il campo puo' stare fuori vista esattamente come a finestra normale.
        Assert.True(
            posiziona < allarga && adegua < allarga,
            "Il riposizionamento deve venire prima dei ritorni anticipati dell'allargamento");

        // E con la finestra ridotta a icona non si misura: le dimensioni sono quelle di prima.
        Assert.Contains("WindowState.Minimized", corpo[..allarga], StringComparison.Ordinal);
    }

    [Fact]
    public void IlMinimoDelleListeHaUnaSolaOrigine()
    {
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));

        // Se il numero fosse scritto qui e li', cambiarne uno lascerebbe il banco di
        // prova e la finestra a ragionare su valori diversi.
        Assert.Contains(
            "MinimumListHeight = MainWindowLayout.ListFloor;", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void QuandoIlMinimoCambiaLAllargamentoRiprova()
    {
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));

        var inizio = codice.IndexOf("private void FitWorkRowMinimum()", StringComparison.Ordinal);
        Assert.True(inizio > 0, "FitWorkRowMinimum non esiste piu'");

        var corpo = codice[inizio..Math.Min(codice.Length, inizio + 1200)];

        // Con un minimo diverso la mancanza a cui si era rinunciato non vale piu': senza
        // questa riga la finestra non riproverebbe a contenere il contenuto.
        Assert.Contains("_unreachableGrowth = 0;", corpo, StringComparison.Ordinal);
    }
}
