using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Collega i pulsanti della cuffia ai comandi della finestra principale, e lo stato
/// della finestra alle spie della cuffia.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non esegue niente da sé.</b> Un pulsante diventa un comando del catalogo dei
/// tasti, deciso da <see cref="HeadsetButtons"/>, e il comando va alla stessa
/// <c>ExecuteCommandAsync</c> delle scorciatoie: rispondere dalla cuffia e rispondere
/// con <c>F2</c> sono la stessa cosa, con lo stesso annuncio e la stessa riga di
/// registro. Un secondo percorso andrebbe tenuto allineato al primo per sempre, e il
/// giorno in cui non lo fosse più l'operatore sentirebbe due comportamenti diversi
/// per lo stesso gesto.
/// </para>
/// <para>
/// <b>Le spie seguono la barra dei comandi.</b> La finestra chiama
/// <see cref="Reflect"/> nello stesso punto in cui ricalcola i pulsanti, cioè dopo ogni
/// comando e ogni cambio di stato: se la barra dice il vero, lo dice anche la cuffia.
/// </para>
/// </remarks>
public sealed class HeadsetController : IDisposable
{
    private const int WmHotkey = 0x0312;

    /// <summary>
    /// Identificativo della scorciatoia del tasto Play e pausa.
    /// </summary>
    /// <remarks>
    /// Il più alto concesso a un programma. Il registro delle scorciatoie globali
    /// numera le sue a partire da uno, e non arriverà mai fin qui.
    /// </remarks>
    private const int MediaHotkeyId = 0xBFFF;

    private const uint VkMediaPlayPause = 0xB3;

    /// <summary>MOD_NOREPEAT: tenere premuto il tasto non risponde e chiude a raffica.</summary>
    private const uint ModNoRepeat = 0x4000;

    private readonly HeadsetMonitor _monitor;
    private readonly SettingsService _settingsService;
    private readonly IAnnouncer _announcer;
    private readonly ILogger<HeadsetController> _log;

    private HwndSource? _source;
    private Dispatcher? _dispatcher;
    private Func<CallActionContext>? _context;
    private Func<HotkeyCommand, Task>? _execute;
    private HeadsetSettings _current = new();

    private bool _mediaKeyHeld;
    private bool _mediaKeyTried;

    /// <summary>Crea il collegamento, ancora inattivo: si avvia con <see cref="Start"/>.</summary>
    /// <param name="monitor">Sorveglianza delle cuffie collegate.</param>
    /// <param name="settingsService">Impostazioni, per accendere e spegnere la funzione a caldo.</param>
    /// <param name="announcer">Annunci e segnali acustici.</param>
    /// <param name="logger">Registro diagnostico.</param>
    public HeadsetController(
        HeadsetMonitor monitor,
        SettingsService settingsService,
        IAnnouncer announcer,
        ILogger<HeadsetController> logger)
    {
        _monitor = monitor;
        _settingsService = settingsService;
        _announcer = announcer;
        _log = logger;
    }

    /// <summary>
    /// Avvia l'ascolto delle cuffie, collegandolo alla finestra principale.
    /// </summary>
    /// <param name="source">Finestra principale, già creata.</param>
    /// <param name="context">Fotografia dello stato, la stessa della barra dei comandi.</param>
    /// <param name="execute">Esecuzione di un comando, la stessa delle scorciatoie.</param>
    public void Start(HwndSource source, Func<CallActionContext> context, Func<HotkeyCommand, Task> execute)
    {
        _source = source;
        _dispatcher = source.Dispatcher;
        _context = context;
        _execute = execute;

        source.AddHook(WndProcHook);

        _monitor.Connected += OnConnected;
        _monitor.Disconnected += OnDisconnected;
        _monitor.ButtonPressed += OnButtonPressed;
        _settingsService.SettingsChanged += OnSettingsChanged;

        Apply(_settingsService.Current.Headset);
    }

    /// <summary>
    /// Porta sulla cuffia lo stato della postazione.
    /// </summary>
    /// <param name="state">Fotografia appena calcolata per la barra dei comandi.</param>
    public void Reflect(CallActionContext state)
    {
        _monitor.Show(HeadsetButtons.Indicators(state));
        UpdateMediaKey(state);
    }

    private void Apply(HeadsetSettings settings)
    {
        _current = settings;

        _log.LogInformation(
            "Pulsanti della cuffia {Stato}, cuffia {Cuffia}, tasto Play e pausa {Ripiego}",
            settings.Enabled ? "attivi" : "disattivati",
            settings.DeviceKey is null ? "qualunque" : $"{settings.DeviceName} ({settings.DeviceKey})",
            settings.MediaKeyFallback ? "ascoltato durante le chiamate" : "lasciato ai lettori multimediali");

        // Fuori dal thread dell'interfaccia: aprire un dispositivo USB di solito costa
        // pochi millisecondi, ma uno che non risponde puo' farne aspettare molti di piu',
        // e la finestra non deve restare ferma per questo.
        _ = Task.Run(() => _monitor.Configure(settings.Enabled, settings.DeviceKey));

        if (_context is { } context) UpdateMediaKey(context());
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (settings.Headset == _current) return;

        _dispatcher?.InvokeAsync(() => Apply(settings.Headset));
    }

    private void OnButtonPressed(object? sender, HeadsetButtonPressedEventArgs e)
        => _dispatcher?.InvokeAsync(() => HandleAsync(e.Button, e.Device.Name));

    /// <summary>Traduce il pulsante nel comando che lo stato richiede, e lo esegue.</summary>
    /// <remarks>
    /// Un pulsante senza significato in questo stato fa sentire il segnale di comando
    /// rifiutato, come il tastierino. Il silenzio direbbe che la cuffia non funziona,
    /// e l'operatore la staccherebbe e riattaccherebbe nel momento sbagliato.
    /// </remarks>
    private async Task HandleAsync(HeadsetButton button, string source)
    {
        if (_context is null || _execute is null) return;

        var comando = HeadsetButtons.Resolve(button, _context());

        _log.LogInformation(
            "Pulsante della cuffia: {Pulsante} da {Origine} -> {Comando}",
            button, source, comando?.ToString() ?? "nessun comando in questo stato");

        if (comando is not { } c)
        {
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return;
        }

        await _execute(c).ConfigureAwait(true);
    }

    private void OnConnected(object? sender, HeadsetDeviceEventArgs e)
    {
        if (!e.HotPlug) return;

        _announcer.Announce(Announcement.Normal(
            AnnouncementTexts.HeadsetConnected(e.Device.Name, e.Device.Capabilities.CanAnswer),
            DateTimeOffset.Now,
            coalesceKey: "cuffia:" + e.Device.Key));
    }

    private void OnDisconnected(object? sender, HeadsetDeviceEventArgs e)
    {
        if (!e.HotPlug) return;

        // Priorita' alta: la cuffia staccata durante una chiamata e' la notizia che
        // serve nei secondi successivi, quando l'operatore vorra' chiudere.
        _announcer.Announce(Announcement.High(
            AnnouncementTexts.HeadsetDisconnected(e.Device.Name),
            DateTimeOffset.Now,
            coalesceKey: "cuffia:" + e.Device.Key));
    }

    // -----------------------------------------------------------------------
    // Ripiego sul tasto Play e pausa
    // -----------------------------------------------------------------------

    /// <summary>
    /// Prende il tasto Play e pausa mentre una chiamata squilla o è in corso, e lo
    /// restituisce ai lettori multimediali appena la postazione torna a riposo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il tasto si prende con <c>RegisterHotKey</c>, come le scorciatoie globali: il
    /// sistema lo consegna a BlindPhone e non lo passa ad altri, ed è il motivo per cui
    /// lo si tiene il meno possibile. Un aggancio di tastiera di basso livello lo
    /// lascerebbe passare anche al lettore, e la musica partirebbe nel momento in cui
    /// l'operatore risponde.
    /// </para>
    /// <para>
    /// Se un altro programma l'ha già preso, lo si dice una volta per chiamata, nel
    /// momento in cui servirebbe, e non si riprova a ogni aggiornamento della finestra.
    /// </para>
    /// </remarks>
    private void UpdateMediaKey(CallActionContext state)
    {
        if (_source is null) return;

        var serve = _current.Enabled && _current.MediaKeyFallback && (state.HasRingingCall || state.HasAnyCall);

        if (!serve)
        {
            ReleaseMediaKey();
            _mediaKeyTried = false;
            return;
        }

        if (_mediaKeyHeld || _mediaKeyTried) return;

        _mediaKeyTried = true;

        if (RegisterHotKey(_source.Handle, MediaHotkeyId, ModNoRepeat, VkMediaPlayPause))
        {
            _mediaKeyHeld = true;
            _log.LogInformation("Tasto Play e pausa preso per la durata della chiamata");
            return;
        }

        _log.LogWarning(
            "Tasto Play e pausa non disponibile: errore di sistema {Errore}", Marshal.GetLastWin32Error());

        _announcer.Announce(Announcement.High(AnnouncementTexts.HeadsetMediaKeyUnavailable, DateTimeOffset.Now));
    }

    private void ReleaseMediaKey()
    {
        if (!_mediaKeyHeld || _source is null) return;

        UnregisterHotKey(_source.Handle, MediaHotkeyId);
        _mediaKeyHeld = false;
        _log.LogInformation("Tasto Play e pausa restituito ai lettori multimediali");
    }

    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmHotkey || (int)wParam != MediaHotkeyId) return nint.Zero;

        handled = true;
        _ = HandleAsync(HeadsetButton.PlayPause, "tasto multimediale");
        return nint.Zero;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _monitor.Connected -= OnConnected;
        _monitor.Disconnected -= OnDisconnected;
        _monitor.ButtonPressed -= OnButtonPressed;
        _settingsService.SettingsChanged -= OnSettingsChanged;

        ReleaseMediaKey();
        _source?.RemoveHook(WndProcHook);

        // Le spie si spengono uscendo: una cuffia che resta «in linea» con il programma
        // chiuso direbbe una cosa falsa a chi la riprende in mano.
        _monitor.Show(default);
        _monitor.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
