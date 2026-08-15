namespace BlindPhone.Core.Abstractions;

/// <summary>
/// Sorgente del tempo, iniettabile.
/// </summary>
/// <remarks>
/// <para>
/// Sembra un'astrazione gratuita e non lo è: metà della logica di accessibilità di
/// questo prodotto è fatta di finestre temporali. L'accorpamento degli annunci a
/// 800 millisecondi, l'anti-ripetizione a 2 secondi, la doppia pressione del tasto
/// di trasferimento a 400 millisecondi, il timeout della consultazione, il
/// promemoria di chiamata in attesa. Testare tutto questo con
/// <c>DateTimeOffset.Now</c> significherebbe test lenti e a intermittenza, cioè
/// test che prima o poi vengono disattivati.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>Istante corrente.</summary>
    DateTimeOffset Now { get; }
}

/// <summary>Orologio di sistema.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Istanza condivisa.</summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>
/// Orologio controllabile, per i test.
/// </summary>
/// <remarks>
/// Non è confinato al progetto di test perché serve anche alla modalità
/// formazione, in cui gli scenari didattici vanno riprodotti a velocità
/// controllata.
/// </remarks>
public sealed class ManualClock : IClock
{
    /// <summary>Crea un orologio fermo all'istante indicato.</summary>
    /// <param name="start">Istante iniziale.</param>
    public ManualClock(DateTimeOffset start) => Now = start;

    /// <inheritdoc />
    public DateTimeOffset Now { get; private set; }

    /// <summary>Fa avanzare l'orologio.</summary>
    /// <param name="delta">Intervallo di avanzamento.</param>
    public void Advance(TimeSpan delta) => Now = Now.Add(delta);

    /// <summary>Fa avanzare l'orologio dei millisecondi indicati.</summary>
    /// <param name="milliseconds">Millisecondi di avanzamento.</param>
    public void AdvanceMilliseconds(double milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));
}
