using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Registra le chiamate concluse nell'elenco delle ultime chiamate.
/// </summary>
/// <remarks>
/// <para>
/// Serve alla domanda che un centralinista si fa più volte al giorno: «chi mi ha
/// cercato mentre non c'ero?». Senza un registro, una chiamata persa mentre si era
/// impegnati su un'altra linea sparisce senza lasciare traccia — e per chi non vede
/// non c'è nemmeno un'icona lampeggiante a ricordarlo.
/// </para>
/// <para>
/// La registrazione avviene alla <b>chiusura</b> della chiamata e non alla sua
/// comparsa: prima di allora non si sa se sarà risposta o persa, e sono due voci
/// diverse in due elenchi diversi.
/// </para>
/// </remarks>
public sealed class CallHistoryService : IDisposable
{
    private readonly CallSessionManager _calls;
    private readonly ICallHistoryRepository _repository;
    private readonly ILogger<CallHistoryService> _log;

    private bool _started;

    /// <summary>Crea il servizio.</summary>
    /// <param name="calls">Gestore delle chiamate.</param>
    /// <param name="repository">Archivio dell'elenco.</param>
    /// <param name="logger">Log diagnostico.</param>
    public CallHistoryService(
        CallSessionManager calls,
        ICallHistoryRepository repository,
        ILogger<CallHistoryService>? logger = null)
    {
        _calls = calls;
        _repository = repository;
        _log = logger ?? NullLogger<CallHistoryService>.Instance;
    }

    /// <summary>Comincia a registrare.</summary>
    public void Start()
    {
        if (_started) return;

        _started = true;
        _calls.CallStateChanged += OnCallStateChanged;
    }

    /// <summary>
    /// Applica il periodo di conservazione, eliminando le voci più vecchie.
    /// </summary>
    /// <param name="retention">Periodo da conservare; zero significa «mai cancellare».</param>
    /// <param name="now">Momento di riferimento.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    public Task<int> PurgeAsync(TimeSpan retention, DateTimeOffset now, CancellationToken cancellationToken = default)
        => _repository.PurgeAsync(retention, now, cancellationToken);

    /// <summary>
    /// Decide in quale dei tre elenchi finisce una chiamata conclusa.
    /// </summary>
    /// <param name="call">Chiamata conclusa.</param>
    /// <returns>L'elenco di destinazione.</returns>
    /// <remarks>
    /// <para>
    /// Una chiamata entrante conta come <b>persa</b> quando non è mai stata risposta.
    /// Non importa perché sia finita — l'operatore era occupato, l'ha rifiutata, il
    /// chiamante ha riagganciato prima: dal punto di vista di chi deve richiamare, il
    /// fatto rilevante è che quella persona voleva parlare e non ci è riuscita.
    /// </para>
    /// <para>
    /// Le chiamate uscite non risposte restano fra le uscite e non diventano «perse»:
    /// perse sono quelle che l'operatore ha subito, non quelle che ha fatto lui.
    /// </para>
    /// </remarks>
    public static CallHistoryKind Classify(Call call)
    {
        if (call.Direction == CallDirection.Outbound) return CallHistoryKind.Outgoing;

        return call.AnsweredAt is null ? CallHistoryKind.Missed : CallHistoryKind.Incoming;
    }

    /// <summary>
    /// Durata della conversazione, zero se non c'è mai stata.
    /// </summary>
    /// <remarks>
    /// Si misura dalla risposta e non dalla comparsa: il tempo di squillo non è
    /// conversazione, e sommarlo gonfierebbe le durate di ogni chiamata dell'elenco.
    /// </remarks>
    public static TimeSpan DurationOf(Call call)
    {
        if (call.AnsweredAt is not { } answered) return TimeSpan.Zero;

        var end = call.EndedAt ?? answered;
        var duration = end - answered;

        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    private void OnCallStateChanged(object? sender, CallStateChangedEventArgs e)
    {
        if (e.Transition.To != CallState.Terminated) return;

        var call = e.Call;

        var entry = new CallHistoryEntry(
            Id: 0,
            Classify(call),
            call.RemoteParty.Number,
            call.RemoteParty.DisplayName,
            call.CreatedAt,
            DurationOf(call),
            call.QueueName,
            call.EndReason);

        _log.LogInformation(
            "Chiamata registrata nell'elenco: {Kind} con {Numero}, durata {Durata}",
            entry.Kind, entry.Number, entry.Duration);

        // Senza attendere: la registrazione non deve rallentare la chiusura della
        // chiamata, e l'archivio assorbe da solo i propri errori.
        _ = _repository.AddAsync(entry, CancellationToken.None);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_started) return;

        _calls.CallStateChanged -= OnCallStateChanged;
        _started = false;
    }
}
