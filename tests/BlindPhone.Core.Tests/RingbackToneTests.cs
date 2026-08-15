using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica il tono di libero sulle chiamate uscenti.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da una differenza che l'operatore notava e non sapeva spiegarsi: chiamando
/// un numero esterno si sente il tono di libero, chiamando un interno no. La causa
/// non è il prodotto ma il protocollo — verso l'esterno il gestore manda il proprio
/// tono come audio in banda, verso un interno il centralino si limita a squillare
/// senza mandare nulla.
/// </para>
/// <para>
/// Il silenzio che ne risultava era indistinguibile da una chiamata non partita, da
/// un tasto premuto male o da un guasto della cuffia. Su un posto operatore non
/// vedente è l'unica informazione disponibile sull'andamento della chiamata.
/// </para>
/// </remarks>
public sealed class RingbackToneTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 4, 15, 0, 0, TimeSpan.FromHours(2));

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

        public async Task<string> ChiamataUscenteAsync(string number = "4021")
        {
            var callId = await Calls.DialAsync(number);
            Announcer.Clear();
            return callId;
        }
    }

    [Fact]
    public async Task InternoCheSquillaSenzaAudioDiRete_GeneraIlTonoInLocale()
    {
        var h = new Harness();
        var callId = await h.ChiamataUscenteAsync();

        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);

        Assert.Contains(EarconKind.RemoteRinging, h.Earcons.Started);
        Assert.Contains(EarconKind.RemoteRinging, h.Earcons.Repeating);
    }

    [Fact]
    public async Task EsternoConAudioDiRete_NonSovrapponeUnSecondoTono()
    {
        var h = new Harness();
        var callId = await h.ChiamataUscenteAsync("0212345678");

        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: true);

        // Due toni con cadenze diverse sovrapposti sono peggio del silenzio: nessuno
        // dei due resta riconoscibile.
        Assert.DoesNotContain(EarconKind.RemoteRinging, h.Earcons.Started);
    }

    [Fact]
    public async Task AllaRisposta_IlTonoSiFerma()
    {
        var h = new Harness();
        var callId = await h.ChiamataUscenteAsync();

        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);
        h.Engine.SimulateProgress(callId, CallTrigger.RemoteAnswered);

        Assert.DoesNotContain(EarconKind.RemoteRinging, h.Earcons.Repeating);
    }

    [Fact]
    public async Task SeLInternoRifiuta_IlTonoSiFerma()
    {
        var h = new Harness();
        var callId = await h.ChiamataUscenteAsync();

        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);
        h.Engine.SimulateProgress(callId, CallTrigger.RemoteRejected, CallEndReason.Busy);

        // Un tono che continua dopo un rifiuto e' informazione sbagliata: l'operatore
        // resterebbe ad aspettare una risposta che non puo' arrivare.
        Assert.DoesNotContain(EarconKind.RemoteRinging, h.Earcons.Repeating);
    }

    [Fact]
    public async Task SeLOperatoreRiaggancia_IlTonoSiFerma()
    {
        var h = new Harness();
        var callId = await h.ChiamataUscenteAsync();

        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);

        // Il riaggancio locale non passa dagli eventi del motore telefonico: se il
        // tono si fermasse solo li', chi rinuncia alla chiamata resterebbe con il
        // tono acceso.
        await h.Calls.HangUpAsync();

        Assert.DoesNotContain(EarconKind.RemoteRinging, h.Earcons.Repeating);
    }

    [Fact]
    public async Task UnaChiamataUscenteInSquillo_SiPuoChiudere()
    {
        // Difetto trovato aggiungendo il tono: una chiamata in instaurazione non
        // compariva fra i bersagli del riaggancio, quindi chi ci ripensava mentre
        // l'interno squillava premeva il tasto e non succedeva niente. Restava
        // invisibile finche' quella fase era muta.
        var h = new Harness();
        var callId = await h.ChiamataUscenteAsync();

        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);

        var closed = await h.Calls.HangUpAsync();

        Assert.True(closed);
        Assert.True(h.Engine.Did($"hangup:{callId}"));
    }

    [Fact]
    public async Task UnSecondoSquilloSullaStessaChiamata_NonFaRipartireLaCadenza()
    {
        var h = new Harness();
        var callId = await h.ChiamataUscenteAsync();

        // I 180 vengono ritrasmessi: riavviare il tono a ogni ritrasmissione lo
        // spezzerebbe a meta' e la cadenza diventerebbe irriconoscibile.
        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);
        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);
        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);

        Assert.Single(h.Earcons.Started, k => k == EarconKind.RemoteRinging);
    }

    [Fact]
    public async Task LoSquilloNonCambiaLoStatoDellaChiamata()
    {
        var h = new Harness();
        var callId = await h.ChiamataUscenteAsync();

        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);

        // Squillare non e' una transizione: la chiamata resta in instaurazione. Se
        // lo fosse, andrebbe dichiarata nella macchina a stati e le servirebbe un
        // annuncio parlato, che si sovrapporrebbe al tono.
        Assert.Equal(CallState.Calling, h.Calls.GetCall(callId)?.State);
    }

    [Fact]
    public async Task LoSquilloNonProduceAnnunciParlati()
    {
        var h = new Harness();
        var callId = await h.ChiamataUscenteAsync();

        h.Engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);

        // Il tono dice gia' tutto. Una frase sopra sarebbe rumore proprio nel momento
        // in cui l'operatore sta ascoltando per capire se l'interno risponde.
        Assert.Empty(h.Announcer.Announcements);
    }

    [Fact]
    public async Task SenzaRiproduttoreDiSegnali_NienteSiRompe()
    {
        // Il riproduttore e' facoltativo: chi costruisce il gestore senza (i test, o
        // una configurazione ridotta) non deve incontrare eccezioni.
        var engine = new FakeEngine();
        var calls = new CallSessionManager(
            engine, new RecordingAnnouncer(), new CallAnnouncementMapper(), new AnnouncementContext());

        var callId = await calls.DialAsync("4021");
        engine.SimulateRemoteRinging(callId, hasEarlyMedia: false);
        engine.SimulateProgress(callId, CallTrigger.RemoteAnswered);

        Assert.Equal(CallState.Active, calls.GetCall(callId)?.State);
    }
}
