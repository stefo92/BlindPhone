using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica la fascia di stato in cima alla scheda della chiamata: quale stato mostra,
/// con che parola, e che cosa misura il cronometro.
/// </summary>
/// <remarks>
/// <para>
/// La fascia prende il posto dei tre avvisi a tutta larghezza che comparivano in cima
/// alla finestra. Serve a chi guarda lo schermo, e proprio per questo si collauda qui:
/// chi sviluppa vede la fascia diventare verde e considera la cosa fatta, senza
/// accorgersi che con una chiamata in attesa e un'altra che squilla dice la cosa
/// sbagliata.
/// </para>
/// <para>
/// La regola che queste prove difendono è la precedenza: squilla, poi in linea, poi in
/// attesa, poi fuori servizio, poi libero. Uno stato alla volta.
/// </para>
/// </remarks>
public sealed class FasciaDiStatoTests
{
    private static readonly DateTimeOffset Adesso = new(2026, 10, 31, 10, 0, 0, TimeSpan.FromHours(1));

    // -----------------------------------------------------------------------
    // Precedenza
    // -----------------------------------------------------------------------

    [Fact]
    public void SenzaNienteLaPostazioneELibera()
        => Assert.Equal(LineStatus.Free, LineStatusCatalog.Resolve(default));

    [Fact]
    public void SquillaVincePrimaDiTutto()
    {
        // E' l'unica cosa con una scadenza: con qualcuno in linea, qualcuno in attesa e
        // la postazione in pausa, una chiamata che squilla resta quella da guardare.
        var tutto = new LineStatusContext(
            HasRingingCall: true, IsTalking: true, HasOutgoingCall: true, WaitingCount: 2, IsServicePaused: true);

        Assert.Equal(LineStatus.Ringing, LineStatusCatalog.Resolve(tutto));
    }

    [Fact]
    public void InLineaVinceSullAttesaESullaPausa()
    {
        var stato = new LineStatusContext(IsTalking: true, WaitingCount: 1, IsServicePaused: true);

        Assert.Equal(LineStatus.Talking, LineStatusCatalog.Resolve(stato));
    }

    [Fact]
    public void LAttesaVinceSullaPausa()
    {
        // Mettersi in pausa vuol dire non prendere chiamate nuove, non dimenticare
        // quella che aspetta: chi guarda deve vedere prima che qualcuno conta i minuti.
        var stato = new LineStatusContext(WaitingCount: 1, IsServicePaused: true);

        Assert.Equal(LineStatus.OnHold, LineStatusCatalog.Resolve(stato));
    }

    [Fact]
    public void LaPausaVinceSuLibero()
        => Assert.Equal(LineStatus.OutOfService, LineStatusCatalog.Resolve(new LineStatusContext(IsServicePaused: true)));

    [Fact]
    public void LaChiamataUscenteCheSquillaAncoraContaComeInLinea()
    {
        // La linea e' impegnata: «libero» sarebbe falso, e «squilla» si confonderebbe
        // con una chiamata da prendere.
        Assert.Equal(LineStatus.Talking, LineStatusCatalog.Resolve(new LineStatusContext(HasOutgoingCall: true)));
    }

    [Theory]
    [InlineData(false, false, false, 0, false, LineStatus.Free)]
    [InlineData(false, false, false, 0, true, LineStatus.OutOfService)]
    [InlineData(false, false, false, 1, false, LineStatus.OnHold)]
    [InlineData(false, false, false, 1, true, LineStatus.OnHold)]
    [InlineData(false, true, false, 0, false, LineStatus.Talking)]
    [InlineData(false, true, false, 1, true, LineStatus.Talking)]
    [InlineData(false, false, true, 1, false, LineStatus.Talking)]
    [InlineData(true, false, false, 0, false, LineStatus.Ringing)]
    [InlineData(true, true, false, 0, false, LineStatus.Ringing)]
    [InlineData(true, false, false, 3, true, LineStatus.Ringing)]
    public void LaPrecedenzaEQuellaDichiarata(
        bool squilla, bool inLinea, bool uscente, int inAttesa, bool inPausa, LineStatus atteso)
    {
        var stato = new LineStatusContext(squilla, inLinea, uscente, inAttesa, inPausa);

        Assert.Equal(atteso, LineStatusCatalog.Resolve(stato));
    }

    // -----------------------------------------------------------------------
    // Come si presenta
    // -----------------------------------------------------------------------

    public static TheoryData<LineStatus> TuttiGliStati => [.. Enum.GetValues<LineStatus>()];

    [Theory]
    [MemberData(nameof(TuttiGliStati))]
    public void OgniStatoHaParolaPittogrammaENomeAccessibile(LineStatus stato)
    {
        var faccia = LineStatusCatalog.Face(stato);

        Assert.Equal(stato, faccia.Status);
        Assert.False(string.IsNullOrWhiteSpace(faccia.Word));
        Assert.False(string.IsNullOrWhiteSpace(faccia.Icon));
        Assert.StartsWith("Stato della linea: ", faccia.AccessibleName, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TuttiGliStati))]
    public void LaParolaEInMaiuscoloEIlNomeAccessibileNo(LineStatus stato)
    {
        // Il maiuscolo e' per l'occhio. Alcune sintesi compitano le parole maiuscole:
        // «IN LINEA» diventerebbe «i enne linea».
        var faccia = LineStatusCatalog.Face(stato);
        var detto = faccia.AccessibleName["Stato della linea: ".Length..];

        Assert.Equal(faccia.Word.ToUpperInvariant(), faccia.Word);
        Assert.Equal(faccia.Word.ToLowerInvariant(), detto);
    }

    [Fact]
    public void LeCinqueParoleSonoQuelleDellaProposta()
    {
        Assert.Equal("LIBERO", LineStatusCatalog.Face(LineStatus.Free).Word);
        Assert.Equal("SQUILLA", LineStatusCatalog.Face(LineStatus.Ringing).Word);
        Assert.Equal("IN LINEA", LineStatusCatalog.Face(LineStatus.Talking).Word);
        Assert.Equal("IN ATTESA", LineStatusCatalog.Face(LineStatus.OnHold).Word);
        Assert.Equal("FUORI SERVIZIO", LineStatusCatalog.Face(LineStatus.OutOfService).Word);
    }

    [Fact]
    public void ParoleEPittogrammiSonoTuttiDiversi()
    {
        // Lo stato non e' mai affidato al solo colore: se due stati condividessero la
        // parola o il segno, in contrasto elevato — dove il colore e' lo stesso per
        // tutti — non si distinguerebbero.
        var facce = Enum.GetValues<LineStatus>().Select(LineStatusCatalog.Face).ToList();

        Assert.Equal(facce.Count, facce.Select(f => f.Word).Distinct().Count());
        Assert.Equal(facce.Count, facce.Select(f => f.Icon).Distinct().Count());
    }

    [Fact]
    public void IlPittogrammaDelFuoriServizioEsisteAncheSuWindows10()
    {
        // E733, il cerchio barrato piu' ovvio, nel carattere di Windows 10 non c'e':
        // al suo posto comparirebbe un rettangolo vuoto.
        Assert.NotEqual("", LineStatusCatalog.Face(LineStatus.OutOfService).Icon);
    }

    [Fact]
    public void LampeggianoSoloSquillaEFuoriServizio()
    {
        // Dove il lampeggio c'era gia', e per la stessa ragione: il primo ha una
        // scadenza, il secondo cambia il significato di tutta la finestra. Una
        // conversazione dura minuti, e una fascia che pulsa per tutto quel tempo accanto
        // agli occhi di chi lavora con l'ingrandimento e' un disturbo.
        var lampeggianti = Enum.GetValues<LineStatus>()
            .Where(s => LineStatusCatalog.Face(s).Flashes)
            .ToList();

        Assert.Equal(new[] { LineStatus.Ringing, LineStatus.OutOfService }, lampeggianti);
    }

    // -----------------------------------------------------------------------
    // Il cronometro
    // -----------------------------------------------------------------------

    [Fact]
    public void LiberoNonHaCronometro()
        => Assert.Equal(string.Empty, LineStatusCatalog.Elapsed(LineStatus.Free, Adesso));

    [Fact]
    public void LoSquilloSiMisuraDaQuandoECominciato()
    {
        var squilla = new Call("s-1", CallDirection.Inbound, new CallParty("4021"), Adesso - TimeSpan.FromSeconds(12));

        Assert.Equal("0:12", LineStatusCatalog.Elapsed(LineStatus.Ringing, Adesso, ringing: squilla));
    }

    [Fact]
    public void LaConversazioneSiMisuraDallaRisposta()
    {
        var chiamata = new Call("a-1", CallDirection.Inbound, new CallParty("4021"), Adesso - TimeSpan.FromMinutes(10));
        chiamata.TryApply(CallTrigger.IncomingCall, Adesso - TimeSpan.FromMinutes(10), out _);
        chiamata.TryApply(CallTrigger.Answer, Adesso - TimeSpan.FromSeconds(222), out _);

        Assert.Equal("3:42", LineStatusCatalog.Elapsed(LineStatus.Talking, Adesso, active: chiamata));
    }

    [Fact]
    public void LaChiamataUscenteNonRispostaNonHaCronometro()
    {
        // «Durata della conversazione» sarebbe falso, e un tempo accanto a IN LINEA si
        // leggerebbe come una conversazione gia' cominciata.
        Assert.Equal(string.Empty, LineStatusCatalog.Elapsed(LineStatus.Talking, Adesso));
    }

    [Fact]
    public void InConferenzaContaLaGambaPiuLunga()
    {
        var prima = InLineaDa("c-1", TimeSpan.FromMinutes(5));
        var seconda = InLineaDa("c-2", TimeSpan.FromMinutes(2));

        Assert.Equal("5:00", LineStatusCatalog.Elapsed(LineStatus.Talking, Adesso, conferenced: [prima, seconda]));
    }

    [Fact]
    public void LAttesaMisuraChiAspettaDaPiuTempo()
    {
        // E' quello che si rischia di dimenticare, come nella casella delle attese.
        var recente = InAttesaDa("h-1", TimeSpan.FromSeconds(30));
        var vecchia = InAttesaDa("h-2", TimeSpan.FromSeconds(72));

        Assert.Equal("1:12", LineStatusCatalog.Elapsed(LineStatus.OnHold, Adesso, waiting: [recente, vecchia]));
    }

    [Fact]
    public void LaPausaSiMisuraDaQuandoECominciata()
        => Assert.Equal("7:30", LineStatusCatalog.Elapsed(LineStatus.OutOfService, Adesso, currentPause: TimeSpan.FromSeconds(450)));

    [Fact]
    public void SopraLOraCompaionoLeOre()
        => Assert.Equal("1:02:07", LineStatusCatalog.Elapsed(LineStatus.OutOfService, Adesso, currentPause: new TimeSpan(1, 2, 7)));

    // -----------------------------------------------------------------------
    // L'ufficio di chi chiama, per la riga sotto il nome
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LUfficioDelChiamanteArrivaDallaRubrica()
    {
        var rubrica = new FakeContactRepository().AddExtension("Mario Rossi", "4021", department: "Ufficio tecnico");
        var (engine, calls) = Avvia(rubrica);

        engine.SimulateIncoming("4021");
        await Task.Delay(50);

        Assert.Equal("Ufficio tecnico", calls.RingingCall?.RemoteParty.Department);
    }

    [Fact]
    public async Task LUfficioDiChiSiChiamaArrivaDallaRubrica()
    {
        var rubrica = new FakeContactRepository().AddExtension("Mario Rossi", "4021", department: "Ufficio tecnico");
        var (_, calls) = Avvia(rubrica);

        var id = await calls.DialAsync("4021");

        Assert.Equal("Ufficio tecnico", calls.GetCall(id)?.RemoteParty.Department);
    }

    [Fact]
    public async Task SenzaRubricaNonSiInventaNessunUfficio()
    {
        var (engine, calls) = Avvia(new FakeContactRepository());

        engine.SimulateIncoming("3491234567");
        await Task.Delay(50);

        Assert.Null(calls.RingingCall?.RemoteParty.Department);
    }

    private static Call InLineaDa(string id, TimeSpan durata)
    {
        var chiamata = new Call(id, CallDirection.Inbound, new CallParty("4021"), Adesso - durata);
        chiamata.TryApply(CallTrigger.IncomingCall, Adesso - durata, out _);
        chiamata.TryApply(CallTrigger.Answer, Adesso - durata, out _);
        return chiamata;
    }

    private static Call InAttesaDa(string id, TimeSpan durata)
    {
        var chiamata = InLineaDa(id, durata + TimeSpan.FromMinutes(1));
        chiamata.TryApply(CallTrigger.Hold, Adesso - durata, out _);
        return chiamata;
    }

    private static (FakeEngine Engine, CallSessionManager Calls) Avvia(FakeContactRepository rubrica)
    {
        var engine = new FakeEngine();

        var calls = new CallSessionManager(
            engine,
            new RecordingAnnouncer(),
            new CallAnnouncementMapper(),
            new AnnouncementContext(),
            rubrica);

        return (engine, calls);
    }
}
