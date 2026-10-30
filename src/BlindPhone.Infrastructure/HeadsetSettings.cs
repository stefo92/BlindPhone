namespace BlindPhone.Infrastructure;

/// <summary>
/// Controllo delle chiamate dai pulsanti della cuffia.
/// </summary>
/// <remarks>
/// <para>
/// <b>Accesa di partenza.</b> È la funzione che fa rispondere un operatore non vedente
/// senza cercare la tastiera, e una cuffia da ufficio collegata la usa senza che
/// nessuno debba saperla accendere. Ascolta solo il canale telefonico della cuffia —
/// quello che nessun altro dispositivo usa — quindi non sottrae niente al resto del
/// sistema.
/// </para>
/// <para>
/// <b>Il ripiego sul tasto Play e pausa invece è spento</b>, e deve restarlo finché
/// l'operatore non lo chiede. Quel tasto è dei lettori musicali e video: ascoltarlo
/// significa toglierlo a loro, ed è una scelta che spetta a chi ha una cuffia che non
/// manda altro. Vedi <see cref="MediaKeyFallback"/>.
/// </para>
/// </remarks>
public sealed record HeadsetSettings
{
    /// <summary>I pulsanti della cuffia rispondono, chiudono e silenziano.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Cuffia da ascoltare, come produttore e modello in esadecimale («0B0E:245E»), o
    /// <c>null</c> per qualunque cuffia collegata.
    /// </summary>
    /// <remarks>
    /// Produttore e modello e non il percorso del dispositivo: il percorso cambia con
    /// la porta USB, e una cuffia spostata da una presa all'altra non deve smettere di
    /// funzionare in silenzio.
    /// </remarks>
    public string? DeviceKey { get; init; }

    /// <summary>
    /// Nome della cuffia scelta, per poterla nominare quando non è collegata.
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Ascolta anche il tasto Play e pausa, per le cuffie che non hanno i pulsanti
    /// telefonici.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il tasto si prende solo mentre una chiamata squilla o è in corso, e si rilascia
    /// appena la postazione torna a riposo: fra una chiamata e l'altra la musica
    /// riparte con il suo tasto. Durante la chiamata invece il tasto è di BlindPhone, da
    /// qualunque tastiera arrivi, ed è il prezzo per cui il ripiego è spento di
    /// partenza.
    /// </para>
    /// </remarks>
    public bool MediaKeyFallback { get; init; }
}
