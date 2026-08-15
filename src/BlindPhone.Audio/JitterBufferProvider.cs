using NAudio.Wave;

namespace BlindPhone.Audio;

/// <summary>
/// Buffer di jitter con pre-riempimento, che restituisce sempre letture complete.
/// </summary>
/// <remarks>
/// <para>
/// Risolve due difetti distinti della catena di riproduzione, che insieme producono
/// il sintomo tipico: parlato comprensibile ma interrotto ciclicamente, con rumore
/// bianco di fondo.
/// </para>
/// <para>
/// <b>Primo: l'assenza di cuscinetto.</b> I pacchetti RTP arrivano ogni 20
/// millisecondi ma non a intervalli perfettamente regolari: la rete introduce
/// variazioni di ritardo, e il sistema operativo non garantisce quando le consegna
/// all'applicazione. Se la riproduzione parte a buffer vuoto, basta un pacchetto in
/// ritardo di pochi millisecondi perché la scheda audio non trovi campioni da
/// suonare. Il risultato è un'interruzione, che si ripete a ogni oscillazione della
/// rete. La soluzione è aspettare di aver accumulato un cuscinetto prima di
/// cominciare, e ricostruirlo ogni volta che si svuota.
/// </para>
/// <para>
/// <b>Secondo: le letture parziali.</b> WASAPI chiede un numero preciso di campioni
/// e copia nel proprio buffer solo quelli ricevuti, ma dichiara alla scheda audio il
/// conteggio pieno: la coda resta memoria non inizializzata e viene riprodotta come
/// rumore. Impostare <c>ReadFully</c> sul buffer sorgente non basta, perché fra
/// quello e WASAPI c'è il ricampionatore, che a sua volta può restituire letture
/// corte. Questo componente garantisce la lunghezza a valle di tutto.
/// </para>
/// <para>
/// <b>Il compromesso da capire per regolare il valore.</b> Un cuscinetto più grande
/// assorbe più variazioni di rete ma aggiunge altrettanto ritardo alla voce. Su un
/// posto operatore il ritardo si paga in sovrapposizioni con l'interlocutore: oltre
/// i 150 millisecondi si comincia a interrompersi a vicenda. Su rete locale
/// bastano 40-60 millisecondi; su collegamenti geografici o Wi-Fi disturbato
/// servono 100-200.
/// </para>
/// </remarks>
public sealed class JitterBufferProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _source;
    private readonly int _targetBytes;
    private readonly object _gate = new();

    private bool _primed;
    private long _underruns;
    private long _silenceBytesInserted;

    /// <summary>Crea il buffer di jitter.</summary>
    /// <param name="source">Buffer alimentato dai pacchetti in arrivo.</param>
    /// <param name="targetMilliseconds">
    /// Cuscinetto da accumulare prima di iniziare la riproduzione, e da
    /// ricostruire dopo ogni svuotamento.
    /// </param>
    public JitterBufferProvider(BufferedWaveProvider source, int targetMilliseconds)
    {
        _source = source;
        TargetMilliseconds = Math.Clamp(targetMilliseconds, 0, 1000);

        _targetBytes = source.WaveFormat.AverageBytesPerSecond * TargetMilliseconds / 1000;
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Cuscinetto configurato, in millisecondi.</summary>
    public int TargetMilliseconds { get; }

    /// <summary>
    /// Numero di volte in cui il buffer si è svuotato durante la riproduzione.
    /// </summary>
    /// <remarks>
    /// È il numero che permette di regolare il cuscinetto per prove successive
    /// invece che a orecchio: se cresce durante una conversazione, il valore è
    /// troppo basso per questa rete. Se resta a zero, si può provare a ridurlo per
    /// guadagnare in prontezza.
    /// </remarks>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Millisecondi di silenzio inseriti al posto dell'audio mancante.</summary>
    public long SilenceMilliseconds =>
        Interlocked.Read(ref _silenceBytesInserted) * 1000 / Math.Max(1, WaveFormat.AverageBytesPerSecond);

    /// <summary>Millisecondi di audio attualmente in attesa di essere riprodotti.</summary>
    public int BufferedMilliseconds => (int)_source.BufferedDuration.TotalMilliseconds;

    /// <inheritdoc />
    /// <remarks>
    /// La destinazione arriva come <see cref="Span{T}"/> e non piu' come terna
    /// buffer-offset-conteggio: da NAudio 3.0 e' l'unica forma dell'interfaccia.
    /// L'offset non sparisce, si sposta — chi chiama affetta lo span prima di
    /// passarlo — e qui dentro si lavora sempre dall'inizio.
    /// </remarks>
    public int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            // Fase di riempimento: si restituisce silenzio finche' non si e'
            // accumulato il cuscinetto. Restituire zero byte farebbe interpretare a
            // WasapiOut la fine del flusso e fermerebbe la riproduzione.
            if (!_primed)
            {
                if (_source.BufferedBytes < _targetBytes)
                {
                    buffer.Clear();
                    Interlocked.Add(ref _silenceBytesInserted, buffer.Length);
                    return buffer.Length;
                }

                _primed = true;
            }

            var read = _source.Read(buffer);

            if (read < buffer.Length)
            {
                // Il buffer si e' svuotato: si completa con silenzio e si torna in
                // riempimento, cosi' il cuscinetto viene ricostruito invece di
                // restare esaurito e produrre un'interruzione a ogni pacchetto.
                buffer[read..].Clear();
                Interlocked.Add(ref _silenceBytesInserted, buffer.Length - read);
                Interlocked.Increment(ref _underruns);

                _primed = false;
            }

            return buffer.Length;
        }
    }

    /// <summary>Azzera i contatori diagnostici.</summary>
    public void ResetCounters()
    {
        Interlocked.Exchange(ref _underruns, 0);
        Interlocked.Exchange(ref _silenceBytesInserted, 0);
    }
}

/// <summary>
/// Garantisce che una lettura restituisca sempre il numero di byte richiesto,
/// completando con silenzio.
/// </summary>
/// <remarks>
/// <para>
/// Si applica come ultimo anello prima di WASAPI, dopo il ricampionatore.
/// <see cref="JitterBufferProvider"/> garantisce la lunghezza a monte, ma
/// <c>MediaFoundationResampler</c> bufferizza internamente e può restituire meno
/// byte di quanti richiesti anche quando la sorgente ne fornisce a sufficienza.
/// </para>
/// <para>
/// WASAPI copia i byte ricevuti ma dichiara alla scheda audio il conteggio pieno:
/// la parte non scritta resta memoria non inizializzata e viene riprodotta. È il
/// rumore bianco che si sente sotto la conversazione, ed è indipendente dal codec
/// negoziato — motivo per cui persiste anche cambiando codec.
/// </para>
/// </remarks>
public sealed class SilencePaddedProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private long _paddedBytes;

    /// <summary>Crea la guardia.</summary>
    /// <param name="source">Sorgente da proteggere.</param>
    public SilencePaddedProvider(IWaveProvider source) => _source = source;

    /// <inheritdoc />
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Byte di silenzio aggiunti per completare letture parziali.</summary>
    /// <remarks>
    /// Un valore che cresce indica che il ricampionatore restituisce letture corte:
    /// senza questa guardia, quegli stessi byte sarebbero stati rumore.
    /// </remarks>
    public long PaddedBytes => Interlocked.Read(ref _paddedBytes);

    /// <inheritdoc />
    public int Read(Span<byte> buffer)
    {
        var read = _source.Read(buffer);

        if (read >= buffer.Length) return buffer.Length;

        // Il massimo con zero non e' difensivismo: una sorgente che restituisce un
        // valore negativo farebbe partire l'azzeramento da un indice negativo, e
        // l'eccezione arriverebbe dentro il thread di riproduzione di WASAPI, dove
        // nessuno la vede e la voce si limita a sparire.
        var scritti = Math.Max(0, read);

        buffer[scritti..].Clear();
        Interlocked.Add(ref _paddedBytes, buffer.Length - scritti);

        return buffer.Length;
    }
}
