using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica i campi della rubrica mostrabili nella ricerca dal campo di composizione.
/// </summary>
/// <remarks>
/// La scelta esiste perché dipende da come è fatta la rubrica dell'ente: dove i
/// nominativi si assomigliano serve la descrizione, dove al chiamante bisogna dire
/// dove presentarsi serve la collocazione. E ogni colonna in più è una freccia destra
/// in più per chi legge la riga con lo screen reader.
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
    public void LeColonneEsconoNellOrdineScelto()
    {
        // Chi legge con lo screen reader percorre la riga da sinistra a destra e si
        // ferma quando ha saputo quello che gli serve: il campo che decide deve
        // stare subito dopo il numero, non in fondo dietro due campi inutili.
        var campi = DialerFieldCatalog.Resolve(["Location", "Description", "Presence"]);

        Assert.Equal(["Location", "Description", "Presence"], campi.Select(c => c.Id));
    }

    [Fact]
    public void LOrdineNonEQuelloDelCatalogo()
    {
        // La rete di sicurezza contro il ritorno silenzioso all'ordine fisso: se
        // qualcuno reintroducesse un filtro sul catalogo il test sopra passerebbe
        // comunque quando la scelta coincide con l'ordine di catalogo.
        var scelta = DialerFieldCatalog.All.Reverse().Select(c => c.Id).ToArray();

        Assert.Equal(scelta, DialerFieldCatalog.Resolve(scelta).Select(c => c.Id));
    }

    [Fact]
    public void UnaChiaveRipetutaNonProduceDueColonne()
    {
        // Due colonne identiche sarebbero una freccia destra sprecata su ogni
        // contatto scorso, per tutta la giornata.
        var campi = DialerFieldCatalog.Resolve(["Department", "Presence", "Department"]);

        Assert.Equal(["Department", "Presence"], campi.Select(c => c.Id));
    }

    [Fact]
    public void LeChiaviSconosciuteVengonoIgnorate()
    {
        // Una configurazione scritta a mano, o arrivata da un pacchetto di
        // provisioning di un'altra versione, non deve lasciare la postazione senza
        // elenco dei contatti.
        var campi = DialerFieldCatalog.Resolve(["Description", "CampoCheNonEsiste", "Presence"]);

        Assert.Equal(2, campi.Count);
    }

    [Fact]
    public void UnElencoVuotoSignificaSoloNominativoEInterno()
        => Assert.Empty(DialerFieldCatalog.Resolve([]));

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
    public void LoStatoEIlCampoPredefinito()
    {
        // E' quello che cambia una decisione: se l'interno e' occupato l'operatore fa
        // un'altra cosa. Gli altri aiutano a riconoscere il contatto giusto.
        Assert.Equal(["Presence"], DialerFieldCatalog.Default);
    }
}
