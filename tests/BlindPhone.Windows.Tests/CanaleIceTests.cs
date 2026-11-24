using System.Net;
using System.Net.Sockets;
using BlindPhone.Core.Services;
using BlindPhone.Telephony;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// ICE (RFC 8445) sulla sessione audio del posto operatore, con due sessioni vere.
/// </summary>
/// <remarks>
/// <para>
/// <b>La prova principale riproduce il centralino in VPN.</b> Ciascuna parte annuncia
/// due candidati: il primo, con la priorità più alta e scritto anche nelle righe
/// <c>c=</c> e <c>m=</c>, porta a un socket che riceve e non risponde mai — come un
/// indirizzo che dal tunnel non si raggiunge; il secondo è quello buono. Senza ICE la
/// voce andrebbe al primo e si perderebbe. La prova passa se le verifiche trovano il
/// secondo e la destinazione dell'audio si sposta lì, da entrambe le parti.
/// </para>
/// <para>
/// Tutto su 127.0.0.1: i candidati scritti dalla postazione si sostituiscono fra
/// un'offerta e l'altra, come farebbe la rete, perché quelli veri dipendono dalle schede
/// della macchina che esegue la prova. Il «socket morto» è un socket aperto e mai letto,
/// e non una porta chiusa: su Windows una porta chiusa risponde con un errore ICMP che
/// si ripercuote sul socket che l'ha raggiunta, cioè proverebbe un'altra cosa.
/// </para>
/// </remarks>
public sealed class CanaleIceTests
{
    private static readonly TimeSpan Attesa = TimeSpan.FromSeconds(10);

    private static AudioOnlyMediaSession Sessione(bool ice)
    {
        var sorgente = new AudioExtrasSource(
            new AudioEncoder(),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });

        return new AudioOnlyMediaSession(
            new VoIPMediaSessionConfig { MediaEndPoint = new MediaEndPoints { AudioSource = sorgente } },
            logger: null,
            ice: ice);
    }

    private static int Porta(RTPSession sessione) => sessione.AudioStream.GetRTPChannel().RTPPort;

    private static SDPMediaAnnouncement Audio(SDP sdp) => sdp.Media.Single(m => m.Media == SDPMediaTypesEnum.audio);

    /// <summary>
    /// Riscrive una descrizione come la vedrebbe l'altra parte: righe c= e m= sul
    /// candidato morto, e i due candidati in ordine di priorità.
    /// </summary>
    private static SDP AttraversoLaRete(SDP sdp, int portaMorta, int portaBuona)
    {
        var audio = Audio(sdp);

        audio.IceCandidates =
        [
            $"H1 1 UDP 2130706431 127.0.0.1 {portaMorta} typ host",
            $"H2 1 udp 2130706175 127.0.0.1 {portaBuona} typ host",
        ];

        audio.Port = portaMorta;
        if (audio.Connection is not null) audio.Connection.ConnectionAddress = "127.0.0.1";
        if (sdp.Connection is not null) sdp.Connection.ConnectionAddress = "127.0.0.1";

        return SDP.ParseSDPDescription(sdp.ToString());
    }

    private static async Task<IceStatus> AspettaConnessa(AudioOnlyMediaSession sessione)
    {
        var limite = DateTime.UtcNow + Attesa;

        while (DateTime.UtcNow < limite)
        {
            if (sessione.Ice is { Progress: IceProgress.Connected } stato) return stato;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"ICE non connesso entro {Attesa.TotalSeconds} secondi: {sessione.Ice?.Describe() ?? "nessuno stato"}");
        return null!;
    }

    [Fact]
    public async Task LeVerificheSpostanoLAudioSulCandidatoCheRispondeDaEntrambeLeParti()
    {
        using var mortoPostazione = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var mortoCentralino = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var portaMortaPostazione = ((IPEndPoint)mortoPostazione.Client.LocalEndPoint!).Port;
        var portaMortaCentralino = ((IPEndPoint)mortoCentralino.Client.LocalEndPoint!).Port;

        using var postazione = Sessione(ice: true);
        using var centralino = Sessione(ice: true);

        // Chiamata uscente: offre la postazione, che quindi controlla la scelta.
        var offerta = AttraversoLaRete(postazione.CreateOffer(IPAddress.Loopback), portaMortaPostazione, Porta(postazione));
        Assert.Equal(SetDescriptionResultEnum.OK, centralino.SetRemoteDescription(SdpType.offer, offerta));

        var risposta = AttraversoLaRete(centralino.CreateAnswer(IPAddress.Loopback), portaMortaCentralino, Porta(centralino));
        Assert.Equal(SetDescriptionResultEnum.OK, postazione.SetRemoteDescription(SdpType.answer, risposta));

        // Prima delle verifiche la libreria segue la descrizione: il candidato morto.
        Assert.Equal(portaMortaCentralino, postazione.AudioDestinationEndPoint!.Port);

        var lato = await AspettaConnessa(postazione);
        var altro = await AspettaConnessa(centralino);

        Assert.True(lato.Controlling);
        Assert.False(altro.Controlling);

        Assert.Equal(new IPEndPoint(IPAddress.Loopback, Porta(centralino)), postazione.AudioDestinationEndPoint);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, Porta(postazione)), centralino.AudioDestinationEndPoint);
        Assert.Contains($"127.0.0.1 porta {Porta(centralino)}", lato.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaRipresaDallAttesaNonRiportaLAudioSulCandidatoScartato()
    {
        using var mortoPostazione = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var mortoCentralino = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var portaMortaPostazione = ((IPEndPoint)mortoPostazione.Client.LocalEndPoint!).Port;
        var portaMortaCentralino = ((IPEndPoint)mortoCentralino.Client.LocalEndPoint!).Port;

        using var postazione = Sessione(ice: true);
        using var centralino = Sessione(ice: true);

        var offerta = AttraversoLaRete(postazione.CreateOffer(IPAddress.Loopback), portaMortaPostazione, Porta(postazione));
        centralino.SetRemoteDescription(SdpType.offer, offerta);
        var risposta = AttraversoLaRete(centralino.CreateAnswer(IPAddress.Loopback), portaMortaCentralino, Porta(centralino));
        postazione.SetRemoteDescription(SdpType.answer, risposta);

        await AspettaConnessa(postazione);

        // Il centralino rimette in attesa e poi riprende: una nuova offerta, con le righe
        // c= e m= ancora sul candidato morto. La libreria riporterebbe li' la voce.
        var ripresa = AttraversoLaRete(centralino.CreateOffer(IPAddress.Loopback), portaMortaCentralino, Porta(centralino));
        Assert.Equal(SetDescriptionResultEnum.OK, postazione.SetRemoteDescription(SdpType.offer, ripresa));
        postazione.CreateAnswer(IPAddress.Loopback);

        Assert.Equal(new IPEndPoint(IPAddress.Loopback, Porta(centralino)), postazione.AudioDestinationEndPoint);
    }

    [Fact]
    public void LOffertaPortaCredenzialiCandidatiERtcpSulloStessoSocket()
    {
        using var postazione = Sessione(ice: true);

        var offerta = postazione.CreateOffer(IPAddress.Loopback);
        var audio = Audio(offerta);

        Assert.True(audio.IceUfrag?.Length >= 4, "RFC 8445 §5.3: almeno 4 caratteri di nome utente");
        Assert.True(audio.IcePwd?.Length >= 22, "RFC 8445 §5.3: almeno 22 caratteri di password");
        Assert.Equal("ice2", audio.IceOptions);
        Assert.Contains("a=rtcp-mux", audio.ExtraMediaAttributes);

        // Il ruolo DTLS di WebRTC non c'entra con una sessione SIP e non deve comparire.
        Assert.Null(audio.IceRole);
        Assert.DoesNotContain("a=setup", offerta.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IlCandidatoPredefinitoCompareFraICandidati()
    {
        // Sulle macchine senza schede accese oltre al ritorno non c'e' nessun candidato
        // da annunciare, e la prova non ha senso.
        if (!IceTranslation.LocalAddresses().Any(IceCandidatePlan.IsUsableHostAddress)) return;

        using var postazione = Sessione(ice: true);

        var offerta = postazione.CreateOffer();
        var audio = Audio(offerta);
        var indirizzo = audio.Connection?.ConnectionAddress ?? offerta.Connection.ConnectionAddress;

        // Senza, un centralino conclude che qualcuno ha riscritto la descrizione e
        // abbandona ICE (RFC 8839 §5.1, ice-mismatch).
        Assert.Contains(postazione.IceLocalCandidates, c =>
            c.EndPoint.Address.ToString() == indirizzo && c.EndPoint.Port == audio.Port);
    }

    [Fact]
    public void UnCentralinoCheRispondeSenzaIceLasciaLaDescrizioneComEra()
    {
        using var postazione = Sessione(ice: true);
        postazione.CreateOffer(IPAddress.Loopback);

        var risposta = SDP.ParseSDPDescription("""
            v=0
            o=- 1 1 IN IP4 192.0.2.10
            s=-
            c=IN IP4 192.0.2.10
            t=0 0
            m=audio 46592 RTP/AVP 8 101
            a=rtpmap:8 PCMA/8000
            a=rtpmap:101 telephone-event/8000
            a=sendrecv
            """);

        Assert.Equal(SetDescriptionResultEnum.OK, postazione.SetRemoteDescription(SdpType.answer, risposta));

        Assert.Equal(IceProgress.NotUsedByPeer, postazione.Ice!.Progress);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 46592), postazione.AudioDestinationEndPoint);

        // E non lo si ripropone a ogni attesa.
        Assert.Null(Audio(postazione.CreateOffer(IPAddress.Loopback)).IceUfrag);
    }

    [Fact]
    public void ConUnCentralinoLiteLaPostazioneControllaAncheSullaChiamataEntrante()
    {
        using var postazione = Sessione(ice: true);

        var offerta = SDP.ParseSDPDescription("""
            v=0
            o=- 1 1 IN IP4 192.0.2.10
            s=-
            a=ice-lite
            c=IN IP4 192.0.2.10
            t=0 0
            m=audio 46592 RTP/AVP 8 101
            a=rtpmap:8 PCMA/8000
            a=rtpmap:101 telephone-event/8000
            a=ice-ufrag:Pbx1
            a=ice-pwd:passwordDelCentralinoLunga
            a=candidate:1 1 UDP 2130706431 192.0.2.10 46592 typ host
            a=candidate:2 2 UDP 2130706430 192.0.2.10 46593 typ host
            a=sendrecv
            """);

        Assert.Equal(SetDescriptionResultEnum.OK, postazione.SetRemoteDescription(SdpType.offer, offerta));

        var stato = postazione.Ice!;
        Assert.Equal(IceProgress.Checking, stato.Progress);
        Assert.True(stato.Controlling, "Con un centralino lite nessun altro sceglierebbe la coppia");
        Assert.True(stato.RemoteIsLite);
        Assert.Equal(1, stato.RemoteCandidates);

        // Il centralino non ha offerto rtcp-mux: la risposta non lo deve accettare.
        var risposta = postazione.CreateAnswer(IPAddress.Loopback);
        Assert.NotNull(Audio(risposta).IceUfrag);
        Assert.DoesNotContain("a=rtcp-mux", Audio(risposta).ExtraMediaAttributes);
    }

    [Fact]
    public void SenzaIceLaSessioneEQuellaDiSempre()
    {
        using var conIce = Sessione(ice: true);
        using var senza = Sessione(ice: false);

        // La seconda e' costruita sullo stesso thread subito dopo la prima: l'opzione
        // passata alla libreria durante la costruzione non deve restare appesa.
        Assert.IsType<IceRtpChannel>(conIce.AudioStream.GetRTPChannel());
        Assert.IsNotType<IceRtpChannel>(senza.AudioStream.GetRTPChannel());

        Assert.Null(senza.Ice);
        Assert.Null(Audio(senza.CreateOffer(IPAddress.Loopback)).IceUfrag);
    }

    [Fact]
    public void LaLibreriaPermetteAncoraDiImpostareIlRuolo()
    {
        // Se un aggiornamento di SIPSorcery toglie o rinomina la proprieta', i centralini
        // ICE lite smettono di funzionare senza un errore: lo deve dire questa prova.
        Assert.True(IceRtpChannel.CanSetRole);

        using var canale = new IceRtpChannel(null, 0, null);
        Assert.True(canale.SetControlling(true));
        Assert.True(canale.IsController);
        Assert.True(canale.SetControlling(false));
        Assert.False(canale.IsController);
    }

    [Fact]
    public void IRapportiRtcpEsconoDalSocketDellRtp()
    {
        using var ricevitore = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var canale = new IceRtpChannel(null, 0, null);

        // Avviato come lo avvia la sessione: la libreria spedisce solo da un canale in
        // ascolto.
        canale.Start();

        // Senza l'adattamento la libreria solleverebbe: il canale ICE non ha un socket
        // di controllo.
        var esito = canale.Send(
            RTPChannelSocketsEnum.Control,
            (IPEndPoint)ricevitore.Client.LocalEndPoint!,
            [0x80, 0xC8, 0x00, 0x01]);

        Assert.Equal(SocketError.Success, esito);
    }

    [Theory]
    [InlineData("1 1 UDP 2130706431 10.20.0.5 12000 typ host", true)]
    [InlineData("1 1 udp 1694498815 203.0.113.9 40000 typ srflx raddr 10.20.0.5 rport 12000", true)]
    [InlineData("1 1 udp 2130706431 10.20.0.5 12000 typ HOST", true)]
    [InlineData("2 2 udp 2130706430 10.20.0.5 12001 typ host", false)]
    [InlineData("3 1 tcp 1518280447 10.20.0.5 9 typ host tcptype active", false)]
    [InlineData("4 1 udp 2130706431 centralino.local 12000 typ host", false)]
    [InlineData("5 1 udp 2130706431 10.20.0.5 0 typ host", false)]
    [InlineData("rotta", false)]
    public void SiVerificanoSoloICandidatiRtpSuUdpConUnIndirizzo(string riga, bool tenuto)
    {
        var candidato = RemoteIceParameters.TryParse(riga);

        Assert.Equal(tenuto, candidato is not null);

        if (candidato is not null)
        {
            Assert.Equal(RTCIceProtocol.udp, candidato.protocol);
            Assert.NotNull(candidato.DestinationEndPoint);
        }
    }
}
