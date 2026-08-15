namespace BlindPhone.Core.Abstractions;

/// <summary>
/// Le quattro vie audio del posto operatore.
/// </summary>
/// <remarks>
/// La quarta via è il differenziale accessibile del prodotto. In un softphone
/// normale la voce dello screen reader e la voce del chiamante finiscono nello
/// stesso auricolare e si sovrappongono: è il problema quotidiano numero uno di chi
/// lavora al centralino senza vedere. Separare l'uscita degli annunci lo risolve
/// (ANALISI §4.2 livello 3 e §8.1).
/// </remarks>
public enum AudioRoute
{
    /// <summary>Cattura della voce dell'operatore.</summary>
    Microphone,

    /// <summary>Audio della conversazione, tipicamente in cuffia.</summary>
    Conversation,

    /// <summary>Suoneria ed earcon di coda, tipicamente sugli altoparlanti.</summary>
    Ringer,

    /// <summary>Voce interna degli annunci, su dispositivo dedicato.</summary>
    Announcements,
}

/// <summary>Un dispositivo audio disponibile sul sistema.</summary>
/// <param name="Id">Identificativo stabile del dispositivo.</param>
/// <param name="Name">Nome descrittivo, quello che l'operatore sente pronunciare.</param>
/// <param name="IsCapture"><c>true</c> se è un dispositivo di ingresso.</param>
/// <param name="IsDefault"><c>true</c> se è il dispositivo predefinito di sistema.</param>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsCapture, bool IsDefault);

/// <summary>
/// Motore audio: enumerazione dei dispositivi, instradamento, gestione del
/// collegamento a caldo.
/// </summary>
public interface IAudioEngine : IAsyncDisposable
{
    /// <summary>Elenca i dispositivi di riproduzione disponibili.</summary>
    IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices();

    /// <summary>Elenca i dispositivi di acquisizione disponibili.</summary>
    IReadOnlyList<AudioDeviceInfo> GetCaptureDevices();

    /// <summary>Assegna un dispositivo a una via audio.</summary>
    /// <param name="route">Via da configurare.</param>
    /// <param name="deviceId">Identificativo del dispositivo, oppure <c>null</c> per il predefinito di sistema.</param>
    void AssignDevice(AudioRoute route, string? deviceId);

    /// <summary>Restituisce il dispositivo attualmente assegnato a una via.</summary>
    /// <param name="route">Via da interrogare.</param>
    AudioDeviceInfo? GetAssignedDevice(AudioRoute route);

    /// <summary>Imposta il volume di una via, da 0 a 1.</summary>
    /// <param name="route">Via da regolare.</param>
    /// <param name="volume">Volume, valori fuori intervallo vengono limitati.</param>
    void SetVolume(AudioRoute route, float volume);

    /// <summary>Riproduce un file audio su una via specifica.</summary>
    /// <param name="route">Via su cui riprodurre.</param>
    /// <param name="filePath">Percorso del file.</param>
    /// <param name="pan">Posizione stereo, da -1 (sinistra) a +1 (destra).</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task PlayAsync(AudioRoute route, string filePath, float pan, CancellationToken cancellationToken);

    /// <summary>
    /// Attenua temporaneamente l'audio della conversazione, per far passare un
    /// annuncio critico senza che si sovrapponga alla voce dell'interlocutore.
    /// </summary>
    /// <param name="attenuationDb">Attenuazione in decibel, valore positivo.</param>
    /// <param name="duration">Durata dell'attenuazione.</param>
    void DuckConversation(float attenuationDb, TimeSpan duration);

    /// <summary>
    /// Sollevato quando un dispositivo assegnato viene scollegato e l'audio migra
    /// sul dispositivo di riserva.
    /// </summary>
    /// <remarks>
    /// Va annunciato con priorità critica. Se la cuffia si scollega e l'audio passa
    /// agli altoparlanti, l'operatore deve saperlo prima di dire qualcosa di
    /// riservato a voce alta in un ufficio aperto.
    /// </remarks>
    event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;
}

/// <summary>Dati dell'evento di cambio dispositivo a caldo.</summary>
/// <param name="Route">Via audio interessata.</param>
/// <param name="PreviousDeviceName">Nome del dispositivo precedente.</param>
/// <param name="NewDeviceName">Nome del dispositivo su cui l'audio è migrato.</param>
/// <param name="WasUnplugged"><c>true</c> se il cambio è dovuto a uno scollegamento.</param>
public sealed record AudioDeviceChangedEventArgs(
    AudioRoute Route,
    string? PreviousDeviceName,
    string NewDeviceName,
    bool WasUnplugged);
