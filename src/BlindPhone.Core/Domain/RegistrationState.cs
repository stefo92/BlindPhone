namespace BlindPhone.Core.Domain;

/// <summary>Stato della registrazione SIP presso il registrar.</summary>
public enum RegistrationStatus
{
    /// <summary>Non registrato e non in tentativo.</summary>
    Unregistered,

    /// <summary>Tentativo di registrazione in corso.</summary>
    Registering,

    /// <summary>Registrato: il posto operatore è raggiungibile.</summary>
    Registered,

    /// <summary>Registrazione fallita.</summary>
    Failed,
}

/// <summary>
/// Stato della registrazione con il motivo dell'eventuale fallimento.
/// </summary>
/// <param name="Status">Stato corrente.</param>
/// <param name="AccountName">Nome dell'account SIP a cui lo stato si riferisce.</param>
/// <param name="FailureReason">
/// Motivo del fallimento in linguaggio naturale, non il codice SIP.
/// </param>
/// <param name="ChangedAt">Istante dell'ultimo cambio di stato.</param>
/// <remarks>
/// La perdita di registrazione è uno dei pochi eventi che giustificano un allarme
/// sonoro ripetuto: significa che il posto operatore non è più raggiungibile e le
/// chiamate non arrivano. L'operatore non vedente non ha modo di accorgersene
/// dall'assenza di squilli, perché il silenzio è indistinguibile da un momento di
/// calma (ANALISI §16.1, M5).
/// </remarks>
public sealed record SipRegistrationState(
    RegistrationStatus Status,
    string AccountName,
    string? FailureReason = null,
    DateTimeOffset? ChangedAt = null)
{
    /// <summary>Indica se il posto operatore è attualmente raggiungibile.</summary>
    public bool IsReachable => Status == RegistrationStatus.Registered;

    /// <summary>
    /// Dice se due stati raccontano la stessa situazione, a meno dell'istante.
    /// </summary>
    /// <param name="other">Stato con cui confrontarsi.</param>
    /// <remarks>
    /// <para>
    /// <b>Serve a distinguere un cambiamento da una conferma.</b> La registrazione SIP
    /// si rinnova da sé ogni dieci minuti circa, e ogni rinnovo è una sequenza
    /// completa — <c>REGISTER</c>, <c>401</c>, <c>REGISTER</c> autenticato,
    /// <c>200 OK</c> — che produce una notifica di stato identica alla precedente.
    /// Trattarla come un cambiamento significa annunciare «telefono registrato»
    /// trentasette volte al giorno, e siccome il segnale acustico precede la voce ed è
    /// immune alla soppressione durante la conversazione, l'operatore lo sente anche
    /// mentre parla, senza la frase che lo spiega.
    /// </para>
    /// <para>
    /// L'istante è escluso dal confronto di proposito: <see cref="ChangedAt"/> dice
    /// quando lo stato è <b>cambiato</b>, e un rinnovo che non cambia niente non deve
    /// spostarlo in avanti.
    /// </para>
    /// <para>
    /// Il nome dell'account invece conta: passando da un account all'altro lo stato è
    /// «registrato» prima e dopo, ma è un'altra postazione telefonica e l'operatore
    /// deve sentirselo dire.
    /// </para>
    /// </remarks>
    public bool RepresentsSameStateAs(SipRegistrationState? other)
        => other is not null
           && Status == other.Status
           && string.Equals(AccountName, other.AccountName, StringComparison.Ordinal)
           && string.Equals(FailureReason, other.FailureReason, StringComparison.Ordinal);
}
