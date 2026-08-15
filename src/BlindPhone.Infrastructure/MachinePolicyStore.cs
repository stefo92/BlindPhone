using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Legge il criterio di postazione dal registro di Windows.
/// </summary>
/// <remarks>
/// <para>
/// <b>È l'unico livello di blocco che costituisca un vincolo vero.</b> Il criterio
/// scritto nella configurazione dell'utente vive in <c>%APPDATA%</c>, cartella su
/// cui l'utente ha pieno controllo: protegge dall'errore, non dalla volontà. Questo
/// invece sta sotto <c>HKEY_LOCAL_MACHINE</c>, dove serve un account amministrativo
/// per scrivere, ed è quello che chi amministra la rete può dichiarare in un
/// collaudo.
/// </para>
/// <para>
/// La chiave segue la convenzione dei criteri di gruppo, quindi si distribuisce con
/// gli stessi strumenti con cui un ente distribuisce ogni altra impostazione:
/// </para>
/// <code>
/// HKEY_LOCAL_MACHINE\SOFTWARE\Policies\BlindPhone
///     Pannello        REG_SZ     Aperto | SoloLettura | Bloccato
///     SezioniBloccate REG_SZ     SipAccounts,Pbx,Diagnostics
///     Messaggio       REG_SZ     Configurazione gestita dall'ufficio IT, interno 4000
///     PasswordHash    REG_SZ     impronta esadecimale
///     PasswordSalt    REG_SZ     sale in base 64
/// </code>
/// <para>
/// <b>Si legge e basta.</b> BlindPhone non scrive mai qui: gira con l'utenza
/// dell'operatore, che quei privilegi non li ha, e un tentativo di scrittura
/// produrrebbe soltanto un errore a ogni avvio. Il valore lo scrive l'installatore o
/// un criterio di gruppo.
/// </para>
/// </remarks>
public sealed class MachinePolicyStore
{
    /// <summary>Chiave del registro che contiene il criterio di postazione.</summary>
    public const string RegistryKeyPath = @"SOFTWARE\Policies\BlindPhone";

    private readonly ILogger<MachinePolicyStore> _log;

    /// <summary>Crea il lettore.</summary>
    /// <param name="logger">Log diagnostico.</param>
    public MachinePolicyStore(ILogger<MachinePolicyStore>? logger = null)
        => _log = logger ?? NullLogger<MachinePolicyStore>.Instance;

    /// <summary>
    /// Legge il criterio di postazione.
    /// </summary>
    /// <returns>
    /// Il criterio, oppure <c>null</c> se la chiave non esiste o non dichiara nulla.
    /// </returns>
    /// <remarks>
    /// Una chiave che esiste ma non dichiara né la modalità né le sezioni non è un
    /// criterio: restituire un criterio vuoto farebbe risultare la postazione
    /// «gestita» nella diagnostica senza che nessuno abbia deciso niente.
    /// </remarks>
    public ConfigurationPolicy? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: false);
            if (key is null) return null;

            var pannello = key.GetValue("Pannello") as string;
            var sezioni = ParseSections(key.GetValue("SezioniBloccate") as string);

            if (string.IsNullOrWhiteSpace(pannello) && sezioni.Count == 0) return null;

            var criterio = new ConfigurationPolicy
            {
                Access = ParseAccess(pannello),
                LockedSections = sezioni,
                Note = key.GetValue("Messaggio") as string,
                PasswordHash = key.GetValue("PasswordHash") as string,
                PasswordSalt = key.GetValue("PasswordSalt") as string,
                Origin = PolicyOrigin.Machine,
            };

            _log.LogInformation(
                "Criterio di postazione letto dal registro: pannello {Accesso}, sezioni bloccate {Sezioni}",
                criterio.Access, criterio.DescribeSections());

            return criterio;
        }
        catch (Exception ex)
        {
            // Un criterio illeggibile non deve impedire l'avvio del posto operatore:
            // si prosegue senza, e lo si registra. La conseguenza — una postazione che
            // risulta non gestita mentre doveva esserlo — e' visibile nel riepilogo
            // diagnostico, che e' il posto in cui chi amministra va a controllare.
            _log.LogError(ex, "Lettura del criterio di postazione non riuscita: si prosegue senza");
            return null;
        }
    }

    private static PanelAccess ParseAccess(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "bloccato" or "locked" => PanelAccess.Locked,
        "sololettura" or "readonly" or "solo lettura" => PanelAccess.ReadOnly,
        _ => PanelAccess.Open,
    };

    /// <summary>
    /// Interpreta l'elenco delle sezioni bloccate.
    /// </summary>
    /// <remarks>
    /// I nomi non riconosciuti vengono ignorati e non fanno fallire la lettura: un
    /// refuso in un criterio di gruppo non deve lasciare la postazione senza criterio
    /// affatto, che è il contrario di quello che chi lo ha scritto voleva ottenere.
    /// </remarks>
    private static IReadOnlyList<SettingsSection> ParseSections(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var sezioni = new List<SettingsSection>();

        foreach (var nome in value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<SettingsSection>(nome.Trim(), ignoreCase: true, out var sezione)
                && !sezioni.Contains(sezione))
            {
                sezioni.Add(sezione);
            }
        }

        return sezioni;
    }
}
