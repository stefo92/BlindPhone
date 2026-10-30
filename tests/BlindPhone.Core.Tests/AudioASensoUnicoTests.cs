using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// L'avviso che la voce dell'interlocutore non arriva.
/// </summary>
/// <remarks>
/// È il guasto tipico del lavoro agile e non ha nessun altro segnale: l'operatore sente
/// silenzio e aspetta. Le prove dicono quando l'avviso parte e soprattutto quando non
/// deve partire — in attesa, nei primi secondi, quando è la postazione a non trasmettere.
/// </remarks>
public sealed class AudioASensoUnicoTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(1));

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public CallSessionManager Calls { get; }

        public OneWayAudioMonitor Monitor { get; }

        public Harness()
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), new AnnouncementContext(), contacts: null,
                options: null, clock: new ManualClock(Start), logger: null, earcons: null, conference: null);

            Monitor = new OneWayAudioMonitor(Engine, Calls, Announcer);
        }

        public async Task<string> InConversazioneAsync()
        {
            var callId = Engine.SimulateIncoming("0212345678", "Mario Rossi");
            await Task.Delay(20);
            await Calls.AnswerAsync();
            return callId;
        }

        /// <summary>Fa passare il tempo, con i pacchetti contati come direbbe il motore.</summary>
        public void Al(string callId, double secondi, long inviati, long ricevuti)
        {
            Engine.MediaFlows[callId] = new MediaFlowSnapshot(inviati, ricevuti);
            Monitor.Pump(Start.AddSeconds(secondi));
        }

        public List<Announcement> Avvisi
            => [.. Announcer.Announcements.Where(a => a.Text == AnnouncementTexts.OneWayAudio)];
    }

    [Fact]
    public void LaVocePartitaSenzaRispostaPerSeiSecondiVieneDetta()
    {
        var h = new Harness();
        var callId = h.InConversazioneAsync().GetAwaiter().GetResult();

        h.Al(callId, 0, 0, 0);
        h.Al(callId, 3, 150, 0);
        Assert.Empty(h.Avvisi);

        h.Al(callId, 6, 300, 0);

        var avviso = Assert.Single(h.Avvisi);

        // Deve passare durante la conversazione, che e' proprio il momento in cui gli
        // annunci normali tacciono.
        Assert.True(avviso.BypassConversationSilence);
        Assert.Equal(AnnouncementPriority.High, avviso.Priority);
    }

    [Fact]
    public void LAvvisoSiDaUnaVoltaSola()
    {
        var h = new Harness();
        var callId = h.InConversazioneAsync().GetAwaiter().GetResult();

        h.Al(callId, 0, 0, 0);
        h.Al(callId, 6, 300, 0);
        h.Al(callId, 12, 600, 0);
        h.Al(callId, 30, 1500, 0);

        Assert.Single(h.Avvisi);
    }

    [Fact]
    public void SeLaVoceArrivaNonSiDiceNiente()
    {
        var h = new Harness();
        var callId = h.InConversazioneAsync().GetAwaiter().GetResult();

        h.Al(callId, 0, 0, 0);
        h.Al(callId, 6, 300, 1);
        h.Al(callId, 20, 1000, 700);

        Assert.Empty(h.Avvisi);
    }

    [Fact]
    public void SeLaPostazioneNonTrasmetteNonEUnProblemaDiRete()
    {
        // Senza voce in uscita il silenzio in ingresso non prova niente: e' il caso del
        // microfono, e ha la sua diagnosi.
        var h = new Harness();
        var callId = h.InConversazioneAsync().GetAwaiter().GetResult();

        h.Al(callId, 0, 0, 0);
        h.Al(callId, 10, 40, 0);

        Assert.Empty(h.Avvisi);
    }

    [Fact]
    public void InAttesaIlSilenzioELegittimo()
    {
        var h = new Harness();
        var callId = h.InConversazioneAsync().GetAwaiter().GetResult();

        h.Al(callId, 0, 0, 0);
        h.Calls.ToggleHoldAsync().GetAwaiter().GetResult();

        h.Al(callId, 10, 500, 0);
        h.Al(callId, 20, 1000, 0);

        Assert.Empty(h.Avvisi);
    }

    [Fact]
    public void DopoLaRipresaSiRicominciaAContare()
    {
        var h = new Harness();
        var callId = h.InConversazioneAsync().GetAwaiter().GetResult();

        h.Al(callId, 0, 0, 0);
        h.Al(callId, 3, 150, 0);

        h.Calls.ToggleHoldAsync().GetAwaiter().GetResult();
        h.Al(callId, 30, 200, 0);
        h.Calls.ToggleHoldAsync().GetAwaiter().GetResult();

        // Ripresa al trentesimo secondo: i sei secondi si contano da qui.
        h.Al(callId, 31, 250, 0);
        h.Al(callId, 35, 450, 0);
        Assert.Empty(h.Avvisi);

        h.Al(callId, 37, 550, 0);
        Assert.Single(h.Avvisi);
    }

    [Fact]
    public void UnMotoreSenzaContatoriNonFaPartireNiente()
    {
        // Il motore simulato non ha pacchetti: «nessun dato» non e' «nessun pacchetto».
        var h = new Harness();
        var callId = h.InConversazioneAsync().GetAwaiter().GetResult();

        h.Monitor.Pump(Start);
        h.Monitor.Pump(Start.AddSeconds(30));

        Assert.Null(h.Engine.GetMediaFlow(callId));
        Assert.Empty(h.Avvisi);
    }

    [Theory]
    [InlineData(0, 0, "nessun pacchetto")]
    [InlineData(300, 0, "SOLO IN USCITA")]
    [InlineData(0, 300, "microfono")]
    [InlineData(300, 290, "entrambe le direzioni")]
    public void IlVersoDellAudioSiLeggeNellaDiagnostica(long inviati, long ricevuti, string frammento)
        => Assert.Contains(frammento, OneWayAudio.Describe(new MediaFlowSnapshot(inviati, ricevuti)), StringComparison.Ordinal);

    [Fact]
    public void LAvvisoNonUsaSigle()
    {
        // Una voce sintetica dice «NAT» come una parola qualunque, e non si capisce.
        Assert.DoesNotContain("NAT", AnnouncementTexts.OneWayAudio, StringComparison.Ordinal);
        Assert.DoesNotContain("RTP", AnnouncementTexts.OneWayAudio, StringComparison.Ordinal);
    }
}
