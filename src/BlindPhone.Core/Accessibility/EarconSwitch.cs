namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Decide quali segnali acustici tacciono quando l'operatore toglie la spunta
/// «Riproduci i segnali acustici».
/// </summary>
/// <remarks>
/// <para>
/// <b>La spunta non spegne tutto, e non deve.</b> Fra i segnali ce ne sono di due
/// specie. Quelli che accompagnano un annuncio — la coda che cambia, la chiamata
/// risposta, il trasferimento riuscito — dicono in un suono ciò che la voce dice in
/// parole: chi li trova di troppo li può togliere senza perdere niente. Gli altri sono
/// l'unica notizia di qualcosa che la voce non copre, e toglierli lascerebbe il
/// silenzio proprio dove serve sentire:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>La suoneria.</b> Una chiamata che entra senza farsi sentire è il guasto
///   peggiore che un posto operatore possa avere.
///   </description></item>
///   <item><description>
///   <b>I toni della linea</b>: libero, occupato, numero inesistente. Non sono un
///   commento ma il telefono stesso, e la rete spesso non li manda: senza, il silenzio
///   dopo una composizione non si distingue da una cuffia guasta.
///   </description></item>
///   <item><description>
///   <b>La registrazione perduta</b>: la postazione ha smesso di ricevere chiamate.
///   </description></item>
///   <item><description>
///   <b>Il promemoria della chiamata in attesa</b>: c'è una persona dimenticata in
///   linea, e mentre l'operatore compone la voce del promemoria tace.
///   </description></item>
///   <item><description>
///   <b>Il comando rifiutato</b>: dal tastierino semplificato e dal pulsante della
///   cuffia è l'unica risposta, e un tasto che non risponde sembra un tasto rotto.
///   </description></item>
/// </list>
/// <para>
/// Sta nel core e non nel riproduttore per la stessa ragione di
/// <see cref="EarconRouting"/>: è una tabella di decisioni, e una decisione sbagliata
/// qui si manifesta come un silenzio, che nessuna compilazione nota.
/// </para>
/// </remarks>
public static class EarconSwitch
{
    /// <summary>Se un segnale deve suonare.</summary>
    /// <param name="kind">Segnale da riprodurre.</param>
    /// <param name="earconsEnabled">Se l'operatore ha lasciato accesi i segnali acustici.</param>
    public static bool ShouldPlay(EarconKind kind, bool earconsEnabled)
        => kind != EarconKind.None && (earconsEnabled || IsEssential(kind));

    /// <summary>
    /// Se un segnale resta anche con i segnali acustici spenti.
    /// </summary>
    /// <param name="kind">Segnale da classificare.</param>
    /// <remarks>
    /// Un segnale aggiunto in futuro è accessorio finché non lo si mette qui: chi lo
    /// aggiunge decide se è il commento di un annuncio o l'unica notizia di un evento.
    /// </remarks>
    public static bool IsEssential(EarconKind kind) => kind is
        EarconKind.IncomingCall
        or EarconKind.RemoteRinging
        or EarconKind.LineBusy
        or EarconKind.LineUnavailable
        or EarconKind.RegistrationLost
        or EarconKind.HoldReminder
        or EarconKind.CommandRejected;
}
