using System.Reflection;
using BlindPhone.Accessibility;
using BlindPhone.Contacts;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BlindPhone.App;

/// <summary>
/// Raccoglie lo stato del posto operatore dai servizi vivi.
/// </summary>
/// <remarks>
/// <para>
/// I valori vengono dai servizi in esecuzione e non dal file di configurazione:
/// quello che serve a chi indaga è cosa sta facendo il posto operatore adesso, non
/// cosa gli era stato chiesto di fare all'avvio.
/// </para>
/// <para>
/// <b>Esiste come classe a sé perché lo stesso riepilogo serve in due punti</b>: il
/// pannello di diagnostica, dove si legge, e la segnalazione all'assistenza, dove si
/// allega. Due versioni che dicessero cose diverse sulla stessa postazione sarebbero
/// peggio di una sola, e la seconda si scoprirebbe sbagliata solo quando qualcuno
/// confronta i due documenti davanti a un guasto.
/// </para>
/// <para>
/// Le credenziali non compaiono, per scelta di <see cref="DiagnosticReport"/>.
/// </para>
/// </remarks>
public static class DiagnosticSnapshotFactory
{
    /// <summary>Compone la fotografia.</summary>
    /// <param name="services">Contenitore da cui prendere i servizi vivi.</param>
    /// <param name="debugMode">
    /// Se dichiarare attiva la modalità debug. Il pannello passa lo stato della
    /// casella, che può essere stato cambiato e non ancora salvato; chi non ha una
    /// casella davanti lascia decidere alla configurazione.
    /// </param>
    public static DiagnosticSnapshot Build(IServiceProvider services, bool? debugMode = null)
    {
        var settingsService = services.GetRequiredService<SettingsService>();
        var settings = settingsService.Current;

        var telephony = services.GetRequiredService<ITelephonyEngine>();
        var queue = services.GetRequiredService<IQueueProvider>();
        var presence = services.GetRequiredService<PresenceMonitorService>();
        var speech = services.GetRequiredService<CompositeSpeechOutput>();
        var hotkeys = services.GetRequiredService<HotkeyProfileStore>();
        var logProvider = services.GetRequiredService<FileLoggerProvider>();
        var logSwitch = services.GetRequiredService<LogLevelSwitch>();
        var contacts = services.GetRequiredService<IContactRepository>();
        var history = services.GetRequiredService<ICallHistoryRepository>();
        var trust = services.GetService<CertificateTrustService>();
        var trustStore = services.GetService<ICertificateTrustStore>();
        var crashes = services.GetService<CrashReporter>();

        var registration = telephony.RegistrationState;
        var attivo = settings.ActiveSipAccount;

        var agente = queue.AgentState.Status switch
        {
            AgentStatus.Available => "disponibile",
            AgentStatus.Paused => "in pausa",
            AgentStatus.LoggedOut => "disconnesso",
            AgentStatus.Busy => "occupato",
            AgentStatus.WrapUp => "post chiamata",
            _ => "sconosciuto",
        };

        return new DiagnosticSnapshot
        {
            GeneratedAt = DateTimeOffset.Now,

            // La versione informativa, non quella numerica: e' quella che porta
            // l'etichetta "beta", ed e' l'informazione che chi riceve la segnalazione
            // deve vedere per prima.
            ProductVersion = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0]
                ?? typeof(App).Assembly.GetName().Version?.ToString()
                ?? "sconosciuta",

            OperatingSystem = Environment.OSVersion.VersionString,
            Runtime = Environment.Version.ToString(),
            DataDirectory = settingsService.DataDirectory,
            LogFilePath = logProvider.CurrentFilePath,
            LogLevel = LogLevelSwitch.Describe(logSwitch.Minimum),
            LogRetentionDays = settings.DiagnosticLogRetentionDays,
            DebugMode = debugMode ?? settings.DebugModeEnabled,

            SipAccount = attivo.Username,
            SipDomain = attivo.Domain,
            SipTransport = attivo.Transport,
            RegistrationState = registration.IsReachable ? "registrato" : "non registrato",
            Simulated = telephony.IsSimulated,
            Codecs = attivo.CodecPreference,

            SipAccountName = attivo.Name,
            SipAccountCount = settings.SipAccounts.Count,

            PbxEnabled = settings.Pbx.Enabled,
            PbxHost = settings.Pbx.Host,
            QueueState = $"{queue.CurrentSnapshot.WaitingCount} in attesa, agente {agente}",
            PresenceSource = presence.SourceName,
            PresenceKnownCount = presence.KnownCount,

            // Una postazione che ha rinunciato a verificare i certificati deve
            // dichiararlo qui: e' proprio il documento che chi la verifica legge.
            CertificateVerification = DescribeCertificatePolicy(settings, trust, trustStore),
            PendingCrashReports = crashes?.Pending().Count ?? 0,

            Microphone = settings.Microphone.DeviceName ?? "predefinito di sistema",
            Conversation = settings.Conversation.DeviceName ?? "predefinito di sistema",
            Ringer = settings.Ringer.DeviceName ?? "predefinito di sistema",
            Announcements = settings.Announcements.DeviceName ?? "predefinito di sistema",
            JitterBufferMs = settings.Audio.JitterBufferMs,
            DeviceLatencyMs = settings.Audio.DeviceLatencyMs,
            MaxBufferMs = settings.Audio.MaxBufferMs,
            PacketMs = settings.Audio.PacketMs,

            SpeechChannel = settings.Accessibility.SpeechChannel,
            InternalVoiceEnabled = speech.InternalVoiceEnabled,
            ScreenReader = speech.DetectedScreenReader,
            HotkeyProfile = hotkeys.Active.Name,
            HotkeyCount = hotkeys.Active.Bindings.Count,

            // Si chiede agli archivi concreti invece di aggiungere la proprieta'
            // all'interfaccia: come sono conservati i dati non riguarda chi li legge,
            // e un archivio finto usato nei collaudi non deve dichiarare cifrature che
            // non ha. Il valore risulta "cifrato" solo se entrambi lo sono davvero.
            ArchivesEncrypted =
                contacts is not SqliteContactRepository { IsEncrypted: false }
                && history is not SqliteCallHistoryRepository { IsEncrypted: false },

            ArchiveWasReset =
                contacts is SqliteContactRepository { WasReset: true }
                || history is SqliteCallHistoryRepository { WasReset: true },

            Counters = telephony.DiagnosticCounters,
        };
    }

    /// <summary>Descrive come vengono trattati i certificati del centralino.</summary>
    private static string DescribeCertificatePolicy(
        AppSettings settings,
        CertificateTrustService? trust,
        ICertificateTrustStore? store)
    {
        if (settings.Pbx.AcceptAnyCertificate || trust?.AcceptAnyCertificate == true)
        {
            return "DISATTIVATA: qualunque certificato viene accettato senza controlli";
        }

        var approvati = store?.Trusted.Count ?? 0;

        return approvati == 0
            ? "attiva, nessun certificato approvato a mano"
            : $"attiva, {approvati} certificati approvati dall'operatore su questa postazione";
    }
}
