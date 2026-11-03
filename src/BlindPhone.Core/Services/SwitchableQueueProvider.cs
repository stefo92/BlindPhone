using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Connettore di coda che può essere sostituito senza riavviare.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il problema che risolve.</b> Il connettore si sceglieva all'avvio, con i
/// parametri di allora: chi attivava l'API del centralino, ne correggeva l'indirizzo
/// o cambiava le code da seguire doveva riavviare BlindPhone, cioè smettere di
/// ricevere chiamate per il tempo di un avvio, per vedere la modifica. E al connettore
/// si agganciano la finestra principale, gli annunci della coda e la sorveglianza
/// degli interni: sostituirlo senza un intermediario vorrebbe dire riagganciarli tutti.
/// </para>
/// <para>
/// È la stessa idea di <c>SwitchableTelephonyEngine</c>: i sottoscrittori restano
/// agganciati qui e non sanno che sotto il connettore è cambiato. Diversamente dal
/// motore telefonico, cambiare connettore non tocca le chiamate: il collegamento
/// all'API porta la coda e lo stato degli interni, non l'audio.
/// </para>
/// </remarks>
public sealed class SwitchableQueueProvider : IQueueProvider
{
    private readonly ILogger<SwitchableQueueProvider> _log;
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    private IQueueProvider _inner;
    private CancellationToken _startToken = CancellationToken.None;
    private bool _started;

    /// <summary>Crea l'involucro attorno a un primo connettore.</summary>
    /// <param name="initial">Connettore con cui si parte.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SwitchableQueueProvider(IQueueProvider initial, ILogger<SwitchableQueueProvider>? logger = null)
    {
        _log = logger ?? NullLogger<SwitchableQueueProvider>.Instance;
        _inner = initial;

        Attach(_inner);
    }

    /// <summary>
    /// Sostituisce il connettore con un altro, e lo avvia se l'involucro era avviato.
    /// </summary>
    /// <param name="next">Nuovo connettore.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// <para>
    /// <b>Il precedente si chiude sullo sfondo.</b> La chiusura restituisce il
    /// gettone al centralino, e verso un indirizzo sbagliato — spesso proprio la
    /// ragione per cui lo si sta cambiando — aspetterebbe la scadenza della richiesta
    /// prima di lasciar partire il nuovo. Sganciato, non manda più niente a nessuno.
    /// </para>
    /// <para>
    /// Un avvio che fallisce arriva al chiamante, come all'avvio del programma: il
    /// nuovo connettore resta comunque in uso, perché il guasto da riferire è il suo,
    /// e tornare al precedente vorrebbe dire usare una configurazione che l'operatore
    /// ha appena cambiato.
    /// </para>
    /// </remarks>
    public async Task SwitchAsync(IQueueProvider next, CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var precedente = _inner;
            var statoPrecedente = precedente.LinkStatus;

            _log.LogInformation("Cambio del connettore di coda: da {Prima} a {Dopo}", precedente.Name, next.Name);

            Detach(precedente);
            _ = DisposeQuietlyAsync(precedente);

            Attach(next);
            _inner = next;

            // Il nuovo connettore puo' nascere in uno stato diverso — «disattivato» non
            // cambia mai, quindi non lo direbbe da solo — e chi guarda il collegamento
            // deve saperlo subito.
            if (next.LinkStatus != statoPrecedente) LinkStatusChanged?.Invoke(this, next.LinkStatus);

            if (_started) await next.StartAsync(_startToken).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task DisposeQuietlyAsync(IQueueProvider provider)
    {
        try
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Al massimo resta un gettone che scade da solo presso il centralino.
            _log.LogWarning(ex, "Chiusura del connettore di coda precedente non riuscita");
        }
    }

    // -----------------------------------------------------------------------
    // Inoltro degli eventi
    // -----------------------------------------------------------------------

    private void Attach(IQueueProvider provider)
    {
        provider.SnapshotUpdated += OnSnapshotUpdated;
        provider.AgentStateChanged += OnAgentStateChanged;
        provider.LinkStatusChanged += OnLinkStatusChanged;
        provider.PresenceChanged += OnPresenceChanged;
    }

    private void Detach(IQueueProvider provider)
    {
        provider.SnapshotUpdated -= OnSnapshotUpdated;
        provider.AgentStateChanged -= OnAgentStateChanged;
        provider.LinkStatusChanged -= OnLinkStatusChanged;
        provider.PresenceChanged -= OnPresenceChanged;
    }

    // Il mittente riportato ai sottoscrittori e' questo involucro e non il connettore
    // interno, per la stessa ragione del motore telefonico commutabile.
    private void OnSnapshotUpdated(object? sender, QueueSnapshot e) => SnapshotUpdated?.Invoke(this, e);

    private void OnAgentStateChanged(object? sender, AgentState e) => AgentStateChanged?.Invoke(this, e);

    private void OnLinkStatusChanged(object? sender, PbxLinkStatus e) => LinkStatusChanged?.Invoke(this, e);

    private void OnPresenceChanged(object? sender, PresenceChangedEventArgs e) => PresenceChanged?.Invoke(this, e);

    /// <inheritdoc />
    public event EventHandler<QueueSnapshot>? SnapshotUpdated;

    /// <inheritdoc />
    public event EventHandler<AgentState>? AgentStateChanged;

    /// <inheritdoc />
    public event EventHandler<PbxLinkStatus>? LinkStatusChanged;

    /// <inheritdoc />
    public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

    // -----------------------------------------------------------------------
    // Inoltro del contratto
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public QueueProviderCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public PbxLinkStatus LinkStatus => _inner.LinkStatus;

    /// <inheritdoc />
    public QueueSnapshot CurrentSnapshot => _inner.CurrentSnapshot;

    /// <inheritdoc />
    public AgentState AgentState => _inner.AgentState;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Prima di provarci: se l'avvio fallisce, il connettore che arrivera' con la
        // configurazione corretta deve partire da solo.
        _started = true;
        _startToken = cancellationToken;

        await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _started = false;
        await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<QueueSnapshot> RefreshAsync(CancellationToken cancellationToken)
        => _inner.RefreshAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetAgentLoginAsync(bool login, CancellationToken cancellationToken)
        => _inner.SetAgentLoginAsync(login, cancellationToken);

    /// <inheritdoc />
    public Task SetAgentPausedAsync(bool paused, string? reasonCode, CancellationToken cancellationToken)
        => _inner.SetAgentPausedAsync(paused, reasonCode, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<PauseReason>> GetPauseReasonsAsync(CancellationToken cancellationToken)
        => _inner.GetPauseReasonsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> PickCallAsync(string queuedCallId, CancellationToken cancellationToken)
        => _inner.PickCallAsync(queuedCallId, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Detach(_inner);
        await _inner.DisposeAsync().ConfigureAwait(false);
        _switchGate.Dispose();
    }
}
