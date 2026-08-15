using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Pbx.Yeastar;

/// <summary>
/// Identificativi degli eventi dell'Open API Yeastar rilevanti per il posto
/// operatore.
/// </summary>
/// <remarks>
/// Sottoscrivere solo ciò che serve non è un'ottimizzazione: ogni evento
/// sottoscritto è un evento da interpretare, e un evento interpretato male produce
/// un annuncio sbagliato, che è il difetto peggiore in un'interfaccia che
/// l'operatore non può verificare a schermo.
/// </remarks>
public static class YeastarEventIds
{
    /// <summary>Cambio di stato della registrazione di un interno.</summary>
    public const int ExtensionRegistrationChanged = 30007;

    /// <summary>Cambio di stato chiamata riferito a un interno specifico.</summary>
    /// <remarks>
    /// È l'evento preferenziale per la correlazione: essendo già filtrato
    /// sull'interno, il numero di chiamate candidate è minimo.
    /// </remarks>
    public const int ExtensionCallStateChanged = 30008;

    /// <summary>Cambio di presenza di un interno.</summary>
    /// <remarks>
    /// Realizza il pannello presenza senza dover implementare le sottoscrizioni SIP
    /// BLF: è l'informazione che permette di sapere se un interno è libero prima di
    /// trasferirgli una chiamata.
    /// </remarks>
    public const int ExtensionPresenceChanged = 30009;

    /// <summary>Cambio di stato di registrazione di una linea esterna.</summary>
    public const int TrunkRegistrationChanged = 30010;

    /// <summary>Cambio di stato di una chiamata, con l'elenco dei partecipanti.</summary>
    public const int CallStateChanged = 30011;

    /// <summary>Dettagli di fine chiamata, corrispondenti al record di tariffazione.</summary>
    public const int CallEndDetails = 30012;

    /// <summary>Esito di un trasferimento.</summary>
    public const int CallTransferReport = 30013;

    /// <summary>Esito di una deviazione di chiamata.</summary>
    public const int CallForwardingReport = 30014;

    /// <summary>Segnalazione di chiamata fallita, con il motivo.</summary>
    public const int CallFailureReport = 30015;

    /// <summary>Richiesta di chiamata entrante, anticipa lo squillo.</summary>
    public const int IncomingCallRequest = 30016;

    /// <summary>
    /// Pausa automatica di un agente imposta dal centralino per chiamate perse.
    /// </summary>
    /// <remarks>
    /// Il più importante degli eventi di coda dal punto di vista dell'accessibilità.
    /// Un operatore vedente se ne accorge dal pannello; uno non vedente smetterebbe
    /// di ricevere chiamate senza capire perché, restando seduto in silenzio a turno
    /// aperto. Per questo viene annunciato con priorità critica.
    /// </remarks>
    public const int AgentAutomaticPause = 30025;

    /// <summary>Chiamata persa da un agente per scadenza del tempo di squillo.</summary>
    public const int AgentRingingTimeout = 30026;

    /// <summary>Cambio di stato di un agente in coda.</summary>
    public const int AgentStatusChanged = 30029;

    /// <summary>Insieme degli eventi sottoscritti da BlindPhone.</summary>
    public static IReadOnlyList<int> Subscribed { get; } =
    [
        ExtensionRegistrationChanged,
        ExtensionCallStateChanged,
        ExtensionPresenceChanged,
        TrunkRegistrationChanged,
        CallStateChanged,
        CallEndDetails,
        CallTransferReport,
        CallForwardingReport,
        CallFailureReport,
        IncomingCallRequest,
        AgentAutomaticPause,
        AgentRingingTimeout,
        AgentStatusChanged,
    ];
}

/// <summary>Un evento ricevuto dal centralino.</summary>
/// <param name="Type">Identificativo dell'evento, vedi <see cref="YeastarEventIds"/>.</param>
/// <param name="Payload">Corpo dell'evento, ancora in forma di documento JSON.</param>
/// <param name="ReceivedAt">Istante di ricezione.</param>
/// <remarks>
/// Il corpo resta un <see cref="JsonElement"/> anziché essere deserializzato in un
/// tipo: la forma esatta varia per tipo di evento e, soprattutto, può cambiare fra
/// versioni di firmware. Un campo aggiunto o rinominato da Yeastar deve degradare
/// in un dato mancante, non in un'eccezione che interrompe il flusso di tutti gli
/// altri eventi.
/// </remarks>
public sealed record YeastarEvent(int Type, JsonElement Payload, DateTimeOffset ReceivedAt);

/// <summary>
/// Collegamento WebSocket agli eventi del centralino, con heartbeat e
/// riconnessione automatica.
/// </summary>
/// <remarks>
/// <para>
/// Due vincoli documentati da Yeastar governano l'implementazione:
/// </para>
/// <list type="number">
///   <item><description>
///   la connessione viene chiusa dopo <b>60 secondi di inattività</b>, quindi serve
///   un heartbeat periodico;
///   </description></item>
///   <item><description>
///   gli eventi <b>non vengono recapitati in differita</b>: quelli avvenuti mentre
///   la connessione era caduta sono persi.
///   </description></item>
/// </list>
/// <para>
/// Il secondo vincolo è quello che conta di più per l'accessibilità, e da solo
/// giustifica il modello ibrido del provider: alla riconnessione lo stato va
/// riallineato interrogando il centralino, altrimenti l'operatore continua a
/// sentire un conteggio di coda che non corrisponde più alla realtà.
/// </para>
/// </remarks>
public sealed class YeastarEventStream : IAsyncDisposable
{
    private readonly YeastarOptions _options;
    private readonly YeastarApiClient _api;
    private readonly ILogger<YeastarEventStream> _log;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;

    /// <summary>Crea il flusso eventi.</summary>
    /// <param name="options">Parametri di collegamento.</param>
    /// <param name="api">Client REST, usato per ottenere il token.</param>
    /// <param name="logger">Log diagnostico.</param>
    public YeastarEventStream(
        YeastarOptions options,
        YeastarApiClient api,
        ILogger<YeastarEventStream>? logger = null)
    {
        _options = options;
        _api = api;
        _log = logger ?? NullLogger<YeastarEventStream>.Instance;
    }

    /// <summary>Sollevato per ogni evento ricevuto.</summary>
    public event EventHandler<YeastarEvent>? EventReceived;

    /// <summary>Sollevato quando il collegamento si stabilisce o cade.</summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>Indica se il collegamento è attivo.</summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>Avvia il collegamento e i cicli di ricezione e heartbeat.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => RunReceiveLoopAsync(_lifetime.Token), CancellationToken.None);
        _heartbeatLoop = Task.Run(() => RunHeartbeatLoopAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Chiude il collegamento in modo ordinato.</summary>
    public async Task StopAsync()
    {
        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_socket is { State: WebSocketState.Open })
        {
            try
            {
                await _socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "chiusura", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Chiusura del WebSocket non riuscita");
            }
        }

        await WaitForLoopAsync(_receiveLoop).ConfigureAwait(false);
        await WaitForLoopAsync(_heartbeatLoop).ConfigureAwait(false);
    }

    private static async Task WaitForLoopAsync(Task? loop)
    {
        if (loop is null) return;
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Chiusura richiesta: e' il percorso normale.
        }
    }

    /// <summary>
    /// Ciclo di ricezione con riconnessione a ritardo crescente.
    /// </summary>
    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var reconnectDelay = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(cancellationToken).ConfigureAwait(false);

                // Riuscita la connessione, il ritardo torna al minimo: un guasto
                // isolato non deve lasciare l'attesa alta per il resto del turno.
                reconnectDelay = TimeSpan.FromSeconds(1);
                ConnectionStateChanged?.Invoke(this, true);

                await ReceiveUntilClosedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Collegamento agli eventi del centralino interrotto");
            }

            ConnectionStateChanged?.Invoke(this, false);

            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            reconnectDelay = TimeSpan.FromSeconds(
                Math.Min(reconnectDelay.TotalSeconds * 2, _options.MaxReconnectDelay.TotalSeconds));
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();

        // Stessa politica del percorso REST, e non una copia: il WebSocket si collega
        // alla stessa macchina sulla stessa porta, quindi il certificato e' lo stesso.
        // Con due politiche separate l'operatore si vedrebbe chiedere due volte la
        // stessa cosa a ogni avvio, che e' il modo piu' rapido per insegnargli a
        // rispondere di si' senza ascoltare.
        _socket.Options.RemoteCertificateValidationCallback =
            (_, certificate, _, errors) => _api.ValidateCertificate(certificate, errors);

        var token = await _api.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var uri = new Uri($"{_options.WebSocketUrl}?access_token={Uri.EscapeDataString(token)}");

        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("Collegato agli eventi del centralino");

        await SubscribeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invia il messaggio di sottoscrizione agli eventi.
    /// </summary>
    /// <remarks>
    /// Il formato del messaggio è <c>{"topic_list":[30011,30008,...]}</c>. La
    /// documentazione Yeastar descrive il meccanismo ma il nome esatto del campo va
    /// confermato sul firmware installato: è la prima cosa da verificare quando si
    /// ha accesso a un PBX di prova, perché una sottoscrizione rifiutata si
    /// manifesta come semplice assenza di eventi, che è facile scambiare per una
    /// coda vuota.
    /// </remarks>
    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        if (_socket is null) return;

        var message = JsonSerializer.Serialize(new { topic_list = YeastarEventIds.Subscribed });
        var buffer = Encoding.UTF8.GetBytes(message);

        await _socket
            .SendAsync(buffer, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);

        _log.LogDebug("Sottoscrizione inviata: {Message}", message);
    }

    private async Task ReceiveUntilClosedAsync(CancellationToken cancellationToken)
    {
        if (_socket is null) return;

        var buffer = new byte[16 * 1024];
        var accumulated = new StringBuilder();

        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await _socket
                .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _log.LogInformation("Il centralino ha chiuso il collegamento agli eventi");
                return;
            }

            accumulated.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (!result.EndOfMessage) continue;

            var payload = accumulated.ToString();
            accumulated.Clear();

            DispatchMessage(payload);
        }
    }

    private void DispatchMessage(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement)) return;
            if (!typeElement.TryGetInt32(out var type)) return;

            // Il documento viene clonato perche' JsonDocument viene liberato
            // all'uscita dal blocco, mentre l'evento viaggia verso altri thread.
            var evt = new YeastarEvent(type, root.Clone(), DateTimeOffset.Now);
            EventReceived?.Invoke(this, evt);
        }
        catch (JsonException ex)
        {
            // Un messaggio malformato non deve interrompere il flusso: si registra e
            // si continua. Un firmware che aggiunge un campo non previsto non puo'
            // togliere all'operatore la visibilita' sulla coda.
            _log.LogWarning(ex, "Messaggio dal centralino non interpretabile");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Errore nella distribuzione di un evento del centralino");
        }
    }

    /// <summary>
    /// Invia periodicamente un messaggio per impedire la chiusura per inattività.
    /// </summary>
    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);

                if (_socket is not { State: WebSocketState.Open }) continue;

                var ping = Encoding.UTF8.GetBytes("{\"type\":\"heartbeat\"}");
                await _socket
                    .SendAsync(ping, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Un heartbeat fallito significa collegamento caduto: al ciclo di
                // ricezione il compito di riconnettere.
                _log.LogDebug(ex, "Heartbeat non inviato");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifetime?.Dispose();
        _socket?.Dispose();
    }
}
