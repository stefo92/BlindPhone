using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;

namespace BlindPhone.Core.Speech;

/// <summary>
/// Che cosa farà Invio sul numero arrivato da un link.
/// </summary>
/// <remarks>
/// Esiste perché l'annuncio deve dire il tasto giusto, e il tasto giusto dipende da
/// quello che sta succedendo: «premi Invio per chiamare» detto a chi è in
/// conversazione sarebbe falso, perché lì Invio non compone.
/// </remarks>
public enum CallLinkEnterEffect
{
    /// <summary>Invio compone il numero.</summary>
    Dial,

    /// <summary>
    /// Invio trasferisce subito, alla cieca: la procedura di trasferimento aspetta la
    /// destinazione.
    /// </summary>
    Transfer,

    /// <summary>
    /// Modalità semplificata con un chiamante: Invio chiama il numero per passargli il
    /// chiamante.
    /// </summary>
    Consult,

    /// <summary>
    /// In conversazione, modalità standard: Invio non compone finché la chiamata non
    /// va in attesa.
    /// </summary>
    HoldFirst,

    /// <summary>C'è un trasferimento in volo e Invio per ora non compone.</summary>
    Busy,
}

/// <summary>
/// Testi pronunciati quando arriva un link di chiamata.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il numero viene prima di tutto il resto.</b> È la cosa che l'operatore deve
/// verificare prima di premere Invio — il link l'ha scritto qualcun altro — e chi
/// ascolta a velocità alta interrompe appena ha capito: quello che sta in fondo alla
/// frase rischia di non essere sentito.
/// </para>
/// <para>
/// <b>La frase dice sempre che cosa fa Invio adesso.</b> Il link non compone da solo,
/// e l'operatore deve sapere qual è il gesto che lo fa, oppure perché in questo
/// momento non c'è.
/// </para>
/// </remarks>
public static class CallLinkTexts
{
    /// <summary>
    /// Decide che cosa farà Invio, con le stesse regole del campo di composizione.
    /// </summary>
    /// <param name="state">Stato della postazione.</param>
    /// <param name="simplified">La postazione è in modalità semplificata.</param>
    /// <remarks>
    /// <para>
    /// Non c'è una regola nuova: è la regola di Invio, letta in anticipo. In modalità
    /// semplificata decide <see cref="SimplifiedKeypad"/>, come per il tasto vero; in
    /// modalità standard decide la composizione, che durante l'inserimento della
    /// destinazione trasferisce e con qualcuno in linea non fa niente
    /// (<see cref="CallActionContext.CanDial"/>).
    /// </para>
    /// <para>
    /// Il numero del link nel campo conta come destinazione digitata: lo stato viene
    /// valutato come sarà dopo l'inserimento, non come era prima.
    /// </para>
    /// </remarks>
    public static CallLinkEnterEffect EffectOf(CallActionContext state, bool simplified)
    {
        var dopo = state with { HasTypedDestination = true };

        if (simplified)
        {
            return SimplifiedKeypad.Resolve(KeypadKey.Confirm, dopo) switch
            {
                KeypadAction.Dial => CallLinkEnterEffect.Dial,
                KeypadAction.StartConsultation => CallLinkEnterEffect.Consult,
                _ => CallLinkEnterEffect.Busy,
            };
        }

        if (dopo.TransferPhase == TransferPhase.EnteringDestination) return CallLinkEnterEffect.Transfer;

        if (!dopo.CanDial) return dopo.IsTransferring ? CallLinkEnterEffect.Busy : CallLinkEnterEffect.HoldFirst;

        return CallLinkEnterEffect.Dial;
    }

    /// <summary>
    /// Annuncio del numero messo nel campo di composizione.
    /// </summary>
    /// <param name="dial">Quello che è stato scritto nel campo.</param>
    /// <param name="extension">Interno indicato dal link, da chiedere dopo la risposta.</param>
    /// <param name="effect">Che cosa farà Invio.</param>
    /// <param name="grouping">Raggruppamento per la lettura delle cifre.</param>
    /// <param name="holdKeyName">Tasto dell'attesa, in forma pronunciabile.</param>
    /// <param name="transferKeyName">Tasto del trasferimento, in forma pronunciabile.</param>
    /// <returns>Per esempio «Numero da link: zero sei, uno due… Premi Invio per chiamare».</returns>
    /// <remarks>
    /// L'interno indicato con <c>;ext=</c> si dice e non si compone: va chiesto a chi
    /// risponde, o digitato con i toni quando il risponditore automatico lo chiede, e
    /// nessuna delle due cose si può fare prima che la chiamata sia instaurata.
    /// </remarks>
    public static string Inserted(
        string dial,
        string? extension,
        CallLinkEnterEffect effect,
        DigitGrouping grouping,
        string holdKeyName,
        string transferKeyName)
    {
        var cosa = IsNumber(dial) ? "Numero da link" : "Indirizzo da link";
        var chi = SpokenDestination(dial, grouping);

        var interno = string.IsNullOrEmpty(extension)
            ? string.Empty
            : $", interno {SpeechFormatter.Digits(extension)} da chiedere dopo la risposta";

        return effect switch
        {
            CallLinkEnterEffect.Transfer =>
                $"{cosa} come destinazione del trasferimento: {chi}{interno}. Premi Invio per trasferire",

            CallLinkEnterEffect.Consult =>
                $"{cosa}: {chi}{interno}. Premi Invio per chiamarlo e passargli il chiamante",

            CallLinkEnterEffect.HoldFirst =>
                $"{cosa}: {chi}{interno}, nel campo di composizione. Sei in conversazione e Invio non compone: "
                + $"metti prima in attesa con {holdKeyName}, oppure premi {transferKeyName} per passargli il chiamante",

            CallLinkEnterEffect.Busy =>
                $"{cosa}: {chi}{interno}, nel campo di composizione. C'e' un trasferimento in corso: "
                + "Invio lo comporra' quando sara' concluso",

            _ => $"{cosa}: {chi}{interno}. Premi Invio per chiamare",
        };
    }

    /// <summary>
    /// Aggiunge all'annuncio che Windows non ha dato il primo piano a BlindPhone.
    /// </summary>
    /// <param name="text">Annuncio del numero inserito.</param>
    /// <param name="showWindowKeyName">
    /// Scorciatoia globale che riporta davanti la finestra, se assegnata.
    /// </param>
    /// <remarks>
    /// Senza questa coda «premi Invio per chiamare» sarebbe un'istruzione sbagliata:
    /// Invio andrebbe al browser o al gestionale da cui è partito il link, e lì può
    /// voler dire inviare un modulo. Il numero resta nel campo, quindi basta
    /// raggiungere la finestra.
    /// </remarks>
    public static string WithForegroundDenied(string text, string? showWindowKeyName)
        => $"{text}. Attenzione: Windows non ha portato BlindPhone in primo piano e Invio andrebbe ancora "
           + $"al programma di prima. Raggiungi BlindPhone con {showWindowKeyName ?? "Alt Tab"}";

    /// <summary>Link scartato perché non valido.</summary>
    /// <param name="reason">Motivo restituito da <see cref="CallLinkParser"/>.</param>
    public static string Rejected(string reason) => $"Link di chiamata non usato: {reason}";

    /// <summary>Link scartato perché la postazione è in pausa.</summary>
    /// <param name="pauseKeyName">Tasto che toglie la pausa, in forma pronunciabile.</param>
    /// <remarks>
    /// In pausa il campo di composizione è bloccato e chi ci scrive viene avvisato: il
    /// link segue la stessa regola invece di aggirarla, e dice come uscirne.
    /// </remarks>
    public static string WhilePaused(string pauseKeyName)
        => "Link di chiamata non usato: la postazione e' in pausa e il campo di composizione e' bloccato. "
           + $"Togli la pausa con {pauseKeyName} e riapri il link";

    /// <summary>Link scartato perché, in modalità semplificata, sta squillando una chiamata.</summary>
    /// <remarks>
    /// Mentre squilla, in modalità semplificata il campo non accetta testo: Invio
    /// diventerebbe subito il tasto che passa il chiamante al numero del link, prima
    /// ancora che l'operatore sappia chi sta chiamando.
    /// </remarks>
    public static string WhileRinging()
        => "Link di chiamata non usato: sta squillando una chiamata. Rispondi o rifiuta, poi riapri il link";

    /// <summary>Forma breve per la riga braille.</summary>
    /// <param name="dial">Quello che è stato scritto nel campo.</param>
    /// <remarks>
    /// Il codice d'accesso di una riunione Zoom non compare nemmeno qui: la riga
    /// braille la legge chiunque passi accanto alla postazione.
    /// </remarks>
    public static string Braille(string dial) => $"LINK {ZoomRoomConnector.Redact(dial)}";

    /// <summary>
    /// Destinazione in forma pronunciabile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Le cifre si leggono una per una, come ogni numero del posto operatore. Nomi e
    /// domini si leggono con la punteggiatura detta a parole — «mario punto rossi
    /// chiocciola ente punto it» — perché un punto taciuto fa sentire «mario rossi»,
    /// che è un altro indirizzo.
    /// </para>
    /// <para>
    /// Una riunione Zoom si descrive senza il codice d'accesso, per la ragione
    /// spiegata in <see cref="ZoomRoomConnector.Redact"/>.
    /// </para>
    /// </remarks>
    public static string SpokenDestination(string dial, DigitGrouping grouping)
    {
        if (ZoomRoomConnector.Describe(dial) is { } riunione) return riunione;

        var chiocciola = dial.LastIndexOf('@');

        if (chiocciola < 0) return IsNumber(dial) ? SpeechFormatter.Digits(dial, grouping) : Spell(dial);

        var utente = dial[..chiocciola];
        var dominio = dial[(chiocciola + 1)..];

        var utenteParlato = IsNumber(utente) ? SpeechFormatter.Digits(utente, grouping) : Spell(utente);

        return $"{utenteParlato} chiocciola {Spell(dominio)}";
    }

    private static bool IsNumber(string text)
        => text.Length > 0 && text.All(c => char.IsAsciiDigit(c) || c == '+');

    private static string Spell(string text)
        => text.Replace(".", " punto ", StringComparison.Ordinal)
               .Replace("_", " trattino basso ", StringComparison.Ordinal)
               .Replace("-", " trattino ", StringComparison.Ordinal)
               .Replace(":", " due punti ", StringComparison.Ordinal)
               .Replace("  ", " ", StringComparison.Ordinal)
               .Trim();
}
