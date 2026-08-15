using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica i campi di collocazione fisica del contatto.
/// </summary>
/// <remarks>
/// In una struttura complessa — un ospedale, un'azienda sanitaria, un ateneo — il
/// centralino riceve tanto la domanda "qual è il numero" quanto "dove si trova".
/// Padiglione e piano sono quindi informazione operativa, non decorazione, e vanno
/// pronunciati bene: una formula composta male costringe l'operatore a chiedere
/// conferma al chiamante, che è esattamente quello che il centralino deve evitare.
/// </remarks>
public sealed class ContactLocationTests
{
    private static Contact Build(string? building, string? floor) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Rossi Mario",
        Building = building,
        Floor = floor,
        Numbers = [new PhoneNumber("4021", PhoneNumberKind.Extension, true)],
    };

    [Fact]
    public void ConEntrambiICampiSiComponeLaFrasePiena()
    {
        Assert.Equal("padiglione B, piano 2", Build("B", "2").LocationSpeech);
    }

    [Fact]
    public void ConIlSoloPadiglioneNonSiNominaIlPiano()
    {
        // Una formula fissa con i pezzi vuoti verrebbe letta come "padiglione B,
        // piano" e farebbe pensare a un dato troncato.
        Assert.Equal("padiglione B", Build("B", null).LocationSpeech);
    }

    [Fact]
    public void ConIlSoloPianoSiDiceComunqueQuello()
    {
        // Meno utile che avere entrambi, ma piu' utile che tacere.
        Assert.Equal("piano 2", Build(null, "2").LocationSpeech);
    }

    /// <summary>
    /// Quando il dato porta già la parola, non la si ripete.
    /// </summary>
    /// <remarks>
    /// Dalla rubrica consegnata il 10 agosto 2026, dove padiglione e piano sono
    /// scritti per esteso: «Padiglione Barbieri», «Piano Secondo». Anteporre comunque
    /// l'etichetta produceva «padiglione Padiglione Barbieri, piano Piano Secondo» —
    /// a schermo si perdona, in ascolto è tempo perso due volte.
    /// </remarks>
    [Theory]
    [InlineData("Padiglione Barbieri", "Piano Secondo", "Padiglione Barbieri, Piano Secondo")]
    [InlineData("padiglione Barbieri", "piano 2", "padiglione Barbieri, piano 2")]
    [InlineData("B", "2", "padiglione B, piano 2")]
    [InlineData("PADIGLIONE C", "3", "PADIGLIONE C, piano 3")]
    public void LEtichettaNonSiRipeteSeIlDatoLaPortaGia(string? padiglione, string? piano, string atteso)
    {
        Assert.Equal(atteso, Build(padiglione, piano).LocationSpeech);
    }

    [Fact]
    public void UnaParolaCheCominciaComeLEtichettaRestaUnValore()
    {
        // "Pianoterra" non e' un piano gia' etichettato: senza il controllo del
        // separatore perderebbe la sua parola e si leggerebbe "Pianoterra" da solo,
        // che in un elenco di collocazioni non si capisce se sia un piano o altro.
        Assert.Equal("piano Pianoterra", Build(null, "Pianoterra").LocationSpeech);
    }

    [Fact]
    public void SenzaDatiDiCollocazioneNonSiInventaNulla()
    {
        Assert.Null(Build(null, null).LocationSpeech);
        Assert.Null(Build("  ", "").LocationSpeech);
    }

    [Fact]
    public void LaDescrizioneCompletaMetteLeCoseInOrdineDiUtilita()
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            DisplayName = "Rossi Mario",
            Category = "Medico",
            Department = "Cardiologia",
            Building = "B",
            Floor = "2",
            Organization = "Ospedale San Giovanni",
            RelatedTo = "Direzione sanitaria",
            Numbers = [new PhoneNumber("4021", PhoneNumberKind.Extension, true)],
        };

        var description = contact.FullSpeechDescription;

        // Chi ascolta a velocita' alta interrompe appena ha capito: l'ordine deve
        // essere chi, che cosa, dove, a chi fa capo.
        Assert.StartsWith("Rossi Mario, Medico, Cardiologia, padiglione B, piano 2", description);
        Assert.Contains("Ospedale San Giovanni", description);
        Assert.Contains("riferimento Direzione sanitaria", description);
    }

    [Fact]
    public void UnaVoceSenzaNomeUsaIlNumeroENonRestaMuta()
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "Reparto",
            Numbers = [new PhoneNumber("4055", PhoneNumberKind.Extension, true)],
        };

        // Una riga di elenco che lo screen reader legge come silenzio e' una voce
        // invisibile a chi non vede.
        Assert.False(string.IsNullOrWhiteSpace(contact.EffectiveDisplayName));
        Assert.Contains("4055", contact.FullSpeechDescription);
    }

    [Fact]
    public void IlNomeFoneticoVinceAncheNellaDescrizioneCompleta()
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ceccherini",
            PhoneticName = "Cekerini",
            Numbers = [new PhoneNumber("4021", PhoneNumberKind.Extension, true)],
        };

        Assert.StartsWith("Cekerini", contact.FullSpeechDescription);
    }
}
