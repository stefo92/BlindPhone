namespace BlindPhone.Core.Services;

/// <summary>
/// Per quanto tempo si conservano le ultime chiamate.
/// </summary>
/// <remarks>
/// <para>
/// Le scelte sono quattro e non un numero libero di giorni. Un campo numerico
/// costringerebbe l'operatore a decidere fra 30 e 45 giorni, differenza che nessuno
/// sa valutare, mentre le quattro voci corrispondono a domande vere: «mi basta la
/// giornata», «mi serve la settimana», «mi serve il mese», «lo gestisce l'ente».
/// </para>
/// <para>
/// L'elenco contiene numeri chiamati e chiamanti, cioè dati di traffico: la scelta
/// non è una preferenza di comodità ma una decisione di conservazione, e va allineata
/// alla procedura dell'ente. Per questo «mai» esiste ed è legittima — dove la
/// conservazione è regolata altrove, cancellare da soli sarebbe sbagliato.
/// </para>
/// </remarks>
public static class CallHistoryRetention
{
    /// <summary>Valore che indica «non cancellare mai».</summary>
    public const int Forever = 0;

    /// <summary>Scelte offerte all'operatore, dalla più breve.</summary>
    public static IReadOnlyList<int> Options { get; } = [1, 7, 30, Forever];

    /// <summary>Valore predefinito: un mese.</summary>
    public const int Default = 30;

    /// <summary>Descrizione pronunciabile di una scelta.</summary>
    /// <param name="days">Giorni di conservazione.</param>
    public static string Describe(int days) => days switch
    {
        Forever => "mai: conserva tutto",
        1 => "1 giorno",
        7 => "1 settimana",
        30 => "1 mese",
        _ => $"{days} giorni",
    };

    /// <summary>
    /// Traduce la scelta in un periodo.
    /// </summary>
    /// <param name="days">Giorni di conservazione.</param>
    /// <returns><see cref="TimeSpan.Zero"/> quando non si cancella mai.</returns>
    public static TimeSpan ToTimeSpan(int days) => days <= 0 ? TimeSpan.Zero : TimeSpan.FromDays(days);

    /// <summary>
    /// Riporta un valore qualunque alla scelta corrispondente.
    /// </summary>
    /// <param name="days">Valore letto dalla configurazione.</param>
    /// <remarks>
    /// Serve ai file scritti prima che esistessero queste quattro voci, e a chi
    /// modifica la configurazione a mano. Un valore fuori elenco viene ricondotto alla
    /// scelta più vicina <b>non più corta</b>: arrotondare per difetto significherebbe
    /// cancellare dati che l'operatore si aspettava di trovare ancora.
    /// </remarks>
    public static int Normalize(int days)
    {
        if (days <= 0) return Forever;

        foreach (var option in Options.Where(o => o > 0))
        {
            if (days <= option) return option;
        }

        // Oltre il mese si conserva tutto: e' l'unica scelta rimasta che non accorcia
        // il periodo gia' configurato.
        return Forever;
    }
}
