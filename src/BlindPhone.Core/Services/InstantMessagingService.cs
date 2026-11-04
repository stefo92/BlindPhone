using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>Dove si trova, rispetto all'operatore, la finestra di una conversazione.</summary>
/// <remarks>
/// Decide come si annuncia un messaggio in arrivo e se conta come da leggere. Lo sa
/// solo l'interfaccia, che lo comunica al servizio con
/// <see cref="InstantMessagingService.VisibilityOf"/>.
/// </remarks>
public enum ConversationVisibility
{
    /// <summary>Nessuna finestra aperta per questa conversazione.</summary>
    Closed,

    /// <summary>Finestra aperta, ma l'operatore sta lavorando altrove.</summary>
    Background,

    /// <summary>Finestra aperta e attiva: l'operatore ha davanti proprio questa conversazione.</summary>
    InFront,
}

/// <summary>La conversazione con un collega.</summary>
/// <remarks>
/// Si legge da qualunque thread: ogni proprietà restituisce una fotografia presa sotto
/// lo stesso lucchetto con cui il servizio la modifica, perché i messaggi arrivano dal
/// thread del trasporto SIP mentre l'interfaccia li sta mostrando.
/// </remarks>
public sealed class MessageConversation
{
    private readonly Lock _gate;
    private readonly List<InstantMessage> _messages = [];
    private string? _name;
    private int _unread;
    private DateTimeOffset _lastActivity;

    internal MessageConversation(string peer, string? name, DateTimeOffset createdAt, Lock gate)
    {
        Peer = peer;
        _name = name;
        _lastActivity = createdAt;
        _gate = gate;
    }

    /// <summary>Interno o numero del collega: la chiave della conversazione.</summary>
    public string Peer { get; }

    /// <summary>Nome del collega, se lo si conosce.</summary>
    public string? Name
    {
        get { lock (_gate) return _name; }
    }

    /// <summary>Messaggi, dal più vecchio al più recente.</summary>
    public IReadOnlyList<InstantMessage> Messages
    {
        get { lock (_gate) return [.. _messages]; }
    }

    /// <summary>Ultimo messaggio, se ce n'è uno.</summary>
    public InstantMessage? Last
    {
        get { lock (_gate) return _messages.Count == 0 ? null : _messages[^1]; }
    }

    /// <summary>Messaggi arrivati che l'operatore non ha ancora avuto davanti.</summary>
    public int UnreadCount
    {
        get { lock (_gate) return _unread; }
    }

    /// <summary>Istante dell'ultimo messaggio, o dell'apertura se non ce ne sono.</summary>
    public DateTimeOffset LastActivity
    {
        get { lock (_gate) return _lastActivity; }
    }

    // Le modifiche sono del servizio, e il servizio le fa gia' sotto il lucchetto.
    internal void SetName(string? name) => _name = name;

    internal void Add(InstantMessage message)
    {
        _messages.Add(message);
        _lastActivity = message.At;
    }

    internal bool Replace(InstantMessage message)
    {
        var indice = _messages.FindIndex(m => m.Id == message.Id);
        if (indice < 0) return false;

        _messages[indice] = message;
        return true;
    }

    internal void AddUnread() => _unread++;

    internal bool ClearUnread()
    {
        if (_unread == 0) return false;

        _unread = 0;
        return true;
    }
}

/// <summary>Un messaggio e la conversazione a cui appartiene.</summary>
/// <param name="Conversation">Conversazione.</param>
/// <param name="Message">Messaggio aggiunto o cambiato.</param>
public sealed record InstantMessageEventArgs(MessageConversation Conversation, InstantMessage Message);

/// <summary>
/// Le conversazioni a messaggi con i colleghi: chi ha scritto, che cosa, che cosa è
/// stato consegnato e che cosa resta da leggere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Si aggancia al motore e non all'account</b>, come la segreteria: il motore è
/// l'involucro che resta lo stesso quando l'operatore cambia account, quindi le
/// conversazioni sopravvivono al cambio e un collega che scrive dopo finisce nella
/// stessa finestra.
/// </para>
/// <para>
/// <b>Le conversazioni durano quanto la sessione.</b> Non si scrivono su disco: sono
/// scambi di servizio fra colleghi — «ti passo una chiamata», «sei libero?» — che
/// hanno valore nell'ora in cui si scrivono. Conservarli vorrebbe dire custodire
/// messaggi personali di terzi su una postazione condivisa a turni, con tutto quello
/// che ne segue per la riservatezza.
/// </para>
/// <para>
/// <b>Un messaggio in arrivo non sposta mai il cursore.</b> È il principio di tutta la
/// postazione: chi sta scrivendo un numero non deve ritrovarsi a scrivere in una
/// conversazione. Il messaggio si annuncia, si conta fra quelli da leggere, e la
/// finestra si apre quando l'operatore la chiede.
/// </para>
/// </remarks>
public sealed class InstantMessagingService : IDisposable
{
    private readonly ITelephonyEngine _engine;
    private readonly IAnnouncer _announcer;
    private readonly IContactRepository? _contacts;
    private readonly IClock _clock;
    private readonly ILogger<InstantMessagingService> _log;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, MessageConversation> _conversations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Annunci dei messaggi in arrivo, uno dopo l'altro.
    /// </summary>
    /// <remarks>
    /// Il nome del mittente si cerca in rubrica prima di annunciare, e la ricerca può
    /// durare un istante: senza la catena, il secondo di due messaggi ravvicinati
    /// poteva essere detto prima del primo.
    /// </remarks>
    private Task _announcing = Task.CompletedTask;

    private long _counter;

    /// <summary>
    /// Colleghi a cui un messaggio è partito senza conferma, dall'ultima consegna riuscita.
    /// </summary>
    /// <remarks>
    /// Serve a distinguere il telefono di un collega dal centralino: con un interno solo
    /// può essere il suo apparecchio, con due diversi è il centralino che non inoltra, e
    /// lì riprovare non serve. Si svuota alla prima consegna riuscita.
    /// </remarks>
    private readonly HashSet<string> _unconfirmedPeers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Se il suggerimento di rivolgersi a chi amministra il centralino è già stato dato.</summary>
    private bool _forwardingAdviceGiven;

    /// <summary>Crea il servizio e lo aggancia al motore.</summary>
    /// <param name="engine">Motore telefonico, possibilmente l'involucro commutabile.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="contacts">Rubrica, per dare un nome a chi scrive; facoltativa.</param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    public InstantMessagingService(
        ITelephonyEngine engine,
        IAnnouncer announcer,
        IContactRepository? contacts = null,
        IClock? clock = null,
        ILogger<InstantMessagingService>? logger = null)
    {
        _engine = engine;
        _announcer = announcer;
        _contacts = contacts;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<InstantMessagingService>.Instance;

        _engine.InstantMessageReceived += OnMessageReceived;
    }

    /// <summary>Raggruppamento delle cifre negli annunci, come lo ha scelto l'operatore.</summary>
    public DigitGrouping DigitGrouping { get; set; } = DigitGrouping.Single;

    /// <summary>
    /// Chiede all'interfaccia dove sta la finestra di una conversazione.
    /// </summary>
    /// <remarks>
    /// Viene chiamata dal thread su cui arriva il messaggio: chi la fornisce deve
    /// occuparsi da sé di passare a quello dell'interfaccia. Senza, ogni conversazione
    /// risulta chiusa.
    /// </remarks>
    public Func<string, ConversationVisibility>? VisibilityOf { get; set; }

    /// <summary>Sollevato quando un messaggio entra in una conversazione, in arrivo o in partenza.</summary>
    public event EventHandler<InstantMessageEventArgs>? MessageAdded;

    /// <summary>Sollevato quando cambia lo stato della consegna di un messaggio mandato.</summary>
    public event EventHandler<InstantMessageEventArgs>? MessageUpdated;

    /// <summary>
    /// Sollevato quando cambia l'elenco delle conversazioni: una nuova, un nome trovato,
    /// un messaggio letto.
    /// </summary>
    public event EventHandler? ConversationsChanged;

    /// <summary>Se il motore in uso sa scambiare messaggi.</summary>
    public bool IsAvailable => _engine.SupportsInstantMessaging;

    /// <summary>Le conversazioni, dalla più recente.</summary>
    public IReadOnlyList<MessageConversation> Conversations
    {
        get
        {
            lock (_gate)
            {
                return [.. _conversations.Values.OrderByDescending(c => c.LastActivity)];
            }
        }
    }

    /// <summary>Messaggi da leggere in tutte le conversazioni.</summary>
    public int UnreadTotal
    {
        get
        {
            lock (_gate)
            {
                return _conversations.Values.Sum(c => c.UnreadCount);
            }
        }
    }

    /// <summary>
    /// Riporta un interno alla forma che fa da chiave: senza spazi, trattini e punti.
    /// </summary>
    /// <param name="peer">Interno o numero come scritto o come arrivato.</param>
    /// <remarks>
    /// «201» scritto nel campo di composizione e «201» arrivato nel <c>From</c> devono
    /// aprire la stessa finestra, e così «02 1234 5678» e «0212345678». Un indirizzo con
    /// lettere — un utente SIP con un nome al posto del numero — si lascia com'è.
    /// </remarks>
    public static string NormalizePeer(string peer)
    {
        var pulito = peer.Trim();
        if (pulito.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) pulito = pulito[4..];

        var senzaSeparatori = new string(pulito.Where(c => c is not (' ' or '-' or '.' or '(' or ')' or '/')).ToArray());

        return senzaSeparatori.Length > 0 && senzaSeparatori.All(c => char.IsDigit(c) || c is '+' or '*' or '#')
            ? senzaSeparatori
            : pulito;
    }

    /// <summary>La conversazione con un interno, se esiste già.</summary>
    /// <param name="peer">Interno o numero.</param>
    public MessageConversation? Find(string peer)
    {
        lock (_gate)
        {
            return _conversations.GetValueOrDefault(NormalizePeer(peer));
        }
    }

    /// <summary>
    /// Apre la conversazione con un interno, creandola se non c'è.
    /// </summary>
    /// <param name="peer">Interno o numero.</param>
    /// <param name="name">
    /// Nome del collega, se chi apre lo conosce già — l'elenco dei colleghi, la rubrica.
    /// Se manca lo si cerca in rubrica.
    /// </param>
    /// <returns>La conversazione.</returns>
    public MessageConversation Open(string peer, string? name = null)
    {
        var (conversazione, nuova) = GetOrCreate(peer, name);

        if (nuova) ConversationsChanged?.Invoke(this, EventArgs.Empty);
        if (conversazione.Name is null) _ = ResolveNameAsync(conversazione);

        return conversazione;
    }

    /// <summary>
    /// Segna come letti i messaggi di una conversazione.
    /// </summary>
    /// <param name="peer">Interno o numero.</param>
    /// <remarks>
    /// La chiama la finestra della conversazione quando diventa attiva: avere davanti la
    /// conversazione è il modo in cui la si legge.
    /// </remarks>
    public void MarkRead(string peer)
    {
        bool cambiato;

        lock (_gate)
        {
            cambiato = _conversations.TryGetValue(NormalizePeer(peer), out var c) && c.ClearUnread();
        }

        if (cambiato) ConversationsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Manda un messaggio a un collega e ne segue la consegna.
    /// </summary>
    /// <param name="peer">Interno o numero del destinatario.</param>
    /// <param name="text">Testo da mandare.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Il messaggio con l'esito della consegna, oppure <c>null</c> se non c'era niente da mandare.</returns>
    /// <remarks>
    /// <para>
    /// Il messaggio entra nella conversazione <b>prima</b> di partire, come «invio in
    /// corso»: chi ha premuto Invio lo ritrova subito in fondo all'elenco, e non lo
    /// riscrive pensando di non averlo mandato.
    /// </para>
    /// <para>
    /// <b>L'esito si dice sempre</b>, e la consegna riuscita in una parola sola. Un invio
    /// muto è indistinguibile da un invio non riuscito: chi non vede lo schermo non ha
    /// altro modo di sapere se il collega l'ha ricevuto.
    /// </para>
    /// </remarks>
    public async Task<InstantMessage?> SendAsync(string peer, string text, CancellationToken cancellationToken = default)
    {
        var testo = text.Trim();

        if (testo.Length == 0)
        {
            _announcer.Announce(Announcement.CommandResponse(InstantMessageTexts.EmptyMessage, _clock.Now));
            return null;
        }

        var (conversazione, nuova) = GetOrCreate(peer, name: null);
        if (nuova) ConversationsChanged?.Invoke(this, EventArgs.Empty);

        var messaggio = new InstantMessage(
            NextId(),
            conversazione.Peer,
            testo,
            InstantMessageDirection.Outgoing,
            _clock.Now,
            InstantMessageDelivery.Sending);

        lock (_gate) conversazione.Add(messaggio);
        MessageAdded?.Invoke(this, new InstantMessageEventArgs(conversazione, messaggio));
        ConversationsChanged?.Invoke(this, EventArgs.Empty);

        InstantMessageSendResult esito;

        try
        {
            esito = _engine.SupportsInstantMessaging
                ? await _engine.SendInstantMessageAsync(conversazione.Peer, testo, cancellationToken).ConfigureAwait(false)
                : InstantMessageSendResult.Failure(InstantMessageTexts.NotSupported);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invio del messaggio a {Destinazione} non riuscito", conversazione.Peer);
            esito = InstantMessageSendResult.Failure(InstantMessageTexts.NotSent);
        }

        var aggiornato = messaggio with
        {
            Delivery = esito switch
            {
                { Delivered: true } => InstantMessageDelivery.Delivered,
                { Unconfirmed: true } => InstantMessageDelivery.Unconfirmed,
                _ => InstantMessageDelivery.Failed,
            },
            FailureReason = esito.Delivered ? null : esito.FailureReason,
        };

        lock (_gate) conversazione.Replace(aggiornato);
        MessageUpdated?.Invoke(this, new InstantMessageEventArgs(conversazione, aggiornato));

        AnnounceOutcome(conversazione.Peer, esito);

        return aggiornato;
    }

    /// <summary>
    /// Dice com'è andato un invio: consegnato, rifiutato, oppure senza conferma.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Senza conferma non si dice «non consegnato»</b>, che affermerebbe una cosa non
    /// saputa: si dice «consegna non confermata», con la causa più probabile.
    /// </para>
    /// <para>
    /// <b>Alla seconda volta con un collega diverso si aggiunge cosa fare</b>, una volta
    /// sola: è il momento in cui si capisce che non è il telefono di qualcuno ma il
    /// centralino, e che la soluzione non sta sulla postazione. Ripeterlo a ogni invio
    /// lo trasformerebbe in un rumore da ignorare.
    /// </para>
    /// </remarks>
    private void AnnounceOutcome(string peer, InstantMessageSendResult esito)
    {
        if (esito.Delivered)
        {
            lock (_gate)
            {
                _unconfirmedPeers.Clear();
                _forwardingAdviceGiven = false;
            }

            _announcer.Announce(Announcement.CommandResponse(InstantMessageTexts.Delivered, _clock.Now));
            return;
        }

        string testo;

        if (esito.Unconfirmed)
        {
            bool consiglia;
            int quanti;

            lock (_gate)
            {
                _unconfirmedPeers.Add(peer);
                quanti = _unconfirmedPeers.Count;
                consiglia = !_forwardingAdviceGiven && quanti >= 2;
                if (consiglia) _forwardingAdviceGiven = true;
            }

            _log.LogInformation(
                "Messaggio a {Destinazione} senza conferma: {Motivo}. Interni senza conferma dall'ultima consegna: {Quanti}",
                peer, esito.FailureReason, quanti);

            testo = InstantMessageTexts.NotConfirmed(esito.FailureReason);
            if (consiglia) testo = $"{testo}. {InstantMessageTexts.ForwardingAdvice}";
        }
        else
        {
            _log.LogInformation("Messaggio a {Destinazione} non consegnato: {Motivo}", peer, esito.FailureReason);
            testo = InstantMessageTexts.NotDelivered(esito.FailureReason);
        }

        _announcer.Announce(Announcement.CommandResponse(testo, _clock.Now));
        _announcer.PlayEarcon(EarconKind.CommandRejected);
    }

    // -----------------------------------------------------------------------
    // Messaggi in arrivo
    // -----------------------------------------------------------------------

    private void OnMessageReceived(object? sender, InstantMessageReceivedEventArgs e)
    {
        try
        {
            var (conversazione, nuova) = GetOrCreate(e.From, e.FromName);

            var messaggio = new InstantMessage(
                NextId(),
                conversazione.Peer,
                e.Text,
                InstantMessageDirection.Incoming,
                e.At);

            // Dove sta la finestra si chiede PRIMA di contare il messaggio fra quelli da
            // leggere: con la conversazione davanti, l'operatore lo sta leggendo adesso.
            var dove = VisibilityOf?.Invoke(conversazione.Peer) ?? ConversationVisibility.Closed;

            lock (_gate)
            {
                conversazione.Add(messaggio);
                if (dove != ConversationVisibility.InFront) conversazione.AddUnread();
            }

            MessageAdded?.Invoke(this, new InstantMessageEventArgs(conversazione, messaggio));
            ConversationsChanged?.Invoke(this, EventArgs.Empty);

            lock (_gate)
            {
                _announcing = AnnounceAfterAsync(_announcing, conversazione, messaggio, dove, nuova);
            }
        }
        catch (Exception ex)
        {
            // Il messaggio e' gia' stato accettato verso il mittente: qui si puo' solo
            // lasciarne traccia, non farlo risalire nel trasporto SIP.
            _log.LogError(ex, "Messaggio istantaneo da {Da} non mostrato", e.From);
        }
    }

    /// <summary>
    /// Annuncia un messaggio arrivato, dopo quelli arrivati prima.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Con la conversazione davanti</b> si dice chi e cosa, senza «messaggio da», e si
    /// dice anche durante una telefonata: l'operatore sta scrivendo proprio a quel
    /// collega, e la risposta è la cosa che aspetta.
    /// </para>
    /// <para>
    /// <b>Altrimenti</b> l'annuncio rispetta il silenzio della conversazione come ogni
    /// altra notizia non urgente: durante una telefonata passa il solo segnale acustico,
    /// e il messaggio resta fra quelli da leggere.
    /// </para>
    /// </remarks>
    private async Task AnnounceAfterAsync(
        Task previous,
        MessageConversation conversazione,
        InstantMessage messaggio,
        ConversationVisibility dove,
        bool nuova)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // Un annuncio precedente non riuscito non deve fermare i successivi.
        }

        try
        {
            if (conversazione.Name is null) await ResolveNameAsync(conversazione).ConfigureAwait(false);
            else if (nuova) ConversationsChanged?.Invoke(this, EventArgs.Empty);

            var nome = conversazione.Name;
            var adesso = _clock.Now;

            var annuncio = dove == ConversationVisibility.InFront
                ? new Announcement
                {
                    Text = InstantMessageTexts.IncomingInChat(nome, messaggio.Peer, messaggio.Text, DigitGrouping),
                    Priority = AnnouncementPriority.High,
                    Earcon = EarconKind.InstantMessageReceived,
                    BrailleText = InstantMessageTexts.IncomingBraille(nome, messaggio.Peer, messaggio.Text),
                    BypassConversationSilence = true,
                    CreatedAt = adesso,
                }
                : Announcement.High(
                    InstantMessageTexts.Incoming(nome, messaggio.Peer, messaggio.Text, DigitGrouping),
                    adesso,
                    EarconKind.InstantMessageReceived,
                    braille: InstantMessageTexts.IncomingBraille(nome, messaggio.Peer, messaggio.Text));

            _announcer.Announce(annuncio);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Annuncio del messaggio istantaneo non riuscito");
        }
    }

    // -----------------------------------------------------------------------
    // Interni
    // -----------------------------------------------------------------------

    private (MessageConversation Conversation, bool Created) GetOrCreate(string peer, string? name)
    {
        var chiave = NormalizePeer(peer);
        var nome = CallParty.MeaningfulNameOf(name, chiave);

        lock (_gate)
        {
            if (_conversations.TryGetValue(chiave, out var esistente))
            {
                if (nome is not null && esistente.Name is null) esistente.SetName(nome);
                return (esistente, false);
            }

            var nuova = new MessageConversation(chiave, nome, _clock.Now, _gate);
            _conversations[chiave] = nuova;

            _log.LogInformation("Conversazione a messaggi aperta con {Interno}", chiave);
            return (nuova, true);
        }
    }

    /// <summary>Cerca in rubrica il nome di chi non si è presentato.</summary>
    private async Task ResolveNameAsync(MessageConversation conversazione)
    {
        if (_contacts is null) return;

        try
        {
            var trovati = await _contacts.ResolveByNumberAsync(conversazione.Peer, CancellationToken.None)
                .ConfigureAwait(false);

            var nome = trovati.Select(c => c.NameOrDescription).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            if (nome is null) return;

            lock (_gate)
            {
                if (conversazione.Name is not null) return;
                conversazione.SetName(nome);
            }

            ConversationsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Senza nome la conversazione resta intestata al numero: e' meno comodo,
            // non e' un guasto.
            _log.LogDebug(ex, "Nome dell'interno {Interno} non trovato in rubrica", conversazione.Peer);
        }
    }

    private string NextId() => $"im-{Interlocked.Increment(ref _counter)}";

    /// <inheritdoc />
    public void Dispose() => _engine.InstantMessageReceived -= OnMessageReceived;
}
