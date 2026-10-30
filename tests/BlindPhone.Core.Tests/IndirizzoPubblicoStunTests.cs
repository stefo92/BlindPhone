using System.Buffers.Binary;
using System.Net;
using System.Text;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Lettura delle risposte STUN e del server scritto nelle impostazioni.
/// </summary>
/// <remarks>
/// <para>
/// Le due risposte di riferimento vengono dai messaggi di esempio della RFC 5769, §2.2
/// e §2.3: identificativo di transazione, indirizzo cifrato con lo XOR e porta sono
/// quelli, e quello che devono dare — 192.0.2.1 e 2001:db8:1234:5678:11:2233:4455:6677,
/// porta 32853 — è scritto nella RFC. È il modo più sicuro di sapere che la decifratura
/// è giusta: un errore di un byte nella chiave darebbe un indirizzo plausibile e falso.
/// </para>
/// <para>
/// Dei messaggi della RFC si tengono SOFTWARE e XOR-MAPPED-ADDRESS; integrità e impronta
/// non ci sono, perché la postazione non ha credenziali STUN e non le verifica.
/// </para>
/// </remarks>
public sealed class IndirizzoPubblicoStunTests
{
    private static readonly byte[] TransazioneRfc = Convert.FromHexString("b7e7a701bc34d686fa87dfae");

    private const ushort Successo = 0x0101;
    private const ushort Errore = 0x0111;

    /// <summary>Compone un messaggio STUN con gli attributi dati, già allineati.</summary>
    private static byte[] Messaggio(ushort tipo, byte[] transazione, params (ushort Tipo, byte[] Valore)[] attributi)
    {
        var corpo = new List<byte>();

        foreach (var (t, v) in attributi)
        {
            var testa = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(testa, t);
            BinaryPrimitives.WriteUInt16BigEndian(testa.AsSpan(2), (ushort)v.Length);
            corpo.AddRange(testa);
            corpo.AddRange(v);

            // Riempimento a quattro byte con spazi, come nei messaggi della RFC 5769.
            while (corpo.Count % 4 != 0) corpo.Add(0x20);
        }

        var intestazione = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(intestazione, tipo);
        BinaryPrimitives.WriteUInt16BigEndian(intestazione.AsSpan(2), (ushort)corpo.Count);
        BinaryPrimitives.WriteUInt32BigEndian(intestazione.AsSpan(4), 0x2112A442);
        transazione.CopyTo(intestazione, 8);

        return [.. intestazione, .. corpo];
    }

    private static (ushort, byte[]) Software => (0x8022, Encoding.UTF8.GetBytes("test vector"));

    // -----------------------------------------------------------------------
    // Risposte
    // -----------------------------------------------------------------------

    [Fact]
    public void LaRispostaIPv4DellaRfc5769DaIlSuoIndirizzo()
    {
        var risposta = Messaggio(
            Successo, TransazioneRfc, Software, (0x0020, Convert.FromHexString("0001a147e112a643")));

        var esito = StunBinding.Parse(risposta, TransazioneRfc);

        Assert.Equal(StunOutcome.Mapped, esito.Outcome);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 32853), esito.Mapped);
    }

    [Fact]
    public void LaRispostaIPv6DellaRfc5769DaIlSuoIndirizzo()
    {
        // Per IPv6 la chiave dello XOR comprende l'identificativo di transazione: e' la
        // parte che una implementazione frettolosa dimentica.
        var risposta = Messaggio(
            Successo,
            TransazioneRfc,
            Software,
            (0x0020, Convert.FromHexString("0002a1470113a9faa5d3f179bc25f4b5bed2b9d9")));

        var esito = StunBinding.Parse(risposta, TransazioneRfc);

        Assert.Equal(StunOutcome.Mapped, esito.Outcome);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("2001:db8:1234:5678:11:2233:4455:6677"), 32853), esito.Mapped);
    }

    [Fact]
    public void UnaRispostaDiUnAltraTransazioneNonVienePresaPerBuona()
    {
        // Sul socket audio arrivano anche i messaggi STUN di chi sta all'altro capo: la
        // libreria SIP prendeva il primo che capitava.
        var risposta = Messaggio(
            Successo, TransazioneRfc, (0x0020, Convert.FromHexString("0001a147e112a643")));

        var esito = StunBinding.Parse(risposta, StunBinding.NewTransactionId());

        Assert.Equal(StunOutcome.OtherTransaction, esito.Outcome);
        Assert.Null(esito.Mapped);
    }

    [Fact]
    public void LIndirizzoCifratoVinceSuQuelloInChiaro()
    {
        // Un router con ALG riscrive l'indirizzo in chiaro con quello privato: e' la
        // ragione per cui la RFC 5389 ha introdotto quello cifrato.
        var risposta = Messaggio(
            Successo,
            TransazioneRfc,
            (0x0001, [0x00, 0x01, 0x80, 0x55, 192, 168, 1, 20]),
            (0x0020, Convert.FromHexString("0001a147e112a643")));

        var esito = StunBinding.Parse(risposta, TransazioneRfc);

        Assert.Equal(IPAddress.Parse("192.0.2.1"), esito.Mapped!.Address);
    }

    [Fact]
    public void UnServerDellaVecchiaRfc3489RispondeConLIndirizzoInChiaro()
    {
        var risposta = Messaggio(Successo, TransazioneRfc, (0x0001, [0x00, 0x01, 0x9C, 0x40, 203, 0, 113, 7]));

        var esito = StunBinding.Parse(risposta, TransazioneRfc);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000), esito.Mapped);
    }

    [Fact]
    public void UnaRispostaDiErroreDiceIlCodiceEIlMotivo()
    {
        var valore = new byte[] { 0, 0, 4, 20 }.Concat(Encoding.UTF8.GetBytes("Unknown Attribute")).ToArray();

        var esito = StunBinding.Parse(Messaggio(Errore, TransazioneRfc, (0x0009, valore)), TransazioneRfc);

        Assert.Equal(StunOutcome.ServerError, esito.Outcome);
        Assert.Contains("420", esito.Detail, StringComparison.Ordinal);
        Assert.Contains("errore", esito.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnaRispostaSenzaIndirizzoLoDice()
    {
        var esito = StunBinding.Parse(Messaggio(Successo, TransazioneRfc, Software), TransazioneRfc);

        Assert.Equal(StunOutcome.NoAddress, esito.Outcome);
    }

    [Fact]
    public void UnPacchettoAudioNonVienePresoPerStun()
    {
        // Versione 2 nei due bit alti: e' l'inizio di ogni pacchetto RTP, ed e' cosi'
        // che sullo stesso socket i due protocolli si distinguono.
        var rtp = new byte[172];
        rtp[0] = 0x80;
        rtp[1] = 0x08;

        Assert.Equal(StunOutcome.Malformed, StunBinding.Parse(rtp, TransazioneRfc).Outcome);
    }

    [Fact]
    public void UnPacchettoTroncoNonSiLegge()
        => Assert.Equal(
            StunOutcome.Malformed,
            StunBinding.Parse(Convert.FromHexString("0101003c2112a442"), TransazioneRfc).Outcome);

    [Fact]
    public void UnAttributoTroncoNonFaLeggereOltreIlPacchetto()
    {
        var risposta = Messaggio(Successo, TransazioneRfc, (0x0020, Convert.FromHexString("0001a147e112a643")));

        // Si dichiara l'attributo piu' lungo di quello che il messaggio contiene.
        BinaryPrimitives.WriteUInt16BigEndian(risposta.AsSpan(22), 40);

        Assert.Equal(StunOutcome.Malformed, StunBinding.Parse(risposta, TransazioneRfc).Outcome);
    }

    [Fact]
    public void LaRichiestaEUnaBindingDiVentiByteConLaTransazioneData()
    {
        var transazione = StunBinding.NewTransactionId();
        var richiesta = StunBinding.CreateRequest(transazione);

        Assert.Equal(20, richiesta.Length);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(richiesta));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(richiesta.AsSpan(2)));
        Assert.Equal(0x2112A442u, BinaryPrimitives.ReadUInt32BigEndian(richiesta.AsSpan(4)));
        Assert.Equal(transazione, richiesta[8..]);
    }

    [Fact]
    public void OgniRichiestaHaUnaTransazioneNuova()
        => Assert.NotEqual(StunBinding.NewTransactionId(), StunBinding.NewTransactionId());

    // -----------------------------------------------------------------------
    // Server scritto nelle impostazioni
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("stun.ente.it", "stun.ente.it", 3478)]
    [InlineData("  stun.ente.it:3479 ", "stun.ente.it", 3479)]
    [InlineData("stun:stun.ente.it", "stun.ente.it", 3478)]
    [InlineData("STUN:stun.ente.it:19302", "stun.ente.it", 19302)]
    [InlineData("192.0.2.10", "192.0.2.10", 3478)]
    [InlineData("[2001:db8::1]:3478", "2001:db8::1", 3478)]
    [InlineData("2001:db8::1", "2001:db8::1", 3478)]
    public void IlServerSiScriveNeiModiCheCircolano(string testo, string host, int porta)
    {
        Assert.True(StunServerAddress.TryParse(testo, out var server, out var errore), errore);
        Assert.Equal(new StunServerAddress(host, porta), server);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnCampoVuotoSignificaNessunServer(string? testo)
    {
        Assert.True(StunServerAddress.TryParse(testo, out var server, out var errore));
        Assert.Null(server);
        Assert.Equal(string.Empty, errore);
    }

    [Theory]
    [InlineData("turn:turn.ente.it", "turn")]
    [InlineData("stuns:stun.ente.it", "stuns")]
    [InlineData("https://stun.ente.it", "nome o un indirizzo")]
    [InlineData("stun.ente.it:porta", "porta")]
    [InlineData("stun.ente.it:70000", "porta")]
    [InlineData("stun ente it", "nome o un indirizzo")]
    public void UnServerScrittoMaleVieneRifiutatoDicendoPerche(string testo, string frammento)
    {
        Assert.False(StunServerAddress.TryParse(testo, out var server, out var errore));
        Assert.Null(server);
        Assert.Contains(frammento, errore, StringComparison.Ordinal);
    }

    [Fact]
    public void LaPortaStandardNonSiDice()
    {
        Assert.Equal("stun.ente.it", new StunServerAddress("stun.ente.it", 3478).ToString());
        Assert.Equal("stun.ente.it:3479", new StunServerAddress("stun.ente.it", 3479).ToString());
    }
}
