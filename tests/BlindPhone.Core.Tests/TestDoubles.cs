using BlindPhone.Core.Accessibility;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Raccoglitore di annunci per i test: registra tutto ciò che verrebbe pronunciato
/// all'operatore.
/// </summary>
/// <remarks>
/// È lo strumento che rende verificabile l'accessibilità invece di lasciarla a
/// un'ispezione manuale. Un test può affermare "questa sequenza di eventi produce
/// questi annunci" con la stessa precisione con cui afferma un valore di ritorno.
/// </remarks>
internal sealed class RecordingAnnouncer : IAnnouncer
{
    private readonly List<Announcement> _announcements = [];
    private readonly List<EarconKind> _earcons = [];

    public IReadOnlyList<Announcement> Announcements => _announcements;

    public IReadOnlyList<EarconKind> Earcons => _earcons;

    public IReadOnlyList<Announcement> History => _announcements;

    public void Announce(Announcement announcement)
    {
        _announcements.Add(announcement);
        if (announcement.Earcon != EarconKind.None) _earcons.Add(announcement.Earcon);
    }

    public void PlayEarcon(EarconKind earcon) => _earcons.Add(earcon);

    public void Silence() => _announcements.Clear();

    public void RepeatLast() { }

    /// <summary>Testo di tutti gli annunci raccolti, per le asserzioni di contenuto.</summary>
    public IEnumerable<string> Texts => _announcements.Select(a => a.Text);

    /// <summary>Verifica che almeno un annuncio contenga il frammento indicato.</summary>
    public bool Said(string fragment)
        => _announcements.Any(a => a.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        _announcements.Clear();
        _earcons.Clear();
    }
}

/// <summary>
/// Riproduttore di segnali acustici che registra invece di suonare.
/// </summary>
/// <remarks>
/// Serve a rendere verificabile il tono di libero: è audio e non testo, quindi non
/// passa dal raccoglitore degli annunci e sfuggirebbe a qualunque asserzione.
/// </remarks>
internal sealed class RecordingEarconPlayer : IEarconPlayer
{
    public List<EarconKind> Played { get; } = [];

    public List<EarconKind> Started { get; } = [];

    public List<EarconKind> Stopped { get; } = [];

    /// <summary>Earcon attualmente in ripetizione.</summary>
    public HashSet<EarconKind> Repeating { get; } = [];

    public void Play(EarconKind kind) => Played.Add(kind);

    public void StartRepeating(EarconKind kind, TimeSpan interval, TimeSpan? initialDelay = null)
    {
        Started.Add(kind);
        Repeating.Add(kind);
    }

    public void StopRepeating(EarconKind kind)
    {
        Stopped.Add(kind);
        Repeating.Remove(kind);
    }
}

/// <summary>Destinazione della voce che registra invece di parlare.</summary>
internal sealed class RecordingSpeechOutput : ISpeechOutput
{
    public List<(string Text, bool Interrupt)> Spoken { get; } = [];

    public List<string> BrailleMessages { get; } = [];

    public int StopCount { get; private set; }

    public void Speak(string text, bool interrupt) => Spoken.Add((text, interrupt));

    public void Stop() => StopCount++;

    public void Braille(string text) => BrailleMessages.Add(text);
}

/// <summary>Riproduttore di earcon che registra invece di suonare.</summary>
