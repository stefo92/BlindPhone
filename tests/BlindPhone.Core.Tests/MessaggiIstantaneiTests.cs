using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica le conversazioni a messaggi con i colleghi: che cosa arriva, che cosa si
/// dice, che cosa resta da leggere, e come si racconta una consegna non riuscita.
/// </summary>
/// <remarks>
/// La parte che conta è quella che chi guarda lo schermo non vede: quale frase parte,
/// quando parte, e se attraversa il silenzio della conversazione. Un messaggio letto ad
/// alta voce sopra un chiamante è un difetto quanto un messaggio taciuto.
/// </remarks>
public sealed class MessaggiIstantaneiTests
{
    private readonly FakeEngine _motore = new();
    private readonly RecordingAnnouncer _annunci = new();

    private InstantMessagingService Servizio(FakeContactRepository? rubrica = null)
        => new(_motore, _annunci, rubrica);

    /// <summary>Gli annunci partono in coda alla ricerca del nome: si aspetta che la catena finisca.</summary>
    private static async Task AspettaAsync(Func<bool> condizione)
    {
        for (var i = 0; i < 100 && !condizione(); i++) await Task.Delay(10);
        Assert.True(condizione(), "la condizione attesa non si e' verificata");
    }

    // -----------------------------------------------------------------------
    // Messaggi in arrivo
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnMessaggioInArrivoApreLaConversazioneEContaFraIDaLeggere()
    {
        using var servizio = Servizio();

        _motore.SimulateInstantMessage("201", "Ti passo una chiamata", "Mario Rossi");

        var conversazione = Assert.Single(servizio.Conversations);
        Assert.Equal("201", conversazione.Peer);
        Assert.Equal("Mario Rossi", conversazione.Name);
        Assert.Equal(1, conversazione.UnreadCount);
        Assert.Equal(1, servizio.UnreadTotal);

        var messaggio = Assert.Single(conversazione.Messages);
        Assert.Equal(InstantMessageDirection.Incoming, messaggio.Direction);
        Assert.Equal("Ti passo una chiamata", messaggio.Text);

        await AspettaAsync(() => _annunci.Announcements.Count > 0);
    }

    [Fact]
    public async Task ConLaFinestraChiusaSiDiceChiScriveECheCosa()
    {
        using var servizio = Servizio();

        _motore.SimulateInstantMessage("201", "Sei libero?", "Mario Rossi");

        await AspettaAsync(() => _annunci.Announcements.Count > 0);

        var annuncio = Assert.Single(_annunci.Announcements);
        Assert.Equal("Messaggio da Mario Rossi: Sei libero?", annuncio.Text);
        Assert.Equal(EarconKind.InstantMessageReceived, annuncio.Earcon);

        // Durante una telefonata la voce tace: passa il solo segnale.
        Assert.False(annuncio.BypassConversationSilence);
    }

    [Fact]
    public async Task ConLaConversazioneDavantiSiLeggeSubitoEAncheInLinea()
    {
        using var servizio = Servizio();
        servizio.VisibilityOf = _ => ConversationVisibility.InFront;

        _motore.SimulateInstantMessage("201", "Arrivo", "Mario Rossi");

        await AspettaAsync(() => _annunci.Announcements.Count > 0);

        var annuncio = Assert.Single(_annunci.Announcements);
        Assert.Equal("Mario Rossi: Arrivo", annuncio.Text);
        Assert.True(annuncio.BypassConversationSilence);

        // L'operatore lo sta leggendo adesso: non resta fra i da leggere.
        Assert.Equal(0, servizio.UnreadTotal);
    }

    [Fact]
    public async Task ConLaFinestraDietroIlMessaggioRestaDaLeggere()
    {
        using var servizio = Servizio();
        servizio.VisibilityOf = _ => ConversationVisibility.Background;

        _motore.SimulateInstantMessage("201", "Ci sei?");

        Assert.Equal(1, servizio.UnreadTotal);
        await AspettaAsync(() => _annunci.Said("Messaggio da"));
    }

    [Fact]
    public async Task SenzaNomeDichiaratoSiCercaInRubricaPrimaDiAnnunciare()
    {
        var rubrica = new FakeContactRepository().AddExtension("Anna Bianchi", "305");
        using var servizio = Servizio(rubrica);

        _motore.SimulateInstantMessage("305", "Grazie");

        await AspettaAsync(() => _annunci.Announcements.Count > 0);

        Assert.Equal("Messaggio da Anna Bianchi: Grazie", _annunci.Announcements[0].Text);
        Assert.Equal("Anna Bianchi", servizio.Find("305")?.Name);
    }

    [Fact]
    public async Task SenzaNomeEFuoriRubricaIlNumeroSiLeggeCifraPerCifra()
    {
        using var servizio = Servizio(new FakeContactRepository());

        _motore.SimulateInstantMessage("4021", "Ok");

        await AspettaAsync(() => _annunci.Announcements.Count > 0);

        Assert.Equal($"Messaggio da {SpeechFormatter.Digits("4021")}: Ok", _annunci.Announcements[0].Text);
    }

    [Fact]
    public async Task UnNomeCheEIlNumeroStessoNonValeComeNome()
    {
        using var servizio = Servizio();

        _motore.SimulateInstantMessage("201", "Ciao", fromName: "201");

        Assert.Null(servizio.Find("201")?.Name);
        await AspettaAsync(() => _annunci.Announcements.Count > 0);
    }

    [Fact]
    public async Task DueMessaggiDelloStessoCollegaFinisconoNellaStessaConversazioneInOrdine()
    {
        using var servizio = Servizio(new FakeContactRepository());

        _motore.SimulateInstantMessage("201", "primo");
        _motore.SimulateInstantMessage("201", "secondo");

        var conversazione = Assert.Single(servizio.Conversations);
        Assert.Equal(["primo", "secondo"], conversazione.Messages.Select(m => m.Text));
        Assert.Equal(2, conversazione.UnreadCount);

        await AspettaAsync(() => _annunci.Announcements.Count == 2);
        Assert.EndsWith("primo", _annunci.Announcements[0].Text);
        Assert.EndsWith("secondo", _annunci.Announcements[1].Text);
    }

    [Fact]
    public async Task UnMessaggioLungoSiTroncaNellAnnuncioMaNonNelloStorico()
    {
        using var servizio = Servizio();
        var lungo = string.Join(' ', Enumerable.Repeat("parola", 120));

        _motore.SimulateInstantMessage("201", lungo, "Mario Rossi");

        await AspettaAsync(() => _annunci.Announcements.Count > 0);

        Assert.Contains("continua nella finestra dei messaggi", _annunci.Announcements[0].Text);
        Assert.True(_annunci.Announcements[0].Text.Length < lungo.Length);
        Assert.Equal(lungo, servizio.Find("201")!.Messages[0].Text);
    }

    [Fact]
    public void LeggereLaConversazioneAzzeraIDaLeggere()
    {
        using var servizio = Servizio();
        var cambiamenti = 0;

        _motore.SimulateInstantMessage("201", "uno");
        _motore.SimulateInstantMessage("202", "due");
        servizio.ConversationsChanged += (_, _) => cambiamenti++;

        servizio.MarkRead("201");

        Assert.Equal(0, servizio.Find("201")!.UnreadCount);
        Assert.Equal(1, servizio.UnreadTotal);
        Assert.Equal(1, cambiamenti);

        // Una seconda volta non cambia niente, e non lo dice.
        servizio.MarkRead("201");
        Assert.Equal(1, cambiamenti);
    }

    // -----------------------------------------------------------------------
    // Messaggi in uscita
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnMessaggioMandatoEntraComeInvioInCorsoEPoiRisultaConsegnato()
    {
        using var servizio = Servizio();
        var inVolo = new TaskCompletionSource<InstantMessageSendResult>();
        _motore.PendingSend = inVolo;

        var stati = new List<InstantMessageDelivery>();
        servizio.MessageAdded += (_, e) => stati.Add(e.Message.Delivery);
        servizio.MessageUpdated += (_, e) => stati.Add(e.Message.Delivery);

        var invio = servizio.SendAsync("201", "  Ti passo il signor Verdi  ");

        // Prima della risposta del centralino il messaggio c'e' gia', «invio in corso».
        var inCorso = Assert.Single(servizio.Find("201")!.Messages);
        Assert.Equal(InstantMessageDelivery.Sending, inCorso.Delivery);
        Assert.Equal("Ti passo il signor Verdi", inCorso.Text);

        inVolo.SetResult(InstantMessageSendResult.Success);
        var esito = await invio;

        Assert.Equal(InstantMessageDelivery.Delivered, esito!.Delivery);
        Assert.Equal(InstantMessageDelivery.Delivered, servizio.Find("201")!.Messages[0].Delivery);
        Assert.Equal([InstantMessageDelivery.Sending, InstantMessageDelivery.Delivered], stati);
        Assert.Equal(("201", "Ti passo il signor Verdi"), Assert.Single(_motore.SentMessages));
        Assert.True(_annunci.Said(InstantMessageTexts.Delivered));

        // Un messaggio mandato non e' un messaggio da leggere.
        Assert.Equal(0, servizio.UnreadTotal);
    }

    [Fact]
    public async Task UnaConsegnaNonRiuscitaDiceIlPercheEFaSuonareIlRifiuto()
    {
        using var servizio = Servizio();
        _motore.NextSendResult = InstantMessageSendResult.Failure(InstantMessageTexts.DeliveryFailure(404));

        var esito = await servizio.SendAsync("999", "Pronto?");

        Assert.Equal(InstantMessageDelivery.Failed, esito!.Delivery);
        Assert.Equal("l'interno non esiste", esito.FailureReason);
        Assert.True(_annunci.Said("Messaggio non consegnato: l'interno non esiste"));
        Assert.Contains(EarconKind.CommandRejected, _annunci.Earcons);

        // L'annuncio passa anche durante una telefonata: risponde a un comando.
        Assert.All(_annunci.Announcements, a => Assert.True(a.BypassConversationSilence));
    }

    // -----------------------------------------------------------------------
    // Consegna senza conferma
    // -----------------------------------------------------------------------

    private static InstantMessageSendResult SenzaConferma()
        => InstantMessageSendResult.NotConfirmed(InstantMessageTexts.UnconfirmedReason);

    [Fact]
    public async Task SenzaConfermaNonSiDiceNonConsegnatoMaConsegnaNonConfermata()
    {
        // E' quello che succede dietro l'SBC di Yeastar: nessuna risposta per settanta
        // secondi, poi un 408. Non si sa se il messaggio sia arrivato, e la frase non
        // deve dire di piu'.
        using var servizio = Servizio();
        _motore.NextSendResult = SenzaConferma();

        var esito = await servizio.SendAsync("7391", "ciao");

        Assert.Equal(InstantMessageDelivery.Unconfirmed, esito!.Delivery);
        Assert.Equal(InstantMessageTexts.UnconfirmedReason, esito.FailureReason);

        var annuncio = Assert.Single(_annunci.Announcements);
        Assert.Equal($"Consegna non confermata: {InstantMessageTexts.UnconfirmedReason}", annuncio.Text);
        Assert.DoesNotContain("non consegnato", annuncio.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(EarconKind.CommandRejected, _annunci.Earcons);
    }

    [Fact]
    public async Task IlSuggerimentoArrivaAllaSecondaVoltaConUnCollegaDiversoEUnaVoltaSola()
    {
        using var servizio = Servizio();
        _motore.NextSendResult = SenzaConferma();

        // Lo stesso collega due volte puo' essere il suo telefono: niente suggerimento.
        await servizio.SendAsync("7381", "uno");
        await servizio.SendAsync("7381", "due");
        Assert.DoesNotContain(_annunci.Texts, t => t.Contains(InstantMessageTexts.ForwardingAdvice));

        // Un secondo collega diverso: e' il centralino, e lo si dice.
        await servizio.SendAsync("7394", "tre");
        Assert.EndsWith(InstantMessageTexts.ForwardingAdvice, _annunci.Announcements[^1].Text);

        // Una volta sola: al terzo collega la frase torna quella breve.
        await servizio.SendAsync("7391", "quattro");
        Assert.Equal(
            1,
            _annunci.Texts.Count(t => t.Contains(InstantMessageTexts.ForwardingAdvice)));
    }

    [Fact]
    public async Task UnaConsegnaRiuscitaRicominciaIlConto()
    {
        using var servizio = Servizio();

        _motore.NextSendResult = SenzaConferma();
        await servizio.SendAsync("7381", "uno");

        _motore.NextSendResult = InstantMessageSendResult.Success;
        await servizio.SendAsync("7388", "arrivato");

        // Dopo una consegna riuscita il centralino inoltra: un collega senza conferma
        // e' di nuovo il primo, non il secondo.
        _motore.NextSendResult = SenzaConferma();
        await servizio.SendAsync("7394", "due");

        Assert.DoesNotContain(_annunci.Texts, t => t.Contains(InstantMessageTexts.ForwardingAdvice));
    }

    [Fact]
    public void LaVoceDelloStoricoDiceConsegnaNonConfermata()
    {
        var messaggio = new InstantMessage(
            "im-1", "7391", "ciao", InstantMessageDirection.Outgoing,
            new DateTimeOffset(2026, 11, 4, 11, 6, 0, TimeSpan.Zero),
            InstantMessageDelivery.Unconfirmed,
            InstantMessageTexts.UnconfirmedReason);

        Assert.EndsWith(
            $"consegna non confermata: {InstantMessageTexts.UnconfirmedReason}",
            InstantMessageTexts.Accessible(messaggio, null));

        Assert.Equal(
            $"Consegna non confermata: {InstantMessageTexts.UnconfirmedReason}",
            InstantMessageTexts.DeliveryShown(messaggio));
    }

    [Fact]
    public async Task UnMessaggioVuotoNonParteESiDiceDiScriverlo()
    {
        using var servizio = Servizio();

        var esito = await servizio.SendAsync("201", "   ");

        Assert.Null(esito);
        Assert.Empty(_motore.SentMessages);
        Assert.Empty(servizio.Conversations);
        Assert.True(_annunci.Said(InstantMessageTexts.EmptyMessage));
    }

    [Fact]
    public async Task UnMotoreCheNonSaScrivereRispondeConUnRifiutoNonConUnEccezione()
    {
        using var servizio = Servizio();
        _motore.SupportsInstantMessaging = false;

        var esito = await servizio.SendAsync("201", "Ciao");

        Assert.Equal(InstantMessageDelivery.Failed, esito!.Delivery);
        Assert.Empty(_motore.SentMessages);
        Assert.True(_annunci.Said(InstantMessageTexts.NotSupported));
    }

    [Fact]
    public async Task LaRispostaDelCollegaFinisceNellaConversazioneApertaDaChiScrive()
    {
        using var servizio = Servizio();

        await servizio.SendAsync("02 1234-5678", "Richiamami");
        _motore.SimulateInstantMessage("0212345678", "Subito");

        var conversazione = Assert.Single(servizio.Conversations);
        Assert.Equal(["Richiamami", "Subito"], conversazione.Messages.Select(m => m.Text));
    }

    [Fact]
    public void LeConversazioniSiElencanoDallaPiuRecente()
    {
        var adesso = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(1));
        using var servizio = Servizio();

        _motore.InstantMessageAt("201", "vecchio", adesso);
        _motore.InstantMessageAt("202", "recente", adesso.AddMinutes(5));

        Assert.Equal(["202", "201"], servizio.Conversations.Select(c => c.Peer));
    }

    // -----------------------------------------------------------------------
    // Chiave della conversazione
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("201", "201")]
    [InlineData(" 201 ", "201")]
    [InlineData("sip:201", "201")]
    [InlineData("02 1234-5678", "0212345678")]
    [InlineData("(02) 1234.5678", "0212345678")]
    [InlineData("+39 02 1234 5678", "+390212345678")]
    [InlineData("mario.rossi", "mario.rossi")]
    public void LaChiaveDellaConversazioneIgnoraISeparatori(string scritto, string atteso)
        => Assert.Equal(atteso, InstantMessagingService.NormalizePeer(scritto));

    // -----------------------------------------------------------------------
    // Testi
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(404, "l'interno non esiste")]
    [InlineData(480, "il collega non è raggiungibile")]
    [InlineData(405, "il centralino non inoltra i messaggi")]
    [InlineData(501, "il centralino non inoltra i messaggi")]
    [InlineData(403, "il centralino non lo consente")]
    [InlineData(415, "il telefono del collega non accetta messaggi di testo")]
    [InlineData(408, InstantMessageTexts.UnconfirmedReason)]
    [InlineData(503, "errore del centralino, codice 503")]
    [InlineData(420, "rifiutato dal centralino, codice 420")]
    public void IRifiutiDelCentralinoSiDiconoInItaliano(int codice, string atteso)
        => Assert.Equal(atteso, InstantMessageTexts.DeliveryFailure(codice));

    [Fact]
    public void LaVoceDelloStoricoDiceChiCheCosaEQuando()
    {
        var alle = new DateTimeOffset(2026, 11, 4, 10, 42, 0, TimeSpan.Zero);
        var mio = new InstantMessage("im-1", "201", "Arrivo", InstantMessageDirection.Outgoing, alle);
        var suo = new InstantMessage("im-2", "201", "Ok", InstantMessageDirection.Incoming, alle);
        var fallito = mio with { Delivery = InstantMessageDelivery.Failed, FailureReason = "l'interno non esiste" };

        Assert.Equal("Tu: Arrivo, alle 10:42", InstantMessageTexts.Accessible(mio, "Mario Rossi"));
        Assert.Equal("Mario Rossi: Ok, alle 10:42", InstantMessageTexts.Accessible(suo, "Mario Rossi"));
        Assert.EndsWith("messaggio non consegnato: l'interno non esiste", InstantMessageTexts.Accessible(fallito, null));
    }

    [Fact]
    public void LaVoceDellElencoDiceIDaLeggerePrimaDellUltimoMessaggio()
    {
        var ultimo = new InstantMessage("im-1", "201", "Sei libero?", InstantMessageDirection.Incoming, DateTimeOffset.Now);

        Assert.Equal(
            "Mario Rossi, 2 messaggi da leggere, ultimo: Sei libero?",
            InstantMessageTexts.ConversationAccessible("Mario Rossi", "201", 2, ultimo));
    }

    // -----------------------------------------------------------------------
    // Pulsante della postazione
    // -----------------------------------------------------------------------

    [Fact]
    public void IlPulsanteDeiMessaggiContaQuelliDaLeggere()
    {
        var senza = CallActionCatalog.Available(default).Single(b => b.Action == CallAction.OpenMessages);
        var con = CallActionCatalog.Available(new CallActionContext(UnreadMessages: 3))
            .Single(b => b.Action == CallAction.OpenMessages);

        Assert.Equal("Messaggi", senza.Label);
        Assert.Equal(CallActionGroup.Station, senza.Group);
        Assert.Equal("Messaggi (3)", con.Label);
        Assert.Contains("3 messaggi da leggere", con.Description);
    }
}
