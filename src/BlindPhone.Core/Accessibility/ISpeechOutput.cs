namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Destinazione finale della voce: screen reader, voce interna o entrambi.
/// </summary>
/// <remarks>
/// Il core non sa e non deve sapere se dietro c'è NVDA, JAWS, una notifica UI
/// Automation o il motore SAPI su un dispositivo audio dedicato. Decide cosa dire e
/// quando; il come è responsabilità dell'infrastruttura, che sceglie il canale in
/// base a cosa è disponibile e alle preferenze dell'operatore.
/// </remarks>
public interface ISpeechOutput
{
    /// <summary>Pronuncia un testo.</summary>
    /// <param name="text">Testo da pronunciare.</param>
    /// <param name="interrupt">Se interrompere il parlato in corso.</param>
    void Speak(string text, bool interrupt);

    /// <summary>Interrompe il parlato in corso.</summary>
    void Stop();

    /// <summary>Aggiorna la riga di stato del display braille.</summary>
    /// <param name="text">Testo compatto.</param>
    void Braille(string text);
}

/// <summary>
/// Contesto operativo che influenza le regole di soppressione degli annunci.
/// </summary>
/// <remarks>
/// Volutamente mutabile e con proprietà semplici: viene aggiornato dai servizi
/// applicativi a ogni cambio di stato ed è letto dalla pipeline a ogni annuncio.
/// Un'astrazione più elaborata non aggiungerebbe nulla e renderebbe più difficile
/// capire, leggendo un test, perché un annuncio è stato soppresso.
/// </remarks>
public sealed class AnnouncementContext
{
    /// <summary>
    /// <c>true</c> quando è in corso una conversazione con audio attivo.
    /// </summary>
    public bool IsConversationActive { get; set; }

    /// <summary>Istante dell'ultima battitura, se l'operatore sta componendo.</summary>
    private DateTimeOffset? _lastKeystrokeAt;

    /// <summary>
    /// Registra una battitura nel campo di composizione.
    /// </summary>
    /// <param name="now">Istante della battitura.</param>
    /// <remarks>
    /// <para>
    /// <b>Si registra l'istante e non uno stato acceso, ed è la correzione di un
    /// difetto che ha tolto la voce alla postazione.</b> Prima esisteva un interruttore
    /// che valeva «l'operatore sta digitando», e la finestra lo teneva acceso finché il
    /// campo di composizione conteneva del testo. Ma un campo pieno non è una persona
    /// che digita: il numero rimasto lì dal trasferimento di un minuto prima teneva
    /// l'interruttore acceso a tempo indeterminato, e con esso la soppressione di tutti
    /// gli annunci sotto la priorità critica.
    /// </para>
    /// <para>
    /// Nel registro del 17 agosto 2026 si vede l'effetto: alle 12:09:55, subito dopo il
    /// tasto di risposta, «In linea con ...» viene scartato. L'operatore risponde e non
    /// gli viene detto con chi sta parlando — e non se ne può accorgere, perché il
    /// silenzio di un annuncio soppresso è identico a quello di un annuncio che non
    /// c'era. Trentasette annunci scartati così in una giornata.
    /// </para>
    /// <para>
    /// Con l'istante, la soppressione dura quanto la digitazione più una breve coda, e
    /// si spegne da sé. Nessuno deve più ricordarsi di dichiarare la fine su ogni
    /// percorso che porta fuori dal campo — che è la ragione per cui il difetto è
    /// tornato più volte da punti diversi.
    /// </para>
    /// </remarks>
    public void NoteTyping(DateTimeOffset now) => _lastKeystrokeAt = now;

    /// <summary>
    /// Dichiara conclusa la digitazione, senza attendere la scadenza.
    /// </summary>
    /// <remarks>
    /// Serve dove il gesto stesso dice che la composizione è finita — si compone, si
    /// trasferisce, si svuota il campo — e l'annuncio che segue è il riscontro di quel
    /// gesto, quindi non può aspettare.
    /// </remarks>
    public void StopTyping() => _lastKeystrokeAt = null;

    /// <summary>
    /// Indica se l'operatore sta componendo in questo momento.
    /// </summary>
    /// <param name="now">Istante corrente.</param>
    /// <param name="window">Quanto una battitura tiene fermi gli annunci.</param>
    public bool IsTyping(DateTimeOffset now, TimeSpan window)
        => _lastKeystrokeAt is { } ultima && now - ultima < window;
}
