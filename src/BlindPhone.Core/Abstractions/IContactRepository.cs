using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Abstractions;

/// <summary>Archivio locale dei contatti, con la ricerca usata dall'interfaccia.</summary>
public interface IContactRepository
{
    /// <summary>Cerca contatti con corrispondenza incrementale su nome, cognome, interno e reparto.</summary>
    /// <param name="query">Testo digitato dall'operatore.</param>
    /// <param name="limit">Numero massimo di risultati.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task<IReadOnlyList<Contact>> SearchAsync(string query, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Risolve un numero nelle voci di rubrica che lo dichiarano.
    /// </summary>
    /// <param name="number">Numero da risolvere.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>
    /// Le voci corrispondenti, la più rappresentativa per prima; elenco vuoto se il
    /// numero non è in rubrica.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Restituisce un elenco perché un interno non identifica una persona.</b> In
    /// una struttura complessa lo stesso apparecchio serve più voci — l'accettazione e
    /// la stanza infermieri, l'ambulatorio e l'ufficio contabilità — e su una rubrica
    /// ospedaliera vera sono centotrentotto numeri, fino a otto voci ciascuno.
    /// Restituirne una sola significava sceglierne una a caso e annunciarla come se
    /// fosse certa: l'operatore sentiva un nome preciso e sbagliato, che è peggio di
    /// un numero.
    /// </para>
    /// <para>
    /// Conta anche per gli avvisi: se una sola delle voci su quel numero non riceve
    /// trasferimenti diretti, il controllo deve vederla anche quando non è la prima.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Contact>> ResolveByNumberAsync(string number, CancellationToken cancellationToken);

    /// <summary>Restituisce il contatto assegnato a uno slot di chiamata rapida.</summary>
    /// <param name="slot">Slot da 1 a 20.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task<Contact?> GetBySpeedDialAsync(int slot, CancellationToken cancellationToken);

    /// <summary>Inserisce o aggiorna i contatti provenienti da una sorgente.</summary>
    /// <param name="contacts">Contatti da salvare.</param>
    /// <param name="sourceName">Nome della sorgente, usato per la strategia sui duplicati.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Numero di contatti scritti.</returns>
    Task<int> UpsertAsync(IEnumerable<Contact> contacts, string sourceName, CancellationToken cancellationToken);

    /// <summary>Aggiorna la presenza di un interno.</summary>
    /// <param name="extension">Interno.</param>
    /// <param name="presence">Nuovo stato.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task UpdatePresenceAsync(string extension, PresenceState presence, CancellationToken cancellationToken);

    /// <summary>Numero di contatti presenti in archivio.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Svuota l'archivio dei contatti.
    /// </summary>
    /// <param name="sourceName">
    /// Se valorizzato, rimuove solo i contatti provenienti da quella sorgente;
    /// altrimenti li rimuove tutti.
    /// </param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Numero di contatti rimossi.</returns>
    /// <remarks>
    /// La selettività per sorgente serve a reimportare un file senza toccare i
    /// contatti che arrivano dal centralino o inseriti a mano.
    /// </remarks>
    Task<int> ClearAsync(string? sourceName, CancellationToken cancellationToken);
}

/// <summary>Sorgente da cui importare o sincronizzare contatti.</summary>
public interface IContactSource
{
    /// <summary>Nome della sorgente, mostrato e pronunciato all'operatore.</summary>
    string Name { get; }

    /// <summary>Carica i contatti dalla sorgente.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task<IReadOnlyList<Contact>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Verifica che la sorgente sia raggiungibile e configurata correttamente.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>
    /// Esito con messaggio in linguaggio naturale: "Credenziali rifiutate dal
    /// server", non "LDAP error 49". Il messaggio viene pronunciato, quindi deve
    /// essere comprensibile a chi lo ascolta una volta sola.
    /// </returns>
    Task<SourceTestResult> TestAsync(CancellationToken cancellationToken);
}

/// <summary>Esito della verifica di una sorgente di contatti.</summary>
/// <param name="Success">Se la verifica è riuscita.</param>
/// <param name="Message">Messaggio in linguaggio naturale, pronunciabile.</param>
/// <param name="ContactCount">Numero di contatti trovati, se determinabile.</param>
public sealed record SourceTestResult(bool Success, string Message, int? ContactCount = null);
