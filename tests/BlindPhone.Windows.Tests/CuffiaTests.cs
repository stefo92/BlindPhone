using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BlindPhone.Core.Domain;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la parte di sistema dei pulsanti della cuffia: la scheda delle
/// impostazioni, i valori predefiniti, il pacchetto di configurazione e l'elenco
/// dei dispositivi.
/// </summary>
/// <remarks>
/// <para>
/// La traduzione dei pulsanti in comandi e la distinzione delle pressioni vere sono
/// nel core, e le provano <c>PulsantiDellaCuffiaTests</c> e
/// <c>SegnaliDellaCuffiaTests</c>. Qui si prova quello che il core non vede: che la
/// funzione si accenda e si spenga dove l'operatore la cerca, che parta con i valori
/// giusti su una postazione già in servizio, e che l'elenco delle cuffie non faccia
/// cadere il programma su una macchina qualunque.
/// </para>
/// <para>
/// Nessuna prova scrive su un dispositivo: una prova che accendesse la spia dello
/// squillo farebbe suonare la cuffia di chi la sta lanciando.
/// </para>
/// </remarks>
public sealed class CuffiaTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    private static XElement Nominato(XDocument documento, string nome)
        => documento.Descendants().Single(e => (string?)e.Attribute(X + "Name") == nome);

    private static readonly string[] Controlli =
        ["HeadsetEnabled", "HeadsetDevice", "HeadsetRefreshButton", "HeadsetMediaFallback"];

    // -----------------------------------------------------------------------
    // La scheda delle impostazioni
    // -----------------------------------------------------------------------

    [Fact]
    public void IControlliStannoNellaSchedaCuffia()
    {
        var documento = Finestra();
        var scheda = Nominato(documento, "HeadsetTab");

        foreach (var nome in Controlli.Append("HeadsetStatus"))
        {
            Assert.Contains(Nominato(documento, nome), scheda.Descendants());
        }
    }

    [Fact]
    public void OgniControlloHaUnNomeAccessibileEUnaSpiegazione()
    {
        var documento = Finestra();

        foreach (var nome in Controlli)
        {
            var controllo = Nominato(documento, nome);

            Assert.False(string.IsNullOrWhiteSpace((string?)controllo.Attribute("AutomationProperties.Name")), nome);
            Assert.False(string.IsNullOrWhiteSpace((string?)controllo.Attribute("AutomationProperties.HelpText")), nome);
        }
    }

    [Fact]
    public void LaRigaDiStatoSiFaSentireQuandoArrivaUnPulsante()
    {
        // E' la prova della cuffia per chi non vede la spia: se la riga cambia in
        // silenzio, premere il pulsante con la scheda aperta non dice niente.
        var stato = Nominato(Finestra(), "HeadsetStatus");

        Assert.Equal("Polite", (string?)stato.Attribute("AutomationProperties.LiveSetting"));
        Assert.False(string.IsNullOrWhiteSpace((string?)stato.Attribute("AutomationProperties.Name")));
    }

    [Fact]
    public void LaSchedaSiBloccaInsiemeAllAudio()
    {
        // Una scheda senza sezione non si blocca mai: chi chiude l'audio di una
        // postazione lascerebbe aperta proprio la cuffia.
        var codice = File.ReadAllText(Percorso("SettingsWindow.xaml.cs"));

        Assert.Contains("SetSectionEnabled(HeadsetTab, SettingsSection.Audio)", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void CaricamentoESalvataggioToccanoTuttiIValori()
    {
        // Un controllo caricato e non salvato torna al valore di prima a ogni
        // apertura, senza errori: chi spegne la funzione la ritrova accesa.
        var codice = File.ReadAllText(Percorso("SettingsWindow.xaml.cs"));

        foreach (var (controllo, proprieta) in new[]
                 {
                     ("HeadsetEnabled", "Enabled"),
                     ("HeadsetMediaFallback", "MediaKeyFallback"),
                 })
        {
            Assert.Matches(new Regex($@"{controllo}\.IsChecked\s*=\s*settings\.{proprieta}"), codice);
            Assert.Matches(new Regex($@"{proprieta}\s*=\s*{controllo}\.IsChecked == true"), codice);
        }

        Assert.Matches(new Regex(@"DeviceKey\s*=\s*SelectedTag\(HeadsetDevice\)"), codice);
    }

    // -----------------------------------------------------------------------
    // Valori predefiniti e persistenza
    // -----------------------------------------------------------------------

    [Fact]
    public void LaFunzioneParteAccesaEIlTastoPlayEPausaSpento()
    {
        var predefinite = new AppSettings().Headset;

        Assert.True(predefinite.Enabled);
        Assert.False(predefinite.MediaKeyFallback);
        Assert.Null(predefinite.DeviceKey);
    }

    [Fact]
    public void UnaConfigurazioneScrittaPrimaDellaCuffiaPrendeIPredefiniti()
    {
        // Una postazione gia' in servizio si aggiorna con un settings.json che la
        // chiave non ce l'ha: il tasto della musica non deve risultare preso.
        var cartella = CreaCartella();

        try
        {
            File.WriteAllText(
                Path.Combine(cartella, "settings.json"),
                """{ "schemaVersion": 4, "language": "it-IT" }""",
                Encoding.UTF8);

            var cuffia = new SettingsService(cartella).Load().Headset;

            Assert.True(cuffia.Enabled);
            Assert.False(cuffia.MediaKeyFallback);
        }
        finally
        {
            Pulisci(cartella);
        }
    }

    [Fact]
    public void LaCuffiaSceltaSopravviveAlRiavvio()
    {
        var cartella = CreaCartella();

        try
        {
            var servizio = new SettingsService(cartella);
            var iniziali = servizio.Load();

            Assert.True(servizio.Save(iniziali with
            {
                Headset = new HeadsetSettings
                {
                    Enabled = false,
                    DeviceKey = "0B0E:245E",
                    DeviceName = "Cuffia di prova",
                    MediaKeyFallback = true,
                },
            }));

            var riletta = new SettingsService(cartella).Load().Headset;

            Assert.False(riletta.Enabled);
            Assert.Equal("0B0E:245E", riletta.DeviceKey);
            Assert.Equal("Cuffia di prova", riletta.DeviceName);
            Assert.True(riletta.MediaKeyFallback);
        }
        finally
        {
            Pulisci(cartella);
        }
    }

    [Fact]
    public void IlPacchettoDiRepartoPortaLaCuffiaInsiemeAllAudio()
    {
        // Marca e modello valgono su ogni postazione che ha la stessa cuffia, a
        // differenza degli identificativi delle schede audio: e' cosi' che un reparto
        // che ha comprato un modello solo lo sceglie una volta.
        var origine = SettingsService.CreateTemporary("cuffia-origine");
        var destinazione = SettingsService.CreateTemporary("cuffia-destinazione");

        try
        {
            origine.Load();
            destinazione.Load();

            origine.Save(origine.Current with
            {
                Headset = new HeadsetSettings { DeviceKey = "0B0E:245E", DeviceName = "Cuffia di reparto" },
            });

            var bundle = new ConfigurationBundleService(origine, new HotkeyProfileStore(origine.DataDirectory))
                .Compose([SettingsSection.Audio], includeHotkeys: false, policy: null, revision: 1, description: null);

            Assert.True(new ConfigurationBundleService(destinazione, new HotkeyProfileStore(destinazione.DataDirectory))
                .Apply(bundle).Success);

            Assert.Equal("0B0E:245E", destinazione.Current.Headset.DeviceKey);
            Assert.Equal("Cuffia di reparto", destinazione.Current.Headset.DeviceName);
        }
        finally
        {
            Pulisci(origine.DataDirectory);
            Pulisci(destinazione.DataDirectory);
        }
    }

    // -----------------------------------------------------------------------
    // Elenco dei dispositivi
    // -----------------------------------------------------------------------

    [Fact]
    public void LElencoDelleCuffieNonFallisceSuNessunaMacchina()
    {
        // Gira su qualunque macchina, con o senza cuffia: deve trovare solo dispositivi
        // con i pulsanti telefonici, e nominarli in modo che si possano dire.
        using var monitor = new HeadsetMonitor();

        foreach (var cuffia in monitor.Detect())
        {
            Assert.Matches("^[0-9A-F]{4}:[0-9A-F]{4}$", cuffia.Key);
            Assert.False(string.IsNullOrWhiteSpace(cuffia.Name));
            Assert.DoesNotContain(cuffia.Name, c => char.IsControl(c));
            Assert.True(
                cuffia.Capabilities.Buttons.Contains(HeadsetButton.HookSwitch)
                || cuffia.Capabilities.Buttons.Contains(HeadsetButton.PhoneMute));
        }
    }

    [Fact]
    public void ConLaFunzioneSpentaNessunaCuffiaVieneAperta()
    {
        using var monitor = new HeadsetMonitor();

        monitor.Configure(enabled: false, deviceKey: null);

        Assert.Empty(monitor.Active);
    }

    [Fact]
    public void ConUnaCuffiaSceltaCheNonCeNonSiApreNientAltro()
    {
        // Chi ha scelto una cuffia ha detto che le altre non devono comandare le
        // chiamate, anche se sono collegate.
        using var monitor = new HeadsetMonitor();

        monitor.Configure(enabled: true, deviceKey: "FFFF:0000");

        Assert.Empty(monitor.Active);
    }

    private static string CreaCartella()
    {
        var cartella = Path.Combine(Path.GetTempPath(), "BlindPhone.Test", $"cuffia-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cartella);
        return cartella;
    }

    private static void Pulisci(string cartella)
    {
        try
        {
            Directory.Delete(cartella, recursive: true);
        }
        catch (IOException)
        {
            // Un file ancora aperto dall'antivirus non e' un fallimento della prova.
        }
    }
}
