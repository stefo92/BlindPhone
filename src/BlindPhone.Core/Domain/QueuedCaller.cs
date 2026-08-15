namespace BlindPhone.Core.Domain;

/// <summary>
/// Un chiamante in attesa in coda.
/// </summary>
/// <param name="Id">
/// Identificativo lato PBX della chiamata in coda. Per Yeastar è il <c>call_id</c>
/// restituito da <c>queue/call_status</c>.
/// </param>
/// <param name="QueueName">Nome della coda in cui il chiamante attende.</param>
/// <param name="CallerNumber">Numero del chiamante, come fornito dal PBX.</param>
/// <param name="CallerName">Nome, se fornito dal PBX o risolto dalla rubrica.</param>
/// <param name="WaitingSince">Istante di ingresso in coda.</param>
/// <param name="Position">
/// Posizione in coda a partire da 1, oppure <c>null</c> se il provider non è in
/// grado di garantirla.
/// </param>
/// <param name="Priority">Priorità, dove valori più alti indicano precedenza.</param>
/// <param name="IsVip">Contrassegno VIP proveniente dalla rubrica.</param>
/// <param name="IsEmergency">Contrassegno di emergenza.</param>
/// <remarks>
/// <para>
/// <see cref="Position"/> è nullable per una ragione precisa: la documentazione
/// Yeastar non garantisce che l'ordine di <c>waiting_list</c> corrisponda
/// all'ordine di servizio della coda (YEASTAR-INTEGRAZIONE.md §5.1, punto da
/// verificare su PBX reale). Finché la verifica non è fatta, il provider lascia il
/// campo vuoto e l'interfaccia non annuncia una posizione che potrebbe essere
/// falsa. Annunciare "lei è il primo" a chi è il terzo è peggio che non dire nulla.
/// </para>
/// </remarks>
public sealed record QueuedCaller(
    string Id,
    string QueueName,
    string? CallerNumber,
    string? CallerName,
    DateTimeOffset WaitingSince,
    int? Position = null,
    int Priority = 0,
    bool IsVip = false,
    bool IsEmergency = false)
{
    /// <summary>Tempo di attesa maturato alla data indicata.</summary>
    /// <param name="now">Istante corrente.</param>
    public TimeSpan WaitDuration(DateTimeOffset now)
    {
        var elapsed = now - WaitingSince;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>
    /// Testo da pronunciare per identificare il chiamante: il nome se disponibile,
    /// altrimenti il numero.
    /// </summary>
    public string? SpeechIdentity => string.IsNullOrWhiteSpace(CallerName) ? CallerNumber : CallerName;
}
