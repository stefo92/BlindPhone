using Microsoft.Extensions.Logging;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Livello minimo del registro, modificabile mentre l'applicazione è in esecuzione.
/// </summary>
/// <remarks>
/// <para>
/// Il filtro predefinito di <c>Microsoft.Extensions.Logging</c> si fissa alla
/// costruzione del contenitore: cambiarlo richiederebbe di ricostruire l'intera
/// catena dei servizi, cioè di riavviare. Su un posto operatore il riavvio fa cadere
/// le chiamate in corso, e il momento in cui serve alzare il dettaglio del registro è
/// esattamente quello in cui il guasto si sta manifestando — con qualcuno in linea.
/// </para>
/// <para>
/// Questa classe è l'interruttore che permette di alzarlo subito: il filtro
/// registrato all'avvio la interroga a ogni riga, e l'operatore che segnala un
/// problema può passare a Debug, riprodurre il guasto e tornare indietro senza
/// chiudere il programma.
/// </para>
/// <para>
/// <see cref="Minimum"/> è <c>volatile</c> perché viene scritto dal thread
/// dell'interfaccia e letto da tutti gli altri: senza, un thread potrebbe continuare
/// a filtrare sul valore vecchio per un tempo non definito.
/// </para>
/// </remarks>
public sealed class LogLevelSwitch
{
    private volatile int _minimum = (int)LogLevel.Information;

    /// <summary>Crea l'interruttore.</summary>
    /// <param name="initial">Livello iniziale.</param>
    public LogLevelSwitch(LogLevel initial = LogLevel.Information) => Minimum = initial;

    /// <summary>Livello minimo registrato.</summary>
    public LogLevel Minimum
    {
        get => (LogLevel)_minimum;
        set => _minimum = (int)value;
    }

    /// <summary>Indica se un livello passa il filtro.</summary>
    /// <param name="level">Livello della riga da scrivere.</param>
    public bool IsEnabled(LogLevel level) => level >= Minimum && level != LogLevel.None;

    /// <summary>
    /// Interpreta il nome di un livello, tollerando maiuscole e valori non validi.
    /// </summary>
    /// <param name="name">Nome del livello.</param>
    /// <param name="fallback">Valore da usare se il nome non è riconosciuto.</param>
    /// <remarks>
    /// Un nome scritto male nel file di configurazione non deve spegnere il registro:
    /// si ricade sul valore predefinito, che è quello che serve a capire perché il
    /// nome era scritto male.
    /// </remarks>
    public static LogLevel Parse(string? name, LogLevel fallback = LogLevel.Information)
        => Enum.TryParse<LogLevel>(name, ignoreCase: true, out var level) ? level : fallback;

    /// <summary>Nome pronunciabile di un livello, per l'interfaccia e gli annunci.</summary>
    /// <param name="level">Livello da descrivere.</param>
    public static string Describe(LogLevel level) => level switch
    {
        LogLevel.Trace => "Tutto, anche il dettaglio interno",
        LogLevel.Debug => "Dettagliato, per indagare un guasto",
        LogLevel.Information => "Normale: comandi, chiamate, stato",
        LogLevel.Warning => "Solo avvisi e problemi",
        LogLevel.Error => "Solo errori",
        _ => level.ToString(),
    };
}
