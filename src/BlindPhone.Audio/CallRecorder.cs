using NAudio.MediaFoundation;
using NAudio.Wave;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Audio;

/// <summary>
/// Registra una conversazione: le due direzioni mescolate in un unico file MP3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché mescolare e non tenere due tracce.</b> Una registrazione di servizio si
/// riascolta per sapere cosa è stato detto, non per analizzare il segnale: un file
/// solo si apre, si inoltra e si archivia con un gesto. Le due tracce separate
/// servirebbero a chi indaga sull'audio, e per quello esiste già la registrazione
/// diagnostica della sola ricezione.
/// </para>
/// <para>
/// <b>Il problema vero è la sincronia.</b> Le due direzioni arrivano da due thread
/// diversi e per strade diverse: la voce dell'operatore dal microfono, quella
/// dell'interlocutore dai pacchetti RTP. Nessuno garantisce che arrivino insieme, e
/// mescolarle appena arrivano produrrebbe un file in cui le due voci scivolano l'una
/// sull'altra fino a diventare un dialogo fra sordi.
/// </para>
/// <para>
/// Qui ogni direzione ha una propria coda e un proprio contatore di campioni scritti.
/// Si versa su file solo fino al punto in cui <b>entrambe</b> sono arrivate: è quel
/// minimo comune che tiene le due voci allineate al campione. Il resto aspetta.
/// </para>
/// <para>
/// <b>E se una direzione si ferma?</b> Succede: perdita di pacchetti, microfono
/// scollegato, interlocutore che mette in attesa. Senza una via d'uscita la coda
/// dell'altra crescerebbe senza fine e il file resterebbe fermo. Oltre
/// <see cref="MaxLagMilliseconds"/> la direzione in ritardo viene completata con
/// silenzio: un secondo di silenzio al posto giusto è un'informazione corretta, la
/// registrazione che si interrompe non lo è.
/// </para>
/// </remarks>
public sealed class CallRecorder : IDisposable
{
    /// <summary>
    /// Quanto una direzione può restare indietro prima di essere completata con
    /// silenzio.
    /// </summary>
    /// <remarks>
    /// Un secondo: abbastanza da assorbire una raffica di perdita di pacchetti senza
    /// inventare nulla, poco abbastanza da non far crescere la memoria e da non
    /// spostare percettibilmente le due voci.
    /// </remarks>
    private const int MaxLagMilliseconds = 1000;

    /// <summary>Ritmo di codifica dell'MP3, in bit al secondo.</summary>
    /// <remarks>
    /// Sessantaquattro kilobit per una voce monofonica a banda telefonica sono
    /// abbondanti: il codec di linea ne usa 64 in G.711 e 8 in G.729, quindi il
    /// pacchetto MP3 non è mai il collo di bottiglia della qualità. Non si scende
    /// oltre perché una registrazione di servizio va anche riascoltata da chi non era
    /// presente, e le sibilanti sono la prima cosa che un ritmo basso porta via.
    /// </remarks>
    private const int Mp3BitRate = 64000;

    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly int _sampleRate;
    private readonly string _wavPath;

    private float[] _outgoing = new float[16000];
    private float[] _incoming = new float[16000];
    private int _outgoingCount;
    private int _incomingCount;

    private WaveFileWriter? _writer;
    private long _samplesWritten;
    private bool _stopped;

    /// <summary>Predispone la registrazione di una conversazione.</summary>
    /// <param name="mp3Path">Percorso del file MP3 da produrre alla chiusura.</param>
    /// <param name="sampleRate">Frequenza di campionamento del codec di linea.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <remarks>
    /// Il file di lavoro è un WAV accanto alla destinazione, e non in una cartella
    /// temporanea di sistema: se la conversione finale non riesce — codificatore MP3
    /// assente su un'edizione di Windows senza Media Feature Pack — la conversazione
    /// resta comunque su disco, nel posto dove l'operatore la sta cercando, invece di
    /// finire in un percorso che nessuno andrà a guardare.
    /// </remarks>
    public CallRecorder(string mp3Path, int sampleRate, ILogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
        Mp3Path = mp3Path;
        _sampleRate = sampleRate > 0 ? sampleRate : 8000;
        _wavPath = Path.ChangeExtension(mp3Path, ".wav");

        Directory.CreateDirectory(Path.GetDirectoryName(mp3Path)!);
        _writer = new WaveFileWriter(_wavPath, new WaveFormat(_sampleRate, 16, 1));
    }

    /// <summary>Percorso del file MP3 prodotto alla chiusura.</summary>
    public string Mp3Path { get; }

    /// <summary>Durata registrata finora.</summary>
    public TimeSpan Duration
    {
        get { lock (_gate) return TimeSpan.FromSeconds((double)_samplesWritten / _sampleRate); }
    }

    /// <summary>Accoda la voce dell'operatore, quella che parte verso l'interlocutore.</summary>
    /// <param name="samples">Campioni a 16 bit alla frequenza del codec.</param>
    /// <param name="count">Quanti campioni del vettore sono validi.</param>
    public void WriteOutgoing(short[] samples, int count) => Append(ref _outgoing, ref _outgoingCount, samples, count);

    /// <summary>Accoda la voce dell'interlocutore, quella che arriva dalla rete.</summary>
    /// <param name="samples">Campioni a 16 bit alla frequenza del codec.</param>
    /// <param name="count">Quanti campioni del vettore sono validi.</param>
    public void WriteIncoming(short[] samples, int count) => Append(ref _incoming, ref _incomingCount, samples, count);

    private void Append(ref float[] queue, ref int queued, short[] samples, int count)
    {
        lock (_gate)
        {
            if (_stopped || _writer is null) return;

            var wanted = queued + count;
            if (wanted > queue.Length) Array.Resize(ref queue, Math.Max(wanted, queue.Length * 2));

            for (var i = 0; i < count; i++) queue[queued + i] = samples[i] / 32768f;
            queued += count;

            Drain();
        }
    }

    /// <summary>
    /// Versa su file la parte in cui entrambe le direzioni sono arrivate.
    /// </summary>
    /// <remarks>Va chiamato con il blocco già acquisito.</remarks>
    private void Drain()
    {
        if (_writer is null) return;

        var lag = Math.Abs(_outgoingCount - _incomingCount);
        var maxLag = _sampleRate * MaxLagMilliseconds / 1000;

        // Una direzione ferma da troppo tempo viene completata con silenzio, altrimenti
        // il file non avanzerebbe piu': vedi la nota della classe.
        if (lag > maxLag)
        {
            if (_outgoingCount < _incomingCount) PadWithSilence(ref _outgoing, ref _outgoingCount, lag);
            else PadWithSilence(ref _incoming, ref _incomingCount, lag);

            _log.LogDebug(
                "Registrazione: una direzione era indietro di {Lag} campioni, completata con silenzio", lag);
        }

        var usable = Math.Min(_outgoingCount, _incomingCount);
        if (usable == 0) return;

        var bytes = new byte[usable * 2];

        for (var i = 0; i < usable; i++)
        {
            // Somma con saturazione. Non si dimezzano i due contributi: la voce
            // telefonica arriva ben sotto il fondo scala — su questa postazione il
            // microfono ha picchi a meno ventitre decibel — e dimezzare produrrebbe una
            // registrazione che va riascoltata col volume al massimo.
            var mixed = Math.Clamp(_outgoing[i] + _incoming[i], -1f, 1f);
            var pcm = (short)Math.Round(mixed * short.MaxValue);

            bytes[i * 2] = (byte)(pcm & 0xFF);
            bytes[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        try
        {
            _writer.Write(bytes, 0, bytes.Length);
            _samplesWritten += usable;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scrittura della registrazione della chiamata non riuscita");
        }

        Consume(_outgoing, ref _outgoingCount, usable);
        Consume(_incoming, ref _incomingCount, usable);
    }

    private static void PadWithSilence(ref float[] queue, ref int queued, int count)
    {
        var wanted = queued + count;
        if (wanted > queue.Length) Array.Resize(ref queue, Math.Max(wanted, queue.Length * 2));

        Array.Clear(queue, queued, count);
        queued += count;
    }

    private static void Consume(float[] queue, ref int queued, int count)
    {
        var remaining = queued - count;
        if (remaining > 0) Array.Copy(queue, count, queue, 0, remaining);

        queued = Math.Max(0, remaining);
    }

    /// <summary>
    /// Chiude la registrazione e restituisce la durata registrata.
    /// </summary>
    /// <remarks>
    /// La conversione in MP3 <b>non</b> avviene qui: vedi
    /// <see cref="ConvertToMp3Async"/>. La durata si conosce subito perché è quella che
    /// va annunciata all'interlocutore nell'istante in cui la registrazione finisce.
    /// </remarks>
    public TimeSpan Stop()
    {
        lock (_gate)
        {
            if (_stopped) return Duration;

            _stopped = true;

            // Un ultimo versamento: quello che resta in una sola delle due code
            // appartiene alla conversazione quanto il resto, e va completato con
            // silenzio sull'altra invece di essere buttato.
            var lag = Math.Abs(_outgoingCount - _incomingCount);

            if (lag > 0)
            {
                if (_outgoingCount < _incomingCount) PadWithSilence(ref _outgoing, ref _outgoingCount, lag);
                else PadWithSilence(ref _incoming, ref _incomingCount, lag);
            }

            Drain();

            var durata = Duration;

            _writer?.Dispose();
            _writer = null;

            return durata;
        }
    }

    /// <summary>
    /// Converte in MP3 il file di lavoro e rimuove il WAV.
    /// </summary>
    /// <returns>
    /// Il percorso del file effettivamente disponibile: l'MP3 se la conversione è
    /// riuscita, altrimenti il WAV.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Gira fuori dal thread dell'interfaccia perché su una conversazione lunga la
    /// codifica dura qualche secondo, e il posto operatore non può restare fermo:
    /// nel frattempo può arrivare una chiamata.
    /// </para>
    /// <para>
    /// <b>Se la conversione non riesce il WAV resta.</b> Il codificatore MP3 di
    /// Windows manca sulle edizioni senza Media Feature Pack, e in quel caso
    /// cancellare il file di lavoro significherebbe buttare la conversazione per un
    /// problema di formato. Un WAV si riascolta lo stesso.
    /// </para>
    /// </remarks>
    public async Task<string> ConvertToMp3Async()
    {
        return await Task.Run(() =>
        {
            try
            {
                MediaFoundationApi.Startup();

                using (var reader = new WaveFileReader(_wavPath))
                {
                    MediaFoundationEncoder.EncodeToMp3(reader, Mp3Path, Mp3BitRate);
                }

                File.Delete(_wavPath);

                _log.LogInformation(
                    "Registrazione convertita in {Path}, {Size} byte",
                    Mp3Path, new FileInfo(Mp3Path).Length);

                return Mp3Path;
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Conversione in MP3 non riuscita: la registrazione resta in formato WAV in {Path}", _wavPath);

                return _wavPath;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            _stopped = true;
        }
    }
}
