namespace BlindPhone.Core.Domain;

/// <summary>
/// Una transizione della macchina a stati della chiamata: da quale stato, con quale
/// evento, verso quale stato.
/// </summary>
/// <param name="From">Stato di partenza.</param>
/// <param name="Trigger">Evento che provoca la transizione.</param>
/// <param name="To">Stato di arrivo.</param>
/// <remarks>
/// Il tipo esiste perché la tabella delle transizioni sia <b>enumerabile</b>, non
/// solo eseguibile. È questo che permette al test di copertura degli annunci di
/// verificare l'esaustività invece di limitarsi a controllare i casi a cui qualcuno
/// ha pensato.
/// </remarks>
public readonly record struct CallTransition(CallState From, CallTrigger Trigger, CallState To)
{
    /// <summary>Rappresentazione leggibile, usata nei messaggi di errore dei test.</summary>
    public override string ToString() => $"{From} --{Trigger}--> {To}";
}
