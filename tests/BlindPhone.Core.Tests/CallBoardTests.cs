using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica le due caselle di riepilogo della finestra principale.
/// </summary>
/// <remarks>
/// Sono l'unica parte del prodotto pensata per chi <b>guarda</b> lo schermo, e
/// proprio per questo vanno collaudate qui e non a occhio: chi sviluppa vede il
/// testo comparire e considera l'informazione trasmessa, senza accorgersi che
/// riporta la persona sbagliata o un orologio fermo.
/// </remarks>
public sealed class CallBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.FromHours(2));

    // -----------------------------------------------------------------------
    // Il nome grande: con chi sono collegato
    // -----------------------------------------------------------------------

    [Fact]
    public void SenzaChiamateSiDiceEsplicitamenteCheNonCeNessuno()
    {
        // Una casella vuota e' ambigua: non si distingue da una casella rotta.
        Assert.Equal("Nessuna chiamata", CallBoardTexts.Connected(null));
        Assert.Equal(CallBoardTexts.NoConnectedCall, CallBoardTexts.Connected(null));
    }

    [Fact]
    public void ConUnaChiamataInCorsoSiLeggonoNomeENumero()
    {
        // Il numero resta nella casella e non solo nella riga sotto: la casella esiste
        // perche' quel numero si possa selezionare e copiare.
        var call = Chiamata("4021", "Mario Rossi", CallState.Active);

        Assert.Equal("Mario Rossi - 4021", CallBoardTexts.Connected(call));
    }

    [Fact]
    public void ANumeroSconosciutoRestaIlNumero()
    {
        var call = Chiamata("3491234567", null, CallState.Active);

        Assert.Equal("3491234567", CallBoardTexts.Connected(call));
    }

    [Fact]
    public void LaCasellaNonDiceMaiChiStaSquillando()
    {
        // Si chiama «chiamata in conversazione»: chi ci arriva con il tabulatore deve
        // poter credere a quello che sente. Chi squilla ha una riga sua.
        Assert.Equal(CallBoardTexts.NoConnectedCall, CallBoardTexts.Connected(null, [], outgoing: null));
    }

    [Fact]
    public void LaChiamataUscenteCompareConLaSuaDidascalia()
    {
        // Il nome di chi si sta chiamando sta dove stara' quando rispondera'; la
        // didascalia dice che non ha ancora risposto, e chi passa non legge «in linea».
        var uscente = new Call("u-1", CallDirection.Outbound, new CallParty("4021", "Mario Rossi"), Now);
        uscente.TryApply(CallTrigger.OutgoingCall, Now, out _);

        Assert.Equal("Mario Rossi - 4021", CallBoardTexts.Connected(null, null, uscente));
        Assert.DoesNotContain("In linea", CallBoardTexts.ConnectedCaption(null, null, uscente), StringComparison.Ordinal);
    }

    [Fact]
    public void LaDidascaliaDiceInCheRapportoSiEConChiCeScritto()
    {
        var attiva = Chiamata("4021", "Mario Rossi", CallState.Active);
        var primo = Chiamata("4022", "Anna Bianchi", CallState.Conferenced);
        var secondo = Chiamata("4023", "Luca Verdi", CallState.Conferenced);

        Assert.Equal("In linea con", CallBoardTexts.ConnectedCaption(attiva));
        Assert.Equal("In conferenza a tre con", CallBoardTexts.ConnectedCaption(null, [primo, secondo]));
        Assert.Equal("Chiamata in corso", CallBoardTexts.ConnectedCaption(null));
    }

    [Fact]
    public void SottoIlNomeCompaionoUfficioECodaMaiIlNumero()
    {
        // Il numero sta gia' nella casella sopra: ripeterlo sotto lo farebbe leggere due
        // volte a chi guarda.
        var party = new CallParty("4021", "Mario Rossi") { Department = "Ufficio tecnico" };
        var call = new Call("d-1", CallDirection.Inbound, party, Now);
        call.TryApply(CallTrigger.IncomingCall, Now, out _);
        call.TryApply(CallTrigger.Answer, Now, out _);
        call.AttachQueue("Anagrafe");

        var dettaglio = CallBoardTexts.ConnectedDetail(call);

        Assert.Equal("Ufficio tecnico · coda Anagrafe", dettaglio);
        Assert.DoesNotContain("4021", dettaglio, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaUfficioNeCodaLaRigaSottoIlNomeEVuota()
    {
        var call = Chiamata("4021", "Mario Rossi", CallState.Active);

        Assert.Equal(string.Empty, CallBoardTexts.ConnectedDetail(call));
        Assert.Equal(string.Empty, CallBoardTexts.ConnectedDetail(null));
    }

    [Fact]
    public void AschermoCompareIlNomeAnagraficoENonLaResaFonetica()
    {
        // "Cekerini" serve alla sintesi vocale. A schermo sarebbe un errore di
        // ortografia, e l'operatore lo riporterebbe sbagliato su un modulo.
        var party = new CallParty("4021", "Ceccherini", "Cekerini");
        var call = new Call("c-1", CallDirection.Inbound, party, Now);

        Assert.Equal("Ceccherini - 4021", CallBoardTexts.Identity(call.RemoteParty));
    }

    [Fact]
    public void InConferenzaSiLeggonoEntrambiIPartecipanti()
    {
        // In conferenza la chiamata attiva non esiste: guardando solo quella la
        // casella direbbe "nessuna chiamata" con due persone in linea.
        var primo = Chiamata("4021", "Mario Rossi", CallState.Conferenced);
        var secondo = Chiamata("4022", "Anna Bianchi", CallState.Conferenced);

        var text = CallBoardTexts.Connected(null, [primo, secondo]);

        Assert.Equal("Mario Rossi - 4021 e Anna Bianchi - 4022", text);
        Assert.Contains("conferenza a tre", CallBoardTexts.ConnectedCaption(null, [primo, secondo]), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Casella di destra: chi sta aspettando, e da quanto
    // -----------------------------------------------------------------------

    [Fact]
    public void SenzaAtteseSiDiceEsplicitamenteCheNonCeNessuno()
    {
        Assert.Equal("In attesa: nessuna chiamata", CallBoardTexts.OnHold([], Now));
    }

    [Fact]
    public void UnaChiamataInAttesaMostraNomeNumeroEDurata()
    {
        var call = Chiamata("4021", "Mario Rossi", CallState.Held, heldFor: TimeSpan.FromSeconds(83));

        Assert.Equal("In attesa: Mario Rossi - 4021 - 0:01:23", CallBoardTexts.OnHold([call], Now));
    }

    [Fact]
    public void ChiAspettaDaPiuTempoCompareperPrimo()
    {
        // E' quello che si rischia di dimenticare, quindi e' quello che deve stare
        // dove l'occhio cade per primo.
        var recente = Chiamata("4021", "Recente", CallState.Held, heldFor: TimeSpan.FromSeconds(10));
        var vecchia = Chiamata("4022", "Dimenticata", CallState.Held, heldFor: TimeSpan.FromMinutes(9));

        var text = CallBoardTexts.OnHold([recente, vecchia], Now);

        Assert.StartsWith("In attesa: Dimenticata", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LeAltreAtteseSiContanoInvecheDiEssereElencate()
    {
        // Un'attesa per esteso per ogni chiamata allungherebbe la scheda a ogni messa
        // in attesa, e spingerebbe giu' la coda proprio mentre si cerca a chi passare
        // qualcuno. Il conteggio ci sta sempre e dice che esistono.
        var prima = Chiamata("4021", "Mario Rossi", CallState.Held, heldFor: TimeSpan.FromSeconds(30));
        var seconda = Chiamata("4022", "Anna Bianchi", CallState.Held, heldFor: TimeSpan.FromSeconds(20));

        var text = CallBoardTexts.OnHold([prima, seconda], Now);

        Assert.Equal("In attesa: Mario Rossi - 4021 - 0:00:30   (+1 in attesa)", text);
    }

    [Fact]
    public void ConUnaSolaAttesaNonCompareNessunConteggio()
    {
        var sola = Chiamata("4021", "Mario Rossi", CallState.Held, heldFor: TimeSpan.FromSeconds(30));

        Assert.DoesNotContain("+", CallBoardTexts.OnHold([sola], Now), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // L'orologio dell'attesa
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(9, "0:00:09")]
    [InlineData(83, "0:01:23")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3723, "1:02:03")]
    public void LaDurataSiScriveComeUnOrologio(int seconds, string expected)
    {
        Assert.Equal(expected, CallBoardTexts.ClockDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void OltreLeVentiquattroOreLOrologioNonRicominciaDaZero()
    {
        // La formattazione standard di TimeSpan qui scriverebbe "1:01:00:00" oppure
        // ripartirebbe da 1:00:00: una chiamata dimenticata in attesa dalla sera
        // prima deve mostrare un tempo palesemente anomalo.
        Assert.Equal("25:00:00", CallBoardTexts.ClockDuration(TimeSpan.FromHours(25)));
    }

    [Fact]
    public void UnoScartoDiOrologioNonProduceMaiUnTempoNegativo()
    {
        Assert.Equal("0:00:00", CallBoardTexts.ClockDuration(TimeSpan.FromSeconds(-4)));
    }

    // -----------------------------------------------------------------------
    // Cronometro dello squillo
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(7, "0:07")]
    [InlineData(30, "0:30")]
    [InlineData(65, "1:05")]
    [InlineData(600, "10:00")]
    public void IlCronometroDelloSquilloUsaMinutiESecondi(int secondi, string atteso)
    {
        // Due gruppi di cifre e non tre: uno squillo non supera quasi mai il minuto, e
        // nel riquadro lo spazio serve al nome di chi chiama.
        Assert.Equal(atteso, CallBoardTexts.ShortDuration(TimeSpan.FromSeconds(secondi)));
    }

    [Theory]
    [InlineData(3, "Squilla da 3 secondi")]
    [InlineData(1, "Squilla da 1 secondo")]
    [InlineData(65, "Squilla da 1 minuto e 5")]
    public void LaDurataDelloSquilloSiPronunciaColVerboDavanti(int secondi, string atteso)
    {
        // Il verbo dice di cosa si parla: chiedendo la durata con una conversazione in
        // corso e una chiamata che squilla si sentono due tempi di fila, e senza le
        // parole non si saprebbe quale e' quale.
        Assert.Equal(atteso, AnnouncementTexts.RingingFor(TimeSpan.FromSeconds(secondi)));
    }

    [Theory]
    [InlineData(45, "In linea da 45 secondi")]
    [InlineData(130, "In linea da 2 minuti e 10")]
    public void LaDurataDellaConversazioneSiPronunciaColVerboDavanti(int secondi, string atteso)
    {
        // Prima era il solo numero. Con lo squillo che ha la precedenza si possono
        // sentire due tempi di fila, e senza le parole non si saprebbe quale e' quale.
        Assert.Equal(atteso, AnnouncementTexts.InCallFor(TimeSpan.FromSeconds(secondi)));
    }

    [Fact]
    public void IDueTempiSiDistinguonoDalleParoleENonDallOrdine()
    {
        // La prova che il difetto originale non puo' tornare: le due frasi non
        // cominciano nello stesso modo.
        var linea = AnnouncementTexts.InCallFor(TimeSpan.FromSeconds(60));
        var squillo = AnnouncementTexts.RingingFor(TimeSpan.FromSeconds(60));

        Assert.NotEqual(linea, squillo);
        Assert.StartsWith("In linea da", linea, StringComparison.Ordinal);
        Assert.StartsWith("Squilla da", squillo, StringComparison.Ordinal);
    }

    [Fact]
    public void IlCronometroDelloSquilloNonVaMaiIndietro()
    {
        // Uno scarto di orologio non deve produrre "meno un secondo" dentro un
        // riquadro lampeggiante: e' il valore che fa dubitare di tutto il resto.
        Assert.Equal("0:00", CallBoardTexts.ShortDuration(TimeSpan.FromSeconds(-3)));
    }

    // -----------------------------------------------------------------------
    // Avviso di chiamata entrante
    // -----------------------------------------------------------------------

    [Fact]
    public void ChiEInRubricaCompareConNomeENumeroFraParentesi()
    {
        var party = new CallParty("4021", "Mario Rossi") { IsKnownContact = true };

        Assert.Equal("Chiamata da: Mario Rossi (4021)", CallBoardTexts.IncomingCall(party));
    }

    [Fact]
    public void ChiNonEInRubricaCompareConIlSoloNumero()
    {
        Assert.Equal(
            "Chiamata da: 3491234567",
            CallBoardTexts.IncomingCall(new CallParty("3491234567")));
    }

    /// <summary>
    /// Il numero compare anche accanto a un nome che non viene dalla rubrica.
    /// </summary>
    /// <remarks>
    /// Stessa correzione dell'annuncio parlato, e stessa ragione: il centralino
    /// dell'ente etichetta quasi tutte le chiamate esterne come «Chiamate esterne»,
    /// e il riquadro diceva «Chiamata da: Chiamate esterne» senza mai mostrare il
    /// numero. Il nome lo sceglie chi chiama, il numero lo porta la rete: nascondere
    /// il secondo per prudenza sul primo toglieva il solo dato utilizzabile.
    /// </remarks>
    [Fact]
    public void UnNomeDichiaratoDallaReteCompareInsiemeAlNumero()
    {
        var party = new CallParty("3491234567", "AGENZIA VIAGGI");

        Assert.Equal("Chiamata da: AGENZIA VIAGGI (3491234567)", CallBoardTexts.IncomingCall(party));
    }

    [Fact]
    public void IlNomeDiComodoDelCentralinoNonNascondeIlNumero()
    {
        // Il caso vero, preso dal registro del 10 agosto 2026.
        var party = new CallParty("0521393111", "Chiamate esterne");

        Assert.Equal("Chiamata da: Chiamate esterne (0521393111)", CallBoardTexts.IncomingCall(party));
    }

    [Fact]
    public void SenzaNumeroESenzaNomeLAvvisoLoDice()
    {
        // Numero riservato: il riquadro deve comunque dire qualcosa, altrimenti
        // sembra un difetto del programma.
        Assert.Equal("Chiamata da: numero sconosciuto", CallBoardTexts.IncomingCall(CallParty.Unknown));
    }

    [Fact]
    public void LaCodaDiProvenienzaCompareNellAvviso()
    {
        var party = new CallParty("4021", "Mario Rossi") { IsKnownContact = true };

        Assert.Equal(
            "Chiamata da: Mario Rossi (4021)  -  coda Assistenza",
            CallBoardTexts.IncomingCall(party, "Assistenza"));
    }

    [Fact]
    public void NellAvvisoCompareIlNomeAnagraficoENonLaResaFonetica()
    {
        var party = new CallParty("4021", "Ceccherini", "Cekerini") { IsKnownContact = true };

        Assert.Equal("Chiamata da: Ceccherini (4021)", CallBoardTexts.IncomingCall(party));
    }

    // -----------------------------------------------------------------------
    // Il cronometro della fascia di stato
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(222, "3:42")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3727, "1:02:07")]
    public void IlCronometroDellaFasciaMostraLeOreSoloQuandoCiSono(int secondi, string atteso)
        => Assert.Equal(atteso, CallBoardTexts.BandDuration(TimeSpan.FromSeconds(secondi)));

    [Fact]
    public void IlCronometroDellaFasciaNonVaMaiSottoZero()
    {
        // Uno scarto di orologio fra la creazione della chiamata e adesso non deve
        // produrre un tempo negativo, che farebbe dubitare di tutta la fascia.
        Assert.Equal("0:00", CallBoardTexts.BandDuration(TimeSpan.FromSeconds(-3)));
    }

    /// <summary>Costruisce una chiamata già portata nello stato richiesto.</summary>
    private static Call Chiamata(
        string number,
        string? name,
        CallState state,
        TimeSpan? heldFor = null)
    {
        var call = new Call($"c-{number}", CallDirection.Inbound, new CallParty(number, name), Now);

        call.TryApply(CallTrigger.IncomingCall, Now, out _);
        call.TryApply(CallTrigger.Answer, Now, out _);

        switch (state)
        {
            case CallState.Held:
                // L'istante della messa in attesa si arretra invece di aspettare: e'
                // cio' che rende la prova deterministica.
                call.TryApply(CallTrigger.Hold, Now - (heldFor ?? TimeSpan.Zero), out _);
                break;

            case CallState.Conferenced:
                call.TryApply(CallTrigger.ConferenceJoined, Now, out _);
                break;
        }

        Assert.Equal(state, call.State);
        return call;
    }
}
