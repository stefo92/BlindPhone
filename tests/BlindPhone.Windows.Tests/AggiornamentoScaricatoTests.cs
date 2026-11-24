using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica il pacchetto scaricato con «Scarica e installa successivamente», che va
/// ritrovato al riavvio per chiedere se installarlo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lo stato è la cartella degli scaricamenti.</b> Un pacchetto con la sua firma
/// accanto e una versione più alta di quella installata è un aggiornamento in attesa;
/// tutto il resto — la versione già installata, quella superata, lo scaricamento
/// rimasto a metà — è un resto da togliere. Sbagliare in un senso ripropone a ogni
/// avvio un pacchetto che non serve più; nell'altro, la domanda che l'operatore
/// aspetta non arriva.
/// </para>
/// <para>
/// Queste prove scrivono i file a mano in una cartella propria: la firma non si
/// verifica qui, ma al momento dell'installazione, sul file tenuto aperto.
/// </para>
/// </remarks>
public sealed class AggiornamentoScaricatoTests : IDisposable
{
    private static readonly Version Installata = new(1, 0, 24);

    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "BlindPhone.Test", $"scaricati-{Guid.NewGuid():N}");

    public AggiornamentoScaricatoTests() => Directory.CreateDirectory(_cartella);

    public void Dispose()
    {
        if (Directory.Exists(_cartella)) Directory.Delete(_cartella, recursive: true);
    }

    private UpdateService Servizio(Version? installata = null, string processore = "x64") => new(
        new UpdateSettings(),
        password: string.Empty,
        installata ?? Installata,
        architecture: processore,
        downloadDirectory: _cartella);

    /// <summary>Deposita un pacchetto come lo lascia lo scaricamento: pacchetto, firma e novità.</summary>
    private string Deposita(string pacchetto, bool conFirma = true, string? novita = "- Una novita'\n")
    {
        var percorso = Path.Combine(_cartella, pacchetto);
        File.WriteAllBytes(percorso, new byte[1024]);

        if (conFirma) File.WriteAllBytes(percorso + ".sig", new byte[72]);
        if (novita is not null) File.WriteAllText(Path.Combine(_cartella, UpdateCatalog.NotesFileNameFor(pacchetto)), novita);

        return percorso;
    }

    private string Percorso(string nome) => Path.Combine(_cartella, nome);

    // -----------------------------------------------------------------------
    // Ritrovare il pacchetto
    // -----------------------------------------------------------------------

    [Fact]
    public void UnPacchettoPiuNuovoConLaSuaFirmaSiRitrovaConLeNovita()
    {
        var percorso = Deposita("BlindPhone-1.0.25-x64.msi", novita: "- Prima\n- Seconda\nSHA256: ABC\n");

        var scaricato = Servizio().FindDownloaded();

        Assert.NotNull(scaricato);
        Assert.Equal(new Version(1, 0, 25), scaricato.Update.Version);
        Assert.Equal(percorso, scaricato.PackagePath);
        Assert.Equal(["Prima", "Seconda"], scaricato.Notes.Features);
        Assert.Equal("ABC", scaricato.Notes.Sha256);
    }

    [Fact]
    public void SenzaFirmaIlPacchettoNonSiPropone()
    {
        // Senza .sig l'installazione verrebbe rifiutata comunque: chiedere
        // «vuoi installarlo?» per poi dire di no sarebbe una domanda finta.
        Deposita("BlindPhone-1.0.25-x64.msi", conFirma: false);

        Assert.Null(Servizio().FindDownloaded());
    }

    [Fact]
    public void LaVersioneGiaInstallataNonSiPropone()
    {
        Deposita("BlindPhone-1.0.24-x64.msi");
        Deposita("BlindPhone-1.0.23-x64.msi");

        Assert.Null(Servizio().FindDownloaded());
    }

    [Fact]
    public void LoScaricamentoAMetaNonEUnPacchetto()
    {
        File.WriteAllBytes(Percorso("BlindPhone-1.0.25-x64.msi.part"), new byte[512]);
        File.WriteAllBytes(Percorso("BlindPhone-1.0.25-x64.msi.sig"), new byte[72]);

        Assert.Null(Servizio().FindDownloaded());
    }

    [Fact]
    public void FraDuePacchettiSiProponeIlPiuRecente()
    {
        Deposita("BlindPhone-1.0.25-x64.msi");
        Deposita("BlindPhone-1.0.26-x64.msi");

        Assert.Equal(new Version(1, 0, 26), Servizio().FindDownloaded()?.Update.Version);
    }

    [Fact]
    public void IlPacchettoDiUnAltroProcessoreNonSiPropone()
    {
        Deposita("BlindPhone-1.0.25-arm64.msi");

        Assert.Null(Servizio(processore: "x64").FindDownloaded());
        Assert.NotNull(Servizio(processore: "arm64").FindDownloaded());
    }

    [Fact]
    public void SenzaNovitaIlPacchettoSiProponeLostesso()
    {
        Deposita("BlindPhone-1.0.25-x64.msi", novita: null);

        var scaricato = Servizio().FindDownloaded();

        Assert.NotNull(scaricato);
        Assert.Empty(scaricato.Notes.Features);
    }

    [Fact]
    public void UnaCartellaCheNonEsisteVuolDireNessunPacchetto()
    {
        Directory.Delete(_cartella, recursive: true);

        Assert.Null(Servizio().FindDownloaded());
    }

    // -----------------------------------------------------------------------
    // Pulizia all'avvio
    // -----------------------------------------------------------------------

    [Fact]
    public void LaPuliziaTieneSoloIlPacchettoDaProporre()
    {
        Deposita("BlindPhone-1.0.23-x64.msi");
        Deposita("BlindPhone-1.0.24-x64.msi");
        Deposita("BlindPhone-1.0.25-x64.msi");
        Deposita("BlindPhone-1.0.26-x64.msi");
        File.WriteAllBytes(Percorso("BlindPhone-1.0.27-x64.msi.part"), new byte[512]);

        Servizio().PurgeStaleDownloads();

        Assert.True(File.Exists(Percorso("BlindPhone-1.0.26-x64.msi")));
        Assert.True(File.Exists(Percorso("BlindPhone-1.0.26-x64.msi.sig")));
        Assert.True(File.Exists(Percorso("BlindPhone-1.0.26-x64.txt")));

        foreach (var vecchio in new[] { "1.0.23", "1.0.24", "1.0.25" })
        {
            Assert.False(File.Exists(Percorso($"BlindPhone-{vecchio}-x64.msi")));
            Assert.False(File.Exists(Percorso($"BlindPhone-{vecchio}-x64.msi.sig")));
            Assert.False(File.Exists(Percorso($"BlindPhone-{vecchio}-x64.txt")));
        }

        Assert.False(File.Exists(Percorso("BlindPhone-1.0.27-x64.msi.part")));
    }

    [Fact]
    public void LaPuliziaNonToccaQuelloCheNonEUnPacchettoDiBlindPhone()
    {
        // Nessun carattere jolly che si allarghi a quello che qualcuno deposita nella
        // cartella.
        File.WriteAllText(Percorso("appunti.txt"), "da tenere");
        File.WriteAllBytes(Percorso("AltroProgramma-1.0.msi"), new byte[16]);

        Servizio().PurgeStaleDownloads();

        Assert.True(File.Exists(Percorso("appunti.txt")));
        Assert.True(File.Exists(Percorso("AltroProgramma-1.0.msi")));
    }

    [Fact]
    public void UnPacchettoScartatoSparisceConFirmaENovita()
    {
        Deposita("BlindPhone-1.0.25-x64.msi");
        var servizio = Servizio();

        servizio.DiscardDownloaded(servizio.FindDownloaded()!);

        Assert.Empty(Directory.GetFiles(_cartella));
        Assert.Null(servizio.FindDownloaded());
    }

    // -----------------------------------------------------------------------
    // Le novità salvate accanto al pacchetto
    // -----------------------------------------------------------------------

    [Fact]
    public void LeNovitaSalvateSiRileggonoUguali()
    {
        var note = new UpdateNotes(["Prima novita'", "Seconda: con i due punti"], "9F86D081");

        var rilette = UpdateNotes.Parse(note.Format());

        Assert.Equal(note.Features, rilette.Features);
        Assert.Equal(note.Sha256, rilette.Sha256);
    }

    [Fact]
    public void NessunaNovitaSiSalvaComeUnFileVuoto()
    {
        Assert.Equal(string.Empty, UpdateNotes.Empty.Format());
        Assert.Empty(UpdateNotes.Parse(UpdateNotes.Empty.Format()).Features);
    }
}
