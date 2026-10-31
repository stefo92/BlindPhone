using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica il quadro dei colleghi in turno: chi c'è, chi non c'è, e quando non si può
/// saperlo.
/// </summary>
/// <remarks>
/// <para>
/// <b>La regola che questi test difendono è una sola</b>, ed è la stessa che vale per lo
/// stato degli interni: un'informazione sbagliata è peggio di un'informazione mancante,
/// perché ci si agisce sopra. Un elenco che dichiara in turno un collega con il telefono
/// spento manda il chiamante a squillare a una scrivania vuota, e chi lo ha mandato non
/// ha modo di accorgersene.
/// </para>
/// <para>
/// Da qui il caso più importante del file: il centralino che risponde «raggiungibile» a
/// qualunque indirizzo, compreso uno inesistente. Non è un'ipotesi di scuola — un
/// controllore di bordo davanti al PBX può rispondere per sé invece che per l'interno —
/// e senza riconoscerlo la tabella si riempirebbe di presenze inventate.
/// </para>
/// </remarks>
public sealed class ShiftBoardTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 9, 0, 0, TimeSpan.FromHours(2));

    /// <summary>Sondaggio finto: risponde quello che la prova gli dice di rispondere.</summary>
    private sealed class SondaFinta : IReachabilityProbe
    {
        private readonly Dictionary<string, ReachabilityOutcome> _risposte = new(StringComparer.Ordinal);

        public bool IsAvailable { get; set; } = true;

        public string Domain => "esempio.it";

        /// <summary>Cosa rispondere a un indirizzo che non corrisponde a nessun interno.</summary>
        public ReachabilityOutcome ControlAnswer { get; set; } = ReachabilityOutcome.NotConfigured;

        /// <summary>Bersagli interrogati, nell'ordine in cui sono stati chiesti.</summary>
        public List<string> Chiesti { get; } = [];

        public void Rispondi(string extension, ReachabilityOutcome outcome) => _risposte[extension] = outcome;

        public Task<ReachabilityOutcome> ProbeAsync(
            string target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Chiesti.Add(target);

            // Il bersaglio di controllo si riconosce dalle lettere: nessun interno ne ha.
            if (target.StartsWith("bp-verifica-", StringComparison.Ordinal))
            {
                return Task.FromResult(ControlAnswer);
            }

            return Task.FromResult(
                _risposte.TryGetValue(target, out var esito) ? esito : ReachabilityOutcome.Unknown);
        }
    }

    private static ShiftBoardService Crea(SondaFinta sonda, IContactRepository? rubrica = null)
        => new(() => sonda, presence: null, rubrica, new ManualClock(Start));

    // -----------------------------------------------------------------------
    // L'elenco che scrive l'operatore
    // -----------------------------------------------------------------------

    [Fact]
    public void LElencoAccettaLeTreFormeCheUnaPersonaScriveDaSe()
    {
        // Sono le tre che vengono spontaneamente, e nessuna e' piu' giusta delle altre:
        // dichiarare un separatore significherebbe che due operatori su tre lo
        // scrivono diverso e si ritrovano meta' elenco scartato.
        var elenco = ColleagueRoster.Parse(
            """
            4021 Mario Rossi
            4022 - Anna Bianchi
            4023, Ufficio Tecnico
            4024: Protocollo
            4025
            """);

        Assert.Equal(5, elenco.Count);
        Assert.Equal("Mario Rossi", elenco[0].Name);
        Assert.Equal("Anna Bianchi", elenco[1].Name);
        Assert.Equal("Ufficio Tecnico", elenco[2].Name);
        Assert.Equal("Protocollo", elenco[3].Name);

        // Senza nominativo si lascia null: vuol dire «cercalo in rubrica», che e' una
        // cosa diversa da «non ne ha uno».
        Assert.Null(elenco[4].Name);
    }

    [Fact]
    public void LeRigheCommentateRestanoNelFileMaNonNellElenco()
    {
        // Serve a mettere da parte un collega senza cancellarlo: chi e' in ferie torna,
        // e riscrivere la riga a memoria e' come non averla mai avuta.
        var elenco = ColleagueRoster.Parse(
            """
            4021 Mario Rossi
            # 4022 Anna Bianchi, in ferie fino al 15
            4023 Ufficio Tecnico
            """);

        Assert.Equal(2, elenco.Count);
        Assert.DoesNotContain(elenco, e => e.Key == "4022");
    }

    [Fact]
    public void UnInternoScrittoDueVolteCompareUnaVolta()
    {
        // "40 21" e "4021" sono lo stesso interno. Se la lettura si fermasse al primo
        // gruppo di cifre diventerebbero due interni diversi, e si finirebbe a
        // sorvegliare il 40 al posto del 4021 senza che niente lo segnali.
        var elenco = ColleagueRoster.Parse("4021 Mario Rossi\n40 21 Mario R.\n4022");

        Assert.Equal(2, elenco.Count);
        Assert.Equal("Mario Rossi", elenco[0].Name);
        Assert.Equal("4022", elenco[1].Number);
    }

    [Fact]
    public void UnNumeroScrittoAGruppiRestaUnNumeroSolo()
    {
        // Come si scrive un numero lungo su un foglio, e come lo si detta al telefono.
        var elenco = ColleagueRoster.Parse("0212 345 678 Centralino sede");

        Assert.Single(elenco);
        Assert.Equal("0212345678", elenco[0].Number);
        Assert.Equal("Centralino sede", elenco[0].Name);
    }

    [Fact]
    public void LElencoNonSuperaIlTetto()
    {
        // Oltre non si sta piu' guardando una squadra: si sta interrogando
        // l'organigramma, e ogni interno costa un sondaggio per giro.
        var righe = string.Join('\n', Enumerable.Range(1000, 200));
        var elenco = ColleagueRoster.Parse(righe);

        Assert.Equal(ColleagueRoster.MaxEntries, elenco.Count);
    }

    [Fact]
    public void LeRigheSenzaCifreSiScartanoInSilenzio()
    {
        // Intestazioni, note, resti di un incollaggio. Segnalarle costringerebbe a
        // correggere un elenco che per il resto funziona.
        var elenco = ColleagueRoster.Parse("Ufficio anagrafe\n4021 Mario Rossi\n---\n");

        Assert.Single(elenco);
    }

    [Fact]
    public void LElencoRiscrittoSiRilegge()
    {
        var originale = ColleagueRoster.Parse("4021 Mario Rossi\n4022");
        var riletto = ColleagueRoster.Parse(ColleagueRoster.Format(originale));

        Assert.Equal(originale, riletto);
    }

    // -----------------------------------------------------------------------
    // Come si legge la risposta del centralino
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(200, ReachabilityOutcome.Reachable)]
    [InlineData(486, ReachabilityOutcome.Reachable)]
    [InlineData(603, ReachabilityOutcome.Reachable)]
    [InlineData(404, ReachabilityOutcome.NotConfigured)]
    [InlineData(480, ReachabilityOutcome.Unreachable)]
    [InlineData(503, ReachabilityOutcome.Unreachable)]
    [InlineData(408, ReachabilityOutcome.Unknown)]
    public void LaRispostaSiLeggePerQuelloCheDice(int codice, ReachabilityOutcome atteso)
        => Assert.Equal(atteso, ReachabilityRules.FromStatus(codice));

    [Theory]
    [InlineData(405)]
    [InlineData(501)]
    [InlineData(403)]
    public void IlRifiutoDelMetodoNonDiventaUnaNotiziaSulCollega(int codice)
    {
        // E' l'errore gia' commesso una volta sulle sottoscrizioni, dove il 404 del
        // centralino diventava «fuori servizio» e ottantuno interni funzionanti
        // risultavano spenti. Qui riguarda tutti allo stesso modo: tradurlo in «non in
        // turno» dichiarerebbe assente l'intero ufficio.
        Assert.Equal(ReachabilityOutcome.Refused, ReachabilityRules.FromStatus(codice));
        Assert.NotEqual(ReachabilityOutcome.Unreachable, ReachabilityRules.FromStatus(codice));
    }

    [Fact]
    public void IlSondaggioNonProduceMaiOccupato()
    {
        // Sa una cosa sola: il telefono risponde o no. Dichiarare «occupato» sarebbe
        // inventarsi un dato che questa strada non possiede.
        foreach (var esito in Enum.GetValues<ReachabilityOutcome>())
        {
            Assert.NotEqual(PresenceState.Busy, ReachabilityRules.ToPresence(esito));
        }
    }

    // -----------------------------------------------------------------------
    // Il centralino che risponde per chiunque
    // -----------------------------------------------------------------------

    [Fact]
    public void UnCentralinoCheRispondeAUnIndirizzoInesistenteNonSiCrede()
    {
        var politica = new ReachabilityProbePolicy();

        Assert.True(politica.CanTrustResults);

        // Un giro solo non basta: puo' essere un rimbalzo di rete o un apparato che si
        // stava riavviando.
        politica.ObserveControl(ReachabilityOutcome.Reachable);
        Assert.True(politica.CanTrustResults);

        politica.ObserveControl(ReachabilityOutcome.Reachable);
        Assert.False(politica.CanTrustResults);
        Assert.True(politica.AnswersForAnyone);
    }

    [Fact]
    public void QuandoIlCentralinoTornaARispondereBeneLaFunzioneTorna()
    {
        // L'amministratore ha corretto la configurazione, o il controllore di bordo e'
        // stato aggiornato: non si deve riavviare la postazione per accorgersene.
        var politica = new ReachabilityProbePolicy();

        politica.ObserveControl(ReachabilityOutcome.Reachable);
        politica.ObserveControl(ReachabilityOutcome.Reachable);
        Assert.False(politica.CanTrustResults);

        politica.ObserveControl(ReachabilityOutcome.NotConfigured);
        Assert.True(politica.CanTrustResults);
    }

    [Fact]
    public void UnaRisposteMancanteNonSpegneLaFunzione()
    {
        // Una rete che non risponde e' un disservizio passeggero: dedurne che la
        // funzione non esista costerebbe il quadro dei turni per tutto il turno.
        var politica = new ReachabilityProbePolicy();

        politica.ObserveControl(ReachabilityOutcome.Unknown);
        politica.ObserveControl(ReachabilityOutcome.Unknown);
        politica.ObserveControl(ReachabilityOutcome.Unknown);

        Assert.True(politica.CanTrustResults);
    }

    [Fact]
    public void IlRifiutoDelMetodoSiRiconosceDaUnaRispostaSola()
    {
        // Il centralino ha appena detto che quella domanda non la tratta, e non la
        // trattera' per nessun altro interno: attendere il giro successivo
        // significherebbe interrogarlo a vuoto un'altra volta.
        var politica = new ReachabilityProbePolicy();

        politica.ObserveExtension(ReachabilityOutcome.Refused);

        Assert.False(politica.CanTrustResults);
        Assert.True(politica.MethodRefused);
    }

    // -----------------------------------------------------------------------
    // Il quadro
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IlQuadroRiportaCiOcheIlCentralinoHaRisposto()
    {
        var sonda = new SondaFinta();
        sonda.Rispondi("4021", ReachabilityOutcome.Reachable);
        sonda.Rispondi("4022", ReachabilityOutcome.Unreachable);
        sonda.Rispondi("4023", ReachabilityOutcome.NotConfigured);

        var quadro = Crea(sonda);
        await quadro.ConfigureAsync(
            ColleagueRoster.Parse("4021 Mario Rossi\n4022 Anna Bianchi\n4023 Sbagliato"),
            TimeSpan.FromMinutes(1));

        await quadro.RefreshAsync();

        var righe = quadro.Snapshot();

        Assert.Equal(DutyState.OnDuty, righe[0].Duty);
        Assert.Equal(DutyState.OffDuty, righe[1].Duty);
        Assert.Equal(DutyState.NotConfigured, righe[2].Duty);
        Assert.Equal(1, ShiftBoardService.OnDutyCount(righe));
    }

    [Fact]
    public async Task LOrdineEQuelloScrittoDallOperatoreENonQuelloDelloStato()
    {
        // Un elenco che si riordina da se' mette il collega cercato ogni volta in un
        // posto diverso, e chi lo percorre con le frecce deve rileggerlo tutto invece
        // di contare le posizioni.
        var sonda = new SondaFinta();
        sonda.Rispondi("4021", ReachabilityOutcome.Unreachable);
        sonda.Rispondi("4022", ReachabilityOutcome.Reachable);

        var quadro = Crea(sonda);
        await quadro.ConfigureAsync(ColleagueRoster.Parse("4021 Primo\n4022 Secondo"), TimeSpan.FromMinutes(1));
        await quadro.RefreshAsync();

        var righe = quadro.Snapshot();

        Assert.Equal("Primo", righe[0].Name);
        Assert.Equal("Secondo", righe[1].Name);
    }

    [Fact]
    public async Task ConIlCentralinoCheRispondeAChiunqueNonSiMostraNessunoInTurno()
    {
        // IL TEST PIU' IMPORTANTE DEL FILE. Senza, la tabella direbbe che ci sono
        // tutti, sempre, e l'operatore passerebbe le chiamate a scrivanie vuote
        // fidandosi di quello che legge.
        var sonda = new SondaFinta { ControlAnswer = ReachabilityOutcome.Reachable };
        sonda.Rispondi("4021", ReachabilityOutcome.Reachable);
        sonda.Rispondi("4022", ReachabilityOutcome.Reachable);

        var quadro = Crea(sonda);
        await quadro.ConfigureAsync(ColleagueRoster.Parse("4021 Mario\n4022 Anna"), TimeSpan.FromMinutes(1));

        await quadro.RefreshAsync();
        await quadro.RefreshAsync();

        Assert.False(quadro.CanTrustResults);
        Assert.True(quadro.AnswersForAnyone);
        Assert.All(quadro.Snapshot(), riga => Assert.Equal(DutyState.Unknown, riga.Duty));
    }

    [Fact]
    public async Task IlBersaglioDiControlloVieneInterrogatoAOgniGiro()
    {
        var sonda = new SondaFinta();
        var quadro = Crea(sonda);

        await quadro.ConfigureAsync(ColleagueRoster.Parse("4021"), TimeSpan.FromMinutes(1));
        await quadro.RefreshAsync();

        Assert.Contains(sonda.Chiesti, t => t.StartsWith("bp-verifica-", StringComparison.Ordinal));

        // E viene per ultimo: prima si chiede degli interni veri, poi si verifica se le
        // risposte volessero dire qualcosa.
        Assert.StartsWith("bp-verifica-", sonda.Chiesti[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SenzaElencoLaFunzioneNonEsiste()
    {
        var sonda = new SondaFinta();
        var quadro = Crea(sonda);

        await quadro.ConfigureAsync([], TimeSpan.FromMinutes(1));

        Assert.False(quadro.IsConfigured);
        Assert.False(await quadro.RefreshAsync());
        Assert.Empty(sonda.Chiesti);
    }

    [Fact]
    public async Task SenzaSondaggioIlQuadroDiceCheNonSaInveceDiInventare()
    {
        var sonda = new SondaFinta { IsAvailable = false };
        var quadro = Crea(sonda);

        await quadro.ConfigureAsync(ColleagueRoster.Parse("4021 Mario"), TimeSpan.FromMinutes(1));
        await quadro.RefreshAsync();

        Assert.Empty(sonda.Chiesti);
        Assert.Equal(DutyState.Unknown, quadro.Snapshot()[0].Duty);
    }

    [Fact]
    public async Task IlGiroNonRipartePrimaDellIntervallo()
    {
        // Ogni interno costa un messaggio: un giro al secondo su venti colleghi
        // sarebbe piu' traffico di quanto il centralino ne mandi a noi.
        var sonda = new SondaFinta();
        var orologio = new ManualClock(Start);
        var quadro = new ShiftBoardService(() => sonda, presence: null, contacts: null, orologio);

        await quadro.ConfigureAsync(ColleagueRoster.Parse("4021"), TimeSpan.FromMinutes(1));

        quadro.Pump(orologio.Now);
        await Task.Delay(50);

        var dopoIlPrimo = sonda.Chiesti.Count;

        quadro.Pump(orologio.Now);
        await Task.Delay(50);

        Assert.Equal(dopoIlPrimo, sonda.Chiesti.Count);
    }

    [Fact]
    public async Task UnIntervalloTroppoCortoVieneRiportatoAlMinimo()
    {
        var sonda = new SondaFinta();
        var orologio = new ManualClock(Start);
        var quadro = new ShiftBoardService(() => sonda, presence: null, contacts: null, orologio);

        await quadro.ConfigureAsync(ColleagueRoster.Parse("4021"), TimeSpan.FromSeconds(1));

        quadro.Pump(orologio.Now);
        await Task.Delay(50);

        var dopoIlPrimo = sonda.Chiesti.Count;

        orologio.Advance(TimeSpan.FromSeconds(5));
        quadro.Pump(orologio.Now);
        await Task.Delay(50);

        Assert.Equal(dopoIlPrimo, sonda.Chiesti.Count);

        orologio.Advance(ShiftBoardService.MinimumInterval);
        quadro.Pump(orologio.Now);
        await Task.Delay(50);

        Assert.True(sonda.Chiesti.Count > dopoIlPrimo);
    }

    // -----------------------------------------------------------------------
    // Quello che si legge e quello che si sente
    // -----------------------------------------------------------------------

    [Fact]
    public void IlRiepilogoNominaGliAssentiENonIPresenti()
    {
        // Su una squadra di dodici persone l'elenco dei presenti e' lungo e non cambia
        // nessuna decisione; quello degli assenti e' corto ed e' l'unico che serve.
        var righe = new[]
        {
            Riga("4021", "Mario Rossi", DutyState.OnDuty),
            Riga("4022", "Anna Bianchi", DutyState.OffDuty),
            Riga("4023", "Ufficio Tecnico", DutyState.OnDuty),
        };

        var testo = ShiftBoardTexts.Summary(righe);

        Assert.Contains("2 colleghi in turno su 3", testo, StringComparison.Ordinal);
        Assert.Contains("Anna Bianchi", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("Mario Rossi", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void OltreQuattroAssentiSiContanoInveceDiElencarli()
    {
        var righe = Enumerable.Range(1, 6)
            .Select(i => Riga($"402{i}", $"Collega {i}", DutyState.OffDuty))
            .ToArray();

        var testo = ShiftBoardTexts.Summary(righe);

        Assert.Contains("6 non in turno", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("Collega 1", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void QuandoNonSiPuoSapereIlRiepilogoLoDiceInveceDiContare()
    {
        var righe = new[] { Riga("4021", "Mario Rossi", DutyState.OnDuty) };

        var testo = ShiftBoardTexts.Summary(righe, canTrust: false);

        Assert.Equal(ShiftBoardTexts.CannotDistinguish, testo);
        Assert.DoesNotContain("in turno su", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LaLetturaDiUnaRigaCominciaDalloStato()
    {
        // E' l'informazione per cui si sta percorrendo l'elenco: scorrendo dieci
        // colleghi per trovare chi c'e', il nome prima dello stato costringe ad
        // ascoltare due volte tanto.
        var testo = ShiftBoardTexts.Accessible(Riga("4021", "Mario Rossi", DutyState.OnDuty));

        Assert.StartsWith("In turno", testo, StringComparison.Ordinal);
        Assert.Contains("Mario Rossi", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void IlNumeroSiLeggeCifraPerCifra()
    {
        // Pronunciato come numero intero non si puo' ne' trascrivere ne' ricomporre.
        var testo = ShiftBoardTexts.Accessible(Riga("4021", string.Empty, DutyState.OnDuty));

        Assert.Contains("quattro, zero, due, uno", testo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IlDettaglioCompareSoloQuandoLaSorgenteLoConosce()
    {
        var conDettaglio = new ShiftEntry(
            "4021", "Mario Rossi", DutyState.OnDuty, PresenceState.Busy, null, "api");

        Assert.Equal("In turno, occupato", ShiftBoardTexts.Status(conDettaglio));

        // Dal sondaggio non arriva mai: ripetere «stato sconosciuto» su ogni riga
        // aggiungerebbe una parola a tutte per non dire niente su nessuna.
        Assert.Equal("In turno", ShiftBoardTexts.Status(Riga("4021", "Mario Rossi", DutyState.OnDuty)));
    }

    [Fact]
    public void LIntestazioneDiceQuandoEStataFattaLaVerifica()
    {
        // Un elenco fermo per un guasto di rete e' indistinguibile da un ufficio in cui
        // non si e' mosso nessuno.
        var righe = new[] { Riga("4021", "Mario Rossi", DutyState.OnDuty) };

        Assert.Contains("in attesa della prima verifica", ShiftBoardTexts.Header(righe, null),
            StringComparison.Ordinal);

        Assert.Contains("09:30", ShiftBoardTexts.Header(righe, Start.AddMinutes(30)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OgniStatoHaUnSegnoDiFormaDiversa()
    {
        // Lo stato non e' affidato al solo colore: due stati con lo stesso segno, nella
        // pastiglia neutra della voce, si distinguerebbero solo leggendo la parola.
        DutyState[] turni = [DutyState.OnDuty, DutyState.OffDuty, DutyState.NotConfigured, DutyState.Unknown];
        PresenceState[] dettagli =
        [
            PresenceState.Unknown, PresenceState.Busy, PresenceState.Ringing,
            PresenceState.DoNotDisturb, PresenceState.Away,
        ];

        var inTurno = dettagli
            .Select(d => ShiftBoardTexts.StatusIcon(new ShiftEntry("4021", "Mario Rossi", DutyState.OnDuty, d, null, "api")))
            .ToList();

        var fuori = turni.Skip(1)
            .Select(t => ShiftBoardTexts.StatusIcon(Riga("4021", "Mario Rossi", t)))
            .ToList();

        var tutti = inTurno.Concat(fuori).ToList();

        Assert.All(tutti, segno => Assert.False(string.IsNullOrWhiteSpace(segno)));
        Assert.Equal(tutti.Count, tutti.Distinct().Count());
    }

    [Fact]
    public void IlSegnoNonCambiaLaParola()
    {
        // Il segno e' decorativo: la voce si legge dal suo nome accessibile, che resta
        // quello di sempre.
        var riga = new ShiftEntry("4021", "Mario Rossi", DutyState.OnDuty, PresenceState.Busy, null, "api");

        Assert.StartsWith("In turno, occupato. Mario Rossi", ShiftBoardTexts.Accessible(riga), StringComparison.Ordinal);
    }

    private static ShiftEntry Riga(string numero, string nome, DutyState stato)
        => new(numero, nome, stato, PresenceState.Unknown, null, "sondaggio");
}
