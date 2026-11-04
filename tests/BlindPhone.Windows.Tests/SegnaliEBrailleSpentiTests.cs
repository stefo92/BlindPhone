using BlindPhone.Accessibility;
using BlindPhone.Audio;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che le spunte dei segnali acustici e del braille cambino davvero ciò che
/// esce, all'avvio e a caldo.
/// </summary>
/// <remarks>
/// <para>
/// Le due spunte stavano nel pannello, si salvavano e si rileggevano, ma <b>non le
/// leggeva nessun servizio</b>: toglierle non spegneva né i segnali né il braille.
/// Dall'interfaccia il difetto non si vede, perché la spunta resta tolta anche
/// riaprendo il pannello; se ne accorge solo chi aspetta il silenzio e non lo ottiene.
/// </para>
/// <para>
/// Per questo ogni passaggio è una prova a sé: l'uscita che obbedisce alla spunta, e
/// i due punti — avvio e salvataggio — da cui la spunta le arriva.
/// </para>
/// </remarks>
public sealed class SegnaliEBrailleSpentiTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(1));

    private readonly List<IAsyncDisposable> _daChiudere = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var risorsa in _daChiudere) await risorsa.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Braille
    // -----------------------------------------------------------------------

    /// <summary>Screen reader finto, che annota quello che riceve sulla riga braille.</summary>
    private sealed class LettoreFinto : IScreenReaderBridge
    {
        public List<string> Riga { get; } = [];

        public string Name => "finto";

        public bool IsAvailable => true;

        public bool Speak(string text, bool interrupt) => true;

        public bool Braille(string text)
        {
            Riga.Add(text);
            return true;
        }

        public bool StopSpeech() => true;
    }

    private sealed class SegnaliMuti : IEarconPlayer
    {
        public void Play(EarconKind kind)
        {
        }

        public void StartRepeating(EarconKind kind, TimeSpan interval, TimeSpan? initialDelay = null)
        {
        }

        public void StopRepeating(EarconKind kind)
        {
        }
    }

    /// <summary>
    /// Porta un annuncio con la sua forma braille lungo la pipeline vera, fino al
    /// lettore finto.
    /// </summary>
    private static void Annuncia(CompositeSpeechOutput voce)
    {
        var annunci = new AnnouncementService(
            voce, new SegnaliMuti(), new AnnouncementContext(), clock: new ManualClock(Now));

        annunci.Announce(Announcement.Critical(
            "Registrazione perduta", Now, EarconKind.RegistrationLost, braille: "NON REG"));
    }

    private static CompositeSpeechOutput Voce(LettoreFinto lettore) => new([lettore])
    {
        // Solo notifiche UI Automation: la prova riguarda la riga braille, e cosi'
        // nessuna voce si mette a parlare mentre i test girano.
        Mode = SpeechChannelMode.UiaOnly,
    };

    [Fact]
    public void ConLaSpuntaLaRigaBrailleSiAggiorna()
    {
        var lettore = new LettoreFinto();
        using var voce = Voce(lettore);

        Annuncia(voce);

        Assert.Equal(["NON REG"], lettore.Riga);
    }

    [Fact]
    public void SenzaLaSpuntaLaRigaBrailleNonSiTocca()
    {
        var lettore = new LettoreFinto();
        using var voce = Voce(lettore);
        voce.BrailleEnabled = false;

        Annuncia(voce);

        Assert.Empty(lettore.Riga);
    }

    [Fact]
    public void RimettereLaSpuntaValeDalPrimoAnnuncioSuccessivo()
    {
        // A caldo la spunta cambia sulla stessa istanza che la pipeline sta usando:
        // non ce n'e' un'altra da ricostruire.
        var lettore = new LettoreFinto();
        using var voce = Voce(lettore);

        voce.BrailleEnabled = false;
        voce.Braille("C:3");

        voce.BrailleEnabled = true;
        voce.Braille("C:4");

        Assert.Equal(["C:4"], lettore.Riga);
    }

    // -----------------------------------------------------------------------
    // Segnali acustici
    // -----------------------------------------------------------------------

    private ToneEarconPlayer Riproduttore()
    {
        var motore = new NAudioEngine();
        var segnali = new ToneEarconPlayer(motore);

        _daChiudere.Add(segnali);
        _daChiudere.Add(motore);
        return segnali;
    }

    [Fact]
    public void SenzaLaSpuntaTaccionoISegnaliDiCommento()
    {
        var segnali = Riproduttore();
        segnali.Enabled = false;

        Assert.False(segnali.WillPlay(EarconKind.QueueCallArrived));
        Assert.False(segnali.WillPlay(EarconKind.CallAnswered));
        Assert.False(segnali.WillPlay(EarconKind.TransferSucceeded));
    }

    [Fact]
    public void SenzaLaSpuntaLaSuoneriaSuonaAncora()
    {
        var segnali = Riproduttore();
        segnali.Enabled = false;

        Assert.True(segnali.WillPlay(EarconKind.IncomingCall));
        Assert.True(segnali.WillPlay(EarconKind.RemoteRinging));
        Assert.True(segnali.WillPlay(EarconKind.RegistrationLost));
    }

    [Fact]
    public void RimettereLaSpuntaRiaccendeISegnaliSubito()
    {
        var segnali = Riproduttore();

        segnali.Enabled = false;
        Assert.False(segnali.WillPlay(EarconKind.QueueCallArrived));

        segnali.Enabled = true;
        Assert.True(segnali.WillPlay(EarconKind.QueueCallArrived));
    }

    // -----------------------------------------------------------------------
    // Da dove arriva la spunta
    // -----------------------------------------------------------------------

    [Fact]
    public void LeSpunteArrivanoAiServiziAllAvvio()
    {
        var codice = Sorgente("App.xaml.cs");

        Assert.Contains("BrailleEnabled = settings.Accessibility.BrailleEnabled", codice, StringComparison.Ordinal);
        Assert.Contains("Enabled = settings.Accessibility.EarconsEnabled", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void LeSpunteArrivanoAiServiziAlSalvataggio()
    {
        // Senza, la spunta tolta valeva solo dal riavvio successivo: proprio quello
        // che il salvataggio a caldo esiste per evitare.
        var codice = Sorgente("LiveSettingsApplier.cs");

        Assert.Contains("voce.BrailleEnabled = accessibilita.BrailleEnabled", codice, StringComparison.Ordinal);
        Assert.Contains("segnali.Enabled = accessibilita.EarconsEnabled", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void LaProvaDeiDispositiviSuonaAncheConISegnaliSpenti()
    {
        // La prova usava i segnali di commento, che a spunta tolta tacciono: una prova
        // muta si scambia per un dispositivo guasto.
        Assert.Contains(".PlayPreview(", Sorgente("SettingsWindow.xaml.cs"), StringComparison.Ordinal);
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
