using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sulla composizione verso il gateway di riunione Zoom.
/// </summary>
/// <remarks>
/// <para>
/// La composizione è <b>posizionale</b>: l'identificativo, un punto, il codice
/// d'accesso. Sbagliare il numero di punti non produce un errore, produce l'ingresso
/// in una riunione che non esiste, con un messaggio che non lo spiega. È la ragione
/// per cui la stringa la costruisce il programma e queste prove esistono.
/// </para>
/// <para>
/// Il resto delle prove riguarda due cose che non si vedono guardando il codice: che
/// il <b>codice d'accesso non finisca dove viene riletto</b>, e che i codec offerti a
/// Zoom siano quelli che accetta.
/// </para>
/// </remarks>
public sealed class RiunioneZoomTests
{
    private static string Componi(string id, string? codice = null)
    {
        Assert.True(ZoomRoomConnector.TryBuild(id, codice, null, out var dial, out var errore), errore);
        return dial.Dial;
    }

    // -----------------------------------------------------------------------
    // Costruzione
    // -----------------------------------------------------------------------

    [Fact]
    public void SenzaCodiceSiComponeIlSoloIdentificativo()
        => Assert.Equal("812345678@zoomcrc.com", Componi("812345678"));

    [Fact]
    public void ConIlCodiceSiAggiungeDopoUnPuntoSolo()
        => Assert.Equal("812345678.4321@zoomcrc.com", Componi("812345678", "4321"));

    [Fact]
    public void GliSpaziDellInvitoNonFannoFallireLaComposizione()
    {
        // Gli inviti scrivono l'identificativo a gruppi, e chi lo detta a voce lo
        // scandisce allo stesso modo. Rifiutarlo per gli spazi sarebbe pedanteria a
        // carico di chi digita a orecchio.
        Assert.Equal("81234567890@zoomcrc.com", Componi("812 3456 7890"));
        Assert.Equal("81234567890@zoomcrc.com", Componi("812-3456-7890"));
    }

    [Fact]
    public void IlGatewayPuoEssereQuelloRegionaleDellEnte()
    {
        Assert.True(ZoomRoomConnector.TryBuild(
            "812345678", "4321", "192.0.2.10", out var dial, out _));

        Assert.Equal("812345678.4321@192.0.2.10", dial.Dial);
    }

    // -----------------------------------------------------------------------
    // Rifiuti: ognuno dice che cosa correggere
    // -----------------------------------------------------------------------

    [Fact]
    public void IlCodiceConLettereVieneRifiutatoSpiegandoPerche()
    {
        // LA TRAPPOLA PRATICA DI QUESTA FUNZIONE. Zoom tiene due codici per la stessa
        // riunione: quello web, che puo' avere lettere, e quello numerico per chi
        // entra per telefono o da apparato SIP. Chi copia dall'invito prende il primo,
        // non entra mai, e il gateway non glielo spiega.
        Assert.False(ZoomRoomConnector.TryBuild(
            "812345678", "Zoom2026", null, out _, out var errore));

        Assert.NotNull(errore);
        Assert.Contains("numerico", errore, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "manca")]
    [InlineData("abcdefghi", "cifre")]
    [InlineData("12345", "cifre")]
    [InlineData("123456789012", "cifre")]
    public void GliIdentificativiSbagliatiDiconoQualEIlProblema(string id, string atteso)
    {
        Assert.False(ZoomRoomConnector.TryBuild(id, null, null, out _, out var errore));

        Assert.NotNull(errore);
        Assert.Contains(atteso, errore, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LIdentificativoTroppoCortoDiceQuanteCifreHa()
    {
        // Undici cifre digitate a orecchio sono l'errore piu' probabile di questa
        // funzione: dire quante ne sono arrivate evita di ricontarle a mano.
        Assert.False(ZoomRoomConnector.TryBuild("12345", null, null, out _, out var errore));

        Assert.Contains("5", errore!, StringComparison.Ordinal);
    }

    [Fact]
    public void INuoviTestiDiRifiutoNonPortanoAccenti()
    {
        // Convenzione dei testi pronunciati del prodotto.
        var rifiuti = new List<string>();

        foreach (var (id, codice) in new[]
                 {
                     ("", (string?)null), ("abc", null), ("12345", null),
                     ("812345678", "Zoom2026"),
                 })
        {
            if (!ZoomRoomConnector.TryBuild(id, codice, null, out _, out var errore)) rifiuti.Add(errore!);
        }

        Assert.NotEmpty(rifiuti);

        var conAccenti = rifiuti
            .Where(t => t.Any(c => "àèéìòùÀÈÉÌÒÙ".Contains(c, StringComparison.Ordinal)))
            .ToList();

        Assert.True(conAccenti.Count == 0,
            "Testi pronunciati con lettere accentate:\n" + string.Join('\n', conAccenti));
    }

    // -----------------------------------------------------------------------
    // Riconoscimento
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("812345678@zoomcrc.com", true)]
    [InlineData("812345678.4321@zoomcrc.com", true)]
    [InlineData("812345678@ZOOMCRC.COM", true)]
    [InlineData("812345678@eu01.zoomcrc.com", true)]
    [InlineData("201@pbx.ente.it", false)]
    [InlineData("0805551234", false)]
    [InlineData("", false)]
    public void SiRiconosceUnIndirizzoDiRiunione(string destinazione, bool atteso)
        => Assert.Equal(atteso, ZoomRoomConnector.IsMeetingAddress(destinazione));

    [Fact]
    public void LIdentificativoSiEstraeDallaComposizione()
        => Assert.Equal("812345678", ZoomRoomConnector.MeetingIdOf("812345678.4321@zoomcrc.com"));

    [Fact]
    public void LaDescrizioneNonPronunciaIlGateway()
    {
        // "Chiocciola zoomcrc punto com" non dice niente all'operatore e occupa il
        // tempo in cui vorrebbe sentire il resto.
        var descrizione = ZoomRoomConnector.Describe("812345678.4321@zoomcrc.com");

        Assert.NotNull(descrizione);
        Assert.DoesNotContain("zoomcrc", descrizione, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("812345678", descrizione, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Riservatezza: il codice non deve poter essere riletto
    // -----------------------------------------------------------------------

    /// <summary>
    /// Il codice d'accesso non deve sopravvivere alla composizione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Viaggia dentro il numero composto, quindi senza questo presidio finirebbe nel
    /// registro delle chiamate, nella ricomposizione dell'ultimo numero e — questo è
    /// il punto — <b>letto a voce alta</b> in uno stanzone dove ci sono altre persone.
    /// </para>
    /// <para>
    /// L'archivio è cifrato e il file non è il problema: il problema è la voce. Il
    /// codice d'accesso della giunta comunale pronunciato in un centralino è un codice
    /// diffuso.
    /// </para>
    /// </remarks>
    [Fact]
    public void IlCodiceDAccessoNonEntraNellaFormaDaPronunciare()
    {
        Assert.True(ZoomRoomConnector.TryBuild("812345678", "4321", null, out var dial, out _));

        Assert.Contains("4321", dial.Dial, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", dial.Spoken, StringComparison.Ordinal);
        Assert.Equal("812345678@zoomcrc.com", dial.Spoken);
    }

    [Fact]
    public void LaRipulituraTogliIlCodiceELasciaRiconoscibileLaRiunione()
    {
        var pulito = ZoomRoomConnector.Redact("812345678.4321@zoomcrc.com");

        Assert.Equal("812345678@zoomcrc.com", pulito);
    }

    [Theory]
    [InlineData("0805551234")]
    [InlineData("201@pbx.ente.it")]
    [InlineData("+39 080 555.1234")]
    public void LaRipulituraNonToccaQuelloCheNonEUnaRiunione(string destinazione)
    {
        // Un numero di telefono con un punto dentro deve restare com'e': questa
        // funzione passa su tutto cio' che viene composto.
        Assert.Equal(destinazione, ZoomRoomConnector.Redact(destinazione));
    }

    [Fact]
    public void RipulireDueVolteNonCambiaNiente()
    {
        var una = ZoomRoomConnector.Redact("812345678.4321@zoomcrc.com");

        Assert.Equal(una, ZoomRoomConnector.Redact(una));
    }

    // -----------------------------------------------------------------------
    // Codec
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verso Zoom non si offre G.729.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il gateway dichiara G.711, G.722 e Opus. G.729 <b>non c'è</b>, ed è il primo
    /// della preferenza predefinita del prodotto — scelta giusta per i centralini
    /// italiani, sbagliata qui.
    /// </para>
    /// <para>
    /// Nel caso migliore si spreca un giro di trattativa. Nel caso peggiore, su una
    /// postazione dove l'amministratore ha ristretto l'elenco al solo G.729, la
    /// chiamata verso la riunione <b>non si instaura affatto</b> e il sintomo è una
    /// chiamata che non parte senza spiegazioni.
    /// </para>
    /// </remarks>
    [Fact]
    public void VersoLaRiunioneNonSiOffreIlCodecCheZoomNonHa()
    {
        var offerti = ZoomRoomConnector.CodecPreferenceFor(
            "812345678.4321@zoomcrc.com", AudioCodecCatalog.DefaultPreference);

        Assert.DoesNotContain(AudioCodec.G729, offerti);
    }

    [Fact]
    public void VersoLaRiunioneLaBandaLargaVienePerPrima()
    {
        // Qui non c'e' nessun trunk che imponga la compressione, ed e' il codec che
        // soddisfa il requisito di banda larga della norma europea.
        var offerti = ZoomRoomConnector.CodecPreferenceFor(
            "812345678@zoomcrc.com", AudioCodecCatalog.DefaultPreference);

        Assert.Equal(AudioCodec.G722, offerti[0]);
        Assert.Contains(AudioCodec.PCMA, offerti);
    }

    [Fact]
    public void ITuttiICodecOffertiAZoomSonoFraQuelliCheAccetta()
    {
        // G.711 nelle due varianti e G.722. Opus resta fuori di proposito: lo stack
        // SIP in uso non lo implementa, e il guadagno lo appiattisce il mixer.
        var accettati = new[] { AudioCodec.G722, AudioCodec.PCMA, AudioCodec.PCMU };

        Assert.All(ZoomRoomConnector.Codecs, c => Assert.Contains(c, accettati));
    }

    [Theory]
    [InlineData("0805551234")]
    [InlineData("201")]
    [InlineData("201@pbx.ente.it")]
    public void SuOgniAltraDestinazioneLaPreferenzaConfigurataNonSiTocca(string destinazione)
    {
        // Questa funzione passa su ogni chiamata uscente del prodotto: non deve
        // cambiare il comportamento di una sola telefonata ordinaria.
        var configurata = AudioCodecCatalog.DefaultPreference;

        Assert.Same(configurata, ZoomRoomConnector.CodecPreferenceFor(destinazione, configurata));
    }

    [Fact]
    public void LaBandaLargaRestaDisponibileQuindiLaNormaEUropeaRestaSoddisfatta()
        => Assert.True(AudioCodecCatalog.IncludesWideband(ZoomRoomConnector.Codecs));

    // -----------------------------------------------------------------------
    // Come l'operatore scrive nel campo di composizione
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("812345678.4321", "812345678", "4321")]
    [InlineData("812345678*4321", "812345678", "4321")]
    [InlineData("812345678,4321", "812345678", "4321")]
    public void ITreSeparatoriAmmessiDividonoIdentificativoECodice(
        string digitato, string id, string codice)
    {
        var (identificativo, passcode) = ZoomRoomConnector.Split(digitato);

        Assert.Equal(id, identificativo);
        Assert.Equal(codice, passcode);
    }

    /// <summary>
    /// Lo spazio non separa.
    /// </summary>
    /// <remarks>
    /// Gli inviti scrivono l'identificativo a gruppi e chi lo detta a voce lo
    /// scandisce allo stesso modo. Trattare lo spazio come separatore trasformerebbe
    /// l'ultimo gruppo in un codice d'accesso, e il rifiuto parlerebbe di un codice
    /// che nessuno ha scritto: un messaggio incomprensibile su un errore inventato.
    /// </remarks>
    [Fact]
    public void LoSpazioNonSeparaPercheGliInvitiScrivonoAGruppi()
    {
        var (id, codice) = ZoomRoomConnector.Split("812 3456 7890");

        Assert.Null(codice);
        Assert.Equal("812 3456 7890", id);

        // E la composizione costruita da li' e' quella giusta.
        Assert.Equal("81234567890@zoomcrc.com", Componi(id, codice));
    }

    [Fact]
    public void SenzaSeparatoreNonCEUnCodice()
        => Assert.Null(ZoomRoomConnector.Split("812345678").Passcode);

    // -----------------------------------------------------------------------
    // Che cosa arriva alla linea e che cosa arriva all'operatore
    // -----------------------------------------------------------------------

    private static (FakeEngine Engine, CallSessionManager Calls) Postazione()
    {
        var engine = new FakeEngine();

        var calls = new CallSessionManager(
            engine, new RecordingAnnouncer(), new CallAnnouncementMapper(), new AnnouncementContext());

        return (engine, calls);
    }

    /// <summary>
    /// Il codice d'accesso va in linea ma non nella controparte.
    /// </summary>
    /// <remarks>
    /// È il presidio di riservatezza della funzione, ed è verificato qui perché è il
    /// punto in cui le due strade si separano: la stringa completa serve al gateway
    /// per farci entrare, la controparte è quella che viene <b>pronunciata</b> e
    /// conservata nelle ultime chiamate. In un centralino dove ci sono altre persone,
    /// un codice d'accesso letto ad alta voce è un codice diffuso.
    /// </remarks>
    [Fact]
    public async Task IlCodiceArrivaAlGatewayMaNonAllaControparte()
    {
        var (engine, calls) = Postazione();

        await calls.DialAsync("812345678.4321@zoomcrc.com", CancellationToken.None);

        // Alla linea serve intero, altrimenti non si entra.
        Assert.Contains(engine.Operations, o => o.Contains("812345678.4321@zoomcrc.com", StringComparison.Ordinal));

        var chiamata = calls.OutgoingCall ?? calls.ActiveCall;

        Assert.NotNull(chiamata);
        Assert.DoesNotContain("4321", chiamata.RemoteParty.Number, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SenzaNomeInRubricaLaRiunioneSiAnnunciaComeRiunione()
    {
        // "Ottocentododici milioni... chiocciola zoomcrc punto com" e' quello che
        // l'operatore sentirebbe senza questo.
        var (_, calls) = Postazione();

        await calls.DialAsync("812345678.4321@zoomcrc.com", CancellationToken.None);

        var chiamata = calls.OutgoingCall ?? calls.ActiveCall;

        Assert.NotNull(chiamata);
        Assert.Contains("riunione", chiamata.RemoteParty.DisplayName ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnaChiamataOrdinariaNonVieneToccata()
    {
        // La ripulitura passa su OGNI composizione del prodotto: non deve cambiare
        // niente per una telefonata normale.
        var (engine, calls) = Postazione();

        await calls.DialAsync("0805551234", CancellationToken.None);

        var chiamata = calls.OutgoingCall ?? calls.ActiveCall;

        Assert.NotNull(chiamata);
        Assert.Equal("0805551234", chiamata.RemoteParty.Number);
        Assert.Contains(engine.Operations, o => o.Contains("0805551234", StringComparison.Ordinal));
    }
}
