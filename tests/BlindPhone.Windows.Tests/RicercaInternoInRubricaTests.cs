using BlindPhone.Contacts;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// La ricerca per interno dal campo di composizione, provata sull'archivio vero.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto di partenza.</b> Digitando solo cifre nel campo di composizione non
/// partiva nessuna ricerca. La via più breve per farla partire era mandare le cifre
/// alla stessa ricerca dei nomi, e sarebbe stata sbagliata: quella ricerca cerca
/// frammenti, e sulle cifre cerca anche i nomi che la tastiera telefonica può comporre.
/// Digitare «402» avrebbe riempito l'elenco di ogni numero che contiene quelle cifre
/// e di ogni nome che quelle cifre possono scrivere.
/// </para>
/// <para>
/// Le prove mettono sempre a fianco le due ricerche sugli stessi dati: è il confronto
/// che spiega perché per l'interno ne serve una diversa, e che farebbe cadere chi un
/// giorno le unificasse per semplificare.
/// </para>
/// </remarks>
public sealed class RicercaInternoInRubricaTests : IDisposable
{
    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "blindphone-interni-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteContactRepository _rubrica;

    public RicercaInternoInRubricaTests()
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
        string nome,
        string numero,
        PhoneNumberKind tipo = PhoneNumberKind.Extension) => new()
        {
            Id = Guid.NewGuid(),
            DisplayName = nome,
            Numbers = [new PhoneNumber(numero, tipo, true)],
            Source = "prova",
        };

    private async Task Carica(params Contact[] contatti)
        => await _rubrica.UpsertAsync(contatti, "prova", CancellationToken.None);

    private Task<IReadOnlyList<ExtensionMatch>> Interno(string digitato)
        => ExtensionSearch.FindAsync(_rubrica, digitato, 20, CancellationToken.None);

    // -----------------------------------------------------------------------
    // Uguaglianza, non somiglianza
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LaRicercaPerFrammentiTrovaTuttoEQuestoELaRagioneDiUnaRicercaPerInterno()
    {
        await Carica(
            Voce("Mario Rossi", "4021"),
            Voce("Cifre dopo", "40213"),
            Voce("Cifre prima", "14021"),
            Voce("Cifre in mezzo", "240213"));

        // Il contrasto: la ricerca per nome, a cui le cifre arriverebbero se non ci
        // fosse la ricerca per interno, restituisce tutte e quattro le voci.
        var perFrammenti = await _rubrica.SearchAsync("4021", 50, CancellationToken.None);
        Assert.Equal(4, perFrammenti.Count);

        // La ricerca per interno ne restituisce una.
        var trovati = await Interno("4021");

        Assert.Equal("Mario Rossi", Assert.Single(trovati).Contact.DisplayName);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("40")]
    [InlineData("402")]
    [InlineData("021")]
    [InlineData("40211")]
    public async Task UnaPartedellInternoNonTrovaNessuno(string digitato)
    {
        // Man mano che si digita «4021» le cifre intermedie non devono far comparire
        // niente: nessuno ha l'interno 4, 40 o 402.
        await Carica(Voce("Mario Rossi", "4021"), Voce("Anna Verdi", "4022"));

        Assert.Empty(await Interno(digitato));
    }

    [Fact]
    public async Task UnaCifraInMenoNonRichiamaINomiDellaTastieraTelefonica()
    {
        // «Gigi» sulla tastiera telefonica è 4444. La ricerca per nome, che sulle cifre
        // guarda anche quella codifica, lo troverebbe digitando quattro volte il 4:
        // con un interno non c'entra nulla.
        await Carica(Voce("Gigi Verdi", "2200"));

        Assert.Single(await _rubrica.SearchAsync("4444", 50, CancellationToken.None));
        Assert.Empty(await Interno("4444"));
    }

    // -----------------------------------------------------------------------
    // Quali recapiti contano
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(PhoneNumberKind.Mobile)]
    [InlineData(PhoneNumberKind.Work)]
    [InlineData(PhoneNumberKind.Fax)]
    public async Task UnNumeroCheNonEUnInternoNonSiTrovaDigitandoloIntero(PhoneNumberKind tipo)
    {
        await Carica(Voce("Telefono personale", "3331234567", tipo));

        Assert.Empty(await Interno("3331234567"));
    }

    [Fact]
    public async Task LInternoDiUnaVoceCheHaAncheUnCellulareSiTrovaComunque()
    {
        await Carica(new Contact
        {
            Id = Guid.NewGuid(),
            DisplayName = "Mario Rossi",
            Numbers =
            [
                new PhoneNumber("3331234567", PhoneNumberKind.Mobile, IsPrimary: true),
                new PhoneNumber("4021", PhoneNumberKind.Extension),
            ],
            Source = "prova",
        });

        var trovati = await Interno("4021");

        // E il numero sotto la riga e' l'interno digitato, non il cellulare primario:
        // chi scende nell'elenco e preme Invio deve chiamare cio' che ha scritto.
        Assert.Equal("4021", Assert.Single(trovati).Number);
    }

    [Fact]
    public async Task LInternoScrittoConUnTrattinoInRubricaSiTrovaDigitandoSoloLeCifre()
    {
        await Carica(Voce("Mario Rossi", "40-21"));

        var trovati = await Interno("4021");

        Assert.Equal("40-21", Assert.Single(trovati).Number);
    }

    // -----------------------------------------------------------------------
    // Prefissi lunghi
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DueNumeriLunghiCheFinisconoAlloStessoModoNonSiConfondono()
    {
        // L'archivio li riduce entrambi alle stesse nove cifre: per lui sono lo stesso
        // numero, ed e' giusto quando si deve riconoscere chi chiama. Per un interno
        // digitato non e' giusto, e il confronto esatto lo corregge.
        await Carica(Voce("Nove cifre", "123456789"), Voce("Dieci cifre", "0123456789"));

        var vistiDallArchivio = await _rubrica.ResolveByNumberAsync("123456789", CancellationToken.None);
        Assert.Equal(2, vistiDallArchivio.Count);

        var trovati = await Interno("123456789");

        Assert.Equal("Nove cifre", Assert.Single(trovati).Contact.DisplayName);
    }

    // -----------------------------------------------------------------------
    // Interni condivisi
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnInternoCondivisoRestituisceTutteLeVociConLaPersonaPrima()
    {
        await Carica(
            Voce("Stanza Lavoro Infermieri", "2095"),
            Voce("Franca Pozzi", "2095"));

        var trovati = await Interno("2095");

        Assert.Equal(2, trovati.Count);
        Assert.All(trovati, t => Assert.Equal("2095", t.Number));
    }
}

/// <summary>
/// Il collegamento della ricerca per interno nella finestra principale.
/// </summary>
/// <remarks>
/// <para>
/// Le prove leggono il sorgente invece di premere i tasti, per la stessa ragione di
/// <see cref="ChiusuraConAltF4Tests"/>: costruire la finestra principale richiederebbe
/// l'intera catena dei servizi, e quello che si vuole proteggere è una scelta di
/// instradamento, non un calcolo.
/// </para>
/// <para>
/// Le tre scelte che contano, ognuna a prova di una semplificazione che sembrerebbe
/// innocua: le cifre vanno alla ricerca per interno e non a quella per nome; quando
/// nessuno ha l'interno si tace; e comporre ferma la ricerca ancora in sospeso.
/// </para>
/// </remarks>
public sealed class RicercaInternoDalCampoTests
{
    private static string Sorgente()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(directory.FullName, "src", "BlindPhone.App", "MainWindow.xaml.cs");
            if (File.Exists(candidato)) return File.ReadAllText(candidato);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Sorgente MainWindow.xaml.cs non trovato");
    }

    /// <summary>Il corpo del metodo che comincia con <paramref name="inizio"/> e finisce dove comincia <paramref name="fine"/>.</summary>
    private static string Corpo(string inizio, string fine)
    {
        var codice = Sorgente();

        var da = codice.IndexOf(inizio, StringComparison.Ordinal);
        Assert.True(da > 0, $"{inizio} non esiste piu' in MainWindow");

        var a = codice.IndexOf(fine, da, StringComparison.Ordinal);
        Assert.True(a > da, $"La fine di {inizio} non e' piu' riconoscibile: cercavo {fine}");

        return codice[da..a];
    }

    [Fact]
    public void IlCampoAvviaLaRicercaAnchePerLeSoleCifre()
    {
        // Prima delle cifre non partiva niente: bastava che il campo non sembrasse un
        // nome. Se questa condizione tornasse a guardare solo i nomi, digitare un
        // interno tornerebbe a non cercare nulla, senza che nessun errore lo dica.
        var corpo = Corpo("private void OnDialerTextChanged", "private static bool LooksLikeName");

        Assert.Contains("ExtensionSearch.IsExtensionQuery", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public void LeCifreVannoAllaRicercaPerInternoENonAQuellaPerNome()
    {
        var corpo = Corpo("private async Task RunSuggestionSearchAsync", "private static string? DialableNumber");

        Assert.Contains("ExtensionSearch", corpo, StringComparison.Ordinal);
        Assert.Contains("FindAsync", corpo, StringComparison.Ordinal);

        // Il ramo dei nomi esiste ancora e resta separato: se le due ricerche si
        // fondessero in una, l'interno tornerebbe a essere cercato per frammenti.
        // Si cerca la chiamata col punto davanti perche' il nome del metodo stesso
        // contiene «SearchAsync».
        var perNome = corpo.IndexOf(".SearchAsync(", StringComparison.Ordinal);
        var scelta = corpo.IndexOf("if (byExtension)", StringComparison.Ordinal);

        Assert.True(perNome > 0, "La ricerca per nome non si trova piu' in RunSuggestionSearchAsync");
        Assert.True(
            scelta > 0 && scelta < perNome,
            "La scelta fra interno e nome deve venire prima della ricerca per nome");
    }

    [Fact]
    public void SeNessunoHaLInternoLaVoceTace()
    {
        var corpo = Corpo("private void AnnounceSuggestions", "private string CurrentDestination");

        var tace = corpo.IndexOf("if (byExtension) return;", StringComparison.Ordinal);
        var nessuno = corpo.IndexOf("Nessun contatto per", StringComparison.Ordinal);

        Assert.True(tace > 0, "Con un interno senza voci l'annuncio non deve parlare");
        Assert.True(
            tace < nessuno,
            "Il silenzio deve venire prima di «Nessun contatto per»: a meta' di un numero lo si sentirebbe a ogni pausa");
    }

    [Fact]
    public void LInternoDigitatoNonVieneRipetutoAChiLHaAppenaScritto()
    {
        var corpo = Corpo("private void AnnounceSuggestions", "private string CurrentDestination");

        // Nel ramo della voce sola, quando si e' cercato per interno, il messaggio e'
        // il solo nome: nominare di nuovo le cifre appena digitate allungherebbe
        // l'annuncio piu' frequente senza dire niente di nuovo.
        var vocesola = corpo.IndexOf("var only = _suggestions[0];", StringComparison.Ordinal);
        var piuVoci = corpo.IndexOf("else if (truncated)", StringComparison.Ordinal);

        Assert.True(vocesola > 0 && piuVoci > vocesola, "Il ramo della voce sola non e' piu' riconoscibile");
        Assert.Contains("byExtension", corpo[vocesola..piuVoci], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "if (_transfer.Phase == TransferPhase.EnteringDestination)",
        "_transfer.BlindTransferNowAsync(destination)",
        "il trasferimento cieco")]
    [InlineData(
        "if (!CurrentActionContext().CanDial)",
        "_calls.DialAsync(destination)",
        "la composizione")]
    public void ComporreFermaLaRicercaInSospesoPrimaDiPartire(string inizioRamo, string azione, string quale)
    {
        // L'annuncio «Mario Rossi» arriverebbe sopra «Chiamata a...», che e' il solo
        // riscontro dell'avvio. Succede con le cifre perche' il campo puo' essere
        // riempito dal programma — ricomposizione dalle ultime chiamate — e comporre
        // subito, con il conto alla rovescia ancora acceso.
        //
        // Si guarda ogni ramo da solo: con un unico controllo sull'intero metodo
        // basterebbe una sola delle due cancellazioni per far passare entrambe.
        var corpo = Corpo("private async Task DialAsync()", "private void OnDialerTextChanged");

        var ramo = corpo.IndexOf(inizioRamo, StringComparison.Ordinal);
        Assert.True(ramo > 0, $"Il ramo di {quale} non e' piu' riconoscibile in DialAsync");

        var parte = corpo.IndexOf(azione, ramo, StringComparison.Ordinal);
        Assert.True(parte > ramo, $"{quale} non si trova piu' nel suo ramo di DialAsync");

        var ferma = corpo.IndexOf("CancelSuggestionSearch();", ramo, StringComparison.Ordinal);

        Assert.True(
            ferma > ramo && ferma < parte,
            $"Prima di avviare {quale} va fermata la ricerca in sospeso");
    }
}
