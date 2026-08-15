namespace BlindPhone.Core.Domain;

/// <summary>
/// Una chiamata sul posto operatore, con il suo stato e la sua storia temporale.
/// </summary>
/// <remarks>
/// <para>
/// L'entità è mutabile e la sua mutazione passa esclusivamente da
/// <see cref="TryApply"/>: non esistono setter pubblici sullo stato. Questo rende
/// impossibile portare una chiamata in uno stato incoerente scrivendoci sopra da
/// un punto qualsiasi del codice, cosa che in un applicativo telefonico produce i
/// difetti più difficili da riprodurre.
/// </para>
/// <para>
/// I timestamp sono conservati tutti perché servono agli annunci a richiesta:
/// "da quanto sono in linea", "da quanto è in attesa", "quanto ha aspettato".
/// </para>
/// </remarks>
public sealed class Call
{
    private readonly List<CallTransition> _history = [];

    /// <summary>
    /// Crea una chiamata nello stato <see cref="CallState.Idle"/>.
    /// </summary>
    /// <param name="id">Identificativo locale, univoco per sessione applicativa.</param>
    /// <param name="direction">Verso della chiamata.</param>
    /// <param name="remoteParty">Controparte.</param>
    /// <param name="createdAt">Istante di creazione.</param>
    public Call(string id, CallDirection direction, CallParty remoteParty, DateTimeOffset createdAt)
    {
        Id = id;
        Direction = direction;
        RemoteParty = remoteParty;
        CreatedAt = createdAt;
        State = CallState.Idle;
    }

    /// <summary>Identificativo locale della chiamata.</summary>
    public string Id { get; }

    /// <summary>Verso della chiamata.</summary>
    public CallDirection Direction { get; }

    /// <summary>Stato corrente.</summary>
    public CallState State { get; private set; }

    /// <summary>Controparte. Può essere aggiornata quando la rubrica risolve il nome.</summary>
    public CallParty RemoteParty { get; private set; }

    /// <summary>
    /// Identificativo di dialogo dello stack SIP, se la chiamata è già instaurata.
    /// </summary>
    public string? SipCallId { get; private set; }

    /// <summary>
    /// Identificativo della stessa chiamata lato PBX (per Yeastar, il <c>call_id</c>
    /// dell'Open API), quando la correlazione è riuscita.
    /// </summary>
    /// <remarks>
    /// Resta <c>null</c> se la correlazione è ambigua. Non è un difetto: è la
    /// scelta di degrado sicuro descritta in YEASTAR-INTEGRAZIONE.md §9. Per un
    /// operatore che non può verificare a schermo, un'informazione sbagliata è
    /// peggio di un'informazione mancante, quindi in caso di dubbio si lascia
    /// vuoto e l'annuncio omette il riferimento specifico.
    /// </remarks>
    public string? PbxCallId { get; private set; }

    /// <summary>Nome della coda da cui proviene la chiamata, se pertinente.</summary>
    public string? QueueName { get; private set; }

    /// <summary>Istante di creazione dell'oggetto chiamata.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Istante della risposta, se la chiamata è stata risposta.</summary>
    public DateTimeOffset? AnsweredAt { get; private set; }

    /// <summary>Istante dell'ultima messa in attesa.</summary>
    public DateTimeOffset? HeldAt { get; private set; }

    /// <summary>Istante di chiusura.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Motivo di chiusura, valorizzato quando la chiamata termina.</summary>
    public CallEndReason? EndReason { get; private set; }

    /// <summary>
    /// <c>true</c> se il codec negoziato è a banda larga (G.722, Opus).
    /// </summary>
    /// <remarks>
    /// Esposto all'operatore tramite il comando "Qualità linea". Non è una curiosità
    /// tecnica: EN 301 549 §6.1 richiede il supporto della banda larga per l'ICT con
    /// comunicazione vocale bidirezionale, e in collaudo va dimostrato che il
    /// requisito è soddisfatto sulla chiamata reale, non solo nella lista dei codec
    /// configurati.
    /// </remarks>
    public bool IsWideband { get; private set; }

    /// <summary>Nome del codec audio negoziato, per la diagnostica.</summary>
    public string? NegotiatedCodec { get; private set; }

    /// <summary><c>true</c> se il media è cifrato (SRTP).</summary>
    public bool IsMediaEncrypted { get; private set; }

    /// <summary>Storico delle transizioni subite, in ordine cronologico.</summary>
    public IReadOnlyList<CallTransition> History => _history;

    /// <summary>
    /// Applica un evento alla chiamata, se la transizione è lecita.
    /// </summary>
    /// <param name="trigger">Evento da applicare.</param>
    /// <param name="now">Istante corrente, iniettato per rendere i test deterministici.</param>
    /// <param name="transition">Transizione effettuata, valorizzata solo in caso di successo.</param>
    /// <returns>
    /// <c>true</c> se la transizione è avvenuta; <c>false</c> se l'evento non è
    /// lecito nello stato corrente, nel qual caso la chiamata resta invariata.
    /// </returns>
    public bool TryApply(CallTrigger trigger, DateTimeOffset now, out CallTransition transition)
    {
        if (!CallStateMachine.TryGetTarget(State, trigger, out var target))
        {
            transition = default;
            return false;
        }

        transition = new CallTransition(State, trigger, target);
        State = target;
        _history.Add(transition);

        UpdateTimestamps(trigger, target, now);
        return true;
    }

    private void UpdateTimestamps(CallTrigger trigger, CallState target, DateTimeOffset now)
    {
        switch (trigger)
        {
            case CallTrigger.Answer:
            case CallTrigger.RemoteAnswered:
                AnsweredAt ??= now;
                break;

            case CallTrigger.Hold:
                HeldAt = now;
                break;

            case CallTrigger.Resume:
            case CallTrigger.ConferenceJoined:
                // Entrare in conferenza toglie dall'attesa senza passare da Resume:
                // lasciando il timestamp, la stessa chiamata risulterebbe "in attesa
                // da tre minuti" nel momento in cui l'operatore ci sta parlando.
                HeldAt = null;
                break;
        }

        if (target == CallState.Terminated)
        {
            EndedAt = now;
            EndReason ??= trigger switch
            {
                CallTrigger.HangUp => CallEndReason.LocalHangUp,
                CallTrigger.RemoteHangUp => CallEndReason.RemoteHangUp,
                CallTrigger.RemoteRejected => CallEndReason.Rejected,
                CallTrigger.TransferCompleted => CallEndReason.Transferred,
                CallTrigger.Failed => CallEndReason.NetworkFailure,
                _ => CallEndReason.Unknown,
            };
        }
    }

    /// <summary>
    /// Aggiorna la controparte, tipicamente quando la rubrica risolve il nome di un
    /// numero fino a quel momento sconosciuto.
    /// </summary>
    /// <param name="party">Nuovi dati della controparte.</param>
    public void UpdateRemoteParty(CallParty party) => RemoteParty = party;

    /// <summary>Associa gli identificativi tecnici della chiamata.</summary>
    /// <param name="sipCallId">Call-ID SIP.</param>
    /// <param name="pbxCallId">Identificativo lato PBX, <c>null</c> se non correlato.</param>
    public void AttachIdentifiers(string? sipCallId, string? pbxCallId = null)
    {
        if (sipCallId is not null) SipCallId = sipCallId;
        if (pbxCallId is not null) PbxCallId = pbxCallId;
    }

    /// <summary>Indica da quale coda proviene la chiamata.</summary>
    /// <param name="queueName">Nome della coda.</param>
    public void AttachQueue(string? queueName) => QueueName = queueName;

    /// <summary>Registra le caratteristiche del media negoziato.</summary>
    /// <param name="codec">Nome del codec (es. "G722", "PCMA", "OPUS").</param>
    /// <param name="isWideband">Se il codec e' a banda larga.</param>
    /// <param name="isEncrypted">Se il flusso e' protetto da SRTP.</param>
    public void SetMediaProfile(string? codec, bool isWideband, bool isEncrypted)
    {
        NegotiatedCodec = codec;
        IsWideband = isWideband;
        IsMediaEncrypted = isEncrypted;
    }

    /// <summary>Imposta esplicitamente il motivo di chiusura.</summary>
    /// <param name="reason">Motivo, tradotto dall'infrastruttura in termini utente.</param>
    /// <remarks>
    /// Va invocato <b>prima</b> del <see cref="TryApply"/> che porta a
    /// <see cref="CallState.Terminated"/>, altrimenti prevale il motivo dedotto dal
    /// trigger, che è più generico.
    /// </remarks>
    public void SetEndReason(CallEndReason reason) => EndReason = reason;

    /// <summary>
    /// Durata della conversazione, cioè dal momento della risposta.
    /// </summary>
    /// <param name="now">Istante corrente.</param>
    /// <returns>Durata, o <see cref="TimeSpan.Zero"/> se la chiamata non è mai stata risposta.</returns>
    public TimeSpan TalkDuration(DateTimeOffset now)
        => AnsweredAt is null ? TimeSpan.Zero : (EndedAt ?? now) - AnsweredAt.Value;

    /// <summary>
    /// Da quanto tempo la chiamata è in attesa.
    /// </summary>
    /// <param name="now">Istante corrente.</param>
    /// <returns>Durata dell'attesa corrente, o <see cref="TimeSpan.Zero"/> se non è in attesa.</returns>
    /// <remarks>
    /// Alimenta il promemoria periodico di chiamata in attesa: un operatore non
    /// vedente non ha la spia luminosa della linea che lampeggia, quindi
    /// dimenticare qualcuno in attesa è un rischio concreto (ANALISI §16.1, M4).
    /// </remarks>
    public TimeSpan HoldDuration(DateTimeOffset now)
        => HeldAt is null ? TimeSpan.Zero : now - HeldAt.Value;
}
