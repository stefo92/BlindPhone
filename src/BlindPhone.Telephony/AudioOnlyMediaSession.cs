using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;

namespace BlindPhone.Telephony;

/// <summary>
/// Sessione media che accetta soltanto i flussi che ha offerto.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nasce da un guasto in esercizio del 12 agosto 2026: tutte le chiamate uscenti
/// erano mute in entrambi i versi.</b> Le entranti funzionavano. Il registro diceva
/// «Inviati 479, ricevuti 0» su ogni chiamata partita dalla postazione, con il cento
/// per cento di silenzio inventato in riproduzione.
/// </para>
/// <para>
/// <b>La causa sta nella risposta del centralino.</b> Il posto operatore offre un
/// flusso solo:
/// </para>
/// <code>
/// m=audio 57476 RTP/AVP 9 8 18 101
/// </code>
/// <para>
/// e da quella mattina il centralino ha cominciato a rispondere con due:
/// </para>
/// <code>
/// m=audio 46592 RTP/AVP 18 101
/// m=video 46630 RTP/AVP 99 34 98 104 100
/// </code>
/// <para>
/// Una risposta SDP <b>non può aggiungere flussi</b>: l'RFC 3264 §6 impone che abbia
/// lo stesso numero di descrizioni dell'offerta, nello stesso ordine, e che quelle
/// non accettate siano rifiutate con porta zero. Aggiungerne una è una violazione, ma
/// il centralino la fa e il posto operatore ci deve convivere: la libreria SIP prende
/// per buona la seconda descrizione, apre una traccia video remota su una sessione
/// che di locale ha il solo audio, e da quel momento i pacchetti audio in arrivo non
/// raggiungono più il percorso di riproduzione. Zero ricevuti, zero scartati: non
/// arrivano proprio.
/// </para>
/// <para>
/// <b>Le chiamate entranti non ne risentivano</b>, ed è la prova che indica la
/// direzione: lì l'offerta la fa il centralino e la risposta la costruisce il posto
/// operatore, che offre e accetta solo audio.
/// </para>
/// <para>
/// <b>Si filtrano le risposte, non le offerte.</b> Su un invito in arrivo la
/// descrizione remota <i>è</i> l'offerta, e toglierne un flusso farebbe costruire una
/// risposta con meno descrizioni di quante ne siano state offerte — cioè la stessa
/// violazione, commessa da noi. Su un'offerta con video ci pensa la libreria a
/// rifiutarlo nei modi previsti.
/// </para>
/// </remarks>
public sealed class AudioOnlyMediaSession : VoIPMediaSession
{
    private readonly ILogger _log;

    /// <summary>Crea la sessione.</summary>
    /// <param name="config">Configurazione della sessione media.</param>
    /// <param name="logger">Log diagnostico.</param>
    public AudioOnlyMediaSession(VoIPMediaSessionConfig config, ILogger? logger = null)
        : base(config)
        => _log = logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public override SetDescriptionResultEnum SetRemoteDescription(SdpType sdpType, SDP sessionDescription)
    {
        if (sdpType != SdpType.answer || sessionDescription is null)
        {
            return base.SetRemoteDescription(sdpType, sessionDescription!);
        }

        var estranei = sessionDescription.Media
            .Where(m => m.Media != SDPMediaTypesEnum.audio)
            .ToList();

        if (estranei.Count == 0) return base.SetRemoteDescription(sdpType, sessionDescription);

        _log.LogWarning(
            "Il centralino ha risposto con {Quanti} flussi che non erano stati offerti ({Tipi}): "
            + "vengono ignorati. Una risposta SDP non puo' aggiungere flussi (RFC 3264 §6), e "
            + "accettarli interrompe la ricezione dell'audio",
            estranei.Count,
            string.Join(", ", estranei.Select(m => m.Media.ToString())));

        return base.SetRemoteDescription(sdpType, WithAudioOnly(sessionDescription));
    }

    /// <summary>
    /// Ricostruisce la descrizione tenendo il solo audio.
    /// </summary>
    /// <param name="original">Descrizione ricevuta dal centralino.</param>
    /// <remarks>
    /// <b>Si lavora su una copia.</b> L'oggetto arriva dalla libreria, che lo conserva
    /// e lo rilegge: modificarlo sul posto significherebbe cambiare sotto i piedi di
    /// chi ce l'ha passato una struttura di cui non siamo proprietari. Il giro per il
    /// testo è il modo più sicuro di ottenere una copia indipendente, e costa una
    /// serializzazione per chiamata — cioè nulla, una volta per telefonata.
    /// </remarks>
    private SDP WithAudioOnly(SDP original)
    {
        try
        {
            var copia = SDP.ParseSDPDescription(original.ToString());
            copia.Media.RemoveAll(m => m.Media != SDPMediaTypesEnum.audio);

            return copia;
        }
        catch (Exception ex)
        {
            // Se la copia non riesce si prosegue con l'originale: una chiamata muta e'
            // meglio di una chiamata che non parte, e il registro dice cosa e'
            // successo.
            _log.LogError(ex, "Ripulitura della descrizione di sessione non riuscita");
            return original;
        }
    }
}
