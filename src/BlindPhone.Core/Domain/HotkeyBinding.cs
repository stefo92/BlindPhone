namespace BlindPhone.Core.Domain;

/// <summary>Modificatori di una combinazione di tasti.</summary>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>Nessun modificatore: tasto singolo.</summary>
    None = 0,

    /// <summary>Tasto ALT.</summary>
    Alt = 1,

    /// <summary>Tasto CTRL.</summary>
    Control = 2,

    /// <summary>Tasto MAIUSC.</summary>
    Shift = 4,

    /// <summary>Tasto Windows.</summary>
    Windows = 8,
}

/// <summary>Ambito in cui una scorciatoia è attiva.</summary>
public enum HotkeyScope
{
    /// <summary>Attiva solo con BlindPhone in primo piano.</summary>
    Local,

    /// <summary>
    /// Attiva in tutto il sistema, anche con l'applicazione in secondo piano.
    /// </summary>
    /// <remarks>
    /// Serve all'operatore che prende appunti in un altro programma e deve
    /// rispondere senza cambiare finestra. È una funzione richiesta di continuo e
    /// spesso assente nei softphone generalisti.
    /// </remarks>
    Global,
}

/// <summary>
/// Una combinazione di tasti assegnata a un comando.
/// </summary>
/// <param name="Command">Comando eseguito.</param>
/// <param name="Key">Nome del tasto principale, per esempio "F9" o "M".</param>
/// <param name="Modifiers">Modificatori richiesti.</param>
/// <param name="Scope">Ambito di attivazione.</param>
public sealed record HotkeyBinding(
    HotkeyCommand Command,
    string Key,
    HotkeyModifiers Modifiers,
    HotkeyScope Scope)
{
    /// <summary>
    /// Rappresentazione leggibile e <b>pronunciabile</b> della combinazione.
    /// </summary>
    /// <remarks>
    /// Non usa i simboli abbreviati che si vedono nei menu: la sintesi vocale
    /// leggerebbe "Ctrl+Maiusc+F9" in modo poco comprensibile, mentre
    /// "Control Maiusc F9" si sente correttamente al primo ascolto. Questa stringa
    /// finisce sia nell'elenco delle scorciatoie sia negli annunci che spiegano
    /// quale tasto premere.
    /// </remarks>
    public string SpeechText
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Control");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Maiusc");
            if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Windows");
            parts.Add(HotkeyKeyNames.Speech(Key));
            return string.Join(' ', parts);
        }
    }

    /// <summary>Rappresentazione compatta per l'interfaccia visiva.</summary>
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Maiusc");
            if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
            parts.Add(HotkeyKeyNames.Display(Key));
            return string.Join('+', parts);
        }
    }

    /// <summary>Chiave di confronto per il rilevamento dei conflitti.</summary>
    public string ConflictKey => $"{Scope}:{Modifiers}:{Key.ToUpperInvariant()}";
}

/// <summary>Esito della verifica di conflitto su una combinazione.</summary>
public enum HotkeyConflictKind
{
    /// <summary>Nessun conflitto.</summary>
    None,

    /// <summary>La combinazione è già assegnata a un altro comando di BlindPhone.</summary>
    OtherBlindPhoneCommand,

    /// <summary>La combinazione è riservata da Windows e non può essere intercettata.</summary>
    ReservedByWindows,

    /// <summary>
    /// La combinazione è usata da NVDA o JAWS.
    /// </summary>
    /// <remarks>
    /// Non è un errore bloccante: l'operatore può avere buone ragioni per
    /// sovrascriverla, per esempio se non usa quella funzione dello screen reader.
    /// Ma va detto con chiarezza <b>prima</b> di salvare, perché il conflitto si
    /// manifesterebbe come una funzione dello screen reader che smette di
    /// rispondere, effetto difficile da ricondurre alla causa.
    /// </remarks>
    UsedByScreenReader,

    /// <summary>La registrazione di sistema è fallita: un'altra applicazione la usa già.</summary>
    TakenByAnotherApplication,
}

/// <summary>Risultato della verifica di conflitto.</summary>
/// <param name="Kind">Tipo di conflitto rilevato.</param>
/// <param name="Message">Spiegazione pronunciabile, pronta per l'annuncio.</param>
/// <param name="CanOverride">
/// <c>true</c> se l'operatore può comunque confermare l'assegnazione.
/// </param>
public sealed record HotkeyConflict(HotkeyConflictKind Kind, string Message, bool CanOverride)
{
    /// <summary>Assenza di conflitti.</summary>
    public static readonly HotkeyConflict None = new(HotkeyConflictKind.None, string.Empty, true);

    /// <summary>Indica se la combinazione è utilizzabile senza ulteriori conferme.</summary>
    public bool IsClear => Kind == HotkeyConflictKind.None;
}
