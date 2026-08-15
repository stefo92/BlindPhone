using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica come si riconosce un aggiornamento fra i file di una cartella remota.
/// </summary>
/// <remarks>
/// <para>
/// È la parte che decide <b>se</b> aggiornare, e sbagliarla ha due conseguenze
/// entrambe silenziose: proporre in eterno un aggiornamento già installato, oppure
/// non proporne mai nessuno e lasciare le postazioni indietro senza che nessuno se ne
/// accorga. Nessuna delle due si nota provando l'applicazione una volta.
/// </para>
/// <para>
/// Sta tutto qui, senza rete: la cartella remota è un elenco di nomi, e l'elenco lo
/// si può scrivere a mano.
/// </para>
/// </remarks>
public sealed class UpdateCatalogTests
{
    private static readonly Version Installata = new(1, 0, 0);

    // -----------------------------------------------------------------------
    // Riconoscimento della versione dal nome
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("BlindPhone-1.2.0-x64.msi", "1.2.0")]
    [InlineData("BlindPhone-1.2-x64.msi", "1.2")]
    [InlineData("BlindPhone-2.0.0.3-x64.msi", "2.0.0.3")]
    [InlineData("BlindPhone-1.2.0.msi", "1.2.0")]
    [InlineData("blindphone-1.2.0-X64.MSI", "1.2.0")]
    public void LaVersioneSiLeggeDalNomeDelPacchetto(string nome, string atteso)
    {
        Assert.Equal(Version.Parse(atteso), UpdateCatalog.ParseVersion(nome));
    }

    [Theory]
    [InlineData("BlindPhone-1.2.0-x64.txt")]
    [InlineData("BlindPhone.msi")]
    [InlineData("AltroProgramma-1.2.0-x64.msi")]
    [InlineData("BlindPhone-beta-x64.msi")]
    [InlineData("BlindPhone-1-x64.msi")]
    [InlineData("")]
    public void CioCheNonEUnPacchettoVieneIgnorato(string nome)
    {
        // Nella cartella remota ci sono anche le note, i file di simboli e quello che
        // l'amministratore ci lascia: nessuno di questi deve diventare un
        // aggiornamento da proporre.
        Assert.Null(UpdateCatalog.ParseVersion(nome));
    }

    // -----------------------------------------------------------------------
    // Scelta dell'aggiornamento
    // -----------------------------------------------------------------------

    [Fact]
    public void SiProponeSoloUnaVersionePiuRecente()
    {
        string[] cartella = ["BlindPhone-1.2.0-x64.msi", "BlindPhone-1.2.0-x64.txt"];

        var trovato = UpdateCatalog.FindNewer(cartella, Installata);

        Assert.NotNull(trovato);
        Assert.Equal(new Version(1, 2, 0), trovato.Version);
        Assert.Equal("BlindPhone-1.2.0-x64.msi", trovato.InstallerFileName);
    }

    [Fact]
    public void LaStessaVersioneInstallataNonSiPropone()
    {
        string[] cartella = ["BlindPhone-1.0.0-x64.msi"];

        Assert.Null(UpdateCatalog.FindNewer(cartella, Installata));
    }

    [Fact]
    public void UnaVersionePiuVecchiaNonSiPropone()
    {
        // Succede sulle postazioni di prova, con una versione piu' avanti di quella
        // pubblicata: proporre il «ritorno» significherebbe proporre di disinstallare
        // la prova in corso, e l'installatore di Windows lo rifiuterebbe comunque.
        string[] cartella = ["BlindPhone-0.9.0-x64.msi", "BlindPhone-1.0.0-x64.msi"];

        Assert.Null(UpdateCatalog.FindNewer(cartella, Installata));
    }

    [Fact]
    public void FraPiuPacchettiSiPrendeIlPiuRecente()
    {
        // Una cartella che conserva lo storico e' la norma, ed e' anche cio' che
        // permette di tornare indietro reinstallando a mano.
        string[] cartella =
        [
            "BlindPhone-1.1.0-x64.msi",
            "BlindPhone-1.10.0-x64.msi",
            "BlindPhone-1.2.0-x64.msi",
        ];

        var trovato = UpdateCatalog.FindNewer(cartella, Installata);

        // 1.10 e' piu' recente di 1.2: confrontando i nomi come testo sarebbe il
        // contrario, ed e' l'errore che questa prova esiste per impedire.
        Assert.Equal(new Version(1, 10, 0), trovato!.Version);
    }

    [Fact]
    public void IlPercorsoDavantiAlNomeNonDisturba()
    {
        // Alcuni server elencano la cartella con il percorso completo.
        string[] cartella = ["/blindphone/aggiornamenti/BlindPhone-1.2.0-x64.msi"];

        var trovato = UpdateCatalog.FindNewer(cartella, Installata);

        Assert.Equal("BlindPhone-1.2.0-x64.msi", trovato!.InstallerFileName);
    }

    [Fact]
    public void LeNoteSiTrovanoDalNomeDelPacchetto()
    {
        // E' tutto il meccanismo di abbinamento: stesso nome, estensione diversa.
        var trovato = UpdateCatalog.FindNewer(["BlindPhone-1.2.0-x64.msi"], Installata);

        Assert.Equal("BlindPhone-1.2.0-x64.txt", trovato!.NotesFileName);
    }

    [Fact]
    public void UnaCartellaVuotaNonProponeNulla()
    {
        Assert.Null(UpdateCatalog.FindNewer([], Installata));
    }

    // -----------------------------------------------------------------------
    // File delle novità
    // -----------------------------------------------------------------------

    [Fact]
    public void LeNovitaSiLeggonoUnaPerRiga()
    {
        var note = UpdateNotes.Parse("""
            - Suoneria che squilla fino alla risposta
            - Barra dei comandi per il mouse
            Trasferimento cieco e assistito con due pulsanti
            """);

        Assert.Equal(3, note.Features.Count);
        Assert.Equal("Suoneria che squilla fino alla risposta", note.Features[0]);
        Assert.Equal("Trasferimento cieco e assistito con due pulsanti", note.Features[2]);
    }

    [Fact]
    public void RigheVuoteECommentiNonCompaiono()
    {
        var note = UpdateNotes.Parse("# versione 1.2.0, pubblicata il 9 agosto\n\n- Prima novita'\n\n");

        Assert.Single(note.Features);
        Assert.Equal("Prima novita'", note.Features[0]);
    }

    [Fact]
    public void LImprontaSiRiconosceENonFinisceFraLeNovita()
    {
        var note = UpdateNotes.Parse("- Una novita'\nSHA256: ABC123DEF456\n");

        Assert.Equal("ABC123DEF456", note.Sha256);
        Assert.Single(note.Features);
    }

    [Fact]
    public void SenzaImprontaLeNoteRestanoValide()
    {
        // La riga e' facoltativa: un file senza impronta non deve far fallire la
        // lettura delle novita'.
        var note = UpdateNotes.Parse("- Una novita'");

        Assert.Null(note.Sha256);
        Assert.Single(note.Features);
    }

    [Fact]
    public void UnFileVuotoNonEUnErrore()
    {
        Assert.Empty(UpdateNotes.Parse("").Features);
        Assert.Empty(UpdateNotes.Parse(null).Features);
        Assert.Empty(UpdateNotes.Parse("   \n\n  ").Features);
    }

    [Fact]
    public void LeRigheDiWindowsSiLeggonoComeQuelleDiUnix()
    {
        // Il file lo scrive una persona su Windows: i ritorni a capo sono CR LF, e un
        // CR lasciato in coda comparirebbe a schermo come un carattere strano.
        var note = UpdateNotes.Parse("- Prima\r\n- Seconda\r\n");

        Assert.Equal(["Prima", "Seconda"], note.Features);
    }

    [Fact]
    public void LaVersioneSiMostraSenzaZeriInutili()
    {
        var due = new AvailableUpdate(new Version(1, 2), "a.msi", "a.txt");
        var tre = new AvailableUpdate(new Version(1, 2, 3), "a.msi", "a.txt");

        Assert.Equal("1.2", due.DisplayVersion);
        Assert.Equal("1.2.3", tre.DisplayVersion);
    }
}
