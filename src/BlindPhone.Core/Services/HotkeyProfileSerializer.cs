using System.Text.Json;
using System.Text.Json.Serialization;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>Una singola assegnazione come appare nel file.</summary>
/// <remarks>
/// I campi sono stringhe e non enumerazioni perché il file viene letto e a volte
/// corretto a mano dall'ufficio IT che prepara le postazioni: <c>"Control, Shift"</c>
/// si capisce, <c>6</c> no.
/// </remarks>
public sealed record HotkeyBindingEntry
{
    /// <summary>Nome del comando.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Nome del tasto principale.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Modificatori, separati da virgola.</summary>
    public string Modifiers { get; init; } = "None";

    /// <summary>Ambito: Local oppure Global.</summary>
    public string Scope { get; init; } = "Local";
}

/// <summary>Un profilo come appare nel file.</summary>
public sealed record HotkeyProfileEntry
{
    /// <summary>Nome del profilo.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Assegnazioni del profilo.</summary>
    public IReadOnlyList<HotkeyBindingEntry> Bindings { get; init; } = [];

    /// <summary>
    /// Comandi che il profilo ha già conosciuto, anche se senza combinazione.
    /// </summary>
    /// <remarks>
    /// Distingue "comando aggiunto da una versione successiva", a cui va assegnata la
    /// combinazione predefinita, da "comando lasciato senza tasto di proposito", che
    /// non va toccato. Un file scritto da una versione che non aveva questo campo lo
    /// presenta vuoto: in quel caso tutti i comandi senza combinazione ricevono la
    /// predefinita, che è il comportamento voluto alla prima migrazione.
    /// </remarks>
    public IReadOnlyList<string> KnownCommands { get; init; } = [];
}

/// <summary>Contenuto completo del file dei tasti rapidi.</summary>
public sealed record HotkeyProfileDocument
{
    /// <summary>Versione dello schema, per le future migrazioni.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Nome del profilo attivo.</summary>
    public string ActiveProfile { get; init; } = "Standard";

    /// <summary>Profili disponibili.</summary>
    public IReadOnlyList<HotkeyProfileEntry> Profiles { get; init; } = [];
}

/// <summary>
/// Esito della lettura di un file di profili.
/// </summary>
/// <param name="Profiles">Profili letti correttamente.</param>
/// <param name="ActiveProfile">Nome del profilo indicato come attivo.</param>
/// <param name="IgnoredBindings">
/// Numero di assegnazioni scartate perché non interpretabili.
/// </param>
/// <remarks>
/// Il conteggio degli scarti va mostrato all'operatore. Una scorciatoia sparita in
/// silenzio si manifesta come "quel tasto non fa più niente", che è il sintomo più
/// difficile da ricondurre alla causa: nessuno pensa a un file di configurazione
/// letto a metà.
/// </remarks>
public sealed record HotkeyProfileLoadResult(
    IReadOnlyList<HotkeyProfile> Profiles,
    string ActiveProfile,
    int IgnoredBindings);

/// <summary>
/// Converte i profili di scorciatoie da e verso il formato JSON.
/// </summary>
/// <remarks>
/// <para>
/// Sta nel core, e non nell'infrastruttura insieme al file, per una ragione precisa:
/// la conversione è la parte che può sbagliare in modo silenzioso, ed è l'unica che
/// si possa verificare senza toccare il disco. Nell'infrastruttura resta la scrittura
/// atomica, che è un problema di piattaforma.
/// </para>
/// <para>
/// <b>La lettura è deliberatamente tollerante.</b> Un comando che non esiste più, o
/// un modificatore scritto male da chi ha corretto il file a mano, fa saltare quella
/// riga e non l'intero profilo: perdere una scorciatoia è un fastidio, perdere tutta
/// la mappa dei tasti mette la postazione fuori uso.
/// </para>
/// </remarks>
public static class HotkeyProfileSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Converte i profili in JSON.</summary>
    /// <param name="profiles">Profili da salvare.</param>
    /// <param name="activeProfile">Nome del profilo attivo.</param>
    public static string Serialize(IEnumerable<HotkeyProfile> profiles, string activeProfile)
    {
        var document = new HotkeyProfileDocument
        {
            ActiveProfile = activeProfile,
            Profiles =
            [
                .. profiles.Select(profile => new HotkeyProfileEntry
                {
                    Name = profile.Name,
                    KnownCommands =
                    [
                        .. profile.KnownCommands
                            .Select(c => c.ToString())
                            .OrderBy(c => c, StringComparer.Ordinal),
                    ],
                    Bindings =
                    [
                        // L'ordine stabile serve a chi confronta due file per capire
                        // cosa e' cambiato fra due postazioni.
                        .. profile.Bindings
                            .OrderBy(b => b.Command.ToString(), StringComparer.Ordinal)
                            .ThenBy(b => b.Scope)
                            .Select(b => new HotkeyBindingEntry
                            {
                                Command = b.Command.ToString(),
                                Key = b.Key,
                                Modifiers = b.Modifiers.ToString(),
                                Scope = b.Scope.ToString(),
                            }),
                    ],
                }),
            ],
        };

        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>Converte un singolo profilo in JSON, per l'esportazione.</summary>
    /// <param name="profile">Profilo da esportare.</param>
    public static string SerializeSingle(HotkeyProfile profile) => Serialize([profile], profile.Name);

    /// <summary>
    /// Legge i profili da JSON.
    /// </summary>
    /// <param name="json">Contenuto del file.</param>
    /// <returns>
    /// I profili leggibili. Se il contenuto non è interpretabile viene sollevata
    /// un'eccezione di deserializzazione: la decisione su cosa fare — ripartire dai
    /// predefiniti conservando l'originale — spetta a chi possiede il file.
    /// </returns>
    public static HotkeyProfileLoadResult Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<HotkeyProfileDocument>(json, Options)
                       ?? new HotkeyProfileDocument();

        var profiles = new List<HotkeyProfile>();
        var ignored = 0;

        foreach (var entry in document.Profiles)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;

            var profile = new HotkeyProfile(entry.Name);

            foreach (var binding in entry.Bindings)
            {
                if (TryConvert(binding) is { } converted) profile.Set(converted);
                else ignored++;
            }

            foreach (var known in entry.KnownCommands)
            {
                if (Enum.TryParse<HotkeyCommand>(known, ignoreCase: true, out var command))
                {
                    profile.KnownCommands.Add(command);
                }
            }

            profiles.Add(profile);
        }

        var active = string.IsNullOrWhiteSpace(document.ActiveProfile)
            ? profiles.FirstOrDefault()?.Name ?? "Standard"
            : document.ActiveProfile;

        return new HotkeyProfileLoadResult(profiles, active, ignored);
    }

    /// <summary>Legge un singolo profilo da JSON, per l'importazione.</summary>
    /// <param name="json">Contenuto del file.</param>
    /// <returns>Il primo profilo del file, oppure <c>null</c> se non ce n'è nessuno.</returns>
    public static HotkeyProfile? DeserializeSingle(string json)
        => Deserialize(json).Profiles.FirstOrDefault();

    private static HotkeyBinding? TryConvert(HotkeyBindingEntry entry)
    {
        if (!Enum.TryParse<HotkeyCommand>(entry.Command, ignoreCase: true, out var command)) return null;
        if (!Enum.TryParse<HotkeyScope>(entry.Scope, ignoreCase: true, out var scope)) return null;
        if (!Enum.TryParse<HotkeyModifiers>(entry.Modifiers, ignoreCase: true, out var modifiers)) return null;
        if (string.IsNullOrWhiteSpace(entry.Key)) return null;

        return new HotkeyBinding(command, entry.Key.Trim(), modifiers, scope);
    }
}
