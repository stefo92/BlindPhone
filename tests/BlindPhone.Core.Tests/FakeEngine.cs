using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Motore telefonico simulato locale al progetto di test.
/// </summary>
/// <remarks>
/// Volutamente non si riusa <c>FakeTelephonyEngine</c> di BlindPhone.Telephony:
/// quel progetto targetta la banda con <c>-windows</c> perché ha bisogno dell'audio,
/// mentre i test del core girano sulla banda senza, proprio per garantire che il
/// dominio resti indipendente dalla piattaforma. Se un giorno questo file non compilasse più
/// significherebbe che qualcosa di specifico di Windows è entrato nel core, ed è
/// esattamente il segnale che si vuole ricevere.
/// </remarks>
internal sealed class FakeEngine : ITelephonyEngine
{
    private int _counter;

    public List<string> Operations { get; } = [];

    public SipRegistrationState RegistrationState { get; private set; } =
        new(RegistrationStatus.Registered, "test");

    public bool IsMuted { get; private set; }

    public bool IsSimulated => true;

    /// <summary>
    /// Sorgente di presenza offerta dal motore, se la prova ne fornisce una.
    /// </summary>
    /// <remarks>
    /// È la strada da cui <see cref="Core.Services.PresenceMonitorService"/> prende le
    /// sottoscrizioni SIP quando il centralino non espone l'API: serve alle prove
    /// dell'inclusione, che hanno bisogno di uno stato «occupato» con gli
    /// identificativi del dialogo.
    /// </remarks>
    public IPresenceSource? PresenceSource { get; set; }

    /// <summary>
    /// Sondaggio di raggiungibilità offerto dal motore, se la prova ne fornisce uno.
    /// </summary>
    /// <remarks>
    /// È la strada da cui <see cref="Core.Services.ShiftBoardService"/> ricava lo stato
    /// dei colleghi quando il centralino non serve né l'API né le sottoscrizioni.
    /// </remarks>
    public IReachabilityProbe? ReachabilityProbe { get; set; }

    public IReadOnlyList<DiagnosticCounter> DiagnosticCounters =>
    [
        new("Motore telefonico", "finto, per le prove"),
        new("Operazioni registrate", Operations.Count.ToString()),
    ];

    public event EventHandler<SipRegistrationState>? RegistrationStateChanged;

    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

    public event EventHandler<string>? SrtpDowngraded;

    /// <summary>Simula il ripiego in chiaro dopo un'offerta cifrata non accettata.</summary>
    public void SimulateSrtpDowngrade(string destination) => SrtpDowngraded?.Invoke(this, destination);

    public event EventHandler<CallProgressEventArgs>? CallProgress;

    public event EventHandler<TransferProgressEventArgs>? TransferProgress;

    public event EventHandler<MessageWaitingEventArgs>? MessageWaitingChanged;

    /// <summary>Simula una notifica della segreteria arrivata dal centralino.</summary>
    public void SimulateMessageWaiting(MessageSummary summary, string account = "test")
        => MessageWaitingChanged?.Invoke(this, new MessageWaitingEventArgs(account, summary, DateTimeOffset.Now));

    /// <summary>Simula la registrazione su un altro account, come dopo un cambio di account.</summary>
    public void SimulateAccountRegistration(string account)
    {
        RegistrationState = new SipRegistrationState(RegistrationStatus.Registered, account, null, DateTimeOffset.Now);
        RegistrationStateChanged?.Invoke(this, RegistrationState);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Inclusione richiesta, se ne è stata chiesta una.</summary>
    public DialogReference? BargedInto { get; private set; }

    /// <summary>Se l'inclusione deve fallire, come farebbe un centralino che non la sostiene.</summary>
    public bool BargeInFails { get; set; }

    public Task<string> BargeInAsync(DialogReference dialog, CancellationToken cancellationToken)
    {
        Operations.Add($"barge:{dialog.Extension}:{dialog.ToJoinHeaderValue()}");

        if (BargeInFails)
        {
            return Task.FromException<string>(
                new InvalidOperationException("Il centralino non sostiene l'inclusione"));
        }

        BargedInto = dialog;

        var id = $"barge-{Interlocked.Increment(ref _counter)}";
        return Task.FromResult(id);
    }

    public Task<string> DialAsync(string destination, CancellationToken cancellationToken)
    {
        var id = $"out-{Interlocked.Increment(ref _counter)}";
        Operations.Add($"dial:{destination}:{id}");
        return Task.FromResult(id);
    }

    public Task AnswerAsync(string callId, CancellationToken cancellationToken)
    {
        Operations.Add($"answer:{callId}");
        return Task.CompletedTask;
    }

    public Task HangUpAsync(string callId, CancellationToken cancellationToken)
    {
        Operations.Add($"hangup:{callId}");
        return Task.CompletedTask;
    }

    public Task HoldAsync(string callId, CancellationToken cancellationToken)
    {
        Operations.Add($"hold:{callId}");
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string callId, CancellationToken cancellationToken)
    {
        Operations.Add($"resume:{callId}");
        return Task.CompletedTask;
    }

    public Task BlindTransferAsync(string callId, string destination, CancellationToken cancellationToken)
    {
        Operations.Add($"blind:{callId}:{destination}");
        return Task.CompletedTask;
    }

    public Task AttendedTransferAsync(string callId, string consultationCallId, CancellationToken cancellationToken)
    {
        Operations.Add($"attended:{callId}:{consultationCallId}");
        return Task.CompletedTask;
    }

    public Task SendDtmfAsync(string callId, char digit, CancellationToken cancellationToken)
    {
        Operations.Add($"dtmf:{callId}:{digit}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Se il motore dichiara di saper unire due chiamate.
    /// </summary>
    /// <remarks>
    /// Scrivibile perché il rifiuto va collaudato quanto la riuscita: un motore che
    /// non sa fare conferenze deve dirlo, e quel percorso è l'unico che l'operatore
    /// incontra su una postazione senza dispositivi audio.
    /// </remarks>
    public bool SupportsConference { get; set; } = true;

    /// <summary>
    /// Se l'apertura della conferenza deve fallire, per collaudare il ripristino.
    /// </summary>
    public bool FailConference { get; set; }

    public IReadOnlyList<string> ConferencedCallIds { get; private set; } = [];

    public Task StartConferenceAsync(IReadOnlyList<string> callIds, CancellationToken cancellationToken)
    {
        Operations.Add($"conference:{string.Join('+', callIds)}");

        if (FailConference) throw new InvalidOperationException("conferenza non riuscita, per le prove");

        ConferencedCallIds = [.. callIds];
        return Task.CompletedTask;
    }

    public Task StopConferenceAsync(CancellationToken cancellationToken)
    {
        Operations.Add("conference-stop");
        ConferencedCallIds = [];
        return Task.CompletedTask;
    }

    public void SetMuted(bool muted) => IsMuted = muted;

    public bool IsDoNotDisturb { get; private set; }

    public void SetDoNotDisturb(bool enabled)
    {
        IsDoNotDisturb = enabled;
        Operations.Add($"dnd:{enabled}");
    }

    /// <summary>Se la segnalazione risulta cifrata. Scrivibile: i due casi vanno collaudati entrambi.</summary>
    public bool IsSignallingEncrypted { get; set; }

    /// <summary>Se la registrazione deve riuscire. Il rifiuto va collaudato quanto la riuscita.</summary>
    public bool CanRecord { get; set; } = true;

    public string? RecordingPath { get; private set; }

    public List<string> RemoteAnnouncements { get; } = [];

    public bool StartCallRecording(string callId, string path)
    {
        if (!CanRecord) return false;

        RecordingPath = path;
        Operations.Add($"record-start:{callId}");
        return true;
    }

    public (TimeSpan Duration, Task<string> File)? StopCallRecording(string callId)
    {
        if (RecordingPath is null) return null;

        var path = RecordingPath;
        RecordingPath = null;
        Operations.Add($"record-stop:{callId}");

        return (TimeSpan.FromSeconds(42), Task.FromResult(path));
    }

    public void PlayToRemote(string callId, short[] pcm, int sampleRate)
        => RemoteAnnouncements.Add($"{callId}:{pcm.Length}@{sampleRate}");

    /// <summary>Pacchetti audio per chiamata, scritti dai collaudi.</summary>
    public Dictionary<string, MediaFlowSnapshot> MediaFlows { get; } = [];

    public MediaFlowSnapshot? GetMediaFlow(string callId)
        => MediaFlows.TryGetValue(callId, out var flow) ? flow : null;

    // -------- comandi di simulazione --------

    public string SimulateIncoming(string number, string? name = null, string? queue = null)
    {
        var id = $"in-{Interlocked.Increment(ref _counter)}";
        IncomingCall?.Invoke(this, new IncomingCallEventArgs(
            id, new CallParty(number, name), $"sip-{id}", queue, DateTimeOffset.Now));
        return id;
    }

    public void SimulateProgress(string callId, CallTrigger trigger, CallEndReason? reason = null)
        => CallProgress?.Invoke(this, new CallProgressEventArgs(
            callId, trigger, reason, "G722", true, false, DateTimeOffset.Now));

    /// <summary>
    /// Simula lo squillo della destinazione di una chiamata uscente.
    /// </summary>
    /// <param name="callId">Chiamata in corso di instaurazione.</param>
    /// <param name="hasEarlyMedia">
    /// <c>true</c> riproduce il comportamento di un numero esterno, il cui gestore
    /// manda il proprio tono di libero come audio in banda; <c>false</c> quello di
    /// un interno, che squilla senza mandare nulla.
    /// </param>
    public void SimulateRemoteRinging(string callId, bool hasEarlyMedia)
        => CallProgress?.Invoke(this, new CallProgressEventArgs(
            callId, CallTrigger.RemoteRinging, null, null, false, false, DateTimeOffset.Now, hasEarlyMedia));

    public void SimulateTransferOutcome(string callId, TransferOutcome outcome)
        => TransferProgress?.Invoke(this, new TransferProgressEventArgs(
            callId, outcome, null, DateTimeOffset.Now));

    public void SimulateRegistration(RegistrationStatus status, string? reason = null)
    {
        RegistrationState = new SipRegistrationState(status, "test", reason, DateTimeOffset.Now);
        RegistrationStateChanged?.Invoke(this, RegistrationState);
    }

    /// <summary>Indica se una certa operazione è stata richiesta al motore.</summary>
    public bool Did(string prefix) => Operations.Any(o => o.StartsWith(prefix, StringComparison.Ordinal));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
