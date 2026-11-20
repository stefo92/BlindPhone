using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che cosa si dice di un indirizzo del server degli aggiornamenti che non
/// può funzionare.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da una postazione con l'indirizzo <c>sftp://</c>: il registro diceva
/// «canale non cifrato», che per SFTP è falso, e mandava a cercare la cifratura su un
/// server che l'aveva già. Il motivo vero era un altro — SFTP è un protocollo diverso,
/// che BlindPhone non parla — e all'avvio il controllo fallito non dice niente a voce:
/// la frase del registro era l'unica traccia, e doveva essere quella giusta.
/// </para>
/// <para>
/// Il controllo con un indirizzo sbagliato si ferma prima di aprire qualunque
/// collegamento, quindi le prove su <see cref="UpdateService.CheckAsync"/> non usano la
/// rete.
/// </para>
/// </remarks>
public sealed class IndirizzoAggiornamentiTests
{
    [Theory]
    [InlineData("sftp://curtiphone.example.org/files/")]
    [InlineData("SFTP://curtiphone.example.org:2222/files/")]
    [InlineData("ssh://curtiphone.example.org/files/")]
    public void SftpSiChiamaPerNomeENonCanaleNonCifrato(string indirizzo)
    {
        var frase = UpdateService.DescribeAddressProblem(indirizzo);

        Assert.NotNull(frase);
        Assert.Contains("SFTP", frase);
        Assert.Contains("ftps://", frase);
        Assert.DoesNotContain("non cifrato", frase);
        Assert.DoesNotContain("in chiaro", frase);
    }

    [Fact]
    public void FtpInChiaroSiDiceInChiaro()
    {
        var frase = UpdateService.DescribeAddressProblem("ftp://aggiornamenti.example.org/blindphone/");

        Assert.NotNull(frase);
        Assert.Contains("in chiaro", frase);
        Assert.Contains("ftps://", frase);
    }

    [Fact]
    public void HttpInChiaroSiDiceInChiaro()
    {
        var frase = UpdateService.DescribeAddressProblem("http://blindphone.example.org/download/");

        Assert.NotNull(frase);
        Assert.Contains("in chiaro", frase);
        Assert.Contains("https://", frase);
    }

    [Theory]
    [InlineData("ftps://aggiornamenti.example.org/blindphone/")]
    [InlineData("FTPS://aggiornamenti.example.org:2121/blindphone/")]
    [InlineData("  ftps://aggiornamenti.example.org/blindphone/  ")]
    [InlineData("https://blindphone.example.org/download/")]
    [InlineData("HTTPS://blindphone.example.org/download")]
    public void FtpsEHttpsVannoBene(string indirizzo)
    {
        Assert.Null(UpdateService.DescribeAddressProblem(indirizzo));
    }

    [Fact]
    public void LIndirizzoPredefinitoELaCartellaDelPortale()
    {
        // Una copia compilata senza toccare niente deve sapere dove cercare, e
        // l'indirizzo deve essere uno che il servizio accetta: un predefinito rifiutato
        // dal controllo sarebbe un aggiornamento automatico spento senza dirlo.
        var predefinito = new AppSettings().Updates.ServerUrl;

        Assert.Equal("https://blindphone.curti.cloud/download/", predefinito);
        Assert.Null(UpdateService.DescribeAddressProblem(predefinito));
        Assert.EndsWith("/", predefinito);
    }

    // -----------------------------------------------------------------------
    // Migrazione allo schema 5
    // -----------------------------------------------------------------------

    private static AppSettings ConfigurazioneEsistente(int schema, string indirizzo) => new()
    {
        SchemaVersion = schema,
        Updates = new UpdateSettings { ServerUrl = indirizzo },
        SipAccounts = [new SipSettings { Id = "a", Username = "200", Domain = "pbx" }],
        DefaultSipAccountId = "a",
    };

    [Fact]
    public void UnaPostazioneConLIndirizzoVuotoPassaAlPortale()
    {
        // Il vuoto era il predefinito fino alla 1.0.20: settings.json lo porta scritto, e
        // cambiare la sola costante non avrebbe acceso gli aggiornamenti su nessuna
        // postazione gia' in uso.
        var migrata = ConfigurazioneEsistente(schema: 4, indirizzo: string.Empty).Migrate();

        Assert.Equal(UpdateSettings.DefaultServerUrl, migrata.Updates.ServerUrl);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrata.SchemaVersion);
    }

    [Fact]
    public void IlServerDellEnteSiTiene()
    {
        var migrata = ConfigurazioneEsistente(schema: 4, indirizzo: "ftps://aggiornamenti.ente.it/blindphone/").Migrate();

        Assert.Equal("ftps://aggiornamenti.ente.it/blindphone/", migrata.Updates.ServerUrl);
    }

    [Fact]
    public void LaMigrazioneNonRiscriveUnIndirizzoSvuotatoDopo()
    {
        // La migrazione avviene una volta sola e il campo resta come l'ha lasciato chi
        // l'ha svuotato. Che cosa vuol dire un campo vuoto lo decide un'altra cosa, e lo
        // provano le prove qui sotto: vale come il portale.
        var scelta = ConfigurazioneEsistente(AppSettings.CurrentSchemaVersion, indirizzo: string.Empty);

        Assert.Equal(string.Empty, scelta.Migrate().Updates.ServerUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NessunIndirizzoNonEUnProblema(string? indirizzo)
    {
        // Un campo vuoto vale come la cartella del portale: chi non ha scritto niente non
        // deve sentirsi dire che ha sbagliato qualcosa.
        Assert.Null(UpdateService.DescribeAddressProblem(indirizzo));
    }

    // -----------------------------------------------------------------------
    // Un indirizzo vuoto vale come il portale
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UnIndirizzoVuotoValeComeLaCartellaDelPortale(string indirizzo)
        => Assert.Equal(
            UpdateSettings.DefaultServerUrl,
            new UpdateSettings { ServerUrl = indirizzo }.EffectiveServerUrl);

    [Fact]
    public void UnIndirizzoScrittoSiUsaComeEScritto()
        => Assert.Equal(
            "ftps://aggiornamenti.ente.it/blindphone/",
            new UpdateSettings { ServerUrl = "  ftps://aggiornamenti.ente.it/blindphone/  " }.EffectiveServerUrl);

    [Fact]
    public void IlServizioConLIndirizzoVuotoCercaNelPortale()
    {
        // Prima rispondeva «Nessun server degli aggiornamenti configurato», e all'avvio
        // non cercava niente senza dirlo.
        var servizio = new UpdateService(
            new UpdateSettings { ServerUrl = string.Empty },
            password: string.Empty,
            installed: new Version(1, 0, 22));

        Assert.True(servizio.IsConfigured);
        Assert.Equal(UpdateSettings.DefaultServerUrl, servizio.ServerUrl);
    }

    [Fact]
    public void LaPostazioneDellaSegnalazioneTornaACercareGliAggiornamenti()
    {
        // Il caso vero: con la 1.0.21.1 l'operatore ha cancellato l'indirizzo del vecchio
        // server FTPS, per passare al portale come dicevano le novita', e ha salvato. Il
        // file e' rimasto con lo schema 5 e l'indirizzo vuoto, la migrazione era gia'
        // passata, e la 1.0.22 diceva che non c'era nessun indirizzo.
        var impostazioni = SettingsService.CreateTemporary("indirizzo-svuotato");
        Directory.CreateDirectory(impostazioni.DataDirectory);
        File.WriteAllText(
            impostazioni.SettingsPath,
            """{ "schemaVersion": 5, "updates": { "serverUrl": "", "checkOnStartup": true } }""");

        var lette = new SettingsService(impostazioni.DataDirectory).Load();
        var servizio = new UpdateService(lette.Updates, password: string.Empty, installed: new Version(1, 0, 22));

        Assert.True(servizio.IsConfigured);
        Assert.Equal(UpdateSettings.DefaultServerUrl, servizio.ServerUrl);
    }

    [Fact]
    public void GliAggiornamentiSiSpengonoConLaCasellaENonSvuotandoIlCampo()
    {
        var vuoto = new AppSettings { Updates = new UpdateSettings { ServerUrl = string.Empty } };
        var spento = new AppSettings { Updates = new UpdateSettings { CheckOnStartup = false } };

        // E' il dato che le statistiche mandano: deve dire se la postazione li cerca.
        Assert.True(TelemetryFactsBuilder.FromSettings(vuoto, "1.0.22").AutomaticUpdates);
        Assert.False(TelemetryFactsBuilder.FromSettings(spento, "1.0.22").AutomaticUpdates);
    }

    [Theory]
    [InlineData("aggiornamenti.example.org/blindphone/")]
    [InlineData("smb://server/condivisione/")]
    public void OgniAltroIndirizzoDiceCheCosaServe(string indirizzo)
    {
        var frase = UpdateService.DescribeAddressProblem(indirizzo);

        Assert.NotNull(frase);
        Assert.Contains("https://", frase);
        Assert.Contains("ftps://", frase);
    }

    [Fact]
    public async Task IlControlloConUnIndirizzoSftpRiportaLaFraseGiusta()
    {
        // E' la frase che sente chi preme Cerca aggiornamenti, e quella che finisce nel
        // registro all'avvio.
        var servizio = new UpdateService(
            new UpdateSettings { ServerUrl = "sftp://curtiphone.example.org/files/" },
            password: string.Empty,
            installed: new Version(1, 0, 20, 1));

        var esito = await servizio.CheckAsync();

        Assert.True(esito.Failed);
        Assert.False(esito.HasUpdate);
        Assert.Contains("SFTP", esito.Error);
        Assert.DoesNotContain("non cifrato", esito.Error);
    }
}
