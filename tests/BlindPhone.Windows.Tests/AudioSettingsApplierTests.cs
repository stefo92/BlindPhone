using BlindPhone.Core.Abstractions;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifiche sull'applicazione della configurazione audio.
/// </summary>
/// <remarks>
/// <para>
/// Il difetto che questi test impediscono è già accaduto: le quattro vie audio erano
/// configurabili, salvate e mostrate correttamente, ma nessuno le assegnava mai al
/// motore. Il risultato era che ogni via ricadeva sul dispositivo di comunicazione
/// predefinito di Windows, e suoneria, annunci e conversazione uscivano tutti dallo
/// stesso posto — mentre il pannello mostrava la scelta giusta.
/// </para>
/// <para>
/// È il tipo di difetto che nessuna compilazione può notare e che chi sviluppa con una
/// cuffia sola in testa non sente affatto.
/// </para>
/// </remarks>
public sealed class AudioSettingsApplierTests
{
    [Fact]
    public void OgniViaRiceveIlDispositivoConfigurato()
    {
        var motore = new MotoreFinto();

        var impostazioni = new AppSettings
        {
            Microphone = new AudioRouteSettings("mic-1", "Microfono cuffia"),
            Conversation = new AudioRouteSettings("cuffia-1", "Cuffia"),
            Ringer = new AudioRouteSettings("casse-1", "Altoparlanti"),
            Announcements = new AudioRouteSettings("casse-1", "Altoparlanti"),
        };

        AudioSettingsApplier.Apply(motore, impostazioni);

        Assert.Equal("mic-1", motore.Dispositivi[AudioRoute.Microphone]);
        Assert.Equal("cuffia-1", motore.Dispositivi[AudioRoute.Conversation]);
        Assert.Equal("casse-1", motore.Dispositivi[AudioRoute.Ringer]);
        Assert.Equal("casse-1", motore.Dispositivi[AudioRoute.Announcements]);
    }

    [Fact]
    public void LaSuoneriaEGliAltoparlantiRestanoViaSeparate()
    {
        var motore = new MotoreFinto();

        // La configurazione che l'operatore si aspetta: conversazione in cuffia,
        // suoneria sugli altoparlanti. Se le due vie finissero sullo stesso
        // dispositivo, la suoneria suonerebbe nell'orecchio impegnato.
        AudioSettingsApplier.Apply(motore, new AppSettings
        {
            Conversation = new AudioRouteSettings("cuffia-1", "Cuffia"),
            Ringer = new AudioRouteSettings("casse-1", "Altoparlanti"),
        });

        Assert.NotEqual(
            motore.Dispositivi[AudioRoute.Conversation],
            motore.Dispositivi[AudioRoute.Ringer]);
    }

    [Fact]
    public void IVolumiPerViaVengonoApplicati()
    {
        var motore = new MotoreFinto();

        // Anche i volumi erano configurabili e non applicati: SetVolume non veniva
        // invocato da nessuno.
        AudioSettingsApplier.Apply(motore, new AppSettings
        {
            Ringer = new AudioRouteSettings("casse-1", "Altoparlanti", 0.8f),
            Conversation = new AudioRouteSettings(null, null, 1f),
        });

        Assert.Equal(0.8f, motore.Volumi[AudioRoute.Ringer]);
        Assert.Equal(1f, motore.Volumi[AudioRoute.Conversation]);
    }

    [Fact]
    public void IlPredefinitoDiSistemaRestaSenzaDispositivo()
    {
        var motore = new MotoreFinto();

        AudioSettingsApplier.Apply(motore, new AppSettings());

        // "Nessuna scelta" e "usa il predefinito" sono la stessa cosa per il motore,
        // ed e' giusto che lo siano: quello che conta e' che l'assegnazione avvenga.
        Assert.All(motore.Dispositivi.Values, Assert.Null);
        Assert.Equal(4, motore.Dispositivi.Count);
    }

    /// <summary>Motore audio finto che annota soltanto cosa gli viene chiesto.</summary>
    private sealed class MotoreFinto : IAudioEngine
    {
        public Dictionary<AudioRoute, string?> Dispositivi { get; } = [];

        public Dictionary<AudioRoute, float> Volumi { get; } = [];

        public void AssignDevice(AudioRoute route, string? deviceId) => Dispositivi[route] = deviceId;

        public void SetVolume(AudioRoute route, float volume) => Volumi[route] = volume;

        public IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices() => [];

        public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() => [];

        public AudioDeviceInfo? GetAssignedDevice(AudioRoute route) => null;

        public Task PlayAsync(AudioRoute route, string filePath, float pan, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void DuckConversation(float level, TimeSpan duration)
        {
        }

        public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged
        {
            add { }
            remove { }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
