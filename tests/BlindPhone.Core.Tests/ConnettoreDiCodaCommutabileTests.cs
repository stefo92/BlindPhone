using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che il connettore di coda si possa sostituire senza riavviare, e senza
/// che chi ci si è agganciato se ne accorga.
/// </summary>
/// <remarks>
/// Il collegamento all'API del centralino si sceglieva all'avvio: attivarlo, o
/// correggerne l'indirizzo, chiedeva di riavviare BlindPhone, cioè di smettere di
/// ricevere chiamate per il tempo di un avvio. Ora si sostituisce sotto l'involucro,
/// e la finestra, gli annunci della coda e la sorveglianza degli interni restano
/// agganciati dove sono.
/// </remarks>
public sealed class ConnettoreDiCodaCommutabileTests
{
    /// <summary>Connettore finto che registra che cosa gli è stato chiesto.</summary>
    private sealed class ConnettoreFinto(string nome, PbxLinkStatus stato = PbxLinkStatus.Disconnected)
        : IQueueProvider
    {
        public int Avvii { get; private set; }

        public bool Chiuso { get; private set; }

        public string Name => nome;

        public QueueProviderCapabilities Capabilities => QueueProviderCapabilities.None;

        public PbxLinkStatus LinkStatus => stato;

        public QueueSnapshot CurrentSnapshot { get; } = QueueSnapshot.Empty(DateTimeOffset.UnixEpoch);

        public AgentState AgentState => AgentState.Unknown;

        public event EventHandler<QueueSnapshot>? SnapshotUpdated;

        public event EventHandler<AgentState>? AgentStateChanged;

        public event EventHandler<PbxLinkStatus>? LinkStatusChanged;

        public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

        public void SollevaFotografia() => SnapshotUpdated?.Invoke(this, CurrentSnapshot);

        public void SollevaTutto()
        {
            AgentStateChanged?.Invoke(this, AgentState.Unknown);
            LinkStatusChanged?.Invoke(this, PbxLinkStatus.Connected);
            PresenceChanged?.Invoke(this, new PresenceChangedEventArgs("4021", PresenceState.Available, DateTimeOffset.UnixEpoch));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Avvii++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<QueueSnapshot> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(CurrentSnapshot);

        public Task SetAgentLoginAsync(bool login, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetAgentPausedAsync(bool paused, string? reasonCode, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<PauseReason>> GetPauseReasonsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PauseReason>>([]);

        public Task<bool> PickCallAsync(string queuedCallId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public ValueTask DisposeAsync()
        {
            Chiuso = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task GliEventiArrivanoDalNuovoConnettoreENonPiuDalVecchio()
    {
        // Chi si e' agganciato all'avvio deve continuare a sentire la coda dopo il
        // cambio, e non deve piu' sentire quella di un collegamento abbandonato.
        var vecchio = new ConnettoreFinto("vecchio");
        var nuovo = new ConnettoreFinto("nuovo");
        var coda = new SwitchableQueueProvider(vecchio);

        var ricevute = 0;
        coda.SnapshotUpdated += (mittente, _) =>
        {
            Assert.Same(coda, mittente);
            ricevute++;
        };

        await coda.SwitchAsync(nuovo);

        vecchio.SollevaFotografia();
        Assert.Equal(0, ricevute);

        nuovo.SollevaFotografia();
        Assert.Equal(1, ricevute);
        Assert.Equal("nuovo", coda.Name);
    }

    [Fact]
    public async Task TuttiGliEventiPassanoDallInvolucro()
    {
        var nuovo = new ConnettoreFinto("nuovo");
        var coda = new SwitchableQueueProvider(new ConnettoreFinto("vecchio"));

        var agente = 0;
        var collegamento = 0;
        var presenza = 0;
        coda.AgentStateChanged += (_, _) => agente++;
        coda.LinkStatusChanged += (_, _) => collegamento++;
        coda.PresenceChanged += (_, _) => presenza++;

        await coda.SwitchAsync(nuovo);
        nuovo.SollevaTutto();

        Assert.Equal(1, agente);
        Assert.Equal(1, collegamento);
        Assert.Equal(1, presenza);
    }

    [Fact]
    public async Task SeEraAvviatoIlNuovoConnettoreParteDaSolo()
    {
        // E' il caso di chi accende l'API dalle impostazioni: il collegamento si apre
        // al salvataggio, non al riavvio.
        var nuovo = new ConnettoreFinto("nuovo");
        var coda = new SwitchableQueueProvider(new ConnettoreFinto("vecchio"));

        await coda.StartAsync(CancellationToken.None);
        await coda.SwitchAsync(nuovo);

        Assert.Equal(1, nuovo.Avvii);
    }

    [Fact]
    public async Task SeNonEraAvviatoIlNuovoConnettoreAspetta()
    {
        // Un cambio arrivato prima dell'avvio dei servizi non deve aprire un
        // collegamento che l'avvio aprirebbe una seconda volta.
        var nuovo = new ConnettoreFinto("nuovo");
        var coda = new SwitchableQueueProvider(new ConnettoreFinto("vecchio"));

        await coda.SwitchAsync(nuovo);

        Assert.Equal(0, nuovo.Avvii);
    }

    [Fact]
    public async Task IlVecchioConnettoreVieneChiuso()
    {
        var vecchio = new ConnettoreFinto("vecchio");
        var coda = new SwitchableQueueProvider(vecchio);

        await coda.SwitchAsync(new ConnettoreFinto("nuovo"));

        Assert.True(vecchio.Chiuso);
    }

    [Fact]
    public async Task UnCollegamentoDisattivatoSiAnnunciaAncheSeNonCambiaMaiDaSolo()
    {
        // Il connettore inerte non solleva mai il cambio di stato: senza l'involucro
        // chi guarda il collegamento resterebbe su «collegato» dopo averlo spento.
        var coda = new SwitchableQueueProvider(new ConnettoreFinto("vecchio", PbxLinkStatus.Connected));

        PbxLinkStatus? annunciato = null;
        coda.LinkStatusChanged += (_, stato) => annunciato = stato;

        await coda.SwitchAsync(new DisabledQueueProvider());

        Assert.Equal(PbxLinkStatus.Disabled, annunciato);
        Assert.Equal(PbxLinkStatus.Disabled, coda.LinkStatus);
    }
}
