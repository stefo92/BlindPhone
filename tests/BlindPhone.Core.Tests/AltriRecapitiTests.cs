using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// I recapiti di una voce oltre al numero predefinito.
/// </summary>
/// <remarks>
/// Il difetto segnalato: importando l'elenco dei numeri brevi e abbinando la colonna
/// «Telefono» al secondo numero, il numero per esteso non compariva nella rubrica. Era
/// in archivio, ma tabella, scheda e anteprima mostravano soltanto il predefinito. La
/// regola su che cosa sia «gli altri recapiti» sta qui, perché tutte e tre la usano.
/// </remarks>
public sealed class AltriRecapitiTests
{
    [Fact]
    public void IlNumeroPerEstesoDiUnNumeroBreveEUnAltroRecapito()
    {
        var voce = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "MDG MEDICINA INTERNA DIP.EMER/UR",
            Kind = ContactKind.ShortCode,
            Numbers =
            [
                new PhoneNumber("6000", PhoneNumberKind.Extension, true),
                new PhoneNumber("03665635293", PhoneNumberKind.Work, false),
            ],
        };

        Assert.Equal("6000", voce.PrimaryNumber);
        Assert.Equal(["03665635293"], voce.OtherNumbers);
    }

    [Fact]
    public void IlPredefinitoNonCompareFraGliAltriAncheSeNonEIlPrimo()
    {
        // Il predefinito e' quello contrassegnato, non il primo dell'elenco: un
        // cellulare primario lascia l'interno fra gli altri recapiti.
        var voce = new Contact
        {
            Id = Guid.NewGuid(),
            Numbers =
            [
                new PhoneNumber("4021", PhoneNumberKind.Extension, false),
                new PhoneNumber("3491234567", PhoneNumberKind.Mobile, true),
            ],
        };

        Assert.Equal(["4021"], voce.OtherNumbers);
    }

    [Fact]
    public void LoStessoNumeroInDueColonneNonSiRipete()
    {
        // Abbinare la stessa colonna a «Numero» e a «Secondo numero» non produce un
        // secondo recapito: dirlo farebbe credere che ce ne siano due.
        var voce = new Contact
        {
            Id = Guid.NewGuid(),
            Numbers =
            [
                new PhoneNumber("2094", PhoneNumberKind.Extension, true),
                new PhoneNumber("2094", PhoneNumberKind.Work, false),
                new PhoneNumber("0376721", PhoneNumberKind.Work, false),
                new PhoneNumber("0376721", PhoneNumberKind.Mobile, false),
            ],
        };

        Assert.Equal(["0376721"], voce.OtherNumbers);
    }

    [Fact]
    public void UnaVoceConUnSoloNumeroNonHaAltriRecapiti()
    {
        var voce = new Contact { Id = Guid.NewGuid(), Numbers = [new PhoneNumber("2095", PhoneNumberKind.Extension, true)] };

        Assert.Empty(voce.OtherNumbers);
    }
}
