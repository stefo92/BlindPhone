using System.Xml.Linq;
using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica i controlli dell'elaborazione del microfono nella scheda Audio delle
/// impostazioni.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto che si impedisce è silenzioso.</b> L'elenco dei livelli si carica per
/// <i>etichetta</i>: se un valore dell'elenco non corrisponde a nessun livello, la
/// ricerca ricade sulla prima voce, cioè «spenta», senza errori. Chi sceglie «forte»,
/// salva, riapre il pannello e lo ritrova su «spenta» non ha modo di capire se sia
/// stato salvato qualcosa. Le prove confrontano le etichette con i livelli veri, e
/// verificano che caricamento e salvataggio tocchino entrambi i controlli.
/// </para>
/// <para>
/// Come le altre prove sui file di interfaccia, leggono il sorgente e non aprono la
/// finestra: costruirla richiederebbe l'intera catena dei servizi.
/// </para>
/// </remarks>
public sealed class PannelloElaborazioneMicrofonoTests
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

    private static XDocument Finestra() => XDocument.Load(Percorso("SettingsWindow.xaml"));

    private static XElement Nominato(XDocument documento, string nome)
        => documento.Descendants().Single(e => (string?)e.Attribute(X + "Name") == nome);

    [Fact]
    public void IControlliStannoNellaSchedaAudio()
    {
        var documento = Finestra();
        var scheda = Nominato(documento, "AudioTab");

        Assert.Contains(Nominato(documento, "MicAutoGain"), scheda.Descendants());
        Assert.Contains(Nominato(documento, "MicNoiseSuppression"), scheda.Descendants());
        Assert.Contains(Nominato(documento, "MicProcessingHint"), scheda.Descendants());
    }

    [Fact]
    public void LeVociDellElencoCorrispondonoAiLivelliVeri()
    {
        var elenco = Nominato(Finestra(), "MicNoiseSuppression");

        var tag = elenco.Elements(P + "ComboBoxItem").Select(i => (string?)i.Attribute("Tag")).ToList();

        // Stesso insieme e stesso ordine: la prima voce e' quella su cui si ricade
        // quando una ricerca non trova niente, e deve essere «spenta».
        Assert.Equal(Enum.GetNames<NoiseSuppressionLevel>(), tag);
        Assert.Equal(nameof(NoiseSuppressionLevel.Off), tag[0]);
    }

    [Fact]
    public void OgniControlloHaUnNomeAccessibileEUnaSpiegazione()
    {
        var documento = Finestra();

        foreach (var nome in new[] { "MicAutoGain", "MicNoiseSuppression" })
        {
            var controllo = Nominato(documento, nome);

            Assert.False(
                string.IsNullOrWhiteSpace((string?)controllo.Attribute("AutomationProperties.Name")),
                $"{nome} non ha un nome accessibile");

            Assert.False(
                string.IsNullOrWhiteSpace((string?)controllo.Attribute("AutomationProperties.HelpText")),
                $"{nome} non ha una spiegazione");
        }
    }

    [Fact]
    public void LElencoHaLEtichettaCollegata()
    {
        var documento = Finestra();

        var etichetta = documento.Descendants(P + "Label")
            .SingleOrDefault(l => ((string?)l.Attribute("Target"))?.Contains("MicNoiseSuppression", StringComparison.Ordinal) == true);

        // Senza il collegamento lo screen reader legge l'elenco senza dire a che cosa
        // serve, e il tasto di accesso dell'etichetta non porta da nessuna parte.
        Assert.NotNull(etichetta);
    }

    [Fact]
    public void IlCommentoDelleScelteEUnaRegioneDinamica()
    {
        var commento = Nominato(Finestra(), "MicProcessingHint");

        // Come i commenti degli altri controlli: l'effetto della scelta si sente a ogni
        // cambio senza spostare il cursore.
        Assert.Equal("Polite", (string?)commento.Attribute("AutomationProperties.LiveSetting"));
    }

    [Fact]
    public void LeDueFunzioniNonSonoAccesePerImpostazionePredefinita()
    {
        var casella = Nominato(Finestra(), "MicAutoGain");

        // Spenta di partenza, e la scheda non puo' dire il contrario: l'impostazione e'
        // la verita', il pannello la rispecchia.
        Assert.Null(casella.Attribute("IsChecked"));
    }

    [Fact]
    public void CaricamentoESalvataggioToccanoEntrambiIControlli()
    {
        var codice = File.ReadAllText(Percorso("SettingsWindow.xaml.cs"));

        // Caricamento
        Assert.Contains("MicAutoGain.IsChecked = settings.MicrophoneProcessing.AutoGain", codice, StringComparison.Ordinal);
        Assert.Contains("SelectByTag(MicNoiseSuppression", codice, StringComparison.Ordinal);

        // Salvataggio
        Assert.Contains("AutoGain = MicAutoGain.IsChecked == true", codice, StringComparison.Ordinal);
        Assert.Contains("SelectedTag(MicNoiseSuppression)", codice, StringComparison.Ordinal);
    }
}
