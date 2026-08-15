using BlindPhone.Contacts;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Che cosa viene detto quando un file di rubrica non si lascia leggere.
/// </summary>
/// <remarks>
/// <para>
/// Segnalazione del 10 agosto 2026: «l'importazione file CSV si è rotta». Il file era
/// aperto in Excel. La lettura delle intestazioni inghiottiva l'eccezione e
/// restituiva un elenco vuoto, e la procedura guidata concludeva «il file non
/// contiene una riga di intestazione riconoscibile».
/// </para>
/// <para>
/// Nessuna delle due cose era vera, e per un operatore non vedente la frase
/// pronunciata è tutto ciò che ha: non può dare un'occhiata al file per smentirla.
/// </para>
/// </remarks>
public sealed class FileNonLeggibileTests : IDisposable
{
    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "blindphone-lock-" + Guid.NewGuid().ToString("N"));

    public FileNonLeggibileTests() => Directory.CreateDirectory(_cartella);

    public void Dispose()
    {
        try { Directory.Delete(_cartella, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void UnFileApertoInUnAltroProgrammaLoDice()
    {
        var percorso = Path.Combine(_cartella, "rubrica.csv");
        File.WriteAllText(percorso, "numero,nome\n4021,Mario\n");

        // Excel tiene i CSV aperti in esclusiva: si riproduce con la stessa
        // condivisione negata.
        using var blocco = File.Open(percorso, FileMode.Open, FileAccess.Read, FileShare.None);

        var errore = Assert.ThrowsAny<IOException>(() => CsvContactSource.ReadHeaders(percorso));

        Assert.True(ContactFileErrors.IsLocked(errore));

        var frase = ContactFileErrors.Describe(errore);

        // La frase deve nominare la causa vera e dire la mossa successiva.
        Assert.Contains("aperto in un altro programma", frase);
        Assert.DoesNotContain("intestazione", frase);
    }

    [Fact]
    public void UnFileCheNonEsisteNonDiventaUnProblemaDiFormato()
    {
        var percorso = Path.Combine(_cartella, "assente.csv");

        var errore = Assert.ThrowsAny<IOException>(() => CsvContactSource.ReadHeaders(percorso));

        Assert.Contains("non esiste", ContactFileErrors.Describe(errore));
    }

    [Fact]
    public void UnFileLeggibileRestituisceLeIntestazioni()
    {
        // La contromisura non deve trasformare in errore un file che sta benissimo.
        var percorso = Path.Combine(_cartella, "buono.csv");
        File.WriteAllText(percorso, "numero,nome,cognome\n4021,Mario,Rossi\n");

        Assert.Equal(["numero", "nome", "cognome"], CsvContactSource.ReadHeaders(percorso));
    }

    [Fact]
    public void UnFileVuotoRestaUnElencoVuotoSenzaEccezione()
    {
        // Un file leggibile ma senza intestazioni: qui l'elenco vuoto e' la risposta
        // giusta, ed e' l'unico caso in cui lo e'.
        var percorso = Path.Combine(_cartella, "vuoto.csv");
        File.WriteAllText(percorso, string.Empty);

        Assert.Empty(CsvContactSource.ReadHeaders(percorso));
    }
}
