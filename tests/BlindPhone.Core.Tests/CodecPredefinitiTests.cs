using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica l'ordine di preferenza predefinito dei codec e la banda larga.
/// </summary>
/// <remarks>
/// <para>
/// EN 301 549 §6.1 chiede che l'apparato <b>sia in grado</b> di codificare e
/// decodificare la banda larga, non che la negozi per prima. Finché G.722 è offerto
/// la conformità è soddisfatta e dimostrabile: toglierlo dall'elenco la farebbe
/// perdere, ed è il motivo per cui questo collaudo esiste.
/// </para>
/// <para>
/// Il pulsante che ripristina l'ordine consigliato annunciava un elenco scritto a
/// mano che non corrispondeva a quello caricato. Adesso l'annuncio si compone dal
/// catalogo: qui si verifica che il catalogo dica quello che il pannello ripete.
/// </para>
/// </remarks>
public sealed class CodecPredefinitiTests
{
    [Fact]
    public void LaBandaLargaEsempreOfferta()
    {
        // Il requisito di conformità: G.722 deve stare nell'elenco predefinito.
        Assert.Contains(AudioCodec.G722, AudioCodecCatalog.DefaultPreference);
    }

    [Fact]
    public void CEsempreUnaRiservaUniversale()
    {
        // Se la negoziazione fallisce con gli altri, G.711 funziona con qualunque
        // apparato: senza, una postazione puo' restare senza audio.
        Assert.Contains(AudioCodec.PCMA, AudioCodecCatalog.DefaultPreference);
    }

    [Fact]
    public void OgniCodecPredefinitoHaUnEtichettaPronunciabile()
    {
        // L'annuncio del pulsante di ripristino si compone da queste etichette: se
        // una fosse vuota, l'operatore sentirebbe un elenco monco.
        Assert.All(AudioCodecCatalog.DefaultPreference, codec =>
        {
            var etichetta = AudioCodecCatalog.Describe(codec).Label;

            Assert.False(string.IsNullOrWhiteSpace(etichetta));
            Assert.DoesNotContain(",", etichetta, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ILaSuccessioneDichiarataEQuellaCaricata()
    {
        // La prova che il difetto non torni: l'elenco annunciato dal pannello si
        // costruisce da questa stessa successione, quindi basta verificare che sia
        // esattamente quella documentata.
        Assert.Equal(
            [AudioCodec.G729, AudioCodec.G722, AudioCodec.PCMA],
            AudioCodecCatalog.DefaultPreference);
    }
}
