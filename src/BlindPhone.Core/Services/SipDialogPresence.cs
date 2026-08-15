using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Traduce lo stato dei dialoghi SIP di un interno nel suo stato per l'operatore.
/// </summary>
/// <remarks>
/// <para>
/// È la regola al centro della sorveglianza per via SIP. Il centralino, in risposta a
/// una sottoscrizione, manda un documento <c>dialog-info</c> (RFC 4235) che elenca le
/// chiamate in corso su quell'interno con il loro stato. Nessun dialogo significa
/// telefono libero; un dialogo <c>confirmed</c> significa conversazione in corso.
/// </para>
/// <para>
/// Sta nel core, separata dal codice che parla SIP, per due ragioni. Si verifica caso
/// per caso senza centralino — ed è l'unica parte di questa funzione che si possa
/// verificare senza. E i nomi degli stati sono quelli della norma, non della
/// libreria: se un domani si cambiasse stack SIP, la regola resterebbe questa.
/// </para>
/// <para>
/// <b>Sul perché lo squillo conta.</b> Un interno che sta squillando non è libero, e
/// dirlo libero manderebbe l'operatore a passargli una seconda chiamata sopra la
/// prima. Ma non è nemmeno occupato: risponderà fra pochi secondi, e con il chiamante
/// in attesa il tempo c'è. È lo stato che merita una parola propria.
/// </para>
/// </remarks>
public static class SipDialogPresence
{
    /// <summary>
    /// Deduce lo stato di un interno dagli stati dei suoi dialoghi.
    /// </summary>
    /// <param name="dialogStates">
    /// Stati dei dialoghi come li riporta il documento: <c>trying</c>,
    /// <c>proceeding</c>, <c>early</c>, <c>confirmed</c>, <c>terminated</c>.
    /// </param>
    /// <returns>Lo stato da mostrare all'operatore.</returns>
    /// <remarks>
    /// Quando i dialoghi sono più di uno vince il più impegnativo: un interno con una
    /// conversazione in corso e una seconda chiamata che squilla è occupato, non
    /// squillante. È l'ordine in cui l'operatore prende la decisione.
    /// </remarks>
    public static PresenceState FromDialogStates(IEnumerable<string?> dialogStates)
    {
        var squilla = false;
        var vivi = 0;

        foreach (var stato in dialogStates)
        {
            switch (stato?.Trim().ToLowerInvariant())
            {
                case "confirmed":
                    // Conversazione in corso: e' la risposta definitiva, non serve
                    // guardare gli altri dialoghi.
                    return PresenceState.Busy;

                case "early":
                case "proceeding":
                case "trying":
                    squilla = true;
                    vivi++;
                    break;

                case "terminated":
                    // Un dialogo chiuso compare nella notifica proprio per dire che e'
                    // finito: non conta come occupazione.
                    break;

                case null or "":
                    break;

                default:
                    // Stato non previsto dalla norma: si conta come dialogo vivo, che
                    // e' l'ipotesi prudente. Meglio un "occupato" di troppo che una
                    // chiamata passata a un telefono gia' impegnato.
                    vivi++;
                    break;
            }
        }

        if (squilla) return PresenceState.Ringing;

        return vivi > 0 ? PresenceState.Busy : PresenceState.Available;
    }

    /// <summary>
    /// Stato di un interno che non risponde alla sottoscrizione.
    /// </summary>
    /// <param name="statusCode">Codice di risposta SIP ricevuto.</param>
    /// <remarks>
    /// <para>
    /// Un 404 o un 480 dicono che l'interno non c'è o non è raggiungibile: per
    /// l'operatore è «fuori servizio», ed è l'informazione che gli evita di
    /// trasferirci una chiamata.
    /// </para>
    /// <para>
    /// Un 403 o un 401 dicono un'altra cosa: il centralino non ci autorizza a
    /// sorvegliare quell'interno. Non è uno stato del telefono, è un limite di
    /// configurazione, e spacciarlo per «fuori servizio» farebbe evitare all'operatore
    /// un interno che invece funziona benissimo.
    /// </para>
    /// </remarks>
    public static PresenceState? FromSubscriptionFailure(int statusCode) => statusCode switch
    {
        404 or 410 or 480 or 484 or 604 => PresenceState.Offline,
        _ => null,
    };
}
