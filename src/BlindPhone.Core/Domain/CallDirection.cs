namespace BlindPhone.Core.Domain;

/// <summary>Verso della chiamata rispetto al posto operatore.</summary>
public enum CallDirection
{
    /// <summary>Chiamata ricevuta dall'operatore.</summary>
    Inbound,

    /// <summary>Chiamata originata dall'operatore.</summary>
    Outbound,
}
