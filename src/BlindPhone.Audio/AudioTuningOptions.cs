namespace BlindPhone.Audio;

/// <summary>
/// Parametri di regolazione del percorso audio della conversazione.
/// </summary>
/// <remarks>
/// <para>
/// Sono esposti all'operatore perché il valore giusto dipende dalla rete e non è
/// determinabile in anticipo: la stessa configurazione che funziona su una rete
/// locale cablata produce interruzioni su Wi-Fi o su un collegamento geografico.
/// La regolazione empirica, con i contatori della diagnostica sotto gli occhi, è
/// l'unico modo pratico di trovarlo.
/// </para>
/// </remarks>
public sealed record AudioTuningOptions
{
    /// <summary>
    /// Cuscinetto da accumulare prima di iniziare a riprodurre, in millisecondi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È il parametro che decide fra interruzioni e ritardo, e non esiste un valore
    /// buono per tutti:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>40-60 ms</b> su rete locale cablata verso un centralino nella stessa sede;</description></item>
    ///   <item><description><b>80-120 ms</b> è il compromesso prudente, ed è il valore predefinito;</description></item>
    ///   <item><description><b>150-250 ms</b> su Wi-Fi disturbato o collegamenti geografici, accettando un ritardo percepibile.</description></item>
    /// </list>
    /// <para>
    /// Oltre i 150 millisecondi il ritardo comincia a farsi sentire come
    /// sovrapposizione con l'interlocutore: si finisce per parlarsi addosso. Su un
    /// posto operatore che gestisce centinaia di chiamate al giorno è un costo
    /// concreto, quindi conviene alzare il valore solo quanto serve a far scendere a
    /// zero il contatore degli svuotamenti.
    /// </para>
    /// </remarks>
    public int JitterBufferMilliseconds { get; init; } = 100;

    /// <summary>
    /// Capienza massima del buffer di riproduzione, in millisecondi.
    /// </summary>
    /// <remarks>
    /// Oltre questa soglia i pacchetti in eccesso vengono scartati. Serve a evitare
    /// che un accumulo progressivo trasformi il ritardo in un distacco crescente fra
    /// quello che l'interlocutore dice e quello che l'operatore sente.
    /// </remarks>
    public int MaxBufferMilliseconds { get; init; } = 500;

    /// <summary>
    /// Latenza richiesta al dispositivo audio, in millisecondi.
    /// </summary>
    /// <remarks>
    /// Valori bassi riducono il ritardo ma aumentano la frequenza con cui il sistema
    /// deve consegnare i campioni: sotto i 50 millisecondi alcune schede audio,
    /// soprattutto USB, cominciano a produrre scatti indipendentemente dallo stato
    /// della rete.
    /// </remarks>
    public int DeviceLatencyMilliseconds { get; init; } = 100;

    /// <summary>
    /// Durata di pacchettizzazione dell'audio trasmesso, in millisecondi.
    /// </summary>
    /// <remarks>
    /// Venti millisecondi è lo standard della telefonia IP e va cambiato solo se il
    /// centralino lo richiede. Valori più alti riducono l'occupazione di banda ma
    /// aggiungono ritardo in trasmissione.
    /// </remarks>
    public int PacketMilliseconds { get; init; } = 20;

    /// <summary>Valori predefiniti.</summary>
    public static AudioTuningOptions Default { get; } = new();

    /// <summary>Limita i valori a intervalli utilizzabili.</summary>
    /// <returns>Una copia con i valori riportati entro i limiti.</returns>
    /// <remarks>
    /// I limiti non sono arbitrari: sotto i 20 millisecondi il cuscinetto è più
    /// corto di un pacchetto e non assorbe nulla; sopra i 500 il ritardo rende la
    /// conversazione impraticabile. Vale la pena impedire configurazioni che non
    /// possono funzionare, invece di lasciare l'operatore a cercare la causa di un
    /// audio inutilizzabile.
    /// </remarks>
    public AudioTuningOptions Sanitized() => this with
    {
        JitterBufferMilliseconds = Math.Clamp(JitterBufferMilliseconds, 20, 500),
        MaxBufferMilliseconds = Math.Clamp(MaxBufferMilliseconds, 100, 2000),
        DeviceLatencyMilliseconds = Math.Clamp(DeviceLatencyMilliseconds, 30, 500),
        PacketMilliseconds = Math.Clamp(PacketMilliseconds, 10, 60),
    };
}
