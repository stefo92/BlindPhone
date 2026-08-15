namespace BlindPhone.Core.Domain;

/// <summary>
/// Macchina a stati della chiamata. Definisce quali transizioni sono lecite e
/// espone la tabella completa perché possa essere verificata dai test.
/// </summary>
/// <remarks>
/// <para>
/// La macchina è dichiarativa e senza stato proprio: <see cref="Transitions"/> è
/// una tabella statica, e <see cref="TryGetTarget"/> è una funzione pura. Lo stato
/// vive nell'entità <see cref="Call"/>, la logica applicativa in
/// <c>CallSessionManager</c>.
/// </para>
/// <para>
/// Vantaggio pratico: la tabella si legge come una specifica ed è confrontabile
/// con il diagramma di ANALISI-BLINDPHONE.md §5.3 senza dover seguire il flusso
/// del codice.
/// </para>
/// </remarks>
public static class CallStateMachine
{
    private static readonly CallTransition[] TransitionTable =
    [
        // Instaurazione
        new(CallState.Idle,     CallTrigger.IncomingCall,      CallState.Alerting),
        new(CallState.Idle,     CallTrigger.OutgoingCall,      CallState.Calling),

        // Risposta
        new(CallState.Alerting, CallTrigger.Answer,            CallState.Active),
        new(CallState.Calling,  CallTrigger.RemoteAnswered,    CallState.Active),

        // Chiusura durante l'instaurazione
        new(CallState.Alerting, CallTrigger.HangUp,            CallState.Terminated),
        new(CallState.Alerting, CallTrigger.RemoteHangUp,      CallState.Terminated),
        new(CallState.Calling,  CallTrigger.HangUp,            CallState.Terminated),
        new(CallState.Calling,  CallTrigger.RemoteRejected,    CallState.Terminated),
        new(CallState.Calling,  CallTrigger.RemoteHangUp,      CallState.Terminated),

        // Attesa e ripresa
        new(CallState.Active,   CallTrigger.Hold,              CallState.Held),
        new(CallState.Held,     CallTrigger.Resume,            CallState.Active),

        // Trasferimento: si puo' partire sia da conversazione sia da attesa, perche'
        // l'operatore puo' aver gia' messo in attesa il chiamante prima di decidere.
        new(CallState.Active,   CallTrigger.TransferStarted,   CallState.Transferring),
        new(CallState.Held,     CallTrigger.TransferStarted,   CallState.Transferring),
        new(CallState.Transferring, CallTrigger.TransferCompleted, CallState.Terminated),

        // ALTERNANZA DURANTE IL TRASFERIMENTO CON ANNUNCIO.
        //
        // Il chiamante parcheggiato torna in linea senza che la procedura si chiuda:
        // l'operatore ci parla — "sto provando, resti in linea" — e poi torna
        // dall'interno, che nel frattempo passa in attesa come una chiamata qualunque.
        // Da li' si riparte con TransferStarted, che da Held e' gia' in tabella.
        //
        // Senza questa riga il tasto di alternanza non trovava nulla da alternare:
        // durante il trasferimento il chiamante non e' ne' attivo ne' in attesa, e chi
        // cercava una chiamata da riprendere si sentiva rispondere che non ce n'erano.
        new(CallState.Transferring, CallTrigger.Resume,            CallState.Active),

        // Annullamento del trasferimento: la chiamata torna ATTIVA, non in attesa.
        // E' una scelta deliberata: dopo un annullamento l'operatore si aspetta di
        // stare di nuovo parlando con il chiamante, non di doverlo riprendere con un
        // secondo comando. Vedi ANALISI 11.4.
        new(CallState.Transferring, CallTrigger.TransferAborted, CallState.Active),
        new(CallState.Transferring, CallTrigger.RemoteHangUp,    CallState.Terminated),
        new(CallState.Transferring, CallTrigger.HangUp,          CallState.Terminated),

        // Conferenza a tre. Si entra sia dalla conversazione sia dall'attesa, perche'
        // le due gambe stanno per definizione una in linea e una in attesa nel momento
        // in cui l'operatore preme il tasto.
        new(CallState.Active,   CallTrigger.ConferenceJoined,  CallState.Conferenced),
        new(CallState.Held,     CallTrigger.ConferenceJoined,  CallState.Conferenced),

        // Uscita dalla conferenza: una gamba torna in conversazione, l'altra in
        // attesa. Nessuna delle due viene chiusa — vedi CallTrigger.ConferenceLeft.
        new(CallState.Conferenced, CallTrigger.ConferenceLeft, CallState.Active),
        new(CallState.Conferenced, CallTrigger.Hold,           CallState.Held),

        new(CallState.Conferenced, CallTrigger.HangUp,         CallState.Terminated),
        new(CallState.Conferenced, CallTrigger.RemoteHangUp,   CallState.Terminated),

        // Chiusura da conversazione o attesa
        new(CallState.Active,   CallTrigger.HangUp,            CallState.Terminated),
        new(CallState.Active,   CallTrigger.RemoteHangUp,      CallState.Terminated),
        new(CallState.Held,     CallTrigger.HangUp,            CallState.Terminated),
        new(CallState.Held,     CallTrigger.RemoteHangUp,      CallState.Terminated),

        // Guasti: da qualunque stato operativo si finisce terminati.
        new(CallState.Alerting, CallTrigger.Failed,            CallState.Terminated),
        new(CallState.Calling,  CallTrigger.Failed,            CallState.Terminated),
        new(CallState.Active,   CallTrigger.Failed,            CallState.Terminated),
        new(CallState.Held,     CallTrigger.Failed,            CallState.Terminated),
        new(CallState.Transferring, CallTrigger.Failed,        CallState.Terminated),
        new(CallState.Conferenced,  CallTrigger.Failed,        CallState.Terminated),
    ];

    /// <summary>
    /// Tutte le transizioni lecite. Sorgente di verità sia per l'esecuzione sia per
    /// i test di copertura degli annunci.
    /// </summary>
    public static IReadOnlyList<CallTransition> Transitions => TransitionTable;

    /// <summary>
    /// Determina lo stato di arrivo per una coppia stato/evento.
    /// </summary>
    /// <param name="from">Stato corrente.</param>
    /// <param name="trigger">Evento ricevuto.</param>
    /// <param name="to">Stato di arrivo, valorizzato solo se la transizione è lecita.</param>
    /// <returns><c>true</c> se la transizione esiste in tabella.</returns>
    /// <remarks>
    /// Restituire <c>false</c> invece di sollevare un'eccezione è deliberato: un
    /// evento fuori sequenza dalla rete (un BYE duplicato, un 486 dopo un 200 OK)
    /// non deve far cadere l'applicazione. Chi chiama tratta il <c>false</c> come
    /// "evento ignorato" e lo registra a log.
    /// </remarks>
    public static bool TryGetTarget(CallState from, CallTrigger trigger, out CallState to)
    {
        foreach (var t in TransitionTable)
        {
            if (t.From == from && t.Trigger == trigger)
            {
                to = t.To;
                return true;
            }
        }

        to = from;
        return false;
    }

    /// <summary>
    /// Indica se lo stato è terminale, cioè se la chiamata non può più cambiare stato.
    /// </summary>
    /// <param name="state">Stato da valutare.</param>
    public static bool IsTerminal(CallState state) => state == CallState.Terminated;

    /// <summary>
    /// Indica se in questo stato esiste un percorso audio attivo con la controparte.
    /// </summary>
    /// <param name="state">Stato da valutare.</param>
    /// <remarks>
    /// Serve alla regola di soppressione degli annunci a bassa priorità durante la
    /// conversazione (principio P4 dell'analisi): mentre l'operatore parla con
    /// qualcuno, il sistema tace e comunica per earcon. La conferenza conta: lì
    /// l'operatore sta parlando con due persone invece che con una, quindi la ragione
    /// per tacere vale doppio.
    /// </remarks>
    public static bool HasActiveAudio(CallState state)
        => state is CallState.Active or CallState.Conferenced;
}
