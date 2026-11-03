using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica i campi della rubrica mostrabili nella ricerca dal campo di composizione.
/// </summary>
/// <remarks>
/// Quali campi compaiono lo decide la rubrica, cioè l'abbinamento delle colonne fatto
/// all'importazione: un campo che nessuna voce porta sarebbe una colonna sempre
/// vuota, e ogni colonna in più è una freccia destra in più per chi legge la riga con
/// lo screen reader.
/// </remarks>
public sealed class CampiRicercaRapidaTests
{
    private static Contact Contatto() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Rossi Mario",
        Category = "Cardiologia ambulatorio",
        Department = "Medicina",
        Building = "Barbieri",
        Floor = "2",
        RelatedTo = "4000",
    };

    [Fact]
    public void IlNominativoEIlNumeroNonSonoScelteELiberi()
    {
        // Una riga senza nome non si riconosce e una senza numero non si chiama:
        // stanno sempre, e infatti nel catalogo delle cose scegliibili non ci sono.
        Assert.DoesNotContain(DialerFieldCatalog.All, c => c.Id is "Name" or "Number");
    }

    [Fact]
    public void ConLaRubricaVuotaRestaLoStato()
    {
        // Lo stato non viene dalla rubrica ma dal centralino: nessun abbinamento di
        // colonne lo puo' togliere, e senza voci e' l'unico campo che ha senso.
        Assert.Equal(["Presence"], DialerFieldCatalog.Present([]).Select(c => c.Id));
    }

    [Fact]
    public void CompaionoSoloICampiImportati()
    {
        // Una rubrica in cui all'importazione si sono abbinati solo nome e reparto:
        // descrizione, collocazione e riferimento non ci sono, e le loro colonne
        // sarebbero sempre vuote.
        var rubrica = new[]
        {
            new Contact { Id = Guid.NewGuid(), LastName = "Rossi", Department = "Medicina" },
            new Contact { Id = Guid.NewGuid(), LastName = "Bianchi", Department = "Chirurgia" },
        };

        Assert.Equal(["Presence", "Department"], DialerFieldCatalog.Present(rubrica).Select(c => c.Id));
    }

    [Fact]
    public void BastaUnaVocePerFarComparireIlCampo()
    {
        // Il reparto lo hanno solo i pochi che servono: una soglia lo farebbe sparire
        // proprio da quella rubrica, senza un motivo che si possa dire a chi ascolta.
        var rubrica = new[]
        {
            new Contact { Id = Guid.NewGuid(), LastName = "Rossi" },
            new Contact { Id = Guid.NewGuid(), LastName = "Verdi" },
            new Contact { Id = Guid.NewGuid(), LastName = "Bianchi", Department = "Chirurgia" },
        };

        Assert.Contains(DialerFieldCatalog.Present(rubrica), c => c.Id == "Department");
    }

    [Fact]
    public void LOrdineEQuelloDelCatalogoQualunqueSiaLaRubrica()
    {
        // Prima cio' che decide, poi cio' che aiuta a riconoscere: le frecce destra
        // da contare non devono dipendere da quale voce e' stata letta per prima.
        var rubrica = new[]
        {
            new Contact { Id = Guid.NewGuid(), LastName = "Rossi", RelatedTo = "4000" },
            new Contact { Id = Guid.NewGuid(), LastName = "Verdi", Building = "Barbieri" },
            Contatto(),
        };

        Assert.Equal(
            DialerFieldCatalog.All.Select(c => c.Id).Where(id => id != "Kind"),
            DialerFieldCatalog.Present(rubrica).Select(c => c.Id));
    }

    [Fact]
    public void UnaDescrizioneCheRipeteIlNominativoNonFaComparireLaColonna()
    {
        // In una rubrica di soli servizi la descrizione e' il nominativo di ogni riga:
        // la colonna sarebbe vuota dappertutto, quindi non c'e'.
        Assert.DoesNotContain(DialerFieldCatalog.Present([Servizio()]), c => c.Id == "Description");
    }

    [Fact]
    public void OgniCampoSaRicavareIlProprioValoreDalContatto()
    {
        var contatto = Contatto();

        Assert.Equal("Cardiologia ambulatorio", DialerFieldCatalog.Find("Description")!.Value(contatto));
        Assert.Equal("Medicina", DialerFieldCatalog.Find("Department")!.Value(contatto));
        Assert.Equal("4000", DialerFieldCatalog.Find("RelatedTo")!.Value(contatto));
        Assert.Contains("Barbieri", DialerFieldCatalog.Find("Location")!.Value(contatto)!, StringComparison.Ordinal);
    }

    [Fact]
    public void OgniCampoHaEtichettaESpiegazione()
    {
        // L'etichetta finisce nell'intestazione della colonna, la spiegazione nel
        // testo di aiuto che lo screen reader legge a chi sceglie: senza, la scelta
        // si fa a indovinare.
        Assert.All(DialerFieldCatalog.All, campo =>
        {
            Assert.False(string.IsNullOrWhiteSpace(campo.Label));
            Assert.True(campo.Description.Length > 30);
        });
    }

    [Fact]
    public void LoStatoVieneSubitoDopoLInterno()
    {
        // E' quello che cambia una decisione: se l'interno e' occupato l'operatore fa
        // un'altra cosa. Gli altri aiutano a riconoscere il contatto giusto.
        Assert.Equal("Presence", DialerFieldCatalog.Present([Contatto()])[0].Id);
    }

    // -----------------------------------------------------------------------
    // Le colonne che tacciono dove ripeterebbero
    // -----------------------------------------------------------------------

    /// <summary>Voce di servizio: la descrizione è anche il nominativo della riga.</summary>
    private static Contact Servizio() => new()
    {
        Id = Guid.NewGuid(),
        Category = "Ambulatorio Diabetologico",
        Department = "Diabetologia",
        Numbers = [new PhoneNumber("2948", PhoneNumberKind.Extension, true)],
    };

    [Fact]
    public void LaDescrizioneTaceQuandoEGiaIlNominativo()
    {
        // Da quando le voci senza persona si chiamano come la loro descrizione, la
        // colonna le ripeteva parola per parola: la riga si leggeva "Ambulatorio
        // Diabetologico, 2948, Ambulatorio Diabetologico". In una rubrica ospedaliera
        // sono la maggioranza delle voci.
        var servizio = Servizio();

        Assert.Equal("Ambulatorio Diabetologico", servizio.EffectiveDisplayName);
        Assert.Null(DialerFieldCatalog.Find("Description")!.Value(servizio));
    }

    [Fact]
    public void SullePersoneLaDescrizioneResta()
    {
        // Qui il nominativo e' il cognome e la descrizione porta la qualifica: e'
        // esattamente quello che distingue due omonimi.
        var persona = new Contact
        {
            Id = Guid.NewGuid(),
            FirstName = "Mario",
            LastName = "Rossi",
            Category = "Studio medico",
            Numbers = [new PhoneNumber("2094", PhoneNumberKind.Extension, true)],
        };

        Assert.Equal("Studio medico", DialerFieldCatalog.Find("Description")!.Value(persona));
    }

    [Fact]
    public void IlTipoTaceQuandoIlNominativoLoDiceGia()
    {
        var fax = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "Fax Accettazione DH",
            Kind = ContactKind.Fax,
            Numbers = [new PhoneNumber("2678", PhoneNumberKind.Extension, true)],
        };

        Assert.Null(DialerFieldCatalog.Find("Kind")!.Value(fax));
    }

    [Fact]
    public void IlTipoParlaSuUnCodiceAbbreviato()
    {
        // Viene da un contrassegno della sorgente e non da una parola: senza la
        // colonna, "MILANO POLICLINICO" non lascia intuire che sia un numero breve.
        var breve = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "MILANO POLICLINICO",
            Kind = ContactKind.ShortCode,
            Numbers = [new PhoneNumber("6019", PhoneNumberKind.Extension, true)],
        };

        Assert.Equal("numero breve", DialerFieldCatalog.Find("Kind")!.Value(breve));
        Assert.Contains(DialerFieldCatalog.Present([breve]), c => c.Id == "Kind");
    }

    [Fact]
    public void IFaxCheSiChiamanoFaxNonFannoComparireIlTipo()
    {
        // Sono la quasi totalita' dei fax di una rubrica ospedaliera: la colonna
        // sarebbe vuota su tutti, ed e' meglio che non ci sia.
        var fax = new Contact
        {
            Id = Guid.NewGuid(),
            Category = "Fax Accettazione DH",
            Kind = ContactKind.Fax,
            Numbers = [new PhoneNumber("2678", PhoneNumberKind.Extension, true)],
        };

        Assert.DoesNotContain(DialerFieldCatalog.Present([fax]), c => c.Id == "Kind");
    }

    [Fact]
    public void NessunCampoRipeteIlNominativoDiUnaVoceDiServizio()
    {
        // La rete di sicurezza: qualunque campo si aggiunga al catalogo, nessuno deve
        // restituire esattamente il nominativo della riga. Sarebbe una freccia destra
        // spesa per risentire quello che si e' appena sentito.
        var servizio = Servizio();

        Assert.All(DialerFieldCatalog.All, campo =>
            Assert.NotEqual(servizio.EffectiveDisplayName, campo.Value(servizio)));
    }
}
