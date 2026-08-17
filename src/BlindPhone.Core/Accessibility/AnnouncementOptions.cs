namespace BlindPhone.Core.Accessibility;

/// <summary>Livello di dettaglio degli annunci automatici.</summary>
public enum VerbosityLevel
{
    /// <summary>
    /// Principiante: annunci estesi, con l'indicazione dei tasti disponibili.
    /// </summary>
    Beginner,

    /// <summary>Standard: annunci completi ma senza suggerimenti sui tasti.</summary>
    Standard,

    /// <summary>
    /// Esperto: annunci ridotti all'essenziale, molti eventi affidati al solo
    /// earcon.
    /// </summary>
    Expert,
}

/// <summary>Quando annunciare a voce le variazioni della coda.</summary>
public enum QueueAnnouncementMode
{
    /// <summary>Ogni variazione viene annunciata.</summary>
    All,

    /// <summary>Solo quando l'attesa supera la soglia configurata.</summary>
    ThresholdOnly,

    /// <summary>Solo variazioni del conteggio, senza dettagli.</summary>
    CountOnly,

    /// <summary>Nessun annuncio automatico: resta l'earcon e il tasto rapido.</summary>
    Off,
}

/// <summary>
/// Parametri che governano il comportamento della pipeline di annuncio.
/// </summary>
/// <remarks>
/// <para>
/// I valori predefiniti derivano dall'analisi (§4.3) e sono tutti modificabili
/// dall'operatore. La configurabilità qui non è una comodità: operatori diversi
/// lavorano a velocità di sintesi molto diverse, e una finestra di accorpamento
/// tarata per chi ascolta a 200 parole al minuto è inutilizzabile per chi ascolta
/// a 600.
/// </para>
/// </remarks>
public sealed record AnnouncementOptions
{
    /// <summary>
    /// Finestra entro cui annunci con la stessa chiave vengono accorpati.
    /// </summary>
    /// <remarks>
    /// Evita che tre chiamate entrate in mezzo secondo producano tre annunci
    /// consecutivi: ne resta uno solo, con il totale aggiornato.
    /// </remarks>
    public TimeSpan CoalesceWindow { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Finestra entro cui un annuncio identico al precedente viene scartato.
    /// </summary>
    public TimeSpan RepeatSuppressionWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Se sopprimere gli annunci non critici durante una conversazione attiva.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È il principio P4 dell'analisi, e <b>non è più attivo per impostazione
    /// predefinita.</b> La ragione originale resta valida — parlare sopra la
    /// conversazione dell'operatore è il modo più rapido per far disinstallare il
    /// prodotto — ma il prezzo si è rivelato più alto del previsto sul campo: fra la
    /// risposta e la chiusura l'operatore non vedente restava senza <b>nessuna</b>
    /// notizia, e le cose che accadono mentre si parla sono proprio quelle che
    /// contano — una seconda chiamata che entra, un trasferimento che fallisce, la
    /// registrazione che si interrompe.
    /// </para>
    /// <para>
    /// La protezione contro la sovrapposizione resta, ma passa da altre tre strade
    /// che agiscono senza togliere l'informazione: gli annunci escono sul dispositivo
    /// degli annunci e non in cuffia, l'attenuazione abbassa conversazione e suoneria
    /// mentre la voce parla, e l'accorpamento evita le raffiche.
    /// </para>
    /// <para>
    /// Chi preferisce il silenzio assoluto lo riattiva da Impostazioni, scheda
    /// Accessibilità: l'interruttore è rimasto dov'era.
    /// </para>
    /// </remarks>
    public bool SilenceDuringConversation { get; init; } = false;

    /// <summary>
    /// Priorità minima che sfugge alla soppressione durante la conversazione.
    /// </summary>
    public AnnouncementPriority ConversationSilenceThreshold { get; init; } = AnnouncementPriority.Critical;

    /// <summary>
    /// Se sopprimere gli annunci non critici mentre l'operatore sta digitando.
    /// </summary>
    /// <remarks>
    /// Durante la composizione di un numero l'eco delle cifre ha la precedenza:
    /// un annuncio di coda che si sovrappone alla lettura delle cifre fa perdere il
    /// filo e produce numeri sbagliati.
    /// </remarks>
    public bool SilenceWhileTyping { get; init; } = true;

    /// <summary>
    /// Per quanto tempo una battitura tiene fermi gli annunci non critici.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>La soppressione dura quanto la digitazione, non quanto il testo resta nel
    /// campo.</b> È la differenza fra proteggere l'eco delle cifre e togliere la voce
    /// alla postazione: un numero rimasto nel campo dal trasferimento precedente non è
    /// una persona che sta digitando, ma prima bastava a far scartare ogni annuncio
    /// fino a quando qualcuno non svuotava la casella.
    /// </para>
    /// <para>
    /// Tre secondi. Deve coprire l'intervallo fra due tasti di chi compone lentamente —
    /// altrimenti la voce si riaccende in mezzo a un numero, che è il difetto opposto —
    /// e deve essere abbastanza breve da non mangiarsi il riscontro del gesto
    /// successivo. Ogni battitura fa ripartire il conto, quindi vale l'intervallo fra
    /// un tasto e l'altro, non la durata della composizione.
    /// </para>
    /// </remarks>
    public TimeSpan TypingSilenceWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Livello di dettaglio.</summary>
    public VerbosityLevel Verbosity { get; init; } = VerbosityLevel.Standard;

    /// <summary>Modalità di annuncio automatico delle variazioni di coda.</summary>
    public QueueAnnouncementMode QueueMode { get; init; } = QueueAnnouncementMode.CountOnly;

    /// <summary>Soglia di attesa oltre la quale scatta l'allerta.</summary>
    public TimeSpan QueueWaitThreshold { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Numero di annunci conservati nello storico consultabile.</summary>
    public int HistorySize { get; init; } = 20;

    /// <summary>Se attenuare l'audio della conversazione durante gli annunci critici.</summary>
    public bool DuckConversationOnCritical { get; init; } = true;

    /// <summary>Attenuazione applicata all'audio della conversazione, in decibel.</summary>
    public float DuckAttenuationDb { get; init; } = 8f;

    /// <summary>Intervallo del promemoria di chiamata dimenticata in attesa.</summary>
    public TimeSpan HoldReminderInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Impostazioni predefinite.</summary>
    public static AnnouncementOptions Default { get; } = new();
}
