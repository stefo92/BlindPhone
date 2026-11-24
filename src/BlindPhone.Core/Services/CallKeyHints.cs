using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Il nome del tasto da suggerire per un comando della chiamata, nella modalità di
/// interazione attiva.
/// </summary>
/// <remarks>
/// <para>
/// Serve al livello di dettaglio «Principiante», che in coda agli annunci dice quali
/// tasti servono in quel momento. <b>Un suggerimento che nomina il tasto sbagliato è
/// peggio di nessun suggerimento</b>, per la stessa ragione spiegata in
/// <see cref="SimplifiedKeypad.TransferKeyName"/>: manda a premere qualcosa che non
/// funziona. Per questo il nome si chiede al momento dell'annuncio, al profilo attivo,
/// e non si copia da nessuna parte.
/// </para>
/// <para>
/// In modalità semplificata si nominano i tasti del tastierino che quella modalità
/// insegna. Il trasferimento lì è un gesto in due tempi — comporre l'interno, poi
/// Invio — e un nome di tasto solo lo descriverebbe male: non lo si suggerisce.
/// </para>
/// </remarks>
public static class CallKeyHints
{
    /// <summary>Nome pronunciabile del tasto che esegue un comando.</summary>
    /// <param name="profile">Profilo dei tasti attivo.</param>
    /// <param name="simplified">Se è attiva la modalità semplificata da tastierino.</param>
    /// <param name="command">Comando di cui si cerca il tasto.</param>
    /// <returns>Il nome, oppure <c>null</c> se non c'è un tasto da suggerire.</returns>
    public static string? KeyName(HotkeyProfile profile, bool simplified, HotkeyCommand command)
    {
        if (simplified)
        {
            return command switch
            {
                HotkeyCommand.Answer or HotkeyCommand.ToggleHold => SimplifiedKeypad.AnswerKeyName,
                HotkeyCommand.HangUp => SimplifiedKeypad.HangUpKeyName,
                _ => null,
            };
        }

        return profile.Get(command, HotkeyScope.Local)?.SpeechText;
    }
}
