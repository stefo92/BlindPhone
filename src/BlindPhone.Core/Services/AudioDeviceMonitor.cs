using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Dice all'operatore quando l'audio passa da un dispositivo a un altro perché il primo
/// è sparito.
/// </summary>
/// <remarks>
/// <para>
/// <b>È una protezione della riservatezza, prima che una comodità.</b> Se la cuffia si
/// scollega durante una conversazione, il motore audio sposta la voce sugli
/// altoparlanti: la chiamata continua, e chi sta dall'altra parte si sente in tutto
/// l'ufficio. Chi vede lo schermo se ne accorge; chi non lo vede no, finché non ha già
/// detto qualcosa di riservato a voce alta. Per questo l'annuncio è critico: interrompe
/// la voce in corso e parla anche durante la conversazione.
/// </para>
/// <para>
/// <b>Un dispositivo, una frase.</b> Una cuffia serve di solito più vie insieme —
/// conversazione, suoneria, annunci — e quando sparisce il motore segnala il cambio una
/// volta per ciascuna. Le frasi sono identiche e portano la stessa chiave, quindi
/// l'anti-ripetizione di <see cref="AnnouncementService"/> ne lascia passare una sola.
/// </para>
/// <para>
/// <b>Il microfono non si annuncia qui.</b> Il testo dice su quale uscita è passato
/// l'audio, e per la via del microfono il motore non ha un'uscita da nominare: la frase
/// sarebbe falsa. Quando sparisce una cuffia, la sua parte di ascolto basta a far
/// parlare la postazione; il caso di un microfono separato scollegato da solo resta
/// scritto nel registro.
/// </para>
/// </remarks>
public sealed class AudioDeviceMonitor : IDisposable
{
    private readonly IAudioEngine _audio;
    private readonly IAnnouncer _announcer;
    private readonly IClock _clock;
    private readonly ILogger<AudioDeviceMonitor> _log;
    private bool _disposed;

    /// <summary>Crea il sorvegliante e lo collega al motore audio.</summary>
    /// <param name="audio">Motore audio di cui seguire i cambi di dispositivo.</param>
    /// <param name="announcer">Voce del posto operatore.</param>
    /// <param name="clock">Orologio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public AudioDeviceMonitor(
        IAudioEngine audio,
        IAnnouncer announcer,
        IClock? clock = null,
        ILogger<AudioDeviceMonitor>? logger = null)
    {
        _audio = audio;
        _announcer = announcer;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<AudioDeviceMonitor>.Instance;

        _audio.DeviceChanged += OnDeviceChanged;
    }

    private void OnDeviceChanged(object? sender, AudioDeviceChangedEventArgs e)
    {
        _log.LogWarning(
            "Via audio {Route}: {Precedente} non disponibile, si passa a {Nuovo}",
            e.Route, e.PreviousDeviceName ?? "dispositivo sconosciuto", e.NewDeviceName);

        if (e.Route == AudioRoute.Microphone) return;

        _announcer.Announce(Announcement.Critical(
            AnnouncementTexts.AudioDeviceChanged(e),
            _clock.Now,
            coalesceKey: "audio:dispositivo"));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audio.DeviceChanged -= OnDeviceChanged;
    }
}
