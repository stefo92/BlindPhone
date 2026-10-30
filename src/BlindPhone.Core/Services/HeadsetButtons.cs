using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Traduce i pulsanti della cuffia nei comandi che lo stato corrente richiede, e lo
/// stato corrente nelle spie della cuffia.
/// </summary>
/// <remarks>
/// <para>
/// <b>La cuffia non ha comandi suoi.</b> Ogni pulsante diventa uno dei comandi del
/// catalogo dei tasti — rispondere, chiudere, attesa, microfono — e passa dalla
/// stessa strada di <c>F2</c> e <c>F3</c>: stesso effetto, stesso annuncio, stessa
/// riga di registro. È la regola della modalità semplificata, applicata a un'altra
/// disposizione di tasti.
/// </para>
/// <para>
/// <b>Il pulsante della chiamata fa una cosa sola per ogni stato</b>, ed è quella che
/// farebbe la cornetta di un telefono da tavolo. Se squilla, risponde. Se si è in
/// linea, chiude. Se c'è solo qualcuno in attesa, lo riprende. A riposo non fa
/// niente, e si sente il segnale di comando rifiutato.
/// </para>
/// <para>
/// <b>Squillo prima di tutto, anche con una conversazione aperta.</b> Su un posto
/// operatore la seconda chiamata si prende senza buttare giù la prima: è quello che
/// fa <c>F2</c>, che mette in attesa chi è in linea e risponde, ed è quello che fa il
/// pulsante. Chi vuole chiudere la conversazione prima di rispondere ha la tastiera,
/// dove le due cose hanno due tasti distinti.
/// </para>
/// <para>
/// <b>La sola chiamata in attesa non si chiude dal pulsante: si riprende.</b> Con
/// <c>F3</c> si chiuderebbe, ed è giusto, perché chi preme <c>F3</c> ha scelto quel
/// tasto. Il pulsante della cuffia invece si tocca anche per sbaglio, sistemandosi la
/// cuffia o appoggiandola sul tavolo, e chi è in attesa sta aspettando proprio
/// l'operatore: chiudergli il telefono in faccia per un tocco è il danno peggiore che
/// questo pulsante possa fare. Riprenderlo è il danno minore, e l'annuncio lo dice.
/// </para>
/// <para>
/// Vive nel core come <see cref="SimplifiedKeypad"/>, ed è verificato allo stesso
/// modo: stato per stato, con la tabella completa nei test.
/// </para>
/// </remarks>
public static class HeadsetButtons
{
    /// <summary>
    /// Decide che cosa fa un pulsante della cuffia, dato lo stato della postazione.
    /// </summary>
    /// <param name="button">Pulsante premuto.</param>
    /// <param name="state">Stato della postazione, lo stesso che governa la barra dei pulsanti.</param>
    /// <returns>
    /// Il comando da eseguire, oppure <c>null</c> se in questo stato il pulsante non
    /// ha significato: va fatto sentire il segnale di comando rifiutato.
    /// </returns>
    public static HotkeyCommand? Resolve(HeadsetButton button, CallActionContext state) => button switch
    {
        HeadsetButton.HookSwitch or HeadsetButton.PlayPause => ResolveCallButton(state),
        HeadsetButton.PhoneMute => state.HasAnyCall ? HotkeyCommand.ToggleMute : null,
        HeadsetButton.Flash => ResolveFlash(state),
        HeadsetButton.Redial => ResolveRedial(state),
        _ => null,
    };

    /// <summary>
    /// Le spie che la cuffia deve mostrare nello stato corrente.
    /// </summary>
    /// <param name="state">Stato della postazione.</param>
    /// <remarks>
    /// <para>
    /// La cornetta è alzata quando l'operatore è su una linea: in conversazione, con
    /// una chiamata che sta partendo, dentro un trasferimento. Con le sole chiamate in
    /// attesa è abbassata, e la spia dell'attesa dice perché: è lo stato in cui il
    /// pulsante riprende invece di chiudere, e la cuffia deve essere d'accordo,
    /// altrimenti la pressione successiva la manderebbe nella direzione sbagliata.
    /// </para>
    /// <para>
    /// La spia del microfono muto si accende solo durante una chiamata. Il microfono
    /// resta silenziato anche dopo, ma una luce accesa a riposo direbbe che c'è
    /// qualcosa in corso quando non c'è.
    /// </para>
    /// </remarks>
    public static HeadsetIndicators Indicators(CallActionContext state) => new(
        Ring: state.HasRingingCall,
        OffHook: IsOnALine(state),
        Mute: state.IsMuted && state.HasAnyCall,
        Hold: state.HeldCount > 0);

    /// <summary>Come si chiama, a voce, un pulsante della cuffia.</summary>
    /// <param name="button">Pulsante da nominare.</param>
    public static string Describe(HeadsetButton button) => button switch
    {
        HeadsetButton.HookSwitch => "pulsante di risposta e chiusura",
        HeadsetButton.PhoneMute => "tasto del microfono",
        HeadsetButton.Flash => "tasto dell'attesa",
        HeadsetButton.Redial => "tasto di richiamata",
        HeadsetButton.PlayPause => "tasto Play e pausa",
        _ => "pulsante sconosciuto",
    };

    /// <summary>Il pulsante della chiamata: rispondere, chiudere, riprendere.</summary>
    private static HotkeyCommand? ResolveCallButton(CallActionContext state)
    {
        if (state.HasRingingCall) return HotkeyCommand.Answer;
        if (IsOnALine(state)) return HotkeyCommand.HangUp;
        if (state.HeldCount > 0) return HotkeyCommand.ToggleHold;

        return null;
    }

    /// <summary>
    /// Il colpetto sul gancio: la seconda chiamata, come sui telefoni da tavolo.
    /// </summary>
    /// <remarks>
    /// Durante un trasferimento non fa niente, per la stessa ragione del <c>+</c> del
    /// tastierino: lì le linee sono due, e «metti in attesa» non direbbe quale.
    /// </remarks>
    private static HotkeyCommand? ResolveFlash(CallActionContext state)
    {
        if (state.HasRingingCall) return HotkeyCommand.Answer;
        if (state.IsTransferring) return null;
        if (state.IsTalking && state.HeldCount > 0) return HotkeyCommand.Swap;
        if (state.IsTalking || state.HeldCount > 0) return HotkeyCommand.ToggleHold;

        return null;
    }

    /// <summary>
    /// La richiamata, solo a riposo: con una chiamata aperta comporre un numero ne
    /// aprirebbe una seconda in parallelo. Vedi <see cref="CallActionContext.CanDial"/>.
    /// </summary>
    private static HotkeyCommand? ResolveRedial(CallActionContext state)
        => state.HasAnyCall || state.HasRingingCall || state.IsServicePaused
            ? null
            : HotkeyCommand.RedialLast;

    /// <summary>L'operatore è su una linea: la cornetta è alzata.</summary>
    private static bool IsOnALine(CallActionContext state)
        => state.IsTalking || state.HasOutgoingCall || state.IsTransferring;
}
