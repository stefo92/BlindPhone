using System.Net;
using BlindPhone.Telephony;
using SIPSorcery.SIP;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la risoluzione del proxy in uscita via DNS SRV (RFC 3263), che serve alle
/// linee d'operatore come TIM: il proxy non e' un indirizzo ma un nome di servizio.
/// </summary>
public sealed class RisoluzioneProxySrvTests
{
    private sealed class DnsFinto : IDnsLookup
    {
        public Dictionary<string, SrvTarget[]> Srv { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IPAddress[]> Indirizzi { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DomandeSrv { get; } = [];

        public Task<IReadOnlyList<SrvTarget>> QuerySrvAsync(string name, CancellationToken cancellationToken)
        {
            DomandeSrv.Add(name);
            return Task.FromResult<IReadOnlyList<SrvTarget>>(Srv.GetValueOrDefault(name) ?? []);
        }

        public Task<IReadOnlyList<IPAddress>> QueryAddressesAsync(string host, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IPAddress>>(Indirizzi.GetValueOrDefault(host) ?? []);
    }

    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public async Task UnIndirizzoIpNonInterrogaIlDns()
    {
        var dns = new DnsFinto();
        var r = new SipServerResolver(dns);

        var esito = await r.ResolveAsync("10.1.2.3", SipTransportKind.Udp, CancellationToken.None);

        Assert.Single(esito);
        Assert.Equal(new IPEndPoint(Ip("10.1.2.3"), 5060), esito[0].GetIPEndPoint());
        Assert.Empty(dns.DomandeSrv);
    }

    [Fact]
    public async Task UnNomeConPortaNonCercaIlRecordSrv()
    {
        var dns = new DnsFinto();
        dns.Indirizzi["proxy.tim.it"] = [Ip("192.0.2.10")];

        var esito = await new SipServerResolver(dns)
            .ResolveAsync("proxy.tim.it:5070", SipTransportKind.Udp, CancellationToken.None);

        Assert.Equal(new IPEndPoint(Ip("192.0.2.10"), 5070), Assert.Single(esito).GetIPEndPoint());
        Assert.Empty(dns.DomandeSrv);
    }

    [Theory]
    [InlineData(SipTransportKind.Udp, "_sip._udp.proxy.tim.it")]
    [InlineData(SipTransportKind.Tcp, "_sip._tcp.proxy.tim.it")]
    [InlineData(SipTransportKind.Tls, "_sips._tcp.proxy.tim.it")]
    public async Task IlNomeDelServizioSegueIlTrasporto(SipTransportKind trasporto, string atteso)
    {
        var dns = new DnsFinto();

        await new SipServerResolver(dns).ResolveAsync("proxy.tim.it", trasporto, CancellationToken.None);

        Assert.Equal(atteso, Assert.Single(dns.DomandeSrv));
    }

    [Fact]
    public async Task IRecordSrvSiProvanoPerPrioritaConPortaPropria()
    {
        var dns = new DnsFinto();
        dns.Srv["_sip._udp.proxy.tim.it"] =
        [
            new SrvTarget("riserva.tim.it", 5080, Priority: 20, Weight: 10),
            new SrvTarget("primario.tim.it", 5062, Priority: 10, Weight: 10),
        ];
        dns.Indirizzi["primario.tim.it"] = [Ip("192.0.2.1")];
        dns.Indirizzi["riserva.tim.it"] = [Ip("192.0.2.2")];

        var esito = await new SipServerResolver(dns)
            .ResolveAsync("proxy.tim.it", SipTransportKind.Udp, CancellationToken.None);

        Assert.Equal(
            [new IPEndPoint(Ip("192.0.2.1"), 5062), new IPEndPoint(Ip("192.0.2.2"), 5080)],
            esito.Select(e => e.GetIPEndPoint()));
    }

    [Fact]
    public async Task SenzaRecordSrvSiRicadeSulNomeConPortaPredefinita()
    {
        var dns = new DnsFinto();
        dns.Indirizzi["proxy.tim.it"] = [Ip("192.0.2.9")];

        var udp = await new SipServerResolver(dns)
            .ResolveAsync("proxy.tim.it", SipTransportKind.Udp, CancellationToken.None);
        var tls = await new SipServerResolver(dns)
            .ResolveAsync("proxy.tim.it", SipTransportKind.Tls, CancellationToken.None);

        Assert.Equal(5060, Assert.Single(udp).Port);
        Assert.Equal(5061, Assert.Single(tls).Port);
        Assert.Equal(SIPProtocolsEnum.tls, tls[0].Protocol);
    }

    [Fact]
    public async Task UnNomeCheNonSiRisolveDaElencoVuoto()
    {
        var esito = await new SipServerResolver(new DnsFinto())
            .ResolveAsync("inesistente.example", SipTransportKind.Udp, CancellationToken.None);

        Assert.Empty(esito);
    }

    [Fact]
    public async Task IlParametroTransportDellIndirizzoPrevaleSuQuelloDellAccount()
    {
        var dns = new DnsFinto();

        await new SipServerResolver(dns)
            .ResolveAsync("sip:proxy.tim.it;transport=tcp", SipTransportKind.Udp, CancellationToken.None);

        Assert.Equal("_sip._tcp.proxy.tim.it", Assert.Single(dns.DomandeSrv));
    }

    [Fact]
    public void UnTargetPuntoSignificaServizioNonOffertoEVieneScartato()
    {
        var ordinati = SipServerResolver.Order(
            [new SrvTarget(".", 0, 1, 1), new SrvTarget("a.it", 5060, 1, 1)], new Random(1));

        Assert.Equal("a.it", Assert.Single(ordinati).Host);
    }

    [Fact]
    public void ARiParitaDiPrioritaOgniRecordResta()
    {
        var ordinati = SipServerResolver.Order(
            [new SrvTarget("a", 1, 1, 0), new SrvTarget("b", 1, 1, 100), new SrvTarget("c", 1, 1, 5)],
            new Random(42));

        Assert.Equal(["a", "b", "c"], ordinati.Select(t => t.Host).Order());
    }
}
