using System.Text;
using System.Text.Json;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>Tempi del ciclo delle statistiche d'uso.</summary>
/// <remarks>
/// Si possono cambiare solo da codice: servono alle prove, che non possono aspettare
/// tre minuti per vedere partire un lotto. Sulla postazione valgono i predefiniti.
/// </remarks>
public sealed record TelemetryTiming
{
    /// <summary>Quanto aspettare dopo l'avvio prima di comporre e mandare qualcosa.</summary>
    /// <remarks>
    /// Tre minuti: nel primo minuto il posto operatore si registra al centralino, carica
    /// la rubrica e apre il flusso degli eventi, ed è il minuto in cui l'operatore
    /// risponde alla prima chiamata. Le statistiche arrivano dopo, quando non pesano su
    /// niente.
    /// </remarks>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Ogni quanto il ciclo guarda se c'è qualcosa da fare.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Ogni quanto mandare un lotto, quando ci sono eventi in attesa.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Attesa massima fra due tentativi quando il server non risponde.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromHours(6);
}

/// <summary>
/// Le statistiche d'uso anonime: raccoglie, conserva e manda a PostHog, sullo sfondo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non deve mai farsi sentire.</b> Le registrazioni che arrivano dall'interfaccia —
/// un comando, un errore, un aggiornamento trovato — costano un blocco e un insieme in
/// memoria. Tutto il resto — il file, la composizione dell'avvio, la rete — avviene su
/// un'attività di sfondo, comincia tre minuti dopo l'avvio e si rimanda quando c'è una
/// chiamata in corso. Alla chiusura non si aspetta la rete: gli eventi non partiti si
/// salvano nel file e partono al prossimo avvio.
/// </para>
/// <para>
/// <b>Muto finché non è configurato.</b> Senza indirizzo, senza chiave o senza consenso
/// il ciclo gira a vuoto: non crea l'identificativo, non scrive il file, non apre
/// connessioni. Vedi <see cref="TelemetrySettings.IsActive"/>.
/// </para>
/// <para>
/// <b>Spegnere il consenso dimentica tutto</b>, nel momento in cui la configurazione
/// viene salvata: si cancella il file con l'identificativo e gli eventi in attesa, e si
/// interrompe un invio in corso.
/// </para>
/// </remarks>
public sealed class TelemetryService : IAsyncDisposable
{
    /// <summary>Nome del file in cui la postazione ricorda le proprie statistiche.</summary>
    public const string StateFileName = "telemetria.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SettingsService _settings;
    private readonly string _version;
    private readonly Func<TelemetryFacts> _facts;
    private readonly Func<bool> _isCallInProgress;
    private readonly IClock _clock;
    private readonly HttpMessageHandler? _handler;
    private readonly TelemetryTiming _timing;
    private readonly ILogger<TelemetryService> _log;
    private readonly ILogger<PostHogClient> _clientLog;
    private readonly DateTimeOffset _createdAt;

    private readonly object _lifecycle = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    private volatile TelemetryDiary? _diary;
    private CancellationTokenSource? _activation;
    private PostHogClient? _client;
    private Task? _loop;
    private bool _begun;
    private string? _lastSaved;
    private DateTimeOffset _nextFlushAt = DateTimeOffset.MinValue;
    private int _failures;
    private bool _pauseLogged;
    private string? _updateBeforeActivation;

    /// <summary>Crea il servizio. Non avvia niente: vedi <see cref="Start"/>.</summary>
    /// <param name="settings">Configurazione, letta a ogni giro: un cambio vale subito.</param>
    /// <param name="version">Versione di BlindPhone, nella forma numerica.</param>
    /// <param name="facts">Chi compone lo stato della postazione per l'avvio e per l'attività.</param>
    /// <param name="isCallInProgress">Se c'è una chiamata in corso; omesso, mai.</param>
    /// <param name="clock">Orologio; omesso, quello di sistema.</param>
    /// <param name="handler">Gestore delle connessioni, per le prove; omesso, quello di <see cref="PostHogClient"/>.</param>
    /// <param name="timing">Tempi del ciclo; omessi, i predefiniti.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="clientLogger">Log diagnostico del client.</param>
    public TelemetryService(
        SettingsService settings,
        string version,
        Func<TelemetryFacts> facts,
        Func<bool>? isCallInProgress = null,
        IClock? clock = null,
        HttpMessageHandler? handler = null,
        TelemetryTiming? timing = null,
        ILogger<TelemetryService>? logger = null,
        ILogger<PostHogClient>? clientLogger = null)
    {
        _settings = settings;
        _version = version;
        _facts = facts;
        _isCallInProgress = isCallInProgress ?? (() => false);
        _clock = clock ?? SystemClock.Instance;
        _handler = handler;
        _timing = timing ?? new TelemetryTiming();
        _log = logger ?? NullLogger<TelemetryService>.Instance;
        _clientLog = clientLogger ?? NullLogger<PostHogClient>.Instance;
        _createdAt = _clock.Now;

        StatePath = Path.Combine(settings.DataDirectory, StateFileName);

        _settings.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Percorso del file con l'identificativo e gli eventi in attesa.</summary>
    public string StatePath { get; }

    /// <summary>Se le statistiche stanno raccogliendo: c'è un identificativo in uso.</summary>
    public bool IsCollecting => _diary is not null;

    /// <summary>Quanti eventi aspettano di partire.</summary>
    public int PendingCount => _diary?.PendingCount ?? 0;

    /// <summary>Avvia il ciclo sullo sfondo. Torna subito.</summary>
    public void Start()
    {
        lock (_lifecycle)
        {
            if (_loop is not null) return;

            // Task.Run e non una chiamata diretta: il primo giro legge un file, e
            // nemmeno quello deve passare dal thread che sta disegnando la finestra.
            _loop = Task.Run(() => LoopAsync(_stop.Token));
        }
    }

    /// <summary>Annota un comando dato dall'operatore.</summary>
    /// <param name="command">Comando.</param>
    /// <param name="origin">Da dove è partito.</param>
    /// <remarks>Lo chiama il thread dell'interfaccia: non scrive e non aspetta niente.</remarks>
    public void RecordCommand(HotkeyCommand command, CommandOrigin origin)
    {
        try
        {
            _diary?.RecordCommand(Today(), command, origin);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Statistiche d'uso: comando non annotato");
        }
    }

    /// <summary>Annota un errore non gestito.</summary>
    /// <param name="exception">Eccezione; di lei esce solo il nome del tipo.</param>
    /// <param name="origin">Da dove è arrivata.</param>
    /// <param name="fatal">Se il processo sta terminando.</param>
    /// <remarks>
    /// Con un errore fatale il file si scrive subito, qui: il processo sta morendo, e
    /// il ciclo che lo scriverebbe fra un minuto non ci sarà più.
    /// </remarks>
    public void RecordError(Exception exception, ErrorOrigin origin, bool fatal)
    {
        try
        {
            var diario = _diary;
            if (diario is null) return;

            if (diario.RecordError(Today(), TelemetryCatalog.TypeNameOf(exception), origin, fatal) && fatal)
            {
                Persist(diario);
            }
        }
        catch (Exception ex)
        {
            // Dentro il gestore degli errori: qui un'eccezione non deve uscire mai.
            _log.LogDebug(ex, "Statistiche d'uso: errore non annotato");
        }
    }

    /// <summary>Annota una versione nuova trovata sul server degli aggiornamenti.</summary>
    /// <param name="version">Versione trovata.</param>
    public void RecordUpdateFound(Version version)
    {
        try
        {
            var testo = version.ToString(version.Build >= 0 ? 3 : 2);

            // Il controllo degli aggiornamenti parte con la finestra, e puo' arrivare
            // prima che il primo giro del ciclo abbia ripreso il diario: la versione si
            // tiene in memoria e la annota l'attivazione.
            if (_diary is { } diario) diario.RecordUpdateFound(Today(), testo);
            else _updateBeforeActivation = testo;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Statistiche d'uso: aggiornamento non annotato");
        }
    }

    /// <summary>
    /// Un giro del ciclo: accende o spegne, chiude il giorno, manda un lotto se è ora.
    /// </summary>
    /// <param name="cancellationToken">Annullamento.</param>
    /// <remarks>
    /// Il ciclo lo chiama ogni minuto; le prove lo chiamano direttamente, per vedere un
    /// giro alla volta.
    /// </remarks>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var impostazioni = _settings.Current.Telemetry;

            if (!impostazioni.Enabled)
            {
                Forget();
                return;
            }

            if (!impostazioni.IsConfigured)
            {
                // Consenso acceso ma niente server, o un server che non e' https: si
                // resta fermi. Un identificativo gia' in uso si conserva, perche' e'
                // il caso del server spostato o in manutenzione, non di una scelta.
                if (!_pauseLogged)
                {
                    _log.LogInformation(
                        "Statistiche d'uso accese ma senza un server https e una chiave di progetto valida: "
                        + "non parte niente");
                    _pauseLogged = true;
                }

                return;
            }

            _pauseLogged = false;

            var diario = _diary ?? Activate();
            var adesso = _clock.Now;

            if (adesso - _createdAt < _timing.StartDelay) return;

            if (!_begun)
            {
                diario.Begin(Today(adesso), _facts());
                _begun = true;

                _log.LogInformation(
                    "Statistiche d'uso attive verso {Host}: {Eventi} eventi in attesa",
                    impostazioni.BatchEndpoint!.Host, diario.PendingCount);
            }

            diario.Advance(Today(adesso), _facts);

            await FlushAsync(diario, impostazioni, adesso, cancellationToken).ConfigureAwait(false);

            PersistIfChanged(diario);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Statistiche d'uso: giro del ciclo non riuscito");
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// Ferma il ciclo e salva gli eventi non partiti, senza aspettare la rete.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        await _stop.CancelAsync().ConfigureAwait(false);

        // Annullato il ciclo, l'invio in corso cade subito: si concede un istante per
        // uscire in ordine e poi si prosegue comunque, perche' la chiusura di un posto
        // operatore non aspetta un server delle statistiche.
        if (_loop is { } ciclo)
        {
            await Task.WhenAny(ciclo, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }

        if (_diary is { } diario && _settings.Current.Telemetry.Enabled) Persist(diario);

        lock (_lifecycle)
        {
            _activation?.Dispose();
            _activation = null;
            _client?.Dispose();
            _client = null;
        }

        _stop.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_timing.Tick);

            do
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Chiusura del programma.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Statistiche d'uso: il ciclo si e' fermato");
        }
    }

    /// <summary>Riprende il diario dal file, o ne comincia uno con un identificativo nuovo.</summary>
    private TelemetryDiary Activate()
    {
        lock (_lifecycle)
        {
            if (_diary is { } esistente) return esistente;

            var diario = new TelemetryDiary(ReadState(), _version);

            if (Interlocked.Exchange(ref _updateBeforeActivation, null) is { } aggiornamento)
            {
                diario.RecordUpdateFound(Today(), aggiornamento);
            }

            _activation = new CancellationTokenSource();
            _begun = false;
            _lastSaved = null;
            _nextFlushAt = DateTimeOffset.MinValue;
            _failures = 0;
            _diary = diario;

            return diario;
        }
    }

    /// <summary>
    /// Dimentica tutto: identificativo, giorno in corso, eventi in attesa.
    /// </summary>
    private void Forget()
    {
        lock (_lifecycle)
        {
            var cera = _diary is not null || File.Exists(StatePath);
            if (!cera) return;

            _activation?.Cancel();
            _diary = null;
            _begun = false;
            _lastSaved = null;

            try
            {
                File.Delete(StatePath);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Statistiche d'uso spente, ma il file {Path} non si e' cancellato", StatePath);
                return;
            }

            _log.LogInformation(
                "Statistiche d'uso spente: identificativo ed eventi in attesa dimenticati");
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // Il consenso tolto vale da questo momento, non dal prossimo giro: e' l'unica
        // cosa del ciclo che non puo' aspettare un minuto.
        if (!settings.Telemetry.Enabled) Forget();
    }

    private async Task FlushAsync(
        TelemetryDiary diario,
        TelemetrySettings impostazioni,
        DateTimeOffset adesso,
        CancellationToken cancellationToken)
    {
        if (adesso < _nextFlushAt || diario.PendingCount == 0) return;

        // Durante una chiamata non si manda niente: il lotto aspetta il giro dopo. Pesa
        // pochissimo, ma la voce viaggia sulla stessa rete, e su un collegamento da casa
        // anche poco e' troppo.
        if (_isCallInProgress()) return;

        CancellationToken attivazione;
        PostHogClient client;

        lock (_lifecycle)
        {
            if (!ReferenceEquals(_diary, diario) || _activation is null) return;

            attivazione = _activation.Token;
            client = _client ??= new PostHogClient(_handler, _clientLog);
        }

        using var annullamento = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attivazione);

        // Pochi lotti per giro: una postazione rimasta senza rete per settimane non deve
        // svuotare cinquecento eventi in un colpo solo.
        for (var giro = 0; giro < 4 && diario.PendingCount > 0; giro++)
        {
            var lotto = diario.PeekBatch(PostHogClient.MaxBatchSize);

            PostHogOutcome esito;

            try
            {
                esito = await client.SendAsync(
                    impostazioni.BatchEndpoint!,
                    impostazioni.ProjectKey,
                    diario.InstallationId,
                    lotto,
                    annullamento.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (attivazione.IsCancellationRequested)
            {
                // Il consenso e' stato tolto mentre il lotto partiva.
                return;
            }

            if (esito == PostHogOutcome.Failed)
            {
                _failures++;

                var attesa = TimeSpan.FromTicks(Math.Min(
                    _timing.MaxBackoff.Ticks,
                    _timing.FlushInterval.Ticks * (1L << Math.Min(_failures, 16))));

                _nextFlushAt = adesso + attesa;
                return;
            }

            diario.Remove(lotto.Count);
        }

        _failures = 0;
        _nextFlushAt = adesso + _timing.FlushInterval;
    }

    private void PersistIfChanged(TelemetryDiary diario)
    {
        var testo = JsonSerializer.Serialize(diario.Snapshot(), JsonOptions);
        if (string.Equals(testo, _lastSaved, StringComparison.Ordinal)) return;

        if (Write(diario, testo)) _lastSaved = testo;
    }

    private void Persist(TelemetryDiary diario)
        => Write(diario, JsonSerializer.Serialize(diario.Snapshot(), JsonOptions));

    /// <summary>Scrive il file, se il diario è ancora quello in uso.</summary>
    /// <remarks>
    /// Il controllo sta dentro il blocco perché il consenso può essere tolto fra la
    /// composizione del testo e la scrittura: senza, il file appena cancellato
    /// tornerebbe un istante dopo con l'identificativo che si voleva dimenticare.
    /// </remarks>
    private bool Write(TelemetryDiary diario, string testo)
    {
        lock (_lifecycle)
        {
            if (!ReferenceEquals(_diary, diario)) return false;

            try
            {
                var temporaneo = StatePath + ".tmp";
                File.WriteAllText(temporaneo, testo, Encoding.UTF8);

                if (File.Exists(StatePath)) File.Replace(temporaneo, StatePath, null);
                else File.Move(temporaneo, StatePath);

                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Statistiche d'uso: file {Path} non scritto", StatePath);
                return false;
            }
        }
    }

    /// <summary>
    /// Legge il file della postazione, ripassando ogni evento in attesa dal catalogo.
    /// </summary>
    /// <remarks>
    /// Il file sta nella cartella dell'utente e lo può scrivere chiunque usi quella
    /// utenza: un valore che non è un sì, un no o un testo semplice viene tolto qui, e
    /// quello che resta lo riverifica il diario prima di tenerlo.
    /// </remarks>
    private TelemetryState? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;

            var stato = JsonSerializer.Deserialize<TelemetryState>(
                File.ReadAllText(StatePath, Encoding.UTF8), JsonOptions);

            if (stato is null) return null;

            return stato with { Pending = [.. stato.Pending.Select(Normalize)] };
        }
        catch (Exception ex)
        {
            // Un file illeggibile vale come nessun file: si riparte con un
            // identificativo nuovo, che al server sembrera' un'installazione nuova.
            // E' un conteggio un po' gonfiato, contro un programma che non sa piu' che
            // cosa ha mandato.
            _log.LogWarning(ex, "Statistiche d'uso: file {Path} non leggibile, si riparte da zero", StatePath);
            return null;
        }

        static TelemetryEvent Normalize(TelemetryEvent evento) => evento with
        {
            Properties = evento.Properties
                .Select(p => (p.Key, Valore: p.Value is JsonElement elemento ? ValueOf(elemento) : p.Value))
                .Where(p => p.Valore is bool or string)
                .ToDictionary(p => p.Key, p => p.Valore!, StringComparer.Ordinal),
        };

        static object? ValueOf(JsonElement elemento) => elemento.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => elemento.GetString(),
            _ => null,
        };
    }

    private DateOnly Today() => Today(_clock.Now);

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.DateTime);
}
