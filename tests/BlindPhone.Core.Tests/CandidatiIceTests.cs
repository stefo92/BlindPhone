using System.Net;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Quali candidati ICE la postazione annuncia, con quale priorità, e chi sceglie la coppia.
/// </summary>
/// <remarks>
/// <para>
/// Il caso di riferimento è la postazione da casa con una VPN parziale: una scheda verso
/// il router di casa, una verso il tunnel, e magari le schede virtuali di VMware. Il
/// centralino si raggiunge solo dal tunnel. Senza ICE la postazione deve indovinare
/// quale indirizzo scrivere; con ICE li annuncia tutti e le verifiche trovano quello
/// buono, purché quello verso il centralino sia il primo e nessuno sia inservibile.
/// </para>
/// <para>
/// Gli indirizzi pubblici sono quelli riservati alla documentazione (RFC 5737).
/// </para>
/// </remarks>
public sealed class CandidatiIceTests
{
    private static readonly IPAddress Casa = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress Tunnel = IPAddress.Parse("10.8.0.6");
    private static readonly IPAddress VmWare = IPAddress.Parse("192.168.247.1");
    private static readonly IPAddress SenzaDhcp = IPAddress.Parse("169.254.110.185");

    private static readonly IPAddress[] Schede = [Casa, Tunnel, VmWare, SenzaDhcp, IPAddress.Loopback];

    [Fact]
    public void LIndirizzoVersoIlCentralinoEIlPrimoConLaPrioritaPiuAlta()
    {
        var candidati = IceCandidatePlan.Build(Schede, Tunnel, 40000, null);

        Assert.Equal(Tunnel, candidati[0].EndPoint.Address);
        Assert.All(candidati.Skip(1), c => Assert.True(c.Priority < candidati[0].Priority));
        Assert.Equal(candidati.OrderByDescending(c => c.Priority), candidati);
    }

    [Fact]
    public void SiAnnuncianoTutteLeSchedeTranneQuelleInservibili()
    {
        var indirizzi = IceCandidatePlan.Build(Schede, Tunnel, 40000, null)
            .Select(c => c.EndPoint.Address)
            .ToList();

        Assert.Equal([Tunnel, Casa, VmWare], indirizzi);
    }

    [Fact]
    public void TuttiICandidatiPortanoLaPortaDelSocketAudio()
    {
        Assert.All(IceCandidatePlan.Build(Schede, Tunnel, 40000, null), c => Assert.Equal(40000, c.EndPoint.Port));
    }

    [Fact]
    public void LIndirizzoPreferitoCompareAncheSeNonEFraLeSchedeElencate()
    {
        // L'elenco delle schede si legge dal sistema, e un tunnel appena salito puo'
        // mancarci: il candidato predefinito deve esserci comunque, o il centralino
        // abbandona ICE per ice-mismatch.
        var candidati = IceCandidatePlan.Build([Casa], Tunnel, 40000, null);

        Assert.Equal(Tunnel, candidati[0].EndPoint.Address);
        Assert.Contains(candidati, c => c.EndPoint.Address.Equals(Casa));
    }

    [Fact]
    public void SiAnnunciaUnaFamigliaSolaQuellaDelCandidatoPredefinito()
    {
        var globale = IPAddress.Parse("2001:db8::20");
        var collegamento = IPAddress.Parse("fe80::1");

        var quattro = IceCandidatePlan.Build([Casa, globale, collegamento], Casa, 40000, null);
        var sei = IceCandidatePlan.Build([Casa, globale, collegamento], globale, 40000, null);

        Assert.Equal([Casa], quattro.Select(c => c.EndPoint.Address));
        Assert.Equal([globale], sei.Select(c => c.EndPoint.Address));
    }

    [Fact]
    public void LaPrioritaSegueLaFormulaDellaRfc()
    {
        // RFC 8445 §5.1.2.1: 2^24 x tipo + 2^8 x preferenza locale + (256 - componente).
        Assert.Equal((126u << 24) + (65535u << 8) + 255u, IceCandidatePlan.Priority(IceCandidateKind.Host, 65535));
        Assert.Equal((100u << 24) + (65535u << 8) + 255u, IceCandidatePlan.Priority(IceCandidateKind.ServerReflexive, 65535));
        Assert.Equal(2_130_706_431u, IceCandidatePlan.Priority(IceCandidateKind.Host, 65535));
    }

    [Fact]
    public void LIndirizzoPubblicoDiventaUnCandidatoRiflessoLegatoAllaSchedaPreferita()
    {
        var pubblico = new IPEndPoint(IPAddress.Parse("203.0.113.45"), 51000);

        var candidati = IceCandidatePlan.Build([Casa], Casa, 40000, pubblico);
        var riflesso = Assert.Single(candidati, c => c.Kind == IceCandidateKind.ServerReflexive);

        Assert.Equal(pubblico, riflesso.EndPoint);
        Assert.Equal(new IPEndPoint(Casa, 40000), riflesso.Related);
        Assert.True(riflesso.Priority < candidati[0].Priority);
        Assert.NotEqual(candidati[0].Foundation, riflesso.Foundation);
    }

    [Fact]
    public void UnIndirizzoPubblicoUgualeAUnoLocaleNonEUnCandidatoInPiu()
    {
        var senzaNat = new IPEndPoint(Casa, 40000);

        var candidati = IceCandidatePlan.Build([Casa], Casa, 40000, senzaNat);

        Assert.Single(candidati);
    }

    [Fact]
    public void IFondamentiSonoDiversiPerOgniScheda()
    {
        var candidati = IceCandidatePlan.Build(Schede, Tunnel, 40000, null);

        Assert.Equal(candidati.Count, candidati.Select(c => c.Foundation).Distinct().Count());
    }

    [Fact]
    public void LaRigaDelCandidatoHostSegueLaGrammaticaDellaRfc8839()
    {
        var candidato = IceCandidatePlan.Build([Tunnel], Tunnel, 40000, null)[0];

        Assert.Equal("H1 1 udp 2130706431 10.8.0.6 40000 typ host", candidato.ToSdpValue());
    }

    [Fact]
    public void LaRigaDelCandidatoRiflessoPortaLIndirizzoDOrigine()
    {
        var pubblico = new IPEndPoint(IPAddress.Parse("203.0.113.45"), 51000);
        var riflesso = IceCandidatePlan.Build([Casa], Casa, 40000, pubblico)[1];

        Assert.Equal(
            "S1 1 udp 1694498815 203.0.113.45 51000 typ srflx raddr 192.168.1.20 rport 40000",
            riflesso.ToSdpValue());
    }

    [Fact]
    public void GliIndirizziIpv4ScrittiComeIpv6SiRiportanoAllaLoroForma()
    {
        var mappato = IPAddress.Parse("::ffff:10.8.0.6");

        var candidato = IceCandidatePlan.Build([mappato], mappato, 40000, null)[0];

        Assert.Equal(Tunnel, candidato.EndPoint.Address);
        Assert.Contains(" 10.8.0.6 ", candidato.ToSdpValue(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("169.254.3.4", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::1", false)]
    [InlineData("10.8.0.6", true)]
    [InlineData("100.64.1.2", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("2001:db8::20", true)]
    public void IndirizziUtilizzabiliComeCandidatoHost(string indirizzo, bool utilizzabile)
    {
        Assert.Equal(utilizzabile, IceCandidatePlan.IsUsableHostAddress(IPAddress.Parse(indirizzo)));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ControllaChiOffrePerPrimoOppureSempreConUnCentralinoLite(bool offerta, bool lite, bool controlla)
    {
        // RFC 8445 §6.1.1. Il caso che non deve sbagliare e' il terzo: chiamata entrante da
        // un centralino lite. Se la postazione restasse controllata, nessuno sceglierebbe la
        // coppia e la chiamata resterebbe muta con le verifiche riuscite.
        Assert.Equal(controlla, IceRoles.IsControlling(offerta, lite));
    }

    [Theory]
    [InlineData("v=0\r\nm=audio 4000 RTP/AVP 8\r\na=ice-ufrag:abcd\r\na=ice-pwd:x\r\n", true)]
    [InlineData("v=0\na=ICE-UFRAG:abcd\n", true)]
    [InlineData("v=0\r\nm=audio 4000 RTP/AVP 8\r\na=sendrecv\r\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UnOffertaDichiaraIceSePortaLeCredenziali(string? corpo, bool dichiara)
    {
        Assert.Equal(dichiara, IceSdp.DeclaresIce(corpo));
    }

    [Fact]
    public void LoStatoConnessoDiceDoveVaLAudioEChiSceglie()
    {
        var stato = new IceStatus(
            IceProgress.Connected,
            Controlling: true,
            RemoteCandidates: 2,
            Remote: new IPEndPoint(IPAddress.Parse("10.20.0.5"), 12000),
            RemoteKind: IceRemoteCandidateKind.Host,
            RemoteIsLite: true);

        var testo = stato.Describe();

        Assert.Contains("10.20.0.5 porta 12000", testo, StringComparison.Ordinal);
        Assert.Contains("la postazione sceglie la coppia", testo, StringComparison.Ordinal);
        Assert.Contains("ICE lite", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LoStatoFallitoDiceChePassaPerLIndirizzoDellaDescrizione()
    {
        var testo = new IceStatus(IceProgress.Failed, RemoteCandidates: 1).Describe();

        Assert.Contains("un candidato del centralino", testo, StringComparison.Ordinal);
        Assert.Contains("descrizione di sessione", testo, StringComparison.Ordinal);
    }
}
