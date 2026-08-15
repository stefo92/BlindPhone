using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che durante la beta il registro parta su «tutto».
/// </summary>
/// <remarks>
/// <para>
/// Il punto che questi test proteggono non è il valore predefinito — quello si legge
/// dal codice — ma il caso che il valore predefinito <b>non</b> copre: una postazione
/// già in uso ha <c>settings.json</c> scritto, quindi il predefinito non la tocca. È
/// esattamente la postazione su cui la beta sta girando e da cui devono arrivare le
/// segnalazioni.
/// </para>
/// <para>
/// L'altra metà è che la scelta dell'operatore, una volta salvata, resti: una
/// migrazione che riportasse il dettaglio a «tutto» a ogni avvio sarebbe una
/// preferenza che torna da sola, e chi installa la beta in un ente con una policy di
/// conservazione stretta deve poter decidere diversamente.
/// </para>
/// </remarks>
public sealed class BetaLoggingTests
{
    [Fact]
    public void UnaConfigurazioneNuovaNasceConIlRegistroSuTutto()
    {
        var nuova = new AppSettings();

        Assert.Equal(nameof(LogLevel.Trace), nuova.DiagnosticLogLevel);
        Assert.True(nuova.DebugModeEnabled);
    }

    [Fact]
    public void UnaPostazioneGiaInUsoVienePortataSuTutto()
    {
        // E' il caso che il solo valore predefinito non copre.
        var esistente = new AppSettings
        {
            SchemaVersion = 2,
            DiagnosticLogLevel = nameof(LogLevel.Information),
            DebugModeEnabled = false,
            SipAccounts = [new SipSettings { Id = "a", Username = "200", Domain = "pbx" }],
            DefaultSipAccountId = "a",
        };

        var migrata = esistente.Migrate();

        Assert.Equal(nameof(LogLevel.Trace), migrata.DiagnosticLogLevel);
        Assert.True(migrata.DebugModeEnabled);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrata.SchemaVersion);
    }

    [Fact]
    public void IlLivelloSceltoDallOperatoreRestaDopoIlSalvataggio()
    {
        // Dopo il primo salvataggio la versione di schema e' quella corrente: chi
        // abbassa il dettaglio se lo tiene abbassato.
        var giaMigrata = new AppSettings
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            DiagnosticLogLevel = nameof(LogLevel.Warning),
            DebugModeEnabled = false,
            SipAccounts = [new SipSettings { Id = "a", Username = "200", Domain = "pbx" }],
            DefaultSipAccountId = "a",
        };

        var migrata = giaMigrata.Migrate();

        Assert.Equal(nameof(LogLevel.Warning), migrata.DiagnosticLogLevel);
        Assert.False(migrata.DebugModeEnabled);
    }

    [Fact]
    public void IlLivelloImpostatoDallaMigrazioneEInterpretabile()
    {
        // Un refuso nel nome produrrebbe un livello che Parse non riconosce, e davanti
        // a un nome irriconoscibile Parse ricade sul livello normale: la migrazione
        // fallirebbe in silenzio, ottenendo il contrario di quello che vuole.
        var migrata = new AppSettings { SchemaVersion = 1 }.Migrate();

        Assert.Equal(LogLevel.Trace, LogLevelSwitch.Parse(migrata.DiagnosticLogLevel));
    }

    [Fact]
    public void LaMigrazioneDelRegistroEIdempotente()
    {
        var una = new AppSettings { SchemaVersion = 1 }.Migrate();
        var due = una.Migrate();

        Assert.Equal(una.DiagnosticLogLevel, due.DiagnosticLogLevel);
        Assert.Equal(una.SchemaVersion, due.SchemaVersion);
    }

    [Fact]
    public void SuDiscoLaMigrazioneSopravviveAlGiroCompleto()
    {
        var servizio = SettingsService.CreateTemporary(nameof(SuDiscoLaMigrazioneSopravviveAlGiroCompleto));

        try
        {
            File.WriteAllText(
                servizio.SettingsPath,
                """
                {
                  "schemaVersion": 2,
                  "diagnosticLogLevel": "Information",
                  "debugModeEnabled": false,
                  "sip": { "username": "200", "domain": "pbx.ente.it" }
                }
                """);

            var caricata = servizio.Load();
            Assert.Equal(nameof(LogLevel.Trace), caricata.DiagnosticLogLevel);

            // Salvando, la versione di schema finisce su disco: da li' in avanti la
            // scelta dell'operatore comanda.
            servizio.Save(caricata with { DiagnosticLogLevel = nameof(LogLevel.Warning) });

            Assert.Equal(nameof(LogLevel.Warning), servizio.Load().DiagnosticLogLevel);
        }
        finally
        {
            try
            {
                if (Directory.Exists(servizio.DataDirectory))
                {
                    Directory.Delete(servizio.DataDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Cartella temporanea trattenuta: non e' il soggetto della prova.
            }
        }
    }

    [Fact]
    public void IlLivelloTuttoSiChiamaTuttoAncheAVoce()
    {
        // E' il testo che l'operatore sente nel pannello di diagnostica: se dicesse
        // "Trace" non significherebbe niente per chi lo ascolta.
        Assert.Contains("Tutto", LogLevelSwitch.Describe(LogLevel.Trace), StringComparison.Ordinal);
    }
}
