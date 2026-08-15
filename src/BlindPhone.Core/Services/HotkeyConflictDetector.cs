using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Rileva i conflitti di una combinazione prima che venga assegnata.
/// </summary>
/// <remarks>
/// <para>
/// La verifica a tre livelli (comandi dell'applicazione, combinazioni riservate di
/// Windows, tasti usati dagli screen reader) esiste perché un conflitto di
/// scorciatoia è, per un operatore non vedente, particolarmente difficile da
/// diagnosticare. Se assegna a BlindPhone una combinazione che NVDA usa per leggere
/// il titolo della finestra, il sintomo che osserverà è "NVDA ha smesso di leggere
/// i titoli", e non lo collegherà mai alla configurazione fatta due settimane
/// prima.
/// </para>
/// <para>
/// La tabella dei tasti riservati non pretende di essere esaustiva: gli screen
/// reader hanno centinaia di comandi e cambiano fra versioni. Copre i comandi di
/// uso quotidiano, quelli la cui perdita si nota subito, ed è manutenibile in un
/// unico punto.
/// </para>
/// </remarks>
public sealed class HotkeyConflictDetector
{
    /// <summary>
    /// Combinazioni che Windows intercetta prima di qualunque applicazione.
    /// </summary>
    private static readonly HashSet<string> WindowsReserved =
    [
        "Alt:TAB",
        "Alt:ESCAPE",
        "Control|Alt:DELETE",
        "Control:ESCAPE",
        "Windows:L",
        "Windows:D",
        "Windows:R",
        "Windows:E",
        "Alt:F4",
    ];

    /// <summary>
    /// Combinazioni di uso quotidiano di NVDA e JAWS, con la funzione che
    /// svolgono.
    /// </summary>
    /// <remarks>
    /// I modificatori speciali dei due screen reader (INS e Blocco maiuscole per
    /// NVDA, INS per JAWS) non sono rappresentabili come
    /// <see cref="HotkeyModifiers"/> e non vengono mai proposti da BlindPhone: il
    /// rischio reale è sulle combinazioni che usano Control, Alt e Maiusc.
    /// </remarks>
    private static readonly Dictionary<string, string> ScreenReaderReserved = new()
    {
        ["Control:F"] = "ricerca dello screen reader",
        ["Control|Shift:F"] = "ricerca successiva dello screen reader",
        ["Alt|Shift:T"] = "lettura dell'ora in JAWS",
        ["Control|Shift:S"] = "profilo vocale in NVDA",
        ["Control:H"] = "navigazione per intestazioni",
        ["Control|Alt:T"] = "lettura del titolo",
        ["Control|Shift:D"] = "descrizione lunga in NVDA",
        ["Control|Shift:N"] = "impostazioni della sintesi",
    };

    /// <summary>
    /// Verifica una combinazione candidata.
    /// </summary>
    /// <param name="candidate">Combinazione da verificare.</param>
    /// <param name="profile">Profilo in cui verrebbe inserita.</param>
    /// <param name="commandLabels">
    /// Etichette leggibili dei comandi, per comporre un messaggio pronunciabile.
    /// </param>
    /// <returns>
    /// Il conflitto rilevato. <see cref="HotkeyConflict.None"/> se la combinazione è
    /// libera.
    /// </returns>
    public HotkeyConflict Check(
        HotkeyBinding candidate,
        HotkeyProfile profile,
        IReadOnlyDictionary<HotkeyCommand, string>? commandLabels = null)
    {
        // 1. Combinazioni che il sistema non lascia mai arrivare all'applicazione.
        //    Assegnarle produrrebbe un comando che non funziona mai, e l'operatore
        //    passerebbe il tempo a chiedersi se ha sbagliato a premere.
        var systemKey = $"{candidate.Modifiers}:{candidate.Key.ToUpperInvariant()}";
        if (WindowsReserved.Contains(systemKey))
        {
            return new HotkeyConflict(
                HotkeyConflictKind.ReservedByWindows,
                $"{candidate.SpeechText} e' riservata da Windows e non puo' essere usata",
                CanOverride: false);
        }

        // 2. Conflitto interno: bloccante ma superabile con conferma esplicita.
        var occupiedBy = profile.FindConflict(candidate);
        if (occupiedBy is { } other)
        {
            var label = commandLabels?.GetValueOrDefault(other) ?? other.ToString();
            return new HotkeyConflict(
                HotkeyConflictKind.OtherBlindPhoneCommand,
                $"{candidate.SpeechText} e' gia' assegnata a {label}. Confermi la sostituzione?",
                CanOverride: true);
        }

        // 3. Conflitto con lo screen reader: avviso forte, ma la scelta resta
        //    all'operatore, che puo' non usare quella funzione.
        if (ScreenReaderReserved.TryGetValue(systemKey, out var usage))
        {
            return new HotkeyConflict(
                HotkeyConflictKind.UsedByScreenReader,
                $"Attenzione: {candidate.SpeechText} e' usata dallo screen reader per {usage}. "
                + "Confermi comunque?",
                CanOverride: true);
        }

        return HotkeyConflict.None;
    }

    /// <summary>
    /// Segnala che la registrazione di sistema di una scorciatoia globale non è
    /// riuscita.
    /// </summary>
    /// <param name="candidate">Combinazione rifiutata.</param>
    /// <param name="ownerProcess">Nome del processo che la detiene, se determinabile.</param>
    /// <remarks>
    /// Non è un conflitto rilevabile in anticipo: solo il sistema sa quali
    /// combinazioni globali sono già registrate, e lo comunica soltanto al momento
    /// del tentativo.
    /// </remarks>
    public static HotkeyConflict SystemRegistrationFailed(HotkeyBinding candidate, string? ownerProcess)
    {
        var message = ownerProcess is null
            ? $"{candidate.SpeechText} e' gia' usata da un altro programma"
            : $"{candidate.SpeechText} e' gia' usata dal programma {ownerProcess}";

        return new HotkeyConflict(HotkeyConflictKind.TakenByAnotherApplication, message, CanOverride: false);
    }
}
