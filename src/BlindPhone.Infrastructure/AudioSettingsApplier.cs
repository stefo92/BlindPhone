using BlindPhone.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Porta la configurazione audio dentro il motore che suona.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto che questa classe corregge.</b> Le quattro vie audio — microfono,
/// conversazione, suoneria, annunci — erano configurabili, salvate correttamente nel
/// file e mostrate correttamente nel pannello, ma <b>non venivano mai applicate</b>:
/// nessuno chiamava <c>AssignDevice</c> all'avvio. Il motore, non avendo alcuna
/// assegnazione, ricadeva sul dispositivo di comunicazione predefinito di Windows per
/// ogni via, e tutto finiva nello stesso posto.
/// </para>
/// <para>
/// Il sintomo era perfettamente coerente e per questo difficile da attribuire alla
/// configurazione: l'operatore impostava gli altoparlanti per la suoneria, salvava,
/// riavviava, e continuava a sentire tutto in cuffia. Il pannello mostrava la scelta
/// giusta, il file la conteneva, e il suono usciva dalla parte sbagliata.
/// </para>
/// <para>
/// Anche i volumi per via erano nella stessa condizione: <c>SetVolume</c> non veniva
/// invocato da nessuno, quindi il volume della suoneria configurato all'ottanta per
/// cento non aveva alcun effetto.
/// </para>
/// </remarks>
public static class AudioSettingsApplier
{
    /// <summary>
    /// Assegna dispositivi e volumi delle quattro vie.
    /// </summary>
    /// <param name="audio">Motore audio.</param>
    /// <param name="settings">Configurazione da applicare.</param>
    /// <param name="log">Log diagnostico.</param>
    /// <remarks>
    /// Da richiamare all'avvio e a ogni salvataggio: il cambio di dispositivo è una
    /// delle poche impostazioni che l'operatore prova subito dopo averla cambiata, e
    /// chiedergli un riavvio per sentirne l'effetto significa fargli credere che non
    /// funzioni.
    /// </remarks>
    public static void Apply(IAudioEngine audio, AppSettings settings, ILogger? log = null)
    {
        Assign(audio, AudioRoute.Microphone, settings.Microphone, log);
        Assign(audio, AudioRoute.Conversation, settings.Conversation, log);
        Assign(audio, AudioRoute.Ringer, settings.Ringer, log);
        Assign(audio, AudioRoute.Announcements, settings.Announcements, log);

        // L'elaborazione del microfono segue la stessa strada dei dispositivi e vale
        // anche per la chiamata in corso: chi sente il rumore se ne accorge parlando, e
        // verifica l'effetto subito dopo aver salvato.
        audio.SetMicrophoneProcessing(settings.MicrophoneProcessing);
    }

    private static void Assign(IAudioEngine audio, AudioRoute route, AudioRouteSettings via, ILogger? log)
    {
        audio.AssignDevice(route, via.DeviceId);
        audio.SetVolume(route, via.Volume);

        log?.LogInformation(
            "Via audio {Route}: dispositivo {Device}, volume {Volume:P0}",
            route,
            via.DeviceName ?? "predefinito di sistema",
            via.Volume);
    }
}
