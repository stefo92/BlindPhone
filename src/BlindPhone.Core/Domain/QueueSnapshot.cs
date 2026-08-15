namespace BlindPhone.Core.Domain;

/// <summary>
/// Fotografia dello stato di una o più code in un dato istante.
/// </summary>
/// <param name="Waiting">Chiamanti in attesa, nell'ordine fornito dal provider.</param>
/// <param name="RingingCount">Chiamate già distribuite a un agente e in squillo.</param>
/// <param name="ActiveCount">Chiamate di coda in conversazione.</param>
/// <param name="CapturedAt">Istante a cui la fotografia si riferisce.</param>
/// <param name="IsAuthoritative">
/// <c>true</c> se lo stato viene dalla fonte di verità (interrogazione diretta del
/// PBX); <c>false</c> se è una stima ricavata dagli eventi in tempo reale.
/// </param>
/// <remarks>
/// <para>
/// La distinzione fra stato autorevole e stimato serve al modello ibrido descritto
/// in YEASTAR-INTEGRAZIONE.md §5.3: gli eventi WebSocket aggiornano subito
/// (bassa latenza, ma si perdono alle disconnessioni), l'interrogazione periodica
/// riconcilia (lenta, ma vera). L'interfaccia usa lo stimato per annunciare in
/// fretta e l'autorevole per correggersi.
/// </para>
/// </remarks>
public sealed record QueueSnapshot(
    IReadOnlyList<QueuedCaller> Waiting,
    int RingingCount,
    int ActiveCount,
    DateTimeOffset CapturedAt,
    bool IsAuthoritative)
{
    /// <summary>Fotografia vuota, usata come stato iniziale.</summary>
    /// <param name="at">Istante di riferimento.</param>
    public static QueueSnapshot Empty(DateTimeOffset at) => new([], 0, 0, at, false);

    /// <summary>Numero di chiamanti in attesa. È il dato che risponde al tasto rapido di conteggio.</summary>
    public int WaitingCount => Waiting.Count;

    /// <summary>Attesa del chiamante che aspetta da più tempo.</summary>
    /// <param name="now">Istante corrente.</param>
    public TimeSpan LongestWait(DateTimeOffset now)
    {
        var longest = TimeSpan.Zero;
        foreach (var caller in Waiting)
        {
            var wait = caller.WaitDuration(now);
            if (wait > longest) longest = wait;
        }

        return longest;
    }

    /// <summary>
    /// Indica se il conteggio dei chiamanti in attesa differisce da quello di
    /// un'altra fotografia.
    /// </summary>
    /// <param name="other">Fotografia da confrontare.</param>
    /// <remarks>
    /// Usata dalla riconciliazione per decidere se correggere un annuncio già
    /// pronunciato. Si confronta solo il conteggio, non i secondi di attesa:
    /// correggere "in attesa da 1 minuto e 23" con "1 minuto e 24" confonde più di
    /// quanto informi.
    /// </remarks>
    public bool DiffersInCountFrom(QueueSnapshot other) => WaitingCount != other.WaitingCount;
}
