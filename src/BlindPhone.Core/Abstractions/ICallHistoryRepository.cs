using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Abstractions;

/// <summary>
/// Archivio delle ultime chiamate.
/// </summary>
/// <remarks>
/// <para>
/// Separato dalla rubrica, e non una tabella in più nello stesso archivio: la rubrica
/// è un elenco che si conserva, il registro delle chiamate è materiale con dati di
/// traffico soggetto a un periodo di conservazione. Tenerli in due file distinti
/// significa che svuotare l'uno non può toccare l'altro — e che chi verifica la
/// conformità può guardare un file solo.
/// </para>
/// </remarks>
public interface ICallHistoryRepository
{
    /// <summary>Registra una chiamata conclusa.</summary>
    /// <param name="entry">Voce da registrare; l'identificativo viene assegnato dall'archivio.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task AddAsync(CallHistoryEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Restituisce le voci di un tipo, dalla più recente.
    /// </summary>
    /// <param name="kind">Tipo di chiamata.</param>
    /// <param name="limit">Numero massimo di voci.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task<IReadOnlyList<CallHistoryEntry>> GetAsync(
        CallHistoryKind kind,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Conta le voci presenti.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>Svuota l'elenco.</summary>
    /// <param name="kind">Tipo da eliminare, oppure <c>null</c> per tutti.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Quante voci sono state eliminate.</returns>
    Task<int> ClearAsync(CallHistoryKind? kind, CancellationToken cancellationToken);

    /// <summary>
    /// Elimina le voci più vecchie del periodo di conservazione.
    /// </summary>
    /// <param name="retention">
    /// Periodo da conservare. <see cref="TimeSpan.Zero"/> o valore negativo significa
    /// «mai cancellare», ed è una scelta legittima quando l'ente ha una propria
    /// procedura di conservazione.
    /// </param>
    /// <param name="now">Momento di riferimento.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Quante voci sono state eliminate.</returns>
    Task<int> PurgeAsync(TimeSpan retention, DateTimeOffset now, CancellationToken cancellationToken);
}
