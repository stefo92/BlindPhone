using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Xml.Linq;
using BlindPhone.Accessibility;
using BlindPhone.Audio;
using BlindPhone.Core.Abstractions;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica i volumi delle uscite audio: dal cursore del pannello, al file, al motore
/// che suona.
/// </summary>
/// <remarks>
/// <para>
/// Il volume per via esisteva già nel file di configurazione, ma non lo si poteva
/// cambiare dal pannello e, peggio, quasi nessuno lo applicava: la conversazione e la
/// suoneria sintetizzata suonavano sempre piene. È lo stesso difetto già capitato ai
/// dispositivi — salvato, mostrato, ignorato — e per questo ogni passaggio ha la sua
/// prova.
/// </para>
/// <para>
/// I cursori si provano anche dalla tastiera, premendo i tasti: è il solo modo in cui
/// un operatore che usa lo screen reader li regola, e quello che lo screen reader
/// legge — minimo, massimo, passo — si prova sull'automazione, che è da dove lo legge.
/// </para>
/// </remarks>
public sealed class VolumiPerViaTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>Le tre uscite, con il loro dispositivo e il loro cursore.</summary>
    public static TheoryData<string, string> Uscite => new()
    {
        { "ConversationDevice", "ConversationVolume" },
        { "RingerDevice", "RingerVolume" },
        { "AnnouncementDevice", "AnnouncementVolume" },
    };

    /// <summary>I tre cursori.</summary>
    public static TheoryData<string> Cursori => new() { "ConversationVolume", "RingerVolume", "AnnouncementVolume" };

    private readonly SettingsService _impostazioni = SettingsService.CreateTemporary("volumi-per-via");
    private readonly List<NAudioEngine> _motori = [];

    public void Dispose()
    {
        foreach (var motore in _motori) motore.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private NAudioEngine NuovoMotore()
    {
        var motore = new NAudioEngine();
        _motori.Add(motore);
        return motore;
    }

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

    private static XElement PerNome(XDocument documento, string nome)
        => documento.Descendants().Single(e => (string?)e.Attribute(X + "Name") == nome);

    /// <summary>Esegue un'azione in un thread STA, come vogliono i controlli WPF.</summary>
    private static void InSta(Action azione)
    {
        Exception? errore = null;

        var thread = new Thread(() =>
        {
            try { azione(); }
            catch (Exception ex) { errore = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (errore is not null) throw new InvalidOperationException("Errore nel thread STA", errore);
    }

    /// <summary>
    /// Un cursore con le stesse impostazioni dello stile <c>VolumeSlider</c>, lette dal
    /// dizionario: se lo stile cambia, cambia anche il cursore provato qui.
    /// </summary>
    private static Slider CursoreDelVolume()
    {
        var stili = XDocument.Load(Percorso(Path.Combine("Temi", "Stili.xaml")));
        var stile = stili.Descendants(P + "Style").Single(s => (string?)s.Attribute(X + "Key") == "VolumeSlider");

        var cursore = new Slider();

        foreach (var setter in stile.Elements(P + "Setter"))
        {
            var proprieta = DependencyPropertyDescriptor
                .FromName((string)setter.Attribute("Property")!, typeof(Slider), typeof(Slider))
                .DependencyProperty;

            var valore = TypeDescriptor.GetConverter(proprieta.PropertyType)
                .ConvertFromInvariantString((string)setter.Attribute("Value")!);

            cursore.SetValue(proprieta, valore);
        }

        return cursore;
    }

    /// <summary>Preme un tasto sul cursore, passando per lo stesso percorso di una pressione vera.</summary>
    private static void Premi(Slider cursore, PresentationSource sorgente, Key tasto)
    {
        cursore.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, sorgente, 0, tasto)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });
    }

    // -----------------------------------------------------------------------
    // Percentuale e frazione
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(1f, 100)]
    [InlineData(0.8f, 80)]
    [InlineData(0.6f, 60)]
    [InlineData(0.1f, 10)]
    [InlineData(0f, 0)]
    [InlineData(1.5f, 100)]
    [InlineData(-0.2f, 0)]
    public void IlVolumeDelFileDiventaUnaPercentualeIntera(float volume, int percentuale)
        => Assert.Equal(percentuale, AudioRouteSettings.ToPercent(volume));

    [Fact]
    public void UnVolumeIllegibileSiMostraPienoENonMuto()
        => Assert.Equal(100, AudioRouteSettings.ToPercent(float.NaN));

    [Fact]
    public void OgniPercentualeTornaUgualeDopoIlGiroNelFile()
    {
        // Aprire il pannello e salvare senza toccare il cursore non deve spostare il
        // volume di un punto: lo si sentirebbe cambiare a ogni salvataggio.
        for (var percentuale = 0; percentuale <= 100; percentuale++)
        {
            Assert.Equal(percentuale, AudioRouteSettings.ToPercent(AudioRouteSettings.FromPercent(percentuale)));
        }
    }

    [Fact]
    public void LaSuoneriaPredefinitaAllOttantaPerCentoResta0Virgola8()
        => Assert.Equal(0.8f, AudioRouteSettings.FromPercent(AudioRouteSettings.ToPercent(new AppSettings().Ringer.Volume)));

    // -----------------------------------------------------------------------
    // File
    // -----------------------------------------------------------------------

    [Fact]
    public void IVolumiSiSalvanoESiRileggonoDalFile()
    {
        _impostazioni.Load();

        Assert.True(_impostazioni.Save(_impostazioni.Current with
        {
            Conversation = _impostazioni.Current.Conversation with { Volume = 0.65f },
            Ringer = _impostazioni.Current.Ringer with { Volume = 0.35f },
            Announcements = _impostazioni.Current.Announcements with { Volume = 0.5f },
        }));

        // Un servizio nuovo sulla stessa cartella: legge dal file, non dalla memoria.
        var riletto = new SettingsService(_impostazioni.DataDirectory).Load();

        Assert.Equal(0.65f, riletto.Conversation.Volume);
        Assert.Equal(0.35f, riletto.Ringer.Volume);
        Assert.Equal(0.5f, riletto.Announcements.Volume);
    }

    // -----------------------------------------------------------------------
    // Motore e voce
    // -----------------------------------------------------------------------

    [Fact]
    public void IlMotoreRestituisceIlVolumeCheLaConversazioneLegge()
    {
        var motore = NuovoMotore();

        AudioSettingsApplier.Apply(motore, new AppSettings
        {
            Conversation = new AudioRouteSettings(null, null, 0.45f),
            Ringer = new AudioRouteSettings(null, null, 0.3f),
        });

        // E' il valore che la chiamata rilegge a ogni pacchetto ricevuto.
        Assert.Equal(0.45f, motore.GetVolume(AudioRoute.Conversation));
        Assert.Equal(0.3f, motore.GetVolume(AudioRoute.Ringer));
    }

    [Fact]
    public void IlMotoreLimitaIVolumiFuoriIntervallo()
    {
        var motore = NuovoMotore();

        motore.SetVolume(AudioRoute.Conversation, 3f);
        motore.SetVolume(AudioRoute.Ringer, -1f);

        Assert.Equal(1f, motore.GetVolume(AudioRoute.Conversation));
        Assert.Equal(0f, motore.GetVolume(AudioRoute.Ringer));
    }

    [Fact]
    public void LaVoceInternaPartePienaELimitaIValoriFuoriIntervallo()
    {
        using var voce = new CompositeSpeechOutput([]);

        Assert.Equal(100, voce.InternalVoiceVolume);

        voce.InternalVoiceVolume = 140;
        Assert.Equal(100, voce.InternalVoiceVolume);

        voce.InternalVoiceVolume = -5;
        Assert.Equal(0, voce.InternalVoiceVolume);

        voce.InternalVoiceVolume = 55;
        Assert.Equal(55, voce.InternalVoiceVolume);
    }

    [Fact]
    public void IlVolumeDegliAnnunciArrivaAllaVoceInternaAllAvvioEACaldo()
    {
        // La voce degli annunci esce dalla sintesi di sistema e non dal motore audio:
        // il volume della via, da solo, non la raggiungeva. Va passato alla voce in
        // entrambi i punti in cui la si configura.
        var avvio = File.ReadAllText(Percorso("App.xaml.cs"));
        var caldo = File.ReadAllText(Percorso("LiveSettingsApplier.cs"));

        Assert.Contains("InternalVoiceVolume = AudioRouteSettings.ToPercent(settings.Announcements.Volume)", avvio, StringComparison.Ordinal);
        Assert.Contains("InternalVoiceVolume = AudioRouteSettings.ToPercent(next.Announcements.Volume)", caldo, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Pannello
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Uscite))]
    public void OgniUscitaHaIlSuoCursoreSubitoDopoIlDispositivo(string dispositivo, string volume)
    {
        var documento = Finestra();
        var scheda = PerNome(documento, "AudioTab");

        var cursore = PerNome(documento, volume);
        Assert.Equal(P + "Slider", cursore.Name);
        Assert.Contains(scheda, cursore.Ancestors());
        Assert.Equal("{StaticResource VolumeSlider}", (string?)cursore.Attribute("Style"));

        // Nell'ordine del documento, che e' l'ordine del Tab: dal dispositivo si passa
        // al suo volume, senza altri campi in mezzo.
        var focalizzabili = scheda.Descendants()
            .Where(e => e.Name.LocalName is "ComboBox" or "Slider" or "CheckBox" or "TextBox" or "Button")
            .Select(e => (string?)e.Attribute(X + "Name"))
            .ToList();

        Assert.Equal(focalizzabili.IndexOf(dispositivo) + 1, focalizzabili.IndexOf(volume));
    }

    [Theory]
    [MemberData(nameof(Cursori))]
    public void IlCursoreHaNomeEtichettaEAiutoSuiTasti(string volume)
    {
        var documento = Finestra();
        var cursore = PerNome(documento, volume);

        var nome = (string?)cursore.Attribute(XName.Get("AutomationProperties.Name"));
        var aiuto = (string?)cursore.Attribute(XName.Get("AutomationProperties.HelpText"));

        Assert.False(string.IsNullOrWhiteSpace(nome));
        Assert.StartsWith("Volume", nome, StringComparison.Ordinal);

        // Lo screen reader dice «dispositivo di scorrimento», non quali tasti usare
        // ne' di quanto si sposta: lo dice l'aiuto.
        Assert.NotNull(aiuto);
        Assert.Contains("freccia", aiuto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pagina su", aiuto, StringComparison.Ordinal);
        Assert.Contains("Inizio e Fine", aiuto, StringComparison.Ordinal);

        // L'etichetta a schermo e' collegata: e' quella che la ricerca per nome trova.
        Assert.Contains(documento.Descendants(P + "Label"),
            l => (string?)l.Attribute("Target") == $"{{Binding ElementName={volume}}}");
    }

    [Theory]
    [MemberData(nameof(Cursori))]
    public void IlPannelloCaricaESalvaIlVolumeDiOgniCursore(string volume)
    {
        var codice = File.ReadAllText(Percorso("SettingsWindow.xaml.cs"));

        // Mostrato e non salvato, o salvato e non mostrato, e' il difetto che questa
        // impostazione ha gia' avuto.
        Assert.Contains($"{volume}.Value = AudioRouteSettings.ToPercent(", codice, StringComparison.Ordinal);
        Assert.Contains($"Volume = AudioRouteSettings.FromPercent({volume}.Value)", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void LeFrecceSpostanoIlVolumeDiCinqueEPaginaSuEGiuDiVenti()
    {
        var valori = new List<double>();

        InSta(() =>
        {
            var cursore = CursoreDelVolume();
            cursore.Value = 80;

            using var sorgente = new HwndSource(new HwndSourceParameters("Prova del cursore") { Width = 400, Height = 80 });
            sorgente.RootVisual = cursore;

            foreach (var tasto in new[] { Key.Right, Key.Up, Key.Left, Key.Down, Key.PageUp, Key.PageDown, Key.Home, Key.End })
            {
                Premi(cursore, sorgente, tasto);
                valori.Add(cursore.Value);
            }
        });

        Assert.Equal(new[] { 85d, 90d, 85d, 80d, 100d, 80d, 10d, 100d }, valori);
    }

    [Fact]
    public void IlCursoreNonScendeSottoIlDieciNeSaleOltreIlCento()
    {
        double minimo = -1, massimo = -1;

        InSta(() =>
        {
            var cursore = CursoreDelVolume();

            cursore.Value = 0;
            minimo = cursore.Value;

            cursore.Value = 250;
            massimo = cursore.Value;
        });

        // Una suoneria muta fa perdere le chiamate.
        Assert.Equal(10, minimo);
        Assert.Equal(100, massimo);
    }

    [Theory]
    [InlineData("Chiaro")]
    [InlineData("Scuro")]
    [InlineData("Sistema")]
    public void IlModelloDelCursoreSiCaricaEDisegnaBinarioEPomelloInOgniTema(string tema)
    {
        // Un modello sbagliato non si vede alla compilazione: si vede all'avvio, o
        // peggio come un cursore senza binario solo in uno dei tre temi. Qui si
        // caricano i dizionari veri e si applica lo stile a un cursore vero.
        string? binario = null, pomello = null;
        var larghezzaPercorso = -1d;

        InSta(() =>
        {
            static ResourceDictionary Carica(string file)
            {
                // Fuori dall'applicazione lo spazio dei nomi locale va qualificato con
                // l'assembly, che a programma compilato e' implicito.
                var testo = File.ReadAllText(Percorso(file)).Replace(
                    "clr-namespace:BlindPhone.App\"",
                    "clr-namespace:BlindPhone.App;assembly=BlindPhone.App\"",
                    StringComparison.Ordinal);

                return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(testo);
            }

            var risorse = new ResourceDictionary();
            risorse.MergedDictionaries.Add(Carica(Path.Combine("Temi", $"{tema}.xaml")));
            risorse.MergedDictionaries.Add(Carica(Path.Combine("Temi", "Stili.xaml")));

            var cursore = new Slider { Width = 300, Style = (Style)risorse["VolumeSlider"], Value = 50 };
            var radice = new Border { Resources = risorse, Child = cursore };

            using var sorgente = new HwndSource(new HwndSourceParameters("Prova del modello") { Width = 400, Height = 80 });
            sorgente.RootVisual = radice;
            radice.Measure(new Size(400, 80));
            radice.Arrange(new Rect(0, 0, 400, 80));
            radice.UpdateLayout();

            var track = cursore.Template.FindName("PART_Track", cursore) as System.Windows.Controls.Primitives.Track;
            binario = track?.GetType().Name;
            pomello = track?.Thumb?.GetType().Name;
            larghezzaPercorso = track?.DecreaseRepeatButton?.ActualWidth ?? -1;
        });

        Assert.Equal("Track", binario);
        Assert.Equal("Thumb", pomello);

        // A meta' volume il tratto gia' percorso occupa una parte visibile del binario:
        // e' la lunghezza, non solo il colore, a dire a che livello si e'.
        Assert.True(larghezzaPercorso > 50, $"Tratto percorso largo {larghezzaPercorso}");
    }

    [Fact]
    public void LoScreenReaderLeggeUnIntervalloDaDieciACentoConPassoDiCinque()
    {
        AutomationControlType? tipo = null;
        string? nome = null;
        (double Valore, double Minimo, double Massimo, double Piccolo, double Grande, bool SolaLettura) intervallo = default;

        InSta(() =>
        {
            var cursore = CursoreDelVolume();
            cursore.Value = 80;
            AutomationProperties.SetName(cursore, "Volume della conversazione");

            var peer = UIElementAutomationPeer.CreatePeerForElement(cursore);
            tipo = peer.GetAutomationControlType();
            nome = peer.GetName();

            var range = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue);
            intervallo = (range.Value, range.Minimum, range.Maximum, range.SmallChange, range.LargeChange, range.IsReadOnly);
        });

        Assert.Equal(AutomationControlType.Slider, tipo);
        Assert.Equal("Volume della conversazione", nome);
        Assert.Equal((80d, 10d, 100d, 5d, 20d, false), intervallo);
    }
}
