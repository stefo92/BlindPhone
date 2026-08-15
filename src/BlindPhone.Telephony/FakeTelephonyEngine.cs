using System.Collections.Concurrent;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;

namespace BlindPhone.Telephony;

/// <summary>
/// Motore telefonico simulato, senza rete né centralino.
/// </summary>
/// <remarks>
/// <para>
/// Serve a due scopi, entrambi concreti.
/// </para>
/// <para>
/// <b>Collaudo.</b> Le macchine a stati della chiamata e del trasferimento vanno
/// verificate su decine di combinazioni di esito: destinazione occupata, che non
/// risponde, che rifiuta, chiamante che riaggancia a metà consultazione, centralino
/// che respinge il REFER. Riprodurre ciascuna con un PBX reale è lento e, per
/// alcune, praticamente impossibile da rendere deterministico. Qui ogni esito si
/// impone con una riga di codice.
/// </para>
/// <para>
/// <b>Modalità formazione.</b> Un operatore appena assunto deve poter imparare le
/// scorciatoie senza rispondere a chiamate vere e senza occupare una linea di
/// servizio. Con questo motore l'applicazione si comporta esattamente come in
/// produzione, comprese le sequenze di trasferimento, ma nessuna telefonata parte.
/// È la funzionalità C11 dell'analisi, e qui arriva quasi gratis.
/// </para>
/// </remarks>
public sealed class FakeTelephonyEngine : ITelephonyEngine
{
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, CallParty> _calls = new();
    private int _counter;

    /// <summary>Crea il motore simulato.</summary>
    /// <param name="clock">Sorgente del tempo.</param>
    public FakeTelephonyEngine(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;

        // Deliberatamente NON si dichiara "registrato". Il motore simulato non e'
        // raggiungibile da nessuno, e dirlo raggiungibile farebbe credere
        // all'operatore di essere in servizio: resterebbe ad aspettare chiamate che
        // non possono arrivare, senza alcun modo di accorgersene.
        RegistrationState = new SipRegistrationState(
            RegistrationStatus.Unregistered,
            "SIMULATO",
            "Modalita' simulata: nessuna telefonata reale",
            _clock.Now);
    }

    /// <inheritdoc />
    public SipRegistrationState RegistrationState { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// Il primo contatore è il più importante che questo motore possa dare: dice che
    /// nessuna telefonata sta partendo davvero. È l'unico caso in cui il posto
    /// operatore funziona perfettamente e non serve a nulla, e chi legge una
    /// segnalazione deve vederlo prima di ogni altra cosa.
    /// </remarks>
    /// <inheritdoc />
    /// <remarks>
    /// Senza rete non c'è nessuno a cui sottoscriversi: in modalità simulata lo stato
    /// degli interni resta sconosciuto, come tutto il resto del centralino.
    /// </remarks>
    public IPresenceSource? PresenceSource => null;

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticCounter> DiagnosticCounters =>
    [
        new("Motore telefonico", "SIMULATO - nessuna telefonata reale"),
        new("Account SIP", "non configurato"),
        new("Chiamate simulate in corso", _calls.Count.ToString()),
    ];

    /// <inheritdoc />
    public bool IsMuted { get; private set; }

    /// <inheritdoc />
    public bool IsSimulated => true;

    /// <summary>
    /// Esito che le chiamate uscenti devono produrre. Consente di programmare lo
    /// scenario di collaudo prima di eseguirlo.
    /// </summary>
    public CallEndReason? OutboundOutcome { get; set; }

    /// <summary>
    /// Se <c>true</c>, il centralino simulato respinge le richieste di
    /// trasferimento cieco, per collaudare il ripiego sulla consultazione.
    /// </summary>
    public bool RejectBlindTransfer { get; set; }

    /// <inheritdoc />
    public event EventHandler<SipRegistrationState>? RegistrationStateChanged;

    /// <inheritdoc />
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <inheritdoc />
#pragma warning disable CS0067 // in modalita' simulata non c'e' cifratura da declassare
    public event EventHandler<string>? SrtpDowngraded;
#pragma warning restore CS0067

    /// <inheritdoc />
    public event EventHandler<CallProgressEventArgs>? CallProgress;

    /// <inheritdoc />
    public event EventHandler<TransferProgressEventArgs>? TransferProgress;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string> DialAsync(string destination, CancellationToken cancellationToken)
    {
        var callId = $"fake-{Interlocked.Increment(ref _counter)}";
        _calls[callId] = new CallParty(destination);
        return Task.FromResult(callId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// In modalità simulata l'inclusione non esiste: non c'è nessun centralino a cui
    /// chiedere di entrare in una conversazione, e restituire una chiamata finta
    /// farebbe credere all'operatore di essere in ascolto quando non lo è. Meglio
    /// dichiararlo.
    /// </remarks>
    public Task<string> BargeInAsync(DialogReference dialog, CancellationToken cancellationToken)
        => Task.FromException<string>(new InvalidOperationException(
            "Modalita' simulata: nessun centralino su cui includersi"));

    /// <inheritdoc />
    public Task AnswerAsync(string callId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task HangUpAsync(string callId, CancellationToken cancellationToken)
    {
        _calls.TryRemove(callId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task HoldAsync(string callId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ResumeAsync(string callId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task BlindTransferAsync(string callId, string destination, CancellationToken cancellationToken)
    {
        if (RejectBlindTransfer)
        {
            TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                callId, TransferOutcome.RejectedByPbx, "Trasferimento cieco non consentito", _clock.Now));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AttendedTransferAsync(string callId, string consultationCallId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Dichiarata disponibile, ed è coerente con il resto del motore: qui nessuna
    /// telefonata parte davvero, quindi non c'è alcun audio da unire né da rovinare.
    /// Serve alla formazione — la sequenza della conferenza si impara come tutte le
    /// altre — e ai test della macchina a stati, che è dove i difetti della conferenza
    /// si possono davvero trovare.
    /// </remarks>
    public bool SupportsConference => true;

    /// <inheritdoc />
    public Task StartConferenceAsync(IReadOnlyList<string> callIds, CancellationToken cancellationToken)
    {
        ConferencedCallIds = [.. callIds];
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopConferenceAsync(CancellationToken cancellationToken)
    {
        ConferencedCallIds = [];
        return Task.CompletedTask;
    }

    /// <summary>
    /// Chiamate attualmente unite in conferenza secondo il motore.
    /// </summary>
    /// <remarks>
    /// Esposta per i test: permette di verificare che il gestore delle chiamate
    /// costruisca il ponte sulle linee giuste e lo smonti quando deve, senza dover
    /// ascoltare dell'audio.
    /// </remarks>
    public IReadOnlyList<string> ConferencedCallIds { get; private set; } = [];

    /// <inheritdoc />
    public Task SendDtmfAsync(string callId, char digit, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void SetMuted(bool muted) => IsMuted = muted;

    /// <inheritdoc />
    public bool IsDoNotDisturb { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// Lo stato si registra e basta: in modalità simulata non arriva alcuna chiamata
    /// vera da rifiutare, ma il posto operatore deve poter mostrare e annunciare la
    /// pausa esattamente come in servizio, perché è così che la si impara.
    /// </remarks>
    public void SetDoNotDisturb(bool enabled) => IsDoNotDisturb = enabled;

    /// <inheritdoc />
    /// <remarks>In modalità simulata non esiste alcun trasporto: nulla è cifrato.</remarks>
    public bool IsSignallingEncrypted => false;

    /// <inheritdoc />
    /// <remarks>
    /// Rifiuta sempre: in modalità simulata non esiste audio da registrare, e produrre
    /// un file vuoto sarebbe peggio che non produrne — l'operatore lo troverebbe nella
    /// cartella e lo scambierebbe per una conversazione andata perduta.
    /// </remarks>
    public bool StartCallRecording(string callId, string path) => false;

    /// <inheritdoc />
    public (TimeSpan Duration, Task<string> File)? StopCallRecording(string callId) => null;

    /// <inheritdoc />
    public void PlayToRemote(string callId, short[] pcm, int sampleRate) { }

    // -----------------------------------------------------------------------
    // Comandi di simulazione
    // -----------------------------------------------------------------------

    /// <summary>Simula l'arrivo di una chiamata entrante.</summary>
    /// <param name="number">Numero del chiamante.</param>
    /// <param name="displayName">Nome del chiamante, se noto.</param>
    /// <param name="queueName">Coda di provenienza.</param>
    /// <returns>Identificativo della chiamata creata.</returns>
    public string SimulateIncomingCall(string number, string? displayName = null, string? queueName = null)
    {
        var callId = $"fake-{Interlocked.Increment(ref _counter)}";
        var party = new CallParty(number, displayName);
        _calls[callId] = party;

        IncomingCall?.Invoke(this, new IncomingCallEventArgs(
            callId, party, $"sip-{callId}", queueName, _clock.Now));

        return callId;
    }

    /// <summary>Simula un avanzamento di chiamata proveniente dalla rete.</summary>
    /// <param name="callId">Chiamata interessata.</param>
    /// <param name="trigger">Evento da propagare.</param>
    /// <param name="reason">Motivo di chiusura, se pertinente.</param>
    /// <param name="codec">Codec negoziato da dichiarare.</param>
    /// <param name="wideband">Se il codec dichiarato è a banda larga.</param>
    public void SimulateProgress(
        string callId,
        CallTrigger trigger,
        CallEndReason? reason = null,
        string? codec = "G722",
        bool wideband = true)
    {
        CallProgress?.Invoke(this, new CallProgressEventArgs(
            callId, trigger, reason, codec, wideband, IsEncrypted: false, _clock.Now));
    }

    /// <summary>Simula l'esito di un trasferimento comunicato dal centralino.</summary>
    /// <param name="callId">Chiamata trasferita.</param>
    /// <param name="outcome">Esito.</param>
    /// <param name="detail">Dettaglio in linguaggio naturale.</param>
    public void SimulateTransferOutcome(string callId, TransferOutcome outcome, string? detail = null)
    {
        TransferProgress?.Invoke(this, new TransferProgressEventArgs(callId, outcome, detail, _clock.Now));
    }

    /// <summary>Simula un cambio di stato della registrazione.</summary>
    /// <param name="status">Nuovo stato.</param>
    /// <param name="failureReason">Motivo del fallimento, in linguaggio naturale.</param>
    public void SimulateRegistration(RegistrationStatus status, string? failureReason = null)
    {
        RegistrationState = new SipRegistrationState(status, "Simulato", failureReason, _clock.Now);
        RegistrationStateChanged?.Invoke(this, RegistrationState);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
