using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che una chiamata entrante squilli per tutto il tempo in cui squilla.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da una segnalazione dell'operatore: <i>"se non rispondo in tempo il
/// softphone riaggancia da solo"</i>. Il registro di una chiamata reale ha mostrato
/// che il softphone non riagganciava affatto — la chiamata veniva chiusa dal
/// centralino con un <c>CANCEL</c> allo scadere del suo tempo di squillo — ma il
/// segnale acustico era un earcon suonato una volta sola all'arrivo. Da lì in poi il
/// silenzio, con la chiamata che continuava a squillare per tutti tranne che per
/// l'orecchio dell'operatore.
/// </para>
/// <para>
/// Per chi non vede lo schermo quel silenzio <b>è</b> il riaggancio: non c'è altro
/// modo di sapere che la chiamata è ancora lì. La suoneria deve quindi durare quanto
/// lo stato di squillo, e finire con esso a qualunque esito.
/// </para>
/// </remarks>
public sealed class SuoneriaEntranteTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 8, 19, 9, 0, TimeSpan.FromHours(2));

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public RecordingEarconPlayer Earcons { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public CallSessionManager Calls { get; }

        public Harness()
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), new AnnouncementContext(),
                contacts: null, options: null, clock: Clock, logger: null, earcons: Earcons);
        }
    }

    [Fact]
    public void UnaChiamataEntrante_ContinuaASquillare()
    {
        var h = new Harness();

        h.Engine.SimulateIncoming("7122");

        Assert.Contains(EarconKind.IncomingCall, h.Earcons.Started);
        Assert.Contains(EarconKind.IncomingCall, h.Earcons.Repeating);
    }

    [Fact]
    public async Task AllaRisposta_LaSuoneriaSiFerma()
    {
        var h = new Harness();
        h.Engine.SimulateIncoming("7122");

        await h.Calls.AnswerAsync();

        // Una suoneria che sopravvive alla risposta suonerebbe sopra la voce del
        // chiamante, che e' il primo suono che l'operatore deve sentire.
        Assert.DoesNotContain(EarconKind.IncomingCall, h.Earcons.Repeating);
    }

    [Fact]
    public void SeIlChiamanteRinuncia_LaSuoneriaSiFerma()
    {
        var h = new Harness();
        var callId = h.Engine.SimulateIncoming("7122");

        // E' il caso reale: il centralino manda CANCEL quando scade il proprio tempo
        // di squillo, ed e' lui a decidere quando la postazione smette di squillare.
        h.Engine.SimulateProgress(callId, CallTrigger.RemoteHangUp, CallEndReason.RemoteHangUp);

        Assert.DoesNotContain(EarconKind.IncomingCall, h.Earcons.Repeating);
    }

    [Fact]
    public async Task SeLOperatoreRifiuta_LaSuoneriaSiFerma()
    {
        var h = new Harness();
        h.Engine.SimulateIncoming("7122");

        await h.Calls.HangUpAsync();

        Assert.DoesNotContain(EarconKind.IncomingCall, h.Earcons.Repeating);
    }

    [Fact]
    public void SeLaTransazioneCade_LaSuoneriaSiFerma()
    {
        var h = new Harness();
        var callId = h.Engine.SimulateIncoming("7122");

        // Rete di sicurezza: se lo stack SIP lascia scadere la transazione entrante
        // nessun 200 OK partirebbe piu', e una suoneria che continua inviterebbe a
        // rispondere a una chiamata che non c'e' piu'.
        h.Engine.SimulateProgress(callId, CallTrigger.Failed, CallEndReason.NoAnswer);

        Assert.DoesNotContain(EarconKind.IncomingCall, h.Earcons.Repeating);
    }

    [Fact]
    public async Task LaSecondaChiamataInSquillo_ContinuaASuonareDopoLaPrima()
    {
        var h = new Harness();
        var prima = h.Engine.SimulateIncoming("7122");
        h.Engine.SimulateIncoming("7123");

        // Rispondendo alla prima resta la seconda in squillo: e' li' che una suoneria
        // fermata "perche' una chiamata e' stata risposta" lascerebbe l'operatore
        // senza sapere di avere ancora qualcuno in attesa di risposta.
        await h.Calls.AnswerAsync();
        h.Engine.SimulateProgress(prima, CallTrigger.RemoteHangUp, CallEndReason.RemoteHangUp);

        Assert.Contains(EarconKind.IncomingCall, h.Earcons.Repeating);
    }

    [Fact]
    public void LaSuoneriaNonRipartePerEventiCheNonCambianoLoSquillo()
    {
        var h = new Harness();
        var callId = h.Engine.SimulateIncoming("7122");

        // Un evento fuori sequenza non deve far ricominciare la suoneria da capo: la
        // cadenza si spezzerebbe a ogni ritrasmissione della rete.
        h.Engine.SimulateProgress(callId, CallTrigger.Resume);

        Assert.Single(h.Earcons.Started, k => k == EarconKind.IncomingCall);
    }
}
