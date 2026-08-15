using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica ciò che finisce nelle celle delle tabelle e nelle proposte del campo
/// di composizione.
/// </summary>
/// <remarks>
/// <para>
/// Quando i risultati sono una tabella, lo screen reader legge il contenuto di una
/// cella alla volta e lo prende dalla proprietà <c>Name</c> di UI Automation, senza
/// passare dalla voce interna dell'applicazione. È l'unico punto in cui il testo
/// pronunciato non attraversa la pipeline di annuncio: se è sbagliato qui, nessun
/// altro strato lo corregge.
/// </para>
/// </remarks>
public sealed class TableReadingTests
{
    [Fact]
    public void UnInternoSiLeggeCifraPerCifraAncheNelleCelle()
    {
        // Senza spaziatura la sintesi direbbe "quattromilaventuno", che non si puo'
        // ne' trascrivere ne' ricomporre.
        Assert.Equal("4 0 2 1", SpeechFormatter.SpacedDigits("4021"));
    }

    [Fact]
    public void LaSpaziaturaTieneIlNumeroCortoAncheInBraille()
    {
        // Il criterio: tanti caratteri quante cifre, piu' gli spazi. La resa a
        // parole ("quattro zero due uno") occuperebbe mezza riga braille per un
        // solo interno.
        var spaced = SpeechFormatter.SpacedDigits("4021");

        Assert.Equal(7, spaced.Length);
        Assert.True(spaced.Length < SpeechFormatter.Digits("4021").Length);
    }

    [Fact]
    public void ISeparatoriDiFormattazioneNonVengonoLetti()
    {
        // Punti, spazi e trattini sono decorazione tipografica: pronunciarli
        // allungherebbe la lettura senza aggiungere nulla.
        Assert.Equal("0 2 1 2 3 4 5 6 7 8", SpeechFormatter.SpacedDigits("02-1234.5678"));
    }

    [Fact]
    public void IPrefissiEISimboliDiComposizioneRestano()
    {
        // Il piu' iniziale e i simboli del tastierino fanno parte del numero: senza,
        // il numero letto non e' quello da comporre.
        Assert.Equal("+ 3 9 0 2 1 2 3", SpeechFormatter.SpacedDigits("+390 2123"));
        Assert.Equal("* 2 1 #", SpeechFormatter.SpacedDigits("*21#"));
    }

    [Fact]
    public void UnaCellaSenzaNumeroNonRestaMuta()
    {
        Assert.Equal(string.Empty, SpeechFormatter.SpacedDigits(null));
        Assert.Equal(string.Empty, SpeechFormatter.SpacedDigits("   "));

        // Un valore senza cifre non e' un numero, ma restituire una stringa vuota
        // produrrebbe una cella che lo screen reader legge come silenzio.
        Assert.Equal("interno", SpeechFormatter.SpacedDigits("interno"));
    }

    [Fact]
    public void LInternoVinceSulRecapitoPrimarioQuandoSiCercaUnNome()
    {
        // Il caso che la distinzione serve a coprire: un contatto il cui recapito
        // primario e' il cellulare. Passare li' una chiamata dal posto operatore
        // manderebbe il chiamante altrove senza che nessuno se ne accorga.
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            DisplayName = "Rossi Mario",
            Numbers =
            [
                new PhoneNumber("3401234567", PhoneNumberKind.Mobile, IsPrimary: true),
                new PhoneNumber("4021", PhoneNumberKind.Extension),
            ],
        };

        Assert.Equal("3401234567", contact.PrimaryNumber);
        Assert.Equal("4021", contact.ExtensionNumber);
    }

    [Fact]
    public void SenzaInternoNonSiInventaUnNumero()
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            DisplayName = "Bianchi Anna",
            Numbers = [new PhoneNumber("3401234567", PhoneNumberKind.Mobile, IsPrimary: true)],
        };

        // Chi propone il contatto ricade sul recapito primario, ma deve poterlo
        // decidere sapendo che un interno non c'e'.
        Assert.Null(contact.ExtensionNumber);
        Assert.Equal("3401234567", contact.PrimaryNumber);
    }
}
