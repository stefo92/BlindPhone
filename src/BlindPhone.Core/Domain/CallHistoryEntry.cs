namespace BlindPhone.Core.Domain;

/// <summary>Tipo di voce nell'elenco delle ultime chiamate.</summary>
/// <remarks>
/// Tre elenchi separati e non uno con una colonna «tipo»: le tre domande che
/// l'operatore si fa sono diverse fra loro — «chi mi ha cercato?», «chi ho chiamato?»,
/// «chi ho perso?» — e la terza è quella urgente. Doverla estrarre da un elenco misto,
/// riga per riga con la sintesi, costerebbe il tempo che serve a richiamare.
/// </remarks>
public enum CallHistoryKind
{
    /// <summary>Chiamata ricevuta e risposta.</summary>
    Incoming,

    /// <summary>Chiamata partita dal posto operatore.</summary>
    Outgoing,

    /// <summary>Chiamata ricevuta e non risposta.</summary>
    Missed,
}

/// <summary>
/// Una voce dell'elenco delle ultime chiamate.
/// </summary>
/// <param name="Id">Identificativo della voce nell'archivio.</param>
/// <param name="Kind">Se ricevuta, effettuata o persa.</param>
/// <param name="Number">Numero della controparte.</param>
/// <param name="DisplayName">Nominativo, se noto al momento della chiamata.</param>
/// <param name="StartedAt">Momento in cui la chiamata è comparsa.</param>
/// <param name="Duration">Durata della conversazione; zero per le chiamate perse.</param>
/// <param name="QueueName">Coda da cui è arrivata, se il centralino l'ha riferita.</param>
/// <param name="EndReason">Motivo della chiusura, in forma leggibile.</param>
/// <remarks>
/// <b>Contiene dati personali.</b> Numeri chiamati e chiamanti sono dati di traffico:
/// da qui la cancellazione automatica dopo un periodo scelto dall'operatore e il
/// comando che svuota l'elenco a mano.
/// </remarks>
public sealed record CallHistoryEntry(
    long Id,
    CallHistoryKind Kind,
    string Number,
    string? DisplayName,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? QueueName,
    CallEndReason? EndReason)
{
    /// <summary>Nome da mostrare, oppure il numero quando il nome non si conosce.</summary>
    public string EffectiveName => string.IsNullOrWhiteSpace(DisplayName) ? Number : DisplayName;

    /// <summary>Indica se la chiamata ha avuto conversazione.</summary>
    public bool WasAnswered => Duration > TimeSpan.Zero;
}
