using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sullo stato degli interni.
/// </summary>
/// <remarks>
/// È l'informazione che decide se una chiamata si può passare, e sbagliarla ha un
/// costo asimmetrico: dire «occupato» di un interno libero fa perdere qualche secondo,
/// dire «libero» di un interno spento manda il chiamante a squillare nel vuoto e lo
/// riporta all'operatore dopo mezzo minuto, quando è già stato salutato.
/// </remarks>
public sealed class PresenceMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 9, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void SenzaNotizieLoStatoESconosciuto()
    {
        using var servizio = new PresenceMonitorService(new FakePresenceProvider());
        servizio.Start();

        Assert.Equal(PresenceState.Unknown, servizio.Get("4021"));
        Assert.Equal(string.Empty, PresenceMonitorService.Describe(servizio.Get("4021")));
    }

    [Fact]
    public void LoStatoAnnunciatoDalCentralinoDiventaConsultabile()
    {
        var centralino = new FakePresenceProvider();
        using var servizio = new PresenceMonitorService(centralino);
        servizio.Start();

        centralino.Raise("4021", PresenceState.Busy, Now);

        Assert.Equal(PresenceState.Busy, servizio.Get("4021"));
        Assert.Equal("occupato", PresenceMonitorService.Describe(servizio.Get("4021")));
    }

    [Fact]
    public void ILNumeroSiConfrontaSulleSoleCifre()
    {
        var centralino = new FakePresenceProvider();
        using var servizio = new PresenceMonitorService(centralino);
        servizio.Start();

        centralino.Raise("40 21", PresenceState.Available, Now);

        // Il centralino puo' riportare l'interno con spazi o punteggiatura, e la
        // rubrica averlo memorizzato in un'altra forma: senza normalizzazione lo stato
        // arriverebbe e non verrebbe attribuito a nessuno.
        Assert.Equal(PresenceState.Available, servizio.Get("4021"));
        Assert.Equal(PresenceState.Available, servizio.Get("40-21"));
    }

    [Fact]
    public void IlCambiamentoVieneSegnalatoUnaVoltaSola()
    {
        var centralino = new FakePresenceProvider();
        using var servizio = new PresenceMonitorService(centralino);
        servizio.Start();

        var segnalazioni = 0;
        servizio.PresenceChanged += (_, _) => segnalazioni++;

        centralino.Raise("4021", PresenceState.Busy, Now);
        centralino.Raise("4021", PresenceState.Busy, Now.AddSeconds(1));
        centralino.Raise("4021", PresenceState.Available, Now.AddSeconds(2));

        // Ripetere lo stesso stato non e' un cambiamento: farebbe ricostruire la riga
        // dell'elenco sotto le dita di chi lo sta percorrendo, senza motivo.
        Assert.Equal(2, segnalazioni);
    }

    [Fact]
    public void LaRegistrazioneRitrovataNonCancellaUnDatoPiuPreciso()
    {
        var centralino = new FakePresenceProvider();
        using var servizio = new PresenceMonitorService(centralino);
        servizio.Start();

        centralino.Raise("4021", PresenceState.Busy, Now);
        centralino.Raise("4021", PresenceState.Available, Now.AddSeconds(1), fromRegistration: true);

        // Un telefono che si registra di nuovo e' raggiungibile, ma puo' essere gia'
        // in conversazione: il dato dell'evento di presenza resta.
        Assert.Equal(PresenceState.Busy, servizio.Get("4021"));
    }

    [Fact]
    public void UnInternoTornatoDalFuoriServizioRisultaRaggiungibile()
    {
        var centralino = new FakePresenceProvider();
        using var servizio = new PresenceMonitorService(centralino);
        servizio.Start();

        centralino.Raise("4021", PresenceState.Offline, Now);
        centralino.Raise("4021", PresenceState.Available, Now.AddSeconds(1), fromRegistration: true);

        // Qui l'alternativa sarebbe continuare a dire "fuori servizio" di un telefono
        // che nel frattempo e' tornato, che e' l'errore piu' costoso dei due.
        Assert.Equal(PresenceState.Available, servizio.Get("4021"));
    }

    [Fact]
    public void LoStatoInizialeSiPuoSeminare()
    {
        using var servizio = new PresenceMonitorService(new FakePresenceProvider());
        servizio.Start();

        servizio.Seed(
        [
            new("4021", PresenceState.Available),
            new("4022", PresenceState.Busy),
            new("4023", PresenceState.Unknown),
        ]);

        Assert.Equal(PresenceState.Available, servizio.Get("4021"));
        Assert.Equal(PresenceState.Busy, servizio.Get("4022"));

        // Seminare uno stato sconosciuto non aggiunge nulla: sarebbe come dichiarare
        // di sapere qualcosa che non si sa.
        Assert.Equal(2, servizio.KnownCount);
    }

    [Fact]
    public void DopoLoSmaltimentoNonSiAscoltaPiu()
    {
        var centralino = new FakePresenceProvider();
        var servizio = new PresenceMonitorService(centralino);
        servizio.Start();
        servizio.Dispose();

        centralino.Raise("4021", PresenceState.Busy, Now);

        Assert.Equal(PresenceState.Unknown, servizio.Get("4021"));
    }

    [Theory]
    [InlineData(PresenceState.Busy, true)]
    [InlineData(PresenceState.DoNotDisturb, true)]
    [InlineData(PresenceState.Offline, true)]
    [InlineData(PresenceState.Away, true)]
    [InlineData(PresenceState.Available, false)]
    [InlineData(PresenceState.Unknown, false)]
    [InlineData(PresenceState.Ringing, false)]
    public void SoloAlcuniStatiSconsiglianoIlTrasferimento(PresenceState stato, bool atteso)
    {
        // "Squilla" non e' fra questi: un interno che squilla puo' rispondere da un
        // momento all'altro, e con il chiamante in attesa il tempo c'e'.
        Assert.Equal(atteso, PresenceMonitorService.DiscouragesTransfer(stato));
    }

    [Fact]
    public void SenzaCentralinoLaFunzioneSiDichiaraAssente()
    {
        using var servizio = new PresenceMonitorService(new FakePresenceProvider(canReadPresence: false));

        // "Non lo so per questo interno" e "non lo sapro' mai per nessuno" sono due
        // cose diverse, e l'operatore deve poterle distinguere.
        Assert.False(servizio.IsAvailable);
    }

    [Fact]
    public async Task LoStatoGiaNotoTornaSubito()
    {
        var centralino = new FakePresenceProvider();
        using var servizio = new PresenceMonitorService(centralino);
        servizio.Start();

        centralino.Raise("4021", PresenceState.Busy, Now);

        var stato = await servizio.QueryAsync("4021", TimeSpan.FromSeconds(5));

        Assert.Equal(PresenceState.Busy, stato);
    }

    [Fact]
    public async Task UnoStatoCheNonArrivaNonBloccaLOperatore()
    {
        var centralino = new FakePresenceProvider();
        using var servizio = new PresenceMonitorService(centralino);
        servizio.Start();

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var stato = await servizio.QueryAsync("4099", TimeSpan.FromMilliseconds(200));
        cronometro.Stop();

        // Meglio "non lo so" dopo un attimo che un'attesa indefinita con un chiamante
        // in linea.
        Assert.Equal(PresenceState.Unknown, stato);
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(3), $"Attesa troppo lunga: {cronometro.Elapsed}");
    }

    [Fact]
    public async Task LaRispostaCheArrivaDuranteLAttesaVieneRaccolta()
    {
        var centralino = new FakePresenceProvider();
        using var servizio = new PresenceMonitorService(centralino);
        servizio.Start();

        // E' il caso normale sulla via SIP: si sottoscrive l'interno e la risposta del
        // centralino arriva dopo un giro di rete.
        var interrogazione = servizio.QueryAsync("4021", TimeSpan.FromSeconds(5));

        await Task.Delay(50);
        centralino.Raise("4021", PresenceState.Available, Now);

        Assert.Equal(PresenceState.Available, await interrogazione);
    }

    [Fact]
    public async Task SenzaSorgenteNonSiAttendeNulla()
    {
        using var servizio = new PresenceMonitorService(new FakePresenceProvider(canReadPresence: false));
        servizio.Start();

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var stato = await servizio.QueryAsync("4021", TimeSpan.FromSeconds(5));
        cronometro.Stop();

        Assert.Equal(PresenceState.Unknown, stato);
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(1), "Senza sorgente la risposta deve essere immediata");
    }

    /// <summary>Centralino finto che si limita a segnalare cambi di presenza.</summary>
    private sealed class FakePresenceProvider(bool canReadPresence = true) : IQueueProvider
    {
        public string Name => "finto";

        public QueueProviderCapabilities Capabilities =>
            new(false, false, false, false, false, false, CanReadPresence: canReadPresence);

        public PbxLinkStatus LinkStatus => PbxLinkStatus.Connected;

        public QueueSnapshot CurrentSnapshot => QueueSnapshot.Empty(Now);

        public AgentState AgentState => AgentState.Unknown;

        // Eventi richiesti dall'interfaccia e non usati da queste prove: il
        // sollevamento esplicito toglie l'avviso del compilatore senza fingere un
        // comportamento che il finto non ha.
        public event EventHandler<QueueSnapshot>? SnapshotUpdated
        {
            add { }
            remove { }
        }

        public event EventHandler<AgentState>? AgentStateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<PbxLinkStatus>? LinkStatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

        public void Raise(string extension, PresenceState state, DateTimeOffset at, bool fromRegistration = false)
            => PresenceChanged?.Invoke(this, new PresenceChangedEventArgs(extension, state, at, fromRegistration));

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<QueueSnapshot> RefreshAsync(CancellationToken cancellationToken)
            => Task.FromResult(QueueSnapshot.Empty(Now));

        public Task SetAgentLoginAsync(bool login, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetAgentPausedAsync(bool paused, string? reasonCode, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<PauseReason>> GetPauseReasonsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PauseReason>>([]);

        public Task<bool> PickCallAsync(string queuedCallId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
