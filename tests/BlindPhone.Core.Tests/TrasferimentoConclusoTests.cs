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
/// <para>
/// Seconda segnalazione, a regola già in vigore: fuori rubrica l'annuncio non diceva
/// niente. L'interno in rubrica c'era, ma con «- -» al posto del nome, e quel
/// segnaposto passava per un nome che lo screen reader tace. Vale come fuori rubrica.
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

        /// <summary>
        /// Trasferimento completato mentre l'interno squilla ancora, fino alla conferma
        /// del centralino.
        /// </summary>
        /// <remarks>
        /// È il caso del registro del 20 novembre 2026: l'operatore preme il tasto la
        /// seconda volta senza aspettare che il collega risponda.
        /// </remarks>
        public async Task ConcludiMentreSquillaAsync(string interno)
        {
            var callId = await ConChiamataAttivaAsync();

            await Transfer.HandleTransferKeyAsync(interno);
            Clock.AdvanceMilliseconds(500);
            Transfer.Pump(Clock.Now);
            await Task.Delay(50);

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

    [Theory]
    [InlineData("- -")]
    [InlineData("-")]
    [InlineData(" . ")]
    public async Task VoceDiRubricaConTrattiniAlPostoDelNome_LAnnuncioPronunciaLInterno(string segnaposto)
    {
        // Il centralino scrive «- -» per gli interni non assegnati. Preso per nome,
        // l'annuncio era «trasferito a - -» e lo screen reader non leggeva niente.
        var h = new Harness(new FakeContactRepository().AddExtension(segnaposto, "4021"));

        await h.ConcludiConAnnuncioAsync("4021");

        Assert.True(h.Announcer.Said("Mario Rossi trasferito a quattro, zero, due, uno"));
        Assert.False(h.Announcer.Said("trasferito a -"));
    }

    [Fact]
    public async Task MentreLInternoSquillaAncora_FuoriRubrica_PronunciaLInterno()
    {
        var h = new Harness();

        await h.ConcludiMentreSquillaAsync("3638");

        Assert.True(h.Engine.Did("attended:"));
        Assert.True(h.Announcer.Said("Mario Rossi trasferito a tre, sei, tre, otto"));
    }

    [Fact]
    public async Task MentreLInternoSquillaAncora_VoceConTrattini_PronunciaLInterno()
    {
        var h = new Harness(new FakeContactRepository().AddExtension("- -", "3638"));

        await h.ConcludiMentreSquillaAsync("3638");

        Assert.True(h.Announcer.Said("Mario Rossi trasferito a tre, sei, tre, otto"));
    }

    [Fact]
    public async Task NumeroEsternoFuoriRubrica_PronunciaLeCifre()
    {
        var h = new Harness();

        await h.ConcludiConAnnuncioAsync("0521123456");

        Assert.True(h.Announcer.Said("Mario Rossi trasferito a zero, cinque, due, uno, uno, due, tre, quattro, cinque, sei"));
    }

    [Fact]
    public async Task TrasferimentoCiecoVoceConTrattini_PronunciaLInterno()
    {
        var h = new Harness(new FakeContactRepository().AddExtension("- -", "4021"));

        await h.TrasferisciAllaCiecaAsync("4021");

        Assert.True(h.Announcer.Said("Trasferimento cieco a quattro, zero, due, uno inviato"));
    }

    // -----------------------------------------------------------------------
    // Gli annunci prima della chiusura seguono la stessa regola
    // -----------------------------------------------------------------------

    /// <summary>Avvia la consultazione verso l'interno, con gli annunci azzerati prima.</summary>
    private static async Task<string> ConsultaAsync(Harness h, string interno)
    {
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync(interno);
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        return h.Transfer.CurrentSession!.ConsultationCall!.Id;
    }

    [Fact]
    public async Task ConsultazioneFuoriRubrica_RileggeLeCifre()
    {
        // È la rilettura di verifica di quello che l'operatore ha scritto sotto
        // dettatura: «tremilaseicentotrentotto» non si confronta con le cifre.
        var h = new Harness();

        await ConsultaAsync(h, "3638");

        Assert.True(h.Announcer.Said("Chiamata a tre, sei, tre, otto, in corso"));
        Assert.False(h.Announcer.Said("Chiamata a 3638"));
    }

    [Fact]
    public async Task ConsultazioneInRubrica_DiceIlNome()
    {
        var h = new Harness(new FakeContactRepository().AddExtension("Mario Bianchi", "3638"));

        await ConsultaAsync(h, "3638");

        Assert.True(h.Announcer.Said("Chiamata a Mario Bianchi, in corso"));
    }

    [Fact]
    public async Task RispostaDellInternoFuoriRubrica_DiceLeCifre()
    {
        var h = new Harness();
        var consultazione = await ConsultaAsync(h, "3638");
        h.Announcer.Clear();

        h.Engine.SimulateProgress(consultazione, CallTrigger.RemoteAnswered);
        await Task.Delay(20);

        Assert.True(h.Announcer.Said("tre, sei, tre, otto ha risposto"));
    }

    [Fact]
    public async Task RispostaDellInternoConTrattini_DiceLeCifre()
    {
        // Nel registro del 20 novembre 2026: «- - ha risposto. F9 per completare».
        var h = new Harness(new FakeContactRepository().AddExtension("- -", "3638"));
        var consultazione = await ConsultaAsync(h, "3638");
        h.Announcer.Clear();

        h.Engine.SimulateProgress(consultazione, CallTrigger.RemoteAnswered);
        await Task.Delay(20);

        Assert.True(h.Announcer.Said("tre, sei, tre, otto ha risposto"));
        Assert.False(h.Announcer.Said("- - ha risposto"));
    }

    [Fact]
    public async Task InternoOccupatoFuoriRubrica_DiceLeCifre()
    {
        var h = new Harness();
        var consultazione = await ConsultaAsync(h, "3638");
        h.Announcer.Clear();

        h.Engine.SimulateProgress(consultazione, CallTrigger.RemoteRejected, CallEndReason.Busy);
        await Task.Delay(50);

        Assert.True(h.Announcer.Said("tre, sei, tre, otto occupato"));
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
