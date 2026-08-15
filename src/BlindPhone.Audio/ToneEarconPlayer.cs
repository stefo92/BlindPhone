using System.Collections.Concurrent;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

namespace BlindPhone.Audio;

/// <summary>
/// Riproduttore di earcon a sintesi, senza file audio a corredo.
/// </summary>
/// <remarks>
/// <para>
/// Gli earcon vengono generati come sequenze di toni invece di essere caricati da
/// file WAV. Tre vantaggi pratici: nessun file da installare e da mantenere
/// coerente fra le postazioni, altezza e durata regolabili dall'operatore senza
/// dover produrre nuovi suoni, e nessun ritardo di caricamento su un segnale che
/// deve arrivare entro poche decine di millisecondi.
/// </para>
/// <para>
/// <b>Il posizionamento stereo è la parte importante.</b> Gli eventi di coda
/// suonano a sinistra, quelli della chiamata al centro, quelli di controllo linea a
/// destra. In pochi giorni l'operatore smette di doverli interpretare e li riconosce
/// dalla posizione, come si riconosce da che parte arriva un rumore in una stanza.
/// È il motivo per cui un earcon comunica più in fretta di una frase e, soprattutto,
/// non impedisce di continuare a parlare al telefono.
/// </para>
/// <para>
/// L'operatore può sostituire qualunque earcon con un file proprio: in quel caso
/// <see cref="CustomSounds"/> ha la precedenza sulla sintesi.
/// </para>
/// </remarks>
public sealed class ToneEarconPlayer : IEarconPlayer, IAsyncDisposable
{
    private const int SampleRate = 44100;

    private readonly NAudioEngine _audio;
    private readonly ILogger<ToneEarconPlayer> _log;
    private readonly ConcurrentDictionary<EarconKind, CancellationTokenSource> _repeating = new();
    private readonly IClock _clock;

    /// <summary>Crea il riproduttore.</summary>
    /// <param name="audio">Motore audio su cui instradare i suoni.</param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    public ToneEarconPlayer(NAudioEngine audio, IClock? clock = null, ILogger<ToneEarconPlayer>? logger = null)
    {
        _audio = audio;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<ToneEarconPlayer>.Instance;
    }

    /// <summary>Suoni personalizzati che sostituiscono la sintesi, per tipo di earcon.</summary>
    public IDictionary<EarconKind, string> CustomSounds { get; } = new Dictionary<EarconKind, string>();

    /// <summary>Volume degli earcon, da 0 a 1.</summary>
    public float Volume { get; set; } = 0.6f;

    /// <summary>Se applicare il posizionamento stereo per famiglia di evento.</summary>
    public bool UseSpatialPanning { get; set; } = true;

    /// <summary>Insieme degli earcon disattivati dall'operatore.</summary>
    public HashSet<EarconKind> Disabled { get; } = [];

    /// <inheritdoc />
    public void Play(EarconKind kind)
    {
        if (kind == EarconKind.None || Disabled.Contains(kind)) return;

        // Deliberatamente non atteso: un earcon che blocca il chiamante rallenta la
        // risposta a un tasto, e sono proprio i millisecondi che l'operatore
        // percepisce come "il comando non ha funzionato".
        _ = PlayInternalAsync(kind, CancellationToken.None);
    }

    private async Task PlayInternalAsync(EarconKind kind, CancellationToken cancellationToken)
    {
        try
        {
            var route = RouteFor(kind);
            var pan = UseSpatialPanning ? kind.Category().SuggestedPan() : 0f;

            // UNA RIGA PER OGNI SEGNALE SUONATO.
            //
            // Serve a rispondere a una domanda che il registro non sapeva affrontare:
            // «questo suono non l'ho sentito» e «questo suono non e' stato prodotto»
            // sono due guasti diversi — uno sta nell'audio, l'altro nella logica — e
            // senza questa riga si distinguono solo per tentativi.
            _log.LogDebug("Earcon {Kind} riprodotto sulla via {Route}", kind, route);

            if (CustomSounds.TryGetValue(kind, out var path) && File.Exists(path))
            {
                await _audio.PlayAsync(route, path, pan, cancellationToken).ConfigureAwait(false);
                return;
            }

            var provider = BuildTone(kind, pan);
            await _audio.PlaySampleAsync(route, provider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Riproduzione dell'earcon {Kind} non riuscita", kind);
        }
    }

    /// <summary>
    /// Sceglie la via audio su cui instradare un earcon.
    /// </summary>
    /// <remarks>
    /// La regola vive nel core, dove si può verificare: vedi
    /// <see cref="EarconRouting"/>. Qui resta il solo atto di riprodurre.
    /// </remarks>
    private static AudioRoute RouteFor(EarconKind kind) => EarconRouting.RouteFor(kind);

    /// <summary>
    /// Costruisce la forma d'onda di un earcon.
    /// </summary>
    /// <remarks>
    /// Le scelte melodiche seguono convenzioni percettive consolidate: le sequenze
    /// ascendenti significano "qualcosa è arrivato o è riuscito", le discendenti
    /// "qualcosa è finito", gli intervalli dissonanti "qualcosa non ha funzionato".
    /// Un operatore le interpreta correttamente al primo ascolto anche senza averle
    /// mai sentite prima.
    /// </remarks>
    private ISampleProvider BuildTone(EarconKind kind, float pan) => kind switch
    {
        EarconKind.QueueCallArrived => Sequence(pan, (880, 70), (1175, 90)),
        EarconKind.QueueEmptied => Sequence(pan, (660, 120)),
        EarconKind.QueueWaitThresholdExceeded => Sequence(pan, (990, 60), (0, 40), (990, 60)),

        EarconKind.IncomingCall => Sequence(pan, (784, 100), (1046, 140)),

        // Tono di libero italiano: 425 Hz per un secondo, poi quattro di silenzio.
        // La pausa non sta qui ma nell'intervallo di StartRepeating, cosi' la
        // cadenza resta quella nazionale (1 acceso, 4 spento) e l'operatore
        // riconosce il tono senza doverlo imparare.
        EarconKind.RemoteRinging => Sequence(pan, (425, 1000)),

        // TONI DI OCCUPATO E DI CONGESTIONE, CON LE CADENZE ITALIANE.
        //
        // Sono gli unici earcon di questo prodotto che non sono stati inventati: li
        // conosce gia' l'orecchio di chiunque abbia usato un telefono, e la cadenza
        // e' l'informazione. Occupato: 425 Hz, mezzo secondo acceso e mezzo spento.
        // Congestione — il numero fuori servizio, il tono "rapido": 425 Hz, due
        // decimi acceso e due spenti.
        //
        // Vanno generati qui perche' la rete non li manda. Il centralino rifiuta la
        // chiamata con un codice di errore e Content-Length zero: non c'e' nessun
        // audio da riprodurre, e sulla cuffia resta il silenzio. Per chi non vede il
        // silenzio dopo una composizione e' indistinguibile da un guasto della
        // cuffia, del microfono o del programma.
        //
        // Tre cicli l'occupato, cinque la congestione: durano poco piu' di due
        // secondi entrambi, il tempo di essere riconosciuti senza costringere ad
        // aspettare che finiscano per fare altro.
        EarconKind.LineBusy => Sequence(
            pan,
            (425, 500), (0, 500), (425, 500), (0, 500), (425, 500)),

        EarconKind.LineUnavailable => Sequence(
            pan,
            (425, 200), (0, 200), (425, 200), (0, 200), (425, 200),
            (0, 200), (425, 200), (0, 200), (425, 200)),

        EarconKind.CallAnswered => Sequence(pan, (523, 60)),
        EarconKind.CallEnded => Sequence(pan, (392, 60), (0, 30), (392, 60)),

        EarconKind.CallHeld => Sequence(pan, (330, 110)),
        EarconKind.CallResumed => Sequence(pan, (660, 110)),
        EarconKind.HoldReminder => Sequence(pan, (494, 60), (0, 60), (494, 60)),

        // Conferenza: tre toni, uno per partecipante. Sale all'apertura e scende alla
        // chiusura, che e' la convenzione gia' in uso per "arrivato" e "finito"; le
        // tre note distinguono la conferenza dal trasferimento riuscito, che ne usa
        // tre anch'esso ma su un intervallo piu' stretto e senza la ripetizione della
        // nota centrale.
        EarconKind.ConferenceStarted => Sequence(pan, (440, 70), (554, 70), (659, 130)),
        EarconKind.ConferenceEnded => Sequence(pan, (659, 70), (554, 70), (440, 130)),

        EarconKind.TransferSucceeded => Sequence(pan, (523, 60), (659, 60), (784, 110)),
        EarconKind.TransferFailed => Sequence(pan, (415, 90), (311, 160)),

        EarconKind.CommandRejected => Sequence(pan, (220, 90)),

        EarconKind.RegistrationLost => Sequence(pan, (466, 130), (0, 60), (466, 130)),
        EarconKind.RegistrationRestored => Sequence(pan, (659, 70), (880, 110)),

        EarconKind.PbxLinkDegraded => Sequence(pan, (349, 100), (0, 50), (294, 130)),
        EarconKind.PbxLinkRestored => Sequence(pan, (587, 70), (784, 100)),

        _ => Sequence(pan, (523, 60)),
    };

    /// <summary>
    /// Compone una sequenza di toni. Frequenza zero produce una pausa.
    /// </summary>
    private ISampleProvider Sequence(float pan, params (int Frequency, int Milliseconds)[] steps)
    {
        var samples = new List<float>();

        foreach (var (frequency, milliseconds) in steps)
        {
            var count = SampleRate * milliseconds / 1000;

            for (var i = 0; i < count; i++)
            {
                if (frequency == 0)
                {
                    samples.Add(0f);
                    continue;
                }

                var t = (double)i / SampleRate;
                var value = Math.Sin(2 * Math.PI * frequency * t);

                // Inviluppo di attacco e rilascio: senza, il taglio netto della
                // forma d'onda produce un click udibile che rende il segnale
                // sgradevole quando si ripete decine di volte all'ora.
                var envelope = Envelope(i, count);
                samples.Add((float)(value * envelope * Volume));
            }
        }

        return new StereoToneProvider(samples, pan);
    }

    private static double Envelope(int index, int total)
    {
        const int FadeSamples = 220;

        if (index < FadeSamples) return (double)index / FadeSamples;
        if (index > total - FadeSamples) return Math.Max(0, (double)(total - index) / FadeSamples);
        return 1.0;
    }

    /// <inheritdoc />
    public void StartRepeating(EarconKind kind, TimeSpan interval, TimeSpan? initialDelay = null)
    {
        // Un earcon spento dall'operatore resta spento anche quando si ripete: la
        // scelta vale per il segnale, non per il modo in cui viene suonato.
        if (kind == EarconKind.None || Disabled.Contains(kind)) return;

        StopRepeating(kind);

        var cts = new CancellationTokenSource();
        if (!_repeating.TryAdd(kind, cts))
        {
            cts.Dispose();
            return;
        }

        // IL TOKEN SI PRENDE ADESSO, NON DENTRO IL CICLO.
        //
        // Chi ferma la ripetizione annulla e poi smaltisce la sorgente. Da quel
        // momento leggere "cts.Token" solleva ObjectDisposedException — non
        // OperationCanceledException — e siccome l'attivita' non e' attesa da nessuno
        // l'eccezione restava non osservata: finiva al finalizzatore, che la
        // ripresentava come errore non gestito e faceva scrivere un rapporto di errore
        // grave. Su una postazione se ne accumulava uno per ogni chiamata entrante.
        //
        // Una copia del token presa prima resta leggibile anche dopo lo smaltimento, e
        // arriva gia' annullata: il ciclo esce da se'.
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (initialDelay is { } attesa && attesa > TimeSpan.Zero)
                {
                    await Task.Delay(attesa, token).ConfigureAwait(false);
                }

                while (!token.IsCancellationRequested)
                {
                    await PlayInternalAsync(kind, token).ConfigureAwait(false);
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Interruzione richiesta: percorso normale.
            }
            catch (Exception ex)
            {
                // Rete di sicurezza: qualunque altro guasto qui dentro non deve
                // arrivare al finalizzatore. Un earcon che non suona e' un fastidio,
                // un rapporto di errore grave per ogni chiamata e' un prodotto che
                // sembra rotto.
                _log.LogDebug(ex, "Ripetizione dell'earcon {Kind} interrotta da un errore", kind);
            }
        }, CancellationToken.None);
    }

    /// <inheritdoc />
    public void StopRepeating(EarconKind kind)
    {
        if (!_repeating.TryRemove(kind, out var cts)) return;

        cts.Cancel();
        cts.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        foreach (var kind in _repeating.Keys.ToList()) StopRepeating(kind);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Espone una sequenza di campioni monofonici come flusso stereo posizionato.
    /// </summary>
    /// <remarks>
    /// <b>internal e non private</b>: e' il punto in cui un indice sbagliato non
    /// produce un errore ma uno schiocco, e serve poterlo collaudare da fuori.
    /// </remarks>
    internal sealed class StereoToneProvider(IReadOnlyList<float> samples, float pan) : ISampleProvider
    {
        private readonly float _leftGain = (float)Math.Sqrt((1 - Math.Clamp(pan, -1f, 1f)) / 2);
        private readonly float _rightGain = (float)Math.Sqrt((1 + Math.Clamp(pan, -1f, 1f)) / 2);
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public int Read(Span<float> buffer)
        {
            var written = 0;

            while (written < buffer.Length && _position < samples.Count)
            {
                var value = samples[_position++];
                buffer[written++] = value * _leftGain;

                if (written < buffer.Length) buffer[written++] = value * _rightGain;
            }

            // Zero significa "flusso finito" e ferma la riproduzione: e' corretto
            // solo quando non c'e' davvero piu' niente da suonare, e va detto prima
            // di riempire di silenzio, altrimenti l'earcon successivo non parte.
            if (written == 0) return 0;

            // Stesso motivo per cui il buffer di riproduzione della conversazione
            // usa ReadFully: se si restituisce meno di quanto richiesto, WASAPI
            // riproduce memoria non inizializzata nella coda del buffer. Sull'ultimo
            // pacchetto di un earcon questo produce uno schiocco secco, che ripetuto
            // decine di volte all'ora diventa fastidioso.
            buffer[written..].Clear();

            return buffer.Length;
        }
    }
}
