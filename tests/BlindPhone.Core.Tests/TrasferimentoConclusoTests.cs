using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Che cosa dice l'annuncio con cui si chiude un trasferimento.
/// </summary>
/// <remarks>
/// <para>
/// Segnalato dal campo: a trasferimento concluso l'annuncio diceva il nome della
/// destinazione quando la rubrica ne aveva uno, e altrimenti il numero così come era
/// stato digitato. Passato intero alla sintesi, «4021» si sentiva «quattromilaventuno»:
/// l'operatore non riconosceva l'interno a cui aveva appena passato il chiamante, cioè
/// l'unica cosa che quell'annuncio deve fargli verificare.
/// </para>
/// <para>
/// La regola: il nome, se la rubrica ne ha uno; altrimenti l'interno, cifra per cifra.
/// Vale per l'annuncio che segue la conferma del centralino e per quello del
/// trasferimento cieco, che dal lato dell'operatore è la chiusura.
/// </para>
/// </remarks>
public sealed class TrasferimentoConclusoTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 15, 10, 0, 0, TimeSpan.FromHours(2));

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public CallSessionManager Calls { get; }

        public TransferOrchestrator Transfer { get; }

        public Harness(FakeContactRepository? rubrica = null)
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), new AnnouncementContext(),
                contacts: null, options: null, clock: Clock);

            Transfer = new TransferOrchestrator(
                Engine, Calls, Announcer, contacts: rubrica,
                options: new TransferOptions { ConfirmExternalDestinations = false }, clock: Clock);
        }

        /// <summary>Chiamante in linea, come dopo il tasto di risposta.</summary>
        public async Task<string> ConChiamataAttivaAsync()
        {
            var callId = Engine.SimulateIncoming("0212345678", "Mario Rossi");
            await Task.Delay(20);
            await Calls.AnswerAsync();
            Announcer.Clear();
            Engine.Operations.Clear();
            return callId;
        }

        /// <summary>
        /// Trasferimento con annuncio verso l'interno, fino alla conferma del centralino.
        /// </summary>
        /// <remarks>
        /// Gli annunci vengono azzerati appena prima della conferma: resta solo quello
        /// che chiude il trasferimento.
        /// </remarks>
        public async Task ConcludiConAnnuncioAsync(string interno)
        {
            var callId = await ConChiamataAttivaAsync();

            await Transfer.HandleTransferKeyAsync(interno);
            Clock.AdvanceMilliseconds(500);
            Transfer.Pump(Clock.Now);
            await Task.Delay(50);

            var consultazione = Transfer.CurrentSession!.ConsultationCall!;
            Engine.SimulateProgress(consultazione.Id, CallTrigger.RemoteAnswered);
            await Task.Delay(20);

            await Transfer.HandleTransferKeyAsync();
            await Task.Delay(50);

            Announcer.Clear();
            Engine.SimulateTransferOutcome(callId, TransferOutcome.Succeeded);
            await Task.Delay(50);
        }

        /// <summary>Trasferimento cieco verso l'interno: doppia pressione rapida.</summary>
        public async Task TrasferisciAllaCiecaAsync(string interno)
        {
            await ConChiamataAttivaAsync();

            await Transfer.HandleTransferKeyAsync();
            await Transfer.SetDestinationAsync(interno);
            await Transfer.HandleTransferKeyAsync();
            Clock.AdvanceMilliseconds(200);
            await Transfer.HandleTransferKeyAsync();
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task FuoriRubrica_LAnnuncioPronunciaLInternoCifraPerCifra()
    {
        var h = new Harness();

        await h.ConcludiConAnnuncioAsync("4021");

        Assert.True(h.Announcer.Said("Mario Rossi trasferito a quattro, zero, due, uno"));
        Assert.False(h.Announcer.Said("trasferito a 4021"));
    }

    [Fact]
    public async Task InRubrica_LAnnuncioDiceIlNome()
    {
        var h = new Harness(new FakeContactRepository().AddExtension("Mario Bianchi", "4021"));

        await h.ConcludiConAnnuncioAsync("4021");

        Assert.True(h.Announcer.Said("Mario Rossi trasferito a Mario Bianchi"));
        Assert.False(h.Announcer.Said("quattro, zero, due, uno"));
    }

    [Fact]
    public async Task VoceDiRubricaSenzaNome_LAnnuncioPronunciaLInterno()
    {
        // Il suo nome sarebbe il numero stesso: vale come fuori rubrica.
        var h = new Harness(new FakeContactRepository().AddExtension(string.Empty, "4021"));

        await h.ConcludiConAnnuncioAsync("4021");

        Assert.True(h.Announcer.Said("Mario Rossi trasferito a quattro, zero, due, uno"));
    }

    [Fact]
    public async Task TrasferimentoCiecoFuoriRubrica_PronunciaLInterno()
    {
        var h = new Harness();

        await h.TrasferisciAllaCiecaAsync("4021");

        Assert.True(h.Engine.Did("blind:"));
        Assert.True(h.Announcer.Said("Trasferimento cieco a quattro, zero, due, uno inviato"));
    }

    [Fact]
    public async Task TrasferimentoCiecoInRubrica_DiceIlNome()
    {
        var h = new Harness(new FakeContactRepository().AddExtension("Mario Bianchi", "4021"));

        await h.TrasferisciAllaCiecaAsync("4021");

        Assert.True(h.Engine.Did("blind:"));
        Assert.True(h.Announcer.Said("Trasferimento cieco a Mario Bianchi inviato"));
    }
}
