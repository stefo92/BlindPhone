using System.Net;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;

namespace BlindPhone.Telephony;

/// <summary>
/// Sessione media che accetta soltanto i flussi che ha offerto.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nasce da un guasto in esercizio del 12 agosto 2026: tutte le chiamate uscenti
/// erano mute in entrambi i versi.</b> Le entranti funzionavano. Il registro diceva
/// «Inviati 479, ricevuti 0» su ogni chiamata partita dalla postazione, con il cento
/// per cento di silenzio inventato in riproduzione.
/// </para>
/// <para>
/// <b>La causa sta nella risposta del centralino.</b> Il posto operatore offre un
/// flusso solo:
/// </para>
/// <code>
/// m=audio 57476 RTP/AVP 9 8 18 101
/// </code>
/// <para>
/// e da quella mattina il centralino ha cominciato a rispondere con due:
/// </para>
/// <code>
/// m=audio 46592 RTP/AVP 18 101
/// m=video 46630 RTP/AVP 99 34 98 104 100
/// </code>
/// <para>
/// Una risposta SDP <b>non può aggiungere flussi</b>: l'RFC 3264 §6 impone che abbia
/// lo stesso numero di descrizioni dell'offerta, nello stesso ordine, e che quelle
/// non accettate siano rifiutate con porta zero. Aggiungerne una è una violazione, ma
/// il centralino la fa e il posto operatore ci deve convivere: la libreria SIP prende
/// per buona la seconda descrizione, apre una traccia video remota su una sessione
/// che di locale ha il solo audio, e da quel momento i pacchetti audio in arrivo non
/// raggiungono più il percorso di riproduzione. Zero ricevuti, zero scartati: non
/// arrivano proprio.
/// </para>
/// <para>
/// <b>Le chiamate entranti non ne risentivano</b>, ed è la prova che indica la
/// direzione: lì l'offerta la fa il centralino e la risposta la costruisce il posto
/// operatore, che offre e accetta solo audio.
/// </para>
/// <para>
/// <b>Si filtrano le risposte, non le offerte.</b> Su un invito in arrivo la
/// descrizione remota <i>è</i> l'offerta, e toglierne un flusso farebbe costruire una
/// risposta con meno descrizioni di quante ne siano state offerte — cioè la stessa
/// violazione, commessa da noi. Su un'offerta con video ci pensa la libreria a
/// rifiutarlo nei modi previsti.
/// </para>
/// <para>
/// <b>Con ICE acceso (RFC 8445) la sessione porta anche l'agente ICE.</b> Il socket audio
/// è un <see cref="IceRtpChannel"/>, le descrizioni di sessione che la postazione scrive
/// portano credenziali e candidati, e quando le verifiche scelgono una coppia l'audio si
/// sposta lì, qualunque cosa dicessero le righe <c>c=</c> e <c>m=</c>. Se il centralino
/// risponde senza ICE l'agente si ferma e tutto procede come senza: vedi
/// <see cref="Ice"/>.
/// </para>
/// </remarks>
public sealed class AudioOnlyMediaSession : VoIPMediaSession
{
    /// <summary>
    /// Opzione ICE della sessione in costruzione, letta dal canale audio.
    /// </summary>
    /// <remarks>
    /// La libreria apre il socket audio dentro il proprio costruttore, chiamando
    /// <see cref="CreateRtpChannel"/> prima che quello di questa classe abbia potuto
    /// assegnare un solo campo. L'unico modo di far arrivare l'opzione fin lì è passarla
    /// sul thread che sta costruendo: la si scrive un istante prima di chiamare il
    /// costruttore della libreria, e il canale la legge e la azzera subito.
    /// </remarks>
    [ThreadStatic]
    private static bool t_iceInCostruzione;

    private readonly ILogger _log;
    private readonly IceRtpChannel? _ice;
    private readonly Lock _iceLock = new();

    private bool _remoteSeen;
    private bool _initialOfferByUs;
    private bool _iceAbandoned;
    private RemoteIceParameters? _remoteIce;
    private IPEndPoint? _nominated;
    private volatile IceStatus? _iceStatus;
    private volatile IReadOnlyList<IceLocalCandidate> _localCandidates = [];

    /// <summary>Crea la sessione.</summary>
    /// <param name="config">Configurazione della sessione media.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="ice">
    /// Se aprire il socket audio con l'agente ICE. Spento, la sessione è identica a quella
    /// di prima che ICE ci fosse.
    /// </param>
    public AudioOnlyMediaSession(VoIPMediaSessionConfig config, ILogger? logger = null, bool ice = false)
        : base(PrepareConstruction(config, ice))
    {
        t_iceInCostruzione = false;
        _log = logger ?? NullLogger.Instance;

        if (!ice) return;

        _ice = AudioStream?.GetRTPChannel() as IceRtpChannel;

        if (_ice is null)
        {
            _log.LogWarning("ICE richiesto ma il socket audio e' stato aperto senza: la chiamata procede senza ICE");
            return;
        }

        _iceStatus = IceStatus.Waiting;
        _ice.OnIceConnectionStateChange += OnIceConnectionStateChange;

        // Senza server STUN o TURN la raccolta finisce subito: i candidati li sceglie
        // IceCandidatePlan. Serve per avviare la ricezione e il ciclo delle verifiche,
        // che resta fermo finche' non arrivano le credenziali del centralino.
        _ice.StartGathering();
    }

    private static VoIPMediaSessionConfig PrepareConstruction(VoIPMediaSessionConfig config, bool ice)
    {
        // Il controllo che la libreria fa dopo aver aperto il socket si anticipa qui: se
        // fallisse la', l'opzione resterebbe scritta sul thread e la sessione successiva
        // si troverebbe ICE senza averlo chiesto.
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(config.MediaEndPoint);

        t_iceInCostruzione = ice;
        return config;
    }

    /// <summary>
    /// Lo stato ICE della chiamata; <c>null</c> se la sessione non ha l'agente ICE.
    /// </summary>
    public IceStatus? Ice => _iceStatus;

    /// <summary>Candidati ICE annunciati nell'ultima descrizione di sessione scritta.</summary>
    public IReadOnlyList<IceLocalCandidate> IceLocalCandidates => _localCandidates;

    /// <summary>Sollevato a ogni cambio dello stato ICE, dal thread delle verifiche.</summary>
    public event Action<IceStatus>? IceStatusChanged;

    /// <inheritdoc />
    protected override RTPChannel CreateRtpChannel()
    {
        if (!t_iceInCostruzione) return base.CreateRtpChannel();

        // Si azzera subito: un secondo flusso aperto piu' tardi dalla libreria, per
        // esempio per un video offerto dal centralino, deve avere il socket normale.
        t_iceInCostruzione = false;

        var channel = new IceRtpChannel(
            rtpSessionConfig.BindAddress,
            rtpSessionConfig.BindPort,
            rtpSessionConfig.RtpPortRange);

        // Come fa la libreria con il canale normale: RTP e RTCP arrivano entrambi da
        // qui, e le verifiche STUN le intercetta il canale prima che arrivino alla
        // sessione.
        channel.OnRTPDataReceived += OnReceive;
        channel.Start();

        return channel;
    }

    /// <summary>
    /// Indirizzo della postazione sul collegamento che porta al centralino, da usare
    /// nelle offerte quando chi le chiede non ne indica uno.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>È la correzione dell'audio a senso unico con la VPN parziale.</b> Senza
    /// indicazioni la libreria SIP scrive nella prima offerta l'indirizzo della scheda di
    /// rete su cui passa la strada verso Internet. In sede è quello giusto; da casa, con
    /// una VPN che porta in ufficio solo il traffico per la rete dell'ente, è l'indirizzo
    /// della rete di casa, che il centralino non raggiunge: la voce dell'operatore parte
    /// dal tunnel e arriva, quella dell'interlocutore viene mandata a un indirizzo
    /// domestico e si perde.
    /// </para>
    /// <para>
    /// Le offerte successive — l'attesa, la ripresa — non avevano il problema, perché a
    /// quel punto la libreria conosce già l'indirizzo della controparte e sceglie da sé il
    /// collegamento giusto. Riguarda la sola prima offerta delle chiamate uscenti.
    /// </para>
    /// <para>
    /// Se l'interrogazione STUN ha stabilito che va annunciato l'indirizzo pubblico, quello
    /// sta sul canale audio e la libreria gli dà la precedenza: questo resta il ripiego.
    /// </para>
    /// </remarks>
    public IPAddress? PreferredLocalAddress { get; set; }

    /// <inheritdoc />
    public override SDP CreateOffer(IPAddress? connectionAddress = null)
    {
        var offer = base.CreateOffer(connectionAddress ?? PreferredLocalAddress);

        if (_ice is null) return offer;

        lock (_iceLock)
        {
            // La prima offerta della chiamata e' nostra se nessuna descrizione del
            // centralino e' ancora arrivata: e' la chiamata uscente. Le offerte
            // successive, per l'attesa e la ripresa, non cambiano il ruolo.
            if (!_remoteSeen) _initialOfferByUs = true;

            // Abbandonato ICE perche' il centralino non lo usa, non lo si ripropone a
            // ogni attesa: la risposta sarebbe la stessa.
            if (!_iceAbandoned) AnnounceIce(offer, rtcpMux: true);
        }

        return offer;
    }

    /// <inheritdoc />
    public override SDP CreateAnswer(IPAddress? connectionAddress = null)
    {
        var answer = base.CreateAnswer(connectionAddress);

        if (_ice is null) return answer;

        lock (_iceLock)
        {
            // Si risponde con ICE solo a chi l'ha offerto, e con l'RTCP sul socket
            // dell'RTP solo se l'ha offerto: la RFC 5761 vieta di accettarlo non offerto.
            if (_remoteIce is { } remoto && !_iceAbandoned) AnnounceIce(answer, remoto.RtcpMux);
        }

        return answer;
    }

    /// <inheritdoc />
    public override SetDescriptionResultEnum SetRemoteDescription(SdpType sdpType, SDP sessionDescription)
    {
        var esito = SetRemoteDescriptionAudioOnly(sdpType, sessionDescription);

        if (esito == SetDescriptionResultEnum.OK && _ice is not null && sessionDescription is not null)
        {
            ApplyRemoteIce(sessionDescription);
        }

        return esito;
    }

    /// <inheritdoc />
    public override void Close(string reason)
    {
        // Ferma le verifiche e i controlli di consenso: il socket lo chiude la libreria,
        // ma i temporizzatori dell'agente vivrebbero oltre la chiamata.
        _ice?.Close();
        base.Close(reason);
    }

    /// <summary>
    /// Scrive credenziali e candidati ICE nel flusso audio della descrizione di sessione.
    /// </summary>
    /// <param name="description">Offerta o risposta appena costruita dalla libreria.</param>
    /// <param name="rtcpMux">Se dichiarare l'RTCP sul socket dell'RTP.</param>
    /// <remarks>
    /// Il candidato predefinito è quello che la libreria ha già scritto nelle righe
    /// <c>c=</c> e <c>m=</c>, scelto da <see cref="MediaAddressPolicy"/>: l'indirizzo
    /// pubblico se è stato deciso così, altrimenti quello della scheda verso il centralino.
    /// Deve comparire fra i candidati, e con la priorità più alta del suo tipo.
    /// </remarks>
    private void AnnounceIce(SDP description, bool rtcpMux)
    {
        var channel = _ice!;
        var audio = description.Media.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);

        if (audio is null || audio.Port == 0) return;

        var scritto = audio.Connection?.ConnectionAddress ?? description.Connection?.ConnectionAddress;
        IPAddress.TryParse(scritto, out var predefinito);

        var pubblico = channel.RTPSrflxEndPoint;
        var host = predefinito;

        if (pubblico is not null
            && predefinito is not null
            && MediaAddressPolicy.Normalize(predefinito).Equals(MediaAddressPolicy.Normalize(pubblico.Address)))
        {
            host = PreferredLocalAddress;
        }

        var candidati = IceCandidatePlan.Build(IceTranslation.LocalAddresses(), host, channel.RTPPort, pubblico);

        audio.IceUfrag = channel.LocalIceUser;
        audio.IcePwd = channel.LocalIcePassword;

        // «ice2» dichiara la RFC 8445 invece della 5245 (RFC 8445 §10): cambia il modo in
        // cui un agente completo calcola i tempi delle verifiche, e chi non lo conosce lo
        // ignora.
        audio.IceOptions = "ice2";
        audio.IceCandidates = [.. candidati.Select(c => c.ToSdpValue())];

        if (rtcpMux) audio.AddExtra("a=rtcp-mux");

        _localCandidates = candidati;

        _log.LogDebug(
            "ICE: annunciati {Quanti} candidati: {Candidati}",
            candidati.Count,
            string.Join("; ", candidati.Select(c => c.ToSdpValue())));
    }

    /// <summary>
    /// Legge ICE dalla descrizione del centralino e avvia, aggiorna o abbandona le verifiche.
    /// </summary>
    private void ApplyRemoteIce(SDP description)
    {
        IceStatus? nuovo = null;

        lock (_iceLock)
        {
            _remoteSeen = true;

            var remoto = RemoteIceParameters.Read(description);
            var channel = _ice!;

            if (remoto is null)
            {
                if (_remoteIce is null && !_iceAbandoned)
                {
                    // RFC 8445 §5.4: la controparte che risponde senza ICE non lo usa, e
                    // vale l'indirizzo della sua descrizione di sessione. Non e' un errore:
                    // e' quello che fanno i centralini che ICE non lo conoscono.
                    _iceAbandoned = true;
                    channel.Close();
                    nuovo = IceStatus.NotUsedByPeer;
                }
                else
                {
                    // Una nuova descrizione senza ICE dopo una coppia verificata, per
                    // esempio la ripresa dall'attesa scritta da un apparato che li toglie:
                    // la coppia buona resta quella verificata.
                    ReapplyNominated();
                }
            }
            else if (_iceAbandoned)
            {
                // Abbandonato una volta, non si riprende: l'agente e' fermo.
            }
            else if (_remoteIce is null)
            {
                _remoteIce = remoto;

                var controlla = IceRoles.IsControlling(_initialOfferByUs, remoto.Lite);

                // Il ruolo prima dei candidati: ogni coppia ricorda quello con cui nasce.
                if (!channel.SetControlling(controlla))
                {
                    _log.LogWarning(
                        "ICE: la libreria non permette di impostare il ruolo nelle verifiche; "
                        + "con un centralino ICE lite nessuno scegliera' la coppia");
                }

                channel.SetRemoteCredentials(remoto.Ufrag, remoto.Pwd);

                foreach (var candidato in remoto.Candidates) channel.AddRemoteCandidate(candidato);

                _log.LogDebug(
                    "ICE: {Quanti} candidati del centralino in verifica ({Scartati} scartati), centralino {Tipo}, "
                    + "RTCP sul socket dell'RTP {Mux}, postazione {Ruolo}",
                    remoto.Candidates.Count,
                    remoto.Ignored,
                    remoto.Lite ? "ICE lite" : "ICE completo",
                    remoto.RtcpMux ? "si'" : "no",
                    controlla ? "controlla" : "controllata");

                nuovo = new IceStatus(IceProgress.Checking, controlla, remoto.Candidates.Count, RemoteIsLite: remoto.Lite);
            }
            else
            {
                if (!string.Equals(remoto.Ufrag, _remoteIce.Ufrag, StringComparison.Ordinal)
                    || !string.Equals(remoto.Pwd, _remoteIce.Pwd, StringComparison.Ordinal))
                {
                    // Credenziali nuove sono un riavvio di ICE (RFC 8445 §9). L'agente della
                    // libreria non sa ricominciare con le stesse credenziali locali: si resta
                    // sulla coppia verificata, che regge finche' la rete non cambia davvero.
                    _log.LogWarning(
                        "ICE: il centralino ha riavviato ICE con credenziali nuove; il riavvio non e' "
                        + "supportato e l'audio resta sul percorso verificato");
                }

                ReapplyNominated();
            }
        }

        if (nuovo is not null) Publish(nuovo);
    }

    /// <summary>
    /// Rimette la destinazione dell'audio sulla coppia verificata.
    /// </summary>
    /// <remarks>
    /// La libreria, a ogni nuova descrizione del centralino, riporta la destinazione sulle
    /// righe <c>c=</c> e <c>m=</c>: è giusto senza ICE, e con ICE rimanderebbe la voce
    /// all'indirizzo che le verifiche avevano scartato a ogni ripresa dall'attesa.
    /// </remarks>
    private void ReapplyNominated()
    {
        if (_nominated is { } scelta) ApplyDestination(scelta);
    }

    private void ApplyDestination(IPEndPoint remote)
    {
        // Con l'RTCP sullo stesso socket la destinazione dei rapporti e' la stessa; senza,
        // il componente RTCP non e' stato verificato e si usa la convenzione della porta
        // successiva, quella che la descrizione di sessione avrebbe comunque implicato.
        var rtcp = _remoteIce?.RtcpMux == true
            ? remote
            : new IPEndPoint(remote.Address, remote.Port + 1);

        SetGlobalDestination(remote, rtcp);
    }

    private void OnIceConnectionStateChange(RTCIceConnectionState state)
    {
        var channel = _ice;
        if (channel is null) return;

        IceStatus? nuovo = null;

        lock (_iceLock)
        {
            var remoto = _remoteIce;
            var quanti = remoto?.Candidates.Count ?? 0;
            var lite = remoto?.Lite ?? false;

            switch (state)
            {
                case RTCIceConnectionState.connected when channel.NominatedEntry?.RemoteCandidate is { } scelto
                                                          && scelto.DestinationEndPoint is { } destinazione:
                    var punto = new IPEndPoint(MediaAddressPolicy.Normalize(destinazione.Address), destinazione.Port);
                    _nominated = punto;
                    ApplyDestination(punto);

                    nuovo = new IceStatus(
                        IceProgress.Connected, channel.IsController, quanti, punto, IceTranslation.Kind(scelto.type), lite);
                    break;

                case RTCIceConnectionState.disconnected:
                case RTCIceConnectionState.failed:
                    // Non si tocca la destinazione: se una coppia era stata scelta resta la
                    // migliore che si conosca, altrimenti vale quella della descrizione.
                    nuovo = new IceStatus(
                        _nominated is null ? IceProgress.Failed : IceProgress.Lost,
                        channel.IsController,
                        quanti,
                        _nominated,
                        RemoteIsLite: lite);
                    break;
            }
        }

        if (nuovo is not null) Publish(nuovo);
    }

    private void Publish(IceStatus status)
    {
        _iceStatus = status;

        try
        {
            IceStatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            // Arriva dal thread delle verifiche: un errore di chi ascolta non deve
            // fermarle.
            _log.LogWarning(ex, "Errore nella notifica dello stato ICE");
        }
    }

    private SetDescriptionResultEnum SetRemoteDescriptionAudioOnly(SdpType sdpType, SDP sessionDescription)
    {
        if (sdpType != SdpType.answer || sessionDescription is null)
        {
            return base.SetRemoteDescription(sdpType, sessionDescription!);
        }

        var estranei = sessionDescription.Media
            .Where(m => m.Media != SDPMediaTypesEnum.audio)
            .ToList();

        if (estranei.Count == 0) return base.SetRemoteDescription(sdpType, sessionDescription);

        _log.LogWarning(
            "Il centralino ha risposto con {Quanti} flussi che non erano stati offerti ({Tipi}): "
            + "vengono ignorati. Una risposta SDP non puo' aggiungere flussi (RFC 3264 §6), e "
            + "accettarli interrompe la ricezione dell'audio",
            estranei.Count,
            string.Join(", ", estranei.Select(m => m.Media.ToString())));

        return base.SetRemoteDescription(sdpType, WithAudioOnly(sessionDescription));
    }

    /// <summary>
    /// Ricostruisce la descrizione tenendo il solo audio.
    /// </summary>
    /// <param name="original">Descrizione ricevuta dal centralino.</param>
    /// <remarks>
    /// <b>Si lavora su una copia.</b> L'oggetto arriva dalla libreria, che lo conserva
    /// e lo rilegge: modificarlo sul posto significherebbe cambiare sotto i piedi di
    /// chi ce l'ha passato una struttura di cui non siamo proprietari. Il giro per il
    /// testo è il modo più sicuro di ottenere una copia indipendente, e costa una
    /// serializzazione per chiamata — cioè nulla, una volta per telefonata.
    /// </remarks>
    private SDP WithAudioOnly(SDP original)
    {
        try
        {
            var copia = SDP.ParseSDPDescription(original.ToString());
            copia.Media.RemoveAll(m => m.Media != SDPMediaTypesEnum.audio);

            return copia;
        }
        catch (Exception ex)
        {
            // Se la copia non riesce si prosegue con l'originale: una chiamata muta e'
            // meglio di una chiamata che non parte, e il registro dice cosa e'
            // successo.
            _log.LogError(ex, "Ripulitura della descrizione di sessione non riuscita");
            return original;
        }
    }
}
