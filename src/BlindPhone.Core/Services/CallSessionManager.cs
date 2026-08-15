using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Gestisce le chiamate del posto operatore: le tiene, ne applica le transizioni,
/// e garantisce che ogni cambiamento venga comunicato all'operatore.
/// </summary>
/// <remarks>
/// <para>
/// È il punto in cui gli eventi del motore telefonico diventano stato di dominio e
/// annunci. La regola che lo governa è una sola e non ammette eccezioni:
/// <b>nessuna transizione applicata senza annuncio prodotto</b>. Tutto passa da
/// <see cref="ApplyTrigger"/>, che è l'unico punto in cui una chiamata cambia stato,
/// e che produce sempre l'annuncio corrispondente.
/// </para>
/// <para>
/// Il servizio è indipendente dall'interfaccia: riceve eventi da
/// <see cref="ITelephonyEngine"/> e comandi dai gestori di scorciatoia, e non sa
/// nulla di WPF. Questo permette di collaudare scenari a più chiamate concorrenti
/// senza avviare la UI.
/// </para>
/// </remarks>
public sealed class CallSessionManager : IAsyncDisposable
{
    private readonly ITelephonyEngine _telephony;
    private readonly IAnnouncer _announcer;
    private readonly CallAnnouncementMapper _mapper;
    private readonly AnnouncementContext _context;
    private readonly IContactRepository? _contacts;
    private readonly IClock _clock;
    private readonly AnnouncementOptions _options;
    private readonly ILogger<CallSessionManager> _log;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Call> _calls = [];

    /// <summary>
    /// Gambe della conferenza in corso, nell'ordine in cui vi sono entrate.
    /// </summary>
    /// <remarks>
    /// L'ordine non è decorativo: sciogliendo la conferenza la prima resta in linea e
    /// le altre passano in attesa, e la prima è quella con cui l'operatore stava
    /// parlando quando ha premuto il tasto. Sciogliere lasciando in linea l'altra
    /// sarebbe corretto quanto arbitrario, e su un'interfaccia sonora l'arbitrario
    /// costa una domanda a voce alta per capire con chi si è rimasti.
    /// </remarks>
    private readonly List<string> _conferenceIds = [];

    private readonly ConferenceOptions _conference;

    private readonly IEarconPlayer? _earcons;

    private string? _activeCallId;
    private DateTimeOffset _lastHoldReminderAt;
    private int _callCounter;

    /// <summary>Chiamata per cui è in corso il tono di libero generato in locale.</summary>
    private string? _ringbackCallId;

    /// <summary>Chiamata entrante per cui la suoneria sta suonando.</summary>
    private string? _ringerCallId;

    /// <summary>
    /// Cadenza del tono di libero italiano: un secondo di tono, quattro di silenzio.
    /// </summary>
    /// <remarks>
    /// Il tono dura un secondo ed è generato dal riproduttore; qui si dichiara solo
    /// la pausa che lo separa dalla ripetizione successiva. Sommate danno i cinque
    /// secondi di periodo che l'operatore riconosce come "sta squillando".
    /// </remarks>
    private static readonly TimeSpan RingbackSilence = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Pausa fra uno squillo e il successivo sulla chiamata entrante.
    /// </summary>
    /// <remarks>
    /// Stessa cadenza del tono di libero, per la stessa ragione: quattro secondi di
    /// silenzio fra due squilli sono la cadenza telefonica italiana, e l'operatore la
    /// riconosce senza doverla imparare. La suoneria dura quanto il segnale scelto —
    /// il file dell'operatore o l'earcon sintetizzato — e la pausa si somma a quella.
    /// </remarks>
    private static readonly TimeSpan RingerSilence = TimeSpan.FromSeconds(4);

    /// <summary>Crea il gestore delle chiamate.</summary>
    /// <param name="telephony">Motore telefonico.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="mapper">Mappatore transizione/annuncio.</param>
    /// <param name="context">Contesto operativo condiviso con la pipeline di annuncio.</param>
    /// <param name="contacts">Rubrica per la risoluzione dei nomi, facoltativa.</param>
    /// <param name="options">Parametri di comportamento.</param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="earcons">
    /// Riproduttore dei segnali acustici, per il tono di libero locale. Facoltativo:
    /// senza, le chiamate uscenti verso destinazioni che non mandano audio in banda
    /// restano silenziose fino alla risposta.
    /// </param>
    /// <param name="conference">
    /// Nomi dei tasti citati dagli annunci della conferenza a tre.
    /// </param>
    public CallSessionManager(
        ITelephonyEngine telephony,
        IAnnouncer announcer,
        CallAnnouncementMapper mapper,
        AnnouncementContext context,
        IContactRepository? contacts = null,
        AnnouncementOptions? options = null,
        IClock? clock = null,
        ILogger<CallSessionManager>? logger = null,
        IEarconPlayer? earcons = null,
        ConferenceOptions? conference = null)
    {
        _telephony = telephony;
        _announcer = announcer;
        _mapper = mapper;
        _context = context;
        _contacts = contacts;
        _options = options ?? AnnouncementOptions.Default;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<CallSessionManager>.Instance;
        _earcons = earcons;
        _conference = conference ?? new ConferenceOptions();

        _telephony.IncomingCall += OnIncomingCall;
        _telephony.CallProgress += OnCallProgress;
        _telephony.RegistrationStateChanged += OnRegistrationStateChanged;
        _telephony.SrtpDowngraded += OnSrtpDowngraded;
    }

    /// <summary>
    /// Annuncia che la chiamata sta ripartendo senza cifratura della voce.
    /// </summary>
    /// <remarks>
    /// Passa dalla pipeline con priorità alta perché deve arrivare <b>mentre la
    /// chiamata parte</b>: è l'ultimo istante in cui l'operatore può decidere di non
    /// dire al telefono quello che stava per dire.
    /// </remarks>
    private void OnSrtpDowngraded(object? sender, string destination)
    {
        _log.LogWarning("Cifratura della voce non accettata da {Destination}: si prosegue in chiaro", destination);

        _announcer.Announce(Announcement.High(
            // Il raggruppamento delle cifre lo conosce il mappatore degli annunci, non
            // le opzioni della pipeline: si legge cifra per cifra, che e' la forma in
            // cui una destinazione va sempre detta.
            AnnouncementTexts.SrtpDowngraded(SpeechFormatter.Digits(destination)),
            _clock.Now,
            EarconKind.PbxLinkDegraded,
            coalesceKey: "srtp:downgrade"));
    }

    /// <summary>Sollevato dopo ogni transizione di stato applicata a una chiamata.</summary>
    public event EventHandler<CallStateChangedEventArgs>? CallStateChanged;

    /// <summary>Chiamata attualmente in conversazione, se presente.</summary>
    public Call? ActiveCall
    {
        get
        {
            lock (_gate)
            {
                return _activeCallId is not null && _calls.TryGetValue(_activeCallId, out var call)
                    ? call
                    : null;
            }
        }
    }

    /// <summary>Chiamate attualmente in attesa.</summary>
    public IReadOnlyList<Call> HeldCalls
    {
        get
        {
            lock (_gate) return [.. _calls.Values.Where(c => c.State == CallState.Held)];
        }
    }

    /// <summary>
    /// Chiamate che dall'altra parte stanno aspettando l'operatore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>È <see cref="HeldCalls"/> più il chiamante parcheggiato dentro un
    /// trasferimento</b>, e serve a tutto ciò che risponde alla domanda «chi sta
    /// aspettando»: le caselle di riepilogo, il conteggio delle attese, la riga di
    /// stato.
    /// </para>
    /// <para>
    /// La distinzione fra <see cref="CallState.Held"/> e
    /// <see cref="CallState.Transferring"/> è vera e utile dentro la macchina a stati —
    /// dice se sulla chiamata è aperta una procedura — ma <b>a chi aspetta non cambia
    /// niente</b>: sente la stessa musica e conta gli stessi minuti. Mostrando le sole
    /// <see cref="HeldCalls"/>, per tutta la durata di un trasferimento con annuncio la
    /// finestra dichiarava «in attesa: nessuna chiamata» mentre qualcuno era in attesa,
    /// cioè proprio nel momento in cui dimenticarsene costa di più.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Call> WaitingCalls
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. _calls.Values.Where(c =>
                        c.State is CallState.Held or CallState.Transferring)
                ];
            }
        }
    }

    /// <summary>Tutte le chiamate non ancora terminate.</summary>
    public IReadOnlyList<Call> LiveCalls
    {
        get
        {
            lock (_gate) return [.. _calls.Values.Where(c => !CallStateMachine.IsTerminal(c.State))];
        }
    }

    /// <summary>Chiamata entrante in squillo, se presente.</summary>
    public Call? RingingCall
    {
        get
        {
            lock (_gate) return _calls.Values.FirstOrDefault(c => c.State == CallState.Alerting);
        }
    }

    /// <summary>
    /// Chiamata coinvolta in una procedura di trasferimento, se presente.
    /// </summary>
    /// <remarks>
    /// Serve a poterla chiudere. Durante un trasferimento il chiamante non è né
    /// attivo né in attesa ma in uno stato suo, e finché quello stato non compariva
    /// fra i bersagli del riaggancio l'operatore si sentiva rispondere <i>"nessuna
    /// chiamata attiva"</i> mentre la conversazione era ancora aperta: il comando
    /// non trovava nulla da chiudere e nessun <c>BYE</c> partiva.
    /// </remarks>
    public Call? TransferringCall
    {
        get
        {
            lock (_gate) return _calls.Values.FirstOrDefault(c => c.State == CallState.Transferring);
        }
    }

    /// <summary>
    /// Chiamata uscente in corso di instaurazione, se presente.
    /// </summary>
    /// <remarks>
    /// È la chiamata che l'operatore ha appena fatto partire e che sta squillando
    /// dall'altra parte. Serve a poterla chiudere: prima non compariva fra i
    /// bersagli del comando di riaggancio, quindi chi ci ripensava mentre l'interno
    /// squillava non aveva modo di rinunciare.
    /// </remarks>
    public Call? OutgoingCall
    {
        get
        {
            lock (_gate) return _calls.Values.FirstOrDefault(c => c.State == CallState.Calling);
        }
    }

    /// <summary>
    /// Partecipanti alla conferenza a tre, nell'ordine in cui vi sono entrati.
    /// </summary>
    /// <remarks>
    /// Vuota quando non c'è nessuna conferenza in corso e anche quando la conferenza
    /// è parcheggiata in attesa: qui compaiono solo le gambe che stanno davvero
    /// sentendo l'operatore. Per il caso parcheggiato c'è
    /// <see cref="ParkedConference"/>.
    /// </remarks>
    public IReadOnlyList<Call> ConferencedCalls => ConferenceLegs(CallState.Conferenced);

    /// <summary>
    /// Partecipanti a una conferenza messa in attesa, che il comando di conferenza o
    /// quello di ripresa possono riportare in linea.
    /// </summary>
    /// <remarks>
    /// La conferenza non si scioglie quando la si mette in attesa: si parcheggia
    /// intera. Riprenderne una gamba sola e lasciare l'altra parcheggiata sarebbe il
    /// comportamento più facile da scrivere e il più difficile da capire ad
    /// orecchio — l'operatore riprende «la chiamata» e si ritrova con metà delle
    /// persone che aveva.
    /// </remarks>
    public IReadOnlyList<Call> ParkedConference
    {
        get
        {
            var legs = ConferenceLegs(CallState.Held);
            return legs.Count >= 2 ? legs : [];
        }
    }

    /// <summary><c>true</c> se una conferenza a tre è in corso in questo momento.</summary>
    public bool IsConferenceActive => ConferencedCalls.Count > 0;

    /// <summary>Gambe della conferenza che si trovano nello stato richiesto.</summary>
    /// <remarks>
    /// L'elenco degli identificativi si ripulisce da sé: una gamba che ha lasciato la
    /// conferenza o che è terminata semplicemente non compare più nel risultato,
    /// senza bisogno di tenere sincronizzate due strutture.
    /// </remarks>
    private IReadOnlyList<Call> ConferenceLegs(CallState state)
    {
        lock (_gate)
        {
            if (_conferenceIds.Count == 0) return [];

            return
            [
                .. _conferenceIds
                    .Select(_calls.GetValueOrDefault)
                    .OfType<Call>()
                    .Where(c => c.State == state)
            ];
        }
    }

    /// <summary>Recupera una chiamata per identificativo.</summary>
    /// <param name="callId">Identificativo locale.</param>
    public Call? GetCall(string callId)
    {
        lock (_gate) return _calls.GetValueOrDefault(callId);
    }

    // -----------------------------------------------------------------------
    // Comandi dell'operatore
    // -----------------------------------------------------------------------

    /// <summary>
    /// Risponde alla chiamata in squillo. Se ce n'è più di una, risponde a quella
    /// che squilla da più tempo.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se c'era una chiamata a cui rispondere.</returns>
    /// <remarks>
    /// Se una conversazione è già in corso, la chiamata attiva viene messa in attesa
    /// automaticamente: è il comportamento atteso su un posto operatore, dove
    /// rispondere a una seconda linea non significa buttare giù la prima.
    /// </remarks>
    public async Task<bool> AnswerAsync(CancellationToken cancellationToken = default)
    {
        var ringing = RingingCall;
        if (ringing is null)
        {
            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.CommandNotAvailable, _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        // Con una conferenza in corso non c'e' una "chiamata attiva" da mettere in
        // attesa: ce ne sono due, e vanno parcheggiate insieme. Metterne in attesa una
        // sola lascerebbe l'altra in linea a sentire l'operatore rispondere a un
        // terzo, che e' il modo piu' rapido di far ascoltare a un chiamante una
        // conversazione che non lo riguarda.
        if (IsConferenceActive)
        {
            await ParkConferenceAsync(announce: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var active = ActiveCall;
            if (active is not null && active.Id != ringing.Id)
            {
                await _telephony.HoldAsync(active.Id, cancellationToken).ConfigureAwait(false);
                ApplyTrigger(active.Id, CallTrigger.Hold);
            }
        }

        await _telephony.AnswerAsync(ringing.Id, cancellationToken).ConfigureAwait(false);
        ApplyTrigger(ringing.Id, CallTrigger.Answer);
        return true;
    }

    /// <summary>Riaggancia la chiamata attiva, o rifiuta quella in squillo.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se c'era una chiamata da chiudere.</returns>
    public async Task<bool> HangUpAsync(CancellationToken cancellationToken = default)
    {
        // La conferenza viene prima di tutto, e si chiude intera. Il posto operatore
        // e' il ponte fra le due linee: uscendo, i due partecipanti smetterebbero
        // comunque di sentirsi, quindi lasciarne una aperta significherebbe lasciare
        // qualcuno da solo in una conversazione che crede ancora a tre.
        //
        // Per congedarne uno solo si scioglie prima la conferenza, e poi si chiude la
        // linea che si vuole chiudere: due gesti, ma nessuno dei due ambiguo.
        var conference = ConferencedCalls;
        if (conference.Count > 0)
        {
            await HangUpConferenceAsync(conference, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // L'ordine e' quello dell'urgenza percepita: prima la conversazione in
        // corso, poi la chiamata che squilla qui, poi quella che l'operatore ha
        // fatto partire e sta squillando altrove, poi quella impegnata in un
        // trasferimento, infine le attese.
        //
        // Le ultime due sono state aggiunte dopo altrettante segnalazioni dal campo,
        // e il sintomo era lo stesso: il comando rispondeva "nessuna chiamata
        // attiva" mentre la linea era aperta. E' la bugia peggiore che questo
        // programma possa dire, perche' l'operatore non ha modo di verificarla e
        // smette di provare a chiudere una chiamata che invece e' ancora in piedi.
        var target = ActiveCall
                     ?? RingingCall
                     ?? OutgoingCall
                     ?? TransferringCall
                     ?? HeldCalls.FirstOrDefault();

        if (target is null)
        {
            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.CommandNotAvailable, _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        await _telephony.HangUpAsync(target.Id, cancellationToken).ConfigureAwait(false);
        ApplyTrigger(target.Id, CallTrigger.HangUp);

        // Se resta una sola chiamata in attesa dopo la chiusura, la si riprende:
        // lasciarla in attesa senza che nessuno le parli e' il modo piu' comune per
        // dimenticare qualcuno in linea.
        await ResumeSingleRemainingHeldCallAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Invia una cifra come tono sulla chiamata in corso.
    /// </summary>
    /// <param name="digit">Cifra, oppure asterisco o cancelletto.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se il tono è stato inviato.</returns>
    /// <remarks>
    /// <para>
    /// Serve a rispondere ai menu automatici: «digita 1 per l'ufficio tecnico». Senza,
    /// un operatore che chiama un ente esterno resta bloccato davanti al centralino
    /// automatico dell'altro, e l'unico rimedio è chiamare da un telefono diverso.
    /// </para>
    /// <para>
    /// <b>Ogni tono inviato viene detto.</b> I toni RFC 4733 viaggiano fuori
    /// dall'audio della conversazione: chi li invia non li sente, quindi senza eco
    /// vocale non avrebbe alcun riscontro di cosa è partito. Su un menu automatico,
    /// dove una cifra sbagliata porta nel ramo sbagliato e spesso non si torna
    /// indietro, il riscontro è la differenza fra correggere e ricominciare.
    /// </para>
    /// </remarks>
    public async Task<bool> SendDtmfAsync(char digit, CancellationToken cancellationToken = default)
    {
        if (!IsDtmfDigit(digit))
        {
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        var active = ActiveCall;

        if (active is null)
        {
            _announcer.Announce(Announcement.CommandResponse(NoLineForDtmf(), _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        try
        {
            await _telephony.SendDtmfAsync(active.Id, digit, cancellationToken).ConfigureAwait(false);

            _log.LogInformation("Tono {Digit} inviato sulla chiamata {CallId}", digit, active.Id);

            _announcer.Announce(Announcement.CommandResponse(Pronuncia(digit), _clock.Now));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invio del tono {Digit} non riuscito", digit);

            _announcer.Announce(Announcement.CommandResponse("Tono non inviato", _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }
    }

    /// <summary>
    /// Invia una sequenza di toni, uno dopo l'altro.
    /// </summary>
    /// <param name="sequence">Cifre da inviare; i caratteri non validi vengono ignorati.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Quanti toni sono stati inviati.</returns>
    /// <remarks>
    /// <para>
    /// È la via comoda quando la sequenza si conosce già: la si scrive con calma nel
    /// campo di composizione, dove ogni cifra viene riletta e si può correggere, e poi
    /// si manda in un gesto solo. Digitare in diretta dentro un menu automatico
    /// significa invece sbagliare e ricominciare.
    /// </para>
    /// <para>
    /// Fra un tono e l'altro passa un intervallo: i centralini automatici che ricevono
    /// due cifre nello stesso istante ne perdono una, e il menu risponde come se
    /// l'operatore avesse premuto altro.
    /// </para>
    /// </remarks>
    public async Task<int> SendDtmfSequenceAsync(string? sequence, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sequence)) return 0;

        var cifre = sequence.Where(IsDtmfDigit).ToList();
        if (cifre.Count == 0) return 0;

        var active = ActiveCall;

        if (active is null)
        {
            _announcer.Announce(Announcement.CommandResponse(NoLineForDtmf(), _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return 0;
        }

        var inviati = 0;

        foreach (var cifra in cifre)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await _telephony.SendDtmfAsync(active.Id, cifra, cancellationToken).ConfigureAwait(false);
                inviati++;

                await Task.Delay(DtmfGapMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Invio della sequenza di toni interrotto al tono {Digit}", cifra);
                break;
            }
        }

        _log.LogInformation("Sequenza di {Count} toni inviata sulla chiamata {CallId}", inviati, active.Id);

        _announcer.Announce(Announcement.CommandResponse(
            inviati == cifre.Count
                ? $"Inviati {inviati} toni: {string.Join(' ', cifre.Select(Pronuncia))}"
                : $"Inviati {inviati} toni su {cifre.Count}: invio interrotto",
            _clock.Now));

        return inviati;
    }

    /// <summary>Motivo per cui non c'è una linea su cui inviare toni.</summary>
    /// <remarks>
    /// In conferenza una linea c'è, ce ne sono due: rispondere «nessuna chiamata in
    /// corso» sarebbe falso, e per un operatore che non vede lo schermo una risposta
    /// falsa è indistinguibile da un guasto. Il tono andrebbe comunque a una sola
    /// delle due linee senza che si possa dire quale, e mandare una cifra a un menu
    /// automatico sbagliato porta in un ramo da cui spesso non si torna.
    /// </remarks>
    private string NoLineForDtmf()
        => IsConferenceActive
            ? $"In conferenza i toni non si inviano: sciogli prima la conferenza con {_conference.ConferenceKeyName}"
            : "Nessuna chiamata in corso: i toni si inviano solo durante una conversazione";

    /// <summary>Intervallo fra due toni consecutivi, in millisecondi.</summary>
    /// <remarks>
    /// Centoventi millisecondi: sopra la durata minima che i centralini automatici
    /// richiedono per distinguere due pressioni, sotto la soglia in cui l'operatore
    /// percepirebbe un'attesa.
    /// </remarks>
    private const int DtmfGapMilliseconds = 120;

    private static bool IsDtmfDigit(char digit)
        => digit is (>= '0' and <= '9') or '*' or '#';

    /// <summary>Nome pronunciabile di un tono.</summary>
    /// <remarks>
    /// Asterisco e cancelletto vanno detti per nome: pronunciati come simboli, la
    /// sintesi li rende in modi diversi fra una voce e l'altra, e su un menu
    /// automatico sono proprio i due tasti che confermano o annullano.
    /// </remarks>
    private static string Pronuncia(char digit) => digit switch
    {
        '*' => "asterisco",
        '#' => "cancelletto",
        _ => digit.ToString(),
    };

    /// <summary>
    /// Alterna fra conversazione e attesa sulla chiamata corrente.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se il comando è stato applicato.</returns>
    public async Task<bool> ToggleHoldAsync(CancellationToken cancellationToken = default)
    {
        // La conferenza si parcheggia e si riprende intera: vedi ParkedConference.
        if (IsConferenceActive)
        {
            await ParkConferenceAsync(announce: true, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (ParkedConference.Count >= 2)
        {
            return await ResumeConferenceAsync(cancellationToken).ConfigureAwait(false);
        }

        var active = ActiveCall;
        if (active is not null)
        {
            await _telephony.HoldAsync(active.Id, cancellationToken).ConfigureAwait(false);
            ApplyTrigger(active.Id, CallTrigger.Hold);
            return true;
        }

        var held = HeldCalls.FirstOrDefault();
        if (held is null)
        {
            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.CommandNotAvailable, _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        await _telephony.ResumeAsync(held.Id, cancellationToken).ConfigureAwait(false);
        ApplyTrigger(held.Id, CallTrigger.Resume);
        return true;
    }

    /// <summary>
    /// Alterna fra due chiamate: mette in attesa quella attiva e riprende quella in
    /// attesa.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se lo scambio è avvenuto.</returns>
    /// <remarks>
    /// È il classico tasto "R" dei centralini tradizionali, e va tenuto ben distinto
    /// dal trasferimento: qui l'operatore resta l'interlocutore di entrambe le
    /// parti, nel trasferimento esce dalla conversazione. Confondere i due comandi è
    /// una delle prime cause di errore operativo, per questo hanno tasti diversi e
    /// annunci diversi.
    /// </remarks>
    public async Task<bool> SwapAsync(CancellationToken cancellationToken = default)
    {
        var active = ActiveCall;
        var held = HeldCalls.FirstOrDefault();

        if (held is null)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Nessuna chiamata in attesa da alternare", _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        if (active is not null)
        {
            await _telephony.HoldAsync(active.Id, cancellationToken).ConfigureAwait(false);
            ApplyTrigger(active.Id, CallTrigger.Hold, suppressAnnouncement: true);
        }

        await _telephony.ResumeAsync(held.Id, cancellationToken).ConfigureAwait(false);
        ApplyTrigger(held.Id, CallTrigger.Resume, suppressAnnouncement: true);

        // Un unico annuncio per l'intera operazione invece dei due prodotti dalle
        // singole transizioni: l'operatore vuole sapere con chi sta parlando adesso
        // e chi resta in attesa, non sentire due frasi consecutive.
        _announcer.Announce(Announcement.CommandResponse(
            AnnouncementTexts.Swapped(held.RemoteParty, active?.RemoteParty),
            _clock.Now));

        return true;
    }

    // -----------------------------------------------------------------------
    // Conferenza a tre
    // -----------------------------------------------------------------------

    /// <summary>
    /// Apre la conferenza a tre, la scioglie se è già aperta, la riporta in linea se
    /// è parcheggiata.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se il comando ha prodotto un effetto.</returns>
    /// <remarks>
    /// <para>
    /// Un tasto solo con tre significati, come il trasferimento, e per la stessa
    /// ragione: su una tastiera usata senza vista ogni combinazione in più è una cosa
    /// in più da ricordare, e il significato giusto è sempre l'unico sensato nello
    /// stato in cui ci si trova. A differenza del trasferimento non serve distinguere
    /// pressione singola e doppia, quindi non c'è nessuna finestra di attesa: l'effetto
    /// è immediato.
    /// </para>
    /// <para>
    /// La conferenza si costruisce sulle due chiamate che l'operatore ha già — quella
    /// in linea e quella in attesa — e non ne compone di nuove. È la stessa situazione
    /// in cui si arriva a metà di un trasferimento con annuncio, quando l'interno
    /// risponde e chiede di parlare anche lui con il chiamante: da lì la conferenza è
    /// una pressione sola.
    /// </para>
    /// </remarks>
    public async Task<bool> ToggleConferenceAsync(CancellationToken cancellationToken = default)
    {
        var conference = ConferencedCalls;
        if (conference.Count > 0)
        {
            return await SplitConferenceAsync(conference, cancellationToken).ConfigureAwait(false);
        }

        if (ParkedConference.Count >= 2)
        {
            return await ResumeConferenceAsync(cancellationToken).ConfigureAwait(false);
        }

        return await StartConferenceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Unisce la chiamata in linea e quella in attesa.</summary>
    private async Task<bool> StartConferenceAsync(CancellationToken cancellationToken)
    {
        if (!_telephony.SupportsConference)
        {
            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.ConferenceNotSupported, _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        // Chi e' in linea entra per primo, perche' e' quello con cui l'operatore
        // stava parlando: sciogliendo la conferenza sara' quello che gli resta.
        var candidates = new List<Call>();
        if (ActiveCall is { } active) candidates.Add(active);
        candidates.AddRange(HeldCalls);

        if (candidates.Count < 2)
        {
            // Non basta rifiutare: la conferenza e' l'unico comando che richiede una
            // condizione che l'operatore deve costruirsi prima, e un rifiuto secco
            // davanti a una chiamata in corso si legge come un guasto del tasto.
            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.ConferenceNeedsSecondCall(
                    _conference.HoldKeyName, _conference.DialerKeyName),
                _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        var legs = candidates.Take(2).ToList();
        var wereHeld = legs.Where(l => l.State == CallState.Held).ToList();

        try
        {
            // Nessuna gamba puo' restare in attesa: in attesa il centralino manda
            // musica al posto della voce, e la conferenza risulterebbe aperta con un
            // partecipante che non sente e non viene sentito.
            foreach (var leg in wereHeld)
            {
                await _telephony.ResumeAsync(leg.Id, cancellationToken).ConfigureAwait(false);
            }

            await _telephony
                .StartConferenceAsync([.. legs.Select(l => l.Id)], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della conferenza non riuscita");
            await RestoreAfterFailedConferenceAsync(wereHeld, cancellationToken).ConfigureAwait(false);
            return false;
        }

        lock (_gate)
        {
            _conferenceIds.Clear();
            _conferenceIds.AddRange(legs.Select(l => l.Id));
        }

        // Le due transizioni tacciono e l'annuncio e' uno solo: entrano insieme, e
        // due frasi consecutive direbbero a pezzi una cosa sola.
        foreach (var leg in legs)
        {
            ApplyTrigger(leg.Id, CallTrigger.ConferenceJoined, suppressAnnouncement: true);
        }

        _log.LogInformation(
            "Conferenza aperta fra {First} e {Second}", legs[0].Id, legs[1].Id);

        _announcer.Announce(Announcement.High(
            AnnouncementTexts.ConferenceStarted(
                legs[0].RemoteParty, legs[1].RemoteParty, _conference.ConferenceKeyName),
            _clock.Now,
            EarconKind.ConferenceStarted,
            coalesceKey: "conference:state",
            braille: "CONF"));

        return true;
    }

    /// <summary>
    /// Scioglie la conferenza: la prima gamba resta in linea, le altre vanno in attesa.
    /// </summary>
    private async Task<bool> SplitConferenceAsync(
        IReadOnlyList<Call> conference,
        CancellationToken cancellationToken)
    {
        await StopConferenceBridgeAsync(cancellationToken).ConfigureAwait(false);

        var stays = conference[0];
        var parked = conference.Skip(1).ToList();

        foreach (var leg in parked)
        {
            try
            {
                await _telephony.HoldAsync(leg.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Se l'attesa non riesce la chiamata resta comunque fuori dalla
                // conferenza: meglio due linee aperte di cui una che sente, che una
                // conferenza che il posto operatore crede sciolta e che invece non lo e'.
                _log.LogWarning(ex, "Messa in attesa della gamba {CallId} non riuscita", leg.Id);
            }

            ApplyTrigger(leg.Id, CallTrigger.Hold, suppressAnnouncement: true);
        }

        ApplyTrigger(stays.Id, CallTrigger.ConferenceLeft, suppressAnnouncement: true);
        lock (_gate) _conferenceIds.Clear();

        _log.LogInformation("Conferenza sciolta: resta in linea {CallId}", stays.Id);

        _announcer.Announce(Announcement.High(
            AnnouncementTexts.ConferenceEnded(stays.RemoteParty, parked.FirstOrDefault()?.RemoteParty),
            _clock.Now,
            EarconKind.ConferenceEnded,
            coalesceKey: "conference:state",
            braille: $"={stays.RemoteParty.DisplayName ?? stays.RemoteParty.Number}"));

        return true;
    }

    /// <summary>Mette in attesa tutti i partecipanti insieme.</summary>
    /// <param name="announce">
    /// Se dirlo. Vale <c>false</c> quando il parcheggio è un passaggio interno di un
    /// altro comando — rispondere a una terza linea — che produce già il proprio
    /// annuncio.
    /// </param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    private async Task ParkConferenceAsync(bool announce, CancellationToken cancellationToken)
    {
        var conference = ConferencedCalls;
        if (conference.Count == 0) return;

        await StopConferenceBridgeAsync(cancellationToken).ConfigureAwait(false);

        foreach (var leg in conference)
        {
            try
            {
                await _telephony.HoldAsync(leg.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Messa in attesa della gamba {CallId} non riuscita", leg.Id);
            }

            ApplyTrigger(leg.Id, CallTrigger.Hold, suppressAnnouncement: true);
        }

        // Gli identificativi NON si azzerano: e' quello che distingue una conferenza
        // parcheggiata da due chiamate in attesa qualsiasi, e permette di riportarla
        // in linea intera invece che una gamba per volta.
        _log.LogInformation("Conferenza parcheggiata: {Count} partecipanti in attesa", conference.Count);

        if (!announce) return;

        _announcer.Announce(Announcement.High(
            AnnouncementTexts.ConferenceParked(_conference.ConferenceKeyName),
            _clock.Now,
            EarconKind.CallHeld,
            coalesceKey: "conference:state",
            braille: "~CONF"));
    }

    /// <summary>Riporta in linea una conferenza parcheggiata.</summary>
    private async Task<bool> ResumeConferenceAsync(CancellationToken cancellationToken)
    {
        var parked = ParkedConference;
        if (parked.Count < 2) return false;

        try
        {
            foreach (var leg in parked)
            {
                await _telephony.ResumeAsync(leg.Id, cancellationToken).ConfigureAwait(false);
            }

            await _telephony
                .StartConferenceAsync([.. parked.Select(l => l.Id)], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ripresa della conferenza non riuscita");
            await RestoreAfterFailedConferenceAsync(parked, cancellationToken).ConfigureAwait(false);
            return false;
        }

        foreach (var leg in parked)
        {
            ApplyTrigger(leg.Id, CallTrigger.ConferenceJoined, suppressAnnouncement: true);
        }

        _announcer.Announce(Announcement.High(
            AnnouncementTexts.ConferenceStarted(
                parked[0].RemoteParty, parked[1].RemoteParty, _conference.ConferenceKeyName),
            _clock.Now,
            EarconKind.ConferenceStarted,
            coalesceKey: "conference:state",
            braille: "CONF"));

        return true;
    }

    /// <summary>Chiude tutte le linee della conferenza e lo dice una volta sola.</summary>
    private async Task HangUpConferenceAsync(
        IReadOnlyList<Call> conference,
        CancellationToken cancellationToken)
    {
        await StopConferenceBridgeAsync(cancellationToken).ConfigureAwait(false);

        var parties = conference.Select(c => c.RemoteParty).ToList();

        foreach (var leg in conference)
        {
            try
            {
                await _telephony.HangUpAsync(leg.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Chiusura della gamba {CallId} non riuscita", leg.Id);
            }

            ApplyTrigger(leg.Id, CallTrigger.HangUp, suppressAnnouncement: true);
        }

        lock (_gate) _conferenceIds.Clear();

        _log.LogInformation("Conferenza chiusa dall'operatore: {Count} linee", conference.Count);

        _announcer.Announce(Announcement.High(
            AnnouncementTexts.ConferenceHungUp(parties),
            _clock.Now,
            EarconKind.CallEnded,
            coalesceKey: "conference:state",
            braille: "libero"));

        await ResumeSingleRemainingHeldCallAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Riporta a una conversazione a due la conferenza da cui è uscito un
    /// partecipante.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È il corrispettivo, per la conferenza, del recupero automatico dopo un
    /// trasferimento fallito. Con una gamba caduta il ponte audio non ha più nulla da
    /// mettere in comunicazione, e chi resta va riportato in una conversazione normale:
    /// altrimenti la sua chiamata resterebbe in uno stato che l'operatore non ha modo
    /// di riconoscere, con i comandi di attesa e di trasferimento che non la trovano.
    /// </para>
    /// <para>
    /// L'annuncio è uno solo e dice entrambe le cose — chi ha chiuso e con chi si
    /// resta — perché sono la stessa domanda: senza la seconda metà, l'operatore non
    /// sa se sta ancora parlando con qualcuno, e su una linea silenziosa non ha modo
    /// di scoprirlo se non parlando nel vuoto.
    /// </para>
    /// </remarks>
    private async Task CollapseConferenceAsync(CancellationToken cancellationToken)
    {
        List<string> ids;
        lock (_gate)
        {
            if (_conferenceIds.Count == 0) return;
            ids = [.. _conferenceIds];
        }

        var legs = ids.Select(GetCall).OfType<Call>().ToList();
        var live = legs.Where(c => c.State == CallState.Conferenced).ToList();

        // Conferenza ancora intera: non e' successo niente che la riguardi.
        if (live.Count > 1) return;

        await StopConferenceBridgeAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) _conferenceIds.Clear();

        if (live.Count == 0)
        {
            // Caso raro ma possibile: un guasto di rete porta via entrambe le gambe.
            // Le transizioni sono state taciute in attesa di questo annuncio, quindi
            // senza di esso la conferenza finirebbe in silenzio.
            _log.LogWarning("Conferenza terminata: nessun partecipante e' rimasto in linea");

            _announcer.Announce(Announcement.Critical(
                "Conferenza terminata: entrambe le linee sono cadute",
                _clock.Now,
                EarconKind.CallEnded,
                coalesceKey: "conference:state",
                braille: "libero"));

            return;
        }

        var survivor = live[0];
        var gone = legs.FirstOrDefault(c => c.Id != survivor.Id);

        ApplyTrigger(survivor.Id, CallTrigger.ConferenceLeft, suppressAnnouncement: true);

        _log.LogInformation(
            "Conferenza sciolta dall'uscita di {Gone}: resta in linea {Survivor}",
            gone?.Id ?? "-", survivor.Id);

        _announcer.Announce(new Announcement
        {
            Text = gone is null
                ? AnnouncementTexts.ConferenceEnded(survivor.RemoteParty, null)
                : AnnouncementTexts.ConferenceParticipantLeft(gone.RemoteParty, survivor.RemoteParty),
            Priority = AnnouncementPriority.High,
            Earcon = EarconKind.ConferenceEnded,
            CoalesceKey = "conference:state",
            BrailleText = $"={survivor.RemoteParty.DisplayName ?? survivor.RemoteParty.Number}",
            CreatedAt = _clock.Now,

            // Stessa deroga del recupero dopo un trasferimento fallito, e per lo stesso
            // motivo: la conversazione a cui si torna e' proprio l'oggetto
            // dell'annuncio. Soppresso perche' "c'e' una conversazione in corso",
            // l'operatore non saprebbe con chi.
            BypassConversationSilence = true,
        });
    }

    /// <summary>Smonta il ponte audio senza far cadere nulla se il motore protesta.</summary>
    private async Task StopConferenceBridgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _telephony.StopConferenceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Il ponte audio e' locale: se non si smonta, il danno e' che i due
            // interlocutori continuano a sentirsi. Fastidioso, ma non vale far
            // fallire un comando che deve chiudere delle chiamate.
            _log.LogWarning(ex, "Chiusura del ponte audio della conferenza non riuscita");
        }
    }

    /// <summary>
    /// Rimette in attesa le chiamate che erano state riprese per una conferenza che
    /// non si è aperta.
    /// </summary>
    /// <remarks>
    /// Senza questo passo un tentativo fallito lascerebbe due linee entrambe in
    /// conversazione, con l'operatore che sente tutti e due e nessuno dei due che
    /// sente l'altro: uno stato che nessun comando successivo sa più raccontare.
    /// </remarks>
    private async Task RestoreAfterFailedConferenceAsync(
        IReadOnlyList<Call> resumed,
        CancellationToken cancellationToken)
    {
        foreach (var leg in resumed)
        {
            try
            {
                await _telephony.HoldAsync(leg.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Ripristino dell'attesa su {CallId} non riuscito", leg.Id);
            }
        }

        _announcer.Announce(Announcement.High(
            "Conferenza non riuscita",
            _clock.Now,
            EarconKind.TransferFailed,
            coalesceKey: "conference:state"));
    }

    /// <summary>Compone un numero.</summary>
    /// <param name="destination">Numero o interno da chiamare.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Identificativo della chiamata creata.</returns>
    public async Task<string> DialAsync(string destination, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolvePartyAsync(destination, cancellationToken).ConfigureAwait(false);
        var callId = await _telephony.DialAsync(destination, cancellationToken).ConfigureAwait(false);

        var call = new Call(callId, CallDirection.Outbound, resolved, _clock.Now);
        lock (_gate) _calls[callId] = call;

        ApplyTrigger(callId, CallTrigger.OutgoingCall);
        return callId;
    }

    /// <summary>
    /// Registra una chiamata creata esternamente, per esempio la consultazione
    /// avviata dall'orchestratore del trasferimento.
    /// </summary>
    /// <param name="callId">Identificativo restituito dal motore telefonico.</param>
    /// <param name="party">Controparte.</param>
    /// <returns>La chiamata registrata.</returns>
    public Call RegisterOutboundCall(string callId, CallParty party)
    {
        var call = new Call(callId, CallDirection.Outbound, party, _clock.Now);
        lock (_gate) _calls[callId] = call;
        ApplyTrigger(callId, CallTrigger.OutgoingCall);
        return call;
    }

    // -----------------------------------------------------------------------
    // Motore della macchina a stati
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applica un evento a una chiamata e produce il relativo annuncio.
    /// </summary>
    /// <param name="callId">Chiamata a cui applicare l'evento.</param>
    /// <param name="trigger">Evento.</param>
    /// <param name="endReason">Motivo di chiusura, se l'evento la comporta.</param>
    /// <param name="suppressAnnouncement">
    /// Se <c>true</c> la transizione avviene senza annuncio, perché chi chiama ne
    /// produrrà uno complessivo. Va usato con parsimonia: è l'unica scappatoia dalla
    /// regola "ogni transizione annuncia", e serve solo per le operazioni composte
    /// come l'alternanza, dove due annunci consecutivi confonderebbero.
    /// </param>
    /// <returns><c>true</c> se la transizione era lecita ed è stata applicata.</returns>
    public bool ApplyTrigger(
        string callId,
        CallTrigger trigger,
        CallEndReason? endReason = null,
        bool suppressAnnouncement = false)
    {
        Call? call;
        CallTransition transition;
        var now = _clock.Now;

        // Qualunque transizione chiude la fase di squillo: risposta, rifiuto,
        // riaggancio remoto o locale. Passa da qui anche il riaggancio dell'operatore,
        // che non arriva dagli eventi del motore telefonico.
        StopRingback(callId);

        lock (_gate)
        {
            if (!_calls.TryGetValue(callId, out call))
            {
                _log.LogWarning("Evento {Trigger} per chiamata sconosciuta {CallId}", trigger, callId);
                return false;
            }

            if (endReason is not null) call.SetEndReason(endReason.Value);

            if (!call.TryApply(trigger, now, out transition))
            {
                // Non e' un errore: la rete puo' consegnare eventi fuori sequenza
                // (un BYE duplicato, un 486 dopo un 200 OK). Si registra e si ignora.
                _log.LogDebug(
                    "Transizione non ammessa: {State} con {Trigger} sulla chiamata {CallId}",
                    call.State, trigger, callId);
                return false;
            }

            UpdateActiveCallTracking(call);
        }

        // Una transizione e' avvenuta: se ha portato una chiamata in squillo la
        // suoneria parte, se ne ha tolta una si ferma. Sta qui, nell'unico punto in cui
        // una chiamata cambia stato, e non nei singoli casi: i modi di smettere di
        // squillare sono quattro — risposta, rifiuto dell'operatore, CANCEL del
        // centralino, caduta della transazione — e ognuno dimenticato e' una suoneria
        // che non si spegne piu'.
        SyncRinger();

        // La conferenza conta come conversazione in corso: l'operatore sta parlando
        // con due persone, quindi la regola del silenzio durante la conversazione vale
        // a maggior ragione.
        _context.IsConversationActive = ActiveCall is not null || IsConferenceActive;

        if (!suppressAnnouncement)
        {
            _announcer.Announce(_mapper.Map(transition, call, now));
        }

        CallStateChanged?.Invoke(this, new CallStateChangedEventArgs(call, transition, now));

        if (CallStateMachine.IsTerminal(call.State)) SchedulePurge(callId);

        return true;
    }

    /// <summary>
    /// Aggiorna quale chiamata è considerata attiva. Va invocato dentro al lock.
    /// </summary>
    private void UpdateActiveCallTracking(Call call)
    {
        if (call.State == CallState.Active)
        {
            _activeCallId = call.Id;
            _lastHoldReminderAt = _clock.Now;
        }
        else if (_activeCallId == call.Id)
        {
            _activeCallId = null;
        }

        // IL CONTO DEL PROMEMORIA PARTE QUANDO PARTE L'ATTESA.
        //
        // Senza questa riga l'intervallo si misurava dall'ultimo promemoria o dalla
        // risposta, non dal momento in cui la chiamata e' stata messa in attesa: chi
        // parlava da ventuno secondi e metteva in attesa si sentiva il primo bip dopo
        // nove, chi metteva in attesa subito lo aspettava mezzo minuto. Il promemoria
        // serve a misurare da quanto qualcuno sta aspettando, quindi deve contare da
        // quando ha cominciato ad aspettare.
        if (call.State == CallState.Held) _lastHoldReminderAt = _clock.Now;
    }

    /// <summary>
    /// Fa avanzare i comportamenti temporizzati: al momento il promemoria di
    /// chiamata dimenticata in attesa.
    /// </summary>
    /// <param name="now">Istante corrente; se omesso si usa l'orologio iniettato.</param>
    /// <remarks>
    /// Richiamato dallo stesso ciclo che alimenta
    /// <see cref="AnnouncementService.Pump"/>. Il promemoria esiste perché un
    /// operatore non vedente non ha la spia della linea che lampeggia: senza un
    /// segnale periodico, una chiamata in attesa può restarci per minuti.
    /// </remarks>
    public void Pump(DateTimeOffset? now = null)
    {
        var timestamp = now ?? _clock.Now;

        if (_options.HoldReminderInterval <= TimeSpan.Zero) return;
        if (timestamp - _lastHoldReminderAt < _options.HoldReminderInterval) return;

        var held = HeldCalls;
        if (held.Count == 0) return;

        // Il promemoria non deve mai coprire la conversazione: se l'operatore sta
        // parlando resta il solo earcon, che passa comunque.
        var oldest = held.OrderByDescending(c => c.HoldDuration(timestamp)).First();
        _announcer.Announce(Announcement.Normal(
            AnnouncementTexts.HoldReminder(oldest.RemoteParty, oldest.HoldDuration(timestamp)),
            timestamp,
            EarconKind.HoldReminder,
            coalesceKey: "hold:reminder"));

        _lastHoldReminderAt = timestamp;
    }

    // -----------------------------------------------------------------------
    // Eventi dal motore telefonico
    // -----------------------------------------------------------------------

    private async void OnIncomingCall(object? sender, IncomingCallEventArgs e)
    {
        try
        {
            var party = await ResolveIncomingPartyAsync(e.RemoteParty, CancellationToken.None)
                .ConfigureAwait(false);

            var call = new Call(e.CallId, CallDirection.Inbound, party, e.At);
            call.AttachIdentifiers(e.SipCallId);
            call.AttachQueue(e.QueueHint);

            lock (_gate) _calls[e.CallId] = call;

            ApplyTrigger(e.CallId, CallTrigger.IncomingCall);
        }
        catch (Exception ex)
        {
            // Un'eccezione non gestita in un gestore async void terminerebbe il
            // processo. Su un posto operatore significa perdere tutte le chiamate in
            // corso per un errore nella risoluzione di un nome in rubrica.
            _log.LogError(ex, "Errore nella gestione della chiamata entrante {CallId}", e.CallId);
        }
    }

    private void OnCallProgress(object? sender, CallProgressEventArgs e)
    {
        var call = GetCall(e.CallId);
        if (call is not null && e.NegotiatedCodec is not null)
        {
            call.SetMediaProfile(e.NegotiatedCodec, e.IsWideband, e.IsEncrypted);
        }

        // Lo squillo della destinazione non e' una transizione: la chiamata resta in
        // Calling. Governa solo il tono di libero, e non produce annunci parlati
        // perche' si sovrapporrebbero al tono che sta per partire.
        if (e.Trigger == CallTrigger.RemoteRinging)
        {
            HandleRemoteRinging(e);
            return;
        }

        // Una gamba di conferenza che cade non si annuncia da sola. L'annuncio utile
        // e' quello complessivo, che dice anche con chi si resta in linea: sentendo
        // solo "Mario Rossi ha chiuso" l'operatore non saprebbe se la conferenza e'
        // finita, se ne e' rimasto uno, o se e' rimasto solo.
        var wasConferenced = call?.State == CallState.Conferenced
                             && e.Trigger is CallTrigger.RemoteHangUp or CallTrigger.Failed;

        ApplyTrigger(e.CallId, e.Trigger, e.EndReason, suppressAnnouncement: wasConferenced);

        if (wasConferenced)
        {
            _ = CollapseConferenceAsync(CancellationToken.None);
            return;
        }

        if (e.Trigger is CallTrigger.RemoteHangUp or CallTrigger.Failed)
        {
            _ = ResumeSingleRemainingHeldCallAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Avvia il tono di libero locale quando la rete non lo fornisce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È la differenza fra chiamare un esterno e chiamare un interno. Verso
    /// l'esterno il gestore manda il proprio tono come audio in banda e si sente;
    /// verso un interno il centralino spesso risponde con un semplice squillo senza
    /// audio, e fino a questa aggiunta l'operatore restava in silenzio senza sapere
    /// se la chiamata fosse partita, se l'interno stesse squillando o se il tasto non
    /// avesse funzionato.
    /// </para>
    /// <para>
    /// Il tono <b>non</b> viene generato quando la rete lo manda già: due toni con
    /// cadenze diverse sovrapposti sono peggio del silenzio, perché nessuno dei due
    /// resta riconoscibile.
    /// </para>
    /// </remarks>
    private void HandleRemoteRinging(CallProgressEventArgs e)
    {
        if (e.HasEarlyMedia)
        {
            _log.LogDebug(
                "Chiamata {CallId}: tono di libero fornito dalla rete, non se ne genera un altro", e.CallId);
            return;
        }

        lock (_gate)
        {
            // Un secondo 180 sulla stessa chiamata non deve far ripartire il tono
            // da capo: la cadenza si spezzerebbe a ogni ritrasmissione.
            if (_ringbackCallId == e.CallId) return;
            _ringbackCallId = e.CallId;
        }

        _log.LogDebug("Chiamata {CallId}: tono di libero generato in locale", e.CallId);
        _earcons?.StartRepeating(EarconKind.RemoteRinging, RingbackSilence);
    }

    /// <summary>Ferma il tono di libero, se stava suonando per questa chiamata.</summary>
    /// <remarks>
    /// Va fermato a qualunque esito, non solo alla risposta: rifiuto, occupato,
    /// riaggancio e annullamento locale lasciano tutti l'operatore con il tono che
    /// continua, e un tono che non finisce vale come informazione sbagliata.
    /// </remarks>
    private void StopRingback(string callId)
    {
        lock (_gate)
        {
            if (_ringbackCallId != callId) return;
            _ringbackCallId = null;
        }

        _earcons?.StopRepeating(EarconKind.RemoteRinging);
    }

    /// <summary>
    /// Allinea la suoneria allo stato delle chiamate: suona finché una chiamata
    /// entrante è in squillo, tace appena non lo è più.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Una chiamata entrante deve squillare finché non le si risponde.</b> Prima
    /// la suoneria era il solo earcon dell'annuncio di arrivo: suonava una volta e poi
    /// silenzio, mentre la chiamata continuava a squillare per il centralino. Per chi
    /// non vede lo schermo quel silenzio è indistinguibile da una chiamata finita, e
    /// dopo qualche secondo l'operatore smette di aspettarla.
    /// </para>
    /// <para>
    /// Quanto a lungo squillare non lo decide il posto operatore: lo decide il
    /// centralino, che chiude la chiamata con un <c>CANCEL</c> quando il suo tempo di
    /// squillo è scaduto. Qui si smette solo quando la chiamata non è più in squillo.
    /// </para>
    /// <para>
    /// La suoneria esce sulla via <see cref="EarconRouting"/> della famiglia, cioè sul
    /// dispositivo della suoneria e non su quello della conversazione: una seconda
    /// chiamata che arriva mentre l'operatore parla squilla negli altoparlanti, non
    /// nell'orecchio impegnato.
    /// </para>
    /// </remarks>
    private void SyncRinger()
    {
        var ringing = RingingCall;
        bool avvia = false, ferma = false;
        string? fermata = null;

        lock (_gate)
        {
            if (ringing is not null)
            {
                // Riavviarla a ogni transizione spezzerebbe la cadenza: si parte solo
                // se non sta gia' suonando per questa chiamata.
                if (_ringerCallId != ringing.Id)
                {
                    _ringerCallId = ringing.Id;
                    avvia = true;
                }
            }
            else if (_ringerCallId is not null)
            {
                fermata = _ringerCallId;
                _ringerCallId = null;
                ferma = true;
            }
        }

        // Il primo squillo l'ha gia' suonato l'annuncio di arrivo, che porta con se'
        // lo stesso earcon: la ripetizione parte dopo la pausa, altrimenti il segnale
        // si sovrapporrebbe a se stesso.
        //
        // Le due righe di registro dicono cosa ha sentito l'operatore, che e' un'altra
        // domanda rispetto a cosa e' successo sulla rete: una suoneria che si ferma
        // mentre la chiamata e' ancora in piedi, o che continua dopo la fine, sono due
        // difetti diversi e nessuno dei due si vede nella traccia SIP.
        if (avvia)
        {
            _log.LogInformation("Suoneria avviata per la chiamata {CallId}", ringing!.Id);
            _earcons?.StartRepeating(EarconKind.IncomingCall, RingerSilence, RingerSilence);
        }
        else if (ferma)
        {
            _log.LogInformation(
                "Suoneria fermata: la chiamata {CallId} non e' piu' in squillo ({Stato})",
                fermata, GetCall(fermata!)?.State.ToString() ?? "chiamata rimossa");

            _earcons?.StopRepeating(EarconKind.IncomingCall);
        }
    }

    /// <summary>Ultimo stato di registrazione già annunciato.</summary>
    private SipRegistrationState? _lastAnnouncedRegistration;

    /// <summary>
    /// Annuncia i cambiamenti della registrazione SIP, una volta per cambiamento.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il controllo qui non è un doppione di quello del motore telefonico</b>,
    /// anche se la domanda che pone è la stessa. Là serve a non mentire sul nome
    /// dell'evento; qui serve a proteggere l'orecchio dell'operatore da qualunque
    /// sorgente — il motore simulato, quello che commuta fra due account e li
    /// rilancia, e qualunque motore venga dopo. È il punto in cui un annuncio di
    /// troppo si sente, e va difeso dove si sente.
    /// </para>
    /// <para>
    /// Perché conta: il segnale acustico dell'annuncio viene suonato <b>prima</b> di
    /// ogni valutazione sulla voce e non è sopprimibile. Una frase di troppo durante
    /// una conversazione viene scartata dalla pipeline; il suono che la accompagna no,
    /// e arriva all'operatore mentre parla al telefono.
    /// </para>
    /// </remarks>
    private void OnRegistrationStateChanged(object? sender, SipRegistrationState state)
    {
        if (state.RepresentsSameStateAs(_lastAnnouncedRegistration)) return;

        _lastAnnouncedRegistration = state;

        var announcement = state.IsReachable
            ? Announcement.High(
                AnnouncementTexts.RegistrationChanged(state),
                _clock.Now,
                EarconKind.RegistrationRestored,
                coalesceKey: "sip:registration")
            : Announcement.Critical(
                AnnouncementTexts.RegistrationChanged(state),
                _clock.Now,
                EarconKind.RegistrationLost,
                coalesceKey: "sip:registration",
                braille: "NON REG");

        _announcer.Announce(announcement);
    }

    // -----------------------------------------------------------------------
    // Ausiliari
    // -----------------------------------------------------------------------

    /// <summary>
    /// Se dopo la chiusura di una chiamata ne resta esattamente una in attesa, la
    /// riprende automaticamente.
    /// </summary>
    /// <remarks>
    /// Comportamento deliberato e non ovvio. L'alternativa sarebbe lasciare
    /// l'operatore in silenzio con qualcuno in attesa che non sa di essere stato
    /// dimenticato. Con una sola chiamata residua l'intenzione è inequivocabile;
    /// con due o più non lo è, e infatti in quel caso non si fa nulla e si lascia
    /// scegliere l'operatore.
    /// </remarks>
    private async Task ResumeSingleRemainingHeldCallAsync(CancellationToken cancellationToken)
    {
        if (ActiveCall is not null) return;

        // Durante una conferenza non esiste una "chiamata attiva", ma l'operatore sta
        // parlando: riprendere anche una terza linea gli metterebbe in un orecchio
        // qualcuno che non c'entra con la conversazione in corso.
        if (IsConferenceActive) return;

        var held = HeldCalls;
        if (held.Count != 1) return;

        try
        {
            await _telephony.ResumeAsync(held[0].Id, cancellationToken).ConfigureAwait(false);
            ApplyTrigger(held[0].Id, CallTrigger.Resume);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ripresa automatica della chiamata {CallId} non riuscita", held[0].Id);
        }
    }

    /// <summary>
    /// Cerca in rubrica il numero di chi sta chiamando e ne completa
    /// l'identificazione.
    /// </summary>
    /// <param name="party">Controparte come l'ha consegnata la segnalazione.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// <para>
    /// La ricerca si fa <b>sempre</b>, anche quando la segnalazione porta già un nome.
    /// Il nome dichiarato nell'intestazione SIP lo sceglie chi chiama, e su una linea
    /// esterna non c'è nulla che ne garantisca la verità; quello della rubrica è stato
    /// verificato da chi l'ha compilata. Prima si saltava la ricerca appena arrivava un
    /// nome dalla rete, e su un centralino che li fornisce quasi sempre significava non
    /// consultare mai la rubrica: né l'interno né la resa fonetica del cognome
    /// arrivavano all'annuncio.
    /// </para>
    /// <para>
    /// Il nome della segnalazione resta comunque la prima scelta quando c'è: è quello
    /// che il centralino ha deciso di mostrare per quella chiamata, e può essere più
    /// specifico della scheda in rubrica.
    /// </para>
    /// </remarks>
    private async Task<CallParty> ResolveIncomingPartyAsync(CallParty party, CancellationToken cancellationToken)
    {
        if (_contacts is null || string.IsNullOrWhiteSpace(party.Number)) return party;

        try
        {
            var contact = await _contacts.ResolveByNumberAsync(party.Number, cancellationToken)
                .ConfigureAwait(false);

            if (contact is null)
            {
                // Va registrato quanto il ritrovamento: davanti a "ha annunciato solo
                // il numero" e' la riga che distingue un contatto assente da una
                // rubrica che non ha risposto.
                _log.LogInformation("Chiamante {Number} non presente in rubrica", party.Number);
                return party;
            }

            _log.LogInformation(
                "Chiamante {Number} riconosciuto in rubrica: {Name}, interno {Extension}",
                party.Number, contact.EffectiveDisplayName, contact.ExtensionNumber ?? "-");

            return party with
            {
                DisplayName = party.DisplayName ?? contact.EffectiveDisplayName,
                PhoneticName = contact.PhoneticName,
                IsKnownContact = true,
                Extension = contact.ExtensionNumber,
            };
        }
        catch (Exception ex)
        {
            // La rubrica non deve mai impedire di annunciare una chiamata: se la
            // risoluzione fallisce si annuncia il numero, che e' comunque utile.
            _log.LogWarning(ex, "Risoluzione del chiamante {Number} non riuscita", party.Number);
            return party;
        }
    }

    private async Task<CallParty> ResolvePartyAsync(string destination, CancellationToken cancellationToken)
    {
        var party = new CallParty(destination);
        if (_contacts is null) return party;

        try
        {
            var contact = await _contacts.ResolveByNumberAsync(destination, cancellationToken)
                .ConfigureAwait(false);

            return contact is null
                ? party
                : party with
                {
                    DisplayName = contact.EffectiveDisplayName,
                    PhoneticName = contact.PhoneticName,
                    IsKnownContact = true,
                    Extension = contact.ExtensionNumber,
                };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Risoluzione della destinazione {Destination} non riuscita", destination);
            return party;
        }
    }

    /// <summary>
    /// Rimuove dalla memoria una chiamata terminata, dopo una finestra di grazia.
    /// </summary>
    /// <remarks>
    /// La finestra serve perché il motore telefonico può ancora consegnare eventi
    /// tardivi sulla stessa chiamata, e perché l'operatore può chiedere la durata
    /// dell'ultima chiamata subito dopo averla chiusa.
    /// </remarks>
    private void SchedulePurge(string callId)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_calls.TryGetValue(callId, out var call) && CallStateMachine.IsTerminal(call.State))
                {
                    _calls.Remove(callId);
                }
            }
        }, TaskScheduler.Default);

        // Il contatore serve solo alla diagnostica: quante chiamate ha gestito la
        // sessione. E' l'informazione che l'operatore chiede a fine turno.
        Interlocked.Increment(ref _callCounter);
    }

    /// <summary>Numero di chiamate gestite dall'avvio dell'applicazione.</summary>
    public int HandledCallCount => _callCounter;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _telephony.IncomingCall -= OnIncomingCall;
        _telephony.CallProgress -= OnCallProgress;
        _telephony.RegistrationStateChanged -= OnRegistrationStateChanged;
        _telephony.SrtpDowngraded -= OnSrtpDowngraded;

        // Un tono che sopravvive alla chiusura del gestore continuerebbe a suonare
        // per sempre: sono gli earcon ripetuti del prodotto e vanno spenti a mano.
        _earcons?.StopRepeating(EarconKind.RemoteRinging);
        _earcons?.StopRepeating(EarconKind.IncomingCall);

        return ValueTask.CompletedTask;
    }
}

/// <summary>Dati dell'evento di cambio stato di una chiamata.</summary>
/// <param name="Call">Chiamata interessata, con lo stato già aggiornato.</param>
/// <param name="Transition">Transizione applicata.</param>
/// <param name="At">Istante della transizione.</param>
public sealed record CallStateChangedEventArgs(Call Call, CallTransition Transition, DateTimeOffset At);
