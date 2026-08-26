namespace BlindPhone.Core.Domain;

/// <summary>Esito di un sondaggio di raggiungibilità su un interno.</summary>
public enum ReachabilityOutcome
{
    /// <summary>Nessuna risposta utile: non si sa.</summary>
    Unknown,

    /// <summary>Il centralino accetta chiamate per quell'interno: il telefono c'è.</summary>
    Reachable,

    /// <summary>L'interno esiste ma non è raggiungibile: telefono spento o scollegato.</summary>
    Unreachable,

    /// <summary>Quell'interno il centralino non lo conosce.</summary>
    NotConfigured,

    /// <summary>Il centralino non serve i sondaggi: la strada è chiusa, per tutti.</summary>
    Refused,
}

/// <summary>
/// Traduce la risposta a un sondaggio <c>OPTIONS</c> in uno stato comprensibile.
/// </summary>
/// <remarks>
/// <para>
/// <b>È la stessa prova che il centralino fa a noi.</b> Yeastar la chiama <i>qualify</i>
/// e la manda ogni due minuti: un <c>OPTIONS</c> fuori dialogo, e chi non risponde
/// viene marcato irraggiungibile e smette di ricevere chiamate. Girata verso gli
/// interni dei colleghi, la stessa domanda risponde a «il suo telefono è acceso e
/// registrato?», che è quello che il centralinista vuole sapere per non passare una
/// chiamata a una scrivania vuota.
/// </para>
/// <para>
/// <b>Vive nel core e non accanto al codice SIP</b> per la ragione di sempre: è una
/// tabella di significati, si legge come una specifica e si verifica caso per caso
/// senza avere un centralino davanti.
/// </para>
/// <para>
/// <b>Un rifiuto del metodo non è una notizia sul collega.</b> <c>405</c> e <c>501</c>
/// dicono che questo impianto i sondaggi non li fa, e riguardano tutti gli interni allo
/// stesso modo: tradurli in «non in turno» dichiarerebbe assente l'intero ufficio. È lo
/// stesso errore già corretto sulle sottoscrizioni, dove <c>404</c> diventava «fuori
/// servizio» e ottantuno interni funzionanti risultavano spenti.
/// </para>
/// </remarks>
public static class ReachabilityRules
{
    /// <summary>Interpreta il codice di risposta.</summary>
    /// <param name="status">Codice di stato SIP ricevuto.</param>
    public static ReachabilityOutcome FromStatus(int status) => status switch
    {
        // 200 e' la risposta piena: il centralino parla per quell'interno.
        200 => ReachabilityOutcome.Reachable,

        // Occupato, che non disturba, che rifiuta: il telefono c'e' e ha risposto
        // qualcosa. Per la domanda «e' in turno?» sono tutti si'.
        486 or 600 or 603 => ReachabilityOutcome.Reachable,

        // Quell'interno il centralino non ce l'ha: non e' spento, non esiste. Vale la
        // pena distinguerlo, perche' si corregge nelle impostazioni e non aspettando
        // che il collega accenda il telefono.
        404 or 410 => ReachabilityOutcome.NotConfigured,

        // Configurato ma senza nessuno al suo posto: e' la risposta tipica di un
        // interno che il centralino conosce e che in questo momento non ha un telefono
        // registrato.
        480 or 502 or 503 or 504 => ReachabilityOutcome.Unreachable,

        // Il metodo non e' servito da questo impianto: non e' una notizia sul collega.
        405 or 501 => ReachabilityOutcome.Refused,

        // Un rifiuto di autorizzazione riguarda noi, non lui.
        401 or 403 or 407 => ReachabilityOutcome.Refused,

        // Tutto il resto e' rumore: si dice «non lo so», che e' la verita'.
        _ => ReachabilityOutcome.Unknown,
    };

    /// <summary>Traduce l'esito nello stato di presenza usato dal resto del prodotto.</summary>
    /// <param name="outcome">Esito del sondaggio.</param>
    /// <remarks>
    /// Il sondaggio sa una cosa sola: il telefono risponde o no. Non sa se il collega
    /// stia parlando, e per questo non produce mai <see cref="PresenceState.Busy"/>:
    /// chi riceve lo stato lo contrassegna come proveniente dalla registrazione, così
    /// un dato più preciso arrivato da altrove non viene sostituito da questo.
    /// </remarks>
    public static PresenceState ToPresence(ReachabilityOutcome outcome) => outcome switch
    {
        ReachabilityOutcome.Reachable => PresenceState.Available,
        ReachabilityOutcome.Unreachable => PresenceState.Offline,
        _ => PresenceState.Unknown,
    };
}
