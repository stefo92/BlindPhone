using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Rubrica che restituisce più del dovuto: tutte le voci, qualunque numero le venga chiesto.
/// </summary>
/// <remarks>
/// Riproduce il peggior comportamento che l'archivio vero possa avere con un interno:
/// il confronto sulle ultime nove cifre, pensato per riconoscere chi chiama, fa
/// risultare uguali due numeri lunghi che finiscono allo stesso modo. Se la ricerca per
/// interno si fidasse dell'archivio, queste prove la farebbero cadere.
/// </remarks>
internal sealed class RubricaCheRestituisceTutto(params Contact[] contatti) : IContactRepository
{
    public int Interrogazioni { get; private set; }

    public Task<IReadOnlyList<Contact>> ResolveByNumberAsync(string number, CancellationToken cancellationToken)
    {
        Interrogazioni++;
        return Task.FromResult<IReadOnlyList<Contact>>(contatti);
    }

    public Task<IReadOnlyList<Contact>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Contact>>([]);

    public Task<Contact?> GetBySpeedDialAsync(int slot, CancellationToken cancellationToken)
        => Task.FromResult<Contact?>(null);

    public Task<int> UpsertAsync(IEnumerable<Contact> contacts, string sourceName, CancellationToken cancellationToken)
        => Task.FromResult(0);

    public Task UpdatePresenceAsync(string extension, PresenceState presence, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(contatti.Length);

    public Task<int> ClearAsync(string? sourceName, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>
/// La ricerca per interno dal campo di composizione è per uguaglianza, non per somiglianza.
/// </summary>
/// <remarks>
/// <para>
/// Chi compone un interno e si ritrova un elenco di voci che contengono quelle cifre in
/// qualunque posizione non ha guadagnato nulla rispetto a prima: deve scorrerle una per
/// una per scoprire che quella cercata non c'è, oppure per capire quale sia. Ancora
/// peggio sul tastierino: le stesse cifre, lette come T9, richiamerebbero dei nomi.
/// </para>
/// <para>
/// Il confronto sull'archivio vero, con la ricerca per frammenti a fianco per
/// contrasto, è in <c>RicercaInternoInRubricaTests</c>.
/// </para>
/// </remarks>
public sealed class RicercaPerInternoTests
{
    private static Contact Voce(string nome, string numero, PhoneNumberKind tipo = PhoneNumberKind.Extension) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = nome,
        Numbers = [new PhoneNumber(numero, tipo, true)],
    };

    // -----------------------------------------------------------------------
    // Che cosa e' un interno digitato
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("4021")]
    [InlineData("2")]
    [InlineData("  4021  ")]
    [InlineData("0612345678")]
    public void SoloCifreSonoUnInterno(string testo)
        => Assert.True(ExtensionSearch.IsExtensionQuery(testo));

    [Theory]
    [InlineData(null, "campo mai toccato")]
    [InlineData("", "campo vuoto")]
    [InlineData("   ", "campo di soli spazi")]
    [InlineData("mario", "e' un nome")]
    [InlineData("40a", "una lettera lo fa diventare un nome")]
    [InlineData("40 21", "uno spazio in mezzo: due numeri, non un interno")]
    [InlineData("+390612345", "il segno piu' e' un numero esterno")]
    [InlineData("*21", "e' un codice di servizio del centralino")]
    [InlineData("4021#", "il cancelletto non e' parte di un interno")]
    [InlineData("٤٠٢١", "cifre di altri alfabeti: l'interno si digita con quelle ASCII")]
    public void TuttoQuelloCheNonESoloCifreNonEUnInterno(string? testo, string perche)
        => Assert.False(ExtensionSearch.IsExtensionQuery(testo), perche);

    // -----------------------------------------------------------------------
    // Corrispondenza esatta
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CompareSoloChiHaProprioQuellInterno()
    {
        // L'archivio qui restituisce tutto, di proposito: a decidere deve essere il
        // confronto esatto, non la cortesia di chi risponde.
        var rubrica = new RubricaCheRestituisceTutto(
            Voce("Mario Rossi", "4021"),
            Voce("Contiene le cifre dopo", "40213"),
            Voce("Contiene le cifre prima", "14021"),
            Voce("Contiene le cifre in mezzo", "240213"),
            Voce("Ne ha solo l'inizio", "402"));

        var trovati = await ExtensionSearch.FindAsync(rubrica, "4021", 10, CancellationToken.None);

        var unico = Assert.Single(trovati);
        Assert.Equal("Mario Rossi", unico.Contact.DisplayName);
        Assert.Equal("4021", unico.Number);
    }

    [Fact]
    public async Task UnPezzoDiInternoNonTrovaNiente()
    {
        // 40 e' l'inizio di 4021: con la ricerca per frammenti sarebbe comparso. Qui
        // deve tacere, perche' nessuno ha l'interno 40.
        var rubrica = new RubricaCheRestituisceTutto(Voce("Mario Rossi", "4021"));

        Assert.Empty(await ExtensionSearch.FindAsync(rubrica, "40", 10, CancellationToken.None));
        Assert.Empty(await ExtensionSearch.FindAsync(rubrica, "21", 10, CancellationToken.None));
        Assert.Empty(await ExtensionSearch.FindAsync(rubrica, "402", 10, CancellationToken.None));
    }

    [Theory]
    [InlineData(PhoneNumberKind.Mobile)]
    [InlineData(PhoneNumberKind.Work)]
    [InlineData(PhoneNumberKind.Home)]
    [InlineData(PhoneNumberKind.Fax)]
    [InlineData(PhoneNumberKind.Other)]
    public async Task UnRecapitoCheNonEUnInternoNonConta(PhoneNumberKind tipo)
    {
        // Non e' una risposta a «chi ha questo interno»: l'archivio confronta tutti i
        // recapiti allo stesso modo, e sta qui scartare quelli che interni non sono.
        var rubrica = new FakeContactRepository()
            .Add("Telefono personale", ("4021", tipo));

        Assert.Empty(await ExtensionSearch.FindAsync(rubrica, "4021", 10, CancellationToken.None));
    }

    [Fact]
    public async Task ContaLInternoAncheSeLaVoceHaAltriRecapiti()
    {
        var rubrica = new FakeContactRepository()
            .Add(
                "Mario Rossi",
                ("3331234567", PhoneNumberKind.Mobile),
                ("4021", PhoneNumberKind.Extension));

        var trovati = await ExtensionSearch.FindAsync(rubrica, "4021", 10, CancellationToken.None);

        Assert.Equal("4021", Assert.Single(trovati).Number);
    }

    [Fact]
    public async Task LInternoScrittoConSeparatoriSiTrovaEDiceComeEScritto()
    {
        // Lo stesso interno puo' essere stato importato come «40-21» o «40 21». Chi
        // digita 4021 lo vuole trovare, ma sotto la riga deve restare il numero della
        // rubrica: e' quello che l'operatore leggera' e sentira'.
        var rubrica = new FakeContactRepository()
            .Add("Trattino", ("40-21", PhoneNumberKind.Extension))
            .Add("Spazio", ("40 21", PhoneNumberKind.Extension));

        var trovati = await ExtensionSearch.FindAsync(rubrica, "4021", 10, CancellationToken.None);

        Assert.Equal(["40-21", "40 21"], trovati.Select(t => t.Number));
    }

    [Fact]
    public async Task NumeriLunghiCheFinisconoAlloStessoModoNonSiConfondono()
    {
        // L'archivio riduce i numeri alle ultime nove cifre, quindi per lui questi due
        // sono lo stesso numero. Per un interno digitato non lo sono.
        var rubrica = new RubricaCheRestituisceTutto(
            Voce("Nove cifre", "123456789"),
            Voce("Dieci cifre", "0123456789"));

        var trovati = await ExtensionSearch.FindAsync(rubrica, "123456789", 10, CancellationToken.None);

        Assert.Equal("Nove cifre", Assert.Single(trovati).Contact.DisplayName);
    }

    // -----------------------------------------------------------------------
    // Interni condivisi
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PiuVociSuLoStessoInternoSonoTutteRestituiteNellOrdineDellArchivio()
    {
        // Lo stesso apparecchio serve l'accettazione e la stanza infermieri: sono tutte
        // risposte giuste alla stessa domanda, e sceglierne una sarebbe annunciarla
        // come se fosse certa.
        var rubrica = new FakeContactRepository()
            .AddExtension("Accettazione", "2093")
            .AddExtension("Stanza infermieri", "2093")
            .AddExtension("Altro interno", "2094");

        var trovati = await ExtensionSearch.FindAsync(rubrica, "2093", 10, CancellationToken.None);

        Assert.Equal(["Accettazione", "Stanza infermieri"], trovati.Select(t => t.Contact.DisplayName));
    }

    [Fact]
    public async Task IlLimiteTagliaLElenco()
    {
        var rubrica = new FakeContactRepository()
            .AddExtension("Uno", "2093")
            .AddExtension("Due", "2093")
            .AddExtension("Tre", "2093");

        var trovati = await ExtensionSearch.FindAsync(rubrica, "2093", 2, CancellationToken.None);

        Assert.Equal(2, trovati.Count);
    }

    // -----------------------------------------------------------------------
    // Quando non si interroga nemmeno
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("mario")]
    [InlineData("+390612345")]
    [InlineData("40 21")]
    public async Task UnTestoCheNonEUnInternoNonInterrogaLArchivio(string testo)
    {
        var rubrica = new RubricaCheRestituisceTutto(Voce("Mario Rossi", "4021"));

        var trovati = await ExtensionSearch.FindAsync(rubrica, testo, 10, CancellationToken.None);

        Assert.Empty(trovati);
        Assert.Equal(0, rubrica.Interrogazioni);
    }

    [Fact]
    public async Task UnInternoCheNessunoHaDaUnElencoVuotoENonUnErrore()
        => Assert.Empty(await ExtensionSearch.FindAsync(
            new FakeContactRepository().AddExtension("Mario Rossi", "4021"),
            "9999",
            10,
            CancellationToken.None));
}
