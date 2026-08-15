using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sull'impaginazione del testo della guida.
/// </summary>
/// <remarks>
/// Per chi legge con la sintesi la riga è l'unità di lettura, e per chi legge in
/// braille la larghezza è un vincolo fisico: un display comune ha quaranta celle. Una
/// riga troppo lunga costringe a farla scorrere, una riga che finisce a metà concetto
/// costringe a ricostruire la frase a memoria.
/// </remarks>
public sealed class HelpTextLayoutTests
{
    [Fact]
    public void NessunaRigaSuperaLaLarghezzaRichiesta()
    {
        var testo = HelpTextLayout.Wrap(
            "Questa è una frase molto lunga che deve essere spezzata in piu' righe perche' "
            + "altrimenti non entrerebbe ne' su un display braille ne' in una lettura riga per riga.",
            width: 40);

        Assert.All(testo.Split(Environment.NewLine), riga => Assert.True(riga.Length <= 40, riga));
    }

    [Fact]
    public void LeVociDiElencoMantengonoIlTrattinoEIlRientro()
    {
        var testo = HelpTextLayout.Wrap(
            "- Rispondi alla chiamata in arrivo premendo la combinazione assegnata al comando",
            width: 40);

        var righe = testo.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("- ", righe[0], StringComparison.Ordinal);
        Assert.True(righe.Length > 1, "La riga di prova doveva essere spezzata");

        // Il rientro e' cio' che rende riconoscibile, all'ascolto, che la riga
        // successiva appartiene ancora alla stessa voce e non e' la prossima.
        Assert.All(righe.Skip(1), riga => Assert.StartsWith("  ", riga, StringComparison.Ordinal));
    }

    [Fact]
    public void GliElenchiNumeratiRientranoDiTreSpazi()
    {
        var testo = HelpTextLayout.Wrap(
            "1. Premi il tasto del trasferimento e attendi che il sistema chieda la destinazione",
            width: 40);

        var righe = testo.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("1. ", righe[0], StringComparison.Ordinal);
        Assert.All(righe.Skip(1), riga => Assert.StartsWith("   ", riga, StringComparison.Ordinal));
    }

    [Fact]
    public void LeRigheVuoteRestanoDoveSono()
    {
        var testo = HelpTextLayout.Wrap("Primo paragrafo.\n\nSecondo paragrafo.", width: 40);
        var righe = testo.Split(Environment.NewLine);

        Assert.Equal("Primo paragrafo.", righe[0]);
        Assert.Equal(string.Empty, righe[1]);
        Assert.Equal("Secondo paragrafo.", righe[2]);
    }

    [Fact]
    public void UnaParolaPiuLungaDellaRigaNonVieneSpezzata()
    {
        // Un percorso o un indirizzo va trascritto con esattezza: spezzarlo
        // renderebbe illeggibile proprio l'informazione che serve.
        const string percorso = @"C:\Users\operatore\AppData\Roaming\BlindPhone\log\blindphone.log";

        var testo = HelpTextLayout.Wrap($"Il registro sta in {percorso} e va allegato.", width: 30);

        Assert.Contains(percorso, testo, StringComparison.Ordinal);
    }

    [Fact]
    public void UnTestoVuotoNonProduceNulla()
        => Assert.Equal(string.Empty, HelpTextLayout.Wrap(string.Empty));
}
