using System.Net.Http;
using System.Text;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica gli aggiornamenti dalla cartella dei download su HTTPS, come quella del
/// portale di BlindPhone.
/// </summary>
/// <remarks>
/// <para>
/// Su HTTP non esiste l'elenco di una cartella: il portale risponde a
/// <c>download/?per=x64</c> con un rimando al pacchetto più recente, ed è lo stesso link
/// che segue chi scarica dalla pagina. Queste prove parlano con una cartella finta, in
/// HTTPS vero sul loopback, che si comporta allo stesso modo.
/// </para>
/// <para>
/// <b>Il pacchetto non arriva mai fino all'installazione</b>: la firma vera richiede la
/// chiave privata di rilascio, che nel repository non c'è. Le prove arrivano fino al
/// rifiuto della firma, e quello basta a dire che pacchetto e firma si scaricano dal
/// posto giusto e che il controllo li vede; la verifica della firma valida la collaudano
/// a parte le prove di <see cref="FirmaAggiornamentoTests"/>.
/// </para>
/// </remarks>
public sealed class AggiornamentiHttpsTests : IAsyncLifetime
{
    private const string PacchettoX64 = "BlindPhone-1.0.21-x64.msi";
    private const string PacchettoArm64 = "BlindPhone-1.0.21-arm64.msi";

    private static readonly Version Installata = new(1, 0, 20, 2);

    private readonly ServerAggiornamentiFinto _server = new();
    private readonly HttpMessageHandler _gestore;

    /// <summary>
    /// Cartella degli scaricamenti di questa prova: quella vera della postazione non si
    /// tocca.
    /// </summary>
    private readonly string _scaricamenti = Path.Combine(
        Path.GetTempPath(), "BlindPhone.Test", $"scaricamenti-{Guid.NewGuid():N}");

    public AggiornamentiHttpsTests()
    {
        _gestore = _server.GestoreCheSiFida();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _gestore.Dispose();
        await _server.DisposeAsync();

        if (Directory.Exists(_scaricamenti)) Directory.Delete(_scaricamenti, recursive: true);
    }

    private UpdateService Servizio(
        Version? installata = null,
        string processore = "x64",
        string? utente = null,
        string password = "") => new(
        new UpdateSettings { ServerUrl = _server.Cartella, Username = utente },
        password,
        installata ?? Installata,
        httpHandler: _gestore,
        architecture: processore,
        downloadDirectory: _scaricamenti);

    /// <summary>Un avanzamento che registra subito, senza passare da un contesto.</summary>
    private sealed class Avanzamento : IProgress<int>
    {
        public List<int> Valori { get; } = [];

        public void Report(int value)
        {
            lock (Valori) Valori.Add(value);
        }
    }

    // -----------------------------------------------------------------------
    // Il controllo
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LaCartellaIndicaLUltimaVersioneConLeSueNovita()
    {
        _server.Rimanda("x64", PacchettoX64);
        _server.Pubblica("BlindPhone-1.0.21-x64.txt", "# commento\n- Prima novita'\n- Seconda novita'\nSHA256: ABC123\n");

        var esito = await Servizio().CheckAsync();

        Assert.False(esito.Failed, esito.Error);
        Assert.True(esito.HasUpdate);
        Assert.Equal(new Version(1, 0, 21), esito.Update!.Version);
        Assert.Equal(PacchettoX64, esito.Update.InstallerFileName);
        Assert.Equal(["Prima novita'", "Seconda novita'"], esito.Notes.Features);
        Assert.Equal("ABC123", esito.Notes.Sha256);

        Assert.Contains("/download/?per=x64", _server.Percorsi);
        Assert.Contains("/download/BlindPhone-1.0.21-x64.txt", _server.Percorsi);
    }

    [Fact]
    public async Task OgniProcessoreChiedeIlProprioPacchetto()
    {
        _server.Rimanda("x64", PacchettoX64);
        _server.Rimanda("arm64", PacchettoArm64);

        var esito = await Servizio(processore: "arm64").CheckAsync();

        Assert.False(esito.Failed, esito.Error);
        Assert.Equal(PacchettoArm64, esito.Update!.InstallerFileName);
        Assert.Contains("/download/?per=arm64", _server.Percorsi);
        Assert.DoesNotContain("/download/?per=x64", _server.Percorsi);
    }

    [Fact]
    public async Task LaVersioneGiaInstallataNonSiRipropone()
    {
        _server.Rimanda("x64", PacchettoX64);

        var esito = await Servizio(installata: new Version(1, 0, 21, 0)).CheckAsync();

        Assert.False(esito.Failed, esito.Error);
        Assert.False(esito.HasUpdate);

        // Nessun aggiornamento, nessuna lettura delle novita': il controllo all'avvio
        // resta di poche centinaia di byte.
        Assert.Equal(["/download/?per=x64"], _server.Percorsi);
    }

    [Fact]
    public async Task SenzaFileDelleNovitaLAggiornamentoSiProponeLostesso()
    {
        // Il 404 e' l'equivalente del 550 di FTP: il file non e' stato pubblicato. Le
        // novita' mancano, e l'impronta mancante la gestisce poi lo scaricamento.
        _server.Rimanda("x64", PacchettoX64);

        var esito = await Servizio().CheckAsync();

        Assert.False(esito.Failed, esito.Error);
        Assert.True(esito.HasUpdate);
        Assert.Empty(esito.Notes.Features);
        Assert.Null(esito.Notes.Sha256);
    }

    [Fact]
    public async Task DelRimandoSiTieneSoloIlNomeDelFile()
    {
        // Il rimando indica un altro server: le novita' si leggono comunque dalla
        // cartella configurata, e da li' si scaricheranno firma e pacchetto.
        _server.Rimanda("x64", "https://altro-server.invalid/altrove/" + PacchettoX64);
        _server.Pubblica("BlindPhone-1.0.21-x64.txt", "- Dalla cartella giusta\n");

        var esito = await Servizio().CheckAsync();

        Assert.False(esito.Failed, esito.Error);
        Assert.Equal(PacchettoX64, esito.Update!.InstallerFileName);
        Assert.Equal(["Dalla cartella giusta"], esito.Notes.Features);
    }

    [Fact]
    public async Task UnRimandoCheNonPortaAUnPacchettoEUnGuastoDaDire()
    {
        // E' cio' che fa il portale con un processore che non conosce: rimanda alla
        // pagina. «Nessun aggiornamento» lascerebbe la postazione indietro in silenzio.
        _server.Rimanda("x64", "../#scarica");

        var esito = await Servizio().CheckAsync();

        Assert.True(esito.Failed);
        Assert.False(esito.HasUpdate);
        Assert.Contains("x64", esito.Error);
    }

    [Fact]
    public async Task UnRimandoAlPacchettoDiUnAltroProcessoreNonSiPropone()
    {
        _server.Rimanda("x64", PacchettoArm64);

        var esito = await Servizio().CheckAsync();

        Assert.True(esito.Failed);
        Assert.Contains(PacchettoArm64, esito.Error);
    }

    [Fact]
    public async Task UnaPaginaAlPostoDelRimandoSiDiceTale()
    {
        // Un indirizzo che porta alla pagina del sito invece che alla cartella dei
        // download: il server risponde, ma non con un pacchetto.
        _server.Risposte["/download/?per=x64"] = new ServerAggiornamentiFinto.Risposta(
            200, Corpo: Encoding.UTF8.GetBytes("<html>Benvenuti</html>"));

        var esito = await Servizio().CheckAsync();

        Assert.True(esito.Failed);
        Assert.Contains("pagina", esito.Error);
    }

    [Fact]
    public async Task UnaCartellaInesistenteSiDiceTale()
    {
        var esito = await Servizio().CheckAsync();

        Assert.True(esito.Failed);
        Assert.Equal("La cartella degli aggiornamenti non esiste sul server", esito.Error);
    }

    [Fact]
    public async Task LeCredenzialiViaggianoSoloSeCiSono()
    {
        _server.Rimanda("x64", PacchettoX64);

        await Servizio().CheckAsync();
        Assert.DoesNotContain(_server.Richieste, r => r.Intestazioni.ContainsKey("Authorization"));

        await Servizio(utente: "postazione", password: "segreto").CheckAsync();

        var atteso = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("postazione:segreto"));
        Assert.Contains(_server.Richieste, r => r.Intestazioni.GetValueOrDefault("Authorization") == atteso);
    }

    [Fact]
    public async Task IlServerVedeIlProgrammaENonLaPersona()
    {
        _server.Rimanda("x64", PacchettoX64);

        await Servizio().CheckAsync();

        var agente = _server.Richieste.First().Intestazioni["User-Agent"];
        Assert.Equal("BlindPhone/1.0.20.2", agente);
    }

    // -----------------------------------------------------------------------
    // Lo scaricamento
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SenzaFirmaIlPacchettoNonSiScaricaNemmeno()
    {
        _server.Rimanda("x64", PacchettoX64);
        _server.Pubblica(PacchettoX64, new byte[4096]);

        var servizio = Servizio();
        var esito = await servizio.CheckAsync();

        var errore = await Assert.ThrowsAsync<InvalidOperationException>(
            () => servizio.DownloadAsync(esito.Update!, esito.Notes));

        Assert.Contains("manca la sua firma", errore.Message);
        Assert.Contains("/download/BlindPhone-1.0.21-x64.msi.sig", _server.Percorsi);
        Assert.DoesNotContain("/download/BlindPhone-1.0.21-x64.msi", _server.Percorsi);
    }

    [Fact]
    public async Task UnaFirmaSbagliataFaScartareIlPacchettoScaricato()
    {
        var contenuto = new byte[300_000];
        Random.Shared.NextBytes(contenuto);

        _server.Rimanda("x64", PacchettoX64);
        _server.Pubblica(PacchettoX64, contenuto);
        _server.Pubblica(PacchettoX64 + ".sig", new byte[72]);

        var servizio = Servizio();
        var esito = await servizio.CheckAsync();
        var avanzamento = new Avanzamento();

        var errore = await Assert.ThrowsAsync<InvalidOperationException>(
            () => servizio.DownloadAsync(esito.Update!, esito.Notes, avanzamento));

        Assert.Contains("non porta la firma di BlindPhone", errore.Message);

        // Il pacchetto e' arrivato tutto, dalla cartella, e poi e' stato buttato.
        Assert.Contains("/download/BlindPhone-1.0.21-x64.msi", _server.Percorsi);
        Assert.Contains(100, avanzamento.Valori);

        Assert.False(File.Exists(Path.Combine(_scaricamenti, PacchettoX64 + ".part")));
        Assert.False(File.Exists(Path.Combine(_scaricamenti, PacchettoX64)));

        // E non resta nemmeno un pacchetto «in attesa» da proporre al riavvio.
        Assert.Null(servizio.FindDownloaded());
    }

    [Fact]
    public async Task UnPacchettoSparitoDopoIlControlloSiDiceInItaliano()
    {
        // La firma c'e', il pacchetto no: il messaggio e' quello che la finestra legge
        // all'operatore, e non deve essere quello grezzo di .NET.
        _server.Rimanda("x64", PacchettoX64);
        _server.Pubblica(PacchettoX64 + ".sig", new byte[72]);

        var servizio = Servizio();
        var esito = await servizio.CheckAsync();

        var errore = await Assert.ThrowsAsync<InvalidOperationException>(
            () => servizio.DownloadAsync(esito.Update!, esito.Notes));

        Assert.StartsWith("Scaricamento del pacchetto non riuscito.", errore.Message);
        Assert.Contains("non e' pubblicato sul server", errore.Message);
    }
}
