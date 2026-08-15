using System.Text;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifiche sulla persistenza della configurazione.
/// </summary>
/// <remarks>
/// Il caso che questi test proteggono è l'aggiornamento di una postazione già in
/// servizio: il file di configurazione che si trova sul disco è stato scritto da una
/// versione precedente e non contiene le chiavi nuove. Se una chiave mancante venisse
/// letta come <c>false</c> invece che con il proprio valore predefinito,
/// l'aggiornamento spegnerebbe funzioni che l'operatore non ha mai toccato — e nel
/// caso della voce degli annunci lo lascerebbe senza alcun riscontro sonoro.
/// </remarks>
public sealed class SettingsPersistenceTests
{
    [Fact]
    public void UnaConfigurazioneDiVersionePrecedenteMantieneIPredefinitiSicuri()
    {
        var cartella = CreaCartella();

        try
        {
            // Come appariva settings.json prima che esistessero l'area di notifica e
            // l'interruttore della voce propria.
            File.WriteAllText(
                Path.Combine(cartella, "settings.json"),
                """
                {
                  "schemaVersion": 1,
                  "accessibility": { "speechChannel": "Automatic", "verbosity": "Standard" },
                  "language": "it-IT"
                }
                """,
                Encoding.UTF8);

            var impostazioni = new SettingsService(cartella).Load();

            // La voce propria resta accesa: su una postazione senza screen reader e'
            // l'unico canale sonoro, e spegnerla con un aggiornamento renderebbe il
            // posto operatore muto senza che nessuno abbia chiesto nulla.
            Assert.True(impostazioni.Accessibility.InternalVoiceEnabled);

            Assert.True(impostazioni.Tray.Enabled);
            Assert.True(impostazioni.Tray.MinimizeToTrayOnClose);
            Assert.True(impostazioni.Tray.RestoreOnIncomingCall);

            // Le chiavi che c'erano non vengono perse.
            Assert.Equal("Automatic", impostazioni.Accessibility.SpeechChannel);
            Assert.Equal("it-IT", impostazioni.Language);
        }
        finally
        {
            Pulisci(cartella);
        }
    }

    [Fact]
    public void LeSceltePersonaliSopravvivonoAlRiavvio()
    {
        var cartella = CreaCartella();

        try
        {
            var servizio = new SettingsService(cartella);
            var iniziali = servizio.Load();

            Assert.True(servizio.Save(iniziali with
            {
                Accessibility = iniziali.Accessibility with { InternalVoiceEnabled = false },
                Tray = iniziali.Tray with { MinimizeToTrayOnClose = false, RestoreOnIncomingCall = false },
                DiagnosticLogLevel = "Debug",
            }));

            var riletto = new SettingsService(cartella).Load();

            Assert.False(riletto.Accessibility.InternalVoiceEnabled);
            Assert.True(riletto.Tray.Enabled);
            Assert.False(riletto.Tray.MinimizeToTrayOnClose);
            Assert.False(riletto.Tray.RestoreOnIncomingCall);
            Assert.Equal("Debug", riletto.DiagnosticLogLevel);
        }
        finally
        {
            Pulisci(cartella);
        }
    }

    private static string CreaCartella()
    {
        var cartella = Path.Combine(Path.GetTempPath(), "BlindPhone.Test", $"impostazioni-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cartella);
        return cartella;
    }

    private static void Pulisci(string cartella)
    {
        try
        {
            if (Directory.Exists(cartella)) Directory.Delete(cartella, recursive: true);
        }
        catch (IOException)
        {
            // Una cartella temporanea rimasta non e' un fallimento della prova.
        }
    }
}
