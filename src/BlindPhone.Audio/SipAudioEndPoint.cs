using System.Net;
using BlindPhone.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using SIPSorceryMedia.Abstractions;

namespace BlindPhone.Audio;

/// <summary>
/// Percorso audio della conversazione: cattura dal microfono verso RTP e
/// riproduzione dell'RTP in arrivo sul dispositivo scelto.
/// </summary>
/// <remarks>
/// <para>
/// Sostituisce il generatore di silenzio che SIPSorcery usa per impostazione
/// predefinita. Quel generatore non cattura, non riproduce e <b>non emette alcun
/// pacchetto RTP</b>: il risultato è una chiamata muta in entrambe le direzioni che
/// il centralino chiude dopo pochi secondi per assenza di flusso, comportamento
/// corretto da parte sua.
/// </para>
/// <para>
/// <b>Perché il ricampionamento è inevitabile.</b> WASAPI in modalità condivisa
/// lavora alla frequenza del dispositivo, tipicamente 44100 o 48000 Hz, mentre la
/// telefonia usa 8000 Hz (G.711, G.729) o 16000 Hz (G.722). La conversione avviene
/// qui, in entrambe le direzioni, e la frequenza di destinazione dipende dal codec
/// effettivamente negoziato, non da una costante.
/// </para>
/// <para>
/// <b>Perché il framing è a 20 millisecondi.</b> È la durata di pacchettizzazione
/// standard della telefonia IP: 160 campioni a 8 kHz, 320 a 16 kHz. Pacchetti più
/// lunghi riducono l'occupazione di banda ma aumentano la latenza percepita, che su
/// un posto operatore si traduce in sovrapposizioni di voce con l'interlocutore.
/// </para>
/// </remarks>
public sealed class SipAudioEndPoint : IAudioSource, IAudioSink, IDisposable
{
    /// <summary>Durata di un pacchetto audio, in millisecondi.</summary>
    /// <remarks>
    /// Venti millisecondi è lo standard della telefonia IP: 160 campioni a 8 kHz,
    /// 320 a 16 kHz. È configurabile perché alcuni centralini impongono valori
    /// diversi, ma va cambiato solo se richiesto.
    /// </remarks>
    private int FrameMilliseconds => _tuning.PacketMilliseconds;

    private readonly IAudioEncoder _encoder;

    /// <summary>
    /// Decodificatore separato da quello di codifica.
    /// </summary>
    /// <remarks>
    /// La codifica gira sul thread della scheda audio, la decodifica su quello che
    /// riceve i pacchetti RTP. I codec a stato interno come G.722 e G.729 mantengono
    /// filtri e memorie che non sopravvivono a un accesso concorrente: condividere
    /// una sola istanza fra i due percorsi significa corrompere lo stato in modo
    /// intermittente, con distorsioni che compaiono e scompaiono senza schema
    /// riconoscibile. Due istanze separate eliminano la condizione di corsa alla
    /// radice, e costano un oggetto per chiamata.
    /// </remarks>
    private readonly IAudioEncoder _decoder;

    private readonly NAudioEngine _engine;
    private readonly AudioTuningOptions _tuning;
    private readonly ILogger<SipAudioEndPoint> _log;
    private readonly Lock _gate = new();

    private readonly List<AudioFormat> _supportedFormats;

    private AudioFormat _sourceFormat;
    private AudioFormat _sinkFormat;

    // Cattura
    private WasapiCapture? _capture;
    private WaveFormat? _captureFormat;

    /// <summary>Ricampionatore della cattura, a contabilità esplicita.</summary>
    /// <remarks>
    /// <para>
    /// Non è un <see cref="IWaveProvider"/> a trazione come nel percorso di
    /// riproduzione, ed è una differenza deliberata. Un ricampionatore a trazione
    /// decide da sé quanto ingresso consumare e quando: su una sorgente dal vivo
    /// come un microfono, che produce a ritmo fisso, quella libertà è il problema.
    /// </para>
    /// <para>
    /// <see cref="WdlResampler"/> dichiara invece in anticipo, con
    /// <c>ResamplePrepare</c>, quanti campioni gli servono per l'uscita richiesta.
    /// Se non ce ne sono abbastanza si aspetta il blocco successivo, e nessuno
    /// inventa niente per riempire il vuoto.
    /// </para>
    /// </remarks>
    private WdlResampler? _captureResampler;

    /// <summary>Campioni acquisiti e non ancora convertiti, mono, in virgola mobile.</summary>
    /// <remarks>
    /// Sostituisce il buffer a trazione di prima. Contiene solo audio reale: è la
    /// proprietà su cui si regge la correttezza del ritmo di trasmissione.
    /// </remarks>
    private float[] _captureQueue = [];
    private int _captureQueued;

    private float[] _frameFloat = [];
    private short[] _framePcm = [];
    private short[] _encodeBuffer = [];
    private int _samplesPerFrame;

    // Riproduzione
    private WasapiOut? _render;
    private BufferedWaveProvider? _renderBuffer;
    private JitterBufferProvider? _jitterBuffer;
    private SilencePaddedProvider? _renderGuard;

    /// <summary>Ricampionatore della riproduzione, applicato all'arrivo di ogni pacchetto.</summary>
    /// <remarks>
    /// Stessa scelta del percorso di cattura e per lo stesso motivo: la conversione
    /// avviene su blocchi noti, con contabilità esplicita, invece di essere affidata
    /// a un componente a trazione che decide da sé quando e quanto leggere.
    /// </remarks>
    private WdlResampler? _renderResampler;
    private WaveFormat? _renderDeviceFormat;

    private float[] _renderQueue = [];
    private int _renderQueued;
    private float[] _renderFloat = [];
    private byte[] _renderBytes = [];
    private int _renderSamplesPerFrame;
    private bool _renderIsFloat;
    private bool _captureIsFloat;

    /// <summary>
    /// Stabilisce se un formato trasporta campioni in virgola mobile.
    /// </summary>
    /// <remarks>
    /// Il controllo sulla sola <c>Encoding</c> non basta e sbagliarlo non fa
    /// rumore, il che lo rende pericoloso. Il formato di missaggio restituito da
    /// WASAPI si dichiara <c>Extensible</c>, non <c>IeeeFloat</c>, pur trasportando
    /// virgola mobile a 32 bit: scrivendoci interi a 16 bit si producono valori che
    /// come virgola mobile sono prossimi allo zero. Il risultato è silenzio, con
    /// tutti i contatori di buffer perfettamente in ordine — perché il numero di
    /// byte è giusto, è il loro contenuto a non esserlo.
    /// </remarks>
    private static bool IsFloatFormat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;

        if (format is WaveFormatExtensible extensible)
        {
            try
            {
                return extensible.ToStandardWaveFormat().Encoding == WaveFormatEncoding.IeeeFloat;
            }
            catch (Exception)
            {
                // Sottotipo non riconosciuto: si ricade sulla profondita' in bit, che
                // in modalita' condivisa e' virgola mobile a 32 bit o intero a 16.
                return format.BitsPerSample == 32;
            }
        }

        return false;
    }

    private bool _sourcePaused;
    private bool _sinkPaused;
    private bool _started;
    private bool _disposed;

    // Contatori, esposti per la diagnostica
    private long _rtpFramesSent;
    private long _rtpFramesReceived;
    private long _rtpFramesDiscarded;

    // -----------------------------------------------------------------------
    // Conferenza a tre: contributo dell'altra gamba
    // -----------------------------------------------------------------------

    private readonly Lock _conferenceGate = new();

    /// <summary>Campioni dell'altra gamba, in virgola mobile, alla frequenza con cui sono arrivati.</summary>
    private float[] _conferenceQueue = [];
    private int _conferenceQueued;

    /// <summary>
    /// Ricampionatore dal codec dell'altra gamba a quello di questa.
    /// </summary>
    /// <remarks>
    /// Esiste perché le due gambe possono aver negoziato codec diversi: è normale che
    /// l'esterno arrivi in G.729 a 8 kHz mentre l'interno risponde in G.722 a 16 kHz.
    /// Modalità guidata dall'uscita come tutti gli altri ricampionatori del percorso,
    /// e per la stessa ragione: si chiede il pacchetto che serve e si scopre quanti
    /// campioni servono per produrlo, invece di lasciare che sia il ricampionatore a
    /// decidere quando leggere.
    /// </remarks>
    private WdlResampler? _conferenceResampler;
    private int _conferenceInputRate;
    private int _conferenceOutputRate;
    private float[] _conferenceFrame = [];
    private bool _conferenceActive;

    private long _conferenceFramesMixed;
    private long _conferenceFramesStarved;

    /// <summary>Crea l'endpoint audio.</summary>
    /// <param name="encoder">Codificatore per il percorso di trasmissione.</param>
    /// <param name="decoder">
    /// Codificatore per il percorso di ricezione. Deve essere un'istanza
    /// <b>diversa</b> da <paramref name="encoder"/>: i due percorsi girano su thread
    /// distinti e i codec a stato interno non tollerano l'accesso concorrente.
    /// </param>
    /// <param name="engine">Motore audio, per l'assegnazione dei dispositivi.</param>
    /// <param name="tuning">Parametri di regolazione del buffer.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SipAudioEndPoint(
        IAudioEncoder encoder,
        IAudioEncoder decoder,
        NAudioEngine engine,
        AudioTuningOptions? tuning = null,
        ILogger<SipAudioEndPoint>? logger = null)
    {
        _encoder = encoder;
        _decoder = decoder;
        _engine = engine;
        _tuning = (tuning ?? AudioTuningOptions.Default).Sanitized();
        _log = logger ?? NullLogger<SipAudioEndPoint>.Instance;

        _supportedFormats = [.. encoder.SupportedFormats];
        _sourceFormat = _supportedFormats[0];
        _sinkFormat = _supportedFormats[0];
    }

    /// <summary>Numero di pacchetti audio inviati sulla chiamata corrente.</summary>
    /// <remarks>
    /// Serve alla diagnostica: se resta a zero mentre la chiamata è attiva, il
    /// problema è la cattura, non la rete. È la prima cosa da guardare quando
    /// l'interlocutore dice di non sentire.
    /// </remarks>
    public long RtpFramesSent => Interlocked.Read(ref _rtpFramesSent);

    /// <summary>Numero di pacchetti audio ricevuti sulla chiamata corrente.</summary>
    public long RtpFramesReceived => Interlocked.Read(ref _rtpFramesReceived);

    /// <summary>
    /// Numero di pacchetti in arrivo non decodificabili, resi come silenzio.
    /// </summary>
    /// <remarks>
    /// Con G.729 un valore diverso da zero è normale e atteso: sono i frame di
    /// comfort noise dell'Annex B inviati durante i silenzi. Se invece cresce con
    /// lo stesso ritmo dei pacchetti ricevuti, il codec negoziato non corrisponde a
    /// quello che il centralino sta effettivamente inviando.
    /// </remarks>
    public long RtpFramesDiscarded => Interlocked.Read(ref _rtpFramesDiscarded);

    /// <summary>Nome del codec effettivamente negoziato per la ricezione.</summary>
    public string NegotiatedFormatName => _sinkFormat.FormatName;

    /// <summary>
    /// Quante volte il buffer di riproduzione si è svuotato durante la chiamata.
    /// </summary>
    /// <remarks>
    /// È il numero su cui regolare il cuscinetto per prove successive invece che a
    /// orecchio. Se cresce, il valore è troppo basso per questa rete; se resta a
    /// zero si può provare a ridurlo per guadagnare in prontezza.
    /// </remarks>
    public long RenderUnderruns => _jitterBuffer?.Underruns ?? _lastUnderruns;

    /// <summary>Millisecondi di silenzio inseriti al posto di audio mancante.</summary>
    public long RenderSilenceMilliseconds => _jitterBuffer?.SilenceMilliseconds ?? _lastSilenceMilliseconds;

    // Valori trattenuti alla chiusura della riproduzione.
    //
    // Senza di questi i tre contatori qui sopra tornano a zero appena la catena
    // viene smontata, e chi legge il rapporto dopo che l'interlocutore ha
    // riagganciato vede "nessuno svuotamento" a prescindere da cosa sia successo.
    // E' il tipo di zero peggiore: indistinguibile da un esito buono.
    private long _lastUnderruns;
    private long _lastSilenceMilliseconds;
    private long _lastPaddedBytes;

    /// <summary>Millisecondi di audio attualmente in coda di riproduzione.</summary>
    public int BufferedMilliseconds => _jitterBuffer?.BufferedMilliseconds ?? 0;

    /// <summary>
    /// Byte di silenzio aggiunti per completare letture parziali del ricampionatore.
    /// </summary>
    /// <remarks>
    /// Un valore diverso da zero conferma che il ricampionatore restituisce letture
    /// corte: senza la guardia, quegli stessi byte sarebbero stati riprodotti come
    /// rumore bianco.
    /// </remarks>
    public long ResamplerPaddedBytes => _renderGuard?.PaddedBytes ?? _lastPaddedBytes;

    /// <summary>Cuscinetto configurato, in millisecondi.</summary>
    public int JitterBufferMilliseconds => _tuning.JitterBufferMilliseconds;

    /// <summary>
    /// Pacchetti trasmessi con dentro anche la voce dell'altro partecipante alla
    /// conferenza.
    /// </summary>
    /// <remarks>
    /// A conferenza aperta deve crescere con lo stesso ritmo di
    /// <see cref="RtpFramesSent"/>. Se resta molto indietro, l'altra gamba non sta
    /// consegnando audio e questo partecipante sente solo l'operatore: è la
    /// differenza fra una conferenza che non funziona e una in cui semplicemente
    /// nessuno parla, e a orecchio le due cose sono identiche.
    /// </remarks>
    public long ConferenceFramesMixed => Interlocked.Read(ref _conferenceFramesMixed);

    /// <summary>
    /// Pacchetti trasmessi senza contributo perché l'altra gamba non ne aveva
    /// abbastanza pronto.
    /// </summary>
    /// <remarks>
    /// Qualche unità all'inizio della conferenza è normale: le due gambe non sono
    /// allineate al millisecondo. Un valore che continua a crescere significa che
    /// l'altra gamba consegna meno di cinquanta pacchetti al secondo, e allora il
    /// problema è la sua ricezione, non la conferenza.
    /// </remarks>
    public long ConferenceFramesStarved => Interlocked.Read(ref _conferenceFramesStarved);

    /// <summary><c>true</c> se questa gamba sta partecipando a una conferenza.</summary>
    public bool IsInConference
    {
        get { lock (_conferenceGate) return _conferenceActive; }
    }

    /// <summary>
    /// Campioni appena decodificati dal flusso in arrivo, alla frequenza del codec
    /// negoziato in ricezione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È la presa da cui il ponte della conferenza preleva la voce di questa gamba per
    /// darla alle altre. Viene sollevato sul thread che riceve i pacchetti RTP, quindi
    /// chi lo gestisce deve limitarsi ad accodare: qualunque lavoro fatto qui ritarda
    /// la riproduzione della conversazione.
    /// </para>
    /// <para>
    /// Il vettore consegnato <b>non va modificato</b>: è lo stesso che viene poi
    /// riprodotto, e lo ricevono tutte le altre gambe.
    /// </para>
    /// </remarks>
    public event Action<SipAudioEndPoint, short[], int>? DecodedAudioReady;

    /// <summary>Mette questa gamba in conferenza.</summary>
    /// <remarks>
    /// Da sola non collega nulla: apre la coda che riceverà il contributo delle altre
    /// gambe. Il collegamento lo fa <see cref="ConferenceBridge"/>.
    /// </remarks>
    public void BeginConference()
    {
        lock (_conferenceGate)
        {
            _conferenceActive = true;
            _conferenceQueued = 0;
        }

        Interlocked.Exchange(ref _conferenceFramesMixed, 0);
        Interlocked.Exchange(ref _conferenceFramesStarved, 0);

        _log.LogInformation("Gamba di conferenza aperta sul percorso audio");
    }

    /// <summary>Toglie questa gamba dalla conferenza.</summary>
    /// <remarks>
    /// La coda viene svuotata: i campioni rimasti sono voce di qualcuno che non fa più
    /// parte della conversazione, e trasmetterli dopo lo scioglimento farebbe sentire
    /// per una frazione di secondo una persona che è appena stata congedata.
    /// </remarks>
    public void EndConference()
    {
        lock (_conferenceGate)
        {
            if (!_conferenceActive) return;

            _conferenceActive = false;
            _conferenceQueued = 0;
        }

        _log.LogInformation(
            "Gamba di conferenza chiusa: {Mixed} pacchetti missati, {Starved} senza contributo",
            ConferenceFramesMixed, ConferenceFramesStarved);
    }

    /// <summary>
    /// Consegna a questa gamba la voce di un altro partecipante, da sommare a quella
    /// dell'operatore.
    /// </summary>
    /// <param name="pcm">Campioni decodificati dell'altra gamba.</param>
    /// <param name="clockRate">Frequenza di campionamento di quei campioni.</param>
    /// <remarks>
    /// <para>
    /// Il contributo si accoda e basta: la conversione alla frequenza di questo codec
    /// avviene al momento di comporre il pacchetto, dove si sa esattamente quanti
    /// campioni servono. La coda è limitata a un quinto di secondo, perché audio di
    /// conferenza più vecchio di così sarebbe voce arrivata in ritardo e riprodurla
    /// aggiungerebbe eco invece che informazione.
    /// </para>
    /// </remarks>
    public void SubmitConferenceAudio(short[] pcm, int clockRate)
    {
        if (_disposed || pcm.Length == 0 || clockRate <= 0) return;

        lock (_conferenceGate)
        {
            if (!_conferenceActive) return;

            EnsureConferenceResampler(clockRate);

            foreach (var sample in pcm)
            {
                if (_conferenceQueued >= _conferenceQueue.Length)
                {
                    // Coda satura: si scarta il piu' vecchio. E' la scelta giusta su
                    // un flusso dal vivo — il piu' vecchio e' quello che l'altro
                    // partecipante ha gia' smesso di aspettare di sentire riprodotto.
                    var drop = Math.Min(pcm.Length, _conferenceQueue.Length);
                    Array.Copy(_conferenceQueue, drop, _conferenceQueue, 0, _conferenceQueue.Length - drop);
                    _conferenceQueued -= drop;
                }

                _conferenceQueue[_conferenceQueued++] = sample / 32768f;
            }
        }
    }

    /// <summary>
    /// Costruisce o ricostruisce il ricampionatore del contributo di conferenza.
    /// </summary>
    /// <remarks>
    /// Va invocato dentro <c>_conferenceGate</c>. Si ricostruisce quando cambia una
    /// delle due frequenze: quella di ingresso perché l'altra gamba può rinegoziare il
    /// codec a chiamata in corso, quella di uscita per lo stesso motivo su questa.
    /// </remarks>
    private void EnsureConferenceResampler(int inputRate)
    {
        var outputRate = _sourceFormat.ClockRate;
        if (outputRate <= 0) return;

        if (_conferenceResampler is not null
            && _conferenceInputRate == inputRate
            && _conferenceOutputRate == outputRate)
        {
            return;
        }

        var resampler = new WdlResampler();

        // Filtro sinc anche qui, e non per simmetria. Con l'altra gamba in G.722 a
        // 16 kHz e questa in G.729 a 8 kHz si decima 2:1, e senza filtro tutto cio'
        // che sta sopra i 4 kHz si ripiega dentro la banda vocale come fruscio —
        // proprio mentre l'altro partecipante parla, perche' e' allora che quelle
        // frequenze esistono. Il costo e' un ordine di grandezza sotto quello del
        // percorso di cattura, che decima 6:1 e usa 256.
        resampler.SetMode(interp: true, filtercnt: 2, sinc: true, sinc_size: 128);
        resampler.SetFilterParms();
        resampler.SetFeedMode(false);
        resampler.SetRates(inputRate, outputRate);

        _conferenceResampler = resampler;
        _conferenceInputRate = inputRate;
        _conferenceOutputRate = outputRate;

        // Un quinto di secondo alla frequenza di ingresso: piu' che sufficiente ad
        // assorbire il disallineamento fra due flussi RTP indipendenti, troppo poco
        // per accumulare ritardo percepibile.
        var capacity = Math.Max(inputRate / 5, 1);
        if (_conferenceQueue.Length != capacity)
        {
            _conferenceQueue = new float[capacity];
            _conferenceQueued = 0;
        }

        _log.LogInformation(
            "Contributo di conferenza: {InputRate} Hz convertiti a {OutputRate} Hz",
            inputRate, outputRate);
    }

    /// <summary>
    /// Somma al pacchetto in partenza la voce dell'altro partecipante.
    /// </summary>
    /// <param name="frame">Pacchetto già riempito con la voce dell'operatore.</param>
    /// <param name="count">Campioni utili del pacchetto.</param>
    /// <remarks>
    /// <para>
    /// La somma avviene <b>dopo</b> l'eventuale azzeramento per microfono muto, ed è
    /// deliberato: silenziare il proprio microfono durante una conferenza non deve
    /// interrompere il collegamento fra gli altri due. Il contrario sarebbe un difetto
    /// difficile da attribuire, perché chi lo provoca non lo sente.
    /// </para>
    /// <para>
    /// Si somma con saturazione invece di dimezzare i due contributi. Dimezzare
    /// costerebbe 6 dB su ogni conferenza per proteggere dal caso in cui entrambi
    /// parlano forte nello stesso istante; su una linea telefonica, dove il livello è
    /// già lontano dal fondo scala, la saturazione occasionale di qualche campione è
    /// meno dannosa di una conversazione costantemente più bassa della metà.
    /// </para>
    /// </remarks>
    private void MixConferenceContribution(short[] frame, int count)
    {
        lock (_conferenceGate)
        {
            if (!_conferenceActive) return;

            var resampler = _conferenceResampler;
            if (resampler is null) return;

            if (_conferenceFrame.Length < count) _conferenceFrame = new float[count * 2];

            var needed = resampler.ResamplePrepare(count, 1, out var input, out var inputOffset);

            if (needed <= 0 || _conferenceQueued < needed)
            {
                // Niente audio pronto dall'altra gamba: si annulla la richiesta e si
                // trasmette la sola voce dell'operatore. Non si inventa nulla, come
                // ovunque su questo percorso.
                resampler.ResampleOut(_conferenceFrame, 0, 0, 0, 1);
                Interlocked.Increment(ref _conferenceFramesStarved);
                return;
            }

            Array.Copy(_conferenceQueue, 0, input, inputOffset, needed);

            var remaining = _conferenceQueued - needed;
            if (remaining > 0) Array.Copy(_conferenceQueue, needed, _conferenceQueue, 0, remaining);
            _conferenceQueued = Math.Max(0, remaining);

            var produced = resampler.ResampleOut(_conferenceFrame, 0, needed, count, 1);
            if (produced < count)
            {
                Interlocked.Increment(ref _conferenceFramesStarved);
                return;
            }

            for (var i = 0; i < count; i++)
            {
                var sum = frame[i] + (int)Math.Round(Math.Clamp(_conferenceFrame[i], -1f, 1f) * short.MaxValue);
                frame[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            }

            Interlocked.Increment(ref _conferenceFramesMixed);
        }
    }

    // -----------------------------------------------------------------------
    // IAudioSource: eventi
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;

    /// <inheritdoc />
    public event SourceErrorDelegate? OnAudioSourceError;

    // I due eventi seguenti fanno parte del contratto di IAudioSource ma questo
    // endpoint non li solleva mai, deliberatamente: consegna sempre campioni gia'
    // codificati tramite OnAudioSourceEncodedSample. Sollevare anche il campione
    // grezzo significherebbe far codificare due volte lo stesso audio dallo stack,
    // raddoppiando il carico su un percorso che gira ogni 20 millisecondi.
#pragma warning disable CS0067

    /// <inheritdoc />
    public event RawAudioSampleDelegate? OnAudioSourceRawSample;

    /// <inheritdoc />
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;

#pragma warning restore CS0067

    /// <inheritdoc />
    public event SourceErrorDelegate? OnAudioSinkError;

    // -----------------------------------------------------------------------
    // IAudioSource: formati
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public List<AudioFormat> GetAudioSourceFormats() => [.. _supportedFormats];

    /// <inheritdoc />
    public List<AudioFormat> GetAudioSinkFormats() => [.. _supportedFormats];

    /// <inheritdoc />
    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        var previousRate = _sourceFormat.ClockRate;
        _sourceFormat = audioFormat;

        _log.LogInformation(
            "Codec di trasmissione negoziato: {Codec} a {Rate} Hz",
            audioFormat.FormatName, audioFormat.ClockRate);

        // Il ricampionatore dipende dalla sola frequenza del codec: cambiando codec a
        // chiamata in corso (raro ma possibile con un re-INVITE) va ricostruito solo
        // se cambia quella.
        //
        // Il controllo non e' un'ottimizzazione. Sulla chiamata del 4 agosto questo
        // metodo e' stato invocato tre volte, tutte con lo stesso G.729 a 8000 Hz, e
        // ogni invocazione riapriva il microfono: tre aperture in tre secondi, con i
        // campioni gia' in coda buttati via a ogni giro.
        if (_started && audioFormat.ClockRate != previousRate) RestartCapture();
    }

    /// <inheritdoc />
    public void SetAudioSinkFormat(AudioFormat audioFormat)
    {
        var previousRate = _sinkFormat.ClockRate;
        _sinkFormat = audioFormat;

        _log.LogInformation(
            "Codec di ricezione negoziato: {Codec} a {Rate} Hz",
            audioFormat.FormatName, audioFormat.ClockRate);

        // La catena di riproduzione dipende dalla sola frequenza di campionamento,
        // non dal codec: ricostruirla quando cambia solo il secondo produce un buco
        // udibile nella conversazione senza cambiare nulla di utile.
        if (_started && audioFormat.ClockRate != previousRate) RestartRender();
    }

    /// <summary>
    /// Allinea il formato di ricezione a quello dichiarato dai pacchetti in arrivo.
    /// </summary>
    /// <param name="observed">Formato con cui il flusso sta effettivamente viaggiando.</param>
    /// <remarks>
    /// <para>
    /// Esiste perché <see cref="SetAudioSinkFormat"/> <b>può non essere mai
    /// chiamato</b>. È stato osservato su una chiamata reale negoziata in G.729: lo
    /// stack ha comunicato il formato di trasmissione tre volte e quello di
    /// ricezione mai, lasciando il decodificatore sul valore iniziale — PCMU, che
    /// dopo la restrizione dei codec non era nemmeno più fra quelli offerti.
    /// </para>
    /// <para>
    /// Il risultato non è silenzio, ed è ciò che rende il guasto difficile da
    /// leggere: un flusso G.729 passato per la tabella µ-law produce campioni
    /// casuali a fondo scala, cioè <b>rumore bianco</b>, e per giunta un ottavo di
    /// quelli che dovrebbe. Venti campioni per pacchetto invece di centosessanta
    /// significano 2,5 ms di audio ogni 20 ms di conversazione: il cuscinetto non si
    /// riempie mai, si svuota circa una volta al secondo e a ogni ricarica sputa una
    /// raffica di rumore.
    /// </para>
    /// <para>
    /// I pacchetti sanno da soli cosa contengono, quindi si crede a loro e non alla
    /// segnalazione: <c>EncodedAudioFrame</c> porta con sé il proprio
    /// <c>AudioFormat</c>, e sull'altra porta di ingresso il tipo di payload RTP
    /// permette di risalire allo stesso dato.
    /// </para>
    /// </remarks>
    private void AdoptSinkFormat(AudioFormat observed)
    {
        // Un formato non valorizzato non e' un'informazione: adottarlo porterebbe la
        // catena di riproduzione a zero hertz, che non e' costruibile.
        if (observed.ClockRate <= 0) return;

        if (observed.Codec == _sinkFormat.Codec && observed.ClockRate == _sinkFormat.ClockRate) return;

        _log.LogWarning(
            "Formato di ricezione riallineato al flusso reale: la segnalazione diceva "
            + "{Old} a {OldRate} Hz, i pacchetti sono {New} a {NewRate} Hz",
            _sinkFormat.FormatName, _sinkFormat.ClockRate,
            observed.FormatName, observed.ClockRate);

        SetAudioSinkFormat(observed);
    }

    /// <summary>
    /// Risale al formato a partire dal tipo di payload RTP.
    /// </summary>
    /// <returns>
    /// Il formato corrispondente, oppure <c>null</c> se il tipo non è fra quelli
    /// negoziabili — caso dei <c>telephone-event</c> che trasportano i DTMF, che non
    /// sono audio e non devono spostare il codec di ricezione.
    /// </returns>
    private AudioFormat? FormatForPayloadId(int payloadId)
    {
        lock (_gate)
        {
            foreach (var format in _supportedFormats)
            {
                if (format.FormatID == payloadId) return format;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        lock (_gate)
        {
            var allowed = _supportedFormats.Where(filter).ToList();

            // Un elenco vuoto renderebbe impossibile negoziare qualunque chiamata:
            // meglio ignorare un filtro troppo restrittivo che restare muti.
            if (allowed.Count == 0)
            {
                _log.LogWarning("Filtro sui codec troppo restrittivo: ignorato");
                return;
            }

            _supportedFormats.Clear();
            _supportedFormats.AddRange(allowed);

            // I due formati correnti vengono dal costruttore, che ha preso il primo
            // dell'elenco non ancora filtrato: tipicamente PCMU, che qui viene quasi
            // sempre rimosso perche' in Europa si offre A-law. Lasciarli puntati a un
            // codec che non e' piu' fra quelli offerti significa che, se lo stack non
            // comunica mai il formato negoziato, si codifica e si decodifica con un
            // codec che la controparte non ha mai visto.
            if (allowed.All(f => f.Codec != _sourceFormat.Codec)) _sourceFormat = allowed[0];
            if (allowed.All(f => f.Codec != _sinkFormat.Codec)) _sinkFormat = allowed[0];
        }
    }

    // -----------------------------------------------------------------------
    // IAudioSource: cattura
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public Task StartAudio()
    {
        if (_started) return Task.CompletedTask;

        _started = true;
        Interlocked.Exchange(ref _rtpFramesSent, 0);
        Interlocked.Exchange(ref _rtpFramesReceived, 0);
        Interlocked.Exchange(ref _rtpFramesDiscarded, 0);

        _lastUnderruns = 0;
        _lastSilenceMilliseconds = 0;
        _lastPaddedBytes = 0;

        StartCapture();
        StartRender();

        return Task.CompletedTask;
    }

    private void StartCapture()
    {
        try
        {
            var device = ResolveDevice(AudioRoute.Microphone, DataFlow.Capture);
            if (device is null)
            {
                RaiseSourceError("Nessun microfono disponibile");
                return;
            }

            _capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
            _captureFormat = _capture.WaveFormat;
            _captureIsFloat = IsFloatFormat(_captureFormat);

            _captureResampler = new WdlResampler();

            // FILTRO ANTI-ALIAS: sinc lungo, e la lunghezza non e' un capriccio.
            //
            // Scendere da 48000 a 8000 Hz butta cinque campioni su sei. Tutto cio'
            // che nel segnale sta sopra i 4000 Hz — le sibilanti della voce, il
            // rumore della stanza, il rumore proprio del microfono — se non viene
            // filtrato prima non sparisce: si ripiega dentro la banda vocale come
            // rumore a banda larga, cioe' come fruscio, e si sente solo mentre si
            // parla perche' e' allora che quelle frequenze esistono.
            //
            // Misurato su banco, con un tono fuori banda e guardando dove ricompare:
            //
            //             4500 Hz   5000 Hz   6000 Hz
            //   sinc off   ------    -22 dB    -29 dB
            //   sinc 64    -13 dB    -23 dB    -43 dB
            //   sinc 256   -43 dB    -50 dB    -92 dB
            //
            // Costa due milioni di moltiplicazioni al secondo, che su un PC non si
            // misurano. La versione precedente usava il ricampionatore di Media
            // Foundation a qualita' 60, che questo filtro ce l'aveva: passando a
            // WdlResampler senza sinc lo si era perso.
            _captureResampler.SetMode(interp: true, filtercnt: 2, sinc: true, sinc_size: 256);
            _captureResampler.SetFilterParms();

            // Modalita' guidata dall'uscita: si chiede un pacchetto e il
            // ricampionatore dichiara quanti campioni gli servono per produrlo.
            _captureResampler.SetFeedMode(false);
            _captureResampler.SetRates(_captureFormat.SampleRate, _sourceFormat.ClockRate);

            _samplesPerFrame = _sourceFormat.ClockRate * FrameMilliseconds / 1000;
            _frameFloat = new float[_samplesPerFrame * 2];
            _framePcm = new short[_samplesPerFrame];

            // Un secondo di margine. Non serve a bufferizzare — l'arretrato a regime
            // e' di un pacchetto — ma ad assorbire una consegna anomala senza
            // perdere audio.
            _captureQueue = new float[_captureFormat.SampleRate];
            _captureQueued = 0;

            _capture.DataAvailable += OnCaptureDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _capture.StartRecording();

            _log.LogInformation(
                "Cattura avviata su {Device}: {DeviceRate} Hz {Channels} canale/i {Bits} bit {Encoding}, "
                + "convertiti a {TargetRate} Hz mono, pacchetti da {Frame} ms",
                device.FriendlyName, _captureFormat.SampleRate, _captureFormat.Channels,
                _captureFormat.BitsPerSample, _captureFormat.Encoding,
                _sourceFormat.ClockRate, FrameMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Avvio della cattura audio non riuscito");
            RaiseSourceError("Microfono non disponibile");
        }
    }

    /// <summary>
    /// Riceve i campioni dal microfono e ne ricava i pacchetti da trasmettere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>È qui che la catena viene pilotata, e non da un timer.</b> La versione
    /// precedente usava un <c>System.Threading.Timer</c> con periodo di 20
    /// millisecondi: su Windows la risoluzione predefinita del timer è 15,625
    /// millisecondi, quindi quel timer non poteva scattare a 20 — scattava a 15,6 o
    /// a 31,2, in modo irregolare e scollegato dal clock della scheda audio.
    /// </para>
    /// <para>
    /// Le conseguenze erano due, entrambe percepibili. In trasmissione, letture in
    /// anticipo sul microfono producevano pacchetti di silenzio al posto della voce,
    /// e l'audio non consumato si accumulava fino a essere scartato per
    /// traboccamento. In generale, un flusso RTP con cadenza irregolare costringe il
    /// buffer di jitter della controparte a inseguire, ed è esattamente ciò che si
    /// sente come voce a tratti.
    /// </para>
    /// <para>
    /// Ora il ritmo lo detta la scheda audio, che è l'unico orologio affidabile
    /// della catena: si trasmette esattamente l'audio che è stato acquisito, quando
    /// è stato acquisito.
    /// </para>
    /// <para>
    /// <b>Perché la conversione avviene qui e non in una catena a trazione.</b> La
    /// versione precedente affidava il ricampionamento a
    /// <c>MediaFoundationResampler</c>, letto a valle di un buffer con
    /// <c>ReadFully</c> attivo. Misurato a cadenza reale, quel percorso emetteva
    /// <b>6,6 pacchetti per ogni pacchetto dovuto</b> e riempiva la linea di
    /// silenzio fabbricato dal buffer per soddisfare letture in anticipo di
    /// dimensione imprevedibile — mentre l'audio vero si accumulava a monte fino a
    /// essere scartato per traboccamento. Da fuori si sente come voce frammentata,
    /// perché lo è: la controparte riceve piu' dati di quanti possa riprodurre, e
    /// una parte di quello che riceve è silenzio al posto di parole.
    /// </para>
    /// </remarks>
    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;

        try
        {
            AppendCapturedSamples(e.Buffer, e.BytesRecorded);
            DrainCaptureFrames();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Elaborazione dei campioni acquisiti non riuscita");
        }
    }

    /// <summary>
    /// Converte un blocco dal formato del dispositivo a mono in virgola mobile e lo
    /// accoda.
    /// </summary>
    /// <remarks>
    /// WASAPI in modalità condivisa consegna nel formato di missaggio del
    /// dispositivo, che su Windows è tipicamente virgola mobile a 32 bit ma non è
    /// garantito esserlo: si gestiscono entrambe le codifiche. I canali multipli si
    /// mediano, perché la telefonia è monofonica e scartare un canale
    /// significherebbe perdere metà del segnale su un microfono stereo.
    /// </remarks>
    private void AppendCapturedSamples(byte[] data, int count)
    {
        var format = _captureFormat;
        if (format is null || count <= 0) return;

        var bytesPerSample = format.BitsPerSample / 8;
        var blockAlign = bytesPerSample * format.Channels;
        if (blockAlign <= 0) return;

        var isFloat = _captureIsFloat;

        for (var offset = 0; offset + blockAlign <= count; offset += blockAlign)
        {
            var sum = 0f;

            for (var channel = 0; channel < format.Channels; channel++)
            {
                var index = offset + channel * bytesPerSample;

                sum += isFloat
                    ? BitConverter.ToSingle(data, index)
                    : BitConverter.ToInt16(data, index) / 32768f;
            }

            if (_captureQueued >= _captureQueue.Length)
            {
                // Un secondo di arretrato significa che il drenaggio non sta girando:
                // si scarta il piu' vecchio, che e' quello che l'interlocutore ha
                // gia' smesso di aspettare.
                Array.Copy(_captureQueue, _samplesPerFrame, _captureQueue, 0, _captureQueue.Length - _samplesPerFrame);
                _captureQueued -= _samplesPerFrame;

                _log.LogDebug("Coda di cattura satura: scartati {Samples} campioni", _samplesPerFrame);
            }

            _captureQueue[_captureQueued++] = sum / format.Channels;
        }
    }

    /// <summary>
    /// Produce e trasmette tutti i pacchetti interi che l'audio acquisito giustifica.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il ciclo si ferma quando i campioni in coda non bastano più a completare un
    /// pacchetto: si aspetta la consegna successiva del microfono invece di
    /// completare con silenzio. È l'unica regola che serve, e sostituisce la stima
    /// del numero di pacchetti che la versione precedente ricavava dal livello del
    /// buffer — una grandezza che il ricampionatore a trazione svuotava a raffiche
    /// proprie, scollegate dai 20 millisecondi di un pacchetto.
    /// </para>
    /// <para>
    /// La terminazione è garantita: ogni giro consuma <c>needed</c> campioni, che è
    /// sempre positivo, e la coda è limitata a un secondo.
    /// </para>
    /// </remarks>
    private void DrainCaptureFrames()
    {
        var resampler = _captureResampler;
        if (resampler is null || _samplesPerFrame <= 0) return;

        while (true)
        {
            var needed = resampler.ResamplePrepare(_samplesPerFrame, 1, out var input, out var inputOffset);

            if (needed <= 0 || _captureQueued < needed)
            {
                // Si annulla la richiesta dichiarando zero campioni forniti, e si
                // aspetta. Nulla viene inventato per riempire il vuoto.
                resampler.ResampleOut(_frameFloat, 0, 0, 0, 1);
                return;
            }

            Array.Copy(_captureQueue, 0, input, inputOffset, needed);
            ConsumeQueuedSamples(needed);

            var produced = resampler.ResampleOut(_frameFloat, 0, needed, _samplesPerFrame, 1);
            if (produced < _samplesPerFrame) return;

            for (var i = 0; i < _samplesPerFrame; i++)
            {
                _framePcm[i] = ToPcm16(_frameFloat[i]);
            }

            SendFrame(_framePcm, _samplesPerFrame);
        }
    }

    /// <summary>
    /// Converte un campione da virgola mobile a intero a 16 bit, <b>arrotondando</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il cast diretto di C# tronca verso lo zero. Su un convertitore audio la
    /// differenza fra troncare e arrotondare non è un dettaglio da manuale: il
    /// troncamento produce un errore che ha sempre lo stesso segno e resta
    /// correlato al segnale, quindi non si comporta come rumore innocuo ma come una
    /// patina che segue la voce. L'arrotondamento dimezza l'errore e lo rende
    /// simmetrico.
    /// </para>
    /// <para>
    /// Conta qui più che altrove per una ragione misurata: il microfono di questa
    /// postazione produce picchi intorno a &#8722;23 dBFS, cioè usa meno di un
    /// quattordicesimo della dinamica disponibile. Su un segnale che occupa dodici
    /// bit invece di sedici, un errore di quantizzazione correlato si sente.
    /// </para>
    /// </remarks>
    private static short ToPcm16(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        return (short)Math.Round(clamped * short.MaxValue);
    }

    /// <summary>Rimuove dalla testa della coda i campioni già convertiti.</summary>
    private void ConsumeQueuedSamples(int count)
    {
        var remaining = _captureQueued - count;

        // Il residuo e' sempre inferiore a un pacchetto di ingresso, quindi la copia
        // e' su poche centinaia di campioni: non vale un buffer circolare.
        if (remaining > 0) Array.Copy(_captureQueue, count, _captureQueue, 0, remaining);

        _captureQueued = Math.Max(0, remaining);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;

        // Tipicamente il microfono e' stato scollegato a caldo. Va segnalato:
        // l'operatore continuerebbe a parlare senza che nessuno lo senta.
        _log.LogWarning(e.Exception, "Cattura audio interrotta");
        RaiseSourceError("Microfono scollegato");
    }

    /// <summary>
    /// Legge un pacchetto da 20 ms, lo codifica e lo consegna allo stack RTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il silenziamento del microfono azzera i campioni ma <b>non interrompe la
    /// trasmissione</b>: continuano a partire pacchetti, con contenuto silenzioso.
    /// Interrompere il flusso RTP durante il mute farebbe scattare i timeout di
    /// inattività del centralino, e la chiamata cadrebbe mentre l'operatore è
    /// semplicemente in muto.
    /// </para>
    /// </remarks>
    /// <param name="samples">Campioni del pacchetto, già alla frequenza del codec.</param>
    /// <param name="count">Numero di campioni utili.</param>
    private void SendFrame(short[] samples, int count)
    {
        if (_disposed) return;

        try
        {
            // Il G.729 codifica blocchi da 10 ms e scarta una coda parziale: si
            // tronca alla granularita' del codec invece di consegnargli campioni
            // che non userebbe.
            var sampleCount = AlignSampleCount(count, _sourceFormat);
            if (sampleCount <= 0) return;

            // Il codificatore vuole un vettore della lunghezza esatta. Si riusa fra
            // un pacchetto e l'altro: e' un percorso che gira cinquanta volte al
            // secondo per tutta la durata della chiamata.
            if (_encodeBuffer.Length != sampleCount) _encodeBuffer = new short[sampleCount];

            // In muto si trasmette silenzio, non si smette di trasmettere.
            if (_sourcePaused) Array.Clear(_encodeBuffer);
            else Array.Copy(samples, _encodeBuffer, sampleCount);

            // In conferenza a questo interlocutore va anche la voce dell'altro. Il
            // missaggio viene dopo il muto di proposito: silenziare il proprio
            // microfono non deve scollegare fra loro gli altri due partecipanti.
            MixConferenceContribution(_encodeBuffer, sampleCount);

            // L'AVVISO DI REGISTRAZIONE PASSA ANCHE SOPRA IL MUTO.
            //
            // Sta dopo il muto e dopo la conferenza perche' e' l'unica cosa che deve
            // arrivare all'interlocutore in ogni caso: un operatore col microfono
            // silenziato che avvia la registrazione senza che l'altro lo sappia e'
            // esattamente la situazione che l'avviso esiste per impedire.
            MixRemoteAnnouncement(_encodeBuffer, sampleCount);

            // La presa per la registrazione della conversazione sta qui: e' il
            // contenuto esatto che l'interlocutore sentira', voce dell'operatore e
            // avvisi compresi.
            _callRecorder?.WriteOutgoing(_encodeBuffer, sampleCount);

            var encoded = _encoder.EncodeAudio(_encodeBuffer, _sourceFormat);
            if (encoded.Length == 0) return;

            // La durata si esprime nelle unita' di clock RTP del codec, che per
            // G.722 sono la meta' della frequenza di campionamento: e' una
            // particolarita' storica di quel codec, non un errore.
            var durationRtpUnits = (uint)(_sourceFormat.RtpClockRate * FrameMilliseconds / 1000);

            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
            Interlocked.Increment(ref _rtpFramesSent);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Invio del pacchetto audio non riuscito");
        }
    }

    // -----------------------------------------------------------------------
    // IAudioSink: riproduzione
    // -----------------------------------------------------------------------

    private void StartRender()
    {
        try
        {
            var device = ResolveDevice(AudioRoute.Conversation, DataFlow.Render);
            if (device is null)
            {
                RaiseSinkError("Nessun dispositivo di riproduzione disponibile");
                return;
            }

            _render = new WasapiOut(
                device,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: _tuning.DeviceLatencyMilliseconds);

            // Si usa il formato di missaggio del dispositivo e non
            // WasapiOut.OutputWaveFormat: quest'ultimo e' valorizzato solo dopo
            // Init, quindi leggerlo prima significa costruire il ricampionatore su
            // un formato non ancora definito.
            var deviceFormat = device.AudioClient.MixFormat;
            _renderDeviceFormat = deviceFormat;
            _renderIsFloat = IsFloatFormat(deviceFormat);

            // Il buffer contiene audio GIA' alla frequenza del dispositivo: la
            // conversione avviene all'arrivo di ogni pacchetto, non qui. Vedi
            // RenderDecodedSamples.
            _renderBuffer = new BufferedWaveProvider(deviceFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(_tuning.MaxBufferMilliseconds),
                DiscardOnBufferOverflow = true,

                // ReadFully DEVE essere disattivato qui, ed e' l'esatto contrario di
                // quanto serve sul buffer di cattura.
                //
                // NAudio lo lascia attivo per impostazione predefinita: con quel
                // valore Read completa da se' con silenzio e restituisce sempre il
                // conteggio pieno. JitterBufferProvider riconosce lo svuotamento
                // proprio da una lettura corta, quindi non ne vedeva mai nessuna: il
                // contatore degli svuotamenti restava a zero anche a buffer
                // completamente vuoto, e soprattutto il cuscinetto non veniva mai
                // ricostruito dopo un vuoto. Da quel momento ogni pacchetto in
                // ritardo produceva un'interruzione, invece che la prima soltanto.
                //
                // Disattivandolo, il buffer di jitter vede il vuoto, lo conta, e
                // torna in fase di riempimento. La lettura resta comunque sempre
                // piena verso valle: ci pensa JitterBufferProvider.Read, che e' il
                // punto giusto in cui garantirlo perche' e' anche quello che sa
                // distinguere il silenzio voluto dal silenzio per mancanza di audio.
                ReadFully = false,
            };

            // CATENA DI RIPRODUZIONE, nell'ordine in cui i campioni la percorrono.
            //
            //   pacchetti RTP
            //        v
            //   decodifica + WdlResampler   conversione ALL'ARRIVO, un pacchetto per
            //                               volta, alla frequenza del dispositivo
            //        v
            //   BufferedWaveProvider        accumula quello che arriva dalla rete
            //        v
            //   JitterBufferProvider        aspetta il cuscinetto prima di partire,
            //                               conta gli svuotamenti, lettura sempre piena
            //        v
            //   SilencePaddedProvider       ultima garanzia di lunghezza verso WASAPI
            //        v
            //   WasapiOut
            //
            // NON c'e' piu' alcun ricampionatore fra il buffer e WASAPI, ed e' la
            // ragione di questa disposizione. MediaFoundationResampler, che stava in
            // quel punto, legge a raffiche di circa un secondo invece che a pacchetti
            // da 20 ms: a ogni raffica chiedeva piu' di quanto il buffer contenesse,
            // il buffer di jitter completava con mezzo secondo di silenzio inventato
            // e contava uno svuotamento, mentre l'RTP che continuava ad arrivare
            // riempiva il buffer fino al tetto e veniva scartato. Misurato su chiamata
            // reale: 37 svuotamenti in 37 secondi e il 52% della conversazione
            // sostituito da silenzio, con la voce che era arrivata integra.
            //
            // Convertendo all'arrivo, WASAPI attinge direttamente da audio gia'
            // pronto: il silenzio si inserisce solo quando l'audio davvero non e'
            // arrivato, che e' l'unico caso in cui deve esserci.
            _jitterBuffer = new JitterBufferProvider(_renderBuffer, _tuning.JitterBufferMilliseconds);
            _renderGuard = new SilencePaddedProvider(_jitterBuffer);

            _renderResampler = new WdlResampler();

            // Anche qui filtro sinc, ma piu' corto che in cattura, e la differenza
            // e' motivata. Qui si sale di frequenza: le repliche spurie finiscono
            // SOPRA i 4000 Hz, fuori dalla banda della voce, dove si percepiscono
            // come durezza e non come rumore sul parlato. In cattura invece si
            // scende, e li' l'energia fuori banda si ripiega DENTRO la voce: e' il
            // caso che va soffocato piu' a fondo.
            _renderResampler.SetMode(interp: true, filtercnt: 2, sinc: true, sinc_size: 128);
            _renderResampler.SetFilterParms();
            _renderResampler.SetFeedMode(false);
            _renderResampler.SetRates(_sinkFormat.ClockRate, deviceFormat.SampleRate);

            _renderSamplesPerFrame = deviceFormat.SampleRate * FrameMilliseconds / 1000;
            _renderFloat = new float[_renderSamplesPerFrame * 2];
            _renderBytes = new byte[_renderSamplesPerFrame * deviceFormat.Channels * (deviceFormat.BitsPerSample / 8)];

            // Un secondo di margine, alla frequenza del codec.
            _renderQueue = new float[Math.Max(1, _sinkFormat.ClockRate)];
            _renderQueued = 0;

            _render.Init(_renderGuard);
            _render.Play();

            _log.LogInformation(
                "Riproduzione avviata su {Device}: {Codec} a {CodecRate} Hz convertiti all'arrivo "
                + "a {DeviceRate} Hz {Channels} canali {Bits} bit {Encoding}, "
                + "cuscinetto {Jitter} ms, latenza dispositivo {Latency} ms",
                device.FriendlyName, _sinkFormat.FormatName, _sinkFormat.ClockRate,
                deviceFormat.SampleRate, deviceFormat.Channels, deviceFormat.BitsPerSample,
                deviceFormat.Encoding, _tuning.JitterBufferMilliseconds, _tuning.DeviceLatencyMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Avvio della riproduzione audio non riuscito");
            RaiseSinkError("Cuffia o altoparlanti non disponibili");
        }
    }

    /// <summary>
    /// Da quale delle due porte di ingresso dello stack arriva l'audio ricevuto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IAudioSink"/> ne espone due: <c>GotAudioRtp</c>, storica, e
    /// <c>GotEncodedMediaFrame</c>, introdotta più tardi. Quale delle due venga
    /// effettivamente usata dipende dalla versione dello stack e dal percorso
    /// interno, e <b>non è escluso che vengano invocate entrambe per lo stesso
    /// pacchetto</b>.
    /// </para>
    /// <para>
    /// Se accadesse, ogni pacchetto verrebbe scritto due volte nella coda di
    /// riproduzione: il buffer si riempirebbe al doppio della velocità di consumo,
    /// arriverebbe al limite, scarterebbe, si svuoterebbe e ricomincerebbe. Il
    /// risultato udibile è voce che va e viene a ritmo regolare, con i frammenti
    /// scartati che spezzano l'allineamento dei campioni e producono rumore.
    /// </para>
    /// <para>
    /// La prima porta che consegna vince; l'altra viene ignorata per il resto della
    /// chiamata. Costa un confronto per pacchetto ed elimina il rischio a
    /// prescindere da quale versione dello stack si abbia sotto.
    /// </para>
    /// </remarks>
    private enum SinkEntryPoint
    {
        /// <summary>Nessun pacchetto ancora ricevuto.</summary>
        None,

        /// <summary>Consegna tramite <c>GotAudioRtp</c>.</summary>
        Rtp,

        /// <summary>Consegna tramite <c>GotEncodedMediaFrame</c>.</summary>
        EncodedFrame,
    }

    private SinkEntryPoint _sinkEntryPoint = SinkEntryPoint.None;
    private long _duplicateDeliveries;

    /// <summary>
    /// Numero di pacchetti scartati perché arrivati dalla porta di ingresso
    /// secondaria.
    /// </summary>
    /// <remarks>
    /// Un valore che cresce insieme ai ricevuti conferma la doppia consegna. Zero
    /// significa che lo stack ne usa una sola, e che quel rischio non era la causa.
    /// </remarks>
    public long DuplicateDeliveries => Interlocked.Read(ref _duplicateDeliveries);

    /// <summary>
    /// Verifica che il pacchetto arrivi dalla porta di ingresso accettata.
    /// </summary>
    private bool AcceptDelivery(SinkEntryPoint entryPoint)
    {
        if (_sinkEntryPoint == SinkEntryPoint.None)
        {
            _sinkEntryPoint = entryPoint;

            _log.LogInformation(
                "Audio ricevuto tramite {EntryPoint}: l'altra porta di ingresso viene ignorata",
                entryPoint);

            return true;
        }

        if (_sinkEntryPoint == entryPoint) return true;

        Interlocked.Increment(ref _duplicateDeliveries);
        return false;
    }

    /// <inheritdoc />
    public void GotAudioRtp(
        IPEndPoint remoteEndPoint,
        uint ssrc,
        uint seqnum,
        uint timestamp,
        int payloadID,
        bool marker,
        byte[] payload)
    {
        if (_disposed || _sinkPaused || payload.Length == 0) return;
        if (!AcceptDelivery(SinkEntryPoint.Rtp)) return;

        // Il tipo di payload e' il formato dichiarato dal pacchetto stesso: vale piu'
        // di quanto abbia detto la segnalazione, che puo' non aver detto nulla.
        if (FormatForPayloadId(payloadID) is { } observed) AdoptSinkFormat(observed);

        DeliverAudioPayload(payload);
    }

    /// <inheritdoc />
    public void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame)
    {
        if (_disposed || _sinkPaused) return;

        var payload = encodedMediaFrame.EncodedAudio;
        if (payload is null || payload.Length == 0) return;
        if (!AcceptDelivery(SinkEntryPoint.EncodedFrame)) return;

        // Il frame dichiara il proprio formato: e' la fonte piu' attendibile che
        // esista, perche' descrive i byte che porta invece di quelli che erano
        // stati promessi in fase di negoziazione.
        AdoptSinkFormat(encodedMediaFrame.AudioFormat);

        DeliverAudioPayload(payload);
    }

    /// <summary>
    /// Decodifica un pacchetto e lo accoda alla riproduzione.
    /// </summary>
    /// <param name="payload">Contenuto codificato del pacchetto RTP.</param>
    private void DeliverAudioPayload(byte[] payload)
    {
        if (!IsDecodablePayload(payload, _sinkFormat))
        {
            // Pacchetto non decodificabile con il codec negoziato: si inserisce
            // silenzio della durata corrispondente invece di saltarlo. Saltarlo
            // creerebbe un vuoto nel flusso, e con ReadFully attivo il buffer si
            // svuoterebbe producendo scatti.
            InsertSilence(FrameMilliseconds);
            Interlocked.Increment(ref _rtpFramesDiscarded);
            return;
        }

        try
        {
            var pcm = _decoder.DecodeAudio(payload, _sinkFormat);
            if (pcm.Length == 0) return;

            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

            // Il conteggio precede la registrazione: se scrivere sul file fallisse,
            // il contatore dei ricevuti non deve risentirne. E' il numero su cui si
            // decide se il problema e' in ricezione, e un errore di scrittura che lo
            // azzerasse porterebbe la diagnosi esattamente nella direzione sbagliata.
            Interlocked.Increment(ref _rtpFramesReceived);

            // La registrazione diagnostica preleva qui, alla frequenza del codec e
            // prima di qualunque conversione: e' cio' che le permette di separare
            // "arriva gia' rovinato" da "lo rovina la riproduzione locale".
            WriteToRecording(bytes, bytes.Length);

            // La registrazione della conversazione preleva dallo stesso punto, per la
            // stessa ragione: e' la voce dell'interlocutore come e' arrivata, prima che
            // la catena locale possa metterci del suo.
            _callRecorder?.WriteIncoming(pcm, pcm.Length);

            // La presa per la conferenza sta qui, sull'audio decodificato e prima di
            // qualunque conversione verso il dispositivo: e' la stessa voce che verra'
            // riprodotta all'operatore, e alle altre gambe serve alla frequenza del
            // codec, non a quella della scheda audio.
            //
            // Prima della riproduzione, non dopo: il ponte deve consegnare all'altra
            // gamba il piu' presto possibile, perche' il ritardo che accumula qui si
            // somma a quello della rete e diventa la latenza che i due partecipanti si
            // sentono l'un l'altro.
            DecodedAudioReady?.Invoke(this, pcm, _sinkFormat.ClockRate);

            RenderDecodedSamples(pcm);
        }
        catch (Exception ex)
        {
            // Un pacchetto malformato non deve interrompere la conversazione: si
            // scarta e si prosegue, perche' su una linea disturbata i pacchetti
            // corrotti sono normali.
            _log.LogDebug(ex, "Pacchetto audio in arrivo non decodificabile");
        }
    }

    /// <summary>
    /// Converte i campioni decodificati alla frequenza del dispositivo e li accoda
    /// alla riproduzione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È il contraltare di <c>DrainCaptureFrames</c>, e obbedisce alla stessa
    /// regola: si produce un blocco solo quando i campioni per farlo ci sono
    /// davvero, altrimenti si aspetta il pacchetto successivo. Il residuo che non
    /// basta a completare un blocco resta in coda.
    /// </para>
    /// <para>
    /// L'attesa qui non produce interruzioni, perché a valle c'è
    /// <see cref="JitterBufferProvider"/>: è quello il punto in cui il silenzio va
    /// inserito, quando l'audio non è arrivato per davvero, ed è anche l'unico che
    /// sappia contarlo.
    /// </para>
    /// </remarks>
    private void RenderDecodedSamples(short[] pcm)
    {
        var resampler = _renderResampler;
        var buffer = _renderBuffer;
        var format = _renderDeviceFormat;

        if (resampler is null || buffer is null || format is null || _renderSamplesPerFrame <= 0) return;

        // 1) accodamento dei campioni decodificati, in virgola mobile
        foreach (var sample in pcm)
        {
            if (_renderQueued >= _renderQueue.Length)
            {
                var drop = Math.Min(pcm.Length, _renderQueue.Length);
                Array.Copy(_renderQueue, drop, _renderQueue, 0, _renderQueue.Length - drop);
                _renderQueued -= drop;

                _log.LogDebug("Coda di riproduzione satura: scartati {Samples} campioni", drop);
            }

            _renderQueue[_renderQueued++] = sample / 32768f;
        }

        // 2) produzione dei blocchi interi che i campioni giustificano
        var bytesPerSample = format.BitsPerSample / 8;
        var isFloat = _renderIsFloat;

        while (true)
        {
            var needed = resampler.ResamplePrepare(_renderSamplesPerFrame, 1, out var input, out var inputOffset);

            if (needed <= 0 || _renderQueued < needed)
            {
                resampler.ResampleOut(_renderFloat, 0, 0, 0, 1);
                return;
            }

            Array.Copy(_renderQueue, 0, input, inputOffset, needed);

            var remaining = _renderQueued - needed;
            if (remaining > 0) Array.Copy(_renderQueue, needed, _renderQueue, 0, remaining);
            _renderQueued = Math.Max(0, remaining);

            var produced = resampler.ResampleOut(_renderFloat, 0, needed, _renderSamplesPerFrame, 1);
            if (produced < _renderSamplesPerFrame) return;

            // 3) da mono in virgola mobile al formato del dispositivo. Il canale si
            //    replica su tutte le uscite: la telefonia e' monofonica, e mandare
            //    la voce su un solo altoparlante di una cuffia stereo e' un difetto
            //    di accessibilita' prima che di fedelta'.
            var index = 0;

            for (var i = 0; i < _renderSamplesPerFrame; i++)
            {
                var value = Math.Clamp(_renderFloat[i], -1f, 1f);

                for (var channel = 0; channel < format.Channels; channel++)
                {
                    if (isFloat)
                    {
                        BitConverter.TryWriteBytes(_renderBytes.AsSpan(index), value);
                    }
                    else
                    {
                        BitConverter.TryWriteBytes(_renderBytes.AsSpan(index), ToPcm16(value));
                    }

                    index += bytesPerSample;
                }
            }

            buffer.AddSamples(_renderBytes, 0, index);
        }
    }

    // -----------------------------------------------------------------------
    // Registrazione della conversazione, e avvisi all'interlocutore
    // -----------------------------------------------------------------------

    private readonly Lock _callRecordingGate = new();

    private CallRecorder? _callRecorder;

    /// <summary>
    /// Campioni di avviso ancora da far sentire all'interlocutore, alla frequenza del
    /// codec di trasmissione.
    /// </summary>
    private short[]? _remoteAnnouncement;
    private int _remoteAnnouncementPosition;

    /// <summary><c>true</c> se la conversazione è in registrazione.</summary>
    public bool IsRecordingCall
    {
        get { lock (_callRecordingGate) return _callRecorder is not null; }
    }

    /// <summary>Durata della registrazione in corso.</summary>
    public TimeSpan CallRecordingDuration
    {
        get { lock (_callRecordingGate) return _callRecorder?.Duration ?? TimeSpan.Zero; }
    }

    /// <summary>
    /// Avvia la registrazione della conversazione, nelle due direzioni.
    /// </summary>
    /// <param name="mp3Path">File MP3 da produrre alla chiusura.</param>
    /// <returns><c>true</c> se la registrazione è partita.</returns>
    /// <remarks>
    /// La frequenza del file è quella del codec di trasmissione. È noto in questo
    /// momento perché la registrazione si avvia a conversazione già aperta, quindi a
    /// negoziazione conclusa: è la differenza con la registrazione diagnostica, che si
    /// predispone prima di comporre il numero e deve rinviare la creazione del file.
    /// </remarks>
    public bool StartCallRecording(string mp3Path)
    {
        lock (_callRecordingGate)
        {
            if (_callRecorder is not null) return false;

            try
            {
                _callRecorder = new CallRecorder(mp3Path, _sourceFormat.ClockRate, _log);

                _log.LogInformation(
                    "Registrazione della conversazione avviata in {Path} a {Rate} Hz",
                    mp3Path, _sourceFormat.ClockRate);

                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Avvio della registrazione della conversazione non riuscito");
                _callRecorder = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Chiude la registrazione e ne avvia la conversione in MP3.
    /// </summary>
    /// <returns>
    /// Durata registrata e attesa sulla conversione, oppure <c>null</c> se non era in
    /// corso alcuna registrazione.
    /// </returns>
    /// <remarks>
    /// La durata torna subito, la conversione prosegue in sottofondo: è quella durata
    /// che va detta all'interlocutore nell'istante in cui la registrazione finisce, e
    /// non si può farlo aspettare la codifica.
    /// </remarks>
    public (TimeSpan Duration, Task<string> File)? StopCallRecording()
    {
        CallRecorder recorder;

        lock (_callRecordingGate)
        {
            if (_callRecorder is null) return null;

            recorder = _callRecorder;
            _callRecorder = null;
        }

        var durata = recorder.Stop();
        _log.LogInformation("Registrazione della conversazione chiusa, durata {Durata}", durata);

        return (durata, recorder.ConvertToMp3Async());
    }

    /// <summary>
    /// Fa sentire all'interlocutore un avviso vocale, sovrapponendolo alla linea.
    /// </summary>
    /// <param name="pcm">Campioni a 16 bit dell'avviso.</param>
    /// <param name="sampleRate">Frequenza di campionamento dei campioni forniti.</param>
    /// <remarks>
    /// <para>
    /// I campioni vengono riportati alla frequenza del codec di trasmissione con lo
    /// stesso ricampionatore a contabilità esplicita usato dalla cattura, e per la
    /// stessa ragione: qui la quantità di campioni deve tornare, perché un avviso che
    /// perde pezzi è un avviso che l'interlocutore non capisce.
    /// </para>
    /// <para>
    /// Un avviso già in corso viene sostituito. È il comportamento giusto: se
    /// l'operatore avvia e ferma la registrazione in due secondi, l'ultimo avviso è
    /// quello vero, e accodarli farebbe sentire «avvio della registrazione» dopo che
    /// la registrazione è già finita.
    /// </para>
    /// </remarks>
    public void PlayToRemote(short[] pcm, int sampleRate)
    {
        if (pcm.Length == 0 || sampleRate <= 0) return;

        try
        {
            var target = _sourceFormat.ClockRate;
            var samples = sampleRate == target ? pcm : ResampleForRemote(pcm, sampleRate, target);

            lock (_gate)
            {
                _remoteAnnouncement = samples;
                _remoteAnnouncementPosition = 0;
            }

            _log.LogInformation(
                "Avviso vocale verso l'interlocutore: {Ms} ms a {Rate} Hz",
                samples.Length * 1000 / target, target);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Preparazione dell'avviso vocale verso l'interlocutore non riuscita");
        }
    }

    /// <summary>Riporta l'avviso alla frequenza del codec di trasmissione.</summary>
    private static short[] ResampleForRemote(short[] pcm, int from, int to)
    {
        var resampler = new NAudio.Dsp.WdlResampler();
        resampler.SetMode(true, 2, true, 128);
        resampler.SetFilterParms();
        resampler.SetFeedMode(true);
        resampler.SetRates(from, to);

        var wanted = (int)((long)pcm.Length * to / from) + 64;
        var output = new float[wanted];

        var needed = resampler.ResamplePrepare(pcm.Length, 1, out var input, out var inputOffset);
        var supplied = Math.Min(needed, pcm.Length);

        for (var i = 0; i < supplied; i++) input[inputOffset + i] = pcm[i] / 32768f;

        var produced = resampler.ResampleOut(output, 0, supplied, wanted, 1);
        var result = new short[produced];

        for (var i = 0; i < produced; i++)
        {
            result[i] = ToPcm16(output[i]);
        }

        return result;
    }

    /// <summary>
    /// Sovrappone al pacchetto in partenza la parte di avviso ancora da far sentire.
    /// </summary>
    /// <remarks>
    /// Si somma invece di sostituire: la voce dell'operatore non viene tagliata, e nel
    /// caso normale — l'operatore che tace mentre l'avviso scorre — il risultato è lo
    /// stesso. Va chiamato sul thread della cattura.
    /// </remarks>
    private void MixRemoteAnnouncement(short[] frame, int count)
    {
        lock (_gate)
        {
            var announcement = _remoteAnnouncement;
            if (announcement is null) return;

            var available = Math.Min(count, announcement.Length - _remoteAnnouncementPosition);

            for (var i = 0; i < available; i++)
            {
                var sum = frame[i] + announcement[_remoteAnnouncementPosition + i];
                frame[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            }

            _remoteAnnouncementPosition += available;

            if (_remoteAnnouncementPosition < announcement.Length) return;

            _remoteAnnouncement = null;
            _remoteAnnouncementPosition = 0;
        }
    }

    // -----------------------------------------------------------------------
    // Registrazione dell'audio ricevuto, per la diagnosi
    // -----------------------------------------------------------------------

    private readonly Lock _recorderGate = new();

    private WaveFileWriter? _recorder;
    private string? _recordingPath;
    private int _recordingRate;

    /// <summary>Frequenza di campionamento del codec di ricezione negoziato.</summary>
    /// <remarks>
    /// Serve a chi legge la registrazione per stimarne la durata: il numero di byte
    /// da solo non basta, perché G.722 lavora a 16 kHz e produce il doppio dei byte
    /// degli altri tre codec a parità di secondi.
    /// </remarks>
    public int NegotiatedClockRate => _sinkFormat.ClockRate;

    /// <summary>
    /// Registra su file l'audio decodificato, prima di qualunque ricampionamento.
    /// </summary>
    /// <param name="path">Percorso del file WAV da creare.</param>
    /// <remarks>
    /// <para>
    /// Serve a separare due domande che a orecchio si confondono: <b>l'audio arriva
    /// già rovinato dalla rete o dal centralino</b>, oppure lo rovina la catena di
    /// riproduzione locale?
    /// </para>
    /// <para>
    /// Il file contiene esattamente i campioni usciti dal decodificatore. Se
    /// riascoltandolo la voce è pulita, il difetto è a valle — buffer,
    /// ricampionatore, dispositivo. Se è già disturbato, il problema è a monte e
    /// nessuna regolazione del buffer lo risolverà.
    /// </para>
    /// <para>
    /// <b>Il file non viene creato qui, ma al primo pacchetto ricevuto</b>, e la
    /// ragione è che senza questo rinvio la registrazione mente. La chiamata si
    /// registra prima di comporre il numero, mentre il codec si conosce solo con la
    /// risposta SDP che arriva quando l'interlocutore solleva: scrivere subito
    /// l'intestazione significa dichiarare la frequenza <i>predefinita</i> e
    /// riempire il file con campioni a quella <i>negoziata</i>. Con G.722, che
    /// campiona a 16 kHz contro gli 8 kHz degli altri, il file risultante si
    /// riascolta a metà velocità: voce lenta e cavernosa, che si scambia per audio
    /// disturbato e manda la diagnosi verso la rete quando la rete non c'entra.
    /// </para>
    /// </remarks>
    public void StartRecording(string path)
    {
        lock (_recorderGate)
        {
            CloseRecorder();
            _recordingPath = path;
        }

        _log.LogInformation(
            "Registrazione dell'audio ricevuto predisposta in {Path}: il file viene creato "
            + "al primo pacchetto, alla frequenza del codec effettivamente negoziato",
            path);
    }

    /// <summary>Chiude il file di registrazione, se aperto.</summary>
    public void StopRecording()
    {
        lock (_recorderGate)
        {
            _recordingPath = null;
            CloseRecorder();
        }
    }

    /// <summary>
    /// Scrive nella registrazione, creando il file alla prima scrittura utile.
    /// </summary>
    /// <remarks>
    /// Gira sul thread che riceve i pacchetti RTP, mentre avvio e chiusura arrivano
    /// dal chiamante: il blocco serve a evitare che una scrittura finisca su un file
    /// appena chiuso. La chiamata diagnostica chiude infatti la registrazione mentre
    /// il flusso è ancora attivo.
    /// </remarks>
    private void WriteToRecording(byte[] bytes, int count)
    {
        lock (_recorderGate)
        {
            if (_recordingPath is null) return;

            var rate = _sinkFormat.ClockRate;

            // Il codec puo' cambiare a chiamata in corso con un re-INVITE. Un file
            // WAV ha una sola frequenza dichiarata, quindi si riparte: meglio
            // perdere la parte gia' scritta che consegnare un file che non
            // corrisponde ai propri campioni per la seconda meta'.
            if (_recorder is null || _recordingRate != rate)
            {
                var path = _recordingPath;
                CloseRecorder();

                try
                {
                    _recorder = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
                    _recordingRate = rate;

                    _log.LogInformation(
                        "Registrazione dell'audio ricevuto in {Path} a {Rate} Hz", path, rate);
                }
                catch (Exception ex)
                {
                    // Si smette di provarci: un percorso non scrivibile non migliora
                    // ripetendo il tentativo a ogni pacchetto, e il registro si
                    // riempirebbe di errori identici venti volte al secondo.
                    _log.LogError(ex, "Avvio della registrazione diagnostica non riuscito");
                    _recordingPath = null;
                    return;
                }
            }

            try
            {
                _recorder.Write(bytes, 0, count);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Scrittura della registrazione diagnostica non riuscita");
            }
        }
    }

    /// <summary>Chiude il file aperto. Va chiamata con <c>_recorderGate</c> acquisito.</summary>
    private void CloseRecorder()
    {
        var recorder = _recorder;
        _recorder = null;
        _recordingRate = 0;

        try
        {
            recorder?.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Chiusura della registrazione non riuscita");
        }
    }

    // -----------------------------------------------------------------------
    // Pausa, ripresa, chiusura
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public Task PauseAudio()
    {
        // La pausa della sorgente e' il silenziamento del microfono. Non ferma il
        // flusso RTP: continua a partire silenzio, perche' interromperlo farebbe
        // scattare i timeout di inattivita' del centralino e la chiamata cadrebbe
        // mentre l'operatore e' semplicemente in muto.
        _sourcePaused = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeAudio()
    {
        _sourcePaused = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PauseAudioSink()
    {
        _sinkPaused = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeAudioSink()
    {
        _sinkPaused = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAudioSink() => StartAudio();

    /// <inheritdoc />
    public Task CloseAudio()
    {
        StopEverything();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CloseAudioSink()
    {
        StopEverything();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample is not null;

    /// <inheritdoc />
    public bool IsAudioSourcePaused() => _sourcePaused;

    /// <inheritdoc />
    public void ExternalAudioSourceRawSample(
        AudioSamplingRatesEnum samplingRate,
        uint durationMilliseconds,
        short[] sample)
    {
        // Punto di iniezione per l'audio generato dall'applicazione: annunci
        // registrati, frasi di cortesia, musica di attesa locale. Non usato per la
        // conversazione, che passa dal microfono.
        if (_disposed || sample.Length == 0) return;

        try
        {
            var encoded = _encoder.EncodeAudio(sample, _sourceFormat);
            if (encoded.Length == 0) return;

            var durationRtpUnits = (uint)(_sourceFormat.RtpClockRate * durationMilliseconds / 1000);
            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Iniezione di audio esterno non riuscita");
        }
    }

    // -----------------------------------------------------------------------
    // Particolarità del G.729
    // -----------------------------------------------------------------------

    /// <summary>Dimensione in byte di un pacchetto G.729 da 10 millisecondi.</summary>
    private const int G729FrameBytes = 10;

    /// <summary>
    /// Verifica che un pacchetto in arrivo sia decodificabile con il codec
    /// negoziato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il controllo esiste per il G.729, che si comporta diversamente dagli altri
    /// tre. G.711 e G.722 hanno un byte per campione: qualunque lunghezza è
    /// decodificabile. Il G.729 invece lavora a blocchi di <b>10 millisecondi</b>,
    /// cioè 10 byte, e un pacchetto di lunghezza diversa non è un frame vocale.
    /// </para>
    /// <para>
    /// Il caso che conta è l'Annex B, il rilevamento di attività vocale: durante i
    /// silenzi il centralino smette di inviare frame vocali e manda pacchetti SID
    /// da 2 byte, che descrivono il rumore di fondo da ricostruire localmente.
    /// Passarli al decodificatore come se fossero voce produce campioni casuali,
    /// cioè <b>rumore</b>, proprio nei momenti in cui dovrebbe esserci silenzio.
    /// </para>
    /// </remarks>
    private static bool IsDecodablePayload(byte[] payload, AudioFormat format)
    {
        if (!IsG729(format)) return true;

        // Solo i multipli esatti della dimensione di frame sono voce. I pacchetti
        // SID da 2 byte e gli altri formati fuori misura vengono resi come silenzio.
        return payload.Length >= G729FrameBytes && payload.Length % G729FrameBytes == 0;
    }

    private static bool IsG729(AudioFormat format)
        => format.FormatName.Contains("G729", StringComparison.OrdinalIgnoreCase);

    /// <summary>Inserisce nel buffer di riproduzione un intervallo di silenzio.</summary>
    /// <param name="milliseconds">Durata del silenzio.</param>
    private void InsertSilence(int milliseconds)
    {
        var buffer = _renderBuffer;
        if (buffer is null) return;

        var bytes = buffer.WaveFormat.AverageBytesPerSecond * milliseconds / 1000;
        if (bytes <= 0) return;

        buffer.AddSamples(new byte[bytes], 0, bytes);
    }

    /// <summary>
    /// Allinea la dimensione del pacchetto di cattura alla granularità del codec.
    /// </summary>
    /// <remarks>
    /// Il G.729 codifica blocchi da 10 ms: un pacchetto da 20 ms sono due blocchi,
    /// e la dimensione deve essere un multiplo esatto perché il codificatore non
    /// scarti la coda. Per gli altri codec la granularità è il singolo campione e
    /// il calcolo non cambia nulla.
    /// </remarks>
    private static int AlignSampleCount(int samples, AudioFormat format)
    {
        if (!IsG729(format)) return samples;

        var samplesPerG729Frame = format.ClockRate / 100;
        if (samplesPerG729Frame <= 0) return samples;

        return samples / samplesPerG729Frame * samplesPerG729Frame;
    }

    // -----------------------------------------------------------------------
    // Ausiliari
    // -----------------------------------------------------------------------

    /// <summary>
    /// Risolve il dispositivo assegnato a una via, ricadendo sul predefinito di
    /// sistema.
    /// </summary>
    private MMDevice? ResolveDevice(AudioRoute route, DataFlow flow)
    {
        var enumerator = new MMDeviceEnumerator();

        try
        {
            var assigned = _engine.GetAssignedDevice(route);

            if (assigned is not null)
            {
                try
                {
                    return enumerator.GetDevice(assigned.Id);
                }
                catch (Exception)
                {
                    // Dispositivo memorizzato ma non piu' presente: si ricade sul
                    // predefinito invece di lasciare la chiamata muta.
                    _log.LogWarning("Dispositivo {Name} non piu' disponibile", assigned.Name);
                }
            }

            return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Nessun dispositivo audio disponibile per la via {Route}", route);
            return null;
        }
    }

    private void RestartCapture()
    {
        StopCapture();
        StartCapture();
    }

    private void RestartRender()
    {
        StopRender();
        StartRender();
    }

    private void StopCapture()
    {
        _captureQueued = 0;

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnCaptureDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                _capture.StopRecording();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Arresto della cattura non riuscito");
            }

            _capture.Dispose();
            _capture = null;
        }

        _captureResampler = null;
        _captureFormat = null;
    }

    private void StopRender()
    {
        // Si trattengono i contatori PRIMA di smontare la catena: e' l'unico momento
        // in cui esistono ancora.
        _lastUnderruns = _jitterBuffer?.Underruns ?? _lastUnderruns;
        _lastSilenceMilliseconds = _jitterBuffer?.SilenceMilliseconds ?? _lastSilenceMilliseconds;
        _lastPaddedBytes = _renderGuard?.PaddedBytes ?? _lastPaddedBytes;

        if (_render is not null)
        {
            try
            {
                _render.Stop();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Arresto della riproduzione non riuscito");
            }

            _render.Dispose();
            _render = null;
        }

        _renderGuard = null;
        _jitterBuffer = null;
        _renderBuffer = null;
        _renderResampler = null;
        _renderDeviceFormat = null;
        _renderQueued = 0;
    }

    private void StopEverything()
    {
        if (!_started) return;

        _started = false;

        // La registrazione della conversazione si chiude per prima e si converte: una
        // chiamata che finisce mentre si registra non deve lasciare il file di lavoro a
        // meta'. Non si attende la conversione, che prosegue da se'.
        _ = StopCallRecording();

        StopRecording();
        StopCapture();
        StopRender();

        _log.LogInformation(
            "Percorso audio chiuso. Inviati {Sent}, ricevuti {Received}, scartati {Discarded}, "
            + "svuotamenti buffer {Underruns}, silenzio inserito {Silence} ms",
            RtpFramesSent, RtpFramesReceived, RtpFramesDiscarded,
            RenderUnderruns, RenderSilenceMilliseconds);
    }

    private void RaiseSourceError(string message)
    {
        _log.LogWarning("Errore sorgente audio: {Message}", message);
        OnAudioSourceError?.Invoke(message);
    }

    private void RaiseSinkError(string message)
    {
        _log.LogWarning("Errore riproduzione audio: {Message}", message);
        OnAudioSinkError?.Invoke(message);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        // Prima di tutto il resto: una gamba di conferenza smaltita mentre il ponte la
        // crede viva continuerebbe a ricevere campioni da accodare su strutture in via
        // di smontaggio.
        EndConference();

        StopEverything();
    }
}
