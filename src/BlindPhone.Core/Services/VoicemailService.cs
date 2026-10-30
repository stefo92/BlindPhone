using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Segue la segreteria telefonica dell'account in uso e avvisa l'operatore dei
/// messaggi nuovi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il problema che risolve.</b> Su un telefono da tavolo i messaggi in segreteria si
/// vedono: c'è una spia rossa che lampeggia. Un operatore che non vede quella spia non
/// ce l'ha, e senza un avviso a voce scopre i messaggi lasciati di notte soltanto se si
/// ricorda di chiamare la casella. Qui la spia diventa una frase, detta una volta.
/// </para>
/// <para>
/// <b>Una volta, e non a ogni notifica.</b> Il centralino rimanda lo stato della
/// casella a ogni rinnovo della sottoscrizione, cioè ogni pochi minuti, anche quando
/// non è cambiato niente. Ripetere «due nuovi messaggi» a ogni rinnovo trasformerebbe
/// l'avviso in un rumore di fondo che si impara a ignorare, che è il modo in cui un
/// avviso smette di funzionare. Si parla solo quando i messaggi nuovi aumentano: vedi
/// <see cref="ShouldAnnounce"/>.
/// </para>
/// <para>
/// <b>Quello che non dice.</b> Che i messaggi sono stati ascoltati: quando il numero
/// scende l'operatore ha appena chiamato la casella e lo sa già, e la riga di stato
/// nella finestra si aggiorna in silenzio.
/// </para>
/// </remarks>
public sealed class VoicemailService : IDisposable
{
    private readonly ITelephonyEngine _telephony;
    private readonly IAnnouncer _announcer;
    private readonly ILogger<VoicemailService> _log;
    private readonly Lock _gate = new();

    private string? _account;
    private MessageSummary? _current;
    private bool _disposed;

    /// <summary>Crea il servizio e lo aggancia al motore telefonico.</summary>
    /// <param name="telephony">Motore telefonico, che riporta le notifiche del centralino.</param>
    /// <param name="announcer">Pipeline degli annunci.</param>
    /// <param name="logger">Log diagnostico.</param>
    public VoicemailService(
        ITelephonyEngine telephony,
        IAnnouncer announcer,
        ILogger<VoicemailService>? logger = null)
    {
        _telephony = telephony;
        _announcer = announcer;
        _log = logger ?? NullLogger<VoicemailService>.Instance;

        _telephony.MessageWaitingChanged += OnMessageWaitingChanged;
        _telephony.RegistrationStateChanged += OnRegistrationStateChanged;
    }

    /// <summary>
    /// Ultimo stato della casella riportato dal centralino, oppure <c>null</c> se
    /// per l'account in uso non è ancora arrivato niente.
    /// </summary>
    /// <remarks>
    /// <c>null</c> non vuol dire «nessun messaggio»: vuol dire che non si sa, perché il
    /// centralino non ha ancora parlato o non manda questi avvisi. La finestra lo
    /// distingue, e non mostra una riga che affermi qualcosa che non si sa.
    /// </remarks>
    public MessageSummary? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    /// <summary>Account a cui si riferisce <see cref="Current"/>.</summary>
    public string? AccountName
    {
        get
        {
            lock (_gate) return _account;
        }
    }

    /// <summary>Sollevato quando lo stato della casella cambia, per aggiornare la finestra.</summary>
    /// <remarks>
    /// Arriva dal thread della segnalazione SIP: chi aggiorna l'interfaccia deve
    /// tornare sul proprio.
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>
    /// Numero della segreteria che il centralino indica per un account.
    /// </summary>
    /// <param name="accountName">Account di cui si chiede, come lo chiama la configurazione.</param>
    /// <param name="ownExtension">Interno dell'account, che non va mai proposto.</param>
    /// <returns>Il numero da proporre, oppure <c>null</c>.</returns>
    /// <remarks>
    /// Solo per l'account in uso: degli altri il centralino non ha detto niente, e
    /// proporre il codice di un ente nella scheda dell'altro sarebbe un errore servito
    /// già pronto.
    /// </remarks>
    public string? ProposedNumber(string accountName, string? ownExtension)
    {
        MessageSummary? attuale;

        lock (_gate)
        {
            if (!string.Equals(_account, accountName, StringComparison.Ordinal)) return null;
            attuale = _current;
        }

        return VoicemailNumbers.ProposalFrom(attuale?.MessageAccount, ownExtension);
    }

    /// <summary>
    /// Decide se una notifica merita un annuncio.
    /// </summary>
    /// <param name="previous">Stato precedente dello stesso account, <c>null</c> se è il primo.</param>
    /// <param name="next">Stato appena arrivato.</param>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>
    ///   <b>Nessun messaggio nuovo</b>: silenzio, sempre.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Prima notizia, o da nessuno a qualcuno</b>: si annuncia. Vale anche
    ///   all'avvio della postazione, quando il centralino riporta i messaggi lasciati
    ///   durante la notte: è il momento in cui l'avviso serve di più.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Il numero cresce</b>: si annuncia, perché è arrivato un messaggio.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Il numero è uguale o scende</b>: silenzio. È il rinnovo della
    ///   sottoscrizione, oppure l'operatore sta ascoltando i messaggi.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Prima o dopo «ci sono, ma non si sa quanti»</b>: silenzio. Si è già detto
    ///   che ci sono messaggi, e un numero che compare o scompare non dice che ne sia
    ///   arrivato uno.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static bool ShouldAnnounce(MessageSummary? previous, MessageSummary next)
    {
        var dopo = next.NewToAnnounce;
        if (dopo == 0) return false;

        var prima = previous is null ? 0 : previous.NewToAnnounce;
        if (prima == 0) return true;

        if (prima is null || dopo is null) return false;

        return dopo > prima;
    }

    private void OnMessageWaitingChanged(object? sender, MessageWaitingEventArgs e)
    {
        bool annuncia;
        bool cambiato;

        lock (_gate)
        {
            if (_disposed) return;

            var precedente = string.Equals(_account, e.AccountName, StringComparison.Ordinal) ? _current : null;

            annuncia = ShouldAnnounce(precedente, e.Summary);
            cambiato = precedente != e.Summary;

            _account = e.AccountName;
            _current = e.Summary;
        }

        if (cambiato)
        {
            _log.LogInformation(
                "Segreteria dell'account {Account}: spia {Spia}, nuovi {Nuovi}, gia' ascoltati {Vecchi}, "
                + "urgenti nuovi {Urgenti}. Annuncio: {Annuncio}",
                e.AccountName,
                e.Summary.MessagesWaiting ? "accesa" : "spenta",
                e.Summary.NewMessages?.ToString() ?? "non indicati",
                e.Summary.OldMessages?.ToString() ?? "non indicati",
                e.Summary.UrgentNewMessages?.ToString() ?? "non indicati",
                annuncia ? "si'" : "no");
        }
        else
        {
            // E' il caso di ogni rinnovo: tenerlo a livello di debug evita una riga
            // ogni dieci minuti per tutto il turno, che non direbbe niente di nuovo.
            _log.LogDebug("Segreteria dell'account {Account}: notifica identica alla precedente", e.AccountName);
        }

        if (annuncia)
        {
            _announcer.Announce(Announcement.Normal(
                AnnouncementTexts.VoicemailArrived(e.Summary),
                e.At,
                coalesceKey: "voicemail"));
        }

        if (cambiato) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Dimentica la casella quando la postazione passa a un altro account.
    /// </summary>
    /// <remarks>
    /// Dopo un cambio di account la riga di stato continuerebbe a mostrare i messaggi
    /// dell'ente di prima, cioè una casella che l'operatore non può più ascoltare con
    /// il tasto della segreteria. Meglio una riga vuota finché il centralino del nuovo
    /// account non dice la sua.
    /// </remarks>
    private void OnRegistrationStateChanged(object? sender, SipRegistrationState e)
    {
        lock (_gate)
        {
            if (_account is null || string.Equals(_account, e.AccountName, StringComparison.Ordinal)) return;

            _account = null;
            _current = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _telephony.MessageWaitingChanged -= OnMessageWaitingChanged;
        _telephony.RegistrationStateChanged -= OnRegistrationStateChanged;
    }
}
