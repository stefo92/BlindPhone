using BlindPhone.Contacts;
using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// La ricerca in rubrica interroga tutti i campi, non il solo nominativo.
/// </summary>
/// <remarks>
/// <para>
/// In una rubrica ospedaliera la maggior parte delle voci non ha un nome: sono
/// ambulatori, sale, guardiole, e si cercano per descrizione. L'operatore ricorda
/// «l'ambulatorio di cardiologia» e digita quello che gli viene in mente, non la
/// dicitura esatta nell'ordine esatto.
/// </para>
/// </remarks>
public sealed class RicercaRubricaTests : IDisposable
{
    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "blindphone-rubrica-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteContactRepository _rubrica;

    public RicercaRubricaTests()
    {
        Directory.CreateDirectory(_cartella);
        _rubrica = new SqliteContactRepository(Path.Combine(_cartella, "contacts.db"), "chiave-di-prova");
    }

    public void Dispose()
    {
        _rubrica.Dispose();
        try { Directory.Delete(_cartella, recursive: true); } catch (IOException) { }
    }

    private static Contact Voce(
        string numero,
        string? descrizione = null,
        string? reparto = null,
        string? nome = null,
        string? cognome = null,
        string? padiglione = null,
        string? note = null,
        ContactKind tipo = ContactKind.Unknown) => new()
        {
            Id = Guid.NewGuid(),
            FirstName = nome,
            LastName = cognome,
            Category = descrizione,
            Department = reparto,
            Building = padiglione,
            Notes = note,
            Kind = tipo,
            Numbers = [new PhoneNumber(numero, PhoneNumberKind.Extension, true)],
            Source = "prova",
        };

    private async Task Carica(params Contact[] contatti)
        => await _rubrica.UpsertAsync(contatti, "prova", CancellationToken.None);

    [Fact]
    public async Task SiTrovaUnaVocePerLaSolaDescrizione()
    {
        await Carica(Voce("2127", descrizione: "Stanza Lavoro Infermieri Degenza"));

        var trovati = await _rubrica.SearchAsync("infermieri", 50, CancellationToken.None);

        Assert.Equal("2127", Assert.Single(trovati).PrimaryNumber);
    }

    [Fact]
    public async Task LeParolePossonoStareInOrdineDiversoDaQuelloScritto()
    {
        // Con un solo LIKE sull'intera frase questa ricerca non trovava nulla: le
        // parole dovevano essere contigue e nell'ordine esatto.
        await Carica(Voce("3050", descrizione: "Ambulatorio di Cardiologia"));

        var diretta = await _rubrica.SearchAsync("ambulatorio cardiologia", 50, CancellationToken.None);
        var invertita = await _rubrica.SearchAsync("cardiologia ambulatorio", 50, CancellationToken.None);

        Assert.Single(diretta);
        Assert.Single(invertita);
    }

    [Fact]
    public async Task LeParolePossonoStareInCampiDiversi()
    {
        // Una nella descrizione, una nel reparto: il testo di ricerca li concatena
        // tutti, quindi la voce si trova lo stesso.
        await Carica(Voce("3051", descrizione: "Ambulatorio", reparto: "Lungodegenza Critica"));

        var trovati = await _rubrica.SearchAsync("ambulatorio lungodegenza", 50, CancellationToken.None);

        Assert.Single(trovati);
    }

    [Fact]
    public async Task LeParoleInPiuRestringonoInveceDiAllargare()
    {
        await Carica(
            Voce("3060", descrizione: "Ambulatorio", reparto: "Cardiologia"),
            Voce("3061", descrizione: "Ambulatorio", reparto: "Radiologia"));

        Assert.Equal(2, (await _rubrica.SearchAsync("ambulatorio", 50, CancellationToken.None)).Count);

        var ristretta = await _rubrica.SearchAsync("ambulatorio radiologia", 50, CancellationToken.None);

        Assert.Equal("3061", Assert.Single(ristretta).PrimaryNumber);
    }

    [Fact]
    public async Task SiTrovaAncoraPerNomeEPerNumero()
    {
        // La modifica non deve rompere le due ricerche che si facevano gia'.
        await Carica(Voce("4021", nome: "Mario", cognome: "Rossi"));

        Assert.Single(await _rubrica.SearchAsync("rossi", 50, CancellationToken.None));
        Assert.Single(await _rubrica.SearchAsync("mario rossi", 50, CancellationToken.None));
        Assert.Single(await _rubrica.SearchAsync("4021", 50, CancellationToken.None));
    }

    [Fact]
    public async Task SiTrovaPerCollocazione()
    {
        await Carica(Voce("2200", descrizione: "Guardiola", padiglione: "Padiglione Barbieri"));

        Assert.Single(await _rubrica.SearchAsync("barbieri", 50, CancellationToken.None));
    }

    [Fact]
    public async Task SiTrovaPerQuelloCheEScrittoNelleNote()
    {
        // Le note raccolgono cio' che la rubrica dell'ente infila nel campo del numero
        // senza che sia un numero: e' informazione che l'operatore cerca esattamente
        // come cerca un reparto.
        await Carica(Voce("2247", descrizione: "Coordinatore Infermieristico", note: "breve 6583"));

        Assert.Single(await _rubrica.SearchAsync("6583", 50, CancellationToken.None));
    }

    [Fact]
    public async Task SiTrovaUnFaxCercandoLaParolaFax()
    {
        // E' il modo in cui si chiede un fax quando non se ne ricorda il reparto.
        await Carica(Voce("2678", descrizione: "Accettazione DH", tipo: ContactKind.Fax));

        Assert.Single(await _rubrica.SearchAsync("fax", 50, CancellationToken.None));
    }

    // -----------------------------------------------------------------------
    // Interni condivisi
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnInternoCondivisoRestituisceTutteLeVoci()
    {
        // Su una rubrica ospedaliera vera sono centotrentotto numeri, fino a otto voci
        // ciascuno. Restituirne una sola significava sceglierne una a caso e
        // annunciarla come se fosse certa: l'operatore sentiva un nome preciso e
        // sbagliato, che e' peggio di un numero.
        await Carica(
            Voce("2093", descrizione: "Coordinatore Infermieristico", reparto: "Centro Hub Emofilia"),
            Voce("2093", descrizione: "Coordinatore infermieristico", reparto: "Diabetologia"));

        var trovati = await _rubrica.ResolveByNumberAsync("2093", CancellationToken.None);

        Assert.Equal(2, trovati.Count);
    }

    [Fact]
    public async Task SuUnInternoCondivisoLaPersonaVienePrimaDelServizio()
    {
        // Su un apparecchio condiviso fra una persona e una stanza, il nome della
        // persona identifica meglio la chiamata.
        await Carica(
            Voce("2095", descrizione: "Stanza Lavoro Infermieri", tipo: ContactKind.Service),
            Voce("2095", nome: "Franca", cognome: "Pozzi", tipo: ContactKind.Person));

        var trovati = await _rubrica.ResolveByNumberAsync("2095", CancellationToken.None);

        Assert.Equal("Franca Pozzi", trovati[0].EffectiveDisplayName);
    }

    [Fact]
    public async Task UnNumeroSconosciutoRestituisceUnElencoVuotoENonUnErrore()
    {
        Assert.Empty(await _rubrica.ResolveByNumberAsync("9999", CancellationToken.None));
        Assert.Empty(await _rubrica.ResolveByNumberAsync("non-un-numero", CancellationToken.None));
    }
}
