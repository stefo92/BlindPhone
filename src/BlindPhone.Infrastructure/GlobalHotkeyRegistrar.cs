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

    /// <summary>
    /// Identificativo riservato alla prova di registrazione.
    /// </summary>
    /// <remarks>
    /// Windows accetta per le applicazioni identificativi fino a 0xBFFF. Si prende
    /// l'ultimo: quelli delle scorciatoie vere partono da 1 e crescono di uno a ogni
    /// registrazione, e non arrivano fin lassu' nemmeno in anni di riconfigurazioni.
    /// </remarks>
    private const int ProbeId = 0xBFFF;

    private readonly ILogger<GlobalHotkeyRegistrar> _log;
    private readonly Dictionary<int, (HotkeyBinding Binding, uint VirtualKey)> _registered = [];
    private readonly Lock _gate = new();

    private nint _windowHandle;
    private int _nextId = 1;
    private int _suspensions;

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

        lock (_gate)
        {
            var id = _nextId++;

            // Durante una sospensione la combinazione si annota e basta: verra'
            // dichiarata a Windows alla ripresa, insieme alle altre. Registrarla ora
            // la farebbe scattare proprio mentre l'operatore la sta riassegnando.
            if (_suspensions > 0)
            {
                _registered[id] = (binding, virtualKey);
                return HotkeyConflict.None;
            }

            if (!RegisterHotKey(_windowHandle, id, ToNativeModifiers(binding.Modifiers), virtualKey))
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

            _registered[id] = (binding, virtualKey);
            return HotkeyConflict.None;
        }
    }

    /// <summary>Rimuove tutte le scorciatoie globali registrate.</summary>
    public void UnregisterAll()
    {
        lock (_gate)
        {
            // Durante una sospensione sono gia' disdette presso Windows: basta
            // dimenticarle, cosi' alla ripresa non tornano.
            if (_suspensions == 0)
            {
                foreach (var id in _registered.Keys)
                {
                    UnregisterHotKey(_windowHandle, id);
                }
            }

            _registered.Clear();
        }
    }

    /// <summary>Indica se le scorciatoie globali sono sospese.</summary>
    public bool IsSuspended
    {
        get
        {
            lock (_gate) return _suspensions > 0;
        }
    }

    /// <summary>
    /// Sospende tutte le scorciatoie globali fino al rilascio dell'oggetto restituito.
    /// </summary>
    /// <returns>
    /// L'oggetto che, rilasciato, le dichiara di nuovo a Windows. Le sospensioni si
    /// possono annidare: le scorciatoie tornano quando è rilasciata l'ultima.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Serve alla finestra che cattura una combinazione nuova. Una scorciatoia globale
    /// registrata non arriva mai alla finestra in primo piano: Windows la consegna a
    /// chi l'ha chiesta. Senza sospensione, premere Control Maiusc F2 per spostarla su
    /// un altro comando risponderebbe alla chiamata invece di farla rileggere, e
    /// l'operatore resterebbe con un'area di cattura muta e una chiamata aperta.
    /// </para>
    /// <para>
    /// Le combinazioni non vengono dimenticate: si disdicono presso Windows e si
    /// dichiarano di nuovo con gli stessi identificativi, quindi i messaggi che
    /// arrivano dopo la ripresa trovano lo stesso comando di prima.
    /// </para>
    /// </remarks>
    public IDisposable Suspend()
    {
        lock (_gate)
        {
            if (_suspensions++ == 0)
            {
                foreach (var id in _registered.Keys)
                {
                    UnregisterHotKey(_windowHandle, id);
                }

                _log.LogInformation(
                    "Scorciatoie globali sospese: {Count} combinazioni disdette", _registered.Count);
            }
        }

        return new Suspension(this);
    }

    /// <summary>
    /// Prova se una combinazione globale si potrebbe registrare adesso.
    /// </summary>
    /// <param name="binding">Combinazione da provare.</param>
    /// <param name="virtualKey">Codice del tasto virtuale Windows.</param>
    /// <returns>
    /// <c>true</c> se la combinazione è tenuta da un altro programma o da Windows,
    /// oppure se il tasto non ha un codice registrabile.
    /// </returns>
    /// <remarks>
    /// <para>
    /// È l'unico modo di sapere in anticipo se una combinazione è libera: Windows non
    /// elenca le scorciatoie degli altri, dice soltanto se una registrazione riesce.
    /// La prova registra e disdice subito, con un identificativo riservato, e non
    /// lascia traccia.
    /// </para>
    /// <para>
    /// Si fa con le scorciatoie di BlindPhone sospese. Altrimenti una combinazione che
    /// BlindPhone stesso tiene per un altro comando risulterebbe occupata da «un altro
    /// programma», e l'operatore non potrebbe mai spostarla: proprio il caso che la
    /// conferma della sostituzione serve a gestire.
    /// </para>
    /// <para>
    /// Senza la finestra collegata la prova non si può fare e la combinazione si
    /// dichiara libera: il rifiuto, se c'è, arriverà al salvataggio come prima.
    /// </para>
    /// </remarks>
    public bool IsTakenElsewhere(HotkeyBinding binding, uint virtualKey)
    {
        if (virtualKey == 0) return true;
        if (_windowHandle == 0) return false;

        using (Suspend())
        {
            if (!RegisterHotKey(_windowHandle, ProbeId, ToNativeModifiers(binding.Modifiers), virtualKey))
            {
                _log.LogInformation(
                    "La combinazione {Key} non si puo' registrare (errore {Error})",
                    binding.DisplayText, Marshal.GetLastWin32Error());
                return true;
            }

            UnregisterHotKey(_windowHandle, ProbeId);
            return false;
        }
    }

    private void Resume()
    {
        lock (_gate)
        {
            if (_suspensions == 0 || --_suspensions > 0) return;

            var lost = new List<int>();

            foreach (var (id, entry) in _registered)
            {
                if (RegisterHotKey(_windowHandle, id, ToNativeModifiers(entry.Binding.Modifiers), entry.VirtualKey))
                {
                    continue;
                }

                // Un altro programma l'ha presa nei pochi istanti della sospensione.
                // Non si puo' riprendere: lo si scrive nel registro, e al prossimo
                // salvataggio dei tasti il rifiuto viene annunciato come sempre.
                _log.LogWarning(
                    "Alla ripresa la scorciatoia globale {Key} non e' piu' registrabile (errore {Error})",
                    entry.Binding.DisplayText, Marshal.GetLastWin32Error());
                lost.Add(id);
            }

            foreach (var id in lost) _registered.Remove(id);

            _log.LogInformation(
                "Scorciatoie globali riprese: {Count} combinazioni registrate di nuovo", _registered.Count);
        }
    }

    /// <summary>Una sospensione in corso: rilasciarla due volte non riprende due volte.</summary>
    private sealed class Suspension(GlobalHotkeyRegistrar owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Resume();
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
            if (!_registered.TryGetValue((int)wParam, out var entry)) return false;
            command = entry.Binding.Command;
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
