using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BlindPhone.App;

/// <summary>
/// Porta la finestra in primo piano <b>con la tastiera</b>, non solo davanti.
/// </summary>
/// <remarks>
/// <para>
/// <b>La distinzione è tutta qui.</b> Portare una finestra davanti e darle il primo
/// piano sono due operazioni diverse di Windows: la prima cambia l'ordine di
/// sovrapposizione e non ha bisogno di permessi, la seconda sposta la tastiera e
/// Windows la nega a chi non ha ricevuto l'ultimo evento di input. Una chiamata SIP
/// che arriva dalla rete non è un evento di input: al momento dello squillo il
/// processo non ha alcun diritto sul primo piano.
/// </para>
/// <para>
/// Il risultato era esattamente quello che l'operatore ha descritto: la finestra
/// compare — perché il passaggio per <c>Topmost</c> cambia la sovrapposizione, che è
/// concessa — ma F2 e «più» continuano ad andare al programma di prima.
/// <c>SetForegroundWindow</c> falliva e restituiva <c>false</c>, e nessuno guardava
/// quel valore.
/// </para>
/// <para>
/// <b>Perché ora si usa <c>AttachThreadInput</c>, che una nota precedente aveva
/// scartato.</b> La nota temeva interferenze con il modo in cui gli screen reader
/// seguono il focus. Il timore era mal riposto: NVDA e JAWS seguono il primo piano
/// attraverso gli eventi di sistema e UI Automation, che vengono emessi comunque,
/// mentre l'aggancio delle code di input dura qualche microsecondo e viene sciolto
/// subito. L'alternativa vera — sintetizzare una pressione di ALT per simulare
/// l'input dell'utente, che è l'altro modo diffuso di aggirare il blocco — sarebbe
/// stata quella pericolosa: ALT apre i menu della finestra che sta davanti e viene
/// intercettato dagli screen reader.
/// </para>
/// <para>
/// Resta il fatto che il primo piano <b>può</b> non arrivare, per esempio se la
/// finestra davanti è bloccata. In quel caso la funzione dice di no invece di
/// mentire, e chi chiama sa che deve avvisare l'operatore.
/// </para>
/// </remarks>
internal static class ForegroundActivator
{
    /// <summary>
    /// Tenta di dare alla finestra il primo piano di sistema.
    /// </summary>
    /// <param name="window">Finestra da portare davanti.</param>
    /// <returns>
    /// <c>true</c> se al termine la finestra è davvero quella in primo piano, e
    /// quindi i tasti arrivano; <c>false</c> se Windows lo ha impedito.
    /// </returns>
    public static bool TryTake(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return false;

        // La strada pulita per prima: quando il processo ha i diritti - perche' e'
        // gia' lui in primo piano, o perche' l'operatore ha appena premuto una
        // scorciatoia globale - basta questa e non si tocca nient'altro.
        if (GetForegroundWindow() == handle) return true;

        SetForegroundWindow(handle);
        if (GetForegroundWindow() == handle) return true;

        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero) return false;

        // UNA FINESTRA BLOCCATA NON SI TOCCA.
        //
        // Agganciare la coda di input di un processo che non risponde puo' fermare
        // anche la nostra: sarebbe il posto operatore a bloccarsi, mentre il
        // telefono squilla. Meglio rinunciare al primo piano e dirlo.
        if (IsHungAppWindow(foreground)) return false;

        var altrui = GetWindowThreadProcessId(foreground, out _);
        var nostro = GetCurrentThreadId();
        if (altrui == 0 || altrui == nostro) return false;

        if (!AttachThreadInput(altrui, nostro, true)) return false;

        try
        {
            // Con le code agganciate Windows considera i due thread come uno solo
            // ai fini dell'input, e il primo piano diventa concesso.
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            SetFocus(handle);
        }
        finally
        {
            // Nel finally e non in coda al blocco: se qualcosa va storto qui in
            // mezzo, due code di input agganciate per sempre sarebbero un guasto
            // molto peggiore di una finestra che non prende il primo piano.
            AttachThreadInput(altrui, nostro, false);
        }

        return GetForegroundWindow() == handle;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
