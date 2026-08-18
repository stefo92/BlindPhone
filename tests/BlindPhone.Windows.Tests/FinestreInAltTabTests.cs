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
}
