using System.Collections.Concurrent;
using System.Net.Sockets;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.SIP;

namespace BlindPhone.Telephony;

/// <summary>
/// Messaggi istantanei fra interni, con il metodo <c>MESSAGE</c> della RFC 3428.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché questo metodo e non un servizio di chat.</b> È quello che i telefoni SIP
/// usano già fra loro: non chiede un server in più, un account in più o una licenza in
/// più, e passa dallo stesso centralino, con le stesse credenziali e lo stesso trasporto
/// delle chiamate. Se il centralino inoltra i messaggi, un collega con un telefono da
/// tavolo li vede sul suo schermo; se non li inoltra, lo dice con un codice di
/// risposta, e l'operatore lo sente in italiano accanto al messaggio non consegnato.
/// </para>
/// <para>
/// <b>Un messaggio, una transazione.</b> Il <c>MESSAGE</c> vive fuori da qualunque
/// dialogo: si manda, il destinatario risponde, fine. Nessuna sessione da tenere aperta,
/// nessuna sottoscrizione da rinnovare. È lo stesso schema del sondaggio di
/// raggiungibilità (vedi <see cref="SipOptionsProbe"/>), e ne riusa le scelte: una
/// transazione non-INVITE della libreria per le ritrasmissioni, una sfida di
/// autenticazione che non vale come rifiuto.
/// </para>
/// <para>
/// <b>I messaggi in arrivo vanno risposti sempre</b>, e subito. Chi manda un
/// <c>MESSAGE</c> su UDP lo ritrasmette finché non riceve risposta, per mezzo minuto: un
/// messaggio lasciato senza risposta comparirebbe qui undici volte, e al mittente
/// risulterebbe non consegnato. Per questo il <c>200</c> parte prima di qualunque altra
/// cosa, e le ritrasmissioni arrivate prima che il <c>200</c> giunga a destinazione si
/// riconoscono dal Call-ID e dalla sequenza: si risponde di nuovo, ma il messaggio non
/// si mostra una seconda volta.
/// </para>
/// </remarks>
public sealed class SipInstantMessaging : IDisposable
{
    /// <summary>Quanto aspettare la risposta del destinatario prima di dichiarare il messaggio non consegnato.</summary>
    /// <remarks>
    /// La transazione del protocollo dura trentadue secondi, e per un messaggio scritto a
    /// mano sono troppi da passare con «invio in corso» accanto. Quindici bastano a una
    /// rete lenta e restano un tempo che l'operatore aspetta senza ripremere Invio.
    /// </remarks>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Per quanto ricordare un messaggio ricevuto, per riconoscerne le ritrasmissioni.</summary>
    /// <remarks>
    /// Il doppio della durata di una transazione non-INVITE (64 volte T1, trentadue
    /// secondi): oltre, il mittente ha smesso di ritrasmettere da un pezzo.
    /// </remarks>
    private static readonly TimeSpan RetransmissionMemory = TimeSpan.FromSeconds(64);

    /// <summary>Tipo di contenuto con cui si mandano i messaggi.</summary>
    private const string PlainText = "text/plain;charset=UTF-8";

    private readonly SIPTransport _transport;
    private readonly SipAccountOptions _account;
    private readonly IClock _clock;
    private readonly ILogger<SipInstantMessaging> _log;

    /// <summary>Proxy in uscita corrente dell'account: i messaggi passano di lì come le chiamate.</summary>
    private readonly Func<SIPEndPoint?> _outboundProxy;

    /// <summary>Messaggi già consegnati all'operatore, per Call-ID e sequenza, con l'istante di arrivo.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>Crea il canale dei messaggi istantanei.</summary>
    /// <param name="transport">Trasporto SIP già avviato dal motore telefonico.</param>
    /// <param name="account">Credenziali e dominio dell'account.</param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="outboundProxy">Fornisce il proxy in uscita corrente dell'account, se c'è.</param>
    public SipInstantMessaging(
        SIPTransport transport,
        SipAccountOptions account,
        IClock? clock = null,
        ILogger<SipInstantMessaging>? logger = null,
        Func<SIPEndPoint?>? outboundProxy = null)
    {
        _transport = transport;
        _account = account;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<SipInstantMessaging>.Instance;
        _outboundProxy = outboundProxy ?? (() => null);

        _transport.SIPTransportRequestReceived += OnRequestReceived;
    }

    /// <summary>Sollevato una volta per ogni messaggio arrivato, ritrasmissioni escluse.</summary>
    public event EventHandler<InstantMessageReceivedEventArgs>? MessageReceived;

    // -----------------------------------------------------------------------
    // Invio
    // -----------------------------------------------------------------------

    /// <summary>Manda un messaggio e attende la risposta del destinatario.</summary>
    /// <param name="destination">Interno, numero o indirizzo SIP completo.</param>
    /// <param name="text">Testo del messaggio.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>L'esito, con il motivo del rifiuto già tradotto.</returns>
    public async Task<InstantMessageSendResult> SendAsync(
        string destination,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return InstantMessageSendResult.Failure(InstantMessageTexts.NoDestination);
        }

        try
        {
            var richiesta = BuildRequest(destination, text);
            var risposta = await SendAndWaitAsync(richiesta, cancellationToken).ConfigureAwait(false);

            // UNA SFIDA DI AUTENTICAZIONE NON E' UN RIFIUTO: e' il modo normale con cui un
            // centralino accetta un MESSAGE da un interno. Si rimanda una volta firmata,
            // con la sequenza e il ramo nuovi che la libreria assegna da se'; una seconda
            // sfida invece vuol dire credenziali sbagliate, e va detto.
            if (risposta is
                {
                    Status: SIPResponseStatusCodesEnum.Unauthorised
                        or SIPResponseStatusCodesEnum.ProxyAuthenticationRequired,
                    Header.AuthenticationHeaders: { Count: > 0 } sfide,
                })
            {
                var firmata = richiesta.DuplicateAndAuthenticate(
                    sfide,
                    _account.AuthId ?? _account.Username,
                    _account.Password);

                risposta = await SendAndWaitAsync(firmata, cancellationToken).ConfigureAwait(false);
            }

            if (risposta is null)
            {
                _log.LogInformation("Messaggio a {Destinazione}: nessuna risposta entro {Attesa}", destination, SendTimeout);
                return InstantMessageSendResult.Failure(InstantMessageTexts.NoAnswer);
            }

            var codice = (int)risposta.Status;

            _log.LogInformation(
                "Messaggio a {Destinazione}: {Codice} {Motivo}",
                destination, codice, risposta.ReasonPhrase);

            return codice is >= 200 and < 300
                ? InstantMessageSendResult.Success
                : InstantMessageSendResult.Failure(InstantMessageTexts.DeliveryFailure(codice));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un messaggio che non parte non deve far cadere niente: l'esito torna
            // accanto al messaggio, e il resto del telefono continua a lavorare.
            _log.LogWarning(ex, "Invio del messaggio a {Destinazione} non riuscito", destination);
            return InstantMessageSendResult.Failure(InstantMessageTexts.NotSent);
        }
    }

    /// <summary>Compone la richiesta <c>MESSAGE</c>.</summary>
    /// <param name="destination">Interno, numero o indirizzo SIP completo.</param>
    /// <param name="text">Testo del messaggio.</param>
    /// <remarks>
    /// Esposto per le prove: la lunghezza del corpo va contata in byte e non in
    /// caratteri, e un messaggio con una lettera accentata è il caso in cui le due cose
    /// divergono.
    /// </remarks>
    internal SIPRequest BuildRequest(string destination, string text)
    {
        var uri = BuildUri(destination);

        // Il mittente e' l'account di questa postazione, con il nome con cui si presenta
        // nelle chiamate: e' quello che il telefono del collega mostra in testa al
        // messaggio.
        var nome = string.IsNullOrWhiteSpace(_account.DisplayName) ? null : _account.DisplayName;
        var da = new SIPFromHeader(
            nome,
            SIPURI.ParseSIPURIRelaxed($"{_account.Username}@{_account.Domain}"),
            CallProperties.CreateNewTag());

        var a = new SIPToHeader(null, uri.CopyOf(), null);

        var richiesta = SIPRequest.GetRequest(SIPMethodsEnum.MESSAGE, uri, a, da);
        richiesta.Header.ContentType = PlainText;
        richiesta.Body = text;

        return richiesta;
    }

    /// <summary>Manda una richiesta e attende la risposta definitiva, entro <see cref="SendTimeout"/>.</summary>
    private async Task<SIPResponse?> SendAndWaitAsync(SIPRequest richiesta, CancellationToken cancellationToken)
    {
        var attesa = new TaskCompletionSource<SIPResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transazione = new SIPNonInviteTransaction(_transport, richiesta, _outboundProxy());

        transazione.NonInviteTransactionFinalResponseReceived += (_, _, _, risposta) =>
        {
            attesa.TrySetResult(risposta);
            return Task.FromResult(SocketError.Success);
        };

        transazione.NonInviteTransactionFailed += (_, _) => attesa.TrySetResult(null);

        transazione.SendRequest();

        using var scadenza = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var completata = await Task.WhenAny(attesa.Task, Task.Delay(SendTimeout, scadenza.Token))
            .ConfigureAwait(false);

        await scadenza.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return completata == attesa.Task ? await attesa.Task.ConfigureAwait(false) : null;
    }

    /// <summary>Compone l'indirizzo del destinatario.</summary>
    /// <remarks>
    /// Il parametro di trasporto va dichiarato, come per i sondaggi e le sottoscrizioni:
    /// senza, la libreria sceglie UDP in chiaro e il messaggio parte da un canale diverso
    /// da quello su cui l'account è registrato.
    /// </remarks>
    private SIPURI BuildUri(string destination)
    {
        var pulito = destination.Trim();
        if (pulito.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) pulito = pulito[4..];

        return pulito.Contains('@')
            ? SIPURI.ParseSIPURIRelaxed(pulito)
            : SIPURI.ParseSIPURIRelaxed($"{pulito}@{_account.Domain}{_account.TransportParameter}");
    }

    // -----------------------------------------------------------------------
    // Ricezione
    // -----------------------------------------------------------------------

    private async Task OnRequestReceived(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        if (request.Method != SIPMethodsEnum.MESSAGE || _disposed) return;

        var tipo = request.Header.ContentType;
        var testo = ExtractText(tipo, request.Body);

        // UN CONTENUTO CHE NON SAPPIAMO MOSTRARE SI RIFIUTA, e lo si dice al mittente con
        // il codice previsto, cosi' il suo telefono puo' riprovare in testo semplice o
        // avvisare chi scrive. L'indicazione «sta scrivendo» della RFC 3994 invece si
        // accetta e si lascia cadere: e' un'informazione di cortesia, e rifiutarla
        // farebbe credere al telefono del collega che i messaggi non arrivano.
        if (testo is null && !IsComposingIndication(tipo))
        {
            _log.LogInformation(
                "Messaggio da {Da} rifiutato: contenuto {Tipo} non gestito",
                request.Header.From?.FromURI?.User ?? "sconosciuto", tipo ?? "non dichiarato");

            await RespondAsync(request, SIPResponseStatusCodesEnum.UnsupportedMediaType, accept: true)
                .ConfigureAwait(false);

            return;
        }

        await RespondAsync(request, SIPResponseStatusCodesEnum.Ok, accept: false).ConfigureAwait(false);

        if (testo is null) return;

        // Le ritrasmissioni ricevono la risposta come l'originale, ma non si mostrano due
        // volte. La chiave e' quella che la RFC 3261 usa per riconoscere una richiesta
        // ripetuta: Call-ID e numero di sequenza.
        var chiave = $"{request.Header.CallId}|{request.Header.CSeq}";
        var adesso = _clock.Now;

        ForgetOldMessages(adesso);
        if (!_seen.TryAdd(chiave, adesso)) return;

        var numero = request.Header.From?.FromURI?.User ?? string.Empty;
        var nome = CallParty.MeaningfulNameOf(request.Header.From?.FromName, numero);

        _log.LogInformation(
            "Messaggio istantaneo da {Da}, {Lunghezza} caratteri, Call-ID {CallId}",
            numero, testo.Length, request.Header.CallId);

        try
        {
            MessageReceived?.Invoke(this, new InstantMessageReceivedEventArgs(numero, nome, testo, adesso));
        }
        catch (Exception ex)
        {
            // La risposta e' gia' partita: un errore di chi ascolta non deve risalire nel
            // trasporto SIP, che lo tratterebbe come un guasto del canale.
            _log.LogError(ex, "Consegna del messaggio istantaneo all'interfaccia non riuscita");
        }
    }

    /// <summary>Risponde a un <c>MESSAGE</c> ricevuto.</summary>
    private async Task RespondAsync(SIPRequest request, SIPResponseStatusCodesEnum status, bool accept)
    {
        try
        {
            var risposta = SIPResponse.GetResponse(request, status, null);
            if (accept) risposta.Header.Accept = "text/plain, message/cpim";

            await _transport.SendResponseAsync(risposta).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Il mittente ritrasmettera': la prossima risposta ha un'altra occasione.
            _log.LogWarning(ex, "Risposta al messaggio istantaneo non riuscita");
        }
    }

    private void ForgetOldMessages(DateTimeOffset now)
    {
        foreach (var (chiave, arrivo) in _seen)
        {
            if (now - arrivo > RetransmissionMemory) _seen.TryRemove(chiave, out _);
        }
    }

    /// <summary>
    /// Ricava il testo da mostrare dal corpo di un <c>MESSAGE</c>.
    /// </summary>
    /// <param name="contentType">Valore dell'intestazione <c>Content-Type</c>.</param>
    /// <param name="body">Corpo della richiesta.</param>
    /// <returns>Il testo, oppure <c>null</c> se il contenuto non è testo da mostrare.</returns>
    /// <remarks>
    /// <para>
    /// <b>Testo semplice</b>, il caso di tutti i telefoni da tavolo e di quasi tutti i
    /// softphone, e <b>CPIM</b> (RFC 3862), la busta che alcuni centralini mettono
    /// attorno al testo: intestazioni del messaggio, una riga vuota, intestazioni del
    /// contenuto, un'altra riga vuota, il testo.
    /// </para>
    /// <para>
    /// Senza <c>Content-Type</c> il corpo si prende come testo: la RFC lo vorrebbe
    /// sempre dichiarato, ma un messaggio di un apparato poco rigoroso è comunque un
    /// messaggio di un collega, e buttarlo per un'intestazione mancante sarebbe
    /// eccesso di zelo.
    /// </para>
    /// </remarks>
    internal static string? ExtractText(string? contentType, string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;

        var tipo = MediaType(contentType);

        if (tipo.Length == 0 || tipo == "text/plain")
        {
            return Clean(body);
        }

        if (tipo == "message/cpim")
        {
            // Due blocchi di intestazioni, ciascuno chiuso da una riga vuota.
            var normalizzato = body.Replace("\r\n", "\n", StringComparison.Ordinal);
            var primo = normalizzato.IndexOf("\n\n", StringComparison.Ordinal);
            if (primo < 0) return null;

            var secondo = normalizzato.IndexOf("\n\n", primo + 2, StringComparison.Ordinal);
            if (secondo < 0) return null;

            var intestazioni = normalizzato[(primo + 2)..secondo];
            var interno = intestazioni
                .Split('\n')
                .FirstOrDefault(r => r.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));

            if (interno is not null && MediaType(interno["Content-Type:".Length..]) != "text/plain") return null;

            return Clean(normalizzato[(secondo + 2)..]);
        }

        return null;
    }

    /// <summary>Se il contenuto è l'indicazione «sta scrivendo» della RFC 3994.</summary>
    private static bool IsComposingIndication(string? contentType)
        => MediaType(contentType) == "application/im-iscomposing+xml";

    /// <summary>Tipo del contenuto senza parametri, in minuscolo: <c>text/plain</c> da <c>Text/Plain; charset=UTF-8</c>.</summary>
    private static string MediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;

        var separatore = contentType.IndexOf(';');
        var tipo = separatore < 0 ? contentType : contentType[..separatore];

        return tipo.Trim().ToLowerInvariant();
    }

    /// <summary>Toglie gli a capo in coda e i caratteri di controllo che la sintesi leggerebbe per nome.</summary>
    private static string? Clean(string text)
    {
        var pulito = new string(text
            .Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t')
            .ToArray())
            .Trim();

        return pulito.Length == 0 ? null : pulito;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _transport.SIPTransportRequestReceived -= OnRequestReceived;
        _seen.Clear();
    }
}
