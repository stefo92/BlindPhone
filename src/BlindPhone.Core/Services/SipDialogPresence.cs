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
/// <para>
/// <b>LO STATO DI UN TELEFONO SI IMPARA SOLO DA UN NOTIFY.</b> È la regola che governa
/// questa classe, e vale la pena dire da dove viene. Prima il rifiuto di una
/// sottoscrizione veniva tradotto in uno stato: un <c>404</c> diventava «fuori
/// servizio». Sembra ragionevole — su un INVITE il <c>404</c> dice davvero che quel
/// numero non esiste — ma su una sottoscrizione al pacchetto <c>dialog</c> dice
/// un'altra cosa: <i>di quella risorsa non riporto lo stato dei dialoghi</i>. Lo
/// rispondono i centralini per le code, i gruppi di squillo, i risponditori
/// automatici, e lo rispondono gli apparati di frontiera che non inoltrano le
/// sottoscrizioni di stato. Sono tutti numeri a cui una chiamata si passa benissimo.
/// </para>
/// <para>
/// Nel registro del 17 agosto 2026 quella traduzione ha marcato «fuori servizio»
/// <b>81 interni funzionanti</b> in una mattina, perché su quel centralino ogni
/// sottoscrizione riceve <c>404</c>: 81 rifiuti, zero accettazioni. Da lì il difetto
/// che l'operatore riferiva — il trasferimento che non partiva e la voce che diceva
/// «non registrato» di un interno che rispondeva al primo squillo.
/// </para>
/// <para>
/// Perciò da un rifiuto non si ricava nessuno stato. Si ricava una cosa diversa e
/// utile: se valga la pena insistere, ed è <see cref="Classify"/>.
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
    /// Dice di che natura è il rifiuto di una sottoscrizione, per sapere se insistere.
    /// </summary>
    /// <param name="statusCode">Codice di risposta SIP ricevuto.</param>
    /// <remarks>
    /// <para>
    /// <b>Non dice niente sullo stato del telefono</b>, e non è una limitazione di
    /// questo metodo: è il punto. Risponde a una sola domanda — «riprovare cambierebbe
    /// la risposta?» — perché è la sola a cui un codice di rifiuto sappia rispondere.
    /// </para>
    /// <para>
    /// <see cref="SubscriptionRefusal.EventPackage"/> è la categoria che pesa più delle
    /// altre: dice che il centralino non serve affatto il pacchetto di eventi, quindi
    /// non c'è nessun interno per cui valga la pena riprovare. Chi la riceve non
    /// sospende una sottoscrizione, sospende la funzione.
    /// </para>
    /// </remarks>
    public static SubscriptionRefusal Classify(int statusCode) => statusCode switch
    {
        // Il metodo non è ammesso, l'evento non è conosciuto, la richiesta non è
        // implementata: sono affermazioni sul centralino, non sull'interno.
        405 or 489 or 501 => SubscriptionRefusal.EventPackage,

        // Le nostre credenziali non bastano. Non è uno stato del telefono ed è
        // inutile ripetere la stessa richiesta con le stesse credenziali.
        401 or 403 or 407 => SubscriptionRefusal.Authorisation,

        // Di quella risorsa il centralino non riporta lo stato dei dialoghi. Vale per
        // un interno che non esiste e vale per una coda che esiste benissimo: da qui
        // non si distinguono, e per questo non si conclude niente sul telefono.
        400 or 404 or 410 or 484 or 604 => SubscriptionRefusal.Resource,

        // Tutto il resto — 480, 500, 503, i timeout — può cambiare da un momento
        // all'altro: la rete torna, il centralino finisce di riavviarsi.
        _ => SubscriptionRefusal.Transient,
    };
}

/// <summary>Che cosa dice il rifiuto di una sottoscrizione allo stato di un interno.</summary>
/// <remarks>
/// Nessuno dei valori è uno stato del telefono, ed è deliberato: vedi le note di
/// <see cref="SipDialogPresence"/>.
/// </remarks>
public enum SubscriptionRefusal
{
    /// <summary>Errore passeggero: fra un minuto la stessa richiesta può riuscire.</summary>
    Transient,

    /// <summary>
    /// Di quella risorsa il centralino non riporta lo stato. Riprovare non serve, ma
    /// non dice nulla sulle altre.
    /// </summary>
    Resource,

    /// <summary>Il centralino non ci autorizza a sorvegliare. È configurazione, non stato.</summary>
    Authorisation,

    /// <summary>
    /// Il centralino non serve il pacchetto di eventi: la sorveglianza per via SIP non
    /// è disponibile su questo impianto, per nessun interno.
    /// </summary>
    EventPackage,
}
