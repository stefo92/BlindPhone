using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using BlindPhone.Telephony;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// La postazione in lavoro agile: indirizzo annunciato per l'audio, interrogazione STUN
/// dal socket audio, segnale di mantenimento, impostazioni.
/// </summary>
/// <remarks>
/// <para>
/// Le prove sull'offerta guardano la descrizione di sessione che parte davvero, costruita
/// dalla libreria SIP: la correzione per la VPN parziale e l'indirizzo pubblico passano
/// entrambi da comportamenti della libreria — quale indirizzo usa quando gliene si passa
/// uno, che cosa fa dell'indirizzo riflesso messo sul canale — e un aggiornamento che li
/// cambiasse deve far fallire queste prove, non le chiamate da casa.
/// </para>
/// <para>
/// La sonda STUN si prova con un server finto sull'indirizzo di loopback: nessun
/// pacchetto esce dalla macchina.
/// </para>
/// </remarks>
public sealed class LavoroAgileTests
{
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

    // -----------------------------------------------------------------------
    // Indirizzo nell'offerta
    // -----------------------------------------------------------------------

    [Fact]
    public void LaPrimaOffertaUsaLIndirizzoDelCollegamentoVersoIlCentralino()
    {
        // Con la VPN parziale e' l'indirizzo del tunnel, non quello della rete di casa su
        // cui passa la strada verso Internet.
        using var sessione = Sessione();
        sessione.PreferredLocalAddress = IPAddress.Parse("10.8.0.6");

        var offerta = sessione.CreateOffer();

        Assert.Equal("10.8.0.6", offerta.Connection.ConnectionAddress);
    }

    [Fact]
    public void UnIndirizzoChiestoEsplicitamenteVinceSuQuelloPreferito()
    {
        using var sessione = Sessione();
        sessione.PreferredLocalAddress = IPAddress.Parse("10.8.0.6");

        var offerta = sessione.CreateOffer(IPAddress.Parse("192.0.2.33"));

        Assert.Equal("192.0.2.33", offerta.Connection.ConnectionAddress);
    }

    [Fact]
    public void LIndirizzoPubblicoSulCanaleFiniscePerInteroNellOfferta()
    {
        // Riga c= e porta della riga m=: con una sola delle due il centralino manderebbe
        // la voce all'indirizzo giusto sulla porta sbagliata, o viceversa.
        using var sessione = Sessione();
        sessione.PreferredLocalAddress = IPAddress.Parse("192.168.1.20");

        var pubblico = new IPEndPoint(IPAddress.Parse("203.0.113.45"), 40012);
        sessione.AudioStream.GetRTPChannel().RTPSrflxEndPoint = pubblico;

        var offerta = sessione.CreateOffer();

        Assert.Equal("203.0.113.45", offerta.Connection.ConnectionAddress);
        Assert.Equal(40012, offerta.Media.Single(m => m.Media == SDPMediaTypesEnum.audio).Port);
    }

    [Fact]
    public void SenzaIndirizzoPubblicoLaPortaDellOffertaEQuellaDelSocket()
    {
        using var sessione = Sessione();
        sessione.PreferredLocalAddress = IPAddress.Parse("192.168.1.20");

        var offerta = sessione.CreateOffer();

        Assert.Equal(
            sessione.AudioStream.GetRTPChannel().RTPPort,
            offerta.Media.Single(m => m.Media == SDPMediaTypesEnum.audio).Port);
    }

    // -----------------------------------------------------------------------
    // Interrogazione STUN dal socket audio
    // -----------------------------------------------------------------------

    /// <summary>Risposta di successo con l'indirizzo dato, cifrato come vuole la RFC 5389.</summary>
    private static byte[] Risposta(byte[] transazione, IPEndPoint visto)
    {
        var risposta = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(risposta, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(risposta.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(risposta.AsSpan(4), 0x2112A442);
        transazione.CopyTo(risposta, 8);

        BinaryPrimitives.WriteUInt16BigEndian(risposta.AsSpan(20), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(risposta.AsSpan(22), 8);
        risposta[25] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(risposta.AsSpan(26), (ushort)(visto.Port ^ 0x2112));

        var indirizzo = visto.Address.GetAddressBytes();
        byte[] chiave = [0x21, 0x12, 0xA4, 0x42];
        for (var i = 0; i < 4; i++) risposta[28 + i] = (byte)(indirizzo[i] ^ chiave[i]);

        return risposta;
    }

    [Fact]
    public async Task LaSondaRitrasmetteEScartaLeRisposteDiAltreTransazioni()
    {
        using var sessione = Sessione();
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        var visto = new IPEndPoint(IPAddress.Parse("203.0.113.45"), 40012);
        var richieste = 0;

        var finto = Task.Run(async () =>
        {
            while (true)
            {
                var arrivata = await server.ReceiveAsync();
                richieste++;

                // La prima richiesta si perde, come su un Wi-Fi disturbato.
                if (richieste == 1) continue;

                var transazione = arrivata.Buffer[8..20];

                // Prima una risposta che non e' sua, poi quella giusta.
                await server.SendAsync(Risposta(StunBinding.NewTransactionId(), new IPEndPoint(IPAddress.Parse("198.51.100.99"), 1)), arrivata.RemoteEndPoint);
                await server.SendAsync(Risposta(transazione, visto), arrivata.RemoteEndPoint);
                return;
            }
        });

        var esito = await StunMediaProbe.QueryAsync(
            sessione.AudioStream.GetRTPChannel(),
            (IPEndPoint)server.Client.LocalEndPoint!,
            CancellationToken.None);

        await finto;

        Assert.Equal(StunOutcome.Mapped, esito.Outcome);
        Assert.Equal(visto, esito.Mapped);
        Assert.True(richieste >= 2, "la richiesta persa non e' stata ritrasmessa");
    }

    [Fact]
    public async Task UnServerMutoFaRinunciareNelTempoStabilito()
    {
        using var sessione = Sessione();
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        var esito = await StunMediaProbe.QueryAsync(
            sessione.AudioStream.GetRTPChannel(),
            (IPEndPoint)server.Client.LocalEndPoint!,
            CancellationToken.None);

        cronometro.Stop();

        Assert.Equal(StunOutcome.NoResponse, esito.Outcome);

        // E' il ritardo massimo che un server muto aggiunge alla partenza di una chiamata.
        Assert.InRange(cronometro.Elapsed, StunMediaProbe.Budget - TimeSpan.FromMilliseconds(100), StunMediaProbe.Budget + TimeSpan.FromSeconds(1));
    }

    // -----------------------------------------------------------------------
    // Segnale di mantenimento
    // -----------------------------------------------------------------------

    [Fact]
    public void IlSegnaleEUnDoppioACapo()
        => Assert.Equal("\r\n\r\n", Encoding.ASCII.GetString(KeepAliveSchedule.Payload));

    [Theory]
    [InlineData(0.0, 20)]
    [InlineData(0.5, 22.5)]
    [InlineData(1.0, 25)]
    [InlineData(7.0, 25)] // un numero fuori scala non deve allungare l'attesa oltre l'intervallo
    public void IlSegnaleParteFraLOttantaEIlCentoPerCentoDellIntervallo(double caso, double secondi)
        => Assert.Equal(TimeSpan.FromSeconds(secondi), KeepAliveSchedule.NextDelay(TimeSpan.FromSeconds(25), caso));

    [Theory]
    [InlineData(25, 25)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 10)] // da un file scritto a mano: non un pacchetto al secondo
    [InlineData(3600, 300)]
    public void LIntervalloVieneRiportatoNeiLimiti(int configurato, int effettivo)
        => Assert.Equal(
            TimeSpan.FromSeconds(effettivo),
            new SipAccountOptions { KeepAliveSeconds = configurato }.KeepAliveInterval);

    // -----------------------------------------------------------------------
    // Impostazioni
    // -----------------------------------------------------------------------

    [Fact]
    public void UnAccountDiPrimaDellAggiornamentoHaIlMantenimentoAccesoESenzaServerStun()
    {
        // Il segnale verso il proprio centralino si accende da solo; il server STUN no,
        // perche' sarebbe traffico verso terzi che nessuno ha deciso.
        var account = new SipSettings();

        Assert.Equal(25, account.KeepAliveSeconds);
        Assert.Null(account.StunServer);
        Assert.Equal(SipAccountOptions.DefaultKeepAliveSeconds, account.KeepAliveSeconds);
    }

    [Fact]
    public void ServerStunEMantenimentoSopravvivonoAlRiavvio()
    {
        var cartella = Path.Combine(Path.GetTempPath(), "BlindPhone.Test", $"lavoro-agile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cartella);

        try
        {
            var servizio = new SettingsService(cartella);
            var iniziali = servizio.Load();

            var account = new SipSettings
            {
                Id = "casa",
                Name = "Da casa",
                Username = "200",
                Domain = "pbx.ente.it",
                StunServer = "stun.ente.it:3479",
                KeepAliveSeconds = 40,
            };

            Assert.True(servizio.Save(iniziali with { SipAccounts = [account], DefaultSipAccountId = "casa" }));

            var riletto = new SettingsService(cartella).Load().SipAccounts.Single(a => a.Id == "casa");

            Assert.Equal("stun.ente.it:3479", riletto.StunServer);
            Assert.Equal(40, riletto.KeepAliveSeconds);
        }
        finally
        {
            try { Directory.Delete(cartella, recursive: true); }
            catch (IOException) { }
        }
    }
}
