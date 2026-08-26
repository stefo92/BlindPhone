using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using BlindPhone.Audio;

namespace BlindPhone.Telephony;

/// <summary>
/// Motore telefonico su SIPSorcery.
/// </summary>
/// <remarks>
/// <para>
/// Traduce il protocollo SIP nel vocabolario del dominio e viceversa. Il confine è
/// preciso: da questo punto in giù si parla di INVITE, REFER e codici di risposta;
/// da questo punto in su si parla di chiamate, trasferimenti e motivi comprensibili
/// all'operatore. La traduzione dei codici SIP in <see cref="CallEndReason"/> è la
/// parte che permette di annunciare "interno occupato" invece di "486 Busy Here".
/// </para>
/// <para>
/// <b>Un agente per chiamata.</b> SIPSorcery associa un <see cref="SIPUserAgent"/> a
/// un singolo dialogo, quindi il posto operatore ne mantiene uno per ogni linea
/// attiva. È il modello che serve comunque: durante un trasferimento con annuncio
/// esistono due chiamate contemporanee, e durante l'alternanza l'operatore ne
/// gestisce due che devono restare indipendenti.
/// </para>
/// <para>
/// <b>Stato del percorso media.</b> La segnalazione è completa e collaudabile
/// (registrazione, chiamate, attesa, REFER cieco e con Replaces, DTMF, NOTIFY di
/// esito). Il percorso audio verso i dispositivi WASAPI è affidato a
/// <c>BlindPhone.Audio</c>: il pacchetto <c>SIPSorceryMedia.Windows</c> non è
/// utilizzabile qui perché la sua versione compatibile con SIPSorcery 10 richiede
/// .NET 10, e la versione precedente porta una vulnerabilità nota. Finché
/// l'endpoint proprio non è collegato, la sessione media usa il generatore interno
/// di SIPSorcery: la segnalazione funziona e si collauda, l'audio della
/// conversazione no.
/// </para>
/// </remarks>
public sealed class SipTelephonyEngine : ITelephonyEngine
{
    private readonly SipAccountOptions _account;
    private readonly NAudioEngine? _audioEngine;
    private readonly AudioTuningOptions _audioTuning;

    /// <summary>
    /// Politica di fiducia sui certificati, condivisa con il connettore del centralino.
    /// </summary>
    /// <remarks>
    /// Condivisa di proposito: un operatore che ha già approvato il certificato del
    /// proprio centralino per l'API non deve rispondere una seconda volta quando la
    /// stessa macchina si presenta sulla porta SIP. Sono lo stesso server e lo stesso
    /// certificato, e chiederlo due volte insegnerebbe soltanto a rispondere di sì
    /// senza ascoltare.
    /// </remarks>
    private readonly CertificateTrustService? _certificateTrust;

    private readonly IClock _clock;
    private readonly ILogger<SipTelephonyEngine> _log;
    private readonly ILogger<SipAudioEndPoint> _audioEndPointLogger;
    private readonly ILogger<SipPresenceSource> _presenceLogger;
    private readonly ILogger<SipOptionsProbe> _probeLogger;

    private readonly ConcurrentDictionary<string, CallContext> _calls = new();

    private SIPTransport? _transport;
    private SipPresenceSource? _presenceSource;
    private SipOptionsProbe? _optionsProbe;
    private SIPRegistrationUserAgent? _registration;
    private SipRegistrationState _registrationState;
    private int _callCounter;
    private bool _muted;

    /// <summary>
    /// Se rifiutare gli INVITE in arrivo, perché la postazione è in pausa.
    /// </summary>
    /// <remarks>
    /// <c>volatile</c> perché lo scrive il thread dell'interfaccia, che esegue il
    /// comando dell'operatore, e lo legge il thread del trasporto SIP, che riceve gli
    /// INVITE. Senza, una pausa appena richiesta potrebbe non essere vista dalla
    /// chiamata che sta arrivando in quell'istante.
    /// </remarks>
    private volatile bool _doNotDisturb;

    /// <summary>Ponte audio della conferenza in corso, se ce n'è una.</summary>
    /// <remarks>
    /// Uno solo per volta, e non è un limite dell'implementazione: un posto operatore
    /// gestisce una conferenza alla volta perché ha un solo paio di orecchie.
    /// </remarks>
    private ConferenceBridge? _conference;
    private readonly Lock _conferenceGate = new();

    /// <summary>
    /// Ogni quanto ripetere il <c>180 Ringing</c> su una chiamata entrante in squillo.
    /// </summary>
    /// <remarks>
    /// Il valore sta sotto qualunque cronometro che un proxy possa avere sulla
    /// transazione: vedi <see cref="StartRingingRefresh"/> per il motivo per cui il
    /// silenzio fra una risposta provvisoria e la successiva è ciò che fa annullare la
    /// chiamata.
    /// </remarks>
    private static readonly TimeSpan RingingRefreshInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Toglie il tetto di squillo che SIPSorcery impone alle proprie transazioni.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Quanto squilla la postazione lo decide il centralino, non il softphone.</b>
    /// SIPSorcery fa scadere da sé una transazione INVITE ferma in <c>Proceeding</c>
    /// oltre <c>SIPTimings.MAX_RING_TIME</c>, che di suo vale tre minuti: passati
    /// quelli la chiamata entrante muore qui dentro anche se il centralino la sta
    /// ancora offrendo, e per l'operatore è una chiamata che sparisce da sola. Vale
    /// anche in uscita: una destinazione lasciata squillare a lungo verrebbe
    /// abbandonata dal posto operatore invece che da chi ha chiamato.
    /// </para>
    /// <para>
    /// Un'ora non è "per sempre" di proposito: una transazione che nessuno chiude —
    /// per un <c>CANCEL</c> perso per strada — resterebbe in memoria a vita. Nessun
    /// centralino fa squillare un interno per un'ora, quindi il limite che conta
    /// resta il suo.
    /// </para>
    /// <para>
    /// Va assegnato prima che venga eseguito il costruttore statico di
    /// <c>SIPTransaction</c>, che ne prende una copia: da lì in poi cambiarlo non ha
    /// più effetto. Per questo sta qui, nel primo punto in cui il programma tocca lo
    /// stack SIP.
    /// </para>
    /// </remarks>
    static SipTelephonyEngine()
    {
        SIPTimings.MAX_RING_TIME = (int)TimeSpan.FromHours(1).TotalMilliseconds;
    }

    /// <summary>Crea il motore telefonico.</summary>
    /// <param name="account">Parametri dell'account SIP.</param>
    /// <param name="audioEngine">
    /// Motore audio da cui ricavare i dispositivi della conversazione. Se omesso la
    /// chiamata si instaura ma <b>non passa audio</b>: utile solo per collaudare la
    /// segnalazione.
    /// </param>
    /// <param name="audioTuning">
    /// Parametri di regolazione del buffer di riproduzione. Il valore giusto
    /// dipende dalla rete e va trovato empiricamente: vedi
    /// <see cref="AudioTuningOptions.JitterBufferMilliseconds"/>.
    /// </param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="audioEndPointLogger">Log del percorso audio.</param>
    /// <param name="presenceLogger">Log della sorveglianza degli interni per via SIP.</param>
    /// <param name="probeLogger">Log del sondaggio di raggiungibilita sugli interni.</param>
    /// <param name="certificateTrust">
    /// Politica di fiducia sui certificati, usata solo con trasporto TLS. Se omessa
    /// vale la verifica di sistema e basta: un certificato autofirmato viene
    /// rifiutato senza appello.
    /// </param>
    public SipTelephonyEngine(
        SipAccountOptions account,
        NAudioEngine? audioEngine = null,
        AudioTuningOptions? audioTuning = null,
        IClock? clock = null,
        ILogger<SipTelephonyEngine>? logger = null,
        ILogger<SipAudioEndPoint>? audioEndPointLogger = null,
        ILogger<SipPresenceSource>? presenceLogger = null,
        ILogger<SipOptionsProbe>? probeLogger = null,
        CertificateTrustService? certificateTrust = null)
    {
        _account = account;
        _audioEngine = audioEngine;
        _audioTuning = audioTuning ?? AudioTuningOptions.Default;
        _certificateTrust = certificateTrust;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<SipTelephonyEngine>.Instance;
        _audioEndPointLogger = audioEndPointLogger ?? NullLogger<SipAudioEndPoint>.Instance;
        _presenceLogger = presenceLogger ?? NullLogger<SipPresenceSource>.Instance;
        _probeLogger = probeLogger ?? NullLogger<SipOptionsProbe>.Instance;
        _registrationState = new SipRegistrationState(RegistrationStatus.Unregistered, account.Name);
    }

    /// <inheritdoc />
    public SipRegistrationState RegistrationState => _registrationState;

    /// <inheritdoc />
    public bool IsMuted => _muted;

    /// <inheritdoc />
    public bool IsDoNotDisturb => _doNotDisturb;

    /// <inheritdoc />
    public bool IsSimulated => false;

    /// <summary>
    /// Restituisce l'endpoint audio di una chiamata, per leggerne i contatori RTP.
    /// </summary>
    /// <param name="callId">Identificativo locale della chiamata.</param>
    /// <returns>L'endpoint, oppure <c>null</c> se la chiamata non esiste o non ha percorso audio reale.</returns>
    /// <remarks>
    /// Esposto per la diagnostica. Il rapporto fra pacchetti inviati e ricevuti è
    /// ciò che distingue un problema del softphone da un problema del centralino, e
    /// senza quel dato la ricerca della causa procede per tentativi.
    /// </remarks>
    public SipAudioEndPoint? GetAudioEndPoint(string callId)
        => _calls.TryGetValue(callId, out var context) ? context.AudioEndPoint : null;

    /// <inheritdoc />
    public IPresenceSource? PresenceSource => _presenceSource;

    /// <inheritdoc />
    public IReachabilityProbe? ReachabilityProbe => _optionsProbe;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Si descrive il percorso audio della <b>prima chiamata con audio attivo</b>. Su
    /// un posto operatore le chiamate contemporanee sono al massimo due, una delle
    /// quali in attesa e quindi senza flusso: quella che interessa a chi sta
    /// indagando un disturbo è sempre quella in conversazione.
    /// </para>
    /// <para>
    /// Il controllo di coerenza da fare per primo è fra durata della chiamata e somma
    /// di audio ricevuto e silenzio inserito: se il silenzio inserito è una frazione
    /// consistente della durata, la voce che manca non è mai arrivata al dispositivo,
    /// e il problema sta prima dell'orecchio dell'operatore.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DiagnosticCounter> DiagnosticCounters
    {
        get
        {
            var counters = new List<DiagnosticCounter>
            {
                new("Motore telefonico", "SIP reale (SIPSorcery)"),
                new("Account SIP", _account.Name),
                new("Registrazione", _registrationState.IsReachable ? "registrato" : "non registrato"),

                // La riga sulla segnalazione va letta insieme a quella sui
                // certificati: «cifrata» da sola non dice se si sa con chi.
                new("Segnalazione", DescribeSignalling()),
                new("Chiamate in corso", _calls.Count.ToString(CultureInfo.CurrentCulture)),
            };

            // La conferenza cambia il significato di tutti i contatori che seguono: i
            // pacchetti inviati contengono anche la voce dell'altro partecipante, e
            // "la prima chiamata con audio attivo" ne descrive una su due. Va detto
            // prima, non dopo.
            ConferenceBridge? conference;
            lock (_conferenceGate) conference = _conference;

            if (conference is not null)
            {
                counters.Add(new DiagnosticCounter(
                    "Conferenza", $"aperta su {conference.LegCount} linee, missaggio locale"));
            }

            var endPoint = _calls.Values.Select(c => c.AudioEndPoint).FirstOrDefault(e => e is not null);

            if (endPoint is null)
            {
                counters.Add(new DiagnosticCounter(
                    "Percorso audio", "nessuna chiamata con audio attivo in questo momento"));

                return counters;
            }

            var received = endPoint.RtpFramesReceived;
            var silence = endPoint.RenderSilenceMilliseconds;

            counters.Add(new DiagnosticCounter("Codec negoziato", $"{endPoint.NegotiatedClockRate} Hz"));
            counters.Add(new DiagnosticCounter("Pacchetti inviati", Formatta(endPoint.RtpFramesSent)));
            counters.Add(new DiagnosticCounter("Pacchetti ricevuti", Formatta(received)));
            counters.Add(new DiagnosticCounter("Pacchetti scartati", Formatta(endPoint.RtpFramesDiscarded)));
            counters.Add(new DiagnosticCounter("Svuotamenti del buffer", Formatta(endPoint.RenderUnderruns)));
            counters.Add(new DiagnosticCounter("Silenzio inserito", $"{Formatta(silence)} ms"));
            counters.Add(new DiagnosticCounter("Buffer in questo momento", $"{endPoint.BufferedMilliseconds} ms"));
            counters.Add(new DiagnosticCounter("Cuscinetto configurato", $"{endPoint.JitterBufferMilliseconds} ms"));

            if (endPoint.IsInConference)
            {
                // Il secondo numero e' quello che conta: sono i pacchetti partiti con
                // la sola voce dell'operatore perche' dall'altra gamba non era arrivato
                // nulla da mettergli dentro. Se cresce con lo stesso ritmo degli
                // inviati, il partecipante che questa gamba serve non sente l'altro.
                counters.Add(new DiagnosticCounter(
                    "Pacchetti con missaggio di conferenza", Formatta(endPoint.ConferenceFramesMixed)));

                counters.Add(new DiagnosticCounter(
                    "Pacchetti senza contributo dell'altra linea", Formatta(endPoint.ConferenceFramesStarved)));
            }

            // Il rapporto e' l'informazione che chi legge dovrebbe calcolare a mano, e
            // che nessuno calcola mentre sta cercando di capire un guasto.
            var audioMs = received * 20;

            if (audioMs + silence > 0)
            {
                var quota = 100.0 * silence / (audioMs + silence);

                counters.Add(new DiagnosticCounter(
                    "Quota di silenzio inventato",
                    $"{quota:F1} per cento del flusso riprodotto"));
            }

            return counters;
        }
    }

    private static string Formatta(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>
    /// Descrive come viaggia la segnalazione, e a quali condizioni.
    /// </summary>
    /// <remarks>
    /// «TLS» da solo sarebbe una mezza verità su una postazione che accetta qualunque
    /// certificato: la riservatezza c'è, la certezza di con chi si sta parlando no.
    /// Chi verifica una postazione deve poter leggere le due cose insieme.
    /// </remarks>
    private string DescribeSignalling() => _account.Transport switch
    {
        SipTransportKind.Tls when _certificateTrust?.AcceptAnyCertificate == true
            => "TLS cifrata, ma con verifica dei certificati DISATTIVATA",
        SipTransportKind.Tls => "TLS cifrata, certificato del centralino verificato",
        SipTransportKind.Tcp => "TCP in chiaro",
        _ => "UDP in chiaro",
    };

    /// <inheritdoc />
    public event EventHandler<SipRegistrationState>? RegistrationStateChanged;

    /// <inheritdoc />
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <inheritdoc />
    public event EventHandler<string>? SrtpDowngraded;

    /// <inheritdoc />
    public event EventHandler<CallProgressEventArgs>? CallProgress;

    /// <inheritdoc />
    public event EventHandler<TransferProgressEventArgs>? TransferProgress;

    // -----------------------------------------------------------------------
    // Ciclo di vita
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_account.Validate(out var error))
        {
            SetRegistrationState(RegistrationStatus.Failed, error);
            throw new InvalidOperationException(error);
        }

        _transport = new SIPTransport();

        var localPort = _account.LocalPort > 0 ? _account.LocalPort : 0;

        switch (_account.Transport)
        {
            case SipTransportKind.Tcp:
                _transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, localPort)));
                break;

            case SipTransportKind.Tls:
                AddTlsChannel(localPort);
                break;

            default:
                _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, localPort)));
                break;
        }

        _transport.SIPTransportRequestReceived += OnTransportRequestReceived;
        WireSignallingTrace(_transport);

        // La sorveglianza degli interni per via SIP nasce con il trasporto: prima non
        // c'e' nulla su cui sottoscrivere. E' il ripiego per i centralini senza API.
        _presenceSource = new SipPresenceSource(_transport, _account, _presenceLogger);

        // Il sondaggio nasce con il trasporto per la stessa ragione: prima non c'e' nulla
        // a cui chiedere. E' la strada che resta quando le sottoscrizioni vengono
        // rifiutate, ed e' l'unica che il quadro dei turni possa usare su questo impianto.
        _optionsProbe = new SipOptionsProbe(_transport, _account, _probeLogger);

        StartRegistration();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Apre il canale della segnalazione cifrata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Questa è la parte che prima non esisteva.</b> Il ramo TLS registrava un
    /// avviso e apriva un canale TCP: chi configurava «TLS» otteneva una segnalazione
    /// in chiaro, e l'unico modo di accorgersene era leggere il registro o mettere uno
    /// sniffer sulla rete. Su un prodotto destinato alla PA è il difetto peggiore
    /// della categoria, perché la promessa non mantenuta è proprio quella di
    /// riservatezza.
    /// </para>
    /// <para>
    /// Si usa il costruttore lato client, quello senza certificato del server:
    /// BlindPhone stabilisce connessioni verso il centralino e non ne accetta di
    /// entranti sul canale TLS, quindi un certificato proprio non serve. La verifica
    /// del certificato del centralino passa dal servizio di fiducia condiviso, lo
    /// stesso che usa il connettore dell'API.
    /// </para>
    /// <para>
    /// <b>Nessun ripiego silenzioso.</b> Se il canale non si apre, la registrazione
    /// va in errore con un messaggio esplicito invece di ricadere su TCP: ricadere
    /// significherebbe trasmettere in chiaro le credenziali di un operatore che ha
    /// chiesto il contrario.
    /// </para>
    /// </remarks>
    private void AddTlsChannel(int localPort)
    {
        if (_transport is null) return;

        try
        {
            var channel = new SIPTLSChannel(
                new IPEndPoint(IPAddress.Any, localPort),
                useDualMode: false,
                remoteCertificateValidation: ValidateServerCertificate);

            _transport.AddSIPChannel(channel);

            _log.LogInformation(
                "Segnalazione SIP cifrata: canale TLS in ascolto sulla porta {Port}",
                channel.Port);
        }
        catch (Exception ex)
        {
            // Il canale non si apre quasi mai per motivi di configurazione: porta
            // occupata, permessi. Va detto per quello che e', e la registrazione deve
            // fallire — non ripiegare.
            _log.LogError(ex, "Apertura del canale TLS non riuscita");

            throw new InvalidOperationException(
                "Segnalazione cifrata non disponibile: il canale TLS non si e' aperto. "
                + "La registrazione non viene tentata in chiaro.",
                ex);
        }
    }

    /// <summary>
    /// Decide se accettare il certificato del centralino.
    /// </summary>
    /// <remarks>
    /// Senza servizio di fiducia si applica la verifica di sistema e basta: è il
    /// comportamento di un client rigoroso, giusto come predefinito e sbagliato come
    /// unica possibilità — la maggioranza dei centralini di una PA italiana espone un
    /// certificato autofirmato, e rifiutarlo senza appello significherebbe che TLS non
    /// si può usare. La via di mezzo la fornisce chi inietta il servizio.
    /// </remarks>
    private bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;

        if (_certificateTrust is null)
        {
            _log.LogError(
                "Certificato del centralino non verificabile ({Errors}) e nessuna politica di fiducia configurata",
                errors);

            return false;
        }

        var description = CertificateDescriptionFactory.Create(
            _account.EffectiveRegistrar, "al telefono", certificate, errors);

        var verdict = _certificateTrust.Evaluate(description);

        if (!verdict.Accepted)
        {
            SetRegistrationState(
                RegistrationStatus.Failed,
                CertificateTexts.ConnectionRefused("al centralino"));
        }

        return verdict.Accepted;
    }

    private void StartRegistration()
    {
        if (_transport is null) return;

        SetRegistrationState(RegistrationStatus.Registering, null);

        _registration = new SIPRegistrationUserAgent(
            _transport,
            _account.AuthId ?? _account.Username,
            _account.Password,
            RegistrarUri(),
            _account.RegistrationExpiry);

        _registration.RegistrationSuccessful += (_, _) =>
            SetRegistrationState(RegistrationStatus.Registered, null);

        _registration.RegistrationFailed += (_, _, message) =>
            SetRegistrationState(RegistrationStatus.Failed, TranslateRegistrationError(message));

        _registration.RegistrationTemporaryFailure += (_, _, message) =>
            SetRegistrationState(RegistrationStatus.Registering, TranslateRegistrationError(message));

        _registration.RegistrationRemoved += (_, _) =>
            SetRegistrationState(RegistrationStatus.Unregistered, null);

        _registration.Start();
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stesso trattamento del riaggancio, e per lo stesso motivo: chiudere
        // l'applicazione con una chiamata uscente in squillo lasciava il telefono
        // chiamato a suonare, perche' il BYE su un dialogo che non esiste ancora non
        // annulla niente.
        await StopConferenceAsync(cancellationToken).ConfigureAwait(false);

        foreach (var context in _calls.Values) CloseCall(context);

        _calls.Clear();

        if (_registration is not null)
        {
            _registration.Stop();
            _registration = null;
        }

        if (_transport is not null)
        {
            _transport.SIPTransportRequestReceived -= OnTransportRequestReceived;
            _transport.Shutdown();
            _transport = null;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Traccia della segnalazione
    // -----------------------------------------------------------------------

    /// <summary>
    /// Porta nel registro ogni messaggio SIP che entra ed esce.
    /// </summary>
    /// <param name="transport">Trasporto appena creato.</param>
    /// <remarks>
    /// <para>
    /// <b>Nasce da una domanda a cui il registro non sapeva rispondere:</b> «chi ha
    /// chiuso questa chiamata?». Quando una chiamata entrante finiva da sola, del
    /// centralino restava una riga di libreria — <i>SIPServerUserAgent got
    /// cancellation request</i> — che dice che è arrivato un <c>CANCEL</c> ma non da
    /// dove, non quando rispetto all'inizio dello squillo e soprattutto non
    /// <b>perché</b>: quella ragione il centralino la scrive nell'intestazione
    /// <c>Reason</c>, che nessuno leggeva.
    /// </para>
    /// <para>
    /// Tre livelli, perché "registrare tutto" e "registrare in modo leggibile" sono
    /// due requisiti in conflitto e vanno soddisfatti separatamente:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <b>Informazione</b> — solo i messaggi che aprono o chiudono una chiamata
    ///   (<c>INVITE</c>, <c>CANCEL</c>, <c>BYE</c>) e i loro esiti negativi. È il
    ///   livello con cui gira una postazione in esercizio, e deve bastare a dire chi
    ///   ha chiuso.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Debug</b> — una riga per ogni messaggio, con metodo, destinatario e
    ///   Call-ID. Bastano a ricostruire il dialogo senza leggere i corpi SDP.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Traccia</b> — il messaggio intero. Serve quando il sospetto è su un
    ///   dettaglio di intestazione o sulla descrizione di sessione.
    ///   </description></item>
    /// </list>
    /// <para>
    /// I messaggi malformati e le ritrasmissioni si registrano sempre come avvisi: un
    /// messaggio che lo stack scarta perché non lo capisce, oggi, non lascia traccia
    /// alcuna — e una ritrasmissione significa che la controparte non ha ricevuto
    /// quello che le abbiamo mandato, che è esattamente il tipo di guasto che si
    /// manifesta come «la chiamata cade da sola».
    /// </para>
    /// </remarks>
    private void WireSignallingTrace(SIPTransport transport)
    {
        transport.SIPRequestInTraceEvent += (local, remote, request) =>
            TraceRequest("ricevuto", remote, request);

        transport.SIPRequestOutTraceEvent += (local, remote, request) =>
            TraceRequest("inviato", remote, request);

        transport.SIPResponseInTraceEvent += (local, remote, response) =>
            TraceResponse("ricevuta", remote, response);

        transport.SIPResponseOutTraceEvent += (local, remote, response) =>
            TraceResponse("inviata", remote, response);

        transport.SIPBadRequestInTraceEvent += (local, remote, message, field, raw) =>
            _log.LogWarning(
                "SIP: richiesta malformata da {Remote} scartata dallo stack ({Campo}): {Messaggio}. Testo: {Raw}",
                remote, field, message, raw);

        transport.SIPBadResponseInTraceEvent += (local, remote, message, field, raw) =>
            _log.LogWarning(
                "SIP: risposta malformata da {Remote} scartata dallo stack ({Campo}): {Messaggio}. Testo: {Raw}",
                remote, field, message, raw);

        // Una ritrasmissione dice che il centralino non ha confermato: e' il sintomo
        // di una segnalazione che non arriva a destinazione, e senza questa riga si
        // vedrebbe solo l'effetto finale.
        transport.SIPRequestRetransmitTraceEvent += (_, request, number) =>
            _log.LogWarning(
                "SIP: ritrasmissione numero {Numero} di {Metodo} {Uri} (Call-ID {CallId})",
                number, request.Method, request.URI, request.Header?.CallId);

        transport.SIPResponseRetransmitTraceEvent += (_, response, number) =>
            _log.LogWarning(
                "SIP: ritrasmissione numero {Numero} della risposta {Stato} (Call-ID {CallId})",
                number, response.StatusCode, response.Header?.CallId);
    }

    /// <summary>Registra una richiesta SIP entrante o uscente.</summary>
    /// <param name="verso">"ricevuto" oppure "inviato".</param>
    /// <param name="remote">Controparte.</param>
    /// <param name="request">Richiesta.</param>
    private void TraceRequest(string verso, SIPEndPoint remote, SIPRequest request)
    {
        var method = request.Method;

        // INVITE, CANCEL e BYE aprono e chiudono le chiamate: sono le uniche richieste
        // che meritano il livello con cui gira una postazione in esercizio.
        if (method is SIPMethodsEnum.INVITE or SIPMethodsEnum.CANCEL or SIPMethodsEnum.BYE)
        {
            _log.LogInformation(
                "SIP {Verso}: {Metodo} da/verso {Remote}, Call-ID {CallId}{Motivo}",
                verso, method, remote, request.Header?.CallId, DescribeReason(request.Header?.Reason));
        }
        else
        {
            _log.LogDebug(
                "SIP {Verso}: {Metodo} {Uri} da/verso {Remote}, Call-ID {CallId}",
                verso, method, request.URI, remote, request.Header?.CallId);
        }

        if (_log.IsEnabled(LogLevel.Trace))
            _log.LogTrace("SIP {Verso} per esteso:\n{Messaggio}", verso, Redact(request.ToString()));
    }

    /// <summary>Registra una risposta SIP entrante o uscente.</summary>
    /// <param name="verso">"ricevuta" oppure "inviata".</param>
    /// <param name="remote">Controparte.</param>
    /// <param name="response">Risposta.</param>
    private void TraceResponse(string verso, SIPEndPoint remote, SIPResponse response)
    {
        // Gli errori definitivi si registrano sempre: sono il motivo per cui una
        // chiamata non e' partita o non e' arrivata, e finora restavano a livello di
        // debug insieme a tutto il resto.
        if ((int)response.StatusCode >= 400)
        {
            _log.LogInformation(
                "SIP {Verso}: {Stato} {Motivo} da/verso {Remote}, Call-ID {CallId}",
                verso, (int)response.StatusCode, response.ReasonPhrase, remote, response.Header?.CallId);
        }
        else
        {
            _log.LogDebug(
                "SIP {Verso}: {Stato} {Motivo} da/verso {Remote}, Call-ID {CallId}",
                verso, (int)response.StatusCode, response.ReasonPhrase, remote, response.Header?.CallId);
        }

        if (_log.IsEnabled(LogLevel.Trace))
            _log.LogTrace("SIP {Verso} per esteso:\n{Messaggio}", verso, Redact(response.ToString()));
    }

    /// <summary>
    /// Toglie dal testo di un messaggio SIP le righe che portano credenziali.
    /// </summary>
    /// <param name="message">Messaggio SIP per esteso.</param>
    /// <remarks>
    /// Il registro finisce nel pacchetto per l'assistenza e viaggia per posta
    /// elettronica. La risposta digest non è la parola d'ordine e scade con il nonce,
    /// ma è pur sempre materiale di autenticazione dell'interno: in un prodotto per la
    /// pubblica amministrazione non si spedisce per comodità di chi indaga. Resta il
    /// nome dell'intestazione, così chi legge sa che c'era.
    /// </remarks>
    private static string Redact(string message)
    {
        if (message.IndexOf("uthor", StringComparison.OrdinalIgnoreCase) < 0
            && message.IndexOf("uthenticate", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return message;
        }

        var righe = message.Split('\n');

        for (var i = 0; i < righe.Length; i++)
        {
            var separatore = righe[i].IndexOf(':');
            if (separatore <= 0) continue;

            var nome = righe[i][..separatore].Trim();
            if (!CredentialHeaders.Contains(nome)) continue;

            // Il messaggio SIP usa CR LF: togliendo il ritorno a capo la riga
            // successiva si attaccherebbe a questa nel registro.
            var ritorno = righe[i].EndsWith('\r') ? "\r" : string.Empty;
            righe[i] = $"{nome}: [omesso dal registro]{ritorno}";
        }

        return string.Join('\n', righe);
    }

    /// <summary>Intestazioni SIP che portano materiale di autenticazione.</summary>
    private static readonly HashSet<string> CredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "WWW-Authenticate",
        "Proxy-Authenticate",
    };

    /// <summary>
    /// Rende leggibile l'intestazione <c>Reason</c>, quando c'è.
    /// </summary>
    /// <param name="reason">Valore dell'intestazione, per esempio <c>Q.850;cause=19</c>.</param>
    /// <remarks>
    /// È l'intestazione con cui il centralino dice <b>perché</b> sta annullando una
    /// chiamata: la causa 19 di Q.850 è «nessuna risposta», la 16 è «chiusura
    /// normale», la 17 «occupato». Su un <c>CANCEL</c> che arriva a squillo in corso
    /// è la differenza fra «è scaduto il tempo di squillo configurato sul centralino»
    /// e «il chiamante ha riagganciato», che sono due problemi diversi con due
    /// soluzioni diverse.
    /// </remarks>
    private static string DescribeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? string.Empty : $", Reason: {reason}";

    // -----------------------------------------------------------------------
    // Chiamate uscenti
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public Task<string> DialAsync(string destination, CancellationToken cancellationToken)
        => DialAsync(destination, SrtpNegotiation.ShouldOffer(_account.Srtp));

    /// <summary>
    /// Compone un numero, proponendo o no la cifratura del flusso audio.
    /// </summary>
    /// <param name="destination">Destinazione.</param>
    /// <param name="secure">Se proporre SRTP.</param>
    /// <remarks>
    /// Il secondo parametro esiste per il ripiego: con politica
    /// <see cref="SrtpPolicy.Preferred"/>, una destinazione che rifiuta l'offerta
    /// cifrata viene richiamata subito in chiaro. Vedi <see cref="RetryInClearAsync"/>.
    /// </remarks>
    private async Task<string> DialAsync(string destination, bool secure)
    {
        if (_transport is null) throw new InvalidOperationException("Motore telefonico non avviato");

        var callId = NextCallId();
        var agent = CreateUserAgent();
        var builder = new CallContextBuilder();
        var media = CreateMediaSession(builder, secure);

        var context = new CallContext(callId, agent, media, destination)
        {
            AudioEndPoint = builder.AudioEndPoint,
            OfferedSrtp = secure,
        };

        _calls[callId] = context;

        WireCallEvents(context);

        var target = BuildTargetUri(destination);

        // Call() ritorna quando il dialogo e' instaurato o fallito; l'esito arriva
        // comunque dagli eventi, che sono l'unica fonte usata per far avanzare la
        // macchina a stati. Non si attende qui per non bloccare il tasto premuto
        // dall'operatore.
        if (BuildCallDescriptor(target, destination) is { } descriptor)
        {
            _ = agent.Call(descriptor, media);
        }
        else
        {
            _ = agent.Call(target, _account.Username, _account.Password, media);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return callId;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>È una chiamata normale con un'intestazione in più.</b> Tutto il resto —
    /// creazione dell'agente, sessione media, aggancio degli eventi, macchina a stati —
    /// è identico a una composizione qualunque, ed è voluto: l'inclusione deve
    /// comparire fra le chiamate come tutte le altre, potersi mettere in attesa e
    /// potersi chiudere con lo stesso tasto. L'unica differenza sta nel <c>Join</c>.
    /// </para>
    /// <para>
    /// <b>L'invito va all'interno, non al suo interlocutore.</b> Il dialogo da
    /// raggiungere è quello fra il centralino e l'interno, ed è l'interno che deve
    /// riceverlo: è il verso che rende coerente la traduzione delle due etichette
    /// (vedi <see cref="DialogReference.ToJoinHeaderValue"/>).
    /// </para>
    /// <para>
    /// <b>Non si offre la cifratura del flusso audio.</b> Con politica «preferita»,
    /// una destinazione che rifiuta l'offerta cifrata viene richiamata in chiaro dal
    /// secondo tentativo, che ricompone la stessa destinazione <i>senza</i> il
    /// <c>Join</c>: l'interno squillerebbe durante la conversazione, che è l'opposto
    /// di quello che l'operatore ha chiesto. Qui si offre subito in chiaro e si evita
    /// del tutto quel percorso.
    /// </para>
    /// </remarks>
    public async Task<string> BargeInAsync(DialogReference dialog, CancellationToken cancellationToken)
    {
        if (_transport is null) throw new InvalidOperationException("Motore telefonico non avviato");

        if (!dialog.IsComplete)
        {
            throw new InvalidOperationException(
                "Il centralino non ha comunicato gli identificativi della conversazione");
        }

        var callId = NextCallId();
        var agent = CreateUserAgent();
        var builder = new CallContextBuilder();
        var media = CreateMediaSession(builder, secure: false);

        var context = new CallContext(callId, agent, media, dialog.Extension)
        {
            AudioEndPoint = builder.AudioEndPoint,
            OfferedSrtp = false,
        };

        _calls[callId] = context;
        WireCallEvents(context);

        var target = BuildTargetUri(dialog.Extension);
        var join = dialog.ToJoinHeaderValue();

        _log.LogInformation(
            "Inclusione sulla conversazione dell'interno {Extension}: chiamata {CallId}, Join {Join}",
            dialog.Extension, callId, join);

        var descriptor = BuildCallDescriptor(target, dialog.Extension)
                         ?? new SIPCallDescriptor(
                             username: _account.Username,
                             password: _account.Password,
                             uri: target,
                             from: null,
                             to: null,
                             routeSet: null,
                             customHeaders: null,
                             authUsername: null,
                             callDirection: SIPCallDirection.Out,
                             contentType: null,
                             content: null,
                             mangleIPAddress: null);

        // Le intestazioni aggiuntive sono una lista che il descrittore puo' avere gia'
        // popolata con l'identita' dichiarata: ci si aggiunge, non la si sostituisce.
        descriptor.CustomHeaders = [.. descriptor.CustomHeaders ?? [], $"Join: {join}"];

        _ = agent.Call(descriptor, media);

        await Task.CompletedTask.ConfigureAwait(false);
        return callId;
    }

    /// <summary>
    /// Dice se il contesto sicuro della sessione è davvero pronto.
    /// </summary>
    /// <remarks>
    /// È la verifica che distingue «ho offerto la cifratura» da «la cifratura c'è».
    /// Si interroga la sessione invece di rileggere l'SDP della risposta perché copre
    /// due casi con una domanda sola: la risposta senza crypto, e la risposta con
    /// crypto da cui però non è stato possibile ricavare le chiavi.
    /// </remarks>
    private bool IsSecureContextReady(CallContext context)
    {
        try
        {
            return context.Media.IsSecureContextReady();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stato del contesto sicuro non leggibile: si considera assente");
            return false;
        }
    }

    /// <summary>
    /// Governa una chiamata che è stata accettata ma senza la cifratura richiesta.
    /// </summary>
    /// <param name="callId">Chiamata interessata.</param>
    /// <param name="context">Stato della chiamata.</param>
    /// <remarks>
    /// <para>
    /// <b>In nessuno dei due casi si lascia proseguire.</b> Una chiamata in questo
    /// stato non trasporta audio: proseguire significa lasciare due persone a parlare
    /// nel vuoto, e su un posto operatore è il guasto peggiore, perché sembra una
    /// conversazione riuscita.
    /// </para>
    /// <para>
    /// Con la politica «preferita» si richiama in chiaro: chi è stato chiamato sente
    /// un secondo squillo, ed è il prezzo per non perdere la telefonata. Con la
    /// politica «obbligatoria» si chiude e si dice perché: lì una conversazione in
    /// chiaro non è un ripiego accettabile.
    /// </para>
    /// </remarks>
    private void HandleMissingEncryption(string callId, CallContext context)
    {
        _log.LogWarning(
            "Chiamata {CallId} verso {Destination}: risposta accettata ma senza cifratura del flusso audio. "
            + "Politica {Policy}",
            callId, context.Destination, _account.Srtp);

        try
        {
            context.ClosedLocally = true;
            context.Agent.Hangup();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Chiusura della chiamata non cifrata non riuscita");
        }

        if (_account.Srtp == SrtpPolicy.Required)
        {
            RaiseProgress(callId, CallTrigger.Failed, CallEndReason.EncryptionUnavailable, null, false);
            RemoveCall(callId);
            return;
        }

        _ = RetryInClearAsync(callId, context.Destination);
    }

    /// <summary>
    /// Ricompone la stessa destinazione senza offrire la cifratura.
    /// </summary>
    /// <param name="callId">Identificativo della chiamata da riusare.</param>
    /// <param name="destination">Destinazione da ricomporre.</param>
    /// <remarks>
    /// <para>
    /// <b>L'identificativo resta lo stesso</b>, ed è la ragione per cui questo metodo
    /// esiste invece di richiamare <c>DialAsync</c> e basta. Per la macchina a stati
    /// delle chiamate, per l'elenco delle ultime chiamate e per l'operatore quella è
    /// una telefonata sola: cambiando identificativo si vedrebbe una chiamata fallita
    /// seguita da una nuova, e l'annuncio direbbe due volte «chiamata a...».
    /// </para>
    /// <para>
    /// Il ripiego viene <b>detto</b>: una protezione che si spegne in silenzio è
    /// peggio di una protezione assente, perché lascia credere di averla.
    /// </para>
    /// </remarks>
    private async Task RetryInClearAsync(string callId, string destination)
    {
        // Si esce prima dal gestore di evento che ci ha chiamati: da qui si smonta la
        // chiamata a cui quel gestore appartiene, e farlo mentre lo stack sta ancora
        // percorrendo la propria lista di sottoscrittori e' il modo di scoprire un
        // difetto di concorrenza durante un turno.
        await Task.Yield();

        RemoveCall(callId);

        try
        {
            if (_transport is null) return;

            var agent = CreateUserAgent();
            var builder = new CallContextBuilder();
            var media = CreateMediaSession(builder, secure: false);

            var context = new CallContext(callId, agent, media, destination)
            {
                AudioEndPoint = builder.AudioEndPoint,
                OfferedSrtp = false,
            };

            _calls[callId] = context;
            WireCallEvents(context);

            SrtpDowngraded?.Invoke(this, destination);

            var target = BuildTargetUri(destination);

            if (BuildCallDescriptor(target, destination) is { } descriptor) _ = agent.Call(descriptor, media);
            else _ = agent.Call(target, _account.Username, _account.Password, media);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Secondo tentativo in chiaro non riuscito per la chiamata {CallId}", callId);
            RaiseProgress(callId, CallTrigger.Failed, CallEndReason.NetworkFailure, null, false);
            RemoveCall(callId);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Prepara la chiamata con un'identità diversa da quella dell'account, quando
    /// configurata.
    /// </summary>
    /// <param name="target">Destinazione già risolta.</param>
    /// <param name="destination">Destinazione come l'ha digitata l'operatore, per il registro.</param>
    /// <returns>
    /// La descrizione della chiamata, oppure <c>null</c> se non c'è nulla da alterare
    /// e va bene la strada breve.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Tre intestazioni per dire la stessa cosa</b>, e servono tutte e tre perché
    /// non esiste un solo modo di dichiarare il chiamante e i centralini non guardano
    /// tutti lo stesso.
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <c>From</c> è l'identità di base, quella che un apparecchio dichiara di
    ///   essere;
    ///   </description></item>
    ///   <item><description>
    ///   <c>P-Asserted-Identity</c> (RFC 3325) è quella che una rete di fiducia
    ///   <b>attesta</b>, ed è quella che i centralini leggono per decidere cosa
    ///   mostrare a destinazione;
    ///   </description></item>
    ///   <item><description>
    ///   <c>Remote-Party-ID</c> è la forma più vecchia, che diversi apparati
    ///   riconoscono ancora.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Va detto con chiarezza che l'ultima parola è del centralino.</b> Quasi tutti
    /// i PBX, Yeastar compreso, sostituiscono l'identità dichiarata da un interno con
    /// quella configurata per quell'interno: è una protezione contro la falsificazione
    /// del chiamante, non un difetto. Su una configurazione così l'impostazione non
    /// produce alcun effetto finché l'amministratore non autorizza l'interno a
    /// presentarsi con un altro numero. Il pannello lo dice, e qui resta la traccia nel
    /// registro con cui verificarlo.
    /// </para>
    /// </remarks>
    private SIPCallDescriptor? BuildCallDescriptor(string target, string destination)
    {
        // La pulizia si rifa' qui e non ci si fida di quella del pannello: la
        // configurazione puo' arrivare da un file modificato a mano o da un pacchetto
        // distribuito, e un carattere di troppo nell'URI farebbe rifiutare ogni
        // chiamata uscente. Vedi CallerIdentity.Normalize.
        var callerId = CallerIdentity.Normalize(_account.OutboundCallerId);
        if (callerId.Length == 0) return null;

        var dominio = string.IsNullOrWhiteSpace(_account.Domain) ? _account.Registrar : _account.Domain;

        var nome = string.IsNullOrWhiteSpace(_account.DisplayName) ? callerId : _account.DisplayName;
        var identita = $"sip:{callerId}@{dominio}";

        _log.LogInformation(
            "Chiamata a {Destination} presentata come {CallerId}: l'esito dipende da cosa il centralino "
            + "consente a questo interno",
            destination, callerId);

        // Le intestazioni aggiuntive si passano dal costruttore, che le prevede: la
        // posizione dei parametri e' stata verificata sulla versione in uso, perche' un
        // ordine sbagliato compilerebbe e romperebbe le chiamate solo a chiamata vera.
        List<string> intestazioni =
        [
            $"P-Asserted-Identity: \"{nome}\" <{identita}>",
            $"Remote-Party-ID: \"{nome}\" <{identita}>;party=calling;screen=yes;privacy=off",
        ];

        return new SIPCallDescriptor(
            username: _account.Username,
            password: _account.Password,
            uri: target,
            from: $"\"{nome}\" <{identita}>",
            to: null,
            routeSet: null,
            customHeaders: intestazioni,
            authUsername: null,
            callDirection: SIPCallDirection.Out,
            contentType: null,
            content: null,
            mangleIPAddress: null);
    }

    // -----------------------------------------------------------------------
    // Chiamate entranti
    // -----------------------------------------------------------------------

    private async Task OnTransportRequestReceived(
        SIPEndPoint localEndPoint,
        SIPEndPoint remoteEndPoint,
        SIPRequest request)
    {
        // IL SONDAGGIO DI RAGGIUNGIBILITA' VA RISPOSTO SEMPRE, E PRIMA DI TUTTO.
        //
        // Sta qui sopra la pausa, e non e' una svista: OPTIONS non chiede «prendi
        // questa chiamata?» ma «sei ancora vivo?», e a quella domanda una postazione
        // in pausa risponde di si'. Il centralino tiene due stati distinti — l'interno
        // raggiungibile e l'interno disponibile — e confonderli significa sparire
        // dall'elenco invece di risultare occupati.
        if (request.Method == SIPMethodsEnum.OPTIONS)
        {
            await AnswerOptionsAsync(request).ConfigureAwait(false);
            return;
        }

        // Le notifiche di stato degli interni (NOTIFY) arrivano sullo stesso trasporto,
        // ma non passano di qui: ogni sottoscrizione si aggancia da se' al trasporto e
        // riconosce le proprie dal Call-ID. Vedi SipPresenceSource.
        if (request.Method != SIPMethodsEnum.INVITE) return;
        if (_transport is null) return;

        // Un re-INVITE appartiene a un dialogo esistente e viene gestito dall'agente
        // che lo possiede: qui interessano solo gli INVITE iniziali.
        if (request.Header.To?.ToTag is not null) return;

        // POSTAZIONE IN PAUSA: si rifiuta subito e non si annuncia nulla.
        //
        // Il rifiuto arriva PRIMA che la chiamata esista per il resto del programma:
        // non si crea il contesto, non si solleva IncomingCall, non squilla niente. Se
        // la si lasciasse entrare per rifiutarla dopo, l'operatore sentirebbe la
        // suoneria di una chiamata che non puo' prendere, che e' il modo piu' rapido di
        // rendere inutile la pausa.
        if (_doNotDisturb)
        {
            await RejectWhilePausedAsync(request).ConfigureAwait(false);
            return;
        }

        try
        {
            // LA CHIAMATA ENTRANTE SEGUE CHI CHIAMA, non la nostra preferenza.
            //
            // Chi chiama ha gia' fatto la propria offerta: rispondere con una sessione
            // cifrata a chi ha offerto RTP in chiaro — o viceversa — fa fallire la
            // negoziazione, e per l'operatore la chiamata semplicemente non arriva.
            // La politica dell'account interviene solo per rifiutare, quando impone la
            // cifratura e chi chiama non la offre.
            var remoteOffersSrtp = OffersSrtp(request);
            var esito = SrtpNegotiation.Decide(_account.Srtp, remoteOffersSrtp);

            if (esito == SrtpOutcome.Reject)
            {
                _log.LogWarning(
                    "Chiamata da {From} rifiutata: la politica impone la cifratura e il chiamante non la offre",
                    request.Header.From?.FromURI?.User ?? "sconosciuto");

                await RejectAsync(request, SIPResponseStatusCodesEnum.NotAcceptableHere, "Cifratura richiesta")
                    .ConfigureAwait(false);

                return;
            }

            var callId = NextCallId();
            var agent = CreateUserAgent();
            var builder = new CallContextBuilder();
            var media = CreateMediaSession(builder, esito == SrtpOutcome.Secure);

            var uas = agent.AcceptCall(request);
            var context = new CallContext(callId, agent, media, request.Header.From?.FromURI?.User ?? string.Empty)
            {
                ServerUserAgent = uas,
                AudioEndPoint = builder.AudioEndPoint,
                RingingSince = _clock.Now,
            };

            // La carta d'identita' della chiamata entrante, in una riga sola. Il
            // Call-ID e' cio' che permette di ritrovarla nel registro del centralino,
            // ed e' il primo dato che l'assistenza chiede.
            //
            // Il destinatario si scrive per intero e non solo la parte utente: quando
            // in mezzo c'e' un SBC che instrada sulla registrazione, la parte utente
            // non c'e' — l'INVITE arriva indirizzato al dominio — e una riga che dice
            // "a sconosciuto" fa sospettare un difetto dove non c'e'.
            _log.LogInformation(
                "Chiamata entrante {CallId}: da {Da} a {A} (destinazione {Uri}), Call-ID {SipCallId}, "
                + "apparato chiamante {UserAgent}, coda {Coda}, cifratura {Cifratura}, sorgente {Remote}",
                callId,
                request.Header.From?.FromURI?.User ?? "sconosciuto",
                request.Header.To?.ToURI?.ToString() ?? "non dichiarato",
                request.URI,
                request.Header.CallId,
                string.IsNullOrWhiteSpace(request.Header.UserAgent) ? "non dichiarato" : request.Header.UserAgent,
                ExtractQueueHint(request) ?? "nessuna",
                esito,
                remoteEndPoint);

            _calls[callId] = context;
            WireCallEvents(context);

            // Da questo momento la chiamata squilla, e finche' squilla va detto al
            // proxy che sta ancora squillando. Va avviata DOPO la registrazione nel
            // dizionario: e' RemoveCall a fermarla, e una ripetizione avviata su una
            // chiamata che il dizionario non conosce non la fermerebbe piu' nessuno.
            StartRingingRefresh(context);

            var party = new CallParty(
                request.Header.From?.FromURI?.User ?? string.Empty,
                string.IsNullOrWhiteSpace(request.Header.From?.FromName) ? null : request.Header.From.FromName,
                PhoneticName: null,
                IsInternal: IsLikelyInternal(request.Header.From?.FromURI?.User));

            IncomingCall?.Invoke(this, new IncomingCallEventArgs(
                callId,
                party,
                request.Header.CallId,
                ExtractQueueHint(request),
                _clock.Now));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Errore nell'accettazione di una chiamata entrante");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Dice al centralino che l'interno non accetta chiamate in questo momento.
    /// </summary>
    /// <param name="request">INVITE arrivato mentre la postazione è in pausa.</param>
    /// <remarks>
    /// <para>
    /// <b>480 «Temporarily Unavailable» e non 486 «Busy Here».</b> Sono due
    /// affermazioni diverse e i centralini le instradano in modo diverso: 486 dice
    /// «sono al telefono», e su molte configurazioni fa sentire al chiamante il tono di
    /// occupato o lo manda alla deviazione su occupato. 480 dice «non ci sono adesso»,
    /// che è quello che l'operatore intende premendo la pausa, e fa seguire alla
    /// chiamata la strada prevista per l'interno non disponibile — il collega
    /// successivo della coda, oppure la casella vocale.
    /// </para>
    /// <para>
    /// Il rifiuto arriva in millisecondi, e questo è il punto: sul chiamante in coda la
    /// differenza fra rifiutare e lasciare squillare sono i venti o trenta secondi di
    /// timeout che passerebbe assegnato a un posto vuoto.
    /// </para>
    /// </remarks>
    private Task RejectWhilePausedAsync(SIPRequest request)
    {
        _log.LogInformation(
            "Chiamata da {From} rifiutata: postazione in pausa",
            request.Header.From?.FromURI?.User ?? "sconosciuto");

        return RejectAsync(request, SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Postazione in pausa");
    }

    /// <summary>
    /// Risponde al sondaggio di raggiungibilità che il centralino invia periodicamente.
    /// </summary>
    /// <param name="request">La richiesta OPTIONS arrivata.</param>
    /// <remarks>
    /// <para>
    /// <b>Un centralino non si fida della registrazione per sapere se un interno è
    /// vivo.</b> La registrazione dura minuti e sopravvive a un cavo staccato, a un
    /// portatile chiuso, a un processo terminato. Per questo il centralino manda
    /// periodicamente un OPTIONS — Yeastar lo chiama <i>qualify</i>, ogni due minuti —
    /// e usa la risposta come prova di vita. Chi non risponde viene marcato
    /// irraggiungibile e <b>smette di ricevere chiamate</b>, pur restando registrato:
    /// dall'esterno sembra che il centralino abbia smesso di deviare, e non c'è nessun
    /// errore da nessuna parte a dirlo.
    /// </para>
    /// <para>
    /// È un obbligo di protocollo, non una cortesia: RFC 3261 §11.2 impone a un UAS di
    /// rispondere a OPTIONS fuori dialogo. La risposta porta con sé l'elenco dei metodi
    /// che sappiamo trattare, che è poi l'informazione per cui il metodo esiste.
    /// </para>
    /// </remarks>
    private async Task AnswerOptionsAsync(SIPRequest request)
    {
        if (_transport is null) return;

        try
        {
            var response = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
            await _transport.SendResponseAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Non si rilancia: il sondaggio successivo arriva fra due minuti, e fermare
            // il trasporto SIP per una risposta non partita significherebbe provocare
            // esattamente il guasto che questo metodo esiste per evitare.
            _log.LogWarning(ex, "Risposta al sondaggio di raggiungibilita' non riuscita");
        }
    }

    /// <summary>Risponde a un INVITE con un rifiuto, senza aprire alcuna chiamata.</summary>
    /// <param name="request">INVITE arrivato.</param>
    /// <param name="status">Codice di stato del rifiuto.</param>
    /// <param name="reason">Motivo, che finisce nella riga di stato.</param>
    private async Task RejectAsync(SIPRequest request, SIPResponseStatusCodesEnum status, string reason)
    {
        try
        {
            var response = SIPResponse.GetResponse(request, status, reason);
            await _transport!.SendResponseAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Un rifiuto che non parte lascia il chiamante a squillare a vuoto fino al
            // timeout del centralino. Fastidioso, ma non c'e' nulla da fare qui se non
            // lasciarne traccia: alzare l'eccezione fermerebbe il trasporto SIP.
            _log.LogWarning(ex, "Rifiuto della chiamata entrante non riuscito");
        }
    }

    /// <summary>
    /// Dice se chi chiama ha offerto la cifratura del flusso audio.
    /// </summary>
    /// <param name="request">INVITE con la descrizione di sessione.</param>
    /// <remarks>
    /// <para>
    /// Si guarda nel corpo SDP e non nella sessione media, perché la decisione va
    /// presa <b>prima</b> di costruirla: è quella descrizione a dire con quale
    /// profilo la sessione dev'essere aperta.
    /// </para>
    /// <para>
    /// Due indizi e basta uno: il profilo di trasporto <c>SAVP</c> nella riga del
    /// media, e l'attributo <c>a=crypto</c> che porta le chiavi. Alcuni apparati
    /// dichiarano l'uno senza l'altro, e trattare come «in chiaro» un'offerta cifrata
    /// significherebbe far fallire la chiamata.
    /// </para>
    /// </remarks>
    private static bool OffersSrtp(SIPRequest request) => DescribesSrtp(request.Body);

    /// <summary>
    /// Dice se una descrizione di sessione dichiara la cifratura del flusso audio.
    /// </summary>
    /// <param name="body">Corpo SDP, dell'offerta o della risposta.</param>
    /// <remarks>
    /// Due indizi, e basta uno: il profilo di trasporto <c>SAVP</c> nella riga del
    /// media e l'attributo <c>a=crypto</c> che porta le chiavi. Alcuni apparati
    /// dichiarano l'uno senza l'altro.
    /// </remarks>
    private static bool DescribesSrtp(string? body)
        => !string.IsNullOrWhiteSpace(body)
           && (body.Contains("RTP/SAVP", StringComparison.OrdinalIgnoreCase)
               || body.Contains("a=crypto", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ricava il nome della coda dalla segnalazione.
    /// </summary>
    /// <remarks>
    /// L'header <c>Alert-Info</c> è il veicolo standard con cui i centralini
    /// distinguono l'origine di una chiamata, e Yeastar lo usa per la suoneria
    /// differenziata. Serve per due cose: annunciare da quale servizio arriva la
    /// chiamata, e scegliere la suoneria giusta, che per un operatore non vedente è
    /// spesso l'informazione più veloce delle due.
    /// </remarks>
    private static string? ExtractQueueHint(SIPRequest request)
    {
        var alertInfo = request.Header.AlertInfo;
        if (!string.IsNullOrWhiteSpace(alertInfo)) return alertInfo.Trim('<', '>');

        var toName = request.Header.To?.ToName;
        return string.IsNullOrWhiteSpace(toName) ? null : toName;
    }

    /// <summary>
    /// Descrive da quanto tempo una chiamata entrante sta squillando.
    /// </summary>
    /// <param name="context">Chiamata.</param>
    /// <returns>Per esempio "dopo 30,0 s di squillo", oppure stringa vuota.</returns>
    /// <remarks>
    /// Un decimo di secondo di risoluzione perché è quello che serve a riconoscere un
    /// tempo scaduto: trenta secondi netti a ogni prova sono un parametro configurato
    /// da qualche parte, trenta e sette una persona che ha riagganciato.
    /// </remarks>
    private string DescribeRinging(CallContext context)
    {
        if (context.RingingSince is not { } inizio) return string.Empty;

        var durata = _clock.Now - inizio;
        return $"dopo {durata.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s di squillo";
    }

    /// <summary>
    /// Ripete il <c>180 Ringing</c> per tutto il tempo in cui la chiamata squilla.
    /// </summary>
    /// <param name="context">Chiamata entrante appena accettata.</param>
    /// <remarks>
    /// <para>
    /// <b>Un solo 180 e poi silenzio non basta quando in mezzo c'è un proxy.</b> La
    /// RFC 3261 §16.6 dà a ogni proxy un cronometro sulla transazione INVITE — il
    /// timer C — che viene <b>riazzerato da ogni risposta provvisoria</b> e che, se
    /// scade, fa annullare il ramo con un <c>CANCEL</c>. La §13.3.1.1 dice la stessa
    /// cosa dalla parte del chiamato: chi ha bisogno di tempo per rispondere emette
    /// risposte provvisorie periodiche, proprio per impedire ai proxy di annullare.
    /// </para>
    /// <para>
    /// Su questa installazione un proxy c'è: fra il posto operatore e il centralino
    /// sta un SBC Yeastar, e il <c>CANCEL</c> che chiude le chiamate non risposte lo
    /// compone lui — porta il suo <c>User-Agent</c> e non l'intestazione
    /// <c>YSUser-Agent</c> con cui marca tutto ciò che si limita a inoltrare — con
    /// <c>Reason: Q.850;cause=0</c>, cioè «nessuna causa dichiarata»: la firma di un
    /// cronometro scaduto, non di una decisione sulla chiamata.
    /// </para>
    /// <para>
    /// Dieci secondi lasciano tre rinnovi dentro i trenta osservati. Si rimanda la
    /// stessa risposta già inviata invece di costruirne una nuova: mantiene
    /// l'etichetta di dialogo e il numero di sequenza, e per la controparte è la
    /// ritrasmissione di una provvisoria — cosa che il protocollo prevede — e non una
    /// seconda risposta diversa.
    /// </para>
    /// </remarks>
    private void StartRingingRefresh(CallContext context)
    {
        var cts = new CancellationTokenSource();
        context.RingingKeepAlive = cts;

        // Copia del token presa adesso: dopo lo smaltimento leggerla dalla sorgente
        // solleverebbe ObjectDisposedException in un'attivita' che nessuno attende.
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(RingingRefreshInterval, token).ConfigureAwait(false);

                    var uas = context.ServerUserAgent;
                    if (uas is null || uas.IsUASAnswered || uas.IsCancelled || uas.IsHungup) return;

                    var transaction = uas.ClientTransaction;
                    if (transaction?.UnreliableProvisionalResponse is not { } squillo) return;

                    await transaction.SendProvisionalResponse(squillo).ConfigureAwait(false);

                    _log.LogDebug(
                        "Chiamata entrante {CallId}: 180 Ringing ripetuto {Durata}",
                        context.CallId, DescribeRinging(context));
                }
            }
            catch (OperationCanceledException)
            {
                // Squillo finito: percorso normale.
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex, "Chiamata entrante {CallId}: ripetizione del 180 Ringing interrotta", context.CallId);
            }
        }, CancellationToken.None);
    }

    /// <summary>Ferma la ripetizione del <c>180 Ringing</c>.</summary>
    private static void StopRingingRefresh(CallContext context)
    {
        var cts = context.RingingKeepAlive;
        if (cts is null) return;

        context.RingingKeepAlive = null;

        cts.Cancel();
        cts.Dispose();
    }

    /// <inheritdoc />
    public async Task AnswerAsync(string callId, CancellationToken cancellationToken)
    {
        if (!_calls.TryGetValue(callId, out var context)) return;
        if (context.ServerUserAgent is null) return;

        _log.LogInformation(
            "Chiamata entrante {CallId}: risposta dall'operatore {Durata}", callId, DescribeRinging(context));

        // Da qui in poi la chiamata non squilla piu': ripetere il 180 dopo il 200 OK
        // manderebbe una risposta provvisoria su una transazione gia' conclusa.
        StopRingingRefresh(context);

        await context.Agent.Answer(context.ServerUserAgent, context.Media).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task HangUpAsync(string callId, CancellationToken cancellationToken)
    {
        if (!_calls.TryGetValue(callId, out var context)) return Task.CompletedTask;

        CloseCall(context);
        RemoveCall(callId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Chiude una chiamata con il metodo che il suo stato richiede.
    /// </summary>
    /// <remarks>
    /// <para>
    /// I tre casi vogliono tre richieste SIP diverse, e sbagliarne uno non produce
    /// un errore visibile: produce una chiamata che nessuno chiude.
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <b>Dialogo instaurato</b> — si manda <c>BYE</c>.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Chiamata entrante non ancora risposta</b> — si risponde alla richiesta
    ///   con un codice di rifiuto.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Chiamata uscente non ancora risposta</b> — si manda <c>CANCEL</c>. Il
    ///   <c>BYE</c> qui non vale nulla, perché il dialogo non esiste ancora: è la
    ///   transazione <c>INVITE</c> che va annullata.
    ///   </description></item>
    /// </list>
    /// <para>
    /// L'ultimo caso mancava del tutto, e il sintomo era questo: l'operatore
    /// rinunciava a una chiamata uscente, per lui la chiamata era finita, e
    /// <b>il telefono chiamato continuava a squillare</b> finché non rispondeva
    /// qualcuno o non scadeva il timeout del centralino. Con nessuno dall'altra
    /// parte, perché il posto operatore aveva già dimenticato la chiamata.
    /// </para>
    /// </remarks>
    private void CloseCall(CallContext context)
    {
        var agent = context.Agent;

        // Va marcato PRIMA di chiudere: lo stack puo' sollevare l'evento di
        // chiusura in modo sincrono, dentro la chiamata qui sotto.
        context.ClosedLocally = true;

        try
        {
            // L'ORDINE DI QUESTI CONTROLLI E' LA PARTE CHE CONTA.
            //
            // "Tentativo uscente ancora in corso" viene PRIMA di "chiamata attiva", e
            // non e' indifferente. IsRinging significa, testualmente, che una
            // chiamata iniziata da questo agente ha ricevuto una risposta di squillo
            // o di progresso: e' esattamente il caso in cui serve CANCEL. Ma quando
            // la controparte manda anche audio in banda puo' esistere gia' un
            // dialogo precoce, e con esso IsCallActive diventa vero.
            //
            // Controllando IsCallActive per primo si finiva a mandare BYE su un
            // dialogo mai confermato: il centralino lo rifiuta, nessuno annulla
            // l'INVITE, e il telefono chiamato continua a squillare. Si manifestava
            // sull'annullamento del trasferimento — dove l'interno squillava e
            // continuava a squillare dopo ESC — e non sulla chiamata diretta, dove
            // il dialogo precoce non si era formato.
            if (agent.IsCalling || agent.IsRinging)
            {
                agent.Cancel();
            }
            else if (agent.IsCallActive)
            {
                agent.Hangup();
            }
            else if (context.ServerUserAgent is not null)
            {
                // Chiusura decisa qui dentro, su una chiamata entrante mai risposta.
                // Va detto per esteso: e' l'unico caso in cui a spegnere lo squillo e'
                // il posto operatore, e va potuto distinguere da tutti gli altri
                // leggendo il registro.
                _log.LogInformation(
                    "Chiamata entrante {CallId} rifiutata da questa postazione con 486 Busy Here {Durata}",
                    context.CallId, DescribeRinging(context));

                context.ServerUserAgent.Reject(SIPResponseStatusCodesEnum.BusyHere, null, null);
            }
            else
            {
                // Stato incerto: CANCEL e' la scelta prudente. Su una chiamata gia'
                // chiusa non fa nulla, su una ancora in piedi la chiude.
                agent.Cancel();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Chiusura della chiamata {CallId} non riuscita", context.CallId);
        }
    }

    // -----------------------------------------------------------------------
    // Attesa
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Non basta dirlo al centralino: bisogna anche smettere di ascoltare.</b> La
    /// segnalazione fa la sua parte — si passa a <c>a=sendonly</c> e il centralino
    /// risponde <c>a=recvonly</c> — ma quella è una dichiarazione di intenti, e chi
    /// sta dall'altra parte può continuare a mandare pacchetti lo stesso. L'SBC che
    /// media queste chiamate lo fa: l'operatore metteva in attesa e continuava a
    /// sentire il chiamante, che sentiva a sua volta la musica di attesa.
    /// </para>
    /// <para>
    /// Chiudendo la riproduzione in locale l'attesa è attesa comunque, qualunque cosa
    /// arrivi dalla rete. Ed è la condizione perché il promemoria periodico si senta:
    /// un doppio bip di sessanta millisecondi sotto la voce di chi parla non lo
    /// distingue nessuno.
    /// </para>
    /// </remarks>
    public Task HoldAsync(string callId, CancellationToken cancellationToken)
    {
        if (!_calls.TryGetValue(callId, out var context)) return Task.CompletedTask;

        context.Agent.PutOnHold();

        _log.LogDebug("Chiamata {CallId} in attesa: riproduzione chiusa verso l'operatore", callId);
        return context.AudioEndPoint?.PauseAudioSink() ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeAsync(string callId, CancellationToken cancellationToken)
    {
        if (!_calls.TryGetValue(callId, out var context)) return Task.CompletedTask;

        context.Agent.TakeOffHold();

        _log.LogDebug("Chiamata {CallId} ripresa: riproduzione riaperta", callId);
        return context.AudioEndPoint?.ResumeAudioSink() ?? Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Trasferimento
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task BlindTransferAsync(string callId, string destination, CancellationToken cancellationToken)
    {
        if (!_calls.TryGetValue(callId, out var context)) return;

        var target = SIPURI.ParseSIPURIRelaxed(
            destination.Contains('@') ? destination : $"{destination}@{_account.Domain}");

        var accepted = await context.Agent
            .BlindTransfer(target, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        // BlindTransfer restituisce l'esito dell'accettazione del REFER, non quello
        // del trasferimento: il centralino puo' accettare il REFER e poi non
        // riuscire a completare. L'esito definitivo arriva dai NOTIFY, gestiti in
        // OnTransferNotify.
        if (!accepted)
        {
            TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                callId,
                TransferOutcome.RejectedByPbx,
                "Il centralino non ha accettato la richiesta di trasferimento",
                _clock.Now));
        }
    }

    /// <inheritdoc />
    public async Task AttendedTransferAsync(
        string callId,
        string consultationCallId,
        CancellationToken cancellationToken)
    {
        if (!_calls.TryGetValue(callId, out var context))
        {
            ReportTransferFailure(callId, "La chiamata da trasferire non esiste piu'");
            return;
        }

        if (!_calls.TryGetValue(consultationCallId, out var consultation))
        {
            ReportTransferFailure(callId, "La linea con la destinazione non esiste piu'");
            return;
        }

        // LA DESTINAZIONE CHE STA ANCORA SQUILLANDO NON HA UN DIALOGO DA SOSTITUIRE.
        //
        // Il trasferimento con annuncio si fa con un REFER che porta Replaces, e
        // Replaces sostituisce un dialogo esistente: finche' l'interno non ha alzato,
        // quel dialogo non c'e' e non c'e' niente da sostituire. E' esattamente il caso
        // che la modalita' semplificata deve coprire — l'operatore preme Invio senza
        // aspettare che il collega risponda — quindi non e' una condizione d'errore ma
        // l'altra meta' della funzione, e si serve con un REFER semplice.
        if (consultation.Agent.Dialogue is null)
        {
            await SemiAttendedTransferAsync(context, consultation, cancellationToken).ConfigureAwait(false);
            return;
        }

        var accepted = await context.Agent
            .AttendedTransfer(consultation.Agent.Dialogue, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        if (!accepted)
        {
            TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                callId,
                TransferOutcome.RejectedByPbx,
                "Il centralino non ha accettato il completamento del trasferimento",
                _clock.Now));
        }
    }

    /// <summary>
    /// Completa il trasferimento verso una destinazione che sta ancora squillando.
    /// </summary>
    /// <param name="original">Chiamata del chiamante, in attesa.</param>
    /// <param name="consultation">Linea verso la destinazione, non ancora risposta.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// <para>
    /// Si manda un REFER semplice sulla chiamata originale, come nel trasferimento
    /// cieco, e <b>si chiude la linea di consultazione</b>. Chiuderla non è un
    /// dettaglio: da quel momento è il centralino a far squillare la destinazione per
    /// conto del chiamante, e lasciarla aperta la farebbe squillare due volte per la
    /// stessa telefonata.
    /// </para>
    /// </remarks>
    private async Task SemiAttendedTransferAsync(
        CallContext original,
        CallContext consultation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(consultation.Destination))
        {
            ReportTransferFailure(original.CallId, "Destinazione del trasferimento non disponibile");
            return;
        }

        var target = SIPURI.ParseSIPURIRelaxed(
            consultation.Destination.Contains('@')
                ? consultation.Destination
                : $"{consultation.Destination}@{_account.Domain}");

        _log.LogInformation(
            "Trasferimento semi-cieco della chiamata {CallId} verso {Destinazione}: la destinazione squilla ancora",
            original.CallId, consultation.Destination);

        var accepted = await original.Agent
            .BlindTransfer(target, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        if (!accepted)
        {
            TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                original.CallId,
                TransferOutcome.RejectedByPbx,
                "Il centralino non ha accettato il completamento del trasferimento",
                _clock.Now));

            return;
        }

        _locallyCancelled[consultation.CallId] = true;

        await HangUpAsync(consultation.CallId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dichiara fallito un trasferimento che non è nemmeno partito.
    /// </summary>
    /// <param name="callId">Chiamata che doveva essere trasferita.</param>
    /// <param name="reason">Motivo, per il registro e per l'operatore.</param>
    /// <remarks>
    /// <b>Un trasferimento che non parte deve dirlo.</b> Uscire in silenzio lascia
    /// l'orchestratore nella fase di completamento e il chiamante parcheggiato dentro
    /// una procedura che non finirà mai: nessun comando lo raggiunge più, e l'unico
    /// modo di liberare la linea diventa terminare il programma. È successo davvero,
    /// il 10 agosto 2026, con la destinazione che squillava ancora.
    /// </remarks>
    private void ReportTransferFailure(string callId, string reason)
    {
        _log.LogWarning("Trasferimento della chiamata {CallId} non avviato: {Motivo}", callId, reason);

        TransferProgress?.Invoke(this, new TransferProgressEventArgs(
            callId,
            TransferOutcome.Failed,
            reason,
            _clock.Now));
    }

    // -----------------------------------------------------------------------
    // Conferenza a tre
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Vincolata alla presenza del motore audio, che è la condizione da cui dipende
    /// tutto: senza dispositivi la sessione media usa il generatore di silenzio di
    /// SIPSorcery, e una conferenza costruita su quello si aprirebbe senza che nessuno
    /// dei due partecipanti senta l'altro. Dichiararla disponibile in quel caso
    /// significherebbe produrre un comando che riesce e non funziona, che per un
    /// operatore non vedente è peggio di uno che rifiuta.
    /// </remarks>
    public bool SupportsConference => _audioEngine is not null;

    /// <inheritdoc />
    public Task StartConferenceAsync(IReadOnlyList<string> callIds, CancellationToken cancellationToken)
    {
        var endPoints = new List<SipAudioEndPoint>();

        foreach (var callId in callIds)
        {
            if (!_calls.TryGetValue(callId, out var context))
            {
                throw new InvalidOperationException($"Chiamata {callId} non trovata");
            }

            if (context.AudioEndPoint is null)
            {
                throw new InvalidOperationException(
                    $"La chiamata {callId} non ha un percorso audio: non puo' entrare in conferenza");
            }

            endPoints.Add(context.AudioEndPoint);
        }

        lock (_conferenceGate)
        {
            // Una conferenza precedente va smontata prima, altrimenti le sue gambe
            // continuerebbero a ricevere il contributo di partecipanti che non ci sono
            // piu': la sottoscrizione all'evento resterebbe attiva.
            _conference?.Dispose();
            _conference = new ConferenceBridge(endPoints, _log);
        }

        _log.LogInformation("Conferenza aperta fra le chiamate {CallIds}", string.Join(", ", callIds));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopConferenceAsync(CancellationToken cancellationToken)
    {
        lock (_conferenceGate)
        {
            _conference?.Dispose();
            _conference = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Smonta il ponte se la chiamata che se ne va ne faceva parte.
    /// </summary>
    /// <remarks>
    /// Una gamba caduta lascia il ponte con un endpoint smaltito a cui continuerebbe a
    /// consegnare campioni. Il gestore delle chiamate scioglie comunque la conferenza
    /// quando se ne accorge, ma se ne accorge dall'evento di chiusura, cioè dopo: qui
    /// si taglia subito, sul thread che sta chiudendo la chiamata.
    /// </remarks>
    private void DetachFromConference(SipAudioEndPoint? endPoint)
    {
        if (endPoint is null) return;

        lock (_conferenceGate)
        {
            if (_conference?.Contains(endPoint) != true) return;

            _log.LogInformation("Conferenza sciolta: una delle gambe si e' chiusa");

            _conference.Dispose();
            _conference = null;
        }
    }

    // -----------------------------------------------------------------------
    // DTMF e microfono
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task SendDtmfAsync(string callId, char digit, CancellationToken cancellationToken)
    {
        if (!_calls.TryGetValue(callId, out var context)) return;

        var tone = digit switch
        {
            >= '0' and <= '9' => (byte)(digit - '0'),
            '*' => (byte)10,
            '#' => (byte)11,
            _ => byte.MaxValue,
        };

        if (tone == byte.MaxValue) return;

        await context.Agent.SendDtmf(tone).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Il silenziamento agisce sulla sorgente audio della sessione, non sul
    /// dispositivo di sistema: silenziare il microfono a livello di Windows
    /// toglierebbe la voce anche allo screen reader in modalità di comando vocale e
    /// a qualunque altro applicativo, effetto collaterale che l'operatore non si
    /// aspetta da un tasto del centralino.
    /// </remarks>
    public void SetMuted(bool muted)
    {
        _muted = muted;

        foreach (var context in _calls.Values)
        {
            try
            {
                // Il silenziamento sospende la cattura ma NON il flusso RTP:
                // continua a partire silenzio codificato. Interrompere i pacchetti
                // farebbe scattare i timeout di inattivita' del centralino e la
                // chiamata cadrebbe mentre l'operatore e' semplicemente in muto.
                if (muted) context.Media.Media.AudioSource?.PauseAudio();
                else context.Media.Media.AudioSource?.ResumeAudio();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Silenziamento del microfono sulla chiamata {CallId} non riuscito", context.CallId);
            }
        }
    }

    /// <inheritdoc />
    public bool StartCallRecording(string callId, string path)
        => _calls.TryGetValue(callId, out var context)
           && context.AudioEndPoint is { } audio
           && audio.StartCallRecording(path);

    /// <inheritdoc />
    public (TimeSpan Duration, Task<string> File)? StopCallRecording(string callId)
        => _calls.TryGetValue(callId, out var context) && context.AudioEndPoint is { } audio
            ? audio.StopCallRecording()
            : null;

    /// <inheritdoc />
    public void PlayToRemote(string callId, short[] pcm, int sampleRate)
    {
        if (!_calls.TryGetValue(callId, out var context)) return;

        context.AudioEndPoint?.PlayToRemote(pcm, sampleRate);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Si agisce sugli INVITE in arrivo e non sulla registrazione. Deregistrarsi
    /// sarebbe il modo apparentemente più diretto per non ricevere chiamate, e sarebbe
    /// sbagliato per tre motivi: il posto operatore risulterebbe spento anche a chi
    /// guarda il pannello del centralino, non potrebbe più <b>fare</b> chiamate — un
    /// operatore in pausa ha spesso proprio bisogno di chiamare qualcuno — e al ritorno
    /// in servizio servirebbe una nuova registrazione, con i secondi di attesa e i modi
    /// di fallire che comporta.
    /// </para>
    /// <para>
    /// Le chiamate già instaurate non vengono toccate: vedi
    /// <see cref="ITelephonyEngine.SetDoNotDisturb"/>.
    /// </para>
    /// </remarks>
    public void SetDoNotDisturb(bool enabled)
    {
        _doNotDisturb = enabled;

        _log.LogInformation(
            "Postazione {Stato}: le chiamate entranti vengono {Esito}",
            enabled ? "in pausa" : "in servizio",
            enabled ? "rifiutate con 480 Temporarily Unavailable" : "accettate");
    }

    // -----------------------------------------------------------------------
    // Eventi delle chiamate
    // -----------------------------------------------------------------------

    private void WireCallEvents(CallContext context)
    {
        var agent = context.Agent;
        var callId = context.CallId;

        agent.ClientCallRinging += (_, response) =>
        {
            // Un 180 o 183 che porta con se' una descrizione di sessione significa
            // che la controparte sta gia' mandando audio: e' il tono di libero del
            // gestore, quello che si sente chiamando un numero esterno. Senza
            // descrizione non arrivera' nulla fino alla risposta, ed e' il caso
            // tipico degli interni: il tono va generato in locale, altrimenti
            // l'operatore resta in un silenzio indistinguibile da una chiamata che
            // non e' partita.
            var hasEarlyMedia = !string.IsNullOrWhiteSpace(response?.Body);

            _log.LogDebug(
                "Chiamata {CallId}: la destinazione squilla, audio dalla rete: {EarlyMedia}",
                callId, hasEarlyMedia);

            // SI RIPIEGA MENTRE SQUILLA, NON DOPO CHE HANNO RISPOSTO.
            //
            // Il centralino manda la propria descrizione di sessione gia' nel 183, cioe'
            // prima che il chiamato sollevi: se li' non c'e' la cifratura, la si scopre
            // in tempo per richiamare in chiaro senza che nessuno abbia ancora alzato la
            // cornetta. Accorgersene solo alla risposta costerebbe al chiamato un primo
            // squillo interrotto e un secondo squillo subito dopo.
            if (context.OfferedSrtp && hasEarlyMedia && !DescribesSrtp(response?.Body))
            {
                HandleMissingEncryption(callId, context);
                return;
            }

            RaiseProgress(callId, CallTrigger.RemoteRinging, null, null, false, hasEarlyMedia);
        };

        agent.ClientCallAnswered += (_, _) =>
        {
            // LA CIFRATURA VA VERIFICATA ALLA RISPOSTA, NON SOLO SUL RIFIUTO.
            //
            // E' il caso che rende inutilizzabile la chiamata senza che nulla lo
            // segnali: il centralino accetta l'INVITE e risponde 200 OK negoziando
            // l'audio SENZA crypto. Non c'e' alcun rifiuto da intercettare, la
            // chiamata risulta attiva, ma la sessione e' in modalita' cifrata e lo
            // stack scarta i pacchetti in entrambe le direzioni: l'operatore e
            // l'interlocutore restano in una conversazione muta credendo di essere
            // collegati.
            //
            // E' successo davvero, verso una linea esterna: 1.105 pacchetti consegnati
            // allo stack, zero ricevuti, cento per cento di silenzio inventato.
            if (context.OfferedSrtp && !IsSecureContextReady(context))
            {
                HandleMissingEncryption(callId, context);
                return;
            }

            var (codec, wideband) = DescribeMedia(context);
            RaiseProgress(callId, CallTrigger.RemoteAnswered, null, codec, wideband);
        };

        // Il primo parametro si nomina invece di scartarlo: dentro il corpo serve il
        // simbolo di scarto per l'attivita' del secondo tentativo, e due "_" nello
        // stesso ambito sarebbero la stessa variabile.
        agent.ClientCallFailed += (uac, message, response) =>
        {
            // RIFIUTO DELL'OFFERTA CIFRATA: si richiama in chiaro invece di arrendersi.
            //
            // E' quello che rende praticabile la politica "preferita" su una rete
            // mista. Il secondo tentativo mantiene lo stesso identificativo di
            // chiamata, cosi' per il resto del programma — e per l'operatore — resta
            // la stessa telefonata: quello che sente e' un istante di attesa in piu',
            // non una chiamata fallita seguita da una nuova.
            if (context.OfferedSrtp
                && _account.Srtp == SrtpPolicy.Preferred
                && SrtpNegotiation.LooksLikeMediaRejection((int)(response?.Status ?? 0)))
            {
                _log.LogWarning(
                    "Chiamata {CallId}: {Status} sull'offerta cifrata, secondo tentativo in chiaro",
                    callId, response?.Status);

                _ = RetryInClearAsync(callId, context.Destination);
                return;
            }

            // UN ANNULLAMENTO NOSTRO NON E' UN RIFIUTO DELLA DESTINAZIONE.
            //
            // Chiudendo la consultazione che squilla per completare un trasferimento,
            // SIPSorcery segnala comunque un fallimento di chiamata: interpretarlo
            // come rifiuto faceva suonare in cuffia il tono di fuori servizio a ogni
            // trasferimento, e annunciare che su quel numero non si passa proprio
            // mentre la chiamata ci stava arrivando. Su un'interfaccia sonora e' una
            // informazione falsa detta nel momento peggiore.
            //
            // TACERE QUI OBBLIGA QUALCUN ALTRO A PARLARE.
            //
            // Questo ramo non solleva nessun evento, quindi la chiamata sparisce dal
            // motore senza che il resto del programma lo sappia: chi ha chiesto il
            // trasferimento deve chiudere la linea di consultazione anche nel proprio
            // modello. Lo fa TransferOrchestrator.CompleteAttendedTransferAsync, ed e'
            // l'unico posto: se domani un'altra strada mettesse una chiamata in
            // _locallyCancelled senza fare altrettanto, si riavrebbe la linea fantasma
            // con il tono di libero che non finisce piu'.
            if (_locallyCancelled.TryRemove(callId, out _))
            {
                _log.LogDebug("Chiamata {CallId}: annullata dalla postazione, nessun esito da annunciare", callId);
                RemoveCall(callId);
                return;
            }

            var reason = TranslateFailure(response?.Status, message);
            RaiseProgress(callId, CallTrigger.RemoteRejected, reason, null, false);
            RemoveCall(callId);
        };

        agent.OnCallHungup += (_) =>
        {
            // Lo stack solleva questo evento anche quando il BYE lo abbiamo mandato
            // noi. Riferirlo come riaggancio remoto farebbe annunciare "il chiamante
            // ha chiuso" a ogni chiusura decisa dall'operatore: la transizione locale
            // l'ha gia' applicata chi ha premuto il tasto, qui non serve altro che
            // liberare le risorse.
            if (context.ClosedLocally)
            {
                RemoveCall(callId);
                return;
            }

            RaiseProgress(callId, CallTrigger.RemoteHangUp, CallEndReason.RemoteHangUp, null, false);
            RemoveCall(callId);
        };

        agent.ServerCallCancelled += (_, cancelRequest) =>
        {
            // QUESTA E' LA RIGA CHE DICE CHI HA CHIUSO.
            //
            // Un CANCEL non nasce mai dentro questo programma: lo stack lo solleva
            // solo ricevendone uno dalla rete. Se compare, la chiamata l'ha ritirata
            // il centralino — o perche' e' scaduto il suo tempo di squillo, o perche'
            // ha riagganciato chi chiamava, e a distinguerli e' l'intestazione Reason
            // insieme alla durata: un tempo di squillo scaduto cade sempre allo stesso
            // secondo, una rinuncia no.
            _log.LogInformation(
                "Chiamata entrante {CallId} ritirata dal centralino con CANCEL {Durata}{Motivo}",
                callId,
                DescribeRinging(context),
                DescribeReason(cancelRequest?.Header?.Reason));

            RaiseProgress(callId, CallTrigger.RemoteHangUp, CallEndReason.RemoteHangUp, null, false);
            RemoveCall(callId);
        };

        // Scadenza della transazione di una chiamata entrante: lo stack ha smesso di
        // tenerla in piedi e nessun 200 OK partirebbe piu' da qui. Va riferito, perche'
        // altrimenti la chiamata resterebbe in squillo per l'operatore — con la
        // suoneria che suona — su una transazione che non esiste piu'.
        //
        // Con il tetto di squillo portato a un'ora e' un caso che in esercizio non si
        // vede: resta come rete di sicurezza, e copre anche la risposta mai confermata
        // dall'ACK del centralino.
        agent.ServerCallRingTimeout += _ =>
        {
            _log.LogWarning(
                "Chiamata entrante {CallId}: transazione scaduta dentro lo stack SIP {Durata}. "
                + "Non e' stato il centralino a chiudere: e' scaduto il tetto locale di squillo",
                callId, DescribeRinging(context));

            RaiseProgress(callId, CallTrigger.Failed, CallEndReason.NoAnswer, null, false);
            RemoveCall(callId);
        };

        // La firma dell'evento porta il solo frammento SIP: l'associazione alla
        // chiamata avviene per cattura, perche' ogni agente serve un dialogo solo.
        agent.OnTransferNotify += sipFrag => HandleTransferNotify(callId, sipFrag);

        // IL PERCORSO AUDIO PUO' CHIUDERE UNA CHIAMATA SENZA CHE LA SEGNALAZIONE DICA
        // NULLA, e finora lo faceva in silenzio.
        //
        // Lo stack sorveglia l'attivita' RTP e RTCP: quando non arriva piu' nulla
        // solleva questi eventi, e chi li ascolta puo' chiudere la chiamata. Sono la
        // seconda spiegazione possibile di una chiamata che "cade da sola", ed erano
        // l'unica che il registro non permetteva ne' di confermare ne' di escludere.
        context.Media.OnTimeout += mediaType =>
            _log.LogWarning(
                "Chiamata {CallId}: lo stack non riceve piu' pacchetti {Media} e ha dichiarato l'inattivita'",
                callId, mediaType);

        context.Media.OnRtcpBye += reason =>
            _log.LogInformation(
                "Chiamata {CallId}: la controparte ha chiuso il flusso audio con RTCP BYE ({Motivo})",
                callId, string.IsNullOrWhiteSpace(reason) ? "senza motivo dichiarato" : reason);

        context.Media.OnRtpClosed += reason =>
            _log.LogDebug("Chiamata {CallId}: percorso RTP chiuso ({Motivo})", callId, reason);

        // IL PRIMO PACCHETTO CHE ARRIVA DALLA RETE, ANNOTATO UNA VOLTA SOLA.
        //
        // Serve a distinguere due guasti che hanno lo stesso sintomo — chiamata muta
        // e "ricevuti 0" — e due correzioni completamente diverse:
        //
        //   - se questa riga NON compare, la controparte non ci sta mandando niente,
        //     e il problema e' nell'instradamento del flusso o nella descrizione di
        //     sessione;
        //   - se compare e i ricevuti restano zero, i pacchetti arrivano e si perdono
        //     dentro la libreria prima di raggiungere la riproduzione.
        //
        // Il 12 agosto 2026 questa distinzione e' costata un giro di prove sul campo,
        // perche' dal registro le due situazioni erano indistinguibili.
        var primoAnnotato = 0;

        context.Media.OnRtpPacketReceived += (remote, media, pacchetto) =>
        {
            // Una riga per chiamata, non una per pacchetto: a cinquanta pacchetti al
            // secondo la seconda forma renderebbe illeggibile il registro e cambierebbe
            // il comportamento di cio' che si sta misurando.
            if (Interlocked.Exchange(ref primoAnnotato, 1) == 1) return;

            _log.LogInformation(
                "Chiamata {CallId}: primo pacchetto {Media} ricevuto da {Origine}, payload {Payload}, "
                + "SSRC {Ssrc}",
                callId, media, remote, pacchetto.Header.PayloadType, pacchetto.Header.SyncSource);
        };
    }

    /// <summary>
    /// Interpreta i NOTIFY che seguono un REFER, secondo RFC 3515.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È l'unico modo di sapere se un trasferimento cieco è andato a buon fine.
    /// Senza, l'operatore invierebbe la chiamata verso un interno e resterebbe
    /// convinto che sia arrivata, anche quando il centralino l'ha respinta: il
    /// chiamante verrebbe perso in silenzio.
    /// </para>
    /// <para>
    /// Il corpo è un frammento di risposta SIP: <c>100 Trying</c> e <c>180 Ringing</c>
    /// sono avanzamenti, <c>200 OK</c> dichiara riuscita la chiamata trasferita, il
    /// resto è fallimento.
    /// </para>
    /// <para>
    /// <b>NESSUN NOTIFY SMONTA PIÙ LA LINEA CON IL CHIAMANTE.</b> È la correzione
    /// dell'11 agosto 2026, e nasce da due tentativi sbagliati prima di questo.
    /// </para>
    /// <para>
    /// Il centralino manda un <c>200 OK</c> quarantatré millisecondi dopo il REFER,
    /// mentre l'interno di destinazione sta ancora squillando: nessuno può aver
    /// risposto in quel tempo. Chiudere la chiamata su quel segnale lasciava
    /// l'interno a squillare a vuoto e il chiamante abbandonato — per giunta senza
    /// <c>BYE</c>, cioè con il dialogo lasciato aperto invece che chiuso.
    /// </para>
    /// <para>
    /// Ho provato due volte a distinguere il <c>200</c> vero da quello prematuro:
    /// prima con lo stato della sottoscrizione, poi pretendendo un avanzamento
    /// precedente. Il primo criterio era sbagliato in partenza — per RFC 3515 una
    /// sottoscrizione REFER si chiude con <c>terminated;reason=noresource</c> anche
    /// quando tutto è andato bene — e il secondo si apre lo stesso, perché il
    /// centralino manda un <c>100 Trying</c> autentico dieci millisecondi prima.
    /// </para>
    /// <para>
    /// Si smette quindi di indovinare. <b>La linea la chiude il protocollo:</b> il
    /// <c>BYE</c> che arriva quando il trasferimento è davvero compiuto, oppure
    /// l'operatore. Se il centralino non lo mandasse mai, resta una linea da chiudere
    /// a mano — fastidioso e recuperabile con un tasto. Perdere il chiamante non lo è.
    /// </para>
    /// </remarks>
    private void HandleTransferNotify(string callId, string? sipFrag)
    {
        if (string.IsNullOrWhiteSpace(sipFrag)) return;

        var statusCode = ExtractStatusCode(sipFrag);

        switch (statusCode)
        {
            case >= 100 and < 200:
                // Avanzamento: nessun esito definitivo, si informa senza concludere.
                TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                    callId, null, "Trasferimento in corso", _clock.Now));
                break;

            case >= 200 and < 300:
                // Il centralino ha accettato e portato avanti il trasferimento. Si
                // annuncia, ma NON si tocca la linea: se il chiamante e' davvero
                // passato all'interno, il dialogo si chiudera' da se' con il BYE.
                _log.LogInformation(
                    "Trasferimento della chiamata {CallId}: il centralino segnala {Stato}. "
                    + "La linea resta aperta finche' non arriva il BYE",
                    callId, statusCode);

                TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                    callId, TransferOutcome.Succeeded, null, _clock.Now));
                break;

            case 486:
                TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                    callId, TransferOutcome.DestinationBusy, "Interno occupato", _clock.Now));
                break;

            case 408 or 480:
                TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                    callId, TransferOutcome.DestinationNoAnswer, "Nessuna risposta", _clock.Now));
                break;

            case 603 or 403:
                TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                    callId, TransferOutcome.DestinationRejected, "Trasferimento rifiutato", _clock.Now));
                break;

            case >= 400:
                TransferProgress?.Invoke(this, new TransferProgressEventArgs(
                    callId, TransferOutcome.Failed, "Trasferimento non riuscito", _clock.Now));
                break;
        }
    }

    /// <summary>
    /// Chiamate uscenti che la postazione ha annullato di proposito.
    /// </summary>
    /// <remarks>
    /// Serve a distinguere «l'ho chiusa io» da «me l'hanno rifiutata»: la libreria
    /// segnala allo stesso modo, e per l'operatore le due cose non si somigliano
    /// affatto.
    /// </remarks>
    private readonly ConcurrentDictionary<string, bool> _locallyCancelled = new();

    private static int ExtractStatusCode(string sipFrag)
    {
        // Il frammento inizia con la riga di stato: "SIP/2.0 200 OK".
        var parts = sipFrag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
    }

    private void RaiseProgress(
        string callId,
        CallTrigger trigger,
        CallEndReason? reason,
        string? codec,
        bool wideband,
        bool hasEarlyMedia = false)
    {
        CallProgress?.Invoke(this, new CallProgressEventArgs(
            callId, trigger, reason, codec, wideband, IsMediaEncrypted(callId), _clock.Now, hasEarlyMedia));
    }

    /// <summary>
    /// Dice se il flusso audio di una chiamata sta viaggiando cifrato.
    /// </summary>
    /// <param name="callId">Chiamata da interrogare.</param>
    /// <remarks>
    /// <para>
    /// <b>Prima qui c'era la costante <c>false</c>.</b> L'annuncio di qualità della
    /// linea la riportava come se fosse una misura, e diceva «chiamata non protetta» in
    /// ogni circostanza: anche se l'audio fosse stato cifrato, avrebbe detto lo stesso.
    /// Un'informazione di sicurezza che non può mai cambiare valore non è
    /// un'informazione, ed è peggio di nessuna informazione — perché sembra una
    /// verifica ed è una dichiarazione.
    /// </para>
    /// <para>
    /// Ora si chiede alla sessione media se il contesto sicuro è pronto. Oggi risponde
    /// di no, perché <see cref="SrtpPolicy"/> è dichiarata nelle opzioni dell'account ma
    /// non viene applicata da nessuna parte: l'audio viaggia in RTP in chiaro anche con
    /// la segnalazione in TLS, che è un'altra cosa e un altro strato. La differenza
    /// rispetto a prima è che adesso il valore <b>segue la realtà</b>: il giorno in cui
    /// SRTP verrà attivato, l'annuncio lo dirà da solo.
    /// </para>
    /// </remarks>
    private bool IsMediaEncrypted(string callId)
    {
        if (!_calls.TryGetValue(callId, out var context)) return false;

        try
        {
            return context.Media.IsSecureContextReady();
        }
        catch (Exception ex)
        {
            // Interrogare una sessione che si sta chiudendo puo' sollevare: in dubbio
            // si risponde "non cifrato", che e' il ripiego prudente su una domanda di
            // sicurezza.
            _log.LogDebug(ex, "Stato di cifratura del flusso audio non leggibile");
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsSignallingEncrypted => _account.Transport == SipTransportKind.Tls;

    // -----------------------------------------------------------------------
    // Ausiliari
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parametro di trasporto da aggiungere agli indirizzi SIP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Senza di questo l'apertura del canale TLS non basta: lo stack sceglie il
    /// canale in base al trasporto <b>dichiarato nell'indirizzo</b>, e un indirizzo
    /// senza parametro vale UDP. Il risultato sarebbe un canale cifrato aperto e mai
    /// usato — cioè lo stesso difetto di prima, con una riga di registro in più che
    /// dice il contrario.
    /// </para>
    /// <para>
    /// Si usa <c>sip:</c> con <c>transport=tls</c> e non lo schema <c>sips:</c>:
    /// il secondo è formalmente più corretto ma impone TLS su <b>tutto</b> il
    /// percorso, compresi i tratti oltre il centralino, e diversi apparati lo
    /// rifiutano per questo. La riservatezza della tratta che ci riguarda —
    /// postazione verso centralino — è garantita comunque.
    /// </para>
    /// </remarks>
    private string TransportParameter => _account.TransportParameter;

    /// <summary>Indirizzo del registrar, con il trasporto dichiarato.</summary>
    private string RegistrarUri() => _account.EffectiveRegistrar + TransportParameter;

    /// <summary>Indirizzo da comporre, con il trasporto dichiarato.</summary>
    private string BuildTargetUri(string destination)
    {
        // Un indirizzo gia' completo di dominio viene dall'operatore o dalla rubrica:
        // si rispetta com'e', limitandosi ad aggiungere il trasporto se non lo porta.
        var uri = destination.Contains('@')
            ? destination
            : $"sip:{destination}@{_account.Domain}";

        return uri.Contains("transport=", StringComparison.OrdinalIgnoreCase)
            ? uri
            : uri + TransportParameter;
    }

    private SIPUserAgent CreateUserAgent()
    {
        var proxy = string.IsNullOrWhiteSpace(_account.OutboundProxy)
            ? null
            : SIPEndPoint.ParseSIPEndPoint(_account.OutboundProxy);

        return new SIPUserAgent(_transport, proxy);
    }

    /// <summary>
    /// Crea la sessione media per una chiamata, offrendo soltanto i codec
    /// configurati e nell'ordine configurato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La restrizione dei formati non è un dettaglio di ottimizzazione. Senza,
    /// SIPSorcery offre in SDP tutti e quattro i codec che sa codificare
    /// nell'ordine <c>PCMU, PCMA, G722, G729</c>, con due conseguenze indesiderate
    /// su un posto operatore europeo:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   <b>PCMU per primo.</b> È la variante mu-law, quella nordamericana. In
    ///   Europa lo standard G.711 è A-law, e diversi apparati accettano la prima
    ///   offerta valida: si finisce per negoziare la variante sbagliata senza alcun
    ///   vantaggio.
    ///   </description></item>
    ///   <item><description>
    ///   <b>G.722 in terza posizione.</b> Significa negoziare quasi sempre banda
    ///   stretta, e quindi non poter dimostrare in collaudo il requisito di banda
    ///   larga di EN 301 549 §6.1 sulla chiamata reale.
    ///   </description></item>
    /// </list>
    /// <para>
    /// L'endpoint WASAPI con l'instradamento a quattro vie è in
    /// <c>BlindPhone.Audio</c> e si collega qui sostituendo la sorgente: la
    /// restrizione dei formati resta valida com'è.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Costruisce la sessione media della chiamata.
    /// </summary>
    /// <param name="context">Raccoglitore dell'endpoint audio creato.</param>
    /// <param name="secure">
    /// Se proporre la cifratura del flusso audio. Deciso da
    /// <see cref="SrtpNegotiation"/> a partire dalla politica dell'account e, sulle
    /// chiamate entranti, da quello che ha offerto chi chiama.
    /// </param>
    private VoIPMediaSession CreateMediaSession(CallContextBuilder context, bool secure)
    {
        var preferred = AudioCodecCatalog.ParseList(_account.CodecPreference);
        var allowed = preferred.Select(ToSipSorceryCodec).ToHashSet();

        VoIPMediaSession session;

        if (_audioEngine is not null)
        {
            // Percorso reale: microfono verso RTP e RTP verso cuffia.
            // Due istanze distinte, una per direzione: i due percorsi girano su
            // thread diversi e i codec a stato interno non tollerano la condivisione.
            var endPoint = new SipAudioEndPoint(
                new AudioEncoder(),
                new AudioEncoder(),
                _audioEngine,
                _audioTuning,
                _audioEndPointLogger);
            endPoint.RestrictFormats(format => allowed.Contains(format.Codec));

            context.AudioEndPoint = endPoint;

            session = new AudioOnlyMediaSession(new VoIPMediaSessionConfig
            {
                MediaEndPoint = new MediaEndPoints
                {
                    AudioSource = endPoint,
                    AudioSink = endPoint,
                },

                // SDES su TLS: le chiavi viaggiano dentro la descrizione di sessione,
                // che e' protetta dal trasporto. E' la strada che i centralini
                // aziendali espongono; DTLS-SRTP e' quella del mondo WebRTC e quasi
                // nessun PBX SIP la offre.
                RtpSecureMediaOption = secure
                    ? RtpSecureMediaOptionEnum.SdpCryptoNegotiation
                    : RtpSecureMediaOptionEnum.None,
            }, _log);
        }
        else
        {
            // Nessun motore audio disponibile: si ripiega sul generatore di
            // silenzio. La chiamata si instaura e la segnalazione e' collaudabile,
            // ma non passa audio. Va registrato in modo visibile, perche' e'
            // esattamente la condizione che produce chiamate mute che cadono dopo
            // pochi secondi per timeout di inattivita' RTP.
            _log.LogWarning(
                "Motore audio non disponibile: la chiamata sara' muta e potrebbe cadere per inattivita' RTP");

            var silence = new AudioExtrasSource(new AudioEncoder(), new AudioSourceOptions
            {
                AudioSource = AudioSourcesEnum.Silence,
            });

            silence.RestrictFormats(format => allowed.Contains(format.Codec));

            session = new AudioOnlyMediaSession(new VoIPMediaSessionConfig
            {
                MediaEndPoint = new MediaEndPoints { AudioSource = silence },
                RtpSecureMediaOption = secure
                    ? RtpSecureMediaOptionEnum.SdpCryptoNegotiation
                    : RtpSecureMediaOptionEnum.None,
            }, _log);
        }

        if (secure)
        {
            // Le due suite della RFC 4568 che qualunque apparato riconosce, nell'ordine
            // in cui vanno offerte: prima l'autenticazione a 80 bit, poi quella a 32
            // per chi ha vincoli di banda. Le suite a 192 e 256 bit restano fuori
            // perche' pochi centralini le implementano, e una lista lunga di proposte
            // che la controparte non capisce e' un modo di far fallire la
            // negoziazione invece di renderla piu' sicura.
            session.SrtpCryptoSuites =
            [
                SDPSecurityDescription.CryptoSuites.AES_CM_128_HMAC_SHA1_80,
                SDPSecurityDescription.CryptoSuites.AES_CM_128_HMAC_SHA1_32,
            ];
        }

        ApplyCodecOrder(session, preferred);
        return session;
    }

    /// <summary>
    /// Riordina le capacità dichiarate in SDP secondo la preferenza configurata.
    /// </summary>
    /// <remarks>
    /// <see cref="AudioExtrasSource.RestrictFormats"/> filtra ma non ordina, e
    /// l'ordine in SDP è ciò che la controparte usa per scegliere. Le voci non
    /// riconosciute (in particolare <c>telephone-event</c>, che trasporta i DTMF)
    /// vengono conservate in coda: rimuoverle toglierebbe all'operatore la
    /// possibilità di navigare i menu vocali dei numeri che chiama.
    /// </remarks>
    private void ApplyCodecOrder(VoIPMediaSession session, IReadOnlyList<AudioCodec> preferred)
    {
        var track = session.AudioLocalTrack;
        if (track?.Capabilities is not { Count: > 0 } capabilities) return;

        var order = preferred
            .Select((codec, index) => (Name: AudioCodecCatalog.Describe(codec).ShortName, Index: index))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var ordered = capabilities
            .OrderBy(c => order.TryGetValue(c.Name(), out var index) ? index : int.MaxValue)
            .ToList();

        capabilities.Clear();
        foreach (var capability in ordered) capabilities.Add(capability);

        _log.LogInformation(
            "Codec offerti in ordine: {Codecs}",
            string.Join(", ", ordered.Select(c => c.Name())));
    }

    /// <summary>Traduce un codec di dominio nell'equivalente della libreria SIP.</summary>
    private static AudioCodecsEnum ToSipSorceryCodec(AudioCodec codec) => codec switch
    {
        AudioCodec.G722 => AudioCodecsEnum.G722,
        AudioCodec.PCMA => AudioCodecsEnum.PCMA,
        AudioCodec.G729 => AudioCodecsEnum.G729,
        AudioCodec.PCMU => AudioCodecsEnum.PCMU,
        _ => AudioCodecsEnum.PCMA,
    };

    /// <summary>
    /// Ricava nome e larghezza di banda del codec negoziato.
    /// </summary>
    /// <remarks>
    /// Non è diagnostica opzionale: EN 301 549 §6.1 richiede il supporto di una
    /// banda audio con limite superiore di almeno 7000 Hz, e in collaudo il
    /// requisito va dimostrato sulla chiamata reale. Questo dato alimenta il
    /// comando "Qualità linea", che è l'evidenza che l'operatore può produrre
    /// da solo.
    /// </remarks>
    private (string? Codec, bool IsWideband) DescribeMedia(CallContext context)
    {
        try
        {
            var capabilities = context.Media.AudioLocalTrack?.Capabilities;
            if (capabilities is null || capabilities.Count == 0) return (null, false);

            var format = capabilities[0];
            var name = format.Name();

            // Il criterio e' la frequenza di campionamento: da 16000 Hz in su si
            // superano i 7000 Hz di banda utile richiesti dalla norma.
            var wideband = format.ClockRate() >= 16000
                           || name.Contains("G722", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("OPUS", StringComparison.OrdinalIgnoreCase);

            return (name, wideband);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Lettura del codec negoziato non riuscita");
            return (null, false);
        }
    }

    private void RemoveCall(string callId)
    {
        if (!_calls.TryRemove(callId, out var context)) return;

        StopRingingRefresh(context);
        DetachFromConference(context.AudioEndPoint);

        try
        {
            context.Media.Close("chiamata terminata");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Chiusura della sessione media {CallId} non riuscita", callId);
        }
    }

    private string NextCallId() => $"call-{Interlocked.Increment(ref _callCounter)}";

    private bool IsLikelyInternal(string? number)
        => !string.IsNullOrWhiteSpace(number) && number.Length <= 5 && number.All(char.IsDigit);

    /// <summary>
    /// Aggiorna lo stato della registrazione e lo notifica, se è cambiato davvero.
    /// </summary>
    /// <param name="status">Stato raggiunto.</param>
    /// <param name="failureReason">Motivo del fallimento, quando c'è.</param>
    /// <remarks>
    /// <para>
    /// <b>L'evento si chiama «cambio di stato» e deve significarlo.</b> Prima veniva
    /// sollevato a ogni notifica dello stack, e lo stack ne produce una a ogni rinnovo
    /// della registrazione: trentasette al giorno sulla postazione di prova, tutte
    /// identiche alla precedente. Da lì partiva un annuncio con il proprio segnale
    /// acustico — e il segnale precede la voce ed è immune alla soppressione durante
    /// la conversazione, quindi l'operatore lo sentiva anche mentre parlava, senza la
    /// frase che lo spiegava.
    /// </para>
    /// <para>
    /// Il filtro sta qui e non in chi ascolta perché gli ascoltatori sono tre e
    /// diventeranno di più: chiedere a ciascuno di riconoscere le conferme dai
    /// cambiamenti significa che il primo che se ne dimentica riporta il difetto.
    /// </para>
    /// </remarks>
    private void SetRegistrationState(RegistrationStatus status, string? failureReason)
    {
        var aggiornato = new SipRegistrationState(status, _account.Name, failureReason, _clock.Now);

        if (_registrationState.RepresentsSameStateAs(aggiornato))
        {
            // Il rinnovo periodico finisce qui. Resta nel registro come traccia della
            // segnalazione SIP, che e' il posto dove serve.
            _log.LogDebug(
                "Registrazione confermata dal centralino, stato invariato: {Stato}", status);

            return;
        }

        _registrationState = aggiornato;
        RegistrationStateChanged?.Invoke(this, _registrationState);
    }

    /// <summary>
    /// Traduce un messaggio di errore di registrazione in linguaggio comprensibile.
    /// </summary>
    /// <remarks>
    /// Il messaggio finisce in un annuncio vocale. "Credenziali rifiutate dal
    /// centralino" permette all'operatore di chiamare l'assistenza sapendo cosa
    /// dire; "403 Forbidden" no.
    /// </remarks>
    private static string TranslateRegistrationError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Registrazione non riuscita";

        if (message.Contains("403", StringComparison.Ordinal)
            || message.Contains("401", StringComparison.Ordinal)
            || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "Credenziali rifiutate dal centralino";
        }

        if (message.Contains("404", StringComparison.Ordinal))
            return "Interno non riconosciuto dal centralino";

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("408", StringComparison.Ordinal))
        {
            return "Centralino non raggiungibile";
        }

        return "Registrazione non riuscita";
    }

    /// <summary>Traduce un codice di risposta SIP nel motivo di chiusura di dominio.</summary>
    private static CallEndReason TranslateFailure(SIPResponseStatusCodesEnum? status, string? message)
        => status switch
        {
            SIPResponseStatusCodesEnum.BusyHere or SIPResponseStatusCodesEnum.BusyEverywhere
                => CallEndReason.Busy,
            SIPResponseStatusCodesEnum.TemporarilyUnavailable
                => CallEndReason.NoAnswer,
            SIPResponseStatusCodesEnum.RequestTimeout
                => CallEndReason.NoAnswer,
            SIPResponseStatusCodesEnum.NotFound
                => CallEndReason.NotFound,
            SIPResponseStatusCodesEnum.Decline
                => CallEndReason.Rejected,
            SIPResponseStatusCodesEnum.Forbidden
                => CallEndReason.NotAuthorized,
            null when message?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true
                => CallEndReason.NoAnswer,
            _ => CallEndReason.Unknown,
        };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Raccoglitore usato durante la costruzione della sessione media, per
    /// recuperare l'endpoint audio prima che il contesto della chiamata esista.
    /// </summary>
    private sealed class CallContextBuilder
    {
        public SipAudioEndPoint? AudioEndPoint { get; set; }
    }

    /// <summary>Stato di una singola chiamata gestita dal motore.</summary>
    private sealed class CallContext(string callId, SIPUserAgent agent, VoIPMediaSession media, string destination)
    {
        public string CallId { get; } = callId;

        public SIPUserAgent Agent { get; } = agent;

        public VoIPMediaSession Media { get; } = media;

        public string Destination { get; } = destination;

        public SIPServerUserAgent? ServerUserAgent { get; init; }

        /// <summary>
        /// Se questa chiamata è stata offerta con la cifratura del flusso audio.
        /// </summary>
        /// <remarks>
        /// Serve a decidere se un rifiuto del centralino merita un secondo tentativo
        /// in chiaro. Senza, si ritenterebbe anche una chiamata che era già in chiaro,
        /// cioè si comporrebbe due volte un numero che ha appena rifiutato.
        /// </remarks>
        public bool OfferedSrtp { get; init; }

        /// <summary>
        /// Endpoint audio della chiamata, quando il percorso reale è attivo.
        /// </summary>
        /// <remarks>
        /// Conservato per due motivi: silenziare il microfono senza toccare il
        /// dispositivo di sistema, e leggere i contatori dei pacchetti RTP inviati
        /// e ricevuti, che sono la prima evidenza da guardare quando qualcuno dice
        /// "non ti sento".
        /// </remarks>
        public SipAudioEndPoint? AudioEndPoint { get; init; }

        /// <summary>
        /// Indica che la chiusura è stata avviata da questa postazione.
        /// </summary>
        /// <remarks>
        /// Serve a non attribuire all'interlocutore un gesto dell'operatore. Lo
        /// stack solleva <c>OnCallHungup</c> anche quando il <c>BYE</c> lo mandiamo
        /// noi: senza distinguere, ogni riaggancio dell'operatore veniva annunciato
        /// come "il chiamante ha chiuso". È una bugia che l'operatore non vedente non
        /// ha modo di verificare, ed è la stessa classe di errore per cui esiste il
        /// gate di copertura degli annunci.
        /// </remarks>
        public bool ClosedLocally { get; set; }

        /// <summary>
        /// Istante in cui la chiamata entrante ha cominciato a squillare.
        /// </summary>
        /// <remarks>
        /// Serve a poter scrivere nel registro <b>dopo quanto</b> è finito lo squillo.
        /// È il dato che distingue un tempo di squillo scaduto sul centralino — che
        /// arriva sempre allo stesso secondo, chiamata dopo chiamata — da una rinuncia
        /// del chiamante, che arriva quando capita. Senza, restano due righe con
        /// un'ora accanto e la sottrazione da fare a mano.
        /// </remarks>
        public DateTimeOffset? RingingSince { get; init; }

        /// <summary>
        /// Comanda la ripetizione del <c>180 Ringing</c> mentre la chiamata squilla.
        /// </summary>
        public CancellationTokenSource? RingingKeepAlive { get; set; }
    }
}
