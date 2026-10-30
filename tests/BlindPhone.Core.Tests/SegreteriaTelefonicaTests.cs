using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sulla segreteria telefonica: lettura delle notifiche, annunci, numero da
/// chiamare.
/// </summary>
/// <remarks>
/// <para>
/// I corpi delle notifiche qui sotto sono quelli che i centralini mandano davvero,
/// ciascuno con le proprie abitudini: maiuscole, spazi, righe di fax, conteggi assenti.
/// È l'unica parte della funzione che si possa collaudare senza un centralino, e la
/// sola da cui dipende che cosa l'operatore sentirà.
/// </para>
/// </remarks>
public sealed class SegreteriaTelefonicaTests
{
    // -----------------------------------------------------------------------
    // Lettura del corpo
    // -----------------------------------------------------------------------

    [Fact]
    public void IlCorpoCompletoDellaNormaSiLeggeRigaPerRiga()
    {
        var stato = MessageSummaryParser.Parse(
            "Messages-Waiting: yes\r\nMessage-Account: sip:*97@centralino.ente.it\r\nVoice-Message: 2/8 (1/0)\r\n");

        Assert.NotNull(stato);
        Assert.True(stato.MessagesWaiting);
        Assert.Equal("sip:*97@centralino.ente.it", stato.MessageAccount);
        Assert.Equal(2, stato.NewMessages);
        Assert.Equal(8, stato.OldMessages);
        Assert.Equal(1, stato.UrgentNewMessages);
        Assert.Equal(0, stato.UrgentOldMessages);
    }

    [Fact]
    public void LaParteFraParentesiEFacoltativa()
    {
        // Lo scrive cosi' piu' di un centralino: nuovi e vecchi, senza gli urgenti.
        var stato = MessageSummaryParser.Parse("Messages-Waiting: yes\nVoice-Message: 1/5\n");

        Assert.NotNull(stato);
        Assert.Equal(1, stato.NewMessages);
        Assert.Equal(5, stato.OldMessages);
        Assert.Null(stato.UrgentNewMessages);
    }

    [Fact]
    public void UnCorpoSenzaConteggiDiceSoloCheCiSonoMessaggi()
    {
        // La norma rende obbligatoria la sola spia: un numero non c'e', e non va
        // inventato.
        var stato = MessageSummaryParser.Parse("Messages-Waiting: yes\r\n");

        Assert.NotNull(stato);
        Assert.True(stato.MessagesWaiting);
        Assert.Null(stato.NewMessages);
        Assert.Null(stato.NewToAnnounce);
    }

    [Theory]
    [InlineData("messages-waiting: YES\nvoice-message: 3/0")]
    [InlineData("MESSAGES-WAITING:yes\nVOICE-MESSAGE:3/0")]
    [InlineData("  Messages-Waiting :  Yes  \n  Voice-Message :  3 / 0  ( 0 / 0 )  ")]
    public void MaiuscoleESpaziNonCambianoLaLettura(string corpo)
    {
        var stato = MessageSummaryParser.Parse(corpo);

        Assert.NotNull(stato);
        Assert.True(stato.MessagesWaiting);
        Assert.Equal(3, stato.NewMessages);
        Assert.Equal(0, stato.OldMessages);
    }

    [Fact]
    public void LeRigheDiAltriTipiDiMessaggioNonDisturbano()
    {
        // Fax e testo stanno in mezzo, e la riga senza due punti e' rumore da saltare.
        var stato = MessageSummaryParser.Parse(
            "Messages-Waiting: yes\r\n"
            + "Fax-Message: 4/1\r\n"
            + "riga senza senso\r\n"
            + "Voice-Message: 2/0 (0/0)\r\n"
            + "Text-Message: 7/7\r\n"
            + "X-Estensione-Del-Produttore: qualcosa\r\n");

        Assert.NotNull(stato);
        Assert.Equal(2, stato.NewMessages);
        Assert.Equal(0, stato.OldMessages);
    }

    [Fact]
    public void LaSpiaSpentaValeZeroAncheSeIConteggiDiconoAltro()
    {
        // E' la riga che spegne la spia dei telefoni da tavolo: annunciare messaggi che
        // la spia nega farebbe cercare all'operatore qualcosa che non c'e'.
        var stato = MessageSummaryParser.Parse("Messages-Waiting: no\nVoice-Message: 3/0");

        Assert.NotNull(stato);
        Assert.False(stato.MessagesWaiting);
        Assert.Equal(0, stato.NewToAnnounce);
    }

    [Fact]
    public void LaSpiaAccesaConZeroVocaliDiceCheCiSonoNovitaSenzaNumero()
    {
        // La spia riguarda tutte le classi di messaggi: puo' averla accesa un fax.
        var stato = MessageSummaryParser.Parse("Messages-Waiting: yes\nVoice-Message: 0/4\nFax-Message: 1/0");

        Assert.NotNull(stato);
        Assert.Null(stato.NewToAnnounce);
    }

    [Theory]
    [InlineData("Voice-Message: 99999999999/0")]
    [InlineData("Voice-Message: 4294967295/0")]
    [InlineData("Voice-Message: 10000/0")]
    public void UnConteggioAssurdoDiventaNonSiSaQuanti(string riga)
    {
        var stato = MessageSummaryParser.Parse("Messages-Waiting: yes\n" + riga);

        Assert.NotNull(stato);
        Assert.Null(stato.NewMessages);
        Assert.Equal(0, stato.OldMessages);
        Assert.Null(stato.NewToAnnounce);
    }

    [Theory]
    [InlineData("Voice-Message: -3/0")]
    [InlineData("Voice-Message: tre/zero")]
    [InlineData("Voice-Message: /")]
    [InlineData("Voice-Message: ٣/٠")]
    public void UnaRigaDeiConteggiIllegibileNonFaPerdereLaSpia(string riga)
    {
        var stato = MessageSummaryParser.Parse("Messages-Waiting: yes\n" + riga);

        Assert.NotNull(stato);
        Assert.True(stato.MessagesWaiting);
        Assert.Null(stato.NewMessages);
        Assert.Null(stato.OldMessages);
    }

    [Fact]
    public void ILimitePlausibileStessoSiAccetta()
        => Assert.Equal(
            MessageSummaryParser.MaxPlausibleCount,
            MessageSummaryParser.Parse($"Messages-Waiting: yes\nVoice-Message: {MessageSummaryParser.MaxPlausibleCount}/0")!.NewMessages);

    [Fact]
    public void UrgentiPiuDeiNuoviSiContanoAlMassimoQuantiINuovi()
    {
        var stato = MessageSummaryParser.Parse("Messages-Waiting: yes\nVoice-Message: 1/0 (5/0)");

        Assert.NotNull(stato);
        Assert.Equal(1, stato.UrgentToAnnounce);
    }

    [Fact]
    public void SenzaSpiaLaSiRicavaDaiConteggi()
    {
        var conMessaggi = MessageSummaryParser.Parse("Voice-Message: 2/1");
        var senza = MessageSummaryParser.Parse("Voice-Message: 0/1");

        Assert.True(conMessaggi!.MessagesWaiting);
        Assert.False(senza!.MessagesWaiting);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("Message-Account: sip:*97@centralino")]
    [InlineData("Messages-Waiting: forse")]
    [InlineData("testo qualunque senza intestazioni")]
    public void UnCorpoCheNonDiceNullaNonProduceStato(string? corpo)
        => Assert.Null(MessageSummaryParser.Parse(corpo));

    [Fact]
    public void DiUnaRigaRipetutaValeLaPrima()
    {
        var stato = MessageSummaryParser.Parse("Messages-Waiting: yes\nVoice-Message: 2/0\nVoice-Message: 9/9");

        Assert.Equal(2, stato!.NewMessages);
    }

    [Fact]
    public void LeParentesiAngolariAttornoAllaCasellaSiTolgono()
    {
        var stato = MessageSummaryParser.Parse("Messages-Waiting: no\nMessage-Account: <sip:*97@centralino>");

        Assert.Equal("sip:*97@centralino", stato!.MessageAccount);
    }

    [Fact]
    public void IlCommentoDopoLaSpiaNonConta()
        => Assert.True(MessageSummaryParser.Parse("Messages-Waiting: yes ; commento")!.MessagesWaiting);

    // -----------------------------------------------------------------------
    // Quando annunciare
    // -----------------------------------------------------------------------

    private static MessageSummary Nuovi(int? nuovi, bool spia = true)
        => new(spia, null, nuovi, 0, null, null);

    [Fact]
    public void LaPrimaNotiziaConMessaggiNuoviSiAnnuncia()
        => Assert.True(VoicemailService.ShouldAnnounce(null, Nuovi(2)));

    [Fact]
    public void LaPrimaNotiziaSenzaMessaggiNuoviTace()
        => Assert.False(VoicemailService.ShouldAnnounce(null, Nuovi(0, spia: false)));

    [Fact]
    public void IlRinnovoConLoStessoNumeroTace()
    {
        // E' il caso di ogni rinnovo della sottoscrizione: ripetere l'avviso ogni dieci
        // minuti lo trasformerebbe in un rumore da ignorare.
        Assert.False(VoicemailService.ShouldAnnounce(Nuovi(2), Nuovi(2)));
    }

    [Fact]
    public void UnMessaggioInPiuSiAnnuncia()
        => Assert.True(VoicemailService.ShouldAnnounce(Nuovi(2), Nuovi(3)));

    [Fact]
    public void UnMessaggioAscoltatoTace()
        => Assert.False(VoicemailService.ShouldAnnounce(Nuovi(3), Nuovi(2)));

    [Fact]
    public void DaNessunoAQualcunoSiAnnuncia()
        => Assert.True(VoicemailService.ShouldAnnounce(Nuovi(0, spia: false), Nuovi(1)));

    [Fact]
    public void SenzaNumeroSiAnnunciaUnaVoltaSola()
    {
        Assert.True(VoicemailService.ShouldAnnounce(null, Nuovi(null)));
        Assert.False(VoicemailService.ShouldAnnounce(Nuovi(null), Nuovi(null)));
    }

    [Fact]
    public void UnNumeroCheCompareOScompareNonEUnMessaggioNuovo()
    {
        Assert.False(VoicemailService.ShouldAnnounce(Nuovi(null), Nuovi(2)));
        Assert.False(VoicemailService.ShouldAnnounce(Nuovi(2), Nuovi(null)));
    }

    // -----------------------------------------------------------------------
    // Il servizio
    // -----------------------------------------------------------------------

    [Fact]
    public void IRinnoviIdenticiProduconoUnSoloAnnuncio()
    {
        var motore = new FakeEngine();
        var voce = new RecordingAnnouncer();
        using var servizio = new VoicemailService(motore, voce);

        for (var i = 0; i < 5; i++) motore.SimulateMessageWaiting(Nuovi(2));

        var annuncio = Assert.Single(voce.Announcements);
        Assert.Equal("2 nuovi messaggi in segreteria", annuncio.Text);
        Assert.Equal(AnnouncementPriority.Normal, annuncio.Priority);
    }

    [Fact]
    public void UnNuovoMessaggioDopoIRinnoviSiAnnunciaDiNuovo()
    {
        var motore = new FakeEngine();
        var voce = new RecordingAnnouncer();
        using var servizio = new VoicemailService(motore, voce);

        motore.SimulateMessageWaiting(Nuovi(1));
        motore.SimulateMessageWaiting(Nuovi(1));
        motore.SimulateMessageWaiting(Nuovi(2));

        Assert.Equal(
            ["1 nuovo messaggio in segreteria", "2 nuovi messaggi in segreteria"],
            voce.Announcements.Select(a => a.Text));
    }

    [Fact]
    public void LaFinestraSiAggiornaSoloQuandoLoStatoCambia()
    {
        var motore = new FakeEngine();
        using var servizio = new VoicemailService(motore, new RecordingAnnouncer());
        var aggiornamenti = 0;
        servizio.Changed += (_, _) => aggiornamenti++;

        motore.SimulateMessageWaiting(Nuovi(1));
        motore.SimulateMessageWaiting(Nuovi(1));
        motore.SimulateMessageWaiting(Nuovi(0, spia: false));

        Assert.Equal(2, aggiornamenti);
        Assert.Equal(0, servizio.Current!.NewToAnnounce);
    }

    [Fact]
    public void CambiandoAccountLaCasellaDiPrimaSiDimentica()
    {
        var motore = new FakeEngine();
        var voce = new RecordingAnnouncer();
        using var servizio = new VoicemailService(motore, voce);

        motore.SimulateMessageWaiting(Nuovi(2), account: "Comune");
        motore.SimulateAccountRegistration("Ospedale");

        Assert.Null(servizio.Current);

        // La casella del nuovo account e' una notizia nuova, anche con lo stesso numero.
        motore.SimulateMessageWaiting(Nuovi(2), account: "Ospedale");

        Assert.Equal(2, voce.Announcements.Count);
        Assert.Equal("Ospedale", servizio.AccountName);
    }

    [Fact]
    public void LaRegistrazioneDelloStessoAccountNonCancellaLaCasella()
    {
        var motore = new FakeEngine();
        using var servizio = new VoicemailService(motore, new RecordingAnnouncer());

        motore.SimulateMessageWaiting(Nuovi(2), account: "Comune");
        motore.SimulateAccountRegistration("Comune");

        Assert.NotNull(servizio.Current);
    }

    [Fact]
    public void IlNumeroPropostoValeSoloPerLAccountInUso()
    {
        var motore = new FakeEngine();
        using var servizio = new VoicemailService(motore, new RecordingAnnouncer());

        motore.SimulateMessageWaiting(
            new MessageSummary(true, "sip:*97@centralino", 1, 0, null, null), account: "Comune");

        Assert.Equal("*97", servizio.ProposedNumber("Comune", "200"));
        Assert.Null(servizio.ProposedNumber("Ospedale", "200"));
    }

    [Fact]
    public void DopoLaChiusuraIlServizioNonParlaPiu()
    {
        var motore = new FakeEngine();
        var voce = new RecordingAnnouncer();
        var servizio = new VoicemailService(motore, voce);

        servizio.Dispose();
        motore.SimulateMessageWaiting(Nuovi(3));

        Assert.Empty(voce.Announcements);
    }

    // -----------------------------------------------------------------------
    // Testi
    // -----------------------------------------------------------------------

    [Fact]
    public void LAnnuncioConcordaIlNumero()
    {
        Assert.Equal("1 nuovo messaggio in segreteria", AnnouncementTexts.VoicemailArrived(Nuovi(1)));
        Assert.Equal("2 nuovi messaggi in segreteria", AnnouncementTexts.VoicemailArrived(Nuovi(2)));
        Assert.Equal("Nuovi messaggi in segreteria", AnnouncementTexts.VoicemailArrived(Nuovi(null)));
    }

    [Fact]
    public void GliUrgentiSiDiconoDopoIlLuogo()
    {
        var stato = new MessageSummary(true, null, 3, 0, 1, 0);

        Assert.Equal("3 nuovi messaggi in segreteria, di cui 1 urgente", AnnouncementTexts.VoicemailArrived(stato));
    }

    [Fact]
    public void SeSonoTuttiUrgentiLoSiDiceNelSostantivo()
    {
        Assert.Equal(
            "1 nuovo messaggio urgente in segreteria",
            AnnouncementTexts.VoicemailArrived(new MessageSummary(true, null, 1, 0, 1, 0)));
        Assert.Equal(
            "2 nuovi messaggi urgenti in segreteria",
            AnnouncementTexts.VoicemailArrived(new MessageSummary(true, null, 2, 0, 2, 0)));
    }

    [Fact]
    public void LaRigaDiStatoDiceDiCheCosaParla()
    {
        Assert.Equal(
            "Segreteria: nessun messaggio nuovo",
            AnnouncementTexts.VoicemailStatus(new MessageSummary(false, null, 0, 0, null, null)));
        Assert.Equal(
            "Segreteria: nessun messaggio nuovo. 1 messaggio già ascoltato",
            AnnouncementTexts.VoicemailStatus(new MessageSummary(false, null, 0, 1, null, null)));
        Assert.Equal(
            "Segreteria: 2 nuovi messaggi, di cui 1 urgente. 5 messaggi già ascoltati",
            AnnouncementTexts.VoicemailStatus(new MessageSummary(true, null, 2, 5, 1, 0)));
        Assert.Equal(
            "Segreteria: nuovi messaggi da ascoltare",
            AnnouncementTexts.VoicemailStatus(Nuovi(null)));
    }

    [Fact]
    public void LIconaParlaSoloQuandoCiSonoMessaggiDaAscoltare()
    {
        Assert.Null(AnnouncementTexts.VoicemailTrayHint(null));
        Assert.Null(AnnouncementTexts.VoicemailTrayHint(Nuovi(0, spia: false)));
        Assert.Equal("1 messaggio in segreteria", AnnouncementTexts.VoicemailTrayHint(Nuovi(1)));
        Assert.Equal("12 messaggi in segreteria", AnnouncementTexts.VoicemailTrayHint(Nuovi(12)));
        Assert.Equal("messaggi in segreteria", AnnouncementTexts.VoicemailTrayHint(Nuovi(null)));
    }

    [Fact]
    public void SenzaNumeroIlComandoDiceDoveImpostarloEQualeProponeIlCentralino()
    {
        Assert.Contains("Impostazioni, scheda Account SIP", AnnouncementTexts.VoicemailNumberMissing(null));

        var conProposta = AnnouncementTexts.VoicemailNumberMissing("*97");
        Assert.Contains("asterisco", conProposta);
        Assert.Contains("Impostazioni, scheda Account SIP", conProposta);
    }

    [Fact]
    public void IlRiepilogoNominaLaSegreteriaSoloSeCiSonoMessaggiNuovi()
    {
        var registrato = new SipRegistrationState(RegistrationStatus.Registered, "test");
        var coda = QueueSnapshot.Empty(DateTimeOffset.Now);
        var agente = new AgentState(AgentStatus.Available);

        var con = AnnouncementTexts.StatusSummary(null, [], coda, agente, registrato, DateTimeOffset.Now, voicemail: Nuovi(2));
        var senza = AnnouncementTexts.StatusSummary(
            null, [], coda, agente, registrato, DateTimeOffset.Now, voicemail: Nuovi(0, spia: false));

        Assert.Contains("2 nuovi messaggi in segreteria", con);
        Assert.DoesNotContain("segreteria", senza);
    }

    // -----------------------------------------------------------------------
    // Numero proposto dal centralino
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("sip:*97@centralino.ente.it", "*97")]
    [InlineData("<sip:*97@centralino.ente.it>", "*97")]
    [InlineData("sip:%2A97@centralino.ente.it", "*97")]
    [InlineData("sips:8500@centralino.ente.it;transport=tls", "8500")]
    [InlineData("sip:8500;phone-context=ente@centralino", "8500")]
    [InlineData("tel:+390612345678", "+390612345678")]
    [InlineData("SIP:#100@centralino", "#100")]
    public void DallaCasellaSiRicavaUnNumeroComponibile(string casella, string atteso)
        => Assert.Equal(atteso, VoicemailNumbers.ProposalFrom(casella, "200"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sip:asterisk@192.168.1.10")]
    [InlineData("sip:voicemail@centralino")]
    [InlineData("sip:@centralino")]
    [InlineData("centralino.ente.it")]
    [InlineData("mailto:segreteria@ente.it")]
    [InlineData("sip:%ZZ@centralino")]
    public void UnNomeDiServizioNonSiPropone(string? casella)
        => Assert.Null(VoicemailNumbers.ProposalFrom(casella, "200"));

    [Fact]
    public void LaPropriaCasellaVistaDaFuoriNonSiPropone()
    {
        // Comporre il proprio interno lascia un messaggio, non lo ascolta.
        Assert.Null(VoicemailNumbers.ProposalFrom("sip:200@centralino", "200"));
    }

    [Fact]
    public void IlNumeroScrittoConservaAsteriscoECancelletto()
        => Assert.Equal("*97#", VoicemailNumbers.Normalize("  *97#  "));

    // -----------------------------------------------------------------------
    // Quanto insistere con la sottoscrizione
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(489)]
    [InlineData(501)]
    [InlineData(405)]
    public void UnRifiutoDefinitivoNonSiRitenta(int codice)
        => Assert.Null(MessageWaitingSubscriptionPolicy.RetryAfterRefusal(codice));

    [Theory]
    [InlineData(408)]
    [InlineData(480)]
    [InlineData(500)]
    [InlineData(503)]
    public void UnErrorePasseggeroSiRitentaFraCinqueMinuti(int codice)
        => Assert.Equal(TimeSpan.FromMinutes(5), MessageWaitingSubscriptionPolicy.RetryAfterRefusal(codice));

    [Theory]
    [InlineData("rejected")]
    [InlineData("noresource")]
    [InlineData("invariant")]
    [InlineData("REJECTED")]
    [InlineData("giveup")]
    public void UnaChiusuraDefinitivaNonSiRiapre(string motivo)
        => Assert.Null(MessageWaitingSubscriptionPolicy.RetryAfterTermination(motivo, null));

    [Theory]
    [InlineData("deactivated")]
    [InlineData("timeout")]
    [InlineData("probation")]
    [InlineData(null)]
    public void UnaChiusuraPasseggeraSiRiapreDopoMezzoMinuto(string? motivo)
        => Assert.Equal(TimeSpan.FromSeconds(30), MessageWaitingSubscriptionPolicy.RetryAfterTermination(motivo, null));

    [Fact]
    public void IlRetryAfterDelCentralinoSiRispettaEntroUnOra()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), MessageWaitingSubscriptionPolicy.RetryAfterTermination("probation", 120));
        Assert.Equal(TimeSpan.FromMinutes(2), MessageWaitingSubscriptionPolicy.RetryAfterTermination("giveup", 120));
        Assert.Equal(TimeSpan.FromHours(1), MessageWaitingSubscriptionPolicy.RetryAfterTermination("timeout", 999_999));
        Assert.Equal(TimeSpan.FromSeconds(30), MessageWaitingSubscriptionPolicy.RetryAfterTermination("timeout", 1));
    }

    [Fact]
    public void LoStatoDellaSottoscrizioneSiLeggeConMotivoEAttesa()
    {
        Assert.Equal((false, null, null), MessageWaitingSubscriptionPolicy.ReadSubscriptionState("active;expires=598"));
        Assert.Equal(
            (true, "timeout", 30),
            MessageWaitingSubscriptionPolicy.ReadSubscriptionState("terminated;reason=timeout;retry-after=30"));
        Assert.Equal(
            (true, "noresource", null),
            MessageWaitingSubscriptionPolicy.ReadSubscriptionState("Terminated ; Reason = noresource"));
        Assert.Equal((true, null, null), MessageWaitingSubscriptionPolicy.ReadSubscriptionState("terminated"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(";;;")]
    [InlineData("pending")]
    public void UnoStatoAssenteOStranoValeAttiva(string? intestazione)
        => Assert.False(MessageWaitingSubscriptionPolicy.ReadSubscriptionState(intestazione).Terminated);

    [Fact]
    public void UnRetryAfterIllegibileSiIgnora()
        => Assert.Null(MessageWaitingSubscriptionPolicy.ReadSubscriptionState("terminated;retry-after=-5").RetryAfterSeconds);

    [Fact]
    public void SeIlCentralinoConcedeMenoTempoSiRinnovaPrima()
    {
        var richiesta = MessageWaitingSubscriptionPolicy.RequestedExpirySeconds;

        Assert.Equal(TimeSpan.FromSeconds(240), MessageWaitingSubscriptionPolicy.EarlyRefresh(richiesta, 300));
        Assert.Equal(TimeSpan.FromSeconds(5), MessageWaitingSubscriptionPolicy.EarlyRefresh(richiesta, 3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(600)]
    [InlineData(3600)]
    public void SeIlCentralinoConcedeQuantoChiestoBastaIlRinnovoOrdinario(int? concessa)
        => Assert.Null(MessageWaitingSubscriptionPolicy.EarlyRefresh(
            MessageWaitingSubscriptionPolicy.RequestedExpirySeconds, concessa));

    // -----------------------------------------------------------------------
    // Tasto rapido
    // -----------------------------------------------------------------------

    [Fact]
    public void LaSegreteriaHaUnTastoPredefinitoSenzaConflitti()
    {
        var profilo = HotkeyProfile.CreateDefault();
        var tasto = profilo.Get(HotkeyCommand.CallVoicemail, HotkeyScope.Local);

        Assert.NotNull(tasto);
        Assert.Equal("V", tasto.Key);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, tasto.Modifiers);

        var esito = new HotkeyConflictDetector().Check(tasto, profilo, HotkeyCommandCatalog.Labels);
        Assert.Equal(HotkeyConflictKind.None, esito.Kind);
    }

    [Fact]
    public void UnProfiloSalvatoPrimaDellaSegreteriaRiceveIlTastoNuovo()
    {
        // Le postazioni in servizio hanno un profilo salvato che il comando non lo
        // conosce: senza la migrazione la segreteria resterebbe senza tasto proprio
        // dove serve.
        var vecchio = HotkeyProfile.CreateDefault();
        vecchio.Remove(HotkeyCommand.CallVoicemail, HotkeyScope.Local);
        vecchio.KnownCommands.Remove(HotkeyCommand.CallVoicemail);

        var riletto = HotkeyProfileSerializer.DeserializeSingle(HotkeyProfileSerializer.SerializeSingle(vecchio))!;
        Assert.Null(riletto.Get(HotkeyCommand.CallVoicemail, HotkeyScope.Local));

        var aggiunti = riletto.AddMissingDefaults();

        Assert.Contains(HotkeyCommand.CallVoicemail, aggiunti);
        Assert.NotNull(riletto.Get(HotkeyCommand.CallVoicemail, HotkeyScope.Local));
    }
}
