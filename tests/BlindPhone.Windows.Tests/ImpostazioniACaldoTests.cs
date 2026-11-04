using BlindPhone.App;
using BlindPhone.Infrastructure;
using BlindPhone.Pbx.Yeastar;
using BlindPhone.Telephony;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica come le impostazioni salvate arrivano ai servizi già avviati, senza
/// riavviare.
/// </summary>
/// <remarks>
/// <para>
/// Il pannello chiedeva di riavviare a ogni salvataggio, anche dopo aver cambiato
/// soltanto il tema. Su un posto operatore il riavvio fa cadere la chiamata e lascia
/// la postazione irraggiungibile per il tempo di un avvio; e una domanda che compare
/// sempre si impara a rifiutarla senza leggerla.
/// </para>
/// <para>
/// I confronti qui sotto decidono se ricostruire il motore telefonico o riaprire il
/// collegamento al centralino. Sbagliarli in un senso lascia una modifica senza
/// effetto; nell'altro fa ripetere la registrazione a ogni salvataggio, anche dopo
/// aver cambiato una voce che col telefono non c'entra.
/// </para>
/// </remarks>
public sealed class ImpostazioniACaldoTests
{
    private static SipAccountOptions Account() => new()
    {
        Name = "Principale",
        Username = "4021",
        Domain = "pbx.ente.local",
        Password = "segreta",
        CodecPreference = ["G729", "G722", "PCMA"],
    };

    [Fact]
    public void LoStessoAccountLettoDueVolteNonRicostruisceIlMotore()
    {
        // L'elenco dei codec e' un'istanza nuova a ogni lettura delle impostazioni:
        // confrontato per riferimento, ogni salvataggio sembrerebbe una modifica.
        var prima = Account();
        var dopo = Account() with { CodecPreference = ["G729", "G722", "PCMA"] };

        Assert.NotSame(prima.CodecPreference, dopo.CodecPreference);
        Assert.True(LiveSettingsApplier.SameAccount(prima, dopo));
    }

    [Fact]
    public void UnCodecSpostatoRicostruisceIlMotore()
    {
        var prima = Account();
        var dopo = Account() with { CodecPreference = ["PCMA", "G729", "G722"] };

        Assert.False(LiveSettingsApplier.SameAccount(prima, dopo));
    }

    [Fact]
    public void UnaPasswordCambiataRicostruisceIlMotore()
    {
        // La password non sta nel file delle impostazioni ma nel deposito protetto: il
        // confronto deve vederla comunque, o correggerla non servirebbe a niente.
        Assert.False(LiveSettingsApplier.SameAccount(Account(), Account() with { Password = "nuova" }));
    }

    [Fact]
    public void UnMotoreSimulatoSiRicostruisceAppenaCEUnAccount()
    {
        // E' la prima configurazione di una postazione nuova: prima serviva il
        // riavvio perche' il telefono smettesse di essere simulato.
        Assert.False(LiveSettingsApplier.SameAccount(null, Account()));
    }

    private static YeastarOptions Centralino() => new()
    {
        Host = "pbx.ente.local",
        ClientId = "blindphone",
        ClientSecret = "segreto",
        QueueIds = [6400, 6401],
    };

    [Fact]
    public void LoStessoCollegamentoLettoDueVolteNonSiRiapre()
    {
        var dopo = Centralino() with { QueueIds = [6400, 6401] };

        Assert.True(LiveSettingsApplier.SamePbx(Centralino(), dopo));
    }

    [Fact]
    public void UnaCodaInPiuRiapreIlCollegamento()
    {
        var dopo = Centralino() with { QueueIds = [6400, 6401, 6402] };

        Assert.False(LiveSettingsApplier.SamePbx(Centralino(), dopo));
    }

    [Fact]
    public void AccendereOSpegnereIlCollegamentoLoRiapre()
    {
        Assert.False(LiveSettingsApplier.SamePbx(null, Centralino()));
        Assert.False(LiveSettingsApplier.SamePbx(Centralino(), null));
        Assert.True(LiveSettingsApplier.SamePbx(null, null));
    }

    [Fact]
    public void IlRegistroAccorciaLaConservazioneSubito()
    {
        // I registri contengono numeri chiamati e chiamanti: chi riduce la
        // conservazione per allinearsi alla policy dell'ente non deve aspettare.
        var cartella = Directory.CreateTempSubdirectory("blindphone-registro-").FullName;

        try
        {
            var vecchio = Path.Combine(cartella, "blindphone-20260101.log");
            File.WriteAllText(vecchio, "riga");
            File.SetLastWriteTime(vecchio, DateTime.Now.AddDays(-10));

            using var registro = new FileLoggerProvider(cartella, retentionDays: 30);
            Assert.True(File.Exists(vecchio));

            registro.RetentionDays = 7;

            Assert.False(File.Exists(vecchio));
            Assert.Equal(7, registro.RetentionDays);
        }
        finally
        {
            Directory.Delete(cartella, recursive: true);
        }
    }

    [Fact]
    public void IlPannelloNonChiedePiuDiRiavviare()
    {
        var codice = Sorgente("SettingsWindow.xaml.cs");

        Assert.DoesNotContain("PromptForRestart", codice, StringComparison.Ordinal);
        Assert.DoesNotContain("Riavviare BlindPhone?", codice, StringComparison.Ordinal);
    }

    private static string Sorgente(string nome)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(directory.FullName, "src", "BlindPhone.App", nome);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{nome} non trovato");
    }
}
