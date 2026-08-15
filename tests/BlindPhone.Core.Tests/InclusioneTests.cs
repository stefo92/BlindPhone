using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica l'inclusione su interno occupato (RFC 3911).
/// </summary>
/// <remarks>
/// <para>
/// <b>La prova che conta più di tutte è che il segnale parta.</b> Entrare nella
/// conversazione di due persone è ammissibile solo se loro lo sanno: senza il segnale
/// periodico questa funzione non è una funzione da posto operatore, è un ascolto
/// silenzioso. Le prove qui sotto trattano il segnale come parte del contratto, non
/// come una rifinitura.
/// </para>
/// <para>
/// La seconda famiglia riguarda <b>quando il comando non deve fare niente</b>: interno
/// libero, stato ignoto, centralino che non comunica gli identificativi della
/// conversazione. In tutti quei casi l'inclusione non ha nulla a cui unirsi, e
/// tentarla produrrebbe un errore che all'operatore non spiega niente.
/// </para>
/// </remarks>
public sealed class InclusioneTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(2));

    private const string Interno = "4021";

    /// <summary>Sorgente di presenza governabile dalla prova.</summary>
    private sealed class FakePresenceSource : IPresenceSource
    {
        public string Name => "prova";

        public bool IsAvailable => true;

        public bool RequiresExplicitWatch => true;

        public DialogReference? Dialog { get; set; }

        public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

        public void Start() { }

        public Task WatchAsync(IReadOnlyCollection<string> extensions, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public DialogReference? ActiveDialog(string extension) => Dialog;

        public void Simulate(string extension, PresenceState state)
            => PresenceChanged?.Invoke(this, new PresenceChangedEventArgs(extension, state, DateTimeOffset.Now));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Centralino senza API: obbliga il sorvegliante a usare la via SIP.</summary>
    private sealed class SenzaApiCentralino : IQueueProvider
    {
        public string Name => "senza API";

        public QueueProviderCapabilities Capabilities =>
            new(false, false, false, false, false, false, CanReadPresence: false);

        public PbxLinkStatus LinkStatus => PbxLinkStatus.Disconnected;

        public QueueSnapshot CurrentSnapshot => QueueSnapshot.Empty(DateTimeOffset.Now);

        public AgentState AgentState => AgentState.Unknown;

        public event EventHandler<QueueSnapshot>? SnapshotUpdated { add { } remove { } }

        public event EventHandler<PbxLinkStatus>? LinkStatusChanged { add { } remove { } }

        public event EventHandler<PresenceChangedEventArgs>? PresenceChanged { add { } remove { } }

        public event EventHandler<AgentState>? AgentStateChanged { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<QueueSnapshot> RefreshAsync(CancellationToken cancellationToken)
            => Task.FromResult(QueueSnapshot.Empty(DateTimeOffset.Now));

        public Task SetAgentLoginAsync(bool login, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SetAgentPausedAsync(bool paused, string? reasonCode, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<PauseReason>> GetPauseReasonsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PauseReason>>([]);

        public Task<bool> PickCallAsync(string queuedCallId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public CallSessionManager Calls { get; }

        public IntrusionMonitor Intrusion { get; }

        public Harness()
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), new AnnouncementContext(),
                contacts: null, options: null, clock: Clock);

            Intrusion = new IntrusionMonitor(Engine, Calls, Announcer, Clock);
        }
    }

    private static DialogReference Dialogo(string interno = Interno) =>
        new(interno, "abc123@pbx", "tag-interno", "tag-altro", Start);

    // -----------------------------------------------------------------------
    // Il segnale che dichiara l'ascolto
    // -----------------------------------------------------------------------

    [Fact]
    public void IlSegnaleParteSubito()
    {
        // Non alla prima scadenza: se il primo avviso arrivasse dopo cinque secondi,
        // l'operatore avrebbe gia' ascoltato cinque secondi di conversazione senza
        // che nessuno lo sapesse.
        var h = new Harness();

        h.Intrusion.Begin("call-1", Interno);
        h.Intrusion.Pump(h.Clock.Now);

        Assert.Single(h.Engine.RemoteAnnouncements);
    }

    [Fact]
    public void IlSegnaleSiRipeteAllaCadenzaPrevista()
    {
        var h = new Harness();

        h.Intrusion.Begin("call-1", Interno);
        h.Intrusion.Pump(h.Clock.Now);

        // Quattro secondi non bastano, cinque si'.
        h.Clock.Advance(TimeSpan.FromSeconds(4));
        h.Intrusion.Pump(h.Clock.Now);
        Assert.Single(h.Engine.RemoteAnnouncements);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Intrusion.Pump(h.Clock.Now);
        Assert.Equal(2, h.Engine.RemoteAnnouncements.Count);
    }

    [Fact]
    public void IlSegnaleVaInLineaENonNellaCuffiaDellOperatore()
    {
        // Un avviso che sente solo chi si e' incluso non avverte nessuno: ricorda a
        // lui una cosa che sa gia'. Deve arrivare all'interno e al suo interlocutore.
        var h = new Harness();

        h.Intrusion.Begin("call-9", Interno);
        h.Intrusion.Pump(h.Clock.Now);

        Assert.StartsWith("call-9:", h.Engine.RemoteAnnouncements[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ChiusaLaChiamataIlSegnaleSiFerma()
    {
        // Da qualunque parte arrivi la chiusura: il tasto dell'operatore, l'interno
        // che riaggancia, il centralino che scioglie tutto.
        var h = new Harness();

        var callId = h.Calls.RegisterOutboundCall("call-1", new CallParty(Interno)).Id;
        h.Intrusion.Begin(callId, Interno);
        h.Intrusion.Pump(h.Clock.Now);

        h.Calls.ApplyTrigger(callId, CallTrigger.HangUp, suppressAnnouncement: true);

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        h.Intrusion.Pump(h.Clock.Now);

        Assert.False(h.Intrusion.IsActive);
        Assert.Single(h.Engine.RemoteAnnouncements);
    }

    [Fact]
    public void SenzaInclusioneInCorsoNonSiImmetteNiente()
    {
        var h = new Harness();

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Intrusion.Pump(h.Clock.Now);

        Assert.Empty(h.Engine.RemoteAnnouncements);
    }

    // -----------------------------------------------------------------------
    // La forma del segnale
    // -----------------------------------------------------------------------

    [Fact]
    public void IlSegnaleDuraTrecentoMillisecondi()
    {
        // E' la specifica del posto operatore, ed e' anche il compromesso: abbastanza
        // per accorgersene, abbastanza poco da cadere fra due parole.
        Assert.Equal(300, IntrusionTone.Duration.TotalMilliseconds);
        Assert.Equal(5, IntrusionTone.Interval.TotalSeconds);

        var campioni = IntrusionTone.Render(8000);

        Assert.Equal(2400, campioni.Length);
    }

    [Fact]
    public void IlSegnaleNonPartENonFinisceDiNetto()
    {
        // Un tono che parte e finisce di colpo produce uno schiocco udibile sui codec
        // a banda stretta, e su una linea telefonica lo schiocco da' piu' fastidio del
        // tono.
        var campioni = IntrusionTone.Render(8000);

        Assert.True(Math.Abs((int)campioni[0]) < 2000);
        Assert.True(Math.Abs((int)campioni[^1]) < 2000);
        Assert.True(campioni.Select(c => Math.Abs((int)c)).Max() > 4000);
    }

    [Fact]
    public void UnaFrequenzaDiCampionamentoAssurdaNonRompeNiente()
    {
        Assert.Empty(IntrusionTone.Render(0));
        Assert.Empty(IntrusionTone.Render(-8000));
    }

    // -----------------------------------------------------------------------
    // L'intestazione Join dell'RFC 3911
    // -----------------------------------------------------------------------

    [Fact]
    public void LIntestazioneJoinHaLaFormaPrevistaDallaSpecifica()
    {
        // RFC 3911: identificativo di chiamata, poi to-tag e from-tag. L'ordine delle
        // due etichette non e' indifferente — to-tag e' la parte a cui l'invito
        // arriva, cioe' l'interno — e scambiarle produrrebbe un rifiuto del centralino
        // impossibile da interpretare.
        var dialogo = new DialogReference(Interno, "xyz@pbx", "locale", "remoto", Start);

        Assert.Equal("xyz@pbx;to-tag=locale;from-tag=remoto", dialogo.ToJoinHeaderValue());
    }

    [Theory]
    [InlineData("", "l", "r")]
    [InlineData("c", "", "r")]
    [InlineData("c", "l", "")]
    public void UnDialogoIncompletoNonSiPuoRaggiungere(string callId, string locale, string remoto)
    {
        // Succede sui dialoghi in instaurazione, dove l'etichetta della parte remota
        // non esiste ancora: non c'e' nessuna conversazione in cui entrare.
        Assert.False(new DialogReference(Interno, callId, locale, remoto, Start).IsComplete);
    }

    // -----------------------------------------------------------------------
    // Quando il comando non deve fare niente
    // -----------------------------------------------------------------------

    private static async Task<(TransferOrchestrator Transfer, FakeEngine Engine, RecordingAnnouncer Announcer,
        FakePresenceSource Presence, PresenceMonitorService Monitor)> ConTrasferimentoVersoAsync(
        PresenceState stato, DialogReference? dialogo)
    {
        var engine = new FakeEngine();
        var announcer = new RecordingAnnouncer();
        var clock = new ManualClock(Start);

        var calls = new CallSessionManager(
            engine, announcer, new CallAnnouncementMapper(), new AnnouncementContext(),
            contacts: null, options: null, clock: clock);

        var presenza = new FakePresenceSource { Dialog = dialogo };
        engine.PresenceSource = presenza;

        // Il sorvegliante prende la sorgente dal motore telefonico quando il
        // centralino non espone l'API: e' la strada delle sottoscrizioni SIP, l'unica
        // che porta gli identificativi del dialogo.
        var monitor = new PresenceMonitorService(new SenzaApiCentralino(), engine);
        monitor.Start();

        var intrusione = new IntrusionMonitor(engine, calls, announcer, clock);

        var transfer = new TransferOrchestrator(
            engine, calls, announcer,
            contacts: null,
            options: new TransferOptions { ConfirmExternalDestinations = false },
            clock: clock,
            logger: null,
            presence: monitor,
            intrusion: intrusione);

        // Chiamata in corso, poi si avvia il trasferimento verso l'interno.
        engine.SimulateIncoming("0212345678", "Mario Rossi");
        await Task.Delay(20);
        await calls.AnswerAsync();

        await transfer.HandleTransferKeyAsync();
        await transfer.SetDestinationAsync(Interno);

        presenza.Simulate(Interno, stato);
        await Task.Delay(20);

        announcer.Clear();
        engine.Operations.Clear();

        return (transfer, engine, announcer, presenza, monitor);
    }

    [Fact]
    public async Task ConInternoOccupatoEDialogoNotoLInclusioneSiPuoFare()
    {
        var (transfer, engine, announcer, _, _) =
            await ConTrasferimentoVersoAsync(PresenceState.Busy, Dialogo());

        Assert.True(transfer.CanBargeIn);

        var esito = await transfer.BargeInAsync();

        Assert.True(esito);
        Assert.True(engine.Did($"barge:{Interno}:"));

        // L'annuncio dice anche cosa sentono gli altri: chi preme sta per parlare
        // dentro la conversazione di due persone.
        Assert.True(announcer.Said("segnale"));
    }

    [Fact]
    public async Task ConInternoLiberoLInclusioneNonSiPropone()
    {
        var (transfer, engine, announcer, _, _) =
            await ConTrasferimentoVersoAsync(PresenceState.Available, Dialogo());

        Assert.False(transfer.CanBargeIn);
        Assert.False(await transfer.BargeInAsync());

        Assert.False(engine.Did("barge:"));
        Assert.True(announcer.Said("non disponibile"));
    }

    [Fact]
    public async Task SenzaGliIdentificativiDelDialogoLInclusioneNonSiPropone()
    {
        // E' il caso dei centralini che riportano lo stato senza il dettaglio del
        // dialogo, e di quelli serviti dalla sola API: l'interno risulta occupato ma
        // non c'e' niente a cui agganciare l'invito.
        var (transfer, engine, _, _, _) =
            await ConTrasferimentoVersoAsync(PresenceState.Busy, dialogo: null);

        Assert.False(transfer.CanBargeIn);
        Assert.False(await transfer.BargeInAsync());
        Assert.False(engine.Did("barge:"));
    }

    [Fact]
    public async Task SeIlCentralinoRifiutaLOperatoreLoSenteDire()
    {
        // L'RFC 3911 e' opzionale: un centralino che non lo sostiene rifiuta l'invito.
        // Restare davanti a un comando muto sarebbe il modo peggiore di scoprirlo.
        var (transfer, engine, announcer, _, _) =
            await ConTrasferimentoVersoAsync(PresenceState.Busy, Dialogo());

        engine.BargeInFails = true;

        Assert.False(await transfer.BargeInAsync());
        Assert.True(announcer.Said("non riuscita"));
        Assert.True(announcer.Said("ancora in attesa"));
    }

    [Fact]
    public async Task IlChiamanteRestaInAttesaDuranteLInclusione()
    {
        // Non viene ripreso e non viene chiuso: l'operatore si affaccia nella
        // conversazione del collega, non gli fa assistere la trattativa.
        var (transfer, engine, _, _, _) =
            await ConTrasferimentoVersoAsync(PresenceState.Busy, Dialogo());

        await transfer.BargeInAsync();

        Assert.False(engine.Did("resume:"));
        Assert.False(engine.Did("hangup:in-"));
    }
}
