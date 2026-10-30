using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace BlindPhone.Telephony;

/// <summary>
/// Stato della segreteria telefonica per via SIP: l'avviso dei messaggi in attesa
/// (MWI, RFC 3842).
/// </summary>
/// <remarks>
/// <para>
/// È la stessa strada che usano i telefoni da tavolo per accendere la spia dei
/// messaggi: il centralino manda un <c>NOTIFY</c> del pacchetto di eventi
/// <c>message-summary</c> ogni volta che la casella cambia, con un corpo di poche righe
/// che dice se ci sono messaggi e quanti. Il corpo lo interpreta
/// <see cref="MessageSummaryParser"/>, nel core; qui si parla soltanto il protocollo.
/// </para>
/// <para>
/// <b>Due strade, e servono tutte e due.</b> La prima è la sottoscrizione: un
/// <c>SUBSCRIBE</c> verso la propria casella, che il centralino accetta e tiene viva
/// finché la postazione la rinnova. La seconda sono le notifiche <b>non sollecitate</b>:
/// molti centralini mandano lo stato della casella a ogni interno registrato senza che
/// nessuno lo chieda, e alcuni rifiutano la sottoscrizione proprio per questo. Una
/// notifica che non appartiene a nessuna sottoscrizione la libreria la ignora senza
/// rispondere, e il centralino la ritrasmette finché non si arrende: per questo la si
/// riconosce e la si conferma qui.
/// </para>
/// <para>
/// <b>Il rinnovo.</b> La sottoscrizione la rinnova da sé la libreria, poco prima della
/// durata <b>chiesta</b>. Se il centralino ne concede una più breve, la risposta lo
/// dice e il rinnovo si anticipa: senza, la casella resterebbe muta per la differenza.
/// Le regole su quanto insistere stanno in <see cref="MessageWaitingSubscriptionPolicy"/>.
/// </para>
/// <para>
/// <b>Nessun errore arriva al telefono.</b> Una sottoscrizione rifiutata, un corpo
/// illeggibile, una risposta non partita finiscono nel registro e basta: la
/// segreteria è un di più, e non deve poter disturbare le chiamate.
/// </para>
/// </remarks>
public sealed class SipMessageWaitingSource : IDisposable
{
    private const string EventPackage = "message-summary";

    private readonly SIPTransport _transport;
    private readonly SipAccountOptions _account;
    private readonly ILogger<SipMessageWaitingSource> _log;

    /// <summary>Proxy in uscita corrente dell'account: le SUBSCRIBE passano di li' come il resto.</summary>
    private readonly Func<SIPEndPoint?> _outboundProxy;

    private readonly Lock _gate = new();

    /// <summary>Riapre la sottoscrizione dopo un errore passeggero o una chiusura del centralino.</summary>
    private readonly Timer _retryTimer;

    /// <summary>Anticipa il rinnovo quando il centralino concede meno tempo di quello chiesto.</summary>
    private readonly Timer _refreshTimer;

    private SIPNotifierClient? _client;
    private bool _accepted;
    private bool _disposed;

    /// <summary>Crea la sorgente e comincia ad ascoltare le notifiche non sollecitate.</summary>
    /// <param name="transport">Trasporto SIP già avviato dal motore telefonico.</param>
    /// <param name="account">Credenziali e dominio dell'account.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="outboundProxy">Fornisce il proxy in uscita corrente dell'account, se c'e'.</param>
    /// <remarks>
    /// L'ascolto parte subito, la sottoscrizione no: vedi <see cref="Start"/>. Un
    /// centralino può mandare lo stato della casella appena riceve la registrazione,
    /// prima ancora di averla confermata, e quella notifica non va persa.
    /// </remarks>
    public SipMessageWaitingSource(
        SIPTransport transport,
        SipAccountOptions account,
        ILogger<SipMessageWaitingSource>? logger = null,
        Func<SIPEndPoint?>? outboundProxy = null)
    {
        _transport = transport;
        _account = account;
        _outboundProxy = outboundProxy ?? (() => null);
        _log = logger ?? NullLogger<SipMessageWaitingSource>.Instance;

        _retryTimer = new Timer(_ => OnRetryDue(), null, Timeout.Infinite, Timeout.Infinite);
        _refreshTimer = new Timer(_ => OnRefreshDue(), null, Timeout.Infinite, Timeout.Infinite);

        _transport.SIPTransportRequestReceived += OnRequestReceived;
        _transport.SIPResponseInTraceEvent += OnResponseReceived;

        _subscriptionStatus = "nessun avviso ricevuto finora";
    }

    /// <summary>Sollevato a ogni notifica leggibile, anche identica alla precedente.</summary>
    public event EventHandler<MessageSummary>? SummaryReceived;

    /// <summary>Descrizione dello stato della sottoscrizione, per la diagnostica.</summary>
    /// <remarks>
    /// Davanti a «la segreteria non mi avvisa» la prima domanda è se il centralino
    /// accetta la sottoscrizione o la rifiuta, e se manda gli avvisi da sé: questa riga
    /// risponde senza dover leggere il registro.
    /// </remarks>
    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _unsolicitedReceived
                    ? $"{_subscriptionStatus}; il centralino manda anche avvisi senza sottoscrizione"
                    : _subscriptionStatus;
            }
        }
    }

    private string _subscriptionStatus;

    /// <summary>Se è arrivata almeno una notifica che nessuna sottoscrizione aveva chiesto.</summary>
    private bool _unsolicitedReceived;

    /// <summary>
    /// Apre la sottoscrizione alla propria casella, se non è già aperta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si chiama a registrazione riuscita, e può essere chiamata a ogni nuova
    /// registrazione: se la sottoscrizione c'è già, o se il centralino ha già detto di
    /// non volerla, non fa niente.
    /// </para>
    /// <para>
    /// Prima della registrazione non avrebbe senso: diversi centralini rifiutano una
    /// sottoscrizione da un interno che non risulta registrato, e quel rifiuto
    /// verrebbe preso per definitivo.
    /// </para>
    /// </remarks>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _client is not null || GaveUp) return;

            Subscribe();
        }
    }

    /// <summary>Se il centralino ha detto di non volere la sottoscrizione, per questa sessione.</summary>
    public bool GaveUp { get; private set; }

    /// <summary>Apre una sottoscrizione nuova. Va chiamato con il lucchetto preso.</summary>
    private void Subscribe()
    {
        try
        {
            // IL TRASPORTO VA DICHIARATO ANCHE QUI, come per lo stato degli interni:
            // senza, la libreria sceglie UDP in chiaro e la richiesta parte da un canale
            // diverso da quello su cui l'account e' registrato. Vedi
            // SipAccountOptions.TransportParameter.
            var casella = SIPURI.ParseSIPURIRelaxed(
                $"{_account.Username}@{_account.Domain}{_account.TransportParameter}");

            var client = new SIPNotifierClient(
                _transport,
                outboundProxy: _outboundProxy(),
                SIPEventPackagesEnum.MessageSummary,
                casella,
                _account.AuthId ?? _account.Username,
                _account.Domain,
                _account.Password,
                MessageWaitingSubscriptionPolicy.RequestedExpirySeconds,
                filter: null);

            client.SubscriptionSuccessful += _ => OnSubscribed(client);
            client.SubscriptionFailed += (_, codice, motivo) => OnRefused(client, codice, motivo);

            _client = client;
            _accepted = false;
            client.Start();

            _log.LogInformation(
                "Sottoscrizione alla segreteria inviata a {Casella}, durata chiesta {Durata} secondi",
                casella, MessageWaitingSubscriptionPolicy.RequestedExpirySeconds);
        }
        catch (Exception ex)
        {
            _client = null;

            _log.LogWarning(ex, "Sottoscrizione alla segreteria non partita: si riprova piu' tardi");
            Schedule(_retryTimer, MessageWaitingSubscriptionPolicy.TransientRetryDelay);
        }
    }

    private void OnSubscribed(SIPNotifierClient client)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_client, client)) return;

            // La libreria la segnala a ogni rinnovo: la riga ordinaria basta la prima
            // volta, le altre restano nella traccia della segnalazione.
            if (_accepted) return;
            _accepted = true;

            _subscriptionStatus = "sottoscrizione accettata dal centralino";
        }

        _log.LogInformation("Sottoscrizione alla segreteria accettata dal centralino");
    }

    /// <summary>
    /// Interpreta un rifiuto della sottoscrizione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Un rifiuto definitivo non spegne la segreteria.</b> Dice soltanto che il
    /// centralino non vuole sottoscrizioni: le notifiche che manda da sé continuano ad
    /// arrivare e a essere lette, ed è anzi la ragione più comune del rifiuto.
    /// </para>
    /// <para>
    /// La chiusura della sottoscrizione rifiutata manda al centralino un'ultima
    /// richiesta con durata zero. È superflua, ma è anche l'unico modo che la libreria
    /// offre per staccare la sottoscrizione dal trasporto: lasciarla attaccata
    /// significherebbe un ascoltatore in più a ogni tentativo, per tutto il turno.
    /// </para>
    /// </remarks>
    private void OnRefused(SIPNotifierClient client, SIPResponseStatusCodesEnum status, string reason)
    {
        var attesa = MessageWaitingSubscriptionPolicy.RetryAfterRefusal((int)status);

        lock (_gate)
        {
            if (!ReferenceEquals(_client, client)) return;

            StopClient(client);
            _client = null;

            if (attesa is { } fra)
            {
                _subscriptionStatus = $"sottoscrizione non riuscita ({(int)status}), nuovo tentativo previsto";
                Schedule(_retryTimer, fra);
            }
            else
            {
                GaveUp = true;
                _subscriptionStatus = $"sottoscrizione rifiutata dal centralino ({(int)status}), non si ritenta";
            }
        }

        if (attesa is { } ritardo)
        {
            _log.LogInformation(
                "Sottoscrizione alla segreteria non riuscita: {Codice} {Motivo}. Errore passeggero, "
                + "si riprova fra {Minuti} minuti",
                (int)status, reason, ritardo.TotalMinutes);
        }
        else
        {
            _log.LogInformation(
                "Sottoscrizione alla segreteria rifiutata dal centralino: {Codice} {Motivo}. Non si "
                + "ritenta in questa sessione; gli avvisi che il centralino manda senza sottoscrizione "
                + "continuano a essere letti",
                (int)status, reason);
        }
    }

    /// <summary>
    /// Riconosce le notifiche della segreteria, comprese quelle che nessuno ha chiesto.
    /// </summary>
    private async Task OnRequestReceived(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        if (request.Method != SIPMethodsEnum.NOTIFY) return;
        if (!IsMessageSummary(request.Header?.Event)) return;

        SIPNotifierClient? client;

        lock (_gate)
        {
            if (_disposed) return;
            client = _client;
        }

        var dellaSottoscrizione = client is not null
            && string.Equals(request.Header!.CallId, client.CallID, StringComparison.Ordinal);

        // CHI CONFERMA LA NOTIFICA.
        //
        // Quella della sottoscrizione la conferma la libreria, e una seconda conferma
        // sarebbe un doppione. Quella non sollecitata non la conferma nessuno: se non
        // lo si fa qui il centralino la ritrasmette per mezzo minuto, e alcuni modelli
        // smettono di mandarle a un interno che non risponde. Fa eccezione la notifica
        // della sottoscrizione senza corpo, che la libreria scarta senza rispondere.
        if (!dellaSottoscrizione || request.Body is null)
        {
            await AnswerAsync(request).ConfigureAwait(false);
        }

        if (dellaSottoscrizione) ReadSubscriptionState(client!, request.Header!.SubscriptionState);

        MessageSummary? stato;

        try
        {
            stato = MessageSummaryParser.Parse(request.Body);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Notifica della segreteria non interpretabile");
            return;
        }

        if (stato is null)
        {
            _log.LogDebug(
                "Notifica della segreteria da {Remote} senza contenuto utilizzabile", remoteEndPoint);
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            if (!dellaSottoscrizione) _unsolicitedReceived = true;
        }

        _log.LogDebug(
            "Notifica della segreteria {Tipo} da {Remote}: spia {Spia}, nuovi {Nuovi}",
            dellaSottoscrizione ? "della sottoscrizione" : "non sollecitata",
            remoteEndPoint,
            stato.MessagesWaiting ? "accesa" : "spenta",
            stato.NewMessages?.ToString() ?? "non indicati");

        try
        {
            SummaryReceived?.Invoke(this, stato);
        }
        catch (Exception ex)
        {
            // Chi ascolta non deve poter far cadere il trasporto SIP.
            _log.LogWarning(ex, "Gestione della notifica della segreteria non riuscita");
        }
    }

    /// <summary>
    /// Riapre la sottoscrizione che il centralino ha chiuso, quando ha senso.
    /// </summary>
    /// <remarks>
    /// La libreria non legge <c>Subscription-State</c>: una sottoscrizione chiusa dal
    /// centralino — per un suo riavvio, per una scadenza — resterebbe chiusa fino al
    /// rinnovo successivo, cioè fino a dieci minuti di segreteria muta.
    /// </remarks>
    private void ReadSubscriptionState(SIPNotifierClient client, string? header)
    {
        var (chiusa, motivo, retryAfter) = MessageWaitingSubscriptionPolicy.ReadSubscriptionState(header);
        if (!chiusa) return;

        var attesa = MessageWaitingSubscriptionPolicy.RetryAfterTermination(motivo, retryAfter);

        lock (_gate)
        {
            if (!ReferenceEquals(_client, client)) return;

            StopClient(client);
            _client = null;

            if (attesa is { } fra)
            {
                _subscriptionStatus = "sottoscrizione chiusa dal centralino, nuovo tentativo previsto";
                Schedule(_retryTimer, fra);
            }
            else
            {
                GaveUp = true;
                _subscriptionStatus = $"sottoscrizione chiusa dal centralino ({motivo}), non si riapre";
            }
        }

        _log.LogInformation(
            "Il centralino ha chiuso la sottoscrizione alla segreteria (motivo {Motivo}). {Esito}",
            motivo ?? "non indicato",
            attesa is { } ritardo
                ? $"Si riapre fra {ritardo.TotalSeconds:0} secondi"
                : "Non si riapre in questa sessione");
    }

    /// <summary>
    /// Legge la durata concessa nella risposta al <c>SUBSCRIBE</c>.
    /// </summary>
    /// <remarks>
    /// Passa dalla traccia del trasporto perché la libreria non la espone: riceve la
    /// risposta, la usa per sapere se la sottoscrizione è riuscita, e la durata la
    /// ignora.
    /// </remarks>
    private void OnResponseReceived(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPResponse response)
    {
        try
        {
            if (response.Header?.CSeqMethod != SIPMethodsEnum.SUBSCRIBE) return;

            var codice = response.StatusCode;
            if (codice is < 200 or >= 300) return;

            SIPNotifierClient? client;

            lock (_gate)
            {
                if (_disposed) return;
                client = _client;
            }

            if (client is null || !string.Equals(response.Header.CallId, client.CallID, StringComparison.Ordinal)) return;

            long concessa = response.Header.Expires;
            int? secondi = concessa is > 0 and <= int.MaxValue ? (int)concessa : null;

            if (MessageWaitingSubscriptionPolicy.EarlyRefresh(
                    MessageWaitingSubscriptionPolicy.RequestedExpirySeconds, secondi) is not { } anticipo)
            {
                return;
            }

            _log.LogInformation(
                "Il centralino concede alla sottoscrizione della segreteria {Concessa} secondi invece di "
                + "{Chiesta}: il rinnovo si anticipa a {Anticipo:0} secondi",
                secondi, MessageWaitingSubscriptionPolicy.RequestedExpirySeconds, anticipo.TotalSeconds);

            Schedule(_refreshTimer, anticipo);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Lettura della durata concessa alla sottoscrizione non riuscita");
        }
    }

    private void OnRetryDue()
    {
        lock (_gate)
        {
            if (_disposed || _client is not null || GaveUp) return;

            Subscribe();
        }
    }

    private void OnRefreshDue()
    {
        SIPNotifierClient? client;

        lock (_gate)
        {
            if (_disposed) return;
            client = _client;
        }

        try
        {
            client?.Resubscribe();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Rinnovo anticipato della sottoscrizione alla segreteria non riuscito");
        }
    }

    private async Task AnswerAsync(SIPRequest request)
    {
        try
        {
            var risposta = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
            await _transport.SendResponseAsync(risposta).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Conferma della notifica della segreteria non partita");
        }
    }

    /// <summary>
    /// Riconosce il pacchetto di eventi, con o senza parametri: <c>message-summary;id=1</c>.
    /// </summary>
    private static bool IsMessageSummary(string? eventHeader)
    {
        if (string.IsNullOrWhiteSpace(eventHeader)) return false;

        var fine = eventHeader.IndexOf(';');
        var nome = (fine < 0 ? eventHeader : eventHeader[..fine]).Trim();

        return nome.Equals(EventPackage, StringComparison.OrdinalIgnoreCase);
    }

    private static void Schedule(Timer timer, TimeSpan delay)
    {
        try
        {
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // La sorgente si sta chiudendo: non c'e' piu' niente da riprovare.
        }
    }

    private void StopClient(SIPNotifierClient client)
    {
        try
        {
            _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
            client.Stop();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Chiusura della sottoscrizione alla segreteria non riuscita");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Chiude la sottoscrizione — la libreria manda la richiesta con durata zero, così
    /// il centralino non tiene in memoria una sottoscrizione che nessuno ascolta — e
    /// smette di ascoltare il trasporto.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_client is { } client) StopClient(client);
            _client = null;
        }

        _transport.SIPTransportRequestReceived -= OnRequestReceived;
        _transport.SIPResponseInTraceEvent -= OnResponseReceived;

        _retryTimer.Dispose();
        _refreshTimer.Dispose();
    }
}
