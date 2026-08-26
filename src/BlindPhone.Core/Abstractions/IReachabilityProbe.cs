using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Abstractions;

/// <summary>
/// Chiede al centralino se un interno è raggiungibile, uno alla volta.
/// </summary>
/// <remarks>
/// <para>
/// È la terza strada per sapere come stanno gli interni, e nasce perché le prime due
/// possono non esserci. L'API del centralino richiede un piano di licenza; le
/// sottoscrizioni SIP richiedono che l'impianto serva il pacchetto di eventi
/// <c>dialog</c>, e sul centralino su cui questo prodotto è in esercizio non lo serve:
/// ottantuno sottoscrizioni inviate, ottantuno rifiutate con <c>404</c>, nessuna
/// accettata.
/// </para>
/// <para>
/// Il sondaggio invece usa un metodo che <b>ogni</b> impianto SIP deve trattare, perché
/// glielo impone la norma (RFC 3261 §11): è la stessa domanda che il centralino gira a
/// noi ogni due minuti per sapere se siamo vivi. Costa un messaggio per interno e non
/// lascia nulla dietro di sé — nessuna sottoscrizione da rinnovare, nessuno stato da
/// tenere sul PBX.
/// </para>
/// <para>
/// <b>Sa meno delle altre due, e la differenza va detta.</b> Risponde «il telefono
/// c'è» o «il telefono non c'è», non «sta parlando». Per la domanda a cui serve — chi
/// è in turno — è esattamente quello che occorre.
/// </para>
/// </remarks>
public interface IReachabilityProbe
{
    /// <summary>Indica se il sondaggio si può fare in questo momento.</summary>
    /// <remarks>
    /// Falso finché il trasporto SIP non è aperto, e falso per sempre quando il
    /// centralino ha dimostrato di non servire il metodo.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>Dominio su cui si formano gli indirizzi da sondare.</summary>
    /// <remarks>
    /// Serve a costruire il bersaglio di controllo: un indirizzo che non può
    /// corrispondere a nessun interno, con cui si verifica se il centralino risponda
    /// «sì» a chiunque. Vedi <see cref="Services.ReachabilityProbePolicy"/>.
    /// </remarks>
    string Domain { get; }

    /// <summary>Interroga un indirizzo e riferisce cosa ha risposto il centralino.</summary>
    /// <param name="target">Parte utente dell'indirizzo: un interno, o il bersaglio di controllo.</param>
    /// <param name="timeout">Quanto attendere prima di dichiarare che non si sa.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>L'esito, mai un'eccezione: un sondaggio che fallisce vale «non lo so».</returns>
    Task<ReachabilityOutcome> ProbeAsync(string target, TimeSpan timeout, CancellationToken cancellationToken);
}
