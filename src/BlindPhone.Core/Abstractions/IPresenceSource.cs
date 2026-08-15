using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Abstractions;

/// <summary>
/// Sorgente dello stato degli interni: libero, occupato, fuori servizio.
/// </summary>
/// <remarks>
/// <para>
/// L'astrazione esiste perché la stessa informazione arriva da due strade molto
/// diverse, e su un parco installato entrambe servono.
/// </para>
/// <para>
/// <b>L'API del centralino</b> è la strada migliore quando c'è: il PBX manda gli
/// eventi da solo, per tutti gli interni, senza che il posto operatore debba chiedere
/// nulla. Ma richiede un piano di licenza che non tutte le installazioni hanno, e su
/// un Yeastar senza API non esiste proprio.
/// </para>
/// <para>
/// <b>Il protocollo SIP</b> è la strada che funziona sempre, perché è la stessa che
/// usano i telefoni da tavolo per accendere i tasti di occupato: si sottoscrive lo
/// stato di un interno e il centralino manda una notifica a ogni cambiamento. Costa
/// una sottoscrizione per interno sorvegliato, quindi si guarda solo ciò che serve.
/// </para>
/// <para>
/// Il consumatore non sa quale delle due stia usando: riceve gli stessi eventi e fa
/// le stesse domande.
/// </para>
/// </remarks>
public interface IPresenceSource : IAsyncDisposable
{
    /// <summary>Nome della sorgente, per la diagnostica e per l'operatore.</summary>
    string Name { get; }

    /// <summary>Indica se la sorgente è in grado di fornire lo stato.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Indica se la sorgente ha bisogno che le si dica quali interni sorvegliare.
    /// </summary>
    /// <remarks>
    /// L'API manda tutto da sola e risponde <c>false</c>; il SIP richiede una
    /// sottoscrizione per interno e risponde <c>true</c>. Chi usa la sorgente lo
    /// consulta per sapere se deve dichiarare cosa ha davanti.
    /// </remarks>
    bool RequiresExplicitWatch { get; }

    /// <summary>Sollevato quando cambia lo stato di un interno.</summary>
    event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

    /// <summary>Avvia la sorgente.</summary>
    void Start();

    /// <summary>
    /// Dichiara quali interni interessano in questo momento.
    /// </summary>
    /// <param name="extensions">Interni da sorvegliare.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// L'elenco è l'insieme completo di ciò che serve adesso, non un'aggiunta: quello
    /// che non compare più smette di essere sorvegliato. Su una sorgente che non
    /// richiede dichiarazione esplicita la chiamata non fa nulla.
    /// </remarks>
    Task WatchAsync(IReadOnlyCollection<string> extensions, CancellationToken cancellationToken);

    /// <summary>
    /// Conversazione attualmente in corso su un interno, se la sorgente la conosce.
    /// </summary>
    /// <param name="extension">Interno da interrogare.</param>
    /// <returns>
    /// Gli identificativi del dialogo, oppure <c>null</c> se l'interno è libero, non è
    /// sorvegliato, o la sorgente non riporta questo livello di dettaglio.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Serve all'inclusione su interno occupato (RFC 3911), che è l'unica funzione del
    /// prodotto a doversi rivolgere a una conversazione di cui il posto operatore non
    /// fa parte: per chiedere di entrarci bisogna saperla nominare.
    /// </para>
    /// <para>
    /// <b>Non tutte le sorgenti possono rispondere.</b> Il pacchetto di eventi
    /// <c>dialog</c> del protocollo SIP porta gli identificativi; una sorgente che
    /// conosce soltanto «libero» e «occupato» — come quella ricavata dalla coda —
    /// risponde <c>null</c> e la funzione si dichiara non disponibile, invece di
    /// tentare un invito che il centralino non potrebbe soddisfare.
    /// </para>
    /// </remarks>
    DialogReference? ActiveDialog(string extension);
}
