using BlindPhone.Accessibility;
using BlindPhone.Audio;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using BlindPhone.Pbx.Yeastar;
using BlindPhone.Telephony;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Porta le impostazioni salvate dentro i servizi già avviati, senza riavviare.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il problema che risolve.</b> Molti servizi ricevevano la configurazione una volta
/// sola, alla costruzione, e il pannello chiedeva di riavviare a ogni salvataggio. Su un
/// posto operatore il riavvio non è gratuito: fa cadere la chiamata in corso e lascia la
/// postazione irraggiungibile per il tempo di un avvio. E una domanda che compare
/// sempre, anche dopo aver cambiato solo il tema, insegna a rispondere «No» senza
/// leggerla.
/// </para>
/// <para>
/// <b>Un punto solo</b>, e non un gestore per servizio: le regole su che cosa si può
/// cambiare subito e che cosa va rimandato a fine chiamata stanno insieme, e chi
/// aggiunge un'impostazione trova qui dove portarla.
/// </para>
/// <list type="bullet">
///   <item><description>
///   Annunci, trasferimento, segnali acustici, suoneria, voce, dispositivi audio,
///   conservazione dei registri, riallineamento della rubrica: subito.
///   </description></item>
///   <item><description>
///   Collegamento all'API del centralino: subito, sostituendo il connettore. Non tocca
///   le chiamate, perché porta la coda e lo stato degli interni, non l'audio.
///   </description></item>
///   <item><description>
///   Account SIP e regolazione del buffer audio: ricostruendo il motore telefonico, che
///   chiude la registrazione e con essa ogni conversazione. Per questo <b>mai con una
///   chiamata in corso</b>: la modifica aspetta che la linea sia libera, e lo dice.
///   </description></item>
/// </list>
/// </remarks>
public sealed class LiveSettingsApplier : IDisposable
{
    /// <summary>Elenco vuoto condiviso, per confrontare le opzioni senza i loro elenchi.</summary>
    private static readonly string[] NessunCodec = [];

    private static readonly int[] NessunaCoda = [];

    private readonly IServiceProvider _services;
    private readonly SettingsService _settingsService;
    private readonly ILogger<LiveSettingsApplier> _log;

    /// <summary>Una modifica di account o di centralino alla volta, nell'ordine in cui arrivano.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppSettings _applied = new();
    private YeastarOptions? _pbxApplied;
    private AudioTuningOptions _tuningApplied = new();

    /// <summary>Account su cui gira il motore telefonico, oppure <c>null</c> se simulato.</summary>
    private string? _engineAccountId;

    /// <summary>C'è una modifica dell'account SIP che aspetta la fine della chiamata.</summary>
    private volatile bool _sipPending;

    private bool _started;

    /// <summary>Crea il servizio.</summary>
    /// <param name="services">
    /// Contenitore da cui prendere i servizi da aggiornare. Sono una quindicina, e
    /// prenderli dal costruttore li farebbe nascere tutti insieme a questo.
    /// </param>
    /// <param name="settingsService">Configurazione, di cui seguire i salvataggi.</param>
    /// <param name="logger">Log diagnostico.</param>
    public LiveSettingsApplier(
        IServiceProvider services,
        SettingsService settingsService,
        ILogger<LiveSettingsApplier> logger)
    {
        _services = services;
        _settingsService = settingsService;
        _log = logger;
    }

    // Presi all'avvio e non a ogni uso: la chiusura arriva quando il contenitore
    // potrebbe essere gia' smontato.
    private SwitchableTelephonyEngine Engine => _engine ?? throw new InvalidOperationException("Servizio non avviato");

    private CallSessionManager Calls => _calls ?? throw new InvalidOperationException("Servizio non avviato");

    private SwitchableTelephonyEngine? _engine;
    private CallSessionManager? _calls;

    private IAnnouncer Announcer => _services.GetRequiredService<IAnnouncer>();

    /// <summary>
    /// Comincia a seguire i salvataggi, prendendo come punto di partenza la
    /// configurazione con cui i servizi sono stati costruiti.
    /// </summary>
    /// <remarks>
    /// <b>Quella con cui sono stati costruiti, non quella corrente.</b> Il pacchetto
    /// dell'amministratore si applica all'avvio dopo la costruzione dei servizi, che
    /// tengono la configurazione di prima: confrontando con la corrente, la differenza
    /// resterebbe fuori fino al salvataggio successivo. Così invece si applica qui.
    /// </remarks>
    public void Start()
    {
        if (_started) return;
        _started = true;

        _engine = _services.GetRequiredService<SwitchableTelephonyEngine>();
        _calls = _services.GetRequiredService<CallSessionManager>();

        var costruiti = _services.GetRequiredService<AppSettings>();

        _applied = costruiti;
        _pbxApplied = App.BuildPbxConnection(_settingsService, costruiti);
        _tuningApplied = App.BuildAudioTuning(costruiti);
        _engineAccountId = IdOf(Engine.CurrentAccount, costruiti);

        _settingsService.SettingsChanged += OnSettingsChanged;
        Engine.AccountChanged += OnAccountChanged;
        Calls.CallStateChanged += OnCallStateChanged;

        if (!ReferenceEquals(costruiti, _settingsService.Current)) OnSettingsChanged(this, _settingsService.Current);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var precedenti = _applied;
        _applied = settings;

        try
        {
            ApplyImmediate(precedenti, settings);
        }
        catch (Exception ex)
        {
            // Una parte che non si applica non deve impedire alle altre di farlo: il
            // salvataggio e' gia' avvenuto, e al riavvio successivo vale comunque.
            _log.LogError(ex, "Applicazione a caldo delle impostazioni non riuscita in parte");
        }

        _ = ApplyConnectionsAsync(precedenti, settings);
    }

    // -----------------------------------------------------------------------
    // Ciò che vale subito
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applica le impostazioni che non toccano chiamate né collegamenti.
    /// </summary>
    /// <remarks>
    /// Le opzioni di annuncio e di trasferimento si aggiornano <b>sulla stessa
    /// istanza</b> che i servizi stanno usando, invece di costruirne una nuova: sono
    /// condivise da più servizi, e sostituirle vorrebbe dire raggiungerli tutti uno
    /// per uno. Si ricompongono con le stesse funzioni dell'avvio, così la traduzione
    /// dalle impostazioni resta una sola.
    /// </remarks>
    private void ApplyImmediate(AppSettings previous, AppSettings next)
    {
        var accessibilita = next.Accessibility;

        // Voce degli annunci. Il pannello applica gia' canale e voce interna al
        // salvataggio; qui si allinea il resto, e tutto per i salvataggi che arrivano
        // da altrove, come un pacchetto di configurazione.
        var voce = _services.GetRequiredService<CompositeSpeechOutput>();
        voce.Mode = Enum.TryParse<SpeechChannelMode>(accessibilita.SpeechChannel, out var canale)
            ? canale
            : SpeechChannelMode.Automatic;
        voce.InternalVoiceEnabled = accessibilita.InternalVoiceEnabled;
        voce.InternalVoiceName = accessibilita.InternalVoiceName;
        voce.InternalVoiceRate = accessibilita.InternalVoiceRate;

        // Dispositivi e volumi delle quattro vie: chi cambia l'uscita della suoneria
        // preme il pulsante di prova subito dopo aver salvato. Il percorso audio di una
        // chiamata gia' in corso non si sposta — lo prende all'apertura — ma suoneria,
        // annunci e segnali seguono da subito.
        AudioSettingsApplier.Apply(_services.GetRequiredService<NAudioEngine>(), next, _log);

        var segnali = _services.GetRequiredService<ToneEarconPlayer>();
        segnali.Volume = accessibilita.EarconVolume;
        segnali.UseSpatialPanning = accessibilita.EarconSpatialPanning;

        if (!string.Equals(previous.RingtonePath, next.RingtonePath, StringComparison.Ordinal))
        {
            // Tolta prima di riapplicarla: un percorso svuotato, o non piu' valido,
            // deve riportare al segnale sintetizzato e non lasciare quella vecchia.
            segnali.CustomSounds.Remove(EarconKind.IncomingCall);
            App.ApplyRingtone(segnali, next, _log);
        }

        var annunci = _services.GetRequiredService<AnnouncementOptions>();
        var nuoviAnnunci = App.BuildAnnouncementOptions(next);
        annunci.Verbosity = nuoviAnnunci.Verbosity;
        annunci.QueueMode = nuoviAnnunci.QueueMode;
        annunci.SilenceDuringConversation = nuoviAnnunci.SilenceDuringConversation;
        annunci.QueueWaitThreshold = nuoviAnnunci.QueueWaitThreshold;
        annunci.HoldReminderInterval = nuoviAnnunci.HoldReminderInterval;

        _services.GetRequiredService<CallAnnouncementMapper>()
            .UpdateOptions(accessibilita.DigitGrouping, accessibilita.Verbosity);
        _services.GetRequiredService<QueueMonitorService>()
            .UpdateOptions(annunci, accessibilita.DigitGrouping);

        // Il nome del tasto non si tocca: lo segue la finestra principale insieme al
        // profilo delle scorciatoie, che qui non si conosce.
        var trasferimento = _services.GetRequiredService<TransferOptions>();
        var nuovoTrasferimento = App.BuildTransferOptions(
            next, _services.GetRequiredService<HotkeyProfileStore>().Active);
        trasferimento.Mode = nuovoTrasferimento.Mode;
        trasferimento.DoubleTapWindow = nuovoTrasferimento.DoubleTapWindow;
        trasferimento.ConsultationTimeout = nuovoTrasferimento.ConsultationTimeout;
        trasferimento.ConfirmExternalDestinations = nuovoTrasferimento.ConfirmExternalDestinations;
        trasferimento.WarnOnUnavailableDestination = nuovoTrasferimento.WarnOnUnavailableDestination;
        trasferimento.MaxInternalNumberLength = nuovoTrasferimento.MaxInternalNumberLength;
        trasferimento.DigitGrouping = nuovoTrasferimento.DigitGrouping;

        // Prima del collegamento al centralino, che sotto la usa.
        _services.GetRequiredService<CertificateTrustService>().AcceptAnyCertificate = next.Pbx.AcceptAnyCertificate;

        _services.GetRequiredService<FileLoggerProvider>().RetentionDays = next.DiagnosticLogRetentionDays;

        if (previous.CallHistoryRetentionDays != next.CallHistoryRetentionDays)
        {
            // Un periodo accorciato vale subito: l'elenco contiene dati di traffico,
            // e chi lo riduce lo fa per allinearsi alla policy dell'ente.
            var conservazione = CallHistoryRetention.ToTimeSpan(
                CallHistoryRetention.Normalize(next.CallHistoryRetentionDays));

            _ = _services.GetRequiredService<CallHistoryService>().PurgeAsync(conservazione, DateTimeOffset.Now);
        }

        if (previous.Contacts != next.Contacts)
        {
            _services.GetRequiredService<ContactsSyncService>().Restart();
        }

        _log.LogInformation("Impostazioni applicate a caldo");
    }

    // -----------------------------------------------------------------------
    // Collegamenti: centralino e account SIP
    // -----------------------------------------------------------------------

    private async Task ApplyConnectionsAsync(AppSettings previous, AppSettings next)
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            await ApplyPbxAsync(previous, next).ConfigureAwait(false);
            await ApplySipAsync(fromCallEnd: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applicazione a caldo dei collegamenti non riuscita");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Riapre il collegamento all'API del centralino se i suoi parametri sono cambiati.
    /// </summary>
    private async Task ApplyPbxAsync(AppSettings previous, AppSettings next)
    {
        var nuovo = App.BuildPbxConnection(_settingsService, next);
        if (SamePbx(_pbxApplied, nuovo)) return;

        _pbxApplied = nuovo;

        var coda = _services.GetRequiredService<SwitchableQueueProvider>();

        try
        {
            await coda.SwitchAsync(App.CreateQueueProvider(_services, _settingsService, next)).ConfigureAwait(false);

            // Quando la spunta e' stata appena accesa o spenta l'ha gia' detto la
            // finestra principale: qui si parla solo di una configurazione cambiata a
            // collegamento attivo, che altrimenti cambierebbe in silenzio.
            if (nuovo is not null && previous.Pbx.Enabled == next.Pbx.Enabled)
            {
                Announcer.Announce(Announcement.High(
                    "Collegamento al centralino riaperto con la nuova configurazione",
                    DateTimeOffset.Now,
                    EarconKind.PbxLinkRestored,
                    coalesceKey: "pbx:link"));
            }
        }
        catch (Exception ex)
        {
            // La stessa frase dell'avvio: e' lo stesso guasto, visto da un altro momento.
            _log.LogWarning(ex, "Collegamento al centralino non riuscito dopo il cambio di configurazione");
            Announcer.Announce(Announcement.High(
                $"Centralino non collegato. {ex.Message}", DateTimeOffset.Now, EarconKind.PbxLinkDegraded));
        }
    }

    /// <summary>
    /// Ricostruisce il motore telefonico se l'account in uso o la regolazione audio sono
    /// cambiati, ma solo a linea libera.
    /// </summary>
    /// <param name="fromCallEnd">Se si sta applicando una modifica rimandata.</param>
    /// <remarks>
    /// <para>
    /// <b>Si ricalcola tutto dalla configurazione corrente</b>, anche quando la modifica
    /// era stata rimandata: se nel frattempo l'operatore ha salvato di nuovo, o ha
    /// rimesso il valore di prima, vale quello che c'è adesso.
    /// </para>
    /// <para>
    /// <b>L'account resta quello in uso.</b> Se l'operatore era passato a un secondo
    /// account, una modifica a quello predefinito non lo deve riportare indietro: si
    /// ricostruisce sull'account attivo, preso con la sua configurazione nuova. Si
    /// passa al predefinito solo se l'account in uso non c'è più, o se il motore era
    /// simulato perché nessun account era configurato.
    /// </para>
    /// </remarks>
    private async Task ApplySipAsync(bool fromCallEnd)
    {
        var impostazioni = _settingsService.Current;
        var motore = Engine;
        var attuale = motore.CurrentAccount;

        var account = (_engineAccountId is { } id
                ? impostazioni.SipAccounts.FirstOrDefault(a => a.Id == id)
                : null)
            ?? impostazioni.ActiveSipAccount;

        if (!account.IsUsable)
        {
            // Passare qui porterebbe in modalita' simulata: sembra funzionare e non
            // riceve chiamate. Se il motore e' gia' simulato non c'e' niente da fare.
            if (attuale is not null && !fromCallEnd)
            {
                Announcer.Announce(Announcement.High(
                    $"L'account {account.Name} non e' completo: mancano interno o centralino. "
                    + "Il telefono resta con la configurazione precedente",
                    DateTimeOffset.Now,
                    coalesceKey: "sip:account"));
            }

            _sipPending = false;
            return;
        }

        var opzioni = App.BuildAccountOptions(account, _settingsService);
        var regolazione = App.BuildAudioTuning(impostazioni);

        if (SameAccount(attuale, opzioni) && regolazione == _tuningApplied)
        {
            _sipPending = false;
            return;
        }

        if (Calls.LiveCalls.Count > 0)
        {
            // Mai con una chiamata in corso: ricostruire il motore la farebbe cadere.
            // Si aspetta la linea libera, e lo si dice, altrimenti l'operatore
            // salverebbe di nuovo credendo che la modifica non sia stata presa.
            _sipPending = true;

            Announcer.Announce(Announcement.High(
                "Le modifiche all'account SIP si applicano appena finisce la chiamata in corso",
                DateTimeOffset.Now,
                coalesceKey: "sip:account"));
            return;
        }

        _sipPending = false;

        Announcer.Announce(Announcement.High(
            attuale is null
                ? $"Account {account.Name} configurato: registrazione al centralino in corso"
                : $"Account {account.Name} aggiornato: nuova registrazione al centralino in corso",
            DateTimeOffset.Now,
            coalesceKey: "sip:account"));

        if (await motore.SwitchAsync(opzioni).ConfigureAwait(false))
        {
            _tuningApplied = regolazione;
            return;
        }

        _log.LogError("Applicazione a caldo dell'account SIP {Name} non riuscita", account.Name);

        Announcer.Announce(Announcement.High(
            $"Modifica dell'account {account.Name} non applicata: il telefono resta con la "
            + "configurazione precedente. Il dettaglio e' nel registro",
            DateTimeOffset.Now,
            EarconKind.RegistrationLost,
            coalesceKey: "sip:account"));
    }

    /// <summary>Applica la modifica rimandata appena l'ultima chiamata si chiude.</summary>
    private void OnCallStateChanged(object? sender, CallStateChangedEventArgs e)
    {
        if (!_sipPending || Calls.LiveCalls.Count > 0) return;

        // Fuori dal filo dell'evento: ricostruire il motore ferma quello che lo sta
        // sollevando.
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_sipPending) await ApplySipAsync(fromCallEnd: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Applicazione rimandata dell'account SIP non riuscita");
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>Tiene il conto dell'account in uso, anche quando lo cambia l'operatore.</summary>
    private void OnAccountChanged(object? sender, SipAccountOptions? account)
        => _engineAccountId = IdOf(account, _settingsService.Current);

    // -----------------------------------------------------------------------
    // Confronti
    // -----------------------------------------------------------------------

    /// <summary>Identificativo dell'account configurato che corrisponde a quello del motore.</summary>
    private static string? IdOf(SipAccountOptions? account, AppSettings settings)
        => account is null
            ? null
            : settings.SipAccounts.FirstOrDefault(a => a.Name == account.Name && a.Username == account.Username)?.Id;

    /// <summary>
    /// Se due configurazioni di account sono la stessa, password compresa.
    /// </summary>
    /// <remarks>
    /// L'uguaglianza dei record confronta gli elenchi per riferimento, e l'elenco dei
    /// codec è un'istanza nuova a ogni lettura: senza toglierlo, ogni salvataggio
    /// ricostruirebbe il motore anche senza nessuna modifica.
    /// </remarks>
    internal static bool SameAccount(SipAccountOptions? current, SipAccountOptions next)
        => current is not null
           && current with { CodecPreference = NessunCodec } == next with { CodecPreference = NessunCodec }
           && current.CodecPreference.SequenceEqual(next.CodecPreference);

    /// <summary>Se due configurazioni del collegamento all'API sono la stessa.</summary>
    /// <remarks>Stessa cautela di <see cref="SameAccount"/>, per l'elenco delle code.</remarks>
    internal static bool SamePbx(YeastarOptions? current, YeastarOptions? next)
    {
        if (current is null || next is null) return current is null && next is null;

        return current with { QueueIds = NessunaCoda } == next with { QueueIds = NessunaCoda }
               && current.QueueIds.SequenceEqual(next.QueueIds);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_started)
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
            Engine.AccountChanged -= OnAccountChanged;
            Calls.CallStateChanged -= OnCallStateChanged;
        }

        _gate.Dispose();
    }
}
