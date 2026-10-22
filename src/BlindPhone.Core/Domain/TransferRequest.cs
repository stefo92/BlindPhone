namespace BlindPhone.Core.Domain;

/// <summary>
/// Contesto di una procedura di trasferimento in corso.
/// </summary>
/// <remarks>
/// Tiene insieme le tre informazioni che servono a ogni annuncio della procedura:
/// chi stiamo trasferendo, verso chi, e a che punto siamo. Senza questo contesto
/// gli annunci diventerebbero generici ("trasferimento completato") proprio nel
/// momento in cui l'operatore ha più bisogno di sapere di quale chiamata si parla.
/// </remarks>
public sealed class TransferSession
{
    /// <summary>Crea una sessione di trasferimento nella fase di inserimento destinazione.</summary>
    /// <param name="originalCall">Chiamata da trasferire, gia' messa in attesa.</param>
    /// <param name="startedAt">Istante di avvio.</param>
    public TransferSession(Call originalCall, DateTimeOffset startedAt)
    {
        OriginalCall = originalCall;
        StartedAt = startedAt;
        Phase = TransferPhase.EnteringDestination;
    }

    /// <summary>Chiamata che si sta trasferendo.</summary>
    public Call OriginalCall { get; }

    /// <summary>Chiamata di consultazione verso la destinazione, se avviata.</summary>
    public Call? ConsultationCall { get; private set; }

    /// <summary>Fase corrente della procedura.</summary>
    public TransferPhase Phase { get; private set; }

    /// <summary>Destinazione inserita dall'operatore.</summary>
    public string Destination { get; private set; } = string.Empty;

    /// <summary>Nome della destinazione risolto dalla rubrica, se disponibile.</summary>
    public CallParty? ResolvedDestination { get; private set; }

    /// <summary>Modalita' con cui il trasferimento e' stato eseguito, nota solo a esito raggiunto.</summary>
    public TransferMode? Mode { get; private set; }

    /// <summary>Esito, valorizzato quando la procedura si chiude.</summary>
    public TransferOutcome? Outcome { get; private set; }

    /// <summary>Istante di avvio della procedura.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Istante in cui la consultazione e' stata avviata.</summary>
    public DateTimeOffset? ConsultationStartedAt { get; private set; }

    /// <summary>Indica se la procedura si e' chiusa, con qualunque esito.</summary>
    public bool IsFinished => Phase is TransferPhase.Completed or TransferPhase.Aborted;

    /// <summary>Aggiorna la destinazione mentre l'operatore la digita o la sceglie in rubrica.</summary>
    /// <param name="destination">Numero o interno di destinazione.</param>
    /// <param name="resolved">Contatto risolto, se la rubrica ha trovato una corrispondenza.</param>
    public void SetDestination(string destination, CallParty? resolved = null)
    {
        Destination = destination;
        ResolvedDestination = resolved;
    }

    /// <summary>Registra l'avvio della consultazione.</summary>
    /// <param name="consultationCall">Chiamata verso la destinazione.</param>
    /// <param name="at">Istante di avvio.</param>
    public void BeginConsultation(Call consultationCall, DateTimeOffset at)
    {
        ConsultationCall = consultationCall;
        ConsultationStartedAt = at;
        Phase = TransferPhase.Consulting;
    }

    /// <summary>Registra l'invio del comando di trasferimento al centralino.</summary>
    /// <param name="mode">Modalita' con cui il trasferimento e' stato inviato.</param>
    public void BeginCompletion(TransferMode mode)
    {
        Mode = mode;
        Phase = TransferPhase.Completing;
    }

    /// <summary>Chiude la procedura con esito positivo.</summary>
    public void MarkSucceeded()
    {
        Outcome = TransferOutcome.Succeeded;
        Phase = TransferPhase.Completed;
    }

    /// <summary>Chiude la procedura con esito negativo o per annullamento.</summary>
    /// <param name="outcome">Motivo della chiusura.</param>
    public void MarkAborted(TransferOutcome outcome)
    {
        Outcome = outcome;
        Phase = TransferPhase.Aborted;
    }

    /// <summary>
    /// Nome o numero della destinazione da usare negli annunci.
    /// </summary>
    /// <remarks>
    /// Il numero resta come è stato digitato. Gli annunci che chiudono il trasferimento,
    /// quando non c'è un nome, lo fanno leggere cifra per cifra: vedi
    /// <c>TransferOrchestrator.SpokenDestination</c>.
    /// </remarks>
    public string DestinationSpeech => ResolvedDestination?.SpeechName ?? Destination;
}
