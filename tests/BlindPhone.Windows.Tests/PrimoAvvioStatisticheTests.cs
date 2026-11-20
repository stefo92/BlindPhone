using System.Text;
using System.Xml.Linq;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Le statistiche d'uso al primo avvio: il server predefinito, la domanda e chi può
/// toglierla.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il server c'è di partenza, il consenso no.</b> Una copia appena compilata conosce
/// il ponte dell'autore e la chiave del progetto, e non manda niente finché qualcuno non
/// dice sì. Le postazioni già installate ricevono lo stesso server una volta sola,
/// all'aggiornamento, se nessuno ne aveva scelto uno.
/// </para>
/// <para>
/// <b>La domanda si fa una volta, e l'ente la può togliere.</b> Le prove la seguono
/// dalla configurazione vera: il file su disco, la migrazione, il pacchetto di
/// configurazione con e senza il blocco della scheda Diagnostica. Le quattro condizioni
/// una per una stanno nelle prove del core, <c>DomandaSulleStatisticheTests</c>.
/// </para>
/// <para>
/// Nessuna di queste prove apre una connessione: la configurazione punta al server
/// vero, ma qui nessuno avvia il servizio delle statistiche.
/// </para>
/// </remarks>
public sealed class PrimoAvvioStatisticheTests : IDisposable
{
    /// <summary>
    /// Una chiave di progetto con la forma giusta e nessun valore, scritta in due pezzi
    /// come nelle altre prove: vedi <c>StatisticheDusoReteTests</c>.
    /// </summary>
    private const string ChiaveFinta = "phc_" + "ProvaProvaProvaProvaProvaProvaProvaProvaPro";

    /// <summary>Una chiave personale, che il programma rifiuta: anche lei in due pezzi.</summary>
    private const string ChiavePersonale = "phx_" + "ProvaProvaProvaProvaProvaProvaProvaProvaPro";

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private readonly List<SettingsService> _postazioni = [];

    public void Dispose()
    {
        foreach (var cartella in _postazioni.Select(s => s.DataDirectory))
        {
            try
            {
                Directory.Delete(cartella, recursive: true);
            }
            catch (IOException)
            {
                // La cartella temporanea si puo' lasciare: e' sotto %TEMP%.
            }
        }
    }

    private SettingsService Postazione(string etichetta)
    {
        var postazione = SettingsService.CreateTemporary(etichetta);
        _postazioni.Add(postazione);
        return postazione;
    }

    private static AppSettings Precedente(int schema, string indirizzo, string chiave, bool consenso = false) => new()
    {
        SchemaVersion = schema,
        Telemetry = new TelemetrySettings { ServerUrl = indirizzo, ProjectKey = chiave, Enabled = consenso },
    };

    // -----------------------------------------------------------------------
    // Il server predefinito
    // -----------------------------------------------------------------------

    [Fact]
    public void IlServerPredefinitoEQuelloDellAutoreEIlConsensoESpento()
    {
        var predefinite = new AppSettings().Telemetry;

        Assert.Equal(TelemetrySettings.DefaultServerUrl, predefinite.ServerUrl);
        Assert.Equal(TelemetrySettings.DefaultProjectKey, predefinite.ProjectKey);
        Assert.Equal(
            "https://" + TelemetryConsentPrompt.AuthorServerHost + "/batch/",
            predefinite.BatchEndpoint?.AbsoluteUri);

        Assert.True(TelemetrySettings.IsProjectKey(TelemetrySettings.DefaultProjectKey), "la chiave predefinita deve essere una chiave di progetto");
        Assert.True(predefinite.IsConfigured);
        Assert.True(predefinite.IsAuthorServer);

        // Il server c'e', il consenso no: una copia appena compilata non manda niente.
        Assert.False(predefinite.Enabled);
        Assert.False(predefinite.IsActive);
    }

    [Fact]
    public void UnAltroServerNonEQuelloDellAutore()
    {
        var ente = new TelemetrySettings { ServerUrl = "https://statistiche.example.it/", ProjectKey = ChiaveFinta };

        Assert.True(ente.IsConfigured);
        Assert.False(ente.IsAuthorServer);
    }

    // -----------------------------------------------------------------------
    // La domanda, sulla configurazione vera
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaPostazioneNuovaSenteLaDomandaUnaVoltaSola()
    {
        var postazione = Postazione("domanda-nuova");
        var impostazioni = postazione.Load();

        Assert.False(impostazioni.TelemetryConsentAsked);
        Assert.True(impostazioni.ShouldAskTelemetryConsent(diagnosticsEditable: true));

        // Quello che fa l'avvio dopo un no, o dopo Esc: la risposta e la domanda fatta.
        Assert.True(postazione.Save(impostazioni with
        {
            TelemetryConsentAsked = true,
            Telemetry = impostazioni.Telemetry with { Enabled = false },
        }));

        // Si rilegge dal disco: la domanda fatta deve sopravvivere al riavvio.
        var riletta = postazione.Load();

        Assert.True(riletta.TelemetryConsentAsked);
        Assert.False(riletta.ShouldAskTelemetryConsent(diagnosticsEditable: true));
    }

    [Fact]
    public void DopoUnSiLeStatisticheSonoAcceseELaDomandaNonTorna()
    {
        var postazione = Postazione("domanda-si");
        var impostazioni = postazione.Load();

        postazione.Save(impostazioni with
        {
            TelemetryConsentAsked = true,
            Telemetry = impostazioni.Telemetry with { Enabled = true },
        });

        Assert.True(postazione.Current.Telemetry.IsActive);
        Assert.False(postazione.Current.ShouldAskTelemetryConsent(diagnosticsEditable: true));
    }

    [Fact]
    public void ConLeStatisticheGiaAcceseNonSiChiede()
    {
        // Le ha accese l'ente con il pacchetto, o l'operatore prima che la domanda
        // esistesse: chiedere vorrebbe dire rimettere in discussione la scelta.
        var accese = new AppSettings { Telemetry = new TelemetrySettings { Enabled = true } };

        Assert.False(accese.ShouldAskTelemetryConsent(diagnosticsEditable: true));
    }

    [Theory]
    [InlineData("", TelemetrySettings.DefaultProjectKey, "senza indirizzo")]
    [InlineData("http://telemetry.curti.cloud/", TelemetrySettings.DefaultProjectKey, "con un indirizzo che non e' https")]
    [InlineData(TelemetrySettings.DefaultServerUrl, "", "senza chiave")]
    [InlineData(TelemetrySettings.DefaultServerUrl, ChiavePersonale, "con una chiave personale")]
    public void SenzaUnServerValidoNonSiChiede(string indirizzo, string chiave, string come)
    {
        // Un si' non manderebbe niente: una domanda senza effetto e' una promessa falsa.
        var impostazioni = new AppSettings
        {
            Telemetry = new TelemetrySettings { ServerUrl = indirizzo, ProjectKey = chiave },
        };

        Assert.False(impostazioni.ShouldAskTelemetryConsent(diagnosticsEditable: true), come);
    }

    [Fact]
    public void ConLaDiagnosticaBloccataNonSiChiede()
    {
        var bloccanti = new[]
        {
            new ConfigurationPolicy { LockedSections = [SettingsSection.Diagnostics] },
            new ConfigurationPolicy { Access = PanelAccess.Locked },
            new ConfigurationPolicy { Access = PanelAccess.ReadOnly },
        };

        foreach (var criterio in bloccanti)
        {
            Assert.False(
                new AppSettings().ShouldAskTelemetryConsent(criterio.CanEdit(SettingsSection.Diagnostics)),
                $"con il criterio {criterio.Access} e le sezioni {criterio.DescribeSections()} la domanda non andava fatta");
        }

        // Un blocco su un'altra scheda non e' una decisione sulle statistiche.
        var soloAudio = new ConfigurationPolicy { LockedSections = [SettingsSection.Audio] };

        Assert.True(new AppSettings().ShouldAskTelemetryConsent(soloAudio.CanEdit(SettingsSection.Diagnostics)));
    }

    // -----------------------------------------------------------------------
    // Il pacchetto di configurazione
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compone il pacchetto sulla postazione dell'amministratore, che alla domanda ha già
    /// risposto, e lo applica a una postazione appena installata.
    /// </summary>
    private SettingsService ApplicaIlPacchettoDellEnte(ConfigurationPolicy? criterio)
    {
        var amministratore = Postazione("domanda-admin");
        amministratore.Load();
        amministratore.Save(amministratore.Current with { TelemetryConsentAsked = true });

        var pacchetto = new ConfigurationBundleService(amministratore, new HotkeyProfileStore(amministratore.DataDirectory))
            .Compose([SettingsSection.Diagnostics], includeHotkeys: false, policy: criterio, revision: 2, description: null);

        var postazione = Postazione("domanda-operatore");
        postazione.Load();

        var esito = new ConfigurationBundleService(postazione, new HotkeyProfileStore(postazione.DataDirectory))
            .Apply(pacchetto);

        Assert.True(esito.Success, esito.Message);
        return postazione;
    }

    [Fact]
    public void LaRispostaDellAmministratoreNonViaggiaNelPacchetto()
    {
        // Se viaggiasse, chi prepara il pacchetto risponderebbe per tutti, e nessun
        // operatore sentirebbe mai la domanda.
        var postazione = ApplicaIlPacchettoDellEnte(criterio: null);

        Assert.False(postazione.Current.TelemetryConsentAsked);
    }

    [Fact]
    public void UnPacchettoCheLasciaLiberaLaDiagnosticaLasciaLaDomanda()
    {
        // E' il caso del §7.2 di CONFORMITA-PA-ITALIANA.md: l'ente distribuisce server e
        // chiave con il consenso spento, e lascia decidere gli operatori.
        var postazione = ApplicaIlPacchettoDellEnte(criterio: null);

        Assert.True(postazione.Current.ShouldAskTelemetryConsent(
            postazione.Current.Policy.CanEdit(SettingsSection.Diagnostics)));
    }

    [Fact]
    public void UnPacchettoCheBloccaLaDiagnosticaToglieLaDomanda()
    {
        var postazione = ApplicaIlPacchettoDellEnte(new ConfigurationPolicy
        {
            LockedSections = [SettingsSection.Diagnostics],
            Note = "Statistiche decise dall'ufficio IT",
        });

        Assert.False(postazione.Current.Policy.CanEdit(SettingsSection.Diagnostics));
        Assert.False(postazione.Current.ShouldAskTelemetryConsent(
            postazione.Current.Policy.CanEdit(SettingsSection.Diagnostics)));
    }

    // -----------------------------------------------------------------------
    // Le postazioni già installate
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaPostazioneGiaInstallataRiceveIlServerAllAggiornamento()
    {
        var migrata = Precedente(schema: 4, indirizzo: string.Empty, chiave: string.Empty).Migrate();

        Assert.Equal(TelemetrySettings.DefaultServerUrl, migrata.Telemetry.ServerUrl);
        Assert.Equal(TelemetrySettings.DefaultProjectKey, migrata.Telemetry.ProjectKey);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrata.SchemaVersion);

        // Il consenso resta spento, e al primo avvio arriva la domanda.
        Assert.False(migrata.Telemetry.Enabled);
        Assert.True(migrata.ShouldAskTelemetryConsent(diagnosticsEditable: true));
    }

    [Fact]
    public void UnaPostazioneConLa1_0_21_1RiceveIlServerDelleStatisticheMaNonRifaLaMigrazioneDegliAggiornamenti()
    {
        // La 1.0.21.1 ha gia' portato lo schema a 5 con la migrazione degli
        // aggiornamenti, nata in parallelo a questa. Le statistiche sono la 6: se avessero
        // preso anche loro il 5, queste postazioni non le avrebbero mai ricevute.
        var dalla1_0_21_1 = Precedente(schema: 5, indirizzo: string.Empty, chiave: string.Empty) with
        {
            // Svuotato dopo la migrazione 5, per esempio perche' gli aggiornamenti li
            // distribuisce l'ente: non deve tornare pieno.
            Updates = new UpdateSettings { ServerUrl = string.Empty },
        };

        var migrata = dalla1_0_21_1.Migrate();

        Assert.Equal(TelemetrySettings.DefaultServerUrl, migrata.Telemetry.ServerUrl);
        Assert.Equal(TelemetrySettings.DefaultProjectKey, migrata.Telemetry.ProjectKey);
        Assert.False(migrata.Telemetry.Enabled);
        Assert.Equal(string.Empty, migrata.Updates.ServerUrl);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrata.SchemaVersion);
    }

    [Fact]
    public void IlFileDiUnaVersionePrecedenteRiceveIlServerAllaLettura()
    {
        // Il caso reale: un settings.json scritto dalla 1.0.20, con indirizzo e chiave
        // vuoti perche' quelli erano i valori predefiniti, e senza la domanda.
        var postazione = Postazione("domanda-aggiornata");

        File.WriteAllText(
            postazione.SettingsPath,
            """
            {
              "schemaVersion": 4,
              "telemetry": {
                "serverUrl": "",
                "projectKey": "",
                "enabled": false
              }
            }
            """,
            Encoding.UTF8);

        var letta = postazione.Load();

        Assert.Equal(TelemetrySettings.DefaultServerUrl, letta.Telemetry.ServerUrl);
        Assert.Equal(TelemetrySettings.DefaultProjectKey, letta.Telemetry.ProjectKey);
        Assert.False(letta.TelemetryConsentAsked);
        Assert.True(letta.ShouldAskTelemetryConsent(diagnosticsEditable: true));
    }

    [Theory]
    [InlineData("https://statistiche.example.it/", ChiaveFinta, "un server e una chiave dell'ente")]
    [InlineData("https://statistiche.example.it/", "", "un server senza chiave")]
    [InlineData("", ChiaveFinta, "una chiave senza server")]
    public void UnServerSceltoDaQualcunoNonSiTocca(string indirizzo, string chiave, string che)
    {
        // Anche scritti a meta', sono una scelta di chi amministra: la migrazione li
        // lascia come sono.
        var migrata = Precedente(schema: 4, indirizzo, chiave).Migrate();

        Assert.True(indirizzo == migrata.Telemetry.ServerUrl, $"{che}: l'indirizzo e' cambiato");
        Assert.True(chiave == migrata.Telemetry.ProjectKey, $"{che}: la chiave e' cambiata");
    }

    [Fact]
    public void UnaCasellaSpuntataSenzaServerRiparteSpentaEFaLaDomanda()
    {
        // Spuntata senza server non mandava niente, e chi l'ha spuntata non sapeva
        // verso chi. Con il server dell'autore comincerebbe a mandare senza che nessuno
        // l'abbia deciso: riparte spenta, e la domanda dice verso chi vanno.
        var migrata = Precedente(schema: 4, string.Empty, string.Empty, consenso: true).Migrate();

        Assert.False(migrata.Telemetry.Enabled);
        Assert.True(migrata.ShouldAskTelemetryConsent(diagnosticsEditable: true));
    }

    [Fact]
    public void UnConsensoAccesoVersoUnServerSceltoNonSiTocca()
    {
        var migrata = Precedente(schema: 4, "https://statistiche.example.it/", ChiaveFinta, consenso: true).Migrate();

        Assert.True(migrata.Telemetry.Enabled);
        Assert.False(migrata.ShouldAskTelemetryConsent(diagnosticsEditable: true));
    }

    [Fact]
    public void ChiSvuotaIlServerDopoLAggiornamentoLoRitrovaVuoto()
    {
        // La migrazione avviene una volta: dopo, un indirizzo vuoto e' una scelta.
        var svuotata = Precedente(AppSettings.CurrentSchemaVersion, string.Empty, string.Empty).Migrate();

        Assert.True(svuotata.Telemetry.ServerUrl.Length == 0, "l'indirizzo svuotato deve restare vuoto");
        Assert.True(svuotata.Telemetry.ProjectKey.Length == 0, "la chiave svuotata deve restare vuota");
        Assert.False(svuotata.ShouldAskTelemetryConsent(diagnosticsEditable: true));
    }

    [Fact]
    public void LaMigrazioneEIdempotente()
    {
        var una = Precedente(schema: 4, string.Empty, string.Empty).Migrate();
        var due = una.Migrate();

        Assert.Equal(una.Telemetry, due.Telemetry);
        Assert.Equal(una.SchemaVersion, due.SchemaVersion);
    }

    // -----------------------------------------------------------------------
    // La finestra
    // -----------------------------------------------------------------------

    private static string CartellaApp()
    {
        var cartella = new DirectoryInfo(AppContext.BaseDirectory);

        while (cartella is not null)
        {
            var candidato = Path.Combine(cartella.FullName, "src", "BlindPhone.App");
            if (Directory.Exists(candidato)) return candidato;

            cartella = cartella.Parent;
        }

        throw new DirectoryNotFoundException("Cartella dell'applicazione non trovata");
    }

    private static string? AttributoDiAutomazione(XElement elemento, string nome)
        => elemento.Attributes().FirstOrDefault(a => a.Name.LocalName == "AutomationProperties." + nome)?.Value;

    [Fact]
    public void LaFinestraNonSceglieInAnticipoEDireNoCostaQuantoDireSi()
    {
        var finestra = XDocument.Load(Path.Combine(CartellaApp(), "TelemetryConsentWindow.xaml"));

        // Il titolo scritto nella finestra e quello pronunciato devono essere lo stesso.
        Assert.Equal(TelemetryConsentPrompt.Title, (string?)finestra.Root!.Attribute("Title"));

        var pulsanti = finestra.Descendants(P + "Button").ToList();
        var si = pulsanti.Single(b => (string?)b.Attribute(X + "Name") == "YesButton");
        var no = pulsanti.Single(b => (string?)b.Attribute(X + "Name") == "NoButton");

        Assert.Equal(2, pulsanti.Count);

        // Nessun pulsante predefinito: un Invio dato per abitudine non sceglie niente.
        Assert.DoesNotContain(pulsanti, b => b.Attribute("IsDefault") is not null);

        // Alt S e Alt N, e i due pulsanti hanno la stessa larghezza.
        Assert.StartsWith("_S", (string?)si.Attribute("Content"), StringComparison.Ordinal);
        Assert.StartsWith("_N", (string?)no.Attribute("Content"), StringComparison.Ordinal);
        Assert.Equal((string?)si.Attribute("MinWidth"), (string?)no.Attribute("MinWidth"));

        // Esc vale come no, e solo come no.
        Assert.Equal("True", (string?)no.Attribute("IsCancel"));
        Assert.Null(si.Attribute("IsCancel"));

        foreach (var pulsante in pulsanti)
        {
            Assert.False(string.IsNullOrWhiteSpace(AttributoDiAutomazione(pulsante, "Name")));
            Assert.False(string.IsNullOrWhiteSpace(AttributoDiAutomazione(pulsante, "HelpText")));
        }

        // Il testo si legge riga per riga, e la casella si presenta con la domanda.
        var testo = finestra.Descendants(P + "TextBox").Single();

        Assert.Equal("True", (string?)testo.Attribute("IsReadOnly"));
        Assert.Equal(TelemetryConsentPrompt.Question, AttributoDiAutomazione(testo, "Name"));
    }

    [Fact]
    public void AllAperturaIlFuocoStaNelTestoENonSuUnPulsante()
    {
        var codice = File.ReadAllText(Path.Combine(CartellaApp(), "TelemetryConsentWindow.xaml.cs"));

        Assert.Contains("ExplanationText.Focus()", codice, StringComparison.Ordinal);
        Assert.DoesNotContain("YesButton.Focus()", codice, StringComparison.Ordinal);
        Assert.DoesNotContain("NoButton.Focus()", codice, StringComparison.Ordinal);

        // Il testo si pronuncia all'apertura, per intero.
        Assert.Contains("TelemetryConsentPrompt.Spoken(", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void LAvvioFaLaDomandaPrimaDellaFinestraPrincipaleEDiceLaRispostaDopo()
    {
        var codice = File.ReadAllText(Path.Combine(CartellaApp(), "App.xaml.cs"));

        var domanda = codice.IndexOf("var rispostaStatistiche = AskTelemetryConsent();", StringComparison.Ordinal);
        var principale = codice.IndexOf("window.Show();", StringComparison.Ordinal);
        var conferma = codice.IndexOf("AnnounceTelemetryConsent(rispostaStatistiche);", StringComparison.Ordinal);
        var pacchetto = codice.IndexOf("settings = ApplyProvisioning(settings);", StringComparison.Ordinal);

        Assert.True(pacchetto >= 0 && domanda > pacchetto, "la domanda va fatta dopo il pacchetto di configurazione");
        Assert.True(principale > domanda, "la domanda va fatta prima della finestra principale");
        Assert.True(conferma > principale, "la risposta si dice a finestra principale aperta");
    }
}
