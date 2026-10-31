using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Xml.Linq;
using BlindPhone.App;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Il pannello delle impostazioni con le schede a sinistra, in gruppi, e la ricerca.
/// </summary>
/// <remarks>
/// <para>
/// <b>Da dove viene.</b> Le undici schede stavano in alto e, su una finestra larga 860
/// pixel, andavano su due righe che WPF scambiava a ogni selezione; i campi dell'account
/// SIP erano una colonna di diciotto voci senza stacchi. Ora le schede sono un elenco
/// verticale in cinque gruppi, l'account ha quattro gruppi e una sezione «Avanzate», e
/// un campo di ricerca porta a qualunque impostazione per nome.
/// </para>
/// <para>
/// <b>Cosa provano queste prove.</b> Le scelte che, rotte, non si vedono: l'ordine di
/// Control Tab, il titolo dei gruppi che non deve essere letto, il tasto di accesso che
/// non deve morire dentro una sezione chiusa, Invio che nella ricerca non deve salvare.
/// La struttura si legge dal file, come nelle altre prove sulle finestre; la ricerca e
/// il titolo dei gruppi si provano su controlli veri, costruiti in un thread STA.
/// </para>
/// </remarks>
public sealed class ImpostazioniRaggruppateTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace App = "clr-namespace:BlindPhone.App";

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

    private static XElement PerNome(XDocument documento, string nome)
        => documento.Descendants().Single(e => (string?)e.Attribute(X + "Name") == nome);

    /// <summary>Esegue un'azione in un thread STA, come vogliono i controlli WPF.</summary>
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

    // -----------------------------------------------------------------------
    // Schede e gruppi
    // -----------------------------------------------------------------------

    [Fact]
    public void LeSchedeSonoNellOrdineDeiGruppi()
    {
        // E' l'ordine di Control Tab: cambiarlo cambia quante pressioni servono per
        // arrivare a ogni scheda, ed e' scritto nella guida.
        string[] attese =
        [
            "SipTab", "PbxTab", "ProvisioningTab",
            "AudioTab", "HeadsetTab",
            "CallsTab", "ColleaguesTab", "ContactsTab",
            "AccessibilityTab", "HotkeyTab",
            "DiagnosticsTab",
        ];

        var schede = Finestra().Descendants(P + "TabItem").Select(t => (string?)t.Attribute(X + "Name")).ToArray();

        Assert.Equal(attese, schede);
    }

    [Fact]
    public void IlTitoloDelGruppoStaSoltantoSullaPrimaSchedaDiOgniGruppo()
    {
        var attesi = new Dictionary<string, string>
        {
            ["SipTab"] = "Collegamento",
            ["AudioTab"] = "Dispositivi",
            ["CallsTab"] = "Lavoro",
            ["AccessibilityTab"] = "Voce e tastiera",
            ["DiagnosticsTab"] = "Assistenza",
        };

        var trovati = Finestra().Descendants(P + "TabItem")
            .Where(t => t.Attribute(App + "GruppoDiSchede.Titolo") is not null)
            .ToDictionary(t => (string)t.Attribute(X + "Name")!, t => (string)t.Attribute(App + "GruppoDiSchede.Titolo")!);

        Assert.Equal(attesi.OrderBy(k => k.Key), trovati.OrderBy(k => k.Key));
    }

    [Fact]
    public void LeSchedeStannoASinistraInUnTabControlVero()
    {
        // Un elenco di pulsanti travestito da schede perderebbe Control Tab e la
        // lettura «scheda 2 di 11». Resta un TabControl, cambia solo il modello.
        var tabs = PerNome(Finestra(), "Tabs");
        Assert.Equal(P + "TabControl", tabs.Name);
        Assert.Equal("{StaticResource SchedeLaterali}", (string?)tabs.Attribute("Style"));

        var stili = XDocument.Load(Percorso(Path.Combine("Temi", "Stili.xaml")));
        var stile = stili.Descendants(P + "Style").Single(s => (string?)s.Attribute(X + "Key") == "SchedeLaterali");

        Assert.Contains(stile.Descendants(P + "Setter"),
            s => (string?)s.Attribute("Property") == "TabStripPlacement" && (string?)s.Attribute("Value") == "Left");
        Assert.Contains(stile.Descendants(P + "TabPanel"), p => (string?)p.Attribute("IsItemsHost") == "True");
    }

    [Fact]
    public void LaVoceDellElencoDisegnaIlContornoDelFocusDaTrePixel()
    {
        var stili = XDocument.Load(Percorso(Path.Combine("Temi", "Stili.xaml")));
        var stile = stili.Descendants(P + "Style").Single(s => (string?)s.Attribute(X + "Key") == "VoceDiSchedaLaterale");

        var anello = stile.Descendants(P + "Border").Single(b => (string?)b.Attribute(X + "Name") == "Anello");
        Assert.Equal("3", (string?)anello.Attribute("BorderThickness"));

        // Il titolo del gruppo e' disegnato dal modello con il testo che non si legge.
        Assert.Single(stile.Descendants(App + "TitoloDecorativo"));
    }

    [Fact]
    public void IlTitoloDelGruppoNonHaUnElementoDiAutomazione()
    {
        // Se lo avesse, finirebbe fra i figli della scheda nell'albero che leggono NVDA
        // e JAWS, e sulla prima scheda di ogni gruppo si sentirebbe una parola in piu'.
        AutomationPeer? titolo = null;
        AutomationPeer? testo = null;

        InSta(() =>
        {
            titolo = UIElementAutomationPeer.CreatePeerForElement(new TitoloDecorativo { Text = "Collegamento" });
            testo = UIElementAutomationPeer.CreatePeerForElement(new TextBlock { Text = "Collegamento" });
        });

        Assert.Null(titolo);
        Assert.NotNull(testo);
    }

    // -----------------------------------------------------------------------
    // Account SIP
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Identità", new[] { "SipAccountName", "SipUsername", "SipAuthId", "SipPassword", "ShowSipPassword" })]
    [InlineData("Centralino", new[] { "SipDomain", "SipTransport", "SipSrtp" })]
    [InlineData("Lavoro agile", new[] { "SipStunServer", "SipKeepAlive" })]
    [InlineData("Segreteria", new[] { "SipVoicemailNumber" })]
    public void ICampiDellAccountStannoNelLoroGruppo(string gruppo, string[] campi)
    {
        var documento = Finestra();
        var sip = PerNome(documento, "SipTab");

        var box = sip.Descendants(P + "GroupBox").Single(g => (string?)g.Attribute("Header") == gruppo);

        Assert.All(campi, c => Assert.Contains(box.Descendants(), e => (string?)e.Attribute(X + "Name") == c));
    }

    [Fact]
    public void LeVociAvanzateStannoInUnaSezioneChiusaAllAvvio()
    {
        var sezione = PerNome(Finestra(), "SipAdvancedExpander");

        Assert.Equal(P + "Expander", sezione.Name);
        Assert.Equal("False", (string?)sezione.Attribute("IsExpanded"));

        foreach (var campo in new[] { "SipRegistrar", "SipOutboundProxy", "SipDisplayName", "SipOutboundCallerId", "CodecList" })
        {
            Assert.Contains(sezione.Descendants(), e => (string?)e.Attribute(X + "Name") == campo);
        }
    }

    [Fact]
    public void VerificaLaRegistrazioneNonScorreConIlContenuto()
    {
        // Sta nella scheda — la sua lettera, F, e' di altri campi nelle altre schede —
        // ma fuori dal riquadro che scorre, cosi' resta sempre in vista.
        var pulsante = PerNome(Finestra(), "TestSipButton");

        Assert.Contains(pulsante.Ancestors(P + "TabItem"), t => (string?)t.Attribute(X + "Name") == "SipTab");
        Assert.Empty(pulsante.Ancestors(P + "ScrollViewer"));
    }

    // -----------------------------------------------------------------------
    // Tasti di accesso nelle sezioni chiuse
    // -----------------------------------------------------------------------

    [Fact]
    public void OgniTastoDiAccessoDentroUnaSezioneSiPuoRitrovareASezioneChiusa()
    {
        // CampoInSezioneChiusa legge la lettera dal testo delle etichette con Target e
        // dei pulsanti. Un tasto di accesso messo altrove dentro una sezione — su una
        // casella combinata, su un'intestazione — tornerebbe a morire in silenzio.
        var difetti = new List<string>();

        foreach (var sezione in Finestra().Descendants(P + "Expander"))
        {
            foreach (var e in sezione.Descendants())
            {
                var testo = (string?)e.Attribute("Content") ?? (string?)e.Attribute("Header");
                if (testo is null || RicercaImpostazioni.TastoDiAccesso(testo) is null) continue;

                var ritrovabile = e.Name.LocalName switch
                {
                    "Label" => e.Attribute("Target") is not null,
                    "Button" or "CheckBox" or "RadioButton" or "ToggleButton" => true,
                    _ => false,
                };

                if (!ritrovabile) difetti.Add($"{e.Name.LocalName}: {testo}");
            }
        }

        Assert.True(difetti.Count == 0, "Tasti non ritrovabili a sezione chiusa:\n" + string.Join('\n', difetti));
    }

    [Fact]
    public void LaFinestraApreLaSezioneQuandoArrivaLaLetteraDiUnSuoCampo()
    {
        var codice = File.ReadAllText(Percorso("SettingsWindow.xaml.cs"));

        var inizio = codice.IndexOf("private void OnWindowPreviewKeyDown", StringComparison.Ordinal);
        Assert.True(inizio > 0, "Gestore dei tasti della finestra non trovato");

        var corpo = codice[inizio..(inizio + 1500)];

        Assert.Contains("CampoInSezioneChiusa(e.SystemKey)", corpo, StringComparison.Ordinal);
        Assert.Contains("PortaAlCampo(campo)", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public void UnaSchedaBloccataApreLeSueSezioni()
    {
        // Una sezione disattivata non si apre: quello che contiene non si leggerebbe.
        var codice = File.ReadAllText(Percorso("SettingsWindow.xaml.cs"));

        var inizio = codice.IndexOf("private void SetSectionEnabled", StringComparison.Ordinal);
        var corpo = codice[inizio..(inizio + 1500)];

        Assert.Contains("IsExpanded = true", corpo, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Ricerca
    // -----------------------------------------------------------------------

    [Fact]
    public void IlCampoDiRicercaStaFuoriDalleSchedeConLaZ()
    {
        var documento = Finestra();

        var etichetta = documento.Descendants(P + "Label")
            .Single(l => (string?)l.Attribute("Target") == "{Binding ElementName=SearchBox}");

        Assert.Equal('Z', RicercaImpostazioni.TastoDiAccesso((string)etichetta.Attribute("Content")!));
        Assert.Empty(etichetta.Ancestors(P + "TabControl"));
    }

    [Theory]
    [InlineData("OnSearchBoxPreviewKeyDown")]
    [InlineData("OnSearchResultsPreviewKeyDown")]
    public void InvioNellaRicercaNonPremeSalva(string gestore)
    {
        // Salva e' il pulsante predefinito: un Invio non gestito lo premerebbe, e chi
        // voleva andare a un campo si troverebbe con le impostazioni salvate.
        var codice = File.ReadAllText(Percorso("SettingsWindow.xaml.cs"));

        var inizio = codice.IndexOf($"private void {gestore}", StringComparison.Ordinal);
        Assert.True(inizio > 0, $"{gestore} non trovato");

        var corpo = codice[inizio..(inizio + 600)];
        var invio = corpo.IndexOf("case Key.Enter:", StringComparison.Ordinal);

        Assert.True(invio > 0, "Invio non gestito");
        Assert.Contains("e.Handled = true;", corpo[invio..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pro_xy in uscita", 'X')]
    [InlineData("Segreteria: numero da c_hiamare", 'H')]
    [InlineData("Usa__questo", null)]
    [InlineData("Nome visualizzato", null)]
    public void LaLetteraDiAccessoSiLeggeComeLaLeggeWpf(string testo, char? attesa)
        => Assert.Equal(attesa, RicercaImpostazioni.TastoDiAccesso(testo));

    [Fact]
    public void IlTestoDellaPropostaNonHaIlTrattinoDelTasto()
    {
        Assert.Equal("Proxy in uscita", RicercaImpostazioni.SenzaTastoDiAccesso("Pro_xy in uscita"));
        Assert.Equal("a_b", RicercaImpostazioni.SenzaTastoDiAccesso("a__b"));
    }

    [Theory]
    [InlineData("Accessibilità", "accessibilita", true)]
    [InlineData("Proxy in uscita (facoltativo)", "uscita proxy", true)]
    [InlineData("Server STUN per l'indirizzo pubblico", "stun", true)]
    [InlineData("Server STUN per l'indirizzo pubblico", "l'indirizzo", true)]
    [InlineData("Proxy in uscita", "proxy entrata", false)]
    [InlineData("Proxy in uscita", "   ", false)]
    public void LaRicercaIgnoraAccentiMaiuscoleEOrdineDelleParole(string voce, string domanda, bool attesa)
        => Assert.Equal(attesa, RicercaImpostazioni.Corrisponde(RicercaImpostazioni.Normalizza(voce), domanda));

    [Fact]
    public void LIndiceTrovaICampiConLaLoroSezioneESaltaQuelliNascosti()
    {
        // Su controlli veri: un'etichetta che indica il suo campo, un campo dentro una
        // sezione chiusa e uno dentro un pannello spento di proposito.
        List<string> voci = [];
        var nascostoRaggiungibile = true;
        var inSezioneRaggiungibile = false;

        InSta(() =>
        {
            var dominio = new TextBox();
            var proxy = new TextBox();
            var nascosto = new TextBox();

            var avanzate = new Expander
            {
                Header = "Avanzate: proxy",
                IsExpanded = false,
                Content = new StackPanel { Children = { new Label { Content = "Pro_xy in uscita", Target = proxy }, proxy } },
            };

            var gruppo = new GroupBox
            {
                Header = "Centralino",
                Content = new StackPanel { Children = { new Label { Content = "_Dominio", Target = dominio }, dominio } },
            };

            var spento = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Children = { new Label { Content = "Utenza", Target = nascosto }, nascosto },
            };

            var schede = new TabControl
            {
                Items =
                {
                    new TabItem
                    {
                        Header = "Account SIP",
                        Content = new StackPanel
                        {
                            Children = { gruppo, avanzate, spento, new CheckBox { Content = "_Mostra la password" } },
                        },
                    },
                },
            };

            var indice = RicercaImpostazioni.Indice(schede);
            voci = indice.Select(v => v.ToString()).ToList();
            nascostoRaggiungibile = RicercaImpostazioni.Raggiungibile(nascosto);
            inSezioneRaggiungibile = RicercaImpostazioni.Raggiungibile(proxy);
        });

        Assert.Contains("Dominio, scheda Account SIP, Centralino", voci);
        Assert.Contains("Proxy in uscita, scheda Account SIP, Avanzate", voci);
        Assert.Contains("Mostra la password, scheda Account SIP", voci);

        // La sezione chiusa la apre la ricerca; il pannello spento no.
        Assert.True(inSezioneRaggiungibile);
        Assert.False(nascostoRaggiungibile);
    }
}
