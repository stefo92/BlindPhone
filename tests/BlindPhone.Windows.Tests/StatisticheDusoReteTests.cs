using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Le statistiche d'uso sulla rete vera: un server PostHog finto, in HTTPS, sulla
/// postazione stessa.
/// </summary>
/// <remarks>
/// <para>
/// <b>La prima metà dimostra il silenzio.</b> Senza indirizzo, con il consenso spento,
/// con un indirizzo che non è HTTPS o con una chiave personale, il server finto non
/// riceve nemmeno una connessione — non una richiesta rifiutata: nessun collegamento
/// aperto. E sulla postazione non nasce il file con l'identificativo.
/// </para>
/// <para>
/// <b>La seconda metà legge i byte che escono</b>, ed è lì che l'elenco chiuso smette di
/// essere un'intenzione: ogni corpo ricevuto dal server finto viene aperto e controllato
/// campo per campo contro il catalogo, con una configurazione piena di interni, nomi e
/// indirizzi che non devono comparire.
/// </para>
/// </remarks>
public sealed class StatisticheDusoReteTests : IAsyncLifetime
{
    /// <summary>
    /// Una chiave di progetto con la forma giusta e nessun valore: non è di nessun
    /// progetto, e nel repository non deve mai finire una chiave vera.
    /// </summary>
    /// <remarks>
    /// <b>Il prefisso è scritto a parte, qui e nella chiave personale più sotto.</b> Scritte
    /// tutte d'un pezzo le due chiavi finte hanno la forma esatta di quelle vere, e la
    /// protezione dei segreti di GitHub rifiuta il push: con la chiave personale è
    /// successo. Il valore resta una costante, e la prova non cambia.
    /// </remarks>
    private const string Chiave = "phc_" + Riempitivo;

    /// <summary>Quarantatré caratteri senza significato, la lunghezza di una chiave vera.</summary>
    private const string Riempitivo = "ProvaProvaProvaProvaProvaProvaProvaProvaPro";

    private static readonly DateTimeOffset LunediMattina = new(2026, 11, 2, 8, 30, 0, TimeSpan.FromHours(1));

    private readonly ServerPostHogFinto _server = new();
    private readonly SettingsService _impostazioni = SettingsService.CreateTemporary("statistiche");
    private readonly ManualClock _orologio = new(LunediMattina);
    private readonly List<SettingsService> _altre = [];

    public ValueTask InitializeAsync()
    {
        _impostazioni.Load();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();

        foreach (var cartella in _altre.Append(_impostazioni).Select(s => s.DataDirectory))
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

    /// <summary>La postazione su cui l'amministratore prepara il pacchetto.</summary>
    private SettingsService Amministratore()
    {
        var amministratore = SettingsService.CreateTemporary("statistiche-admin");
        amministratore.Load();
        _altre.Add(amministratore);
        return amministratore;
    }

    private void Configura(bool consenso, string? indirizzo = null, string chiave = Chiave)
        => _impostazioni.Save(_impostazioni.Current with
        {
            Telemetry = new TelemetrySettings
            {
                Enabled = consenso,
                ServerUrl = indirizzo ?? _server.Indirizzo,
                ProjectKey = chiave,
            },
        });

    private TelemetryService Servizio(HttpMessageHandler? gestore = null, Func<bool>? inChiamata = null) => new(
        _impostazioni,
        "1.0.19",
        () => TelemetryFactsBuilder.FromSettings(_impostazioni.Current, "1.0.19") with { ScreenReader = "nvda" },
        inChiamata,
        _orologio,
        gestore ?? _server.GestoreCheSiFida(),
        new TelemetryTiming { StartDelay = TimeSpan.Zero, FlushInterval = TimeSpan.Zero });

    private static async Task Giri(TelemetryService servizio, int quanti = 3)
    {
        for (var i = 0; i < quanti; i++) await servizio.RunOnceAsync(CancellationToken.None);
    }

    private static void RegistraUnPoDiTutto(TelemetryService servizio)
    {
        servizio.RecordCommand(HotkeyCommand.Answer, CommandOrigin.Keyboard);
        servizio.RecordError(new InvalidOperationException("Impossibile chiamare 0612345678"), ErrorOrigin.Interface, fatal: false);
        servizio.RecordUpdateFound(new Version(1, 0, 20));
    }

    /// <summary>Gli eventi di tutti i lotti ricevuti, nell'ordine di arrivo.</summary>
    private List<JsonElement> EventiRicevuti()
        => [.. _server.Richieste.SelectMany(r => JsonDocument.Parse(r.Corpo).RootElement.GetProperty("batch").EnumerateArray())];

    // -----------------------------------------------------------------------
    // Il silenzio
    // -----------------------------------------------------------------------

    [Fact]
    public void LeStatisticheNasconoSpenteESenzaServer()
    {
        var predefinite = new AppSettings().Telemetry;

        // Le due costanti si leggono dalle impostazioni nate da loro: confrontarle con un
        // testo vuoto scritto qui farebbe protestare l'analizzatore di xUnit, che vuole la
        // costante dalla parte del valore atteso.
        Assert.False(predefinite.Enabled);
        Assert.True(predefinite.ServerUrl.Length == 0, "l'indirizzo predefinito deve essere vuoto");
        Assert.True(predefinite.ProjectKey.Length == 0, "la chiave predefinita deve essere vuota");
        Assert.False(predefinite.IsConfigured);

        // I valori derivati non finiscono nel file: chi lo apre con un editor non deve
        // trovare un «isActive» da cambiare che non cambia niente.
        var testo = File.ReadAllText(_impostazioni.SettingsPath);

        Assert.Contains("\"telemetry\"", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("isActive", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("batchEndpoint", testo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SenzaIndirizzoNonSiApreNessunaConnessione()
    {
        Configura(consenso: true, indirizzo: string.Empty);

        await using var servizio = Servizio();
        RegistraUnPoDiTutto(servizio);
        await Giri(servizio);

        Assert.Equal(0, _server.Connessioni);
        Assert.False(servizio.IsCollecting);
        Assert.False(File.Exists(servizio.StatePath), "l'identificativo non deve nascere senza un server");
    }

    [Fact]
    public async Task ConIlConsensoSpentoNonSiApreNessunaConnessione()
    {
        Configura(consenso: false);

        await using var servizio = Servizio();
        RegistraUnPoDiTutto(servizio);
        await Giri(servizio);

        Assert.Equal(0, _server.Connessioni);
        Assert.False(servizio.IsCollecting);
        Assert.False(File.Exists(servizio.StatePath), "l'identificativo non deve nascere senza consenso");
    }

    [Fact]
    public async Task ConUnIndirizzoCheNonEHttpsNonSiApreNessunaConnessione()
    {
        foreach (var indirizzo in new[]
                 {
                     $"http://127.0.0.1:{_server.Porta}/",
                     $"ftps://127.0.0.1:{_server.Porta}/",
                     $"https://utente:password@127.0.0.1:{_server.Porta}/",
                     $"127.0.0.1:{_server.Porta}",
                 })
        {
            Configura(consenso: true, indirizzo: indirizzo);

            await using var servizio = Servizio();
            RegistraUnPoDiTutto(servizio);
            await Giri(servizio);

            Assert.False(servizio.IsCollecting, indirizzo);
        }

        Assert.Equal(0, _server.Connessioni);
    }

    [Theory]
    [InlineData("phx_" + Riempitivo, "una chiave personale")]
    [InlineData("ProvaProvaProvaProvaProvaProvaProvaProvaProva", "una chiave senza prefisso")]
    [InlineData("phc_corta", "una chiave troppo corta")]
    [InlineData("phc_Prova Prova Prova Prova Prova Prova Prova", "una chiave con gli spazi")]
    [InlineData("", "nessuna chiave")]
    public async Task UnaChiaveCheNonEDiProgettoNonFaPartireNiente(string chiave, string che)
    {
        Configura(consenso: true, chiave: chiave);

        await using var servizio = Servizio();
        RegistraUnPoDiTutto(servizio);
        await Giri(servizio);

        Assert.True(_server.Connessioni == 0, $"con {che} si e' aperta una connessione");
        Assert.False(TelemetrySettings.IsProjectKey(chiave));
    }

    [Theory]
    [InlineData("https://eu.i.posthog.com/", "https://eu.i.posthog.com/batch/")]
    [InlineData("https://eu.i.posthog.com", "https://eu.i.posthog.com/batch/")]
    [InlineData("https://us.i.posthog.com/", "https://us.i.posthog.com/batch/")]
    [InlineData("https://statistiche.example.it", "https://statistiche.example.it/batch/")]
    [InlineData("https://statistiche.example.it/", "https://statistiche.example.it/batch/")]
    [InlineData("https://example.it/statistiche", "https://example.it/statistiche/batch/")]
    [InlineData(" https://example.it:8443/statistiche/ ", "https://example.it:8443/statistiche/batch/")]
    [InlineData("http://statistiche.example.it/", null)]
    [InlineData("https://statistiche.example.it/?chiave=1", null)]
    [InlineData("statistiche.example.it", null)]
    public void LIndirizzoDeiLottiSiRicavaSoloDaUnIndirizzoHttps(string indirizzo, string? atteso)
        => Assert.Equal(atteso, TelemetrySettings.ToBatchEndpoint(indirizzo)?.AbsoluteUri);

    [Fact]
    public void LaConfigurazioneDelCloudUeEQuellaDellaDocumentazione()
    {
        // E' l'esempio di settings.json in docs/INSTALLAZIONE.md, paragrafo 11: se un
        // giorno il programma smettesse di accettarlo, la documentazione mentirebbe a chi
        // configura la prima postazione. Nessuna connessione verso PostHog: la prova
        // guarda solo come l'indirizzo viene letto.
        var cloud = new TelemetrySettings { ServerUrl = "https://eu.i.posthog.com/", ProjectKey = Chiave, Enabled = true };

        Assert.True(cloud.IsActive);
        Assert.Equal("https://eu.i.posthog.com/batch/", cloud.BatchEndpoint?.AbsoluteUri);
    }

    [Fact]
    public async Task DuranteUnaChiamataNonSiMandaNiente()
    {
        Configura(consenso: true);

        var inLinea = true;

        await using var servizio = Servizio(inChiamata: () => inLinea);
        await Giri(servizio);

        Assert.Equal(0, _server.Connessioni);
        Assert.True(servizio.PendingCount > 0, "gli eventi devono aspettare, non perdersi");

        inLinea = false;
        await Giri(servizio, 1);

        Assert.Single(_server.Richieste);
        Assert.Equal(0, servizio.PendingCount);
    }

    // -----------------------------------------------------------------------
    // Gli invii
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GliEventiPartonoALottiVersoIlServerFinto()
    {
        Configura(consenso: true);

        await using var servizio = Servizio();
        await Giri(servizio, 1);

        var (metodo, percorso, corpo) = Assert.Single(_server.Richieste);
        Assert.Equal("POST", metodo);
        Assert.Equal("/batch/", percorso);

        var radice = JsonDocument.Parse(corpo).RootElement;
        Assert.Equal(Chiave, radice.GetProperty("api_key").GetString());
        Assert.False(radice.GetProperty("historical_migration").GetBoolean());

        var primi = radice.GetProperty("batch").EnumerateArray().ToList();
        Assert.Equal(["installazione", "avvio", "attivita_giornaliera"], primi.Select(e => e.GetProperty("event").GetString()));

        var identificativo = primi[0].GetProperty("distinct_id").GetString()!;
        Assert.True(TelemetryDiary.IsInstallationId(identificativo));

        foreach (var evento in primi)
        {
            Assert.Equal(identificativo, evento.GetProperty("distinct_id").GetString());

            // Il giorno, non l'ora: alle otto e mezza del mattino l'evento dice mezzogiorno.
            Assert.Equal("2026-11-02T12:00:00Z", evento.GetProperty("timestamp").GetString());

            var proprieta = evento.GetProperty("properties");
            Assert.False(proprieta.GetProperty("$process_person_profile").GetBoolean());
            Assert.True(proprieta.GetProperty("$geoip_disable").GetBoolean());
        }

        // Ogni comando da ogni origine: piu' eventi di quanti ne stiano in un lotto.
        foreach (var comando in Enum.GetValues<HotkeyCommand>())
        {
            foreach (var origine in Enum.GetValues<CommandOrigin>()) servizio.RecordCommand(comando, origine);
        }

        var attesi = Enum.GetValues<HotkeyCommand>().Length * Enum.GetValues<CommandOrigin>().Length;

        _orologio.Advance(TimeSpan.FromDays(1));
        await Giri(servizio, 5);

        var lotti = _server.Richieste.Skip(1)
            .Select(r => JsonDocument.Parse(r.Corpo).RootElement.GetProperty("batch").GetArrayLength())
            .ToList();

        Assert.True(lotti.Count > 1, "con " + attesi + " eventi deve partire piu' di un lotto");
        Assert.All(lotti, n => Assert.InRange(n, 1, PostHogClient.MaxBatchSize));

        var comandi = EventiRicevuti().Where(e => e.GetProperty("event").GetString() == "comando_usato").ToList();

        Assert.Equal(attesi, comandi.Count);
        Assert.All(comandi, c => Assert.Equal("2026-11-02T12:00:00Z", c.GetProperty("timestamp").GetString()));
        Assert.Equal(0, servizio.PendingCount);
    }

    [Fact]
    public async Task NelCorpoInviatoCeSoloQuelloCheIlCatalogoAmmette()
    {
        // Una postazione piena di dati che non devono uscire: interno, dominio,
        // centralino, STUN, colleghi con il nome, numero della segreteria.
        _impostazioni.Save(_impostazioni.Current with
        {
            SipAccounts =
            [
                new SipSettings
                {
                    Id = "a1",
                    Name = "Centralino Rossi",
                    Username = "4711",
                    Domain = "centralino.asl-prova.example.it",
                    StunServer = "stun.asl-prova.example.it",
                    VoicemailNumber = "*9797",
                    Transport = "Tls",
                    Srtp = "Required",
                },
                new SipSettings { Id = "a2", Name = "Notte", Username = "4713", Domain = "asl-prova.example.it" },
            ],
            DefaultSipAccountId = "a1",
            Pbx = new PbxSettings { Host = "pbx.asl-prova.example.it", ClientId = "cliente-asl-prova" },
            Colleagues = new ColleaguesSettings { Monitored = "4712 Mario Rossi" },
            Updates = new UpdateSettings { ServerUrl = "ftps://aggiornamenti.asl-prova.example.it/" },
            Support = new SupportSettings { Email = "assistenza@asl-prova.example.it" },
        });

        Configura(consenso: true);

        await using var servizio = Servizio();
        RegistraUnPoDiTutto(servizio);
        servizio.RecordError(new FileNotFoundException("C:\\Users\\mrossi\\rubrica.csv"), ErrorOrigin.BackgroundTask, fatal: true);
        await Giri(servizio, 1);

        _orologio.Advance(TimeSpan.FromDays(1));
        await Giri(servizio, 2);

        Assert.NotEmpty(_server.Richieste);

        var seiCifre = new Regex(@"\d{6,}");
        var proibiti = new[] { "4711", "4712", "4713", "9797", "asl-prova", "Rossi", "mrossi", "0612345678", "Impossibile" };

        foreach (var (_, _, corpo) in _server.Richieste)
        {
            foreach (var parola in proibiti) Assert.DoesNotContain(parola, corpo, StringComparison.OrdinalIgnoreCase);

            var radice = JsonDocument.Parse(corpo).RootElement;
            Assert.Equal(["api_key", "batch", "historical_migration"], radice.EnumerateObject().Select(p => p.Name).Order());

            foreach (var evento in radice.GetProperty("batch").EnumerateArray())
            {
                Assert.Equal(
                    ["distinct_id", "event", "properties", "timestamp"],
                    evento.EnumerateObject().Select(p => p.Name).Order());

                var nome = evento.GetProperty("event").GetString()!;
                var definizione = TelemetryCatalog.Find(nome);
                Assert.True(definizione is not null, $"evento {nome} fuori dal catalogo");

                Assert.DoesNotMatch(seiCifre, evento.GetProperty("distinct_id").GetString()!);
                Assert.DoesNotMatch(seiCifre, evento.GetProperty("timestamp").GetString()!);

                foreach (var proprieta in evento.GetProperty("properties").EnumerateObject())
                {
                    if (proprieta.Name is "$process_person_profile" or "$geoip_disable") continue;

                    var ammessa = definizione!.Find(proprieta.Name);
                    Assert.True(ammessa is not null, $"{nome}.{proprieta.Name} non e' nel catalogo");

                    // Solo si', no e testi: nessun numero, nessun oggetto, nessun elenco.
                    Assert.True(
                        proprieta.Value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.String,
                        $"{nome}.{proprieta.Name} ha un valore di tipo {proprieta.Value.ValueKind}");

                    var valore = proprieta.Value.ValueKind == JsonValueKind.String
                        ? (object)proprieta.Value.GetString()!
                        : proprieta.Value.GetBoolean();

                    Assert.True(ammessa!.Accepts(valore), $"{nome}.{proprieta.Name} = {valore}");

                    if (valore is string testo) Assert.DoesNotMatch(seiCifre, testo);
                }
            }
        }
    }

    [Fact]
    public async Task UnServerCheNonRispondeNonFaPerdereGliEventi()
    {
        Configura(consenso: true);
        _server.Risposta = HttpStatusCode.ServiceUnavailable;

        await using var servizio = Servizio();
        await Giri(servizio, 1);

        var inAttesa = servizio.PendingCount;
        Assert.True(inAttesa > 0);
        Assert.Single(_server.Richieste);

        _server.Risposta = HttpStatusCode.OK;
        await Giri(servizio, 1);

        Assert.Equal(0, servizio.PendingCount);
        Assert.Equal(inAttesa, EventiRicevuti().Count / 2);
    }

    [Fact]
    public async Task UnLottoRifiutatoNonSiRiprovaAllInfinito()
    {
        // Un 401 per una chiave sbagliata restera' un 401: riprovarlo ogni pochi minuti
        // per settimane sarebbe solo traffico.
        Configura(consenso: true);
        _server.Risposta = HttpStatusCode.Unauthorized;

        await using var servizio = Servizio();
        await Giri(servizio, 3);

        Assert.Single(_server.Richieste);
        Assert.Equal(0, servizio.PendingCount);
    }

    [Fact]
    public async Task GliEventiNonPartitiSopravvivonoAllaChiusura()
    {
        Configura(consenso: true);
        _server.Risposta = HttpStatusCode.ServiceUnavailable;

        string identificativo;

        await using (var primo = Servizio())
        {
            await Giri(primo, 1);
            identificativo = JsonDocument.Parse(_server.Richieste.Single().Corpo).RootElement
                .GetProperty("batch")[0].GetProperty("distinct_id").GetString()!;
        }

        Assert.True(File.Exists(Path.Combine(_impostazioni.DataDirectory, TelemetryService.StateFileName)));

        _server.Risposta = HttpStatusCode.OK;

        await using var secondo = Servizio();
        await Giri(secondo, 1);

        var ultimi = JsonDocument.Parse(_server.Richieste.Last().Corpo).RootElement.GetProperty("batch")
            .EnumerateArray().ToList();

        Assert.All(ultimi, e => Assert.Equal(identificativo, e.GetProperty("distinct_id").GetString()));
        Assert.Single(ultimi, e => e.GetProperty("event").GetString() == "installazione");
        Assert.Equal(2, ultimi.Count(e => e.GetProperty("event").GetString() == "avvio"));
    }

    [Fact]
    public async Task SpegnendoIlConsensoLaPostazioneDimenticaTutto()
    {
        Configura(consenso: true);

        await using var servizio = Servizio();
        await Giri(servizio, 1);

        var primoIdentificativo = EventiRicevuti()[0].GetProperty("distinct_id").GetString();
        Assert.True(File.Exists(servizio.StatePath));

        servizio.RecordCommand(HotkeyCommand.Answer, CommandOrigin.Keyboard);

        // Vale dal salvataggio, non dal giro successivo.
        Configura(consenso: false);

        Assert.False(File.Exists(servizio.StatePath));
        Assert.False(servizio.IsCollecting);

        var richieste = _server.Richieste.Count;
        _orologio.Advance(TimeSpan.FromDays(1));
        await Giri(servizio);

        Assert.Equal(richieste, _server.Richieste.Count);

        // Riaccendendo, la postazione e' un'installazione nuova: i due periodi non si
        // ricollegano.
        Configura(consenso: true);
        await Giri(servizio, 1);

        var nuovi = JsonDocument.Parse(_server.Richieste.Last().Corpo).RootElement.GetProperty("batch")
            .EnumerateArray().ToList();

        Assert.Equal("installazione", nuovi[0].GetProperty("event").GetString());
        Assert.NotEqual(primoIdentificativo, nuovi[0].GetProperty("distinct_id").GetString());
        Assert.DoesNotContain(nuovi, e => e.GetProperty("event").GetString() == "comando_usato");
    }

    [Fact]
    public async Task UnFileScrittoAManoNonFaUscireNiente()
    {
        // Il file sta nella cartella dell'utente: chiunque usi quella utenza lo puo'
        // scrivere. Quello che non e' del catalogo non parte. L'identificativo invece e'
        // valido, perche' il file venga davvero ripreso: con uno inventato il diario lo
        // scarta intero, e la prova non proverebbe niente.
        Configura(consenso: true);

        const string identificativo = "abcdefghijklmnopabcdefghijklmnop";

        var percorso = Path.Combine(_impostazioni.DataDirectory, TelemetryService.StateFileName);
        File.WriteAllText(percorso, $$"""
            {
              "installationId": "{{identificativo}}",
              "lastVersion": "1.0.19",
              "pending": [
                { "name": "chiamata", "day": "2026-11-01", "properties": { "numero": "0612345678" } },
                { "name": "attivita_giornaliera", "day": "2026-11-01", "properties": { "versione": "1.0.19", "modalita": "Mario Rossi", "chiamate": 42 } }
              ]
            }
            """);

        await using var servizio = Servizio();
        await Giri(servizio, 1);

        Assert.All(EventiRicevuti(), e => Assert.Equal(identificativo, e.GetProperty("distinct_id").GetString()));
        Assert.Equal(["avvio", "attivita_giornaliera"], EventiRicevuti().Select(e => e.GetProperty("event").GetString()));

        foreach (var (_, _, corpo) in _server.Richieste)
        {
            Assert.DoesNotContain("0612345678", corpo, StringComparison.Ordinal);
            Assert.DoesNotContain("Rossi", corpo, StringComparison.Ordinal);
            Assert.DoesNotContain("chiamat", corpo, StringComparison.Ordinal);
        }
    }

    // -----------------------------------------------------------------------
    // Configurazione distribuita e pannello
    // -----------------------------------------------------------------------

    [Fact]
    public void IlPacchettoDiConfigurazionePortaLeStatisticheEDiceCheSonoAccese()
    {
        var amministratore = Amministratore();
        amministratore.Save(amministratore.Current with
        {
            Telemetry = new TelemetrySettings { Enabled = true, ServerUrl = "https://statistiche.example.it/", ProjectKey = Chiave },
        });

        var pacchetto = new ConfigurationBundleService(amministratore, new HotkeyProfileStore(amministratore.DataDirectory))
            .Compose([SettingsSection.Diagnostics], includeHotkeys: false, policy: null, revision: 2, description: null);

        var esito = new ConfigurationBundleService(_impostazioni, new HotkeyProfileStore(_impostazioni.DataDirectory))
            .Apply(pacchetto);

        Assert.True(esito.Success, esito.Message);
        Assert.Equal(amministratore.Current.Telemetry, _impostazioni.Current.Telemetry);

        // Chi siede davanti alla postazione lo sente: da oggi qualcosa parte.
        Assert.Contains("statistiche d'uso", esito.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnPacchettoSenzaLaDiagnosticaNonToccaLeStatistiche()
    {
        Configura(consenso: false);

        var amministratore = Amministratore();
        amministratore.Save(amministratore.Current with { Telemetry = new TelemetrySettings { Enabled = true } });

        var pacchetto = new ConfigurationBundleService(amministratore, new HotkeyProfileStore(amministratore.DataDirectory))
            .Compose([SettingsSection.Audio], includeHotkeys: false, policy: null, revision: 2, description: null);

        new ConfigurationBundleService(_impostazioni, new HotkeyProfileStore(_impostazioni.DataDirectory)).Apply(pacchetto);

        Assert.False(_impostazioni.Current.Telemetry.Enabled);
    }

    [Fact]
    public void LaCasellaDelConsensoStaNellaDiagnosticaConNomeAiutoETasto()
    {
        var cartella = new DirectoryInfo(AppContext.BaseDirectory);
        while (cartella is not null && !Directory.Exists(Path.Combine(cartella.FullName, "src", "BlindPhone.App")))
        {
            cartella = cartella.Parent;
        }

        Assert.NotNull(cartella);

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var documento = XDocument.Load(Path.Combine(cartella!.FullName, "src", "BlindPhone.App", "SettingsWindow.xaml"));
        var casella = documento.Descendants(p + "CheckBox").Single(c => (string?)c.Attribute(x + "Name") == "TelemetryEnabled");

        Assert.Equal("DiagnosticsTab", (string?)casella.Ancestors(p + "TabItem").First().Attribute(x + "Name"));
        Assert.Contains("_t", (string?)casella.Attribute("Content"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(AttributoDiAutomazione(casella, "Name")));
        Assert.False(string.IsNullOrWhiteSpace(AttributoDiAutomazione(casella, "HelpText")));
    }

    private static string? AttributoDiAutomazione(XElement elemento, string nome)
        => elemento.Attributes().FirstOrDefault(a => a.Name.LocalName == "AutomationProperties." + nome)?.Value;
}
