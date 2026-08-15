using System.Runtime.InteropServices;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Registra le scorciatoie globali presso Windows.
/// </summary>
/// <remarks>
/// <para>
/// Le scorciatoie globali funzionano anche con BlindPhone in secondo piano. Non è
/// un vezzo: l'operatore di centralino prende appunti in un altro programma mentre
/// parla, e senza questa funzione dovrebbe cambiare finestra per rispondere alla
/// chiamata successiva, perdendo secondi preziosi e, per chi non vede, anche il
/// punto in cui stava scrivendo.
/// </para>
/// <para>
/// Si usa <c>RegisterHotKey</c> e non un hook di tastiera di basso livello. La
/// differenza conta: l'hook intercetterebbe <b>tutti</b> i tasti del sistema,
/// compresi quelli diretti allo screen reader, con il rischio concreto di
/// interferire con NVDA o JAWS e di far apparire BlindPhone come la causa di
/// malfunzionamenti altrui. <c>RegisterHotKey</c> chiede al sistema solo le
/// combinazioni dichiarate, e se una è già occupata lo dice invece di rubarla.
/// </para>
/// </remarks>
public sealed class GlobalHotkeyRegistrar : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly ILogger<GlobalHotkeyRegistrar> _log;
    private readonly Dictionary<int, HotkeyCommand> _registered = [];
    private readonly Lock _gate = new();

    private nint _windowHandle;
    private int _nextId = 1;

    /// <summary>Crea il registratore.</summary>
    /// <param name="logger">Log diagnostico.</param>
    public GlobalHotkeyRegistrar(ILogger<GlobalHotkeyRegistrar>? logger = null)
        => _log = logger ?? NullLogger<GlobalHotkeyRegistrar>.Instance;

    /// <summary>Sollevato quando una scorciatoia globale viene premuta.</summary>
    public event EventHandler<HotkeyCommand>? HotkeyPressed;

    /// <summary>
    /// Collega il registratore alla finestra che riceverà i messaggi di sistema.
    /// </summary>
    /// <param name="windowHandle">Handle della finestra principale.</param>
    /// <remarks>
    /// Va invocato dopo che la finestra è stata creata: prima l'handle non esiste e
    /// la registrazione fallirebbe silenziosamente.
    /// </remarks>
    public void Attach(nint windowHandle) => _windowHandle = windowHandle;

    /// <summary>
    /// Registra una scorciatoia globale.
    /// </summary>
    /// <param name="binding">Combinazione da registrare.</param>
    /// <param name="virtualKey">Codice del tasto virtuale Windows.</param>
    /// <returns>
    /// <see cref="HotkeyConflict.None"/> se la registrazione è riuscita, altrimenti
    /// il conflitto con il messaggio pronto per l'annuncio.
    /// </returns>
    public HotkeyConflict Register(HotkeyBinding binding, uint virtualKey)
    {
        if (_windowHandle == 0)
        {
            return new HotkeyConflict(
                HotkeyConflictKind.TakenByAnotherApplication,
                "Scorciatoie globali non disponibili: finestra non pronta",
                CanOverride: false);
        }

        var modifiers = ToNativeModifiers(binding.Modifiers);
        int id;

        lock (_gate) id = _nextId++;

        if (!RegisterHotKey(_windowHandle, id, modifiers, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            _log.LogWarning(
                "Registrazione della scorciatoia globale {Key} non riuscita (errore {Error})",
                binding.DisplayText, error);

            // Errore 1409 significa "hot key gia' registrata": e' il caso comune, e
            // va comunicato all'operatore in modo che possa sceglierne un'altra
            // invece di credere che il comando non funzioni.
            return HotkeyConflictDetectorBridge.SystemRegistrationFailed(binding, null);
        }

        lock (_gate) _registered[id] = binding.Command;
        return HotkeyConflict.None;
    }

    /// <summary>Rimuove tutte le scorciatoie globali registrate.</summary>
    public void UnregisterAll()
    {
        lock (_gate)
        {
            foreach (var id in _registered.Keys)
            {
                UnregisterHotKey(_windowHandle, id);
            }

            _registered.Clear();
        }
    }

    /// <summary>
    /// Elabora un messaggio della finestra, riconoscendo le pressioni delle
    /// scorciatoie globali.
    /// </summary>
    /// <param name="message">Codice del messaggio.</param>
    /// <param name="wParam">Identificativo della scorciatoia.</param>
    /// <returns><c>true</c> se il messaggio è stato gestito.</returns>
    /// <remarks>
    /// Va collegato all'hook della finestra WPF tramite <c>HwndSource.AddHook</c>.
    /// </remarks>
    public bool HandleMessage(int message, nint wParam)
    {
        if (message != WmHotkey) return false;

        HotkeyCommand command;
        lock (_gate)
        {
            if (!_registered.TryGetValue((int)wParam, out command)) return false;
        }

        HotkeyPressed?.Invoke(this, command);
        return true;
    }

    private static uint ToNativeModifiers(HotkeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) result |= 0x0001;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) result |= 0x0002;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) result |= 0x0004;
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) result |= 0x0008;

        // MOD_NOREPEAT: senza, tenere premuto il tasto genera una raffica di
        // comandi. Su "Rispondi" significherebbe rispondere e riagganciare decine
        // di volte, e per chi non vede il risultato sarebbe incomprensibile.
        result |= 0x4000;

        return result;
    }

    /// <inheritdoc />
    public void Dispose() => UnregisterAll();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}

/// <summary>
/// Ponte verso il rilevatore di conflitti del core.
/// </summary>
/// <remarks>
/// Esiste per non far dipendere l'infrastruttura Win32 dai dettagli del servizio di
/// dominio: la registrazione fallita è un fatto della piattaforma, il messaggio da
/// pronunciare è una decisione di dominio.
/// </remarks>
internal static class HotkeyConflictDetectorBridge
{
    public static HotkeyConflict SystemRegistrationFailed(HotkeyBinding binding, string? ownerProcess)
        => Core.Services.HotkeyConflictDetector.SystemRegistrationFailed(binding, ownerProcess);
}
