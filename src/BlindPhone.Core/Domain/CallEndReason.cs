namespace BlindPhone.Core.Domain;

/// <summary>
/// Motivo della chiusura di una chiamata, in termini comprensibili all'operatore.
/// </summary>
/// <remarks>
/// Deliberatamente non è un codice SIP. L'annuncio deve dire "interno occupato",
/// non "486 Busy Here": il codice numerico serve al log e alla diagnostica, non a
/// chi sta lavorando. La traduzione da codice SIP a questo enum avviene nel
/// progetto BlindPhone.Telephony, cioè al confine con l'infrastruttura.
/// </remarks>
public enum CallEndReason
{
    /// <summary>Chiusura normale da parte dell'operatore.</summary>
    LocalHangUp,

    /// <summary>Chiusura normale da parte della controparte.</summary>
    RemoteHangUp,

    /// <summary>La destinazione risulta occupata.</summary>
    Busy,

    /// <summary>La destinazione non ha risposto entro il tempo previsto.</summary>
    NoAnswer,

    /// <summary>La destinazione ha rifiutato esplicitamente la chiamata.</summary>
    Rejected,

    /// <summary>Il numero chiamato non esiste.</summary>
    NotFound,

    /// <summary>La destinazione è in "non disturbare".</summary>
    DoNotDisturb,

    /// <summary>La chiamata è stata trasferita ed è uscita dal controllo dell'operatore.</summary>
    Transferred,

    /// <summary>Guasto di rete o del percorso media.</summary>
    NetworkFailure,

    /// <summary>Rifiuto per credenziali o permessi (es. chiamata non autorizzata verso l'esterno).</summary>
    NotAuthorized,

    /// <summary>
    /// La cifratura della voce è obbligatoria e la controparte non la offre.
    /// </summary>
    /// <remarks>
    /// Distinto da <see cref="NotAuthorized"/> perché il rimedio è diverso e sta
    /// altrove: qui non manca un permesso sul numero chiamato, manca il supporto alla
    /// cifratura sulla linea. Chi lo sente deve sapere se cambiare numero o cambiare
    /// impostazione, e «chiamata non consentita» non lo distingue.
    /// </remarks>
    EncryptionUnavailable,

    /// <summary>Motivo non determinabile.</summary>
    Unknown,
}
