using System.Runtime.InteropServices;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Microsoft.Win32;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Traduce la configurazione nelle voci del catalogo delle statistiche.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ogni campo dice se una cosa c'è, mai come è fatta.</b> L'indirizzo del centralino
/// diventa «c'è un'API del centralino», il server STUN «c'è uno STUN», l'elenco dei
/// colleghi «la sorveglianza è accesa». È la traduzione a decidere che cosa si perde, e
/// si perde di proposito tutto quello che distingue un ente da un altro.
/// </para>
/// <para>
/// Lo screen reader e il contrasto elevato non si leggono qui: li conosce chi ha in mano
/// la sintesi e l'interfaccia, e li aggiunge l'applicazione. Vedi
/// <see cref="TelemetryFacts"/>.
/// </para>
/// </remarks>
public static class TelemetryFactsBuilder
{
    /// <summary>Lo stato della postazione secondo la configurazione.</summary>
    /// <param name="settings">Configurazione corrente.</param>
    /// <param name="version">Versione di BlindPhone, nella forma numerica.</param>
    public static TelemetryFacts FromSettings(AppSettings settings, string version)
    {
        var account = settings.ActiveSipAccount;

        return new TelemetryFacts
        {
            Version = version,
            Architecture = Architecture(),
            Windows = WindowsFamily(),
            WindowsBuild = WindowsBuild(),
            Theme = settings.Accessibility.Theme switch
            {
                AppTheme.Light => "chiaro",
                AppTheme.Dark => "scuro",
                _ => "sistema",
            },
            Interaction = settings.Interaction == InteractionMode.Simplified ? "semplificata" : "standard",
            SpeechChannel = settings.Accessibility.SpeechChannel switch
            {
                "Automatic" => "automatico",
                "UiaOnly" => "solo_uia",
                "ScreenReaderOnly" => "solo_screen_reader",
                "InternalVoiceOnly" => "solo_voce_interna",
                "Both" => "entrambi",
                _ => "altro",
            },
            InternalVoice = settings.Accessibility.InternalVoiceEnabled,
            Braille = settings.Accessibility.BrailleEnabled,

            // Acceso e con un indirizzo: l'interruttore da solo e' acceso di partenza, e
            // senza la seconda condizione ogni postazione risulterebbe collegata a
            // un'API che non ha.
            PbxApi = settings.Pbx.Enabled && !string.IsNullOrWhiteSpace(settings.Pbx.Host),
            Contacts = settings.Contacts.Mode switch
            {
                ContactsMode.SqlServer => "sql_server",
                ContactsMode.Ldap => "ldap",
                ContactsMode.Outlook => "outlook",
                _ => "file",
            },
            Headset = settings.Headset.Enabled,
            Stun = settings.SipAccounts.Any(a => !string.IsNullOrWhiteSpace(a.StunServer)),
            Colleagues = !string.IsNullOrWhiteSpace(settings.Colleagues.Monitored),
            Voicemail = !string.IsNullOrWhiteSpace(account.VoicemailNumber),
            MultipleAccounts = settings.SipAccounts.Count > 1,
            SipTransport = account.Transport?.Trim().ToLowerInvariant() switch
            {
                "udp" => "udp",
                "tcp" => "tcp",
                "tls" => "tls",
                _ => "altro",
            },
            VoiceEncryption = account.Srtp?.Trim().ToLowerInvariant() switch
            {
                "disabled" => "disabilitata",
                "preferred" => "preferita",
                "required" => "obbligatoria",
                _ => "altro",
            },
            MicrophoneProcessing = settings.MicrophoneProcessing.IsActive,
            HotkeysOnButtons = settings.Accessibility.ShowHotkeysOnButtons,
            AutomaticUpdates = !string.IsNullOrWhiteSpace(settings.Updates.ServerUrl),
            DistributedConfiguration = settings.AppliedConfigurationRevision > 0,
            Policy = PolicyName(settings.Policy.IsRestrictive ? settings.Policy.Origin : PolicyOrigin.None),
            DebugMode = settings.DebugModeEnabled,
        };
    }

    /// <summary>Nome con cui l'origine di un criterio arriva al server.</summary>
    /// <param name="origin">Origine del criterio in vigore.</param>
    public static string PolicyName(PolicyOrigin origin) => origin switch
    {
        PolicyOrigin.Machine => "postazione",
        PolicyOrigin.User => "utente",
        _ => "nessuno",
    };

    private static string Architecture() => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "altro",
    };

    /// <summary>Famiglia di Windows: 10, 11, server, altro.</summary>
    /// <remarks>
    /// Windows 11 si presenta ancora come 10.0: lo distingue il numero di compilazione,
    /// dal 22000 in su. Un server si riconosce solo dal tipo di installazione nel
    /// registro, perché il numero di versione è lo stesso di un client.
    /// </remarks>
    private static string WindowsFamily()
    {
        var versione = Environment.OSVersion.Version;

        if (IsServer()) return "server";
        if (versione.Major != 10) return "altro";

        return versione.Build >= 22000 ? "11" : "10";
    }

    private static string WindowsBuild()
    {
        var versione = Environment.OSVersion.Version;
        return $"{versione.Major}.{versione.Minor}.{Math.Max(0, versione.Build)}";
    }

    private static bool IsServer()
    {
        try
        {
            using var chiave = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return chiave?.GetValue("InstallationType") is string tipo
                   && tipo.StartsWith("Server", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Un registro illeggibile non e' un motivo per non dire niente: la
            // postazione risulta un client, che e' il caso di gran lunga piu' frequente.
            return false;
        }
    }
}
