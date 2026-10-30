using System.Runtime.InteropServices;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace BlindPhone.Infrastructure;

/// <summary>Una cuffia con i pulsanti telefonici, trovata fra i dispositivi collegati.</summary>
/// <param name="Path">Percorso del dispositivo: cambia con la porta USB.</param>
/// <param name="Key">Produttore e modello, «0B0E:245E»: non cambia.</param>
/// <param name="Name">Nome dichiarato dal produttore.</param>
/// <param name="Capabilities">Pulsanti e spie.</param>
public sealed record HeadsetDeviceInfo(string Path, string Key, string Name, HeadsetCapabilities Capabilities);

/// <summary>
/// Una cuffia aperta: ne legge i pulsanti e ne comanda le spie.
/// </summary>
/// <remarks>
/// <para>
/// <b>Un thread per cuffia, e tutto su quel thread.</b> La lettura resta in attesa del
/// prossimo rapporto, e la stessa attesa si sveglia quando c'è da aggiornare una spia
/// o da chiudere. Così l'interprete dei segnali, che non è pensato per essere usato da
/// due thread, vede letture e spie nello stesso ordine in cui succedono, e il thread
/// dell'interfaccia non resta mai fermo dietro una cuffia che non risponde: una
/// scrittura che non torna entro un secondo viene annullata.
/// </para>
/// <para>
/// <b>La cuffia non si apre in esclusiva.</b> Il canale telefonico di una cuffia lo
/// leggono anche il programma del produttore e gli altri programmi di telefonia, e
/// ciascuno riceve gli stessi rapporti: aprirla da soli significherebbe toglierla a
/// loro, e farli smettere di funzionare per colpa di BlindPhone.
/// </para>
/// </remarks>
internal sealed class HidHeadsetDevice : IDisposable
{
    /// <summary>Quanto si aspetta una scrittura prima di rinunciare.</summary>
    private const int WriteTimeoutMs = 1000;

    private readonly HeadsetDeviceInfo _info;
    private readonly SafeFileHandle _handle;
    private readonly nint _preparsed;
    private readonly bool _canWrite;
    private readonly ushort _inputLength;
    private readonly ushort _outputLength;
    private readonly uint _maxUsages;
    private readonly IReadOnlyList<(HeadsetButton Button, ushort Usage, byte ReportId)> _inputs;
    private readonly IReadOnlyList<(byte ReportId, IReadOnlySet<ushort> Leds)> _ledReports;
    private readonly HeadsetSignalInterpreter _interpreter;
    private readonly Action<HeadsetButton> _pressed;
    private readonly Action<int> _lost;
    private readonly ILogger _log;

    private readonly ManualResetEvent _readDone = new(false);
    private readonly ManualResetEvent _writeDone = new(false);
    private readonly AutoResetEvent _indicatorsChanged = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Lock _gate = new();
    private readonly Thread _thread;

    private HeadsetIndicators _pending;
    private HeadsetIndicators _shown;
    private int _disposed;

    private HidHeadsetDevice(
        HeadsetDeviceInfo info,
        SafeFileHandle handle,
        nint preparsed,
        bool canWrite,
        HidNative.Caps caps,
        IReadOnlyList<HidNative.ButtonField> inputFields,
        IReadOnlyList<HidNative.ButtonField> outputFields,
        Action<HeadsetButton> pressed,
        Action<int> lost,
        ILogger log)
    {
        _info = info;
        _handle = handle;
        _preparsed = preparsed;
        _canWrite = canWrite;
        _inputLength = caps.InputLength;
        _outputLength = caps.OutputLength;
        _maxUsages = HidNative.HidP_MaxUsageListLength(HidNative.ReportInput, HidTelephonyUsages.TelephonyPage, preparsed);
        _pressed = pressed;
        _lost = lost;
        _log = log;

        _inputs =
        [
            .. inputFields
                .Where(f => f.UsagePage == HidTelephonyUsages.TelephonyPage)
                .SelectMany(f => f.Usages().Select(u => (Usage: u, f.ReportId)))
                .Select(x => (Button: HidTelephonyUsages.ButtonFor(HidTelephonyUsages.TelephonyPage, x.Usage), x.Usage, x.ReportId))
                .Where(x => x.Button is not null)
                .Select(x => (x.Button!.Value, x.Usage, x.ReportId))
        ];

        // Le spie possono stare in rapporti diversi: se ne manda uno per ciascuno.
        _ledReports = canWrite && _outputLength > 0
            ?
            [
                .. outputFields
                    .Where(f => f.UsagePage == HidTelephonyUsages.LedPage)
                    .SelectMany(f => f.Usages().Select(u => (Usage: u, f.ReportId)))
                    .Where(x => HidTelephonyUsages.KnownLeds.Contains(x.Usage))
                    .GroupBy(x => x.ReportId)
                    .Select(g => (g.Key, (IReadOnlySet<ushort>)g.Select(x => x.Usage).ToHashSet()))
            ]
            : [];

        // Una cuffia di cui non si possono comandare le spie non segue lo stato di
        // BlindPhone, anche se le dichiara: l'interprete deve saperlo, perche' e'
        // proprio quello che decide se un cambiamento del gancio e' un'eco o un dito.
        var capacita = _ledReports.Count > 0
            ? info.Capabilities
            : info.Capabilities with { Leds = new HashSet<ushort>() };

        _interpreter = new HeadsetSignalInterpreter(capacita);

        _thread = new Thread(Run) { IsBackground = true, Name = $"Cuffia {info.Name}" };
    }

    /// <summary>La cuffia, com'è stata trovata.</summary>
    public HeadsetDeviceInfo Info => _info;

    /// <summary>
    /// Guarda un dispositivo HID e dice se è una cuffia con i pulsanti telefonici.
    /// </summary>
    /// <param name="path">Percorso del dispositivo.</param>
    /// <returns>La descrizione della cuffia, o <c>null</c> se non lo è.</returns>
    /// <remarks>
    /// Si apre senza chiedere né lettura né scrittura: basta per leggere le
    /// caratteristiche, e funziona anche con tastiere e mouse, che Windows tiene aperti
    /// in esclusiva e che quindi rifiuterebbero qualunque altra richiesta.
    /// </remarks>
    public static HeadsetDeviceInfo? Probe(string path)
    {
        using var handle = HidNative.CreateFile(
            path, 0, HidNative.ShareReadWrite, 0, HidNative.OpenExisting, 0, 0);

        if (handle.IsInvalid) return null;
        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed)) return null;

        try
        {
            if (HidNative.GetCaps(preparsed) is not { } caps) return null;

            var inputs = HidNative.GetButtonFields(HidNative.ReportInput, caps.InputButtonCaps, preparsed);
            if (!IsTelephonyHeadset(caps, inputs)) return null;

            var outputs = HidNative.GetButtonFields(HidNative.ReportOutput, caps.OutputButtonCaps, preparsed);

            var attributi = new HidNative.HiddAttributes { Size = Marshal.SizeOf<HidNative.HiddAttributes>() };
            var chiave = HidNative.HidD_GetAttributes(handle, ref attributi)
                ? $"{attributi.VendorId:X4}:{attributi.ProductId:X4}"
                : "0000:0000";

            return new HeadsetDeviceInfo(path, chiave, ProductName(handle, chiave), DescribeCapabilities(inputs, outputs));
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>
    /// Apre la cuffia per leggerne i pulsanti e comandarne le spie, e comincia ad
    /// ascoltarla.
    /// </summary>
    /// <param name="info">Cuffia da aprire.</param>
    /// <param name="pressed">Chiamato, sul thread della cuffia, per ogni pressione vera.</param>
    /// <param name="lost">
    /// Chiamato, sul thread della cuffia, quando la cuffia smette di rispondere, con il
    /// codice di errore di sistema: di solito perché è stata scollegata.
    /// </param>
    /// <param name="log">Registro diagnostico.</param>
    /// <returns>La cuffia aperta, o <c>null</c> se non è stato possibile aprirla.</returns>
    public static HidHeadsetDevice? Open(
        HeadsetDeviceInfo info, Action<HeadsetButton> pressed, Action<int> lost, ILogger log)
    {
        var canWrite = true;
        var handle = HidNative.CreateFile(
            info.Path, HidNative.GenericRead | HidNative.GenericWrite, HidNative.ShareReadWrite,
            0, HidNative.OpenExisting, HidNative.FlagOverlapped, 0);

        if (handle.IsInvalid)
        {
            // Senza scrittura le spie restano spente, ma i pulsanti funzionano: e' meglio
            // di niente, ed e' quello che succede quando un criterio di sicurezza nega la
            // scrittura sui dispositivi USB.
            handle.Dispose();
            canWrite = false;
            handle = HidNative.CreateFile(
                info.Path, HidNative.GenericRead, HidNative.ShareReadWrite,
                0, HidNative.OpenExisting, HidNative.FlagOverlapped, 0);
        }

        if (handle.IsInvalid)
        {
            log.LogWarning(
                "Cuffia {Nome} ({Chiave}) non apribile: errore di sistema {Errore}",
                info.Name, info.Key, Marshal.GetLastWin32Error());
            handle.Dispose();
            return null;
        }

        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed)
            || HidNative.GetCaps(preparsed) is not { } caps)
        {
            log.LogWarning("Cuffia {Nome} ({Chiave}): caratteristiche non leggibili", info.Name, info.Key);
            handle.Dispose();
            return null;
        }

        var device = new HidHeadsetDevice(
            info,
            handle,
            preparsed,
            canWrite,
            caps,
            HidNative.GetButtonFields(HidNative.ReportInput, caps.InputButtonCaps, preparsed),
            HidNative.GetButtonFields(HidNative.ReportOutput, caps.OutputButtonCaps, preparsed),
            pressed,
            lost,
            log);

        log.LogInformation(
            "Cuffia {Nome} ({Chiave}) aperta: {Capacita}. Gancio {Natura}, spie {Spie}",
            info.Name,
            info.Key,
            info.Capabilities.Describe(),
            info.Capabilities.HookSwitchIsAbsolute ? "a stato" : "a impulsi",
            device._ledReports.Count > 0 ? "comandabili" : canWrite ? "assenti" : "non comandabili, scrittura negata");

        device._thread.Start();
        return device;
    }

    /// <summary>
    /// Chiede alla cuffia di mostrare uno stato.
    /// </summary>
    /// <param name="indicators">Spie da mostrare.</param>
    /// <remarks>
    /// Torna subito: la scrittura la fa il thread della cuffia. Se arrivano più stati
    /// prima che li scriva, scrive l'ultimo, che è il solo che conti.
    /// </remarks>
    public void Show(HeadsetIndicators indicators)
    {
        lock (_gate) _pending = indicators;
        _indicatorsChanged.Set();
    }

    /// <summary>Lettura dei rapporti, fino alla chiusura o alla perdita della cuffia.</summary>
    private void Run()
    {
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        var buffer = Marshal.AllocHGlobal(_inputLength);
        var accesi = new ushort[Math.Max(_maxUsages, 1)];

        try
        {
            while (true)
            {
                _readDone.Reset();
                PrepareOverlapped(overlapped, _readDone);

                if (!HidNative.ReadFile(_handle, buffer, _inputLength, 0, overlapped))
                {
                    var errore = Marshal.GetLastWin32Error();
                    if (errore != HidNative.ErrorIoPending)
                    {
                        _lost(errore);
                        return;
                    }
                }

                // Mentre la lettura aspetta il prossimo rapporto, la stessa attesa
                // serve a scrivere le spie e ad accorgersi della chiusura.
                while (true)
                {
                    var evento = WaitHandle.WaitAny([_readDone, _indicatorsChanged, _stop]);

                    if (evento == 0) break;

                    if (evento == 1)
                    {
                        ApplyIndicators();
                        continue;
                    }

                    // La lettura in sospeso usa memoria che sta per essere liberata:
                    // va annullata e attesa, non abbandonata.
                    HidNative.CancelIoEx(_handle, overlapped);
                    HidNative.GetOverlappedResult(_handle, overlapped, out _, true);
                    return;
                }

                if (!HidNative.GetOverlappedResult(_handle, overlapped, out var letti, false))
                {
                    _lost(Marshal.GetLastWin32Error());
                    return;
                }

                Process(buffer, letti, accesi);
            }
        }
        catch (Exception ex)
        {
            // Un errore qui non deve chiudere il programma: la cuffia smette di
            // rispondere e la tastiera resta.
            _log.LogError(ex, "Lettura della cuffia {Nome} interrotta da un errore", _info.Name);
            _lost(-1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(overlapped);
        }
    }

    /// <summary>Traduce un rapporto nei valori dei pulsanti e li passa all'interprete.</summary>
    private void Process(nint buffer, uint length, ushort[] accesi)
    {
        if (length == 0) return;

        var reportId = Marshal.ReadByte(buffer, 0);
        var quanti = (uint)accesi.Length;

        var esito = HidNative.HidP_GetUsages(
            HidNative.ReportInput, HidTelephonyUsages.TelephonyPage, 0, accesi, ref quanti,
            _preparsed, buffer, _inputLength);

        // Un rapporto che non contiene la pagina telefonica — il volume, per esempio,
        // su una cuffia che lo manda nella stessa collezione — non riguarda nessun
        // pulsante di questo elenco.
        if (esito != HidNative.StatusSuccess) return;

        var premuti = accesi.Take((int)quanti).ToHashSet();

        if (_log.IsEnabled(LogLevel.Trace))
        {
            _log.LogTrace(
                "Cuffia {Nome}: rapporto {Rapporto}, usage telefonici attivi [{Usage}]",
                _info.Name, reportId, string.Join(", ", premuti.Select(u => $"0x{u:X2}")));
        }

        var adesso = DateTimeOffset.Now;

        foreach (var (pulsante, usage, rapporto) in _inputs)
        {
            if (rapporto != reportId) continue;

            if (_interpreter.Feed(pulsante, premuti.Contains(usage), adesso))
            {
                _pressed(pulsante);
            }
        }
    }

    /// <summary>Scrive sulla cuffia l'ultimo stato richiesto, se è cambiato.</summary>
    private void ApplyIndicators()
    {
        HeadsetIndicators richiesto;
        lock (_gate) richiesto = _pending;

        if (richiesto == _shown) return;

        // Prima l'interprete e poi la cuffia: la conferma della cuffia puo' arrivare
        // appena scritta la spia, e a quel punto l'interprete deve gia' sapere che e'
        // un'eco.
        _interpreter.Reflect(richiesto);
        _shown = richiesto;

        if (_ledReports.Count == 0) return;

        var rapporto = Marshal.AllocHGlobal(_outputLength);
        try
        {
            foreach (var (reportId, spie) in _ledReports)
            {
                Marshal.Copy(new byte[_outputLength], 0, rapporto, _outputLength);
                HidNative.HidP_InitializeReportForID(HidNative.ReportOutput, reportId, _preparsed, rapporto, _outputLength);

                var accese = HidTelephonyUsages.LitLeds(richiesto, spie).ToArray();
                if (accese.Length > 0)
                {
                    var quante = (uint)accese.Length;
                    HidNative.HidP_SetUsages(
                        HidNative.ReportOutput, HidTelephonyUsages.LedPage, 0, accese, ref quante,
                        _preparsed, rapporto, _outputLength);
                }

                if (!Write(rapporto))
                {
                    _log.LogDebug(
                        "Cuffia {Nome}: spie non aggiornate (rapporto {Rapporto}, errore {Errore})",
                        _info.Name, reportId, Marshal.GetLastWin32Error());
                }
            }

            _log.LogDebug(
                "Cuffia {Nome}: spie squillo {Squillo}, linea {Linea}, muto {Muto}, attesa {Attesa}",
                _info.Name, richiesto.Ring, richiesto.OffHook, richiesto.Mute, richiesto.Hold);
        }
        finally
        {
            Marshal.FreeHGlobal(rapporto);
        }
    }

    /// <summary>Scrive un rapporto di uscita, senza aspettare più di un secondo.</summary>
    private bool Write(nint report)
    {
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        try
        {
            _writeDone.Reset();
            PrepareOverlapped(overlapped, _writeDone);

            if (!HidNative.WriteFile(_handle, report, _outputLength, 0, overlapped)
                && Marshal.GetLastWin32Error() != HidNative.ErrorIoPending)
            {
                return false;
            }

            if (!_writeDone.WaitOne(WriteTimeoutMs))
            {
                HidNative.CancelIoEx(_handle, overlapped);
                HidNative.GetOverlappedResult(_handle, overlapped, out _, true);
                return false;
            }

            return HidNative.GetOverlappedResult(_handle, overlapped, out _, false);
        }
        finally
        {
            Marshal.FreeHGlobal(overlapped);
        }
    }

    private static void PrepareOverlapped(nint overlapped, WaitHandle done)
        => Marshal.StructureToPtr(
            new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() },
            overlapped,
            fDeleteOld: false);

    /// <summary>
    /// È una cuffia da telefono: manda il pulsante della chiamata o quello del
    /// microfono della pagina Telefonia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Non si guarda la pagina della collezione principale</b>, anche se lo standard
    /// vorrebbe una collezione Telefonia a sé. Misurato su una cuffia Sennheiser
    /// (1395:0024): il gancio sta dentro la collezione dei tasti multimediali, accanto
    /// al volume, e una regola più stretta l'avrebbe scartata senza dire niente.
    /// </para>
    /// <para>
    /// Le tastiere con un tasto a forma di cornetta restano fuori lo stesso: quel tasto
    /// è un usage della pagina Consumer, non il gancio della pagina Telefonia.
    /// </para>
    /// </remarks>
    private static bool IsTelephonyHeadset(HidNative.Caps caps, IReadOnlyList<HidNative.ButtonField> inputs)
        => caps.InputLength > 0
           && inputs.Any(f => f.UsagePage == HidTelephonyUsages.TelephonyPage
                              && f.Usages().Any(u => u is HidTelephonyUsages.HookSwitch or HidTelephonyUsages.PhoneMute));

    private static HeadsetCapabilities DescribeCapabilities(
        IReadOnlyList<HidNative.ButtonField> inputs, IReadOnlyList<HidNative.ButtonField> outputs)
    {
        var pulsanti = new HashSet<HeadsetButton>();
        var ganciAStato = false;

        foreach (var campo in inputs.Where(f => f.UsagePage == HidTelephonyUsages.TelephonyPage))
        {
            foreach (var usage in campo.Usages())
            {
                if (HidTelephonyUsages.ButtonFor(campo.UsagePage, usage) is not { } pulsante) continue;

                pulsanti.Add(pulsante);
                if (pulsante == HeadsetButton.HookSwitch && campo.IsAbsolute) ganciAStato = true;
            }
        }

        var spie = outputs
            .Where(f => f.UsagePage == HidTelephonyUsages.LedPage)
            .SelectMany(f => f.Usages())
            .Where(HidTelephonyUsages.KnownLeds.Contains)
            .ToHashSet();

        return new HeadsetCapabilities(pulsanti, ganciAStato, spie);
    }

    /// <summary>Nome dichiarato dal produttore, o un nome di ripiego che si possa dire.</summary>
    private static string ProductName(SafeFileHandle handle, string key)
    {
        // 126 caratteri e' il massimo di una stringa USB.
        var buffer = new char[127];

        if (HidNative.HidD_GetProductString(handle, buffer, (uint)(buffer.Length * sizeof(char))))
        {
            // Il nome finisce detto ad alta voce: un carattere di controllo scritto male
            // nel firmware diventerebbe un rumore della sintesi, o un silenzio.
            var fine = Array.IndexOf(buffer, '\0');
            var nome = new string(buffer, 0, fine < 0 ? buffer.Length : fine);
            nome = new string([.. nome.Where(c => !char.IsControl(c))]).Trim();

            if (nome.Length > 0) return nome;
        }

        return $"cuffia USB {key}";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _stop.Set();

        // Chi chiude puo' essere il thread stesso della cuffia, quando la chiusura
        // nasce dalla sua perdita: aspettarlo da li' non finirebbe mai.
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));

        HidNative.HidD_FreePreparsedData(_preparsed);
        _handle.Dispose();

        _readDone.Dispose();
        _writeDone.Dispose();
        _indicatorsChanged.Dispose();
        _stop.Dispose();
    }
}
