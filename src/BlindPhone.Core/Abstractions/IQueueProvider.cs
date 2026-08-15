using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Abstractions;

/// <summary>
/// Capacità che un connettore di coda dichiara di supportare.
/// </summary>
/// <remarks>
/// <para>
/// L'interfaccia le espone perché la UI possa <b>nascondere</b> le funzioni non
/// disponibili invece di offrire comandi che non fanno nulla. Per un operatore non
/// vedente un comando muto è particolarmente costoso: non vede che il pannello non
/// è cambiato, quindi lo ripete, poi dubita di aver sbagliato tasto.
/// </para>
/// <para>
/// Il caso concreto è il prelievo selettivo dalla coda: la documentazione pubblica
/// Yeastar non espone un endpoint dedicato, e finché la verifica su PBX reale non
/// è fatta il connettore dichiara <see cref="CanPickSpecificCall"/> a <c>false</c>
/// (YEASTAR-INTEGRAZIONE.md §8.1).
/// </para>
/// </remarks>
/// <param name="CanReadWaitingList">Il provider fornisce l'elenco dei chiamanti, non solo il conteggio.</param>
/// <param name="CanReadCallerIdentity">Il provider fornisce numero e nome del chiamante in attesa.</param>
/// <param name="CanReadWaitTime">Il provider fornisce il tempo di attesa di ciascun chiamante.</param>
/// <param name="CanReadPosition">Il provider garantisce l'ordine di servizio della coda.</param>
/// <param name="CanPickSpecificCall">L'operatore può prelevare una chiamata specifica dalla coda.</param>
/// <param name="CanManageAgentState">Il provider consente login, logout e pausa dell'agente.</param>
/// <param name="CanReadPresence">Il provider fornisce la presenza degli interni.</param>
public readonly record struct QueueProviderCapabilities(
    bool CanReadWaitingList,
    bool CanReadCallerIdentity,
    bool CanReadWaitTime,
    bool CanReadPosition,
    bool CanPickSpecificCall,
    bool CanManageAgentState,
    bool CanReadPresence)
{
    /// <summary>
    /// Capacità di un provider che non fornisce alcun dettaglio di coda: il livello
    /// minimo di degrado, in cui l'operatore vede solo ciò che squilla da lui.
    /// </summary>
    public static QueueProviderCapabilities None => new(false, false, false, false, false, false, false);
}

/// <summary>Stato del collegamento con il centralino.</summary>
public enum PbxLinkStatus
{
    /// <summary>Mai collegato o collegamento non ancora avviato.</summary>
    Disconnected,

    /// <summary>
    /// Collegamento disattivato dall'operatore nelle impostazioni.
    /// </summary>
    /// <remarks>
    /// È distinto da <see cref="Disconnected"/> perché va comunicato in modo
    /// diverso. Un collegamento caduto è un guasto e va segnalato; un collegamento
    /// che nessuno ha chiesto non lo è, e annunciarlo come tale produce un avviso
    /// che si ripete a ogni interrogazione senza che ci sia nulla da riparare.
    /// È il caso dei centralini che non espongono l'API.
    /// </remarks>
    Disabled,

    /// <summary>Tentativo di collegamento in corso.</summary>
    Connecting,

    /// <summary>Collegato e ricezione eventi attiva.</summary>
    Connected,

    /// <summary>
    /// Collegato in modo parziale: gli eventi in tempo reale non arrivano ma
    /// l'interrogazione periodica funziona ancora, o viceversa.
    /// </summary>
    Degraded,

    /// <summary>Collegamento perso.</summary>
    Failed,
}

/// <summary>
/// Connettore verso il sistema di code del centralino.
/// </summary>
/// <remarks>
/// <para>
/// Il protocollo SIP non trasporta l'informazione "ci sono N chiamate in attesa":
/// quel dato vive nel PBX. Questa interfaccia è il confine dietro cui sta la
/// specificità del centralino, e l'implementazione primaria è Yeastar P-Series.
/// </para>
/// <para>
/// L'astrazione va mantenuta anche avendo un PBX di riferimento noto: costa poco e
/// mantiene il prodotto vendibile alla PA che ha Asterisk o 3CX senza riscrivere il
/// core.
/// </para>
/// </remarks>
public interface IQueueProvider : IAsyncDisposable
{
    /// <summary>Nome del connettore, per la diagnostica.</summary>
    string Name { get; }

    /// <summary>Capacità effettivamente supportate.</summary>
    QueueProviderCapabilities Capabilities { get; }

    /// <summary>Stato corrente del collegamento con il centralino.</summary>
    PbxLinkStatus LinkStatus { get; }

    /// <summary>Ultima fotografia nota dello stato delle code.</summary>
    QueueSnapshot CurrentSnapshot { get; }

    /// <summary>Stato corrente dell'agente.</summary>
    AgentState AgentState { get; }

    /// <summary>Sollevato a ogni aggiornamento dello stato delle code.</summary>
    event EventHandler<QueueSnapshot>? SnapshotUpdated;

    /// <summary>Sollevato quando cambia lo stato dell'agente.</summary>
    event EventHandler<AgentState>? AgentStateChanged;

    /// <summary>
    /// Sollevato quando cambia lo stato del collegamento con il centralino.
    /// </summary>
    /// <remarks>
    /// Va annunciato all'operatore: un contatore di coda congelato è
    /// indistinguibile da una coda ferma, e continuare a fidarsi di un numero
    /// vecchio porta a decisioni sbagliate sui tempi di risposta.
    /// </remarks>
    event EventHandler<PbxLinkStatus>? LinkStatusChanged;

    /// <summary>Sollevato quando cambia la presenza di un interno monitorato.</summary>
    event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

    /// <summary>Avvia il collegamento e la ricezione degli eventi.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Interrompe il collegamento in modo ordinato.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forza un allineamento immediato con la fonte di verità del centralino.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>La fotografia autorevole appena ottenuta.</returns>
    Task<QueueSnapshot> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Collega o scollega l'operatore dalle code di cui è agente.</summary>
    /// <param name="login"><c>true</c> per collegarsi, <c>false</c> per scollegarsi.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task SetAgentLoginAsync(bool login, CancellationToken cancellationToken);

    /// <summary>Mette in pausa o riprende il servizio dell'agente.</summary>
    /// <param name="paused"><c>true</c> per andare in pausa.</param>
    /// <param name="reasonCode">Codice del motivo di pausa, se richiesto dal centralino.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task SetAgentPausedAsync(bool paused, string? reasonCode, CancellationToken cancellationToken);

    /// <summary>Elenca i motivi di pausa configurati sul centralino.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task<IReadOnlyList<PauseReason>> GetPauseReasonsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Preleva dalla coda una chiamata specifica, portandola sul posto operatore.
    /// </summary>
    /// <param name="queuedCallId">Identificativo della chiamata in attesa.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se il centralino ha accettato il comando.</returns>
    /// <exception cref="NotSupportedException">
    /// Se <see cref="QueueProviderCapabilities.CanPickSpecificCall"/> è <c>false</c>.
    /// </exception>
    Task<bool> PickCallAsync(string queuedCallId, CancellationToken cancellationToken);
}

/// <summary>Dati dell'evento di cambio presenza di un interno.</summary>
/// <param name="Extension">Interno interessato.</param>
/// <param name="Presence">Nuovo stato di presenza.</param>
/// <param name="At">Istante del cambio.</param>
/// <param name="FromRegistration">
/// <c>true</c> se lo stato deriva dalla registrazione dell'interno presso il
/// centralino e non da un evento di presenza.
/// </param>
/// <remarks>
/// L'origine va distinta perché le due sorgenti sanno cose diverse. L'evento di
/// presenza dice se l'interno è libero o occupato; la registrazione dice soltanto se
/// il telefono è raggiungibile, e un telefono raggiungibile può benissimo essere già
/// in conversazione. Chi riceve l'informazione usa questo contrassegno per non
/// sostituire un dato preciso con uno generico.
/// </remarks>
public sealed record PresenceChangedEventArgs(
    string Extension,
    PresenceState Presence,
    DateTimeOffset At,
    bool FromRegistration = false);
