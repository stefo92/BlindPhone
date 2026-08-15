using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica la pulizia del numero presentato in uscita.
/// </summary>
/// <remarks>
/// Sembra una formattazione e non lo è: un carattere di troppo dentro l'URI di
/// un'intestazione SIP la rende malformata, e il centralino rifiuta la chiamata invece
/// di farla partire. Il guasto si manifesterebbe come «tutte le chiamate uscenti non
/// funzionano più», senza nulla che lo colleghi a un punto scritto settimane prima in
/// un campo di configurazione.
/// </remarks>
public sealed class CallerIdentityNormalizationTests
{
    [Theory]
    [InlineData("0521 123456", "0521123456")]
    [InlineData("0521.123.456", "0521123456")]
    [InlineData("(0521) 123-456", "0521123456")]
    [InlineData("0521/123456", "0521123456")]
    [InlineData("  0521123456  ", "0521123456")]
    public void SpaziEPunteggiaturaVengonoTolti(string scritto, string atteso)
    {
        Assert.Equal(atteso, CallerIdentity.Normalize(scritto));
    }

    [Fact]
    public void IlPiuInizialeSiConserva()
    {
        // Distingue la forma internazionale da quella locale: togliendolo cambierebbe
        // la destinazione a cui arrivano le richiamate.
        Assert.Equal("+390521123456", CallerIdentity.Normalize("+39 0521 123456"));
    }

    [Fact]
    public void UnPiuInMezzoNonDiventaUnPrefisso()
    {
        Assert.Equal("0521123456", CallerIdentity.Normalize("0521+123456"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("centralino")]
    [InlineData("+")]
    public void QuandoNonRestaNessunaCifraIlRisultatoEVuoto(string? scritto)
    {
        // Vuoto significa "non alterare l'identita'": e' il ripiego giusto, perche' un
        // URI costruito su un numero inesistente farebbe fallire ogni chiamata.
        Assert.Equal(string.Empty, CallerIdentity.Normalize(scritto));
    }
}
