using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BlindPhone.Accessibility;
using BlindPhone.Contacts;
using BlindPhone.Contacts.SqlBackup;
using BlindPhone.Contacts.SqlServer;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using BlindPhone.Infrastructure;
using BlindPhone.Pbx.Yeastar;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Pannello delle impostazioni.
/// </summary>
/// <remarks>
/// <para>
/// È il punto in cui un operatore non vedente resta bloccato più facilmente: molti
/// campi, spesso senza etichetta letta correttamente, ed errori di validazione che
/// compaiono solo a schermo. Le contromisure applicate qui sono tre, e vanno
/// mantenute in ogni campo aggiunto in futuro.
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Ogni campo ha una Label con Target.</b> Lo screen reader legge l'etichetta
///   quando il focus entra nel campo, e la lettera di accesso porta direttamente al
///   campo senza doverci arrivare a forza di Tab.
///   </description></item>
///   <item><description>
///   <b>Le password si possono rivelare.</b> Un utente vedente controlla a schermo
///   cosa ha digitato; chi non vede ha bisogno di poter far leggere il valore
///   carattere per carattere, altrimenti un errore di battitura nella password SIP
///   diventa un'ora di telefonate all'assistenza.
///   </description></item>
///   <item><description>
///   <b>Ogni esito è annunciato, non solo scritto.</b> Le verifiche di connessione
///   e il salvataggio parlano, con messaggi in linguaggio naturale e mai codici di
///   errore.
///   </description></item>
/// </list>
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SettingsService _settingsService;
    private readonly ConfigurationPolicyService _policy;
    private readonly IAnnouncer _announcer;
    private readonly IAudioEngine _audio;
    private readonly IContactRepository _contacts;
    private readonly ICallHistoryRepository _history;
    private readonly CompositeSpeechOutput _speech;
    private readonly ITelephonyEngine _telephony;
    private readonly IQueueProvider _queueProvider;
    private readonly PresenceMonitorService _presence;
    private readonly ShiftBoardService _shift;
    private readonly FileLoggerProvider _logProvider;
    private readonly LogLevelSwitch _logSwitch;
    private readonly HotkeyProfileStore _hotkeyStore;
    private readonly HotkeyConflictDetector _hotkeyConflicts;
    private readonly ILogger<SettingsWindow> _log;
    private readonly ILogger<CsvContactSource> _csvLogger;

    private IReadOnlyList<AudioDeviceInfo> _playbackDevices = [];
    private IReadOnlyList<AudioDeviceInfo> _captureDevices = [];

    private readonly ObservableCollection<CodecEntry> _codecs = [];

    /// <summary>Campi della rubrica mostrabili nella ricerca rapida, con la scelta corrente.</summary>
    private readonly ObservableCollection<DialerFieldEntry> _dialerFields = [];

    /// <summary>Crea il pannello.</summary>
    /// <param name="settingsService">Servizio di configurazione.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="audio">Motore audio, per l'elenco dei dispositivi e le prove.</param>
    /// <param name="contacts">Archivio della rubrica.</param>
    /// <param name="history">Archivio delle ultime chiamate.</param>
    /// <param name="speech">
    /// Destinazione della voce, per rilevare lo screen reader e applicare subito le
    /// modifiche all'instradamento.
    /// </param>
    /// <param name="telephony">Motore telefonico, per lo stato e i contatori diagnostici.</param>
    /// <param name="queueProvider">Connettore di coda, per lo stato dell'agente.</param>
    /// <param name="presence">Stato degli interni, per il riepilogo diagnostico.</param>
    /// <param name="shift">Quadro dei colleghi in turno, per dire da dove arriva lo stato.</param>
    /// <param name="logProvider">Registro su file, per posizione e rotazione.</param>
    /// <param name="logSwitch">Livello del registro, modificabile a caldo.</param>
    /// <param name="hotkeyStore">Deposito dei profili di scorciatoie.</param>
    /// <param name="hotkeyConflicts">Rilevatore dei conflitti fra combinazioni.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="policy">
    /// Criterio che limita ciò che l'operatore può modificare.
    /// </param>
    /// <param name="serviceProvider">
    /// Contenitore, per comporre il riepilogo diagnostico dagli stessi servizi che
    /// usa la segnalazione all'assistenza.
    /// </param>
    /// <param name="csvLogger">Log delle importazioni da file.</param>
    public SettingsWindow(
        SettingsService settingsService,
        IAnnouncer announcer,
        IAudioEngine audio,
        IContactRepository contacts,
        ICallHistoryRepository history,
        CompositeSpeechOutput speech,
        ITelephonyEngine telephony,
        IQueueProvider queueProvider,
        PresenceMonitorService presence,
        ShiftBoardService shift,
        FileLoggerProvider logProvider,
        LogLevelSwitch logSwitch,
        HotkeyProfileStore hotkeyStore,
        HotkeyConflictDetector hotkeyConflicts,
        ConfigurationPolicyService policy,
        IServiceProvider serviceProvider,
        ILogger<SettingsWindow> logger,
        ILogger<CsvContactSource> csvLogger)
    {
        _serviceProvider = serviceProvider;
        _policy = policy;
        _settingsService = settingsService;
        _announcer = announcer;
        _audio = audio;
        _contacts = contacts;
        _history = history;
        _speech = speech;
        _telephony = telephony;
        _queueProvider = queueProvider;
        _presence = presence;
        _shift = shift;
        _logProvider = logProvider;
        _logSwitch = logSwitch;
        _hotkeyStore = hotkeyStore;
        _hotkeyConflicts = hotkeyConflicts;
        _log = logger;
        _csvLogger = csvLogger;

        InitializeComponent();

        CodecList.ItemsSource = _codecs;
        DialerFieldList.ItemsSource = _dialerFields;
        HotkeyGrid.ItemsSource = _hotkeyRows;

        // Le etichette originali si conservano prima di toccarle: il criterio vi
        // appende "(bloccata)", e senza l'originale una scheda sbloccata a meta'
        // sessione resterebbe con la parola attaccata.
        foreach (var tab in Tabs.Items.OfType<TabItem>())
        {
            _tabLabels[tab] = tab.Header as string ?? string.Empty;
        }

        LoadDevices();
        LoadSettings(_settingsService.Current);
        LoadHotkeys();
        LoadProvisioning(_settingsService.Current);
        ApplyPolicyToPanel();

        Loaded += async (_, _) =>
        {
            SipUsername.Focus();

            var criterio = _policy.Effective;

            // Con un criterio attivo lo si annuncia per primo. Un operatore che trova
            // i campi che non rispondono, e nessuno che glielo dica, conclude che il
            // programma si e' bloccato — e chiama l'assistenza per un guasto che non
            // c'e'.
            _announcer.Announce(Announcement.CommandResponse(
                criterio.IsRestrictive
                    ? $"Impostazioni. {criterio.Describe()} Control Tab per cambiare scheda"
                    : "Impostazioni. Control Tab per cambiare scheda, Alt piu' la lettera sottolineata "
                      + "per andare a un campo",
                DateTimeOffset.Now));

            await RefreshContactCountAsync().ConfigureAwait(true);
        };
    }

    // -----------------------------------------------------------------------
    // Caricamento
    // -----------------------------------------------------------------------

    private void LoadDevices()
    {
        _playbackDevices = _audio.GetPlaybackDevices();
        _captureDevices = _audio.GetCaptureDevices();

        FillDeviceCombo(MicDevice, _captureDevices);
        FillDeviceCombo(ConversationDevice, _playbackDevices);
        FillDeviceCombo(RingerDevice, _playbackDevices);
        FillDeviceCombo(AnnouncementDevice, _playbackDevices);
    }

    /// <summary>
    /// Popola un elenco di dispositivi, con la voce "predefinito di sistema" in
    /// testa.
    /// </summary>
    /// <remarks>
    /// La voce esplicita per il predefinito serve perché "nessuna selezione" e
    /// "usa il predefinito" sono due cose diverse che, in un elenco letto a voce,
    /// sarebbero indistinguibili.
    /// </remarks>
    private static void FillDeviceCombo(ComboBox combo, IReadOnlyList<AudioDeviceInfo> devices)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "Dispositivo predefinito di sistema", Tag = null });

        foreach (var device in devices)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = device.IsDefault ? $"{device.Name} (predefinito)" : device.Name,
                Tag = device.Id,
            });
        }

        combo.SelectedIndex = 0;
    }

    // -----------------------------------------------------------------------
    // Account SIP multipli
    // -----------------------------------------------------------------------

    /// <summary>
    /// Account in corso di modifica, con le modifiche non ancora salvate.
    /// </summary>
    /// <remarks>
    /// I campi del pannello scrivono qui a ogni cambio di selezione, e da qui vanno
    /// nel file solo al salvataggio. È ciò che permette di confrontare due account
    /// senza che il pannello chieda «vuoi salvare?» a ogni passaggio, cioè senza
    /// mettere una domanda in mezzo all'operazione più normale del pannello.
    /// </remarks>
    private readonly List<SipSettings> _sipAccounts = [];

    /// <summary>Password per account, tenute a parte perché non stanno nel record.</summary>
    private readonly Dictionary<string, string> _sipPasswords = [];

    private string? _defaultSipAccountId;
    private int _selectedSipIndex = -1;

    /// <summary>Impedisce che il riempimento dell'elenco scateni il salvataggio dei campi.</summary>
    private bool _loadingSipAccounts;

    private void LoadSipAccounts(AppSettings settings)
    {
        _loadingSipAccounts = true;

        try
        {
            _sipAccounts.Clear();
            _sipPasswords.Clear();
            _sipAccounts.AddRange(settings.SipAccounts.Count > 0 ? settings.SipAccounts : [settings.Sip]);

            // Si legge per il registrar dell'account, non in assoluto. Se qualcuno ha
            // spostato il registrar fuori da qui — settings.json a mano, un pacchetto
            // distribuito — il campo resta vuoto: l'operatore si accorge che la
            // password va rimessa, invece di salvarla inconsapevolmente verso il
            // server nuovo.
            foreach (var account in _sipAccounts)
            {
                _sipPasswords[account.Id] = _settingsService.Secrets.GetForHost(
                    account.PasswordKey,
                    account.EffectiveRegistrar,
                    $"la password dell'account SIP {account.Name}");
            }

            _defaultSipAccountId = settings.DefaultSipAccountId;
            _selectedSipIndex = -1;

            RefreshSipAccountSelector();

            var attivo = Math.Max(0, _sipAccounts.FindIndex(a => a.Id == _defaultSipAccountId));
            SipAccountSelector.SelectedIndex = attivo;
        }
        finally
        {
            _loadingSipAccounts = false;
        }

        ShowSipAccount(SipAccountSelector.SelectedIndex);
    }

    private void RefreshSipAccountSelector()
    {
        var precedente = SipAccountSelector.SelectedIndex;

        SipAccountSelector.ItemsSource = _sipAccounts
            .Select(a => new
            {
                a.Id,
                Label = (a.Id == _defaultSipAccountId ? "* " : string.Empty)
                        + (string.IsNullOrWhiteSpace(a.Name) ? "senza nome" : a.Name)
                        + (string.IsNullOrWhiteSpace(a.Username) ? string.Empty : $" - interno {a.Username}"),
            })
            .ToList();

        if (precedente >= 0 && precedente < _sipAccounts.Count) SipAccountSelector.SelectedIndex = precedente;
    }

    /// <summary>Porta nei campi l'account indicato.</summary>
    private void ShowSipAccount(int index)
    {
        if (index < 0 || index >= _sipAccounts.Count)
        {
            _selectedSipIndex = -1;
            return;
        }

        _loadingSipAccounts = true;

        try
        {
            var account = _sipAccounts[index];

            SipAccountName.Text = account.Name;
            SipUsername.Text = account.Username;
            SipAuthId.Text = account.AuthId ?? string.Empty;
            SipDomain.Text = account.Domain;
            SipRegistrar.Text = account.Registrar ?? string.Empty;
            SipOutboundProxy.Text = account.OutboundProxy ?? string.Empty;
            SipDisplayName.Text = account.DisplayName;
            SipOutboundCallerId.Text = account.OutboundCallerId;
            SelectByTag(SipTransport, account.Transport);
            SelectByTag(SipSrtp, account.Srtp);
            UpdateSrtpHint();

            var password = _sipPasswords.GetValueOrDefault(account.Id, string.Empty);
            SipPassword.Password = password;
            SipPasswordPlain.Text = password;

            _selectedSipIndex = index;

            UpdateSipAccountHint();
        }
        finally
        {
            _loadingSipAccounts = false;
        }
    }

    /// <summary>Riporta i campi nell'account che si sta lasciando.</summary>
    private void CaptureSipAccount()
    {
        if (_loadingSipAccounts) return;
        if (_selectedSipIndex < 0 || _selectedSipIndex >= _sipAccounts.Count) return;

        var account = _sipAccounts[_selectedSipIndex];

        _sipAccounts[_selectedSipIndex] = account with
        {
            Name = string.IsNullOrWhiteSpace(SipAccountName.Text) ? account.Name : SipAccountName.Text.Trim(),
            Username = SipUsername.Text.Trim(),
            AuthId = NullIfEmpty(SipAuthId.Text),
            Domain = SipDomain.Text.Trim(),
            Registrar = NullIfEmpty(SipRegistrar.Text),
            OutboundProxy = NullIfEmpty(SipOutboundProxy.Text),
            DisplayName = SipDisplayName.Text.Trim(),

            // Si conservano le sole cifre e il piu' iniziale: vedi CallerIdentity.
            // La stessa pulizia la rifa' il motore telefonico, perche' la
            // configurazione puo' arrivare anche da un file scritto a mano.
            OutboundCallerId = CallerIdentity.Normalize(SipOutboundCallerId.Text),
            Transport = SelectedTag(SipTransport) ?? "Udp",
            Srtp = SelectedTag(SipSrtp) ?? "Disabled",
            CodecPreference = [.. _codecs.Where(c => c.IsEnabled).Select(c => c.Info.ShortName)],
        };

        _sipPasswords[account.Id] = ShowSipPassword.IsChecked == true
            ? SipPasswordPlain.Text
            : SipPassword.Password;
    }

    private void OnSipAccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSipAccounts) return;

        // Prima si conserva quello che si lascia, poi si mostra quello che si prende:
        // invertire l'ordine farebbe perdere le modifiche in corso a ogni cambio di
        // selezione, senza che nulla lo dica.
        CaptureSipAccount();
        ShowSipAccount(SipAccountSelector.SelectedIndex);
    }

    private void OnAddSipAccountClick(object sender, RoutedEventArgs e)
    {
        CaptureSipAccount();

        // Identificativo nuovo, quindi chiave del segreto nuova: la password di
        // questo account non tocca quella degli altri.
        var account = new SipSettings
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = $"Account {_sipAccounts.Count + 1}",
        };

        _sipAccounts.Add(account);
        _sipPasswords[account.Id] = string.Empty;

        _loadingSipAccounts = true;
        try
        {
            RefreshSipAccountSelector();
            SipAccountSelector.SelectedIndex = _sipAccounts.Count - 1;
        }
        finally
        {
            _loadingSipAccounts = false;
        }

        ShowSipAccount(_sipAccounts.Count - 1);
        SipAccountName.Focus();
        SipAccountName.SelectAll();

        Report($"Nuovo account aggiunto. Compila nome, interno, password e centralino, poi salva");
    }

    private void OnRemoveSipAccountClick(object sender, RoutedEventArgs e)
    {
        if (_selectedSipIndex < 0 || _selectedSipIndex >= _sipAccounts.Count) return;

        if (_sipAccounts.Count == 1)
        {
            // Restare senza account significa restare senza telefono: si svuotano i
            // campi invece di togliere l'ultima voce dall'elenco.
            Report("Non si puo' eliminare l'unico account. Svuota i campi se vuoi azzerarlo");
            return;
        }

        var account = _sipAccounts[_selectedSipIndex];

        var risposta = MessageBox.Show(
            this,
            $"Eliminare l'account {account.Name}, interno {account.Username}?\n\n"
            + "La password verra' rimossa dal deposito protetto e dovra' essere reinserita.",
            "Elimina account SIP",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (risposta != MessageBoxResult.Yes)
        {
            Report("Eliminazione annullata");
            return;
        }

        _sipAccounts.RemoveAt(_selectedSipIndex);
        _sipPasswords.Remove(account.Id);
        _settingsService.Secrets.Remove(account.PasswordKey);

        if (_defaultSipAccountId == account.Id) _defaultSipAccountId = _sipAccounts[0].Id;

        _loadingSipAccounts = true;
        try
        {
            _selectedSipIndex = -1;
            RefreshSipAccountSelector();
            SipAccountSelector.SelectedIndex = 0;
        }
        finally
        {
            _loadingSipAccounts = false;
        }

        ShowSipAccount(0);
        Report($"Account {account.Name} eliminato. Salva per rendere definitiva la modifica");
    }

    private void OnDefaultSipAccountClick(object sender, RoutedEventArgs e)
    {
        CaptureSipAccount();

        if (_selectedSipIndex < 0 || _selectedSipIndex >= _sipAccounts.Count) return;

        var account = _sipAccounts[_selectedSipIndex];

        if (!account.IsUsable)
        {
            Report($"L'account {account.Name} non e' completo: mancano interno o centralino");
            return;
        }

        _defaultSipAccountId = account.Id;
        RefreshSipAccountSelector();
        UpdateSipAccountHint();

        Report($"{account.Name} sara' l'account usato all'avvio. Salva per rendere definitiva la scelta");
    }

    /// <summary>Descrive lo stato dell'account selezionato.</summary>
    private void UpdateSipAccountHint()
    {
        if (SipAccountHint is null) return;
        if (_selectedSipIndex < 0 || _selectedSipIndex >= _sipAccounts.Count)
        {
            SipAccountHint.Text = string.Empty;
            return;
        }

        var account = _sipAccounts[_selectedSipIndex];

        var parti = new List<string>
        {
            $"Account {_selectedSipIndex + 1} di {_sipAccounts.Count}",
            account.Id == _defaultSipAccountId ? "usato all'avvio" : "non usato all'avvio",
        };

        if (!account.IsUsable) parti.Add("incompleto: mancano interno o centralino");

        LiveRegion.Update(SipAccountHint, string.Join(". ", parti) + ".");
    }

    // -----------------------------------------------------------------------
    // Segnalazione all'assistenza e certificati
    // -----------------------------------------------------------------------

    private void OnSendSupportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new SupportReportWindow(
                _serviceProvider.GetRequiredService<SupportPackageService>(),
                _settingsService,
                _serviceProvider.GetRequiredService<CrashReporter>(),
                () => DiagnosticReport.Build(BuildSnapshot()),
                _announcer,
                _serviceProvider.GetRequiredService<ILogger<SupportReportWindow>>())
            {
                Owner = this,
            };

            window.ShowDialog();
            UpdateCrashReportsHint();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della segnalazione non riuscita");
            Report("Impossibile aprire la segnalazione");
        }
    }

    private void OnClearCrashReportsClick(object sender, RoutedEventArgs e)
    {
        var reporter = _serviceProvider.GetRequiredService<CrashReporter>();
        var pendenti = reporter.Pending().Count;

        if (pendenti == 0)
        {
            Report("Non ci sono rapporti di errore da eliminare");
            return;
        }

        var risposta = MessageBox.Show(
            this,
            $"Eliminare {pendenti} rapporti di errore senza inviarli?\n\n"
            + "Sono l'unica traccia dei guasti che si sono verificati: una volta eliminati, "
            + "l'assistenza non potra' piu' vederli.",
            "Elimina i rapporti di errore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (risposta != MessageBoxResult.Yes)
        {
            Report("Eliminazione annullata");
            return;
        }

        Report($"{reporter.Clear()} rapporti di errore eliminati");
        UpdateCrashReportsHint();
    }

    private void UpdateCrashReportsHint()
    {
        if (CrashReportsHint is null) return;

        try
        {
            var pendenti = _serviceProvider.GetRequiredService<CrashReporter>().Pending();

            LiveRegion.Update(CrashReportsHint, pendenti.Count switch
            {
                0 => "Nessun errore registrato in attesa di invio.",
                1 => $"C'e' un errore registrato, del {pendenti[0].OccurredAt:d MMMM alle HH:mm}, in attesa di essere inviato.",
                _ => $"Ci sono {pendenti.Count} errori registrati in attesa di essere inviati, "
                     + $"l'ultimo del {pendenti[0].OccurredAt:d MMMM alle HH:mm}.",
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Lettura dei rapporti di errore non riuscita");
        }
    }

    private void OnForgetCertificatesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var store = _serviceProvider.GetRequiredService<ICertificateTrustStore>();
            var approvati = store.Trusted;

            if (approvati.Count == 0)
            {
                Report("Nessun certificato approvato da dimenticare");
                return;
            }

            var risposta = MessageBox.Show(
                this,
                $"Dimenticare {approvati.Count} certificati approvati?\n\n"
                + "Al prossimo collegamento BlindPhone tornera' a chiedere se fidarsi. "
                + "E' l'operazione da fare quando il certificato del centralino e' cambiato.",
                "Dimentica i certificati approvati",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (risposta != MessageBoxResult.Yes)
            {
                Report("Operazione annullata");
                return;
            }

            foreach (var certificato in approvati) store.Revoke(certificato.TrustKey);

            // Anche le decisioni "solo per adesso" vanno dimenticate: altrimenti il
            // collegamento in corso continuerebbe a valere su una fiducia appena
            // revocata, e la differenza si vedrebbe solo al riavvio.
            _serviceProvider.GetService<CertificateTrustService>()?.ForgetSessionDecisions();

            Report($"{approvati.Count} certificati dimenticati. Al prossimo collegamento verra' chiesto di nuovo");
            UpdateTrustedCertificatesHint();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Revoca dei certificati approvati non riuscita");
            Report("Revoca non riuscita");
        }
    }

    private void UpdateTrustedCertificatesHint()
    {
        if (TrustedCertificatesHint is null) return;

        try
        {
            var approvati = _serviceProvider.GetRequiredService<ICertificateTrustStore>().Trusted;

            LiveRegion.Update(TrustedCertificatesHint, approvati.Count switch
            {
                0 => "Nessun certificato approvato a mano su questa postazione.",
                1 => $"Un certificato approvato: {approvati[0].Subject} per {approvati[0].Host}.",
                _ => $"{approvati.Count} certificati approvati: "
                     + string.Join(", ", approvati.Select(c => $"{c.Subject} per {c.Host}")) + ".",
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Lettura dei certificati approvati non riuscita");
        }
    }

    /// <summary>
    /// Descrive la conseguenza di accettare qualunque certificato.
    /// </summary>
    /// <remarks>
    /// L'avviso compare mentre si accende la casella e non al salvataggio: è una
    /// scelta che si fa senza immaginarne la conseguenza, e dirla dopo servirebbe
    /// solo a spiegare un errore già commesso.
    /// </remarks>
    private void OnAcceptAnyCertificateChanged(object sender, RoutedEventArgs e)
    {
        if (AcceptAnyCertificateHint is null) return;

        var attivo = AcceptAnyCertificate.IsChecked == true;

        LiveRegion.Update(AcceptAnyCertificateHint, attivo
            ? "ATTENZIONE: con questa opzione BlindPhone non verifica piu' l'identita' del centralino. "
              + "La segnalazione resta cifrata, ma nessuno garantisce che dall'altra parte ci sia il "
              + "centralino giusto. La scelta compare nel riepilogo diagnostico e nel registro."
            : "BlindPhone verifica l'identita' del centralino e, quando non ci riesce, ti mostra il "
              + "certificato e ti chiede se fidarti.");

        if (attivo)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Attenzione: verifica dei certificati disattivata", DateTimeOffset.Now));
        }
    }

    private void LoadSettings(AppSettings settings)
    {
        // Account SIP
        LoadSipAccounts(settings);

        // Centralino
        PbxEnabled.IsChecked = settings.Pbx.Enabled;
        UpdatePbxEnabledHint();
        PbxHost.Text = settings.Pbx.Host;
        PbxPort.Text = settings.Pbx.Port.ToString(CultureInfo.InvariantCulture);
        PbxUseTls.IsChecked = settings.Pbx.UseTls;
        PbxClientId.Text = settings.Pbx.ClientId;
        PbxClientSecret.Password = _settingsService.Secrets.GetForHost(
            SecretStore.PbxClientSecretKey,
            settings.Pbx.Host,
            "il Client Secret dell'API del centralino");
        PbxQueueIds.Text = string.Join(',', settings.Pbx.QueueIds);

        CaricaRubricaCollegata(settings.Contacts);
        PbxOrderedList.IsChecked = settings.Pbx.WaitingListIsOrdered;
        PbxQueuePick.IsChecked = settings.Pbx.EnableExperimentalQueuePick;

        // Assistenza e certificati
        SupportEmail.Text = settings.Support.Email ?? string.Empty;
        SupportUploadUrl.Text = settings.Support.UploadUrl ?? string.Empty;
        CollectCrashReports.IsChecked = settings.Support.CollectCrashReports;
        AcceptAnyCertificate.IsChecked = settings.Pbx.AcceptAnyCertificate;

        // Modalita' di interazione: decide anche se il pannello dei tasti rapidi si
        // puo' usare, quindi va applicata subito dopo averla letta.
        SelectByTag(InteractionMode, settings.Interaction.ToString());
        OnInteractionModeChanged(this, null!);

        // Aggiornamenti. La password si legge dal deposito protetto e si riscrive nel
        // campo: lasciarlo vuoto quando una password c'e' farebbe credere che non sia
        // configurata, e salvando la si cancellerebbe senza volerlo.
        UpdateServerUrl.Text = settings.Updates.ServerUrl;
        UpdateUsername.Text = settings.Updates.Username ?? string.Empty;
        UpdatePassword.Password = _settingsService.Secrets.GetForHost(
            settings.Updates.PasswordKey,
            settings.Updates.ServerUrl,
            "la password del server degli aggiornamenti");
        UpdateCheckOnStartup.IsChecked = settings.Updates.CheckOnStartup;

        OnAcceptAnyCertificateChanged(this, new RoutedEventArgs());
        UpdateCrashReportsHint();
        UpdateTrustedCertificatesHint();

        // Audio
        SelectByTag(MicDevice, settings.Microphone.DeviceId);
        SelectByTag(ConversationDevice, settings.Conversation.DeviceId);
        SelectByTag(RingerDevice, settings.Ringer.DeviceId);
        SelectByTag(AnnouncementDevice, settings.Announcements.DeviceId);

        // L'elaborazione del microfono si carica prima di agganciare il commento
        // sull'effetto: la casella e l'elenco lo aggiornano a ogni cambio, e a finestra
        // in costruzione leggerebbero l'una il valore nuovo e l'altro quello vecchio.
        MicAutoGain.IsChecked = settings.MicrophoneProcessing.AutoGain;
        SelectByTag(MicNoiseSuppression, settings.MicrophoneProcessing.Level.ToString());
        UpdateMicProcessingHint();

        RingtonePath.Text = settings.RingtonePath ?? string.Empty;
        UpdateRingtoneHint();

        JitterBuffer.Text = settings.Audio.JitterBufferMs.ToString(CultureInfo.InvariantCulture);
        DeviceLatency.Text = settings.Audio.DeviceLatencyMs.ToString(CultureInfo.InvariantCulture);
        MaxBuffer.Text = settings.Audio.MaxBufferMs.ToString(CultureInfo.InvariantCulture);
        PacketSize.Text = settings.Audio.PacketMs.ToString(CultureInfo.InvariantCulture);
        UpdateBufferHint();

        LoadCodecs(settings.Sip.CodecPreference);

        // Accessibilita'
        // La casella va impostata prima del canale: il commento sull'effetto della
        // configurazione le legge entrambe, e il canale ne provoca l'aggiornamento.
        InternalVoiceEnabled.IsChecked = settings.Accessibility.InternalVoiceEnabled;
        SelectByTag(SpeechChannel, settings.Accessibility.SpeechChannel);
        UpdateSpeechRoutingHint();
        LoadDialerFields(settings.Contacts.DialerFields);
        SelectByTag(Theme, settings.Accessibility.Theme.ToString());
        UpdateThemeHint();
        SelectByTag(Verbosity, settings.Accessibility.Verbosity.ToString());
        SelectByTag(QueueMode, settings.Accessibility.QueueMode.ToString());
        SelectByTag(DigitGrouping, settings.Accessibility.DigitGrouping.ToString());
        SilenceDuringCall.IsChecked = settings.Accessibility.SilenceDuringConversation;
        EarconsEnabled.IsChecked = settings.Accessibility.EarconsEnabled;
        SpatialPanning.IsChecked = settings.Accessibility.EarconSpatialPanning;
        BrailleEnabled.IsChecked = settings.Accessibility.BrailleEnabled;
        DoubleTapWindow.Text = settings.Accessibility.DoubleTapWindowMs.ToString(CultureInfo.InvariantCulture);
        HoldReminder.Text = settings.Accessibility.HoldReminderSeconds.ToString(CultureInfo.InvariantCulture);

        // Finestra e area di notifica
        TrayEnabled.IsChecked = settings.Tray.Enabled;
        TrayMinimizeOnClose.IsChecked = settings.Tray.MinimizeToTrayOnClose;
        TrayRestoreOnCall.IsChecked = settings.Tray.RestoreOnIncomingCall;
        UpdateTrayHint();

        // Ultime chiamate. Il valore letto viene ricondotto a una delle quattro scelte:
        // un file scritto a mano puo' contenerne altri, e il menu deve mostrare
        // qualcosa di vero.
        SelectByTag(
            HistoryRetention,
            CallHistoryRetention.Normalize(settings.CallHistoryRetentionDays)
                .ToString(CultureInfo.InvariantCulture));

        _ = RefreshHistoryHintAsync();

        // Diagnostica
        _logLevelOnOpen = _logSwitch.Minimum;

        DebugModeEnabled.IsChecked = settings.DebugModeEnabled;
        SelectByTag(LogLevel, settings.DiagnosticLogLevel);
        LogRetention.Text = settings.DiagnosticLogRetentionDays.ToString(CultureInfo.InvariantCulture);

        UpdateDebugHint();
        RefreshDiagnostics();

        // Chiamate
        SelectByTag(TransferMode, settings.Calls.TransferMode);
        ConsultationTimeout.Text = settings.Calls.ConsultationTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ConfirmExternal.IsChecked = settings.Calls.ConfirmExternalTransfers;
        WarnUnavailable.IsChecked = settings.Calls.WarnOnUnavailableDestination;
        MaxInternalLength.Text = settings.Calls.MaxInternalNumberLength.ToString(CultureInfo.InvariantCulture);

        AnnounceRecording.IsChecked = settings.Calls.AnnounceRecording;
        UpdateAnnounceRecordingHint();

        // Colleghi in turno
        ColleaguesMonitored.Text = settings.Colleagues.Monitored;
        ColleaguesRefresh.Text = settings.Colleagues.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        UpdateColleaguesSourceText();
    }

    /// <summary>
    /// Spiega che cosa comporta ciascuna politica di cifratura della voce.
    /// </summary>
    /// <remarks>
    /// La terza riga è quella che conta: <c>Required</c> è l'unica impostazione del
    /// pannello che può <b>impedire</b> una telefonata, e chi la sceglie deve saperlo
    /// prima e non scoprirlo durante un turno.
    /// </remarks>
    private void UpdateSrtpHint()
    {
        SipSrtpHint.Text = SelectedTag(SipSrtp) switch
        {
            "Preferred" => "La voce viene cifrata dove la controparte lo consente. Se rifiuta, la chiamata "
                           + "riparte automaticamente in chiaro e la voce te lo dice: nessuna chiamata va persa.",

            "Required" => "ATTENZIONE: senza cifratura la chiamata NON si fa, ne' in uscita ne' in entrata. "
                          + "Verifica con una chiamata di prova che il centralino esponga SRTP, altrimenti la "
                          + "postazione risultera' irraggiungibile.",

            _ => "La voce viaggia in chiaro. Il trasporto TLS, se attivo, protegge comunque la segnalazione: "
                 + "chi chiama chi, quando e verso quale interno.",
        };
    }

    private void OnSipSrtpChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateSrtpHint();

    private void OnAnnounceRecordingChanged(object sender, RoutedEventArgs e) => UpdateAnnounceRecordingHint();


    /// <summary>
    /// Dice da dove sta arrivando lo stato dei colleghi, adesso.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Serve a chiudere la domanda «perché la colonna è vuota».</b> Le strade sono
    /// tre e non tutte sono disponibili su tutti gli impianti: senza questa riga
    /// l'operatore vedrebbe una tabella che non si popola e non avrebbe modo di sapere
    /// se il problema sia l'elenco che ha scritto, il centralino, o BlindPhone.
    /// </para>
    /// <para>
    /// I due casi negativi si dicono per esteso perché si risolvono in modo diverso: un
    /// centralino che rifiuta il metodo non si convince insistendo, mentre uno che
    /// risponde per chiunque si aggira collegando l'API.
    /// </para>
    /// </remarks>
    private void UpdateColleaguesSourceText()
    {
        var elenco = ColleagueRoster.Parse(ColleaguesMonitored.Text);

        ColleaguesSourceText.Text = elenco.Count switch
        {
            0 => "Nessun interno dichiarato: la sezione non compare nella finestra principale.",
            _ when _shift.MethodRefused => ShiftBoardTexts.ProbeRefused,
            _ when _shift.AnswersForAnyone => ShiftBoardTexts.CannotDistinguish,
            1 => $"Un interno sorvegliato. Lo stato arriva da: {_shift.SourceName}.",
            _ => $"{elenco.Count} interni sorvegliati. Lo stato arriva da: {_shift.SourceName}.",
        };
    }

    /// <summary>
    /// Spiega che cosa comporta togliere l'avviso di registrazione.
    /// </summary>
    /// <remarks>
    /// La seconda metà conta quanto la prima: senza, togliere la spunta sembra
    /// disattivare un fastidio, mentre sposta su chi lavora un adempimento che prima
    /// era automatico.
    /// </remarks>
    private void UpdateAnnounceRecordingHint()
    {
        var attivo = AnnounceRecording.IsChecked == true;

        AnnounceRecordingHint.Text = attivo
            ? "L'interlocutore sente «avvio della registrazione» quando cominci e «registrazione interrotta», con la durata, quando fermi."
            : "L'interlocutore NON verra' avvisato. Informare l'interessato prima che la registrazione cominci resta obbligatorio: assicurati che l'informativa arrivi per un'altra via, e BlindPhone te lo ricordera' a ogni avvio di registrazione.";
    }

    // -----------------------------------------------------------------------
    // Collegamento al centralino
    // -----------------------------------------------------------------------

    private void OnPbxEnabledChanged(object sender, RoutedEventArgs e) => UpdatePbxEnabledHint();

    /// <summary>
    /// Spiega cosa comporta disattivare il collegamento, e cosa continua a
    /// funzionare.
    /// </summary>
    /// <remarks>
    /// La seconda parte conta quanto la prima: senza, disattivare il collegamento
    /// sembra rinunciare al centralino, e nessuno lo farebbe.
    /// </remarks>
    private void UpdatePbxEnabledHint()
    {
        var enabled = PbxEnabled.IsChecked == true;

        PbxEnabledHint.Text = enabled
            ? "Collegamento attivo: le chiamate in coda vengono annunciate. Se il centralino non espone l'API, il sistema segnalera' ripetutamente il collegamento come interrotto."
            : "Collegamento disattivato: telefonare, rispondere, trasferire e la rubrica funzionano normalmente. Non si vedono le chiamate in coda e non vengono piu' segnalati errori di collegamento.";

        // I campi restano modificabili anche a collegamento disattivato: si compila
        // la configurazione oggi e la si attiva quando l'API viene esposta, senza
        // doverla ridigitare.
    }

    // -----------------------------------------------------------------------
    // Voce degli annunci
    // -----------------------------------------------------------------------

    private void OnSpeechRoutingChanged(object sender, RoutedEventArgs e) => UpdateSpeechRoutingHint();

    private void OnSpeechChannelChanged(object sender, SelectionChangedEventArgs e) => UpdateSpeechRoutingHint();

    /// <summary>
    /// Riflette la modalità scelta: descrizione dei tasti e blocco della rimappatura.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il pannello dei tasti rapidi si disattiva in modalità semplificata</b>, ed è
    /// la parte che l'operatore deve vedere prima di salvare. La via breve del
    /// tastierino ha senso se è identica su tutte le postazioni: un «più» che su una
    /// macchina risponde e su un'altra fa altro è peggio di nessuna via breve, perché
    /// chi copre il turno di un collega si porta dietro l'abitudine sbagliata.
    /// </para>
    /// <para>
    /// La scheda resta visibile e leggibile: nascondere una scheda cambierebbe il
    /// numero di tabulazioni per raggiungere le altre, e chi la cerca per capire
    /// perché non risponde più non la troverebbe. Si disattiva la tabella, non la
    /// scheda, e l'avviso accanto dice perché.
    /// </para>
    /// </remarks>
    private void OnInteractionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Chiamato anche mentre la finestra si sta costruendo, quando i controlli
        // della scheda dei tasti non esistono ancora.
        if (InteractionModeHint is null || HotkeyGrid is null) return;

        // Il testo di aiuto originale si conserva alla prima passata: e' quello da
        // rimettere se l'operatore torna alla modalita' standard senza chiudere la
        // finestra.
        if (_hotkeyGridHelpText.Length == 0)
        {
            _hotkeyGridHelpText = System.Windows.Automation.AutomationProperties.GetHelpText(HotkeyGrid) ?? string.Empty;
        }

        var semplificata = string.Equals(SelectedTag(InteractionMode), "Simplified", StringComparison.Ordinal);

        InteractionModeHint.Text = semplificata
            ? "Con il tastierino: PIU' risponde alla chiamata che squilla, e poi alterna fra linea e pausa. "
              + "DIVISO riaggancia, e durante un trasferimento chiude la linea con l'interno riportando il chiamante. "
              + "INVIO chiama l'interno composto, cosi' si sente se squilla o se e' occupato. MENO svincola, "
              + "cioe' passa il chiamante all'interno. Le altre scorciatoie continuano a funzionare, ma non si "
              + "possono riassegnare."
            : "Tutte le scorciatoie sono attive e si possono riassegnare dalla scheda Tasti rapidi.";

        HotkeyGrid.IsEnabled = !semplificata;

        System.Windows.Automation.AutomationProperties.SetHelpText(
            HotkeyGrid,
            semplificata
                ? "Riassegnazione bloccata: la postazione e' in modalita' semplificata, dove la disposizione dei tasti e' fissa."
                : _hotkeyGridHelpText);
    }

    /// <summary>Testo di aiuto originale della tabella dei tasti, per poterlo rimettere.</summary>
    private string _hotkeyGridHelpText = string.Empty;

    /// <summary>
    /// Descrive che cosa l'operatore sentirà con la configurazione scelta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Include il rilevamento dello screen reader fatto <b>adesso</b>, ed è la parte
    /// che serve davvero: la scelta giusta dipende da cosa è in esecuzione sulla
    /// postazione, e finora non c'era modo di saperlo dall'interfaccia. Chi sente
    /// gli annunci doppi non ha modo di capire quale delle due voci sia quale.
    /// </para>
    /// <para>
    /// Avvisa anche della sola combinazione che lascerebbe il posto operatore quasi
    /// muto — voce propria spenta e canale impostato sulla sola voce propria — invece
    /// di limitarsi a correggerla in silenzio al salvataggio.
    /// </para>
    /// </remarks>
    private void UpdateSpeechRoutingHint()
    {
        // Chiamato dai gestori di evento anche mentre la finestra si sta costruendo.
        if (SpeechRoutingHint is null || InternalVoiceEnabled is null || SpeechChannel is null) return;

        var enabled = InternalVoiceEnabled.IsChecked == true;
        var channel = SelectedTag(SpeechChannel) ?? "Automatic";
        var detected = _speech.DetectedScreenReader;

        var reader = detected is null
            ? "Nessuno screen reader rilevato in questo momento."
            : $"Screen reader rilevato: {detected}.";

        var effect = (enabled, channel) switch
        {
            (false, "InternalVoiceOnly") =>
                "Attenzione: voce di BlindPhone spenta ma canale impostato sulla sola voce di BlindPhone. "
                + "Al salvataggio il canale verra' riportato ad Automatica, altrimenti resterebbero solo "
                + "le notifiche di sistema.",
            (false, _) when detected is not null =>
                "Gli annunci arrivano solo dallo screen reader: nessuna sovrapposizione.",
            (false, _) =>
                "Attenzione: la voce di BlindPhone e' spenta e non risulta alcuno screen reader in esecuzione. "
                + "Gli annunci resterebbero affidati alle sole notifiche di sistema, che senza screen reader "
                + "nessuno pronuncia.",
            (true, _) when detected is not null =>
                "Con uno screen reader in esecuzione BlindPhone tace e lascia parlare lui: la voce propria "
                + "interviene solo se lo screen reader viene chiuso.",
            _ =>
                "BlindPhone pronuncia gli annunci con la propria voce, sul dispositivo scelto nella scheda Audio.",
        };

        LiveRegion.Update(SpeechRoutingHint, $"{reader} {effect}");
    }

    // -----------------------------------------------------------------------
    // Elenco delle ultime chiamate
    // -----------------------------------------------------------------------

    private void OnHistoryRetentionChanged(object sender, SelectionChangedEventArgs e)
        => _ = RefreshHistoryHintAsync();

    /// <summary>
    /// Dice quante chiamate ci sono in elenco e cosa comporta la scelta.
    /// </summary>
    /// <remarks>
    /// Il conteggio serve prima di premere «cancella»: l'elenco non si vede da qui, e
    /// una cancellazione irreversibile fatta senza sapere cosa si sta eliminando è
    /// esattamente il gesto che si rimpiange.
    /// </remarks>
    private async Task RefreshHistoryHintAsync()
    {
        if (HistoryHint is null) return;

        var giorni = ParseInt(SelectedTag(HistoryRetention), CallHistoryRetention.Default);

        var effetto = giorni == CallHistoryRetention.Forever
            ? "Nessuna chiamata viene eliminata automaticamente."
            : $"Le chiamate piu' vecchie di {CallHistoryRetention.Describe(giorni)} vengono eliminate all'avvio.";

        try
        {
            var totale = await _history.CountAsync(null, CancellationToken.None).ConfigureAwait(true);

            LiveRegion.Update(
                HistoryHint,
                totale == 0
                    ? $"L'elenco e' vuoto. {effetto}"
                    : $"In elenco: {totale} chiamate. {effetto}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Conteggio delle chiamate in elenco non riuscito");
            LiveRegion.Update(HistoryHint, effetto);
        }
    }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var totale = await _history.CountAsync(null, CancellationToken.None).ConfigureAwait(true);

        if (totale == 0)
        {
            Report("L'elenco delle chiamate e' gia' vuoto");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Stai per eliminare {totale} chiamate dall'elenco: perse, ricevute ed effettuate.\n\n"
            + "L'operazione non si puo' annullare.\n\nProcedere?",
            "Cancella l'elenco delle chiamate",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            Report("Cancellazione annullata");
            return;
        }

        var rimosse = await _history.ClearAsync(null, CancellationToken.None).ConfigureAwait(true);
        await RefreshHistoryHintAsync().ConfigureAwait(true);

        Report($"Elenco delle chiamate svuotato: {rimosse} voci eliminate");
    }

    // -----------------------------------------------------------------------
    // Diagnostica
    // -----------------------------------------------------------------------

    /// <summary>Livello del registro all'apertura, per poterlo ripristinare annullando.</summary>
    private Microsoft.Extensions.Logging.LogLevel _logLevelOnOpen;

    /// <summary>Porta il pannello sulla scheda della diagnostica.</summary>
    /// <remarks>
    /// Chiamato dal comando dedicato della finestra principale: chi lo preme sta
    /// cercando la diagnostica, non le impostazioni in generale, e farlo arrivare
    /// sulla prima scheda gli costerebbe sei pressioni di Control Tab.
    /// </remarks>
    public void ShowDiagnostics()
    {
        Tabs.SelectedItem = DiagnosticsTab;

        // A priorita' Input, cioe' dopo il gestore di Loaded, che porta il cursore sul
        // primo campo dell'account SIP: senza questo passaggio la scheda sarebbe
        // quella giusta ma il cursore starebbe altrove, e chi non vede si troverebbe
        // in un punto che non corrisponde a quello che gli e' stato annunciato.
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                DebugModeEnabled.Focus();

                _announcer.Announce(Announcement.CommandResponse(
                    "Diagnostica. "
                    + (DebugModeEnabled.IsChecked == true
                        ? "Modalita' debug attiva."
                        : "Modalita' debug spenta.")
                    + " Sotto trovi il riepilogo da mandare all'assistenza",
                    DateTimeOffset.Now));
            }),
            DispatcherPriority.Input);
    }

    /// <summary>
    /// Accende o spegne la modalità debug, portandosi dietro il livello del registro.
    /// </summary>
    /// <remarks>
    /// La casella è il comando semplice, il menu del dettaglio è quello fine, e la
    /// casella muove il menu: accendendo si scende a «dettagliato» se si era più in
    /// alto, spegnendo si risale a «normale» se si era più in basso. Senza questo
    /// legame le due impostazioni si contraddirebbero — modalità debug accesa con
    /// registro «solo errori» non registra nulla di utile — e l'operatore non avrebbe
    /// modo di accorgersene.
    /// </remarks>
    private void OnDebugModeChanged(object sender, RoutedEventArgs e)
    {
        if (LogLevel is null) return;

        var attiva = DebugModeEnabled.IsChecked == true;
        var corrente = LogLevelSwitch.Parse(SelectedTag(LogLevel));

        if (attiva && corrente > Microsoft.Extensions.Logging.LogLevel.Debug)
        {
            SelectByTag(LogLevel, nameof(Microsoft.Extensions.Logging.LogLevel.Debug));
        }
        else if (!attiva && corrente < Microsoft.Extensions.Logging.LogLevel.Information)
        {
            SelectByTag(LogLevel, nameof(Microsoft.Extensions.Logging.LogLevel.Information));
        }

        ApplyLogLevel();
        UpdateDebugHint();
    }

    private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyLogLevel();
        UpdateDebugHint();
    }

    /// <summary>
    /// Applica subito il livello scelto al registro in esecuzione.
    /// </summary>
    /// <remarks>
    /// Senza attendere il salvataggio: il momento in cui serve alzare il dettaglio è
    /// quello in cui il guasto si sta manifestando, e chiedere prima di salvare e poi
    /// di riprodurre il problema significa perdere l'occasione. Se si chiude con
    /// Annulla il livello torna com'era.
    /// </remarks>
    private void ApplyLogLevel()
    {
        if (LogLevel is null) return;

        _logSwitch.Minimum = LogLevelSwitch.Parse(SelectedTag(LogLevel));
        _log.LogInformation("Dettaglio del registro portato a {Level}", _logSwitch.Minimum);
    }

    private void UpdateDebugHint()
    {
        if (DebugModeHint is null || LogLevel is null) return;

        var attiva = DebugModeEnabled.IsChecked == true;
        var livello = LogLevelSwitch.Parse(SelectedTag(LogLevel));

        var testo = attiva
            ? "Modalita' debug attiva: il registro annota il dettaglio interno, e mentre una chiamata e' in "
              + "corso vi aggiunge i contatori del percorso audio ogni dieci secondi. Contiene i numeri "
              + "chiamati e chiamanti, e cresce di parecchi megabyte per turno."
            : "Modalita' debug spenta: il registro annota comandi, chiamate e stato, quanto basta a capire "
              + "un guasto grave senza accumulare dati di traffico.";

        // In beta il registro parte su "tutto" ed e' voluto: un difetto che si presenta
        // una volta ogni tre giorni si indaga solo se il registro c'era gia' quando e'
        // successo. Ma va detto che e' cosi' di proposito, altrimenti l'operatore che
        // trova il dettaglio al massimo pensa a una dimenticanza e lo abbassa —
        // togliendo alla beta l'unica cosa che le serve.
        var beta = attiva && livello <= Microsoft.Extensions.Logging.LogLevel.Trace
            ? " In questa versione di prova il dettaglio massimo e' l'impostazione predefinita, "
              + "perche' un difetto raro si indaga solo se il registro c'era gia' quando si e' "
              + "presentato. Puoi abbassarlo se la policy dell'ente lo richiede: la scelta viene "
              + "conservata."
            : string.Empty;

        LiveRegion.Update(
            DebugModeHint,
            $"{testo} Dettaglio in uso: {LogLevelSwitch.Describe(livello)}.{beta}");
    }

    private void OnRefreshDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        RefreshDiagnostics();
        Report("Riepilogo diagnostico aggiornato");
    }

    private void RefreshDiagnostics()
    {
        try
        {
            DiagnosticText.Text = DiagnosticReport.Build(BuildSnapshot());
            DiagnosticText.CaretIndex = 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Composizione del riepilogo diagnostico non riuscita");
            DiagnosticText.Text = "Riepilogo non disponibile.";
        }

        UpdateLogFolderText();
    }

    /// <summary>
    /// Raccoglie lo stato dai servizi vivi.
    /// </summary>
    /// <remarks>
    /// Delega alla fabbrica condivisa: lo stesso riepilogo serve anche alla
    /// segnalazione all'assistenza, e due versioni che dicessero cose diverse sulla
    /// stessa postazione sarebbero peggio di una sola. L'unica cosa che questo
    /// pannello sa e la fabbrica no e' lo stato della casella della modalita' debug,
    /// che puo' essere stato cambiato e non ancora salvato.
    /// </remarks>
    private DiagnosticSnapshot BuildSnapshot()
        => DiagnosticSnapshotFactory.Build(_serviceProvider, DebugModeEnabled.IsChecked == true);

    private void UpdateLogFolderText()
    {
        try
        {
            var cartella = Path.GetDirectoryName(_logProvider.CurrentFilePath)!;
            var file = Directory.Exists(cartella)
                ? Directory.GetFiles(cartella, "blindphone-*.log")
                : [];

            var totale = file.Sum(f => new FileInfo(f).Length);

            LiveRegion.Update(
                LogFolderText,
                $"{file.Length} file di registro in {cartella}, {totale / 1024.0 / 1024.0:F1} megabyte in tutto.");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Lettura della cartella dei registri non riuscita");
            LiveRegion.Update(LogFolderText, "Cartella dei registri non leggibile.");
        }
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshDiagnostics();
            Clipboard.SetText(DiagnosticText.Text);

            Report("Riepilogo copiato negli appunti: incollalo nel messaggio all'assistenza con Control V");
        }
        catch (Exception ex)
        {
            // Gli appunti possono essere occupati da un altro programma: capita, e non
            // e' un guasto di BlindPhone.
            _log.LogWarning(ex, "Copia negli appunti non riuscita");
            Report("Copia negli appunti non riuscita. Usa il pulsante Salva su file");
        }
    }

    private void OnSaveDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Salva il riepilogo diagnostico",
            Filter = "Testo semplice (*.txt)|*.txt|Tutti i file|*.*",
            FileName = $"BlindPhone-diagnostica-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            AddExtension = true,
            DefaultExt = ".txt",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            RefreshDiagnostics();
            File.WriteAllText(dialog.FileName, DiagnosticText.Text, Encoding.UTF8);

            Report($"Riepilogo salvato in {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Salvataggio del riepilogo non riuscito");
            Report("Salvataggio non riuscito. Verifica i permessi sulla cartella scelta");
        }
    }

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        var cartella = Path.GetDirectoryName(_logProvider.CurrentFilePath);

        if (string.IsNullOrWhiteSpace(cartella) || !Directory.Exists(cartella))
        {
            Report("La cartella dei registri non esiste ancora");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cartella)
            {
                UseShellExecute = true,
            });

            Report("Cartella dei registri aperta in Esplora risorse");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Apertura della cartella dei registri non riuscita");
            Report($"Non e' stato possibile aprire la cartella. Si trova in {cartella}");
        }
    }

    // -----------------------------------------------------------------------
    // Spazio su disco
    // -----------------------------------------------------------------------

    /// <summary>
    /// Dice quanto si può recuperare, senza eliminare niente.
    /// </summary>
    /// <remarks>
    /// Esiste come pulsante suo perché la domanda «quanto sto occupando?» è legittima
    /// da sola: chi la fa non ha ancora deciso di cancellare, e non deve trovarsi
    /// davanti una conferma di eliminazione per ottenere una risposta.
    /// </remarks>
    private void OnInspectCleanupClick(object sender, RoutedEventArgs e)
    {
        var preview = InspectCleanup();
        if (preview is null) return;

        if (preview.IsEmpty)
        {
            Report("Non c'e' niente da eliminare: nessun file temporaneo e nessun registro dei giorni scorsi");
            return;
        }

        Report($"Si possono liberare {DiskCleanupService.DescribeSize(preview.TotalBytes)} "
               + $"eliminando {preview.TotalFiles} file");
    }

    /// <summary>
    /// Elimina i file temporanei, dopo averli elencati.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>La conferma elenca le famiglie una per una, con quanto pesano.</b> Non è
    /// burocrazia: fra i file che spariscono ci sono i registri dei giorni scorsi, che
    /// sono l'unica traccia di quello che è successo. Se la segnalazione di un
    /// operatore arriva lunedì e il registro di venerdì è stato eliminato la domenica,
    /// il guasto non si indaga più. Chi preme deve sapere prima che cosa perde, e su
    /// una postazione che si usa a orecchio l'unico modo è sentirselo dire.
    /// </para>
    /// <para>
    /// La risposta predefinita è No, come per il pacchetto di assistenza: un Invio
    /// premuto per abitudine non deve cancellare niente.
    /// </para>
    /// </remarks>
    private void OnCleanupClick(object sender, RoutedEventArgs e)
    {
        var preview = InspectCleanup();
        if (preview is null) return;

        if (preview.IsEmpty)
        {
            Report("Non c'e' niente da eliminare: nessun file temporaneo e nessun registro dei giorni scorsi");
            return;
        }

        var elenco = string.Join(
            Environment.NewLine,
            preview.Categories.Select(c => $"- {c.Describe()}"));

        var answer = MessageBox.Show(
            this,
            $"Verranno eliminati {preview.TotalFiles} file, per "
            + $"{DiskCleanupService.DescribeSize(preview.TotalBytes)}:\n\n"
            + elenco
            + "\n\nI registri eliminati non si recuperano: sono la sola traccia di quello che e' "
            + "successo nei giorni scorsi, e servono a indagare una segnalazione.\n\n"
            + "Restano dove sono la configurazione, la rubrica, l'elenco delle chiamate, le "
            + "registrazioni e il registro di oggi.\n\n"
            + "Procedere?",
            "Libera spazio su disco",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            Report("Pulizia annullata: non e' stato eliminato niente");
            return;
        }

        try
        {
            var esito = BuildCleanupService().Clean();

            // I file rimasti indietro si dicono, non si nascondono dietro un "fatto":
            // quasi sempre e' un antivirus che tiene aperto un registro, e la seconda
            // pressione fra qualche minuto li porta via.
            var coda = esito.SkippedFiles > 0
                ? $". {esito.SkippedFiles} file erano in uso e sono rimasti: riprova fra qualche minuto"
                : string.Empty;

            Report($"Eliminati {esito.DeletedFiles} file, liberati "
                   + $"{DiskCleanupService.DescribeSize(esito.BytesFreed)}{coda}");

            UpdateLogFolderText();
            LiveRegion.Update(CleanupText, string.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pulizia del disco non riuscita");
            Report("Pulizia non riuscita. Verifica i permessi sulla cartella dati di BlindPhone");
        }
    }

    /// <summary>Legge che cosa si può eliminare, riportandolo nella regione dinamica.</summary>
    /// <returns>L'anteprima, oppure <c>null</c> se la lettura non è riuscita.</returns>
    private CleanupPreview? InspectCleanup()
    {
        try
        {
            var preview = BuildCleanupService().Inspect();

            LiveRegion.Update(
                CleanupText,
                preview.IsEmpty
                    ? "Niente da eliminare."
                    : $"Recuperabili {DiskCleanupService.DescribeSize(preview.TotalBytes)} da "
                      + $"{preview.TotalFiles} file: "
                      + string.Join("; ", preview.Categories.Select(c => c.Describe())) + ".");

            return preview;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lettura dello spazio recuperabile non riuscita");
            Report("Non e' stato possibile leggere la cartella dati di BlindPhone");
            return null;
        }
    }

    private DiskCleanupService BuildCleanupService()
        => new(
            _settingsService,
            _logProvider,
            _serviceProvider.GetService<ILogger<DiskCleanupService>>());

    /// <summary>
    /// Prepara un file compresso con riepilogo e registri recenti.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È quello che serve davvero a chi riceve una segnalazione: lo stato della
    /// postazione e le righe che raccontano cosa è successo, in un allegato solo.
    /// Senza, la richiesta di assistenza comincia con due giorni di scambi per farsi
    /// mandare i file giusti.
    /// </para>
    /// <para>
    /// <b>Il pacchetto lo costruisce <see cref="SupportPackageService"/>, e non questo
    /// pannello.</b> Fino alla 1.0.8 qui c'era una seconda costruzione dell'archivio,
    /// scritta prima e mai riallineata: copiava i registri così com'erano, con dentro
    /// i numeri chiamati e chiamanti in chiaro. Nel frattempo l'altra strada — la
    /// segnalazione all'assistenza — aveva imparato ad anonimizzarli, e le note di
    /// versione dichiaravano che i registri allegati escono anonimi. Da questo
    /// pulsante non era vero.
    /// </para>
    /// <para>
    /// Due implementazioni della stessa cosa divergono sempre, e quando quella cosa è
    /// una misura di protezione dei dati la divergenza non si vede finché il file non
    /// è già uscito dall'ente. Ne resta una sola.
    /// </para>
    /// <para>
    /// La conferma non è burocrazia: anche anonimizzato il pacchetto racconta il
    /// traffico della postazione, e chi lo invia deve sapere <b>prima</b> che cosa
    /// contiene.
    /// </para>
    /// </remarks>
    private void OnSupportPackageClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Il pacchetto conterra' il riepilogo diagnostico, i rapporti di errore e i "
            + "file di registro recenti.\n\n"
            + "Numeri e nominativi vengono sostituiti da etichette stabili prima di finire "
            + "nell'archivio, ma il pacchetto racconta comunque il traffico di questa "
            + "postazione: va inviato solo a chi e' autorizzato a riceverlo.\n\n"
            + "Procedere?",
            "Pacchetto per l'assistenza",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            Report("Creazione del pacchetto annullata");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Salva il pacchetto per l'assistenza",
            Filter = "Archivio compresso (*.zip)|*.zip|Tutti i file|*.*",
            FileName = SupportPackageService.SuggestedFileName,
            AddExtension = true,
            DefaultExt = ".zip",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            RefreshDiagnostics();

            var esito = _serviceProvider
                .GetRequiredService<SupportPackageService>()
                .Build(dialog.FileName, DiagnosticText.Text);

            Report(esito.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Creazione del pacchetto per l'assistenza non riuscita");
            Report("Creazione del pacchetto non riuscita. Verifica i permessi sulla cartella scelta");
        }
    }

    // -----------------------------------------------------------------------
    // Guida in linea
    // -----------------------------------------------------------------------

    /// <summary>
    /// Apre la guida sull'argomento della scheda in cui ci si trova.
    /// </summary>
    /// <remarks>
    /// F1 è il tasto della guida in ogni programma Windows, e qui serve più che
    /// altrove: le impostazioni sono il punto in cui un operatore resta bloccato più
    /// facilmente, e la domanda che si fa non è "come funziona questo pannello" ma
    /// "cosa devo mettere in questo campo".
    /// </remarks>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1) return;

        e.Handled = true;

        var topic = Tabs.SelectedItem switch
        {
            var tab when ReferenceEquals(tab, HotkeyTab) => HelpBook.HotkeysTopicId,
            _ => HelpBook.SettingsTopicId,
        };

        try
        {
            new HelpWindow(HelpBook.Build(_hotkeyStore.Active, DateTimeOffset.Now), _announcer, topic)
            {
                Owner = this,
            }.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della guida non riuscita");
            Report("Impossibile aprire la guida");
        }
    }

    // -----------------------------------------------------------------------
    // Finestra e area di notifica
    // -----------------------------------------------------------------------

    private void OnTraySettingChanged(object sender, RoutedEventArgs e) => UpdateTrayHint();

    /// <summary>
    /// Descrive che cosa succederà chiudendo la finestra.
    /// </summary>
    /// <remarks>
    /// La combinazione da segnalare è "chiusura che mette via" senza icona: la
    /// finestra sparirebbe e resterebbe solo la scorciatoia globale per riaverla, che
    /// un altro programma può avere occupato. Al salvataggio viene corretta, ma va
    /// detto prima, perché è il tipo di configurazione che si sceglie senza
    /// immaginarne la conseguenza.
    /// </remarks>
    private void UpdateTrayHint()
    {
        if (TrayHint is null || TrayEnabled is null) return;

        var icon = TrayEnabled.IsChecked == true;
        var minimize = TrayMinimizeOnClose.IsChecked == true;
        var restore = TrayRestoreOnCall.IsChecked == true;

        var closing = (icon, minimize) switch
        {
            (true, true) => "Chiudendo la finestra con la X BlindPhone resta in servizio nell'area di notifica. "
                            + "Con Alt piu' F4 si chiude davvero.",
            (true, false) => "Chiudendo la finestra BlindPhone si chiude davvero e smette di ricevere chiamate.",
            (false, true) => "Attenzione: senza icona nell'area di notifica la chiusura non puo' mettere via "
                             + "la finestra, e chiuderla fermera' il telefono. Al salvataggio l'opzione verra' "
                             + "riportata a chiusura normale.",
            _ => "Chiudendo la finestra BlindPhone si chiude davvero e smette di ricevere chiamate.",
        };

        var call = restore
            ? "Una chiamata entrante riporta la finestra in primo piano."
            : "Una chiamata entrante viene annunciata ma la finestra resta dov'e': si risponde con le scorciatoie globali.";

        LiveRegion.Update(TrayHint, $"{closing} {call}");
    }

    // -----------------------------------------------------------------------
    // Suoneria
    // -----------------------------------------------------------------------

    private void OnBrowseRingtoneClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Scegli il file della suoneria",
            Filter = "File audio (*.wav;*.mp3;*.wma;*.aiff)|*.wav;*.mp3;*.wma;*.aiff|Tutti i file|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        RingtonePath.Text = dialog.FileName;
        UpdateRingtoneHint();

        Report($"Suoneria scelta: {Path.GetFileName(dialog.FileName)}. Premi Ascolta la suoneria per provarla");
    }

    private void OnClearRingtoneClick(object sender, RoutedEventArgs e)
    {
        RingtonePath.Text = string.Empty;
        UpdateRingtoneHint();

        Report("Suoneria riportata al segnale predefinito di BlindPhone");
    }

    /// <summary>
    /// Riproduce la suoneria scelta sulla via su cui suonera' davvero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La prova usa <see cref="AudioRoute.Ringer"/> perché è da lì che esce il segnale
    /// di chiamata in arrivo. Provarla altrove darebbe una falsa idea di dove la
    /// sentirà.
    /// </para>
    /// <para>
    /// Prima usciva dalla conversazione, con la motivazione che chi riceve una
    /// chiamata non sta parlando. Su un posto operatore non è vero: la chiamata
    /// successiva arriva mentre la precedente è ancora in corso, ed è esattamente il
    /// momento in cui uno squillo nell'orecchio copre la voce dell'interlocutore.
    /// </para>
    /// </remarks>
    private async void OnTestRingtoneClick(object sender, RoutedEventArgs e)
    {
        var path = RingtonePath.Text.Trim();

        if (string.IsNullOrWhiteSpace(path))
        {
            Report("Nessun file scelto: verrebbe usato il segnale sintetizzato di BlindPhone");
            return;
        }

        if (!File.Exists(path))
        {
            Report("Il file indicato non esiste. Controlla il percorso oppure usa il pulsante Scegli");
            return;
        }

        try
        {
            TestRingtoneButton.IsEnabled = false;
            Report("Riproduzione della suoneria in corso");

            // Si applica prima la scelta corrente dei dispositivi: la prova deve
            // suonare dove suonera' davvero, anche se le impostazioni non sono ancora
            // state salvate.
            _audio.AssignDevice(AudioRoute.Ringer, SelectedTag(RingerDevice));

            await _audio.PlayAsync(AudioRoute.Ringer, path, 0f, CancellationToken.None);

            Report("Suoneria riprodotta sul dispositivo della suoneria. "
                   + "Se non l'hai sentita, controlla quale dispositivo hai scelto per la suoneria");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Riproduzione della suoneria di prova non riuscita");
            Report("Non e' stato possibile riprodurre il file. Potrebbe essere in un formato non supportato");
        }
        finally
        {
            TestRingtoneButton.IsEnabled = true;
        }
    }

    private void UpdateRingtoneHint()
    {
        var path = RingtonePath.Text.Trim();

        if (string.IsNullOrWhiteSpace(path))
        {
            RingtoneHint.Text = "Nessun file scelto: viene usato il segnale sintetizzato di BlindPhone.";
            return;
        }

        // Un percorso di rete non viene usato: aprirlo manderebbe un'autenticazione
        // all'host che lo ospita. Lo si dice qui, dove il percorso si sceglie, perche'
        // scoprirlo al primo squillo mancato e' il modo peggiore.
        if (!ProtectedLocation.IsLocalPath(path))
        {
            RingtoneHint.Text =
                "Attenzione: e' un percorso di rete e non verra' usato. Copia il file sul disco della "
                + "postazione e scegli quello: aprirlo dalla rete fa partire un'autenticazione verso "
                + "il server che lo ospita.";
            return;
        }

        RingtoneHint.Text = File.Exists(path)
            ? $"File scelto: {Path.GetFileName(path)}."
            : "Attenzione: il file indicato non esiste. Alla prossima chiamata verra' usato il segnale predefinito.";
    }

    // -----------------------------------------------------------------------
    // Elaborazione del microfono
    // -----------------------------------------------------------------------

    private void OnMicProcessingChanged(object sender, RoutedEventArgs e) => UpdateMicProcessingHint();

    /// <summary>
    /// Descrive a voce che cosa produce la scelta fatta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Una casella e un elenco con nomi tecnici — AGC, riduzione del rumore — non dicono
    /// a chi li cambia che cosa sentirà l'interlocutore. Il commento lo traduce, e
    /// ripete l'unico costo che c'è: il ritardo, che con la riduzione del rumore
    /// accesa passa da zero a circa sedici millisecondi e non si percepisce, ma è
    /// meglio saperlo.
    /// </para>
    /// <para>
    /// Chiamato anche mentre la finestra si sta costruendo, quando uno dei due
    /// controlli può non esistere ancora: in quel caso non fa niente.
    /// </para>
    /// </remarks>
    private void UpdateMicProcessingHint()
    {
        if (MicProcessingHint is null || MicAutoGain is null || MicNoiseSuppression is null) return;

        var gain = MicAutoGain.IsChecked == true;
        var level = ParseEnum(SelectedTag(MicNoiseSuppression), NoiseSuppressionLevel.Off);

        var gainText = gain
            ? "Il livello della voce viene regolato in automatico."
            : "Il livello della voce non viene toccato.";

        var noiseText = level switch
        {
            NoiseSuppressionLevel.Light => "Riduzione del rumore leggera: ritardo aggiunto circa sedici millisecondi.",
            NoiseSuppressionLevel.Medium => "Riduzione del rumore media: ritardo aggiunto circa sedici millisecondi, qualche effetto sulla voce.",
            NoiseSuppressionLevel.Strong => "Riduzione del rumore forte: ritardo aggiunto circa sedici millisecondi, la voce diventa meno naturale.",
            _ => "Nessuna riduzione del rumore.",
        };

        LiveRegion.Update(MicProcessingHint, $"{gainText} {noiseText}");
    }

    // -----------------------------------------------------------------------
    // Regolazione del buffer audio
    // -----------------------------------------------------------------------

    private void OnBufferPresetLanClick(object sender, RoutedEventArgs e)
        => ApplyBufferPreset(50, 60, 400, "rete locale cablata");

    private void OnBufferPresetSafeClick(object sender, RoutedEventArgs e)
        => ApplyBufferPreset(100, 100, 500, "prudente");

    private void OnBufferPresetSlowClick(object sender, RoutedEventArgs e)
        => ApplyBufferPreset(200, 150, 800, "rete disturbata o geografica");

    /// <summary>
    /// Applica una configurazione preimpostata del buffer.
    /// </summary>
    /// <remarks>
    /// I valori preimpostati esistono perché la regolazione richiede di capire un
    /// compromesso — interruzioni contro ritardo — che non si può chiedere a ogni
    /// operatore di padroneggiare. Tre punti di partenza ragionevoli coprono la
    /// quasi totalità dei casi, e chi vuole affinare può comunque digitare i valori.
    /// </remarks>
    private void ApplyBufferPreset(int jitter, int latency, int maxBuffer, string label)
    {
        JitterBuffer.Text = jitter.ToString(CultureInfo.InvariantCulture);
        DeviceLatency.Text = latency.ToString(CultureInfo.InvariantCulture);
        MaxBuffer.Text = maxBuffer.ToString(CultureInfo.InvariantCulture);

        UpdateBufferHint();
        Report($"Preimpostazione {label}: cuscinetto {jitter} millisecondi, latenza {latency}");
    }

    /// <summary>
    /// Descrive l'effetto atteso della configurazione corrente.
    /// </summary>
    /// <remarks>
    /// Un numero da solo non dice nulla a chi non conosce il dominio. Tradurlo in
    /// una conseguenza percepibile — "ritardo non percepibile", "possibile
    /// sovrapposizione" — permette di regolare consapevolmente invece che a caso.
    /// </remarks>
    private void UpdateBufferHint()
    {
        var jitter = ParseInt(JitterBuffer.Text, 100);
        var latency = ParseInt(DeviceLatency.Text, 100);
        var total = jitter + latency;

        var effect = total switch
        {
            < 100 => "ritardo minimo, ma poco margine contro le irregolarita' della rete",
            < 200 => "buon compromesso: ritardo non percepibile in conversazione",
            < 350 => "ritardo lieve ma avvertibile, buona tenuta su reti irregolari",
            _ => "ritardo percepibile: possibile sovrapposizione con l'interlocutore",
        };

        LiveRegion.Update(BufferHintText, $"Ritardo complessivo stimato {total} millisecondi. {effect}");
    }

    // -----------------------------------------------------------------------
    // Codec
    // -----------------------------------------------------------------------

    /// <summary>
    /// Popola l'elenco dei campi mostrabili nella ricerca rapida.
    /// </summary>
    /// <param name="scelti">
    /// Campi già scelti, oppure <c>null</c> se la postazione non ha mai deciso.
    /// </param>
    /// <remarks>
    /// <b>Un elenco vuoto è una scelta, l'assenza di elenco no.</b> Chi ha tolto tutte
    /// le spunte vuole vedere solo nominativo e interno, e ricadere sul valore
    /// predefinito gliele rimetterebbe a ogni riapertura del pannello. Per questo si
    /// distingue fra elenco vuoto e impostazione mai scritta.
    /// </remarks>
    private void LoadDialerFields(IReadOnlyList<string>? scelti)
    {
        _dialerFields.Clear();

        // Prima gli attivi nell'ordine scelto, poi gli altri: la stessa forma
        // dell'elenco dei codec, dove l'ordine e' l'informazione principale e
        // leggerlo dall'alto in basso deve bastare a conoscerlo.
        var attivi = DialerFieldCatalog.Resolve(scelti ?? DialerFieldCatalog.Default);

        foreach (var campo in attivi)
        {
            _dialerFields.Add(new DialerFieldEntry(campo, isEnabled: true, RenumberDialerFields));
        }

        foreach (var campo in DialerFieldCatalog.All)
        {
            if (attivi.Any(a => string.Equals(a.Id, campo.Id, StringComparison.Ordinal))) continue;

            _dialerFields.Add(new DialerFieldEntry(campo, isEnabled: false, RenumberDialerFields));
        }

        RenumberDialerFields();
        if (_dialerFields.Count > 0) DialerFieldList.SelectedIndex = 0;
    }

    /// <summary>Aggiorna le posizioni mostrate accanto ai campi attivi.</summary>
    private void RenumberDialerFields()
    {
        var position = 1;

        foreach (var entry in _dialerFields)
        {
            entry.Position = entry.IsEnabled ? position++ : null;
        }
    }

    /// <summary>
    /// Gestisce barra spaziatrice e Alt più frecce sull'elenco dei campi.
    /// </summary>
    /// <remarks>
    /// Gemello di <see cref="OnCodecListKeyDown"/>: due elenchi che governano cose
    /// diverse ma si usano con gli stessi tasti. Chi ha imparato a ordinare i codec
    /// sa già ordinare i campi, e non deve scoprire una seconda convenzione.
    /// </remarks>
    private void OnDialerFieldListKeyDown(object sender, KeyEventArgs e)
    {
        if (DialerFieldList.SelectedItem is not DialerFieldEntry entry) return;

        if (e.Key == Key.Space)
        {
            entry.IsEnabled = !entry.IsEnabled;
            Report(entry.IsEnabled
                ? $"{entry.Field.Label} mostrato, posizione {entry.Position}"
                : $"{entry.Field.Label} nascosto");
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Alt) return;

        if (e.Key == Key.Up)
        {
            MoveSelectedDialerField(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            MoveSelectedDialerField(1);
            e.Handled = true;
        }
    }

    private void OnDialerFieldUpClick(object sender, RoutedEventArgs e) => MoveSelectedDialerField(-1);

    private void OnDialerFieldDownClick(object sender, RoutedEventArgs e) => MoveSelectedDialerField(1);

    private void MoveSelectedDialerField(int delta)
    {
        var index = DialerFieldList.SelectedIndex;
        if (index < 0) return;

        var target = index + delta;
        if (target < 0 || target >= _dialerFields.Count)
        {
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return;
        }

        var entry = _dialerFields[index];
        _dialerFields.Move(index, target);
        DialerFieldList.SelectedIndex = target;

        RenumberDialerFields();

        // Dopo lo spostamento va detto dove e' finito: su una lista letta a voce,
        // contare a ritroso le voci scorse e' lento e si sbaglia.
        Report(entry.Position is { } position
            ? $"{entry.Field.Label} spostato in posizione {position}"
            : $"{entry.Field.Label} spostato, non mostrato");
    }

    private void OnDialerFieldResetClick(object sender, RoutedEventArgs e)
    {
        LoadDialerFields(null);
        Report("Campi consigliati ripristinati: accanto a nominativo e interno resta solo lo stato dell'interno");
    }

    /// <summary>
    /// Popola l'elenco dei codec: prima quelli attivi nell'ordine configurato, poi
    /// i restanti disattivati.
    /// </summary>
    /// <remarks>
    /// L'elenco unico con le caselle di spunta, invece di due liste separate
    /// "disponibili" e "selezionati", è una scelta di accessibilità: due liste
    /// costringerebbero l'operatore a spostare il focus avanti e indietro per
    /// capire lo stato complessivo, mentre qui una sola passata con le frecce dice
    /// contemporaneamente quali sono attivi e in che ordine.
    /// </remarks>
    private void LoadCodecs(IReadOnlyList<string> preference)
    {
        _codecs.Clear();

        var enabled = AudioCodecCatalog.ParseList(preference);

        foreach (var codec in enabled)
        {
            _codecs.Add(new CodecEntry(AudioCodecCatalog.Describe(codec), isEnabled: true, OnCodecEnabledChanged));
        }

        foreach (var info in AudioCodecCatalog.All)
        {
            if (enabled.Contains(info.Codec)) continue;
            _codecs.Add(new CodecEntry(info, isEnabled: false, OnCodecEnabledChanged));
        }

        RenumberCodecs();
        if (_codecs.Count > 0) CodecList.SelectedIndex = 0;
    }

    /// <summary>
    /// Aggiorna le posizioni mostrate e l'avviso di conformità.
    /// </summary>
    private void RenumberCodecs()
    {
        var position = 1;

        foreach (var entry in _codecs)
        {
            entry.Position = entry.IsEnabled ? position++ : null;
        }

        UpdateCodecWarning();
    }

    /// <summary>
    /// Verifica la selezione e avvisa quando compromette la conformità o la
    /// possibilità stessa di telefonare.
    /// </summary>
    /// <remarks>
    /// L'avviso non blocca: ci sono centralini che non offrono G.722, e in quel caso
    /// disattivarlo è la scelta giusta. Ma l'operatore deve saperlo, perché la
    /// conseguenza — perdere la conformità a EN 301 549 §6.1, che nel mercato della
    /// PA è un requisito contrattuale — non è deducibile dall'interfaccia.
    /// </remarks>
    private void UpdateCodecWarning()
    {
        var active = _codecs.Where(c => c.IsEnabled).Select(c => c.Info.Codec).ToList();

        if (active.Count == 0)
        {
            SetCodecWarning("Attenzione: nessun codec attivo. Senza almeno un codec non e' possibile telefonare.");
            return;
        }

        if (!AudioCodecCatalog.IncludesWideband(active))
        {
            SetCodecWarning(
                "Attenzione: nessun codec a banda larga attivo. La norma europea sull'accessibilita' EN 301 549 "
                + "richiede il supporto della banda larga: senza G.722 questa conformita' non e' piu' dimostrabile.");
            return;
        }

        if (!active.Contains(AudioCodec.PCMA) && !active.Contains(AudioCodec.PCMU))
        {
            SetCodecWarning(
                "Attenzione: nessun codec G.711 attivo. E' la riserva universale: senza, alcune chiamate "
                + "potrebbero non essere instaurate.");
            return;
        }

        SetCodecWarning(string.Empty);
    }

    private string _lastCodecWarning = string.Empty;

    private void SetCodecWarning(string message)
    {
        LiveRegion.Update(CodecWarningText, message);

        // Si annuncia solo il cambiamento: ripetere lo stesso avviso a ogni
        // spunta lo renderebbe rumore di fondo che l'operatore impara a ignorare.
        if (message == _lastCodecWarning) return;

        _lastCodecWarning = message;
        if (!string.IsNullOrEmpty(message)) Report(message);
    }

    private void OnCodecEnabledChanged() => RenumberCodecs();

    private void OnCodecSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Nessun annuncio: la ListBox e' un controllo standard e lo screen reader
        // legge gia' la voce selezionata. Una lettura nostra sarebbe doppia.
    }

    /// <summary>
    /// Gestisce barra spaziatrice e Alt più frecce sull'elenco dei codec.
    /// </summary>
    /// <remarks>
    /// Il riordino da tastiera è indispensabile: trascinare con il mouse è l'unico
    /// modo previsto dalla maggior parte dei softphone, ed è esattamente il gesto
    /// che un operatore non vedente non può fare.
    /// </remarks>
    private void OnCodecListKeyDown(object sender, KeyEventArgs e)
    {
        if (CodecList.SelectedItem is not CodecEntry entry) return;

        if (e.Key == Key.Space)
        {
            entry.IsEnabled = !entry.IsEnabled;
            Report(entry.IsEnabled
                ? $"{entry.Info.Label} attivato"
                : $"{entry.Info.Label} disattivato");
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Alt) return;

        if (e.Key == Key.Up)
        {
            MoveSelectedCodec(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            MoveSelectedCodec(1);
            e.Handled = true;
        }
    }

    private void OnCodecUpClick(object sender, RoutedEventArgs e) => MoveSelectedCodec(-1);

    private void OnCodecDownClick(object sender, RoutedEventArgs e) => MoveSelectedCodec(1);

    private void MoveSelectedCodec(int delta)
    {
        var index = CodecList.SelectedIndex;
        if (index < 0) return;

        var target = index + delta;
        if (target < 0 || target >= _codecs.Count)
        {
            _announcer.PlayEarcon(EarconKind.CommandRejected);
            return;
        }

        var entry = _codecs[index];
        _codecs.Move(index, target);
        CodecList.SelectedIndex = target;

        RenumberCodecs();

        // La posizione va detta: dopo lo spostamento l'operatore deve sapere dove e'
        // finito il codec, e su una lista letta a voce contare le voci scorse a
        // ritroso e' lento e soggetto a errore.
        Report(entry.Position is { } position
            ? $"{entry.Info.Label} spostato in posizione {position}"
            : $"{entry.Info.Label} spostato, non attivo");
    }

    private void OnCodecResetClick(object sender, RoutedEventArgs e)
    {
        LoadCodecs([.. AudioCodecCatalog.DefaultPreference.Select(c => AudioCodecCatalog.Describe(c).ShortName)]);
        // L'ELENCO SI COMPONE DAL CATALOGO, NON SI SCRIVE A MANO.
        //
        // Scritto a mano diceva "G.722 banda larga, G.711 A-law, G.729 compresso"
        // mentre l'ordine caricato era ed e' G.729, G.722, G.711: il pulsante
        // dichiarava una cosa e ne faceva un'altra. Per un operatore che non vede
        // lo schermo quell'annuncio e' l'unico riscontro che riceve, quindi era la
        // meta' peggiore da sbagliare. Componendolo dalla stessa fonte che il
        // pulsante carica, le due cose non possono piu' divergere.
        var ordine = string.Join(", ", AudioCodecCatalog.DefaultPreference
            .Select(c => AudioCodecCatalog.Describe(c).Label));

        Report($"Ordine consigliato ripristinato: {ordine}");
    }

    /// <summary>
    /// Dice all'operatore che cosa sta effettivamente comandando i colori.
    /// </summary>
    /// <remarks>
    /// Con il contrasto elevato attivo la scelta nell'elenco non ha effetto, e un
    /// comando che non fa niente senza dire perché porta a premerlo di nuovo. La
    /// riga lo spiega invece di lasciarlo scoprire.
    /// </remarks>
    private void UpdateThemeHint()
        => LiveRegion.Update(
            ThemeHint,
            System.Windows.SystemParameters.HighContrast
                ? "Windows e' in contrasto elevato: i colori li decide il sistema e questa scelta resta in attesa. "
                  + "Tornera' valida spegnendo il contrasto elevato."
                : "La scelta vale subito, senza riavviare.");

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e) => UpdateThemeHint();


    private static void SelectByTag(ComboBox combo, string? deviceId)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            // Senza distinguere maiuscole e minuscole: i valori scritti a mano nel file di
            // configurazione («udp», «TLS») erano accettati quando la scelta si cercava per
            // testo, e non devono smettere di esserlo.
            if (string.Equals(item.Tag as string, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    // -----------------------------------------------------------------------
    // Preconfigurazione: pacchetto esportabile
    // -----------------------------------------------------------------------

    /// <summary>Voce con casella di spunta per una sezione.</summary>
    /// <remarks>
    /// Serve a due elenchi che sembrano uguali e non lo sono: quello delle sezioni da
    /// mettere nel pacchetto e quello delle sezioni da bloccare. Tenerli separati
    /// costa una classe e evita l'errore che li unirebbe — esportare una sezione e
    /// bloccarla sono decisioni diverse, e capita spesso di volere l'una senza
    /// l'altra.
    /// </remarks>
    private sealed class SectionChoice(SettingsSection section, bool included)
        : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _included = included;

        public SettingsSection Section { get; } = section;

        public string Label { get; } = ConfigurationPolicy.DescribeSection(section);

        public bool IsIncluded
        {
            get => _included;
            set
            {
                if (_included == value) return;

                _included = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsIncluded)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<SectionChoice> _bundleSections = [];
    private readonly ObservableCollection<SectionChoice> _policySections = [];

    private void LoadProvisioning(AppSettings settings)
    {
        _bundleSections.Clear();
        _policySections.Clear();

        var criterio = settings.Policy;

        foreach (var sezione in ConfigurationBundleService.AllSections)
        {
            // L'account SIP non e' spuntato di partenza: e' la sezione che cambia da
            // una postazione all'altra, e includerla per errore sovrascriverebbe
            // l'interno di chi riceve il pacchetto.
            _bundleSections.Add(new SectionChoice(sezione, sezione != SettingsSection.SipAccounts));
            _policySections.Add(new SectionChoice(sezione, criterio.LockedSections.Contains(sezione)));
        }

        BundleSections.ItemsSource = _bundleSections;
        PolicySections.ItemsSource = _policySections;

        BundleRevision.Text = Math.Max(1, settings.AppliedConfigurationRevision + 1)
            .ToString(CultureInfo.InvariantCulture);

        SelectByTag(PanelAccessBox, criterio.Access.ToString());

        PolicyNote.Text = criterio.Note ?? string.Empty;
        PolicyPassword.Clear();

        UpdateProvisioningHints();
    }

    private void UpdateProvisioningHints()
    {
        if (BundleHint is null || PolicyOriginHint is null) return;

        var applicata = _settingsService.Current.AppliedConfigurationRevision;

        LiveRegion.Update(BundleHint, applicata > 0
            ? $"Su questa postazione e' stata applicata la revisione {applicata}. "
              + "Per distribuire un aggiornamento, esporta con una revisione piu' alta."
            : "Nessun pacchetto di configurazione e' mai stato applicato a questa postazione.");

        var postazione = _policy.MachinePolicy;

        LiveRegion.Update(PolicyOriginHint, postazione.Origin == PolicyOrigin.Machine
            ? "ATTENZIONE: su questa postazione e' attivo un criterio di sistema, che ha la precedenza "
              + $"su quanto imposti qui. {postazione.Describe()}"
            : "Nessun criterio di sistema su questa postazione: vale quello impostato qui sotto, che "
              + "protegge dalla modifica accidentale ma non da un utente determinato.");
    }

    private void OnPanelAccessChanged(object sender, SelectionChangedEventArgs e) => UpdateProvisioningHints();

    private void OnExportBundleClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Esporta la configurazione",
            Filter = $"Configurazione BlindPhone (*{ConfigurationBundle.FileExtension})"
                     + $"|*{ConfigurationBundle.FileExtension}|Tutti i file|*.*",
            FileName = ConfigurationBundle.ProvisioningFileName,
            AddExtension = true,
            DefaultExt = ConfigurationBundle.FileExtension,
        };

        if (dialog.ShowDialog(this) != true)
        {
            Report("Esportazione annullata");
            return;
        }

        try
        {
            // Si salva prima: il pacchetto deve contenere quello che si vede nei
            // campi, non quello che c'era all'apertura del pannello. Senza, si
            // esporterebbe una configurazione che nessuno ha mai visto.
            if (SaveButton.IsEnabled) OnSaveClick(this, new RoutedEventArgs());

            var servizio = _serviceProvider.GetRequiredService<ConfigurationBundleService>();

            var sezioni = _bundleSections.Where(s => s.IsIncluded).Select(s => s.Section).ToList();

            var bundle = servizio.Compose(
                sezioni,
                BundleIncludeHotkeys.IsChecked == true,
                BundleIncludePolicy.IsChecked == true ? ComposePolicy() : null,
                ParseInt(BundleRevision.Text, 1),
                NullIfEmpty(BundleDescription.Text));

            var esito = servizio.Save(bundle, dialog.FileName);

            Report(esito.Success
                ? $"{esito.Message}. Mettilo in ProgramData BlindPhone sulle altre postazioni: "
                  + "al primo avvio si configurano da sole"
                : esito.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Esportazione della configurazione non riuscita");
            Report("Esportazione non riuscita");
        }
    }

    private void OnImportBundleClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importa una configurazione",
            Filter = $"Configurazione BlindPhone (*{ConfigurationBundle.FileExtension})"
                     + $"|*{ConfigurationBundle.FileExtension}|Tutti i file|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            Report("Importazione annullata");
            return;
        }

        try
        {
            var servizio = _serviceProvider.GetRequiredService<ConfigurationBundleService>();
            var bundle = servizio.Read(dialog.FileName);

            if (bundle is null)
            {
                Report("Il file non e' un pacchetto di configurazione di BlindPhone");
                return;
            }

            // Si dice che cosa contiene PRIMA di applicarlo: un pacchetto sovrascrive
            // sezioni intere, e chi lo applica deve sapere quali. La conferma non e'
            // una formalita' — l'errore tipico e' applicare il pacchetto sbagliato e
            // perdere la configurazione dell'account SIP.
            var risposta = MessageBox.Show(
                this,
                $"{bundle.Describe()}\n\n"
                + "Le sezioni contenute nel pacchetto verranno sovrascritte. "
                + "Le altre restano come sono.\n\nProcedere?",
                "Importa una configurazione",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (risposta != MessageBoxResult.Yes)
            {
                Report("Importazione annullata");
                return;
            }

            var esito = servizio.Apply(bundle);

            if (esito.Success)
            {
                // I campi vanno ricaricati: quelli a schermo sono ancora quelli di
                // prima, e un salvataggio successivo riscriverebbe la configurazione
                // appena importata con quella vecchia.
                LoadSettings(_settingsService.Current);
                LoadHotkeys();
                LoadConfigurationPolicy();
            }

            Report(esito.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importazione della configurazione non riuscita");
            Report("Importazione non riuscita");
        }
    }

    /// <summary>Compone il criterio dai campi del pannello.</summary>
    private ConfigurationPolicy ComposePolicy()
    {
        var corrente = _settingsService.Current.Policy;

        var accesso = Enum.TryParse<PanelAccess>(SelectedTag(PanelAccessBox), out var scelto)
            ? scelto
            : PanelAccess.Open;

        var criterio = corrente with
        {
            Access = accesso,
            LockedSections = [.. _policySections.Where(s => s.IsIncluded).Select(s => s.Section)],
            Note = NullIfEmpty(PolicyNote.Text),
            Origin = PolicyOrigin.User,
        };

        // Campo vuoto significa "non cambiare": chi apre il pannello per correggere
        // un'altra impostazione non deve cancellare la password di sblocco solo
        // perche' non l'ha ridigitata.
        if (!string.IsNullOrEmpty(PolicyPassword.Password))
        {
            var (hash, salt) = ConfigurationPolicyService.HashPassword(PolicyPassword.Password);
            criterio = criterio with { PasswordHash = hash, PasswordSalt = salt };
        }

        return criterio;
    }

    /// <summary>Rilegge il criterio dopo che è cambiato, e riapplica il pannello.</summary>
    private void LoadConfigurationPolicy()
    {
        var postazione = _serviceProvider.GetRequiredService<MachinePolicyStore>().Read();
        var utente = _settingsService.Current.Policy;

        _policy.Load(
            postazione,
            utente with { Origin = utente.IsRestrictive ? PolicyOrigin.User : PolicyOrigin.None });

        LoadProvisioning(_settingsService.Current);
        ApplyPolicyToPanel();
    }

    // -----------------------------------------------------------------------
    // Criterio di blocco
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sezioni che il criterio non consente di modificare.
    /// </summary>
    /// <remarks>
    /// Restituisce le sezioni bloccate <b>e modificate</b>? No, e la distinzione è
    /// deliberata: confrontare i valori campo per campo per capire se qualcosa è
    /// cambiato sarebbe fragile — un decimale riformattato, uno spazio in coda — e
    /// produrrebbe rifiuti che l'operatore non sa spiegarsi. Con una sezione bloccata
    /// il salvataggio viene rifiutato e basta, e il messaggio dice quale.
    /// </remarks>
    private IReadOnlyList<SettingsSection> BlockedSections()
        => [.. ConfigurationBundleService.AllSections.Where(s => !_policy.CanEdit(s))];

    /// <summary>
    /// Disabilita i campi delle sezioni bloccate e lo dice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si disabilitano le schede intere e non i singoli campi. Una scheda con metà
    /// dei campi grigi costringe l'operatore a percorrerla per scoprire quali: con lo
    /// screen reader significa sentire «non disponibile» venti volte prima di capire
    /// che quella sezione non si tocca.
    /// </para>
    /// <para>
    /// La scheda resta <b>raggiungibile e leggibile</b>: è la differenza fra
    /// «bloccata» e «nascosta». Quando l'operatore chiama l'assistenza, la prima cosa
    /// che gli viene chiesta è come è configurata la sua postazione, e una scheda
    /// nascosta gli toglie la possibilità di rispondere.
    /// </para>
    /// </remarks>
    private void ApplyPolicyToPanel()
    {
        var criterio = _policy.Effective;

        SetSectionEnabled(SipTab, SettingsSection.SipAccounts);
        SetSectionEnabled(PbxTab, SettingsSection.Pbx);
        SetSectionEnabled(AudioTab, SettingsSection.Audio);
        SetSectionEnabled(AccessibilityTab, SettingsSection.Accessibility);
        SetSectionEnabled(CallsTab, SettingsSection.Calls);
        SetSectionEnabled(HotkeyTab, SettingsSection.Hotkeys);
        SetSectionEnabled(ContactsTab, SettingsSection.Contacts);
        SetSectionEnabled(DiagnosticsTab, SettingsSection.Diagnostics);

        var bloccate = BlockedSections();

        SaveButton.IsEnabled = bloccate.Count == 0;
        UnlockButton.Visibility = criterio.IsRestrictive ? Visibility.Visible : Visibility.Collapsed;

        LiveRegion.Update(PolicyBanner, bloccate.Count == 0
            ? string.Empty
            : criterio.Describe());

        PolicyBanner.Visibility = bloccate.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (bloccate.Count > 0)
        {
            _log.LogInformation(
                "Pannello aperto con criterio attivo: sezioni bloccate {Sezioni}",
                string.Join(", ", bloccate));
        }
    }

    /// <summary>Abilita o disabilita una scheda secondo il criterio.</summary>
    /// <remarks>
    /// L'etichetta della scheda dice che è bloccata: senza, lo screen reader annuncia
    /// «Account SIP» e poi silenzio, e l'operatore conclude che il programma non
    /// risponde.
    /// </remarks>
    private void SetSectionEnabled(TabItem tab, SettingsSection section)
    {
        _tabSections[tab] = section;

        var editable = _policy.CanEdit(section);

        // Il contenuto si disabilita, la scheda no: una scheda disabilitata non si
        // raggiunge con Control Tab, e quello che l'operatore perde e' la possibilita'
        // di leggere come e' configurata la sua postazione.
        if (tab.Content is UIElement contenuto) contenuto.IsEnabled = editable;

        var etichetta = _tabLabels.GetValueOrDefault(tab, tab.Header as string ?? string.Empty);

        tab.Header = editable ? etichetta : etichetta + " (bloccata)";

        System.Windows.Automation.AutomationProperties.SetName(
            tab,
            editable
                ? etichetta.Replace("_", string.Empty, StringComparison.Ordinal)
                : etichetta.Replace("_", string.Empty, StringComparison.Ordinal)
                  + ", bloccata dall'amministratore");
    }

    /// <summary>Etichette originali delle schede, per rimetterle allo sblocco.</summary>
    private readonly Dictionary<TabItem, string> _tabLabels = [];

    /// <summary>Sezione del criterio a cui corrisponde ciascuna scheda.</summary>
    private readonly Dictionary<TabItem, SettingsSection> _tabSections = [];

    /// <summary>
    /// Dice a voce che la scheda appena aperta è bloccata, e come sbloccarla.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Senza, il blocco è indistinguibile da un guasto.</b> Il contenuto di una
    /// scheda bloccata è disattivato, e un contenitore disattivato in Windows non si
    /// raggiunge nemmeno con il TAB: chi non vede ci passa sopra e trova il pannello
    /// muto, chi guarda ci clicca sopra e non succede niente. La parola «(bloccata)»
    /// è nell'etichetta della scheda, ma la si sente solo passando di scheda in scheda
    /// con Control TAB — non arrivandoci da un'altra parte, e non provando a usarla.
    /// </para>
    /// <para>
    /// È il difetto per cui la tabella dei tasti rapidi risultava irraggiungibile
    /// anche con il mouse: la sezione era bloccata da un criterio, e nulla lo diceva
    /// nel momento in cui l'operatore provava a riassegnare una combinazione.
    /// </para>
    /// <para>
    /// Il messaggio dell'amministratore viene ripetuto qui perché è la parte che dice
    /// a chi rivolgersi, e nel banner in cima al pannello è già scorso via.
    /// </para>
    /// </remarks>
    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // IL CAMBIO DI SELEZIONE RISALE ANCHE DAGLI ELENCHI CONTENUTI NELLE SCHEDE.
        //
        // Un TabControl riceve il SelectionChanged di ogni ComboBox e di ogni elenco
        // che ha dentro: senza questo controllo l'annuncio partirebbe a ogni scelta
        // di un dispositivo audio o di un codec. Misurato: cambiando scheda l'origine
        // e' il TabControl, scegliendo nella casella e' la ComboBox.
        //
        // Si guarda "sender" e non il campo della finestra perche' il primo cambio di
        // selezione arriva mentre la finestra si sta costruendo, quando quel campo
        // puo' non essere ancora assegnato.
        if (sender is not TabControl schede) return;
        if (!ReferenceEquals(e.OriginalSource, schede)) return;

        if (schede.SelectedItem is not TabItem tab) return;
        if (!_tabSections.TryGetValue(tab, out var sezione)) return;
        if (_policy.CanEdit(sezione)) return;

        var nome = ConfigurationPolicy.DescribeSection(sezione);
        var nota = _policy.Effective.Note;

        var testo = $"Sezione {nome} bloccata dall'amministratore: i campi non rispondono."
                    + (string.IsNullOrWhiteSpace(nota) ? string.Empty : $" {nota.Trim()}")
                    + " Il pulsante Sblocca o riblocca, in fondo al pannello, la apre per questa sessione.";

        Report(testo);
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e)
    {
        if (_policy.IsUnlockedForSession)
        {
            _policy.Relock();
            ApplyPolicyToPanel();
            Report("Impostazioni ribloccate");
            return;
        }

        var finestra = new UnlockSettingsWindow(_policy, _announcer) { Owner = this };
        finestra.ShowDialog();

        if (!finestra.Unlocked) return;

        ApplyPolicyToPanel();
        Report("Impostazioni sbloccate per questa sessione. Il pulsante le riblocca subito");
    }

    // -----------------------------------------------------------------------
    // Salvataggio
    // -----------------------------------------------------------------------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Il criterio si verifica anche qui e non solo disabilitando i campi. I campi
        // disabilitati sono la parte che si vede; questa e' quella che regge, e serve
        // perche' fra l'apertura del pannello e il salvataggio l'amministratore puo'
        // aver ribloccato, e perche' un campo che sfugge alla disabilitazione e' un
        // difetto che si scopre solo quando qualcuno ci scrive dentro.
        if (BlockedSections() is { Count: > 0 } bloccate)
        {
            var elenco = string.Join(", ", bloccate.Select(ConfigurationPolicy.DescribeSection));

            Report($"Salvataggio rifiutato: {elenco} sono bloccate dall'amministratore. "
                   + _policy.Effective.Describe());

            return;
        }

        var errors = Validate();

        if (errors.Count > 0)
        {
            // Gli errori si annunciano tutti insieme e in linguaggio naturale: una
            // finestra di avviso che ruba il focus costringerebbe l'operatore a
            // ritrovare il campo da correggere, e un errore alla volta lo
            // costringerebbe a fare il giro tante volte quanti sono gli errori.
            var message = errors.Count == 1
                ? $"Impossibile salvare. {errors[0]}"
                : $"Impossibile salvare, {errors.Count} campi da correggere. {string.Join(". ", errors)}";

            Report(message);
            return;
        }

        // Prima dei campi: la scrittura del profilo aggiorna il nome del profilo
        // attivo nella configurazione, e quella qui sotto deve partire da un valore
        // gia' aggiornato invece di sovrascriverlo con il precedente.
        var hotkeyNote = SaveHotkeysIfChanged();

        // Voce propria spenta e canale impostato sulla sola voce propria e' l'unica
        // combinazione che lascerebbe il posto operatore senza nulla di pronunciato.
        // Si corregge invece di rifiutare il salvataggio, e lo si dice: rifiutarlo
        // costringerebbe a capire l'instradamento per poter salvare il resto.
        var internalVoice = InternalVoiceEnabled.IsChecked == true;
        var speechChannel = SelectedTag(SpeechChannel) ?? "Automatic";
        var speechNote = string.Empty;

        if (!internalVoice && speechChannel == "InternalVoiceOnly")
        {
            speechChannel = "Automatic";
            SelectByTag(SpeechChannel, speechChannel);

            speechNote = " Canale della voce riportato ad Automatica: era impostato sulla sola voce di "
                         + "BlindPhone, che hai appena spento.";
        }

        // Senza icona nell'area di notifica, "chiudendo lascia in servizio" farebbe
        // sparire la finestra lasciando come sola via di ritorno la scorciatoia
        // globale, che un altro programma puo' avere occupato. Si corregge e si dice.
        var trayEnabled = TrayEnabled.IsChecked == true;
        var trayMinimize = TrayMinimizeOnClose.IsChecked == true;
        var trayNote = string.Empty;

        if (!trayEnabled && trayMinimize)
        {
            trayMinimize = false;
            TrayMinimizeOnClose.IsChecked = false;

            trayNote = " Senza icona nell'area di notifica la chiusura torna a chiudere davvero: "
                       + "altrimenti la finestra sparirebbe senza un modo sicuro di riaverla.";
        }

        var current = _settingsService.Current;

        // I campi appartengono all'account selezionato: si riportano dentro prima di
        // comporre l'elenco, altrimenti l'ultima modifica andrebbe persa proprio
        // perche' l'operatore ha premuto Salva senza cambiare selezione.
        CaptureSipAccount();

        var updated = current with
        {
            SipAccounts = [.. _sipAccounts],
            DefaultSipAccountId = _defaultSipAccountId,
            Pbx = current.Pbx with
            {
                Enabled = PbxEnabled.IsChecked == true,
                Host = PbxHost.Text.Trim(),
                Port = ParseInt(PbxPort.Text, 8088),
                UseTls = PbxUseTls.IsChecked == true,
                ClientId = PbxClientId.Text.Trim(),
                QueueIds = ParseQueueIds(PbxQueueIds.Text),
                WaitingListIsOrdered = PbxOrderedList.IsChecked == true,
                EnableExperimentalQueuePick = PbxQueuePick.IsChecked == true,
                AcceptAnyCertificate = AcceptAnyCertificate.IsChecked == true,
            },
            Support = current.Support with
            {
                Email = NullIfEmpty(SupportEmail.Text),
                UploadUrl = NullIfEmpty(SupportUploadUrl.Text),
                CollectCrashReports = CollectCrashReports.IsChecked == true,
            },
            Updates = current.Updates with
            {
                ServerUrl = UpdateServerUrl.Text.Trim(),
                Username = NullIfEmpty(UpdateUsername.Text),
                CheckOnStartup = UpdateCheckOnStartup.IsChecked == true,
            },
            Contacts = ComponiRubricaCollegata(current.Contacts),
            Policy = ComposePolicy(),
            Microphone = current.Microphone with { DeviceId = SelectedTag(MicDevice), DeviceName = SelectedName(MicDevice) },
            Conversation = current.Conversation with { DeviceId = SelectedTag(ConversationDevice), DeviceName = SelectedName(ConversationDevice) },
            Ringer = current.Ringer with { DeviceId = SelectedTag(RingerDevice), DeviceName = SelectedName(RingerDevice) },
            Announcements = current.Announcements with { DeviceId = SelectedTag(AnnouncementDevice), DeviceName = SelectedName(AnnouncementDevice) },
            RingtonePath = NullIfEmpty(RingtonePath.Text),
            MicrophoneProcessing = new MicrophoneProcessing
            {
                AutoGain = MicAutoGain.IsChecked == true,
                NoiseSuppression = ParseEnum(SelectedTag(MicNoiseSuppression), NoiseSuppressionLevel.Off),
            },
            Audio = current.Audio with
            {
                JitterBufferMs = ParseInt(JitterBuffer.Text, 100),
                DeviceLatencyMs = ParseInt(DeviceLatency.Text, 100),
                MaxBufferMs = ParseInt(MaxBuffer.Text, 500),
                PacketMs = ParseInt(PacketSize.Text, 20),
            },
            Accessibility = current.Accessibility with
            {
                SpeechChannel = speechChannel,
                InternalVoiceEnabled = internalVoice,
                Theme = ParseEnum(SelectedTag(Theme), AppTheme.FollowSystem),
                Verbosity = ParseEnum(SelectedTag(Verbosity), VerbosityLevel.Standard),
                QueueMode = ParseEnum(SelectedTag(QueueMode), QueueAnnouncementMode.CountOnly),
                DigitGrouping = ParseEnum(SelectedTag(DigitGrouping), Core.Speech.DigitGrouping.Single),
                SilenceDuringConversation = SilenceDuringCall.IsChecked == true,
                EarconsEnabled = EarconsEnabled.IsChecked == true,
                EarconSpatialPanning = SpatialPanning.IsChecked == true,
                BrailleEnabled = BrailleEnabled.IsChecked == true,
                DoubleTapWindowMs = ParseInt(DoubleTapWindow.Text, 400),
                HoldReminderSeconds = ParseInt(HoldReminder.Text, 30),
            },
            DebugModeEnabled = DebugModeEnabled.IsChecked == true,
            DiagnosticLogLevel = SelectedTag(LogLevel) ?? "Information",
            DiagnosticLogRetentionDays = ParseInt(LogRetention.Text, 14),
            Tray = current.Tray with
            {
                Enabled = trayEnabled,
                MinimizeToTrayOnClose = trayMinimize,
                RestoreOnIncomingCall = TrayRestoreOnCall.IsChecked == true,
            },
            Calls = current.Calls with
            {
                TransferMode = SelectedTag(TransferMode) ?? "ProgressiveDoubleTap",
                ConsultationTimeoutSeconds = ParseInt(ConsultationTimeout.Text, 20),
                ConfirmExternalTransfers = ConfirmExternal.IsChecked == true,
                WarnOnUnavailableDestination = WarnUnavailable.IsChecked == true,
                MaxInternalNumberLength = ParseInt(MaxInternalLength.Text, 5),
                AnnounceRecording = AnnounceRecording.IsChecked == true,
            },
            Colleagues = current.Colleagues with
            {
                Monitored = ColleagueRoster.Format(ColleagueRoster.Parse(ColleaguesMonitored.Text)),
                RefreshSeconds = ParseInt(ColleaguesRefresh.Text, 60),
            },
            CallHistoryRetentionDays = ParseInt(SelectedTag(HistoryRetention), CallHistoryRetention.Default),
            Interaction = ParseEnum(SelectedTag(InteractionMode), BlindPhone.Core.Domain.InteractionMode.Standard),
        };

        // I segreti non passano dal file di configurazione: vanno nel deposito
        // cifrato con DPAPI, che e' legato a questo utente e a questa macchina.
        //
        // Una password per account, sotto la chiave dell'account: e' quello che
        // permette a piu' account di coesistere senza sovrascriversi la credenziale.
        //
        // OGNUNA VIENE LEGATA AL SERVER PER CUI VALE.
        //
        // Qui il legame si puo' rifare, e deve: chi salva ha davanti il campo
        // dell'indirizzo e quello della password, quindi e' una scelta presa da una
        // persona. Quello che non deve poter spostare il destinatario di una
        // credenziale e' una modifica silenziosa della configurazione — settings.json
        // aperto con un editor di testo, un pacchetto distribuito — e quella passa
        // dalla lettura, che non rilega niente e lascia la credenziale dov'era.
        //
        // Lo spostamento viene detto, perche' e' l'unica traccia che l'operatore ha
        // di una credenziale che da ora parte verso un server diverso.
        var spostamenti = new List<string>();

        foreach (var account in _sipAccounts)
        {
            SaveSecretForHost(
                account.PasswordKey,
                _sipPasswords.GetValueOrDefault(account.Id, string.Empty),
                account.EffectiveRegistrar,
                $"La password dell'account {account.Name}",
                spostamenti);
        }

        var pbxSecret = ShowPbxSecret.IsChecked == true ? PbxClientSecretPlain.Text : PbxClientSecret.Password;

        SaveSecretForHost(
            SecretStore.PbxClientSecretKey,
            pbxSecret,
            updated.Pbx.Host,
            "Il Client Secret dell'API del centralino",
            spostamenti);

        // La password del server degli aggiornamenti segue la stessa strada di quelle
        // SIP: nel deposito protetto e mai nel file di configurazione, che finisce nel
        // pacchetto per l'assistenza e nei backup.
        SaveSecretForHost(
            updated.Updates.PasswordKey,
            UpdatePassword.Password,
            updated.Updates.ServerUrl,
            "La password del server degli aggiornamenti",
            spostamenti);

        // La password del server della rubrica segue la stessa strada. Resta la
        // condizione sul campo vuoto: questa scheda si salva anche da chi non ha mai
        // aperto quella della rubrica, e li' il campo e' vuoto perche' non e' stato
        // caricato, non perche' la password vada cancellata.
        if (SqlServerPassword.Password.Length > 0)
        {
            SaveSecretForHost(
                updated.Contacts.SqlServer.PasswordSecretKey,
                SqlServerPassword.Password,
                updated.Contacts.SqlServer.Server,
                "La password del server della rubrica",
                spostamenti);
        }

        var secretNote = spostamenti.Count == 0 ? string.Empty : " " + string.Join(" ", spostamenti);

        if (!_settingsService.Save(updated))
        {
            Report("Salvataggio non riuscito. Verifica i permessi sulla cartella dati");
            return;
        }

        // La voce si applica subito, senza riavvio: e' l'impostazione che si viene a
        // cambiare proprio mentre si sta sentendo il problema, e farla verificare al
        // riavvio successivo significherebbe cambiarla alla cieca.
        _speech.Mode = ParseEnum(speechChannel, SpeechChannelMode.Automatic);
        _speech.InternalVoiceEnabled = internalVoice;

        // Lo stesso vale per i dispositivi audio: chi cambia l'uscita della suoneria
        // preme il pulsante di prova subito dopo aver salvato. Il percorso audio di
        // una chiamata gia' in corso non si sposta — quello lo prende all'apertura —
        // ma suoneria, annunci e segnali seguono da subito.
        AudioSettingsApplier.Apply(_audio, updated, _log);

        Report($"Impostazioni salvate.{speechNote}{trayNote}{hotkeyNote}{secretNote}");
        DialogResult = true;
        Close();

        // La domanda si pone quando questa finestra e' effettivamente sparita, non
        // mentre si sta chiudendo: altrimenti lo screen reader legge la finestra in
        // uscita al posto della domanda, e l'operatore sente parlare di riavvio senza
        // sapere che gli e' stato chiesto qualcosa.
        Dispatcher.BeginInvoke(new Action(PromptForRestart), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Scrive un segreto legandolo al server per cui vale, e annota quando quel
    /// server cambia.
    /// </summary>
    /// <param name="key">Chiave del segreto nel deposito protetto.</param>
    /// <param name="value">Valore da conservare. Vuoto rimuove la credenziale.</param>
    /// <param name="host">Server per cui il segreto vale.</param>
    /// <param name="what">Come si chiama la credenziale, per l'annuncio.</param>
    /// <param name="notices">Elenco a cui aggiungere la frase, se c'è da dirne una.</param>
    /// <remarks>
    /// <para>
    /// L'annotazione serve a chi non vede lo schermo. Chi vede si ricorda di aver
    /// cambiato il campo dell'indirizzo un momento prima; chi non vede ha salvato una
    /// scheda con una cinquantina di campi, e «la password del centralino ora vale
    /// per un altro indirizzo» è l'unica conferma che quel cambio è stato capito come
    /// lo intendeva.
    /// </para>
    /// <para>
    /// Non si annota il primo inserimento, dove non c'è nessuno spostamento da
    /// riferire: sarebbe una frase in più su ogni salvataggio di una postazione
    /// nuova.
    /// </para>
    /// </remarks>
    private void SaveSecretForHost(
        string key, string value, string? host, string what, List<string> notices)
    {
        var precedente = _settingsService.Secrets.BoundHostOf(key);

        _settingsService.Secrets.SetForHost(key, value, host);

        var adesso = _settingsService.Secrets.BoundHostOf(key);

        if (value.Length == 0)
        {
            // Il campo era vuoto perche' la credenziale vale per un altro server: il
            // deposito l'ha conservata, e l'operatore deve sapere che non viene usata
            // qui — altrimenti il sintomo sara' soltanto un collegamento che non parte.
            if (adesso is not null)
            {
                notices.Add(
                    $"{what} resta quella inserita per {adesso} e non viene usata con il server "
                    + "configurato adesso: per usarla qui va reinserita.");
            }

            return;
        }

        if (precedente is null) return;

        if (string.Equals(precedente, adesso, StringComparison.Ordinal)) return;

        _log.LogInformation(
            "Credenziale {Chiave} rilegata dal pannello: da {Prima} a {Adesso}", key, precedente, adesso);

        notices.Add($"{what} ora vale per {adesso ?? "nessun server"} e non piu' per {precedente}.");
    }

    /// <summary>
    /// Chiede se riavviare subito per applicare le impostazioni salvate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il riavvio serve davvero: motore telefonico, connettore del centralino,
    /// suoneria e instradamento audio vengono costruiti all'avvio con i parametri di
    /// allora. Senza una richiesta esplicita l'operatore salva, non vede cambiare
    /// nulla e conclude che l'impostazione non ha funzionato.
    /// </para>
    /// <para>
    /// È una domanda e non un riavvio automatico perché <b>può esserci una chiamata
    /// in corso</b>: riavviare senza chiedere la farebbe cadere. Il valore
    /// predefinito è No proprio per questo — chi preme Invio distrattamente non
    /// perde la conversazione.
    /// </para>
    /// <para>
    /// La finestra viene mostrata dopo la chiusura del pannello, così lo screen
    /// reader legge la domanda invece della finestra che sta sparendo. Il proprietario
    /// è la finestra principale, che a quel punto è quella attiva.
    /// </para>
    /// </remarks>
    private void PromptForRestart()
    {
        var answer = MessageBox.Show(
            Application.Current.MainWindow,
            "Le impostazioni sono state salvate.\n\n"
            + "Voce degli annunci e tasti rapidi sono gia' attivi.\n"
            + "Account SIP, centralino e dispositivi audio richiedono il riavvio.\n\n"
            + "Vuoi riavviare adesso?\n"
            + "Se hai una chiamata in corso, scegli No: la chiamata cadrebbe.",
            "Riavviare BlindPhone?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Impostazioni salvate. Verranno applicate al prossimo avvio di BlindPhone",
                DateTimeOffset.Now));
            return;
        }

        Restart();
    }

    /// <summary>Riavvia l'applicazione.</summary>
    /// <remarks>
    /// Se l'avvio del nuovo processo fallisce non si chiude quello corrente: meglio
    /// un posto operatore con impostazioni vecchie che un posto operatore spento.
    /// </remarks>
    private void Restart()
    {
        var executable = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(executable))
        {
            _log.LogWarning("Percorso dell'eseguibile non determinabile: riavvio automatico non possibile");
            Report("Riavvio automatico non riuscito. Chiudi e riapri BlindPhone a mano");
            return;
        }

        try
        {
            _announcer.Announce(Announcement.CommandResponse("Riavvio di BlindPhone in corso", DateTimeOffset.Now));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = true,
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Riavvio automatico non riuscito");

            MessageBox.Show(
                Application.Current.MainWindow,
                "Non e' stato possibile riavviare automaticamente.\n\n"
                + "Chiudi e riapri BlindPhone a mano per applicare le impostazioni.",
                "Riavvio non riuscito",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Verifica i campi e restituisce gli errori in linguaggio naturale.
    /// </summary>
    /// <remarks>
    /// I messaggi dicono <b>quale campo</b> e <b>cosa manca</b>, perché vengono
    /// pronunciati e l'operatore deve poter tornare al campo giusto senza cercarlo.
    /// </remarks>
    private List<string> Validate()
    {
        var errors = new List<string>();

        var hasSip = !string.IsNullOrWhiteSpace(SipUsername.Text) || !string.IsNullOrWhiteSpace(SipDomain.Text);

        if (hasSip)
        {
            if (string.IsNullOrWhiteSpace(SipUsername.Text)) errors.Add("Manca l'interno SIP");
            if (string.IsNullOrWhiteSpace(SipDomain.Text)) errors.Add("Manca il dominio del centralino");
        }

        // A collegamento disattivato i campi del centralino non vengono verificati:
        // restano compilabili in anticipo, per essere pronti quando l'API verra'
        // esposta, senza che una compilazione parziale impedisca di salvare il resto.
        var hasPbx = PbxEnabled.IsChecked == true
                     && (!string.IsNullOrWhiteSpace(PbxHost.Text) || !string.IsNullOrWhiteSpace(PbxClientId.Text));

        if (hasPbx)
        {
            if (string.IsNullOrWhiteSpace(PbxHost.Text)) errors.Add("Manca l'indirizzo del centralino");
            if (string.IsNullOrWhiteSpace(PbxClientId.Text)) errors.Add("Manca il Client ID dell'API");
            if (ParseQueueIds(PbxQueueIds.Text).Count == 0)
                errors.Add("Manca almeno un numero di coda da sorvegliare");
        }

        if (!int.TryParse(PbxPort.Text, out var port) || port is < 1 or > 65535)
            errors.Add("La porta del centralino non e' un numero valido");

        if (!int.TryParse(DoubleTapWindow.Text, out var window) || window is < 100 or > 1500)
            errors.Add("La finestra della doppia pressione deve essere fra 100 e 1500 millisecondi");

        if (!int.TryParse(ConsultationTimeout.Text, out var timeout) || timeout is < 5 or > 120)
            errors.Add("Il tempo di attesa della consultazione deve essere fra 5 e 120 secondi");

        // Un elenco di codec vuoto rende impossibile qualunque chiamata: e' l'unico
        // caso della sezione codec che blocca il salvataggio, mentre le scelte
        // sconsigliate ma legittime producono solo un avviso.
        if (!_codecs.Any(c => c.IsEnabled))
            errors.Add("Attiva almeno un codec audio, altrimenti non e' possibile telefonare");

        // I limiti del buffer non sono arbitrari: sotto i 20 millisecondi il
        // cuscinetto e' piu' corto di un pacchetto e non assorbe nulla, sopra i 500
        // il ritardo rende la conversazione impraticabile.
        if (!int.TryParse(JitterBuffer.Text, out var jitter) || jitter is < 20 or > 500)
            errors.Add("Il cuscinetto audio deve essere fra 20 e 500 millisecondi");

        if (!int.TryParse(DeviceLatency.Text, out var latency) || latency is < 30 or > 500)
            errors.Add("La latenza del dispositivo deve essere fra 30 e 500 millisecondi");

        if (!int.TryParse(MaxBuffer.Text, out var maxBuffer) || maxBuffer is < 100 or > 2000)
            errors.Add("La capienza massima del buffer deve essere fra 100 e 2000 millisecondi");

        if (!int.TryParse(PacketSize.Text, out var packet) || packet is < 10 or > 60)
            errors.Add("La durata dei pacchetti deve essere fra 10 e 60 millisecondi");

        // Zero giorni cancellerebbe i registri appena scritti, cioe' proprio quelli
        // che servono a indagare la segnalazione in corso.
        if (!int.TryParse(LogRetention.Text, out var retention) || retention is < 1 or > 365)
            errors.Add("I giorni di conservazione dei registri devono essere fra 1 e 365");

        return errors;
    }

    /// <remarks>
    /// Le modifiche ai tasti si perdono come tutte le altre, ma vanno chieste a parte:
    /// hanno un pulsante di salvataggio proprio, e chi le ha appena fatte può non
    /// collegarle al pulsante Annulla generale. Sono anche le più lunghe da rifare,
    /// perché ogni combinazione va premuta una per una.
    /// </remarks>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (HasHotkeyChanges())
        {
            var answer = MessageBox.Show(
                this,
                $"Ci sono {CountHotkeyChanges()} modifiche ai tasti rapidi non salvate.\n\n"
                + "Chiudendo ora vanno perse.\n\nChiudere lo stesso?",
                "Modifiche ai tasti non salvate",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                // Si torna sulla scheda dei tasti: chi ha appena scelto di non
                // perdere le modifiche deve trovarsele davanti, non doverle cercare.
                Tabs.SelectedItem = HotkeyTab;
                Report("Chiusura annullata. Le modifiche ai tasti sono ancora in corso");
                return;
            }
        }

        // Il livello del registro si applica mentre lo si cambia, per poter riprodurre
        // subito il guasto: chiudendo senza salvare va riportato com'era, altrimenti
        // la postazione resterebbe a scrivere in dettaglio senza che nessuno lo sappia.
        if (_logSwitch.Minimum != _logLevelOnOpen)
        {
            _logSwitch.Minimum = _logLevelOnOpen;
            _log.LogInformation("Dettaglio del registro riportato a {Level}", _logLevelOnOpen);
        }

        _announcer.Announce(Announcement.CommandResponse("Impostazioni chiuse senza salvare", DateTimeOffset.Now));
        DialogResult = false;
        Close();
    }

    // -----------------------------------------------------------------------
    // Verifiche
    // -----------------------------------------------------------------------

    private void OnTestSipClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SipUsername.Text) || string.IsNullOrWhiteSpace(SipDomain.Text))
        {
            Report("Compila prima interno e dominio");
            return;
        }

        // La verifica reale della registrazione richiede il riavvio del motore
        // telefonico, che porterebbe giu' le chiamate in corso. Si conferma quindi
        // solo la completezza dei dati, dicendolo con chiarezza invece di lasciar
        // credere a una verifica che non e' avvenuta.
        Report($"Dati SIP completi per l'interno {SpeechFormatter.Digits(SipUsername.Text)} "
               + $"sul centralino {SipDomain.Text}. La registrazione viene provata al riavvio");
    }

    private async void OnTestPbxClick(object sender, RoutedEventArgs e)
    {
        var secret = ShowPbxSecret.IsChecked == true ? PbxClientSecretPlain.Text : PbxClientSecret.Password;

        if (string.IsNullOrWhiteSpace(PbxHost.Text)
            || string.IsNullOrWhiteSpace(PbxClientId.Text)
            || string.IsNullOrWhiteSpace(secret))
        {
            Report("Compila prima indirizzo, Client ID e Client Secret");
            return;
        }

        TestPbxButton.IsEnabled = false;
        Report("Verifica in corso");

        try
        {
            var options = new YeastarOptions
            {
                Host = PbxHost.Text.Trim(),
                Port = ParseInt(PbxPort.Text, 8088),
                UseTls = PbxUseTls.IsChecked == true,
                ClientId = PbxClientId.Text.Trim(),
                ClientSecret = secret,
                Extension = SipUsername.Text.Trim(),
                QueueIds = ParseQueueIds(PbxQueueIds.Text),
            };

            using var client = new YeastarApiClient(options);
            var source = new YeastarContactSource(client);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await source.TestAsync(timeout.Token).ConfigureAwait(true);

            Report(result.Success
                ? $"Collegamento riuscito. {result.Message}"
                : $"Collegamento non riuscito. {result.Message}");
        }
        catch (OperationCanceledException)
        {
            Report("Il centralino non ha risposto entro venti secondi. Verifica indirizzo, porta e firewall");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Verifica del collegamento al centralino non riuscita");
            Report("Centralino non raggiungibile. Verifica indirizzo e porta");
        }
        finally
        {
            TestPbxButton.IsEnabled = true;
        }
    }

    private async void OnTestAudioClick(object sender, RoutedEventArgs e)
    {
        TestAudioButton.IsEnabled = false;

        try
        {
            // Ogni via viene provata singolarmente e annunciata prima di suonare:
            // e' l'unico modo per capire, sentendo, quale dispositivo e' assegnato
            // a cosa.
            await TestRouteAsync(AudioRoute.Conversation, SelectedTag(ConversationDevice), "conversazione")
                .ConfigureAwait(true);

            await TestRouteAsync(AudioRoute.Ringer, SelectedTag(RingerDevice), "suoneria")
                .ConfigureAwait(true);

            Report("Prova audio completata");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Prova audio non riuscita");
            Report("Prova audio non riuscita");
        }
        finally
        {
            TestAudioButton.IsEnabled = true;
        }
    }

    private async Task TestRouteAsync(AudioRoute route, string? deviceId, string label)
    {
        _audio.AssignDevice(route, deviceId);
        Report($"Suono di prova sulla via {label}");

        _announcer.PlayEarcon(route == AudioRoute.Ringer
            ? EarconKind.QueueCallArrived
            : EarconKind.CallAnswered);

        await Task.Delay(900).ConfigureAwait(true);
    }

    // -----------------------------------------------------------------------
    // Rivelazione delle password
    // -----------------------------------------------------------------------

    /// <summary>
    /// Alterna fra campo mascherato e campo in chiaro per la password SIP.
    /// </summary>
    /// <remarks>
    /// Non è una comodità: chi non vede non può controllare a schermo cosa ha
    /// digitato, e un errore di battitura in una password si manifesta come
    /// "il telefono non si registra", sintomo che non punta alla causa.
    /// </remarks>
    private void OnShowSipPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ShowSipPassword.IsChecked == true)
        {
            SipPasswordPlain.Text = SipPassword.Password;
            SipPasswordPlain.Visibility = Visibility.Visible;
            SipPassword.Visibility = Visibility.Collapsed;
            SipPasswordPlain.Focus();
            _announcer.Announce(Announcement.CommandResponse("Password visibile", DateTimeOffset.Now));
        }
        else
        {
            SipPassword.Password = SipPasswordPlain.Text;
            SipPassword.Visibility = Visibility.Visible;
            SipPasswordPlain.Visibility = Visibility.Collapsed;
            SipPassword.Focus();
            _announcer.Announce(Announcement.CommandResponse("Password mascherata", DateTimeOffset.Now));
        }
    }

    private void OnShowPbxSecretChanged(object sender, RoutedEventArgs e)
    {
        if (ShowPbxSecret.IsChecked == true)
        {
            PbxClientSecretPlain.Text = PbxClientSecret.Password;
            PbxClientSecretPlain.Visibility = Visibility.Visible;
            PbxClientSecret.Visibility = Visibility.Collapsed;
            PbxClientSecretPlain.Focus();
            _announcer.Announce(Announcement.CommandResponse("Client Secret visibile", DateTimeOffset.Now));
        }
        else
        {
            PbxClientSecret.Password = PbxClientSecretPlain.Text;
            PbxClientSecret.Visibility = Visibility.Visible;
            PbxClientSecretPlain.Visibility = Visibility.Collapsed;
            PbxClientSecret.Focus();
            _announcer.Announce(Announcement.CommandResponse("Client Secret mascherato", DateTimeOffset.Now));
        }
    }

    // -----------------------------------------------------------------------
    // Ausiliari
    // -----------------------------------------------------------------------

    /// <summary>Scrive un esito nella regione dinamica e lo annuncia.</summary>
    private void Report(string message)
    {
        LiveRegion.Update(ResultText, message);
        _announcer.Announce(Announcement.CommandResponse(message, DateTimeOffset.Now));
    }

    private static string? SelectedTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private static string? SelectedName(ComboBox combo)
    {
        var item = combo.SelectedItem as ComboBoxItem;
        return item?.Tag is null ? null : item.Content?.ToString();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static TEnum ParseEnum<TEnum>(string? text, TEnum fallback) where TEnum : struct
        => Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) ? value : fallback;

    // -----------------------------------------------------------------------
    // Tasti rapidi
    // -----------------------------------------------------------------------

    /// <summary>Righe della tabella dei comandi, una per comando assegnabile.</summary>
    /// <remarks>
    /// L'insieme delle righe non cambia mai: sono i comandi del catalogo, sempre
    /// tutti e sempre nello stesso ordine. Cambiano soltanto le due celle delle
    /// combinazioni, aggiornate sul posto. È una scelta di accessibilità: ricostruire
    /// la collezione a ogni modifica farebbe perdere il focus alla tabella, e
    /// l'operatore si ritroverebbe il cursore all'inizio dell'elenco dopo ogni
    /// assegnazione, cioè trenta righe più in alto di dove stava lavorando.
    /// </remarks>
    private readonly ObservableCollection<HotkeyRow> _hotkeyRows = [];

    /// <summary>Profilo in modifica: una bozza, finché non viene salvata.</summary>
    private HotkeyProfile _hotkeyWorking = HotkeyProfile.CreateDefault();

    /// <summary>Contenuto del profilo all'ultimo salvataggio, per contare le modifiche.</summary>
    private HotkeyProfile _hotkeySaved = HotkeyProfile.CreateDefault();

    /// <summary>Sospende la reazione al cambio di voce mentre l'elenco viene ricostruito.</summary>
    private bool _hotkeyComboLoading;

    private void LoadHotkeys()
    {
        foreach (var category in HotkeyCommandCatalog.Categories)
        {
            foreach (var info in HotkeyCommandCatalog.InCategory(category))
            {
                _hotkeyRows.Add(new HotkeyRow(info));
            }
        }

        FillHotkeyProfileCombo(_hotkeyStore.Active.Name);
        LoadHotkeyProfile(_hotkeyStore.Active.Name);

        if (_hotkeyStore.IgnoredBindings > 0)
        {
            // Va detto all'apertura del pannello: una scorciatoia sparita in silenzio
            // si manifesta come "quel tasto non fa piu' niente", sintomo che nessuno
            // riconduce a un file di configurazione letto a meta'.
            LiveRegion.Update(
                HotkeyProfileHint,
                $"Attenzione: {_hotkeyStore.IgnoredBindings} assegnazioni del file dei tasti non erano "
                + "interpretabili e sono state ignorate. Controlla i comandi senza combinazione.");
        }
    }

    private void FillHotkeyProfileCombo(string? selected)
    {
        _hotkeyComboLoading = true;

        HotkeyProfileCombo.Items.Clear();

        foreach (var name in _hotkeyStore.ProfileNames)
        {
            var active = string.Equals(name, _hotkeyStore.Active.Name, StringComparison.CurrentCultureIgnoreCase);

            HotkeyProfileCombo.Items.Add(new ComboBoxItem
            {
                // Quale profilo sia attivo va detto nell'elenco stesso: in un menu
                // letto a voce non c'e' modo di dedurlo guardando altrove.
                Content = active ? $"{name} (attivo)" : name,
                Tag = name,
            });
        }

        SelectByTag(HotkeyProfileCombo, selected);
        _hotkeyComboLoading = false;
    }

    private void LoadHotkeyProfile(string? name)
    {
        _hotkeyWorking = _hotkeyStore.Get(name) ?? HotkeyProfile.CreateDefault();
        _hotkeySaved = _hotkeyWorking.Clone();

        SyncHotkeyRows();
        UpdateHotkeyProfileHint();

        if (HotkeyGrid.Items.Count > 0 && HotkeyGrid.SelectedIndex < 0) HotkeyGrid.SelectedIndex = 0;
    }

    /// <summary>Riporta nelle righe le combinazioni del profilo in modifica.</summary>
    private void SyncHotkeyRows()
    {
        foreach (var row in _hotkeyRows)
        {
            row.Local = _hotkeyWorking.Get(row.Command, HotkeyScope.Local);
            row.Global = _hotkeyWorking.Get(row.Command, HotkeyScope.Global);
        }

        UpdateHotkeyProfileHint();
    }

    private void UpdateHotkeyProfileHint()
    {
        var changes = CountHotkeyChanges();
        var assigned = _hotkeyRows.Count(r => r.Local is not null || r.Global is not null);

        var state = changes == 0
            ? "nessuna modifica da salvare"
            : changes == 1 ? "una modifica da salvare" : $"{changes} modifiche da salvare";

        var activeNote = string.Equals(
            _hotkeyWorking.Name, _hotkeyStore.Active.Name, StringComparison.CurrentCultureIgnoreCase)
            ? "e' il profilo attivo"
            : $"non e' attivo: il profilo in uso e' {_hotkeyStore.Active.Name}";

        LiveRegion.Update(
            HotkeyProfileHint,
            $"Profilo {_hotkeyWorking.Name}: {activeNote}. Comandi con almeno una combinazione: "
            + $"{assigned} su {_hotkeyRows.Count}. Stato: {state}.");
    }

    /// <summary>
    /// Conta le assegnazioni che differiscono dall'ultimo salvataggio.
    /// </summary>
    /// <remarks>
    /// Si contano le differenze in entrambe le direzioni: una combinazione rimossa è
    /// una modifica esattamente come una aggiunta, e chi chiude il pannello deve
    /// sapere che sta per perderla.
    /// </remarks>
    private int CountHotkeyChanges()
    {
        var before = _hotkeySaved.Bindings.Select(Signature).ToHashSet(StringComparer.Ordinal);
        var after = _hotkeyWorking.Bindings.Select(Signature).ToHashSet(StringComparer.Ordinal);

        return before.Except(after).Count() + after.Except(before).Count();

        static string Signature(HotkeyBinding binding)
            => $"{binding.Command}|{binding.Scope}|{binding.Modifiers}|{binding.Key.ToUpperInvariant()}";
    }

    private bool HasHotkeyChanges() => CountHotkeyChanges() > 0;

    // ---------------------------- selezione ----------------------------

    private void OnHotkeyProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_hotkeyComboLoading) return;

        var name = SelectedTag(HotkeyProfileCombo);
        if (name is null) return;
        if (string.Equals(name, _hotkeyWorking.Name, StringComparison.CurrentCultureIgnoreCase)) return;

        if (HasHotkeyChanges())
        {
            var answer = MessageBox.Show(
                this,
                $"Hai {CountHotkeyChanges()} modifiche non salvate sul profilo {_hotkeyWorking.Name}.\n\n"
                + $"Caricando il profilo {name} andranno perse.\n\nProcedere?",
                "Modifiche ai tasti non salvate",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                FillHotkeyProfileCombo(_hotkeyWorking.Name);
                Report("Cambio di profilo annullato: resti sulle modifiche in corso");
                return;
            }
        }

        LoadHotkeyProfile(name);

        Report($"Profilo {name} caricato nell'elenco. Non e' ancora attivo: "
               + "premi Salva la configurazione dei tasti per applicarlo");
    }

    private void OnHotkeySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Nessun annuncio: la tabella e' un controllo standard e lo screen reader
        // legge gia' la cella su cui arriva il cursore. La descrizione resta a
        // schermo per chi la rilegge con il cursore virtuale.
        if (HotkeyGrid.SelectedItem is HotkeyRow row) HotkeyDescriptionText.Text = row.Description;
    }

    /// <summary>
    /// Ricava l'ambito dalla colonna su cui si trova il cursore.
    /// </summary>
    /// <remarks>
    /// È il meccanismo che permette due assegnazioni per riga senza raddoppiare le
    /// righe. Sulle colonne che non sono combinazioni si ricade sull'ambito locale,
    /// che è quello che si modifica quasi sempre.
    /// </remarks>
    private HotkeyScope CurrentHotkeyScope()
        => HotkeyGrid.CurrentCell.Column?.DisplayIndex == 2 ? HotkeyScope.Global : HotkeyScope.Local;

    private HotkeyRow? CurrentHotkeyRow()
        => HotkeyGrid.CurrentCell.Item as HotkeyRow ?? HotkeyGrid.SelectedItem as HotkeyRow;

    // ---------------------------- modifica ----------------------------

    private void OnHotkeyGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CurrentHotkeyRow() is not { } row) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EditHotkey(row, CurrentHotkeyScope());
            return;
        }

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            ClearHotkey(row, CurrentHotkeyScope());
        }
    }

    private void OnHotkeyGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CurrentHotkeyRow() is { } row) EditHotkey(row, CurrentHotkeyScope());
    }

    private void OnHotkeyEditClick(object sender, RoutedEventArgs e)
    {
        if (CurrentHotkeyRow() is { } row) EditHotkey(row, CurrentHotkeyScope());
        else Report("Scegli prima un comando nell'elenco");
    }

    private void OnHotkeyClearClick(object sender, RoutedEventArgs e)
    {
        if (CurrentHotkeyRow() is { } row) ClearHotkey(row, CurrentHotkeyScope());
        else Report("Scegli prima un comando nell'elenco");
    }

    /// <summary>
    /// Assegna una nuova combinazione a un comando, verificando i conflitti.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il conflitto va risolto <b>prima</b> di salvare e va detto a parole. Per un
    /// operatore non vedente una combinazione assegnata due volte non si manifesta
    /// come un avviso ma come un comando che ne esegue un altro, e il collegamento
    /// con la configurazione fatta settimane prima non lo fa nessuno.
    /// </para>
    /// <para>
    /// Quando l'operatore conferma la sostituzione, la combinazione viene <b>tolta</b>
    /// al comando che la occupava. Lasciarla a entrambi renderebbe non deterministico
    /// quale dei due parte, che su "Riaggancia" contro "Rispondi" è un guaio serio.
    /// </para>
    /// </remarks>
    private void EditHotkey(HotkeyRow row, HotkeyScope scope)
    {
        var current = _hotkeyWorking.Get(row.Command, scope);

        var dialog = new HotkeyCaptureWindow(row.CommandLabel, scope, current, _announcer) { Owner = this };

        if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.CapturedKey))
        {
            Report($"{row.CommandLabel}: combinazione invariata");
            return;
        }

        var candidate = new HotkeyBinding(row.Command, dialog.CapturedKey, dialog.CapturedModifiers, scope);

        if (current is not null && current.ConflictKey == candidate.ConflictKey)
        {
            Report($"{row.CommandLabel}: {candidate.SpeechText} era gia' la combinazione assegnata");
            return;
        }

        var conflict = _hotkeyConflicts.Check(candidate, _hotkeyWorking, HotkeyCommandCatalog.Labels);

        if (!conflict.IsClear)
        {
            if (!conflict.CanOverride)
            {
                Report(conflict.Message);
                return;
            }

            var answer = MessageBox.Show(
                this,
                conflict.Message,
                "Conflitto di combinazione",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                Report($"{row.CommandLabel}: assegnazione annullata, la combinazione resta com'era");
                return;
            }

            foreach (var other in _hotkeyWorking.Bindings
                         .Where(b => b.Command != row.Command && b.ConflictKey == candidate.ConflictKey)
                         .ToList())
            {
                _hotkeyWorking.Remove(other.Command, other.Scope);
            }
        }

        _hotkeyWorking.Set(candidate);
        SyncHotkeyRows();

        // L'avviso sul campo di composizione non blocca ed e' deliberato: un tasto
        // singolo puo' essere la scelta giusta per chi non usa mai il dialer. Ma
        // scoprirlo da soli richiederebbe di collegare "il comando a volte non parte"
        // alla posizione del cursore, cosa che nessuno fa.
        var swallowed = scope == HotkeyScope.Local
                        && candidate.Modifiers == HotkeyModifiers.None
                        && HotkeyKeyNames.IsSwallowedByDialer(candidate.Key);

        var advisory = swallowed
            ? " Attenzione: senza modificatori questo tasto compone il numero, quindi il comando "
              + "non parte mentre il cursore e' nel campo di composizione."
            : string.Empty;

        var scopeLabel = scope == HotkeyScope.Global ? "globale" : "locale";

        Report($"{row.CommandLabel}: combinazione {scopeLabel} {candidate.SpeechText}.{advisory} "
               + "Premi Salva la configurazione dei tasti per applicarla");
    }

    private void ClearHotkey(HotkeyRow row, HotkeyScope scope)
    {
        var scopeLabel = scope == HotkeyScope.Global ? "globale" : "locale";

        if (_hotkeyWorking.Get(row.Command, scope) is null)
        {
            Report($"{row.CommandLabel}: non c'era nessuna combinazione {scopeLabel} da togliere");
            return;
        }

        _hotkeyWorking.Remove(row.Command, scope);
        SyncHotkeyRows();

        Report($"{row.CommandLabel}: combinazione {scopeLabel} rimossa. "
               + "Il comando resta disponibile ma senza tasto assegnato");
    }

    private void OnHotkeyResetClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Tutte le combinazioni tornano ai valori predefiniti di BlindPhone.\n\n"
            + "Le modifiche non salvate vanno perse. I profili gia' salvati non vengono toccati.\n\n"
            + "Procedere?",
            "Ripristina i tasti predefiniti",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            Report("Ripristino annullato");
            return;
        }

        _hotkeyWorking.ReplaceAll(HotkeyProfile.CreateDefault().Bindings);
        SyncHotkeyRows();

        Report("Combinazioni riportate ai valori predefiniti. "
               + "Premi Salva la configurazione dei tasti per applicarle");
    }

    // ---------------------------- salvataggio ----------------------------

    /// <summary>
    /// Apre la scelta di come salvare la configurazione dei tasti.
    /// </summary>
    /// <remarks>
    /// Quattro destinazioni dietro un solo pulsante: aggiornare il profilo in uso,
    /// crearne uno nuovo, produrre un file per un'altra postazione, produrre la scheda
    /// da leggere o stampare in braille. Sono quattro esigenze diverse dello stesso
    /// momento — "ho finito di configurare" — e tenerle in un unico punto evita di
    /// costringere l'operatore a percorrere quattro pulsanti per capire quali sono.
    /// </remarks>
    private void OnHotkeySaveClick(object sender, RoutedEventArgs e)
    {
        var isBuiltIn = HotkeyProfile.IsBuiltIn(_hotkeyWorking.Name);

        var proposed = _hotkeyStore.ProposeName(
            isBuiltIn ? $"{_hotkeyWorking.Name} personalizzato" : _hotkeyWorking.Name);

        var dialog = new HotkeySaveWindow(
            _hotkeyWorking.Name, isBuiltIn, proposed, CountHotkeyChanges(), _announcer)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            Report("Salvataggio dei tasti annullato: nulla e' cambiato");
            return;
        }

        switch (dialog.Kind)
        {
            case HotkeySaveKind.UpdateCurrent:
                ApplyHotkeyProfile(_hotkeyWorking.Name);
                break;

            case HotkeySaveKind.SaveAsNew:
                ApplyHotkeyProfile(_hotkeyStore.ProposeName(dialog.NewProfileName));
                break;

            case HotkeySaveKind.ExportProfile:
                ExportHotkeyProfile();
                break;

            case HotkeySaveKind.ExportCard:
                ExportHotkeyCard();
                break;
        }
    }

    /// <summary>
    /// Registra il profilo in modifica e lo rende attivo.
    /// </summary>
    /// <remarks>
    /// Le combinazioni diventano operative subito, senza riavviare: quelle locali
    /// perché il profilo attivo è un'istanza condivisa, quelle globali perché la
    /// finestra principale le registra di nuovo presso Windows quando il deposito
    /// segnala il cambiamento. È il comportamento che serve: una scorciatoia si
    /// verifica provandola, e chiedere un riavvio dopo ogni tentativo renderebbe la
    /// configurazione impraticabile.
    /// </remarks>
    /// <param name="name">Nome con cui registrare il profilo.</param>
    /// <param name="report">
    /// Se annunciare l'esito. Va disattivato quando il salvataggio avviene dentro
    /// quello generale delle impostazioni, dove l'esito viene detto una volta sola
    /// per tutto: due annunci ravvicinati si accavallano e il secondo copre il primo.
    /// </param>
    /// <returns><c>true</c> se il profilo è stato scritto su disco.</returns>
    private bool ApplyHotkeyProfile(string name, bool report = true)
    {
        var toSave = _hotkeyWorking.Clone(name);
        var saved = _hotkeyStore.SaveAndActivate(toSave);

        _hotkeyWorking = toSave;
        _hotkeySaved = toSave.Clone();

        // Il nome del profilo attivo lo registra il deposito dentro hotkeys.json, ed
        // e' l'unico posto in cui vive: vedi il commento in AppSettings.

        FillHotkeyProfileCombo(name);
        SyncHotkeyRows();

        if (report)
        {
            Report(saved
                ? $"Profilo {name} salvato e attivo: le combinazioni funzionano da subito"
                : $"Profilo {name} attivato, ma la scrittura su disco non e' riuscita: "
                  + "alla chiusura di BlindPhone andrebbe perso. Verifica i permessi sulla cartella dati");
        }

        return saved;
    }

    /// <summary>
    /// Salva le modifiche ai tasti insieme al salvataggio generale.
    /// </summary>
    /// <returns>Frase da aggiungere all'esito, oppure stringa vuota se non c'era nulla da salvare.</returns>
    /// <remarks>
    /// La scheda dei tasti ha un pulsante proprio, ma chi preme Salva si aspetta che
    /// salvi tutto quello che ha toccato. Perdere in silenzio venti minuti di
    /// riassegnazioni perché si è usato il pulsante "sbagliato" è il tipo di
    /// comportamento che fa perdere fiducia in un pannello di configurazione.
    /// Un profilo predefinito non si sovrascrive: le modifiche finiscono in una copia
    /// con un nome libero, e il nome viene detto.
    /// </remarks>
    private string SaveHotkeysIfChanged()
    {
        if (!HasHotkeyChanges()) return string.Empty;

        var isBuiltIn = HotkeyProfile.IsBuiltIn(_hotkeyWorking.Name);

        var name = isBuiltIn
            ? _hotkeyStore.ProposeName($"{_hotkeyWorking.Name} personalizzato")
            : _hotkeyWorking.Name;

        var saved = ApplyHotkeyProfile(name, report: false);

        if (!saved) return $" Attenzione: i tasti rapidi del profilo {name} sono attivi ma non salvati su disco.";

        return isBuiltIn
            ? $" Tasti rapidi salvati nel nuovo profilo {name}, gia' attivo."
            : $" Tasti rapidi del profilo {name} salvati e gia' attivi.";
    }

    private void ExportHotkeyProfile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Esporta il profilo dei tasti rapidi",
            Filter = "Profilo BlindPhone (*.json)|*.json|Tutti i file|*.*",
            FileName = $"BlindPhone-tasti-{Sanitize(_hotkeyWorking.Name)}.json",
            AddExtension = true,
            DefaultExt = ".json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            Report("Esportazione annullata");
            return;
        }

        Report(_hotkeyStore.ExportProfile(_hotkeyWorking, dialog.FileName)
            ? $"Profilo esportato in {Path.GetFileName(dialog.FileName)}. "
              + "Sull'altra postazione usa il pulsante Importa di questa stessa scheda"
            : "Esportazione non riuscita. Verifica i permessi sulla cartella scelta");
    }

    private void ExportHotkeyCard()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Esporta la scheda dei tasti rapidi",
            Filter = "Testo semplice (*.txt)|*.txt|Tutti i file|*.*",
            FileName = $"BlindPhone-scheda-tasti-{Sanitize(_hotkeyWorking.Name)}.txt",
            AddExtension = true,
            DefaultExt = ".txt",
        };

        if (dialog.ShowDialog(this) != true)
        {
            Report("Esportazione annullata");
            return;
        }

        Report(_hotkeyStore.ExportCard(_hotkeyWorking, dialog.FileName, DateTimeOffset.Now)
            ? $"Scheda dei tasti scritta in {Path.GetFileName(dialog.FileName)}. "
              + "E' testo semplice: si legge con lo screen reader e si manda a una stampante braille"
            : "Scrittura della scheda non riuscita. Verifica i permessi sulla cartella scelta");
    }

    private void OnHotkeyImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importa un profilo di tasti rapidi",
            Filter = "Profilo BlindPhone (*.json)|*.json|Tutti i file|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        var imported = _hotkeyStore.Import(dialog.FileName);

        if (imported is null)
        {
            Report("Il file non contiene un profilo di tasti rapidi leggibile");
            return;
        }

        FillHotkeyProfileCombo(imported.Name);
        LoadHotkeyProfile(imported.Name);

        // Non viene attivato da solo: un profilo che arriva da un'altra postazione
        // va guardato prima di ritrovarselo sotto le dita durante un turno.
        Report($"Profilo {imported.Name} importato e caricato nell'elenco. "
               + "Controllalo, poi premi Salva la configurazione dei tasti per attivarlo");
    }

    private void OnHotkeyDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        var name = SelectedTag(HotkeyProfileCombo);

        if (name is null)
        {
            Report("Scegli prima un profilo");
            return;
        }

        if (HotkeyProfile.IsBuiltIn(name))
        {
            Report($"Il profilo {name} e' fornito con BlindPhone e non si puo' eliminare. "
                   + "Puoi modificarlo e salvarne una copia con un nome tuo");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Stai per eliminare il profilo di tasti rapidi {name}.\n\n"
            + "L'operazione non si puo' annullare.\n\nProcedere?",
            "Elimina il profilo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            Report("Eliminazione annullata");
            return;
        }

        if (_hotkeyStore.Delete(name) is { } error)
        {
            Report(error);
            return;
        }

        FillHotkeyProfileCombo(_hotkeyStore.Active.Name);
        LoadHotkeyProfile(_hotkeyStore.Active.Name);

        Report($"Profilo {name} eliminato. Ora e' in uso il profilo {_hotkeyStore.Active.Name}");
    }

    /// <summary>Ripulisce un nome di profilo per usarlo come nome di file.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = new string([.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)]);
        return string.IsNullOrWhiteSpace(cleaned) ? "profilo" : cleaned.Trim();
    }

    // -----------------------------------------------------------------------
    // Rubrica: importazione da CSV
    // -----------------------------------------------------------------------

    /// <summary>Colonne trovate nell'ultimo file analizzato.</summary>
    private IReadOnlyList<string> _csvHeaders = [];

    /// <summary>L'operatore ha deciso lui se il file abbia la riga dei titoli.</summary>
    private bool _csvHeaderModeChosen;

    /// <summary>Evita che l'allineamento della spunta si scambi per una scelta.</summary>
    private bool _settingHeaderMode;

    /// <summary>Elenco delle caselle di abbinamento, per iterarle senza ripetersi.</summary>
    private IEnumerable<(ComboBox Combo, string Label)> MappingCombos =>
    [
        (MapNumber, "Numero"),
        (MapSecondNumber, "Secondo numero"),
        (MapRelatedTo, "Relativo"),
        (MapCategory, "Descrizione"),
        (MapFirstName, "Nome"),
        (MapLastName, "Cognome"),
        (MapDepartment, "Reparto"),
        (MapBuilding, "Padiglione"),
        (MapFloor, "Piano"),
    ];

    /// <summary>
    /// Colonne che la sorgente dichiara di sé e che la procedura guidata non chiede.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arrivano dal profilo riconosciuto o dalle impostazioni già salvate, e vanno
    /// conservate qui perché <see cref="BuildMappingFromUi"/> compone la mappatura
    /// dalle sole caselle a schermo: senza questo, ogni passaggio dalla procedura
    /// guidata cancellerebbe l'identificativo stabile delle voci, e al
    /// riallineamento successivo l'intera rubrica si presenterebbe come nuova —
    /// preferiti e numeri rapidi compresi.
    /// </para>
    /// </remarks>
    private CsvColumnMapping _structuralMapping = new();

    /// <summary>Profilo riconosciuto nell'ultima analisi, se ce n'è uno.</summary>
    private ContactSourceProfile? _recognizedProfile;

    /// <summary>Tabelle trovate nel backup, quando il file è un backup.</summary>
    private IReadOnlyList<SqlTableInfo> _backupTables = [];

    /// <summary>Impedisce che il riempimento dell'elenco scateni una rianalisi.</summary>
    private bool _fillingBackupTables;

    /// <summary>Il file scelto è un backup di banca dati e non un CSV.</summary>
    private bool IsBackupSelected => _backupTables.Count > 0;

    private async void OnBrowseCsvClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Scegli il file della rubrica",
            Filter =
                "Tutti i formati supportati|*.csv;*.txt;*.sql;*.db;*.sqlite;*.sqlite3;*.bak;*.bkp;*.dump;*.dmp;*.backup"
                + "|File CSV (*.csv;*.txt)|*.csv;*.txt"
                + "|Backup di banca dati (*.sql;*.db;*.sqlite;*.bak;*.bkp)|*.sql;*.db;*.sqlite;*.sqlite3;*.bak;*.bkp;*.dump;*.dmp;*.backup"
                + "|Tutti i file (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        CsvPath.Text = dialog.FileName;

        // Un file nuovo merita un rilevamento nuovo: la scelta fatta sul precedente
        // non ha motivo di valere anche per questo.
        _csvHeaderModeChosen = false;

        // L'analisi parte da sola dopo la scelta del file: chiedere una seconda
        // azione esplicita costringerebbe a cercare un altro pulsante senza che
        // nulla sia cambiato nel frattempo.
        await AnalyzeCsvAsync().ConfigureAwait(true);
    }

    private async void OnAnalyzeCsvClick(object sender, RoutedEventArgs e)
        => await AnalyzeCsvAsync().ConfigureAwait(true);

    /// <summary>
    /// Legge le intestazioni del file, propone l'abbinamento e mostra l'anteprima.
    /// </summary>
    private async Task AnalyzeCsvAsync()
    {
        var path = CsvPath.Text.Trim();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Report("File non trovato. Verifica il percorso");
            return;
        }

        // UN BACKUP E UN CSV SI DISTINGUONO PRIMA DI LEGGERLI COME TABELLA.
        //
        // Il riconoscimento binario ha la precedenza sull'estensione, perche' un
        // mysqldump rinominato .bak e' comunissimo negli uffici. Fra i file di testo
        // decide invece l'estensione: un CSV e un dump SQL sono entrambi testo, e
        // stabilire quale sia scandendo il contenuto costringerebbe ad attraversare
        // per intero ogni CSV solo per non trovarci istruzioni SQL.
        if (LooksLikeBackup(path))
        {
            _importKind = ImportKind.Backup;
            await AnalyzeBackupAsync(path).ConfigureAwait(true);
            return;
        }

        _importKind = ImportKind.Csv;

        try
        {
            ShowBackupTables([]);

            // La spunta ha la precedenza sull'euristica solo dopo che l'operatore
            // l'ha toccata: alla prima analisi decide il rilevamento, e la spunta si
            // allinea a quello che ha deciso.
            var layout = CsvContactSource.Inspect(path, _csvHeaderModeChosen ? CsvNoHeader.IsChecked != true : null);

            if (layout.Columns.Count == 0)
            {
                Report("Il file e' vuoto: non c'e' nessuna colonna da abbinare");
                return;
            }

            if (!_csvHeaderModeChosen)
            {
                _settingHeaderMode = true;
                try { CsvNoHeader.IsChecked = layout.LooksHeaderless; }
                finally { _settingHeaderMode = false; }
            }

            _csvHeaders = layout.Columns;

            FillMappingCombos(layout.Columns, layout.Labels);

            // Senza titoli non c'e' niente da riconoscere: i nomi delle colonne sono
            // posizioni, e l'abbinamento lo fa l'operatore.
            var senzaTitoli = CsvNoHeader.IsChecked == true;

            if (senzaTitoli)
            {
                _recognizedProfile = null;
                _structuralMapping = new CsvColumnMapping();
            }
            else
            {
                ApplyMappingFor(layout.Columns);
            }

            var source = BuildCsvSource(path);
            LiveRegion.Update(
                CsvFormatText,
                $"Formato: delimitatore {source.DelimiterDescription}, codifica {source.EncodingName}, "
                + $"{layout.Columns.Count} colonne"
                + (senzaTitoli ? ", senza riga dei titoli" : string.Empty));

            await RefreshPreviewAsync((righe, token) => source.PreviewAsync(righe, token)).ConfigureAwait(true);

            // Si dice quante colonne sono state riconosciute: e' l'informazione che
            // permette di capire, senza guardare, se il riconoscimento automatico ha
            // funzionato o se c'e' da rimettere le mani sugli abbinamenti.
            var recognized = MappingCombos.Count(m => SelectedTag(m.Combo) is not null);

            Report(senzaTitoli
                ? $"File analizzato: {layout.Columns.Count} colonne, senza riga dei titoli. "
                  + "Abbina le colonne per posizione: ognuna porta un esempio preso dalla prima riga"
                : $"File analizzato: {layout.Columns.Count} colonne, {recognized} abbinate automaticamente. "
                  + "Controlla l'anteprima prima di importare");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Analisi del file CSV non riuscita");
            Report(ContactFileErrors.Describe(ex));
        }
    }

    // -----------------------------------------------------------------------
    // Rubrica collegata a Microsoft SQL Server
    // -----------------------------------------------------------------------

    /// <summary>
    /// Da dove la procedura guidata sta leggendo in questo momento.
    /// </summary>
    /// <remarks>
    /// La procedura e' una sola per tutte e tre le provenienze: cambia solo chi
    /// fornisce le righe. Tenere il tipo qui, invece di dedurlo ogni volta dallo stato
    /// dei controlli, evita che due percorsi si contraddicano su cosa stia importando.
    /// </remarks>
    private enum ImportKind
    {
        Csv,
        Backup,
        SqlServer,
    }

    private ImportKind _importKind = ImportKind.Csv;

    /// <summary>
    /// Servizio di riallineamento, preso dal contenitore.
    /// </summary>
    /// <remarks>
    /// Si risolve qui invece di aggiungerlo al costruttore: serve solo a chi apre la
    /// scheda della rubrica, e la finestra delle impostazioni ha gia' otto dipendenze.
    /// </remarks>
    private ContactsSyncService _contactsSync
        => _serviceProvider.GetRequiredService<ContactsSyncService>();

    /// <summary>Riporta a schermo il collegamento salvato.</summary>
    /// <param name="settings">Impostazioni della rubrica.</param>
    private void CaricaRubricaCollegata(ContactsSettings settings)
    {
        SelectByTag(ContactsSourceMode, settings.Mode == ContactsMode.SqlServer ? "SqlServer" : "LocalFile");

        var server = settings.SqlServer;

        SqlServerHost.Text = server.Server;
        SqlServerDatabase.Text = server.Database;
        SelectByTag(SqlServerAuth, server.UseWindowsAuthentication ? "Windows" : "SqlLogin");
        SqlServerUser.Text = server.Username ?? string.Empty;
        SqlServerPassword.Password = _settingsService.Secrets.GetForHost(
            server.PasswordSecretKey,
            server.Server,
            "la password del server della rubrica");
        SqlServerTrustCertificate.IsChecked = server.TrustServerCertificate;
        SelectByTag(SqlServerInterval, server.SyncIntervalMinutes.ToString(CultureInfo.InvariantCulture));

        SqlServerPanel.Visibility = settings.Mode == ContactsMode.SqlServer
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Le colonne strutturali salvate tornano in memoria anche se le caselle
        // restano vuote: non hanno una casella, e senza questo un salvataggio fatto
        // dopo essersi ricollegati alla stessa tabella le perderebbe.
        _structuralMapping = new CsvColumnMapping
        {
            EntryKey = server.Mapping.EntryKey,
            ShortCodeFlag = server.Mapping.ShortCodeFlag,
            InactiveFlag = server.Mapping.InactiveFlag,
            UpdatedAt = server.Mapping.UpdatedAt,
        };

        // La tabella salvata non compare finche' non ci si collega: l'elenco lo
        // fornisce il server, e mostrarne una sola voce presa dalle impostazioni
        // farebbe credere che il collegamento sia gia' stato verificato.
        OnSqlServerAuthChanged(this, null!);
    }

    /// <summary>Compone le impostazioni della rubrica da salvare.</summary>
    /// <remarks>
    /// La tabella e l'abbinamento si conservano solo se ci si e' collegati in questa
    /// sessione: altrimenti resterebbero quelli salvati, che e' cio' che si vuole
    /// quando si apre la finestra per cambiare tutt'altro.
    /// </remarks>
    private ContactsSettings ComponiRubricaCollegata(ContactsSettings precedenti)
    {
        var collegata = string.Equals(SelectedTag(ContactsSourceMode), "SqlServer", StringComparison.Ordinal);
        var corrente = CurrentSqlServerSettings();

        var server = _importKind == ImportKind.SqlServer
            ? corrente
            : precedenti.SqlServer with
            {
                Server = corrente.Server,
                Database = corrente.Database,
                UseWindowsAuthentication = corrente.UseWindowsAuthentication,
                Username = corrente.Username,
                TrustServerCertificate = corrente.TrustServerCertificate,
                SyncIntervalMinutes = corrente.SyncIntervalMinutes,
            };

        return new ContactsSettings
        {
            Mode = collegata ? ContactsMode.SqlServer : ContactsMode.LocalFile,
            SqlServer = server,

            // Le spunte tolte tutte sono una scelta legittima — solo nominativo e
            // interno — e vanno salvate come elenco vuoto, non come "mai deciso".
            DialerFields = [.. _dialerFields.Where(f => f.IsEnabled).Select(f => f.Field.Id)],
        };
    }

    private void OnContactsModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var collegata = string.Equals(SelectedTag(ContactsSourceMode), "SqlServer", StringComparison.Ordinal);

        SqlServerPanel.Visibility = collegata ? Visibility.Visible : Visibility.Collapsed;

        if (!collegata && _importKind == ImportKind.SqlServer)
        {
            // Tornando alla rubrica da file l'elenco delle tabelle del server non ha
            // piu' significato: lasciarlo a schermo inviterebbe a importare da un
            // collegamento che non e' piu' quello scelto.
            ShowBackupTables([]);
            _importKind = ImportKind.Csv;
        }
    }

    private void OnSqlServerAuthChanged(object sender, SelectionChangedEventArgs e)
    {
        var conPassword = string.Equals(SelectedTag(SqlServerAuth), "SqlLogin", StringComparison.Ordinal);

        SqlServerUser.IsEnabled = conPassword;
        SqlServerPassword.IsEnabled = conPassword;
    }

    /// <summary>Impostazioni del collegamento come stanno a schermo.</summary>
    private ContactsSqlServerSettings CurrentSqlServerSettings() => new()
    {
        Server = SqlServerHost.Text.Trim(),
        Database = SqlServerDatabase.Text.Trim(),
        UseWindowsAuthentication = !string.Equals(SelectedTag(SqlServerAuth), "SqlLogin", StringComparison.Ordinal),
        Username = SqlServerUser.Text.Trim(),
        Table = _backupTables.Count > 0 && BackupTable.SelectedIndex >= 0
            ? _backupTables[BackupTable.SelectedIndex].Name
            : string.Empty,
        TrustServerCertificate = SqlServerTrustCertificate.IsChecked == true,
        SyncIntervalMinutes = ParseInt(SelectedTag(SqlServerInterval), 60),
        Mapping = new ContactsColumnMapping
        {
            Extension = SelectedTag(MapNumber),
            WorkPhone = SelectedTag(MapSecondNumber),
            RelatedTo = SelectedTag(MapRelatedTo),
            Category = SelectedTag(MapCategory),
            FirstName = SelectedTag(MapFirstName),
            LastName = SelectedTag(MapLastName),
            Department = SelectedTag(MapDepartment),
            Building = SelectedTag(MapBuilding),
            Floor = SelectedTag(MapFloor),

            // Non vengono da caselle a schermo: le porta il profilo riconosciuto, e
            // vanno salvate perche' senza l'identificativo stabile ogni
            // riallineamento presenterebbe l'intera rubrica come nuova.
            EntryKey = _structuralMapping.EntryKey,
            ShortCodeFlag = _structuralMapping.ShortCodeFlag,
            InactiveFlag = _structuralMapping.InactiveFlag,
            UpdatedAt = _structuralMapping.UpdatedAt,
        },
    };

    /// <summary>
    /// Si collega al server e riempie l'elenco delle tabelle.
    /// </summary>
    /// <remarks>
    /// La password digitata viene depositata prima del tentativo: se il collegamento
    /// riesce, la prova successiva parte gia' configurata senza doverla ridigitare.
    /// </remarks>
    private async void OnSqlServerConnectClick(object sender, RoutedEventArgs e)
    {
        var impostazioni = CurrentSqlServerSettings();

        if (string.IsNullOrWhiteSpace(impostazioni.Server) || string.IsNullOrWhiteSpace(impostazioni.Database))
        {
            Report("Indica prima il server e la banca dati");
            SqlServerHost.Focus();
            return;
        }

        if (!impostazioni.UseWindowsAuthentication && SqlServerPassword.Password.Length > 0)
        {
            // Legata al server appena indicato: la prova di collegamento e' la
            // richiesta esplicita di usarla verso quell'indirizzo.
            _settingsService.Secrets.SetForHost(
                impostazioni.PasswordSecretKey, SqlServerPassword.Password, impostazioni.Server);
        }

        SqlServerConnectButton.IsEnabled = false;
        Report("Collegamento al server in corso");

        try
        {
            var password = impostazioni.UseWindowsAuthentication
                ? null
                : _settingsService.Secrets.GetForHost(
                    impostazioni.PasswordSecretKey,
                    impostazioni.Server,
                    "la password del server della rubrica");

            var opzioni = ContactsSyncService.ToOptions(impostazioni);

            var tabelle = await Task.Run(() =>
            {
                using var lettore = new SqlServerReader(opzioni, password);
                return lettore.Tables;
            }).ConfigureAwait(true);

            if (tabelle.Count == 0)
            {
                LiveRegion.Update(SqlServerStatusText, "Collegamento riuscito, ma l'utenza non vede nessuna tabella");
                Report("Collegamento riuscito, ma l'utenza non vede nessuna tabella");
                return;
            }

            _importKind = ImportKind.SqlServer;
            ShowBackupTables(tabelle);

            var scelta = PickLikelyTable(tabelle);

            _fillingBackupTables = true;
            try { BackupTable.SelectedIndex = tabelle.ToList().IndexOf(scelta); }
            finally { _fillingBackupTables = false; }

            FillMappingCombos(scelta.Columns);
            var riconoscimento = ApplyMappingFor(scelta.Columns);

            await RefreshServerPreviewAsync().ConfigureAwait(true);

            LiveRegion.Update(
                SqlServerStatusText,
                $"Collegamento riuscito: {tabelle.Count} fra tabelle e viste");

            Report($"Collegamento riuscito: {tabelle.Count} fra tabelle e viste. Proposta {scelta.Name}: "
                   + $"{riconoscimento}. Controlla la tabella e l'anteprima, poi salva");
        }
        catch (SqlException ex)
        {
            _log.LogWarning(ex, "Collegamento a SQL Server non riuscito");

            var motivo = SqlServerContactSource.DescribeSqlFailure(ex);
            LiveRegion.Update(SqlServerStatusText, motivo);
            Report(motivo);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Collegamento a SQL Server non riuscito");
            LiveRegion.Update(SqlServerStatusText, "Collegamento al server non riuscito");
            Report("Collegamento al server non riuscito");
        }
        finally
        {
            SqlServerConnectButton.IsEnabled = true;
        }
    }

    private async void OnSqlServerSyncClick(object sender, RoutedEventArgs e)
    {
        SqlServerSyncButton.IsEnabled = false;
        Report("Riallineamento della rubrica in corso");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var esito = await _contactsSync.SyncAsync(timeout.Token).ConfigureAwait(true);

            LiveRegion.Update(SqlServerStatusText, esito.Message);
            Report(esito.Message);

            await RefreshContactCountAsync().ConfigureAwait(true);
        }
        finally
        {
            SqlServerSyncButton.IsEnabled = true;
        }
    }

    private async Task RefreshServerPreviewAsync()
    {
        var sorgente = _contactsSync.BuildSource(CurrentSqlServerSettings());

        await RefreshPreviewAsync((righe, token) => Task.Run(
            () => sorgente.PreviewAsync(righe, token), token)).ConfigureAwait(true);
    }

    // -----------------------------------------------------------------------
    // Importazione da backup di banca dati
    // -----------------------------------------------------------------------

    /// <summary>
    /// Estensioni che dichiarano un backup senza bisogno di guardarci dentro.
    /// </summary>
    /// <remarks>
    /// Non sono un elenco di ammissione: un file con un'estensione diversa viene
    /// comunque riconosciuto dal contenuto. Servono a evitare la lettura delle prime
    /// pagine quando il nome del file basta già.
    /// </remarks>
    private static readonly string[] BackupExtensions =
        [".sql", ".db", ".sqlite", ".sqlite3", ".bak", ".bkp", ".dump", ".dmp", ".backup"];

    /// <summary>
    /// Decide se trattare il file come backup invece che come CSV.
    /// </summary>
    /// <remarks>
    /// <para>
    /// L'ordine è quello della certezza decrescente: prima l'impronta binaria, che non
    /// mente; poi l'estensione, che di solito dice il vero; infine il contenuto, che
    /// risolve i casi in cui il file si chiama in un modo qualsiasi.
    /// </para>
    /// <para>
    /// <b>L'estensione non è più una condizione necessaria.</b> I backup arrivano dagli
    /// uffici con i nomi più vari, e un elenco chiuso rifiuterebbe file validi finché
    /// qualcuno non segnala il nome mancante — cioè al primo cliente che ne ha uno.
    /// </para>
    /// </remarks>
    private static bool LooksLikeBackup(string path)
    {
        try
        {
            if (SqlBackupFile.Detect(path) is SqlBackupFormat.Sqlite or SqlBackupFormat.SqlServerBackup)
                return true;
        }
        catch (IOException)
        {
            return false;
        }

        if (BackupExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return true;

        // Un CSV non contiene CREATE TABLE ne' INSERT INTO nelle sue prime pagine:
        // il rischio di scambiarlo per un dump e' teorico, quello di rifiutare un
        // dump chiamato male era quotidiano.
        return SqlBackupFile.ContainsSqlStatements(path);
    }

    /// <summary>
    /// Apre il backup, ne elenca le tabelle e propone quella che sembra la rubrica.
    /// </summary>
    /// <remarks>
    /// La tabella proposta va sempre confermata, mai applicata in silenzio: vale la
    /// stessa ragione dell'abbinamento delle colonne. Una tabella sbagliata produce
    /// una rubrica in cui i numeri appartengono alle persone sbagliate, e l'errore si
    /// scopre trasferendo una chiamata a chi non c'entra.
    /// </remarks>
    private async Task AnalyzeBackupAsync(string path)
    {
        try
        {
            IReadOnlyList<SqlTableInfo> tabelle;
            string formato;

            using (var lettore = SqlBackupFile.Open(path))
            {
                tabelle = lettore.Tables;
                formato = lettore.FormatDescription;
            }

            if (tabelle.Count == 0)
            {
                ShowBackupTables([]);
                Report("Il backup non contiene tabelle leggibili");
                return;
            }

            ShowBackupTables(tabelle);

            LiveRegion.Update(
                CsvFormatText,
                $"Formato: {formato}, {tabelle.Count} "
                + (tabelle.Count == 1 ? "tabella" : "tabelle"));

            var scelta = PickLikelyTable(tabelle);

            _fillingBackupTables = true;
            try { BackupTable.SelectedIndex = tabelle.ToList().IndexOf(scelta); }
            finally { _fillingBackupTables = false; }

            var riconoscimento = await ApplyBackupTableAsync(path, scelta).ConfigureAwait(true);

            Report($"Backup analizzato: {tabelle.Count} tabelle. Proposta {scelta.Name}: "
                   + $"{riconoscimento}. Controlla la tabella e l'anteprima prima di importare");
        }
        catch (NotSupportedException ex)
        {
            ShowBackupTables([]);

            // Il messaggio dice cosa chiedere a chi ha prodotto il file, e viene
            // pronunciato: un "formato non supportato" lascerebbe l'operatore senza
            // nessuna mossa successiva.
            Report(ex.Message);
        }
        catch (Exception ex)
        {
            ShowBackupTables([]);
            _log.LogError(ex, "Analisi del backup {Percorso} non riuscita", path);
            Report(ContactFileErrors.Describe(ex));
        }
    }

    /// <summary>
    /// Sceglie la tabella che ha più l'aria di essere una rubrica.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il punteggio è il numero di campi che il riconoscimento automatico saprebbe
    /// riempire, con la colonna del numero che pesa quanto tutte le altre insieme:
    /// senza numero non si importa nulla, quindi una tabella che ce l'ha batte
    /// sempre una che ha solo nomi e indirizzi.
    /// </para>
    /// <para>
    /// <b>Contano anche tre cose che il solo elenco delle colonne non dice</b>, e la
    /// ragione per cui contano è che si sono verificate tutte e tre su una rubrica di
    /// centralino vera:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <b>Un oggetto vuoto o non leggibile non è la rubrica.</b> La vista pubblicata
    ///   per i sistemi esterni univa la rubrica alla segreteria di ciascuna voce, e in
    ///   quell'ente nessuna voce aveva una segreteria: restituiva zero righe su
    ///   duemila. Aveva le colonne giuste e il nome giusto, e veniva proposta per
    ///   prima.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Un profilo riconosciuto vale più di qualunque punteggio</b>: significa che
    ///   la tabella è stata identificata per quello che è, non indovinata dai nomi
    ///   delle colonne.
    ///   </description></item>
    ///   <item><description>
    ///   <b>A parità di tutto, la tabella batte la vista.</b> Una vista filtra, unisce
    ///   o restringe, e sulla rubrica di quell'ente le viste facevano tutte e tre.
    ///   </description></item>
    /// </list>
    /// </remarks>
    private static SqlTableInfo PickLikelyTable(IReadOnlyList<SqlTableInfo> tables)
    {
        SqlTableInfo? migliore = null;
        var punteggioMigliore = int.MinValue;

        foreach (var tabella in tables)
        {
            var punteggio = Punteggio(tabella);

            // A parità di punteggio vince la tabella con più righe: fra "anagrafica"
            // e "anagrafica_storico" la rubrica in uso è quasi sempre la più popolata.
            var vince = punteggio > punteggioMigliore
                        || (punteggio == punteggioMigliore
                            && (tabella.RowCount ?? 0) > (migliore?.RowCount ?? 0));

            if (!vince) continue;

            migliore = tabella;
            punteggioMigliore = punteggio;
        }

        return migliore ?? tables[0];

        static int Punteggio(SqlTableInfo tabella)
        {
            // Non leggibile e vuoto vanno in fondo, e non semplicemente in basso: una
            // proposta sbagliata qui costa un'importazione che non produce nulla e non
            // spiega perche'.
            if (!tabella.IsReadable) return -1000;
            if (tabella.RowCount == 0) return -500;

            if (ContactSourceProfile.IsKnown(tabella.Columns)) return 1000 - (tabella.IsView ? 1 : 0);

            var ipotesi = CsvColumnMapping.Guess(tabella.Columns);

            var punteggio = new[]
            {
                ipotesi.FirstName, ipotesi.LastName, ipotesi.DisplayName,
                ipotesi.Department, ipotesi.Organization, ipotesi.Building,
                ipotesi.Floor, ipotesi.Email, ipotesi.JobTitle,
            }.Count(v => v is not null);

            if (ipotesi.Extension is not null || ipotesi.WorkPhone is not null || ipotesi.MobilePhone is not null)
                punteggio += 10;

            return tabella.IsView ? punteggio * 2 : punteggio * 2 + 1;
        }
    }

    private void ShowBackupTables(IReadOnlyList<SqlTableInfo> tables)
    {
        _backupTables = tables;
        _fillingBackupTables = true;

        try
        {
            BackupTable.Items.Clear();

            foreach (var tabella in tables)
            {
                BackupTable.Items.Add(new ComboBoxItem
                {
                    Content = tabella.Description,
                    Tag = tabella.Name,
                });
            }
        }
        finally
        {
            _fillingBackupTables = false;
        }

        BackupTablePanel.Visibility = tables.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnBackupTableChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingBackupTables) return;
        if (BackupTable.SelectedIndex < 0 || BackupTable.SelectedIndex >= _backupTables.Count) return;

        var path = CsvPath.Text.Trim();
        if (_importKind != ImportKind.SqlServer && !File.Exists(path)) return;

        var tabella = _backupTables[BackupTable.SelectedIndex];

        if (_importKind == ImportKind.SqlServer)
        {
            FillMappingCombos(tabella.Columns);
            var riconoscimentoServer = ApplyMappingFor(tabella.Columns);

            await RefreshServerPreviewAsync().ConfigureAwait(true);

            Report($"{tabella.Description}. {riconoscimentoServer}");
            return;
        }

        var riconoscimento = await ApplyBackupTableAsync(path, tabella).ConfigureAwait(true);

        Report($"{tabella.Description}. {riconoscimento}");
    }

    /// <summary>Riempie abbinamenti e anteprima per la tabella scelta.</summary>
    /// <param name="path">Percorso del backup.</param>
    /// <param name="table">Tabella scelta.</param>
    /// <returns>Come riferire all'operatore che cosa è stato riconosciuto.</returns>
    private async Task<string> ApplyBackupTableAsync(string path, SqlTableInfo table)
    {
        FillMappingCombos(table.Columns);
        var riconoscimento = ApplyMappingFor(table.Columns);

        var sorgente = BuildBackupSource(path, table.Name);

        await RefreshPreviewAsync((righe, token) => sorgente.PreviewAsync(righe, token)).ConfigureAwait(true);

        return riconoscimento;
    }

    /// <summary>
    /// Log dedicato alla lettura dei backup, preso dal contenitore.
    /// </summary>
    /// <remarks>
    /// Si risolve qui invece di aggiungerlo al costruttore: il registro serve quando
    /// un backup di un ente non si lascia leggere, ed e' un caso che si diagnostica
    /// sul pacchetto di assistenza, non in laboratorio.
    /// </remarks>
    private ILogger<SqlBackupContactSource> BackupLogger
        => _serviceProvider.GetService<ILogger<SqlBackupContactSource>>()
           ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlBackupContactSource>.Instance;

    private SqlBackupContactSource BuildBackupSource(string path, string table)
        => new(path, table, BuildMappingFromUi(), BackupLogger);

    /// <summary>
    /// Popola le caselle di abbinamento con le colonne trovate.
    /// </summary>
    /// <remarks>
    /// La voce "nessuna colonna" è esplicita e prima in elenco: in un menu letto a
    /// voce, "nessuna selezione" e "non usare questo campo" devono essere
    /// distinguibili, altrimenti l'operatore non sa se ha dimenticato qualcosa.
    /// </remarks>
    private void FillMappingCombos(IReadOnlyList<string> headers)
        => FillMappingCombos(headers, headers);

    /// <summary>
    /// Popola le caselle di abbinamento tenendo distinto il valore dall'etichetta.
    /// </summary>
    /// <param name="columns">Identificativi da salvare nella mappatura.</param>
    /// <param name="labels">Come le colonne vanno mostrate e pronunciate.</param>
    /// <remarks>
    /// I due elenchi coincidono quando il file ha i titoli. Senza titoli il valore e'
    /// la posizione e l'etichetta porta un esempio: "Colonna 6, esempio GIULIA" dice
    /// a chi ascolta che cosa contiene quella colonna, cosa che "Colonna 6" da sola
    /// non farebbe.
    /// </remarks>
    private void FillMappingCombos(IReadOnlyList<string> columns, IReadOnlyList<string> labels)
    {
        foreach (var (combo, _) in MappingCombos)
        {
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = "(nessuna colonna)", Tag = null });

            for (var i = 0; i < columns.Count; i++)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = i < labels.Count ? labels[i] : columns[i],
                    Tag = columns[i],
                });
            }

            combo.SelectedIndex = 0;
        }
    }

    /// <summary>L'operatore ha cambiato idea sulla riga dei titoli: si rianalizza.</summary>
    private async void OnCsvHeaderModeChanged(object sender, RoutedEventArgs e)
    {
        if (_settingHeaderMode) return;

        _csvHeaderModeChosen = true;
        await AnalyzeCsvAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Porta una mappatura a schermo, conservandone le colonne strutturali.
    /// </summary>
    /// <param name="mapping">Mappatura proposta o già salvata.</param>
    private void ApplyMapping(CsvColumnMapping mapping)
    {
        SelectByTag(MapNumber, mapping.Extension);
        SelectByTag(MapSecondNumber, mapping.WorkPhone);
        SelectByTag(MapRelatedTo, mapping.RelatedTo);
        SelectByTag(MapCategory, mapping.Category);
        SelectByTag(MapFirstName, mapping.FirstName);
        SelectByTag(MapLastName, mapping.LastName);
        SelectByTag(MapDepartment, mapping.Department);
        SelectByTag(MapBuilding, mapping.Building);
        SelectByTag(MapFloor, mapping.Floor);

        _structuralMapping = new CsvColumnMapping
        {
            EntryKey = mapping.EntryKey,
            ShortCodeFlag = mapping.ShortCodeFlag,
            InactiveFlag = mapping.InactiveFlag,
            UpdatedAt = mapping.UpdatedAt,
        };
    }

    /// <summary>
    /// Riconosce il profilo della sorgente e ne applica la mappatura.
    /// </summary>
    /// <param name="columns">Colonne della tabella o del file.</param>
    /// <returns>Come riferire all'operatore che cosa è stato riconosciuto.</returns>
    /// <remarks>
    /// <b>Il profilo si dice, non si applica in silenzio.</b> Riconoscere una rubrica
    /// di gestionale significa abbinare colonne il cui nome non corrisponde al
    /// contenuto — la descrizione dentro un campo che si chiama «iniziali» — e chi
    /// importa deve poterlo verificare nell'anteprima invece di scoprirlo trasferendo
    /// una chiamata.
    /// </remarks>
    private string ApplyMappingFor(IReadOnlyList<string> columns)
    {
        _recognizedProfile = ContactSourceProfile.Recognize(columns);

        ApplyMapping(CsvColumnMapping.Guess(columns));

        if (_recognizedProfile is { } profilo) return $"{profilo.Name}. {profilo.Description}";

        var abbinate = MappingCombos.Count(m => SelectedTag(m.Combo) is not null);
        return $"{abbinate} colonne abbinate automaticamente";
    }

    /// <summary>
    /// Compone la mappatura dalle scelte a schermo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// I campi sono quelli che la rubrica dell'ente dichiara, e non uno di piu'. Le
    /// voci che il modello del contatto prevede ma che questa rubrica non ha —
    /// posta elettronica, cellulare, struttura, resa fonetica — restano vuote
    /// all'importazione da file: continuano a esistere perche' le riempiono le altre
    /// sorgenti, il centralino e la directory LDAP.
    /// </para>
    /// <para>
    /// Alle scelte a schermo si aggiungono le colonne strutturali, che non sono a
    /// schermo perche' non sono scelte: vedi <see cref="_structuralMapping"/>.
    /// </para>
    /// </remarks>
    private CsvColumnMapping BuildMappingFromUi() => new()
    {
        Extension = SelectedTag(MapNumber),
        WorkPhone = SelectedTag(MapSecondNumber),
        RelatedTo = SelectedTag(MapRelatedTo),
        Category = SelectedTag(MapCategory),
        FirstName = SelectedTag(MapFirstName),
        LastName = SelectedTag(MapLastName),
        Department = SelectedTag(MapDepartment),
        Building = SelectedTag(MapBuilding),
        Floor = SelectedTag(MapFloor),

        EntryKey = _structuralMapping.EntryKey,
        ShortCodeFlag = _structuralMapping.ShortCodeFlag,
        InactiveFlag = _structuralMapping.InactiveFlag,
        UpdatedAt = _structuralMapping.UpdatedAt,
    };

    private CsvContactSource BuildCsvSource(string path)
        => new(path, BuildMappingFromUi(), logger: _csvLogger,
               hasHeaderRecord: CsvNoHeader.IsChecked != true);

    /// <summary>
    /// Mostra le prime voci come verrebbero importate.
    /// </summary>
    /// <remarks>
    /// Il testo è composto in righe leggibili una per una con le frecce, non in una
    /// tabella: chi non vede controlla l'anteprima ascoltandola in sequenza, e una
    /// griglia richiederebbe di navigare cella per cella per ricostruire ogni voce.
    /// </remarks>
    private async Task RefreshPreviewAsync(
        Func<int, CancellationToken, Task<IReadOnlyList<Contact>>> preview5)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var preview = await preview5(5, timeout.Token).ConfigureAwait(true);

            if (preview.Count == 0)
            {
                PreviewText.Text =
                    "Nessuna voce importabile. Verifica di aver abbinato la colonna del numero: "
                    + "le righe senza numero vengono ignorate.";
                return;
            }

            var lines = preview.Select((c, i) =>
                $"{i + 1}. {c.FullSpeechDescription} - numero {c.PrimaryNumber}");

            PreviewText.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Anteprima del file CSV non riuscita");
            PreviewText.Text = "Anteprima non disponibile.";
        }
    }

    /// <summary>
    /// Importa adesso quello che è a schermo, da qualunque provenienza arrivi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il pulsante presumeva un file.</b> Con la rubrica collegata a un server non
    /// c'è nessun percorso da controllare, e il primo controllo rispondeva «Scegli
    /// prima un file da importare» a chi si era appena collegato, aveva scelto la
    /// tabella e aveva controllato l'anteprima. Il messaggio non era solo inutile: era
    /// fuorviante, perché indicava un campo che in quella modalità non va compilato.
    /// </para>
    /// <para>
    /// <b>Questo pulsante e «Riallinea adesso» non fanno la stessa cosa</b>, e la
    /// differenza va detta perché sono uno accanto all'altro. Questo importa la
    /// configurazione che si vede a schermo, anche se non è ancora stata salvata: è
    /// quello che serve subito dopo aver controllato l'anteprima. «Riallinea adesso»
    /// rifà il riallineamento con la configurazione salvata, cioè quella che userà
    /// anche il riallineamento periodico.
    /// </para>
    /// </remarks>
    private async void OnImportCsvClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTag(MapNumber) is null)
        {
            // Senza numero un contatto non serve a un centralino, e importarne
            // migliaia di inutilizzabili riempirebbe i risultati di ricerca. Il
            // controllo viene prima di quelli sulla provenienza perche' vale per
            // tutte e tre.
            Report("Abbina la colonna del numero: senza, non c'e' nulla da importare");
            MapNumber.Focus();
            return;
        }

        var sorgente = BuildSourceFromUi();
        if (sorgente is null) return;

        ImportCsvButton.IsEnabled = false;
        Report("Importazione in corso");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            // La lettura di una tabella di server o di backup e' sincrona sotto:
            // portarla fuori dal filo dell'interfaccia evita che la finestra smetta di
            // rispondere allo screen reader per tutta la durata dell'importazione.
            var contacts = await Task.Run(
                () => sorgente.LoadAsync(timeout.Token), timeout.Token).ConfigureAwait(true);

            if (contacts.Count == 0)
            {
                Report(_importKind == ImportKind.Csv
                    ? "Nessun contatto con numero telefonico trovato nel file"
                    : "Nessun contatto con numero telefonico trovato nella tabella scelta");
                return;
            }

            var written = await _contactsSync
                .WriteAsync(sorgente, contacts, ReplaceExisting.IsChecked == true, timeout.Token)
                .ConfigureAwait(true);

            var total = await _contacts.CountAsync(timeout.Token).ConfigureAwait(true);

            Report($"Importati {ContactsSyncService.Describe(contacts)}. In rubrica ci sono {total} voci");
            await RefreshContactCountAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Report("Importazione interrotta: la rubrica e' troppo grande o non risponde");
        }
        catch (SqlException ex)
        {
            _log.LogError(ex, "Importazione della rubrica dal server non riuscita");
            Report(SqlServerContactSource.DescribeSqlFailure(ex));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importazione della rubrica non riuscita");
            Report(ContactFileErrors.Describe(ex));
        }
        finally
        {
            ImportCsvButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Costruisce la sorgente corrispondente a ciò che è a schermo.
    /// </summary>
    /// <returns>
    /// La sorgente, oppure <c>null</c> se manca qualcosa: in quel caso il motivo è già
    /// stato riferito e il fuoco spostato dove si rimedia.
    /// </returns>
    private IContactSource? BuildSourceFromUi()
    {
        if (_importKind == ImportKind.SqlServer)
        {
            var collegamento = CurrentSqlServerSettings();

            if (string.IsNullOrWhiteSpace(collegamento.Table))
            {
                Report("Scegli prima la tabella del server da importare");
                BackupTable.Focus();
                return null;
            }

            return _contactsSync.BuildSource(collegamento);
        }

        var path = CsvPath.Text.Trim();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Report("Scegli prima un file da importare");
            CsvPath.Focus();
            return null;
        }

        if (!IsBackupSelected) return BuildCsvSource(path);

        if (BackupTable.SelectedIndex < 0)
        {
            Report("Scegli prima la tabella del backup da importare");
            BackupTable.Focus();
            return null;
        }

        return BuildBackupSource(path, _backupTables[BackupTable.SelectedIndex].Name);
    }

    private async void OnClearContactsClick(object sender, RoutedEventArgs e)
    {
        // Cancellare la rubrica e' irreversibile e toglie all'operatore la
        // possibilita' di trasferire per nome: la conferma e' obbligatoria, e la
        // finestra dice quanti contatti sta per eliminare.
        var total = await _contacts.CountAsync(CancellationToken.None).ConfigureAwait(true);

        if (total == 0)
        {
            Report("La rubrica e' gia' vuota");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Stai per eliminare {total} contatti dalla rubrica locale.\n\n"
            + "I contatti del centralino verranno ricaricati al prossimo avvio, "
            + "quelli importati da file no.\n\nProcedere?",
            "Svuota la rubrica",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            Report("Operazione annullata");
            return;
        }

        var removed = await _contacts.ClearAsync(null, CancellationToken.None).ConfigureAwait(true);
        Report($"Rimossi {removed} contatti");
        await RefreshContactCountAsync().ConfigureAwait(true);
    }

    private async Task RefreshContactCountAsync()
    {
        try
        {
            var total = await _contacts.CountAsync(CancellationToken.None).ConfigureAwait(true);

            LiveRegion.Update(ContactCountText, total == 0
                ? "Rubrica vuota"
                : $"In rubrica: {total} contatti");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Conteggio dei contatti non riuscito");
            LiveRegion.Update(ContactCountText, "Conteggio dei contatti non disponibile");
        }
    }

    /// <summary>
    /// Interpreta l'elenco delle code, tollerando separatori e spazi diversi.
    /// </summary>
    /// <remarks>
    /// Chi digita "1, 2" e chi digita "1;2" intende la stessa cosa. Rifiutare una
    /// delle due forme costringerebbe a indovinare quale separatore vuole il
    /// programma, informazione che a schermo si legge e ad ascolto no.
    /// </remarks>
    private static List<int> ParseQueueIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return [.. text
            .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part.Trim(), out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()];
    }
}

/// <summary>
/// Una riga della tabella dei tasti rapidi: un comando con le sue due combinazioni.
/// </summary>
/// <remarks>
/// <para>
/// Due assegnazioni per riga, e non due righe, perché locale e globale sono lo
/// <b>stesso</b> comando visto da due situazioni diverse: chi cerca "Rispondi" vuole
/// vedere subito con che tasto si risponde in entrambi i casi, non trovarne due voci
/// distanti nell'elenco.
/// </para>
/// <para>
/// Il testo esposto a UI Automation è la forma pronunciabile della combinazione,
/// diversa da quella mostrata a schermo. "Ctrl+Maiusc+F9" si legge bene con gli
/// occhi; letto a voce diventa una sequenza in cui i segni di punteggiatura si
/// sentono e le parole no.
/// </para>
/// </remarks>
internal sealed class HotkeyRow : INotifyPropertyChanged
{
    private HotkeyBinding? _local;
    private HotkeyBinding? _global;

    /// <summary>Crea la riga di un comando.</summary>
    /// <param name="info">Descrizione del comando.</param>
    public HotkeyRow(HotkeyCommandInfo info) => Info = info;

    /// <summary>Descrizione del comando.</summary>
    public HotkeyCommandInfo Info { get; }

    /// <summary>Comando della riga.</summary>
    public HotkeyCommand Command => Info.Command;

    /// <summary>Etichetta del comando.</summary>
    public string CommandLabel => Info.Label;

    /// <summary>Gruppo di appartenenza.</summary>
    public string CategoryLabel => HotkeyCommandCatalog.CategoryLabel(Info.Category);

    /// <summary>Rappresentazione testuale della riga.</summary>
    /// <remarks>
    /// <b>È il nome che lo screen reader dà alla riga della tabella.</b> WPF, non
    /// trovandone uno dichiarato, ricade su questo metodo: senza, la riga si chiamava
    /// «BlindPhone.App.HotkeyRow» e l'operatore se lo sentiva ripetere per ogni
    /// comando dell'elenco. Il comando è ciò che identifica la riga; le combinazioni
    /// stanno nelle celle e le dicono da sé.
    /// </remarks>
    public override string ToString() => CommandLabel;

    /// <summary>Spiegazione estesa del comando.</summary>
    public string Description => Info.Description;

    /// <summary>Combinazione attiva con l'applicazione in primo piano.</summary>
    public HotkeyBinding? Local
    {
        get => _local;
        set
        {
            _local = value;
            Raise(nameof(Local));
            Raise(nameof(LocalDisplay));
            Raise(nameof(LocalSpeech));
        }
    }

    /// <summary>Combinazione attiva anche con l'applicazione in secondo piano.</summary>
    public HotkeyBinding? Global
    {
        get => _global;
        set
        {
            _global = value;
            Raise(nameof(Global));
            Raise(nameof(GlobalDisplay));
            Raise(nameof(GlobalSpeech));
        }
    }

    /// <summary>Forma compatta della combinazione locale.</summary>
    public string LocalDisplay => _local?.DisplayText ?? "non assegnata";

    /// <summary>Forma pronunciabile della combinazione locale.</summary>
    public string LocalSpeech => _local?.SpeechText ?? "non assegnata";

    /// <summary>Forma compatta della combinazione globale.</summary>
    public string GlobalDisplay => _global?.DisplayText ?? "non assegnata";

    /// <summary>Forma pronunciabile della combinazione globale.</summary>
    public string GlobalSpeech => _global?.SpeechText ?? "non assegnata";

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Una voce dell'elenco dei codec, con il suo stato di attivazione e la sua
/// posizione nell'ordine di preferenza.
/// </summary>
/// <remarks>
/// <para>
/// Il testo esposto a UI Automation è deliberatamente più esteso di quello mostrato
/// a schermo. A schermo bastano poche parole perché l'occhio coglie anche la
/// posizione nella lista e la spunta; nell'ascolto quelle informazioni vanno dette,
/// e per capire se un codec conviene serve sapere che banda occupa e che qualità
/// dà — dati che un operatore non ha modo di ricavare altrove.
/// </para>
/// </remarks>
internal sealed class CodecEntry : INotifyPropertyChanged
{
    private readonly Action _onEnabledChanged;
    private bool _isEnabled;
    private int? _position;

    /// <summary>Crea la voce.</summary>
    /// <param name="info">Descrizione del codec.</param>
    /// <param name="isEnabled">Se il codec è offerto in negoziazione.</param>
    /// <param name="onEnabledChanged">Azione da eseguire quando l'attivazione cambia.</param>
    public CodecEntry(AudioCodecInfo info, bool isEnabled, Action onEnabledChanged)
    {
        Info = info;
        _isEnabled = isEnabled;
        _onEnabledChanged = onEnabledChanged;
    }

    /// <summary>Descrizione del codec.</summary>
    public AudioCodecInfo Info { get; }

    /// <summary>Se il codec viene offerto in negoziazione.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;

            _isEnabled = value;
            Raise();
            Raise(nameof(Display));
            Raise(nameof(AccessibleName));
            _onEnabledChanged();
        }
    }

    /// <summary>Posizione nell'ordine di preferenza, oppure <c>null</c> se disattivato.</summary>
    public int? Position
    {
        get => _position;
        set
        {
            if (_position == value) return;

            _position = value;
            Raise();
            Raise(nameof(Display));
            Raise(nameof(AccessibleName));
        }
    }

    /// <summary>Testo mostrato a schermo.</summary>
    public string Display => Position is { } position
        ? $"{position}. {Info.Label}"
        : Info.Label;

    /// <summary>
    /// Testo letto dallo screen reader: include posizione, banda e qualità.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var band = Info.IsWideband ? "banda larga" : "banda stretta";
            var state = Position is { } position
                ? $"attivo, posizione {position}"
                : "non attivo";

            return $"{Info.Label}, {state}, {band}, {Info.BitrateKbps} kilobit al secondo";
        }
    }

    /// <summary>Spiegazione estesa, esposta come testo di aiuto.</summary>
    public string Description => Info.Description;

    /// <summary>Rappresentazione testuale della voce.</summary>
    /// <remarks>
    /// Stessa ragione della riga dei tasti e della voce dei campi: senza, sulla voce
    /// dell'elenco lo screen reader pronunciava il nome della classe.
    /// </remarks>
    public override string ToString() => AccessibleName;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Un campo della rubrica nell'elenco di quelli mostrabili, con la sua spunta e la
/// sua posizione nell'ordine di lettura.
/// </summary>
/// <remarks>
/// <para>
/// Notifica i cambiamenti perché la spunta viene legata in scrittura: senza, la
/// scelta dell'operatore resterebbe nell'interfaccia e non arriverebbe mai al
/// salvataggio.
/// </para>
/// <para>
/// La posizione compare nel testo perché è l'informazione che l'operatore sta
/// governando: «Reparto» da solo non dice se il reparto si leggerà prima o dopo lo
/// stato, e chi ascolta non ha modo di dedurlo dalla riga.
/// </para>
/// </remarks>
internal sealed class DialerFieldEntry : INotifyPropertyChanged
{
    private readonly Action _onEnabledChanged;
    private bool _isEnabled;
    private int? _position;

    /// <summary>Crea la voce.</summary>
    /// <param name="field">Campo descritto dal catalogo.</param>
    /// <param name="isEnabled">Se l'operatore vuole vederlo.</param>
    /// <param name="onEnabledChanged">Azione da eseguire quando la spunta cambia.</param>
    public DialerFieldEntry(DialerField field, bool isEnabled, Action onEnabledChanged)
    {
        Field = field;
        _isEnabled = isEnabled;
        _onEnabledChanged = onEnabledChanged;
    }

    /// <summary>Campo descritto dal catalogo.</summary>
    public DialerField Field { get; }

    /// <summary>Se il campo viene mostrato accanto ai contatti proposti.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;

            _isEnabled = value;
            Raise();
            Raise(nameof(Display));
            Raise(nameof(AccessibleName));
            _onEnabledChanged();
        }
    }

    /// <summary>Posizione nell'ordine di lettura, oppure <c>null</c> se nascosto.</summary>
    public int? Position
    {
        get => _position;
        set
        {
            if (_position == value) return;

            _position = value;
            Raise();
            Raise(nameof(Display));
            Raise(nameof(AccessibleName));
        }
    }

    /// <summary>Testo mostrato a schermo.</summary>
    public string Display => Position is { } position
        ? $"{position}. {Field.Label}"
        : Field.Label;

    /// <summary>Testo letto dallo screen reader: dice se il campo si vede e dove.</summary>
    public string AccessibleName => Position is { } position
        ? $"{Field.Label}, mostrato, posizione {position}"
        : $"{Field.Label}, non mostrato";

    /// <summary>Rappresentazione testuale della voce.</summary>
    /// <remarks>
    /// <para>
    /// <b>È quello che lo screen reader pronuncia sulla voce dell'elenco.</b> WPF non
    /// mette un nome accessibile sul contenitore della voce e ricade sul risultato di
    /// questo metodo: senza, era il nome della classe, e l'operatore si sentiva
    /// annunciare «BlindPhone punto App punto Dialer Field Entry» prima di ogni campo.
    /// </para>
    /// <para>
    /// Verificato sull'albero di accessibilità con la stessa interfaccia usata da NVDA
    /// e JAWS: prima <c>ListItem nome: "BlindPhone.App.DialerFieldEntry"</c>, dopo
    /// <c>ListItem nome: "Stato, mostrato, posizione 1"</c>.
    /// </para>
    /// </remarks>
    public override string ToString() => AccessibleName;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
