using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica la conferenza a tre.
/// </summary>
/// <remarks>
/// <para>
/// Il criterio è lo stesso dei test del trasferimento: non la copertura del codice
/// ma quella degli <b>esiti</b>, e in particolare di quelli in cui qualcuno resta
/// in linea senza che l'operatore lo sappia. Una conferenza che si scioglie in
/// silenzio è indistinguibile, per chi non vede lo schermo, da una che sta ancora
/// andando: continua a parlare a due persone di cui una non c'è più.
/// </para>
/// <para>
/// Il missaggio audio vero e proprio non si prova qui — vive in
/// <c>BlindPhone.Audio</c> e richiede dispositivi reali. Qui si prova che il posto
/// operatore chieda al motore le cose giuste, nell'ordine giusto, e che le dica.
/// </para>
/// </remarks>
public sealed class ConferenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 6, 9, 0, 0, TimeSpan.FromHours(2));

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public AnnouncementContext Context { get; } = new();

        public ConferenceOptions Options { get; } = new();

        public CallSessionManager Calls { get; }

        public Harness()
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), Context, contacts: null,
                options: null, clock: Clock, logger: null, earcons: null, conference: Options);
        }

        /// <summary>Porta il posto operatore in conversazione con un chiamante.</summary>
        public async Task<string> RispondiA(string number, string name)
        {
            var callId = Engine.SimulateIncoming(number, name);
            await Task.Delay(20);
            await Calls.AnswerAsync();
            return callId;
        }

        /// <summary>
        /// Ricostruisce la situazione da cui parte ogni conferenza: un chiamante in
        /// attesa e un secondo interlocutore in linea.
        /// </summary>
        /// <remarks>
        /// È la stessa in cui ci si trova a metà di un trasferimento con annuncio,
        /// quando l'interno ha risposto: da lì la conferenza è una pressione sola, ed
        /// è il motivo per cui non compone numeri per conto proprio.
        /// </remarks>
        public async Task<(string Esterno, string Interno)> ConDueChiamateAsync()
        {
            var esterno = await RispondiA("0212345678", "Mario Rossi");

            // La sequenza e' quella reale: si mette in attesa il chiamante, si chiama
            // il secondo interlocutore, lui risponde. Da qui la conferenza e' una
            // pressione sola.
            await Calls.ToggleHoldAsync();

            var interno = await Calls.DialAsync("4021");
            Engine.SimulateProgress(interno, CallTrigger.RemoteAnswered);

            Announcer.Clear();
            Engine.Operations.Clear();

            return (esterno, interno);
        }
    }

    [Fact]
    public async Task DueChiamate_IlTastoLeUnisceEDiceChiCEDentro()
    {
        var h = new Harness();
        var (esterno, interno) = await h.ConDueChiamateAsync();

        Assert.True(await h.Calls.ToggleConferenceAsync());

        Assert.Equal(CallState.Conferenced, h.Calls.GetCall(esterno)!.State);
        Assert.Equal(CallState.Conferenced, h.Calls.GetCall(interno)!.State);
        Assert.Equal(2, h.Calls.ConferencedCalls.Count);

        // I due nomi sono l'informazione per cui l'annuncio esiste: una conferenza
        // aperta sulla coppia sbagliata va sciolta subito, e per accorgersene serve
        // sentire con chi e' stata aperta.
        Assert.True(h.Announcer.Said("Mario Rossi"));
        Assert.True(h.Announcer.Said("4021"));
        Assert.Contains(EarconKind.ConferenceStarted, h.Announcer.Earcons);
    }

    [Fact]
    public async Task LaGambaInAttesaVieneRipresaPrimaDiUnire()
    {
        // In attesa il centralino manda musica al posto della voce: una conferenza
        // aperta senza togliere l'attesa avrebbe un partecipante che non sente e non
        // viene sentito, e nessun contatore lo direbbe.
        var h = new Harness();
        var (esterno, _) = await h.ConDueChiamateAsync();

        await h.Calls.ToggleConferenceAsync();

        var ripresa = h.Engine.Operations.IndexOf($"resume:{esterno}");
        var unione = h.Engine.Operations.FindIndex(o => o.StartsWith("conference:", StringComparison.Ordinal));

        Assert.True(ripresa >= 0, "la chiamata in attesa non e' stata ripresa");
        Assert.True(unione >= 0, "il ponte audio non e' stato chiesto al motore");
        Assert.True(ripresa < unione, "il ponte e' stato aperto prima di togliere l'attesa");
    }

    [Fact]
    public async Task ChiEraInLineaEntraPerPrimoEResta_QuandoSiScioglie()
    {
        var h = new Harness();
        var (esterno, interno) = await h.ConDueChiamateAsync();

        await h.Calls.ToggleConferenceAsync();
        h.Announcer.Clear();

        Assert.True(await h.Calls.ToggleConferenceAsync());

        // L'interno era in linea quando la conferenza si e' aperta, quindi e' quello
        // con cui l'operatore resta: lasciargli l'altro sarebbe corretto quanto
        // arbitrario, e l'arbitrario costa una domanda a voce alta.
        Assert.Equal(CallState.Active, h.Calls.GetCall(interno)!.State);
        Assert.Equal(CallState.Held, h.Calls.GetCall(esterno)!.State);
        Assert.Empty(h.Calls.ConferencedCalls);

        Assert.True(h.Announcer.Said("Conferenza chiusa"));
        Assert.True(h.Announcer.Said("in attesa"));
        Assert.Empty(h.Engine.ConferencedCallIds);
    }

    [Fact]
    public async Task SenzaSecondaChiamata_IlRifiutoSpiegaComeProcurarsela()
    {
        // La conferenza e' l'unico comando che richiede una condizione che l'operatore
        // deve costruirsi prima. Un rifiuto secco, con una chiamata in corso, si legge
        // come un tasto rotto.
        var h = new Harness();
        await h.RispondiA("0212345678", "Mario Rossi");
        h.Announcer.Clear();

        Assert.False(await h.Calls.ToggleConferenceAsync());

        Assert.True(h.Announcer.Said(h.Options.HoldKeyName));
        Assert.True(h.Announcer.Said(h.Options.DialerKeyName));
        Assert.Contains(EarconKind.CommandRejected, h.Announcer.Earcons);
    }

    [Fact]
    public async Task MotoreSenzaAudio_LoDiceInvecDiAprireUnaConferenzaMuta()
    {
        var h = new Harness();
        h.Engine.SupportsConference = false;
        await h.ConDueChiamateAsync();

        Assert.False(await h.Calls.ToggleConferenceAsync());

        Assert.True(h.Announcer.Said("Conferenza non disponibile"));
        Assert.Empty(h.Calls.ConferencedCalls);
    }

    [Fact]
    public async Task UnPartecipanteChiude_LaConferenzaSiScioglieEDiceConChiSiResta()
    {
        var h = new Harness();
        var (esterno, interno) = await h.ConDueChiamateAsync();

        await h.Calls.ToggleConferenceAsync();
        h.Announcer.Clear();

        h.Engine.SimulateProgress(esterno, CallTrigger.RemoteHangUp, CallEndReason.RemoteHangUp);
        await Task.Delay(50);

        // Chi resta torna a essere una conversazione normale: lasciarlo "in
        // conferenza" da solo lo renderebbe invisibile ai comandi di attesa e di
        // trasferimento, che cercano una chiamata attiva.
        Assert.Equal(CallState.Active, h.Calls.GetCall(interno)!.State);
        Assert.Empty(h.Calls.ConferencedCalls);

        // Le due meta' della frase sono una domanda sola: senza la seconda,
        // l'operatore non sa se sta ancora parlando con qualcuno.
        Assert.True(h.Announcer.Said("Mario Rossi"));
        Assert.True(h.Announcer.Said("in linea con 4021"));
    }

    [Fact]
    public async Task LAnnuncioDelloScioglimentoPassaAncheDuranteLaConversazione()
    {
        // La regola del silenzio durante la conversazione esiste per non parlare sopra
        // l'interlocutore. Qui la conversazione a cui si torna e' proprio l'oggetto
        // dell'annuncio: sopprimerlo lascerebbe l'operatore a parlare a due persone di
        // cui una ha gia' riagganciato.
        var h = new Harness();
        var (esterno, _) = await h.ConDueChiamateAsync();

        await h.Calls.ToggleConferenceAsync();
        h.Announcer.Clear();

        h.Engine.SimulateProgress(esterno, CallTrigger.RemoteHangUp, CallEndReason.RemoteHangUp);
        await Task.Delay(50);

        var annuncio = h.Announcer.Announcements.Single(a => a.Text.Contains("Conferenza terminata"));
        Assert.True(annuncio.BypassConversationSilence);
    }

    [Fact]
    public async Task IlRiaggancioChiudeTutteELineEUnaVoltaSola()
    {
        var h = new Harness();
        var (esterno, interno) = await h.ConDueChiamateAsync();

        await h.Calls.ToggleConferenceAsync();
        h.Announcer.Clear();
        h.Engine.Operations.Clear();

        Assert.True(await h.Calls.HangUpAsync());

        Assert.True(h.Engine.Did($"hangup:{esterno}"));
        Assert.True(h.Engine.Did($"hangup:{interno}"));
        Assert.Equal(CallState.Terminated, h.Calls.GetCall(esterno)!.State);
        Assert.Equal(CallState.Terminated, h.Calls.GetCall(interno)!.State);

        // Un annuncio solo, non due chiusure consecutive: l'operatore ha fatto un
        // gesto solo e deve sentirsi rispondere una cosa sola.
        Assert.Single(h.Announcer.Announcements);
        Assert.True(h.Announcer.Said("Conferenza chiusa"));
    }

    [Fact]
    public async Task NonDiceMaiCheNonCEUnaChiamataMentreLaConferenzaEAperta()
    {
        // E' la bugia peggiore che questo programma possa dire: l'operatore non ha
        // modo di verificarla e smette di provare a chiudere linee che sono aperte.
        var h = new Harness();
        await h.ConDueChiamateAsync();
        await h.Calls.ToggleConferenceAsync();
        h.Announcer.Clear();

        await h.Calls.HangUpAsync();

        Assert.False(h.Announcer.Said("Comando non disponibile"));
    }

    [Fact]
    public async Task LAttesaParcheggiaTuttiEIlTastoLiRiportaInLineaInsieme()
    {
        var h = new Harness();
        var (esterno, interno) = await h.ConDueChiamateAsync();

        await h.Calls.ToggleConferenceAsync();
        h.Announcer.Clear();

        await h.Calls.ToggleHoldAsync();

        Assert.Equal(CallState.Held, h.Calls.GetCall(esterno)!.State);
        Assert.Equal(CallState.Held, h.Calls.GetCall(interno)!.State);
        Assert.Equal(2, h.Calls.ParkedConference.Count);
        Assert.True(h.Announcer.Said("Conferenza in attesa"));

        // Riprenderne una sola lascerebbe l'operatore con meta' delle persone che
        // aveva, senza che nulla glielo dica.
        h.Announcer.Clear();
        Assert.True(await h.Calls.ToggleConferenceAsync());

        Assert.Equal(2, h.Calls.ConferencedCalls.Count);
        Assert.True(h.Announcer.Said("Conferenza a tre"));
    }

    [Fact]
    public async Task RispondereAUnaTerzaLinea_ParcheggiaLaConferenzaIntera()
    {
        var h = new Harness();
        var (esterno, interno) = await h.ConDueChiamateAsync();

        await h.Calls.ToggleConferenceAsync();

        var terza = h.Engine.SimulateIncoming("0687654321", "Ufficio Tecnico");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        // Mettere in attesa una gamba sola lascerebbe l'altra a sentire l'operatore
        // rispondere a un terzo.
        Assert.Equal(CallState.Held, h.Calls.GetCall(esterno)!.State);
        Assert.Equal(CallState.Held, h.Calls.GetCall(interno)!.State);
        Assert.Equal(terza, h.Calls.ActiveCall?.Id);
    }

    [Fact]
    public async Task ApertureNonRiuscita_RimetteInAttesaCiOEraEDiceCheNonHaFunzionato()
    {
        // Senza il ripristino resterebbero due linee entrambe in conversazione, con
        // l'operatore che sente tutti e due e nessuno dei due che sente l'altro: uno
        // stato che nessun comando successivo sa piu' raccontare.
        var h = new Harness();
        var (esterno, interno) = await h.ConDueChiamateAsync();
        h.Engine.FailConference = true;

        Assert.False(await h.Calls.ToggleConferenceAsync());

        Assert.Empty(h.Calls.ConferencedCalls);
        Assert.Equal(CallState.Held, h.Calls.GetCall(esterno)!.State);
        Assert.Equal(CallState.Active, h.Calls.GetCall(interno)!.State);
        Assert.True(h.Announcer.Said("Conferenza non riuscita"));
    }

    [Fact]
    public async Task InConferenzaITonNonPartonoESiDiceComeUscirne()
    {
        // Il tono andrebbe a una sola delle due linee senza che si possa dire quale, e
        // su un menu automatico una cifra nel ramo sbagliato non si annulla.
        var h = new Harness();
        await h.ConDueChiamateAsync();
        await h.Calls.ToggleConferenceAsync();
        h.Announcer.Clear();

        Assert.False(await h.Calls.SendDtmfAsync('1'));

        Assert.False(h.Announcer.Said("Nessuna chiamata in corso"));
        Assert.True(h.Announcer.Said(h.Options.ConferenceKeyName));
    }

    [Fact]
    public async Task LaConferenzaValeComeConversazioneInCorso()
    {
        // Da cui discende la soppressione degli annunci non urgenti: l'operatore sta
        // parlando con due persone, quindi la ragione per tacere vale doppio.
        var h = new Harness();
        await h.ConDueChiamateAsync();
        await h.Calls.ToggleConferenceAsync();

        Assert.True(h.Context.IsConversationActive);
        Assert.True(h.Calls.IsConferenceActive);
        Assert.Null(h.Calls.ActiveCall);
    }

    [Fact]
    public async Task EntrareInConferenzaAzzeraIlCronometroDellAttesa()
    {
        // Lasciando il timestamp, la stessa chiamata risulterebbe "in attesa da tre
        // minuti" nel momento in cui l'operatore ci sta parlando, e il promemoria
        // periodico inviterebbe a riprendere qualcuno che e' gia' in linea.
        var h = new Harness();
        var (esterno, _) = await h.ConDueChiamateAsync();

        h.Clock.AdvanceMilliseconds(180_000);
        await h.Calls.ToggleConferenceAsync();

        Assert.Equal(TimeSpan.Zero, h.Calls.GetCall(esterno)!.HoldDuration(h.Clock.Now));
    }

    [Fact]
    public void LaConferenzaHaAudioAttivoComeUnaConversazione()
    {
        Assert.True(CallStateMachine.HasActiveAudio(CallState.Conferenced));
        Assert.False(CallStateMachine.IsTerminal(CallState.Conferenced));
    }
}
