namespace BlindPhone.Core.Domain;

/// <summary>
/// Nomi pronunciabili dei tasti e regole su quali sono assegnabili.
/// </summary>
/// <remarks>
/// <para>
/// I nomi dei tasti che arrivano dall'interfaccia sono quelli dell'enumerazione
/// <c>System.Windows.Input.Key</c>, pensata per il codice e non per l'orecchio:
/// "OemComma" al posto di "virgola", "Prior" al posto di "Pagina su". Finché le
/// combinazioni erano fissate nel profilo predefinito la cosa non si notava, perché
/// erano quasi tutte tasti funzione; da quando l'operatore può assegnare qualunque
/// tasto, il nome grezzo finirebbe negli annunci e nella scheda dei tasti.
/// </para>
/// <para>
/// La tabella copre i tasti che si assegnano davvero. Per i tasti simbolo di cui il
/// nome dipende dal layout della tastiera si restituisce il nome grezzo invece di
/// indovinare: dire "punto e virgola" su una tastiera italiana quando il tasto ne
/// produce un altro sarebbe peggio che dire un nome tecnico.
/// </para>
/// </remarks>
public static class HotkeyKeyNames
{
    /// <summary>Nomi pronunciabili, per la sintesi vocale e la scheda dei tasti.</summary>
    private static readonly Dictionary<string, string> SpokenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = "Invio",
        ["Enter"] = "Invio",
        ["Escape"] = "Esc",
        ["Space"] = "barra spaziatrice",
        ["Back"] = "Backspace",
        ["Delete"] = "Canc",
        ["Insert"] = "Ins",
        ["Home"] = "Home",
        ["End"] = "Fine",
        ["Prior"] = "Pagina su",
        ["PageUp"] = "Pagina su",
        ["Next"] = "Pagina giu'",
        ["PageDown"] = "Pagina giu'",
        ["Up"] = "Freccia su",
        ["Down"] = "Freccia giu'",
        ["Left"] = "Freccia sinistra",
        ["Right"] = "Freccia destra",
        ["Tab"] = "Tab",

        // Il tasto e' marcato "Pause" o "Pausa/Interr" a seconda della tastiera: si
        // pronuncia "Pausa", che e' anche il nome della funzione a cui e' assegnato.
        ["Pause"] = "Pausa",
        ["Cancel"] = "Pausa",
        ["OemComma"] = "virgola",
        ["OemPeriod"] = "punto",
        ["OemMinus"] = "meno",
        ["OemPlus"] = "piu'",
        ["Add"] = "piu' del tastierino",
        ["Subtract"] = "meno del tastierino",
        ["Multiply"] = "per del tastierino",
        ["Divide"] = "diviso del tastierino",
        ["Decimal"] = "virgola del tastierino",
    };

    /// <summary>
    /// Tasti che non possono essere assegnati, con il motivo da pronunciare.
    /// </summary>
    /// <remarks>
    /// I modificatori sono esclusi perché da soli non sono una combinazione: chi
    /// preme solo Control sta iniziando a comporne una. Tab è escluso perché è il
    /// tasto con cui si esce dall'area di cattura, e assegnarlo renderebbe
    /// impossibile confermare l'assegnazione stessa.
    /// </remarks>
    private static readonly Dictionary<string, string> NotAssignable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LeftCtrl"] = "Control da solo non e' una combinazione: tienilo premuto e aggiungi un altro tasto",
        ["RightCtrl"] = "Control da solo non e' una combinazione: tienilo premuto e aggiungi un altro tasto",
        ["LeftAlt"] = "Alt da solo non e' una combinazione: tienilo premuto e aggiungi un altro tasto",
        ["RightAlt"] = "Alt da solo non e' una combinazione: tienilo premuto e aggiungi un altro tasto",
        ["LeftShift"] = "Maiusc da solo non e' una combinazione: tienilo premuto e aggiungi un altro tasto",
        ["RightShift"] = "Maiusc da solo non e' una combinazione: tienilo premuto e aggiungi un altro tasto",
        ["LWin"] = "Il tasto Windows da solo non e' una combinazione",
        ["RWin"] = "Il tasto Windows da solo non e' una combinazione",
        ["Apps"] = "Il tasto menu contestuale e' riservato al sistema",
        ["Capital"] = "Blocco maiuscole e' il tasto modificatore di NVDA e non va assegnato",
        ["CapsLock"] = "Blocco maiuscole e' il tasto modificatore di NVDA e non va assegnato",
        ["NumLock"] = "Blocco numerico cambia il significato del tastierino e non va assegnato",
        ["Scroll"] = "Blocco scorrimento non arriva in modo affidabile all'applicazione",
        ["Snapshot"] = "Stampa schermo e' intercettato dal sistema prima dell'applicazione",
        ["PrintScreen"] = "Stampa schermo e' intercettato dal sistema prima dell'applicazione",
        ["Sleep"] = "Il tasto di sospensione e' gestito dal sistema",
        ["Tab"] = "Tab serve a spostarsi fra i campi e non puo' essere assegnato",
        ["None"] = "Nessun tasto riconosciuto",
        ["System"] = "Nessun tasto riconosciuto",
        ["ImeProcessed"] = "Il tasto e' stato consumato dal metodo di immissione",
        ["DeadCharProcessed"] = "Il tasto e' un accento morto e non identifica una combinazione",
    };

    /// <summary>
    /// Restituisce il nome pronunciabile di un tasto.
    /// </summary>
    /// <param name="key">Nome tecnico del tasto.</param>
    public static string Speech(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        if (SpokenNames.TryGetValue(key, out var spoken)) return spoken;

        // Le cifre della riga superiore arrivano come D0..D9: pronunciarle cosi'
        // farebbe sentire "di cinque" invece di "cinque".
        if (key.Length == 2 && key[0] == 'D' && char.IsAsciiDigit(key[1])) return key[1].ToString();

        // Il tastierino numerico va distinto: "5" e "5 del tastierino" sono due tasti
        // diversi e l'operatore deve sapere quale premere.
        if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && key.Length == 7)
            return $"{key[6]} del tastierino";

        return key;
    }

    /// <summary>Restituisce il nome compatto di un tasto, per l'interfaccia visiva.</summary>
    /// <param name="key">Nome tecnico del tasto.</param>
    public static string Display(string key) => key switch
    {
        _ when string.IsNullOrWhiteSpace(key) => string.Empty,
        "Return" or "Enter" => "Invio",
        "Escape" => "Esc",
        "Pause" or "Cancel" => "Pausa",
        "Space" => "Spazio",
        "Prior" or "PageUp" => "PagSu",
        "Next" or "PageDown" => "PagGiu",
        "Up" => "Su",
        "Down" => "Giu",
        "Left" => "Sinistra",
        "Right" => "Destra",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemMinus" => "-",
        "OemPlus" => "+",
        _ when key.Length == 2 && key[0] == 'D' && char.IsAsciiDigit(key[1]) => key[1].ToString(),
        _ when key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && key.Length == 7
            => $"Tast{key[6]}",
        _ => key,
    };

    /// <summary>
    /// Verifica se un tasto può essere assegnato a un comando.
    /// </summary>
    /// <param name="key">Nome tecnico del tasto.</param>
    /// <param name="reason">Motivo del rifiuto, pronto per l'annuncio.</param>
    /// <returns><c>true</c> se il tasto è assegnabile.</returns>
    public static bool IsAssignable(string? key, out string reason)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            reason = "Nessun tasto riconosciuto";
            return false;
        }

        if (NotAssignable.TryGetValue(key, out var why))
        {
            reason = why;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Riporta a un nome solo i tasti che il sistema consegna sotto due nomi diversi.
    /// </summary>
    /// <param name="key">Nome consegnato dall'interfaccia.</param>
    /// <remarks>
    /// <para>
    /// Il caso è il tasto <b>Pausa</b>, e non è un dettaglio teorico. Premuto da solo
    /// Windows lo consegna come <c>Pause</c>; premuto insieme a Control diventa
    /// «Break» e arriva come <c>Cancel</c>, che è un nome di tasto che sulla tastiera
    /// non esiste. Senza questa riduzione, una combinazione assegnata su
    /// Control Alt Pausa verrebbe cercata nel profilo sotto un nome che il profilo non
    /// ha, e il comando non partirebbe: per l'operatore, un tasto che non fa niente.
    /// </para>
    /// <para>
    /// Si normalizza verso <c>Pause</c> e non verso <c>Cancel</c> perché è il nome
    /// scritto sul tasto, quello che compare nella scheda dei tasti rapidi e quello
    /// che la voce pronuncia.
    /// </para>
    /// </remarks>
    public static string Normalize(string? key)
        => string.Equals(key, "Cancel", StringComparison.OrdinalIgnoreCase) ? "Pause" : key ?? string.Empty;

    /// <summary>
    /// Restituisce il nome vero di un tasto che il sistema ha marcato come «di
    /// sistema».
    /// </summary>
    /// <param name="key">Nome consegnato dall'interfaccia.</param>
    /// <param name="systemKey">Nome consegnato nel campo riservato ai tasti di sistema.</param>
    /// <remarks>
    /// <para>
    /// Windows tratta come «di sistema» F10 da solo e qualunque combinazione con Alt.
    /// In quel caso WPF mette in <c>e.Key</c> il valore segnaposto <c>System</c> e il
    /// tasto vero in <c>e.SystemKey</c>. Chi legge il solo <c>e.Key</c> cerca nella
    /// mappa un tasto chiamato "System", che non esiste: il comando non viene
    /// riconosciuto e il tasto prosegue verso il comportamento predefinito di Windows,
    /// che per F10 apre la barra dei menu e sposta il cursore sul titolo della
    /// finestra.
    /// </para>
    /// <para>
    /// È esattamente il sintomo osservato sul campo con F10 assegnato alla rubrica: il
    /// tasto non apriva nulla e il cursore finiva in un punto della finestra che non
    /// c'entrava niente.
    /// </para>
    /// </remarks>
    public static string ResolveSystemKey(string? key, string? systemKey)
        => string.Equals(key, "System", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(systemKey)
            ? systemKey
            : key ?? string.Empty;

    /// <summary>Indica se il tasto è un tasto funzione da F1 a F24.</summary>
    /// <param name="key">Nome tecnico del tasto.</param>
    /// <remarks>
    /// Serve alla regola che lascia passare i tasti al campo di composizione. Il
    /// controllo va fatto qui e non con un semplice confronto sull'iniziale: anche la
    /// lettera F comincia per F, e trattarla come tasto funzione le impedirebbe di
    /// essere digitata nel numero da comporre.
    /// </remarks>
    public static bool IsFunctionKey(string? key)
    {
        if (key is null || key.Length is < 2 or > 3) return false;
        if (key[0] is not ('F' or 'f')) return false;

        return int.TryParse(key.AsSpan(1), out var number) && number is >= 1 and <= 24;
    }

    /// <summary>
    /// Indica se il tasto, premuto senza modificatori, verrebbe assorbito dal campo
    /// di composizione.
    /// </summary>
    /// <param name="key">Nome tecnico del tasto.</param>
    /// <remarks>
    /// È il caso di lettere e cifre: con il cursore nel dialer devono comporre il
    /// numero, quindi il comando assegnato non scatta. Fanno eccezione i tasti
    /// funzione ed Esc, che il campo di composizione non usa e che quindi arrivano
    /// sempre al comando. Non è un errore, ma va detto al momento dell'assegnazione:
    /// altrimenti si manifesta come "il tasto funziona a volte sì e a volte no", che
    /// è il tipo di sintomo che nessuno riconduce alla configurazione.
    /// </remarks>
    public static bool IsSwallowedByDialer(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        return !IsFunctionKey(key) && key is not "Escape";
    }
}
