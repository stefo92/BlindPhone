using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che ALT+F4 chiuda davvero BlindPhone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto che queste prove impediscono di far tornare.</b> La finestra
/// principale converte la chiusura in un "metti via" nell'area di notifica, perché un
/// softphone chiuso per sbaglio si manifesta come chiamanti che squillano a vuoto.
/// Fino alla correzione la conversione prendeva anche ALT+F4, che arriva allo stesso
/// gestore del clic sulla X. Il risultato era il peggiore dei due esiti possibili: il
/// programma restava acceso e chi aveva premuto credeva di averlo chiuso.
/// </para>
/// <para>
/// Per chi non vede lo schermo ALT+F4 non è una scorciatoia fra le altre: è l'unico
/// modo di chiudere una finestra, perché la X va cercata con il mouse. La protezione
/// dalla chiusura accidentale resta dov'è utile — sul gesto che si compie per sbaglio
/// — e il gesto deliberato torna a fare quello che fa in ogni altro programma.
/// </para>
/// <para>
/// Le prove leggono il sorgente invece di premere i tasti: costruire la finestra
/// principale richiederebbe l'intera catena dei servizi — telefonia, audio, rubrica —
/// e fabbricare un <c>KeyEventArgs</c> di sistema credibile richiede una sorgente di
/// presentazione viva. È lo stesso criterio di <see cref="FinestreInAltTabTests"/>.
/// </para>
/// </remarks>
public sealed class ChiusuraConAltF4Tests
{
    private static string Sorgente(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(directory.FullName, "src", "BlindPhone.App", file);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Sorgente {file} non trovato");
    }

    /// <summary>Il corpo di <c>OnWindowPreviewKeyDown</c>, dove si decide l'ordine.</summary>
    private static string InstradamentoDeiTasti()
    {
        var codice = Sorgente("MainWindow.xaml.cs");

        var inizio = codice.IndexOf(
            "private void OnWindowPreviewKeyDown", StringComparison.Ordinal);
        Assert.True(inizio > 0, "OnWindowPreviewKeyDown non esiste piu' in MainWindow");

        var fine = codice.IndexOf("private bool TryHandleDtmfKey", StringComparison.Ordinal);
        Assert.True(fine > inizio, "La fine di OnWindowPreviewKeyDown non e' piu' riconoscibile");

        return codice[inizio..fine];
    }

    [Fact]
    public void AltF4PortaAllUscitaENonAllAreaDiNotifica()
    {
        var instradamento = InstradamentoDeiTasti();

        var guardia = instradamento.IndexOf("IsCloseWindowCombination(e)", StringComparison.Ordinal);
        Assert.True(guardia > 0, "ALT+F4 non viene piu' intercettato: tornerebbe a mettere via la finestra");

        var ramo = instradamento[guardia..];
        var uscita = ramo.IndexOf("QuitApplication();", StringComparison.Ordinal);

        Assert.True(uscita > 0, "ALT+F4 non chiama piu' l'uscita vera");

        // Senza «Handled» Windows chiuderebbe comunque per conto suo: la chiusura
        // tornerebbe al gestore che la devia nell'area di notifica, e si perderebbe
        // anche il caso in cui la conferma viene annullata e la finestra deve restare.
        var consumato = ramo.IndexOf("e.Handled = true;", StringComparison.Ordinal);

        Assert.True(consumato > 0 && consumato < uscita,
            "ALT+F4 deve essere consumato prima di uscire, altrimenti la chiusura predefinita prosegue");
    }

    [Theory]
    [InlineData("_dtmfMode", "in modalita' toni ALT+F4 smetterebbe di chiudere")]
    [InlineData("TryHandleSimplifiedKeypad", "in modalita' semplificata ALT+F4 smetterebbe di chiudere")]
    [InlineData("_duty.IsPaused", "con la postazione in pausa ALT+F4 smetterebbe di chiudere")]
    public void AltF4VienePrimaDiOgniAltroInstradamento(string successivo, string perche)
    {
        // Non e' pedanteria sull'ordine: ognuno di questi rami puo' consumare il tasto
        // e rientrare. Chi preme ALT+F4 si aspetta che chiuda sempre, e un gesto di
        // sistema che funziona solo in certi stati e' peggio di uno che non funziona
        // mai, perche' non si impara che non si puo' fare.
        var instradamento = InstradamentoDeiTasti();

        var altF4 = instradamento.IndexOf("IsCloseWindowCombination(e)", StringComparison.Ordinal);
        var altro = instradamento.IndexOf(successivo, StringComparison.Ordinal);

        Assert.True(altro > 0, $"{successivo} non si trova piu' nell'instradamento dei tasti");
        Assert.True(altF4 < altro, perche);
    }

    [Fact]
    public void ServeAltPremuto()
    {
        // F4 da solo e' un tasto che un operatore puo' premere cercando F3 o F5.
        // Chiudere la postazione per quello sarebbe un difetto peggiore di quello che
        // si stava correggendo, e il riconoscimento deve pretendere il modificatore.
        var codice = Sorgente("MainWindow.xaml.cs");

        var inizio = codice.IndexOf(
            "private static bool IsCloseWindowCombination", StringComparison.Ordinal);
        Assert.True(inizio > 0, "Il riconoscimento di ALT+F4 non esiste piu'");

        var corpo = codice[inizio..Math.Min(codice.Length, inizio + 400)];

        Assert.Contains("Key.F4", corpo, StringComparison.Ordinal);
        Assert.Contains("ModifierKeys.Alt", corpo, StringComparison.Ordinal);

        // Le combinazioni con Alt arrivano sotto il nome di sistema: guardare il solo
        // e.Key non troverebbe mai ALT+F4. E' lo stesso difetto per cui F10 assegnato
        // alla rubrica non la apriva.
        Assert.Contains("e.SystemKey", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public void IlRilascioNonLasciaIlCursoreSullaBarraDeiMenu()
    {
        // Quando la conferma di uscita viene annullata la finestra resta aperta, e il
        // rilascio di un tasto di sistema non consumato manda Windows in modalita'
        // menu: il cursore salta sul titolo della finestra. E' il sintomo gia' visto
        // con F10, e qui arriverebbe subito dopo un annullamento, cioe' nel momento in
        // cui l'operatore sta tornando a una chiamata.
        var codice = Sorgente("MainWindow.xaml.cs");

        var inizio = codice.IndexOf("private void OnWindowPreviewKeyUp", StringComparison.Ordinal);
        Assert.True(inizio > 0, "OnWindowPreviewKeyUp non esiste piu'");

        var corpo = codice[inizio..Math.Min(codice.Length, inizio + 800)];

        Assert.Contains("IsCloseWindowCombination(e)", corpo, StringComparison.Ordinal);
    }
}
