namespace BlindPhone.Core.Domain;

/// <summary>
/// Eventi che possono far cambiare stato a una chiamata.
/// </summary>
/// <remarks>
/// I trigger si dividono in due famiglie, e la distinzione conta per gli annunci:
/// quelli avviati dall'operatore (<see cref="Answer"/>, <see cref="HangUp"/>, ...)
/// e quelli subiti (<see cref="RemoteHangUp"/>, <see cref="RemoteRejected"/>, ...).
/// Un evento subito è sempre più importante da annunciare di uno provocato, perché
/// l'operatore non se lo aspetta: chi preme un tasto sa cosa ha chiesto, chi
/// riceve un BYE no.
/// </remarks>
public enum CallTrigger
{
    /// <summary>Arrivo di una chiamata entrante (INVITE ricevuto).</summary>
    IncomingCall,

    /// <summary>Avvio di una chiamata uscente da parte dell'operatore.</summary>
    OutgoingCall,

    /// <summary>L'operatore risponde alla chiamata entrante.</summary>
    Answer,

    /// <summary>
    /// La destinazione di una chiamata uscente ha iniziato a squillare.
    /// </summary>
    /// <remarks>
    /// <b>Non cambia stato</b>, e infatti non compare in
    /// <see cref="CallStateMachine"/>: la chiamata resta in
    /// <see cref="CallState.Calling"/>. Esiste per trasportare un'informazione che
    /// serve solo all'audio — se il tono di libero lo manda la rete o se va generato
    /// localmente — senza inventare uno stato che non esiste e senza obbligare a un
    /// annuncio parlato che si sovrapporrebbe al tono stesso.
    /// </remarks>
    RemoteRinging,

    /// <summary>Il chiamato remoto ha risposto alla nostra chiamata uscente.</summary>
    RemoteAnswered,

    /// <summary>L'operatore riaggancia o rifiuta.</summary>
    HangUp,

    /// <summary>La controparte ha riagganciato (BYE ricevuto).</summary>
    RemoteHangUp,

    /// <summary>La controparte ha rifiutato la chiamata o risulta occupata.</summary>
    RemoteRejected,

    /// <summary>L'operatore mette la chiamata in attesa.</summary>
    Hold,

    /// <summary>L'operatore riprende la chiamata dall'attesa.</summary>
    Resume,

    /// <summary>Inizio di una procedura di trasferimento su questa chiamata.</summary>
    TransferStarted,

    /// <summary>Trasferimento completato con successo: la chiamata esce dal nostro controllo.</summary>
    TransferCompleted,

    /// <summary>
    /// Trasferimento annullato o fallito: la chiamata torna sotto il controllo
    /// dell'operatore. È il caso che rende obbligatorio il recupero automatico
    /// (ANALISI §11.4): senza, il chiamante resterebbe appeso.
    /// </summary>
    TransferAborted,

    /// <summary>La chiamata entra in conferenza con l'altra linea.</summary>
    /// <remarks>
    /// Si applica a entrambe le linee coinvolte nello stesso istante: una conferenza
    /// in cui una gamba sia entrata e l'altra no non esiste come stato intermedio, ed
    /// è la ragione per cui le due transizioni non producono annunci propri ma uno
    /// solo, complessivo, prodotto da <c>CallSessionManager</c>.
    /// </remarks>
    ConferenceJoined,

    /// <summary>
    /// La chiamata esce dalla conferenza e torna a essere una conversazione a due.
    /// </summary>
    /// <remarks>
    /// Sciogliere la conferenza <b>non</b> chiude le chiamate: una gamba resta in
    /// conversazione e l'altra va in attesa, che è lo stato da cui la conferenza era
    /// partita. Chiudere entrambe le linee su un tasto che l'operatore preme per
    /// «tornare a parlare con uno solo» sarebbe distruttivo e non recuperabile.
    /// </remarks>
    ConferenceLeft,

    /// <summary>Perdita della chiamata per guasto di rete o del media.</summary>
    Failed,
}
