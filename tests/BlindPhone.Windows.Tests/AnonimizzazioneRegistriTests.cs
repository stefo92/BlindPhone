using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Anonimizzazione dei registri diagnostici.
/// </summary>
/// <remarks>
/// <para>
/// I registri finiscono nel pacchetto per l'assistenza, che viaggia via posta
/// elettronica e quindi esce dall'ente. Contengono chi ha chiamato, da quale numero e
/// con chi ha parlato: dati di traffico telefonico ai sensi del GDPR.
/// </para>
/// <para>
/// Queste prove verificano le due promesse insieme, perché una sola non basta:
/// <b>nessun recapito sopravvive</b>, e <b>il registro resta leggibile</b>. Una
/// funzione che cancella tutto rispetterebbe la prima e tradirebbe la seconda,
/// rendendo inutile il pacchetto proprio per la diagnosi a cui serve.
/// </para>
/// <para>
/// Le righe usate sono ricalcate su quelle vere del centralino dell'ente, con i
/// recapiti sostituiti da numeri inventati.
/// </para>
/// </remarks>
public sealed class AnonimizzazioneRegistriTests
{
    private static IReadOnlyList<string> Anonimizza(params string[] righe)
        => new LogAnonymizer().Anonymize(righe);

    // -----------------------------------------------------------------------
    // Prima promessa: i recapiti spariscono
    // -----------------------------------------------------------------------

    [Fact]
    public void IlRecapitoDentroUnUriSipSparisce()
    {
        var righe = Anonimizza("From: <sip:+393331234567@aopr.ras.uccpbx.com>;tag=ABC");

        Assert.DoesNotContain("393331234567", righe[0]);
        Assert.Contains("ESTERNO-01", righe[0]);

        // Il dominio resta: serve a riconoscere di quale centralino si parla.
        Assert.Contains("@aopr.ras.uccpbx.com", righe[0]);
    }

    [Fact]
    public void LoStessoRecapitoDiventaSempreLaStessaEtichetta()
    {
        // Senza questa proprieta' un registro non si potrebbe piu' leggere: seguire
        // una chiamata significa riconoscere lo stesso recapito in venti righe.
        var righe = Anonimizza(
            "SIP ricevuto: INVITE da <sip:+393331234567@pbx>",
            "Chiamante +393331234567 non presente in rubrica",
            "Chiamata registrata nell'elenco: Incoming con +393331234567, durata 00:00:12");

        var etichetta = "ESTERNO-01";

        Assert.All(righe, r => Assert.Contains(etichetta, r));
        Assert.All(righe, r => Assert.DoesNotContain("393331234567", r));
    }

    [Fact]
    public void InterniEdEsterniRestanoDistinguibili()
    {
        // Leggendo un registro cambia tutto sapere se chi chiama e' dentro la
        // struttura o fuori: l'etichetta lo deve dire.
        var righe = Anonimizza("da <sip:4021@pbx> verso <sip:+393331234567@pbx>");

        Assert.Contains("INTERNO-01", righe[0]);
        Assert.Contains("ESTERNO-01", righe[0]);
    }

    [Fact]
    public void IlNominativoDiUnaPersonaSparisce()
    {
        // Nei registri veri il centralino accompagna la chiamata con il nominativo:
        // e' un dato personale come il numero.
        var righe = Anonimizza("From: \"Chiamate esterne:Mario Rossi\" <sip:+393331234567@pbx>");

        Assert.DoesNotContain("Mario Rossi", righe[0]);
        Assert.Contains("NOMINATIVO-01", righe[0]);
    }

    [Fact]
    public void LEtichettaDiLineaNonEUnDatoPersonale()
    {
        // "Chiamate esterne" dice da quale fascio arriva la chiamata e non chi
        // stesse telefonando: toglierla sarebbe perdere contesto senza proteggere
        // nessuno.
        var righe = Anonimizza("From: \"Chiamate esterne\" <sip:+393331234567@pbx>");

        Assert.Contains("Chiamate esterne", righe[0]);
    }

    [Fact]
    public void IlRecapitoSparisceAncheDalleFrasiInglesiDellaLibreria()
    {
        // La libreria SIP scrive frasi proprie, e inseguirle una per una
        // significherebbe restare sempre indietro di una versione.
        var righe = Anonimizza(
            "SIP inviato: INVITE a <sip:4021@pbx>",
            "Stopping SIP notifier user agent for user 4021 and resource URI sip:4021@pbx");

        Assert.All(righe, r => Assert.DoesNotContain("4021", r));
    }

    // -----------------------------------------------------------------------
    // Seconda promessa: il registro resta leggibile
    // -----------------------------------------------------------------------

    [Fact]
    public void LePorteNonVengonoScambiatePerRecapiti()
    {
        var righe = Anonimizza(
            "da <sip:5061@pbx>",
            "RTPChannel closing, RTP receiver on port 5061, control 5062");

        // Il recapito 5061 e' stato riconosciuto come tale nella prima riga, ma
        // nella seconda le stesse cifre sono una porta: il confine dopo i due punti
        // e dopo "port " le protegge.
        Assert.Contains("port 5061", righe[1]);
    }

    [Fact]
    public void ILContatoriDiTransazioneRestanoIntatti()
    {
        // E' il difetto trovato l'11 agosto 2026 leggendo un registro anonimo:
        // compariva "CSeq: INTERNO-202 NOTIFY", cioe' il registro rovinato.
        var righe = Anonimizza(
            "da <sip:4712@pbx>",
            "CSeq: 4712 NOTIFY");

        Assert.Equal("CSeq: 4712 NOTIFY", righe[1]);
    }

    [Fact]
    public void GliIdentificativiEsadecimaliRestanoInteri()
    {
        // Stesso difetto, altra forma: "tag=d08a-c6d6d01d4021f59c" conteneva una
        // sequenza preceduta da una lettera, e veniva sostituita dentro il tag.
        var righe = Anonimizza(
            "da <sip:4021@pbx>",
            "To: <sip:pbx>;tag=d08a-c6d6d01d4021f59ca84fb07842a24592");

        Assert.Contains("tag=d08a-c6d6d01d4021f59ca84fb07842a24592", righe[1]);
    }

    [Fact]
    public void GliIndirizziIpRestanoIntatti()
    {
        var righe = Anonimizza(
            "da <sip:143@pbx>",
            "Call-ID abc, sorgente tls:15.161.143.250:5061");

        Assert.Contains("15.161.143.250:5061", righe[1]);
    }

    [Fact]
    public void UnaRigaSenzaRecapitiNonVieneToccata()
    {
        const string riga = "Codec di trasmissione negoziato: G729 a 8000 Hz";

        Assert.Equal(riga, Anonimizza(riga)[0]);
    }

    // -----------------------------------------------------------------------
    // La mappa
    // -----------------------------------------------------------------------

    [Fact]
    public void LaMappaCollegaLEtichettaAlRecapitoVero()
    {
        var anonimizzatore = new LogAnonymizer();
        anonimizzatore.Anonymize(["da <sip:+393331234567@pbx>"]);

        var mappa = anonimizzatore.BuildMap();

        Assert.Contains("ESTERNO-01 = 393331234567", mappa);

        // Chi la apre deve capire subito che non va inviata.
        Assert.Contains("NON VA INVIATO A NESSUNO", mappa);
    }

    [Fact]
    public void ConteggiDiCioCheEStatoSostituito()
    {
        var anonimizzatore = new LogAnonymizer();

        anonimizzatore.Anonymize([
            "From: \"Chiamate esterne:Mario Rossi\" <sip:+393331234567@pbx>",
            "da <sip:4021@pbx>",
        ]);

        Assert.Equal(2, anonimizzatore.ContactCount);
        Assert.Equal(1, anonimizzatore.NameCount);
    }
    [Fact]
    public void IContatoriDelPercorsoAudioRestanoLeggibili()
    {
        // Sono la riga con cui si distingue una chiamata muta da una che funziona, e
        // il 12 agosto 2026 l'anonimizzatore ne aveva mangiato un pezzo: «Inviati
        // INTERNO-247, ricevuti 1639», perche' quel giorno esisteva davvero un interno
        // 247. La diagnosi si e' dovuta appoggiare all'altro numero.
        var righe = Anonimizza(
            "From: <sip:247@aopr.ras.uccpbx.com>;tag=ABC",
            "Percorso audio chiuso. Inviati 247, ricevuti 1639, scartati 247, "
            + "svuotamenti buffer 0, silenzio inserito 247 ms");

        // Il recapito sparisce dove e' un recapito...
        Assert.DoesNotContain("247@", righe[0], StringComparison.Ordinal);

        // ...e resta dove e' un conteggio.
        Assert.Contains("Inviati 247", righe[1], StringComparison.Ordinal);
        Assert.Contains("scartati 247", righe[1], StringComparison.Ordinal);
        Assert.Contains("inserito 247 ms", righe[1], StringComparison.Ordinal);
    }
}
