using BlindPhone.Telephony;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica il trattamento delle risposte SDP che aggiungono flussi non offerti.
/// </summary>
/// <remarks>
/// <para>
/// Nasce dal guasto del 12 agosto 2026: <b>tutte le chiamate uscenti mute nei due
/// versi</b>, entranti perfette. Il centralino aveva cominciato a rispondere a
/// un'offerta di solo audio con audio più video; la libreria SIP apriva una traccia
/// video su una sessione che di locale aveva il solo audio, e i pacchetti audio in
/// arrivo smettevano di raggiungere la riproduzione — «Inviati 479, ricevuti 0».
/// </para>
/// <para>
/// Le due prove coprono i due versi della regola, e il secondo conta quanto il primo:
/// le <b>risposte</b> si ripuliscono, le <b>offerte</b> no. Togliere un flusso da
/// un'offerta in arrivo farebbe costruire una risposta con meno descrizioni di quante
/// ne siano state offerte, cioè la stessa violazione dell'RFC 3264 commessa da noi.
/// </para>
/// </remarks>
public sealed class RispostaSdpAnomalaTests
{
    private const string Audio = """
        v=0
        o=- 1701623639 1701623639 IN IP4 192.0.2.10
        s=-
        c=IN IP4 192.0.2.10
        t=0 0
        m=audio 46592 RTP/AVP 18 101
        a=rtpmap:18 G729/8000
        a=rtpmap:101 telephone-event/8000
        a=sendrecv
        """;

    private const string Video = """
        m=video 46630 RTP/AVP 99 34
        a=rtpmap:99 H264/90000
        a=rtpmap:34 H263/90000
        a=sendrecv
        """;

    /// <summary>Sessione di solo audio, come quelle che il posto operatore apre davvero.</summary>
    private static AudioOnlyMediaSession Sessione()
    {
        var sorgente = new AudioExtrasSource(
            new AudioEncoder(),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });

        return new AudioOnlyMediaSession(new VoIPMediaSessionConfig
        {
            MediaEndPoint = new MediaEndPoints { AudioSource = sorgente },
        });
    }

    private static SDP Descrizione(bool conVideo)
        => SDP.ParseSDPDescription(conVideo ? Audio + "\r\n" + Video : Audio);

    [Fact]
    public void UnaRispostaCheAggiungeIlVideoVieneRipulita()
    {
        // E' esattamente la risposta che il centralino ha cominciato a mandare: due
        // flussi a fronte di un'offerta che ne conteneva uno solo.
        using var sessione = Sessione();
        sessione.CreateOffer(System.Net.IPAddress.Loopback);

        var esito = sessione.SetRemoteDescription(SdpType.answer, Descrizione(conVideo: true));

        Assert.Equal(SetDescriptionResultEnum.OK, esito);
        Assert.Single(sessione.RemoteDescription!.Media);
        Assert.Equal(SDPMediaTypesEnum.audio, sessione.RemoteDescription.Media[0].Media);
    }

    [Fact]
    public void UnaRispostaNormaleNonVieneToccata()
    {
        using var sessione = Sessione();
        sessione.CreateOffer(System.Net.IPAddress.Loopback);

        var esito = sessione.SetRemoteDescription(SdpType.answer, Descrizione(conVideo: false));

        Assert.Equal(SetDescriptionResultEnum.OK, esito);
        Assert.Single(sessione.RemoteDescription!.Media);
    }

    [Fact]
    public void UnOffertaInArrivoNonVieneRipulita()
    {
        // Su una chiamata entrante la descrizione remota E' l'offerta: la risposta la
        // costruisce il posto operatore, e deve poter rifiutare il video nei modi
        // previsti invece di fingere che non sia stato offerto.
        using var sessione = Sessione();

        sessione.SetRemoteDescription(SdpType.offer, Descrizione(conVideo: true));

        Assert.Equal(2, sessione.RemoteDescription!.Media.Count);
    }
}
