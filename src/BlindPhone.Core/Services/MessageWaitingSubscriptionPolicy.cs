namespace BlindPhone.Core.Services;

/// <summary>
/// Quanto e quando insistere con la sottoscrizione alla segreteria telefonica.
/// </summary>
/// <remarks>
/// <para>
/// <b>La sottoscrizione è un di più, non la condizione perché la segreteria funzioni.</b>
/// Molti centralini mandano lo stato della casella a ogni interno registrato senza che
/// nessuno lo chieda, e rifiutano la sottoscrizione proprio perché la ritengono
/// superflua. Per questo un rifiuto definitivo non spegne niente: si smette di chiedere,
/// e si continua ad ascoltare quello che arriva da sé.
/// </para>
/// <para>
/// Le regole stanno qui e non nel codice SIP per la stessa ragione di
/// <see cref="PresenceSubscriptionPolicy"/>: sono decisioni sul comportamento verso il
/// centralino, e si collaudano senza un centralino.
/// </para>
/// </remarks>
public static class MessageWaitingSubscriptionPolicy
{
    /// <summary>Durata chiesta per la sottoscrizione, in secondi.</summary>
    /// <remarks>
    /// <para>
    /// Dieci minuti: la segreteria cambia di rado e un rinnovo ogni dieci minuti non
    /// pesa su nessun centralino. Abbastanza corta da stare sotto il massimo che quasi
    /// ogni impianto concede, che è il caso in cui la durata richiesta viene accettata
    /// così com'è.
    /// </para>
    /// <para>
    /// Quando il centralino concede meno, lo dice nella risposta, e il rinnovo si
    /// anticipa: vedi <see cref="EarlyRefresh"/>.
    /// </para>
    /// </remarks>
    public const int RequestedExpirySeconds = 600;

    /// <summary>Attesa prima di riprovare dopo un errore passeggero.</summary>
    /// <remarks>
    /// Cinque minuti, e non pochi secondi: chi aspetta la segreteria non si accorge di
    /// cinque minuti, mentre un centralino che si sta riavviando si accorge eccome di
    /// una richiesta ogni pochi secondi da ogni postazione.
    /// </remarks>
    public static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>Attesa minima prima di una nuova sottoscrizione, qualunque cosa dica il centralino.</summary>
    /// <remarks>
    /// L'RFC 6665 consente di ripartire subito dopo certe chiusure. Un centralino che
    /// chiude ogni sottoscrizione appena aperta trasformerebbe quel «subito» in un
    /// ciclo senza fine; mezzo minuto di pausa lo impedisce senza che nessuno se ne
    /// accorga.
    /// </remarks>
    public static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Attesa massima accettata da un <c>retry-after</c> del centralino.</summary>
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(1);

    /// <summary>
    /// Decide se riprovare dopo un rifiuto della sottoscrizione.
    /// </summary>
    /// <param name="statusCode">Codice della risposta.</param>
    /// <returns>Quanto aspettare, oppure <c>null</c> per non riprovare in questa sessione.</returns>
    /// <remarks>
    /// La natura del rifiuto si legge con la stessa regola delle sottoscrizioni allo
    /// stato degli interni, <see cref="SipDialogPresence.Classify"/>: un <c>404</c>, un
    /// <c>403</c> o un <c>489</c> non cambieranno idea per insistenza.
    /// </remarks>
    public static TimeSpan? RetryAfterRefusal(int statusCode)
        => SipDialogPresence.Classify(statusCode) == SubscriptionRefusal.Transient
            ? TransientRetryDelay
            : null;

    /// <summary>
    /// Decide se riaprire una sottoscrizione che il centralino ha chiuso.
    /// </summary>
    /// <param name="reason">
    /// Motivo di <c>Subscription-State: terminated</c>, se c'è: <c>deactivated</c>,
    /// <c>timeout</c>, <c>probation</c>, <c>giveup</c>, <c>rejected</c>,
    /// <c>noresource</c>, <c>invariant</c>.
    /// </param>
    /// <param name="retryAfterSeconds">Il parametro <c>retry-after</c>, se c'è.</param>
    /// <returns>Quanto aspettare, oppure <c>null</c> per non riaprirla.</returns>
    /// <remarks>
    /// Segue l'RFC 6665 §4.2.2: <c>rejected</c>, <c>noresource</c> e <c>invariant</c>
    /// dicono che la risposta non cambierà, gli altri che si può tornare a chiedere. Il
    /// motivo assente vale come «si può riprovare»: è il caso dei centralini che chiudono
    /// la sottoscrizione a ogni riavvio senza spiegare perché.
    /// </remarks>
    public static TimeSpan? RetryAfterTermination(string? reason, int? retryAfterSeconds)
    {
        switch (reason?.Trim().ToLowerInvariant())
        {
            case "rejected":
            case "noresource":
            case "invariant":
                return null;

            // «Ho rinunciato»: si torna a chiedere solo se il centralino dice quando.
            case "giveup":
                return retryAfterSeconds is > 0 ? Clamp(TimeSpan.FromSeconds(retryAfterSeconds.Value)) : null;

            default:
                return Clamp(retryAfterSeconds is > 0
                    ? TimeSpan.FromSeconds(retryAfterSeconds.Value)
                    : TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Decide se rinnovare prima del previsto, perché il centralino ha concesso meno tempo.
    /// </summary>
    /// <param name="requestedSeconds">Durata chiesta.</param>
    /// <param name="grantedSeconds">Durata concessa nella risposta, se dichiarata.</param>
    /// <returns>Fra quanto rinnovare, oppure <c>null</c> se basta il rinnovo ordinario.</returns>
    /// <remarks>
    /// <para>
    /// <b>Il rinnovo ordinario si regola sulla durata chiesta, non su quella concessa.</b>
    /// Se il centralino concede cinque minuti a chi ne ha chiesti dieci, la
    /// sottoscrizione scade a metà strada e per cinque minuti la segreteria resta muta.
    /// Si rinnova all'ottanta per cento della durata concessa: abbastanza prima da
    /// assorbire una risposta lenta, abbastanza tardi da non raddoppiare le richieste.
    /// </para>
    /// <para>
    /// Una durata concessa uguale o superiore a quella chiesta non cambia nulla: il
    /// rinnovo ordinario arriva comunque prima.
    /// </para>
    /// </remarks>
    public static TimeSpan? EarlyRefresh(int requestedSeconds, int? grantedSeconds)
    {
        if (grantedSeconds is not > 0) return null;
        if (grantedSeconds.Value >= requestedSeconds) return null;

        var anticipo = TimeSpan.FromSeconds(grantedSeconds.Value * 0.8);
        return anticipo < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : anticipo;
    }

    /// <summary>
    /// Legge l'intestazione <c>Subscription-State</c> di una notifica.
    /// </summary>
    /// <param name="header">
    /// Il valore, per esempio <c>active;expires=598</c> oppure
    /// <c>terminated;reason=timeout;retry-after=30</c>.
    /// </param>
    /// <returns>
    /// Se la sottoscrizione è chiusa, con motivo e attesa suggerita; un'intestazione
    /// assente o illeggibile vale «attiva», perché è l'unica lettura che non fa nulla.
    /// </returns>
    public static (bool Terminated, string? Reason, int? RetryAfterSeconds) ReadSubscriptionState(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return (false, null, null);

        var parti = header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parti.Length == 0) return (false, null, null);

        var chiusa = parti[0].Equals("terminated", StringComparison.OrdinalIgnoreCase);

        string? motivo = null;
        int? attesa = null;

        foreach (var parte in parti.Skip(1))
        {
            var uguale = parte.IndexOf('=');
            if (uguale <= 0) continue;

            var nome = parte[..uguale].Trim();
            var valore = parte[(uguale + 1)..].Trim().Trim('"');

            if (nome.Equals("reason", StringComparison.OrdinalIgnoreCase))
            {
                motivo = valore;
            }
            else if (nome.Equals("retry-after", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(valore, System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out var secondi))
            {
                attesa = secondi;
            }
        }

        return (chiusa, motivo, attesa);
    }

    private static TimeSpan Clamp(TimeSpan delay)
        => delay < MinimumRetryDelay ? MinimumRetryDelay
            : delay > MaximumRetryDelay ? MaximumRetryDelay
            : delay;
}
