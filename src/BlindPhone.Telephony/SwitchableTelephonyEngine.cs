using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Telephony;

/// <summary>
/// Motore telefonico che può cambiare account senza essere ricostruito.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il problema che risolve.</b> Il motore telefonico è un singleton, e mezzo
/// prodotto vi si aggancia: il gestore delle chiamate, l'orchestratore del
/// trasferimento, la sorveglianza degli interni, la finestra principale, il pannello
/// delle impostazioni. Cambiare account significa buttare via il motore e costruirne
/// un altro, e senza un intermediario significherebbe anche riagganciare a mano
/// tutti quei sottoscrittori — oppure riavviare l'applicazione, che su un telefono
/// di servizio vuol dire smettere di essere raggiungibili per il tempo di un avvio.
/// </para>
/// <para>
/// Questo involucro tiene ferma la superficie: i sottoscrittori restano agganciati
/// qui e non sanno che sotto il motore è cambiato. È la stessa idea per cui il
/// numero di telefono di un ufficio non cambia quando cambia la persona che
/// risponde.
/// </para>
/// <para>
/// <b>Cosa NON fa.</b> Non salva le chiamate in corso: cambiare account chiude la
/// registrazione precedente, e con essa qualunque conversazione. Chi chiama il
/// cambio deve verificarlo prima e chiedere conferma, ed è il motivo per cui
/// <see cref="SwitchAsync"/> rifiuta di procedere a linea occupata invece di
/// decidere da solo.
/// </para>
/// </remarks>
public sealed class SwitchableTelephonyEngine : ITelephonyEngine
{
    private readonly Func<SipAccountOptions?, ITelephonyEngine> _factory;
    private readonly ILogger<SwitchableTelephonyEngine> _log;
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    private ITelephonyEngine _inner;
    private CancellationToken _startToken = CancellationToken.None;
    private bool _started;

    /// <summary>
    /// Se la postazione è in pausa, cioè se rifiuta le chiamate entranti.
    /// </summary>
    /// <remarks>
    /// Vive qui e non solo nel motore sottostante perché deve sopravvivere al cambio
    /// di account: un motore appena costruito nasce in servizio, e senza riapplicare
    /// la pausa la postazione ricomincerebbe a ricevere chiamate mentre l'operatore
    /// è ancora via, senza che nulla glielo dica.
    /// </remarks>
    private bool _doNotDisturb;

    /// <summary>Crea l'involucro attorno a un primo motore.</summary>
    /// <param name="factory">
    /// Costruisce un motore per l'account indicato. Riceve <c>null</c> quando non c'è
    /// alcun account utilizzabile, e in quel caso deve restituire il motore simulato:
    /// un posto operatore senza account deve comunque partire, perché è da lì che
    /// l'operatore raggiunge le impostazioni per configurarlo.
    /// </param>
    /// <param name="account">Account iniziale, oppure <c>null</c> per la modalità simulata.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SwitchableTelephonyEngine(
        Func<SipAccountOptions?, ITelephonyEngine> factory,
        SipAccountOptions? account,
        ILogger<SwitchableTelephonyEngine>? logger = null)
    {
        _factory = factory;
        _log = logger ?? NullLogger<SwitchableTelephonyEngine>.Instance;

        _inner = factory(account);
        CurrentAccount = account;

        Attach(_inner);
    }

    /// <summary>Account attualmente in uso, <c>null</c> in modalità simulata.</summary>
    public SipAccountOptions? CurrentAccount { get; private set; }

    /// <summary>Sollevato dopo un cambio di account riuscito.</summary>
    public event EventHandler<SipAccountOptions?>? AccountChanged;

    /// <summary>
    /// Sostituisce il motore con uno registrato su un altro account.
    /// </summary>
    /// <param name="account">Nuovo account, oppure <c>null</c> per la modalità simulata.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns><c>true</c> se il cambio è riuscito.</returns>
    /// <remarks>
    /// <para>
    /// L'ordine conta e non è reversibile: si sgancia, si ferma, si costruisce, si
    /// aggancia, si avvia. Agganciare prima di avviare è ciò che garantisce di non
    /// perdere l'evento di registrazione del nuovo account, che è il primo che arriva
    /// e l'unico che l'operatore stia aspettando.
    /// </para>
    /// <para>
    /// Se il nuovo motore non parte si torna indietro sul precedente. Un cambio
    /// fallito che lasciasse la postazione senza motore la renderebbe muta senza
    /// alcun modo di rimediare se non riavviando.
    /// </para>
    /// </remarks>
    public async Task<bool> SwitchAsync(SipAccountOptions? account, CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var precedente = _inner;
        var precedenteAccount = CurrentAccount;

        try
        {
            _log.LogInformation(
                "Cambio account SIP: da {Prima} a {Dopo}",
                precedenteAccount?.Name ?? "simulato",
                account?.Name ?? "simulato");

            Detach(precedente);

            try
            {
                await precedente.StopAsync(cancellationToken).ConfigureAwait(false);
                await precedente.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Il motore precedente che non si chiude bene non deve impedire al
                // nuovo di partire: al massimo resta una registrazione che scade da
                // sola presso il centralino.
                _log.LogWarning(ex, "Chiusura del motore precedente non riuscita");
            }

            var nuovo = _factory(account);
            Attach(nuovo);

            // La pausa segue l'operatore, non il motore: vedi _doNotDisturb.
            nuovo.SetDoNotDisturb(_doNotDisturb);

            _inner = nuovo;
            CurrentAccount = account;

            if (_started)
            {
                await nuovo.StartAsync(_startToken).ConfigureAwait(false);
            }

            AccountChanged?.Invoke(this, account);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cambio dell'account SIP non riuscito: si torna al precedente");

            try
            {
                // Il motore precedente e' gia' stato fermato e smaltito: si ricostruisce
                // sul suo account invece di riusarne l'istanza.
                var recuperato = _factory(precedenteAccount);
                Attach(recuperato);
                recuperato.SetDoNotDisturb(_doNotDisturb);

                _inner = recuperato;
                CurrentAccount = precedenteAccount;

                if (_started) await recuperato.StartAsync(_startToken).ConfigureAwait(false);
            }
            catch (Exception recovery)
            {
                _log.LogError(recovery, "Ripristino dell'account precedente non riuscito");
            }

            return false;
        }
        finally
        {
            _switchGate.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Inoltro degli eventi
    // -----------------------------------------------------------------------

    private void Attach(ITelephonyEngine engine)
    {
        engine.RegistrationStateChanged += OnRegistrationStateChanged;
        engine.IncomingCall += OnIncomingCall;
        engine.CallProgress += OnCallProgress;
        engine.TransferProgress += OnTransferProgress;
        engine.SrtpDowngraded += OnSrtpDowngraded;
    }

    private void Detach(ITelephonyEngine engine)
    {
        engine.RegistrationStateChanged -= OnRegistrationStateChanged;
        engine.IncomingCall -= OnIncomingCall;
        engine.CallProgress -= OnCallProgress;
        engine.TransferProgress -= OnTransferProgress;
        engine.SrtpDowngraded -= OnSrtpDowngraded;
    }

    // Il mittente riportato ai sottoscrittori e' questo involucro e non il motore
    // interno: chi si e' agganciato qui non deve poter risalire a un'istanza che
    // domani non esistera' piu'.
    private void OnRegistrationStateChanged(object? sender, SipRegistrationState e)
        => RegistrationStateChanged?.Invoke(this, e);

    private void OnIncomingCall(object? sender, IncomingCallEventArgs e)
        => IncomingCall?.Invoke(this, e);

    private void OnCallProgress(object? sender, CallProgressEventArgs e)
        => CallProgress?.Invoke(this, e);

    private void OnTransferProgress(object? sender, TransferProgressEventArgs e)
        => TransferProgress?.Invoke(this, e);

    private void OnSrtpDowngraded(object? sender, string e) => SrtpDowngraded?.Invoke(this, e);

    /// <inheritdoc />
    public event EventHandler<SipRegistrationState>? RegistrationStateChanged;

    /// <inheritdoc />
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <inheritdoc />
    public event EventHandler<string>? SrtpDowngraded;

    /// <inheritdoc />
    public event EventHandler<CallProgressEventArgs>? CallProgress;

    /// <inheritdoc />
    public event EventHandler<TransferProgressEventArgs>? TransferProgress;

    // -----------------------------------------------------------------------
    // Inoltro del contratto
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public SipRegistrationState RegistrationState => _inner.RegistrationState;

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticCounter> DiagnosticCounters => _inner.DiagnosticCounters;

    /// <inheritdoc />
    public IPresenceSource? PresenceSource => _inner.PresenceSource;

    /// <inheritdoc />
    public IReachabilityProbe? ReachabilityProbe => _inner.ReachabilityProbe;

    /// <inheritdoc />
    public bool IsMuted => _inner.IsMuted;

    /// <inheritdoc />
    public bool IsSimulated => _inner.IsSimulated;

    /// <inheritdoc />
    public bool SupportsConference => _inner.SupportsConference;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = true;
        _startToken = cancellationToken;
        return _inner.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _started = false;
        return _inner.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> DialAsync(string destination, CancellationToken cancellationToken)
        => _inner.DialAsync(destination, cancellationToken);

    /// <inheritdoc />
    public Task<string> BargeInAsync(DialogReference dialog, CancellationToken cancellationToken)
        => _inner.BargeInAsync(dialog, cancellationToken);

    /// <inheritdoc />
    public Task AnswerAsync(string callId, CancellationToken cancellationToken)
        => _inner.AnswerAsync(callId, cancellationToken);

    /// <inheritdoc />
    public Task HangUpAsync(string callId, CancellationToken cancellationToken)
        => _inner.HangUpAsync(callId, cancellationToken);

    /// <inheritdoc />
    public Task HoldAsync(string callId, CancellationToken cancellationToken)
        => _inner.HoldAsync(callId, cancellationToken);

    /// <inheritdoc />
    public Task ResumeAsync(string callId, CancellationToken cancellationToken)
        => _inner.ResumeAsync(callId, cancellationToken);

    /// <inheritdoc />
    public Task BlindTransferAsync(string callId, string destination, CancellationToken cancellationToken)
        => _inner.BlindTransferAsync(callId, destination, cancellationToken);

    /// <inheritdoc />
    public Task AttendedTransferAsync(string callId, string consultationCallId, CancellationToken cancellationToken)
        => _inner.AttendedTransferAsync(callId, consultationCallId, cancellationToken);

    /// <inheritdoc />
    public Task StartConferenceAsync(IReadOnlyList<string> callIds, CancellationToken cancellationToken)
        => _inner.StartConferenceAsync(callIds, cancellationToken);

    /// <inheritdoc />
    public Task StopConferenceAsync(CancellationToken cancellationToken)
        => _inner.StopConferenceAsync(cancellationToken);

    /// <inheritdoc />
    public Task SendDtmfAsync(string callId, char digit, CancellationToken cancellationToken)
        => _inner.SendDtmfAsync(callId, digit, cancellationToken);

    /// <inheritdoc />
    public void SetMuted(bool muted) => _inner.SetMuted(muted);

    /// <inheritdoc />
    public bool IsDoNotDisturb => _inner.IsDoNotDisturb;

    /// <inheritdoc />
    public bool IsSignallingEncrypted => _inner.IsSignallingEncrypted;

    /// <inheritdoc />
    public bool StartCallRecording(string callId, string path) => _inner.StartCallRecording(callId, path);

    /// <inheritdoc />
    public (TimeSpan Duration, Task<string> File)? StopCallRecording(string callId)
        => _inner.StopCallRecording(callId);

    /// <inheritdoc />
    public void PlayToRemote(string callId, short[] pcm, int sampleRate)
        => _inner.PlayToRemote(callId, pcm, sampleRate);

    /// <inheritdoc />
    /// <remarks>
    /// Lo stato viene ricordato qui e riapplicato al motore che subentra: cambiare
    /// account SIP mentre la postazione è in pausa non deve rimetterla in servizio
    /// senza che nessuno lo dica.
    /// </remarks>
    public void SetDoNotDisturb(bool enabled)
    {
        _doNotDisturb = enabled;
        _inner.SetDoNotDisturb(enabled);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Detach(_inner);
        await _inner.DisposeAsync().ConfigureAwait(false);
        _switchGate.Dispose();
    }
}
