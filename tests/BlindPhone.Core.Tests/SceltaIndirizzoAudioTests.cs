using System.Net;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Quando la descrizione di sessione porta l'indirizzo pubblico, e quando no.
/// </summary>
/// <remarks>
/// <para>
/// I casi sono quelli che una postazione incontra davvero: casa con il router, sede,
/// VPN che porta in ufficio solo il traffico per l'ente, server STUN muto o messo in
/// rete interna. La regola vale solo se in tutti tranne il primo lascia l'indirizzo
/// della postazione, perché sono i casi in cui oggi l'audio funziona.
/// </para>
/// <para>
/// Gli indirizzi sono quelli riservati alla documentazione (RFC 5737): nessun test
/// tocca la rete.
/// </para>
/// </remarks>
public sealed class SceltaIndirizzoAudioTests
{
    private static readonly IPAddress CentralinoPubblico = IPAddress.Parse("198.51.100.10");
    private static readonly IPAddress PostazioneDiCasa = IPAddress.Parse("192.168.1.20");
    private static readonly IPEndPoint VistoDalServer = new(IPAddress.Parse("203.0.113.45"), 40012);

    private static StunBindingResult Risposta(IPEndPoint visto) => new(StunOutcome.Mapped, visto);

    [Fact]
    public void DaCasaDietroIlRouterSiAnnunciaLIndirizzoPubblico()
    {
        var scelta = MediaAddressPolicy.Decide(
            Risposta(VistoDalServer), CentralinoPubblico, PostazioneDiCasa, PostazioneDiCasa);

        Assert.True(scelta.UsesPublicAddress);
        Assert.Equal(VistoDalServer, scelta.PublicEndPoint);

        // La ragione si legge ad alta voce: «porta», non «due punti».
        Assert.Contains("203.0.113.45 porta 40012", scelta.Reason, StringComparison.Ordinal);
        Assert.Contains("dietro NAT", scelta.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaServerStunNonCambiaNiente()
    {
        var scelta = MediaAddressPolicy.Decide(null, CentralinoPubblico, PostazioneDiCasa, null);

        Assert.False(scelta.UsesPublicAddress);
        Assert.Contains("non configurato", scelta.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.20.0.5")] // centralino in sede, raggiunto dalla VPN
    [InlineData("172.16.4.2")]
    [InlineData("192.168.10.1")]
    [InlineData("100.100.1.1")] // spazio condiviso della RFC 6598, usato da diverse VPN
    [InlineData("fd12:3456::1")] // indirizzo locale unico IPv6
    public void ConIlCentralinoInReteInternaLIndirizzoPubblicoNonSiUsa(string centralino)
    {
        // E' la sede e la VPN: le postazioni che oggi funzionano. Una risposta STUN,
        // anche perfetta, non deve poterle cambiare.
        var scelta = MediaAddressPolicy.Decide(
            Risposta(VistoDalServer), IPAddress.Parse(centralino), PostazioneDiCasa, PostazioneDiCasa);

        Assert.False(scelta.UsesPublicAddress);
        Assert.Contains("rete interna", scelta.Reason, StringComparison.Ordinal);
        Assert.False(MediaAddressPolicy.ShouldQuery(IPAddress.Parse(centralino)));
    }

    [Fact]
    public void ConLaVpnParzialeLIndirizzoDelServerStunNonValePerIlCentralino()
    {
        // Il centralino pubblico si raggiunge dal tunnel, il server STUN dalla linea di
        // casa: quello che il server vede e' l'uscita di casa, non quella del tunnel.
        var tunnel = IPAddress.Parse("10.8.0.6");

        var scelta = MediaAddressPolicy.Decide(Risposta(VistoDalServer), CentralinoPubblico, tunnel, PostazioneDiCasa);

        Assert.False(scelta.UsesPublicAddress);
        Assert.Contains("collegamenti diversi", scelta.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaNatLIndirizzoDellaPostazioneBasta()
    {
        // Postazione con indirizzo pubblico proprio: il server vede quello.
        var propria = IPAddress.Parse("203.0.113.45");

        var scelta = MediaAddressPolicy.Decide(Risposta(VistoDalServer), CentralinoPubblico, propria, propria);

        Assert.False(scelta.UsesPublicAddress);
        Assert.Contains("nessun NAT", scelta.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnServerStunMutoLasciaLIndirizzoDellaPostazioneEDiceCheNonHaRisposto()
    {
        var scelta = MediaAddressPolicy.Decide(
            StunBindingResult.NoResponse("nessuna risposta in 1,5 secondi"),
            CentralinoPubblico,
            PostazioneDiCasa,
            PostazioneDiCasa);

        Assert.False(scelta.UsesPublicAddress);
        Assert.Contains("non ha risposto", scelta.Reason, StringComparison.Ordinal);
        Assert.Contains("indirizzo della postazione", scelta.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnServerStunInReteInternaNonVedeLIndirizzoPubblico()
    {
        var scelta = MediaAddressPolicy.Decide(
            Risposta(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 50000)),
            CentralinoPubblico,
            PostazioneDiCasa,
            PostazioneDiCasa);

        Assert.False(scelta.UsesPublicAddress);
        Assert.Contains("rete privata", scelta.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FinoAllaPrimaRegistrazioneIlCentralinoNonENotoENonSiRischia()
    {
        var scelta = MediaAddressPolicy.Decide(Risposta(VistoDalServer), null, null, PostazioneDiCasa);

        Assert.False(scelta.UsesPublicAddress);
        Assert.False(MediaAddressPolicy.ShouldQuery(null));
    }

    [Fact]
    public void UnIndirizzoIPv4ScrittoComeIPv6EStessoIndirizzo()
    {
        // I socket a doppio protocollo dicono ::ffff:192.168.1.20: senza normalizzare,
        // la postazione risulterebbe diversa da se stessa e si annuncerebbe pubblica.
        var mappato = IPAddress.Parse("::ffff:203.0.113.45");

        var scelta = MediaAddressPolicy.Decide(
            Risposta(VistoDalServer), CentralinoPubblico, mappato, mappato);

        Assert.False(scelta.UsesPublicAddress);
        Assert.True(MediaAddressPolicy.IsInternal(IPAddress.Parse("::ffff:192.168.1.20")));
    }

    [Fact]
    public void UnaRispostaIPv6NonSiAnnunciaAUnCentralinoIPv4()
    {
        var scelta = MediaAddressPolicy.Decide(
            Risposta(new IPEndPoint(IPAddress.Parse("2001:db8::45"), 40012)),
            CentralinoPubblico,
            PostazioneDiCasa,
            PostazioneDiCasa);

        Assert.False(scelta.UsesPublicAddress);
    }

    [Theory]
    [InlineData("192.0.2.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.3.4", true)] // Windows senza DHCP
    [InlineData("127.0.0.1", true)]
    [InlineData("100.63.255.255", false)] // appena fuori dallo spazio condiviso
    [InlineData("100.64.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:db8::1", false)]
    public void LaReteInternaSiRiconosce(string indirizzo, bool interna)
        => Assert.Equal(interna, MediaAddressPolicy.IsInternal(IPAddress.Parse(indirizzo)));

    [Fact]
    public void ConIlCentralinoPubblicoConvieneChiedere()
        => Assert.True(MediaAddressPolicy.ShouldQuery(CentralinoPubblico));
}

/// <summary>
/// Il NAT fra postazione e centralino letto dalla risposta alla registrazione, RFC 3581.
/// </summary>
public sealed class NatSullaSegnalazioneTests
{
    private static readonly IPEndPoint Socket = new(IPAddress.Parse("192.168.1.20"), 5060);

    [Fact]
    public void UnIndirizzoDiversoDaQuelloDelSocketVuolDireNat()
    {
        var visto = SignallingNat.Observe(Socket, "203.0.113.45", 61234);

        Assert.True(visto.BehindNat);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.45"), 61234), visto.SeenAs);
        Assert.Contains("203.0.113.45 porta 61234", visto.Description, StringComparison.Ordinal);
        Assert.Contains("192.168.1.20 porta 5060", visto.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void LoStessoIndirizzoConUnAltraPortaENatComunque()
    {
        // Il router che lascia l'indirizzo e cambia la porta sta traducendo: il passaggio
        // che il centralino deve usare e' quello.
        var visto = SignallingNat.Observe(Socket, "192.168.1.20", 40000);

        Assert.True(visto.BehindNat);
    }

    [Fact]
    public void LoStessoIndirizzoELaStessaPortaVuolDireNessunNat()
    {
        var visto = SignallingNat.Observe(Socket, "192.168.1.20", 5060);

        Assert.False(visto.BehindNat);
        Assert.StartsWith("assente", visto.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("non-un-indirizzo")]
    public void UnCentralinoCheNonApplicaLaRfc3581NonPermetteDiDirlo(string? received)
    {
        var visto = SignallingNat.Observe(Socket, received, 0);

        Assert.Null(visto.BehindNat);
        Assert.StartsWith("non determinato", visto.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void UnSocketInAscoltoSuTutteLeSchedeNonSiConfronta()
    {
        var visto = SignallingNat.Observe(new IPEndPoint(IPAddress.Any, 5060), "203.0.113.45", 61234);

        Assert.Null(visto.BehindNat);
        Assert.NotNull(visto.SeenAs);
    }

    [Theory]
    [InlineData("61234", 61234)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("porta", 0)]
    [InlineData("70000", 0)]
    public void IlParametroRportPuoMancareOEssereVuoto(string? valore, int porta)
        => Assert.Equal(porta, SignallingNat.ParsePort(valore));
}
