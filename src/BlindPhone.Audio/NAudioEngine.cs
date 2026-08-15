using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BlindPhone.Audio;

/// <summary>
/// Motore audio su WASAPI, con instradamento indipendente delle quattro vie del
/// posto operatore.
/// </summary>
/// <remarks>
/// <para>
/// Le quattro vie (microfono, conversazione, suoneria, annunci) non sono una
/// comodità di configurazione: sono la soluzione al problema quotidiano numero uno
/// di chi lavora al centralino senza vedere, cioè la voce dello screen reader e la
/// voce dell'interlocutore che arrivano nello stesso auricolare e si sovrappongono.
/// Separare l'uscita degli annunci su un dispositivo diverso da quello della
/// conversazione risolve la sovrapposizione senza chiedere all'operatore di
/// scegliere fra sentire il chiamante e sentire il sistema.
/// </para>
/// <para>
/// <b>Gestione del collegamento a caldo.</b> Se la cuffia viene scollegata durante
/// una chiamata l'audio migra sul dispositivo di riserva e l'evento viene
/// annunciato in priorità critica. Non è una raffinatezza: l'operatore deve sapere
/// che sta parlando dagli altoparlanti <i>prima</i> di dire ad alta voce qualcosa di
/// riservato in un ufficio aperto.
/// </para>
/// </remarks>
public sealed class NAudioEngine : IAudioEngine, IAudioDucker
{
    private readonly ILogger<NAudioEngine> _log;
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDeviceNotificationClient? _notifiche;
    private readonly Lock _gate = new();

    private readonly Dictionary<AudioRoute, string?> _assignedDeviceIds = new()
    {
        [AudioRoute.Microphone] = null,
        [AudioRoute.Conversation] = null,
        [AudioRoute.Ringer] = null,
        [AudioRoute.Announcements] = null,
    };

    private readonly Dictionary<AudioRoute, float> _volumes = new()
    {
        [AudioRoute.Microphone] = 1f,
        [AudioRoute.Conversation] = 1f,
        [AudioRoute.Ringer] = 0.8f,
        [AudioRoute.Announcements] = 1f,
    };

    private float _duckFactor = 1f;
    private DateTimeOffset _duckUntil;

    /// <summary>Crea il motore audio.</summary>
    /// <param name="logger">Log diagnostico.</param>
    public NAudioEngine(ILogger<NAudioEngine>? logger = null)
    {
        _log = logger ?? NullLogger<NAudioEngine>.Instance;

        try
        {
            // DA NAUDIO 3.0 NON SI IMPLEMENTA PIU' L'INTERFACCIA COM.
            //
            // IMMNotificationClient e' diventata internal: al suo posto NAudio
            // espone un oggetto che solleva eventi. Cambia la forma, non la
            // sostanza — gli avvisi che interessano restano i due di sempre, il
            // dispositivo che cambia stato e quello che viene tolto.
            _notifiche = _enumerator.CreateNotificationClient(false);

            _notifiche.DeviceStateChanged += OnDeviceStateChanged;
            _notifiche.DeviceRemoved += OnDeviceRemoved;
        }
        catch (Exception ex)
        {
            // Senza notifiche di sistema il motore resta funzionante: si perde solo
            // il rilevamento automatico dello scollegamento, che viene comunque
            // intercettato al primo errore di riproduzione.
            _log.LogWarning(ex, "Registrazione delle notifiche sui dispositivi audio non riuscita");
        }
    }

    /// <inheritdoc />
    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;

    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices() => Enumerate(DataFlow.Render);

    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() => Enumerate(DataFlow.Capture);

    private IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        try
        {
            var defaultId = TryGetDefaultDeviceId(flow);

            return [.. _enumerator
                .EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(
                    d.ID,
                    d.FriendlyName,
                    flow == DataFlow.Capture,
                    d.ID == defaultId))];
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Enumerazione dei dispositivi audio non riuscita");
            return [];
        }
    }

    private string? TryGetDefaultDeviceId(DataFlow flow)
    {
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(flow, Role.Communications).ID;
        }
        catch (Exception)
        {
            // Nessun dispositivo predefinito configurato: non e' un errore, capita
            // su macchine senza scheda audio attiva.
            return null;
        }
    }

    /// <inheritdoc />
    public void AssignDevice(AudioRoute route, string? deviceId)
    {
        lock (_gate) _assignedDeviceIds[route] = deviceId;
        _log.LogInformation("Via audio {Route} assegnata al dispositivo {DeviceId}", route, deviceId ?? "predefinito");
    }

    /// <inheritdoc />
    public AudioDeviceInfo? GetAssignedDevice(AudioRoute route)
    {
        string? id;
        lock (_gate) id = _assignedDeviceIds[route];

        var flow = route == AudioRoute.Microphone ? DataFlow.Capture : DataFlow.Render;

        try
        {
            if (id is null)
            {
                var fallback = _enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
                return new AudioDeviceInfo(fallback.ID, fallback.FriendlyName, flow == DataFlow.Capture, true);
            }

            var device = _enumerator.GetDevice(id);
            return new AudioDeviceInfo(device.ID, device.FriendlyName, flow == DataFlow.Capture, false);
        }
        catch (Exception)
        {
            // Dispositivo memorizzato ma non piu' presente: si segnala come assente
            // invece di sollevare, cosi' l'interfaccia puo' dirlo all'operatore.
            return null;
        }
    }

    /// <inheritdoc />
    public void SetVolume(AudioRoute route, float volume)
    {
        lock (_gate) _volumes[route] = Math.Clamp(volume, 0f, 1f);
    }

    /// <inheritdoc />
    public async Task PlayAsync(AudioRoute route, string filePath, float pan, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            _log.LogWarning("File audio non trovato: {Path}", filePath);
            return;
        }

        try
        {
            await using var reader = new AudioFileReader(filePath);
            var sample = ApplyGain(new PanningSampleProvider(reader.ToMono()) { Pan = Math.Clamp(pan, -1f, 1f) }, route);
            await PlaySampleAsync(route, sample, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Riproduzione del file {Path} non riuscita", filePath);
        }
    }

    /// <summary>
    /// Riproduce un flusso di campioni su una via audio, attendendone la fine.
    /// </summary>
    /// <param name="route">Via audio di destinazione.</param>
    /// <param name="sample">Sorgente dei campioni.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    public async Task PlaySampleAsync(
        AudioRoute route,
        ISampleProvider sample,
        CancellationToken cancellationToken)
    {
        var deviceId = GetEffectiveDeviceId(route);

        try
        {
            using var device = deviceId is null
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications)
                : _enumerator.GetDevice(deviceId);

            using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);

            var completion = new TaskCompletionSource();
            output.PlaybackStopped += (_, _) => completion.TrySetResult();

            output.Init(sample);
            output.Play();

            await using var registration = cancellationToken.Register(() =>
            {
                // IL TRONCAMENTO SI VEDE NEL REGISTRO, NON SOLO A ORECCHIO.
                //
                // E' cio' che distingue «la suoneria e' stata interrotta e non l'ho
                // sentita finire» da «la suoneria ha continuato a suonare sopra il
                // chiamante»: con una suoneria personalizzata di otto secondi la
                // differenza fra le due e' tutta la conversazione iniziale.
                _log.LogDebug("Riproduzione sulla via {Route} interrotta su richiesta", route);

                try { output.Stop(); } catch { /* gia' fermo */ }
            });

            await completion.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Un dispositivo scomparso durante la riproduzione e' il caso tipico:
            // si segnala la migrazione e si riprova sul predefinito, perche' un
            // earcon perso e' un'informazione persa.
            _log.LogWarning(ex, "Riproduzione sulla via {Route} non riuscita", route);
            HandleDeviceFailure(route);
        }
    }

    private string? GetEffectiveDeviceId(AudioRoute route)
    {
        lock (_gate) return _assignedDeviceIds[route];
    }

    private ISampleProvider ApplyGain(ISampleProvider source, AudioRoute route)
    {
        float volume;
        lock (_gate) volume = _volumes[route];

        // L'attenuazione per gli annunci critici si applica solo alla via della
        // conversazione: attenuare anche la suoneria renderebbe meno udibile
        // proprio l'evento che si sta annunciando.
        // L'ATTENUAZIONE VALE ANCHE PER LA SUONERIA, E NON SOLO PER LA CONVERSAZIONE.
        //
        // Nasce da una segnalazione: «gli annunci cessano da quando squilla la chiamata».
        // Non cessavano — venivano coperti. Su una postazione dove suoneria e annunci
        // escono dallo stesso altoparlante, e con una suoneria personalizzata che dura
        // quasi nove secondi su un ciclo di tredici, la voce che dice chi sta chiamando
        // arriva sotto il segnale che sta suonando in quel momento.
        //
        // Attenuare la sola conversazione risolveva meta' del problema: quella meta'
        // era l'unica che esisteva quando l'attenuazione e' stata scritta, perche' la
        // suoneria di allora durava due decimi di secondo.
        if (DateTimeOffset.Now < _duckUntil
            && route is AudioRoute.Conversation or AudioRoute.Ringer)
        {
            volume *= _duckFactor;
        }

        return new VolumeSampleProvider(source) { Volume = volume };
    }

    /// <inheritdoc />
    public void DuckConversation(float attenuationDb, TimeSpan duration)
    {
        _duckFactor = (float)Math.Pow(10, -Math.Abs(attenuationDb) / 20.0);
        _duckUntil = DateTimeOffset.Now.Add(duration);
    }

    /// <inheritdoc />
    void IAudioDucker.Duck(float attenuationDb, TimeSpan duration) => DuckConversation(attenuationDb, duration);

    private void HandleDeviceFailure(AudioRoute route)
    {
        string? previousName = null;

        try
        {
            var previousId = GetEffectiveDeviceId(route);
            if (previousId is not null) previousName = _enumerator.GetDevice(previousId).FriendlyName;
        }
        catch (Exception)
        {
            // Il dispositivo non esiste piu': e' esattamente il caso in gestione.
        }

        lock (_gate) _assignedDeviceIds[route] = null;

        var fallbackName = TryGetDefaultDeviceName() ?? "dispositivo predefinito";

        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
            route, previousName, fallbackName, WasUnplugged: true));
    }

    private string? TryGetDefaultDeviceName()
    {
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications).FriendlyName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Notifiche di sistema sui dispositivi
    // -----------------------------------------------------------------------

    /// <summary>
    /// Un dispositivo ha cambiato stato: se e' uno di quelli in uso e non e' piu'
    /// attivo, l'audio migra sulla riserva.
    /// </summary>
    /// <remarks>
    /// Gli altri avvisi disponibili — dispositivo aggiunto, dispositivo predefinito
    /// cambiato, proprieta' modificata — non vengono sottoscritti, come non venivano
    /// gestiti prima: un dispositivo che compare non cambia nulla per chi sta
    /// parlando, e il predefinito di sistema non e' quello che il posto operatore usa.
    /// </remarks>
    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
        => DispositivoNonPiuUtilizzabile(e.DeviceId, e.NewState);

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e)
        => DispositivoNonPiuUtilizzabile(e.DeviceId, DeviceState.NotPresent);

    private void DispositivoNonPiuUtilizzabile(string deviceId, DeviceState stato)
    {
        if (stato == DeviceState.Active) return;

        foreach (var route in Enum.GetValues<AudioRoute>())
        {
            if (GetEffectiveDeviceId(route) == deviceId) HandleDeviceFailure(route);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        try
        {
            if (_notifiche is not null)
            {
                _notifiche.DeviceStateChanged -= OnDeviceStateChanged;
                _notifiche.DeviceRemoved -= OnDeviceRemoved;
                _notifiche.Dispose();
                _notifiche = null;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Deregistrazione delle notifiche audio non riuscita");
        }

        _enumerator.Dispose();
        return ValueTask.CompletedTask;
    }
}
