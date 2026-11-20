using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using BlindPhone.Core.Abstractions;
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
    /// <summary>Frequenza a cui si sintetizza la voce destinata al dispositivo degli annunci.</summary>
    /// <remarks>
    /// È quella che le voci SAPI producono di loro, quindi la sintesi non ricampiona:
    /// la conversione verso la frequenza della scheda la fa il motore audio, una volta.
    /// </remarks>
    public const int SpeechSampleRate = 22050;

    private readonly IReadOnlyList<IScreenReaderBridge> _bridges;
    private readonly IAnnouncementAudioOutput? _announcementOutput;
    private readonly ILogger<CompositeSpeechOutput> _log;
    private readonly Lock _gate = new();

    /// <summary>Sintesi che parla sul dispositivo predefinito di sistema.</summary>
    private SpeechSynthesizer? _synthesizer;

    /// <summary>
    /// Sintesi che scrive in memoria, per il dispositivo scelto degli annunci.
    /// </summary>
    /// <remarks>
    /// È un'istanza separata e non la stessa con l'uscita cambiata: la prima parla in
    /// modo asincrono, e spostarle l'uscita mentre sta ancora pronunciando una frase la
    /// interromperebbe a metà, o la manderebbe in memoria al posto degli altoparlanti.
    /// </remarks>
    private SpeechSynthesizer? _streamSynthesizer;
    private bool _synthesizerFailed;

    // Coda delle frasi per il dispositivo degli annunci. Una frase alla volta, nell'ordine
    // in cui sono arrivate, come faceva la sintesi di sistema.
    private readonly Lock _queueGate = new();
    private readonly Queue<(string Text, CancellationToken Cancellation)> _queue = new();
    private CancellationTokenSource _queueCancellation = new();
    private bool _queueRunning;

    /// <summary>Crea la destinazione della voce.</summary>
    /// <param name="bridges">
    /// Bridge verso gli screen reader, in ordine di preferenza. Il primo disponibile
    /// viene usato.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="announcementOutput">
    /// Uscita verso il dispositivo scelto per gli annunci. Senza, o senza un dispositivo
    /// scelto, la voce interna esce dal predefinito di sistema.
    /// </param>
    public CompositeSpeechOutput(
        IEnumerable<IScreenReaderBridge> bridges,
        ILogger<CompositeSpeechOutput>? logger = null,
        IAnnouncementAudioOutput? announcementOutput = null)
    {
        _bridges = [.. bridges];
        _log = logger ?? NullLogger<CompositeSpeechOutput>.Instance;
        _announcementOutput = announcementOutput;
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

    /// <summary>
    /// Se BlindPhone scrive sul display braille la forma compatta degli annunci.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È la spunta «Aggiorna la riga di stato sul display braille». Il messaggio
    /// occupa la riga per qualche secondo e copre quello che l'operatore stava
    /// leggendo: chi legge una scheda della rubrica, o il numero che sta componendo,
    /// può preferire che la coda che cambia non glielo porti via.
    /// </para>
    /// <para>
    /// Spenta, tace solo ciò che BlindPhone manda di suo. Il braille dello screen
    /// reader — il controllo con il fuoco, la navigazione — resta com'è, e resta
    /// affare suo. Si legge a ogni annuncio, quindi cambiarla vale dal successivo.
    /// </para>
    /// </remarks>
    public bool BrailleEnabled { get; set; } = true;

    /// <summary>Velocità della voce interna, da -10 a 10.</summary>
    public int InternalVoiceRate { get; set; } = 2;

    private int _internalVoiceVolume = 100;

    /// <summary>Volume della voce interna, da 0 a 100.</summary>
    /// <remarks>
    /// <para>
    /// Vale anche sulla sintesi già creata, dalla frase successiva: la sintesi si
    /// costruisce una volta sola, al primo annuncio, e un volume letto solo in quel
    /// momento costringerebbe a riavviare per sentire il cambio.
    /// </para>
    /// <para>
    /// Regola soltanto la voce di BlindPhone. Quella dello screen reader appartiene allo
    /// screen reader, e da qui non si tocca.
    /// </para>
    /// <para>
    /// Lo applica la sintesi in entrambe le uscite, anche quando la frase va al
    /// dispositivo scelto e la suona il motore audio: la scala della sintesi non è
    /// lineare, e lo stesso valore deve suonare uguale da qualunque parte esca.
    /// </para>
    /// </remarks>
    public int InternalVoiceVolume
    {
        get => Volatile.Read(ref _internalVoiceVolume);
        set
        {
            var volume = Math.Clamp(value, 0, 100);

            lock (_gate)
            {
                Volatile.Write(ref _internalVoiceVolume, volume);

                foreach (var sintesi in new[] { _synthesizer, _streamSynthesizer })
                {
                    if (sintesi is null) continue;

                    try
                    {
                        sintesi.Volume = volume;
                    }
                    catch (Exception ex)
                    {
                        // La voce continua al volume di prima: un volume non applicato
                        // non deve togliere gli annunci.
                        _log.LogDebug(ex, "Volume della voce interna non applicato");
                    }
                }
            }
        }
    }

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
        //
        // <b>E' un'assunzione, non una verifica</b>, e quando cade il guasto e' totale:
        // il bridge diretto non c'e', il contrassegno di sistema dice che uno screen
        // reader e' in esecuzione, la voce interna tace per non sovrapporsi, e se quello
        // screen reader la notifica non la legge — perche' la finestra non ha il fuoco,
        // perche' e' configurato per ignorarle — l'operatore non sente niente da nessun
        // canale. Per questo la riga di registro qui sotto non e' facoltativa: e' l'unico
        // modo di distinguere "non l'ha detto" da "l'ha detto e non l'hai sentito".
        var screenReaderDiSistema = IsScreenReaderRunning;
        var alreadySpokenByAssistiveTech = usedScreenReader || screenReaderDiSistema;

        var voceInterna = ShouldSpeakInternally(Mode, InternalVoiceEnabled, alreadySpokenByAssistiveTech);

        if (voceInterna) SpeakInternally(text, interrupt);

        // CHI HA PARLATO SI SCRIVE, non solo chi e' stato zittito.
        //
        // Il registro sapeva dire perche' un annuncio veniva scartato ma non attraverso
        // quale canale uscisse quando non lo era: davanti a "non parla piu'" mancava
        // proprio il dato che serve, e la diagnosi doveva ricostruirlo dalle
        // impostazioni. Livello di debug: e' una riga per annuncio.
        _log.LogDebug(
            "Annuncio instradato ({Modo}): bridge diretto {Bridge}, voce interna {Interna}, "
            + "screen reader di sistema in esecuzione {Sistema}",
            Mode, usedScreenReader, voceInterna, screenReaderDiSistema);
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

    /// <summary>
    /// Pronuncia un testo con la voce di BlindPhone, a prescindere dal canale scelto e
    /// dallo screen reader in esecuzione.
    /// </summary>
    /// <param name="text">Testo da pronunciare.</param>
    /// <remarks>
    /// Serve alla prova dei dispositivi del pannello: chi preme «suono di prova» vuole
    /// sentire da dove esce la voce degli annunci anche se in quel momento parla lo
    /// screen reader. Interrompe quello che la voce stava dicendo, come un comando.
    /// </remarks>
    public void SpeakWithInternalVoice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        SpeakInternally(text, interrupt: true);
    }

    /// <summary>
    /// Sceglie da dove esce la voce interna: il dispositivo scelto per gli annunci, se
    /// c'è, altrimenti il predefinito di sistema.
    /// </summary>
    /// <remarks>
    /// La scelta si rifà a ogni frase: cambiare il dispositivo dal pannello deve valere
    /// dall'annuncio successivo, e il motore audio torna da solo al predefinito se il
    /// dispositivo scelto viene staccato.
    /// </remarks>
    private void SpeakInternally(string text, bool interrupt)
    {
        if (_announcementOutput is { HasDedicatedDevice: true })
        {
            // Una frase urgente non deve arrivare mentre l'altra uscita sta ancora
            // finendo quella vecchia: capita nel passaggio da un'uscita all'altra.
            if (interrupt) CancelDefaultDeviceSpeech();

            EnqueueForAnnouncementDevice(text, interrupt);
            return;
        }

        if (interrupt) CancelAnnouncementDeviceSpeech();

        SpeakOnDefaultDevice(text, interrupt);
    }

    private void SpeakOnDefaultDevice(string text, bool interrupt)
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

    private void CancelDefaultDeviceSpeech()
    {
        try
        {
            _synthesizer?.SpeakAsyncCancelAll();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Interruzione della sintesi interna non riuscita");
        }
    }

    // -----------------------------------------------------------------------
    // Voce sul dispositivo scelto per gli annunci
    // -----------------------------------------------------------------------

    /// <summary>Mette una frase in coda per il dispositivo degli annunci.</summary>
    /// <remarks>
    /// Una frase urgente svuota la coda e interrompe quella che sta suonando, come
    /// faceva la sintesi di sistema con l'annullamento di tutto: un annuncio critico
    /// non aspetta che finisca l'elenco di quelli di prima.
    /// </remarks>
    private void EnqueueForAnnouncementDevice(string text, bool interrupt)
    {
        lock (_queueGate)
        {
            if (interrupt) CancelQueueLocked();

            _queue.Enqueue((text, _queueCancellation.Token));

            if (_queueRunning) return;
            _queueRunning = true;
        }

        _ = Task.Run(DrainAnnouncementQueueAsync);
    }

    private void CancelAnnouncementDeviceSpeech()
    {
        lock (_queueGate) CancelQueueLocked();
    }

    private void CancelQueueLocked()
    {
        _queue.Clear();

        // Non si chiude la sorgente annullata: la frase che sta suonando ne tiene
        // ancora il token, e lo legge per sapere che deve fermarsi.
        _queueCancellation.Cancel();
        _queueCancellation = new CancellationTokenSource();
    }

    /// <summary>
    /// Pronuncia le frasi in coda una alla volta: sintesi in memoria, poi riproduzione
    /// sul dispositivo degli annunci.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La frase si sintetizza tutta prima di suonarla. Costa poco: dopo la prima frase,
    /// che carica la voce, una frase di sei secondi si sintetizza in una ventina di
    /// millisecondi.
    /// </para>
    /// <para>
    /// <b>Se il dispositivo non suona, la frase si ripete dal predefinito di sistema.</b>
    /// Un annuncio perso è un'informazione persa, e per un operatore che non vede può
    /// essere la sola notizia di una chiamata in arrivo.
    /// </para>
    /// </remarks>
    private async Task DrainAnnouncementQueueAsync()
    {
        while (true)
        {
            (string Text, CancellationToken Cancellation) frase;

            lock (_queueGate)
            {
                if (!_queue.TryDequeue(out frase))
                {
                    _queueRunning = false;
                    return;
                }
            }

            if (frase.Cancellation.IsCancellationRequested) continue;

            try
            {
                var pcm = SynthesizeToMemory(frase.Text);
                if (pcm is null || frase.Cancellation.IsCancellationRequested) continue;

                var suonata = await _announcementOutput!
                    .PlaySpeechAsync(pcm, SpeechSampleRate, frase.Cancellation)
                    .ConfigureAwait(false);

                if (!suonata && !frase.Cancellation.IsCancellationRequested)
                {
                    _log.LogWarning(
                        "Il dispositivo degli annunci non ha suonato: la frase si ripete dal predefinito di sistema");

                    SpeakOnDefaultDevice(frase.Text, interrupt: false);
                }
            }
            catch (Exception ex)
            {
                // Una frase che non si riesce a pronunciare non deve fermare quelle dopo.
                _log.LogWarning(ex, "Annuncio sul dispositivo degli annunci non riuscito");
            }
        }
    }

    /// <summary>Sintetizza una frase in memoria, a 16 bit mono.</summary>
    /// <returns>I campioni, oppure <c>null</c> se la sintesi non è disponibile.</returns>
    private byte[]? SynthesizeToMemory(string text)
    {
        var synthesizer = GetStreamSynthesizer();
        if (synthesizer is null) return null;

        using var flusso = new MemoryStream();

        lock (_gate)
        {
            synthesizer.SetOutputToAudioStream(
                flusso,
                new SpeechAudioFormatInfo(SpeechSampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

            try
            {
                synthesizer.Speak(text);
            }
            finally
            {
                synthesizer.SetOutputToNull();
            }
        }

        return flusso.ToArray();
    }

    private SpeechSynthesizer? GetSynthesizer()
    {
        if (_synthesizerFailed) return null;

        lock (_gate)
        {
            return _synthesizer ??= CreateSynthesizer(toDefaultDevice: true);
        }
    }

    private SpeechSynthesizer? GetStreamSynthesizer()
    {
        if (_synthesizerFailed) return null;

        lock (_gate)
        {
            return _streamSynthesizer ??= CreateSynthesizer(toDefaultDevice: false);
        }
    }

    /// <summary>Crea una sintesi con voce, velocità e volume configurati.</summary>
    /// <remarks>Da chiamare dentro <c>_gate</c>.</remarks>
    private SpeechSynthesizer? CreateSynthesizer(bool toDefaultDevice)
    {
        try
        {
            var synthesizer = new SpeechSynthesizer();

            if (toDefaultDevice) synthesizer.SetOutputToDefaultAudioDevice();
            else synthesizer.SetOutputToNull();

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

            return synthesizer;
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

    /// <inheritdoc />
    public void Stop()
    {
        foreach (var bridge in _bridges)
        {
            if (bridge.IsAvailable) bridge.StopSpeech();
        }

        CancelDefaultDeviceSpeech();
        CancelAnnouncementDeviceSpeech();
    }

    /// <inheritdoc />
    public void Braille(string text)
    {
        if (!BrailleEnabled || string.IsNullOrWhiteSpace(text)) return;

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
        CancelAnnouncementDeviceSpeech();

        lock (_gate)
        {
            _synthesizer?.Dispose();
            _streamSynthesizer?.Dispose();
        }

        foreach (var bridge in _bridges)
        {
            if (bridge is IDisposable disposable) disposable.Dispose();
        }
    }
}
