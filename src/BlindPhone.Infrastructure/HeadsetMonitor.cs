using System.Runtime.InteropServices;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>Una cuffia collegata o scollegata.</summary>
/// <param name="device">La cuffia.</param>
/// <param name="hotPlug">
/// <c>true</c> se è successo mentre BlindPhone era già in servizio; <c>false</c> per
/// quelle trovate all'avvio o al cambio delle impostazioni, che non vanno annunciate.
/// </param>
public sealed class HeadsetDeviceEventArgs(HeadsetDeviceInfo device, bool hotPlug) : EventArgs
{
    /// <summary>La cuffia.</summary>
    public HeadsetDeviceInfo Device { get; } = device;

    /// <summary>Collegata o scollegata mentre BlindPhone era in servizio.</summary>
    public bool HotPlug { get; } = hotPlug;
}

/// <summary>Un pulsante premuto su una cuffia.</summary>
/// <param name="device">Cuffia da cui arriva.</param>
/// <param name="button">Pulsante premuto.</param>
public sealed class HeadsetButtonPressedEventArgs(HeadsetDeviceInfo device, HeadsetButton button) : EventArgs
{
    /// <summary>Cuffia da cui arriva.</summary>
    public HeadsetDeviceInfo Device { get; } = device;

    /// <summary>Pulsante premuto.</summary>
    public HeadsetButton Button { get; } = button;
}

/// <summary>
/// Trova le cuffie con i pulsanti telefonici, le apre, e si accorge di quando vengono
/// collegate e scollegate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il collegamento a caldo è il caso normale, non l'eccezione.</b> La cuffia di un
/// operatore si stacca a fine turno, passa a un collega, torna nella presa sbagliata.
/// Windows avvisa quando un'interfaccia HID compare o sparisce; a quel punto si
/// riguarda l'elenco intero, invece di fidarsi del dispositivo nominato dall'avviso,
/// perché una cuffia ne porta diverse e arrivano a raffica. Una cuffia staccata se ne
/// accorge comunque anche da sola: la lettura in corso si interrompe.
/// </para>
/// <para>
/// Non sa niente delle chiamate. Riceve gli stati da mostrare e restituisce i pulsanti
/// premuti: a decidere che cosa significano è <c>HeadsetButtons</c>, nel core.
/// </para>
/// <para>
/// Gli eventi arrivano dal thread di lettura della cuffia o da un thread del sistema,
/// mai da quello dell'interfaccia.
/// </para>
/// </remarks>
public sealed class HeadsetMonitor : IDisposable
{
    /// <summary>
    /// Quanto si aspetta, dopo un avviso di Windows, prima di riguardare l'elenco.
    /// </summary>
    /// <remarks>
    /// Una cuffia collegata produce un avviso per ciascuna delle sue interfacce, nel
    /// giro di qualche centinaio di millisecondi. Riguardare l'elenco a ogni avviso
    /// significherebbe trovarla a metà, con il canale telefonico non ancora pronto.
    /// </remarks>
    private static readonly TimeSpan RescanDelay = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<HeadsetMonitor> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HidHeadsetDevice> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _rescan;

    // Il richiamo va tenuto in vita da un campo: Windows lo chiama da codice nativo, e un
    // delegato raccolto dal garbage collector fa cadere il processo al primo avviso.
    private readonly HidNative.NotifyCallback _notifyCallback;

    private nint _notification;
    private bool _enabled;
    private string? _deviceKey;
    private HeadsetIndicators _indicators;
    private bool _disposed;

    /// <summary>Crea il sorvegliante, spento: si accende con <see cref="Configure"/>.</summary>
    /// <param name="logger">Registro diagnostico.</param>
    public HeadsetMonitor(ILogger<HeadsetMonitor>? logger = null)
    {
        _log = logger ?? NullLogger<HeadsetMonitor>.Instance;
        _notifyCallback = OnDeviceNotification;
        _rescan = new Timer(_ => Rescan(hotPlug: true), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Una cuffia è stata aperta e viene ascoltata.</summary>
    public event EventHandler<HeadsetDeviceEventArgs>? Connected;

    /// <summary>Una cuffia non viene più ascoltata.</summary>
    public event EventHandler<HeadsetDeviceEventArgs>? Disconnected;

    /// <summary>È stato premuto un pulsante di una cuffia ascoltata.</summary>
    public event EventHandler<HeadsetButtonPressedEventArgs>? ButtonPressed;

    /// <summary>Le cuffie ascoltate in questo momento.</summary>
    public IReadOnlyList<HeadsetDeviceInfo> Active
    {
        get
        {
            lock (_gate) return [.. _open.Values.Select(d => d.Info)];
        }
    }

    /// <summary>
    /// Le cuffie con i pulsanti telefonici collegate adesso, ascoltate o no.
    /// </summary>
    /// <remarks>
    /// Serve al pannello delle impostazioni, che le elenca per farne scegliere una.
    /// Non lancia eccezioni: un dispositivo che non si lascia leggere viene saltato,
    /// perché una tastiera capricciosa non deve nascondere la cuffia accanto.
    /// </remarks>
    public IReadOnlyList<HeadsetDeviceInfo> Detect()
    {
        var trovate = new List<HeadsetDeviceInfo>();

        try
        {
            foreach (var percorso in HidNative.PresentHidInterfaces())
            {
                try
                {
                    if (HidHeadsetDevice.Probe(percorso) is { } cuffia) trovate.Add(cuffia);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Dispositivo HID {Percorso} non leggibile", percorso);
                }
            }
        }
        catch (Exception ex)
        {
            // hid.dll e cfgmgr32.dll ci sono su ogni Windows; se mancano, la cuffia non
            // funziona e la tastiera si'.
            _log.LogWarning(ex, "Elenco dei dispositivi HID non disponibile");
        }

        return trovate;
    }

    /// <summary>
    /// Applica le impostazioni: accende o spegne l'ascolto e sceglie le cuffie.
    /// </summary>
    /// <param name="enabled">Se ascoltare le cuffie.</param>
    /// <param name="deviceKey">
    /// Produttore e modello della cuffia da ascoltare, o <c>null</c> per tutte quelle
    /// collegate.
    /// </param>
    /// <remarks>
    /// Vale subito, senza riavviare: chi cambia cuffia nelle impostazioni la prova
    /// premendone il pulsante appena salvato.
    /// </remarks>
    public void Configure(bool enabled, string? deviceKey)
    {
        lock (_gate)
        {
            if (_disposed) return;

            _enabled = enabled;
            _deviceKey = string.IsNullOrWhiteSpace(deviceKey) ? null : deviceKey.Trim();
        }

        if (enabled)
        {
            RegisterNotification();
            Rescan(hotPlug: false);
        }
        else
        {
            UnregisterNotification();
            CloseAll();
        }
    }

    /// <summary>Mostra uno stato su tutte le cuffie ascoltate.</summary>
    /// <param name="indicators">Spie da mostrare.</param>
    /// <remarks>
    /// Si può chiamare a ogni aggiornamento dell'interfaccia: le cuffie scrivono solo
    /// quando lo stato cambia davvero.
    /// </remarks>
    public void Show(HeadsetIndicators indicators)
    {
        List<HidHeadsetDevice> aperte;

        lock (_gate)
        {
            if (indicators == _indicators) return;

            _indicators = indicators;
            aperte = [.. _open.Values];
        }

        foreach (var cuffia in aperte) cuffia.Show(indicators);
    }

    /// <summary>Riguarda l'elenco dei dispositivi e apre o chiude quello che è cambiato.</summary>
    private void Rescan(bool hotPlug)
    {
        bool enabled;
        string? chiave;

        lock (_gate)
        {
            if (_disposed) return;

            enabled = _enabled;
            chiave = _deviceKey;
        }

        if (!enabled) return;

        var presenti = Detect()
            .Where(c => chiave is null || string.Equals(c.Key, chiave, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var chiuse = new List<HidHeadsetDevice>();
        var aperte = new List<HidHeadsetDevice>();

        lock (_gate)
        {
            if (_disposed || !_enabled) return;

            foreach (var (percorso, cuffia) in _open.ToList())
            {
                if (presenti.Any(p => string.Equals(p.Path, percorso, StringComparison.OrdinalIgnoreCase))) continue;

                _open.Remove(percorso);
                chiuse.Add(cuffia);
            }

            foreach (var info in presenti.Where(p => !_open.ContainsKey(p.Path)))
            {
                var cuffia = HidHeadsetDevice.Open(
                    info,
                    pulsante => OnButton(info, pulsante),
                    errore => OnLost(info, errore),
                    _log);

                if (cuffia is null) continue;

                _open[info.Path] = cuffia;
                aperte.Add(cuffia);

                // Una cuffia collegata durante una chiamata deve mostrarla subito. A
                // riposo invece non si scrive niente: la cuffia puo' essere in uso da un
                // altro programma di telefonia, e spegnergli le spie sarebbe un dispetto.
                cuffia.Show(_indicators);
            }
        }

        foreach (var cuffia in chiuse)
        {
            _log.LogInformation("Cuffia {Nome} ({Chiave}) non piu' presente", cuffia.Info.Name, cuffia.Info.Key);
            cuffia.Dispose();
            Disconnected?.Invoke(this, new HeadsetDeviceEventArgs(cuffia.Info, hotPlug));
        }

        foreach (var cuffia in aperte)
        {
            Connected?.Invoke(this, new HeadsetDeviceEventArgs(cuffia.Info, hotPlug));
        }
    }

    private void OnButton(HeadsetDeviceInfo device, HeadsetButton button)
    {
        _log.LogInformation("Cuffia {Nome}: premuto {Pulsante}", device.Name, button);
        ButtonPressed?.Invoke(this, new HeadsetButtonPressedEventArgs(device, button));
    }

    /// <summary>La lettura di una cuffia si è interrotta: quasi sempre è stata staccata.</summary>
    private void OnLost(HeadsetDeviceInfo device, int error)
    {
        // Si esce dal thread della cuffia prima di chiuderla: e' quel thread a
        // chiamare qui, e la chiusura lo aspetta.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            HidHeadsetDevice? persa = null;

            lock (_gate)
            {
                if (_open.TryGetValue(device.Path, out var cuffia))
                {
                    _open.Remove(device.Path);
                    persa = cuffia;
                }
            }

            if (persa is null) return;

            _log.LogInformation(
                "Cuffia {Nome} ({Chiave}) persa: errore di sistema {Errore}", device.Name, device.Key, error);

            persa.Dispose();
            Disconnected?.Invoke(this, new HeadsetDeviceEventArgs(device, hotPlug: true));

            // Puo' essere ricomparsa con lo stesso nome, dopo un riavvio della porta USB.
            ScheduleRescan();
        });
    }

    private int OnDeviceNotification(nint notify, nint context, int action, nint eventData, int eventDataSize)
    {
        ScheduleRescan();
        return 0;
    }

    private void ScheduleRescan()
    {
        try
        {
            _rescan.Change(RescanDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Un avviso arrivato mentre il programma si chiude: non c'e' piu' niente da riguardare.
        }
    }

    private void RegisterNotification()
    {
        lock (_gate)
        {
            if (_notification != 0) return;

            HidNative.HidD_GetHidGuid(out var guid);

            var filtro = Marshal.AllocHGlobal(HidNative.NotifyFilterSize);
            try
            {
                Marshal.Copy(new byte[HidNative.NotifyFilterSize], 0, filtro, HidNative.NotifyFilterSize);

                // CM_NOTIFY_FILTER: cbSize a 0, Flags a 4, FilterType a 8 (zero, cioe'
                // interfaccia di dispositivo), la classe d'interfaccia a 16.
                Marshal.WriteInt32(filtro, 0, HidNative.NotifyFilterSize);
                Marshal.StructureToPtr(guid, filtro + 16, fDeleteOld: false);

                var esito = HidNative.CM_Register_Notification(filtro, 0, _notifyCallback, out _notification);

                if (esito != HidNative.ConfigRetSuccess)
                {
                    // Senza avvisi le cuffie trovate all'avvio funzionano lo stesso; manca
                    // solo il collegamento a caldo, e il registro lo dice.
                    _notification = 0;
                    _log.LogWarning("Avviso di collegamento delle cuffie non disponibile: codice {Codice}", esito);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(filtro);
            }
        }
    }

    private void UnregisterNotification()
    {
        nint notifica;

        lock (_gate)
        {
            notifica = _notification;
            _notification = 0;
        }

        if (notifica != 0) HidNative.CM_Unregister_Notification(notifica);
    }

    private void CloseAll()
    {
        List<HidHeadsetDevice> chiuse;

        lock (_gate)
        {
            chiuse = [.. _open.Values];
            _open.Clear();
        }

        foreach (var cuffia in chiuse)
        {
            // Spenta la funzione durante una chiamata, la cuffia resterebbe «in linea»
            // per sempre: il thread scrive le spie spente prima di accorgersi della
            // chiusura, perche' fra i due segnali guarda prima quello delle spie.
            cuffia.Show(default);
            cuffia.Dispose();
            Disconnected?.Invoke(this, new HeadsetDeviceEventArgs(cuffia.Info, hotPlug: false));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        UnregisterNotification();
        _rescan.Dispose();
        CloseAll();
    }
}
