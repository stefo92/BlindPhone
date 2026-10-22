namespace BlindPhone.App;

/// <summary>
/// Dove va l'elenco dei contatti proposti e quanto spazio si lascia alle liste.
/// </summary>
/// <param name="Bottom">
/// Distanza del bordo basso dell'elenco dal bordo alto del campo di composizione, nel
/// sistema di coordinate della tela che lo contiene.
/// </param>
/// <param name="MaxHeight">Altezza massima dell'elenco.</param>
public readonly record struct SuggestionsPlacement(double Bottom, double MaxHeight);

/// <summary>
/// Le due decisioni di impaginazione della finestra principale che sono pura
/// aritmetica, tenute qui perché si possano provare senza aprire una finestra.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nascono dallo stesso difetto.</b> Il campo di composizione sta in fondo alla
/// finestra, sotto le due liste — coda e colleghi — che hanno ciascuna un'altezza
/// minima. Su uno schermo basso la somma non entra, la finestra non può crescere
/// oltre lo schermo, e a restare fuori è il campo di composizione: proprio quello che
/// l'operatore deve vedere mentre scrive e, soprattutto, i contatti che la ricerca gli
/// propone.
/// </para>
/// <para>
/// Il sintomo, riportato da chi la usa: cercando un interno si vedeva solo la fascia
/// delle intestazioni, a tutta larghezza, e i risultati si sentivano dallo screen
/// reader senza comparire. L'elenco è ancorato al bordo alto del campo, e con il
/// campo sotto il bordo della finestra l'elenco scendeva con lui: della sua parte
/// alta — l'intestazione — restava un pezzo, delle righe niente.
/// </para>
/// <para>
/// Le correzioni sono due, e servono entrambe: far restare il campo in vista più a
/// lungo (<see cref="ListMinimum"/>), e far comparire comunque l'elenco dentro la
/// finestra quando il campo non c'è più (<see cref="PlaceSuggestions"/>).
/// </para>
/// </remarks>
public static class MainWindowLayout
{
    /// <summary>
    /// Altezza che ciascuna lista ha quando lo spazio basta, in pixel indipendenti dal
    /// dispositivo.
    /// </summary>
    /// <remarks>
    /// <b>Misurata, non scelta a occhio.</b> Serve a contenere il titolo della scheda,
    /// la riga delle intestazioni e almeno un paio di voci: sotto, la sezione mostra i
    /// nomi delle colonne e nient'altro.
    /// </remarks>
    public const double PreferredListHeight = 180;

    /// <summary>
    /// Altezza sotto cui una lista non scende mai, nemmeno su uno schermo molto basso.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Con la scheda che ha circa cinquanta pixel di margini e riempimento, e trenta di
    /// titolo, cento pixel lasciano vedere quasi tutta la riga delle intestazioni e
    /// nessuna voce: è una lista ridotta a una striscia, che non si legge a colpo
    /// d'occhio. Ma resta percorribile con la tastiera e letta per intero dallo
    /// screen reader, e il campo di composizione resta in vista, che a quella
    /// altezza è la cosa più importante della finestra.
    /// </para>
    /// <para>
    /// Il pavimento interviene solo quando lo spazio è davvero poco: con coda e
    /// colleghi insieme, sotto circa 660 pixel utili (430 delle altre righe, 32 di
    /// margini e due liste da cento). Uno schermo da 768 righe ne dà circa 689, e lì
    /// ciascuna lista ottiene 113: il campo di composizione entra e le liste restano
    /// un po' sopra il pavimento.
    /// </para>
    /// </remarks>
    public const double CompactListHeight = 100;

    /// <summary>Stacco fra il bordo basso dell'elenco dei contatti e il campo di composizione.</summary>
    public const double SuggestionsGap = 8;

    /// <summary>Altezza massima dell'elenco dei contatti proposti.</summary>
    public const double SuggestionsMaxHeight = 180;

    /// <summary>
    /// Altezza sotto cui l'elenco non si riduce: l'intestazione e una riga.
    /// </summary>
    public const double SuggestionsMinHeight = 74;

    /// <summary>
    /// Altezza minima da dare a ciascuna lista, dato lo spazio che la finestra può avere.
    /// </summary>
    /// <param name="capacity">
    /// Altezza utile massima che la finestra può dare al contenuto — al netto dei margini
    /// della griglia — cioè quella dell'area di lavoro dello schermo, non quella di
    /// adesso: la finestra cresce da sé fin lì.
    /// </param>
    /// <param name="fixedHeight">Altezza delle righe che non sono liste: stato, chiamata, composizione, annuncio.</param>
    /// <param name="lists">Quante liste sono visibili: da zero a due.</param>
    /// <returns>
    /// L'altezza minima di ciascuna lista: <see cref="PreferredListHeight"/> quando lo
    /// spazio basta, meno quando non basta, mai meno di <see cref="CompactListHeight"/>.
    /// Zero se non ci sono liste.
    /// </returns>
    /// <remarks>
    /// <b>Si rapporta alla capacità e non allo spazio di adesso</b>, ed è la differenza
    /// che conserva il comportamento di prima dove andava bene. Su uno schermo alto la
    /// capacità basta per le liste intere: il valore resta quello preferito e la
    /// finestra si allarga, come ha sempre fatto, per darglielo. Solo dove nemmeno
    /// allargandosi fino al bordo dello schermo le liste entrerebbero intere, ciascuna
    /// cede quanto serve a lasciare il campo di composizione in vista.
    /// </remarks>
    public static double ListMinimum(double capacity, double fixedHeight, int lists)
    {
        if (lists <= 0) return 0;

        var share = (capacity - fixedHeight) / lists;

        return Math.Clamp(Math.Floor(share), CompactListHeight, PreferredListHeight);
    }

    /// <summary>
    /// Dove mettere l'elenco dei contatti proposti perché stia dentro la finestra.
    /// </summary>
    /// <param name="canvasTop">Ordinata del bordo alto del campo di composizione.</param>
    /// <param name="visibleTop">Ordinata del primo punto in cui l'elenco può comparire.</param>
    /// <param name="visibleBottom">Ordinata dell'ultimo punto in cui l'elenco può comparire.</param>
    /// <remarks>
    /// <para>
    /// Di solito il bordo basso dell'elenco sta <see cref="SuggestionsGap"/> sopra il
    /// campo, come un menu di completamento. <b>Se il campo è sotto il bordo della
    /// finestra l'elenco sale</b> fin dove la finestra finisce: coprirà la parte bassa di
    /// ciò che sta sopra, ma si vedrà, e un elenco che sta fuori dalla finestra non
    /// serve a nessuno.
    /// </para>
    /// <para>
    /// L'altezza si limita allo spazio che sta sopra il punto scelto, senza scendere
    /// sotto <see cref="SuggestionsMinHeight"/>: in una finestra bassissima meglio
    /// un'intestazione e una riga che si vedono, della speranza di mostrarne otto.
    /// </para>
    /// </remarks>
    public static SuggestionsPlacement PlaceSuggestions(double canvasTop, double visibleTop, double visibleBottom)
    {
        var bottomEdge = Math.Min(canvasTop - SuggestionsGap, visibleBottom);
        var available = bottomEdge - visibleTop;

        return new SuggestionsPlacement(
            canvasTop - bottomEdge,
            Math.Clamp(available, SuggestionsMinHeight, SuggestionsMaxHeight));
    }
}
