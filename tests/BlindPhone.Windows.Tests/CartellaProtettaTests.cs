using BlindPhone.Core.Domain;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che un pacchetto di configurazione distribuito venga applicato solo se
/// arriva da una cartella che soltanto gli amministratori possono scrivere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il caso che questi test proteggono.</b> Un pacchetto distribuito vale per tutti
/// i profili della postazione: sposta l'indirizzo del centralino, cambia il registrar
/// SIP, dirotta l'indirizzo a cui vanno i pacchetti di assistenza e può installare un
/// criterio con password che impedisce all'operatore di rimettere le cose a posto.
/// </para>
/// <para>
/// Con i permessi che <c>%ProgramData%</c> ha di suo, un utente standard può creare
/// quella sottocartella — diventandone proprietario — oppure scriverci dentro se
/// l'ha creata chi non ha interrotto l'ereditarietà. Su una postazione condivisa fra
/// turni, tanto basta a un operatore qualsiasi per riconfigurare quella di tutti gli
/// altri. Fino alla 1.0.16 la posizione del file era l'unica cosa che veniva
/// guardata.
/// </para>
/// <para>
/// La cartella usata qui è una cartella temporanea del profilo di chi esegue i test,
/// cioè scrivibile dall'utente: è esattamente la condizione che va rifiutata, e non
/// richiede di toccare i permessi di una cartella di sistema per riprodurla.
/// </para>
/// </remarks>
public sealed class CartellaProtettaTests : IDisposable
{
    private readonly SettingsService _settings = SettingsService.CreateTemporary("cartella-protetta");
    private readonly HotkeyProfileStore _hotkeys;
    private readonly string _distribuzione;

    public CartellaProtettaTests()
    {
        _settings.Load();
        _hotkeys = new HotkeyProfileStore(_settings.DataDirectory);

        _distribuzione = Path.Combine(_settings.DataDirectory, "distribuzione");
        Directory.CreateDirectory(_distribuzione);
    }

    private ConfigurationBundleService Servizio()
        => new(_settings, _hotkeys, logger: null, provisioningFolders: [_distribuzione]);

    /// <summary>Scrive un pacchetto nella cartella di distribuzione di prova.</summary>
    private void ScriviPacchetto(int revisione, string host)
    {
        var servizio = Servizio();

        var impostazioni = _settings.Current with
        {
            Pbx = _settings.Current.Pbx with { Host = host },
        };

        Assert.True(_settings.Save(impostazioni));

        var bundle = servizio.Compose(
            [SettingsSection.Pbx],
            includeHotkeys: false,
            policy: null,
            revision: revisione,
            description: "Pacchetto di prova");

        var percorso = Path.Combine(_distribuzione, ConfigurationBundle.ProvisioningFileName);
        Assert.True(servizio.Save(bundle, percorso).Success);

        // Si riporta la postazione a com'era, cosi' l'applicazione del pacchetto sia
        // un cambiamento osservabile e non un nulla di fatto.
        Assert.True(_settings.Save(_settings.Current with
        {
            Pbx = _settings.Current.Pbx with { Host = "centralino-di-partenza" },
            AppliedConfigurationRevision = 0,
        }));
    }

    [Fact]
    public void UnPacchettoInUnaCartellaScrivibileDallUtenteVieneIgnorato()
    {
        ScriviPacchetto(revisione: 5, host: "centralino-dell-attaccante");

        var esito = Servizio().ApplyProvisioningIfNewer();

        // Non "null": l'operatore deve sentire che un pacchetto c'era e perche' e'
        // stato scartato. Un pacchetto ignorato in silenzio e' indistinguibile da un
        // pacchetto mai arrivato, e manderebbe chi amministra a cercare il guasto sul
        // server di distribuzione.
        Assert.NotNull(esito);
        Assert.False(esito!.Success);
        Assert.Contains("non e' protetta", esito.Message, StringComparison.OrdinalIgnoreCase);

        // E soprattutto: la configurazione non e' stata toccata.
        Assert.Equal("centralino-di-partenza", _settings.Current.Pbx.Host);
        Assert.Equal(0, _settings.Current.AppliedConfigurationRevision);
    }

    [Fact]
    public void UnPacchettoInUnaCartellaCheNonEsisteNonProduceNulla()
    {
        Directory.Delete(_distribuzione, recursive: true);

        Assert.Null(Servizio().ApplyProvisioningIfNewer());
    }

    [Fact]
    public void IPermessiDiUnaCartellaTemporaneaNonSonoSoloAmministrativi()
    {
        var percorso = Path.Combine(_distribuzione, "prova.bpcfg");
        File.WriteAllText(percorso, "{}");

        Assert.False(ProtectedLocation.IsAdminOnly(percorso, out var motivo));

        // Il motivo serve a chi legge il registro: deve dire che cosa non torna, non
        // soltanto che qualcosa non torna.
        Assert.False(string.IsNullOrWhiteSpace(motivo));
    }

    [Fact]
    public void UnFileCheNonEsisteNonRisultaProtetto()
    {
        var percorso = Path.Combine(_distribuzione, "mai-scritto.bpcfg");

        Assert.False(ProtectedLocation.IsAdminOnly(percorso, out var motivo));
        Assert.False(string.IsNullOrWhiteSpace(motivo));
    }

    [Fact]
    public void UnaCartellaDiSistemaRisultaProtetta()
    {
        // Il verso positivo del controllo, su una cartella i cui permessi Windows
        // scrive da se': in System32 solo TrustedInstaller, SYSTEM e gli
        // amministratori possono scrivere. Se questa prova fallisse, il controllo
        // sarebbe troppo severo e rifiuterebbe anche i pacchetti legittimi.
        var percorso = Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        Assert.True(File.Exists(percorso));
        Assert.True(ProtectedLocation.IsAdminOnly(percorso, out var motivo), motivo);
    }

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
            // Una cartella temporanea che non si cancella non e' il risultato del
            // test: la pulizia del profilo ci arriva comunque.
        }
    }
}
