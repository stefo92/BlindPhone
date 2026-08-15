using System.Runtime.InteropServices;
using BlindPhone.Core.Accessibility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Accessibility;

/// <summary>
/// Bridge verso NVDA tramite il client controller ufficiale.
/// </summary>
/// <remarks>
/// <para>
/// Usa <c>nvdaControllerClient64.dll</c>, la libreria che NV Access distribuisce
/// per consentire alle applicazioni di parlare e scrivere in braille attraverso
/// NVDA. Va affiancata all'eseguibile: se manca, il bridge si dichiara non
/// disponibile e l'applicazione ricade su UI Automation senza alcuna perdita di
/// funzionalità essenziale.
/// </para>
/// <para>
/// Il valore aggiunto rispetto a UIA è il controllo fine sull'interruzione del
/// parlato e l'accesso diretto alla riga braille. Su un posto operatore la prima
/// conta: quando arriva una chiamata mentre lo screen reader sta leggendo altro,
/// l'annuncio deve interrompere, non accodarsi.
/// </para>
/// </remarks>
public sealed class NvdaScreenReaderBridge : IScreenReaderBridge
{
    private readonly ILogger<NvdaScreenReaderBridge> _log;
    private readonly bool _libraryPresent;

    /// <summary>Crea il bridge e verifica la presenza della libreria.</summary>
    /// <param name="logger">Log diagnostico.</param>
    public NvdaScreenReaderBridge(ILogger<NvdaScreenReaderBridge>? logger = null)
    {
        _log = logger ?? NullLogger<NvdaScreenReaderBridge>.Instance;

        try
        {
            _libraryPresent = TestIfRunning() == 0;
        }
        catch (DllNotFoundException)
        {
            _log.LogInformation("Client controller NVDA non presente: bridge diretto non disponibile");
            _libraryPresent = false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Inizializzazione del bridge NVDA non riuscita");
            _libraryPresent = false;
        }
    }

    /// <inheritdoc />
    public string Name => "NVDA";

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (!_libraryPresent) return false;

            try
            {
                return TestIfRunning() == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool Speak(string text, bool interrupt)
    {
        if (!IsAvailable) return false;

        try
        {
            if (interrupt) CancelSpeech();
            return SpeakText(text) == 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Invio del testo a NVDA non riuscito");
            return false;
        }
    }

    /// <inheritdoc />
    public bool Braille(string text)
    {
        if (!IsAvailable) return false;

        try
        {
            return BrailleMessage(text) == 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Invio del messaggio braille a NVDA non riuscito");
            return false;
        }
    }

    /// <inheritdoc />
    public bool StopSpeech()
    {
        if (!IsAvailable) return false;

        try
        {
            return CancelSpeech() == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_testIfRunning")]
    private static extern int TestIfRunning();

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_speakText",
        CharSet = CharSet.Unicode)]
    private static extern int SpeakText([MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_brailleMessage",
        CharSet = CharSet.Unicode)]
    private static extern int BrailleMessage([MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_cancelSpeech")]
    private static extern int CancelSpeech();
}

/// <summary>
/// Bridge verso JAWS tramite la sua interfaccia COM.
/// </summary>
/// <remarks>
/// <para>
/// JAWS espone un oggetto COM con ProgID <c>FreedomSci.JawsApi</c>. Non serve alcuna
/// libreria a corredo: se JAWS è installato l'oggetto esiste, altrimenti il bridge
/// si dichiara non disponibile.
/// </para>
/// <para>
/// La creazione dell'oggetto COM avviene una volta sola e viene conservata: crearlo
/// a ogni annuncio introdurrebbe una latenza di decine di millisecondi su un
/// percorso che deve restare sotto la soglia di percezione.
/// </para>
/// </remarks>
public sealed class JawsScreenReaderBridge : IScreenReaderBridge, IDisposable
{
    private readonly ILogger<JawsScreenReaderBridge> _log;
    private readonly object? _jaws;

    /// <summary>Crea il bridge e tenta di ottenere l'oggetto COM di JAWS.</summary>
    /// <param name="logger">Log diagnostico.</param>
    public JawsScreenReaderBridge(ILogger<JawsScreenReaderBridge>? logger = null)
    {
        _log = logger ?? NullLogger<JawsScreenReaderBridge>.Instance;

        try
        {
            var type = Type.GetTypeFromProgID("FreedomSci.JawsApi");
            if (type is not null) _jaws = Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "JAWS non disponibile: bridge diretto non attivo");
            _jaws = null;
        }
    }

    /// <inheritdoc />
    public string Name => "JAWS";

    /// <inheritdoc />
    public bool IsAvailable => _jaws is not null;

    /// <inheritdoc />
    public bool Speak(string text, bool interrupt)
    {
        if (_jaws is null) return false;

        try
        {
            var result = _jaws.GetType().InvokeMember(
                "SayString",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: _jaws,
                args: [text, interrupt]);

            return result is true or null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Invio del testo a JAWS non riuscito");
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// JAWS non espone un metodo diretto per la riga braille attraverso questa
    /// interfaccia: il testo pronunciato compare comunque sul display secondo le
    /// impostazioni dello screen reader. Si restituisce <c>false</c> per lasciare
    /// che la pipeline provi un altro canale, invece di dichiarare un successo che
    /// non c'è stato.
    /// </remarks>
    public bool Braille(string text) => false;

    /// <inheritdoc />
    public bool StopSpeech()
    {
        if (_jaws is null) return false;

        try
        {
            _jaws.GetType().InvokeMember(
                "StopSpeech",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: _jaws,
                args: []);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_jaws is not null && Marshal.IsComObject(_jaws)) Marshal.ReleaseComObject(_jaws);
    }
}
