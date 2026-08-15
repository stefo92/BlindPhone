using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che la finestra, quando torna davanti, prenda anche la tastiera.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto nasceva da un valore ignorato.</b> <c>Activate</c> restituisce se
/// Windows ha concesso il primo piano, e Windows lo nega a chi non ha ricevuto
/// l'ultimo evento di input — una chiamata che arriva dalla rete non lo è. Il
/// passaggio per <c>Topmost</c> intanto faceva comparire la finestra, perché l'ordine
/// di sovrapposizione non richiede permessi: si vedeva, e i tasti andavano altrove.
/// </para>
/// <para>
/// Misurato con due processi: con la sola coppia <c>Activate</c> più <c>Topmost</c>
/// la finestra risultava visibile, il primo piano restava all'altro processo e un F2
/// inviato con <c>SendInput</c> non arrivava alla finestra. Con l'attivazione
/// completa il primo piano passava e lo stesso F2 arrivava.
/// </para>
/// </remarks>
public sealed class AttivazioneFinestraTests
{
    private static string Sorgente(string nome)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(directory.FullName, "src", "BlindPhone.App", nome);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{nome} non trovato");
    }

    [Fact]
    public void LEsitoDellAttivazioneNonPuoEssereIgnorato()
    {
        // La firma che restituisce l'esito e' cio' che impedisce al difetto di
        // tornare: finche' RestoreWindow non diceva se il primo piano era arrivato,
        // nessuno poteva accorgersi che non lo era.
        var codice = Sorgente("MainWindow.xaml.cs");

        Assert.Contains("private bool RestoreWindow(string? reason)", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void SullaChiamataEntranteLEsitoVieneGuardato()
    {
        // E' il solo punto in cui il primo piano arriva senza che l'operatore abbia
        // premuto niente, quindi il solo in cui Windows lo puo' negare.
        var codice = Sorgente("MainWindow.xaml.cs");

        Assert.Contains("if (!RestoreWindow(null)) AnnounceForegroundDenied();", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void LeCodeDiInputVengonoSempreSganciate()
    {
        // Due code di input agganciate e mai sciolte sono un guasto peggiore di una
        // finestra che non prende il primo piano: il finally non e' uno stile, e'
        // la condizione per poter usare AttachThreadInput.
        var codice = Sorgente("ForegroundActivator.cs");

        var aggancio = codice.IndexOf("AttachThreadInput(altrui, nostro, true)", StringComparison.Ordinal);
        var finally_ = codice.IndexOf("finally", StringComparison.Ordinal);
        var sgancio = codice.IndexOf("AttachThreadInput(altrui, nostro, false)", StringComparison.Ordinal);

        Assert.True(aggancio > 0 && finally_ > aggancio && sgancio > finally_,
            "Lo sgancio delle code di input deve stare in un blocco finally dopo l'aggancio.");
    }

    [Fact]
    public void UnaFinestraBloccataNonVieneAgganciata()
    {
        // Agganciarsi a un processo che non risponde puo' fermare anche il posto
        // operatore, e succederebbe mentre il telefono squilla.
        var codice = Sorgente("ForegroundActivator.cs");

        Assert.Contains("IsHungAppWindow(foreground)", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void LEsitoVieneVerificatoSulSistemaENonSupposto()
    {
        // SetForegroundWindow puo' fallire in silenzio: l'unica prova che il primo
        // piano sia arrivato e' richiederlo al sistema dopo averlo chiesto.
        var codice = Sorgente("ForegroundActivator.cs");

        Assert.Contains("return GetForegroundWindow() == handle;", codice, StringComparison.Ordinal);
    }
}
