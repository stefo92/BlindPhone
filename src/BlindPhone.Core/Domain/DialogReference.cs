namespace BlindPhone.Core.Domain;

/// <summary>
/// Identifica una conversazione già in corso fra altre due persone.
/// </summary>
/// <param name="Extension">Interno sorvegliato a cui la conversazione appartiene.</param>
/// <param name="CallId">Identificativo di chiamata SIP del dialogo.</param>
/// <param name="LocalTag">Etichetta dell'interno, cioè della parte che riceverà l'invito.</param>
/// <param name="RemoteTag">Etichetta dell'altra parte.</param>
/// <param name="ObservedAt">Quando il centralino ha comunicato questi dati.</param>
/// <remarks>
/// <para>
/// <b>Serve all'inclusione, e senza non si può fare.</b> Il meccanismo dell'RFC 3911
/// consiste nel chiedere di entrare in un dialogo <i>già esistente</i>, e per farlo
/// bisogna saperlo nominare: i tre valori qui sotto sono esattamente ciò che
/// l'intestazione <c>Join</c> deve trasportare. Il posto operatore non li può
/// inventare né dedurre, perché appartengono a una conversazione di cui non fa parte.
/// </para>
/// <para>
/// <b>Da dove arrivano.</b> Dalle notifiche del pacchetto di eventi <c>dialog</c>
/// (RFC 4235), le stesse con cui questo prodotto accende il «libero» e l'«occupato»
/// accanto ai nomi in rubrica. È la ragione per cui l'inclusione richiede la
/// sorveglianza dello stato degli interni: senza sottoscrizione non si conosce
/// nessun dialogo, e il comando non ha nulla su cui agire.
/// </para>
/// <para>
/// <b>Invecchia in fretta.</b> Descrive una conversazione che può essere finita un
/// istante dopo la notifica. Chi lo usa deve trattarlo come una fotografia e non come
/// un fatto: l'esito vero lo dà il centralino rispondendo all'invito.
/// </para>
/// </remarks>
public sealed record DialogReference(
    string Extension,
    string CallId,
    string LocalTag,
    string RemoteTag,
    DateTimeOffset ObservedAt)
{
    /// <summary>Indica se i tre identificativi ci sono tutti.</summary>
    /// <remarks>
    /// Una notifica può descrivere un dialogo in fase di instaurazione, dove
    /// l'etichetta della parte remota non esiste ancora. Includersi in un dialogo del
    /// genere non ha senso — non c'è ancora nessuna conversazione — e il centralino
    /// rifiuterebbe l'invito con un errore che all'operatore non spiegherebbe niente.
    /// </remarks>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(CallId)
        && !string.IsNullOrWhiteSpace(LocalTag)
        && !string.IsNullOrWhiteSpace(RemoteTag);

    /// <summary>
    /// Compone il valore dell'intestazione <c>Join</c> come lo prevede l'RFC 3911.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La forma è <c>identificativo;to-tag=…;from-tag=…</c>, e <b>l'ordine delle due
    /// etichette non è indifferente</b>: <c>to-tag</c> è quella della parte a cui
    /// l'invito viene mandato — l'interno — e <c>from-tag</c> quella dell'altra. Il
    /// pacchetto di eventi le chiama <c>local-tag</c> e <c>remote-tag</c> dal punto di
    /// vista dell'interno sorvegliato, che è appunto il destinatario: le due
    /// prospettive coincidono, ed è per questo che la traduzione è diretta.
    /// </para>
    /// <para>
    /// Scambiarle produrrebbe un rifiuto del centralino difficile da interpretare,
    /// perché la richiesta sarebbe formalmente valida e semplicemente non
    /// corrisponderebbe a nessun dialogo esistente.
    /// </para>
    /// </remarks>
    public string ToJoinHeaderValue() => $"{CallId};to-tag={LocalTag};from-tag={RemoteTag}";
}
