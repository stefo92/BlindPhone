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
        string? padiglione = null) => new()
        {
            Id = Guid.NewGuid(),
            FirstName = nome,
            LastName = cognome,
            Category = descrizione,
            Department = reparto,
            Building = padiglione,
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
}
