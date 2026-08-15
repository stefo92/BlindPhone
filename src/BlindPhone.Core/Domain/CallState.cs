namespace BlindPhone.Core.Domain;

/// <summary>
/// Stati possibili di una singola chiamata, secondo la macchina a stati
/// descritta in ANALISI-BLINDPHONE.md §5.3.
/// </summary>
/// <remarks>
/// <para>
/// Ogni transizione fra questi stati deve produrre un annuncio accessibile. Non è
/// una raccomandazione: <c>AnnouncementCoverageTests</c> enumera la tabella di
/// <see cref="CallStateMachine"/> e fallisce se anche una sola transizione risulta
/// muta. Il motivo è che un operatore non vedente non ha alcun modo di accorgersi
/// che lo stato è cambiato se non glielo si dice.
/// </para>
/// </remarks>
public enum CallState
{
    /// <summary>Nessuna chiamata su questa linea.</summary>
    Idle = 0,

    /// <summary>Chiamata entrante che sta squillando e non è ancora stata accettata.</summary>
    Alerting = 1,

    /// <summary>Chiamata uscente in corso di instaurazione (INVITE inviato, nessuna risposta finale).</summary>
    Calling = 2,

    /// <summary>Conversazione in corso con audio bidirezionale.</summary>
    Active = 3,

    /// <summary>Chiamata messa in attesa dall'operatore.</summary>
    Held = 4,

    /// <summary>
    /// Chiamata coinvolta in una procedura di trasferimento.
    /// Il dettaglio della fase è in <see cref="TransferPhase"/>, gestito da
    /// <c>TransferOrchestrator</c>: la chiamata sa solo di essere "in trasferimento".
    /// </summary>
    Transferring = 5,

    /// <summary>Chiamata terminata. Stato finale: la chiamata non torna più operativa.</summary>
    Terminated = 6,

    /// <summary>
    /// Chiamata unita alle altre in una conferenza a tre.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ha audio bidirezionale come <see cref="Active"/>, ma è uno stato distinto e
    /// non una variante: in conferenza le chiamate attive sono <b>due</b>, e con lo
    /// stesso stato di una conversazione normale il posto operatore non avrebbe modo
    /// di dire quale delle due è "quella in linea" — perché non lo è nessuna, lo sono
    /// entrambe. Distinguerle rende inoltre annunciabile l'ingresso e l'uscita dalla
    /// conferenza, che è il solo modo in cui un operatore non vedente può sapere in
    /// quale delle due modalità si trova.
    /// </para>
    /// </remarks>
    Conferenced = 7,
}
