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

    // Altezza delle righe che non sono liste, misurata sulla finestra a riposo:
    // stato 97, chiamata 148, caselle di riepilogo 71, composizione 61, annuncio 53.
    private const double AltreRighe = 430;

    // -----------------------------------------------------------------------
    // Altezza delle liste
    // -----------------------------------------------------------------------

    [Fact]
    public void SuUnoSchermoAltoLeListeRestanoAllAltezzaPreferita()
    {
        // Capacita' di un monitor da 1080 righe: il comportamento di prima resta, e la
        // finestra si allarga per dare alle liste i loro 180 pixel.
        Assert.Equal(MainWindowLayout.PreferredListHeight, MainWindowLayout.ListMinimum(1000, AltreRighe, 2));
        Assert.Equal(MainWindowLayout.PreferredListHeight, MainWindowLayout.ListMinimum(1000, AltreRighe, 1));
    }

    [Fact]
    public void SuUnoSchermoDa768RigheLeDueListeCedonoQuantoServeAlCampo()
    {
        // 689 pixel utili meno i 32 di margini: restano 657, e le altre righe ne
        // prendono 430. Alle due liste ne restano 227, cioe' 113 a testa.
        var minimo = MainWindowLayout.ListMinimum(689 - 32, AltreRighe, 2);

        Assert.Equal(113, minimo);

        // E il totale non supera lo spazio: e' la proprieta' che tiene il campo in vista.
        Assert.True(AltreRighe + (2 * minimo) <= 689 - 32);
    }

    [Fact]
    public void ConUnaSolaListaBastaLoSpazioDi768Righe()
    {
        // Con una lista sola lo schermo da 768 righe non ha bisogno di toccare niente.
        Assert.Equal(MainWindowLayout.PreferredListHeight, MainWindowLayout.ListMinimum(689 - 32, AltreRighe, 1));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(450)]
    [InlineData(500)]
    public void MaiSottoIlPavimento(double capacita)
    {
        // Sotto, la lista non si distingue piu' dal suo bordo: e quando nemmeno il
        // pavimento basta, il resto del contenuto esce dalla finestra come sempre.
        Assert.Equal(MainWindowLayout.CompactListHeight, MainWindowLayout.ListMinimum(capacita, AltreRighe, 2));
    }

    [Fact]
    public void SenzaListeNonCeNienteDaDecidere()
        => Assert.Equal(0, MainWindowLayout.ListMinimum(700, AltreRighe, 0));

    [Fact]
    public void PiuSpazioNonDaMaiMenoAlleListe()
    {
        var precedente = 0.0;

        for (var capacita = 300; capacita <= 1200; capacita += 7)
        {
            var minimo = MainWindowLayout.ListMinimum(capacita, AltreRighe, 2);

            Assert.True(minimo >= precedente, $"A {capacita} pixel le liste scendono da {precedente} a {minimo}");
            Assert.InRange(minimo, MainWindowLayout.CompactListHeight, MainWindowLayout.PreferredListHeight);

            precedente = minimo;
        }
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
    public void LImpaginazioneRiposizionaLElencoEAdeguaLeListeAncheAFinestraIngrandita()
    {
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));

        var inizio = codice.IndexOf("private void OnRootLayoutUpdated", StringComparison.Ordinal);
        Assert.True(inizio > 0, "OnRootLayoutUpdated non esiste piu'");

        var corpo = codice[inizio..Math.Min(codice.Length, inizio + 1400)];

        var posiziona = corpo.IndexOf("PositionSuggestions();", StringComparison.Ordinal);
        var adegua = corpo.IndexOf("ScheduleListFit();", StringComparison.Ordinal);
        var allarga = corpo.IndexOf("if (_resizePending) return;", StringComparison.Ordinal);

        Assert.True(posiziona > 0, "L'elenco non viene piu' riposizionato a ogni impaginazione");
        Assert.True(adegua > 0, "Le liste non vengono piu' adeguate allo spazio");

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
            "MinimumListHeight = MainWindowLayout.PreferredListHeight;", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void QuandoLeListeCambianoLAllargamentoRiprova()
    {
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));

        var inizio = codice.IndexOf("private void FitListMinimums()", StringComparison.Ordinal);
        Assert.True(inizio > 0, "FitListMinimums non esiste piu'");

        var corpo = codice[inizio..Math.Min(codice.Length, inizio + 1200)];

        // Con un minimo diverso la mancanza a cui si era rinunciato non vale piu': senza
        // questa riga la finestra non riproverebbe a contenere il contenuto.
        Assert.Contains("_unreachableGrowth = 0;", corpo, StringComparison.Ordinal);
    }
}
