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

/// <summary>Una cella della griglia principale: riga, colonna e colonne occupate.</summary>
/// <param name="Row">Riga.</param>
/// <param name="Column">Prima colonna.</param>
/// <param name="ColumnSpan">Quante colonne occupa, intercapedine compresa.</param>
public readonly record struct GridCell(int Row, int Column, int ColumnSpan);

/// <summary>Dove stanno la scheda della chiamata e la colonna di coda e colleghi.</summary>
/// <param name="Call">Cella della scheda della chiamata.</param>
/// <param name="Side">Cella della colonna con coda e colleghi.</param>
/// <param name="TwoColumns">Se chiamata e colonna stanno affiancate.</param>
public readonly record struct WorkAreaLayout(GridCell Call, GridCell Side, bool TwoColumns);

/// <summary>
/// Le decisioni di impaginazione della finestra principale che sono pura aritmetica,
/// tenute qui perché si possano provare senza aprire una finestra.
/// </summary>
/// <remarks>
/// <para>
/// <b>La griglia principale ha cinque righe e tre colonne, e non cambiano mai.</b>
/// Righe: la postazione; la chiamata quando la finestra è stretta; l'area di lavoro,
/// l'unica elastica; la composizione; l'ultimo annuncio. Colonne: la chiamata
/// (<c>3*</c>), un'intercapedine fissa di sedici pixel, coda e colleghi (<c>2*</c>).
/// </para>
/// <para>
/// <b>Passando da una a due colonne cambiano soltanto riga, colonna e numero di colonne
/// occupate</b> della scheda della chiamata e della colonna laterale
/// (<see cref="ArrangeWorkArea"/>). Nessun elemento cambia genitore, quindi l'albero
/// visuale, l'ordine di tabulazione e l'ordine in cui lo screen reader legge restano
/// quelli del XAML: postazione, chiamata, coda, colleghi, composizione, ultimo
/// annuncio. Spostare gli elementi da un contenitore all'altro avrebbe fatto saltare il
/// focus a chi ci stava dentro, e avrebbe cambiato l'ordine di lettura a seconda della
/// larghezza della finestra, che chi non vede non conosce. L'intercapedine è una colonna
/// e non un margine per la stessa ragione: un margine andrebbe cambiato insieme alla
/// disposizione, una colonna fissa no.
/// </para>
/// <para>
/// In entrambe le disposizioni l'ordine con cui l'occhio legge — dall'alto in basso,
/// da sinistra a destra — coincide con quello di tabulazione: la chiamata viene prima
/// della colonna laterale perché sta sopra (stretta) o a sinistra (larga).
/// </para>
/// </remarks>
public static class MainWindowLayout
{
    /// <summary>Riga della scheda della postazione.</summary>
    public const int StationRow = 0;

    /// <summary>Riga della scheda della chiamata quando la finestra è stretta.</summary>
    /// <remarks>Ad altezza automatica: con la finestra larga resta vuota e non occupa niente.</remarks>
    public const int CallRow = 1;

    /// <summary>
    /// Riga elastica: chiamata e colonna laterale affiancate, oppure la sola colonna
    /// laterale quando la finestra è stretta, oppure la sola chiamata quando coda e
    /// colleghi non ci sono.
    /// </summary>
    public const int WorkRow = 2;

    /// <summary>Riga della composizione.</summary>
    public const int DialerRow = 3;

    /// <summary>Riga dell'ultimo annuncio.</summary>
    public const int AnnouncementRow = 4;

    /// <summary>Colonne della griglia: chiamata, intercapedine, colonna laterale.</summary>
    public const int ColumnCount = 3;

    /// <summary>Colonna della colonna laterale quando la finestra è larga.</summary>
    public const int SideColumn = 2;

    /// <summary>
    /// Larghezza disponibile da cui chiamata e colonna laterale si affiancano, in pixel
    /// indipendenti dal dispositivo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Misurata sul contenuto delle due colonne, non scelta a occhio.</b> Sottratta
    /// l'intercapedine, a mille pixel la colonna laterale ne riceve 394 e la chiamata
    /// 590. Dentro la scheda laterale, tolto il riempimento, ne restano 362: è quanto
    /// serve a una riga dei colleghi per stare su una riga sola — lo stato, che non
    /// scende sotto 128, più un nominativo di una ventina di lettere, l'interno e gli
    /// spazi fra loro. La chiamata a 590 tiene il nome grande su una o due righe e i
    /// comandi di una conversazione su due.
    /// </para>
    /// <para>
    /// Sotto la soglia peggiorano tutte e due insieme: ogni collega va su due righe e la
    /// barra della chiamata su tre. Una colonna sola a tutta larghezza è meglio di due
    /// colonne che vanno a capo dappertutto.
    /// </para>
    /// <para>
    /// La larghezza è quella del contenuto, al netto dei margini della finestra. Con
    /// la misura iniziale di 1280 la finestra nasce larga; uno schermo da 1920 con la
    /// scala al 200 per cento (960 pixel) la fa nascere stretta, come deve.
    /// </para>
    /// </remarks>
    public const double TwoColumnMinWidth = 1000;

    /// <summary>
    /// Altezza sotto cui una lista non scende mai, in pixel indipendenti dal dispositivo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il titolo della scheda e una voce.</b> Con le voci a capo non c'è più la riga
    /// delle intestazioni di colonna, quindi i conti sono: riempimento della scheda
    /// 32, titolo e il suo stacco 30, margine sotto la scheda 16, una voce su due
    /// righe 52. Sotto, la sezione mostrerebbe il titolo e nient'altro.
    /// </para>
    /// <para>
    /// <b>È un pavimento, non una misura da raggiungere.</b> Prima ogni lista voleva 180
    /// pixel e la finestra si allungava da sé per darglieli. Adesso, quando lo spazio
    /// manca, le liste si accorciano fino a qui e scorrono al loro interno: la finestra
    /// cresce solo se nemmeno così il contenuto ci sta. Vedi
    /// <see cref="WorkRowMinimum"/>.
    /// </para>
    /// </remarks>
    public const double ListFloor = 130;

    /// <summary>Stacco fra il bordo basso dell'elenco dei contatti e il campo di composizione.</summary>
    public const double SuggestionsGap = 8;

    /// <summary>Altezza massima dell'elenco dei contatti proposti.</summary>
    public const double SuggestionsMaxHeight = 180;

    /// <summary>
    /// Altezza sotto cui l'elenco non si riduce: l'intestazione e una riga.
    /// </summary>
    public const double SuggestionsMinHeight = 74;

    /// <summary>
    /// Dove mettere la scheda della chiamata e la colonna di coda e colleghi.
    /// </summary>
    /// <param name="availableWidth">Larghezza del contenuto della finestra, senza margini.</param>
    /// <param name="hasSideSections">Se almeno una fra coda e colleghi è a schermo.</param>
    /// <remarks>
    /// <para>
    /// Larga: la chiamata a sinistra e la colonna laterale a destra, tutte e due nella
    /// riga elastica. Stretta: la chiamata in alto, nella riga ad altezza automatica, e
    /// la colonna laterale sotto, nella riga elastica, dove le liste si accorciano e
    /// scorrono.
    /// </para>
    /// <para>
    /// <b>Senza coda né colleghi la chiamata prende tutta la larghezza</b>, a qualunque
    /// misura, e sta nella riga elastica: una colonna vuota a destra sarebbe spazio
    /// sottratto al nome di chi chiama per non mostrare niente. Dove sta la colonna
    /// laterale in quel caso non importa, perché è nascosta: resta dove starebbe in una
    /// finestra stretta.
    /// </para>
    /// </remarks>
    public static WorkAreaLayout ArrangeWorkArea(double availableWidth, bool hasSideSections)
    {
        var tutta = new GridCell(WorkRow, 0, ColumnCount);

        if (!hasSideSections) return new(tutta, tutta, TwoColumns: false);

        return availableWidth >= TwoColumnMinWidth
            ? new(new GridCell(WorkRow, 0, 1), new GridCell(WorkRow, SideColumn, 1), TwoColumns: true)
            : new(new GridCell(CallRow, 0, ColumnCount), tutta, TwoColumns: false);
    }

    /// <summary>
    /// Altezza sotto cui la riga elastica non scende.
    /// </summary>
    /// <param name="layout">Disposizione in vigore.</param>
    /// <param name="callHeight">
    /// Altezza naturale della scheda della chiamata, margine compreso: quella che avrebbe
    /// con tutto lo spazio che vuole.
    /// </param>
    /// <param name="visibleLists">Quante liste sono a schermo: da zero a due.</param>
    /// <returns>Pixel indipendenti dal dispositivo.</returns>
    /// <remarks>
    /// <para>
    /// <b>Perché la riga elastica ha un minimo.</b> Una riga elastica dà ai figli lo
    /// spazio che resta, e un figlio che ne vorrebbe di più viene tagliato in silenzio:
    /// niente barra di scorrimento, niente segno che manchi qualcosa. Per le liste
    /// andrebbe bene fino al pavimento, perché scorrono; per la scheda della chiamata
    /// no, perché a sparire sarebbero i comandi o la casella delle attese. Con il minimo
    /// dichiarato il contenuto non si taglia: sfonda la finestra, e la finestra se ne
    /// accorge e si allunga fino al bordo dello schermo.
    /// </para>
    /// <para>
    /// Conta la chiamata solo se sta in questa riga: con la finestra stretta sta nella
    /// riga sopra, ad altezza automatica, e non si taglia comunque.
    /// </para>
    /// </remarks>
    public static double WorkRowMinimum(WorkAreaLayout layout, double callHeight, int visibleLists)
    {
        var liste = Math.Clamp(visibleLists, 0, 2) * ListFloor;
        var chiamata = layout.Call.Row == WorkRow ? Math.Max(0, callHeight) : 0;

        return Math.Max(chiamata, liste);
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
    /// Il caso è diventato raro da quando le liste si accorciano invece di allungare la
    /// finestra, ma non impossibile: una finestra bassa, con la scheda della chiamata
    /// piena e lo schermo che non lascia crescere, mette ancora il campo sotto il bordo.
    /// Il sintomo, riportato da chi la usava, era vedere della ricerca solo la fascia
    /// delle intestazioni e sentire i risultati dallo screen reader senza vederli.
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
