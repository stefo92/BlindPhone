using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Tiene acceso il segnale che avverte i presenti dell'ascolto dell'operatore.
/// </summary>
/// <remarks>
/// <para>
/// <b>È il pezzo che rende lecita l'inclusione.</b> Entrare in una conversazione fra
/// due persone è una cosa che si può fare solo se loro lo sanno; il segnale periodico
/// è il modo in cui glielo si dice, e questa classe esiste per garantire che parta e
/// che continui finché l'operatore resta dentro. Se smettesse a metà, l'inclusione
/// diventerebbe un ascolto silenzioso.
/// </para>
/// <para>
/// <b>Il primo segnale parte subito, non dopo cinque secondi.</b> È il momento in cui
/// serve di più: se l'avviso arrivasse alla prima scadenza, l'operatore avrebbe già
/// ascoltato cinque secondi di conversazione senza che nessuno lo sapesse.
/// </para>
/// <para>
/// <b>Va nella linea, non nella cuffia.</b> Un avviso che sente solo chi si è incluso
/// non avverte nessuno. Viene immesso in trasmissione, quindi lo ricevono l'interno e
/// il suo interlocutore — che sono le persone che hanno diritto di saperlo.
/// </para>
/// <para>
/// <b>Non c'è nessun modo di spegnerlo.</b> Non esiste impostazione, non esiste
/// parametro, e la durata e la cadenza sono costanti in
/// <see cref="IntrusionTone"/>. Chi volesse renderle configurabili si fermi a
/// chiedersi a cosa servirebbe il valore «mai».
/// </para>
/// </remarks>
public sealed class IntrusionMonitor : IDisposable
{
    /// <summary>Frequenza di campionamento del segnale generato.</summary>
    /// <remarks>
    /// Banda stretta: è la frequenza che ogni codec telefonico regge, e il percorso
    /// audio la riporta comunque a quella del codec negoziato.
    /// </remarks>
    private const int SampleRate = 8000;

    private readonly ITelephonyEngine _telephony;
    private readonly CallSessionManager _calls;
    private readonly IAnnouncer _announcer;
    private readonly IClock _clock;
    private readonly ILogger<IntrusionMonitor> _log;

    private readonly Lock _gate = new();
    private readonly short[] _tono = IntrusionTone.Render(SampleRate);

    private string? _callId;
    private string? _extension;
    private DateTimeOffset _lastTone = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>Crea il sorvegliante.</summary>
    /// <param name="telephony">Motore telefonico, per immettere il segnale in linea.</param>
    /// <param name="calls">Gestore delle chiamate, per accorgersi che l'inclusione è finita.</param>
    /// <param name="announcer">Voce del posto operatore.</param>
    /// <param name="clock">Orologio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public IntrusionMonitor(
        ITelephonyEngine telephony,
        CallSessionManager calls,
        IAnnouncer announcer,
        IClock? clock = null,
        ILogger<IntrusionMonitor>? logger = null)
    {
        _telephony = telephony;
        _calls = calls;
        _announcer = announcer;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<IntrusionMonitor>.Instance;

        _calls.CallStateChanged += OnCallStateChanged;
    }

    /// <summary>Chiamata su cui l'inclusione è in corso, se ce n'è una.</summary>
    public string? CallId
    {
        get { lock (_gate) return _callId; }
    }

    /// <summary>Indica se un'inclusione è attiva.</summary>
    public bool IsActive => CallId is not null;

    /// <summary>
    /// Dichiara che una chiamata è un'inclusione e fa partire il segnale.
    /// </summary>
    /// <param name="callId">Chiamata appena aperta verso l'interno occupato.</param>
    /// <param name="extension">Interno in cui ci si è inclusi.</param>
    public void Begin(string callId, string extension)
    {
        lock (_gate)
        {
            _callId = callId;
            _extension = extension;

            // Il primo segnale deve partire alla prima passata, non fra cinque
            // secondi: e' l'istante in cui la conversazione viene raggiunta.
            _lastTone = DateTimeOffset.MinValue;
        }

        _log.LogInformation(
            "Inclusione avviata sull'interno {Extension} (chiamata {CallId}): segnale di ascolto "
            + "ogni {Intervallo} secondi",
            extension, callId, IntrusionTone.Interval.TotalSeconds);

        if (_tono.Length == 0)
        {
            // Non dovrebbe succedere — il segnale e' generato, non caricato da un file
            // — ma se succedesse l'operatore starebbe ascoltando senza che nessuno lo
            // sappia, e questa e' l'unica cosa che non deve accadere in silenzio.
            _log.LogError("Segnale di inclusione non generato: l'ascolto non viene dichiarato ai presenti");

            _announcer.Announce(Announcement.High(
                "Attenzione: il segnale che avverte i presenti non e' partito. Dichiara a voce la tua "
                + "presenza oppure chiudi l'inclusione",
                _clock.Now,
                EarconKind.CommandRejected));
        }
    }

    /// <summary>Dichiara finita l'inclusione.</summary>
    public void End()
    {
        string? chiusa;

        lock (_gate)
        {
            chiusa = _callId;
            _callId = null;
            _extension = null;
        }

        if (chiusa is not null) _log.LogInformation("Inclusione conclusa (chiamata {CallId})", chiusa);
    }

    /// <summary>
    /// Fa avanzare il segnale periodico.
    /// </summary>
    /// <param name="now">Istante corrente; se omesso si usa l'orologio iniettato.</param>
    /// <remarks>
    /// Richiamato dallo stesso ciclo che alimenta gli annunci e il promemoria delle
    /// chiamate in attesa: una sorgente di tempo sola per tutto ciò che nel prodotto
    /// si ripete.
    /// </remarks>
    public void Pump(DateTimeOffset? now = null)
    {
        var istante = now ?? _clock.Now;

        string? callId;

        lock (_gate)
        {
            if (_callId is null) return;
            if (istante - _lastTone < IntrusionTone.Interval) return;

            callId = _callId;
            _lastTone = istante;
        }

        if (_tono.Length == 0) return;

        try
        {
            _telephony.PlayToRemote(callId, _tono, SampleRate);
        }
        catch (Exception ex)
        {
            // Un percorso audio che rifiuta il segnale non deve far cadere il ciclo
            // che alimenta anche gli annunci: si registra e si riprova alla scadenza
            // successiva.
            _log.LogWarning(ex, "Segnale di inclusione non immesso in linea");
        }
    }

    /// <summary>
    /// Chiude l'inclusione quando la chiamata finisce, da qualunque parte sia decisa.
    /// </summary>
    /// <remarks>
    /// L'operatore può chiuderla con il tasto di riaggancio, l'interno può chiudere la
    /// conversazione, il centralino può sciogliere tutto: sono tre strade diverse per
    /// lo stesso fatto, e il segnale deve smettere in tutte e tre. Ascoltare da qui
    /// invece che dal comando è ciò che le copre insieme.
    /// </remarks>
    private void OnCallStateChanged(object? sender, CallStateChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_callId != e.Call.Id) return;
            if (!CallStateMachine.IsTerminal(e.Transition.To)) return;

            _callId = null;
            _extension = null;
        }

        _log.LogInformation("Inclusione chiusa insieme alla chiamata {CallId}", e.Call.Id);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _calls.CallStateChanged -= OnCallStateChanged;
    }
}
