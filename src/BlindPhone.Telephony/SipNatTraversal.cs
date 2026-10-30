using System.Globalization;
using System.Net;
using System.Net.Sockets;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;

namespace BlindPhone.Telephony;

/// <summary>
/// Interroga il server STUN dal socket audio di una chiamata.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dal socket audio, e non da uno qualunque.</b> L'indirizzo pubblico serve a dire al
/// centralino dove mandare la voce, e il router assegna una porta pubblica diversa a
/// ogni socket: chiedere da un socket a parte darebbe l'indirizzo giusto e la porta di
/// un altro. Per questo l'interrogazione è per chiamata, e non una volta all'avvio.
/// </para>
/// <para>
/// <b>Tre tentativi in un secondo e mezzo.</b> La RFC 5389 §7.2.1 ritrasmette con
/// intervalli che raddoppiano a partire da mezzo secondo, fino a quasi quaranta secondi:
/// pensati per chi non ha fretta. Qui ogni attesa ritarda la partenza della chiamata, e
/// un server che non risponde in un secondo e mezzo non risponderà nemmeno al
/// tentativo successivo; un pacchetto perso sul Wi-Fi di casa invece sì, ed è quello
/// che i tre tentativi coprono. Quando il server risponde, come quasi sempre, il ritardo
/// è quello di un viaggio di andata e ritorno.
/// </para>
/// </remarks>
internal static class StunMediaProbe
{
    /// <summary>Quando mandare la richiesta, contando dall'inizio.</summary>
    internal static readonly TimeSpan[] Attempts =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(900),
    ];

    /// <summary>Oltre questo tempo si rinuncia e la chiamata parte con l'indirizzo della postazione.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Chiede al server l'indirizzo da cui vede arrivare il socket audio.
    /// </summary>
    /// <param name="channel">Canale RTP della chiamata, già in ascolto.</param>
    /// <param name="server">Server STUN già risolto.</param>
    /// <param name="cancellationToken">Annulla l'attesa, per esempio a chiamata chiusa.</param>
    public static async Task<StunBindingResult> QueryAsync(
        RTPChannel channel,
        IPEndPoint server,
        CancellationToken cancellationToken)
    {
        var transazione = StunBinding.NewTransactionId();
        var richiesta = StunBinding.CreateRequest(transazione);
        var esito = new TaskCompletionSource<StunBindingResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStun(STUNMessage message, IPEndPoint remote, bool relayed)
        {
            var letto = StunBinding.Interpret(
                (ushort)message.Header.MessageType,
                message.Header.TransactionId,
                message.Attributes.Select(a => ((ushort)a.AttributeType, a.Value ?? [])),
                transazione);

            // Una risposta che non e' nostra si ignora e si continua ad aspettare: sullo
            // stesso socket arrivano anche i controlli di chi sta all'altro capo.
            if (letto.Outcome != StunOutcome.OtherTransaction) esito.TrySetResult(letto);
        }

        channel.OnStunMessageReceived += OnStun;

        try
        {
            var inizio = Environment.TickCount64;

            foreach (var tentativo in Attempts)
            {
                var attesa = tentativo - TimeSpan.FromMilliseconds(Environment.TickCount64 - inizio);
                if (attesa > TimeSpan.Zero && await Completes(esito.Task, attesa, cancellationToken).ConfigureAwait(false))
                    return await esito.Task.ConfigureAwait(false);

                channel.Send(RTPChannelSocketsEnum.RTP, server, richiesta);
            }

            var resto = Budget - TimeSpan.FromMilliseconds(Environment.TickCount64 - inizio);

            return resto > TimeSpan.Zero && await Completes(esito.Task, resto, cancellationToken).ConfigureAwait(false)
                ? await esito.Task.ConfigureAwait(false)
                : StunBindingResult.NoResponse(
                    $"nessuna risposta in {Budget.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture)} secondi");
        }
        catch (OperationCanceledException)
        {
            return StunBindingResult.NoResponse("interrogazione interrotta");
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return StunBindingResult.NoResponse("il socket audio non ha potuto spedire la richiesta");
        }
        finally
        {
            channel.OnStunMessageReceived -= OnStun;
        }
    }

    private static async Task<bool> Completes(Task task, TimeSpan timeout, CancellationToken cancellationToken)
        => await Task.WhenAny(task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) == task;
}

/// <summary>
/// Risolve il nome del server STUN e ne ricorda l'indirizzo per qualche minuto.
/// </summary>
/// <remarks>
/// Si preferisce IPv4: è la famiglia dei centralini e delle reti degli enti, e un
/// indirizzo pubblico IPv6 annunciato a un centralino IPv4 non servirebbe a nulla. Il
/// ricordo evita una domanda al DNS a ogni chiamata, che su certe linee domestiche costa
/// più dell'interrogazione STUN stessa.
/// </remarks>
internal sealed class StunServerLocator(StunServerAddress server)
{
    private static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(1);

    private IPEndPoint? _endPoint;
    private long _resolvedAt;

    /// <summary>Il server, come configurato.</summary>
    public StunServerAddress Server { get; } = server;

    /// <summary>L'indirizzo del server, oppure <c>null</c> se il nome non si risolve.</summary>
    public async Task<IPEndPoint?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_endPoint is { } noto && Environment.TickCount64 - _resolvedAt < Validity.TotalMilliseconds) return noto;

        if (IPAddress.TryParse(Server.Host, out var diretto))
        {
            _endPoint = new IPEndPoint(diretto, Server.Port);
            _resolvedAt = Environment.TickCount64;
            return _endPoint;
        }

        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limite.CancelAfter(DnsTimeout);

            var indirizzi = await Dns.GetHostAddressesAsync(Server.Host, limite.Token).ConfigureAwait(false);
            var scelto = indirizzi.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                         ?? indirizzi.FirstOrDefault();

            if (scelto is null) return null;

            _endPoint = new IPEndPoint(scelto, Server.Port);
            _resolvedAt = Environment.TickCount64;
            return _endPoint;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // Si torna l'ultimo indirizzo noto anche se scaduto: un DNS che non risponde
            // da casa non vuol dire che il server abbia cambiato indirizzo.
            return _endPoint;
        }
    }
}

/// <summary>
/// Quando mandare il prossimo segnale di mantenimento.
/// </summary>
/// <remarks>
/// <b>Fra l'ottanta e il cento per cento dell'intervallo</b>, come raccomanda la RFC 5626
/// §4.4.1. Con un intervallo fisso tutte le postazioni accese alla stessa ora
/// manderebbero il segnale nello stesso istante, e il centralino lo riceverebbe a ondate.
/// Il tetto resta l'intervallo configurato: chi scrive venticinque secondi vuole che il
/// router non resti mai più di venticinque secondi senza un pacchetto.
/// </remarks>
public static class KeepAliveSchedule
{
    /// <summary>Il segnale: un doppio a capo, RFC 5626 §3.5.1.</summary>
    public static ReadOnlySpan<byte> Payload => "\r\n\r\n"u8;

    /// <summary>
    /// Attesa prima del prossimo segnale.
    /// </summary>
    /// <param name="interval">Intervallo configurato.</param>
    /// <param name="random">Un numero fra zero e uno.</param>
    public static TimeSpan NextDelay(TimeSpan interval, double random)
        => interval * (0.8 + 0.2 * Math.Clamp(random, 0, 1));
}

/// <summary>
/// Tiene aperto, fra una chiamata e l'altra, il passaggio che la registrazione ha aperto
/// nel router.
/// </summary>
/// <remarks>
/// <para>
/// Il segnale va al server che ha accettato l'ultima registrazione, dallo stesso socket
/// da cui la registrazione è partita: è quello il passaggio da cui il centralino
/// manderà la prossima chiamata entrante. Su TCP e TLS lo stesso indirizzo individua la
/// connessione già aperta, e il segnale ci viaggia dentro, cifrato come tutto il resto.
/// </para>
/// <para>
/// Prima della prima registrazione riuscita non c'è niente da tenere aperto, e non parte
/// nulla.
/// </para>
/// </remarks>
internal sealed class SipKeepAlive : IDisposable
{
    private readonly SIPTransport _transport;
    private readonly TimeSpan _interval;
    private readonly Func<(SIPEndPoint? Local, SIPEndPoint? Remote)> _target;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();

    private int _failures;

    public SipKeepAlive(
        SIPTransport transport,
        TimeSpan interval,
        Func<(SIPEndPoint? Local, SIPEndPoint? Remote)> target,
        ILogger log)
    {
        _transport = transport;
        _interval = interval;
        _target = target;
        _log = log;
    }

    /// <summary>Se il segnale è acceso.</summary>
    public bool IsEnabled => _interval > TimeSpan.Zero;

    /// <summary>Ultimo segnale spedito senza errori.</summary>
    public DateTimeOffset? LastSentAt { get; private set; }

    /// <summary>Avvia il ciclo, se l'intervallo non è zero.</summary>
    public void Start()
    {
        if (!IsEnabled) return;

        _log.LogInformation(
            "Segnale di mantenimento verso il centralino ogni {Secondi} secondi",
            (int)_interval.TotalSeconds);

        _ = LoopAsync(_stop.Token);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveSchedule.NextDelay(_interval, Random.Shared.NextDouble()), cancellationToken)
                    .ConfigureAwait(false);

                await SendOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Chiusura del motore: e' l'unica uscita prevista.
        }
    }

    private async Task SendOnceAsync()
    {
        var (local, remote) = _target();
        if (local is null || remote is null) return;

        try
        {
            var esito = await _transport.SendRawAsync(local, remote, KeepAliveSchedule.Payload.ToArray()).ConfigureAwait(false);

            if (esito == SocketError.Success)
            {
                LastSentAt = DateTimeOffset.Now;
                _failures = 0;
                return;
            }

            Fallito(remote, esito.ToString());
        }
        catch (Exception ex)
        {
            // Una connessione TCP chiusa dal centralino fa sollevare qui: la riapre il
            // prossimo rinnovo della registrazione, e il segnale riprende da solo.
            Fallito(remote, ex.Message);
        }
    }

    private void Fallito(SIPEndPoint remote, string motivo)
    {
        // Il primo errore si dice, quelli che seguono no: venticinque secondi di
        // ripetizioni identiche riempirebbero il registro senza aggiungere niente.
        if (_failures++ == 0)
            _log.LogWarning("Segnale di mantenimento verso {Remote} non spedito: {Motivo}", remote, motivo);
        else
            _log.LogDebug("Segnale di mantenimento verso {Remote} non spedito: {Motivo}", remote, motivo);
    }

    /// <summary>Come sta andando, per la scheda Diagnostica.</summary>
    public string Describe()
    {
        if (!IsEnabled) return "spento: il passaggio nel router resta aperto solo durante le chiamate";

        var ogni = $"ogni {((int)_interval.TotalSeconds).ToString(CultureInfo.CurrentCulture)} secondi";

        return LastSentAt is { } ultimo
            ? $"{ogni}, ultimo invio alle {ultimo.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}"
            : $"{ogni}, nessun segnale ancora spedito";
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
