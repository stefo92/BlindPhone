using BlindPhone.Contacts;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Come un numero scritto a mano in una directory o in Outlook diventa un numero
/// componibile.
/// </summary>
/// <remarks>
/// <para>
/// Il numero scelto in rubrica arriva al motore telefonico così com'è e finisce
/// nell'indirizzo SIP della chiamata: uno spazio o una parentesi rimasti dentro lo
/// rendono malformato, e il centralino rifiuta la chiamata mentre il chiamante è in
/// attesa. L'errore opposto — fondere due numeri in uno — compone un numero che non
/// esiste. Queste prove coprono entrambi.
/// </para>
/// </remarks>
public sealed class NumeriDiDirectoryTests
{
    [Theory]
    [InlineData("+39 0521 703111", "+390521703111")]
    [InlineData("(0521) 703-111", "0521703111")]
    [InlineData("0521/703111", "0521703111")]
    [InlineData("0521.703.111", "0521703111")]
    [InlineData("+39 (0521) 703111", "+390521703111")]
    [InlineData("tel:+39-0521-703111", "+390521703111")]
    [InlineData("4021", "4021")]
    [InlineData("  4021  ", "4021")]
    [InlineData("0521 703111", "0521703111")]
    public void ISeparatoriVisiviSiTolgono(string scritto, string atteso)
    {
        var letto = DirectoryNumber.Parse(scritto);

        Assert.NotNull(letto);
        Assert.Equal(atteso, letto.Number);
        Assert.Null(letto.Extension);
    }

    [Fact]
    public void LoZeroTraParentesiResta_InItalia()
    {
        // In Italia lo zero fa parte del numero anche dall'estero: toglierlo
        // porterebbe la chiamata da un'altra parte.
        Assert.Equal("+390521703111", DirectoryNumber.Parse("+39 (0)521 703111")!.Number);
        Assert.Equal("+3780549123456", DirectoryNumber.Parse("+378 (0)549 123456")!.Number);
    }

    [Fact]
    public void LoZeroTraParentesiSiToglie_AllEstero()
    {
        // «(0)» e' la convenzione con cui si scrive lo zero da non comporre
        // dall'estero: Londra e' +44 20, non +44 020.
        Assert.Equal("+442079460000", DirectoryNumber.Parse("+44 (0)20 7946 0000")!.Number);
    }

    [Theory]
    [InlineData("+39 0521 703111 int. 4021", "+390521703111", "4021")]
    [InlineData("0521 703111 interno 4021", "0521703111", "4021")]
    [InlineData("+1 555 123 4567 x123", "+15551234567", "123")]
    [InlineData("+1 555 123 4567 ext. 123", "+15551234567", "123")]
    [InlineData("tel:+390521703111;ext=4021", "+390521703111", "4021")]
    public void LInternoInCodaSiRiconosce(string scritto, string numero, string interno)
    {
        var letto = DirectoryNumber.Parse(scritto);

        Assert.NotNull(letto);
        Assert.Equal(numero, letto.Number);
        Assert.Equal(interno, letto.Extension);
    }

    [Theory]
    [InlineData("Int. 4021")]
    [InlineData("interno 4021")]
    [InlineData("ext 4021")]
    public void UnInternoScrittoDaSoloNonSiPerde(string scritto)
    {
        // E' il modo in cui molte directory di ente scrivono chi ha soltanto
        // l'interno: rifiutarlo per le lettere toglierebbe a quella persona l'unico
        // recapito.
        var letto = DirectoryNumber.Parse(scritto);

        Assert.NotNull(letto);
        Assert.Null(letto.Number);
        Assert.Equal("4021", letto.Extension);
    }

    [Theory]
    [InlineData("chiedere in segreteria")]
    [InlineData("0521 703111 / 703112")]
    [InlineData("0521703111, 0521703112")]
    [InlineData("2876   2877")]
    [InlineData("vedi sito")]
    [InlineData("0521 703111 fax")]
    [InlineData("---")]
    public void UnCampoCheNonEUnNumeroNonSiCompone(string scritto)
    {
        // Due numeri fusi in uno ne comporrebbero un terzo che non esiste; una nota
        // non e' una destinazione. Restano leggibili fra le note del contatto.
        Assert.Null(DirectoryNumber.Parse(scritto));
        Assert.True(DirectoryNumber.IsDiscarded(scritto));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnCampoVuotoNonEUnoScarto(string? scritto)
    {
        // Le note fatte di niente sono rumore: un campo vuoto si tace.
        Assert.Null(DirectoryNumber.Parse(scritto));
        Assert.False(DirectoryNumber.IsDiscarded(scritto));
    }

    [Fact]
    public void IlPiuVaSoloInTesta()
    {
        Assert.Null(DirectoryNumber.Parse("0521+703111"));
    }

    [Fact]
    public void LeLettereDiUnaParolaNonSonoUnInterno()
    {
        // La «x» di «fax» e l'«int» di una parola qualsiasi non devono staccare un
        // pezzo del numero e chiamarlo interno.
        Assert.Null(DirectoryNumber.Parse("0521 703111 interessato"));
    }
}
