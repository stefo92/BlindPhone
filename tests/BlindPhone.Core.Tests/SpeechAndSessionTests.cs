using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica la resa parlata dei dati.
/// </summary>
/// <remarks>
/// Sembra cosmetica e non lo è. Una sintesi che legge "0212345" come
/// "duecentododicimilatrecentoquarantacinque" costringe l'operatore a farsi
/// ripetere il numero, e su un centralino il numero va trascritto o ricomposto al
/// primo ascolto.
/// </remarks>
public sealed class SpeechFormatterTests
{
    [Fact]
    public void UnNumeroSiLeggeCifraPerCifra()
    {
        var result = SpeechFormatter.Digits("021234");
        Assert.Equal("zero, due, uno, due, tre, quattro", result);
    }

    [Fact]
    public void GliInterniBreviSiLeggonoSempreCifraPerCifra()
    {
        // Anche chiedendo il raggruppamento naturale: "quattro zero due uno" e' come
        // l'operatore lo dira' al telefono, "quattromilaventuno" no.
        var result = SpeechFormatter.Digits("4021", DigitGrouping.Natural);
        Assert.Equal("zero", result.Split(", ")[1]);
        Assert.Contains("quattro", result);
    }

    [Fact]
    public void IlRaggruppamentoAcoppieInseriscePauseOgniDueCifre()
    {
        var result = SpeechFormatter.Digits("0212345678", DigitGrouping.Pairs);
        Assert.Equal("zero due, uno due, tre quattro, cinque sei, sette otto", result);
    }

    [Theory]
    [InlineData(0, "0 secondi")]
    [InlineData(1, "1 secondo")]
    [InlineData(45, "45 secondi")]
    [InlineData(60, "1 minuto")]
    [InlineData(84, "1 minuto e 24")]
    [InlineData(3600, "1 ora")]
    [InlineData(3900, "1 ora e 5 minuti")]
    public void LeDurateSiLeggonoInItalianoCorretto(int seconds, string expected)
    {
        Assert.Equal(expected, SpeechFormatter.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void IlNomeFoneticoHaLaPrecedenzaSulNomeAnagrafico()
    {
        // La sintesi pronuncia male molti cognomi, e un operatore che sente un nome
        // sbagliato lo ripete sbagliato al telefono.
        var party = new CallParty("4021", "Ceccherini", "Cekerini");
        Assert.Equal("Cekerini", party.SpeechName);
    }

    [Fact]
    public void SenzaNomeNeNumeroSiDiceSconosciuto()
    {
        Assert.Equal("sconosciuto", SpeechFormatter.Party(null, null));
    }
}

/// <summary>Verifica il conteggio e la lettura della coda.</summary>
public sealed class QueueTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void CodaVuota_SiDiceEsplicitamente()
    {
        var snapshot = QueueSnapshot.Empty(Now);
        Assert.Equal("Nessuna chiamata in coda", AnnouncementTexts.QueueCount(snapshot, Now));
    }

    [Fact]
    public void ConChiamateInAttesa_SiAnnunciaAncheLAttesaMassima()
    {
        var waiting = new List<QueuedCaller>
        {
            new("1", "Centralino", "0212345", "Mario Rossi", Now.AddSeconds(-130)),
            new("2", "Centralino", "0212346", null, Now.AddSeconds(-20)),
        };

        var snapshot = new QueueSnapshot(waiting, 0, 0, Now, true);
        var text = AnnouncementTexts.QueueCount(snapshot, Now);

        Assert.Contains("2 chiamate in coda", text);
        Assert.Contains("2 minuti e 10", text);
    }

    [Fact]
    public void LAttesaNonVaMaiNegativa()
    {
        // Uno scarto di orologio fra PBX e postazione non deve produrre
        // "in attesa da meno 3 secondi".
        var caller = new QueuedCaller("1", "Coda", "0212345", null, Now.AddSeconds(5));
        Assert.Equal(TimeSpan.Zero, caller.WaitDuration(Now));
    }
}

/// <summary>Verifica il gestore delle chiamate.</summary>
public sealed class CallSessionManagerTests
{
    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public AnnouncementContext Context { get; } = new();

        public CallSessionManager Calls { get; }

        public Harness()
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), Context);
        }
    }

    [Fact]
    public async Task MettereInPausaSiAnnunciaConDueParole()
    {
        // Segnalazione dal campo: alla pausa lo screen reader diceva «chiamata
        // attualmente in corso, stato del posto operatore» — i nomi delle due righe di
        // riepilogo, che erano regioni dinamiche. Quelle ora tacciono; qui si fissa
        // cosa deve dire la voce al posto loro.
        var h = new Harness();
        h.Engine.SimulateIncoming("0212345678", "Mario Rossi");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();
        h.Announcer.Clear();

        await h.Calls.ToggleHoldAsync();

        Assert.Equal("Chiamata in pausa", h.Announcer.Announcements.Single().Text);
    }

    [Fact]
    public async Task RiprendereSiAnnunciaConDueParole()
    {
        var h = new Harness();
        h.Engine.SimulateIncoming("0212345678", "Mario Rossi");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();
        await h.Calls.ToggleHoldAsync();
        h.Announcer.Clear();

        await h.Calls.ToggleHoldAsync();

        Assert.Equal("Chiamata ripresa", h.Announcer.Announcements.Single().Text);
    }

    [Fact]
    public async Task ConDueLineeLAlternanzaContinuaADireQualeEQuale()
    {
        // Il nome sparisce dalla pausa perche' li' l'operatore sa gia' chi ha in
        // linea. Dove invece la domanda «quale delle due?» esiste davvero, il nome
        // deve restare: e' la ragione per cui la frase breve non danneggia nulla.
        var h = new Harness();

        h.Engine.SimulateIncoming("0211111111", "Primo");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        h.Engine.SimulateIncoming("0222222222", "Secondo");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        h.Announcer.Clear();
        await h.Calls.SwapAsync();

        Assert.True(h.Announcer.Said("Primo"));
        Assert.True(h.Announcer.Said("Secondo"));
    }

    [Fact]
    public async Task RispondereAggiornaIlContestoDiConversazione()
    {
        var h = new Harness();
        h.Engine.SimulateIncoming("0212345678", "Mario Rossi");
        await Task.Delay(20);

        Assert.False(h.Context.IsConversationActive);

        await h.Calls.AnswerAsync();

        // Da questo momento la pipeline sopprime gli annunci non critici: e' il
        // principio del silenzio durante la conversazione.
        Assert.True(h.Context.IsConversationActive);
    }

    [Fact]
    public async Task RispondereConUnaChiamataInCorsoMetteInAttesaLaPrima()
    {
        var h = new Harness();
        var first = h.Engine.SimulateIncoming("0211111111", "Primo");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        h.Engine.SimulateIncoming("0222222222", "Secondo");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        // Rispondere a una seconda linea non deve buttare giu' la prima.
        Assert.True(h.Engine.Did($"hold:{first}"));
        Assert.Single(h.Calls.HeldCalls);
    }

    [Fact]
    public async Task ChiudereLUnicaChiamataAttivaRiprendeQuellaRimastaInAttesa()
    {
        var h = new Harness();
        var first = h.Engine.SimulateIncoming("0211111111", "Primo");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        h.Engine.SimulateIncoming("0222222222", "Secondo");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        h.Engine.Operations.Clear();
        await h.Calls.HangUpAsync();
        await Task.Delay(50);

        // Lasciare qualcuno in attesa senza che nessuno gli parli e' il modo piu'
        // comune per dimenticarlo in linea.
        Assert.True(h.Engine.Did($"resume:{first}"));
    }

    [Fact]
    public async Task AlternareProduceUnSoloAnnuncioCheDiceEntrambiGliInterlocutori()
    {
        var h = new Harness();
        h.Engine.SimulateIncoming("0211111111", "Primo");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        h.Engine.SimulateIncoming("0222222222", "Secondo");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        h.Announcer.Clear();
        await h.Calls.SwapAsync();

        // Due annunci consecutivi ("in attesa" e poi "ripresa") costringerebbero ad
        // ascoltare due frasi per capire una cosa sola.
        Assert.Single(h.Announcer.Announcements);
        Assert.True(h.Announcer.Said("Primo"));
        Assert.True(h.Announcer.Said("Secondo"));
    }

    [Fact]
    public void UnEventoFuoriSequenzaVieneIgnoratoSenzaEccezioni()
    {
        var h = new Harness();

        // La rete consegna BYE duplicati e risposte tardive: non devono far cadere
        // l'applicazione, che su un posto operatore significa perdere tutte le
        // chiamate in corso.
        var applied = h.Calls.ApplyTrigger("inesistente", CallTrigger.RemoteHangUp);
        Assert.False(applied);
    }

    [Fact]
    public async Task LaPerditaDiRegistrazioneVieneAnnunciataComeCritica()
    {
        var h = new Harness();
        h.Announcer.Clear();

        h.Engine.SimulateRegistration(RegistrationStatus.Failed, "Credenziali rifiutate dal centralino");
        await Task.Delay(20);

        var announcement = h.Announcer.Announcements.Single();
        Assert.Equal(AnnouncementPriority.Critical, announcement.Priority);
        Assert.Equal(EarconKind.RegistrationLost, announcement.Earcon);

        // L'operatore non ha modo di accorgersi da solo di non essere piu'
        // raggiungibile: il silenzio e' indistinguibile da un momento di calma.
        Assert.True(h.Announcer.Said("non registrato"));
    }

    // -----------------------------------------------------------------------
    // Quando c'e' qualcuno in linea
    // -----------------------------------------------------------------------
    //
    // E' la condizione che vieta di comporre un numero nuovo, ed e' letta dal vivo
    // dalle finestre che devono decidere in quel momento: rubrica ed elenco delle
    // ultime chiamate. Su quegli elenchi Invio chiamava il contatto selezionato, e con
    // un chiamante in conversazione apriva una seconda linea in parallelo chiudendo per
    // giunta la finestra. Il tasto per passargli la chiamata su quegli elenchi esiste
    // gia' ed e' F9.

    [Fact]
    public async Task ConIlChiamanteInConversazioneSiRisultaInLinea()
    {
        var h = new Harness();
        h.Engine.SimulateIncoming("0212345678", "Mario Rossi");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();

        Assert.True(h.Calls.IsTalking);
    }

    [Fact]
    public async Task MettendoInAttesaNonSiRisultaPiuInLinea()
    {
        // E' la meta' della regola che permette di procurarsi la seconda chiamata di
        // una conferenza a tre: si mette in attesa, poi si compone.
        var h = new Harness();
        h.Engine.SimulateIncoming("0212345678", "Mario Rossi");
        await Task.Delay(20);
        await h.Calls.AnswerAsync();
        await h.Calls.ToggleHoldAsync();

        Assert.False(h.Calls.IsTalking);
    }

    [Fact]
    public async Task UnaChiamataCheSquillaNonEUnaConversazione()
    {
        // Confine dichiarato: finche' non le si e' risposto nessuno sente l'operatore.
        var h = new Harness();
        h.Engine.SimulateIncoming("0212345678", "Mario Rossi");
        await Task.Delay(20);

        Assert.False(h.Calls.IsTalking);
    }

    [Fact]
    public void SenzaChiamateNonSiRisultaInLinea()
        => Assert.False(new Harness().Calls.IsTalking);
}
