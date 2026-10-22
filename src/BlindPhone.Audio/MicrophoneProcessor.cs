using System.Numerics;
using BlindPhone.Core.Domain;
using NAudio.Effects;
using NAudio.Wave;

namespace BlindPhone.Audio;

/// <summary>
/// Elabora la voce dell'operatore prima che venga codificata: riduzione del rumore
/// ambientale e controllo automatico del livello.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non è una libreria scritta qui.</b> Gli effetti sono quelli di
/// <c>NAudio.Effects</c>, già nel pacchetto NAudio che il prodotto usa per WASAPI: qui
/// si decide solo la catena, i valori e che cosa fare quando qualcosa va storto.
/// SIPSorcery non offre niente del genere — la sola «AGC» che compare nel suo codice
/// appartiene al decodificatore G.729 e non agisce sul microfono.
/// </para>
/// <para>
/// <b>L'ordine è quello che conta.</b> Prima la riduzione del rumore, poi il controllo
/// del livello, poi il limitatore. Con l'ordine inverso il controllo del livello
/// alzerebbe il rumore insieme alla voce e la riduzione dovrebbe poi toglierlo da un
/// segnale più forte; il limitatore sta in fondo perché è la rete di sicurezza contro
/// la saturazione che il guadagno può produrre su un attacco improvviso.
/// </para>
/// <para>
/// <b>Gira sul thread della scheda audio</b>, ogni venti millisecondi. Per questo non
/// alloca, non blocca e non lascia propagare un'eccezione: un difetto qui non può
/// interrompere la trasmissione, e se l'elaborazione produce valori non validi si
/// rinuncia a lei e si lascia passare la voce com'è. Vedi <see cref="Fault"/>.
/// </para>
/// <para>
/// Non è thread-safe, e non serve esserlo: chi la usa la chiama sempre dallo stesso
/// thread.
/// </para>
/// </remarks>
internal sealed class MicrophoneProcessor
{
    // Valori misurati su segnali di prova a 8 e 16 kHz, a pacchetti da 20 ms.
    //
    // Riduzione del rumore: l'aggressività è l'unica manopola che cambia davvero il
    // compromesso. Misurato su rumore bianco, rosa e ronzio, con la voce pulita come
    // riferimento:
    //
    //                 rumore nelle pause    voce
    //   leggera  1,0       -8 ... -14 dB     -0,4 dB
    //   media    1,5      -11 ... -18 dB     -0,6 dB
    //   forte    2,5      -14 ... -20 dB     -1,0 dB (e il rapporto S/R peggiora)
    //
    // Oltre 2,5 il guadagno sulle pause e' piccolo e la voce comincia a deformarsi.
    private const float LightAggressiveness = 1.0f;
    private const float MediumAggressiveness = 1.5f;
    private const float StrongAggressiveness = 2.5f;
    private const float NoiseFloor = 0.08f;
    private const float StrongNoiseFloor = 0.05f;

    // Controllo del livello. -20 dBFS di valore efficace e' il livello a cui la
    // telefonia IP porta il parlato: lascia circa 15 dB sopra, che e' il margine fra il
    // valore efficace e i picchi di una voce vera. Il guadagno massimo non e' quello
    // predefinito della libreria (30 dB): oltre i 24 si inizia ad amplificare il
    // rumore proprio del microfono e non la voce.
    private const float AutoGainTargetDb = -20f;
    private const float AutoGainMaxGainDb = 24f;
    private const float AutoGainMinGainDb = -12f;

    // Limitatore. Ci vuole in coda al controllo del livello, e basta una finestra
    // d'anticipo breve: ogni millisecondo qui e' un millisecondo in piu' sul ritardo.
    private const float LimiterCeilingDb = -1f;
    private const float LimiterLookaheadMs = 2f;

    private readonly int _sampleRate;
    private readonly WaveFormat _format;

    private MicrophoneProcessing _applied = MicrophoneProcessing.None;
    private NoiseSuppressionEffect? _noise;
    private AutomaticGainControlEffect? _gain;
    private LimiterEffect? _limiter;
    private float[] _backup = [];

    /// <summary>Crea il processore per una frequenza di campionamento.</summary>
    /// <param name="sampleRate">Frequenza della voce da elaborare, in Hz: quella del codec.</param>
    public MicrophoneProcessor(int sampleRate)
    {
        _sampleRate = sampleRate;
        _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    }

    /// <summary>Se almeno una funzione è attiva e funzionante.</summary>
    public bool IsActive => _applied.IsActive && Fault is null;

    /// <summary>
    /// Il motivo per cui l'elaborazione è stata esclusa, oppure <c>null</c> se funziona.
    /// </summary>
    /// <remarks>
    /// Resta valorizzato fino al prossimo cambio di configurazione: riprovare a ogni
    /// pacchetto cinquanta volte al secondo, con lo stesso risultato, riempirebbe il
    /// registro e non cambierebbe niente.
    /// </remarks>
    public string? Fault { get; private set; }

    /// <summary>Ritardo che l'elaborazione aggiunge alla voce, in millisecondi.</summary>
    public double LatencyMilliseconds
        => 1000.0 * ((_noise?.LatencySamples ?? 0) + (_limiter?.LatencySamples ?? 0)) / _sampleRate;

    /// <summary>
    /// Dimensione del frame di analisi della riduzione del rumore, per una frequenza.
    /// </summary>
    /// <param name="sampleRate">Frequenza di campionamento, in Hz.</param>
    /// <remarks>
    /// <para>
    /// Circa sedici millisecondi, e sempre una potenza di due, come la libreria
    /// pretende: 128 campioni a 8 kHz, 256 a 16 kHz. È anche il ritardo che la
    /// riduzione del rumore aggiunge alla voce.
    /// </para>
    /// <para>
    /// Il valore predefinito della libreria è 512, cioè 64 ms a 8 kHz: troppo per un
    /// percorso di trasmissione che ha già venti millisecondi di pacchetto e il buffer
    /// di chi ascolta. E misurato a parità di impostazioni il frame da 128 a 8 kHz è
    /// anche <i>migliore</i> di quello da 256: più veloce a reagire fra una parola e
    /// l'altra.
    /// </para>
    /// </remarks>
    public static int NoiseFrameSize(int sampleRate)
        => Math.Max(64, (int)BitOperations.RoundUpToPowerOf2((uint)(sampleRate * 16 / 1000)));

    /// <summary>
    /// Adegua la catena alla configurazione richiesta.
    /// </summary>
    /// <param name="requested">Elaborazione desiderata.</param>
    /// <remarks>
    /// Si può chiamare a ogni pacchetto: se la configurazione non è cambiata non fa
    /// niente. Quando cambia, ricostruisce <b>solo la parte interessata</b>: accendere
    /// la riduzione del rumore non azzera il guadagno già raggiunto dal controllo del
    /// livello, che altrimenti ripartirebbe da zero nel mezzo di una frase.
    /// </remarks>
    public void Apply(MicrophoneProcessing requested)
    {
        var wanted = requested.Sanitized();
        if (wanted == _applied) return;

        var previous = _applied;
        _applied = wanted;

        // Un cambio di configurazione e' un nuovo tentativo.
        Fault = null;

        try
        {
            if (wanted.Level != previous.Level)
            {
                _noise = wanted.Level == NoiseSuppressionLevel.Off ? null : BuildNoise(wanted.Level);
            }

            if (wanted.AutoGain != previous.AutoGain)
            {
                _gain = wanted.AutoGain ? BuildGain() : null;
                _limiter = wanted.AutoGain ? BuildLimiter() : null;
            }
        }
        catch (Exception ex)
        {
            Exclude($"costruzione non riuscita: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Elabora un pacchetto, sul posto.
    /// </summary>
    /// <param name="frame">Campioni mono in virgola mobile, fra -1 e 1.</param>
    /// <remarks>
    /// Se l'elaborazione solleva un'eccezione o produce valori non finiti, il pacchetto
    /// torna com'era e da lì in poi si trasmette la voce non elaborata: un pacchetto di
    /// rumore o di silenzio al posto della voce è un difetto che l'interlocutore sente
    /// e che l'operatore non può sapere di avere.
    /// </remarks>
    public void Process(Span<float> frame)
    {
        if (!IsActive || frame.IsEmpty) return;

        if (_backup.Length < frame.Length) _backup = new float[frame.Length];
        frame.CopyTo(_backup);

        try
        {
            _noise?.Process(frame);
            _gain?.Process(frame);
            _limiter?.Process(frame);

            foreach (var sample in frame)
            {
                if (float.IsFinite(sample)) continue;

                _backup.AsSpan(0, frame.Length).CopyTo(frame);
                Exclude("l'elaborazione ha prodotto valori non validi");
                return;
            }
        }
        catch (Exception ex)
        {
            _backup.AsSpan(0, frame.Length).CopyTo(frame);
            Exclude($"errore durante l'elaborazione: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Descrizione della configurazione attiva, per il rapporto diagnostico.</summary>
    public string Describe()
    {
        if (Fault is not null) return $"esclusa ({Fault})";
        if (!_applied.IsActive) return "nessuna";

        var parts = new List<string>(2);

        if (_applied.AutoGain) parts.Add("controllo del livello");

        parts.Add(_applied.Level switch
        {
            NoiseSuppressionLevel.Light => "riduzione del rumore leggera",
            NoiseSuppressionLevel.Medium => "riduzione del rumore media",
            NoiseSuppressionLevel.Strong => "riduzione del rumore forte",
            _ => string.Empty,
        });

        var text = string.Join(", ", parts.Where(p => p.Length > 0));
        return LatencyMilliseconds > 0 ? $"{text}, ritardo aggiunto {LatencyMilliseconds:0} ms" : text;
    }

    private void Exclude(string reason)
    {
        Fault = reason;

        // Gli stati interni di una catena che ha prodotto valori non validi non sono
        // piu' affidabili: si buttano, e un eventuale nuovo tentativo parte da zero.
        _noise = null;
        _gain = null;
        _limiter = null;

        // Lo stato "applicato" torna vuoto, cosi' una nuova richiesta uguale alla
        // precedente ricostruisce la catena invece di essere scambiata per invariata.
        _applied = MicrophoneProcessing.None;
    }

    private NoiseSuppressionEffect BuildNoise(NoiseSuppressionLevel level)
    {
        var (aggressiveness, floor) = level switch
        {
            NoiseSuppressionLevel.Light => (LightAggressiveness, NoiseFloor),
            NoiseSuppressionLevel.Medium => (MediumAggressiveness, NoiseFloor),
            _ => (StrongAggressiveness, StrongNoiseFloor),
        };

        var effect = new NoiseSuppressionEffect
        {
            Aggressiveness = aggressiveness,
            SpectralFloor = floor,
            FrameSize = NoiseFrameSize(_sampleRate),
        };

        effect.Configure(_format);
        return effect;
    }

    private AutomaticGainControlEffect BuildGain()
    {
        var effect = new AutomaticGainControlEffect
        {
            TargetDb = AutoGainTargetDb,
            MaxGainDb = AutoGainMaxGainDb,
            MinGainDb = AutoGainMinGainDb,

            // Con la rilevazione vocale il guadagno resta fermo nelle pause: senza,
            // in un secondo di silenzio il controllo crescerebbe fino al massimo e
            // alla prima parola la voce arriverebbe schiacciata dall'attacco.
            UseVoiceDetection = true,
        };

        effect.Configure(_format);
        return effect;
    }

    private LimiterEffect BuildLimiter()
    {
        var effect = new LimiterEffect
        {
            CeilingDb = LimiterCeilingDb,
            LookaheadMs = LimiterLookaheadMs,

            // La rilevazione del picco «vero» sovracampiona di quattro volte: serve a
            // chi consegna a un convertitore ad alta qualita', non a un codec a 8 kHz
            // che comprime la dinamica e che riceve campioni arrotondati a 16 bit.
            TruePeak = false,
        };

        effect.Configure(_format);
        return effect;
    }
}
