using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica il trasferimento a combinazione unica, il requisito più delicato del
/// capitolato.
/// </summary>
/// <remarks>
/// Ogni test qui corrisponde a una sequenza che l'operatore esegue davvero, scritta
/// nell'ordine in cui preme i tasti. Il criterio non è la copertura del codice ma
/// la copertura degli <b>esiti</b>: destinazione occupata, che non risponde, che
/// rifiuta, chiamante che riaggancia a metà, centralino che respinge il REFER. Sono
/// i casi in cui, senza recupero automatico, il chiamante resterebbe appeso senza
/// che l'operatore possa accorgersene.
/// </remarks>
public sealed class TransferOrchestratorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 11, 0, 0, TimeSpan.FromHours(2));

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        /// <summary>
        /// Serve a un test solo, ma deve stare qui: il tono di libero lo governa il
        /// gestore delle chiamate, e senza riproduttore non ne resta traccia.
        /// </summary>
        public RecordingEarconPlayer Earcons { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public AnnouncementContext Context { get; } = new();

        public CallSessionManager Calls { get; }

        public TransferOrchestrator Transfer { get; }

        public Harness(TransferOptions? options = null)
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), Context, contacts: null,
                options: null, clock: Clock, logger: null, earcons: Earcons);

            Transfer = new TransferOrchestrator(
                Engine, Calls, Announcer, contacts: null, options: options, clock: Clock);
        }

        /// <summary>Porta il posto operatore in conversazione con un chiamante esterno.</summary>
        public async Task<string> ConChiamataAttivaAsync(string number = "0212345678", string? name = "Mario Rossi")
        {
            var callId = Engine.SimulateIncoming(number, name);
            await Task.Delay(20);
            await Calls.AnswerAsync();
            Announcer.Clear();
            Engine.Operations.Clear();
            return callId;
        }
    }

    [Fact]
    public async Task PrimaPressione_MetteInAttesaESpiegaICasi()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();

        Assert.Equal(TransferPhase.EnteringDestination, h.Transfer.Phase);
        Assert.True(h.Engine.Did("hold:"));

        // L'annuncio deve dire cosa fanno i tasti adesso, non solo che la modalita'
        // e' iniziata: e' la differenza fra un'interfaccia usabile al buio e una
        // che costringe a ricordare una sequenza a memoria.
        //
        // Invio viene per primo perche' e' il gesto che si fa quasi sempre.
        Assert.True(h.Announcer.Said("Trasferimento"));
        Assert.True(h.Announcer.Said("INVIO"));
        Assert.True(h.Announcer.Said("ESC"));
    }

    [Fact]
    public async Task PressioneSingola_AvviaLaConsultazioneDopoLaFinestra()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();

        // Entro la finestra la decisione e' sospesa: potrebbe arrivare la seconda
        // pressione.
        Assert.False(h.Engine.Did("dial:4021"));

        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        Assert.True(h.Engine.Did("dial:4021"));
        Assert.Equal(TransferPhase.Consulting, h.Transfer.Phase);
    }

    [Fact]
    public async Task DoppiaPressione_EseguIlTrasferimentoCiecoSenzaChiamareLInterno()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");

        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(200);
        await h.Transfer.HandleTransferKeyAsync();
        await Task.Delay(50);

        // Nessuna consultazione: l'operatore e' libero subito.
        Assert.True(h.Engine.Did($"blind:{callId}:4021"));
        Assert.False(h.Engine.Did("dial:4021"));
        Assert.True(h.Announcer.Said("cieco"));
    }

    [Fact]
    public async Task DuePressioniLente_SonoUnaConsultazioneNonUnTrasferimentoCieco()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");

        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(800);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        Assert.True(h.Engine.Did("dial:4021"));
        Assert.False(h.Engine.Did("blind:"));
    }

    [Fact]
    public async Task EscAnnulla_ERiportaIlChiamanteInLinea()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.CancelAsync();

        Assert.Equal(TransferPhase.Idle, h.Transfer.Phase);
        Assert.True(h.Engine.Did($"resume:{callId}"));

        // L'annuncio deve nominare chi torna in linea: e' l'informazione che
        // permette di riprendere la conversazione senza chiedere "pronto, chi parla?".
        Assert.True(h.Announcer.Said("Mario Rossi"));
    }

    [Fact]
    public async Task InternoOccupato_RecuperaIlChiamanteDaSolo()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession?.ConsultationCall;
        Assert.NotNull(consultation);

        h.Announcer.Clear();
        h.Engine.SimulateProgress(consultation!.Id, CallTrigger.RemoteRejected, CallEndReason.Busy);
        await Task.Delay(50);

        // Senza questo recupero il chiamante resterebbe in attesa a tempo
        // indeterminato, e l'operatore non vedente non avrebbe modo di accorgersene.
        Assert.True(h.Engine.Did($"resume:{callId}"));
        Assert.True(h.Announcer.Said("occupato"));
        Assert.True(h.Announcer.Said("Mario Rossi"));
        Assert.Equal(TransferPhase.Idle, h.Transfer.Phase);
    }

    [Fact]
    public async Task InternoCheNonRisponde_ScadeERecupera()
    {
        var h = new Harness(new TransferOptions
        {
            ConfirmExternalDestinations = false,
            ConsultationTimeout = TimeSpan.FromSeconds(5),
        });

        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        h.Announcer.Clear();
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        Assert.True(h.Engine.Did($"resume:{callId}"));
        Assert.True(h.Announcer.Said("non risponde"));
    }

    [Fact]
    public async Task InternoRisponde_LAnnuncioDiceComeCompletare()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession?.ConsultationCall;
        h.Announcer.Clear();

        h.Engine.SimulateProgress(consultation!.Id, CallTrigger.RemoteAnswered);
        await Task.Delay(50);

        Assert.True(h.Announcer.Said("F9"));
        Assert.True(h.Announcer.Said("completare"));
    }

    [Fact]
    public async Task TerzaPressione_CompletaIlTrasferimentoConAnnuncio()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession!.ConsultationCall!;
        h.Engine.SimulateProgress(consultation.Id, CallTrigger.RemoteAnswered);
        await Task.Delay(20);

        await h.Transfer.HandleTransferKeyAsync();
        await Task.Delay(50);

        Assert.True(h.Engine.Did($"attended:{callId}:{consultation.Id}"));
    }

    [Fact]
    public async Task TrasferimentoConfermatoMentreLInternoSquilla_ChiudeLaLineaDiConsultazione()
    {
        // E' la sequenza piu' frequente del posto operatore, e l'11 agosto 2026 in
        // esercizio lasciava dietro di se' una linea fantasma: il trasferimento
        // riusciva, ma per il programma l'interno continuava a squillare. Il tono di
        // libero non si spegneva piu' e a schermo restava una chiamata uscente in
        // attesa di risposta, che l'operatore ha dovuto chiudere a mano.
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession!.ConsultationCall!;

        // L'interno squilla senza audio di rete, quindi il tono lo genera la
        // postazione: e' quello che l'operatore si e' sentito continuare.
        h.Engine.SimulateRemoteRinging(consultation.Id, hasEarlyMedia: false);
        Assert.Contains(EarconKind.RemoteRinging, h.Earcons.Repeating);

        // Invio senza aspettare che il collega risponda: trasferimento semi-cieco.
        await h.Transfer.HandleTransferKeyAsync();
        await Task.Delay(50);

        Assert.True(h.Engine.Did($"attended:{callId}:{consultation.Id}"));
        Assert.Equal(CallState.Terminated, h.Calls.GetCall(consultation.Id)?.State);
        Assert.DoesNotContain(consultation.Id, h.Calls.LiveCalls.Select(c => c.Id));
        Assert.DoesNotContain(EarconKind.RemoteRinging, h.Earcons.Repeating);
    }

    [Fact]
    public async Task TrasferimentoConAnnuncio_NonChiudeLaConsultazionePrimaDelCentralino()
    {
        // Il contrappeso del test precedente. Quando l'interno ha gia' risposto la
        // linea di consultazione non la chiudiamo noi: il REFER porta Replaces e la
        // chiude il centralino con un BYE. Dichiararla finita qui significherebbe
        // farla sparire dal programma mentre e' ancora aperta sulla rete.
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession!.ConsultationCall!;
        h.Engine.SimulateProgress(consultation.Id, CallTrigger.RemoteAnswered);
        await Task.Delay(20);

        await h.Transfer.HandleTransferKeyAsync();
        await Task.Delay(50);

        Assert.True(h.Engine.Did($"attended:{callId}:{consultation.Id}"));
        Assert.NotEqual(CallState.Terminated, h.Calls.GetCall(consultation.Id)?.State);
    }

    [Fact]
    public async Task DestinazioneEsterna_ChiedeConfermaPrimaDiTrasferire()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = true });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("3391234567");
        h.Announcer.Clear();

        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        // Protezione contro la composizione errata e contro l'uso improprio del
        // centralino verso numerazioni a valore aggiunto.
        Assert.True(h.Announcer.Said("esterna"));
        Assert.False(h.Engine.Did("dial:3391234567"));

        await h.Transfer.HandleTransferKeyAsync();
        await Task.Delay(50);

        Assert.True(h.Engine.Did("dial:3391234567"));
    }

    [Fact]
    public async Task DestinazioneVuota_RifiutaSenzaRompereLaSessione()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        h.Announcer.Clear();

        await h.Transfer.HandleTransferKeyAsync();

        Assert.True(h.Announcer.Said("Nessuna destinazione"));
        Assert.Equal(TransferPhase.EnteringDestination, h.Transfer.Phase);
    }

    [Fact]
    public async Task ChiamanteCheRiaggancia_LasciaVivaLaConsultazione()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        h.Announcer.Clear();
        h.Engine.SimulateProgress(callId, CallTrigger.RemoteHangUp, CallEndReason.RemoteHangUp);
        await Task.Delay(50);

        // Chiudere la consultazione sarebbe scortese verso il collega che ha appena
        // risposto, e lo lascerebbe senza spiegazione.
        Assert.True(h.Announcer.Said("riagganciato"));
        Assert.Equal(TransferPhase.Idle, h.Transfer.Phase);
    }

    [Fact]
    public async Task SenzaChiamataInCorso_IlTastoNonFaNullaMaLoDice()
    {
        var h = new Harness();

        var result = await h.Transfer.HandleTransferKeyAsync();

        Assert.False(result);
        Assert.Contains(EarconKind.CommandRejected, h.Announcer.Earcons);
        Assert.True(h.Announcer.Said("non disponibile"));
    }

    [Fact]
    public async Task ModalitaProgressivaPura_NonIntroduceAlcunaAttesa()
    {
        var h = new Harness(new TransferOptions
        {
            Mode = TransferInteractionMode.ProgressiveOnly,
            ConfirmExternalDestinations = false,
        });

        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();
        await Task.Delay(50);

        // Chi non usa mai il trasferimento cieco elimina cosi' la latenza della
        // finestra di doppia pressione.
        Assert.True(h.Engine.Did("dial:4021"));
    }

    [Fact]
    public async Task EscMentreLInternoSquilla_ChiudeLaChiamataVersoLInterno()
    {
        // Il test precedente annulla PRIMA che la consultazione parta, quindi non
        // tocca mai il ramo che chiude la chiamata verso l'interno. Questo lo tocca:
        // e' il caso in cui l'interno sta squillando e l'operatore rinuncia.
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession?.ConsultationCall;
        Assert.NotNull(consultation);
        Assert.Equal(CallState.Calling, consultation!.State);

        h.Engine.Operations.Clear();

        await h.Transfer.CancelAsync();

        Assert.True(h.Engine.Did($"hangup:{consultation.Id}"));
        Assert.True(h.Engine.Did($"resume:{callId}"));
        Assert.Equal(TransferPhase.Idle, h.Transfer.Phase);
    }

    [Fact]
    public async Task DopoIlRecupero_LaChiamataRipresaSiPuoChiudere()
    {
        // La sequenza segnalata dal campo: si annulla il trasferimento, il chiamante
        // torna in linea e si continua a parlargli, poi si riaggancia. Il riaggancio
        // rispondeva "nessuna chiamata attiva" mentre la conversazione proseguiva.
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession?.ConsultationCall;
        h.Engine.SimulateProgress(consultation!.Id, CallTrigger.RemoteAnswered);

        await h.Transfer.HandleHangUpKeyAsync();

        // Il chiamante e' tornato: deve risultare la chiamata attiva.
        Assert.Equal(CallState.Active, h.Calls.GetCall(callId)?.State);
        Assert.Equal(callId, h.Calls.ActiveCall?.Id);

        h.Engine.Operations.Clear();
        h.Announcer.Clear();

        var closed = await h.Calls.HangUpAsync();

        Assert.True(closed);
        Assert.True(h.Engine.Did($"hangup:{callId}"));
        Assert.False(h.Announcer.Said("Nessuna chiamata"));
    }

    [Fact]
    public async Task UnaChiamataInTrasferimento_SiPuoSempreChiudere()
    {
        // Il difetto segnalato dal campo: il comando di riaggancio rispondeva
        // "nessuna chiamata attiva" mentre la conversazione era ancora aperta,
        // perche' durante un trasferimento la chiamata non e' ne' attiva ne' in
        // attesa e quello stato non era fra i bersagli. Nessun BYE partiva.
        var h = new Harness();
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        Assert.Equal(CallState.Transferring, h.Calls.GetCall(callId)?.State);

        h.Engine.Operations.Clear();
        h.Announcer.Clear();

        var closed = await h.Calls.HangUpAsync();

        Assert.True(closed);
        Assert.True(h.Engine.Did($"hangup:{callId}"));
        Assert.False(h.Announcer.Said("Nessuna chiamata"));
    }

    [Fact]
    public async Task SeRiaggancioIo_NonMiVieneDettoCheHaRiagganciatoIlChiamante()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        h.Announcer.Clear();

        await h.Calls.HangUpAsync();

        // Un annuncio che attribuisce all'interlocutore un gesto dell'operatore non
        // e' un dettaglio di stile: e' l'unica fonte di verita' su cosa e' successo.
        Assert.False(h.Announcer.Said("Il chiamante ha riagganciato"));
        Assert.Equal(TransferPhase.Idle, h.Transfer.Phase);
    }

    [Fact]
    public async Task SeRiaggancioDuranteLaConsultazione_MiVieneDettoCosaResta()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession?.ConsultationCall;
        h.Engine.SimulateProgress(consultation!.Id, CallTrigger.RemoteAnswered);
        h.Announcer.Clear();

        // L'operatore chiude il CHIAMANTE mentre sta parlando con l'interno. Si usa
        // ApplyTrigger perche' il comando di riaggancio bersaglierebbe la
        // conversazione in corso, che qui e' quella con l'interno.
        h.Calls.ApplyTrigger(callId, CallTrigger.HangUp);

        // Resta in linea con l'interno e deve saperlo.
        Assert.True(h.Announcer.Said("Resta attiva"));
        Assert.False(h.Announcer.Said("Il chiamante ha riagganciato"));
    }

    // -----------------------------------------------------------------------
    // Invio: trasferimento cieco immediato
    // -----------------------------------------------------------------------
    //
    // La via corta per smaltire la coda: si digita il numero, si preme Invio, la
    // chiamata parte. Salta deliberatamente tutti e tre i controlli del percorso
    // normale — finestra di doppia pressione, conferma per le destinazioni esterne,
    // avviso di destinazione non disponibile.

    [Fact]
    public async Task Invio_TrasferisceSubitoSenzaAttendereLaFinestra()
    {
        var h = new Harness();
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        var sent = await h.Transfer.BlindTransferNowAsync("4021");

        Assert.True(sent);

        // Nessun Pump, nessun avanzamento dell'orologio: deve essere gia' partito.
        Assert.True(h.Engine.Did($"blind:{callId}:4021"));
        Assert.False(h.Engine.Did("dial:4021"));
    }

    [Fact]
    public async Task Invio_NonChiedeConfermaNemmenoVersoUnNumeroEsterno()
    {
        // Con la conferma attiva, che e' il valore predefinito: la richiesta
        // esplicita e' che Invio non chieda nulla a nessuno.
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = true });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.BlindTransferNowAsync("3391234567");

        Assert.True(h.Engine.Did($"blind:{callId}:3391234567"));
        Assert.False(h.Announcer.Said("conferma"));
    }

    [Fact]
    public async Task Invio_RileggeLaDestinazioneCifraPerCifra()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        h.Announcer.Clear();

        await h.Transfer.BlindTransferNowAsync("4021");

        // E' l'unico riscontro rimasto: se leggesse "quattromilaventuno" l'operatore
        // non potrebbe confrontarlo con quello che ha digitato.
        Assert.True(h.Announcer.Said("quattro, zero, due, uno"));
    }

    [Fact]
    public async Task Invio_NonLasciaUnaConsultazioneInSospeso()
    {
        var h = new Harness();
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");

        // L'operatore preme il tasto di trasferimento e poi cambia idea e usa Invio.
        // La pressione in sospeso non deve risolversi in una chiamata all'interno
        // DOPO che il trasferimento cieco e' gia' partito.
        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.BlindTransferNowAsync("4021");

        h.Clock.AdvanceMilliseconds(800);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        Assert.True(h.Engine.Did($"blind:{callId}:4021"));
        Assert.False(h.Engine.Did("dial:4021"));
    }

    [Fact]
    public async Task Invio_ConCampoVuotoNonTrasferisceEViceDice()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        h.Announcer.Clear();

        var sent = await h.Transfer.BlindTransferNowAsync("   ");

        Assert.False(sent);
        Assert.False(h.Engine.Did("blind:"));
        Assert.True(h.Announcer.Said("Nessuna destinazione"));
        Assert.Equal(TransferPhase.EnteringDestination, h.Transfer.Phase);
    }

    [Fact]
    public async Task Invio_FuoriDalTrasferimentoNonFaNulla()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        // Senza trasferimento avviato non c'e' niente da trasferire: il comando va
        // rifiutato, non deve inventarsi una sessione.
        var sent = await h.Transfer.BlindTransferNowAsync("4021");

        Assert.False(sent);
        Assert.False(h.Engine.Did("blind:"));
    }

    [Fact]
    public async Task IlTastoDiTrasferimento_ContinuaAChiamareLInterno()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        // Invio non deve aver sostituito l'altro gesto: chi vuole annunciare la
        // chiamata prima di passarla continua a poterlo fare.
        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        Assert.True(h.Engine.Did("dial:4021"));
        Assert.False(h.Engine.Did("blind:"));
    }

    // -----------------------------------------------------------------------
    // L'interno rifiuta il trasferimento
    // -----------------------------------------------------------------------
    //
    // E' l'esito piu' comune del trasferimento con annuncio, ed e' il motivo per cui
    // l'annuncio si fa: si chiede all'interno se vuole la chiamata, e a volte dice di
    // no. Il gesto naturale a quel punto e' riagganciare la linea con cui si sta
    // parlando — quella dell'interno — e ritrovarsi con il chiamante.

    [Fact]
    public async Task RiaggancioInConsultazione_ChiudeLInternoERiprendeIlChiamante()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession?.ConsultationCall;
        Assert.NotNull(consultation);
        h.Engine.SimulateProgress(consultation!.Id, CallTrigger.RemoteAnswered);
        h.Announcer.Clear();
        h.Engine.Operations.Clear();

        // "No, non me la passare." L'operatore riaggancia.
        var handled = await h.Transfer.HandleHangUpKeyAsync();

        Assert.True(handled);
        Assert.True(h.Engine.Did($"hangup:{consultation.Id}"));
        Assert.True(h.Engine.Did($"resume:{callId}"));
        Assert.Equal(TransferPhase.Idle, h.Transfer.Phase);
    }

    [Fact]
    public async Task RiaggancioInConsultazione_DiceCheSiTornaDalChiamante()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession?.ConsultationCall;
        h.Engine.SimulateProgress(consultation!.Id, CallTrigger.RemoteAnswered);
        h.Announcer.Clear();

        await h.Transfer.HandleHangUpKeyAsync();

        // Una linea che torna muta senza spiegazione e' indistinguibile da un
        // guasto: va detto sia cosa e' successo sia con chi si sta parlando adesso.
        Assert.True(h.Announcer.Said("annullato"));
        Assert.True(h.Announcer.Said("Ripresa chiamata"));
    }

    [Fact]
    public async Task IlChiamanteTornaAttivo_NonInAttesa()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession?.ConsultationCall;
        h.Engine.SimulateProgress(consultation!.Id, CallTrigger.RemoteAnswered);

        await h.Transfer.HandleHangUpKeyAsync();

        // Deve tornare in conversazione, non in attesa: l'operatore si aspetta di
        // stare gia' parlando, non di dover premere un secondo tasto.
        Assert.Equal(CallState.Active, h.Calls.GetCall(callId)?.State);
    }

    [Fact]
    public async Task FuoriDalTrasferimento_IlRiaggancioNonVieneAssorbito()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        // Senza trasferimento in corso il tasto deve restare quello di sempre,
        // altrimenti chiudere una conversazione normale smetterebbe di funzionare.
        Assert.False(await h.Transfer.HandleHangUpKeyAsync());
    }

    [Fact]
    public async Task DuranteLInserimentoDellaDestinazione_IlRiaggancioNonVieneAssorbito()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();

        // Qui non esiste una seconda linea da chiudere: interpretare il riaggancio
        // come "chiudi il chiamante" sarebbe distruttivo per un tasto premuto per
        // abitudine. Per annullare c'e' ESC, e l'annuncio di avvio lo dice.
        Assert.False(await h.Transfer.HandleHangUpKeyAsync());
        Assert.Equal(TransferPhase.EnteringDestination, h.Transfer.Phase);
    }

    // -----------------------------------------------------------------------
    // Numero dettato durante la conversazione
    // -----------------------------------------------------------------------
    //
    // E' la sequenza piu' frequente al centralino e per molto tempo e' stata la piu'
    // scomoda: il chiamante detta il numero mentre parla, l'operatore lo scrive nel
    // campo di composizione in quel momento, e la pressione del tasto di
    // trasferimento svuotava il campo. Restava solo annotarlo altrove e ridigitarlo,
    // con il chiamante gia' in attesa.

    [Fact]
    public async Task NumeroGiaDigitato_DiventaLaDestinazioneSenzaRidigitarlo()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        // L'operatore ha scritto il numero mentre ascoltava, poi preme il tasto.
        await h.Transfer.HandleTransferKeyAsync("4021");

        Assert.Equal(TransferPhase.EnteringDestination, h.Transfer.Phase);
        Assert.Equal("4021", h.Transfer.CurrentSession?.Destination);
    }

    [Fact]
    public async Task NumeroGiaDigitato_VieneRilettoQuandoParteLaConsultazione()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.Advance(TimeSpan.FromMilliseconds(500));
        h.Transfer.Pump();
        await Task.Delay(50);

        // La rilettura non si e' persa insieme alla pressione tolta: adesso la fa
        // l'annuncio della consultazione, mentre l'interno squilla. Va letta cifra
        // per cifra, perche' "quattromilaventuno" non si confronta con quello che si
        // e' sentito dettare.
        Assert.True(h.Announcer.Said("Chiamata a"));
        Assert.True(h.Announcer.Said("quattro, zero, due, uno"));
    }

    [Fact]
    public async Task NumeroGiaDigitato_ChiamaLInternoSenzaUnaSecondaPressione()
    {
        // LA PRESSIONE DI CONFERMA E' STATA TOLTA, ED E' UNA RICHIESTA DEL CAMPO.
        //
        // Prima il tasto rileggeva la destinazione e aspettava di essere premuto di
        // nuovo. Fra le due pressioni pero' non succedeva niente che potesse far
        // cambiare idea all'operatore: aveva scritto lui il numero un istante prima.
        // In centralino quella pressione si paga su ogni chiamata del turno.
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.Advance(TimeSpan.FromMilliseconds(500));
        h.Transfer.Pump();
        await Task.Delay(50);

        Assert.True(h.Engine.Did("dial:4021"));
    }

    [Fact]
    public async Task NumeroGiaDigitato_ChiamaLInternoMaNonPassaAncoraIlChiamante()
    {
        // E' cio' che resta della verifica, ed e' la parte che conta: parte la
        // chiamata verso l'interno, non il trasferimento. Finche' l'operatore non
        // preme di nuovo, e' lui a parlare con l'interno e il chiamante e' ancora
        // suo: una cifra sbagliata si annulla con ESC senza che nessuno sia stato
        // passato a uno sconosciuto.
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.Advance(TimeSpan.FromMilliseconds(500));
        h.Transfer.Pump();
        await Task.Delay(50);

        Assert.False(h.Engine.Did("blind:"));
        Assert.Equal(TransferPhase.Consulting, h.Transfer.Phase);
    }

    [Fact]
    public async Task NumeroGiaDigitato_TuttoIlTrasferimentoInDuePressioni()
    {
        // La sequenza completa come la fa il centralinista: il numero e' gia' nel
        // campo perche' il chiamante l'ha dettato mentre parlava. Da li' in fondo
        // sono due pressioni, non tre.
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        var callId = await h.ConChiamataAttivaAsync();

        // Prima: l'esterno va in attesa e l'interno viene chiamato.
        await h.Transfer.HandleTransferKeyAsync("4021");
        h.Clock.AdvanceMilliseconds(500);
        h.Transfer.Pump(h.Clock.Now);
        await Task.Delay(50);

        var consultation = h.Transfer.CurrentSession!.ConsultationCall!;
        h.Engine.SimulateProgress(consultation.Id, CallTrigger.RemoteAnswered);
        await Task.Delay(20);

        // Seconda: il chiamante passa all'interno.
        await h.Transfer.HandleTransferKeyAsync();
        await Task.Delay(50);

        Assert.True(h.Engine.Did($"attended:{callId}:{consultation.Id}"));
    }

    [Fact]
    public async Task NumeroGiaDigitato_ConDoppiaPressioneTrasferisceSubito()
    {
        // Il trasferimento cieco resta a due pressioni rapide, come sempre: quello
        // che e' cambiato e' da dove si contano.
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        await h.Transfer.HandleTransferKeyAsync();
        await Task.Delay(50);

        Assert.True(h.Engine.Did("blind:"));
        Assert.False(h.Engine.Did("dial:4021"));
    }

    [Fact]
    public async Task CampoVuoto_MantieneIlComportamentoDiPrima()
    {
        var h = new Harness();
        await h.ConChiamataAttivaAsync();

        // Chi preme il tasto senza aver digitato nulla deve continuare a sentirsi
        // dire cosa fare: e' la maggioranza dei trasferimenti verso interni noti.
        await h.Transfer.HandleTransferKeyAsync(string.Empty);

        Assert.Equal(TransferPhase.EnteringDestination, h.Transfer.Phase);
        Assert.True(h.Announcer.Said("Digita numero o nome"));
    }

    [Fact]
    public async Task NumeroPreparatoAMetaProcedura_NonScavalcaLaFaseInCorso()
    {
        var h = new Harness(new TransferOptions { ConfirmExternalDestinations = false });
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync("4021");
        await h.Transfer.SetDestinationAsync("5000");

        // Fuori dalla finestra della doppia pressione, altrimenti la seconda
        // pressione sarebbe un trasferimento cieco e non la conferma che si vuole
        // misurare qui.
        h.Clock.Advance(TimeSpan.FromMilliseconds(500));

        // Una destinazione preparata che arrivasse a procedura avviata non deve
        // riportare indietro la macchina a stati: il tasto sta confermando, non
        // ricominciando.
        await h.Transfer.HandleTransferKeyAsync("9999");
        h.Clock.Advance(TimeSpan.FromMilliseconds(500));
        h.Transfer.Pump();
        await Task.Delay(50);

        Assert.True(h.Engine.Did("dial:5000"));
        Assert.False(h.Engine.Did("dial:9999"));
    }

    [Fact]
    public async Task SenzaChiamataInCorso_IlNumeroPreparatoNonAvviaNulla()
    {
        var h = new Harness();

        var handled = await h.Transfer.HandleTransferKeyAsync("4021");

        Assert.False(handled);
        Assert.Equal(TransferPhase.Idle, h.Transfer.Phase);
        Assert.False(h.Engine.Did("dial:4021"));
    }
}
