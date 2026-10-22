using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Governa il trasferimento di chiamata a combinazione unica.
/// </summary>
/// <remarks>
/// <para>
/// Realizza il requisito più delicato del capitolato: <b>una sola combinazione di
/// tasti per entrambe le modalità di trasferimento</b>, cieco e con annuncio. La
/// soluzione è una macchina a stati progressiva in cui il tasto assume un
/// significato diverso a seconda della fase, e in cui la distinzione fra le due
/// modalità nasce da un solo elemento aggiuntivo: pressione singola contro doppia
/// pressione rapida.
/// </para>
/// <para>
/// Sequenza per il trasferimento <b>con annuncio</b> (il caso frequente), quando il
/// numero è già nel campo di composizione perché il chiamante lo ha dettato mentre
/// parlava:
/// </para>
/// <code>
/// "4021"        -> eco delle cifre, con il chiamante ancora in linea
/// F9            -> mette in attesa l'esterno e chiama subito l'interno
/// (conversazione)
/// F9            -> completa il trasferimento
/// </code>
/// <para>
/// <b>Due pressioni, non tre.</b> La pressione che confermava la destinazione
/// appena riletta è stata tolta: fra la digitazione e quella conferma non succedeva
/// nulla che potesse far cambiare idea all'operatore, e in centralino una pressione
/// in più si paga su ogni chiamata del turno. La rilettura del numero resta, la fa
/// l'annuncio della consultazione mentre l'interno squilla.
/// </para>
/// <para>
/// Se il campo è vuoto la procedura è quella di prima, perché la destinazione va
/// pur chiesta:
/// </para>
/// <code>
/// F9            -> mette in attesa l'esterno, chiede la destinazione
/// "4021"        -> eco delle cifre
/// F9            -> chiama l'interno per parlargli
/// F9            -> completa il trasferimento
/// </code>
/// <para>
/// Sequenza per il trasferimento <b>cieco</b>, stessa combinazione:
/// </para>
/// <code>
/// "4021"        -> eco delle cifre
/// F9 F9         -> trasferimento immediato, operatore subito libero
/// </code>
/// <para>
/// <b>ESC annulla sempre</b> e riporta il chiamante in linea. Se la destinazione è
/// occupata, rifiuta o non risponde, il recupero avviene da solo: è il
/// comportamento più importante di tutta la classe, perché la sua assenza
/// lascerebbe il chiamante in attesa senza che nessuno se ne accorga.
/// </para>
/// </remarks>
public sealed class TransferOrchestrator : IAsyncDisposable
{
    private readonly ITelephonyEngine _telephony;
    private readonly CallSessionManager _calls;
    private readonly IAnnouncer _announcer;
    private readonly IContactRepository? _contacts;

    /// <summary>
    /// Stato degli interni, quando il centralino lo fornisce.
    /// </summary>
    /// <remarks>
    /// Facoltativo: senza centralino collegato il trasferimento funziona lo stesso, e
    /// l'avviso sulla destinazione non disponibile semplicemente non scatta. È la
    /// differenza fra una funzione assente e un prodotto che non parte.
    /// </remarks>
    private readonly PresenceMonitorService? _presence;

    /// <summary>
    /// Sorvegliante del segnale che dichiara l'ascolto durante un'inclusione.
    /// </summary>
    /// <remarks>
    /// Facoltativo come la presenza, ma con una conseguenza diversa: senza di lui
    /// l'inclusione <b>non parte affatto</b>. Aprire la linea e non poter garantire il
    /// segnale significherebbe ascoltare due persone senza dichiararlo, ed è la sola
    /// cosa che questa funzione non deve poter fare.
    /// </remarks>
    private readonly IntrusionMonitor? _intrusion;

    private readonly IClock _clock;
    private readonly ILogger<TransferOrchestrator> _log;

    private readonly Lock _gate = new();

    private TransferSession? _session;

    /// <summary>Istante della prima pressione in attesa di essere risolta come singola o doppia.</summary>
    private DateTimeOffset? _pendingKeyPressAt;

    /// <summary>Conferma richiesta all'operatore prima di procedere, se presente.</summary>
    private PendingConfirmation? _pendingConfirmation;

    /// <summary>Crea l'orchestratore.</summary>
    /// <param name="telephony">Motore telefonico.</param>
    /// <param name="calls">Gestore delle chiamate.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="contacts">Rubrica, per risolvere la destinazione e leggerne la presenza.</param>
    /// <param name="options">Parametri della procedura.</param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="presence">
    /// Stato degli interni, quando il centralino lo fornisce. Senza, l'avviso sulla
    /// destinazione non disponibile non scatta e il trasferimento procede.
    /// </param>
    /// <param name="intrusion">
    /// Sorvegliante del segnale che dichiara l'ascolto. Senza, l'inclusione su interno
    /// occupato non è disponibile: la funzione esiste solo se si può garantire che i
    /// presenti sappiano di essere ascoltati.
    /// </param>
    public TransferOrchestrator(
        ITelephonyEngine telephony,
        CallSessionManager calls,
        IAnnouncer announcer,
        IContactRepository? contacts = null,
        TransferOptions? options = null,
        IClock? clock = null,
        ILogger<TransferOrchestrator>? logger = null,
        PresenceMonitorService? presence = null,
        IntrusionMonitor? intrusion = null)
    {
        _intrusion = intrusion;
        _telephony = telephony;
        _calls = calls;
        _announcer = announcer;
        _contacts = contacts;
        _presence = presence;
        Options = options ?? TransferOptions.Default;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<TransferOrchestrator>.Instance;

        _calls.CallStateChanged += OnCallStateChanged;
        _telephony.TransferProgress += OnTransferProgress;
    }

    /// <summary>Parametri correnti della procedura.</summary>
    public TransferOptions Options { get; set; }

    /// <summary>Fase corrente, <see cref="TransferPhase.Idle"/> se non c'è nulla in corso.</summary>
    public TransferPhase Phase
    {
        get { lock (_gate) return _session?.Phase ?? TransferPhase.Idle; }
    }

    /// <summary>Sessione di trasferimento in corso, se presente.</summary>
    public TransferSession? CurrentSession
    {
        get { lock (_gate) return _session; }
    }

    /// <summary>Sollevato a ogni cambio di fase, per l'aggiornamento dell'interfaccia.</summary>
    public event EventHandler<TransferPhase>? PhaseChanged;

    // -----------------------------------------------------------------------
    // Inclusione su interno occupato (RFC 3911)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Indica se l'inclusione è eseguibile in questo istante.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Servono quattro cose insieme, e la mancanza di una sola rende il comando una
    /// promessa che fallisce: una destinazione scelta, il suo stato che dice
    /// <i>occupato</i>, gli identificativi della conversazione comunicati dal
    /// centralino, e il sorvegliante del segnale di ascolto.
    /// </para>
    /// <para>
    /// La proprietà esiste perché la stessa domanda la fanno in due: la barra dei
    /// pulsanti, per decidere se mostrarlo, e il comando, per decidere se rifiutare.
    /// Due risposte diverse alla stessa domanda sarebbero un pulsante che c'è e non
    /// funziona.
    /// </para>
    /// </remarks>
    public bool CanBargeIn => ResolveBargeInTarget() is not null;

    /// <summary>
    /// Trova la conversazione in cui l'operatore può includersi, se c'è.
    /// </summary>
    private DialogReference? ResolveBargeInTarget()
    {
        if (_intrusion is null || _presence is null) return null;

        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null || session.IsFinished) return null;
        if (session.Phase is not (TransferPhase.EnteringDestination or TransferPhase.Consulting)) return null;
        if (string.IsNullOrWhiteSpace(session.Destination)) return null;

        // Lo stato deve dire occupato. Includersi in un interno libero non ha senso —
        // non c'e' nessuna conversazione — e su uno di stato ignoto significherebbe
        // tentare alla cieca.
        if (_presence.Get(session.Destination) != PresenceState.Busy) return null;

        var dialogo = _presence.ActiveDialog(session.Destination);
        return dialogo is { IsComplete: true } ? dialogo : null;
    }

    /// <summary>
    /// Entra nella conversazione dell'interno occupato per segnalargli la chiamata.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se l'inclusione è partita.</returns>
    /// <remarks>
    /// <para>
    /// <b>Il chiamante resta dov'è.</b> Non viene ripreso dall'attesa e non viene
    /// chiuso: l'operatore si affaccia nella conversazione del collega, gli dice che
    /// c'è una chiamata urgente, chiude l'inclusione e completa il trasferimento con
    /// la destinazione ancora in memoria. Riprendere il chiamante qui lo farebbe
    /// assistere alla trattativa.
    /// </para>
    /// <para>
    /// <b>La linea di consultazione, se c'è, viene chiusa prima.</b> È il tentativo
    /// che non è andato a buon fine — l'interno era occupato — e lasciarla aperta
    /// significherebbe far squillare l'apparecchio del collega mentre gli si parla
    /// dentro la conversazione.
    /// </para>
    /// <para>
    /// <b>Il fallimento si annuncia per quello che è.</b> L'RFC 3911 è opzionale: un
    /// centralino che non lo sostiene rifiuta l'invito, e l'operatore deve sentirsi
    /// dire che la funzione non è disponibile su questo impianto invece di restare
    /// davanti a un comando muto.
    /// </para>
    /// </remarks>
    public async Task<bool> BargeInAsync(CancellationToken cancellationToken = default)
    {
        if (ResolveBargeInTarget() is not { } dialogo || _intrusion is null)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Inclusione non disponibile: serve un interno occupato di cui il centralino comunichi "
                + "la conversazione in corso",
                _clock.Now));

            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null) return false;

        // 1. Si chiude il tentativo che non e' riuscito.
        if (session.ConsultationCall is { } consultazione
            && !CallStateMachine.IsTerminal(consultazione.State))
        {
            try
            {
                await _telephony.HangUpAsync(consultazione.Id, cancellationToken).ConfigureAwait(false);
                _calls.ApplyTrigger(consultazione.Id, CallTrigger.HangUp, suppressAnnouncement: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Chiusura della consultazione prima dell'inclusione non riuscita");
            }
        }

        try
        {
            _log.LogInformation(
                "Inclusione richiesta sull'interno {Extension}: dialogo {CallId}",
                dialogo.Extension, dialogo.CallId);

            var callId = await _telephony.BargeInAsync(dialogo, cancellationToken).ConfigureAwait(false);

            var party = session.ResolvedDestination ?? new CallParty(session.Destination);
            _calls.RegisterOutboundCall(callId, party);

            _intrusion.Begin(callId, dialogo.Extension);

            // L'annuncio dice anche cosa sentono gli altri: l'operatore sta per
            // parlare dentro la conversazione di due persone e deve sapere che loro lo
            // sanno.
            _announcer.Announce(Announcement.High(
                $"Inclusione nella conversazione di {session.DestinationSpeech}. "
                + "Tutti sentono un segnale periodico che dichiara il tuo ascolto",
                _clock.Now,
                EarconKind.CallAnswered,
                coalesceKey: "transfer:intrusion",
                braille: $"INCL {session.Destination}"));

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Inclusione sull'interno {Extension} non riuscita", dialogo.Extension);

            _announcer.Announce(Announcement.High(
                "Inclusione non riuscita: il centralino non l'ha consentita. Il chiamante e' ancora in attesa",
                _clock.Now,
                EarconKind.TransferFailed,
                coalesceKey: "transfer:intrusion"));

            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Il tasto unico
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gestisce una pressione del tasto di trasferimento, interpretandola secondo la
    /// fase corrente.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se la pressione ha prodotto un effetto.</returns>
    public async Task<bool> HandleTransferKeyAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.Now;

        // Una conferma in sospeso intercetta la pressione: il tasto conferma invece
        // di avanzare. E' il modo per chiedere "sei sicuro?" senza introdurre una
        // finestra modale, che ruberebbe il focus e costringerebbe l'operatore a
        // ritrovarlo dopo.
        //
        // Ma vale per il numero su cui era stata chiesta. Se nel frattempo l'operatore
        // ne ha corretto una cifra — e dopo un "interno occupato" e' quello che fa — la
        // conferma decade e i controlli si rifanno sul numero nuovo: il via libera dato
        // per il 4021 non e' un via libera per il 4022.
        PendingConfirmation? confirmation;

        lock (_gate)
        {
            confirmation = _pendingConfirmation;

            if (confirmation is not null
                && !string.Equals(
                    confirmation.Destination.Trim(),
                    _session?.Destination.Trim(),
                    StringComparison.Ordinal))
            {
                confirmation = null;
            }

            _pendingConfirmation = null;
        }

        if (confirmation is not null)
        {
            return await ExecuteConfirmedActionAsync(confirmation, cancellationToken).ConfigureAwait(false);
        }

        return Phase switch
        {
            TransferPhase.Idle => await BeginTransferAsync(null, cancellationToken).ConfigureAwait(false),
            TransferPhase.EnteringDestination => HandleDestinationConfirmKey(now),
            TransferPhase.Consulting => await CompleteAttendedTransferAsync(cancellationToken).ConfigureAwait(false),
            _ => RejectCommand(),
        };
    }

    /// <summary>
    /// Avvia il trasferimento verso una destinazione già nota, digitata durante la
    /// conversazione.
    /// </summary>
    /// <param name="preparedDestination">Numero o nome già inserito dall'operatore.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se la pressione ha prodotto un effetto.</returns>
    /// <remarks>
    /// <para>
    /// Risponde al modo in cui il trasferimento avviene davvero: il chiamante detta
    /// il numero <b>mentre parla</b>, e l'operatore lo scrive in quel momento. Senza
    /// questo percorso l'unica sequenza possibile era annotare il numero altrove,
    /// premere il tasto e ridigitarlo — con il chiamante già in attesa e nessuno a
    /// cui chiedere conferma se il foglietto era illeggibile.
    /// </para>
    /// <para>
    /// La destinazione viene risolta sulla rubrica e riletta nell'annuncio prima che
    /// parta qualunque cosa: è l'unica occasione di verifica che resta.
    /// </para>
    /// </remarks>
    public async Task<bool> HandleTransferKeyAsync(
        string preparedDestination,
        CancellationToken cancellationToken = default)
    {
        // Fuori dalla fase iniziale la destinazione preparata non ha senso: il tasto
        // sta confermando o completando qualcosa che e' gia' in corso.
        if (Phase != TransferPhase.Idle || string.IsNullOrWhiteSpace(preparedDestination))
        {
            return await HandleTransferKeyAsync(cancellationToken).ConfigureAwait(false);
        }

        return await BeginTransferAsync(preparedDestination.Trim(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invia subito un trasferimento cieco verso la destinazione indicata, senza
    /// attese e senza conferme.
    /// </summary>
    /// <param name="destination">Numero o nome digitato dall'operatore.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se il trasferimento è partito.</returns>
    /// <remarks>
    /// <para>
    /// È il percorso più corto possibile: si digita il numero, si preme Invio, la
    /// chiamata parte. Serve a smaltire la coda, dove ogni pressione risparmiata su
    /// una singola chiamata si moltiplica per il numero di chiamate del turno.
    /// </para>
    /// <para>
    /// Salta <b>tutti</b> e tre i controlli che il percorso normale applica: la
    /// finestra di doppia pressione, la conferma per le destinazioni esterne e
    /// l'avviso di destinazione non disponibile. È una scelta deliberata di chi
    /// gestisce il centralino, non una dimenticanza, ed è il motivo per cui vive in
    /// un metodo separato invece che come variante di
    /// <see cref="HandleTransferKeyAsync(CancellationToken)"/>: chi legge deve
    /// vedere subito che questa è la via senza rete.
    /// </para>
    /// <para>
    /// L'unico riscontro che resta è l'annuncio successivo, che rilegge la
    /// destinazione cifra per cifra. Non previene l'errore ma permette di
    /// accorgersene subito, che su un trasferimento cieco è la differenza fra
    /// richiamare l'interno giusto e non sapere dove sia finito il chiamante.
    /// </para>
    /// </remarks>
    public async Task<bool> BlindTransferNowAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null || session.Phase != TransferPhase.EnteringDestination) return RejectCommand();

        if (string.IsNullOrWhiteSpace(destination))
        {
            _announcer.Announce(Announcement.CommandResponse("Nessuna destinazione inserita", _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        // Una pressione del tasto di trasferimento gia' in sospeso verrebbe risolta
        // da Pump come consultazione, dopo che il trasferimento cieco e' gia'
        // partito: la chiamata risulterebbe trasferita e contemporaneamente si
        // chiamerebbe l'interno una seconda volta.
        lock (_gate)
        {
            _pendingKeyPressAt = null;
            _pendingConfirmation = null;
        }

        var trimmed = destination.Trim();
        var resolved = await ResolveDestinationAsync(trimmed, cancellationToken).ConfigureAwait(false);
        session.SetDestination(trimmed, resolved);

        return await ExecuteBlindTransferConfirmedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Chiama subito la destinazione per annunciarle la chiamata, senza passare dalla
    /// finestra di doppia pressione.
    /// </summary>
    /// <param name="destination">Numero o nome digitato dall'operatore.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se la consultazione è partita.</returns>
    /// <remarks>
    /// <para>
    /// È il gemello di <see cref="BlindTransferNowAsync"/> per l'altra metà del
    /// trasferimento, e nasce dai pulsanti. Sulla tastiera le due modalità si
    /// distinguono per la velocità con cui si preme due volte: è una destrezza che si
    /// impara, e con un tasto solo non c'era altro modo. Su un pulsante quella
    /// distinzione non serve — il pulsante dice già quale delle due si vuole — e
    /// soprattutto <b>sarebbe pericolosa</b>: due clic ravvicinati su «annuncia e
    /// trasferisci» verrebbero letti come doppia pressione, cioè come trasferimento
    /// cieco, mandando il chiamante a un interno senza che nessuno lo annunci.
    /// </para>
    /// <para>
    /// Diversamente dal trasferimento cieco, qui i controlli restano tutti: la
    /// conferma per le destinazioni esterne e l'avviso di interno non disponibile
    /// valgono ancora, perché questa è la via prudente e non quella rapida.
    /// </para>
    /// </remarks>
    public async Task<bool> AttendedTransferNowAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null || session.Phase != TransferPhase.EnteringDestination) return RejectCommand();

        if (string.IsNullOrWhiteSpace(destination))
        {
            _announcer.Announce(Announcement.CommandResponse("Nessuna destinazione inserita", _clock.Now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        // Una pressione del tasto rimasta in sospeso verrebbe risolta da Pump come una
        // seconda consultazione verso lo stesso interno, che squillerebbe due volte.
        //
        // LA CONFERMA IN SOSPESO INVECE NON SI BUTTA: E' UNA DOMANDA, E QUESTA
        // PRESSIONE E' LA RISPOSTA.
        //
        // Buttarla era il difetto piu' visibile del trasferimento, e si presentava
        // cosi': l'avviso preventivo diceva "Attenzione, interno tal dei tali fuori
        // servizio, premi X per procedere comunque", l'operatore premeva, e questo
        // metodo azzerava la conferma per poi rifare il medesimo controllo, che
        // riproduceva il medesimo avviso. Da Invio non si usciva piu': nel registro del
        // 17 agosto 2026, alle 10:22:02, sei pressioni in un secondo e mezzo, nessun
        // INVITE, e il trasferimento partito solo alle 10:22:05 con il tasto rapido —
        // che quella strada non ce l'aveva, perche' passa da HandleTransferKeyAsync,
        // dove la conferma viene consumata.
        PendingConfirmation? conferma;

        lock (_gate)
        {
            conferma = _pendingConfirmation;
            _pendingKeyPressAt = null;
            _pendingConfirmation = null;
        }

        var trimmed = destination.Trim();

        // La conferma vale per il numero su cui era stata chiesta. Se nel frattempo
        // l'operatore ne ha corretto una cifra — ed e' proprio quello che fa chi si
        // sente dire che l'interno e' fuori servizio — il controllo va rifatto sul
        // numero nuovo, altrimenti il "si', procedi" di prima varrebbe per una
        // destinazione che nessuno ha verificato.
        var confermaValida = conferma is not null
            && string.Equals(conferma.Destination.Trim(), trimmed, StringComparison.Ordinal);

        var resolved = await ResolveDestinationAsync(trimmed, cancellationToken).ConfigureAwait(false);
        session.SetDestination(trimmed, resolved);

        if (confermaValida)
        {
            _log.LogInformation(
                "Trasferimento verso {Destination}: l'avviso preventivo ({Genere}) e' stato confermato",
                trimmed, conferma!.Kind);

            // La variante la decide il gesto di adesso, non quella per cui l'avviso era
            // stato chiesto: l'operatore ha premuto il tasto che chiama l'interno per
            // annunciargli la chiamata, e quello si fa.
            await StartConsultationConfirmedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        await StartConsultationAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Interpreta il tasto di riaggancio durante una procedura di trasferimento.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>
    /// <c>true</c> se il trasferimento ha assorbito il comando; <c>false</c> se il
    /// tasto va gestito come un normale riaggancio.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Esiste per il caso più frequente del trasferimento con annuncio: l'operatore
    /// chiama l'interno, glielo annuncia, e l'interno <b>non lo vuole</b>. Il gesto
    /// naturale a quel punto è riagganciare la linea con cui si sta parlando, cioè
    /// quella dell'interno, e ritrovarsi con il chiamante.
    /// </para>
    /// <para>
    /// Senza questo percorso il tasto di riaggancio chiudeva la sola consultazione e
    /// lasciava la sessione di trasferimento aperta, con il chiamante fermo in stato
    /// di trasferimento e nessun audio ripreso: dal punto di vista dell'operatore,
    /// una linea muta senza spiegazione. Il recupero c'era già ma si raggiungeva solo
    /// con ESC, che è il tasto dell'annullamento e non quello che viene in mente
    /// dopo un "no, non me lo passare".
    /// </para>
    /// <para>
    /// Fuori dalla consultazione il comando <b>non</b> viene assorbito. In fase di
    /// inserimento della destinazione non esiste una seconda linea da chiudere, e
    /// interpretare il riaggancio come "chiudi il chiamante" sarebbe distruttivo per
    /// un tasto premuto per abitudine.
    /// </para>
    /// </remarks>
    public async Task<bool> HandleHangUpKeyAsync(CancellationToken cancellationToken = default)
    {
        if (Phase != TransferPhase.Consulting) return false;

        return await CancelAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Interpreta il tasto di alternanza durante una procedura di trasferimento.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>
    /// <c>true</c> se il trasferimento ha assorbito il comando; <c>false</c> se il
    /// tasto va gestito come un'alternanza ordinaria fra due chiamate.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Serve al gesto più comune del trasferimento con annuncio</b>: l'operatore ha
    /// il chiamante parcheggiato, sta parlando con l'interno, e ha bisogno di tornare
    /// un attimo dal chiamante — per chiedergli un nome, per dirgli che il collega
    /// arriva, per farsi ripetere il motivo. Poi torna dall'interno e completa. Su un
    /// centralino tradizionale è il gesto che si fa senza pensarci.
    /// </para>
    /// <para>
    /// <b>Il comando veniva rifiutato, e il motivo stava nella macchina a stati.</b>
    /// Durante il trasferimento il chiamante non è né in conversazione né in attesa: è
    /// in uno stato suo. L'alternanza ordinaria cerca una chiamata in attesa, non ne
    /// trovava nessuna e rispondeva <i>"nessuna chiamata in attesa da alternare"</i>
    /// mentre il chiamante era lì, fermo, da un minuto.
    /// </para>
    /// <para>
    /// <b>La procedura resta aperta.</b> Alternare non è annullare: la destinazione
    /// resta in memoria, la sessione resta viva e il tasto di trasferimento continua a
    /// completare da entrambi i lati. È la differenza fra tornare un attimo dal
    /// chiamante e dover ricominciare da capo.
    /// </para>
    /// <para>
    /// Il comando viene assorbito anche quando non c'è nulla da alternare — la
    /// destinazione squilla ancora — perché lì la risposta giusta è dire cosa sta
    /// succedendo, non lasciar cadere il tasto in un rifiuto generico.
    /// </para>
    /// </remarks>
    public async Task<bool> HandleSwapKeyAsync(CancellationToken cancellationToken = default)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null || session.IsFinished) return false;

        // In fase di completamento le due linee sono in mano al centralino: qualunque
        // cosa si facesse qui litigherebbe con il REFER gia' partito.
        if (session.Phase == TransferPhase.Completing) return false;

        var original = session.OriginalCall;
        if (CallStateMachine.IsTerminal(original.State)) return false;

        // Non c'e' ancora una seconda conversazione da alternare. I due casi si dicono
        // in modo diverso perche' si risolvono in modo diverso: con un telefono che
        // squilla si puo' aspettare, con la destinazione ancora da scegliere no.
        if (session.ConsultationCall is not { } consultation
            || consultation.State is not (CallState.Active or CallState.Held))
        {
            _announcer.Announce(Announcement.CommandResponse(
                session.ConsultationCall is null
                    ? AnnouncementTexts.SwapBeforeConsultation
                    : AnnouncementTexts.SwapWhileDestinationRinging(session.DestinationSpeech),
                _clock.Now));

            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return true;
        }

        try
        {
            var tornaAlChiamante = original.State != CallState.Active;

            if (tornaAlChiamante)
            {
                await ParkAsync(consultation, asTransferSubject: false, cancellationToken)
                    .ConfigureAwait(false);

                await ResumeAsync(original, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Il chiamante torna parcheggiato dentro la procedura, non
                // semplicemente in attesa: e' lo stato da cui il completamento sa
                // partire, ed e' lo stesso in cui l'aveva messo l'avvio.
                await ParkAsync(original, asTransferSubject: true, cancellationToken)
                    .ConfigureAwait(false);

                await ResumeAsync(consultation, cancellationToken).ConfigureAwait(false);
            }

            var inLinea = tornaAlChiamante ? original : consultation;
            var inAttesa = tornaAlChiamante ? consultation : original;

            // Un annuncio solo per l'intera operazione: le due transizioni sono
            // soppresse perche' due frasi consecutive direbbero a pezzi una cosa sola.
            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.SwappedDuringTransfer(
                    inLinea.RemoteParty, inAttesa.RemoteParty, Options.TransferKeyName),
                _clock.Now,
                braille: $"={inLinea.RemoteParty.DisplayName ?? inLinea.RemoteParty.Number}"));

            return true;
        }
        catch (Exception ex)
        {
            // Un'alternanza a meta' lascerebbe due linee mute e nessuna delle due in
            // conversazione: si recupera il chiamante, che e' l'unica cosa che non si
            // puo' perdere.
            _log.LogError(ex, "Alternanza durante il trasferimento non riuscita");
            await AbortAsync(session, TransferOutcome.Failed, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// Mette una linea in attesa sul filo e nel modello, in silenzio.
    /// </summary>
    /// <param name="call">Linea da parcheggiare.</param>
    /// <param name="asTransferSubject">
    /// <c>true</c> per il chiamante, che va riportato nello stato di trasferimento e non
    /// nella semplice attesa: è quello da cui il completamento sa ripartire.
    /// </param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// L'evento di attesa si applica comunque prima dell'eventuale stato di
    /// trasferimento, perché è quello che segna <b>da quando</b> la persona sta
    /// aspettando: saltarlo lascerebbe il cronometro dell'attesa fermo a zero per tutta
    /// la durata della procedura.
    /// </remarks>
    private async Task ParkAsync(Call call, bool asTransferSubject, CancellationToken cancellationToken)
    {
        if (CallStateMachine.HasActiveAudio(call.State))
        {
            await _telephony.HoldAsync(call.Id, cancellationToken).ConfigureAwait(false);
            _calls.ApplyTrigger(call.Id, CallTrigger.Hold, suppressAnnouncement: true);
        }

        if (asTransferSubject && call.State != CallState.Transferring)
        {
            _calls.ApplyTrigger(call.Id, CallTrigger.TransferStarted, suppressAnnouncement: true);
        }
    }

    /// <summary>Riporta una linea in conversazione sul filo e nel modello, in silenzio.</summary>
    /// <param name="call">Linea da riprendere.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    private async Task ResumeAsync(Call call, CancellationToken cancellationToken)
    {
        if (call.State == CallState.Active) return;

        await _telephony.ResumeAsync(call.Id, cancellationToken).ConfigureAwait(false);
        _calls.ApplyTrigger(call.Id, CallTrigger.Resume, suppressAnnouncement: true);
    }

    /// <summary>
    /// Annulla la procedura in corso e riporta il chiamante originale in linea.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se c'era una procedura da annullare.</returns>
    public async Task<bool> CancelAsync(CancellationToken cancellationToken = default)
    {
        TransferSession? session;
        lock (_gate)
        {
            session = _session;
            _pendingKeyPressAt = null;
            _pendingConfirmation = null;
        }

        if (session is null || session.IsFinished) return false;

        await AbortAsync(session, TransferOutcome.CancelledByOperator, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Imposta la destinazione mentre l'operatore la digita o la sceglie dalla
    /// rubrica.
    /// </summary>
    /// <param name="destination">Numero o interno.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    public async Task SetDestinationAsync(string destination, CancellationToken cancellationToken = default)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null || session.Phase != TransferPhase.EnteringDestination) return;

        var resolved = await ResolveDestinationAsync(destination, cancellationToken).ConfigureAwait(false);
        session.SetDestination(destination, resolved);
    }

    /// <summary>
    /// Fa avanzare i comportamenti temporizzati: risoluzione della doppia pressione
    /// e timeout della consultazione.
    /// </summary>
    /// <param name="now">Istante corrente; se omesso si usa l'orologio iniettato.</param>
    /// <remarks>
    /// Va richiamato dallo stesso ciclo che alimenta la pipeline di annuncio.
    /// </remarks>
    public void Pump(DateTimeOffset? now = null)
    {
        var timestamp = now ?? _clock.Now;

        ResolvePendingKeyPress(timestamp);
        CheckConsultationTimeout(timestamp);
    }

    // -----------------------------------------------------------------------
    // Fasi
    // -----------------------------------------------------------------------

    /// <param name="preparedDestination">
    /// Destinazione già digitata durante la conversazione, oppure <c>null</c> se
    /// l'operatore la inserirà adesso.
    /// </param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    private async Task<bool> BeginTransferAsync(string? preparedDestination, CancellationToken cancellationToken)
    {
        var call = _calls.ActiveCall ?? _calls.HeldCalls.FirstOrDefault();
        if (call is null)
        {
            return RejectCommand();
        }

        // Il chiamante va messo in attesa prima di qualunque altra cosa: durante
        // l'inserimento della destinazione non deve sentire l'operatore digitare.
        if (call.State == CallState.Active)
        {
            await _telephony.HoldAsync(call.Id, cancellationToken).ConfigureAwait(false);
            _calls.ApplyTrigger(call.Id, CallTrigger.Hold, suppressAnnouncement: true);
        }

        _calls.ApplyTrigger(call.Id, CallTrigger.TransferStarted, suppressAnnouncement: true);

        var session = new TransferSession(call, _clock.Now);
        lock (_gate) _session = session;

        if (!string.IsNullOrWhiteSpace(preparedDestination))
        {
            var resolved = await ResolveDestinationAsync(preparedDestination, cancellationToken)
                .ConfigureAwait(false);

            session.SetDestination(preparedDestination, resolved);

            RaisePhaseChanged(TransferPhase.EnteringDestination);

            // LA PRESSIONE CHE HA PORTATO QUI E' GIA' LA CONFERMA DELLA DESTINAZIONE.
            //
            // Il numero e' nel campo, l'operatore l'ha appena scritto sotto dettatura
            // e ha premuto il tasto: chiedergli di premerlo una seconda volta per
            // dire di nuovo "si', quel numero" non aggiunge nessuna verifica, perche'
            // fra le due pressioni non succede niente che possa cambiargli idea. In
            // centralino quella pressione in piu' si paga su ogni chiamata del turno.
            //
            // La rilettura della destinazione non si perde: la fa l'annuncio della
            // consultazione, "chiamata a ... in corso", che parte adesso. E il
            // trasferimento resta comunque revocabile fino all'ultima pressione,
            // perche' finche' l'operatore non completa e' lui a parlare con
            // l'interno, non il chiamante.
            return HandleDestinationConfirmKey(_clock.Now);
        }

        // L'annuncio dice cosa fanno i tasti adesso, non solo che la modalita' e'
        // iniziata: e' la differenza fra un'interfaccia usabile al buio e una che
        // costringe a ricordare a memoria una sequenza.
        _announcer.Announce(Announcement.CommandResponse(
            AnnouncementTexts.TransferStarted(Options.TransferKeyName),
            _clock.Now,
            braille: "TRASF>"));

        RaisePhaseChanged(TransferPhase.EnteringDestination);
        return true;
    }

    /// <summary>
    /// Gestisce la pressione che conferma la destinazione, distinguendo singola da
    /// doppia.
    /// </summary>
    private bool HandleDestinationConfirmKey(DateTimeOffset now)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null) return RejectCommand();

        if (string.IsNullOrWhiteSpace(session.Destination))
        {
            _announcer.Announce(Announcement.CommandResponse("Nessuna destinazione inserita", now));
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return false;
        }

        if (Options.Mode == TransferInteractionMode.ProgressiveOnly)
        {
            // Nessuna ambiguita' da risolvere: si parte subito con la consultazione.
            _ = StartConsultationAsync(CancellationToken.None);
            return true;
        }

        lock (_gate)
        {
            if (_pendingKeyPressAt is { } first && now - first <= Options.DoubleTapWindow)
            {
                // Seconda pressione dentro la finestra: e' un trasferimento cieco.
                _pendingKeyPressAt = null;
                _ = ExecuteBlindTransferAsync(CancellationToken.None);
                return true;
            }

            // Prima pressione: si attende la scadenza della finestra per sapere se
            // ne arrivera' una seconda. La risoluzione avviene in Pump.
            _pendingKeyPressAt = now;
        }

        return true;
    }

    /// <summary>
    /// Se la finestra di doppia pressione è scaduta senza una seconda pressione,
    /// avvia la consultazione.
    /// </summary>
    private void ResolvePendingKeyPress(DateTimeOffset now)
    {
        bool shouldConsult = false;

        lock (_gate)
        {
            if (_pendingKeyPressAt is { } pressedAt && now - pressedAt > Options.DoubleTapWindow)
            {
                _pendingKeyPressAt = null;
                shouldConsult = _session?.Phase == TransferPhase.EnteringDestination;
            }
        }

        if (shouldConsult) _ = StartConsultationAsync(CancellationToken.None);
    }

    private async Task StartConsultationAsync(CancellationToken cancellationToken)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null || session.Phase != TransferPhase.EnteringDestination) return;

        if (await RequiresConfirmationAsync(session, isBlind: false, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var callId = await _telephony.DialAsync(session.Destination, cancellationToken).ConfigureAwait(false);
            var party = session.ResolvedDestination ?? new CallParty(session.Destination);
            var consultation = _calls.RegisterOutboundCall(callId, party);

            session.BeginConsultation(consultation, _clock.Now);

            _announcer.Announce(Announcement.High(
                AnnouncementTexts.ConsultationStarted(session.DestinationSpeech),
                _clock.Now,
                coalesceKey: "transfer:consultation",
                braille: $"CONS {session.DestinationSpeech}"));

            RaisePhaseChanged(TransferPhase.Consulting);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Avvio della consultazione verso {Destination} non riuscito", session.Destination);
            await AbortAsync(session, TransferOutcome.Failed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> ExecuteBlindTransferAsync(CancellationToken cancellationToken)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null) return false;

        if (await RequiresConfirmationAsync(session, isBlind: true, cancellationToken).ConfigureAwait(false))
            return true;

        try
        {
            session.BeginCompletion(TransferMode.Blind);
            RaisePhaseChanged(TransferPhase.Completing);

            await _telephony
                .BlindTransferAsync(session.OriginalCall.Id, session.Destination, cancellationToken)
                .ConfigureAwait(false);

            _announcer.Announce(Announcement.High(
                AnnouncementTexts.BlindTransferSent(SpokenDestination(session)),
                _clock.Now,
                EarconKind.TransferSucceeded,
                coalesceKey: "transfer:result",
                braille: "TRASF OK"));

            // Il trasferimento cieco e' "inviato", non ancora "riuscito": l'esito
            // definitivo arriva dai NOTIFY del centralino e puo' essere negativo.
            // Fino ad allora la sessione resta in Completing.
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Trasferimento cieco verso {Destination} non riuscito", session.Destination);
            await AbortAsync(session, TransferOutcome.Failed, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> CompleteAttendedTransferAsync(CancellationToken cancellationToken)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session?.ConsultationCall is not { } consultation) return RejectCommand();

        try
        {
            // LE DUE LINEE VANNO RIMESSE COME IL COMPLETAMENTO LE VUOLE.
            //
            // Con l'alternanza l'operatore puo' trovarsi dalla parte opposta: in linea
            // con il chiamante e l'interno in attesa. Da li' il REFER partirebbe lo
            // stesso, ma con due conseguenze sbagliate — la modalita' verrebbe letta
            // come semi-cieca su una destinazione che invece aveva gia' risposto, e
            // l'esito positivo non troverebbe il chiamante nello stato da cui la sua
            // chiusura e' prevista, lasciandolo aperto a trasferimento riuscito.
            var original = session.OriginalCall;

            if (!CallStateMachine.IsTerminal(original.State) && original.State != CallState.Transferring)
            {
                await ParkAsync(original, asTransferSubject: true, cancellationToken).ConfigureAwait(false);
            }

            if (consultation.State == CallState.Held)
            {
                await ResumeAsync(consultation, cancellationToken).ConfigureAwait(false);
            }

            // Se la destinazione sta ancora squillando il trasferimento e'
            // semi-cieco: e' il comportamento standard dei centralini e va
            // annunciato per quello che e', perche' l'operatore non ha modo di
            // sapere se il collega avesse gia' alzato la cornetta.
            var mode = consultation.State == CallState.Active
                ? TransferMode.Attended
                : TransferMode.SemiAttended;

            session.BeginCompletion(mode);
            RaisePhaseChanged(TransferPhase.Completing);

            await _telephony
                .AttendedTransferAsync(session.OriginalCall.Id, consultation.Id, cancellationToken)
                .ConfigureAwait(false);

            // LA LINEA DI CONSULTAZIONE E' FINITA, E VA DETTO AL RESTO DEL PROGRAMMA.
            //
            // Sul trasferimento semi-cieco il motore telefonico chiude da se' la linea
            // verso l'interno — l'ha appena annullata con un CANCEL, perche' da questo
            // momento e' il centralino a farlo squillare per conto del chiamante — ma
            // quella chiusura non produce nessun evento: e' un annullamento nostro, e
            // il motore tace apposta per non far annunciare un rifiuto che non c'e'
            // stato. Il risultato era che per il gestore delle chiamate quella linea
            // restava in squillo per sempre, e da li' due sintomi che l'operatore
            // riferiva come uno solo:
            //
            //   - il tono di libero continuava a suonare dopo il trasferimento, perche'
            //     si ferma solo su una transizione di quella chiamata;
            //   - restava a schermo una chiamata uscente in attesa di risposta, che
            //     nessun tasto poteva piu' chiudere se non il riaggancio a mano.
            //
            // Nel registro dell'11 agosto 2026, alle 15:29:10 e alle 15:31:11, la linea
            // fantasma e' sopravvissuta al trasferimento riuscito e l'operatore l'ha
            // dovuta chiudere a mano trenta secondi dopo.
            //
            // L'annuncio si sopprime: quello che conta l'ha gia' detto l'esito del
            // trasferimento, e "chiamata annullata" subito dopo "trasferimento
            // riuscito" direbbe il contrario di quello che e' successo.
            //
            // Sul trasferimento con annuncio vero — l'interno aveva gia' risposto — la
            // linea non la chiudiamo noi: la chiude il centralino con un BYE quando
            // sostituisce il dialogo, e quel BYE un evento lo produce.
            if (mode == TransferMode.SemiAttended && !CallStateMachine.IsTerminal(consultation.State))
            {
                _calls.ApplyTrigger(consultation.Id, CallTrigger.HangUp, suppressAnnouncement: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Completamento del trasferimento non riuscito");
            await AbortAsync(session, TransferOutcome.Failed, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Conferme preventive
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifica se la destinazione richiede una conferma esplicita e, in tal caso,
    /// la chiede.
    /// </summary>
    /// <returns><c>true</c> se la procedura è stata sospesa in attesa di conferma.</returns>
    private async Task<bool> RequiresConfirmationAsync(
        TransferSession session,
        bool isBlind,
        CancellationToken cancellationToken)
    {
        var destination = session.Destination;

        // 1. Destinazione esterna: protezione contro la composizione errata e
        //    contro l'uso improprio del centralino.
        if (Options.ConfirmExternalDestinations && !IsInternalNumber(destination))
        {
            lock (_gate) _pendingConfirmation =
                new PendingConfirmation(isBlind, ConfirmationKind.ExternalDestination, destination);

            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.ExternalTransferConfirmation(
                    SpeechFormatter.Digits(destination),
                    Options.TransferKeyName),
                _clock.Now));

            return true;
        }

        if (!Options.WarnOnUnavailableDestination || _contacts is null) return false;

        // 2. Destinazione non disponibile secondo la presenza nota.
        try
        {
            var matches = await _contacts.ResolveByNumberAsync(destination, cancellationToken)
                .ConfigureAwait(false);

            if (matches.Count == 0) return false;

            var contact = matches[0];

            // IL DIVIETO SI CERCA FRA TUTTE LE VOCI DEL NUMERO, NON SOLO NELLA PRIMA.
            //
            // Su un apparecchio condiviso basta che una delle voci sia contrassegnata
            // perche' il trasferimento vada confermato: il contrassegno riguarda il
            // telefono che squilla, non la riga di rubrica con cui lo si e' cercato.
            // Guardare solo la prima significava che l'ordine dei risultati decideva
            // se l'avviso scattasse, cioe' che a volte non scattava.
            if (matches.FirstOrDefault(c => c.DoNotTransfer) is { } riservato)
            {
                lock (_gate) _pendingConfirmation =
                    new PendingConfirmation(isBlind, ConfirmationKind.DoNotTransfer, destination);

                _announcer.Announce(Announcement.CommandResponse(
                    $"Attenzione: {riservato.SpeechName} non riceve trasferimenti diretti. "
                    + $"{Options.TransferKeyName} per procedere comunque, ESC per annullare",
                    _clock.Now));

                return true;
            }

            // Lo stato vivo, se il centralino lo fornisce, vince su quello memorizzato
            // nella rubrica: quest'ultimo e' fermo al momento dell'importazione, e un
            // "libero" di ieri sera manderebbe il chiamante su un telefono spento.
            // Quando dal vivo non si sa nulla si ricade sul valore in archivio, che
            // resta meglio di niente.
            var live = _presence?.Get(destination) ?? PresenceState.Unknown;
            var presence = live != PresenceState.Unknown ? live : contact.Presence;

            if (PresenceMonitorService.DiscouragesTransfer(presence))
            {
                lock (_gate) _pendingConfirmation =
                    new PendingConfirmation(isBlind, ConfirmationKind.PresenceWarning, destination);

                _announcer.Announce(Announcement.CommandResponse(
                    AnnouncementTexts.TransferDestinationWarning(
                        contact.SpeechName, presence, Options.TransferKeyName),
                    _clock.Now));

                return true;
            }
        }
        catch (Exception ex)
        {
            // Un problema di rubrica non deve impedire un trasferimento: si procede
            // senza avviso, che e' comunque meglio che bloccare l'operatore.
            _log.LogWarning(ex, "Verifica della destinazione {Destination} non riuscita", destination);
        }

        return false;
    }

    private async Task<bool> ExecuteConfirmedActionAsync(
        PendingConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        if (confirmation.IsBlind)
        {
            return await ExecuteBlindTransferConfirmedAsync(cancellationToken).ConfigureAwait(false);
        }

        await StartConsultationConfirmedAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Esegue il trasferimento cieco saltando i controlli già confermati.</summary>
    private async Task<bool> ExecuteBlindTransferConfirmedAsync(CancellationToken cancellationToken)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null) return false;

        try
        {
            session.BeginCompletion(TransferMode.Blind);
            RaisePhaseChanged(TransferPhase.Completing);

            await _telephony
                .BlindTransferAsync(session.OriginalCall.Id, session.Destination, cancellationToken)
                .ConfigureAwait(false);

            // La destinazione si rilegge cifra per cifra, non come numero intero.
            // Su un trasferimento cieco questo annuncio e' l'unico riscontro che
            // esiste: se dice "quattromilaventuno" l'operatore non puo' confrontarlo
            // con quello che ha digitato, e un errore resta invisibile finche' non
            // richiama qualcuno per chiedere dove sia finito il chiamante.
            var spoken = SpeechFormatter.Party(
                session.ResolvedDestination?.SpeechName,
                session.Destination,
                Options.DigitGrouping,
                includeNumberWithName: true);

            _announcer.Announce(Announcement.High(
                AnnouncementTexts.BlindTransferSent(spoken),
                _clock.Now,
                EarconKind.TransferSucceeded,
                coalesceKey: "transfer:result"));

            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Trasferimento cieco confermato non riuscito");
            await AbortAsync(session, TransferOutcome.Failed, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Avvia la consultazione saltando i controlli già confermati.</summary>
    private async Task StartConsultationConfirmedAsync(CancellationToken cancellationToken)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null || session.Phase != TransferPhase.EnteringDestination) return;

        try
        {
            var callId = await _telephony.DialAsync(session.Destination, cancellationToken).ConfigureAwait(false);
            var party = session.ResolvedDestination ?? new CallParty(session.Destination);
            var consultation = _calls.RegisterOutboundCall(callId, party);

            session.BeginConsultation(consultation, _clock.Now);

            _announcer.Announce(Announcement.High(
                AnnouncementTexts.ConsultationStarted(session.DestinationSpeech),
                _clock.Now,
                coalesceKey: "transfer:consultation"));

            RaisePhaseChanged(TransferPhase.Consulting);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Avvio della consultazione confermata non riuscito");
            await AbortAsync(session, TransferOutcome.Failed, cancellationToken).ConfigureAwait(false);
        }
    }

    // -----------------------------------------------------------------------
    // Recupero
    // -----------------------------------------------------------------------

    /// <summary>
    /// Chiude la procedura con esito negativo e riporta il chiamante originale in
    /// linea.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È il metodo più importante della classe. Un trasferimento che fallisce senza
    /// recupero lascia il chiamante in attesa a tempo indeterminato, e l'operatore
    /// non vedente non ha modo di accorgersene: non vede una linea che lampeggia,
    /// e il silenzio non lo distingue da una pausa qualsiasi.
    /// </para>
    /// </remarks>
    private async Task AbortAsync(
        TransferSession session,
        TransferOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (session.IsFinished) return;

        session.MarkAborted(outcome);

        // 1. Chiude la consultazione, se ne era stata avviata una.
        if (session.ConsultationCall is { } consultation
            && !CallStateMachine.IsTerminal(consultation.State))
        {
            try
            {
                await _telephony.HangUpAsync(consultation.Id, cancellationToken).ConfigureAwait(false);
                _calls.ApplyTrigger(consultation.Id, CallTrigger.HangUp, suppressAnnouncement: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Chiusura della consultazione {CallId} non riuscita", consultation.Id);
            }
        }

        // 2. Riporta la chiamata originale sotto il controllo dell'operatore.
        var original = session.OriginalCall;
        CallParty? recovered = null;

        if (!CallStateMachine.IsTerminal(original.State))
        {
            try
            {
                switch (original.State)
                {
                    // GIA' IN LINEA, e succede dopo un'alternanza: l'operatore stava
                    // parlando con il chiamante quando l'interno ha chiuso. Qui non
                    // c'e' niente da riprendere, e un secondo re-INVITE sulla stessa
                    // linea sarebbe solo un modo di disturbare una conversazione che
                    // sta funzionando.
                    case CallState.Active:
                        break;

                    case CallState.Transferring:
                        _calls.ApplyTrigger(original.Id, CallTrigger.TransferAborted, suppressAnnouncement: true);
                        await _telephony.ResumeAsync(original.Id, cancellationToken).ConfigureAwait(false);
                        break;

                    // In attesa vera: ci si arriva se l'operatore ha usato il tasto
                    // dell'attesa sul chiamante dopo essere tornato da lui.
                    default:
                        _calls.ApplyTrigger(original.Id, CallTrigger.Resume, suppressAnnouncement: true);
                        await _telephony.ResumeAsync(original.Id, cancellationToken).ConfigureAwait(false);
                        break;
                }

                recovered = original.RemoteParty;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Recupero della chiamata {CallId} non riuscito", original.Id);
            }
        }

        // 3. Un unico annuncio che dice cosa e' andato storto e cosa succede adesso.
        //    Due annunci separati ("interno occupato" e poi "ripresa chiamata")
        //    costringerebbero ad ascoltare due frasi per capire una cosa sola.
        // DEVE passare anche durante la conversazione, e il motivo e' che la
        // conversazione a cui si torna e' proprio l'oggetto dell'annuncio.
        //
        // La regola del silenzio durante la conversazione esiste per non parlare
        // sopra l'interlocutore, ed e' giusta. Ma qui la chiamata e' appena stata
        // ripresa: l'operatore non sa ancora con chi si trova, e la frase che
        // glielo dice veniva scartata proprio perche' la ripresa era riuscita.
        // Il registro l'ha mostrato:
        //
        //   DBG AnnouncementService  Annuncio scartato (ConversationActive):
        //       Trasferimento annullato. Ripresa chiamata con 7129
        //
        // Il risultato era un trasferimento annullato in silenzio, indistinguibile
        // da un comando che non ha funzionato.
        _announcer.Announce(new Announcement
        {
            Text = AnnouncementTexts.TransferFailed(outcome, session.DestinationSpeech, recovered),
            Priority = AnnouncementPriority.High,
            Earcon = EarconKind.TransferFailed,
            CoalesceKey = "transfer:result",
            BrailleText = recovered is null ? "TRASF KO" : $"={recovered.DisplayName ?? recovered.Number}",
            CreatedAt = _clock.Now,
            BypassConversationSilence = true,
        });

        lock (_gate)
        {
            _session = null;
            _pendingKeyPressAt = null;
            _pendingConfirmation = null;
        }

        RaisePhaseChanged(TransferPhase.Idle);
    }

    private void CheckConsultationTimeout(DateTimeOffset now)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session?.Phase != TransferPhase.Consulting) return;
        if (session.ConsultationStartedAt is not { } startedAt) return;
        if (now - startedAt < Options.ConsultationTimeout) return;

        // La destinazione squilla da troppo tempo: si recupera il chiamante senza
        // aspettare che sia l'operatore ad accorgersene.
        if (session.ConsultationCall?.State is CallState.Calling or CallState.Alerting)
        {
            _ = AbortAsync(session, TransferOutcome.DestinationNoAnswer, CancellationToken.None);
        }
    }

    // -----------------------------------------------------------------------
    // Eventi
    // -----------------------------------------------------------------------

    private void OnCallStateChanged(object? sender, CallStateChangedEventArgs e)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null || session.IsFinished) return;

        // La destinazione ha risposto: si annuncia e si dice come completare.
        //
        // SI GUARDA L'EVENTO, NON LO STATO DI ARRIVO. Con l'alternanza l'interno
        // ripassa in conversazione ogni volta che l'operatore torna da lui, e leggendo
        // il solo stato di arrivo si sentiva "ha risposto" a ogni giro — su una linea
        // aperta da un minuto, cioe' l'informazione piu' fuorviante possibile.
        if (session.ConsultationCall?.Id == e.Call.Id
            && e.Transition.Trigger == CallTrigger.RemoteAnswered
            && e.Transition.To == CallState.Active
            && session.Phase == TransferPhase.Consulting)
        {
            _announcer.Announce(Announcement.High(
                AnnouncementTexts.ConsultationAnswered(session.DestinationSpeech, Options.TransferKeyName),
                e.At,
                EarconKind.CallAnswered,
                coalesceKey: "transfer:consultation"));
            return;
        }

        // La destinazione ha rifiutato, era occupata o ha chiuso: recupero.
        if (session.ConsultationCall?.Id == e.Call.Id
            && CallStateMachine.IsTerminal(e.Transition.To)
            && session.Phase == TransferPhase.Consulting)
        {
            var outcome = e.Call.EndReason switch
            {
                CallEndReason.Busy => TransferOutcome.DestinationBusy,
                CallEndReason.NoAnswer => TransferOutcome.DestinationNoAnswer,
                CallEndReason.Rejected => TransferOutcome.DestinationRejected,
                CallEndReason.DoNotDisturb => TransferOutcome.DestinationRejected,
                _ => TransferOutcome.DestinationNoAnswer,
            };

            _ = AbortAsync(session, outcome, CancellationToken.None);
            return;
        }

        // Il chiamante originale ha riagganciato mentre l'operatore consultava.
        // La consultazione resta in piedi come chiamata normale: chiuderla sarebbe
        // scortese verso il collega che ha appena risposto.
        if (session.OriginalCall.Id == e.Call.Id && CallStateMachine.IsTerminal(e.Transition.To))
        {
            // Chi ha chiuso cambia sia il motivo sia cosa vale la pena dire.
            var closedByOperator = e.Transition.Trigger == CallTrigger.HangUp;

            session.MarkAborted(closedByOperator
                ? TransferOutcome.CancelledByOperator
                : TransferOutcome.OriginalCallerHungUp);

            // LA LINEA CHE RESTA VA RIPRESA, non solo lasciata aperta.
            //
            // Ci si arriva con l'alternanza: l'operatore era tornato dal chiamante e
            // l'interno stava in attesa. Se ora il chiamante chiude, senza questa
            // ripresa la postazione resterebbe con una sola linea, in attesa, e
            // nessuno in conversazione — cioe' un silenzio che dall'esterno non si
            // distingue da una chiamata caduta, mentre il collega e' ancora li'.
            if (session.ConsultationCall is { } rimasta && rimasta.State == CallState.Held)
            {
                _ = ResumeAsync(rimasta, CancellationToken.None);
            }

            // Se e' stato l'operatore a riagganciare, la chiusura e' gia' stata
            // annunciata dalla transizione: qui si aggiunge qualcosa solo se resta
            // una seconda linea aperta, che e' l'unica informazione che non ha
            // ancora. Dirgli "il chiamante ha riagganciato" quando ha riagganciato
            // lui sarebbe semplicemente falso.
            var text = (closedByOperator, session.ConsultationCall) switch
            {
                (true, { } c) => $"Trasferimento interrotto. Resta attiva la chiamata con {c.RemoteParty.SpeechName}",
                (true, null) => null,
                (false, { } c) => $"Il chiamante ha riagganciato. Resta attiva la chiamata con {c.RemoteParty.SpeechName}",
                (false, null) => "Il chiamante ha riagganciato",
            };

            if (text is not null)
            {
                _announcer.Announce(Announcement.High(
                    text,
                    e.At,
                    EarconKind.TransferFailed,
                    coalesceKey: "transfer:result"));
            }

            lock (_gate)
            {
                _session = null;
                _pendingKeyPressAt = null;
                _pendingConfirmation = null;
            }

            RaisePhaseChanged(TransferPhase.Idle);
        }
    }

    private void OnTransferProgress(object? sender, TransferProgressEventArgs e)
    {
        TransferSession? session;
        lock (_gate) session = _session;

        if (session is null || session.IsFinished) return;
        if (session.OriginalCall.Id != e.CallId) return;
        if (e.Outcome is not { } outcome) return;

        if (outcome == TransferOutcome.Succeeded)
        {
            session.MarkSucceeded();
            _calls.ApplyTrigger(session.OriginalCall.Id, CallTrigger.TransferCompleted, suppressAnnouncement: true);

            // ANCHE LA LINEA CON LA DESTINAZIONE E' FINITA CON IL TRASFERIMENTO,
            // E VA CHIUSA QUI.
            //
            // Sul trasferimento con annuncio vero — la destinazione aveva gia'
            // risposto — quella linea non la chiudiamo noi: la chiude il centralino
            // con un BYE quando sostituisce il dialogo. Quel BYE arriva DOPO questo
            // punto, quando la sessione di trasferimento e' gia' stata azzerata,
            // quindi nessuno sapeva piu' che quella chiusura era la conclusione di un
            // trasferimento riuscito. Veniva percio' trattata come una chiusura
            // qualunque della controparte, e l'operatore sentiva:
            //
            //     "l'interno 5000 ha chiuso, durata 4 secondi"
            //
            // subito dopo aver sentito che il trasferimento era riuscito. Due
            // affermazioni che si contraddicono, di cui la seconda falsa.
            //
            // Si vedeva quasi sempre trasferendo a un risponditore automatico, e non
            // e' un caso: un IVR risponde all'istante, quindi il trasferimento e'
            // sempre di quelli "con annuncio" e la linea viene sempre chiusa dal
            // centralino. Verso un interno umano capitava di meno solo perche' spesso
            // si completa mentre squilla ancora, e quel percorso era gia' corretto.
            //
            // Chiudendola qui, con il motivo giusto e senza annuncio, il BYE che
            // arriva dopo trova una chiamata gia' conclusa e viene ignorato. Il
            // motivo "trasferita" finisce anche nell'elenco delle ultime chiamate,
            // dove prima quella riga risultava una normale uscente chiusa dall'altra
            // parte.
            if (session.ConsultationCall is { } consultation
                && !CallStateMachine.IsTerminal(consultation.State))
            {
                _calls.ApplyTrigger(
                    consultation.Id,
                    CallTrigger.HangUp,
                    CallEndReason.Transferred,
                    suppressAnnouncement: true);
            }

            _announcer.Announce(Announcement.High(
                AnnouncementTexts.TransferCompleted(
                    session.OriginalCall.RemoteParty.SpeechName,
                    SpokenDestination(session)),
                e.At,
                EarconKind.TransferSucceeded,
                coalesceKey: "transfer:result",
                braille: "TRASF OK"));

            lock (_gate)
            {
                _session = null;
                _pendingConfirmation = null;
            }

            RaisePhaseChanged(TransferPhase.Idle);
            return;
        }

        // Il centralino ha rifiutato il REFER: si ripiega sulla consultazione,
        // annunciandolo. Copre le configurazioni in cui il trasferimento cieco da
        // client di terze parti e' disabilitato per policy.
        if (outcome == TransferOutcome.RejectedByPbx && session.Mode == TransferMode.Blind)
        {
            _announcer.Announce(Announcement.High(
                AnnouncementTexts.BlindTransferNotSupported,
                e.At,
                coalesceKey: "transfer:result"));

            session.BeginCompletion(TransferMode.Attended);
            _ = StartConsultationConfirmedAsync(CancellationToken.None);
            return;
        }

        _ = AbortAsync(session, outcome, CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Ausiliari
    // -----------------------------------------------------------------------

    private bool RejectCommand()
    {
        _announcer.Announce(Announcement.CommandResponse(
            AnnouncementTexts.CommandNotAvailable, _clock.Now));
        _announcer.PlayEarcon(EarconKind.CommandRejected);
        return false;
    }

    /// <summary>
    /// Determina se un numero è un interno del centralino.
    /// </summary>
    /// <remarks>
    /// Regola volutamente semplice, basata sulla lunghezza. È sufficiente per la
    /// numerazione interna tipica di una PA italiana e non richiede configurazione
    /// aggiuntiva; se un'installazione avesse un piano di numerazione anomalo, il
    /// criterio va spostato nelle impostazioni insieme alle regole di
    /// normalizzazione dei numeri.
    /// </remarks>
    private bool IsInternalNumber(string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return false;

        var digits = number.Count(char.IsDigit);
        return digits > 0
               && digits <= Options.MaxInternalNumberLength
               && !number.StartsWith('+');
    }

    /// <summary>
    /// La destinazione come si pronuncia negli annunci che chiudono il trasferimento:
    /// il nome che le dà la rubrica, oppure il numero cifra per cifra.
    /// </summary>
    /// <param name="session">Trasferimento in corso.</param>
    /// <remarks>
    /// <para>
    /// <see cref="TransferSession.DestinationSpeech"/> ricade sul numero così come è
    /// stato digitato, e «4021» passato intero alla sintesi si sente
    /// «quattromilaventuno». A trasferimento concluso l'annuncio diceva il nome quando
    /// la rubrica ne aveva uno, e fuori rubrica un numero che l'operatore non
    /// riconosceva come l'interno a cui aveva appena passato il chiamante: cioè proprio
    /// la cosa che quell'annuncio serve a verificare.
    /// </para>
    /// <para>
    /// Una voce di rubrica senza nome vale come fuori rubrica: il suo nome sarebbe il
    /// numero stesso, e <see cref="SpeechFormatter.Party"/> non lo prende per un nome.
    /// </para>
    /// </remarks>
    private string SpokenDestination(TransferSession session)
        => SpeechFormatter.Party(session.ResolvedDestination?.SpeechName, session.Destination, Options.DigitGrouping);

    private async Task<CallParty?> ResolveDestinationAsync(string destination, CancellationToken cancellationToken)
    {
        if (_contacts is null) return null;

        try
        {
            var matches = await _contacts.ResolveByNumberAsync(destination, cancellationToken)
                .ConfigureAwait(false);

            if (matches.Count == 0) return null;

            var contact = matches[0];

            // Una voce senza nome non presta il proprio numero come nome: la destinazione
            // comparirebbe due volte. Vedi CallParty.MeaningfulName.
            return new CallParty(
                destination,
                CallParty.MeaningfulNameOf(contact.NameOrDescription, destination),
                contact.PhoneticName,
                IsInternalNumber(destination));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Risoluzione della destinazione {Destination} non riuscita", destination);
            return null;
        }
    }

    private void RaisePhaseChanged(TransferPhase phase) => PhaseChanged?.Invoke(this, phase);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _calls.CallStateChanged -= OnCallStateChanged;
        _telephony.TransferProgress -= OnTransferProgress;
        return ValueTask.CompletedTask;
    }

    private enum ConfirmationKind
    {
        ExternalDestination,
        PresenceWarning,
        DoNotTransfer,
    }

    /// <summary>Conferma chiesta all'operatore e non ancora data.</summary>
    /// <param name="IsBlind">Se la procedura in corso era un trasferimento cieco.</param>
    /// <param name="Kind">Quale controllo l'ha prodotta.</param>
    /// <param name="Destination">
    /// Numero per cui è stata chiesta.
    /// <para>
    /// <b>Vale per quel numero e non per la procedura</b>, ed è la parte che serve
    /// davvero: chi si sente dire che l'interno è occupato o fuori servizio spesso ha
    /// sbagliato una cifra e la corregge. Senza ricordare su cosa era stata chiesta, il
    /// «sì, procedi» dell'operatore verrebbe applicato a una destinazione diversa, che
    /// nessun controllo ha guardato — e la destinazione la sessione la aggiorna a ogni
    /// battitura, quindi confrontarsi con quella non servirebbe a niente.
    /// </para>
    /// </param>
    private sealed record PendingConfirmation(bool IsBlind, ConfirmationKind Kind, string Destination);
}
