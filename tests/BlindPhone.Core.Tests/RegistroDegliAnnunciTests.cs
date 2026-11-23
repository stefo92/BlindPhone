using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Il registro dice che cosa è stato pronunciato e che cosa no, e perché.
/// </summary>
/// <remarks>
/// Davanti a «non me l'ha detto» il registro deve distinguere quattro casi: detto,
/// scartato all'ingresso, scartato alla scadenza della finestra di accorpamento,
/// sostituito da uno più recente. Prima sapeva riconoscere soltanto il secondo.
/// </remarks>
public sealed class RegistroDegliAnnunciTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 9, 0, 0, TimeSpan.FromHours(1));

    private sealed class RegistroInMemoria : ILogger<AnnouncementService>
    {
        public List<(LogLevel Level, string Text)> Righe { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Righe.Add((logLevel, formatter(state, exception)));

        public bool Contiene(string frammento)
            => Righe.Any(r => r.Text.Contains(frammento, StringComparison.Ordinal));
    }

    private static (AnnouncementService Service, RegistroInMemoria Log, AnnouncementContext Context, ManualClock Clock) Build()
    {
        var context = new AnnouncementContext();
        var clock = new ManualClock(Start);
        var log = new RegistroInMemoria();

        var service = new AnnouncementService(
            new RecordingSpeechOutput(), new RecordingEarconPlayer(), context,
            options: null, clock: clock, ducker: null, logger: log);

        return (service, log, context, clock);
    }

    [Fact]
    public void LAnnuncioPronunciato_SiScriveConIlTesto()
    {
        var (service, log, _, clock) = Build();

        service.Announce(Announcement.High(
            "Chiamate esterne trasferito a tre, sei, tre, otto", clock.Now, coalesceKey: "transfer:result"));
        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        Assert.True(log.Contiene("Annuncio pronunciato"));
        Assert.True(log.Contiene("Chiamate esterne trasferito a tre, sei, tre, otto"));
    }

    [Fact]
    public void LAnnuncioCritico_SiScriveComeInterruzione()
    {
        var (service, log, _, clock) = Build();

        service.Announce(Announcement.Critical("Chiamata da Mario Rossi", clock.Now));

        Assert.True(log.Contiene("interrompe"));
        Assert.True(log.Contiene("Chiamata da Mario Rossi"));
    }

    [Fact]
    public void LoScartoAllaScadenzaDellaFinestra_SiScriveConIlMotivo()
    {
        var (service, log, context, clock) = Build();

        // Ammesso all'ingresso, scartato al pump: l'operatore ha cominciato a digitare
        // nel frattempo.
        service.Announce(Announcement.Normal("3 chiamate in coda", clock.Now, coalesceKey: "queue:count"));
        context.NoteTyping(clock.Now);
        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        Assert.True(log.Contiene("Annuncio scartato (OperatorTyping): 3 chiamate in coda"));
        Assert.False(log.Contiene("Annuncio pronunciato"));
    }

    [Fact]
    public void LAnnuncioSostituitoDaUnoPiuRecente_SiScrive()
    {
        var (service, log, _, clock) = Build();

        service.Announce(Announcement.Normal("1 chiamata in coda", clock.Now, coalesceKey: "queue:count"));
        clock.AdvanceMilliseconds(100);
        service.Announce(Announcement.Normal("2 chiamate in coda", clock.Now, coalesceKey: "queue:count"));

        Assert.True(log.Contiene("Annuncio accorpato in uno piu' recente (queue:count): 1 chiamata in coda"));
    }
}
