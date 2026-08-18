using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che le finestre in cui l'operatore si ferma a lavorare restino
/// raggiungibili con ALT+TAB.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto che queste prove impediscono di far tornare.</b> Impostazioni, ultime
/// chiamate, guida e rubrica nascevano con <c>ShowInTaskbar="False"</c>, che è il valore
/// predefinito dei dialoghi. Una finestra di proprietà di un'altra compare nell'elenco
/// di ALT+TAB solo se porta lo stile esteso <c>WS_EX_APPWINDOW</c>, e WPF glielo mette
/// quando <c>ShowInTaskbar</c> è vero: senza, quelle finestre sparivano da ALT+TAB e
/// dalla barra delle applicazioni.
/// </para>
/// <para>
/// Misurato su finestre WPF costruite apposta: di proprietà e con
/// <c>ShowInTaskbar="False"</c> risultano <c>TOOLWINDOW=False, APPWINDOW=False</c>,
/// cioè fuori da ALT+TAB; con <c>True</c> arriva <c>APPWINDOW=True</c> e rientrano.
/// </para>
/// <para>
/// <b>Perché conta più per chi non vede lo schermo.</b> Chi guarda ritrova una finestra
/// perduta con il mouse, o dalla barra delle applicazioni. Chi non guarda ha ALT+TAB e
/// poco altro: una finestra che non è in quell'elenco, ma esiste e tiene il fuoco da
/// qualche parte, è un vicolo cieco da cui si esce solo chiudendo e riaprendo. E ci si
/// finisce senza fare niente di sbagliato — basta una chiamata entrante, che porta
/// avanti la finestra principale da sola.
/// </para>
/// <para>
/// La prova legge il sorgente XAML invece di costruire le finestre: costruirle
/// richiederebbe l'intera catena di dipendenze di ciascuna, e la regola da difendere è
/// una dichiarazione, non un comportamento a tempo di esecuzione.
/// </para>
/// </remarks>
public sealed class FinestreInAltTabTests
{
    /// <summary>
    /// Le finestre in cui l'operatore si ferma, e che quindi può voler lasciare e
    /// ritrovare.
    /// </summary>
    /// <remarks>
    /// Non ci sono i dialoghi momentanei — cattura di una combinazione, conferma di
    /// salvataggio, sblocco — e la ragione è che vanno chiusi rispondendo, non
    /// abbandonati. Quello della cattura, in particolare, deve restare fuori: il suo
    /// mestiere è leggere combinazioni di tasti, e ALT+TAB sarebbe una di quelle.
    /// </remarks>
    public static TheoryData<string> FinestreDiLavoro =>
    [
        "SettingsWindow",
        "CallHistoryWindow",
        "HelpWindow",
        "ContactsWindow",
    ];

    private static string Xaml(string finestra)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(
                directory.FullName, "src", "BlindPhone.App", finestra + ".xaml");

            if (File.Exists(candidato)) return File.ReadAllText(candidato);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Sorgente XAML di {finestra} non trovato");
    }

    [Theory]
    [MemberData(nameof(FinestreDiLavoro))]
    public void LeFinestreDiLavoroRestanoInAltTab(string finestra)
    {
        var xaml = Xaml(finestra);

        Assert.Contains("ShowInTaskbar=\"True\"", xaml);
        Assert.DoesNotContain("ShowInTaskbar=\"False\"", xaml);
    }

    [Fact]
    public void LaFinestraCheCatturaLeCombinazioniRestaFuori()
    {
        // Se comparisse in ALT+TAB, ALT+TAB diventerebbe una delle combinazioni che
        // sta cercando di catturare.
        Assert.Contains("ShowInTaskbar=\"False\"", Xaml("HotkeyCaptureWindow"));
    }

    // -----------------------------------------------------------------------
    // Modali e non modali
    // -----------------------------------------------------------------------
    //
    // Comparire in ALT+TAB non basta: finche' una finestra e' modale, la principale
    // resta disabilitata e passare all'una o all'altra non serve a niente. Guida,
    // rubrica ed elenco delle ultime chiamate sono finestre in cui si consulta
    // qualcosa MENTRE si lavora, e da modali alcuni dei loro comandi non erano
    // nemmeno raggiungibili: il trasferimento non si puo' avviare mentre un dialogo
    // modale tiene disabilitata la finestra principale.

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

    [Theory]
    [InlineData("ContactsWindow.xaml.cs")]
    [InlineData("CallHistoryWindow.xaml.cs")]
    public void RubricaEUltimeChiamateNonAproneNienteDiModale(string file)
    {
        // Queste due finestre aprivano solo la guida, e la aprivano modale. Adesso non
        // devono aprire piu' niente che blocchi il resto: qualunque ShowDialog che
        // ricompaia qui e' una regressione.
        var codice = Sorgente(file);

        Assert.DoesNotContain("ShowDialog", codice);
        Assert.Contains("FinestreNonModali.MostraOAttiva", codice);
    }

    [Fact]
    public void LaFinestraPrincipaleApreLeTreFinestreDiLavoroSenzaBloccarsi()
    {
        // Tre, e sono guida, rubrica ed elenco delle ultime chiamate. Il conteggio e'
        // li' apposta: se qualcuno ne riporta una a ShowDialog il numero cala, e la
        // prova lo dice invece di lasciar passare la modifica.
        var codice = Sorgente("MainWindow.xaml.cs");
        var aperture = Regex.Matches(codice, @"FinestreNonModali\.MostraOAttiva").Count;

        Assert.Equal(3, aperture);
    }

    [Fact]
    public void LeImpostazioniRestanoModali()
    {
        // E' l'unica in cui il rischio e' concreto: modificare la configurazione mentre
        // l'applicazione la sta usando apre casi che oggi non esistono. Se un giorno si
        // decidera' diversamente, questa prova va cambiata di proposito e non per
        // sbaglio.
        var codice = Sorgente("MainWindow.xaml.cs");
        var apertura = codice.IndexOf("GetRequiredService<SettingsWindow>", StringComparison.Ordinal);

        Assert.True(apertura > 0, "Le impostazioni non vengono piu' aperte da MainWindow");

        // Si guarda solo il tratto di codice che segue l'ottenimento della finestra:
        // cercare "ShowDialog" nell'intero file non direbbe niente, perche' altri
        // dialoghi - aggiornamento, sblocco, segnalazione - restano modali per conto
        // loro.
        var seguito = codice[apertura..Math.Min(codice.Length, apertura + 600)];

        Assert.Contains("ShowDialog", seguito);
        Assert.DoesNotContain("MostraOAttiva", seguito);
    }
}
