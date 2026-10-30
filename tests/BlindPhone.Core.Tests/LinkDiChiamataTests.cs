using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sui link <c>tel:</c>, <c>sip:</c> e <c>callto:</c> che arrivano da un
/// gestionale, da una pagina web o da un messaggio di posta.
/// </summary>
/// <remarks>
/// <para>
/// Le prove sono in tre gruppi, e il terzo è quello che conta di più. I link scritti
/// come vuole la RFC; i link scritti come li scrivono davvero i gestionali, che una
/// lettura rigida della RFC rifiuterebbe; e i link costruiti per far fare al posto
/// operatore qualcosa che l'operatore non ha deciso.
/// </para>
/// <para>
/// Il link non compone mai da solo: anche il peggiore dei link accettati produce un
/// numero letto ad alta voce e non chiamato. Ma il numero finisce in un'intestazione
/// SIP, e lì un carattere di troppo non è un errore di battitura.
/// </para>
/// </remarks>
public sealed class LinkDiChiamataTests
{
    private const string Centralino = "centralino.ente.local";

    /// <summary>Quello che finirebbe nel campo di composizione.</summary>
    private static string Campo(string uri, string? dominio = Centralino)
    {
        Assert.True(CallLinkParser.TryParse(uri, out var link, out var errore), errore);
        return link!.Dial(dominio);
    }

    private static string Rifiuto(string? uri)
    {
        Assert.False(CallLinkParser.TryParse(uri, out var link, out var errore));
        Assert.Null(link);
        Assert.False(string.IsNullOrWhiteSpace(errore));
        return errore!;
    }

    // -----------------------------------------------------------------------
    // tel: secondo la RFC 3966
    // -----------------------------------------------------------------------

    [Fact]
    public void UnNumeroItalianoInFormaInternazionaleTornaNazionale()
    {
        // E' il numero che chiunque in Italia comporrebbe a mano, e passa da
        // qualunque centralino; il piu' lo accettano solo i piani di numerazione
        // configurati per riceverlo.
        Assert.Equal("0612345678", Campo("tel:+39-06-1234-5678"));
        Assert.Equal("3331234567", Campo("tel:+39.333.123.4567"));
    }

    [Fact]
    public void UnNumeroEsteroConservaIlPiu()
        => Assert.Equal("+41221234567", Campo("tel:+41-22-123-45-67"));

    [Fact]
    public void IlNumeroLocaleConIlContestoItalianoVieneAccettato()
        => Assert.Equal("0612345678", Campo("tel:06-1234-5678;phone-context=+39"));

    [Fact]
    public void IlNumeroLocaleConIlContestoDiUnEnteVieneAccettato()
        => Assert.Equal("204", Campo("tel:204;phone-context=ente.example.it"));

    [Fact]
    public void IlNumeroLocaleDiUnAltroPaeseVieneRifiutatoPercheChiamerebbeUnAltro()
    {
        // La RFC dice che il contesto NON e' un prefisso da aggiungere: e' il posto
        // dove il numero vale. Fuori di li', le stesse cifre sono un altro abbonato.
        var errore = Rifiuto("tel:0221234567;phone-context=+41");
        Assert.Contains("altro numero", errore, StringComparison.Ordinal);
    }

    [Fact]
    public void LInternoIndicatoConExtSiDiceMaNonSiCompone()
    {
        Assert.True(CallLinkParser.TryParse("tel:+39-06-1234567;ext=204", out var link, out _));

        Assert.Equal("061234567", link!.Dial(Centralino));
        Assert.Equal("204", link.Extension);
    }

    [Fact]
    public void LeCodificheVengonoDecodificate()
    {
        // Le pagine web e i client di posta codificano spazi e piu'.
        Assert.Equal("0612345678", Campo("tel:06%2012345678"));
        Assert.Equal("0612345678", Campo("tel:%2B39%2006%2012345678"));
    }

    [Fact]
    public void IParametriSconosciutiSiIgnorano()
        => Assert.Equal("061234567", Campo("tel:+39061234567;isub=1234;tgrp=uscita;npdi"));

    [Fact]
    public void LoSchemaNonDipendeDalleMaiuscole()
        => Assert.Equal("061234567", Campo("TEL:061234567"));

    // -----------------------------------------------------------------------
    // tel: come lo scrivono i gestionali
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("tel:0612345678", "0612345678")]
    [InlineData("tel:06 12345678", "0612345678")]
    [InlineData("tel:06/12345678", "0612345678")]
    [InlineData("tel:(06) 1234 5678", "0612345678")]
    [InlineData("tel:06\u00A012345678", "0612345678")]
    [InlineData("tel:06\u20131234\u20135678", "0612345678")]
    [InlineData("tel://0612345678", "0612345678")]
    [InlineData("tel:  0612345678  ", "0612345678")]
    public void ISeparatoriDUsoItalianoNonFannoRifiutareIlNumero(string uri, string atteso)
        => Assert.Equal(atteso, Campo(uri));

    [Fact]
    public void LoZeroFraParentesiDopoUnPrefissoEsteroSiToglie()
    {
        // +44 (0)20 si compone +44 20: lo zero si fa solo dall'interno del paese.
        Assert.Equal("+442079460000", Campo("tel:+44 (0)20 7946 0000"));
    }

    [Fact]
    public void LoZeroFraParentesiDopoIlPrefissoItalianoResta()
    {
        // In Italia lo zero fa parte del numero anche dall'estero.
        Assert.Equal("0612345678", Campo("tel:+39 (0)6 12345678"));
    }

    [Fact]
    public void UnNumeroLocaleSenzaContestoSiPrendePerBuono()
    {
        // La RFC lo vorrebbe con il contesto, nessun gestionale lo mette: rifiutarlo
        // vorrebbe dire rifiutare quasi tutti i link che il centralino incontra.
        Assert.Equal("204", Campo("tel:204"));
    }

    [Fact]
    public void IParametriAggiuntiDalGestionaleDopoIlPuntoInterrogativoSiIgnorano()
        => Assert.Equal("0612345678", Campo("tel:0612345678?pratica=4711"));

    // -----------------------------------------------------------------------
    // callto:
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("callto:0612345678", "0612345678")]
    [InlineData("callto://0612345678", "0612345678")]
    [InlineData("callto://+390612345678/", "0612345678")]
    [InlineData("callto:+39 06 1234 5678", "0612345678")]
    public void CalltoConUnNumeroValeComeTel(string uri, string atteso)
        => Assert.Equal(atteso, Campo(uri));

    [Fact]
    public void CalltoConUnIndirizzoValeComeSip()
        => Assert.Equal("mario.rossi@ente.example.it", Campo("callto:mario.rossi@ente.example.it"));

    [Fact]
    public void CalltoConUnNomeSkypeVieneRifiutato()
        => Assert.Contains("lettere", Rifiuto("callto:mario.rossi.skype"), StringComparison.Ordinal);

    [Fact]
    public void LoSchemaDelLinkVieneConservato()
    {
        Assert.True(CallLinkParser.TryParse("callto:0612345678", out var link, out _));
        Assert.Equal(CallLinkScheme.Callto, link!.Scheme);
    }

    // -----------------------------------------------------------------------
    // sip: e sips: secondo la RFC 3261
    // -----------------------------------------------------------------------

    [Fact]
    public void VersoIlProprioCentralinoRestaIlSoloNumero()
    {
        Assert.Equal("204", Campo("sip:204@centralino.ente.local"));

        // Il dominio non dipende dalle maiuscole, e la porta non conta: la porta su
        // cui il centralino ascolta la sa gia' l'account.
        Assert.Equal("204", Campo("sip:204@Centralino.Ente.Local:5060"));
    }

    [Fact]
    public void VersoUnAltroDominioLIndirizzoRestaIntero()
        => Assert.Equal("mario.rossi@comune.example.it", Campo("sip:mario.rossi@Comune.Example.IT"));

    [Fact]
    public void UnNumeroVersoUnAltroDominioRestaComeIlLinkLoScriveva()
    {
        // Un fornitore esterno puo' volere la forma internazionale: riscriverla
        // sarebbe decidere al posto suo.
        Assert.Equal("+390612345678@gateway.example.net", Campo("sip:+39-06-1234-5678@gateway.example.net;user=phone"));
    }

    [Fact]
    public void UnNumeroVersoIlProprioCentralinoTornaNazionale()
        => Assert.Equal("0612345678", Campo("sip:+390612345678@centralino.ente.local;user=phone"));

    [Fact]
    public void IlPuntoDiUnIndirizzoZoomNonVieneTrattatoComeSeparatore()
    {
        // Per la RFC la parte utente SIP e' opaca. Il gateway di Zoom legge
        // identificativo e codice d'accesso proprio grazie a quel punto: toglierlo
        // manderebbe in una riunione che non esiste.
        Assert.Equal("812345678.4321@zoomcrc.com", Campo("sip:812345678.4321@zoomcrc.com"));
    }

    [Fact]
    public void ITrattiniSonoSeparatoriSoloQuandoLIndirizzoDichiaraUnNumero()
    {
        Assert.Equal("0612345678", Campo("sip:06-1234-5678@centralino.ente.local;user=phone"));
        Assert.Equal("ufficio-tributi", Campo("sip:ufficio-tributi@centralino.ente.local"));
    }

    [Fact]
    public void SipsSiComponeComeSip()
    {
        Assert.True(CallLinkParser.TryParse("sips:204@centralino.ente.local", out var link, out _));

        Assert.Equal(CallLinkScheme.Sips, link!.Scheme);
        Assert.Equal("204", link.Dial(Centralino));
    }

    [Fact]
    public void LaPasswordNellIndirizzoSiScarta()
        => Assert.Equal("204", Campo("sip:204:segreta@centralino.ente.local"));

    [Fact]
    public void UnIndirizzoIPv6VieneAccettato()
        => Assert.Equal("204@[2001:db8::1]:5060", Campo("sip:204@[2001:db8::1]:5060"));

    [Fact]
    public void SipSenzaDominioConUnNumeroEUnInternoDelProprioCentralino()
    {
        // Formalmente «204» sarebbe il nome di un server; in pratica e' come i
        // gestionali scrivono l'interno.
        Assert.Equal("204", Campo("sip:204"));
    }

    [Fact]
    public void SipConIlSoloServerVieneRifiutato()
        => Assert.Contains("server", Rifiuto("sip:centralino.ente.local"), StringComparison.Ordinal);

    [Fact]
    public void SenzaUnAccountConfiguratoLIndirizzoRestaIntero()
        => Assert.Equal("204@centralino.ente.local", Campo("sip:204@centralino.ente.local", dominio: null));

    // -----------------------------------------------------------------------
    // Quello che un link NON deve poter fare
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("tel:*21*3331234567#")]
    [InlineData("tel:%2A21%2A3331234567%23")]
    [InlineData("tel:#31#0612345678")]
    [InlineData("sip:*21*3331234567@centralino.ente.local")]
    [InlineData("callto:*72")]
    public void ICodiciDiServizioDelCentralinoVengonoRifiutati(string uri)
    {
        // La deviazione di tutte le chiamate verso un numero esterno si attiva con un
        // asterisco, qualche cifra e un cancelletto. Una pagina web non ha alcuna
        // ragione legittima per proporne una.
        Assert.Contains("codice di servizio", Rifiuto(uri), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tel:0612345678%0D%0AVia: SIP/2.0/UDP attaccante.example")]
    [InlineData("tel:0612345678%0A")]
    [InlineData("tel:0612345678\r\nVia: x")]
    [InlineData("sip:204%0D%0AContact:%20x@centralino.ente.local")]
    [InlineData("tel:0612%00345678")]
    public void UnACapoNonPuoInventareUnaIntestazione(string uri)
        => Rifiuto(uri);

    [Fact]
    public void LeIntestazioniDelLinkSipSiIgnorano()
    {
        // Replaces si inserisce in una conversazione altrui, body mette contenuto
        // nella richiesta: del link si tengono utente e dominio e nient'altro.
        Assert.Equal("204", Campo("sip:204@centralino.ente.local?Replaces=abc%3Bto-tag%3D1&body=x"));
    }

    [Fact]
    public void IParametriSipCheDeciderebberoIlPercorsoSiIgnorano()
    {
        // maddr sceglierebbe il server a cui mandare la chiamata, transport e method
        // come mandarla: sono decisioni della configurazione dell'account.
        Assert.True(CallLinkParser.TryParse(
            "sip:204@centralino.ente.local;maddr=attaccante.example;transport=udp;method=REGISTER",
            out var link, out _));

        Assert.Equal("centralino.ente.local", link!.Host);
        Assert.Equal("204", link.Dial(Centralino));
    }

    [Theory]
    [InlineData("sip:\"><script>@centralino.ente.local")]
    [InlineData("sip:mario rossi@centralino.ente.local")]
    [InlineData("sip:mario%20rossi@centralino.ente.local")]
    [InlineData("sip:<mario>@centralino.ente.local")]
    [InlineData("sip:mario%40altro@centralino.ente.local")]
    public void UnNomeConCaratteriStraniVieneRifiutato(string uri)
        => Rifiuto(uri);

    [Theory]
    [InlineData("sip:204@")]
    [InlineData("sip:@centralino.ente.local")]
    [InlineData("sip:204@centralino@altro.example")]
    [InlineData("sip:204@-centralino.example")]
    [InlineData("sip:204@centralino..example")]
    [InlineData("sip:204@centralino.ente.local:99999")]
    [InlineData("sip:204@centralino.ente.local:porta")]
    [InlineData("sip:204@[2001:db8::1")]
    [InlineData("sip:204@[non-un-indirizzo]")]
    [InlineData("sip:204@centr%61lino.example")]
    public void UnIndirizzoMalformatoVieneRifiutato(string uri)
        => Rifiuto(uri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tel:")]
    [InlineData("tel:;ext=12")]
    [InlineData("tel:+")]
    [InlineData("tel:---")]
    public void UnLinkVuotoVieneRifiutato(string? uri)
        => Rifiuto(uri);

    [Theory]
    [InlineData("mailto:ufficio@ente.example.it")]
    [InlineData("http://ente.example.it/chiama?n=0612345678")]
    [InlineData("javascript:alert(1)")]
    [InlineData("0612345678")]
    [InlineData("telefono:0612345678")]
    public void CioCheNonELinkDiChiamataVieneRifiutato(string uri)
        => Assert.Contains("non e' un link di chiamata", Rifiuto(uri), StringComparison.Ordinal);

    [Theory]
    [InlineData("tel:javascript:alert(1)")]
    [InlineData("tel:1-800-FLOWERS")]
    [InlineData("tel:12AB;phone-context=+39")]
    public void UnNumeroConLettereVieneRifiutato(string uri)
        => Assert.Contains("lettere", Rifiuto(uri), StringComparison.Ordinal);

    [Theory]
    [InlineData("tel:+0612345678")]
    [InlineData("tel:+39+0612345678")]
    [InlineData("tel:06+12345678")]
    [InlineData("tel:+39")]
    [InlineData("tel:+1234567890123456")]
    [InlineData("tel:123456789012345678901")]
    public void UnNumeroImpossibileVieneRifiutato(string uri)
        => Rifiuto(uri);

    [Theory]
    [InlineData("tel:%")]
    [InlineData("tel:%2")]
    [InlineData("tel:%ZZ0612345678")]
    [InlineData("tel:%FF%FE0612345678")]
    [InlineData("tel:0612345678;ext=%E2%28")]
    public void UnaCodificaMalformataVieneRifiutata(string uri)
        => Assert.Contains("codifica", Rifiuto(uri), StringComparison.Ordinal);

    [Fact]
    public void LaCodificaSiApplicaUnaVoltaSola()
    {
        // «%252B» diventa «%2B» e si ferma li': il segno di percentuale rimasto non
        // e' un piu', e non deve diventarlo a una seconda decodifica.
        Rifiuto("tel:%252B390612345678");
    }

    [Theory]
    [InlineData("tel:\uFF10\uFF16\uFF11\uFF12\uFF13")]
    [InlineData("tel:\u0660\u0666\u0661\u0662")]
    public void LeCifreNonLatineVengonoRifiutate(string uri)
    {
        // char.IsDigit le accetterebbe, ma un centralino no: comporre «٠٦١٢» vuol dire
        // mandare caratteri che nessun piano di numerazione conosce.
        Rifiuto(uri);
    }

    [Fact]
    public void UnInternoNonNumericoVieneRifiutato()
        => Assert.Contains("interno", Rifiuto("tel:061234567;ext=uffici"), StringComparison.Ordinal);

    [Fact]
    public void UnLinkEnormeVieneRifiutatoSenzaEsaminarlo()
    {
        var enorme = "tel:" + new string('1', CallLinkParser.MaxLength * 100);
        Assert.Contains("troppo lungo", Rifiuto(enorme), StringComparison.Ordinal);
    }

    [Fact]
    public void UnNomeUtenteTroppoLungoVieneRifiutato()
        => Rifiuto($"sip:{new string('a', 200)}@centralino.ente.local");

    // -----------------------------------------------------------------------
    // Riconoscimento sulla riga di comando
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("tel:0612345678", true)]
    [InlineData("SIP:204@x", true)]
    [InlineData("sips:204@x", true)]
    [InlineData("callto://0612345678", true)]
    [InlineData("tel:", true)]
    [InlineData("--avvio-silenzioso", false)]
    [InlineData("C:\\Programmi\\BlindPhone\\BlindPhone.App.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SiRiconosceUnLinkSullaRigaDiComando(string? argomento, bool atteso)
    {
        // Anche «tel:» vuoto e' un link: deve arrivare fino al parser, che e' il solo
        // punto che sa spiegare all'operatore che cosa non va.
        Assert.Equal(atteso, CallLinkParser.LooksLikeCallLink(argomento));
    }

    // -----------------------------------------------------------------------
    // Che cosa fa Invio, e che cosa si sente
    // -----------------------------------------------------------------------

    [Fact]
    public void APostazioneLiberaInvioCompone()
        => Assert.Equal(CallLinkEnterEffect.Dial, CallLinkTexts.EffectOf(new CallActionContext(), simplified: false));

    [Fact]
    public void InConversazioneInvioNonComponeENessunoVaInAttesaDaSolo()
    {
        // E' la regola del campo di composizione: comporre non mette in attesa
        // nessuno, quindi con qualcuno in linea Invio non fa niente.
        var inLinea = new CallActionContext(HasActiveCall: true);

        Assert.Equal(CallLinkEnterEffect.HoldFirst, CallLinkTexts.EffectOf(inLinea, simplified: false));
    }

    [Fact]
    public void ConLaChiamataInAttesaInvioCompone()
    {
        var inAttesa = new CallActionContext(HeldCount: 1);

        Assert.Equal(CallLinkEnterEffect.Dial, CallLinkTexts.EffectOf(inAttesa, simplified: false));
    }

    [Fact]
    public void DuranteLInserimentoDellaDestinazioneInvioTrasferisce()
    {
        var trasferimento = new CallActionContext(TransferPhase: TransferPhase.EnteringDestination);

        Assert.Equal(CallLinkEnterEffect.Transfer, CallLinkTexts.EffectOf(trasferimento, simplified: false));
    }

    [Fact]
    public void DuranteLaConsultazioneInvioNonCompone()
    {
        var consultazione = new CallActionContext(HasActiveCall: true, TransferPhase: TransferPhase.Consulting);

        Assert.Equal(CallLinkEnterEffect.Busy, CallLinkTexts.EffectOf(consultazione, simplified: false));
    }

    [Fact]
    public void InModalitaSemplificataConUnChiamanteInvioChiamaPerPassarlo()
    {
        // In modalita' semplificata Invio e' il tasto che chiama l'interno a cui
        // passare il chiamante: il numero del link conta come digitato.
        var inLinea = new CallActionContext(HasActiveCall: true);

        Assert.Equal(CallLinkEnterEffect.Consult, CallLinkTexts.EffectOf(inLinea, simplified: true));
        Assert.Equal(CallLinkEnterEffect.Dial, CallLinkTexts.EffectOf(new CallActionContext(), simplified: true));
    }

    [Fact]
    public void LAnnuncioCominciaDalNumeroLettoCifraPerCifra()
    {
        var testo = CallLinkTexts.Inserted(
            "061234", null, CallLinkEnterEffect.Dial, DigitGrouping.Single, "F4", "F9");

        Assert.Equal("Numero da link: zero, sei, uno, due, tre, quattro. Premi Invio per chiamare", testo);
    }

    [Fact]
    public void LAnnuncioInConversazioneDiceComeProcedere()
    {
        var testo = CallLinkTexts.Inserted(
            "204", null, CallLinkEnterEffect.HoldFirst, DigitGrouping.Single, "F4", "F9");

        Assert.StartsWith("Numero da link: due, zero, quattro", testo, StringComparison.Ordinal);
        Assert.Contains("Invio non compone", testo, StringComparison.Ordinal);
        Assert.Contains("F4", testo, StringComparison.Ordinal);
        Assert.Contains("F9", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LInternoDelLinkVieneDettoDaChiedereDopoLaRisposta()
    {
        var testo = CallLinkTexts.Inserted(
            "061234567", "12", CallLinkEnterEffect.Dial, DigitGrouping.Single, "F4", "F9");

        Assert.Contains("interno uno, due da chiedere dopo la risposta", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void UnIndirizzoSiLeggeConLaPunteggiaturaDettaAParole()
    {
        // Un punto taciuto fa sentire «mario rossi», che e' un altro indirizzo.
        var testo = CallLinkTexts.Inserted(
            "mario.rossi@comune.example.it", null, CallLinkEnterEffect.Dial, DigitGrouping.Single, "F4", "F9");

        Assert.StartsWith(
            "Indirizzo da link: mario punto rossi chiocciola comune punto example punto it",
            testo,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IlCodiceDiUnaRiunioneZoomNonVieneLettoNeMostrato()
    {
        // Il codice d'accesso letto a voce alta in un centralino e' un codice diffuso.
        Assert.True(CallLinkParser.TryParse("sip:812345678.4321@zoomcrc.com", out var link, out _));
        var campo = link!.Dial(Centralino);

        var testo = CallLinkTexts.Inserted(campo, null, CallLinkEnterEffect.Dial, DigitGrouping.Single, "F4", "F9");

        Assert.DoesNotContain("4321", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("quattro, tre, due, uno", testo, StringComparison.Ordinal);
        Assert.Contains("riunione Zoom", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", CallLinkTexts.Braille(campo), StringComparison.Ordinal);
    }

    [Fact]
    public void IlRifiutoDiceCheIlLinkNonEStatoUsatoEPerche()
    {
        Assert.False(CallLinkParser.TryParse("tel:*21*333#", out _, out var errore));

        var testo = CallLinkTexts.Rejected(errore!);

        Assert.StartsWith("Link di chiamata non usato: ", testo, StringComparison.Ordinal);
        Assert.Contains("codice di servizio", testo, StringComparison.Ordinal);
    }
}
