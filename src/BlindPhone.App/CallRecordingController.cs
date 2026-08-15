using System.IO;
using BlindPhone.Accessibility;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Governa la registrazione della conversazione: avvio, chiusura, avvisi.
/// </summary>
/// <remarks>
/// <para>
/// Sta nel livello applicativo e non nel core perché mette insieme tre cose che il
/// core non conosce: il motore telefonico che registra, la sintesi vocale di Windows
/// che parla all'interlocutore, e la cartella Documenti dell'utente. Le parti che si
/// possono verificare senza nulla di tutto questo — dove va il file, come si chiama,
/// che cosa si dice — vivono nel core e qui si usano soltanto.
/// </para>
/// <para>
/// <b>L'ordine delle operazioni non è arbitrario.</b> All'avvio si avvisa
/// l'interlocutore <b>prima</b> di cominciare a registrare, perché l'informativa deve
/// precedere la raccolta e non accompagnarla. Alla chiusura si fa il contrario: prima
/// si chiude, poi si avvisa, altrimenti l'avviso di fine finirebbe registrato dentro
/// la registrazione che dichiara conclusa.
/// </para>
/// </remarks>
public sealed class CallRecordingController
{
    private readonly CallSessionManager _calls;
    private readonly ITelephonyEngine _telephony;
    private readonly IAnnouncer _announcer;
    private readonly RemoteVoice _remoteVoice;
    private readonly SettingsService _settings;
    private readonly ILogger<CallRecordingController> _log;

    private string? _recordingCallId;

    /// <summary>Crea il controllore.</summary>
    /// <param name="calls">Gestore delle chiamate, per sapere chi è in linea.</param>
    /// <param name="telephony">Motore telefonico, che registra e mette in linea gli avvisi.</param>
    /// <param name="announcer">Pipeline di annuncio verso l'operatore.</param>
    /// <param name="remoteVoice">Sintesi delle frasi destinate all'interlocutore.</param>
    /// <param name="settings">Configurazione, letta al momento dell'uso.</param>
    /// <param name="logger">Log diagnostico.</param>
    public CallRecordingController(
        CallSessionManager calls,
        ITelephonyEngine telephony,
        IAnnouncer announcer,
        RemoteVoice remoteVoice,
        SettingsService settings,
        ILogger<CallRecordingController> logger)
    {
        _calls = calls;
        _telephony = telephony;
        _announcer = announcer;
        _remoteVoice = remoteVoice;
        _settings = settings;
        _log = logger;
    }

    /// <summary><c>true</c> se una registrazione è in corso.</summary>
    public bool IsRecording => _recordingCallId is not null;

    /// <summary>
    /// Avvia la registrazione, o la interrompe se è già in corso.
    /// </summary>
    /// <param name="recordingKeyName">Combinazione da citare negli annunci.</param>
    public async Task ToggleAsync(string recordingKeyName)
    {
        if (_recordingCallId is not null)
        {
            await StopAsync().ConfigureAwait(true);
            return;
        }

        await StartAsync(recordingKeyName).ConfigureAwait(true);
    }

    /// <summary>
    /// Chiude la registrazione se la chiamata registrata non è più in linea.
    /// </summary>
    /// <remarks>
    /// Una chiamata che finisce mentre si registra non lascia il comando appeso: il
    /// percorso audio chiude il file da sé, e qui si azzera lo stato perché la
    /// combinazione torni a significare «avvia» invece di «interrompi». Senza, la
    /// pressione successiva cercherebbe di fermare una registrazione che non esiste
    /// più, e l'operatore si sentirebbe dire che è stata interrotta una cosa che era
    /// già finita.
    /// </remarks>
    public void OnCallsChanged()
    {
        if (_recordingCallId is null) return;
        if (_calls.GetCall(_recordingCallId) is { } call && !CallStateMachine.IsTerminal(call.State)) return;

        _log.LogInformation("Registrazione chiusa dalla fine della chiamata {CallId}", _recordingCallId);
        _recordingCallId = null;
    }

    /// <remarks>
    /// Non attende nulla: la registrazione parte in un istante e la sintesi
    /// dell'avviso è già pronta quando arriva qui. Resta un metodo asincrono per
    /// simmetria con la chiusura, che invece attende la conversione del file.
    /// </remarks>
    private Task StartAsync(string recordingKeyName)
    {
        // La conferenza non si registra: le due gambe sono due percorsi audio distinti
        // e ne risulterebbe una registrazione con una sola delle due voci.
        if (_calls.IsConferenceActive)
        {
            Rifiuta("In conferenza la registrazione non e' disponibile: sciogli prima la conferenza");
            return Task.CompletedTask;
        }

        if (_calls.ActiveCall is not { } call)
        {
            Rifiuta(AnnouncementTexts.RecordingNeedsCall);
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.Now;
        var avvisa = _settings.Current.Calls.AnnounceRecording;

        // L'AVVISO PRECEDE LA REGISTRAZIONE, e non e' un dettaglio di ordine:
        // l'informativa deve arrivare prima che la raccolta cominci.
        if (avvisa) AvvisaInterlocutore(call.Id, AnnouncementTexts.RecordingStartedForRemote);

        var percorso = CallRecordingPaths.FileFor(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            call.RemoteParty,
            now);

        if (!_telephony.StartCallRecording(call.Id, percorso))
        {
            Rifiuta(_telephony.IsSimulated
                ? "Registrazione non disponibile in modalita' simulata: nessuna telefonata reale in corso"
                : "Registrazione non avviata: il percorso audio della chiamata non e' disponibile");

            return Task.CompletedTask;
        }

        _recordingCallId = call.Id;

        _log.LogInformation(
            "Registrazione avviata sulla chiamata {CallId} in {Path}, avviso all'interlocutore {Avviso}",
            call.Id, percorso, avvisa ? "attivo" : "disattivato");

        _announcer.Announce(Announcement.High(
            AnnouncementTexts.RecordingStartedForOperator(avvisa, recordingKeyName),
            now,
            coalesceKey: "recording:state"));

        return Task.CompletedTask;
    }

    private async Task StopAsync()
    {
        var callId = _recordingCallId;
        if (callId is null) return;

        _recordingCallId = null;

        var esito = _telephony.StopCallRecording(callId);

        if (esito is null)
        {
            // Il percorso audio l'aveva gia' chiusa, tipicamente perche' la chiamata e'
            // caduta un istante prima: non e' un guasto e non va raccontato come tale.
            _log.LogInformation("Registrazione gia' chiusa sulla chiamata {CallId}", callId);
            return;
        }

        var (durata, file) = esito.Value;

        // LA CHIUSURA PRECEDE L'AVVISO, al contrario dell'avvio: dicendolo prima,
        // "registrazione interrotta" finirebbe dentro la registrazione stessa.
        if (_settings.Current.Calls.AnnounceRecording)
        {
            AvvisaInterlocutore(callId, AnnouncementTexts.RecordingStoppedForRemote(durata));
        }

        // L'operatore sente subito la durata; il nome del file arriva quando la
        // conversione e' finita, perche' prima non si sa quale dei due formati sara'.
        _announcer.Announce(Announcement.High(
            $"Registrazione interrotta, {SpeechFormatter.Duration(durata)}",
            DateTimeOffset.Now,
            coalesceKey: "recording:state"));

        try
        {
            var percorso = await file.ConfigureAwait(true);

            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.RecordingStoppedForOperator(durata, Path.GetFileName(percorso)),
                DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Conversione della registrazione non riuscita");

            _announcer.Announce(Announcement.CommandResponse(
                "La registrazione e' stata salvata ma la conversione in MP3 non e' riuscita: "
                + "il file resta in formato WAV nella cartella Registrazione Chiamate",
                DateTimeOffset.Now));
        }
    }

    /// <summary>Mette in linea una frase per chi sta parlando con l'operatore.</summary>
    private void AvvisaInterlocutore(string callId, string testo)
    {
        var pcm = _remoteVoice.Render(testo);

        if (pcm.Length == 0)
        {
            // Senza voci installate l'avviso non parte. Va detto all'operatore, perche'
            // da quel momento informare l'interlocutore tocca a lui.
            _announcer.Announce(Announcement.High(
                "Attenzione: l'avviso vocale non e' partito, sul sistema non c'e' una voce di sintesi. "
                + "Informa tu l'interlocutore che la conversazione viene registrata",
                DateTimeOffset.Now));

            return;
        }

        _telephony.PlayToRemote(callId, pcm, RemoteVoice.SampleRate);
    }

    private void Rifiuta(string motivo)
    {
        _announcer.Announce(Announcement.CommandResponse(motivo, DateTimeOffset.Now));
        _announcer.PlayEarcon(EarconKind.CommandRejected);
    }
}
