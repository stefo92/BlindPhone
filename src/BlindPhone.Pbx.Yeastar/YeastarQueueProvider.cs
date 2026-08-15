using System.Text.Json;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Pbx.Yeastar;

/// <summary>
/// Connettore di coda per Yeastar P-Series.
/// </summary>
/// <remarks>
/// <para>
/// È il componente che soddisfa il requisito centrale del capitolato: sapere in
/// tempo reale quante chiamate ci sono in coda e chi sono. Yeastar lo rende
/// possibile con l'endpoint <c>queue/call_status</c>, che restituisce direttamente
/// l'elenco dei chiamanti in attesa con numero, nome e tempo di attesa.
/// </para>
/// <para>
/// <b>Modello ibrido.</b> Né il solo interrogare periodicamente né i soli eventi
/// bastano: l'interrogazione ha latenza, gli eventi si perdono alle disconnessioni
/// e Yeastar non li recapita in differita. Il provider combina i due:
/// </para>
/// <list type="bullet">
///   <item><description>gli <b>eventi WebSocket</b> innescano un riallineamento immediato, per la reattività;</description></item>
///   <item><description>l'<b>interrogazione periodica</b> è la fonte di verità, con intervallo adattivo: rado a coda vuota, fitto a coda popolata;</description></item>
///   <item><description>alla <b>riconnessione</b> si riallinea sempre, perché nell'intervallo di disconnessione sono stati persi eventi.</description></item>
/// </list>
/// <para>
/// <b>Capacità dichiarate con prudenza.</b> Due funzioni restano disattive finché
/// non sono verificate su un PBX reale: il prelievo selettivo di una chiamata dalla
/// coda (nessun endpoint documentato) e la posizione in coda (l'ordine
/// dell'elenco non è garantito). Dichiararle senza verifica produrrebbe comandi
/// muti e annunci di posizione potenzialmente falsi, che per un operatore che non
/// può controllare a schermo sono peggio dell'assenza della funzione.
/// </para>
/// </remarks>
public sealed class YeastarQueueProvider : IQueueProvider
{
    private readonly YeastarOptions _options;
    private readonly YeastarApiClient _api;
    private readonly YeastarEventStream _events;
    private readonly IClock _clock;
    private readonly ILogger<YeastarQueueProvider> _log;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _waitingSince = [];

    private CancellationTokenSource? _lifetime;
    private Task? _pollLoop;
    private QueueSnapshot _snapshot;
    private AgentState _agentState = AgentState.Unknown;
    private PbxLinkStatus _linkStatus = PbxLinkStatus.Disconnected;
    private volatile bool _refreshRequested;

    /// <summary>Crea il connettore.</summary>
    /// <param name="options">Parametri di collegamento.</param>
    /// <param name="api">Client REST.</param>
    /// <param name="events">Flusso eventi WebSocket.</param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    public YeastarQueueProvider(
        YeastarOptions options,
        YeastarApiClient api,
        YeastarEventStream events,
        IClock? clock = null,
        ILogger<YeastarQueueProvider>? logger = null)
    {
        _options = options;
        _api = api;
        _events = events;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<YeastarQueueProvider>.Instance;

        _snapshot = QueueSnapshot.Empty(_clock.Now);

        _events.EventReceived += OnEventReceived;
        _events.ConnectionStateChanged += OnConnectionStateChanged;
    }

    /// <inheritdoc />
    public string Name => "Yeastar P-Series";

    /// <inheritdoc />
    public QueueProviderCapabilities Capabilities => new(
        CanReadWaitingList: true,
        CanReadCallerIdentity: true,
        CanReadWaitTime: true,
        CanReadPosition: _options.WaitingListIsOrdered,
        CanPickSpecificCall: _options.EnableExperimentalQueuePick,
        CanManageAgentState: true,
        CanReadPresence: true);

    /// <inheritdoc />
    public PbxLinkStatus LinkStatus
    {
        get { lock (_gate) return _linkStatus; }
    }

    /// <inheritdoc />
    public QueueSnapshot CurrentSnapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <inheritdoc />
    public AgentState AgentState
    {
        get { lock (_gate) return _agentState; }
    }

    /// <inheritdoc />
    public event EventHandler<QueueSnapshot>? SnapshotUpdated;

    /// <inheritdoc />
    public event EventHandler<AgentState>? AgentStateChanged;

    /// <inheritdoc />
    public event EventHandler<PbxLinkStatus>? LinkStatusChanged;

    /// <inheritdoc />
    public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

    // -----------------------------------------------------------------------
    // Ciclo di vita
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Validate(out var error))
        {
            SetLinkStatus(PbxLinkStatus.Failed);
            throw new InvalidOperationException(error);
        }

        SetLinkStatus(PbxLinkStatus.Connecting);

        try
        {
            await _api.EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (YeastarApiException)
        {
            SetLinkStatus(PbxLinkStatus.Failed);
            throw;
        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await _events.StartAsync(_lifetime.Token).ConfigureAwait(false);
        _pollLoop = Task.Run(() => RunPollLoopAsync(_lifetime.Token), CancellationToken.None);

        // Primo allineamento immediato: senza, per i primi secondi l'operatore
        // sentirebbe "nessuna chiamata in coda" anche con la coda piena.
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAgentStateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);

        await _events.StopAsync().ConfigureAwait(false);

        if (_pollLoop is not null)
        {
            try
            {
                await _pollLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Percorso normale di chiusura.
            }
        }

        await _api.ReleaseTokenAsync(cancellationToken).ConfigureAwait(false);
        SetLinkStatus(PbxLinkStatus.Disconnected);
    }

    // -----------------------------------------------------------------------
    // Interrogazione periodica
    // -----------------------------------------------------------------------

    /// <summary>
    /// Ciclo di interrogazione a intervallo adattivo.
    /// </summary>
    /// <remarks>
    /// L'intervallo è più fitto quando ci sono chiamate in attesa, perché è in quel
    /// momento che il tempo di attesa cambia ed è il dato su cui l'operatore decide
    /// la priorità. A coda vuota si interroga di rado: il PBX non va caricato per
    /// confermare che non è successo nulla.
    /// </remarks>
    private async Task RunPollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = CurrentSnapshot.WaitingCount > 0
                ? _options.ActivePollInterval
                : _options.IdlePollInterval;

            try
            {
                if (_refreshRequested)
                {
                    // Un evento ha segnalato un cambiamento: si concede al PBX il
                    // tempo di aggiornare il proprio stato interno prima di
                    // interrogarlo, altrimenti si rischia di leggere il valore
                    // precedente e di doversi correggere subito dopo.
                    _refreshRequested = false;
                    await Task.Delay(_options.PostEventRefreshDelay, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }

                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (YeastarApiException ex)
            {
                _log.LogWarning(ex, "Interrogazione della coda non riuscita");
                SetLinkStatus(PbxLinkStatus.Degraded);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Errore imprevisto nel ciclo di interrogazione della coda");
                SetLinkStatus(PbxLinkStatus.Degraded);
            }
        }
    }

    /// <inheritdoc />
    public async Task<QueueSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        var now = _clock.Now;
        var waiting = new List<QueuedCaller>();
        var ringing = 0;
        var active = 0;

        foreach (var queueId in _options.QueueIds)
        {
            var response = await _api
                .GetAsync<YeastarQueueCallStatusResponse>(
                    "queue/call_status",
                    new Dictionary<string, string> { ["id"] = queueId.ToString() },
                    cancellationToken)
                .ConfigureAwait(false);

            ringing += response.RingingCalls;
            active += response.ActiveCalls;

            for (var i = 0; i < response.WaitingList.Count; i++)
            {
                var call = response.WaitingList[i];
                if (call.CallId is null) continue;

                waiting.Add(ToQueuedCaller(call, i, now));
            }
        }

        PruneWaitTimeCache(waiting);

        var snapshot = new QueueSnapshot(waiting, ringing, active, now, IsAuthoritative: true);

        lock (_gate)
        {
            _snapshot = snapshot;
            if (_linkStatus != PbxLinkStatus.Connected) _linkStatus = PbxLinkStatus.Connected;
        }

        LinkStatusChanged?.Invoke(this, PbxLinkStatus.Connected);
        SnapshotUpdated?.Invoke(this, snapshot);
        return snapshot;
    }

    /// <summary>
    /// Traduce una voce dell'API nel modello di dominio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// L'istante di ingresso in coda viene <b>calcolato e memorizzato</b> alla prima
    /// osservazione, invece di essere ricavato ogni volta da <c>duration</c>. Il
    /// motivo è la stabilità: <c>duration</c> ha risoluzione al secondo e viene
    /// arrotondata dal PBX, quindi ricalcolare l'istante di ingresso a ogni
    /// interrogazione lo farebbe oscillare avanti e indietro di un secondo. In un
    /// elenco letto a voce quell'oscillazione si sente, perché il tempo annunciato
    /// a volte torna indietro.
    /// </para>
    /// </remarks>
    private QueuedCaller ToQueuedCaller(YeastarQueueCall call, int index, DateTimeOffset now)
    {
        DateTimeOffset waitingSince;

        lock (_gate)
        {
            if (!_waitingSince.TryGetValue(call.CallId!, out waitingSince))
            {
                waitingSince = now.AddSeconds(-Math.Max(0, call.Duration));
                _waitingSince[call.CallId!] = waitingSince;
            }
        }

        return new QueuedCaller(
            Id: call.CallId!,
            QueueName: call.CalleeDisplayName ?? call.CalleeNumber ?? "coda",
            CallerNumber: call.CallerNumber,
            CallerName: string.IsNullOrWhiteSpace(call.CallerDisplayName) ? null : call.CallerDisplayName,
            WaitingSince: waitingSince,
            // La posizione viene esposta solo se la configurazione dichiara che
            // l'ordine dell'elenco corrisponde all'ordine di servizio. Vedi
            // YeastarOptions.WaitingListIsOrdered.
            Position: _options.WaitingListIsOrdered ? index + 1 : null);
    }

    /// <summary>Rimuove dalla cache i chiamanti che non sono più in coda.</summary>
    private void PruneWaitTimeCache(List<QueuedCaller> waiting)
    {
        lock (_gate)
        {
            if (_waitingSince.Count == 0) return;

            var present = waiting.Select(c => c.Id).ToHashSet();
            var stale = _waitingSince.Keys.Where(id => !present.Contains(id)).ToList();
            foreach (var id in stale) _waitingSince.Remove(id);
        }
    }

    // -----------------------------------------------------------------------
    // Stato agente
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task SetAgentLoginAsync(bool login, CancellationToken cancellationToken)
    {
        foreach (var queueId in _options.QueueIds)
        {
            await _api.PostAsync<YeastarResponse>(
                "queue/agent_login",
                new
                {
                    queue_id = queueId,
                    // L'API accetta piu' agenti per chiamata; qui interessa solo
                    // l'interno di questa postazione.
                    member_list = new[] { _options.Extension },
                    login_status = login ? "login" : "logout",
                },
                cancellationToken).ConfigureAwait(false);
        }

        UpdateAgentState(new AgentState(
            login ? AgentStatus.Available : AgentStatus.LoggedOut,
            Since: _clock.Now));
    }

    /// <inheritdoc />
    public async Task SetAgentPausedAsync(bool paused, string? reasonCode, CancellationToken cancellationToken)
    {
        foreach (var queueId in _options.QueueIds)
        {
            await _api.PostAsync<YeastarResponse>(
                "queue/agent_pause",
                new
                {
                    queue_id = queueId,
                    member_list = new[] { _options.Extension },
                    pause_status = paused ? "pause" : "unpause",
                    pause_reason = reasonCode ?? string.Empty,
                },
                cancellationToken).ConfigureAwait(false);
        }

        UpdateAgentState(new AgentState(
            paused ? AgentStatus.Paused : AgentStatus.Available,
            ReasonCode: reasonCode,
            Since: _clock.Now,
            WasAutomatic: false));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PauseReason>> GetPauseReasonsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _api
                .GetAsync<YeastarPauseReasonListResponse>("queue_pause_reason/list", null, cancellationToken)
                .ConfigureAwait(false);

            return [.. response.Data
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new PauseReason(r.Id.ToString(), r.Name!))];
        }
        catch (YeastarApiException ex)
        {
            // Senza motivi di pausa configurati l'operatore deve poter comunque
            // andare in pausa: si restituisce un elenco vuoto e chi chiama passera'
            // un motivo nullo.
            _log.LogWarning(ex, "Lettura dei motivi di pausa non riuscita");
            return [];
        }
    }

    private async Task RefreshAgentStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var queueId in _options.QueueIds)
            {
                await _api.GetAsync<YeastarResponse>(
                    "queue/agent_status",
                    new Dictionary<string, string> { ["id"] = queueId.ToString() },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (YeastarApiException ex)
        {
            _log.LogDebug(ex, "Lettura dello stato agente non riuscita all'avvio");
        }
    }

    private void UpdateAgentState(AgentState state)
    {
        lock (_gate) _agentState = state;
        AgentStateChanged?.Invoke(this, state);
    }

    // -----------------------------------------------------------------------
    // Prelievo dalla coda
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// L'implementazione usa <c>call/transfer</c> di tipo cieco con destinazione
    /// l'interno dell'operatore: semanticamente è "prendi questa chiamata e portala
    /// a me", cioè il gesto del trascinamento nel Queue Panel di Yeastar.
    /// </para>
    /// <para>
    /// <b>È un'ipotesi implementativa non ancora verificata su PBX reale</b>, e per
    /// questo è disattivata per impostazione predefinita
    /// (<see cref="YeastarOptions.EnableExperimentalQueuePick"/>). Se la verifica
    /// dovesse fallire, la coda resta in sola lettura: il requisito di capitolato
    /// (visualizzare le chiamate e conoscerne il numero) è comunque soddisfatto, ma
    /// va comunicato al cliente prima della firma e non dopo.
    /// </para>
    /// </remarks>
    public async Task<bool> PickCallAsync(string queuedCallId, CancellationToken cancellationToken)
    {
        if (!Capabilities.CanPickSpecificCall)
        {
            throw new NotSupportedException(
                "Il prelievo selettivo dalla coda non e' disponibile con questa configurazione del centralino.");
        }

        try
        {
            await _api.PostAsync<YeastarResponse>(
                "call/transfer",
                new
                {
                    channel_id = queuedCallId,
                    type = "blind",
                    number = _options.Extension,
                },
                cancellationToken).ConfigureAwait(false);

            _refreshRequested = true;
            return true;
        }
        catch (YeastarApiException ex)
        {
            _log.LogWarning(ex, "Prelievo della chiamata {CallId} dalla coda non riuscito", queuedCallId);
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Eventi
    // -----------------------------------------------------------------------

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        if (connected)
        {
            // Gli eventi accaduti mentre il collegamento era caduto sono persi e
            // Yeastar non li recapita in differita: l'unico modo di tornare
            // allineati e' rileggere lo stato dal centralino.
            _refreshRequested = true;
            SetLinkStatus(PbxLinkStatus.Connected);
        }
        else
        {
            SetLinkStatus(PbxLinkStatus.Degraded);
        }
    }

    private void OnEventReceived(object? sender, YeastarEvent evt)
    {
        try
        {
            switch (evt.Type)
            {
                case YeastarEventIds.CallStateChanged:
                case YeastarEventIds.IncomingCallRequest:
                case YeastarEventIds.CallEndDetails:
                case YeastarEventIds.AgentRingingTimeout:
                    // Qualcosa e' cambiato nel traffico: si chiede un riallineamento
                    // invece di dedurre il nuovo stato dall'evento. Dedurre sarebbe
                    // piu' rapido ma introdurrebbe una seconda fonte di verita', e
                    // due fonti che divergono producono annunci contraddittori.
                    _refreshRequested = true;
                    break;

                case YeastarEventIds.AgentStatusChanged:
                    HandleAgentStatusEvent(evt, automatic: false);
                    break;

                case YeastarEventIds.AgentAutomaticPause:
                    HandleAgentStatusEvent(evt, automatic: true);
                    break;

                case YeastarEventIds.ExtensionPresenceChanged:
                    HandlePresenceEvent(evt);
                    break;

                case YeastarEventIds.ExtensionRegistrationChanged:
                    HandleRegistrationEvent(evt);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Evento {Type} del centralino non interpretabile", evt.Type);
        }
    }

    private void HandleAgentStatusEvent(YeastarEvent evt, bool automatic)
    {
        if (!TryGetMessage(evt, out var msg)) return;

        var extension = ReadString(msg, "ext_num") ?? ReadString(msg, "number");
        if (extension is not null && extension != _options.Extension) return;

        var status = automatic
            ? AgentStatus.Paused
            : ReadString(msg, "status")?.ToLowerInvariant() switch
            {
                "login" or "idle" or "available" => AgentStatus.Available,
                "pause" or "paused" => AgentStatus.Paused,
                "logout" => AgentStatus.LoggedOut,
                "busy" => AgentStatus.Busy,
                _ => AgentState.Status,
            };

        UpdateAgentState(new AgentState(
            status,
            ReasonCode: ReadString(msg, "pause_reason"),
            ReasonLabel: ReadString(msg, "pause_reason_name"),
            Since: evt.ReceivedAt,
            WasAutomatic: automatic));
    }

    private void HandlePresenceEvent(YeastarEvent evt)
    {
        if (!TryGetMessage(evt, out var msg)) return;

        var extension = ReadString(msg, "ext_num") ?? ReadString(msg, "number");
        if (extension is null) return;

        var presence = ReadString(msg, "presence_status")?.ToLowerInvariant() switch
        {
            "available" or "online" => PresenceState.Available,
            "busy" or "onthephone" => PresenceState.Busy,
            "ringing" => PresenceState.Ringing,
            "dnd" or "donotdisturb" => PresenceState.DoNotDisturb,
            "away" or "lunchbreak" or "businesstrip" or "offwork" => PresenceState.Away,
            "offline" or "unavailable" => PresenceState.Offline,
            _ => PresenceState.Unknown,
        };

        if (presence == PresenceState.Unknown) return;

        PresenceChanged?.Invoke(this, new PresenceChangedEventArgs(extension, presence, evt.ReceivedAt));
    }

    /// <summary>
    /// Traduce il cambio di stato di registrazione di un interno in presenza.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Un interno non registrato è un telefono spento, scollegato o con la rete
    /// caduta: è la destinazione peggiore per un trasferimento, perché la chiamata
    /// squilla nel vuoto e il chiamante torna indietro dopo il timeout, quando
    /// l'operatore lo ha già salutato. Per l'operatore è «fuori servizio», ed è uno
    /// dei tre stati che gli servono davvero insieme a libero e occupato.
    /// </para>
    /// <para>
    /// <b>Sulla registrazione ritrovata.</b> Sapere che un interno è tornato
    /// raggiungibile non dice se in questo momento è libero: potrebbe essere già in
    /// conversazione. Per questo l'informazione viene contrassegnata come proveniente
    /// dalla registrazione, e chi la riceve decide se sostituire un dato più preciso
    /// arrivato dall'evento di presenza. Qui si riporta ciò che il centralino dice,
    /// senza interpretarlo.
    /// </para>
    /// <para>
    /// I nomi dei campi non sono verificati su firmware reale, come per gli altri
    /// eventi: una forma non riconosciuta produce un dato assente e non un'eccezione.
    /// </para>
    /// </remarks>
    private void HandleRegistrationEvent(YeastarEvent evt)
    {
        if (!TryGetMessage(evt, out var msg)) return;

        var extension = ReadString(msg, "ext_num") ?? ReadString(msg, "number");
        if (extension is null) return;

        var registered = ReadString(msg, "status")?.ToLowerInvariant() switch
        {
            "registered" or "online" or "1" => true,
            "unregistered" or "offline" or "0" => false,
            _ => (bool?)null,
        };

        if (registered is null) return;

        var presence = registered.Value ? PresenceState.Available : PresenceState.Offline;

        _log.LogDebug(
            "Interno {Extension} {Stato} presso il centralino",
            extension,
            registered.Value ? "registrato" : "NON registrato");

        PresenceChanged?.Invoke(
            this, new PresenceChangedEventArgs(extension, presence, evt.ReceivedAt, FromRegistration: true));
    }

    /// <summary>
    /// Estrae il corpo <c>msg</c> di un evento.
    /// </summary>
    /// <remarks>
    /// La lettura dei campi è deliberatamente difensiva: la forma esatta del corpo
    /// varia per tipo di evento e può cambiare fra versioni di firmware. Un campo
    /// mancante deve produrre un dato assente, non un'eccezione che interrompe il
    /// flusso degli altri eventi.
    /// </remarks>
    private static bool TryGetMessage(YeastarEvent evt, out JsonElement message)
    {
        if (evt.Payload.ValueKind == JsonValueKind.Object
            && evt.Payload.TryGetProperty("msg", out message)
            && message.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        message = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void SetLinkStatus(PbxLinkStatus status)
    {
        bool changed;
        lock (_gate)
        {
            changed = _linkStatus != status;
            _linkStatus = status;
        }

        if (changed) LinkStatusChanged?.Invoke(this, status);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _events.EventReceived -= OnEventReceived;
        _events.ConnectionStateChanged -= OnConnectionStateChanged;

        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        _lifetime?.Dispose();
        await _events.DisposeAsync().ConfigureAwait(false);
        _api.Dispose();
    }
}
