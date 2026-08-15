using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica la regola che decide se una conversazione può viaggiare in chiaro.
/// </summary>
/// <remarks>
/// È separata dal codice che apre le chiamate per la stessa ragione per cui lo è la
/// regola che instrada la voce: i suoi modi di sbagliare non si vedono leggendo il
/// codice. Una politica «obbligatoria» che ripiega in chiaro produce una
/// conversazione che sembra protetta e non lo è, e non c'è nulla, sul posto operatore,
/// che lo riveli.
/// </remarks>
public sealed class SrtpNegotiationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConLaCifraturaDisattivataSiVaSempreInChiaro(bool controparteCifra)
    {
        Assert.Equal(SrtpOutcome.Clear, SrtpNegotiation.Decide(SrtpPolicy.Disabled, controparteCifra));
    }

    [Fact]
    public void ConLaCifraturaPreferitaSiCifraDovePossibile()
    {
        Assert.Equal(SrtpOutcome.Secure, SrtpNegotiation.Decide(SrtpPolicy.Preferred, true));
    }

    [Fact]
    public void ConLaCifraturaPreferitaSiRipiegaInChiaroInveceDiPerdereLaChiamata()
    {
        Assert.Equal(SrtpOutcome.Clear, SrtpNegotiation.Decide(SrtpPolicy.Preferred, false));
    }

    [Fact]
    public void ConLaCifraturaObbligatoriaSenzaControparteLaChiamataNonSiFa()
    {
        // La chiamata che fallisce E' il comportamento voluto: dove questa politica e'
        // scelta, una conversazione in chiaro e' un incidente peggiore di una
        // telefonata non fatta.
        Assert.Equal(SrtpOutcome.Reject, SrtpNegotiation.Decide(SrtpPolicy.Required, false));
    }

    [Fact]
    public void LaCifraturaObbligatoriaNonRipiegaMaiInChiaro()
    {
        // La prova che vale piu' di tutte: nessun ingresso deve poter produrre Clear
        // con politica Required. E' l'unico modo in cui questa impostazione potrebbe
        // mentire, e mentirebbe in silenzio.
        foreach (var controparte in new[] { true, false })
        {
            Assert.NotEqual(SrtpOutcome.Clear, SrtpNegotiation.Decide(SrtpPolicy.Required, controparte));
        }
    }

    [Theory]
    [InlineData(SrtpPolicy.Disabled, false)]
    [InlineData(SrtpPolicy.Preferred, true)]
    [InlineData(SrtpPolicy.Required, true)]
    public void SoloLePoliticheCheVoglionoLaCifraturaLaPropongono(SrtpPolicy policy, bool atteso)
    {
        Assert.Equal(atteso, SrtpNegotiation.ShouldOffer(policy));
    }

    // -----------------------------------------------------------------------
    // Quando un rifiuto merita un secondo tentativo
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(488)]   // Not Acceptable Here: il rifiuto previsto dalla RFC 3261
    [InlineData(606)]   // Not Acceptable: la variante globale
    [InlineData(415)]   // Unsupported Media Type: alcuni centralini piu' vecchi
    public void IRifiutiSullaDescrizioneDiSessioneMeritanoUnSecondoTentativo(int stato)
    {
        Assert.True(SrtpNegotiation.LooksLikeMediaRejection(stato));
    }

    [Theory]
    [InlineData(486)]   // occupato
    [InlineData(404)]   // non esiste
    [InlineData(603)]   // rifiutata dalla persona
    [InlineData(480)]   // temporaneamente non disponibile
    [InlineData(200)]
    [InlineData(0)]
    public void GliAltriRifiutiNonCEntranoConLaCifratura(int stato)
    {
        // Ritentare su questi significherebbe comporre due volte un numero che ha
        // appena rifiutato: il chiamato sentirebbe squillare di nuovo.
        Assert.False(SrtpNegotiation.LooksLikeMediaRejection(stato));
    }

    // -----------------------------------------------------------------------
    // Che cosa sente l'operatore
    // -----------------------------------------------------------------------

    [Fact]
    public void IlRipiegoInChiaroVieneDetto()
    {
        // Una protezione che si spegne in silenzio lascia credere di averla, ed e' la
        // peggiore delle tre situazioni possibili: peggio di non averla e saperlo.
        var testo = AnnouncementTexts.SrtpDowngraded("tre, quattro, nove");

        Assert.Contains("Attenzione", testo, StringComparison.Ordinal);
        Assert.Contains("in chiaro", testo, StringComparison.Ordinal);
        Assert.Contains("tre, quattro, nove", testo, StringComparison.Ordinal);
    }
}
