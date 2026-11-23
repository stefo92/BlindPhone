using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// I trattini con cui le sorgenti riempiono i campi vuoti non fanno un nome.
/// </summary>
/// <remarks>
/// La rubrica del centralino scrive «-» in nome e cognome degli interni non assegnati.
/// La voce si chiamava «- -»: nell'elenco una riga che lo screen reader legge come
/// silenzio, e negli annunci «trasferito a - -».
/// </remarks>
public sealed class SegnapostoInRubricaTests
{
    private static Contact Voce(
        string? nome = null,
        string? cognome = null,
        string? nominativo = null,
        string? categoria = null,
        string? fonetico = null)
        => new()
        {
            Id = Guid.NewGuid(),
            FirstName = nome,
            LastName = cognome,
            DisplayName = nominativo,
            Category = categoria,
            PhoneticName = fonetico,
            Numbers = [new PhoneNumber("3638", PhoneNumberKind.Extension)],
        };

    [Theory]
    [InlineData("-", "-")]
    [InlineData("-", null)]
    [InlineData(".", " ")]
    [InlineData("--", "- -")]
    public void NomeECognomeSoloTrattini_LaVoceNonHaNome(string? nome, string? cognome)
    {
        var voce = Voce(nome, cognome);

        Assert.Null(voce.NameOrDescription);
        Assert.Equal("3638", voce.EffectiveDisplayName);
    }

    [Fact]
    public void NominativoSoloTrattini_SiPassaANomeECognome()
    {
        var voce = Voce("Mario", "Rossi", nominativo: "- -");

        Assert.Equal("Mario Rossi", voce.NameOrDescription);
    }

    [Fact]
    public void UnCampoConTrattiniEUnoVero_RestaQuelloVero()
    {
        var voce = Voce("-", "Rossi");

        Assert.Equal("Rossi", voce.NameOrDescription);
    }

    [Fact]
    public void TrattiniInNomeECognome_SiRicadeSullaDescrizione()
    {
        var voce = Voce("-", "-", categoria: "Ambulatorio di cardiologia");

        Assert.Equal("Ambulatorio di cardiologia", voce.NameOrDescription);
    }

    [Fact]
    public void DescrizioneSoloTrattini_NonEUnNome()
    {
        var voce = Voce("-", "-", categoria: "-");

        Assert.Null(voce.NameOrDescription);
    }

    [Fact]
    public void UnNomeConTrattiniDentro_RestaIntero()
    {
        // I trattini dentro un nome vero restano: si scarta solo il campo che non
        // ha nient'altro.
        var voce = Voce(nominativo: "AZIENDA USL - Centralino");

        Assert.Equal("AZIENDA USL - Centralino", voce.NameOrDescription);
    }

    [Fact]
    public void ResaFoneticaSoloTrattini_SiPronunciaIlNome()
    {
        var voce = Voce("Mario", "Rossi", fonetico: "-");

        Assert.Equal("Mario Rossi", voce.SpeechName);
    }

    [Fact]
    public void ControparteConResaFoneticaSoloTrattini_SiPronunciaIlNumero()
    {
        var parte = new CallParty("3638", DisplayName: "- -", PhoneticName: "-");

        Assert.Equal("3638", parte.SpeechName);
        Assert.False(parte.HasName);
    }
}
