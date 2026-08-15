using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica l'alternanza fra il chiamante e la destinazione durante un trasferimento
/// con annuncio.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nasce da una segnalazione dal campo</b>: con il chiamante messo in attesa e un
/// trasferimento con annuncio in corso, il tasto di alternanza non scambiava le due
/// linee, e la casella «in attesa» non mostrava chi stava aspettando.
/// </para>
/// <para>
/// Le due cose avevano la stessa causa, ed è una sola riga della macchina a stati:
/// durante il trasferimento il chiamante non è né in conversazione né in attesa ma in
/// uno stato suo. Chi cercava «una chiamata in attesa» — il comando di alternanza e
/// la casella di riepilogo — non lo trovava, e rispondeva che non c'era nessuno.
/// </para>
/// <para>
/// I test seguono la sequenza dei tasti come la preme l'operatore, come quelli del
/// trasferimento: il criterio non è la copertura del codice ma la copertura dei
/// momenti in cui, sbagliando, qualcuno resta in linea senza che nessuno lo sappia.
/// </para>
/// </remarks>
public sealed class TransferSwapTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(2));

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public CallSessionManager Calls { get; }

        public TransferOrchestrator Transfer { get; }

        public Harness()
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), new AnnouncementContext(),
                contacts: null, options: null, clock: Clock, logger: null);

            Transfer = new TransferOrchestrator(
                Engine, Calls, Announcer, contacts: null,
                options: new TransferOptions { ConfirmExternalDestinations = false },
                clock: Clock);
        }

        /// <summary>Identificativo della chiamata del chiamante esterno.</summary>
        public string CallerId { get; private set; } = string.Empty;

        /// <summary>Chiamata verso l'interno, quando la consultazione è partita.</summary>
        public Call Consultation => Transfer.CurrentSession!.ConsultationCall!;

        /// <summary>Porta la postazione in conversazione con un chiamante esterno.</summary>
        public async Task InConversazioneAsync()
        {
            CallerId = Engine.SimulateIncoming("0212345678", "Mario Rossi");
            await Task.Delay(20);
            await Calls.AnswerAsync();
        }

        /// <summary>
        /// Mette il chiamante in attesa e fa squillare l'interno, senza aspettare che
        /// risponda.
        /// </summary>
        public async Task ConInternoCheSquillaAsync()
        {
            await InConversazioneAsync();

            await Transfer.HandleTransferKeyAsync();
            await Transfer.SetDestinationAsync("4021");
            await Transfer.HandleTransferKeyAsync();

            Clock.AdvanceMilliseconds(500);
            Transfer.Pump(Clock.Now);
            await Task.Delay(50);
        }

        /// <summary>
        /// Porta la postazione dove il difetto si manifesta: chiamante parcheggiato,
        /// interno che ha risposto, operatore che gli sta parlando.
        /// </summary>
        public async Task InConsultazioneAsync()
        {
            await InConversazioneAsync();

            await Transfer.HandleTransferKeyAsync();
            await Transfer.SetDestinationAsync("4021");
            await Transfer.HandleTransferKeyAsync();

            Clock.AdvanceMilliseconds(500);
            Transfer.Pump(Clock.Now);
            await Task.Delay(50);

            Engine.SimulateProgress(Consultation.Id, CallTrigger.RemoteAnswered);
            await Task.Delay(20);

            Announcer.Clear();
            Engine.Operations.Clear();
        }
    }

    // -----------------------------------------------------------------------
    // Il difetto segnalato
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DuranteLaConsultazione_LAlternanzaRiportaIlChiamanteInLinea()
    {
        var h = new Harness();
        await h.InConsultazioneAsync();

        var assorbito = await h.Transfer.HandleSwapKeyAsync();

        Assert.True(assorbito);

        // Sul filo: l'interno passa in attesa, il chiamante torna in conversazione.
        Assert.True(h.Engine.Did($"hold:{h.Consultation.Id}"));
        Assert.True(h.Engine.Did($"resume:{h.CallerId}"));

        // E nel modello, che e' cio' che decide cosa mostrano le caselle e cosa
        // rispondono i comandi successivi.
        Assert.Equal(CallState.Active, h.Calls.GetCall(h.CallerId)!.State);
        Assert.Equal(CallState.Held, h.Consultation.State);
    }

    [Fact]
    public async Task DopoLAlternanza_LaCasellaInAttesaMostraChiAspetta()
    {
        var h = new Harness();
        await h.InConsultazioneAsync();

        // Prima ancora di alternare: il chiamante e' parcheggiato dentro la procedura
        // e la casella deve gia' dirlo. E' la meta' visiva della segnalazione.
        var parcheggiato = CallBoardTexts.OnHold(h.Calls.WaitingCalls, h.Clock.Now);

        Assert.Contains("Mario Rossi", parcheggiato, StringComparison.Ordinal);
        Assert.Contains("0212345678", parcheggiato, StringComparison.Ordinal);

        await h.Transfer.HandleSwapKeyAsync();

        // Dopo l'alternanza le parti si scambiano, e la casella segue.
        var dopo = CallBoardTexts.OnHold(h.Calls.WaitingCalls, h.Clock.Now);

        Assert.Contains("4021", dopo, StringComparison.Ordinal);
        Assert.DoesNotContain("Mario Rossi", dopo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuranteLaConsultazione_LaCasellaNonDiceMaiCheNonAspettaNessuno()
    {
        // La regressione da cui e' nato tutto, scritta come la si e' vista a schermo.
        var h = new Harness();
        await h.InConsultazioneAsync();

        Assert.NotEqual(
            CallBoardTexts.NoHeldCall,
            CallBoardTexts.OnHold(h.Calls.WaitingCalls, h.Clock.Now));
    }

    // -----------------------------------------------------------------------
    // Andata e ritorno
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SecondaAlternanza_RiportaLOperatoreDallInterno()
    {
        var h = new Harness();
        await h.InConsultazioneAsync();

        await h.Transfer.HandleSwapKeyAsync();
        h.Engine.Operations.Clear();

        await h.Transfer.HandleSwapKeyAsync();

        Assert.True(h.Engine.Did($"hold:{h.CallerId}"));
        Assert.True(h.Engine.Did($"resume:{h.Consultation.Id}"));

        // Il chiamante torna PARCHEGGIATO, non semplicemente in attesa: e' lo stato da
        // cui il completamento sa ripartire.
        Assert.Equal(CallState.Transferring, h.Calls.GetCall(h.CallerId)!.State);
        Assert.Equal(CallState.Active, h.Consultation.State);
    }

    [Fact]
    public async Task LAlternanzaNonChiudeLaProceduraDiTrasferimento()
    {
        // Alternare non e' annullare: la destinazione resta in memoria e il tasto di
        // trasferimento continua a completare. Senza questa garanzia l'unica via per
        // tornare dal chiamante sarebbe annullare e ricominciare.
        var h = new Harness();
        await h.InConsultazioneAsync();

        await h.Transfer.HandleSwapKeyAsync();

        Assert.Equal(TransferPhase.Consulting, h.Transfer.Phase);
        Assert.Equal("4021", h.Transfer.CurrentSession?.Destination);
    }

    [Fact]
    public async Task CompletamentoDopoLAlternanza_TrasferisceLoStesso()
    {
        var h = new Harness();
        await h.InConsultazioneAsync();

        // L'operatore e' tornato dal chiamante e da li' preme il tasto che completa.
        await h.Transfer.HandleSwapKeyAsync();
        h.Engine.Operations.Clear();

        await h.Transfer.HandleTransferKeyAsync();
        await Task.Delay(50);

        Assert.True(h.Engine.Did($"attended:{h.CallerId}:{h.Consultation.Id}"));

        // Le due linee vengono rimesse come il completamento le vuole: senza, la
        // modalita' verrebbe letta come semi-cieca su un interno che aveva risposto.
        Assert.True(h.Engine.Did($"hold:{h.CallerId}"));
        Assert.True(h.Engine.Did($"resume:{h.Consultation.Id}"));
    }

    // -----------------------------------------------------------------------
    // Quello che non deve succedere
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MentreLInternoSquilla_LAlternanzaSpiegaInveceDiScambiare()
    {
        var h = new Harness();
        await h.ConInternoCheSquillaAsync();

        h.Announcer.Clear();
        h.Engine.Operations.Clear();

        var assorbito = await h.Transfer.HandleSwapKeyAsync();

        // Il tasto viene assorbito comunque: la risposta giusta e' dire cosa sta
        // succedendo, non lasciarlo cadere in un rifiuto generico che l'operatore
        // leggerebbe come "il comando non ha funzionato".
        Assert.True(assorbito);
        Assert.True(h.Announcer.Said("squillando"));

        // Nessuna delle due linee si e' mossa: il chiamante resta dov'e'.
        Assert.False(h.Engine.Did("resume:"));
        Assert.Equal(CallState.Transferring, h.Calls.GetCall(h.CallerId)!.State);
    }

    [Fact]
    public async Task RiprendendoLInterno_NonSiRiannunciaCheHaRisposto()
    {
        // L'interno ripassa in conversazione a ogni ritorno dell'operatore. Leggendo
        // il solo stato di arrivo si sentiva "ha risposto" a ogni giro, su una linea
        // aperta da un minuto.
        var h = new Harness();
        await h.InConsultazioneAsync();

        await h.Transfer.HandleSwapKeyAsync();
        h.Announcer.Clear();

        await h.Transfer.HandleSwapKeyAsync();

        Assert.False(h.Announcer.Said("ha risposto"));
        Assert.True(h.Announcer.Said("4021"));
    }

    [Fact]
    public async Task MentreSiSceglieLaDestinazione_LAlternanzaIndicaLaViaDUscita()
    {
        // Qui non e' stato chiamato nessuno: la seconda linea non esiste proprio, e la
        // frase deve dirlo in modo diverso da "sta squillando" — un telefono che
        // squilla si puo' aspettare, una destinazione da scegliere no.
        var h = new Harness();
        await h.InConversazioneAsync();

        await h.Transfer.HandleTransferKeyAsync();
        h.Announcer.Clear();

        Assert.True(await h.Transfer.HandleSwapKeyAsync());
        Assert.True(h.Announcer.Said("non hai ancora chiamato nessuno"));
        Assert.False(h.Announcer.Said("squillando"));
    }

    [Fact]
    public async Task SenzaTrasferimentoInCorso_IlTastoNonVieneAssorbito()
    {
        // Fuori dalla procedura l'alternanza resta quella di sempre, governata dal
        // gestore delle chiamate: l'orchestratore deve farsi da parte.
        var h = new Harness();
        await h.InConversazioneAsync();

        Assert.False(await h.Transfer.HandleSwapKeyAsync());
    }

    // -----------------------------------------------------------------------
    // Le linee non restano mai mute
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SeIlChiamanteRiaggancia_LInternoRimastoTornaInConversazione()
    {
        // Ci si arriva solo con l'alternanza: l'operatore era tornato dal chiamante e
        // l'interno stava in attesa. Senza ripresa la postazione resterebbe con una
        // linea sola, in attesa, e nessuno in conversazione: un silenzio che
        // dall'esterno non si distingue da una chiamata caduta.
        var h = new Harness();
        await h.InConsultazioneAsync();

        // Il riferimento si prende adesso: chiudendo il chiamante la sessione di
        // trasferimento si chiude, e con lei la strada per arrivare alla consultazione.
        var interno = h.Consultation;

        await h.Transfer.HandleSwapKeyAsync();
        h.Engine.Operations.Clear();

        h.Engine.SimulateProgress(h.CallerId, CallTrigger.RemoteHangUp, CallEndReason.RemoteHangUp);
        await Task.Delay(50);

        Assert.True(h.Engine.Did($"resume:{interno.Id}"));
        Assert.Equal(CallState.Active, interno.State);
    }

    [Fact]
    public async Task SeLInternoRiaggancia_IlChiamanteGiaInLineaNonVieneToccato()
    {
        // L'operatore stava parlando con il chiamante: qui non c'e' niente da
        // riprendere, e un secondo invito sulla stessa linea disturberebbe una
        // conversazione che sta funzionando.
        var h = new Harness();
        await h.InConsultazioneAsync();

        await h.Transfer.HandleSwapKeyAsync();
        h.Engine.Operations.Clear();

        h.Engine.SimulateProgress(h.Consultation.Id, CallTrigger.RemoteHangUp, CallEndReason.RemoteHangUp);
        await Task.Delay(50);

        Assert.False(h.Engine.Did($"resume:{h.CallerId}"));
        Assert.Equal(CallState.Active, h.Calls.GetCall(h.CallerId)!.State);
        Assert.Equal(TransferPhase.Idle, h.Transfer.Phase);
    }
}
