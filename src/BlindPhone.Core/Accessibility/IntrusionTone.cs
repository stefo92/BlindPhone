namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Genera il segnale che avverte tutti i presenti che l'operatore è in ascolto.
/// </summary>
/// <remarks>
/// <para>
/// <b>È la condizione che rende lecita l'inclusione, non un dettaglio di comodo.</b>
/// Entrare in una conversazione già in corso significa ascoltare due persone che non
/// hanno chiesto la nostra presenza — in un ospedale, verosimilmente mentre parlano
/// di un paziente. La differenza fra una funzione da posto operatore e un'intercettazione
/// è tutta qui: che i presenti lo sappiano. Per questo il segnale <b>non si può
/// spegnere</b> da nessuna impostazione, e chi lo togliesse toglierebbe l'unica cosa
/// che distingue le due situazioni.
/// </para>
/// <para>
/// <b>Va nella conversazione, non nella cuffia dell'operatore.</b> Un avviso che
/// sente solo chi si è incluso non avverte nessuno: ricorda a lui una cosa che sa
/// già. Il segnale viene immesso nella linea, quindi lo sentono l'interno, il suo
/// interlocutore e — di conseguenza — anche l'operatore, che così sa di essere
/// ancora dentro.
/// </para>
/// <para>
/// <b>Perché queste forme d'onda.</b> Trecento millisecondi a 1400 Hz: abbastanza
/// acuto da distinguersi dalla voce senza coprirla, abbastanza breve da cadere fra
/// due parole. L'ampiezza è un quarto della scala piena, cioè si sente ma non
/// costringe ad alzare la voce. La rampa di entrata e di uscita non è un vezzo: un
/// segnale che parte e finisce di netto produce uno schiocco udibile sui codec a
/// banda stretta, e su una linea telefonica lo schiocco dà più fastidio del tono.
/// </para>
/// </remarks>
public static class IntrusionTone
{
    /// <summary>Frequenza del segnale, in hertz.</summary>
    public const int FrequencyHz = 1400;

    /// <summary>Durata del segnale, come da specifica del posto operatore.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);

    /// <summary>Distanza fra un segnale e il successivo.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>Ampiezza, in frazione della scala piena.</summary>
    private const double Amplitude = 0.25;

    /// <summary>Durata della rampa di entrata e di uscita.</summary>
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// Costruisce il segnale campionato alla frequenza indicata.
    /// </summary>
    /// <param name="sampleRate">Frequenza di campionamento della linea.</param>
    /// <returns>Campioni a 16 bit con segno, un canale.</returns>
    public static short[] Render(int sampleRate)
    {
        if (sampleRate <= 0) return [];

        var campioni = (int)(sampleRate * Duration.TotalSeconds);
        if (campioni <= 0) return [];

        var rampa = Math.Max(1, (int)(sampleRate * Fade.TotalSeconds));
        var risultato = new short[campioni];

        for (var i = 0; i < campioni; i++)
        {
            var onda = Math.Sin(2 * Math.PI * FrequencyHz * i / sampleRate);

            // Rampa lineare in entrata e in uscita: al centro vale 1 e non tocca il
            // segnale.
            var inviluppo = Math.Min(1.0, Math.Min(i + 1, campioni - i) / (double)rampa);

            risultato[i] = (short)(onda * inviluppo * Amplitude * short.MaxValue);
        }

        return risultato;
    }
}
