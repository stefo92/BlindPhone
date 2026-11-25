using BlindPhone.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Attenuatore dell'audio della conversazione, usato per far passare gli annunci
/// critici senza che si sovrappongano alla voce dell'interlocutore.
/// </summary>
public interface IAudioDucker
{
    /// <summary>Attenua temporaneamente l'audio della conversazione.</summary>
    /// <param name="attenuationDb">Attenuazione in decibel, valore positivo.</param>
    /// <param name="duration">Durata dell'attenuazione.</param>
    void Duck(float attenuationDb, TimeSpan duration);
}

/// <summary>
/// Pipeline di annuncio: raccoglie i messaggi prodotti dai servizi, applica le
/// regole di priorità, accorpamento, anti-ripetizione e soppressione, e li
/// consegna alla voce.
/// </summary>
/// <remarks>
/// <para>
/// È il componente che decide <b>cosa l'operatore sente</b>, ed è quindi il punto
/// in cui il prodotto è accessibile o non lo è. Le regole implementate qui non sono
/// ottimizzazioni: sono il risultato del vincolo per cui l'unico canale di uscita
/// disponibile (l'udito) è anche quello che serve all'operatore per fare il suo
/// lavoro, cioè parlare al telefono.
/// </para>
/// <para>
/// <b>Modello di esecuzione.</b> Il servizio non possiede alcun timer.
/// <see cref="Announce"/> accoda o emette, e <see cref="Pump"/> va richiamato
/// periodicamente dall'infrastruttura (tipicamente ogni 100 ms) per far scadere le
/// finestre di accorpamento. Questa scelta rende il comportamento temporale
/// completamente deterministico nei test: con un <see cref="ManualClock"/> si
/// riproduce una raffica di eventi e si verifica esattamente cosa viene pronunciato
/// e cosa no, senza attese reali.
/// </para>
/// </remarks>
public sealed class AnnouncementService : IAnnouncer
{
    private readonly ISpeechOutput _speech;
    private readonly IEarconPlayer _earcons;
    private readonly AnnouncementContext _context;
    private readonly IClock _clock;
    private readonly ILogger<AnnouncementService> _log;
    private readonly IAudioDucker? _ducker;

    private readonly Lock _gate = new();

    /// <summary>Annunci in attesa che scada la finestra di accorpamento, per chiave.</summary>
    private readonly Dictionary<string, PendingAnnouncement> _pending = [];

    /// <summary>Annunci senza chiave, da emettere al prossimo pump nell'ordine di arrivo.</summary>
    private readonly List<Announcement> _immediateQueue = [];

    /// <summary>
    /// Annunci che aspettano il loro turno, nell'ordine di arrivo. Vedi
    /// <see cref="Announcement.Sequential"/>.
    /// </summary>
    private readonly List<PendingAnnouncement> _sequence = [];

    /// <summary>
    /// Fino a quando si presume che parli l'ultimo annuncio in coda, o l'annuncio
    /// critico che l'ha interrotto: prima di allora il turno successivo aspetta.
    /// </summary>
    private DateTimeOffset _sequenceBusyUntil = DateTimeOffset.MinValue;

    private readonly LinkedList<Announcement> _history = [];

    private string? _lastSpokenText;
    private DateTimeOffset _lastSpokenAt;

    /// <summary>Crea la pipeline di annuncio.</summary>
    /// <param name="speech">Destinazione della voce.</param>
    /// <param name="earcons">Riproduttore dei segnali acustici.</param>
    /// <param name="context">Contesto operativo che governa le soppressioni.</param>
    /// <param name="options">Parametri di comportamento.</param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="ducker">Attenuatore dell'audio di conversazione, facoltativo.</param>
    /// <param name="logger">Log diagnostico.</param>
    public AnnouncementService(
        ISpeechOutput speech,
        IEarconPlayer earcons,
        AnnouncementContext context,
        AnnouncementOptions? options = null,
        IClock? clock = null,
        IAudioDucker? ducker = null,
        ILogger<AnnouncementService>? logger = null)
    {
        _speech = speech;
        _earcons = earcons;
        _context = context;
        Options = options ?? AnnouncementOptions.Default;
        _clock = clock ?? SystemClock.Instance;
        _ducker = ducker;
        _log = logger ?? NullLogger<AnnouncementService>.Instance;
    }

    /// <summary>Parametri di comportamento correnti.</summary>
    public AnnouncementOptions Options { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<Announcement> History
    {
        get
        {
            lock (_gate) return [.. _history];
        }
    }

    /// <summary>
    /// Sollevato ogni volta che un annuncio viene effettivamente pronunciato.
    /// </summary>
    /// <remarks>
    /// Serve alla UI per aggiornare la regione dinamica UI Automation con lo stesso
    /// testo che la voce sta dicendo: così un operatore che usa il cursore virtuale
    /// dello screen reader ritrova a schermo ciò che ha appena sentito.
    /// </remarks>
    public event EventHandler<Announcement>? Spoken;

    /// <summary>
    /// Sollevato quando un annuncio viene scartato, con il motivo.
    /// </summary>
    /// <remarks>
    /// Non è telemetria: durante la messa a punto con operatori reali la domanda
    /// ricorrente è "perché non me l'ha detto?", e senza questo evento la risposta
    /// richiede di rileggere il codice.
    /// </remarks>
    public event EventHandler<AnnouncementDroppedEventArgs>? Dropped;

    /// <summary>
    /// Se la coda degli annunci è vuota e l'ultimo si presume finito.
    /// </summary>
    /// <remarks>
    /// Serve a chi sta per aprire una finestra senza che l'operatore l'abbia chiesta:
    /// la finestra che passa in primo piano fa tacere lo screen reader, e la frase in
    /// corso si perderebbe a metà. Vedi <see cref="Announcement.Sequential"/>.
    /// </remarks>
    public bool IsSequenceIdle
    {
        get
        {
            lock (_gate) return _sequence.Count == 0 && _clock.Now >= _sequenceBusyUntil;
        }
    }

    /// <inheritdoc />
    public void Announce(Announcement announcement)
    {
        var now = _clock.Now;

        // L'earcon precede sempre la decisione sulla voce ed e' immune a ogni
        // soppressione. E' il canale che permette di tacere senza smettere di
        // informare: se la voce viene soppressa perche' l'operatore sta parlando,
        // il suono passa comunque.
        //
        // Quello di un annuncio in coda parte con la sua frase, quando arriva il suo
        // turno: suonato adesso cadrebbe sopra la frase di un altro, e il suono della
        // registrazione persa sotto la notizia della cuffia non spiega niente. Resta
        // immune alla soppressione, solo che la decisione si prende piu' tardi.
        var earconAlTurno = announcement.Sequential && !announcement.EarconOnly;

        if (announcement.Earcon != EarconKind.None && !earconAlTurno) _earcons.Play(announcement.Earcon);

        // Il suono e' gia' partito, ed e' tutto quello che l'operatore deve sentire.
        // Braille e registro si aggiornano lo stesso: chi legge la riga o rilegge gli
        // ultimi annunci deve trovarci anche questo. Non si solleva Spoken, perche'
        // la finestra principale lo riscrive in una regione dinamica e lo screen
        // reader lo leggerebbe ad alta voce, cioe' proprio quello che si vuole evitare.
        if (announcement.EarconOnly)
        {
            _log.LogDebug("Annuncio affidato al solo segnale {Earcon}: {Text}", announcement.Earcon, announcement.Text);

            if (announcement.BrailleText is { Length: > 0 } braille) _speech.Braille(braille);

            lock (_gate)
            {
                _history.AddFirst(announcement);
                while (_history.Count > Options.HistorySize) _history.RemoveLast();
            }

            return;
        }

        var suppression = EvaluateSuppression(announcement);
        if (suppression is not null)
        {
            // Non avra' un turno: il suono che aspettava il turno parte adesso.
            if (earconAlTurno && announcement.Earcon != EarconKind.None) _earcons.Play(announcement.Earcon);

            _log.LogDebug("Annuncio scartato ({Reason}): {Text}", suppression, announcement.Text);
            Dropped?.Invoke(this, new AnnouncementDroppedEventArgs(announcement, suppression.Value));
            return;
        }

        if (announcement.Sequential)
        {
            // Anche un critico, se e' in coda, aspetta il turno: non deve tagliare la
            // frase di prima, che e' la ragione per cui e' in coda.
            EnqueueInSequence(announcement, now);
            return;
        }

        if (announcement.Priority == AnnouncementPriority.Critical)
        {
            // I critici non passano dalla coda: interrompono e parlano subito.
            // Aspettare la finestra di accorpamento per dire "chiamata in arrivo"
            // significherebbe dirlo dopo che il telefono ha gia' squillato.
            EmitNow(announcement, now, interrupt: true);
            return;
        }

        lock (_gate)
        {
            if (announcement.CoalesceKey is { } key)
            {
                if (_pending.TryGetValue(key, out var existing))
                {
                    // Sostituisce il testo ma conserva la scadenza originale: sotto
                    // una raffica continua di eventi, rinviare ogni volta la
                    // scadenza produrrebbe un annuncio che non arriva mai.
                    _pending[key] = existing with { Announcement = announcement };

                    // Anche questo e' un annuncio che non si sentira': va scritto
                    // come gli scartati, altrimenti sparisce senza lasciare traccia.
                    _log.LogDebug(
                        "Annuncio accorpato in uno piu' recente ({Key}): {Text}",
                        key, existing.Announcement.Text);
                }
                else
                {
                    _pending[key] = new PendingAnnouncement(announcement, now + Options.CoalesceWindow);
                }
            }
            else
            {
                _immediateQueue.Add(announcement);
            }
        }
    }

    /// <summary>
    /// Fa avanzare la pipeline: emette gli annunci la cui finestra di accorpamento
    /// è scaduta e quelli senza chiave, e dà il turno al prossimo annuncio in coda se
    /// quello di prima ha finito.
    /// </summary>
    /// <param name="now">
    /// Istante corrente. Se omesso si usa l'orologio iniettato; il parametro
    /// esplicito serve ai test.
    /// </param>
    /// <remarks>
    /// Va richiamato periodicamente, tipicamente ogni 100 ms. Un intervallo più
    /// lungo aggiunge latenza percepita agli annunci; uno più corto non serve,
    /// perché la finestra di accorpamento è comunque nell'ordine delle centinaia di
    /// millisecondi.
    /// </remarks>
    public void Pump(DateTimeOffset? now = null)
    {
        var timestamp = now ?? _clock.Now;
        List<Announcement> ready = [];

        lock (_gate)
        {
            if (_immediateQueue.Count > 0)
            {
                ready.AddRange(_immediateQueue);
                _immediateQueue.Clear();
            }

            if (_pending.Count > 0)
            {
                var expired = _pending
                    .Where(kv => kv.Value.DueAt <= timestamp)
                    .ToList();

                foreach (var kv in expired)
                {
                    ready.Add(kv.Value.Announcement);
                    _pending.Remove(kv.Key);
                }
            }
        }

        // Priorita' decrescente, poi ordine di creazione: dentro allo stesso pump,
        // l'esito di un trasferimento va detto prima di una variazione di coda.
        ready.Sort(static (a, b) =>
        {
            var byPriority = b.Priority.CompareTo(a.Priority);
            return byPriority != 0 ? byPriority : a.CreatedAt.CompareTo(b.CreatedAt);
        });

        foreach (var announcement in ready)
        {
            // Le condizioni possono essere cambiate fra l'accodamento e adesso: una
            // chiamata puo' essere stata risposta nel frattempo, e un annuncio di
            // coda che era ammesso ora va soppresso.
            var suppression = EvaluateSuppression(announcement);
            if (suppression is not null)
            {
                // SCRITTO COME QUELLI SCARTATI IN INGRESSO, e prima non lo era.
                //
                // Uno scarto qui, alla scadenza della finestra di accorpamento, non
                // lasciava nessuna riga: davanti a «non me l'ha detto» il registro
                // non sapeva distinguere un annuncio scartato da uno mai prodotto.
                _log.LogDebug("Annuncio scartato ({Reason}): {Text}", suppression, announcement.Text);
                Dropped?.Invoke(this, new AnnouncementDroppedEventArgs(announcement, suppression.Value));
                continue;
            }

            EmitNow(announcement, timestamp, interrupt: false);
        }

        PumpSequence(timestamp);
    }

    /// <summary>
    /// Dà il turno al prossimo annuncio in coda, se quello di prima ha finito.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Uno per volta, e solo quando il precedente si presume finito.</b> La fine
    /// non la dice nessuno — la notifica UI Automation non ha riscontro — e quindi la
    /// si stima dalla lunghezza del testo: vedi
    /// <see cref="AnnouncementOptions.SpeechCharactersPerSecond"/>. Un annuncio critico
    /// fuori coda, come la chiamata in arrivo, interrompe anche la coda, e il turno
    /// successivo aspetta che abbia finito lui.
    /// </para>
    /// <para>
    /// Fra quelli pronti passa prima il più importante, poi il più vecchio. Un
    /// annuncio in coda con la stessa chiave di uno che aspetta ancora lo sostituisce
    /// al suo posto: «registrazione in corso» seguito da «telefono registrato» prima
    /// che arrivi il turno si sente come «telefono registrato», l'unico ancora vero.
    /// </para>
    /// </remarks>
    private void PumpSequence(DateTimeOffset now)
    {
        while (true)
        {
            Announcement next;

            lock (_gate)
            {
                if (_sequence.Count == 0 || now < _sequenceBusyUntil) return;

                var index = NextDueInSequence(now);
                if (index < 0) return;

                next = _sequence[index].Announcement;
                _sequence.RemoveAt(index);
            }

            if (next.Earcon != EarconKind.None) _earcons.Play(next.Earcon);

            var suppression = EvaluateSuppression(next);
            if (suppression is not null)
            {
                // Il turno passa al successivo: uno scartato non occupa la voce.
                _log.LogDebug("Annuncio in coda scartato ({Reason}): {Text}", suppression, next.Text);
                Dropped?.Invoke(this, new AnnouncementDroppedEventArgs(next, suppression.Value));
                continue;
            }

            EmitNow(next, now, interrupt: false);
            return;
        }
    }

    /// <summary>Posizione del prossimo annuncio in coda da dire, o -1 se nessuno è pronto.</summary>
    /// <remarks>Da chiamare dentro <c>_gate</c>.</remarks>
    private int NextDueInSequence(DateTimeOffset now)
    {
        var best = -1;

        for (var i = 0; i < _sequence.Count; i++)
        {
            if (_sequence[i].DueAt > now) continue;

            if (best < 0 || _sequence[i].Announcement.Priority > _sequence[best].Announcement.Priority) best = i;
        }

        return best;
    }

    private void EnqueueInSequence(Announcement announcement, DateTimeOffset now)
    {
        lock (_gate)
        {
            var index = announcement.CoalesceKey is { } key
                ? _sequence.FindIndex(p => p.Announcement.CoalesceKey == key)
                : -1;

            if (index >= 0)
            {
                // Al posto di quello vecchio e con la sua scadenza, come l'accorpamento
                // fuori coda: chi era in fila da prima non perde il posto.
                var existing = _sequence[index];
                _sequence[index] = existing with { Announcement = announcement };

                _log.LogDebug(
                    "Annuncio in coda accorpato in uno piu' recente ({Key}): {Text}",
                    announcement.CoalesceKey, existing.Announcement.Text);

                return;
            }

            // Con la chiave aspetta la finestra di accorpamento, come fuori coda:
            // la registrazione che passa da «in corso» a «registrato» in mezzo secondo
            // si dice una volta sola.
            var dueAt = announcement.CoalesceKey is null ? now : now + Options.CoalesceWindow;
            _sequence.Add(new PendingAnnouncement(announcement, dueAt));
        }
    }

    /// <summary>Quanto si presume che duri la lettura di un testo, pausa compresa.</summary>
    private TimeSpan EstimateSpeechDuration(string text)
    {
        var caratteriAlSecondo = Options.SpeechCharactersPerSecond > 0 ? Options.SpeechCharactersPerSecond : 15;

        // In millisecondi interi: un turno che scade un decimo di tick dopo il previsto
        // non si sente, ma rende le prove dipendenti dagli arrotondamenti.
        var durata = TimeSpan.FromMilliseconds(Math.Round(text.Length * 1000 / caratteriAlSecondo))
                     + Options.SequencePause;

        return durata < Options.SequenceMaxWait ? durata : Options.SequenceMaxWait;
    }

    /// <inheritdoc />
    public void PlayEarcon(EarconKind earcon) => _earcons.Play(earcon);

    /// <inheritdoc />
    public void Silence()
    {
        lock (_gate)
        {
            _pending.Clear();
            _immediateQueue.Clear();

            // Chi zittisce la postazione zittisce anche quello che aspettava il turno.
            _sequence.Clear();
            _sequenceBusyUntil = DateTimeOffset.MinValue;
        }

        _speech.Stop();
    }

    /// <inheritdoc />
    public void RepeatLast()
    {
        Announcement? last;
        lock (_gate) last = _history.First?.Value;

        if (last is null)
        {
            _speech.Speak("Nessun annuncio da ripetere", interrupt: true);
            return;
        }

        // La ripetizione salta ogni filtro: e' una richiesta esplicita
        // dell'operatore, e un comando che non risponde e' indistinguibile da un
        // comando che non ha funzionato.
        _speech.Speak(last.Text, interrupt: true);
        if (last.BrailleText is { Length: > 0 } braille) _speech.Braille(braille);
    }

    private void EmitNow(Announcement announcement, DateTimeOffset now, bool interrupt)
    {
        // Sul critico e non sull'interruzione: un critico in coda non interrompe, ma
        // la registrazione persa durante una chiamata va sentita sopra l'interlocutore
        // come prima. Fuori coda le due cose coincidono.
        if (announcement.Priority == AnnouncementPriority.Critical
            && Options.DuckConversationOnCritical
            && _context.IsConversationActive)
        {
            _ducker?.Duck(Options.DuckAttenuationDb, TimeSpan.FromSeconds(3));
        }

        // CHE COSA E' STATO DETTO, PAROLA PER PAROLA.
        //
        // Il registro sapeva dire perche' un annuncio era stato scartato, non che cosa
        // dicesse quello pronunciato. Il 20 novembre 2026 l'annuncio di fine
        // trasferimento era «… trasferito a - -», che lo screen reader legge come
        // «trasferito a», e dal registro non si poteva vedere: lo si e' dedotto da un
        // altro annuncio, scartato, che per caso conteneva lo stesso nome.
        _log.LogDebug(
            "Annuncio pronunciato ({Priority}{Interrupt}): {Text}",
            announcement.Priority,
            interrupt ? ", interrompe" : string.Empty,
            announcement.Text);

        _speech.Speak(announcement.Text, interrupt);
        if (announcement.BrailleText is { Length: > 0 } braille) _speech.Braille(braille);

        lock (_gate)
        {
            _lastSpokenText = announcement.Text;
            _lastSpokenAt = now;

            // Un annuncio in coda occupa la voce per il suo turno. Un critico che
            // interrompe l'ha appena tolta a chiunque stesse parlando, coda compresa:
            // il turno successivo aspetta che finisca lui.
            if (announcement.Sequential || interrupt)
            {
                _sequenceBusyUntil = now + EstimateSpeechDuration(announcement.Text);
            }

            _history.AddFirst(announcement);
            while (_history.Count > Options.HistorySize) _history.RemoveLast();
        }

        Spoken?.Invoke(this, announcement);
    }

    /// <summary>
    /// Decide se un annuncio va scartato, e per quale motivo.
    /// </summary>
    /// <returns><c>null</c> se l'annuncio va pronunciato.</returns>
    private AnnouncementDropReason? EvaluateSuppression(Announcement announcement)
    {
        // 1. Anti-ripetizione. Si applica solo agli annunci con chiave: quelli senza
        //    sono risposte a comandi, e vanno ripetute anche identiche perche'
        //    l'operatore che preme due volte lo stesso tasto deve sentire due
        //    conferme, altrimenti crede che la seconda pressione non sia passata.
        if (announcement.CoalesceKey is not null && _lastSpokenText == announcement.Text)
        {
            lock (_gate)
            {
                if (_clock.Now - _lastSpokenAt < Options.RepeatSuppressionWindow)
                    return AnnouncementDropReason.DuplicateWithinWindow;
            }
        }

        var isExemptFromSilence =
            announcement.BypassConversationSilence ||
            announcement.Priority >= Options.ConversationSilenceThreshold;

        // 2. Silenzio durante la conversazione (principio P4).
        if (Options.SilenceDuringConversation && _context.IsConversationActive && !isExemptFromSilence)
            return AnnouncementDropReason.ConversationActive;

        // 3. Silenzio durante la digitazione: l'eco delle cifre ha la precedenza.
        //    Solo i critici passano, perche' una chiamata entrante va annunciata
        //    anche mentre si compone un numero.
        //
        //    "STA DIGITANDO" E' UNO STATO CHE SCADE, e prima non lo era: valeva finche'
        //    il campo di composizione conteneva del testo, quindi il numero rimasto li'
        //    dal trasferimento precedente zittiva la postazione a tempo indeterminato.
        //    Vedi AnnouncementContext.NoteTyping per il conto degli annunci persi.
        if (Options.SilenceWhileTyping
            && _context.IsTyping(_clock.Now, Options.TypingSilenceWindow)
            && announcement.Priority < AnnouncementPriority.Critical
            && !announcement.BypassConversationSilence)
        {
            return AnnouncementDropReason.OperatorTyping;
        }

        // 4. Gli annunci informativi parlano solo se non c'e' nient'altro da dire.
        if (announcement.Priority == AnnouncementPriority.Low)
        {
            lock (_gate)
            {
                if (_pending.Count > 0 || _immediateQueue.Count > 0 || _sequence.Count > 0)
                    return AnnouncementDropReason.HigherPriorityPending;
            }

            if (_context.IsConversationActive) return AnnouncementDropReason.ConversationActive;
        }

        return null;
    }

    private readonly record struct PendingAnnouncement(Announcement Announcement, DateTimeOffset DueAt);
}

/// <summary>Motivo per cui un annuncio non è stato pronunciato.</summary>
public enum AnnouncementDropReason
{
    /// <summary>Identico a un annuncio pronunciato da poco.</summary>
    DuplicateWithinWindow,

    /// <summary>Soppresso perché è in corso una conversazione.</summary>
    ConversationActive,

    /// <summary>Soppresso perché l'operatore sta digitando.</summary>
    OperatorTyping,

    /// <summary>Annuncio informativo scartato per presenza di messaggi più importanti.</summary>
    HigherPriorityPending,
}

/// <summary>Dati dell'evento di annuncio scartato.</summary>
/// <param name="Announcement">Annuncio scartato.</param>
/// <param name="Reason">Motivo dello scarto.</param>
public sealed record AnnouncementDroppedEventArgs(Announcement Announcement, AnnouncementDropReason Reason);
