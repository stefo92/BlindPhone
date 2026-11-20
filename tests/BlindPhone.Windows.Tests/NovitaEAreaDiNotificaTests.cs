using System.Xml.Linq;
using BlindPhone.App;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la finestra delle novità di un aggiornamento e il menu dell'icona
/// nell'area di notifica.
/// </summary>
/// <remarks>
/// <para>
/// <b>La finestra delle novità non si faceva leggere.</b> Si apriva con il fuoco sul
/// pulsante «Più tardi», e le novità — la ragione della finestra — stavano in una
/// casella raggiungibile solo con Tab. Le righe con la versione e la provenienza
/// avevano un nome accessibile scritto a mano che copriva il testo: lo screen reader
/// diceva «Versione installata e versione disponibile» e non le versioni.
/// </para>
/// <para>
/// <b>Il menu dell'area di notifica</b> ha due voci in più, perché dalla finestra
/// riposta si possano cercare gli aggiornamenti e aprire le impostazioni.
/// </para>
/// </remarks>
public sealed class NovitaEAreaDiNotificaTests
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

    private static XElement PerNome(string nome)
        => XDocument.Load(Percorso("UpdateWindow.xaml")).Descendants()
            .Single(e => (string?)e.Attribute(X + "Name") == nome);

    // -----------------------------------------------------------------------
    // Finestra delle novità
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("VersionText")]
    [InlineData("ChannelText")]
    public void LeRigheDiTestoSiLegganoPerQuelloCheDicono(string nome)
    {
        // Un nome accessibile fisso su un TextBlock sostituisce il testo: lo screen
        // reader leggeva l'etichetta e mai le versioni.
        Assert.Null(PerNome(nome).Attribute(XName.Get("AutomationProperties.Name")));
    }

    [Fact]
    public void LaCasellaDelleNovitaNonPrendeInvio()
    {
        // Cosi' Invio arriva al pulsante predefinito, «Piu' tardi», anche con il cursore
        // fra le novita'.
        var casella = PerNome("NotesText");

        Assert.Equal("True", (string?)casella.Attribute("IsReadOnly"));
        Assert.Equal("False", (string?)casella.Attribute("AcceptsReturn"));
    }

    [Fact]
    public void LaFinestraSiApreConIlCursoreSulleNovita()
    {
        var codice = File.ReadAllText(Percorso("UpdateWindow.xaml.cs"));

        Assert.Contains("NotesText.Focus();", codice, StringComparison.Ordinal);
        Assert.DoesNotContain("LaterButton.Focus();", codice, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Menu dell'area di notifica
    // -----------------------------------------------------------------------

    [Fact]
    public void IlMenuDellAreaDiNotificaHaLeQuattroVociNellOrdine()
    {
        IReadOnlyList<string> voci = [];

        var thread = new Thread(() =>
        {
            using var icona = new TrayIcon();
            voci = icona.MenuLabels;
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Equal(
            ["&Apri BlindPhone", "&Ricerca aggiornamenti", "&Impostazioni", "&Esci da BlindPhone"],
            voci);

        // Ogni voce ha la sua lettera, e le lettere non si ripetono: con due voci sulla
        // stessa lettera il tasto porterebbe alla prima e mai alla seconda.
        var lettere = voci.Select(v => char.ToUpperInvariant(v[v.IndexOf('&') + 1])).ToList();
        Assert.Equal(lettere.Count, lettere.Distinct().Count());
    }

    [Fact]
    public void LeVociNuoveSonoCollegateAllaFinestraPrincipale()
    {
        var codice = File.ReadAllText(Percorso("MainWindow.xaml.cs"));

        Assert.Contains("_tray.UpdatesRequested +=", codice, StringComparison.Ordinal);
        Assert.Contains("_tray.SettingsRequested +=", codice, StringComparison.Ordinal);
    }
}
