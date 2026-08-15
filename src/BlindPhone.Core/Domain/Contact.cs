namespace BlindPhone.Core.Domain;

/// <summary>Tipo di recapito telefonico.</summary>
public enum PhoneNumberKind
{
    /// <summary>Interno del centralino.</summary>
    Extension,

    /// <summary>Telefono di ufficio.</summary>
    Work,

    /// <summary>Telefono cellulare.</summary>
    Mobile,

    /// <summary>Telefono di casa.</summary>
    Home,

    /// <summary>Fax.</summary>
    Fax,

    /// <summary>Altro recapito.</summary>
    Other,
}

/// <summary>Stato di presenza di un interno, ricavato dal PBX.</summary>
/// <remarks>
/// Trasforma il trasferimento da tentativo a decisione informata. È l'informazione
/// che l'operatore vedente ottiene guardando i LED del pannello e che senza questa
/// funzione manca del tutto a chi non vede (ANALISI §16.2, S2).
/// </remarks>
public enum PresenceState
{
    /// <summary>Presenza non nota: il provider non la fornisce o non è ancora arrivata.</summary>
    Unknown,

    /// <summary>Interno registrato e libero.</summary>
    Available,

    /// <summary>Interno in conversazione.</summary>
    Busy,

    /// <summary>Interno che sta squillando.</summary>
    Ringing,

    /// <summary>Interno in "non disturbare".</summary>
    DoNotDisturb,

    /// <summary>Interno assente o in pausa.</summary>
    Away,

    /// <summary>
    /// Interno non registrato: il telefono è spento o scollegato. Trasferire qui
    /// significa quasi certamente perdere il chiamante, quindi va avvisato prima.
    /// </summary>
    Offline,
}

/// <summary>Un recapito telefonico di un contatto.</summary>
/// <param name="Number">Numero, nella forma memorizzata.</param>
/// <param name="Kind">Tipo di recapito.</param>
/// <param name="IsPrimary">Se è il recapito da usare per la chiamata rapida.</param>
public sealed record PhoneNumber(string Number, PhoneNumberKind Kind, bool IsPrimary = false);

/// <summary>Un contatto della rubrica.</summary>
/// <remarks>
/// <para>
/// Due campi meritano una nota, perché non compaiono nelle rubriche dei softphone
/// generalisti e sono qui per ragioni operative precise:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <c>PhoneticName</c>: la sintesi vocale pronuncia male molti cognomi
///     italiani. Un operatore che sente "Ceccherini" letto male lo ripete male al
///     telefono. Il campo consente la resa corretta senza alterare il dato
///     anagrafico usato per la ricerca.
///     </description>
///   </item>
///   <item>
///     <description>
///     <c>DoNotTransfer</c>: alcuni interni non devono ricevere trasferimenti
///     diretti (direzione, uffici riservati). Il contrassegno fa scattare una
///     conferma esplicita invece di lasciare che l'errore accada.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed record Contact
{
    /// <summary>Identificativo locale del contatto.</summary>
    public required Guid Id { get; init; }

    /// <summary>Nome.</summary>
    public string? FirstName { get; init; }

    /// <summary>Cognome.</summary>
    public string? LastName { get; init; }

    /// <summary>Nome visualizzato; se assente si compone da nome e cognome.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Resa fonetica del nome per la sintesi vocale.</summary>
    public string? PhoneticName { get; init; }

    /// <summary>Organizzazione o ente di appartenenza.</summary>
    public string? Organization { get; init; }

    /// <summary>Reparto o ufficio.</summary>
    public string? Department { get; init; }

    /// <summary>Qualifica.</summary>
    public string? JobTitle { get; init; }

    /// <summary>
    /// Tipo di voce: distingue per esempio un interno da un reparto, da un
    /// servizio o da un numero di emergenza.
    /// </summary>
    /// <remarks>
    /// In una rubrica di grandi dimensioni è il campo che permette all'operatore di
    /// capire, sentendo un risultato di ricerca, se sta per chiamare una persona o
    /// un servizio. Cambia il modo in cui si presenta al telefono.
    /// </remarks>
    public string? Category { get; init; }

    /// <summary>
    /// Entità a cui la voce fa riferimento: il reparto di un medico, il servizio a
    /// cui appartiene un numero.
    /// </summary>
    public string? RelatedTo { get; init; }

    /// <summary>Edificio o padiglione.</summary>
    /// <remarks>
    /// Insieme a <see cref="Floor"/> risponde alla domanda che un centralino di una
    /// struttura complessa riceve di continuo: non "qual è il numero" ma "dove si
    /// trova". Per questo entrambi sono ricercabili e compaiono nella lettura del
    /// contatto, invece di finire fra le note.
    /// </remarks>
    public string? Building { get; init; }

    /// <summary>Piano.</summary>
    public string? Floor { get; init; }

    /// <summary>Indirizzo di posta elettronica.</summary>
    public string? Email { get; init; }

    /// <summary>Note libere.</summary>
    public string? Notes { get; init; }

    /// <summary>Recapiti telefonici.</summary>
    public IReadOnlyList<PhoneNumber> Numbers { get; init; } = [];

    /// <summary>Se il contatto è fra i preferiti.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Slot di chiamata rapida assegnato, da 1 a 20.</summary>
    public int? SpeedDialSlot { get; init; }

    /// <summary>Contrassegno VIP: fa scattare suoneria e priorità dedicate.</summary>
    public bool IsVip { get; init; }

    /// <summary>Se il contatto non deve ricevere trasferimenti senza conferma esplicita.</summary>
    public bool DoNotTransfer { get; init; }

    /// <summary>Sorgente da cui il contatto proviene.</summary>
    public string Source { get; init; } = "Locale";

    /// <summary>Istante dell'ultimo aggiornamento.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Presenza corrente dell'interno, se il contatto ne ha uno monitorato.</summary>
    public PresenceState Presence { get; init; } = PresenceState.Unknown;

    /// <summary>Nome da mostrare, composto se <see cref="DisplayName"/> è assente.</summary>
    /// <remarks>
    /// L'ultima risorsa è il numero: un contatto senza nome è raro ma esiste (voci
    /// importate da CSV incompleti), e restituire una stringa vuota produrrebbe una
    /// riga di elenco che lo screen reader legge come silenzio, cioè una voce
    /// invisibile a chi non vede.
    /// </remarks>
    public string EffectiveDisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName)) return DisplayName;

            var composed = string.Join(
                ' ',
                new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));

            if (!string.IsNullOrWhiteSpace(composed)) return composed;

            return PrimaryNumber ?? string.Empty;
        }
    }

    /// <summary>Nome da pronunciare, con la resa fonetica se presente.</summary>
    public string SpeechName => PhoneticName ?? EffectiveDisplayName;

    /// <summary>
    /// Collocazione fisica in forma pronunciabile, oppure <c>null</c> se ignota.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Restituisce per esempio "padiglione B, piano 2". Se un solo campo è
    /// valorizzato compone solo quello: dire "piano 2" senza sapere di quale
    /// edificio è comunque più utile che tacere, mentre una formula fissa con
    /// pezzi vuoti verrebbe letta come "padiglione, piano 2" e confonderebbe.
    /// </para>
    /// <para>
    /// <b>La parola non si aggiunge se il dato la porta già.</b> Molte rubriche
    /// scrivono per esteso "Padiglione Barbieri" e "Piano Secondo", e anteporre
    /// comunque l'etichetta produrrebbe "padiglione Padiglione Barbieri": una
    /// ripetizione che a schermo si perdona e in ascolto no, perché ogni parola
    /// costa tempo e chi ascolta a velocità alta sente due volte la stessa cosa
    /// senza capire se sia un errore del dato.
    /// </para>
    /// </remarks>
    public string? LocationSpeech
    {
        get
        {
            var parts = new List<string>(2);

            if (!string.IsNullOrWhiteSpace(Building)) parts.Add(WithLabel("padiglione", Building));
            if (!string.IsNullOrWhiteSpace(Floor)) parts.Add(WithLabel("piano", Floor));

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Antepone l'etichetta al valore, a meno che il valore non cominci già con quella.
    /// </summary>
    /// <param name="label">Parola da anteporre, in minuscolo.</param>
    /// <param name="value">Valore letto dalla rubrica.</param>
    /// <remarks>
    /// Il confronto pretende che dopo la parola ci sia un separatore: senza,
    /// "Pianoterra" verrebbe scambiato per un piano già etichettato e resterebbe
    /// senza la sua parola. La forma originale del dato viene conservata — se la
    /// rubrica scrive "Padiglione" con la maiuscola, si legge com'è scritto.
    /// </remarks>
    private static string WithLabel(string label, string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length > label.Length
            && trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase)
            && !char.IsLetterOrDigit(trimmed[label.Length]))
        {
            return trimmed;
        }

        return $"{label} {trimmed}";
    }

    /// <summary>
    /// Descrizione completa del contatto per la lettura a richiesta.
    /// </summary>
    /// <remarks>
    /// L'ordine segue l'utilità decrescente per chi ascolta: chi è, che cosa è,
    /// dove sta, a chi fa capo. L'operatore interrompe appena ha l'informazione che
    /// cercava, quindi ciò che serve più spesso va detto prima.
    /// </remarks>
    public string FullSpeechDescription
    {
        get
        {
            var parts = new List<string> { SpeechName };

            if (!string.IsNullOrWhiteSpace(Category)) parts.Add(Category);
            if (!string.IsNullOrWhiteSpace(Department)) parts.Add(Department);
            if (LocationSpeech is { } location) parts.Add(location);
            if (!string.IsNullOrWhiteSpace(Organization)) parts.Add(Organization);
            if (!string.IsNullOrWhiteSpace(RelatedTo)) parts.Add($"riferimento {RelatedTo}");

            return string.Join(", ", parts);
        }
    }

    /// <summary>Recapito predefinito: il primario, altrimenti il primo interno, altrimenti il primo disponibile.</summary>
    public string? PrimaryNumber
        => Numbers.FirstOrDefault(n => n.IsPrimary)?.Number
           ?? Numbers.FirstOrDefault(n => n.Kind == PhoneNumberKind.Extension)?.Number
           ?? Numbers.FirstOrDefault()?.Number;

    /// <summary>Interno del centralino, se il contatto ne ha uno.</summary>
    /// <remarks>
    /// Diverso da <see cref="PrimaryNumber"/>, che privilegia il recapito
    /// contrassegnato come primario anche quando è un cellulare. Dove si cerca un
    /// nome per passargli una chiamata, l'interno è il numero giusto: è quello che
    /// squilla sulla scrivania, ed è l'unico che regge un trasferimento.
    /// </remarks>
    public string? ExtensionNumber
        => Numbers.FirstOrDefault(n => n.Kind == PhoneNumberKind.Extension)?.Number;
}
