using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// La cuffia staccata durante l'uso: l'operatore deve sapere che l'audio è passato
/// agli altoparlanti prima di dire qualcosa di riservato a voce alta.
/// </summary>
/// <remarks>
/// Fino a novembre 2026 il motore audio segnalava il cambio e nessuno lo ascoltava: il
/// testo esisteva, l'annuncio no.
/// </remarks>
public sealed class CambioDispositivoAudioTests
{
    private static readonly DateTimeOffset Adesso = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void LaCuffiaStaccataSiAnnunciaConPrioritaCritica()
    {
        var motore = new MotoreAudioFinto();
        var voce = new RecordingAnnouncer();
        using var sorvegliante = new AudioDeviceMonitor(motore, voce, new ManualClock(Adesso));

        motore.Scollega(AudioRoute.Conversation, "Cuffia Jabra", "Altoparlanti");

        var annuncio = Assert.Single(voce.Announcements);
        Assert.Equal("Cuffia Jabra scollegato, audio su Altoparlanti", annuncio.Text);
        Assert.Equal(AnnouncementPriority.Critical, annuncio.Priority);
        Assert.True(annuncio.BypassConversationSilence);
    }

    [Fact]
    public void LeViePerseInsiemePortanoLaStessaChiave()
    {
        // L'anti-ripetizione accorpa solo gli annunci con la stessa chiave e lo stesso
        // testo: e' cosi' che una cuffia che serviva tre vie produce una frase sola.
        var motore = new MotoreAudioFinto();
        var voce = new RecordingAnnouncer();
        using var sorvegliante = new AudioDeviceMonitor(motore, voce, new ManualClock(Adesso));

        motore.Scollega(AudioRoute.Conversation, "Cuffia Jabra", "Altoparlanti");
        motore.Scollega(AudioRoute.Ringer, "Cuffia Jabra", "Altoparlanti");
        motore.Scollega(AudioRoute.Announcements, "Cuffia Jabra", "Altoparlanti");

        Assert.Equal(3, voce.Announcements.Count);
        Assert.Single(voce.Announcements.Select(a => (a.CoalesceKey, a.Text)).Distinct());
        Assert.NotNull(voce.Announcements[0].CoalesceKey);
    }

    [Fact]
    public void IlMicrofonoNonSiAnnunciaConUnaFraseSullUscita()
    {
        var motore = new MotoreAudioFinto();
        var voce = new RecordingAnnouncer();
        using var sorvegliante = new AudioDeviceMonitor(motore, voce, new ManualClock(Adesso));

        motore.Scollega(AudioRoute.Microphone, "Microfono USB", "Altoparlanti");

        Assert.Empty(voce.Announcements);
    }

    [Fact]
    public void DopoLoSmaltimentoNonAscoltaPiu()
    {
        var motore = new MotoreAudioFinto();
        var voce = new RecordingAnnouncer();
        var sorvegliante = new AudioDeviceMonitor(motore, voce, new ManualClock(Adesso));

        sorvegliante.Dispose();
        motore.Scollega(AudioRoute.Conversation, "Cuffia Jabra", "Altoparlanti");

        Assert.Empty(voce.Announcements);
        Assert.False(motore.HaAscoltatori);
    }

    /// <summary>Motore audio finto che sa soltanto sollevare il cambio di dispositivo.</summary>
    private sealed class MotoreAudioFinto : IAudioEngine
    {
        public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;

        public bool HaAscoltatori => DeviceChanged is not null;

        public void Scollega(AudioRoute via, string precedente, string nuovo)
            => DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(via, precedente, nuovo, WasUnplugged: true));

        public IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices() => [];

        public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() => [];

        public void AssignDevice(AudioRoute route, string? deviceId) { }

        public AudioDeviceInfo? GetAssignedDevice(AudioRoute route) => null;

        public void SetVolume(AudioRoute route, float volume) { }

        public void SetMicrophoneProcessing(MicrophoneProcessing processing) { }

        public Task PlayAsync(AudioRoute route, string filePath, float pan, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void DuckConversation(float attenuationDb, TimeSpan duration) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
