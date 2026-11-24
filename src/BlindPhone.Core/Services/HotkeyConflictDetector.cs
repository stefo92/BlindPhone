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

        // Alt+F4 resta qui anche se BlindPhone la gestisce: la usa per chiudere, che
        // e' quello che Windows le fa fare dappertutto. Assegnarle un altro comando
        // renderebbe imprevedibile il gesto con cui si chiude qualunque finestra, e
        // lascerebbe la postazione senza il modo standard di spegnersi.
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
    /// <param name="takenElsewhere">
    /// Prova presso il sistema: restituisce <c>true</c> se la combinazione non si
    /// può registrare perché la tiene un altro programma o Windows stesso. Si
    /// interroga solo per le combinazioni globali. Se manca, la prova non si fa e
    /// l'eventuale rifiuto si scopre al salvataggio.
    /// </param>
    /// <returns>
    /// Il conflitto rilevato. <see cref="HotkeyConflict.None"/> se la combinazione è
    /// libera.
    /// </returns>
    /// <remarks>
    /// <para>
    /// L'ordine delle verifiche conta. Ciò che non si può superare viene prima di ciò
    /// che chiede una conferma: chiedere all'operatore se togliere la combinazione a
    /// Rispondi e poi dirgli che tanto non funzionerebbe lo costringerebbe a una
    /// decisione inutile, e gli lascerebbe il dubbio di aver tolto qualcosa.
    /// </para>
    /// </remarks>
    public HotkeyConflict Check(
        HotkeyBinding candidate,
        HotkeyProfile profile,
        IReadOnlyDictionary<HotkeyCommand, string>? commandLabels = null,
        Func<HotkeyBinding, bool>? takenElsewhere = null)
    {
        // 1. Combinazioni che il sistema non lascia mai arrivare all'applicazione.
        //    Assegnarle produrrebbe un comando che non funziona mai, e l'operatore
        //    passerebbe il tempo a chiedersi se ha sbagliato a premere.
        var systemKey = candidate.ChordKey;
        if (WindowsReserved.Contains(systemKey))
        {
            return new HotkeyConflict(
                HotkeyConflictKind.ReservedByWindows,
                $"{candidate.SpeechText} e' riservata da Windows e non puo' essere usata",
                CanOverride: false);
        }

        // 2. Combinazione globale gia' registrata da qualcun altro. Si prova adesso,
        //    al momento dell'assegnazione, invece di scoprirlo al salvataggio: e'
        //    l'unico conflitto che solo Windows conosce, e senza questa prova
        //    l'operatore lo sentirebbe annunciare quando ha gia' chiuso il pannello.
        //    Solo per le globali: una locale non si registra presso il sistema, e
        //    provarla darebbe rifiuti che non la riguardano, come quello di F12 da
        //    solo, che Windows riserva al debugger.
        if (candidate.Scope == HotkeyScope.Global && takenElsewhere?.Invoke(candidate) == true)
        {
            return new HotkeyConflict(
                HotkeyConflictKind.TakenByAnotherApplication,
                $"{candidate.SpeechText} e' gia' registrata da un altro programma o da Windows: "
                + "come combinazione globale non funzionerebbe. Scegline un'altra",
                CanOverride: false);
        }

        // 3. Conflitto interno: bloccante ma superabile con conferma esplicita.
        var occupiedBy = profile.FindConflict(candidate);
        if (occupiedBy is { } other)
        {
            var label = Label(other, commandLabels);
            return new HotkeyConflict(
                HotkeyConflictKind.OtherBlindPhoneCommand,
                $"{candidate.SpeechText} e' gia' assegnata a {label}. Confermi la sostituzione?",
                CanOverride: true);
        }

        // 4. Conflitto fra ambiti: stessi tasti, comandi diversi, una globale e una
        //    locale. Superabile come il precedente, ma il messaggio deve dire perche'
        //    e' un conflitto, visto che a prima vista le due combinazioni non si
        //    toccano: stanno in colonne diverse della tabella.
        if (profile.FindOtherScopeConflict(candidate) is { } shadowed)
        {
            var label = Label(shadowed.Command, commandLabels);
            var mine = Label(candidate.Command, commandLabels);

            var why = candidate.Scope == HotkeyScope.Global
                ? $"{candidate.SpeechText} e' la combinazione locale di {label}. Se diventa la globale di "
                  + $"{mine}, Windows la consegna a {mine} anche con BlindPhone in primo piano, e {label} "
                  + "smette di rispondere"
                : $"{candidate.SpeechText} e' la combinazione globale di {label}. Windows la consegna a "
                  + $"{label} anche con BlindPhone in primo piano, quindi come locale di {mine} non "
                  + "arriverebbe mai";

            return new HotkeyConflict(
                HotkeyConflictKind.OtherBlindPhoneCommand,
                $"{why}. Confermi la sostituzione?",
                CanOverride: true);
        }

        // 5. Conflitto con lo screen reader: avviso forte, ma la scelta resta
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

    private static string Label(HotkeyCommand command, IReadOnlyDictionary<HotkeyCommand, string>? labels)
        => labels?.GetValueOrDefault(command) ?? command.ToString();

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
