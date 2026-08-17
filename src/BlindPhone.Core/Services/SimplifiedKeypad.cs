using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>I tre tasti del tastierino che governano la chiamata.</summary>
public enum KeypadKey
{
    /// <summary>Il <c>+</c> del tastierino: risponde, mette in pausa, riprende.</summary>
    Answer,

    /// <summary>Il <c>/</c> del tastierino: chiude quello che c'è da chiudere.</summary>
    HangUp,

    /// <summary>L'<c>Invio</c>: manda avanti il trasferimento.</summary>
    Confirm,
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

    /// <summary>Manda il chiamante all'interno composto, senza annunciarlo.</summary>
    BlindTransfer,

    /// <summary>Chiama l'interno composto per annunciargli la chiamata.</summary>
    StartConsultation,

    /// <summary>Passa il chiamante all'interno, che risponda o stia ancora squillando.</summary>
    CompleteTransfer,

    /// <summary>Compone il numero: è il comportamento normale del campo, fuori da una chiamata.</summary>
    Dial,
}

/// <summary>
/// Traduce i tre tasti del tastierino nell'azione che lo stato corrente richiede.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tre tasti per tutto il lavoro di centralino.</b> <c>+</c> risponde, e da lì in
/// poi alterna fra linea e pausa. <c>/</c> chiude. <c>Invio</c> manda avanti il
/// trasferimento. Non c'è nient'altro da imparare, e nessuno dei tre cambia posto:
/// stanno tutti e tre sotto la mano destra, su una tastiera qualunque.
/// </para>
/// <para>
/// <b>Il segreto sta in cosa distingue i due trasferimenti</b>, ed è la pausa. Se il
/// chiamante è ancora in linea, <c>Invio</c> lo manda direttamente all'interno
/// composto: è il gesto rapido, quello che smaltisce la coda. Se invece è stato messo
/// in pausa con <c>+</c>, allora l'operatore ha dichiarato di volerci parlare prima:
/// il primo <c>Invio</c> chiama l'interno e lo fa squillare, il secondo gli passa la
/// chiamata — che abbia risposto o che stia ancora squillando.
/// </para>
/// <para>
/// <b>Nessun tasto significa due cose nello stesso stato.</b> È la regola che rende
/// la modalità insegnabile in un minuto: davanti a una situazione, ciascuno dei tre
/// tasti fa una cosa sola, sempre la stessa. Quando un tasto non ha significato non
/// ne prende uno per approssimazione — restituisce <see cref="KeypadAction.None"/>, e
/// l'operatore sente il segnale di comando rifiutato invece di ottenere qualcosa che
/// non aveva chiesto.
/// </para>
/// <para>
/// Vive nel core e non nel gestore dei tasti della finestra perché è una macchina a
/// stati, e le macchine a stati si verificano stato per stato: qui la tabella
/// completa sta nei test, dove si legge tutta insieme.
/// </para>
/// </remarks>
public static class SimplifiedKeypad
{
    /// <summary>Come si chiama, a voce, il tasto che manda avanti il trasferimento.</summary>
    public const string ConfirmKeyName = "Invio";

    /// <summary>
    /// Nome del tasto da pronunciare negli annunci del trasferimento.
    /// </summary>
    /// <param name="simplified">Se la modalità semplificata è attiva.</param>
    /// <param name="configuredKeyName">Combinazione assegnata al comando nel profilo.</param>
    /// <remarks>
    /// <para>
    /// <b>Un annuncio che nomina il tasto sbagliato è peggio del silenzio</b>: manda a
    /// premere qualcosa e, quando non succede niente, la conclusione è che il programma
    /// non funzioni. In modalità semplificata l'operatore ha imparato tre tasti e il
    /// trasferimento è Invio: dirgli «F9 per procedere comunque» gli nomina un tasto
    /// che quella modalità non gli ha mai insegnato.
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

    /// <summary>L'<c>Invio</c>: il trasferimento, in tutte le sue fasi.</summary>
    /// <remarks>
    /// <para>
    /// L'ordine dei controlli è l'ordine in cui le fasi si susseguono, e non è
    /// intercambiabile: una consultazione in corso ha la precedenza su tutto, perché
    /// lì <c>Invio</c> significa «passa la chiamata» e nient'altro.
    /// </para>
    /// <para>
    /// Fuori da qualunque chiamata <c>Invio</c> torna a essere quello che è sempre
    /// stato: compone il numero scritto nel campo. Una modalità semplificata che
    /// impedisse di telefonare sarebbe semplificata di troppo.
    /// </para>
    /// </remarks>
    private static KeypadAction ResolveConfirmKey(CallActionContext state)
    {
        if (state.TransferPhase == TransferPhase.Consulting) return KeypadAction.CompleteTransfer;

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

        // IL CHIAMANTE IN PAUSA E' LA DICHIARAZIONE DI VOLERCI PARLARE PRIMA.
        //
        // E' l'unico bivio della modalita': chi ha premuto "+" prima di comporre ha
        // gia' detto che vuole annunciare la chiamata, chi non l'ha premuto vuole
        // mandarla e passare alla successiva.
        if (state.HeldCount > 0 && !state.HasActiveCall) return KeypadAction.StartConsultation;
        if (state.HasActiveCall || state.IsConferenceActive) return KeypadAction.BlindTransfer;

        return KeypadAction.Dial;
    }
}
