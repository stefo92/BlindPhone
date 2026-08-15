namespace BlindPhone.Core.Domain;

/// <summary>Stato dell'operatore rispetto alle code di cui è agente.</summary>
public enum AgentStatus
{
    /// <summary>Non collegato ad alcuna coda: non riceve chiamate di coda.</summary>
    LoggedOut,

    /// <summary>Collegato e disponibile a ricevere chiamate.</summary>
    Available,

    /// <summary>Collegato ma in pausa: non riceve chiamate finché non riprende.</summary>
    Paused,

    /// <summary>Occupato in conversazione.</summary>
    Busy,

    /// <summary>In lavorazione post-chiamata (wrap-up): non riceve nuove chiamate.</summary>
    WrapUp,
}

/// <summary>
/// Stato dell'agente con il relativo motivo, quando la pausa ne prevede uno.
/// </summary>
/// <param name="Status">Stato corrente.</param>
/// <param name="ReasonCode">Codice del motivo di pausa, come definito sul PBX.</param>
/// <param name="ReasonLabel">Etichetta leggibile del motivo.</param>
/// <param name="Since">Da quando l'agente si trova in questo stato.</param>
/// <param name="WasAutomatic">
/// <c>true</c> se la transizione è stata imposta dal PBX e non richiesta
/// dall'operatore.
/// </param>
/// <remarks>
/// <para>
/// <see cref="WasAutomatic"/> merita attenzione: Yeastar mette in pausa
/// automaticamente un agente che accumula chiamate perse (evento 30025). Un
/// operatore vedente se ne accorge dal pannello; uno non vedente smetterebbe di
/// ricevere chiamate senza capire perché, restando seduto in silenzio a turno
/// aperto. Per questo la pausa automatica è annunciata con priorità critica.
/// </para>
/// </remarks>
public sealed record AgentState(
    AgentStatus Status,
    string? ReasonCode = null,
    string? ReasonLabel = null,
    DateTimeOffset? Since = null,
    bool WasAutomatic = false)
{
    /// <summary>Stato iniziale, prima di conoscere lo stato reale sul PBX.</summary>
    public static readonly AgentState Unknown = new(AgentStatus.LoggedOut);

    /// <summary>Indica se in questo stato l'agente può ricevere chiamate dalla coda.</summary>
    public bool CanReceiveQueueCalls => Status == AgentStatus.Available;
}
