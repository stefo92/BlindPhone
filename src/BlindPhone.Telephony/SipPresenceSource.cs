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
/// <b>Ogni errore degrada verso «stato sconosciuto», mai verso un'informazione
/// sbagliata.</b> Era scritto qui anche prima e non era vero: il rifiuto di una
/// sottoscrizione veniva tradotto in «fuori servizio», e su quel dato falso l'avviso
/// preventivo sconsigliava trasferimenti verso interni che rispondevano al primo
/// squillo. Adesso lo stato di un telefono arriva soltanto da un NOTIFY, e un rifiuto
/// decide un'altra cosa: se valga la pena insistere.
/// </para>
/// <para>
/// <b>Limite noto e dichiarato.</b> I nomi degli stati e la forma del documento seguono
/// la norma RFC 4235 e la loro traduzione è coperta da test, ma il comportamento del
/// singolo firmware differisce: sul centralino su cui il prodotto è in esercizio le
/// sottoscrizioni al pacchetto <c>dialog</c> ricevono tutte <c>404</c>, e la
/// sorveglianza non produce nulla. Per questo esiste
/// <see cref="PresenceSubscriptionPolicy"/>: una funzione che l'impianto non serve va
/// dichiarata assente, non tentata a ogni ricerca.
/// </para>
/// </remarks>
public sealed class SipPresenceSource : IPresenceSource
{
    /// <summary>
    /// Numero massimo di interni sorvegliati contemporaneamente.
    /// </summary>
    /// <remarks>
    /// L'elenco dei contatti proposti sopra il campo di composizione ne mostra al
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

    /// <summary>Proxy in uscita corrente dell'account: le SUBSCRIBE passano di li' come il resto.</summary>
    private readonly Func<SIPEndPoint?> _outboundProxy;

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

    /// <summary>
    /// Quanto insistere con un centralino che rifiuta: la regola sta nel core.
    /// </summary>
    /// <remarks>
    /// Vale per la sessione: si riprova al riavvio della postazione, che è anche il
    /// momento in cui un amministratore ha appena cambiato la configurazione del
    /// centralino e vuole vedere se ha funzionato.
    /// </remarks>
    private readonly PresenceSubscriptionPolicy _policy = new();

    private bool _started;
    private bool _disposed;

    /// <summary>Se la rinuncia è già stata scritta nel registro.</summary>
    /// <remarks>
    /// I rifiuti arrivano a raffica e più d'uno può riguardare lo stesso interno: la
    /// riga che spiega perché la funzione si è spenta va scritta una volta, e va scritta
    /// sempre — anche se in quel momento non restava nessuna sottoscrizione da chiudere.
    /// </remarks>
    private bool _gaveUpLogged;

    /// <summary>Crea la sorgente.</summary>
    /// <param name="transport">Trasporto SIP già avviato dal motore telefonico.</param>
    /// <param name="account">Credenziali e dominio dell'account.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="outboundProxy">Fornisce il proxy in uscita corrente dell'account, se c'e'.</param>
    public SipPresenceSource(
        SIPTransport transport,
        SipAccountOptions account,
        ILogger<SipPresenceSource>? logger = null,
        Func<SIPEndPoint?>? outboundProxy = null)
    {
        _transport = transport;
        _account = account;
        _outboundProxy = outboundProxy ?? (() => null);
        _log = logger ?? NullLogger<SipPresenceSource>.Instance;
    }

    /// <inheritdoc />
    public string Name => "sottoscrizioni SIP";

    /// <inheritdoc />
    /// <remarks>
    /// Diventa falso quando il centralino ha dimostrato di non servire la funzione: da
    /// quel momento «lo stato degli interni non è disponibile» è la verità, e dirla è
    /// meglio che continuare a mostrare una colonna sempre vuota.
    /// </remarks>
    public bool IsAvailable => !_disposed && !_policy.GaveUp;

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
        if (_disposed || !_started || _policy.GaveUp) return Task.CompletedTask;

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
                outboundProxy: _outboundProxy(),
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
            client.SubscriptionSuccessful += _ => OnSubscriptionSucceeded(extension);

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

    private void OnSubscriptionSucceeded(string extension)
    {
        // Un'accettazione azzera il conto dei rifiuti: da qui in poi si sa che su
        // questo centralino la funzione c'e', e i rifiuti tornano a essere quello che
        // sono davvero, cioe' notizie sul singolo numero.
        _policy.Accept();

        _log.LogInformation("Sorveglianza dell'interno {Extension} accettata dal centralino", extension);
    }

    /// <summary>
    /// Interpreta un rifiuto del centralino.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Da qui non esce nessuno stato di telefono</b>, e prima ne usciva: un
    /// <c>404</c> diventava «fuori servizio», e l'avviso che sconsiglia un
    /// trasferimento si fondava su quello. La ragione per cui è sbagliato sta nelle
    /// note di <see cref="SipDialogPresence"/>, insieme al conto del registro che l'ha
    /// mostrato: 81 interni funzionanti dichiarati fuori servizio in una mattina.
    /// </para>
    /// <para>
    /// Quello che il rifiuto dice davvero è se riprovare abbia senso, e su quale scala:
    /// per quel numero o per l'intero impianto.
    /// </para>
    /// </remarks>
    private void OnSubscriptionFailed(string extension, SIPResponseStatusCodesEnum status, string reason)
    {
        var natura = SipDialogPresence.Classify((int)status);

        _log.LogInformation(
            "Sorveglianza dell'interno {Extension} rifiutata dal centralino: {Codice} {Motivo}. "
            + "Natura del rifiuto: {Natura}. Lo stato del telefono resta sconosciuto, "
            + "perche' un rifiuto della sottoscrizione non lo dice",
            extension, (int)status, reason, natura);

        switch (_policy.Refuse(natura))
        {
            case PresenceSubscriptionAction.KeepTrying:
                break;

            case PresenceSubscriptionAction.StopThisExtension:
                StopWatching(extension, status);
                break;

            case PresenceSubscriptionAction.GiveUp:
                GiveUp(natura == SubscriptionRefusal.EventPackage
                    ? $"il centralino non serve il pacchetto di eventi dialog ({(int)status} {reason})"
                    : $"{_policy.ConsecutiveRefusals} sottoscrizioni rifiutate di fila e nessuna "
                      + $"accettata, l'ultima con {(int)status}");
                break;
        }
    }

    /// <summary>
    /// Smette di sorvegliare gli interni su questo centralino, per questa sessione.
    /// </summary>
    /// <param name="motivo">Perché si rinuncia, come finisce nel registro.</param>
    /// <remarks>
    /// <para>
    /// <b>Rinunciare è la cosa giusta da fare, e va detto in chiaro.</b> Un centralino
    /// che rifiuta ogni sottoscrizione non cambierà idea per insistenza: continuare
    /// significa mandargli una richiesta per ogni nome cercato per tutto il turno.
    /// Oltre a non servire a niente, una raffica del genere verso un apparato di
    /// frontiera somiglia a un attacco, e la difesa tipica è mettere in lista nera
    /// l'indirizzo che la produce — cioè la postazione, che da quel momento non
    /// telefonerebbe più.
    /// </para>
    /// <para>
    /// Il livello è quello dell'avviso e non dell'informazione: è una funzione
    /// dichiarata in offerta che su questo impianto non c'è, e chi legge il registro
    /// per capire perché la colonna dello stato è vuota deve trovare la risposta senza
    /// cercarla.
    /// </para>
    /// </remarks>
    private void GiveUp(string motivo)
    {
        lock (_gate)
        {
            if (_gaveUpLogged) return;
            _gaveUpLogged = true;

            foreach (var client in _watched.Values) Stop(client);
            _watched.Clear();
        }

        _log.LogWarning(
            "Sorveglianza dello stato degli interni disattivata su questo centralino: {Motivo}. "
            + "Lo stato degli interni non sara' disponibile fino al prossimo avvio della postazione. "
            + "Il trasferimento delle chiamate non ne dipende e continua a funzionare",
            motivo);
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
