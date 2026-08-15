using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Accessibility;

/// <summary>
/// Sintetizza in campioni audio le frasi destinate a chi è in linea.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non è la voce degli annunci</b>, e vale la pena tenerle distinte. Quella parla
/// all'operatore e può uscire dallo screen reader, da una voce interna o da nessuna
/// delle due secondo la configurazione. Questa parla all'<b>interlocutore</b>: non ha
/// dispositivi audio, non ha modalità, non ha nulla da negoziare. Produce campioni, e
/// chi la chiama li mette nel flusso della telefonata.
/// </para>
/// <para>
/// <b>Si sceglie una voce italiana quando c'è.</b> Chi riceve l'avviso non ha
/// configurato nulla e non ha scelto niente: sente una frase italiana pronunciata da
/// una voce inglese solo se sul sistema non c'è altro, e in quel caso è comunque meglio
/// di niente.
/// </para>
/// <para>
/// Sedici kilohertz e non otto: la conversione verso la frequenza del codec la fa il
/// percorso audio, con il ricampionatore buono, e partire dal doppio lascia margine.
/// Sintetizzare direttamente a 8 kHz produrrebbe una voce già impastata prima ancora
/// di entrare in linea.
/// </para>
/// </remarks>
public sealed class RemoteVoice : IDisposable
{
    /// <summary>Frequenza di campionamento dei campioni prodotti.</summary>
    public const int SampleRate = 16000;

    private readonly ILogger<RemoteVoice> _log;
    private readonly Lock _gate = new();

    private SpeechSynthesizer? _synthesizer;
    private bool _failed;

    /// <summary>Crea la voce.</summary>
    /// <param name="logger">Log diagnostico.</param>
    public RemoteVoice(ILogger<RemoteVoice>? logger = null)
        => _log = logger ?? NullLogger<RemoteVoice>.Instance;

    /// <summary>
    /// Sintetizza una frase in campioni monofonici a 16 bit.
    /// </summary>
    /// <param name="text">Frase da pronunciare.</param>
    /// <returns>
    /// I campioni, oppure un vettore vuoto se la sintesi non è disponibile su questo
    /// sistema.
    /// </returns>
    /// <remarks>
    /// Il vettore vuoto non è un'eccezione perché un sistema senza voci SAPI è una
    /// configurazione possibile, non un guasto: chi chiama se ne accorge e avvisa
    /// l'operatore che l'interlocutore non ha sentito nulla, che è l'informazione che
    /// serve. Un'eccezione lo costringerebbe a gestire come errore una cosa che ha già
    /// una risposta.
    /// </remarks>
    public short[] Render(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var synthesizer = GetSynthesizer();
        if (synthesizer is null) return [];

        try
        {
            lock (_gate)
            {
                using var memory = new MemoryStream();

                synthesizer.SetOutputToAudioStream(
                    memory,
                    new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

                synthesizer.Speak(text);
                synthesizer.SetOutputToNull();

                var bytes = memory.ToArray();
                var samples = new short[bytes.Length / 2];

                Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);

                _log.LogInformation(
                    "Avviso per l'interlocutore sintetizzato: \"{Testo}\", {Ms} ms",
                    text, samples.Length * 1000 / SampleRate);

                return samples;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sintesi dell'avviso per l'interlocutore non riuscita");
            return [];
        }
    }

    private SpeechSynthesizer? GetSynthesizer()
    {
        if (_failed) return null;

        lock (_gate)
        {
            if (_synthesizer is not null) return _synthesizer;

            try
            {
                var synthesizer = new SpeechSynthesizer();

                var italiana = synthesizer.GetInstalledVoices()
                    .FirstOrDefault(v => v.Enabled
                                         && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "it");

                if (italiana is not null)
                {
                    synthesizer.SelectVoice(italiana.VoiceInfo.Name);
                    _log.LogInformation(
                        "Voce per gli avvisi all'interlocutore: {Voce}", italiana.VoiceInfo.Name);
                }
                else
                {
                    _log.LogWarning(
                        "Nessuna voce italiana installata: gli avvisi all'interlocutore useranno "
                        + "la voce predefinita del sistema");
                }

                _synthesizer = synthesizer;
                return _synthesizer;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sintesi vocale non disponibile: gli avvisi all'interlocutore non partiranno");
                _failed = true;
                return null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate) _synthesizer?.Dispose();
    }
}
