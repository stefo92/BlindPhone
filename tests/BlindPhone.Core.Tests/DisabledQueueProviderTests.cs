using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che un collegamento al centralino disattivato venga raccontato come
/// una scelta e non come un guasto.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da un difetto vero. Su un centralino che non espone l'API, ogni pressione
/// del tasto di conteggio rispondeva <i>"dettaglio coda non disponibile,
/// collegamento al centralino interrotto"</i>. La frase è tecnicamente esatta ma
/// manda a cercare un guasto che non esiste, e su un comando premuto decine di
/// volte al giorno diventa un ostacolo invece di un'informazione.
/// </para>
/// <para>
/// È un difetto che non si manifesta a chi sviluppa con un PBX di prova
/// correttamente configurato: si manifesta solo a chi ha un centralino senza API.
/// </para>
/// </remarks>
public sealed class DisabledQueueProviderTests
{
    [Fact]
    public void IlConnettoreInerteSiDichiaraDisattivatoENonDisconnesso()
    {
        // La distinzione e' il perno di tutto il resto: Disconnected significa
        // "doveva collegarsi e non ci e' riuscito", Disabled significa "nessuno
        // glielo ha chiesto".
        var provider = new DisabledQueueProvider();

        Assert.Equal(PbxLinkStatus.Disabled, provider.LinkStatus);
        Assert.NotEqual(PbxLinkStatus.Disconnected, provider.LinkStatus);
    }

    [Fact]
    public void IlConnettoreInerteNonDichiaraAlcunaCapacita()
    {
        var provider = new DisabledQueueProvider();

        Assert.Equal(QueueProviderCapabilities.None, provider.Capabilities);
        Assert.False(provider.Capabilities.CanReadWaitingList);
    }

    [Fact]
    public void ConIlCollegamentoDisattivatoNonSiParlaDiInterruzione()
    {
        var announcer = new RecordingAnnouncer();
        var monitor = new QueueMonitorService(new DisabledQueueProvider(), announcer);

        monitor.AnnounceCount();

        // La parola "interrotto" descrive un guasto: qui non ce n'e' nessuno.
        Assert.False(announcer.Said("interrotto"));
        Assert.True(announcer.Said("non attivo"));
    }

    [Fact]
    public void ConIlCollegamentoDisattivatoAncheIlDettaglioTaceSulGuasto()
    {
        var announcer = new RecordingAnnouncer();
        var monitor = new QueueMonitorService(new DisabledQueueProvider(), announcer);

        monitor.AnnounceWaitingList();

        Assert.False(announcer.Said("interrotto"));
        Assert.True(announcer.Said("non attivo"));
    }

    [Fact]
    public void UnCollegamentoDavveroCadutoContinuaAEssereSegnalatoComeTale()
    {
        // Il contrario del rischio precedente: ammorbidire il messaggio per tutti
        // toglierebbe la segnalazione di caduta a chi l'API ce l'ha e ne dipende.
        var announcer = new RecordingAnnouncer();
        var monitor = new QueueMonitorService(new FailedQueueProvider(), announcer);

        monitor.AnnounceCount();

        Assert.True(announcer.Said("interrotto"));
    }

    /// <summary>
    /// Connettore che dichiara un collegamento caduto: serve a verificare che il
    /// messaggio di guasto non sia stato ammorbidito per tutti.
    /// </summary>
    private sealed class FailedQueueProvider : IQueueProvider
    {
        public string Name => "Finto, collegamento caduto";

        public QueueProviderCapabilities Capabilities => QueueProviderCapabilities.None;

        public PbxLinkStatus LinkStatus => PbxLinkStatus.Failed;

        public Domain.QueueSnapshot CurrentSnapshot { get; } = Domain.QueueSnapshot.Empty(DateTimeOffset.UnixEpoch);

        public Domain.AgentState AgentState => Domain.AgentState.Unknown;

#pragma warning disable CS0067
        public event EventHandler<Domain.QueueSnapshot>? SnapshotUpdated;
        public event EventHandler<Domain.AgentState>? AgentStateChanged;
        public event EventHandler<PbxLinkStatus>? LinkStatusChanged;
        public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Domain.QueueSnapshot> RefreshAsync(CancellationToken cancellationToken)
            => Task.FromResult(CurrentSnapshot);

        public Task SetAgentLoginAsync(bool login, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetAgentPausedAsync(bool paused, string? reasonCode, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Domain.PauseReason>> GetPauseReasonsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Domain.PauseReason>>([]);

        public Task<bool> PickCallAsync(string queuedCallId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task IlPrelievoDallaCodaNonEDisponibileSenzaCollegamento()
    {
        var provider = new DisabledQueueProvider();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.PickCallAsync("qualunque", CancellationToken.None));
    }

    [Fact]
    public async Task AvvioEArrestoNonFallisconoMai()
    {
        // Il connettore inerte viene avviato dalla finestra principale come
        // qualunque altro: se sollevasse eccezioni, l'operatore sentirebbe un
        // avviso di errore proprio nel caso in cui non c'e' nulla che non va.
        var provider = new DisabledQueueProvider();

        await provider.StartAsync(CancellationToken.None);
        var snapshot = await provider.RefreshAsync(CancellationToken.None);
        await provider.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();

        Assert.Equal(0, snapshot.WaitingCount);
    }
}
