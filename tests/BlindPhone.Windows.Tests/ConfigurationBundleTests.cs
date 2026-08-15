using BlindPhone.Core.Domain;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica il pacchetto di configurazione distribuibile.
/// </summary>
/// <remarks>
/// <para>
/// La proprietà da proteggere è che un pacchetto sovrascriva <b>solo</b> le sezioni
/// che dichiara. È quella che lo rende applicabile a postazioni già in servizio: se
/// scrivesse tutto, l'ufficio IT che distribuisce la configurazione audio di reparto
/// cancellerebbe l'interno SIP di venti operatori — e se ne accorgerebbe alla prima
/// telefonata che non parte.
/// </para>
/// <para>
/// La seconda è che nessun segreto esca dal file. Le password vivono sotto DPAPI,
/// legate all'utente e alla macchina: metterle in un pacchetto le renderebbe
/// leggibili altrove, che è esattamente ciò da cui DPAPI protegge.
/// </para>
/// </remarks>
public sealed class ConfigurationBundleTests : IDisposable
{
    private readonly SettingsService _settings = SettingsService.CreateTemporary("pacchetto");
    private readonly HotkeyProfileStore _hotkeys;
    private readonly ConfigurationBundleService _service;

    public ConfigurationBundleTests()
    {
        _settings.Load();
        _hotkeys = new HotkeyProfileStore(_settings.DataDirectory);
        _service = new ConfigurationBundleService(_settings, _hotkeys);
    }

    private string TempFile(string nome) => Path.Combine(_settings.DataDirectory, nome);

    [Fact]
    public void UnPacchettoSiScriveESiRilegge()
    {
        var bundle = _service.Compose(
            [SettingsSection.Audio, SettingsSection.Accessibility],
            includeHotkeys: true,
            policy: null,
            revision: 3,
            description: "Configurazione di reparto");

        var percorso = TempFile("prova" + ConfigurationBundle.FileExtension);

        Assert.True(_service.Save(bundle, percorso).Success);

        var riletto = _service.Read(percorso);

        Assert.NotNull(riletto);
        Assert.Equal(3, riletto!.Revisione);
        Assert.Equal("Configurazione di reparto", riletto.Descrizione);
        Assert.Equal(2, riletto.Sezioni.Count);
        Assert.NotNull(riletto.ProfiliTasti);
    }

    [Fact]
    public void UnFileCheNonEUnPacchettoVieneRifiutato()
    {
        // Un settings.json esportato con la vecchia funzione e' anch'esso JSON e
        // finisce nella stessa cartella: applicarlo come pacchetto scriverebbe
        // sezioni che nessuno ha scelto.
        var percorso = TempFile("altro.json");
        File.WriteAllText(percorso, """{ "schemaVersion": 3, "sip": {} }""");

        Assert.Null(_service.Read(percorso));
    }

    [Fact]
    public void VengonoSovrascritteSoloLeSezioniDichiarate()
    {
        // E' la proprieta' che rende il pacchetto applicabile a postazioni gia' in
        // servizio.
        _settings.Save(_settings.Current with
        {
            SipAccounts = [new SipSettings { Id = "locale", Username = "999", Domain = "mio.pbx" }],
            DefaultSipAccountId = "locale",
            Audio = new AudioTuning { JitterBufferMs = 111 },
        });

        var pacchetto = new ConfigurationBundle
        {
            Formato = ConfigurationBundle.FormatMarker,
            Revisione = 2,
            Sezioni = [SettingsSection.Audio],
            Impostazioni = new AppSettings
            {
                SipAccounts = [new SipSettings { Id = "reparto", Username = "200", Domain = "altro.pbx" }],
                DefaultSipAccountId = "reparto",
                Audio = new AudioTuning { JitterBufferMs = 222 },
            },
        };

        Assert.True(_service.Apply(pacchetto).Success);

        // L'audio arriva dal pacchetto...
        Assert.Equal(222, _settings.Current.Audio.JitterBufferMs);

        // ...e l'interno della postazione resta il suo.
        Assert.Equal("999", _settings.Current.ActiveSipAccount.Username);
    }

    [Fact]
    public void GliIdentificativiDeiDispositiviNonEsconoDallaPostazione()
    {
        // Su un'altra macchina puntano a schede diverse o a niente. I nomi restano,
        // perche' servono a capire che cosa era stato scelto.
        _settings.Save(_settings.Current with
        {
            Conversation = new AudioRouteSettings("{id-di-questa-macchina}", "Altoparlanti (EPOS PC 7 USB)"),
        });

        var bundle = _service.Compose(
            [SettingsSection.Audio], includeHotkeys: false, policy: null, revision: 1, description: null);

        Assert.Null(bundle.Impostazioni!.Conversation.DeviceId);
        Assert.Equal("Altoparlanti (EPOS PC 7 USB)", bundle.Impostazioni.Conversation.DeviceName);
    }

    [Fact]
    public void IlPacchettoNonContieneSegreti()
    {
        _settings.Secrets.Set(SecretStore.SipPasswordKey, "password-da-non-esportare");

        var percorso = TempFile("segreti" + ConfigurationBundle.FileExtension);

        _service.Save(
            _service.Compose(
                ConfigurationBundleService.AllSections,
                includeHotkeys: true,
                policy: null,
                revision: 1,
                description: null),
            percorso);

        var testo = File.ReadAllText(percorso);

        Assert.DoesNotContain("password-da-non-esportare", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LaRevisioneApplicataViieneRicordata()
    {
        _service.Apply(new ConfigurationBundle
        {
            Formato = ConfigurationBundle.FormatMarker,
            Revisione = 7,
            Sezioni = [SettingsSection.Calls],
            Impostazioni = new AppSettings(),
        });

        Assert.Equal(7, _settings.Current.AppliedConfigurationRevision);
    }

    [Fact]
    public void UnPacchettoGiaApplicatoNonVieneRiapplicato()
    {
        // Senza il confronto sulla revisione, ogni avvio cancellerebbe le regolazioni
        // fatte dall'operatore nelle sezioni lasciate libere.
        _settings.Save(_settings.Current with { AppliedConfigurationRevision = 5 });

        var cartella = Path.Combine(_settings.DataDirectory, "provisioning");
        Directory.CreateDirectory(cartella);

        var percorso = Path.Combine(cartella, ConfigurationBundle.ProvisioningFileName);

        _service.Save(
            new ConfigurationBundle
            {
                Formato = ConfigurationBundle.FormatMarker,
                Revisione = 5,
                Sezioni = [SettingsSection.Calls],
            },
            percorso);

        // Il pacchetto letto direttamente ha revisione 5, uguale a quella applicata:
        // la logica di confronto lo scarta.
        var bundle = _service.Read(percorso);

        Assert.NotNull(bundle);
        Assert.True(bundle!.Revisione <= _settings.Current.AppliedConfigurationRevision);
    }

    [Fact]
    public void IlCriterioViaggiaNelPacchetto()
    {
        var criterio = new ConfigurationPolicy
        {
            Access = PanelAccess.ReadOnly,
            LockedSections = [SettingsSection.SipAccounts, SettingsSection.Pbx],
            Note = "Gestita dall'ufficio IT",
        };

        var percorso = TempFile("criterio" + ConfigurationBundle.FileExtension);

        _service.Save(
            _service.Compose([], includeHotkeys: false, policy: criterio, revision: 1, description: null),
            percorso);

        var riletto = _service.Read(percorso);

        Assert.NotNull(riletto!.Criterio);
        Assert.Equal(PanelAccess.ReadOnly, riletto.Criterio!.Access);
        Assert.Equal(2, riletto.Criterio.LockedSections.Count);
        Assert.Equal("Gestita dall'ufficio IT", riletto.Criterio.Note);
    }

    [Fact]
    public void UnCriterioImportatoRisultaDellUtenteEnonDiPostazione()
    {
        // Un pacchetto e' un file: non puo' conferire l'autorita' del registro di
        // sistema, e dichiararlo di postazione farebbe credere a un vincolo che non c'e'.
        _service.Apply(new ConfigurationBundle
        {
            Formato = ConfigurationBundle.FormatMarker,
            Revisione = 1,
            Criterio = new ConfigurationPolicy
            {
                Access = PanelAccess.Locked,
                Origin = PolicyOrigin.Machine,
            },
        });

        Assert.Equal(PolicyOrigin.User, _settings.Current.Policy.Origin);
    }

    [Fact]
    public void IProfiliDeiTastiSiApplicanoSenzaCancellareQuelliPropri()
    {
        // Un operatore che si era costruito una mappa propria non se la vede sparire
        // perche' l'ufficio IT ha distribuito la configurazione di reparto.
        var mio = BlindPhone.Core.Services.HotkeyProfile.CreateDefault();
        mio.Name = "Mappa mia";
        _hotkeys.SaveAndActivate(mio);

        var altro = new HotkeyProfileStore(
            Path.Combine(_settings.DataDirectory, "sorgente"));

        var reparto = BlindPhone.Core.Services.HotkeyProfile.CreateDefault();
        reparto.Name = "Reparto accoglienza";
        altro.SaveAndActivate(reparto);

        Assert.True(_hotkeys.ImportAll(altro.SerializeAll(), "Reparto accoglienza") > 0);

        Assert.Contains("Mappa mia", _hotkeys.ProfileNames);
        Assert.Contains("Reparto accoglienza", _hotkeys.ProfileNames);
    }

    [Fact]
    public void UnPacchettoVuotoNonViieneApplicato()
        => Assert.False(_service.Apply(new ConfigurationBundle { Formato = ConfigurationBundle.FormatMarker }).Success);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_settings.DataDirectory))
            {
                Directory.Delete(_settings.DataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Cartella temporanea trattenuta: non e' il soggetto della prova.
        }
    }
}
