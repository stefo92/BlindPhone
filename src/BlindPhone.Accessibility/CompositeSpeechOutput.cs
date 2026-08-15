using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using BlindPhone.Core.Accessibility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Accessibility;

/// <summary>Come instradare la voce degli annunci.</summary>
public enum SpeechChannelMode
{
    /// <summary>
    /// Automatica: usa lo screen reader se disponibile, altrimenti la voce interna.
    /// </summary>
    Automatic,

    /// <summary>
    /// Solo UI Automation: nessun canale diretto, gli annunci passano dalle
    /// notifiche della piattaforma.
    /// </summary>
    UiaOnly,

    /// <summary>Solo screen reader, tramite bridge diretto.</summary>
    ScreenReaderOnly,

    /// <summary>
    /// Solo voce interna, su dispositivo audio dedicato.
    /// </summary>
    InternalVoiceOnly,

    /// <summary>
    /// Entrambi: lo screen reader per la navigazione, la voce interna per gli eventi
    /// telefonici.
    /// </summary>
    Both,
}

/// <summary>
/// Destinazione della voce, che sceglie il canale disponibile secondo le
/// preferenze dell'operatore.
/// </summary>
/// <remarks>
/// <para>
/// La modalità che giustifica l'esistenza di questa classe è
/// <see cref="SpeechChannelMode.InternalVoiceOnly"/>. In un softphone normale la
/// voce dello screen reader esce dal dispositivo audio predefinito, che per un
/// operatore di centralino è la cuffia, cioè lo stesso orecchio in cui parla
/// l'interlocutore. Le due voci si sovrappongono e nessuna delle due è
/// comprensibile. L'applicazione non può cambiare il dispositivo di uscita dello
/// screen reader, ma può <b>parlare da sé</b> con una voce propria instradata
/// altrove, e questo risolve il problema.
/// </para>
/// <para>
/// Resta comunque un potenziamento: in <see cref="SpeechChannelMode.UiaOnly"/>
/// l'applicazione non pronuncia nulla di suo e si affida interamente alle notifiche
/// UI Automation, che è il comportamento conforme a EN 301 549 §11.5 e funziona con
/// qualunque tecnologia assistiva.
/// </para>
/// </remarks>
public sealed class CompositeSpeechOutput : ISpeechOutput, IDisposable
{
    private readonly IReadOnlyList<IScreenReaderBridge> _bridges;
    private readonly ILogger<CompositeSpeechOutput> _log;
    private readonly Lock _gate = new();

    private SpeechSynthesizer? _synthesizer;
    private bool _synthesizerFailed;

    /// <summary>Crea la destinazione della voce.</summary>
    /// <param name="bridges">
    /// Bridge verso gli screen reader, in ordine di preferenza. Il primo disponibile
    /// viene usato.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    public CompositeSpeechOutput(
        IEnumerable<IScreenReaderBridge> bridges,
        ILogger<CompositeSpeechOutput>? logger = null)
    {
        _bridges = [.. bridges];
        _log = logger ?? NullLogger<CompositeSpeechOutput>.Instance;
    }

    /// <summary>Modalità di instradamento corrente.</summary>
    public SpeechChannelMode Mode { get; set; } = SpeechChannelMode.Automatic;

    /// <summary>
    /// Se BlindPhone può pronunciare gli annunci con la propria voce sintetica.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È un interruttore che vince su qualunque modalità: a <c>false</c> la sintesi
    /// interna non parla mai, nemmeno in
    /// <see cref="SpeechChannelMode.InternalVoiceOnly"/>. Serve al caso in cui la
    /// voce propria non aggiunge nulla e disturba: con uno screen reader in
    /// esecuzione gli annunci arrivano già dalle notifiche UI Automation, e sentirli
    /// due volte da due voci diverse, leggermente sfasate, è peggio che sentirli una
    /// volta sola.
    /// </para>
    /// <para>
    /// Resta un interruttore separato dalla modalità perché risponde a una domanda
    /// diversa. La modalità dice <b>da dove</b> deve uscire la voce; questo dice
    /// <b>se</b> BlindPhone ha il permesso di parlare per conto proprio, ed è la sola
    /// cosa che l'operatore vuole poter cambiare quando si accorge della
    /// sovrapposizione, senza dover capire un instradamento a cinque voci.
    /// </para>
    /// </remarks>
    public bool InternalVoiceEnabled { get; set; } = true;

    /// <summary>
    /// Indica se il sistema segnala uno screen reader in esecuzione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interroga <c>SPI_GETSCREENREADER</c>, il contrassegno che NVDA e JAWS
    /// impostano all'avvio. È il solo modo per accorgersi di uno screen reader senza
    /// il suo client controller: il bridge diretto verso NVDA richiede
    /// <c>nvdaControllerClient64.dll</c> accanto all'eseguibile, e dove quella
    /// libreria non c'è il bridge si dichiara — correttamente — non disponibile.
    /// </para>
    /// <para>
    /// Senza questa verifica la modalità automatica concludeva "nessuno screen
    /// reader" e si metteva a parlare con la voce interna <b>mentre</b> lo screen
    /// reader leggeva la stessa frase dalla notifica UI Automation. Il risultato è
    /// esattamente il difetto che la modalità automatica esisteva per evitare.
    /// </para>
    /// <para>
    /// Si interroga a ogni annuncio e non una volta all'avvio: uno screen reader si
    /// avvia e si chiude a sessione aperta, e un posto operatore non si riavvia per
    /// seguirlo.
    /// </para>
    /// </remarks>
    public bool IsScreenReaderRunning
    {
        get
        {
            try
            {
                var running = false;
                return SystemParametersInfo(SpiGetScreenReader, 0, ref running, 0) && running;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Rilevamento dello screen reader di sistema non riuscito");
                return false;
            }
        }
    }

    /// <summary>Velocità della voce interna, da -10 a 10.</summary>
    public int InternalVoiceRate { get; set; } = 2;

    /// <summary>Volume della voce interna, da 0 a 100.</summary>
    public int InternalVoiceVolume { get; set; } = 100;

    /// <summary>Nome della voce interna da usare, se disponibile sul sistema.</summary>
    public string? InternalVoiceName { get; set; }

    /// <summary>
    /// Sollevato per ogni testo pronunciato, perché la UI possa riportarlo nella
    /// regione dinamica UI Automation.
    /// </summary>
    /// <remarks>
    /// In modalità <see cref="SpeechChannelMode.UiaOnly"/> è l'unico canale: la UI
    /// riceve il testo e lo espone tramite una notifica UIA, che lo screen reader
    /// legge senza che l'applicazione debba parlare.
    /// </remarks>
    public event EventHandler<string>? TextEmitted;

    /// <summary>Screen reader attualmente rilevato, per la diagnostica.</summary>
    /// <remarks>
    /// Il bridge diretto viene prima perché ne conosce il nome. Quando manca resta il
    /// contrassegno di sistema, che dice che uno screen reader c'è ma non quale: è
    /// comunque l'informazione che conta per decidere se tacere.
    /// </remarks>
    public string? DetectedScreenReader
        => _bridges.FirstOrDefault(b => b.IsAvailable)?.Name
           ?? (IsScreenReaderRunning ? "screen reader di sistema" : null);

    /// <inheritdoc />
    public void Speak(string text, bool interrupt)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // La UI riceve sempre il testo, in ogni modalita': serve alla regione
        // dinamica UIA e allo storico degli annunci consultabile dall'operatore.
        TextEmitted?.Invoke(this, text);

        if (Mode == SpeechChannelMode.UiaOnly) return;

        var usedScreenReader = false;

        if (Mode is SpeechChannelMode.Automatic or SpeechChannelMode.ScreenReaderOnly or SpeechChannelMode.Both)
        {
            usedScreenReader = TrySpeakViaScreenReader(text, interrupt);
        }

        if (Mode == SpeechChannelMode.ScreenReaderOnly) return;

        // In modalita' automatica la voce interna interviene solo se il testo non e'
        // gia' arrivato a uno screen reader: parlare due volte lo stesso testo e'
        // peggio che non parlare affatto.
        //
        // "Gia' arrivato" comprende due strade. La prima e' il bridge diretto, che
        // dice subito se ha accettato. La seconda e' la notifica UI Automation, che la
        // finestra principale solleva per ogni annuncio e che uno screen reader in
        // esecuzione legge: in quel caso non c'e' alcun riscontro da attendere, e
        // l'unico indizio disponibile e' il contrassegno di sistema.
        var alreadySpokenByAssistiveTech = usedScreenReader || IsScreenReaderRunning;

        if (ShouldSpeakInternally(Mode, InternalVoiceEnabled, alreadySpokenByAssistiveTech))
        {
            SpeakInternally(text, interrupt);
        }
    }

    /// <summary>
    /// Decide se la voce propria di BlindPhone deve pronunciare il testo.
    /// </summary>
    /// <param name="mode">Modalità di instradamento configurata.</param>
    /// <param name="internalVoiceEnabled">Se la voce propria è consentita.</param>
    /// <param name="handledByAssistiveTech">
    /// Se il testo è già stato preso in carico da una tecnologia assistiva, per
    /// bridge diretto o perché uno screen reader in esecuzione leggerà la notifica UI
    /// Automation.
    /// </param>
    /// <remarks>
    /// È una funzione pura e pubblica per una ragione precisa: è la regola che decide
    /// se un operatore non vedente sente gli annunci, e i due modi in cui può
    /// sbagliare — parlare due volte, o non parlare affatto — non si notano leggendo
    /// il codice. Separata dall'atto di parlare, si verifica caso per caso senza
    /// motore di sintesi né screen reader in esecuzione.
    /// </remarks>
    public static bool ShouldSpeakInternally(
        SpeechChannelMode mode, bool internalVoiceEnabled, bool handledByAssistiveTech)
    {
        // L'interruttore vince su tutto: se l'operatore ha detto di non voler sentire
        // la voce di BlindPhone, nessuna modalita' puo' riaccenderla.
        if (!internalVoiceEnabled) return false;

        return mode switch
        {
            SpeechChannelMode.UiaOnly => false,
            SpeechChannelMode.ScreenReaderOnly => false,
            SpeechChannelMode.InternalVoiceOnly => true,
            SpeechChannelMode.Both => true,
            SpeechChannelMode.Automatic => !handledByAssistiveTech,
            _ => false,
        };
    }

    private bool TrySpeakViaScreenReader(string text, bool interrupt)
    {
        foreach (var bridge in _bridges)
        {
            if (!bridge.IsAvailable) continue;
            if (bridge.Speak(text, interrupt)) return true;
        }

        return false;
    }

    private void SpeakInternally(string text, bool interrupt)
    {
        var synthesizer = GetSynthesizer();
        if (synthesizer is null) return;

        try
        {
            if (interrupt) synthesizer.SpeakAsyncCancelAll();
            synthesizer.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sintesi vocale interna non riuscita");
        }
    }

    private SpeechSynthesizer? GetSynthesizer()
    {
        if (_synthesizerFailed) return null;

        lock (_gate)
        {
            if (_synthesizer is not null) return _synthesizer;

            try
            {
                var synthesizer = new SpeechSynthesizer();
                synthesizer.SetOutputToDefaultAudioDevice();
                synthesizer.Rate = Math.Clamp(InternalVoiceRate, -10, 10);
                synthesizer.Volume = Math.Clamp(InternalVoiceVolume, 0, 100);

                if (!string.IsNullOrWhiteSpace(InternalVoiceName))
                {
                    var available = synthesizer.GetInstalledVoices()
                        .Where(v => v.Enabled)
                        .Select(v => v.VoiceInfo.Name)
                        .ToList();

                    var match = available.FirstOrDefault(
                        v => v.Contains(InternalVoiceName, StringComparison.OrdinalIgnoreCase));

                    if (match is not null) synthesizer.SelectVoice(match);
                    else _log.LogWarning("Voce {Voice} non disponibile sul sistema", InternalVoiceName);
                }

                _synthesizer = synthesizer;
                return _synthesizer;
            }
            catch (Exception ex)
            {
                // Un sistema senza voci SAPI installate e' possibile. Si registra
                // una volta sola e si smette di riprovare, perche' un errore
                // ripetuto a ogni annuncio riempirebbe il log rendendolo inutile.
                _log.LogWarning(ex, "Sintesi vocale interna non disponibile su questo sistema");
                _synthesizerFailed = true;
                return null;
            }
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        foreach (var bridge in _bridges)
        {
            if (bridge.IsAvailable) bridge.StopSpeech();
        }

        try
        {
            _synthesizer?.SpeakAsyncCancelAll();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Interruzione della sintesi interna non riuscita");
        }
    }

    /// <inheritdoc />
    public void Braille(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (var bridge in _bridges)
        {
            if (bridge.IsAvailable && bridge.Braille(text)) return;
        }
    }

    private const uint SpiGetScreenReader = 0x0046;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, [MarshalAs(UnmanagedType.Bool)] ref bool pvParam, uint fWinIni);

    /// <inheritdoc />
    public void Dispose()
    {
        _synthesizer?.Dispose();

        foreach (var bridge in _bridges)
        {
            if (bridge is IDisposable disposable) disposable.Dispose();
        }
    }
}
