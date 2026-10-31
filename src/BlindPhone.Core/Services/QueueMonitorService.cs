using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Sorveglia le code e traduce le loro variazioni in informazione per l'operatore.
/// </summary>
/// <remarks>
/// <para>
/// Risponde al requisito centrale del capitolato: sapere in tempo reale quante
/// chiamate ci sono in coda e chi sono. La difficoltà non è tecnica ma di dosaggio:
/// una coda che cambia in continuazione produrrebbe un flusso di annunci sopra cui
/// l'operatore non riuscirebbe a lavorare. Per questo la modalità predefinita è
/// <see cref="QueueAnnouncementMode.CountOnly"/> e il dettaglio si ottiene a
/// richiesta, con un tasto.
/// </para>
/// <para>
/// Il servizio distingue fra <b>annunci automatici</b> (dosati, sopprimibili,
/// silenziati durante la conversazione) e <b>risposte a comando</b> (sempre
/// pronunciate, anche mentre l'operatore parla, perché le ha chieste lui).
/// </para>
/// </remarks>
public sealed class QueueMonitorService : IAsyncDisposable
{
    private readonly IQueueProvider _provider;
    private readonly IAnnouncer _announcer;
    private readonly IClock _clock;
    private readonly ILogger<QueueMonitorService> _log;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _thresholdAlerted = [];

    private QueueSnapshot _previous;
    private DigitGrouping _grouping;
    private AnnouncementOptions _options;
    private PbxLinkStatus _lastLinkStatus = PbxLinkStatus.Disconnected;
    private DateTimeOffset _linkDegradedSince;

    /// <summary>Crea il servizio di sorveglianza della coda.</summary>
    /// <param name="provider">Connettore verso il sistema di code.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="options">Parametri di annuncio.</param>
    /// <param name="grouping">Come leggere i numeri telefonici.</param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    public QueueMonitorService(
        IQueueProvider provider,
        IAnnouncer announcer,
        AnnouncementOptions? options = null,
        DigitGrouping grouping = DigitGrouping.Single,
        IClock? clock = null,
        ILogger<QueueMonitorService>? logger = null)
    {
        _provider = provider;
        _announcer = announcer;
        _options = options ?? AnnouncementOptions.Default;
        _grouping = grouping;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<QueueMonitorService>.Instance;

        _previous = QueueSnapshot.Empty(_clock.Now);

        _provider.SnapshotUpdated += OnSnapshotUpdated;
        _provider.LinkStatusChanged += OnLinkStatusChanged;
        _provider.AgentStateChanged += OnAgentStateChanged;
    }

    /// <summary>Fotografia corrente della coda.</summary>
    public QueueSnapshot Current => _provider.CurrentSnapshot;

    /// <summary>Aggiorna i parametri di annuncio a caldo.</summary>
    /// <param name="options">Nuovi parametri.</param>
    /// <param name="grouping">Nuova modalità di lettura dei numeri.</param>
    public void UpdateOptions(AnnouncementOptions options, DigitGrouping grouping)
    {
        _options = options;
        _grouping = grouping;
    }

    // -----------------------------------------------------------------------
    // Risposte a comando
    // -----------------------------------------------------------------------

    /// <summary>
    /// Annuncia il numero di chiamate in coda. È la risposta al tasto rapido di
    /// conteggio.
    /// </summary>
    /// <remarks>
    /// Funziona anche durante una conversazione: l'operatore lo preme apposta
    /// mentre parla, per sapere se deve accelerare. Sopprimerlo per rispettare il
    /// silenzio durante la conversazione significherebbe rendere inutile la
    /// funzione proprio nel momento in cui serve.
    /// </remarks>
    public void AnnounceCount()
    {
        var now = _clock.Now;

        if (!IsQueueDataTrustworthy())
        {
            _announcer.Announce(Announcement.CommandResponse(UnavailableReason(), now));
            return;
        }

        var snapshot = _provider.CurrentSnapshot;
        _announcer.Announce(Announcement.CommandResponse(
            AnnouncementTexts.QueueCount(snapshot, now),
            now,
            braille: $"C:{snapshot.WaitingCount}"));
    }

    /// <summary>
    /// Legge l'elenco completo dei chiamanti in attesa, senza spostare il focus.
    /// È la risposta alla doppia pressione del tasto di conteggio.
    /// </summary>
    /// <param name="maxEntries">Numero massimo di voci da leggere.</param>
    /// <remarks>
    /// Il limite esiste perché con venti chiamate in coda la lettura integrale
    /// diventa un monologo che l'operatore non può interrompere senza perdere il
    /// filo. Oltre il limite si annuncia il totale residuo.
    /// </remarks>
    public void AnnounceWaitingList(int maxEntries = 5)
    {
        var now = _clock.Now;

        if (!IsQueueDataTrustworthy())
        {
            _announcer.Announce(Announcement.CommandResponse(UnavailableReason(), now));
            return;
        }

        var snapshot = _provider.CurrentSnapshot;

        if (snapshot.WaitingCount == 0)
        {
            _announcer.Announce(Announcement.CommandResponse("Nessuna chiamata in coda", now));
            return;
        }

        if (!_provider.Capabilities.CanReadWaitingList)
        {
            // Il provider conosce il numero ma non i nomi: si dice quello che si sa,
            // invece di tacere o di inventare.
            _announcer.Announce(Announcement.CommandResponse(
                AnnouncementTexts.QueueCount(snapshot, now), now));
            return;
        }

        var lines = snapshot.Waiting
            .Take(maxEntries)
            .Select(c => AnnouncementTexts.QueuedCallerLine(
                c, now, _grouping, _provider.Capabilities.CanReadPosition));

        var text = string.Join(". ", lines);

        if (snapshot.WaitingCount > maxEntries)
        {
            var remaining = snapshot.WaitingCount - maxEntries;
            text += $". e altre {remaining}";
        }

        _announcer.Announce(Announcement.CommandResponse(text, now));
    }

    /// <summary>Annuncia lo stato corrente dell'agente.</summary>
    public void AnnounceAgentState()
    {
        // Senza API lo stato non si conosce: il connettore spento risponde
        // «disconnesso» solo perche' qualcosa deve rispondere, e detto a voce faceva
        // pensare al telefono scollegato. Vedi AnnouncementTexts.AgentLinkDisabled.
        if (_provider.LinkStatus == PbxLinkStatus.Disabled)
        {
            _announcer.Announce(Announcement.CommandResponse(AnnouncementTexts.AgentLinkDisabled, _clock.Now));
            return;
        }

        var state = _provider.AgentState;
        var text = AnnouncementTexts.AgentStateChanged(state);

        if (state.Since is { } since)
        {
            text += $", da {SpeechFormatter.Duration(_clock.Now - since)}";
        }

        _announcer.Announce(Announcement.CommandResponse(text, _clock.Now));
    }

    // -----------------------------------------------------------------------
    // Annunci automatici
    // -----------------------------------------------------------------------

    private void OnSnapshotUpdated(object? sender, QueueSnapshot snapshot)
    {
        try
        {
            var now = _clock.Now;
            QueueSnapshot previous;

            lock (_gate)
            {
                previous = _previous;
                _previous = snapshot;
            }

            CheckWaitThresholds(snapshot, now);

            if (_options.QueueMode == QueueAnnouncementMode.Off) return;
            if (_options.QueueMode == QueueAnnouncementMode.ThresholdOnly) return;

            if (!snapshot.DiffersInCountFrom(previous))
            {
                // Nessuna variazione del conteggio: non si annuncia. Aggiornare
                // l'operatore ogni volta che un secondo di attesa passa sarebbe
                // rumore, non informazione.
                return;
            }

            // L'earcon distingue l'arrivo dallo svuotamento. Passa sempre, anche
            // quando la voce viene soppressa perche' l'operatore sta parlando.
            var earcon = snapshot.WaitingCount switch
            {
                0 => EarconKind.QueueEmptied,
                var n when n > previous.WaitingCount => EarconKind.QueueCallArrived,
                _ => EarconKind.None,
            };

            var text = _options.QueueMode == QueueAnnouncementMode.All && snapshot.WaitingCount > 0
                ? BuildDetailedChangeText(snapshot, previous, now)
                : AnnouncementTexts.QueueChanged(snapshot.WaitingCount);

            _announcer.Announce(Announcement.Normal(
                text,
                now,
                earcon,
                coalesceKey: "queue:count",
                braille: $"C:{snapshot.WaitingCount}"));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Errore nella gestione dell'aggiornamento di coda");
        }
    }

    private string BuildDetailedChangeText(QueueSnapshot snapshot, QueueSnapshot previous, DateTimeOffset now)
    {
        // In modalita' dettagliata, all'arrivo di una nuova chiamata si dice anche
        // chi e': e' l'informazione che permette di decidere se rispondere subito.
        var arrived = snapshot.Waiting
            .Where(c => previous.Waiting.All(p => p.Id != c.Id))
            .ToList();

        if (arrived.Count == 1 && _provider.Capabilities.CanReadCallerIdentity)
        {
            var who = SpeechFormatter.Party(arrived[0].CallerName, arrived[0].CallerNumber, _grouping);
            return $"In coda {who}, totale {snapshot.WaitingCount}";
        }

        return AnnouncementTexts.QueueChanged(snapshot.WaitingCount);
    }

    /// <summary>
    /// Segnala i chiamanti che hanno superato la soglia di attesa, una volta sola
    /// per chiamante.
    /// </summary>
    private void CheckWaitThresholds(QueueSnapshot snapshot, DateTimeOffset now)
    {
        if (_options.QueueWaitThreshold <= TimeSpan.Zero) return;
        if (!_provider.Capabilities.CanReadWaitTime) return;

        lock (_gate)
        {
            // Ripulisce i contrassegni dei chiamanti che non sono piu' in coda,
            // altrimenti l'insieme cresce per tutta la durata del turno.
            var stillWaiting = snapshot.Waiting.Select(c => c.Id).ToHashSet();
            _thresholdAlerted.IntersectWith(stillWaiting);

            foreach (var caller in snapshot.Waiting)
            {
                if (caller.WaitDuration(now) < _options.QueueWaitThreshold) continue;
                if (!_thresholdAlerted.Add(caller.Id)) continue;

                _announcer.Announce(Announcement.Normal(
                    AnnouncementTexts.QueueWaitThresholdExceeded(_options.QueueWaitThreshold),
                    now,
                    EarconKind.QueueWaitThresholdExceeded,
                    coalesceKey: "queue:threshold"));
            }
        }
    }

    private void OnLinkStatusChanged(object? sender, PbxLinkStatus status)
    {
        var now = _clock.Now;
        var previous = _lastLinkStatus;
        _lastLinkStatus = status;

        switch (status)
        {
            case PbxLinkStatus.Connected when previous is PbxLinkStatus.Degraded or PbxLinkStatus.Failed:
                _announcer.Announce(Announcement.High(
                    AnnouncementTexts.QueueDetailRestored,
                    now,
                    EarconKind.PbxLinkRestored,
                    coalesceKey: "pbx:link"));
                break;

            case PbxLinkStatus.Degraded or PbxLinkStatus.Failed:
                _linkDegradedSince = now;
                break;
        }
    }

    private void OnAgentStateChanged(object? sender, AgentState state)
    {
        // La pausa imposta dal centralino e' l'unico cambio di stato agente che
        // l'operatore non ha chiesto, e quindi l'unico che deve interromperlo:
        // altrimenti smetterebbe di ricevere chiamate senza capire perche',
        // restando seduto in silenzio a turno aperto.
        var announcement = state is { Status: AgentStatus.Paused, WasAutomatic: true }
            ? Announcement.Critical(
                AnnouncementTexts.AgentStateChanged(state),
                _clock.Now,
                EarconKind.CommandRejected,
                coalesceKey: "agent:state",
                braille: "PAUSA AUTO")
            : Announcement.High(
                AnnouncementTexts.AgentStateChanged(state),
                _clock.Now,
                coalesceKey: "agent:state");

        _announcer.Announce(announcement);
    }

    /// <summary>
    /// Fa avanzare i comportamenti temporizzati: al momento l'avviso di degrado del
    /// collegamento con il centralino.
    /// </summary>
    /// <param name="now">Istante corrente; se omesso si usa l'orologio iniettato.</param>
    public void Pump(DateTimeOffset? now = null)
    {
        var timestamp = now ?? _clock.Now;

        if (_lastLinkStatus is not (PbxLinkStatus.Degraded or PbxLinkStatus.Failed)) return;
        if (_linkDegradedSince == default) return;
        if (timestamp - _linkDegradedSince < TimeSpan.FromSeconds(15)) return;

        // Dopo quindici secondi di dati non aggiornati si avvisa. La regola e' di
        // onesta' dell'interfaccia: un contatore congelato e' indistinguibile da una
        // coda ferma, e l'operatore continuerebbe a fidarsi di un numero vecchio.
        _announcer.Announce(Announcement.High(
            AnnouncementTexts.QueueDetailUnavailable,
            timestamp,
            EarconKind.PbxLinkDegraded,
            coalesceKey: "pbx:link",
            braille: "PBX KO"));

        _linkDegradedSince = timestamp;
    }

    private bool IsQueueDataTrustworthy()
        => _provider.LinkStatus is PbxLinkStatus.Connected
           && _provider.Capabilities.CanReadWaitingList;

    /// <summary>
    /// Spiega perché il dato di coda non è utilizzabile, distinguendo la scelta dal
    /// guasto.
    /// </summary>
    /// <remarks>
    /// Chi ha disattivato il collegamento non deve sentirsi dire che è "interrotto":
    /// è la frase che manda a cercare un problema inesistente, e su un comando che
    /// l'operatore preme decine di volte al giorno diventa un ostacolo.
    /// </remarks>
    private string UnavailableReason()
        => _provider.LinkStatus == PbxLinkStatus.Disabled
            ? AnnouncementTexts.QueueLinkDisabled
            : AnnouncementTexts.QueueDetailUnavailable;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _provider.SnapshotUpdated -= OnSnapshotUpdated;
        _provider.LinkStatusChanged -= OnLinkStatusChanged;
        _provider.AgentStateChanged -= OnAgentStateChanged;
        return ValueTask.CompletedTask;
    }
}
