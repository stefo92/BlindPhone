using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BlindPhone.Core.Domain;
using BlindPhone.Telephony;
using SIPSorcery.SIP;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la segreteria telefonica sul protocollo vero, con un centralino finto in
/// locale.
/// </summary>
/// <remarks>
/// <para>
/// Il centralino qui è un secondo trasporto SIPSorcery sulla stessa macchina: riceve la
/// sottoscrizione, risponde come risponderebbe un impianto, e manda le notifiche — sia
/// quelle della sottoscrizione sia quelle che nessuno ha chiesto. È il modo di
/// collaudare il comportamento della <b>libreria</b>, che è proprio la parte su cui
/// questa funzione fa delle ipotesi: che ignori senza rispondere le notifiche non
/// sollecitate, che confermi da sé quelle della sottoscrizione, che non legga la durata
/// concessa.
/// </para>
/// <para>
/// Non sostituisce la prova su un centralino reale, e non la dichiara fatta.
/// </para>
/// </remarks>
public sealed class SegreteriaSipTests : IAsyncDisposable
{
    private static readonly TimeSpan Attesa = TimeSpan.FromSeconds(10);

    private readonly SIPTransport _telefono = new();
    private readonly SIPTransport _centralino = new();
    private readonly SIPUDPChannel _canaleTelefono;
    private readonly SIPUDPChannel _canaleCentralino;

    /// <summary>Risposte arrivate al centralino, per contare le conferme.</summary>
    private readonly ConcurrentQueue<SIPResponse> _risposteAlCentralino = new();

    public SegreteriaSipTests()
    {
        _canaleTelefono = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0));
        _canaleCentralino = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0));

        _telefono.AddSIPChannel(_canaleTelefono);
        _centralino.AddSIPChannel(_canaleCentralino);

        _centralino.SIPResponseInTraceEvent += (_, _, risposta) => _risposteAlCentralino.Enqueue(risposta);
    }

    private SipAccountOptions Account() => new()
    {
        Name = "Comune",
        Username = "200",
        Password = "segreta",
        Domain = $"127.0.0.1:{_canaleCentralino.Port}",
    };

    private SIPURI Telefono() => SIPURI.ParseSIPURI($"sip:200@127.0.0.1:{_canaleTelefono.Port}");

    /// <summary>Manda una notifica dal centralino e aspetta la risposta del telefono.</summary>
    private async Task<SIPResponse?> NotifyAsync(
        SIPURI destinazione,
        string corpo,
        string? callId = null,
        string? fromTag = null,
        string? toTag = null,
        string? statoSottoscrizione = null,
        int cseq = 1,
        TimeSpan? entro = null)
    {
        var a = new SIPToHeader(null, destinazione.CopyOf(), toTag);
        var da = new SIPFromHeader(
            null, SIPURI.ParseSIPURI($"sip:centralino@127.0.0.1:{_canaleCentralino.Port}"),
            fromTag ?? CallProperties.CreateNewTag());

        var richiesta = SIPRequest.GetRequest(SIPMethodsEnum.NOTIFY, destinazione, a, da);
        richiesta.Header.Event = "message-summary";
        richiesta.Header.ContentType = "application/simple-message-summary";
        richiesta.Header.CSeq = cseq;
        if (callId is not null) richiesta.Header.CallId = callId;
        if (statoSottoscrizione is not null) richiesta.Header.SubscriptionState = statoSottoscrizione;
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

        var finita = await Task.WhenAny(esito.Task, Task.Delay(entro ?? Attesa));
        return finita == esito.Task ? await esito.Task : null;
    }

    private static async Task<T?> AspettaAsync<T>(TaskCompletionSource<T> tcs)
    {
        var finita = await Task.WhenAny(tcs.Task, Task.Delay(Attesa));
        return finita == tcs.Task ? await tcs.Task : default;
    }

    // -----------------------------------------------------------------------
    // Notifiche non sollecitate
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnaNotificaNonSollecitataVieneConfermataELetta()
    {
        using var segreteria = new SipMessageWaitingSource(_telefono, Account());

        var letto = new TaskCompletionSource<MessageSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        segreteria.SummaryReceived += (_, stato) => letto.TrySetResult(stato);

        // Nessuna sottoscrizione: e' il caso dei centralini che mandano lo stato della
        // casella a ogni interno registrato. Senza conferma il centralino ritrasmette.
        var risposta = await NotifyAsync(Telefono(), "Messages-Waiting: yes\r\nVoice-Message: 2/1 (0/0)\r\n");

        Assert.NotNull(risposta);
        Assert.Equal(SIPResponseStatusCodesEnum.Ok, risposta.Status);

        var stato = await AspettaAsync(letto);
        Assert.NotNull(stato);
        Assert.Equal(2, stato.NewMessages);
        Assert.Equal(1, stato.OldMessages);
        Assert.Contains("senza sottoscrizione", segreteria.Status);
    }

    [Fact]
    public async Task UnaNotificaDiUnAltroPacchettoNonVieneToccata()
    {
        using var segreteria = new SipMessageWaitingSource(_telefono, Account());

        var letta = false;
        segreteria.SummaryReceived += (_, _) => letta = true;

        var a = new SIPToHeader(null, Telefono(), null);
        var da = new SIPFromHeader(null, SIPURI.ParseSIPURI("sip:centralino@127.0.0.1"), CallProperties.CreateNewTag());
        var richiesta = SIPRequest.GetRequest(SIPMethodsEnum.NOTIFY, Telefono(), a, da);
        richiesta.Header.Event = "dialog";
        richiesta.Body = "Messages-Waiting: yes";

        await _centralino.SendRequestAsync(richiesta);
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Le notifiche degli altri pacchetti hanno i loro destinatari: rispondere qui
        // vorrebbe dire confermare al posto di chi deve leggerle.
        Assert.False(letta);
        Assert.DoesNotContain(_risposteAlCentralino, r => r.Header.CallId == richiesta.Header.CallId);
    }

    [Fact]
    public async Task UnCorpoIllegibileSiConfermaMaNonProduceStato()
    {
        using var segreteria = new SipMessageWaitingSource(_telefono, Account());

        var letta = false;
        segreteria.SummaryReceived += (_, _) => letta = true;

        var risposta = await NotifyAsync(Telefono(), "questo non e' un riepilogo");

        Assert.Equal(SIPResponseStatusCodesEnum.Ok, risposta?.Status);
        Assert.False(letta);
    }

    // -----------------------------------------------------------------------
    // Sottoscrizione
    // -----------------------------------------------------------------------

    /// <summary>Fa rispondere il centralino ai SUBSCRIBE con il codice e la durata dati.</summary>
    private ConcurrentQueue<(SIPRequest Richiesta, SIPEndPoint Da)> RispondiAiSubscribe(
        SIPResponseStatusCodesEnum codice,
        long? durata,
        string etichetta)
    {
        var ricevuti = new ConcurrentQueue<(SIPRequest, SIPEndPoint)>();

        _centralino.SIPTransportRequestReceived += async (_, remoto, richiesta) =>
        {
            if (richiesta.Method != SIPMethodsEnum.SUBSCRIBE) return;

            ricevuti.Enqueue((richiesta, remoto));

            var risposta = SIPResponse.GetResponse(richiesta, codice, null);
            risposta.Header.To.ToTag = etichetta;
            if (durata is { } secondi) risposta.Header.Expires = secondi;

            await _centralino.SendResponseAsync(risposta);
        };

        return ricevuti;
    }

    private static async Task<bool> FinoAAsync(Func<bool> condizione, TimeSpan? entro = null)
    {
        var limite = DateTime.UtcNow + (entro ?? Attesa);

        while (DateTime.UtcNow < limite)
        {
            if (condizione()) return true;
            await Task.Delay(50);
        }

        return condizione();
    }

    [Fact]
    public async Task LaSottoscrizioneVaAllaPropriaCasellaConIlPacchettoGiusto()
    {
        var ricevuti = RispondiAiSubscribe(SIPResponseStatusCodesEnum.Ok, 600, "pbx1");
        using var segreteria = new SipMessageWaitingSource(_telefono, Account());

        segreteria.Start();

        Assert.True(await FinoAAsync(() => !ricevuti.IsEmpty), "Il SUBSCRIBE non e' arrivato al centralino");

        ricevuti.TryPeek(out var primo);
        Assert.Equal("200", primo.Richiesta.URI.User);
        Assert.Equal("message-summary", primo.Richiesta.Header.Event);
        Assert.True(await FinoAAsync(() => segreteria.Status.Contains("accettata")), segreteria.Status);
    }

    [Fact]
    public async Task LaNotificaDellaSottoscrizioneSiConfermaUnaVoltaSola()
    {
        var ricevuti = RispondiAiSubscribe(SIPResponseStatusCodesEnum.Ok, 600, "pbx2");
        using var segreteria = new SipMessageWaitingSource(_telefono, Account());

        var letti = new ConcurrentQueue<MessageSummary>();
        segreteria.SummaryReceived += (_, stato) => letti.Enqueue(stato);

        segreteria.Start();
        Assert.True(await FinoAAsync(() => !ricevuti.IsEmpty));
        ricevuti.TryPeek(out var sottoscrizione);

        var indietro = SIPURI.ParseSIPURI($"sip:200@{sottoscrizione.Da.Address}:{sottoscrizione.Da.Port}");

        var risposta = await NotifyAsync(
            indietro,
            "Messages-Waiting: yes\r\nMessage-Account: sip:*97@centralino\r\nVoice-Message: 3/0\r\n",
            callId: sottoscrizione.Richiesta.Header.CallId,
            fromTag: "pbx2",
            toTag: sottoscrizione.Richiesta.Header.From.FromTag,
            statoSottoscrizione: "active;expires=600");

        Assert.Equal(SIPResponseStatusCodesEnum.Ok, risposta?.Status);
        Assert.True(await FinoAAsync(() => !letti.IsEmpty));
        letti.TryPeek(out var stato);
        Assert.Equal(3, stato!.NewMessages);
        Assert.Equal("sip:*97@centralino", stato.MessageAccount);

        // La libreria conferma da se' la notifica della propria sottoscrizione: una
        // seconda conferma da parte nostra sarebbe un doppione sulla rete.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var conferme = _risposteAlCentralino.Count(r =>
            r.Header.CSeqMethod == SIPMethodsEnum.NOTIFY && r.Header.CallId == sottoscrizione.Richiesta.Header.CallId);
        Assert.Equal(1, conferme);
    }

    [Fact]
    public async Task UnRifiutoDelPacchettoFermaLaSottoscrizioneMaNonGliAvvisi()
    {
        var ricevuti = RispondiAiSubscribe(SIPResponseStatusCodesEnum.BadEvent, null, "pbx3");
        using var segreteria = new SipMessageWaitingSource(_telefono, Account());

        var letto = new TaskCompletionSource<MessageSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        segreteria.SummaryReceived += (_, stato) => letto.TrySetResult(stato);

        segreteria.Start();

        Assert.True(await FinoAAsync(() => segreteria.GaveUp), segreteria.Status);
        Assert.Contains("489", segreteria.Status);

        // Il caso piu' comune del rifiuto: il centralino gli avvisi li manda da se'.
        await NotifyAsync(Telefono(), "Messages-Waiting: yes\r\nVoice-Message: 1/0\r\n");
        Assert.NotNull(await AspettaAsync(letto));

        // E una volta rinunciato non si torna a chiedere a ogni registrazione.
        var tentativi = ricevuti.Count(r => r.Richiesta.Header.Expires != 0);
        segreteria.Start();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(tentativi, ricevuti.Count(r => r.Richiesta.Header.Expires != 0));
    }

    [Fact]
    public async Task UnaChiusuraDefinitivaDelCentralinoNonSiRiapre()
    {
        var ricevuti = RispondiAiSubscribe(SIPResponseStatusCodesEnum.Ok, 600, "pbx4");
        using var segreteria = new SipMessageWaitingSource(_telefono, Account());

        segreteria.Start();
        Assert.True(await FinoAAsync(() => !ricevuti.IsEmpty));
        ricevuti.TryPeek(out var sottoscrizione);

        var indietro = SIPURI.ParseSIPURI($"sip:200@{sottoscrizione.Da.Address}:{sottoscrizione.Da.Port}");

        await NotifyAsync(
            indietro,
            "Messages-Waiting: no\r\n",
            callId: sottoscrizione.Richiesta.Header.CallId,
            fromTag: "pbx4",
            toTag: sottoscrizione.Richiesta.Header.From.FromTag,
            statoSottoscrizione: "terminated;reason=noresource");

        Assert.True(await FinoAAsync(() => segreteria.GaveUp), segreteria.Status);
    }

    [Fact]
    public async Task SeIlCentralinoConcedeMenoTempoIlRinnovoArrivaPrimaDellaScadenza()
    {
        // Tre secondi concessi a fronte dei seicento chiesti: il rinnovo ordinario della
        // libreria arriverebbe dopo quasi dieci minuti, cioe' con la casella muta per
        // tutto quel tempo. Quello anticipato deve arrivare entro pochi secondi.
        var ricevuti = RispondiAiSubscribe(SIPResponseStatusCodesEnum.Ok, 3, "pbx5");
        using var segreteria = new SipMessageWaitingSource(_telefono, Account());

        segreteria.Start();

        Assert.True(
            await FinoAAsync(() => ricevuti.Count >= 2, TimeSpan.FromSeconds(15)),
            $"SUBSCRIBE ricevuti: {ricevuti.Count}");
    }

    [Fact]
    public async Task ChiudendoSiStaccaDalTrasporto()
    {
        var segreteria = new SipMessageWaitingSource(_telefono, Account());
        var letta = false;
        segreteria.SummaryReceived += (_, _) => letta = true;

        segreteria.Dispose();

        // Nessuno risponde piu': basta aspettare quanto serve a una risposta in locale.
        var risposta = await NotifyAsync(
            Telefono(), "Messages-Waiting: yes\r\nVoice-Message: 1/0\r\n", entro: TimeSpan.FromSeconds(1));

        Assert.Null(risposta);
        Assert.False(letta);
    }

    public ValueTask DisposeAsync()
    {
        _telefono.Shutdown();
        _centralino.Shutdown();
        return ValueTask.CompletedTask;
    }
}
