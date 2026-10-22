using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BlindPhone.App;

/// <summary>
/// Accorda la barra del titolo delle finestre al tema dei colori.
/// </summary>
/// <remarks>
/// <para>
/// <b>La barra del titolo non è di WPF.</b> La disegna Windows, e i dizionari dei
/// colori non la raggiungono: con il tema scuro il contenuto si ridipingeva e la barra
/// restava bianca, una striscia accesa in cima a una finestra scura — cioè proprio la
/// cosa che il tema scuro serve a togliere a chi lavora in penombra.
/// </para>
/// <para>
/// Il colore si chiede a Windows finestra per finestra, con l'attributo che le
/// applicazioni native usano per la modalità scura. Vale per tutte le finestre aperte
/// quando il tema cambia, e per ognuna di quelle che si aprono dopo, al caricamento.
/// L'aggancio è sulla classe <see cref="Window"/> e non sulle singole finestre: così
/// vale anche per quelle che verranno scritte in futuro, senza che nessuno debba
/// ricordarsene.
/// </para>
/// <para>
/// <b>Con il contrasto elevato la barra resta a Windows.</b> Il tema in vigore è
/// quello di sistema, e la cornice la colora Windows con i colori scelti dall'utente.
/// </para>
/// <para>
/// Nelle versioni di Windows 10 precedenti alla 20H1 l'attributo ha un altro numero, e
/// si ritenta con quello. Dove non esiste proprio la richiesta fallisce senza
/// conseguenze, e la barra resta chiara come prima.
/// </para>
/// </remarks>
internal static class TitleBarTheme
{
    /// <summary>Attributo della modalità scura, da Windows 10 20H1 in poi.</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Lo stesso attributo, nelle versioni di Windows 10 precedenti.</summary>
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private static bool _registrato;
    private static bool _scura;

    /// <summary>
    /// Rende scura o chiara la barra del titolo di tutte le finestre, aperte e future.
    /// </summary>
    /// <param name="dark"><c>true</c> per la barra scura.</param>
    public static void Set(bool dark)
    {
        if (!_registrato)
        {
            // Una volta sola per tutta la vita del processo: un aggancio sulla classe non
            // si toglie, e registrarlo due volte applicherebbe tutto due volte.
            EventManager.RegisterClassHandler(
                typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            _registrato = true;
        }

        _scura = dark;

        if (Application.Current is not { } applicazione) return;

        foreach (Window finestra in applicazione.Windows)
        {
            ApplyTo(finestra);
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // Una finestra nasce con la barra chiara: con il tema chiaro non c'e' niente da
        // cambiare, e ricalcolarne la cornice a ogni apertura sarebbe lavoro per nulla.
        if (_scura && sender is Window finestra) ApplyTo(finestra);
    }

    private static void ApplyTo(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;

        // Finestra non ancora creata: ci pensera' il caricamento.
        if (hwnd == 0) return;

        var valore = _scura ? 1 : 0;

        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref valore, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref valore, sizeof(int));
        }

        // L'attributo da solo non ridisegna la cornice di una finestra gia' visibile, che
        // puo' restare del colore vecchio finche' qualcosa non la ridipinge. Qui la si fa
        // ricalcolare subito, senza spostarla, ridimensionarla o attivarla.
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
