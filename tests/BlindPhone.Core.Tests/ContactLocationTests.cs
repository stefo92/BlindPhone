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
    public void UnaVoceSenzaNomeSiChiamaComeLaSuaDescrizione()
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "Ambulatorio Diabetologico",
            Numbers = [new PhoneNumber("4055", PhoneNumberKind.Extension, true)],
        };

        // In una rubrica ospedaliera le voci senza persona sono la maggioranza, e sono
        // quelle che l'operatore cerca piu' spesso. Prima si chiamavano come il proprio
        // numero, e un elenco di risultati si leggeva "2033, 2091, 2095".
        Assert.Equal("Ambulatorio Diabetologico", contact.EffectiveDisplayName);

        // E la descrizione non si sente due volte.
        Assert.Equal("Ambulatorio Diabetologico", contact.FullSpeechDescription);
    }

    [Fact]
    public void SenzaNomeESenzaDescrizioneRestaIlNumero()
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            Numbers = [new PhoneNumber("4055", PhoneNumberKind.Extension, true)],
        };

        // Una riga di elenco che lo screen reader legge come silenzio e' una voce
        // invisibile a chi non vede: il numero e' l'ultima risorsa, non la prima.
        Assert.Equal("4055", contact.EffectiveDisplayName);
        Assert.Contains("4055", contact.FullSpeechDescription);
    }

    /// <summary>
    /// Il tipo di voce si sente prima del nome, e solo quando cambia una decisione.
    /// </summary>
    /// <remarks>
    /// È l'unico elemento che può far decidere di <b>non</b> comporre: sentirlo dopo il
    /// nome e la descrizione significa averlo saputo quando la mano è già sul
    /// tastierino.
    /// </remarks>
    [Theory]
    [InlineData(ContactKind.Fax, "fax, Accettazione")]
    [InlineData(ContactKind.Emergency, "emergenza, Accettazione")]
    [InlineData(ContactKind.ShortCode, "numero breve, Accettazione")]
    [InlineData(ContactKind.Person, "Accettazione")]
    [InlineData(ContactKind.Service, "Accettazione")]
    [InlineData(ContactKind.Unknown, "Accettazione")]
    public void IlTipoDiVoceParlaSoloQuandoCambiaUnaDecisione(ContactKind tipo, string atteso)
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "Accettazione",
            Kind = tipo,
            Numbers = [new PhoneNumber("2678", PhoneNumberKind.Extension, true)],
        };

        Assert.Equal(atteso, contact.FullSpeechDescription);
    }

    /// <summary>
    /// Il tipo tace quando il nominativo lo dice già.
    /// </summary>
    /// <remarks>
    /// Sui fax accade quasi sempre, e per costruzione: una voce è riconosciuta come fax
    /// perché la descrizione comincia con quella parola, e sulle voci senza persona la
    /// descrizione è anche il nominativo. Senza questa regola la riga si leggeva «fax,
    /// Fax Accettazione DH», cioè la stessa parola due volte di fila.
    /// </remarks>
    [Fact]
    public void UnFaxCheSiChiamaGiaFaxNonLoRipete()
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "Fax Accettazione DH",
            Kind = ContactKind.Fax,
            Numbers = [new PhoneNumber("2678", PhoneNumberKind.Extension, true)],
        };

        Assert.Null(contact.KindNotice);
        Assert.Equal("Fax Accettazione DH", contact.FullSpeechDescription);

        // La forma grezza resta, ed e' quella che rende cercabile "fax".
        Assert.Equal("fax", contact.KindSpeech);
    }

    [Fact]
    public void IlFaxIntestatoAUnaPersonaSiAnnunciaComeFax()
    {
        // Qui il nominativo e' il cognome, e nient'altro direbbe che quel numero e' un
        // apparecchio: e' il caso in cui l'avvertimento informa davvero.
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            FirstName = "Maria",
            LastName = "Rossi",
            Category = "Fax  Resp. s.s.d.",
            Kind = ContactKind.Fax,
            Numbers = [new PhoneNumber("2495", PhoneNumberKind.Extension, true)],
        };

        Assert.Equal("fax", contact.KindNotice);
        Assert.StartsWith("fax, Maria Rossi", contact.FullSpeechDescription);
    }

    [Fact]
    public void UnNumeroBreveSiAnnunciaSempre()
    {
        // Viene da un contrassegno della sorgente e non da una parola: "MILANO
        // POLICLINICO" non lascia intuire che sia un codice abbreviato.
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "MILANO POLICLINICO",
            Kind = ContactKind.ShortCode,
            Numbers = [new PhoneNumber("6019", PhoneNumberKind.Extension, true)],
        };

        Assert.Equal("numero breve", contact.KindNotice);
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
