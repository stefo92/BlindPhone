using System.Collections.Concurrent;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace BlindPhone.Telephony;

/// <summary>
/// Stato degli interni ottenuto per via SIP, senza API del centralino.
/// </summary>
/// <remarks>
/// <para>
/// È la strada che funziona sempre, perché è la stessa che usano i telefoni da tavolo
/// per accendere i tasti di occupato: si sottoscrive lo stato di un interno con un
/// SUBSCRIBE al pacchetto di eventi <c>dialog</c> (RFC 4235) e il centralino manda un
/// NOTIFY a ogni cambiamento. Non richiede alcun piano di licenza, alcuna API
/// abilitata, alcun Client ID: solo un centralino che parli SIP.
/// </para>
/// <para>
/// <b>Perché si sorveglia solo ciò che serve.</b> Ogni interno guardato costa una
/// sottoscrizione, che il centralino tiene in memoria e rinnova periodicamente.
/// Sottoscrivere l'intera rubrica di un ente significherebbe centinaia di
/// sottoscrizioni per un posto operatore che alla volta ne guarda cinque: è il modo
/// più rapido di far arrabbiare chi amministra il PBX, e su alcuni modelli di
/// superare il limite di sottoscrizioni concorrenti. Si sottoscrive quello che
/// l'operatore ha davanti, e si lascia andare il resto.
/// </para>
/// <para>
/// <b>Sulla consegna delle notifiche.</b> Ogni sottoscrizione si aggancia da sé al
/// trasporto SIP e riconosce le proprie notifiche dal Call-ID: non c'è nulla da
/// smistare a mano. Il metodo con cui lo fa non è pubblico nella libreria, il che è
/// la conferma che il compito non spetta a chi la usa.
/// </para>
/// <para>
/// <b>Limite noto e dichiarato.</b> Questo percorso non è stato verificato su un
/// centralino reale: i nomi degli stati e la forma del documento seguono la norma
/// RFC 4235, e la traduzione in stato per l'operatore è coperta da test, ma il
/// comportamento del singolo firmware può differire. Ogni errore qui degrada verso
/// «stato sconosciuto», mai verso un'informazione sbagliata, e non tocca la
/// possibilità di telefonare.
/// </para>
/// </remarks>
public sealed class SipPresenceSource : IPresenceSource
{
    /// <summary>
    /// Numero massimo di interni sorvegliati contemporaneamente.
    /// </summary>
    /// <remarks>
    /// L'elenco dei contatti proposti sotto il campo di composizione ne mostra al
    /// massimo otto, e i risultati della rubrica si guardano pochi alla volta: oltre
    /// questa soglia si stanno sottoscrivendo interni che nessuno sta guardando.
    /// </remarks>
    private const int MaxWatched = 24;

    /// <summary>Durata della sottoscrizione, in secondi.</summary>
    /// <remarks>
    /// Tre minuti: abbastanza lunga da non riempire la rete di rinnovi, abbastanza
    /// corta da accorgersi in fretta che il centralino ha dimenticato la
    /// sottoscrizione dopo un riavvio.
    /// </remarks>
    private const int SubscriptionExpirySeconds = 180;

    private readonly SIPTransport _transport;
    private readonly SipAccountOptions _account;
    private readonly ILogger<SipPresenceSource> _log;

    private readonly ConcurrentDictionary<string, SIPNotifierClient> _watched = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Ultimo dialogo confermato per ciascun interno sorvegliato.
    /// </summary>
    /// <remarks>
    /// È quello che rende possibile l'inclusione: l'intestazione <c>Join</c> dell'RFC
    /// 3911 vuole gli identificativi di una conversazione a cui non partecipiamo, e le
    /// notifiche di stato sono l'unico posto in cui questa postazione li vede passare.
    /// Prima venivano letti per ricavarne «libero» o «occupato» e poi buttati via.
    /// </remarks>
    private readonly ConcurrentDictionary<string, DialogReference> _dialogs = new(StringComparer.Ordinal);

    private bool _started;
    private bool _disposed;

    /// <summary>Crea la sorgente.</summary>
    /// <param name="transport">Trasporto SIP già avviato dal motore telefonico.</param>
    /// <param name="account">Credenziali e dominio dell'account.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SipPresenceSource(
        SIPTransport transport,
        SipAccountOptions account,
        ILogger<SipPresenceSource>? logger = null)
    {
        _transport = transport;
        _account = account;
        _log = logger ?? NullLogger<SipPresenceSource>.Instance;
    }

    /// <inheritdoc />
    public string Name => "sottoscrizioni SIP";

    /// <inheritdoc />
    public bool IsAvailable => !_disposed;

    /// <inheritdoc />
    /// <remarks>
    /// Ogni interno costa una sottoscrizione: chi usa questa sorgente deve dire cosa
    /// sta guardando.
    /// </remarks>
    public bool RequiresExplicitWatch => true;

    /// <inheritdoc />
    public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

    /// <inheritdoc />
    public void Start()
    {
        _started = true;
        _log.LogInformation(
            "Stato degli interni per via SIP: sottoscrizioni al pacchetto dialog, massimo {Max} interni",
            MaxWatched);
    }

    /// <inheritdoc />
    public Task WatchAsync(IReadOnlyCollection<string> extensions, CancellationToken cancellationToken)
    {
        if (_disposed || !_started) return Task.CompletedTask;

        var voluti = extensions
            .Select(Normalize)
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxWatched)
            .ToHashSet(StringComparer.Ordinal);

        lock (_gate)
        {
            // Quello che non serve piu' si lascia andare: una sottoscrizione dimenticata
            // continua a costare al centralino e a produrre notifiche che nessuno legge.
            foreach (var (interno, client) in _watched.ToList())
            {
                if (voluti.Contains(interno)) continue;

                Stop(client);
                _watched.TryRemove(interno, out _);
            }

            foreach (var interno in voluti)
            {
                if (_watched.ContainsKey(interno)) continue;
                if (_watched.Count >= MaxWatched) break;

                if (TrySubscribe(interno) is { } client) _watched[interno] = client;
            }
        }

        return Task.CompletedTask;
    }

    private SIPNotifierClient? TrySubscribe(string extension)
    {
        try
        {
            // IL TRASPORTO VA DICHIARATO ANCHE QUI.
            //
            // Senza, la libreria sceglie il valore predefinito - UDP in chiaro - e la
            // sottoscrizione parte da un canale diverso da quello su cui l'account e'
            // registrato. Il centralino la rifiuta, e lo stato degli interni non
            // compare mai: fra il 10 e l'11 agosto 2026 sono state 1.589 sottoscrizioni
            // respinte contro 57 accettate.
            var risorsa = SIPURI.ParseSIPURIRelaxed(
                $"{extension}@{_account.Domain}{_account.TransportParameter}");

            var client = new SIPNotifierClient(
                _transport,
                outboundProxy: null,
                SIPEventPackagesEnum.Dialog,
                risorsa,
                _account.AuthId ?? _account.Username,
                _account.Domain,
                _account.Password,
                SubscriptionExpirySeconds,
                filter: null);

            client.NotificationReceived += (_, corpo) => OnNotification(extension, corpo);
            client.SubscriptionFailed += (_, codice, motivo) => OnSubscriptionFailed(extension, codice, motivo);

            // Riuscita e fallimento si registrano allo stesso livello, quello
            // ordinario: davanti a "lo stato degli interni non compare" la prima cosa
            // da sapere e' se le sottoscrizioni partono, e chiederlo all'operatore
            // significherebbe fargli accendere la modalita' debug e riprodurre il caso.
            client.SubscriptionSuccessful += _ =>
                _log.LogInformation("Sorveglianza dell'interno {Extension} accettata dal centralino", extension);

            client.Start();

            _log.LogInformation(
                "Sottoscrizione allo stato dell'interno {Extension} inviata a {Risorsa}", extension, risorsa);

            return client;
        }
        catch (Exception ex)
        {
            // Una sottoscrizione che non parte non deve impedire le altre, ne' tanto
            // meno il funzionamento del telefono.
            _log.LogWarning(ex, "Sottoscrizione allo stato dell'interno {Extension} non riuscita", extension);
            return null;
        }
    }

    /// <summary>
    /// Interpreta il documento di stato ricevuto dal centralino.
    /// </summary>
    /// <remarks>
    /// La lettura è difensiva per scelta: un corpo in una forma inattesa produce uno
    /// stato assente, non un'eccezione che interrompe la sorveglianza degli altri
    /// interni. La regola che traduce i dialoghi in stato sta nel core ed è verificata
    /// dai test: qui si estraggono soltanto i dati.
    /// </remarks>
    private void OnNotification(string extension, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;

        try
        {
            var documento = new SIPEventDialogInfo();
            documento.Load(body);

            var stato = SipDialogPresence.FromDialogStates(documento.DialogItems.Select(d => d.State));

            RecordDialog(extension, documento);

            _log.LogInformation(
                "Interno {Extension}: notifica con {Dialoghi} dialoghi, stato {Stato}",
                extension, documento.DialogItems.Count, stato);

            PresenceChanged?.Invoke(
                this, new PresenceChangedEventArgs(extension, stato, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Documento di stato dell'interno {Extension} non interpretabile", extension);
        }
    }

    /// <summary>
    /// Conserva gli identificativi della conversazione in corso su un interno.
    /// </summary>
    /// <param name="extension">Interno a cui la notifica si riferisce.</param>
    /// <param name="documento">Documento di stato ricevuto.</param>
    /// <remarks>
    /// <para>
    /// <b>Si tiene solo il dialogo confermato.</b> Una notifica può descriverne più
    /// d'uno — uno che sta squillando e uno in conversazione — e includersi in un
    /// dialogo non ancora instaurato non significa niente: non c'è ancora nessuna
    /// conversazione da raggiungere, e l'etichetta della parte remota non esiste.
    /// </para>
    /// <para>
    /// <b>Quando l'interno torna libero il ricordo si cancella.</b> Un identificativo
    /// rimasto in memoria dopo la fine della conversazione farebbe partire un invito
    /// verso un dialogo che non c'è più: il centralino lo rifiuterebbe, e l'operatore
    /// si sentirebbe dire che l'inclusione non è riuscita senza capire perché. Meglio
    /// dichiarare subito che non c'è niente in cui entrare.
    /// </para>
    /// </remarks>
    private void RecordDialog(string extension, SIPEventDialogInfo documento)
    {
        var confermato = documento.DialogItems.FirstOrDefault(
            d => string.Equals(d.State, "confirmed", StringComparison.OrdinalIgnoreCase));

        if (confermato is null)
        {
            _dialogs.TryRemove(extension, out _);
            return;
        }

        var riferimento = new DialogReference(
            extension,
            confermato.CallID ?? string.Empty,
            confermato.LocalTag ?? string.Empty,
            confermato.RemoteTag ?? string.Empty,
            DateTimeOffset.Now);

        if (!riferimento.IsComplete)
        {
            // Succede con i centralini che riportano lo stato senza i dettagli del
            // dialogo. La presenza continua a funzionare, l'inclusione no: e' una
            // limitazione del centralino e va detta, non nascosta.
            _log.LogDebug(
                "Interno {Extension}: la notifica non porta gli identificativi del dialogo, "
                + "l'inclusione non sara' disponibile",
                extension);

            _dialogs.TryRemove(extension, out _);
            return;
        }

        _dialogs[extension] = riferimento;
    }

    /// <inheritdoc />
    public DialogReference? ActiveDialog(string extension)
        => _dialogs.TryGetValue(Normalize(extension), out var dialogo) ? dialogo : null;

    /// <summary>
    /// Chiude la sorveglianza di un interno che il centralino non concede.
    /// </summary>
    /// <param name="extension">Interno rifiutato.</param>
    /// <param name="status">Codice della risposta.</param>
    /// <remarks>
    /// <para>
    /// <b>Un rifiuto definitivo non si ritenta.</b> La libreria riprova a oltranza, e
    /// per un errore passeggero è il comportamento giusto: la rete torna, il centralino
    /// si riavvia, la sottoscrizione riparte da sola. Ma un <c>404</c> dice che quella
    /// risorsa non esiste e un <c>403</c> che non ci è concessa: riprovare ogni pochi
    /// secondi non cambierà la risposta.
    /// </para>
    /// <para>
    /// Il conto di due giorni di esercizio: <b>1.589 sottoscrizioni respinte</b>, quasi
    /// tutte ripetizioni delle stesse poche richieste. Oltre a non servire a niente,
    /// una raffica del genere verso un SBC somiglia a un attacco, e la difesa tipica di
    /// quegli apparati è mettere in lista nera l'indirizzo che la produce: cioè la
    /// postazione, che da quel momento non telefonerebbe più.
    /// </para>
    /// <para>
    /// La sorveglianza riparte da sé quando l'operatore torna a guardare quell'interno,
    /// perché <see cref="WatchAsync"/> ricrea le sottoscrizioni mancanti.
    /// </para>
    /// </remarks>
    private void StopWatching(string extension, SIPResponseStatusCodesEnum status)
    {
        lock (_gate)
        {
            if (!_watched.TryRemove(extension, out var client)) return;

            Stop(client);
        }

        _log.LogInformation(
            "Sorveglianza dell'interno {Extension} sospesa dopo un rifiuto definitivo ({Codice}): "
            + "riprovare non cambierebbe la risposta e riempirebbe il centralino di richieste",
            extension, (int)status);
    }

    /// <summary>Rifiuti su cui non ha senso insistere.</summary>
    /// <remarks>
    /// <c>404</c> e <c>410</c>: quella risorsa non c'è. <c>403</c> e <c>401</c>: non ci
    /// è concessa. <c>489</c>: il centralino non conosce il pacchetto di eventi, quindi
    /// non lo conoscerà nemmeno al prossimo tentativo. <c>400</c>: la richiesta è
    /// malformata per quell'apparato, e ripeterla identica darà lo stesso esito.
    /// </remarks>
    private static bool IsPermanent(SIPResponseStatusCodesEnum status) => (int)status switch
    {
        400 or 401 or 403 or 404 or 405 or 410 or 489 => true,
        _ => false,
    };

    private void OnSubscriptionFailed(string extension, SIPResponseStatusCodesEnum status, string reason)
    {
        var stato = SipDialogPresence.FromSubscriptionFailure((int)status);

        _log.LogInformation(
            "Sorveglianza dell'interno {Extension} rifiutata dal centralino: {Codice} {Motivo}",
            extension, (int)status, reason);

        // Un rifiuto che dice "quell'interno non esiste" e' esso stesso
        // un'informazione per l'operatore; un rifiuto per mancanza di autorizzazione
        // non lo e', e non va spacciato per stato del telefono.
        if (stato is { } presenza)
        {
            PresenceChanged?.Invoke(
                this, new PresenceChangedEventArgs(extension, presenza, DateTimeOffset.Now));
        }

        if (IsPermanent(status)) StopWatching(extension, status);
    }

    private void Stop(SIPNotifierClient client)
    {
        try
        {
            client.Stop();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Chiusura di una sottoscrizione non riuscita");
        }
    }

    private static string Normalize(string? extension)
        => extension is null ? string.Empty : new string([.. extension.Where(char.IsAsciiDigit)]);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        lock (_gate)
        {
            foreach (var client in _watched.Values) Stop(client);
            _watched.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
