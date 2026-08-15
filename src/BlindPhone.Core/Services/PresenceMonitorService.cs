using System.Collections.Concurrent;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Tiene aggiornato lo stato degli interni: libero, occupato, fuori servizio.
/// </summary>
/// <remarks>
/// <para>
/// È l'informazione che decide se una chiamata si può passare. Senza, l'operatore
/// trasferisce alla cieca e scopre che l'interno era spento solo quando il chiamante
/// torna indietro dopo trenta secondi di squilli — e nel frattempo ha già salutato e
/// chiuso, quindi il chiamante ha l'impressione di essere stato abbandonato.
/// </para>
/// <para>
/// <b>Lo stato vive in memoria e non nell'archivio.</b> È la scelta centrale di questa
/// classe. L'archivio conserva i contatti fra un avvio e l'altro, ma una presenza
/// conservata è una presenza vecchia: al riavvio del mattino direbbe "libero" perché
/// così era ieri sera, e l'operatore trasferirebbe una chiamata a un telefono spento
/// fidandosi di un dato scritto. Meglio dire "sconosciuto", che è la verità: non lo
/// sappiamo finché il centralino non ce lo dice.
/// </para>
/// <para>
/// La conseguenza è che all'avvio non si sa nulla di nessuno. Per questo esiste
/// <see cref="Seed"/>: l'elenco degli interni letto dal centralino all'avvio porta con
/// sé lo stato del momento, e da lì in avanti gli eventi lo mantengono aggiornato.
/// </para>
/// <para>
/// <b>Quando il centralino non fornisce la presenza</b> — piano di licenza senza API,
/// collegamento disattivato, firmware che non espone l'evento — la classe lo dichiara
/// con <see cref="IsAvailable"/> invece di rispondere "sconosciuto" a ogni domanda.
/// Sono due cose diverse: "non lo so per questo interno" e "non lo saprò mai per
/// nessuno", e l'operatore deve poter distinguere un dato mancante da una funzione
/// assente.
/// </para>
/// </remarks>
public sealed class PresenceMonitorService : IDisposable
{
    private readonly IQueueProvider _provider;
    private readonly ITelephonyEngine? _telephony;
    private readonly ILogger<PresenceMonitorService> _log;
    private readonly ConcurrentDictionary<string, PresenceState> _states = new(StringComparer.Ordinal);

    private IPresenceSource? _source;
    private bool _started;

    /// <summary>Crea il servizio.</summary>
    /// <param name="provider">Connettore verso il centralino.</param>
    /// <param name="telephony">
    /// Motore telefonico, che offre la sorveglianza per via SIP quando l'API non c'è.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    public PresenceMonitorService(
        IQueueProvider provider,
        ITelephonyEngine? telephony = null,
        ILogger<PresenceMonitorService>? logger = null)
    {
        _provider = provider;
        _telephony = telephony;
        _log = logger ?? NullLogger<PresenceMonitorService>.Instance;
    }

    /// <summary>
    /// Indica se lo stato degli interni è ottenibile, da una qualunque delle strade.
    /// </summary>
    public bool IsAvailable => _source?.IsAvailable
        ?? (_provider.Capabilities.CanReadPresence || _telephony?.PresenceSource is not null);

    /// <summary>
    /// Nome della strada in uso, per la diagnostica e per l'operatore.
    /// </summary>
    public string SourceName => _source?.Name ?? "nessuna";

    /// <summary>Numero di interni di cui si conosce lo stato.</summary>
    public int KnownCount => _states.Count;

    /// <summary>
    /// Sollevato quando cambia lo stato di un interno.
    /// </summary>
    /// <remarks>
    /// Serve alle finestre aperte per aggiornare quello che l'operatore ha già davanti.
    /// Un elenco di contatti che mostra uno stato di dieci minuti prima è peggio di un
    /// elenco che non lo mostra affatto.
    /// </remarks>
    public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

    /// <summary>
    /// Sceglie la strada da cui ottenere lo stato e comincia ad ascoltare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// L'API del centralino viene per prima quando c'è: manda gli eventi da sola, per
    /// tutti gli interni, senza che il posto operatore debba sottoscrivere nulla.
    /// </para>
    /// <para>
    /// Quando non c'è — piano di licenza senza API, collegamento disattivato, firmware
    /// che non le espone — si ripiega sulle sottoscrizioni SIP, che è la stessa strada
    /// dei telefoni da tavolo e funziona su qualunque centralino. Il ripiego non è una
    /// versione ridotta: cambia solo che vanno dichiarati gli interni da sorvegliare,
    /// perché ciascuno costa una sottoscrizione.
    /// </para>
    /// <para>
    /// Va richiamato dopo l'avvio del motore telefonico: prima non esiste il trasporto
    /// SIP su cui sottoscrivere, e la scelta ricadrebbe sull'assenza di sorgenti.
    /// </para>
    /// </remarks>
    public void Start()
    {
        if (_started) return;

        _started = true;

        _source = _provider.Capabilities.CanReadPresence
            ? new QueuePresenceSource(_provider)
            : _telephony?.PresenceSource;

        if (_source is null)
        {
            _log.LogInformation(
                "Stato degli interni non disponibile: il centralino non espone l'API e il motore telefonico "
                + "non offre le sottoscrizioni SIP");

            return;
        }

        _source.PresenceChanged += OnProviderPresenceChanged;
        _source.Start();

        _log.LogInformation(
            "Sorveglianza dello stato degli interni avviata tramite {Sorgente}. "
            + "Richiede la dichiarazione degli interni da guardare: {Esplicita}",
            _source.Name,
            _source.RequiresExplicitWatch);
    }

    /// <summary>
    /// Dichiara quali interni interessano in questo momento.
    /// </summary>
    /// <param name="extensions">Interni che l'operatore ha davanti.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// Sulla strada SIP ogni interno costa una sottoscrizione al centralino, quindi si
    /// sorveglia quello che l'operatore sta guardando — i risultati di una ricerca, i
    /// contatti proposti sotto il campo di composizione — e si lascia andare il resto.
    /// Sulla strada dell'API la chiamata non fa nulla, perché il centralino manda tutto
    /// da solo.
    /// </remarks>
    public async Task WatchAsync(IReadOnlyCollection<string> extensions, CancellationToken cancellationToken)
    {
        if (_source is not { RequiresExplicitWatch: true }) return;

        try
        {
            await _source.WatchAsync(extensions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // La sorveglianza e' un di piu': se non riesce, l'operatore perde lo stato
            // degli interni, non la possibilita' di telefonare.
            _log.LogWarning(ex, "Sorveglianza degli interni non riuscita");
        }
    }

    /// <summary>
    /// Registra lo stato iniziale di un gruppo di interni.
    /// </summary>
    /// <param name="states">Coppie interno e stato, come lette dal centralino.</param>
    /// <remarks>
    /// Da usare con la lettura dell'elenco interni fatta all'avvio: è l'unico momento
    /// in cui si può sapere lo stato di chi non lo ha ancora cambiato.
    /// </remarks>
    public void Seed(IEnumerable<KeyValuePair<string, PresenceState>> states)
    {
        var count = 0;

        foreach (var (extension, state) in states)
        {
            if (state == PresenceState.Unknown) continue;
            if (Normalize(extension) is not { Length: > 0 } key) continue;

            _states[key] = state;
            count++;
        }

        if (count > 0) _log.LogInformation("Stato iniziale noto per {Count} interni", count);
    }

    /// <summary>
    /// Stato di un interno.
    /// </summary>
    /// <param name="extension">Numero dell'interno.</param>
    /// <returns>
    /// Lo stato noto, oppure <see cref="PresenceState.Unknown"/> se non è mai arrivato.
    /// </returns>
    public PresenceState Get(string? extension)
    {
        if (Normalize(extension) is not { Length: > 0 } key) return PresenceState.Unknown;

        return _states.TryGetValue(key, out var state) ? state : PresenceState.Unknown;
    }

    /// <summary>
    /// Conversazione in corso su un interno, quando la sorgente sa dirlo.
    /// </summary>
    /// <param name="extension">Numero dell'interno.</param>
    /// <returns>
    /// Gli identificativi del dialogo, oppure <c>null</c> se l'interno è libero,
    /// non è sorvegliato, o la sorgente non riporta questo dettaglio.
    /// </returns>
    /// <remarks>
    /// L'unico consumatore è l'inclusione su interno occupato. Passa di qui e non
    /// direttamente dalla sorgente perché il resto del prodotto non deve sapere quale
    /// delle due strade — API del centralino o sottoscrizioni SIP — sia in uso.
    /// </remarks>
    public DialogReference? ActiveDialog(string? extension)
    {
        if (Normalize(extension) is not { Length: > 0 } key) return null;

        return _source?.ActiveDialog(key);
    }

    /// <summary>
    /// Chiede lo stato di un interno, sottoscrivendolo se necessario.
    /// </summary>
    /// <param name="extension">Interno da interrogare.</param>
    /// <param name="timeout">Quanto attendere la risposta del centralino.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>
    /// Lo stato noto, oppure <see cref="PresenceState.Unknown"/> se non arriva in
    /// tempo.
    /// </returns>
    /// <remarks>
    /// <para>
    /// È la domanda diretta: «com'è messo l'interno 4021?». Serve prima di passare una
    /// chiamata, ed è il caso in cui l'operatore accetta di aspettare qualche decimo
    /// di secondo per avere una risposta, perché l'alternativa è mandare il chiamante
    /// a squillare nel vuoto.
    /// </para>
    /// <para>
    /// Sulla strada SIP lo stato di un interno si conosce solo dopo averlo
    /// sottoscritto: se non lo si è mai guardato, la prima risposta arriva dopo un
    /// giro di rete. Per questo si sottoscrive e si attende, invece di rispondere
    /// subito «non lo so» — che sarebbe vero ma inutile.
    /// </para>
    /// </remarks>
    public async Task<PresenceState> QueryAsync(
        string? extension,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (Normalize(extension) is not { Length: > 0 } key) return PresenceState.Unknown;

        var noto = Get(key);
        if (noto != PresenceState.Unknown) return noto;
        if (_source is null) return PresenceState.Unknown;

        var atteso = new TaskCompletionSource<PresenceState>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Ascolta(object? sender, PresenceChangedEventArgs e)
        {
            if (Normalize(e.Extension) == key) atteso.TrySetResult(e.Presence);
        }

        PresenceChanged += Ascolta;

        try
        {
            await WatchAsync([key], cancellationToken).ConfigureAwait(false);

            // Puo' essere arrivato mentre ci si registrava all'evento.
            var adesso = Get(key);
            if (adesso != PresenceState.Unknown) return adesso;

            using var scadenza = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var completato = await Task.WhenAny(atteso.Task, Task.Delay(timeout, scadenza.Token))
                .ConfigureAwait(false);

            await scadenza.CancelAsync().ConfigureAwait(false);

            return completato == atteso.Task ? await atteso.Task.ConfigureAwait(false) : PresenceState.Unknown;
        }
        catch (OperationCanceledException)
        {
            return PresenceState.Unknown;
        }
        finally
        {
            PresenceChanged -= Ascolta;
        }
    }

    /// <summary>
    /// Descrive uno stato in una parola pronunciabile.
    /// </summary>
    /// <param name="state">Stato da descrivere.</param>
    /// <returns>La parola, oppure stringa vuota se lo stato non è noto.</returns>
    /// <remarks>
    /// <para>
    /// Una parola sola, e nessun colore. La colonna dello stato viene letta a ogni
    /// spostamento orizzontale nell'elenco dei contatti: "occupato" costa mezzo
    /// secondo di ascolto, "attualmente impegnato in conversazione" ne costa tre, e
    /// moltiplicati per venti contatti diventano un minuto perso.
    /// </para>
    /// <para>
    /// Lo stato sconosciuto restituisce stringa vuota invece di "sconosciuto": in una
    /// tabella dove la maggioranza delle celle sarebbe sconosciuta, quella parola
    /// ripetuta diventa rumore che si impara a ignorare — e allora smette di essere
    /// notata anche quando conta.
    /// </para>
    /// </remarks>
    public static string Describe(PresenceState state) => state switch
    {
        PresenceState.Available => "libero",
        PresenceState.Busy => "occupato",
        PresenceState.Ringing => "squilla",
        PresenceState.DoNotDisturb => "non disturbare",
        PresenceState.Away => "assente",
        PresenceState.Offline => "fuori servizio",
        _ => string.Empty,
    };

    /// <summary>
    /// Indica se lo stato sconsiglia di passare una chiamata.
    /// </summary>
    /// <param name="state">Stato da valutare.</param>
    /// <remarks>
    /// «Squilla» non è fra questi: un interno che squilla può rispondere da un momento
    /// all'altro, e con la chiamata su attesa il tempo c'è.
    /// </remarks>
    public static bool DiscouragesTransfer(PresenceState state)
        => state is PresenceState.Busy or PresenceState.DoNotDisturb
            or PresenceState.Offline or PresenceState.Away;

    private void OnProviderPresenceChanged(object? sender, PresenceChangedEventArgs e)
    {
        if (Normalize(e.Extension) is not { Length: > 0 } key) return;

        var previous = _states.TryGetValue(key, out var known) ? known : PresenceState.Unknown;

        // Un interno che si registra di nuovo e' raggiungibile, ma questo non dice se
        // sia libero: potrebbe essere gia' in conversazione. Se di lui sapevamo
        // qualcosa di piu' preciso, arrivato dall'evento di presenza, quel dato resta.
        // Si accetta il generico solo quando l'alternativa e' continuare a dire "fuori
        // servizio" di un telefono che nel frattempo e' tornato.
        if (e.FromRegistration
            && e.Presence == PresenceState.Available
            && previous is not (PresenceState.Unknown or PresenceState.Offline))
        {
            return;
        }

        _states[key] = e.Presence;

        if (previous == e.Presence) return;

        _log.LogDebug("Interno {Extension}: {Previous} diventa {Current}", key, previous, e.Presence);

        PresenceChanged?.Invoke(this, e);
    }

    /// <summary>
    /// Riduce un numero alla forma con cui viene confrontato.
    /// </summary>
    /// <remarks>
    /// Il centralino può riportare l'interno con spazi o punteggiatura, e la rubrica
    /// può averlo memorizzato in un'altra forma ancora. Confrontando le sole cifre,
    /// "4021" e "40 21" restano lo stesso interno: senza, lo stato arriverebbe e non
    /// verrebbe attribuito a nessuno.
    /// </remarks>
    private static string Normalize(string? extension)
        => extension is null ? string.Empty : new string([.. extension.Where(char.IsAsciiDigit)]);

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_started) return;

        if (_source is not null)
        {
            _source.PresenceChanged -= OnProviderPresenceChanged;

            // La sorgente SIP appartiene al motore telefonico, che la chiude insieme
            // al trasporto: qui si smette solo di ascoltarla.
        }

        _source = null;
        _started = false;
    }
}
