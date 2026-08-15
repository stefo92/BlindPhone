using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica gli account SIP multipli e la migrazione dallo schema a un account solo.
/// </summary>
/// <remarks>
/// <para>
/// Il caso che questi test proteggono è una postazione in servizio che si aggiorna:
/// ha un account funzionante e la password nel deposito sotto la chiave storica. Se
/// la migrazione cambiasse quella chiave la password risulterebbe persa, e il
/// sintomo sarebbe una postazione muta al primo riavvio — con l'operatore che non ha
/// modo di capire perché, perché a schermo la configurazione sembra intatta.
/// </para>
/// </remarks>
public sealed class SipAccountsTests
{
    [Fact]
    public void UnaConfigurazioneVecchiaDiventaUnElencoDiUnAccount()
    {
        var vecchia = new AppSettings
        {
            SchemaVersion = 1,
            Sip = new SipSettings { Username = "200", Domain = "pbx.ente.it", Name = "Principale" },
        };

        var migrata = vecchia.Migrate();

        Assert.Single(migrata.SipAccounts);
        Assert.Equal("200", migrata.SipAccounts[0].Username);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrata.SchemaVersion);
    }

    [Fact]
    public void LAccountMigratoConservaLaChiaveStoricaDellaPassword()
    {
        // E' il punto su cui si gioca l'aggiornamento di una postazione in servizio:
        // una chiave diversa significa password persa, cioe' telefono muto.
        var migrata = new AppSettings
        {
            Sip = new SipSettings { Username = "200", Domain = "pbx.ente.it" },
        }.Migrate();

        Assert.Equal(SecretStore.SipPasswordKey, migrata.SipAccounts[0].PasswordKey);
    }

    [Fact]
    public void UnAccountNuovoHaUnaChiaveTuttaSua()
    {
        var account = new SipSettings { Id = "abc123", Username = "300" };

        Assert.NotEqual(SecretStore.SipPasswordKey, account.PasswordKey);
        Assert.Contains("abc123", account.PasswordKey, StringComparison.Ordinal);
    }

    [Fact]
    public void LaMigrazioneEIdempotente()
    {
        var una = new AppSettings { Sip = new SipSettings { Username = "200", Domain = "pbx" } }.Migrate();
        var due = una.Migrate();

        Assert.Single(due.SipAccounts);
        Assert.Equal(una.DefaultSipAccountId, due.DefaultSipAccountId);
    }

    [Fact]
    public void UnPredefinitoOrfanoTornaSulPrimoAccount()
    {
        // Un identificativo che punta a un account eliminato a mano dal file
        // lascerebbe la postazione non registrata senza dire perche'.
        var settings = new AppSettings
        {
            SipAccounts =
            [
                new SipSettings { Id = "a", Username = "200", Domain = "pbx" },
                new SipSettings { Id = "b", Username = "201", Domain = "pbx" },
            ],
            DefaultSipAccountId = "sparito",
        }.Migrate();

        Assert.Equal("a", settings.DefaultSipAccountId);
        Assert.Equal("200", settings.ActiveSipAccount.Username);
    }

    [Fact]
    public void LAccountAttivoEQuelloPredefinito()
    {
        var settings = new AppSettings
        {
            SipAccounts =
            [
                new SipSettings { Id = "a", Username = "200", Domain = "pbx" },
                new SipSettings { Id = "b", Username = "201", Domain = "pbx" },
            ],
            DefaultSipAccountId = "b",
        };

        Assert.Equal("201", settings.ActiveSipAccount.Username);
    }

    [Fact]
    public void UnPredefinitoIncompletoCedeIlPostoAUnoUtilizzabile()
    {
        // Un account senza dominio porterebbe in modalita' simulata: sembra
        // funzionare e non riceve chiamate, che e' il modo peggiore di sbagliare.
        var settings = new AppSettings
        {
            SipAccounts =
            [
                new SipSettings { Id = "a", Username = "200" },
                new SipSettings { Id = "b", Username = "201", Domain = "pbx" },
            ],
            DefaultSipAccountId = "a",
        };

        Assert.Equal("201", settings.ActiveSipAccount.Username);
    }

    [Fact]
    public void UnAccountSenzaInternoOSenzaDominioNonEUtilizzabile()
    {
        Assert.False(new SipSettings { Username = "200" }.IsUsable);
        Assert.False(new SipSettings { Domain = "pbx" }.IsUsable);
        Assert.True(new SipSettings { Username = "200", Domain = "pbx" }.IsUsable);
    }

    [Fact]
    public void LEtichettaParlataDistingueAccountConLoStessoNome()
    {
        // Due account sullo stesso centralino si distinguono per l'interno, due con
        // lo stesso interno su centralini diversi per il dominio. Leggere il solo
        // nome li renderebbe indistinguibili proprio nell'elenco che serve a
        // sceglierli.
        var uno = new SipSettings { Name = "Centralino", Username = "200", Domain = "a.it" };
        var due = new SipSettings { Name = "Centralino", Username = "200", Domain = "b.it" };

        Assert.NotEqual(uno.SpeechLabel, due.SpeechLabel);
        Assert.Contains("200", uno.SpeechLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void SalvandoSiAllineaIlCampoStoricoAllAccountPredefinito()
    {
        // Una configurazione esportata da qui deve restare utilizzabile da una
        // versione che gli account multipli non li conosce: deve trovare in "sip"
        // l'account predefinito e non un oggetto vuoto.
        var servizio = SettingsService.CreateTemporary(nameof(SalvandoSiAllineaIlCampoStoricoAllAccountPredefinito));

        try
        {
            servizio.Save(new AppSettings
            {
                SipAccounts =
                [
                    new SipSettings { Id = "a", Username = "200", Domain = "pbx" },
                    new SipSettings { Id = "b", Username = "201", Domain = "pbx" },
                ],
                DefaultSipAccountId = "b",
            });

            Assert.Equal("201", servizio.Current.Sip.Username);
            Assert.Equal("201", servizio.Load().Sip.Username);
        }
        finally
        {
            TryDelete(servizio.DataDirectory);
        }
    }

    [Fact]
    public void UnaConfigurazioneVecchiaSuDiscoVieneMigrataInLettura()
    {
        // La migrazione avviene in lettura e non in scrittura: una postazione che si
        // aggiorna deve funzionare al primo avvio, prima che qualcuno apra le
        // impostazioni e salvi.
        var servizio = SettingsService.CreateTemporary(nameof(UnaConfigurazioneVecchiaSuDiscoVieneMigrataInLettura));

        try
        {
            File.WriteAllText(
                servizio.SettingsPath,
                """
                {
                  "schemaVersion": 1,
                  "sip": { "name": "Principale", "username": "200", "domain": "pbx.ente.it" }
                }
                """);

            var caricata = servizio.Load();

            Assert.Single(caricata.SipAccounts);
            Assert.Equal("200", caricata.ActiveSipAccount.Username);
            Assert.Equal(SecretStore.SipPasswordKey, caricata.ActiveSipAccount.PasswordKey);
        }
        finally
        {
            TryDelete(servizio.DataDirectory);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Cartella temporanea trattenuta da un altro processo: non e' il soggetto
            // della prova.
        }
    }
}
