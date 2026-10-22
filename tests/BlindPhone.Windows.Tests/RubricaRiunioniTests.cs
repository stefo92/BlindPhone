using BlindPhone.Contacts;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Le riunioni ricorrenti stanno in rubrica come contatti normali.
/// </summary>
/// <remarks>
/// <para>
/// È la via più economica alle riunioni, e forse la migliore: le riunioni che un
/// centralino serve davvero sono quasi sempre le stesse — la giunta del lunedì, il
/// consiglio, la commissione. Messe in rubrica riusano la ricerca, i preferiti, gli
/// annunci e la composizione rapida che esistono già, e costano zero interfaccia
/// nuova.
/// </para>
/// <para>
/// <b>L'ostacolo che queste prove tolgono.</b> L'importazione considera componibile
/// solo un valore fatto di cifre e priva di lettere — regola giusta, che tiene fuori
/// le annotazioni che le rubriche vere infilano nel campo del numero («breve 6583»,
/// «int. 4 – stanza 12»). Un indirizzo SIP ha delle lettere, quindi finiva fra le
/// note e non si poteva comporre.
/// </para>
/// <para>
/// La deroga è stretta apposta: se si allargasse, tornerebbero dentro proprio le
/// annotazioni che quella regola esiste per escludere. Metà di queste prove verifica
/// che <b>non</b> passino.
/// </para>
/// </remarks>
public sealed class RubricaRiunioniTests
{
    private static readonly string[] Colonne =
    [
        "_site_group_code", "_entry_number", "_inactive", "_surname", "_forename",
        "_initials", "_extn_num", "_alt_extn_num", "_hier1_name", "_location",
        "_user01", "_yesno01", "_modn_date",
    ];

    private static Dictionary<string, string?> Riga(string nome, string? numero, string? secondo = null)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["_site_group_code"] = "1",
            ["_entry_number"] = "1",
            ["_inactive"] = "0",
            ["_surname"] = nome,
            ["_extn_num"] = numero,
            ["_alt_extn_num"] = secondo,
            ["_yesno01"] = "0",
            ["_modn_date"] = "2026-09-23T10:00:00",
        };

    /// <summary>
    /// I numeri componibili della voce, oppure un elenco vuoto se la voce non esiste.
    /// </summary>
    /// <remarks>
    /// Una riga il cui unico recapito non è componibile <b>non produce un contatto
    /// affatto</b>: è la regola che già vale per le righe di intestazione, e va
    /// rispettata qui invece di pretendere una voce senza numeri.
    /// </remarks>
    private static IReadOnlyList<string> NumeriDi(string nome, string? valore)
    {
        var sorgente = new SorgenteFinta(CsvColumnMapping.Guess(Colonne), Riga(nome, valore));

        return sorgente.Leggi()
            .SelectMany(c => c.Numbers.Select(n => n.Number))
            .ToList();
    }

    [Fact]
    public void UnaRiunioneRicorrenteDiventaUnContattoComponibile()
    {
        var numeri = NumeriDi("Giunta settimanale", "88012345678.4321@zoomcrc.com");

        Assert.Contains("88012345678.4321@zoomcrc.com", numeri);
    }

    [Fact]
    public void UnInternoSuUnDominioRestaComponibile()
    {
        // Non e' un caso di Zoom: e' la forma che prende un interno quando la rubrica
        // dell'ente lo scrive per intero.
        Assert.Contains("201@pbx.ente.it", NumeriDi("Portineria", "201@pbx.ente.it"));
    }

    [Theory]
    [InlineData("breve 6583")]
    [InlineData("int. 4 - stanza 12")]
    [InlineData("vedi sotto")]
    [InlineData("mario.rossi@comune.it")]
    [InlineData("@zoomcrc.com")]
    [InlineData("812345678@")]
    [InlineData("812345678@zoomcrc")]
    public void QuelloCheNonEUnIndirizzoComponibileRestaFuori(string valore)
    {
        // Le prime tre sono annotazioni che le rubriche vere infilano nel campo del
        // numero. La quarta e' un indirizzo di posta, che in una colonna di numeri ci
        // finisce per errore e componendolo non porterebbe da nessuna parte. Le ultime
        // tre sono indirizzi monchi.
        Assert.DoesNotContain(valore, NumeriDi("Voce qualunque", valore));
    }

    [Fact]
    public void IlSecondoRecapitoScartatoNonSparisceMaVaNelleNote()
    {
        // E' la regola gia' stabilita per le annotazioni che le rubriche vere infilano
        // nel campo del secondo numero: quello che non si puo' comporre si conserva
        // comunque, perche' spesso e' l'unica informazione utile della riga.
        var sorgente = new SorgenteFinta(
            CsvColumnMapping.Guess(Colonne),
            Riga("Portineria", "4010", secondo: "breve 6583"));

        var contatto = Assert.Single(sorgente.Leggi());

        Assert.Contains("6583", contatto.Notes ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("breve 6583", contatto.Numbers.Select(n => n.Number));
    }

    [Fact]
    public void UnaVoceSenzaRecapitoComponibileNonDiventaUnContatto()
    {
        // Regola gia' in vigore per le righe di intestazione, e vale anche qui: un
        // contatto senza niente da comporre in rubrica e' rumore, non una voce.
        var sorgente = new SorgenteFinta(
            CsvColumnMapping.Guess(Colonne), Riga("Voce qualunque", "vedi sotto"));

        Assert.Empty(sorgente.Leggi());
    }
}
