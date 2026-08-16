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
    // Casella di sinistra: con chi sono collegato
    // -----------------------------------------------------------------------

    [Fact]
    public void SenzaChiamateSiDiceEsplicitamenteCheNonCeNessuno()
    {
        // Una casella vuota e' ambigua: non si distingue da una casella rotta.
        Assert.Equal("Collegato a: nessuna chiamata", CallBoardTexts.Connected(null));
    }

    [Fact]
    public void ConUnaChiamataInCorsoSiLeggonoNomeENumero()
    {
        var call = Chiamata("4021", "Mario Rossi", CallState.Active);

        Assert.Equal("Collegato a: Mario Rossi - 4021", CallBoardTexts.Connected(call));
    }

    [Fact]
    public void ANumeroSconosciutoRestaIlNumero()
    {
        var call = Chiamata("3491234567", null, CallState.Active);

        Assert.Equal("Collegato a: 3491234567", CallBoardTexts.Connected(call));
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

        Assert.Contains("Mario Rossi", text, StringComparison.Ordinal);
        Assert.Contains("Anna Bianchi", text, StringComparison.Ordinal);
        Assert.Contains("conferenza a tre", text, StringComparison.Ordinal);
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
        // La casella sta su una riga sola e occupa mezza finestra: due attese per
        // esteso verrebbero tagliate a meta' di un nome, cioe' nel punto in cui chi
        // guarda non puo' accorgersi che manca qualcosa. Il conteggio ci sta sempre.
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
    // Avviso di conversazione in corso
    // -----------------------------------------------------------------------

    [Fact]
    public void InConversazioneLAvvisoDiceConChiSiParla()
    {
        var call = ChiamataInRubrica("4021", "Mario Rossi");

        Assert.Equal("In linea con: Mario Rossi (4021)", CallBoardTexts.InCall(call));
    }

    [Fact]
    public void ChiNonEInRubricaCompareConIlSoloNumeroAncheInLinea()
    {
        // Stessa regola dell'avviso di chiamata entrante: il numero accanto al nome
        // vale solo per chi la rubrica conferma.
        var call = Chiamata("3491234567", null, CallState.Active);

        Assert.Equal("In linea con: 3491234567", CallBoardTexts.InCall(call));
    }

    [Fact]
    public void SenzaConversazioneLAvvisoNonHaTesto()
    {
        // La stringa vuota e' cio' che spegne il riquadro: un testo di ripiego lo
        // lascerebbe acceso a dire che si e' in linea con nessuno.
        Assert.Equal(string.Empty, CallBoardTexts.InCall(null));
    }

    [Fact]
    public void InConferenzaLAvvisoNominaEntrambi()
    {
        // In conferenza la chiamata attiva non esiste: guardare solo quella
        // spegnerebbe il riquadro nel momento di massimo impegno della postazione.
        var primo = Chiamata("4021", "Mario Rossi", CallState.Conferenced);
        var secondo = Chiamata("4022", "Anna Bianchi", CallState.Conferenced);

        var testo = CallBoardTexts.InCall(null, [primo, secondo]);

        Assert.Contains("Mario Rossi", testo, StringComparison.Ordinal);
        Assert.Contains("Anna Bianchi", testo, StringComparison.Ordinal);
        Assert.Contains("conferenza", testo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LAvvisoUsaLaStessaFormaDiQuelloDiChiamataEntrante()
    {
        // I due riquadri si susseguono sullo stesso punto dello schermo: cambiare
        // disposizione a meta' costringerebbe a rileggere invece di riconoscere.
        var party = new CallParty("4021", "Mario Rossi") { IsKnownContact = true };
        var call = new Call("c-1", CallDirection.Inbound, party, Now);
        call.TryApply(CallTrigger.IncomingCall, Now, out _);
        call.TryApply(CallTrigger.Answer, Now, out _);

        var entrante = CallBoardTexts.IncomingCall(party);
        var inLinea = CallBoardTexts.InCall(call);

        Assert.Equal("Chiamata da: Mario Rossi (4021)", entrante);
        Assert.Equal("In linea con: Mario Rossi (4021)", inLinea);
    }

    /// <summary>Chiamata in conversazione con una controparte ritrovata in rubrica.</summary>
    private static Call ChiamataInRubrica(string number, string name)
    {
        var party = new CallParty(number, name) { IsKnownContact = true };
        var call = new Call($"c-{number}", CallDirection.Inbound, party, Now);

        call.TryApply(CallTrigger.IncomingCall, Now, out _);
        call.TryApply(CallTrigger.Answer, Now, out _);

        return call;
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
