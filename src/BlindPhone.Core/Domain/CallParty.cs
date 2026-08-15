namespace BlindPhone.Core.Domain;

/// <summary>
/// La controparte di una chiamata: chi c'è dall'altro lato.
/// </summary>
/// <param name="Number">Numero grezzo come arrivato dalla segnalazione.</param>
/// <param name="DisplayName">
/// Nome visualizzato, se fornito dalla segnalazione o risolto dalla rubrica.
/// </param>
/// <param name="PhoneticName">
/// Resa fonetica del nome per la sintesi vocale, se il contatto ne ha una.
/// </param>
/// <param name="IsInternal">
/// <c>true</c> se la controparte è un interno del centralino. Distinzione
/// operativa importante: cambia la suoneria, cambia il testo dell'annuncio e
/// determina se serve la conferma anti-frode prima di trasferire.
/// </param>
public sealed record CallParty(
    string Number,
    string? DisplayName = null,
    string? PhoneticName = null,
    bool IsInternal = false)
{
    /// <summary>
    /// Controparte sconosciuta: usata quando la segnalazione non porta alcun
    /// identificativo (numero riservato, chiamata anonima).
    /// </summary>
    public static readonly CallParty Unknown = new(string.Empty);

    /// <summary>
    /// <c>true</c> se il numero è stato ritrovato in rubrica e il nome viene da lì.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distingue il nome che <b>si è verificato</b> da quello che <b>è stato
    /// dichiarato</b>. Un nome che arriva dall'intestazione SIP lo sceglie chi chiama:
    /// su una linea esterna non c'è nulla che ne garantisca la verità, e annunciarlo
    /// con lo stesso tono di un nome ritrovato in rubrica metterebbe l'operatore nella
    /// condizione di non poter distinguere le due cose.
    /// </para>
    /// <para>
    /// È anche ciò che permette all'annuncio di comportarsi come previsto: chi è in
    /// rubrica si annuncia con nome e interno, chi non c'è con il solo numero.
    /// </para>
    /// </remarks>
    public bool IsKnownContact { get; init; }

    /// <summary>
    /// Interno del centralino che la rubrica associa alla controparte, se ne ha uno.
    /// </summary>
    /// <remarks>
    /// Non coincide sempre con <see cref="Number"/>: lo stesso contatto può chiamare
    /// dal cellulare pur avendo un interno sulla scrivania. Serve a decidere se il
    /// numero che sta chiamando <b>è</b> l'interno, cioè se annunciarlo come tale.
    /// </remarks>
    public string? Extension { get; init; }

    /// <summary>
    /// Indica se della controparte non si conosce nulla di pronunciabile.
    /// </summary>
    public bool IsAnonymous => string.IsNullOrWhiteSpace(Number) && string.IsNullOrWhiteSpace(DisplayName);

    /// <summary>
    /// <c>true</c> se la controparte ha un nome, da qualunque parte provenga.
    /// </summary>
    /// <remarks>
    /// Diverso dal controllo su <see cref="SpeechName"/>, che quando il nome manca
    /// ricade sul numero e quindi non è mai vuoto: usarlo per sapere se c'è un nome
    /// darebbe sempre risposta affermativa.
    /// </remarks>
    public bool HasName
        => !string.IsNullOrWhiteSpace(PhoneticName) || !string.IsNullOrWhiteSpace(DisplayName);

    /// <summary>
    /// Testo da usare per la sintesi vocale, con la resa fonetica se disponibile.
    /// </summary>
    /// <remarks>
    /// La priorità è: nome fonetico, poi nome visualizzato, poi numero. Il nome
    /// fonetico esiste perché la sintesi pronuncia male molti cognomi italiani, e
    /// un operatore che sente un nome sbagliato lo ripete sbagliato al telefono
    /// (ANALISI §12.1).
    /// </remarks>
    public string SpeechName => PhoneticName ?? DisplayName ?? Number;
}
