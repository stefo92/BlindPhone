using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>I quattro tasti del tastierino che governano la chiamata.</summary>
public enum KeypadKey
{
    /// <summary>Il <c>+</c> del tastierino: risponde, mette in pausa, riprende.</summary>
    Answer,

    /// <summary>Il <c>/</c> del tastierino: chiude quello che c'è da chiudere.</summary>
    HangUp,

    /// <summary>L'<c>Invio</c>: chiama l'interno a cui si vuole passare il chiamante.</summary>
    Confirm,

    /// <summary>
    /// Il <c>-</c> del tastierino: svincola il chiamante, cioè glielo passa davvero.
    /// </summary>
    Release,
}

/// <summary>Cosa deve succedere alla pressione di un tasto del tastierino.</summary>
public enum KeypadAction
{
    /// <summary>Niente: in questo stato il tasto non ha significato.</summary>
    None,

    /// <summary>Risponde alla chiamata che squilla.</summary>
    Answer,

    /// <summary>Mette in pausa chi è in linea.</summary>
    Hold,

    /// <summary>Riprende chi è in pausa.</summary>
    Resume,

    /// <summary>Chiude la chiamata, o la linea con l'interno durante un trasferimento.</summary>
    HangUp,

    /// <summary>Chiama l'interno composto per sentire se squilla o se è occupato.</summary>
    StartConsultation,

    /// <summary>Svincola il chiamante verso l'interno: il trasferimento si compie qui.</summary>
    CompleteTransfer,

    /// <summary>
    /// Ricorda a voce qual è il tasto che svincola, invece di rifiutare in silenzio.
    /// </summary>
    AnnounceReleaseKey,

    /// <summary>Compone il numero: è il comportamento normale del campo, fuori da una chiamata.</summary>
    Dial,
}

/// <summary>
/// Traduce i tasti del tastierino nell'azione che lo stato corrente richiede.
/// </summary>
/// <remarks>
/// <para>
/// <b>Quattro tasti per tutto il lavoro di centralino.</b> <c>+</c> risponde, e da lì
/// in poi alterna fra linea e pausa. <c>/</c> chiude. <c>Invio</c> chiama l'interno a
/// cui si vuole passare il chiamante. <c>-</c> svincola, cioè glielo passa davvero.
/// Non c'è nient'altro da imparare, e nessuno dei quattro cambia posto: stanno tutti
/// sotto la mano destra, su una tastiera qualunque.
/// </para>
/// <para>
/// <b>Il trasferimento ha una strada sola, e passa sempre dall'ascolto.</b> Composto
/// l'interno, <c>Invio</c> lo chiama e l'operatore <i>sente</i> cosa succede: se
/// squilla, se è occupato, se non risponde nessuno. Solo allora <c>-</c> gli passa il
/// chiamante. Finché non si preme <c>-</c>, il chiamante resta dov'è e lo si riprende
/// con <c>/</c>.
/// </para>
/// <para>
/// <b>Perché non c'è più il trasferimento alla cieca.</b> Prima <c>Invio</c> con il
/// chiamante ancora in linea lo mandava all'interno senza annunciarlo: un solo tasto,
/// veloce, e nella coda piena era il gesto che la smaltiva. Ma su un interno occupato
/// l'operatore non aveva modo di accorgersene — il centralino se la sbrigava da solo,
/// e il chiamante finiva su un telefono che non poteva rispondergli, quando non cadeva
/// del tutto. Chi non vede non ha nessun altro canale per scoprirlo: non c'è una spia
/// che si accende, e il silenzio dopo il trasferimento è identico a quello di un
/// trasferimento riuscito. La velocità di un tasto non vale un chiamante perso, e la
/// modalità semplificata è proprio quella usata da chi ha meno strumenti per rimediare.
/// </para>
/// <para>
/// <b>Nessun tasto significa due cose nello stesso stato.</b> È la regola che rende la
/// modalità insegnabile in un minuto: davanti a una situazione, ciascuno dei quattro
/// tasti fa una cosa sola, sempre la stessa. Quando un tasto non ha significato non ne
/// prende uno per approssimazione — restituisce <see cref="KeypadAction.None"/>, e
/// l'operatore sente il segnale di comando rifiutato invece di ottenere qualcosa che
/// non aveva chiesto.
/// </para>
/// <para>
/// Vive nel core e non nel gestore dei tasti della finestra perché è una macchina a
/// stati, e le macchine a stati si verificano stato per stato: qui la tabella completa
/// sta nei test, dove si legge tutta insieme.
/// </para>
/// </remarks>
public static class SimplifiedKeypad
{
    /// <summary>Come si chiama, a voce, il tasto che chiama l'interno.</summary>
    public const string ConfirmKeyName = "Invio";

    /// <summary>Come si chiama, a voce, il tasto che svincola il chiamante.</summary>
    /// <remarks>
    /// Si pronuncia per esteso e non come simbolo: una sintesi vocale che legge
    /// «meno» è capita al primo ascolto, mentre il carattere da solo viene reso in
    /// modi diversi da NVDA, da JAWS e dalla voce interna — talvolta come «trattino»,
    /// talvolta saltato del tutto.
    /// </remarks>
    public const string ReleaseKeyName = "meno";

    /// <summary>
    /// Nome del tasto da pronunciare negli annunci del trasferimento.
    /// </summary>
    /// <param name="simplified">Se la modalità semplificata è attiva.</param>
    /// <param name="configuredKeyName">Combinazione assegnata al comando nel profilo.</param>
    /// <remarks>
    /// <para>
    /// <b>Un annuncio che nomina il tasto sbagliato è peggio del silenzio</b>: manda a
    /// premere qualcosa e, quando non succede niente, la conclusione è che il programma
    /// non funzioni. In modalità semplificata l'operatore ha imparato quattro tasti e
    /// l'avviso da superare si supera con Invio: dirgli «F9 per procedere comunque» gli
    /// nomina un tasto che quella modalità non gli ha mai insegnato.
    /// </para>
    /// <para>
    /// <b>Qui si nomina Invio e non il meno</b>, ed è la distinzione che conta: gli
    /// avvisi preventivi arrivano <i>prima</i> di chiamare l'interno — destinazione
    /// esterna, interno fuori servizio — e quello che l'operatore deve ripremere per
    /// procedere è il tasto che ha appena premuto. Il meno viene dopo, quando l'interno
    /// è già stato chiamato e si è sentito cosa risponde.
    /// </para>
    /// <para>
    /// La combinazione del profilo continua a funzionare — le due strade convivono — ma
    /// negli annunci si nomina quella che la modalità attiva insegna.
    /// </para>
    /// </remarks>
    public static string TransferKeyName(bool simplified, string configuredKeyName)
        => simplified ? ConfirmKeyName : configuredKeyName;

    /// <summary>
    /// Decide cosa fa un tasto, dato lo stato della postazione.
    /// </summary>
    /// <param name="key">Tasto premuto.</param>
    /// <param name="state">Stato della postazione, lo stesso che governa la barra dei pulsanti.</param>
    public static KeypadAction Resolve(KeypadKey key, CallActionContext state) => key switch
    {
        KeypadKey.Answer => ResolveAnswerKey(state),
        KeypadKey.HangUp => state.HasRingingCall || state.HasAnyCall ? KeypadAction.HangUp : KeypadAction.None,
        KeypadKey.Confirm => ResolveConfirmKey(state),
        KeypadKey.Release => ResolveReleaseKey(state),
        _ => KeypadAction.None,
    };

    /// <summary>Il <c>+</c>: rispondere, mettere in pausa, riprendere.</summary>
    /// <remarks>
    /// Durante un trasferimento non fa niente, ed è deliberato: lì le linee sono due e
    /// «metti in pausa» non direbbe quale. La via per tornare al chiamante quando
    /// l'interno non vuole la chiamata è <c>/</c>, che chiude la linea con lui e
    /// riporta l'operatore su chi stava aspettando.
    /// </remarks>
    private static KeypadAction ResolveAnswerKey(CallActionContext state)
    {
        if (state.HasRingingCall) return KeypadAction.Answer;
        if (state.IsTransferring) return KeypadAction.None;
        if (state.HasActiveCall || state.IsConferenceActive) return KeypadAction.Hold;
        if (state.HeldCount > 0) return KeypadAction.Resume;

        return KeypadAction.None;
    }

    /// <summary>L'<c>Invio</c>: chiamare l'interno a cui passare il chiamante.</summary>
    /// <remarks>
    /// <para>
    /// L'ordine dei controlli è l'ordine in cui le fasi si susseguono, e non è
    /// intercambiabile: una consultazione in corso ha la precedenza su tutto, perché lì
    /// l'interno è già stato chiamato e <c>Invio</c> non ha più niente da fare.
    /// </para>
    /// <para>
    /// Fuori da qualunque chiamata <c>Invio</c> torna a essere quello che è sempre
    /// stato: compone il numero scritto nel campo. Una modalità semplificata che
    /// impedisse di telefonare sarebbe semplificata di troppo.
    /// </para>
    /// </remarks>
    private static KeypadAction ResolveConfirmKey(CallActionContext state)
    {
        // L'INTERNO E' GIA' IN LINEA: QUI INVIO NON TRASFERISCE PIU'.
        //
        // E' il punto in cui la modalita' e' cambiata, ed e' anche quello in cui la
        // vecchia abitudine sopravvive piu' a lungo: chi ha imparato "Invio, Invio"
        // lo ripremera' per mesi. Rifiutare in silenzio lo lascerebbe a chiedersi se
        // il programma abbia smesso di funzionare, quindi si risponde dicendo qual e'
        // il tasto giusto. E' anche la ragione per cui il trasferimento non si compie
        // qui: chi martella Invio non ha ascoltato niente, ed e' esattamente
        // l'operatore che questa modifica deve proteggere.
        if (state.TransferPhase == TransferPhase.Consulting) return KeypadAction.AnnounceReleaseKey;

        if (state.TransferPhase == TransferPhase.EnteringDestination)
        {
            return state.HasTypedDestination ? KeypadAction.StartConsultation : KeypadAction.None;
        }

        // TRASFERIMENTO GIA' INVIATO: non c'e' piu' niente da confermare.
        //
        // Senza questo controllo il numero rimasto nel campo di composizione veniva
        // interpretato come una nuova chiamata da fare, e l'operatore che insisteva su
        // Invio perche' "non succedeva niente" apriva una terza linea mentre il
        // trasferimento era ancora in volo.
        if (state.TransferPhase == TransferPhase.Completing) return KeypadAction.None;

        // Una chiamata che squilla va risposta o rifiutata, non trasferita: finche' non
        // le si e' risposto non esiste nessuna linea da passare a nessuno.
        if (state.HasRingingCall) return KeypadAction.None;

        if (!state.HasTypedDestination)
        {
            return state.HasAnyCall ? KeypadAction.None : KeypadAction.Dial;
        }

        // C'E' UN CHIAMANTE E UN INTERNO COMPOSTO: SI CHIAMA L'INTERNO, SEMPRE.
        //
        // Non conta piu' se il chiamante sia in linea o in pausa. Prima era il bivio
        // fra le due forme di trasferimento; ora la forma e' una sola, e la pausa resta
        // solo quello che dice di essere.
        if (state.HasActiveCall || state.IsConferenceActive || state.HeldCount > 0)
            return KeypadAction.StartConsultation;

        return KeypadAction.Dial;
    }

    /// <summary>Il <c>-</c>: svincolare il chiamante verso l'interno.</summary>
    /// <remarks>
    /// <para>
    /// <b>Ha significato in un momento solo</b>, e il momento è quello in cui
    /// l'operatore ha appena sentito cosa fa l'interno: squilla, è occupato, non
    /// risponde. Prima di quel momento non c'è niente da svincolare, e dopo il
    /// trasferimento è già partito.
    /// </para>
    /// <para>
    /// Il chiamante viene passato <b>che l'interno abbia risposto o stia ancora
    /// squillando</b>: è la decisione dell'operatore, che ha sentito e sa cosa sta
    /// facendo. Se invece l'interno è occupato, la via è <c>/</c>, che chiude la linea
    /// con lui e riporta il chiamante.
    /// </para>
    /// </remarks>
    private static KeypadAction ResolveReleaseKey(CallActionContext state)
        => state.TransferPhase == TransferPhase.Consulting
            ? KeypadAction.CompleteTransfer
            : KeypadAction.None;
}
