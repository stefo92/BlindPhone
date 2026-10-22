using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Il numero scritto al posto del nome non deve comparire due volte.
/// </summary>
/// <remarks>
/// <para>
/// Il difetto segnalato: in circostanze che l'operatore non sapeva descrivere, il
/// riquadro della chiamata diceva «Chiamata da: 3491234567 (3491234567)». La
/// circostanza è il nome. Il centralino può scrivere il numero anche al posto del
/// nome, e una voce di rubrica senza nominativo prestava il proprio: da quando il
/// numero compare accanto a qualunque nome — la correzione di «Chiamate esterne» —
/// quel nome finiva accanto a sé stesso, e l'annuncio lo leggeva come numero intero.
/// </para>
/// <para>
/// Le prove seguono i casi con cui il difetto è stato riprodotto, e tengono fermi i
/// due controlli che la correzione non deve toccare: l'etichetta del centralino e il
/// nome vero restano accanto al numero.
/// </para>
/// </remarks>
public sealed class NumeroAlPostoDelNomeTests
{
    private static readonly DateTimeOffset Adesso = new(2026, 9, 15, 10, 0, 0, TimeSpan.FromHours(2));

    // -----------------------------------------------------------------------
    // La regola: quando un nome è soltanto il numero
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("3491234567", "3491234567")]
    [InlineData("349 123 4567", "3491234567")]
    [InlineData("349-123.4567", "3491234567")]
    [InlineData("+39 349 1234567", "3491234567")]
    [InlineData("0039 349 1234567", "3491234567")]
    [InlineData("3491234567", "+393491234567")]
    [InlineData("+393491234567", "00393491234567")]
    [InlineData("+44 20 7946 0958", "00442079460958")]
    [InlineData("4021", "4021")]
    [InlineData("40 21", "4021")]
    public void UnNomeCheEIlNumeroStessoNonEUnNome(string nome, string numero)
    {
        Assert.Null(CallParty.MeaningfulNameOf(nome, numero));
    }

    [Theory]
    [InlineData("Chiamate esterne", "0521393111")]
    [InlineData("Mario Rossi", "4021")]
    [InlineData("4021", "3491234567")]
    public void UnNomeCheAggiungeQualcosaResta(string nome, string numero)
    {
        // Anche un nome fatto di cifre, se sono un altro numero: l'interno da cui la
        // chiamata e' stata inoltrata, per esempio, e' un'informazione.
        Assert.Equal(nome, CallParty.MeaningfulNameOf(nome, numero));
    }

    [Fact]
    public void IlTrentanoveDiUnCellulareNonEUnPrefisso()
    {
        // Il 39 si toglie solo dopo un + o un 00. Un cellulare italiano puo' cominciare
        // per 39 da se': toglierlo sempre renderebbe «3931234567» uguale a «31234567».
        Assert.Equal("3931234567", CallParty.MeaningfulNameOf("3931234567", "31234567"));
        Assert.Null(CallParty.MeaningfulNameOf("+39 393 1234567", "3931234567"));
    }

    [Fact]
    public void UnNumeroDentroUnEtichettaPerOraResta()
    {
        // Deliberato, finche' non si e' visto un caso vero: togliere il numero da
        // un'etichetta vorrebbe dire indovinare come il centralino la compone.
        Assert.Equal(
            "Chiamate esterne 3491234567",
            CallParty.MeaningfulNameOf("Chiamate esterne 3491234567", "3491234567"));
    }

    [Fact]
    public void UnNomeVuotoNonEUnNome()
    {
        Assert.Null(CallParty.MeaningfulNameOf("   ", "4021"));
        Assert.Null(CallParty.MeaningfulNameOf(null, "4021"));
    }

    [Fact]
    public void SenzaNumeroUnNomeFattoDiCifreResta()
    {
        // Senza un numero con cui confrontarlo non e' "il numero scritto due volte":
        // e' l'unico dato che c'e', e toglierlo lascerebbe «numero sconosciuto».
        Assert.Equal("4021", CallParty.MeaningfulNameOf("4021", string.Empty));
    }

    // -----------------------------------------------------------------------
    // Quello che si vede e si sente
    // -----------------------------------------------------------------------

    [Fact]
    public void IlRiquadroDellaChiamataNonRipeteIlNumeroFraParentesi()
    {
        var party = new CallParty("3491234567", "3491234567");

        Assert.Equal("Chiamata da: 3491234567", CallBoardTexts.IncomingCall(party));
    }

    [Fact]
    public void LoStessoNumeroInFormaInternazionaleNonSiRipete()
    {
        var party = new CallParty("3491234567", "+39 349 1234567");

        Assert.Equal("Chiamata da: 3491234567", CallBoardTexts.IncomingCall(party));
    }

    [Fact]
    public void IlRiquadroVerdeELaCasellaNonRipetonoIlNumero()
    {
        var chiamata = new Call(
            "doppio-1", CallDirection.Inbound, new CallParty("3491234567", "3491234567"), Adesso);

        Assert.Equal("In linea con: 3491234567", CallBoardTexts.InCall(chiamata));
        Assert.Equal("Collegato a: 3491234567", CallBoardTexts.Connected(chiamata));
    }

    [Fact]
    public void LInternoSenzaNomeCompareUnaVolta()
    {
        var party = new CallParty("4021", "4021", IsInternal: true);

        Assert.Equal("Chiamata da: 4021", CallBoardTexts.IncomingCall(party));
        Assert.Equal("4021", CallBoardTexts.Identity(party));
    }

    [Fact]
    public void LAnnuncioLeggeIlNumeroUnaVoltaSolaECifraPerCifra()
    {
        // Prima: «Chiamata da 3491234567, numero tre, quattro, nove...», con la prima
        // meta' passata alla sintesi come numero intero.
        var party = new CallParty("3491234567", "3491234567");

        Assert.False(party.HasName);
        Assert.Equal(
            "Chiamata da tre, quattro, nove, uno, due, tre, quattro, cinque, sei, sette",
            AnnouncementTexts.IncomingCall(party, null, DigitGrouping.Single));
    }

    [Fact]
    public void LaSintesiUsaIlNumeroVeroENonQuelloScrittoComeNome()
    {
        Assert.Equal("3491234567", new CallParty("3491234567", "+39 349 1234567").SpeechName);
    }

    [Fact]
    public void IlChiamanteInCodaNonVieneLettoComeNumeroIntero()
    {
        const string atteso = "tre, quattro, nove, uno, due, tre, quattro, cinque, sei, sette";

        Assert.Equal(atteso, SpeechFormatter.Party("3491234567", "3491234567", DigitGrouping.Single));
        Assert.Equal(
            atteso,
            SpeechFormatter.Party("3491234567", "3491234567", DigitGrouping.Single, includeNumberWithName: true));
    }

    // -----------------------------------------------------------------------
    // I controlli: quello che deve restare com'era
    // -----------------------------------------------------------------------

    [Fact]
    public void LEtichettaDelCentralinoRestaAccantoAlNumero()
    {
        var party = new CallParty("0521393111", "Chiamate esterne");

        Assert.Equal("Chiamata da: Chiamate esterne (0521393111)", CallBoardTexts.IncomingCall(party));
    }

    [Fact]
    public void UnNomeVeroRestaAccantoAlNumero()
    {
        var party = new CallParty("4021", "Mario Rossi") { IsKnownContact = true, Extension = "4021" };

        Assert.Equal("Chiamata da: Mario Rossi (4021)", CallBoardTexts.IncomingCall(party));
        Assert.Equal("Mario Rossi - 4021", CallBoardTexts.Identity(party));
    }

    // -----------------------------------------------------------------------
    // La rubrica
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaVoceSenzaNomeHaIlNumeroNegliElenchiMaNonComeNome()
    {
        // Negli elenchi la riga deve leggersi sempre, e il numero al posto del nome e'
        // giusto; come nome di una chiamata no, perche' il numero c'e' gia'.
        var voce = new Contact
        {
            Id = Guid.NewGuid(),
            Numbers = [new PhoneNumber("4021", PhoneNumberKind.Extension)],
        };

        Assert.Null(voce.NameOrDescription);
        Assert.Equal("4021", voce.EffectiveDisplayName);
    }

    [Fact]
    public void LaDescrizioneDiUnaVoceSenzaPersonaEUnNome()
    {
        var voce = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "Ambulatorio",
            Numbers = [new PhoneNumber("2033", PhoneNumberKind.Extension)],
        };

        Assert.Equal("Ambulatorio", voce.NameOrDescription);
    }

    [Fact]
    public async Task IlNumeroMandatoComeNomeNonNascondeIlNominativoDiRubrica()
    {
        // Prima il nome della segnalazione aveva sempre la precedenza, anche quando era
        // il numero: il chiamante in rubrica compariva come «4021 (4021)».
        var rubrica = new FakeContactRepository().Add("Mario Rossi", ("4021", PhoneNumberKind.Extension));
        var (engine, calls, announcer) = Avvia(rubrica);

        var id = engine.SimulateIncoming("4021", "4021");
        await Task.Delay(50);

        var chiamata = calls.GetCall(id);
        Assert.NotNull(chiamata);
        Assert.Equal("Mario Rossi", chiamata.RemoteParty.DisplayName);
        Assert.Equal("Chiamata da: Mario Rossi (4021)", CallBoardTexts.IncomingCall(chiamata.RemoteParty));
        Assert.True(announcer.Said("Mario Rossi"));
    }

    [Fact]
    public async Task UnaVoceDiRubricaSenzaNomeNonPrestaIlProprioNumero()
    {
        var rubrica = new FakeContactRepository().Add(string.Empty, ("4021", PhoneNumberKind.Extension));
        var (engine, calls, _) = Avvia(rubrica);

        var id = engine.SimulateIncoming("4021");
        await Task.Delay(50);

        var chiamata = calls.GetCall(id);
        Assert.NotNull(chiamata);
        Assert.True(chiamata.RemoteParty.IsKnownContact);
        Assert.Null(chiamata.RemoteParty.DisplayName);
        Assert.Equal("Chiamata da: 4021", CallBoardTexts.IncomingCall(chiamata.RemoteParty));
    }

    [Fact]
    public async Task UnaChiamataVersoUnaVoceSenzaNomeNonRipeteIlNumero()
    {
        var rubrica = new FakeContactRepository().Add(string.Empty, ("4021", PhoneNumberKind.Extension));
        var (_, calls, _) = Avvia(rubrica);

        var id = await calls.DialAsync("4021");

        var chiamata = calls.GetCall(id);
        Assert.NotNull(chiamata);
        Assert.Null(chiamata.RemoteParty.DisplayName);
        Assert.Equal("In linea con: 4021", CallBoardTexts.InCall(chiamata));
    }

    [Fact]
    public async Task LaLineaDiConsultazioneVersoUnaVoceSenzaNomeNonRipeteIlNumero()
    {
        // Durante un trasferimento la linea con l'interno occupa il riquadro verde, e la
        // sua controparte viene dalla rubrica del trasferimento: con una voce senza nome
        // diceva «In linea con: 4021 (4021)».
        var clock = new ManualClock(Adesso);
        var engine = new FakeEngine();
        var announcer = new RecordingAnnouncer();
        var rubrica = new FakeContactRepository().AddExtension(string.Empty, "4021");

        var calls = new CallSessionManager(
            engine, announcer, new CallAnnouncementMapper(), new AnnouncementContext(),
            contacts: null, options: null, clock: clock);

        var transfer = new TransferOrchestrator(
            engine, calls, announcer, contacts: rubrica,
            options: new TransferOptions { ConfirmExternalDestinations = false }, clock: clock);

        engine.SimulateIncoming("0212345678", "Mario Rossi");
        await Task.Delay(20);
        await calls.AnswerAsync();

        await transfer.HandleTransferKeyAsync();
        await transfer.SetDestinationAsync("4021");
        await transfer.AttendedTransferNowAsync("4021");
        await Task.Delay(50);

        var composto = Assert.Single(engine.Operations, o => o.StartsWith("dial:4021:", StringComparison.Ordinal));
        var consultazione = calls.GetCall(composto["dial:4021:".Length..]);

        Assert.NotNull(consultazione);
        Assert.Null(consultazione.RemoteParty.DisplayName);
        Assert.Equal("In linea con: 4021", CallBoardTexts.InCall(consultazione));
    }

    /// <summary>
    /// Monta il percorso vero: motore telefonico, gestore delle chiamate e rubrica.
    /// </summary>
    private static (FakeEngine Engine, CallSessionManager Calls, RecordingAnnouncer Announcer) Avvia(
        IContactRepository rubrica)
    {
        var engine = new FakeEngine();
        var announcer = new RecordingAnnouncer();

        var calls = new CallSessionManager(
            engine,
            announcer,
            new CallAnnouncementMapper(),
            new AnnouncementContext(),
            rubrica);

        return (engine, calls, announcer);
    }
}
