using System.Collections.Concurrent;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Tiene il quadro dei colleghi in turno: chi è alla postazione e chi no.
/// </summary>
/// <remarks>
/// <para>
/// <b>Risponde alla domanda che il centralinista si fa venti volte al giorno</b>: «c'è
/// qualcuno all'ufficio tecnico?». Chi vede lo ricava dal pannello dei tasti luminosi;
/// chi non vede, senza questa funzione, lo scopre passando la chiamata e sentendola
/// tornare indietro dopo trenta squilli — con il chiamante che nel frattempo è stato
/// salutato e messo in attesa.
/// </para>
/// <para>
/// <b>Tre strade, e si prende quella che c'è.</b> In ordine di ricchezza:
/// </para>
/// <list type="number">
///   <item><description>
///   <b>L'API del centralino</b>, quando è collegata: sa tutto di tutti e lo manda da
///   sé. Arriva qui attraverso <see cref="PresenceMonitorService"/>.
///   </description></item>
///   <item><description>
///   <b>Le sottoscrizioni SIP</b>, cioè i tasti BLF dei telefoni da tavolo: sanno anche
///   se il collega sta parlando. Passano dallo stesso servizio.
///   </description></item>
///   <item><description>
///   <b>Il sondaggio di raggiungibilità</b>: un <c>OPTIONS</c> per interno, la stessa
///   domanda che il centralino gira a noi ogni due minuti. Sa meno delle altre due — il
///   telefono c'è o non c'è — ma è l'unica che non chiede all'impianto niente oltre il
///   rispetto della norma, e sull'impianto su cui questo prodotto è in esercizio è
///   l'unica che funziona.
///   </description></item>
/// </list>
/// <para>
/// <b>Le tre strade non si escludono.</b> Per ogni interno vale il dato più preciso
/// disponibile: se la presenza lo conosce si usa quello, altrimenti il sondaggio. Un
/// interno di cui nessuna delle due sa niente resta «sconosciuto», che è la verità.
/// </para>
/// <para>
/// <b>Lo stato non sopravvive al riavvio</b>, come per la presenza e per la stessa
/// ragione: un turno conservato è un turno di ieri, e all'apertura del mattino direbbe
/// che ci sono tutti perché così era alla chiusura.
/// </para>
/// </remarks>
public sealed class ShiftBoardService
{
    /// <summary>Quanto attendere la risposta a un singolo sondaggio.</summary>
    /// <remarks>
    /// Quattro secondi: il triplo del tempo di ritrasmissione del protocollo, quindi
    /// abbastanza perché un pacchetto perso non produca un falso «non in turno», e
    /// abbastanza poco perché un giro su cinquanta interni non si trascini.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Intervallo minimo fra due giri, qualunque cosa dicano le impostazioni.</summary>
    /// <remarks>
    /// Quindici secondi. Sotto, si comincia a pesare sul centralino per una domanda la
    /// cui risposta cambia poche volte in un turno: un collega non si siede e non si
    /// alza ogni cinque secondi.
    /// </remarks>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);

    private readonly Func<IReachabilityProbe?> _probe;
    private readonly PresenceMonitorService? _presence;
    private readonly IContactRepository? _contacts;
    private readonly IClock _clock;
    private readonly ILogger<ShiftBoardService> _log;

    private readonly ReachabilityProbePolicy _policy = new();
    private readonly ConcurrentDictionary<string, Sondaggio> _probed = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    private IReadOnlyList<ColleagueEntry> _roster = [];
    private Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private TimeSpan _interval = TimeSpan.FromSeconds(60);

    private DateTimeOffset _lastRoundAt = DateTimeOffset.MinValue;
    private bool _roundRunning;

    /// <summary>Bersaglio di controllo: non può corrispondere a nessun interno.</summary>
    /// <remarks>
    /// La parte utente contiene lettere e un trattino, quindi non è componibile da una
    /// tastiera telefonica e nessun amministratore può averla configurata come interno.
    /// Il suffisso casuale evita che una regola di instradamento scritta apposta finisca
    /// per riconoscerla. Vedi <see cref="ReachabilityProbePolicy"/>.
    /// </remarks>
    private readonly string _controlTarget = $"bp-verifica-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Crea il servizio.</summary>
    /// <param name="probe">
    /// Come ottenere il sondaggio di raggiungibilità. È una funzione e non un oggetto
    /// perché il sondaggio nasce con il trasporto SIP, cioè dopo che il contenitore ha
    /// già costruito questo servizio: catturarlo alla costruzione significherebbe
    /// catturare «niente» per sempre.
    /// </param>
    /// <param name="presence">Stato degli interni dall'API o dalle sottoscrizioni, se disponibile.</param>
    /// <param name="contacts">Rubrica, per risolvere i nominativi non scritti a mano.</param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    public ShiftBoardService(
        Func<IReachabilityProbe?>? probe = null,
        PresenceMonitorService? presence = null,
        IContactRepository? contacts = null,
        IClock? clock = null,
        ILogger<ShiftBoardService>? logger = null)
    {
        _probe = probe ?? (() => null);
        _presence = presence;
        _contacts = contacts;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<ShiftBoardService>.Instance;
    }

    /// <summary>Sollevato quando cambia qualcosa nel quadro.</summary>
    /// <remarks>
    /// Serve alla sezione della finestra principale. Non produce annunci da sé: un
    /// collega che si alza dalla scrivania non è un fatto che debba interrompere una
    /// conversazione in corso. Chi vuole sapere come stanno le cose lo chiede con il
    /// tasto rapido.
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>Se l'operatore ha dichiarato dei colleghi da sorvegliare.</summary>
    /// <remarks>
    /// Senza elenco la funzione non esiste, e la sezione non compare nella finestra: una
    /// tabella permanentemente vuota sul percorso di navigazione costa una tabulazione a
    /// ogni giro per non dire mai nulla.
    /// </remarks>
    public bool IsConfigured
    {
        get { lock (_gate) return _roster.Count > 0; }
    }

    /// <summary>Se agli stati mostrati si possa credere.</summary>
    /// <remarks>
    /// Falso quando il centralino risponde «raggiungibile» anche a un indirizzo
    /// inesistente, oppure quando rifiuta il metodo. In quel caso il quadro dichiara di
    /// non saperlo invece di mostrare tutti presenti.
    /// </remarks>
    public bool CanTrustResults => _policy.CanTrustResults;

    /// <summary>Se il centralino risponde «raggiungibile» a qualunque indirizzo.</summary>
    public bool AnswersForAnyone => _policy.AnswersForAnyone;

    /// <summary>Se il centralino ha dichiarato di non trattare i sondaggi.</summary>
    public bool MethodRefused => _policy.MethodRefused;

    /// <summary>Istante dell'ultimo giro completato.</summary>
    public DateTimeOffset? LastUpdatedAt
    {
        get { lock (_gate) return _lastRoundAt == DateTimeOffset.MinValue ? null : _lastRoundAt; }
    }

    /// <summary>Da quale strada arrivano gli stati, per la diagnostica e per l'operatore.</summary>
    public string SourceName
    {
        get
        {
            if (_presence is { IsAvailable: true }) return _presence.SourceName;
            if (_probe() is { IsAvailable: true }) return "sondaggio di raggiungibilita";

            return "nessuna";
        }
    }

    /// <summary>
    /// Dichiara quali colleghi sorvegliare e ogni quanto chiedere.
    /// </summary>
    /// <param name="roster">Elenco letto dalle impostazioni.</param>
    /// <param name="interval">Intervallo fra due giri.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// I nominativi si risolvono adesso e non a ogni giro: la rubrica sta su disco, e
    /// interrogarla cinquanta volte al minuto per un dato che cambia quando cambia il
    /// personale sarebbe lavoro per nessuno.
    /// </remarks>
    public async Task ConfigureAsync(
        IReadOnlyList<ColleagueEntry> roster,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        var nomi = await ResolveNamesAsync(roster, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _roster = roster;
            _names = nomi;
            _interval = interval < MinimumInterval ? MinimumInterval : interval;

            // Gli stati di chi non e' piu' nell'elenco si buttano: tenerli
            // significherebbe rimetterlo in tabella con un dato vecchio il giorno in cui
            // qualcuno lo riaggiunge.
            var chiavi = roster.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var chiave in _probed.Keys.Where(k => !chiavi.Contains(k)).ToList())
            {
                _probed.TryRemove(chiave, out _);
            }
        }

        _log.LogInformation(
            "Colleghi da sorvegliare: {Count}, un giro ogni {Secondi} secondi, per via di {Sorgente}",
            roster.Count, (int)interval.TotalSeconds, SourceName);

        // Sulle sottoscrizioni SIP ogni interno costa: si dichiara che questi
        // interessano, cosi' il quadro dei turni ne approfitta quando l'impianto le
        // serve. Su un impianto che non le serve la chiamata non fa nulla.
        if (_presence is { IsAvailable: true } && roster.Count > 0)
        {
            await _presence.WatchAsync([.. roster.Select(r => r.Key)], cancellationToken)
                .ConfigureAwait(false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fa partire un giro di sondaggi se è ora.
    /// </summary>
    /// <param name="now">Istante corrente; se omesso si usa l'orologio iniettato.</param>
    /// <remarks>
    /// Va richiamato dallo stesso ciclo che alimenta tutto il resto: una sorgente di
    /// tempo sola, e nessun timer che possa restare acceso per conto suo.
    /// </remarks>
    public void Pump(DateTimeOffset? now = null)
    {
        var adesso = now ?? _clock.Now;

        lock (_gate)
        {
            if (_roundRunning) return;
            if (_roster.Count == 0) return;
            if (adesso - _lastRoundAt < _interval) return;

            _roundRunning = true;
        }

        _ = RunRoundAsync(CancellationToken.None);
    }

    /// <summary>
    /// Chiede subito, senza aspettare il giro successivo.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se un giro è partito.</returns>
    /// <remarks>
    /// È quello che succede quando l'operatore apre la sezione: sta guardando adesso, e
    /// un dato di cinquanta secondi fa è vecchio proprio nel momento in cui conta.
    /// </remarks>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_roundRunning || _roster.Count == 0) return false;
            _roundRunning = true;
        }

        await RunRoundAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Il quadro come si presenta adesso, nell'ordine in cui l'operatore l'ha scritto.
    /// </summary>
    /// <remarks>
    /// L'ordine è quello delle impostazioni e non quello dello stato: un elenco che si
    /// riordina da sé mette il collega cercato ogni volta in un posto diverso, e chi lo
    /// percorre con le frecce deve rileggerlo tutto invece di contare le posizioni.
    /// </remarks>
    public IReadOnlyList<ShiftEntry> Snapshot()
    {
        IReadOnlyList<ColleagueEntry> roster;
        Dictionary<string, string> nomi;

        lock (_gate)
        {
            roster = _roster;
            nomi = _names;
        }

        var righe = new List<ShiftEntry>(roster.Count);

        foreach (var voce in roster) righe.Add(Build(voce, nomi));

        return righe;
    }

    /// <summary>Quanti colleghi risultano alla postazione.</summary>
    /// <param name="entries">Quadro già calcolato, per non ricalcolarlo.</param>
    public static int OnDutyCount(IReadOnlyList<ShiftEntry> entries)
        => entries.Count(e => e.Duty == DutyState.OnDuty);

    // -----------------------------------------------------------------------
    // Il giro di sondaggi
    // -----------------------------------------------------------------------

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ColleagueEntry> roster;
            lock (_gate) roster = _roster;

            // Il sondaggio si chiede una volta per giro: e' lo stesso oggetto per tutte le
            // righe, e chiederlo a ogni riga aprirebbe la porta a un giro fatto meta' con
            // una sonda e meta' con un'altra.
            var sonda = _probe();

            if (sonda is not { IsAvailable: true })
            {
                // Senza sondaggio resta quello che sa la presenza, che puo' bastare: con
                // l'API del centralino collegata il quadro e' gia' completo.
                lock (_gate) _lastRoundAt = _clock.Now;

                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            var cambiato = false;

            foreach (var voce in roster)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (voce.Key.Length == 0) continue;

                // Chi la presenza conosce gia' non si sonda: il suo stato e' piu' preciso
                // di quello che il sondaggio saprebbe dire, e un messaggio risparmiato e'
                // un messaggio che il centralino non deve trattare.
                if (_presence is { IsAvailable: true } && _presence.Get(voce.Key) != PresenceState.Unknown)
                {
                    continue;
                }

                var esito = await sonda
                    .ProbeAsync(voce.Key, ProbeTimeout, cancellationToken)
                    .ConfigureAwait(false);

                _policy.ObserveExtension(esito);

                if (Record(voce.Key, esito)) cambiato = true;
            }

            // IL BERSAGLIO DI CONTROLLO VIENE PER ULTIMO, E VIENE SEMPRE.
            //
            // E' l'unica cosa che distingue un centralino che risponde per gli interni da
            // uno che risponde per se': senza, un impianto che dice «raggiungibile» a
            // chiunque riempirebbe la tabella di presenze inventate, e l'operatore
            // passerebbe le chiamate a scrivanie vuote fidandosi di quello che legge.
            if (!cancellationToken.IsCancellationRequested)
            {
                var attendibilePrima = _policy.CanTrustResults;

                var controllo = await sonda
                    .ProbeAsync(_controlTarget, ProbeTimeout, cancellationToken)
                    .ConfigureAwait(false);

                if (_policy.ObserveControl(controllo) != attendibilePrima) cambiato = true;

                if (!_policy.CanTrustResults && attendibilePrima)
                {
                    _log.LogWarning(
                        "Il centralino ha risposto «raggiungibile» all'indirizzo inesistente {Controllo}: "
                        + "i sondaggi non distinguono un interno dall'altro, e il quadro dei turni lo dichiara "
                        + "invece di mostrare presenze inventate",
                        _controlTarget);
                }
            }

            lock (_gate) _lastRoundAt = _clock.Now;

            if (cambiato) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Un giro che va storto non deve fermare quelli successivi, ne' tanto meno il
            // telefono: il quadro dei turni e' un di piu'.
            _log.LogWarning(ex, "Giro di sondaggi sui colleghi non riuscito");
        }
        finally
        {
            lock (_gate) _roundRunning = false;
        }
    }

    /// <summary>Annota l'esito e dice se ha cambiato qualcosa.</summary>
    private bool Record(string key, ReachabilityOutcome outcome)
    {
        var adesso = _clock.Now;

        if (_probed.TryGetValue(key, out var precedente) && precedente.Outcome == outcome)
        {
            _probed[key] = precedente with { At = adesso };
            return false;
        }

        _probed[key] = new Sondaggio(outcome, adesso, adesso);

        if (precedente is not null)
        {
            _log.LogInformation(
                "Collega {Interno}: {Prima} diventa {Adesso}", key, precedente.Outcome, outcome);
        }

        return true;
    }

    /// <summary>Compone una riga mettendo insieme quello che si sa da tutte le strade.</summary>
    private ShiftEntry Build(ColleagueEntry voce, Dictionary<string, string> nomi)
    {
        var nome = voce.Name ?? (nomi.TryGetValue(voce.Key, out var trovato) ? trovato : string.Empty);

        // 1. La presenza, quando c'e': sa anche se il collega sta parlando.
        if (_presence is { IsAvailable: true })
        {
            var stato = _presence.Get(voce.Key);

            if (stato != PresenceState.Unknown)
            {
                return new ShiftEntry(
                    voce.Number, nome, FromPresence(stato), stato, null, _presence.SourceName);
            }
        }

        // 2. Il sondaggio, che sa se il telefono c'e'.
        if (_policy.CanTrustResults && _probed.TryGetValue(voce.Key, out var sondaggio))
        {
            return new ShiftEntry(
                voce.Number,
                nome,
                FromOutcome(sondaggio.Outcome),
                PresenceState.Unknown,
                sondaggio.Da,
                "sondaggio");
        }

        // 3. Niente: si dice che non si sa, che e' la verita'.
        return new ShiftEntry(voce.Number, nome, DutyState.Unknown, PresenceState.Unknown, null, "nessuna");
    }

    private static DutyState FromPresence(PresenceState state) => state switch
    {
        PresenceState.Offline => DutyState.OffDuty,
        PresenceState.Unknown => DutyState.Unknown,

        // Libero, occupato, che squilla, che non disturba, assente: il telefono e'
        // registrato, quindi qualcuno c'e'. Il dettaglio lo porta la colonna accanto.
        _ => DutyState.OnDuty,
    };

    private static DutyState FromOutcome(ReachabilityOutcome outcome) => outcome switch
    {
        ReachabilityOutcome.Reachable => DutyState.OnDuty,
        ReachabilityOutcome.Unreachable => DutyState.OffDuty,
        ReachabilityOutcome.NotConfigured => DutyState.NotConfigured,
        _ => DutyState.Unknown,
    };

    private async Task<Dictionary<string, string>> ResolveNamesAsync(
        IReadOnlyList<ColleagueEntry> roster,
        CancellationToken cancellationToken)
    {
        var nomi = new Dictionary<string, string>(StringComparer.Ordinal);

        if (_contacts is null) return nomi;

        foreach (var voce in roster)
        {
            // Il nome scritto a mano vince: e' quello che l'operatore ha deciso di
            // vedere, e cercarlo in rubrica sarebbe lavoro per sovrascriverlo.
            if (!string.IsNullOrWhiteSpace(voce.Name)) continue;
            if (voce.Key.Length == 0) continue;

            try
            {
                var contatto = await _contacts
                    .ResolveByNumberAsync(voce.Number, cancellationToken)
                    .ConfigureAwait(false);

                if (contatto is not null) nomi[voce.Key] = contatto.EffectiveDisplayName;
            }
            catch (Exception ex)
            {
                // Una rubrica che non risponde costa il nominativo, non la funzione: il
                // numero da solo identifica comunque il collega.
                _log.LogWarning(ex, "Nominativo dell'interno {Interno} non risolto", voce.Number);
            }
        }

        return nomi;
    }

    /// <summary>Ultimo esito di sondaggio per un interno.</summary>
    /// <param name="Outcome">Cosa ha risposto il centralino.</param>
    /// <param name="At">Quando è arrivata l'ultima risposta.</param>
    /// <param name="Da">Da quando l'esito è quello.</param>
    private sealed record Sondaggio(ReachabilityOutcome Outcome, DateTimeOffset At, DateTimeOffset Da);
}
