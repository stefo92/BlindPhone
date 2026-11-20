using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Xml.Linq;
using BlindPhone.App;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che lo screen reader legga nelle tabelle soltanto il testo a schermo.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da una segnalazione: navigando nelle tabelle si sentiva qualcosa come
/// «BlindPhone.App.SipAccount…», diverso a seconda della tabella e della cella. Misurato
/// con l'automazione di WPF, la stessa da cui leggono NVDA e JAWS: a ogni cella vuota e
/// a ogni casella di spunta WPF dava il nome «Elemento: &lt;tutta la riga&gt;, indice
/// visualizzazione colonna: N», e la riga degli account si chiamava come il suo tipo.
/// </para>
/// <para>
/// Le prove caricano i dizionari veri del tema e guardano i nomi che l'automazione
/// espone, non il codice che li produce.
/// </para>
/// </remarks>
public sealed class CelleLeggibiliTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>Una riga senza testo suo, come era quella degli account: il caso peggiore.</summary>
    private sealed class RigaSenzaTesto
    {
        public string Nome { get; set; } = "Principale";

        public string Reparto { get; set; } = string.Empty;

        public bool Scelta { get; set; }
    }

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

    private static void InSta(Action azione)
    {
        Exception? errore = null;

        var thread = new Thread(() =>
        {
            try { azione(); }
            catch (Exception ex) { errore = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (errore is not null) throw new InvalidOperationException("Errore nel thread STA", errore);
    }

    private static ResourceDictionary Tema()
    {
        static ResourceDictionary Carica(string file)
        {
            // Fuori dall'applicazione lo spazio dei nomi locale va qualificato con
            // l'assembly, che a programma compilato e' implicito.
            var testo = File.ReadAllText(Percorso(file)).Replace(
                "clr-namespace:BlindPhone.App\"",
                "clr-namespace:BlindPhone.App;assembly=BlindPhone.App\"",
                StringComparison.Ordinal);

            return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(testo);
        }

        var risorse = new ResourceDictionary();
        risorse.MergedDictionaries.Add(Carica(Path.Combine("Temi", "Chiaro.xaml")));
        risorse.MergedDictionaries.Add(Carica(Path.Combine("Temi", "Stili.xaml")));
        return risorse;
    }

    /// <summary>I nomi delle celle di una tabella costruita con lo stile del tema.</summary>
    private static List<List<string>> NomiDelleCelle(params RigaSenzaTesto[] righe)
    {
        var nomi = new List<List<string>>();

        InSta(() =>
        {
            var tabella = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Width = 480, Height = 200 };
            tabella.Columns.Add(new DataGridTextColumn { Header = "Nome", Binding = new Binding(nameof(RigaSenzaTesto.Nome)) });
            tabella.Columns.Add(new DataGridTextColumn { Header = "Reparto", Binding = new Binding(nameof(RigaSenzaTesto.Reparto)) });
            tabella.Columns.Add(new DataGridCheckBoxColumn { Header = "Scelto", Binding = new Binding(nameof(RigaSenzaTesto.Scelta)) });
            tabella.ItemsSource = righe;

            var radice = new Border { Resources = Tema(), Child = tabella };

            using var sorgente = new HwndSource(new HwndSourceParameters("Prova delle celle") { Width = 520, Height = 240 });
            sorgente.RootVisual = radice;
            radice.Measure(new Size(520, 240));
            radice.Arrange(new Rect(0, 0, 520, 240));
            radice.UpdateLayout();

            var peer = UIElementAutomationPeer.CreatePeerForElement(tabella);

            foreach (var riga in (peer.GetChildren() ?? []).Where(r => r.GetAutomationControlType() == AutomationControlType.DataItem))
            {
                nomi.Add([.. (riga.GetChildren() ?? [])
                    .Where(c => c.GetAutomationControlType() != AutomationControlType.HeaderItem)
                    .Take(3)
                    .Select(c => c.GetName())]);
            }
        });

        return nomi;
    }

    [Fact]
    public void NessunaCellaLeggeElementoNeIlNomeDelTipo()
    {
        var celle = NomiDelleCelle(new RigaSenzaTesto(), new RigaSenzaTesto { Nome = "Secondo", Reparto = "Protocollo", Scelta = true });

        Assert.Equal(2, celle.Count);
        Assert.All(celle.SelectMany(c => c), nome =>
        {
            Assert.DoesNotContain("Elemento", nome, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("indice", nome, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BlindPhone", nome, StringComparison.Ordinal);
            Assert.DoesNotContain("+", nome, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OgniCellaLeggeQuelloCheMostra()
    {
        var celle = NomiDelleCelle(new RigaSenzaTesto(), new RigaSenzaTesto { Nome = "Secondo", Reparto = "Protocollo", Scelta = true });

        // Il testo cosi' com'e'; «vuoto» dove non c'e' niente; per le caselle le parole
        // di NVDA.
        Assert.Equal(["Principale", TestoDellaCella.Vuoto, TestoDellaCella.NonSelezionato], celle[0]);
        Assert.Equal(["Secondo", "Protocollo", TestoDellaCella.Selezionato], celle[1]);
    }

    [Fact]
    public void GliStiliDiCellaDelleFinestreEstendonoQuelloDelTemaCheDaIlNome()
    {
        // Gli stili con nome proprio, come il numero detto cifra per cifra, si basano su
        // quello del tema: e' da li' che ogni altra cella riceve il nome.
        var stili = XDocument.Load(Percorso(Path.Combine("Temi", "Stili.xaml")));
        var stile = stili.Descendants(P + "Style").Single(s =>
            (string?)s.Attribute("TargetType") == "DataGridCell" && s.Attribute(X + "Key") is null);

        var nome = stile.Elements(P + "Setter").Single(s => (string?)s.Attribute("Property") == "AutomationProperties.Name");
        Assert.Contains(nome.Descendants(P + "MultiBinding"),
            m => ((string?)m.Attribute("Converter"))?.Contains("TestoDellaCella", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void LaRigaDegliAccountSiLeggeComeLeSueColonne()
    {
        // Prima si chiamava «BlindPhone.App.SipAccountsWindow+AccountRow».
        var tipo = typeof(SipAccountsWindow).GetNestedType("AccountRow", BindingFlags.NonPublic)!;
        var account = new SipSettings { Id = "a", Name = "Principale", Username = "4021", Domain = "pbx.ente.local", Transport = "Tls" };

        var riga = Activator.CreateInstance(tipo, account, true, true)!;
        var testo = riga.ToString()!;

        Assert.DoesNotContain("BlindPhone", testo, StringComparison.Ordinal);
        Assert.StartsWith("Principale, interno 4021, centralino pbx.ente.local, TLS cifrato", testo, StringComparison.Ordinal);
        Assert.Contains("in uso", testo, StringComparison.Ordinal);
        Assert.Contains("all'avvio", testo, StringComparison.Ordinal);
    }
}
