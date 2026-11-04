namespace BlindPhone.Core.Domain;

/// <summary>Verso di un messaggio istantaneo, visto da questa postazione.</summary>
public enum InstantMessageDirection
{
    /// <summary>Scritto da un collega e arrivato qui.</summary>
    Incoming,

    /// <summary>Scritto dall'operatore e mandato a un collega.</summary>
    Outgoing,
}

/// <summary>A che punto è la consegna di un messaggio mandato dall'operatore.</summary>
/// <remarks>
/// <para>
/// <b>Tre stati e non due</b>, e il terzo è quello che conta. Fra l'Invio e la risposta
/// del centralino passa un tempo breve ma non nullo, e su una rete lenta anche dieci
/// secondi: un messaggio che in quel tempo risultasse già consegnato direbbe una cosa
/// che non si sa ancora, uno che risultasse non consegnato farebbe premere Invio una
/// seconda volta.
/// </para>
/// <para>
/// <b>«Consegnato» vuol dire accettato da chi doveva riceverlo, non letto.</b> La
/// risposta 200 al <c>MESSAGE</c> arriva dal telefono del collega o dal centralino che
/// lo custodisce per lui; nessuno dei due sa se qualcuno l'abbia guardato. Il testo
/// parlato dice «consegnato», mai «letto».
/// </para>
/// </remarks>
public enum InstantMessageDelivery
{
    /// <summary>Mandato, risposta non ancora arrivata.</summary>
    Sending,

    /// <summary>Accettato dalla destinazione.</summary>
    Delivered,

    /// <summary>Rifiutato: chi doveva riceverlo, o il centralino, ha risposto di no.</summary>
    Failed,

    /// <summary>
    /// Nessuna conferma: il centralino non ha risposto, oppure ha risposto che a valle
    /// non ha risposto nessuno (<c>408</c>).
    /// </summary>
    /// <remarks>
    /// Non è un rifiuto e non va detto come tale. Dire «non consegnato» sarebbe
    /// affermare una cosa che non si sa; la causa più comune però è nota, ed è un
    /// centralino che non inoltra i messaggi fra gli interni: su un Yeastar dietro il
    /// suo SBC il <c>MESSAGE</c> resta senza risposta per settanta secondi, poi l'SBC
    /// risponde <c>408</c>.
    /// </remarks>
    Unconfirmed,
}

/// <summary>Un messaggio della conversazione con un collega.</summary>
/// <param name="Id">Identificativo locale, unico per tutta la sessione.</param>
/// <param name="Peer">Interno o numero del collega, in cifre: è la chiave della conversazione.</param>
/// <param name="Text">Testo del messaggio, così come scritto.</param>
/// <param name="Direction">Chi l'ha scritto.</param>
/// <param name="At">Istante in cui è stato mandato o ricevuto.</param>
/// <param name="Delivery">
/// Stato della consegna. Significativo solo per i messaggi in uscita: quelli in arrivo
/// sono consegnati per definizione, e lo dichiarano.
/// </param>
/// <param name="FailureReason">
/// Perché la consegna non è riuscita, in italiano e già pronto da pronunciare; <c>null</c>
/// se è riuscita o è ancora in corso.
/// </param>
public sealed record InstantMessage(
    string Id,
    string Peer,
    string Text,
    InstantMessageDirection Direction,
    DateTimeOffset At,
    InstantMessageDelivery Delivery = InstantMessageDelivery.Delivered,
    string? FailureReason = null)
{
    /// <summary>Se l'ha scritto l'operatore.</summary>
    public bool IsOutgoing => Direction == InstantMessageDirection.Outgoing;
}

/// <summary>Esito dell'invio di un messaggio istantaneo, come lo riporta il motore.</summary>
/// <param name="Delivered">Se la destinazione l'ha accettato.</param>
/// <param name="FailureReason">
/// Perché non è stato accettato, già tradotto in italiano: «l'interno non esiste» e non
/// «404 Not Found». <c>null</c> se è stato accettato.
/// </param>
/// <param name="Unconfirmed">
/// Se nessuno ha confermato né rifiutato: il messaggio è partito, ma il centralino non
/// ha risposto, o ha risposto <c>408</c>. Vedi <see cref="InstantMessageDelivery.Unconfirmed"/>.
/// </param>
public sealed record InstantMessageSendResult(bool Delivered, string? FailureReason = null, bool Unconfirmed = false)
{
    /// <summary>Consegna riuscita.</summary>
    public static InstantMessageSendResult Success { get; } = new(true);

    /// <summary>Consegna non riuscita, con il motivo da dire all'operatore.</summary>
    /// <param name="reason">Motivo, in italiano.</param>
    public static InstantMessageSendResult Failure(string reason) => new(false, reason);

    /// <summary>Messaggio partito, ma nessuna conferma dal centralino.</summary>
    /// <param name="reason">Motivo, in italiano.</param>
    public static InstantMessageSendResult NotConfirmed(string reason) => new(false, reason, Unconfirmed: true);
}
