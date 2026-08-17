using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che l'avviso preventivo del trasferimento si possa confermare con Invio, e
/// non soltanto con il tasto rapido.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nasce da una segnalazione dal campo, e il registro la conferma alla riga.</b>
/// Verso alcuni interni Invio non faceva partire il trasferimento e la voce diceva che
/// il telefono non era registrato; con F9 lo stesso trasferimento andava a buon fine.
/// Nel registro del 17 agosto 2026, alle 10:22:02, sei pressioni di Invio in un secondo
/// e mezzo non producono nessun INVITE, e il trasferimento parte alle 10:22:05 alla
/// prima pressione del tasto rapido.
/// </para>
/// <para>
/// La causa: la strada di Invio azzerava la conferma in sospeso e poi rifaceva il
/// controllo che l'aveva prodotta, riproducendo lo stesso avviso. Il tasto rapido no,
/// perché passa da un punto in cui la conferma viene <i>consumata</i>. Due strade per lo
/// stesso gesto, e una sola funzionante — con l'aggravante che l'avviso nominava proprio
/// l'altra.
/// </para>
/// <para>
/// Nelle prove Invio è <c>AttendedTransferNowAsync</c>: è il metodo che la finestra
/// chiama quando il tastierino semplificato risolve Invio in «chiama l'interno per
/// annunciargli la chiamata».
/// </para>
/// </remarks>
public sealed class ConfermaDellAvvisoTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 17, 10, 22, 0, TimeSpan.FromHours(2));

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public CallSessionManager Calls { get; }

        public TransferOrchestrator Transfer { get; }

        public Harness(TransferOptions options, FakeContactRepository? rubrica = null)
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), new AnnouncementContext(),
                contacts: null, options: null, clock: Clock);

            Transfer = new TransferOrchestrator(
                Engine, Calls, Announcer, contacts: rubrica, options: options, clock: Clock);
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
    }

    /// <summary>
    /// Opzioni che riproducono la postazione in esercizio: avviso sulle destinazioni non
    /// disponibili attivo, e il nome del tasto che la modalità semplificata insegna.
    /// </summary>
    private static TransferOptions Opzioni() => new()
    {
        ConfirmExternalDestinations = false,
        WarnOnUnavailableDestination = true,
        TransferKeyName = SimplifiedKeypad.ConfirmKeyName,
    };

    /// <summary>
    /// Porta la postazione al punto esatto della segnalazione: chiamante parcheggiato,
    /// interno composto, avviso preventivo appena pronunciato.
    /// </summary>
    private static async Task<Harness> AllAvvisoAsync(
        PresenceState presenza = PresenceState.Offline)
    {
        var rubrica = new FakeContactRepository().AddExtension("Reparto Analisi", "4021", presenza);
        var h = new Harness(Opzioni(), rubrica);

        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.AttendedTransferNowAsync("4021");
        await Task.Delay(50);

        return h;
    }

    [Fact]
    public async Task LAvvisoVieneDettoEIlTrasferimentoNonParteAncora()
    {
        // Il primo Invio deve continuare ad avvertire: la protezione non si toglie, si
        // rende superabile.
        var h = await AllAvvisoAsync();

        Assert.True(h.Announcer.Said("fuori servizio"));
        Assert.False(h.Engine.Did("dial:4021"));
        Assert.Equal(TransferPhase.EnteringDestination, h.Transfer.Phase);
    }

    [Fact]
    public async Task IlSecondoInvioConfermaEIlTrasferimentoParte()
    {
        // E' il difetto segnalato: prima di qui il secondo Invio ripeteva l'avviso, e
        // il sesto pure.
        var h = await AllAvvisoAsync();
        h.Announcer.Clear();

        await h.Transfer.AttendedTransferNowAsync("4021");
        await Task.Delay(50);

        Assert.True(h.Engine.Did("dial:4021"));
        Assert.Equal(TransferPhase.Consulting, h.Transfer.Phase);
    }

    [Fact]
    public async Task IlSecondoInvioNonRipeteLAvviso()
    {
        var h = await AllAvvisoAsync();
        h.Announcer.Clear();

        await h.Transfer.AttendedTransferNowAsync("4021");
        await Task.Delay(50);

        // Sentire di nuovo "fuori servizio" dopo aver confermato direbbe che la
        // conferma non e' stata raccolta, ed e' esattamente il messaggio che l'operatore
        // riceveva.
        Assert.False(h.Announcer.Said("fuori servizio"));
        Assert.True(h.Announcer.Said("Chiamata a"));
    }

    [Fact]
    public async Task SeiInvioDiFilaNonRestanoSenzaEffetto()
    {
        // La sequenza del registro: sei pressioni in un secondo e mezzo. Prima
        // producevano sei avvisi e zero INVITE; ora la prima avverte, la seconda
        // conferma, e le altre non devono aprire una seconda linea.
        var h = await AllAvvisoAsync();

        for (var i = 0; i < 5; i++)
        {
            await h.Transfer.AttendedTransferNowAsync("4021");
            h.Clock.AdvanceMilliseconds(300);
            await Task.Delay(20);
        }

        // La chiamata all'interno porta l'identificativo in coda, quindi si conta il
        // prefisso: quello che conta e' che di linee verso l'interno ne parta una sola.
        Assert.Equal(
            1,
            h.Engine.Operations.Count(o => o.StartsWith("dial:4021", StringComparison.Ordinal)));
        Assert.Equal(TransferPhase.Consulting, h.Transfer.Phase);
    }

    [Fact]
    public async Task CorreggereUnaCifraFaRifareIlControllo()
    {
        // Chi si sente dire che l'interno e' fuori servizio spesso ha sbagliato una
        // cifra, e la corregge. La conferma di prima valeva per quel numero: sul numero
        // nuovo il controllo va rifatto, altrimenti il "procedi" verrebbe applicato a
        // una destinazione che nessuno ha verificato.
        var rubrica = new FakeContactRepository()
            .AddExtension("Reparto Analisi", "4021", PresenceState.Offline)
            .AddExtension("Segreteria", "4022", PresenceState.DoNotDisturb);

        var h = new Harness(Opzioni(), rubrica);
        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("4021");
        await h.Transfer.AttendedTransferNowAsync("4021");
        await Task.Delay(50);

        h.Announcer.Clear();

        await h.Transfer.SetDestinationAsync("4022");
        await h.Transfer.AttendedTransferNowAsync("4022");
        await Task.Delay(50);

        Assert.True(h.Announcer.Said("non disturbare"));
        Assert.False(h.Engine.Did("dial:4022"));
    }

    [Fact]
    public async Task SenzaAvvisoIlPrimoInvioBastaComePrima()
    {
        // La strada normale non cambia: un interno libero si chiama alla prima
        // pressione, senza conferme.
        var h = await AllAvvisoAsync(PresenceState.Available);

        Assert.True(h.Engine.Did("dial:4021"));
        Assert.Equal(TransferPhase.Consulting, h.Transfer.Phase);
    }

    [Fact]
    public async Task LaConfermaValeAncheSullaDestinazioneEsterna()
    {
        // Stessa strada, altro controllo: la conferma per il numero esterno era
        // superabile solo col tasto rapido, per la medesima ragione.
        var h = new Harness(new TransferOptions
        {
            ConfirmExternalDestinations = true,
            TransferKeyName = SimplifiedKeypad.ConfirmKeyName,
        });

        await h.ConChiamataAttivaAsync();

        await h.Transfer.HandleTransferKeyAsync();
        await h.Transfer.SetDestinationAsync("3491234567");
        await h.Transfer.AttendedTransferNowAsync("3491234567");
        await Task.Delay(50);

        Assert.True(h.Announcer.Said("esterna"));
        Assert.False(h.Engine.Did("dial:3491234567"));

        await h.Transfer.AttendedTransferNowAsync("3491234567");
        await Task.Delay(50);

        Assert.True(h.Engine.Did("dial:3491234567"));
    }

    [Fact]
    public void LAvvisoNominaIlTastoDellaModalitaAttiva()
    {
        // Un annuncio che nomina un tasto che in quella modalita' non esiste manda a
        // premere qualcosa che non fa niente, e la conclusione dell'operatore e' che il
        // programma sia guasto.
        Assert.Equal("Invio", SimplifiedKeypad.TransferKeyName(simplified: true, "F9"));
        Assert.Equal("F9", SimplifiedKeypad.TransferKeyName(simplified: false, "F9"));

        // Vale qualunque combinazione porti il profilo: in modalita' semplificata il
        // tasto insegnato e' Invio.
        Assert.Equal("Invio", SimplifiedKeypad.TransferKeyName(simplified: true, "Control T"));
    }
}
