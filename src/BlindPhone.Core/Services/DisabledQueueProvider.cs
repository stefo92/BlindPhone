using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Connettore di coda inerte, usato quando il collegamento al centralino è
/// disattivato nelle impostazioni.
/// </summary>
/// <remarks>
/// <para>
/// Serve a un caso concreto e frequente: un centralino su cui l'API non è esposta,
/// per piano di licenza o per scelta dell'amministratore. Senza questo oggetto
/// l'applicazione tenta comunque il collegamento, fallisce, e ogni interrogazione
/// della coda risponde <i>"dettaglio coda non disponibile, collegamento al
/// centralino interrotto"</i>. La frase è corretta ma inutile: non c'è nulla da
/// riparare, e ripetuta a ogni pressione del tasto diventa un ostacolo.
/// </para>
/// <para>
/// L'alternativa scartata era rendere il connettore Yeastar tollerante all'assenza
/// di API. Sarebbe stato peggio: avrebbe confuso <b>non configurato</b> con
/// <b>guasto</b> proprio nel componente che deve saperli distinguere, e avrebbe
/// tolto la segnalazione di caduta a chi l'API ce l'ha e ne dipende.
/// </para>
/// <para>
/// Il posto operatore resta pienamente utilizzabile: si ricevono e si effettuano
/// chiamate, si trasferisce, si consulta la rubrica. Si perde solo la visibilità
/// sulle chiamate in attesa, che senza API non è comunque ottenibile.
/// </para>
/// </remarks>
public sealed class DisabledQueueProvider : IQueueProvider
{
    private readonly IClock _clock;

    /// <summary>Crea il connettore inerte.</summary>
    /// <param name="clock">Sorgente del tempo, per datare la fotografia vuota.</param>
    public DisabledQueueProvider(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
        CurrentSnapshot = QueueSnapshot.Empty(_clock.Now);
    }

    /// <inheritdoc />
    public string Name => "Collegamento al centralino disattivato";

    /// <inheritdoc />
    public QueueProviderCapabilities Capabilities => QueueProviderCapabilities.None;

    /// <inheritdoc />
    public PbxLinkStatus LinkStatus => PbxLinkStatus.Disabled;

    /// <inheritdoc />
    public QueueSnapshot CurrentSnapshot { get; }

    /// <inheritdoc />
    public AgentState AgentState => AgentState.Unknown;

    // Nessuno di questi eventi viene mai sollevato: non esiste una fonte che possa
    // farlo. Restano dichiarati perche' fanno parte del contratto.
#pragma warning disable CS0067

    /// <inheritdoc />
    public event EventHandler<QueueSnapshot>? SnapshotUpdated;

    /// <inheritdoc />
    public event EventHandler<AgentState>? AgentStateChanged;

    /// <inheritdoc />
    public event EventHandler<PbxLinkStatus>? LinkStatusChanged;

    /// <inheritdoc />
    public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

#pragma warning restore CS0067

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<QueueSnapshot> RefreshAsync(CancellationToken cancellationToken)
        => Task.FromResult(CurrentSnapshot);

    /// <inheritdoc />
    public Task SetAgentLoginAsync(bool login, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task SetAgentPausedAsync(bool paused, string? reasonCode, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<PauseReason>> GetPauseReasonsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PauseReason>>([]);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Sempre: non c'è alcuna coda da cui prelevare.</exception>
    public Task<bool> PickCallAsync(string queuedCallId, CancellationToken cancellationToken)
        => throw new NotSupportedException("Il collegamento al centralino e' disattivato");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
