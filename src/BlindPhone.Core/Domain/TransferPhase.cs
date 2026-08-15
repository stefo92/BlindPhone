namespace BlindPhone.Core.Domain;

/// <summary>
/// Fasi della procedura di trasferimento a combinazione unica descritta in
/// ANALISI-BLINDPHONE.md §11.
/// </summary>
/// <remarks>
/// <para>
/// Il requisito di capitolato è che una sola combinazione di tasti governi sia il
/// trasferimento cieco sia quello con annuncio. La soluzione è una macchina a stati
/// progressiva in cui lo stesso tasto assume un significato diverso a seconda della
/// fase, <b>sempre annunciata prima dell'uso</b>. La distinzione fra le due modalità
/// nasce da un solo elemento aggiuntivo: pressione singola contro doppia pressione
/// rapida nel momento della conferma della destinazione.
/// </para>
/// <para>
/// Il punto delicato non è la macchina a stati ma il fatto che l'operatore sappia
/// sempre in quale fase si trova. Per questo ogni ingresso in una fase produce un
/// annuncio che dice cosa fa il tasto adesso, e non solo cosa è appena successo.
/// </para>
/// </remarks>
public enum TransferPhase
{
    /// <summary>Nessun trasferimento in corso.</summary>
    Idle = 0,

    /// <summary>
    /// L'operatore sta inserendo la destinazione. Il chiamante è già in attesa.
    /// In questa fase il tasto singolo avvia la consultazione, il doppio esegue il
    /// trasferimento cieco.
    /// </summary>
    EnteringDestination = 1,

    /// <summary>
    /// Consultazione in corso verso la destinazione: sta squillando oppure
    /// l'operatore ci sta parlando. Il tasto completa il trasferimento.
    /// </summary>
    Consulting = 2,

    /// <summary>
    /// Trasferimento inviato al centralino, in attesa dell'esito.
    /// </summary>
    Completing = 3,

    /// <summary>Trasferimento riuscito. Fase finale.</summary>
    Completed = 4,

    /// <summary>
    /// Trasferimento fallito o annullato. Fase finale: da qui la chiamata originale
    /// torna sotto il controllo dell'operatore.
    /// </summary>
    Aborted = 5,
}

/// <summary>Modalità con cui il trasferimento è stato eseguito.</summary>
public enum TransferMode
{
    /// <summary>
    /// Trasferimento cieco: REFER immediato, senza consultare la destinazione.
    /// L'operatore è libero subito.
    /// </summary>
    Blind,

    /// <summary>
    /// Trasferimento con annuncio: l'operatore parla prima con la destinazione,
    /// poi collega le due parti (REFER con header Replaces).
    /// </summary>
    Attended,

    /// <summary>
    /// Trasferimento completato mentre la destinazione stava ancora squillando.
    /// Comportamento standard dei centralini, a metà fra le due modalità.
    /// </summary>
    SemiAttended,
}

/// <summary>Esito di una procedura di trasferimento.</summary>
public enum TransferOutcome
{
    /// <summary>Trasferimento completato con successo.</summary>
    Succeeded,

    /// <summary>Annullato dall'operatore con ESC.</summary>
    CancelledByOperator,

    /// <summary>La destinazione risultava occupata.</summary>
    DestinationBusy,

    /// <summary>La destinazione non ha risposto entro il tempo previsto.</summary>
    DestinationNoAnswer,

    /// <summary>La destinazione ha rifiutato.</summary>
    DestinationRejected,

    /// <summary>Il centralino ha rifiutato la richiesta di trasferimento.</summary>
    RejectedByPbx,

    /// <summary>Il chiamante originale ha riagganciato durante la procedura.</summary>
    OriginalCallerHungUp,

    /// <summary>Guasto tecnico.</summary>
    Failed,
}
