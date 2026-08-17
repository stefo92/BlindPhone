using System.Reflection;
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
    /// <summary>Nome dichiarato nelle importazioni, risolto da <see cref="Risolvi"/>.</summary>
    private const string LibreriaDichiarata = "nvdaControllerClient64.dll";

    /// <summary>
    /// I nomi con cui NV Access ha distribuito la stessa libreria, in ordine di prova.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sono due perché il pacchetto ha cambiato forma.</b> Le versioni storiche
    /// mettevano l'architettura nel nome del file — <c>nvdaControllerClient32.dll</c> e
    /// <c>nvdaControllerClient64.dll</c> nella stessa cartella. Quelle recenti la
    /// mettono nella cartella e il file si chiama <c>nvdaControllerClient.dll</c> per
    /// tutte e tre le architetture.
    /// </para>
    /// <para>
    /// La ricerca predefinita del runtime non copre il caso: prova il nome dichiarato
    /// con e senza estensione, ma non toglie il <c>64</c>. Chi scarica il pacchetto
    /// odierno e lo copia accanto all'eseguibile si vedrebbe dire che la libreria non
    /// c'è, mentre è lì. Provare entrambi i nomi costa una riga e toglie di mezzo un
    /// errore di installazione che nessuno saprebbe diagnosticare.
    /// </para>
    /// </remarks>
    private static readonly string[] NomiCandidati =
    [
        "nvdaControllerClient.dll",
        "nvdaControllerClient64.dll",
    ];

    /// <summary>Nome del file effettivamente caricato, per il registro.</summary>
    private static string? _fileCaricato;

    /// <remarks>
    /// La registrazione vale per l'intero assembly e si può fare una volta sola: se un
    /// giorno servisse risolvere un'altra libreria nativa da qui, va aggiunta a questo
    /// risolutore e non registrata a parte.
    /// </remarks>
    static NvdaScreenReaderBridge()
        => NativeLibrary.SetDllImportResolver(typeof(NvdaScreenReaderBridge).Assembly, Risolvi);

    private static IntPtr Risolvi(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibreriaDichiarata, StringComparison.OrdinalIgnoreCase))
        {
            // Non è roba nostra: se ne occupi la ricerca predefinita.
            return IntPtr.Zero;
        }

        foreach (var candidato in NomiCandidati)
        {
            if (!NativeLibrary.TryLoad(candidato, assembly, searchPath, out var handle)) continue;

            _fileCaricato = candidato;
            return handle;
        }

        return IntPtr.Zero;
    }

    private readonly ILogger<NvdaScreenReaderBridge> _log;
    private readonly bool _libraryPresent;

    /// <summary>Crea il bridge e verifica la presenza della libreria.</summary>
    /// <param name="logger">Log diagnostico.</param>
    /// <remarks>
    /// <para>
    /// <b>Presente e in esecuzione sono due cose distinte</b>, e confonderle costava
    /// l'intera sessione. Prima qui si teneva per buono solo l'esito zero, che vuol dire
    /// «NVDA è in ascolto adesso»: se la postazione partiva prima dello screen reader —
    /// e parte all'avvio di Windows, quindi succede spesso — il bridge si dichiarava
    /// assente e restava assente fino al riavvio successivo, pur avendo la libreria
    /// accanto all'eseguibile.
    /// </para>
    /// <para>
    /// Quello che conta qui è che la chiamata sia <i>possibile</i>: se non solleva
    /// eccezioni la libreria c'è, e da quel momento
    /// <see cref="IsAvailable"/> chiede a ogni annuncio se NVDA sia in ascolto.
    /// </para>
    /// </remarks>
    public NvdaScreenReaderBridge(ILogger<NvdaScreenReaderBridge>? logger = null)
    {
        _log = logger ?? NullLogger<NvdaScreenReaderBridge>.Instance;

        try
        {
            var inAscolto = TestIfRunning() == 0;
            _libraryPresent = true;

            _log.LogInformation(
                "Client controller NVDA disponibile ({File}). NVDA in ascolto adesso: {InAscolto}",
                _fileCaricato ?? LibreriaDichiarata,
                inAscolto);
        }
        catch (DllNotFoundException)
        {
            _log.LogInformation(
                "Client controller NVDA non presente: bridge diretto non disponibile. "
                + "Cercata come {Nomi} accanto all'eseguibile",
                string.Join(" oppure ", NomiCandidati));

            _libraryPresent = false;
        }
        catch (BadImageFormatException)
        {
            // Il caso di chi scarica il pacchetto giusto e prende la cartella sbagliata.
            // Senza questa riga l'errore e' un'eccezione generica che non dice cosa fare.
            _log.LogWarning(
                "Client controller NVDA di architettura sbagliata: serve la versione a 64 bit. "
                + "Nel pacchetto di NV Access e' quella nella cartella x64");

            _libraryPresent = false;
        }
        catch (EntryPointNotFoundException ex)
        {
            _log.LogWarning(
                ex,
                "Client controller NVDA presente ma senza le funzioni attese: "
                + "il file non e' il client controller, oppure e' troncato");

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
