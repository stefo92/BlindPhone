using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica il catalogo dei codec.
/// </summary>
/// <remarks>
/// La parte che conta non è la conversione dei nomi, ma il fatto che l'ordine
/// predefinito metta un codec a banda larga in testa: da quell'ordine dipende se in
/// collaudo si riesce a dimostrare il requisito di EN 301 549 §6.1 sulla chiamata
/// reale, che nel mercato della PA è contrattuale e non facoltativo.
/// </remarks>
public sealed class AudioCodecTests
{
    [Fact]
    public void LOrdinePredefinitoOffreIlG729PerPrimo()
    {
        // E' quello che i centralini di riferimento negoziano nella pratica.
        Assert.Equal(AudioCodec.G729, AudioCodecCatalog.DefaultPreference[0]);
    }

    [Fact]
    public void LOrdinePredefinitoConservaLaCapacitaDiBandaLarga()
    {
        // Questo test protegge la conformita' a EN 301 549 6.1, che richiede la
        // CAPACITA' di codificare in banda larga e non che sia negoziata per prima.
        // Riordinare l'elenco e' legittimo; rimuovere G.722 fa perdere la
        // conformita', ed e' il caso che il test deve intercettare.
        Assert.True(AudioCodecCatalog.IncludesWideband(AudioCodecCatalog.DefaultPreference));
    }

    [Fact]
    public void LOrdinePredefinitoNonIncludeLaVarianteAmericana()
    {
        // PCMU e' mu-law, la variante nordamericana di G.711. Offrirla in Europa
        // porta alcuni apparati a negoziarla senza alcun vantaggio.
        Assert.DoesNotContain(AudioCodec.PCMU, AudioCodecCatalog.DefaultPreference);
    }

    [Fact]
    public void LOrdinePredefinitoIncludeLaRiservaUniversale()
    {
        // Senza G.711 A-law alcune chiamate non verrebbero instaurate: e' il minimo
        // comune denominatore di qualunque apparato.
        Assert.Contains(AudioCodec.PCMA, AudioCodecCatalog.DefaultPreference);
    }

    [Fact]
    public void IlG729HaUnaDurataDiFrameDiversaDagliAltri()
    {
        // Il G.729 codifica blocchi da 10 millisecondi, gli altri lavorano campione
        // per campione. E' la ragione per cui il percorso audio allinea la
        // dimensione dei pacchetti e scarta i frame SID dell'Annex B, che altrimenti
        // il decodificatore renderebbe come rumore proprio durante i silenzi.
        var g729 = AudioCodecCatalog.Describe(AudioCodec.G729);

        Assert.Equal(8000, g729.SampleRateHz);
        Assert.Equal(80, g729.SampleRateHz / 100);
        Assert.True(g729.BitrateKbps < 16, "Il G.729 deve restare il codec a banda ridotta");
    }

    [Fact]
    public void IlCatalogoEuropeoContieneTreCodec()
    {
        var european = AudioCodecCatalog.European;

        Assert.Equal(3, european.Count);
        Assert.Contains(european, c => c.Codec == AudioCodec.G722);
        Assert.Contains(european, c => c.Codec == AudioCodec.PCMA);
        Assert.Contains(european, c => c.Codec == AudioCodec.G729);
    }

    [Theory]
    [InlineData("G722", AudioCodec.G722)]
    [InlineData("g.722", AudioCodec.G722)]
    [InlineData("PCMA", AudioCodec.PCMA)]
    [InlineData("G711A", AudioCodec.PCMA)]
    [InlineData("alaw", AudioCodec.PCMA)]
    [InlineData("G729", AudioCodec.G729)]
    [InlineData("g729a", AudioCodec.G729)]
    [InlineData("PCMU", AudioCodec.PCMU)]
    [InlineData("ulaw", AudioCodec.PCMU)]
    public void INomiSiRiconosconoNelleFormeInCuiVengonoScritti(string name, AudioCodec expected)
    {
        // Questi nomi vengono anche digitati a mano nel file di configurazione da un
        // amministratore, che puo' ragionevolmente scrivere "G.711A" invece di "PCMA".
        Assert.Equal(expected, AudioCodecCatalog.Parse(name));
    }

    [Theory]
    [InlineData("OPUS")]
    [InlineData("")]
    [InlineData("qualcosa")]
    public void UnNomeNonRiconosciutoNonProduceUnCodecInventato(string name)
    {
        Assert.Null(AudioCodecCatalog.Parse(name));
    }

    [Fact]
    public void UnElencoVuotoRicadeSullOrdinePredefinito()
    {
        // Un elenco vuoto renderebbe impossibile qualunque chiamata: meglio
        // ripartire dai valori consigliati che lasciare il telefono muto.
        Assert.Equal(AudioCodecCatalog.DefaultPreference, AudioCodecCatalog.ParseList([]));
        Assert.Equal(AudioCodecCatalog.DefaultPreference, AudioCodecCatalog.ParseList(["inesistente"]));
    }

    [Fact]
    public void LElencoConservaLOrdineEScartaIDuplicati()
    {
        var parsed = AudioCodecCatalog.ParseList(["G729", "PCMA", "g729", "G722"]);

        Assert.Equal([AudioCodec.G729, AudioCodec.PCMA, AudioCodec.G722], parsed);
    }

    [Fact]
    public void SoloIlG722SoddisfaIlRequisitoDiBandaLarga()
    {
        Assert.True(AudioCodecCatalog.Describe(AudioCodec.G722).IsWideband);
        Assert.False(AudioCodecCatalog.Describe(AudioCodec.PCMA).IsWideband);
        Assert.False(AudioCodecCatalog.Describe(AudioCodec.G729).IsWideband);
        Assert.False(AudioCodecCatalog.Describe(AudioCodec.PCMU).IsWideband);
    }

    [Fact]
    public void SenzaG722LaConformitaAllaBandaLargaSiPerde()
    {
        // E' la condizione che fa scattare l'avviso nel pannello impostazioni.
        Assert.True(AudioCodecCatalog.IncludesWideband([AudioCodec.G722, AudioCodec.PCMA]));
        Assert.False(AudioCodecCatalog.IncludesWideband([AudioCodec.PCMA, AudioCodec.G729]));
        Assert.False(AudioCodecCatalog.IncludesWideband([]));
    }

    [Fact]
    public void OgniCodecHaUnaDescrizioneUtilizzabileNellInterfaccia()
    {
        // Le descrizioni vengono lette a voce: un campo vuoto produrrebbe una voce
        // di elenco che lo screen reader legge come silenzio.
        foreach (var info in AudioCodecCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(info.Label));
            Assert.False(string.IsNullOrWhiteSpace(info.Description));
            Assert.False(string.IsNullOrWhiteSpace(info.ShortName));
            Assert.True(info.SampleRateHz > 0);
            Assert.True(info.BitrateKbps > 0);
        }
    }
}
