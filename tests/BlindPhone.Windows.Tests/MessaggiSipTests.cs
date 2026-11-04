using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Speech;
using BlindPhone.Telephony;
using SIPSorcery.SIP;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica i messaggi istantanei sul protocollo vero: un secondo trasporto SIPSorcery
/// in locale fa da centralino, e da telefono del collega.
/// </summary>
/// <remarks>
/// <para>
/// Le ipotesi da verificare sono sulla libreria e sul protocollo, ed è per questo che
/// la prova passa da un trasporto vero e non da un motore finto: che un <c>MESSAGE</c>
/// sfidato con un <c>401</c> riparta firmato, che la lunghezza del corpo si conti in
/// byte, che un messaggio ritrasmesso riceva la risposta senza comparire due volte.
/// </para>
/// <para>
/// Non sostituisce la prova su un centralino reale, e non la dichiara fatta.
/// </para>
/// </remarks>
public sealed class MessaggiSipTests : IAsyncDisposable
{
    private static readonly TimeSpan Attesa = TimeSpan.FromSeconds(10);

    private readonly SIPTransport _telefono = new();
    private readonly SIPTransport _centralino = new();
    private readonly SIPUDPChannel _canaleTelefono;
    private readonly SIPUDPChannel _canaleCentralino;

    /// <summary>Richieste MESSAGE arrivate al centralino, nell'ordine.</summary>
    private readonly ConcurrentQueue<SIPRequest> _arrivate = new();

    public MessaggiSipTests()
    {
        _canaleTelefono = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0));
        _canaleCentralino = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0));

        _telefono.AddSIPChannel(_canaleTelefono);
        _centralino.AddSIPChannel(_canaleCentralino);
    }

    private SipAccountOptions Account() => new()
    {
        Name = "Comune",
        Username = "200",
        Password = "segreta",
        DisplayName = "Centralino",
        Domain = $"127.0.0.1:{_canaleCentralino.Port}",
    };

    /// <summary>Il centralino risponde a ogni MESSAGE con la risposta che decide la prova.</summary>
    private void CentralinoRisponde(Func<SIPRequest, SIPResponse> risposta)
    {
        _centralino.SIPTransportRequestReceived += async (_, _, richiesta) =>
        {
            if (richiesta.Method != SIPMethodsEnum.MESSAGE) return;

            _arrivate.Enqueue(richiesta);
            await _centralino.SendResponseAsync(risposta(richiesta));
        };
    }

    // -----------------------------------------------------------------------
    // Invio
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnMessaggioAccettatoRisultaConsegnato()
    {
        CentralinoRisponde(r => SIPResponse.GetResponse(r, SIPResponseStatusCodesEnum.Ok, null));
        using var canale = new SipInstantMessaging(_telefono, Account());

        var esito = await canale.SendAsync("201", "Ti passo una chiamata", CancellationToken.None);

        Assert.True(esito.Delivered);

        var arrivata = Assert.Single(_arrivate);
        Assert.Equal("201", arrivata.URI.User);
        Assert.Equal("Ti passo una chiamata", arrivata.Body);
        Assert.StartsWith("text/plain", arrivata.Header.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("200", arrivata.Header.From.FromURI.User);
        Assert.Equal("Centralino", arrivata.Header.From.FromName);
    }

    [Fact]
    public async Task UnaSfidaDiAutenticazioneFaRipartireIlMessaggioFirmato()
    {
        CentralinoRisponde(r =>
        {
            if (r.Header.AuthenticationHeaders.Count > 0)
            {
                return SIPResponse.GetResponse(r, SIPResponseStatusCodesEnum.Ok, null);
            }

            var sfida = SIPResponse.GetResponse(r, SIPResponseStatusCodesEnum.Unauthorised, null);
            sfida.Header.AuthenticationHeaders.Add(SIPAuthenticationHeader.ParseSIPAuthenticationHeader(
                SIPAuthorisationHeadersEnum.WWWAuthenticate,
                "Digest realm=\"centralino\", nonce=\"8a1f2c\", algorithm=MD5"));

            return sfida;
        });

        using var canale = new SipInstantMessaging(_telefono, Account());

        var esito = await canale.SendAsync("201", "Firmato", CancellationToken.None);

        Assert.True(esito.Delivered);

        var richieste = _arrivate.ToArray();
        Assert.Equal(2, richieste.Length);
        Assert.Empty(richieste[0].Header.AuthenticationHeaders);
        Assert.NotEmpty(richieste[1].Header.AuthenticationHeaders);

        // La seconda e' una richiesta nuova dello stesso dialogo, non una ritrasmissione.
        Assert.Equal(richieste[0].Header.CallId, richieste[1].Header.CallId);
        Assert.True(richieste[1].Header.CSeq > richieste[0].Header.CSeq);
        Assert.Equal("Firmato", richieste[1].Body);
    }

    [Theory]
    [InlineData(SIPResponseStatusCodesEnum.NotFound, "l'interno non esiste")]
    [InlineData(SIPResponseStatusCodesEnum.TemporarilyUnavailable, "il collega non è raggiungibile")]
    [InlineData(SIPResponseStatusCodesEnum.MethodNotAllowed, "il centralino non inoltra i messaggi")]
    public async Task UnRifiutoTornaComeMotivoInItaliano(SIPResponseStatusCodesEnum codice, string motivo)
    {
        CentralinoRisponde(r => SIPResponse.GetResponse(r, codice, null));
        using var canale = new SipInstantMessaging(_telefono, Account());

        var esito = await canale.SendAsync("999", "Pronto?", CancellationToken.None);

        Assert.False(esito.Delivered);
        Assert.Equal(motivo, esito.FailureReason);
    }

    [Fact]
    public async Task SulFiloLaLunghezzaDelCorpoSiContaInByte()
    {
        // Con una lettera accentata caratteri e byte divergono: «perché» sono sei
        // caratteri e sette byte. Un Content-Length sbagliato fa leggere al centralino un
        // corpo troncato, o vuoto. Si guarda il datagramma come arriva, con un socket
        // grezzo al posto del centralino: il parser di SIPSorcery e' tollerante, e su un
        // trasporto SIPSorcery dall'altra parte un errore qui non si vedrebbe.
        using var grezzo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var porta = ((IPEndPoint)grezzo.Client.LocalEndPoint!).Port;

        var account = Account() with { Domain = $"127.0.0.1:{porta}" };
        using var canale = new SipInstantMessaging(_telefono, account);
        const string testo = "Perché non rispondi? Città, così.";

        using var stop = new CancellationTokenSource();
        var invio = canale.SendAsync("201", testo, stop.Token);

        var ricezione = grezzo.ReceiveAsync();
        Assert.Same(ricezione, await Task.WhenAny(ricezione, Task.Delay(Attesa)));

        var pacchetto = Encoding.UTF8.GetString((await ricezione).Buffer);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invio);

        var separatore = pacchetto.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var intestazioni = pacchetto[..separatore].Split("\r\n");
        var corpo = pacchetto[(separatore + 4)..];

        var byteAttesi = Encoding.UTF8.GetByteCount(testo);
        Assert.True(byteAttesi > testo.Length);

        Assert.StartsWith("MESSAGE sip:201@", intestazioni[0], StringComparison.Ordinal);
        Assert.Contains($"Content-Length: {byteAttesi}", intestazioni);
        Assert.Contains(intestazioni, r => r.StartsWith("Content-Type: text/plain", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(testo, corpo);
    }

    // -----------------------------------------------------------------------
    // Ricezione
    // -----------------------------------------------------------------------

    /// <summary>Il centralino consegna un MESSAGE al telefono e aspetta la risposta.</summary>
    private async Task<SIPResponse?> ConsegnaAsync(
        string corpo,
        string? tipo = "text/plain;charset=UTF-8",
        string? callId = null,
        int cseq = 1,
        string? nome = "Mario Rossi")
    {
        var destinazione = SIPURI.ParseSIPURI($"sip:200@127.0.0.1:{_canaleTelefono.Port}");
        var a = new SIPToHeader(null, destinazione.CopyOf(), null);
        var da = new SIPFromHeader(
            nome, SIPURI.ParseSIPURI($"sip:201@127.0.0.1:{_canaleCentralino.Port}"), CallProperties.CreateNewTag());

        var richiesta = SIPRequest.GetRequest(SIPMethodsEnum.MESSAGE, destinazione, a, da);
        richiesta.Header.CSeq = cseq;
        if (callId is not null) richiesta.Header.CallId = callId;
        if (tipo is not null) richiesta.Header.ContentType = tipo;
        richiesta.Body = corpo;

        var esito = new TaskCompletionSource<SIPResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transazione = new SIPNonInviteTransaction(_centralino, richiesta, null);

        transazione.NonInviteTransactionFinalResponseReceived += (_, _, _, risposta) =>
        {
            esito.TrySetResult(risposta);
            return Task.FromResult(SocketError.Success);
        };
        transazione.NonInviteTransactionFailed += (_, _) => esito.TrySetResult(null);

        transazione.SendRequest();

        var finita = await Task.WhenAny(esito.Task, Task.Delay(Attesa));
        return finita == esito.Task ? await esito.Task : null;
    }

    [Fact]
    public async Task UnMessaggioInArrivoVieneConfermatoEConsegnato()
    {
        using var canale = new SipInstantMessaging(_telefono, Account());

        var ricevuto = new TaskCompletionSource<InstantMessageReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        canale.MessageReceived += (_, e) => ricevuto.TrySetResult(e);

        var risposta = await ConsegnaAsync("Sei libero? È urgente");

        Assert.NotNull(risposta);
        Assert.Equal(SIPResponseStatusCodesEnum.Ok, risposta.Status);

        var finita = await Task.WhenAny(ricevuto.Task, Task.Delay(Attesa));
        Assert.Same(ricevuto.Task, finita);

        var messaggio = await ricevuto.Task;
        Assert.Equal("201", messaggio.From);
        Assert.Equal("Mario Rossi", messaggio.FromName);
        Assert.Equal("Sei libero? È urgente", messaggio.Text);
    }

    [Fact]
    public async Task UnaRitrasmissioneRiceveLaRispostaMaNonSiMostraDueVolte()
    {
        using var canale = new SipInstantMessaging(_telefono, Account());

        var ricevuti = 0;
        canale.MessageReceived += (_, _) => Interlocked.Increment(ref ricevuti);

        // Stesso Call-ID e stessa sequenza: e' la stessa richiesta, ripetuta perche' il
        // mittente non ha visto la prima risposta.
        var callId = CallProperties.CreateNewCallId();
        var prima = await ConsegnaAsync("Una volta sola", callId: callId, cseq: 7);
        var seconda = await ConsegnaAsync("Una volta sola", callId: callId, cseq: 7);

        Assert.Equal(SIPResponseStatusCodesEnum.Ok, prima?.Status);
        Assert.Equal(SIPResponseStatusCodesEnum.Ok, seconda?.Status);
        Assert.Equal(1, Volatile.Read(ref ricevuti));
    }

    [Fact]
    public async Task UnContenutoCheNonSiSaMostrareSiRifiutaConIlCodicePrevisto()
    {
        using var canale = new SipInstantMessaging(_telefono, Account());

        var ricevuti = 0;
        canale.MessageReceived += (_, _) => Interlocked.Increment(ref ricevuti);

        var risposta = await ConsegnaAsync("<html>ciao</html>", tipo: "text/html");

        Assert.Equal(SIPResponseStatusCodesEnum.UnsupportedMediaType, risposta?.Status);
        Assert.Equal(0, Volatile.Read(ref ricevuti));
    }

    [Fact]
    public async Task LIndicazioneStaScrivendoSiAccettaESiLasciaCadere()
    {
        using var canale = new SipInstantMessaging(_telefono, Account());

        var ricevuti = 0;
        canale.MessageReceived += (_, _) => Interlocked.Increment(ref ricevuti);

        var risposta = await ConsegnaAsync(
            "<?xml version=\"1.0\"?><isComposing><state>active</state></isComposing>",
            tipo: "application/im-iscomposing+xml");

        Assert.Equal(SIPResponseStatusCodesEnum.Ok, risposta?.Status);
        Assert.Equal(0, Volatile.Read(ref ricevuti));
    }

    // -----------------------------------------------------------------------
    // Lettura del corpo
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("text/plain", "Ciao", "Ciao")]
    [InlineData("Text/Plain; charset=UTF-8", "Ciao\r\n", "Ciao")]
    [InlineData(null, "Senza tipo", "Senza tipo")]
    [InlineData("text/html", "<b>no</b>", null)]
    [InlineData("application/im-iscomposing+xml", "<isComposing/>", null)]
    [InlineData("text/plain", "   ", null)]
    public void IlTestoSiPrendeSoloDaiContenutiDiTesto(string? tipo, string corpo, string? atteso)
        => Assert.Equal(atteso, SipInstantMessaging.ExtractText(tipo, corpo));

    [Fact]
    public void DallaBustaCpimSiPrendeIlSoloTesto()
    {
        const string corpo =
            "From: <sip:201@comune.it>\r\n"
            + "To: <sip:200@comune.it>\r\n"
            + "DateTime: 2026-11-04T10:42:00Z\r\n"
            + "\r\n"
            + "Content-Type: text/plain; charset=UTF-8\r\n"
            + "\r\n"
            + "Ti passo una chiamata";

        Assert.Equal("Ti passo una chiamata", SipInstantMessaging.ExtractText("message/cpim", corpo));
    }

    [Fact]
    public async Task IlMotoreSimulatoRispondeDaCollegaDichiarato()
    {
        var motore = new FakeTelephonyEngine { SimulatedReplyDelay = TimeSpan.Zero };
        var ricevuto = new TaskCompletionSource<InstantMessageReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        motore.InstantMessageReceived += (_, e) => ricevuto.TrySetResult(e);

        var esito = await motore.SendInstantMessageAsync("201", "Prova", CancellationToken.None);

        Assert.True(esito.Delivered);
        Assert.Same(ricevuto.Task, await Task.WhenAny(ricevuto.Task, Task.Delay(Attesa)));

        var risposta = await ricevuto.Task;
        Assert.Equal("201", risposta.From);
        Assert.Contains("simulata", risposta.Text, StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        _telefono.Shutdown();
        _centralino.Shutdown();
        return ValueTask.CompletedTask;
    }
}
