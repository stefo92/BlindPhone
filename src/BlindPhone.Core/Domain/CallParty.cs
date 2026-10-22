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
    /// <para>
    /// Diverso dal controllo su <see cref="SpeechName"/>, che quando il nome manca
    /// ricade sul numero e quindi non è mai vuoto: usarlo per sapere se c'è un nome
    /// darebbe sempre risposta affermativa.
    /// </para>
    /// <para>
    /// Un nome che è soltanto il numero non conta: vedi <see cref="MeaningfulName"/>.
    /// </para>
    /// </remarks>
    public bool HasName
        => !string.IsNullOrWhiteSpace(PhoneticName) || MeaningfulName is not null;

    /// <summary>
    /// Testo da usare per la sintesi vocale, con la resa fonetica se disponibile.
    /// </summary>
    /// <remarks>
    /// La priorità è: nome fonetico, poi nome visualizzato, poi numero. Il nome
    /// fonetico esiste perché la sintesi pronuncia male molti cognomi italiani, e
    /// un operatore che sente un nome sbagliato lo ripete sbagliato al telefono
    /// (ANALISI §12.1). Del nome visualizzato conta <see cref="MeaningfulName"/>: un
    /// numero scritto al posto del nome lascia il posto al numero vero.
    /// </remarks>
    public string SpeechName => PhoneticName ?? MeaningfulName ?? Number;

    /// <summary>
    /// Il nome della controparte, quando dice qualcosa che il numero non dice già.
    /// </summary>
    /// <value>
    /// <see cref="DisplayName"/>, oppure <c>null</c> se manca o se è il numero stesso
    /// scritto un'altra volta.
    /// </value>
    /// <remarks>
    /// <para>
    /// <b>Un centralino che per un chiamante non ha un nome può scrivere al suo posto
    /// il numero</b>: <c>"3491234567" &lt;sip:3491234567@…&gt;</c>. Preso per un nome,
    /// compariva due volte — «Chiamata da: 3491234567 (3491234567)» — e l'annuncio lo
    /// passava alla sintesi come numero intero prima di rileggerlo cifra per cifra. Lo
    /// stesso accadeva con le voci di rubrica senza nominativo, che prestavano il
    /// proprio numero come nome.
    /// </para>
    /// <para>
    /// <b>Il confronto guarda le cifre, non il testo.</b> Spazi, punti e trattini non
    /// fanno un nome diverso, e nemmeno il prefisso internazionale: «+39 349 1234567»
    /// e «0039 349 1234567» sono lo stesso numero di «3491234567». Il 39 si toglie solo
    /// dopo un <c>+</c> o un <c>00</c>, perché un cellulare italiano può cominciare per
    /// 39 da sé.
    /// </para>
    /// <para>
    /// <b>Basta una lettera per fare un nome.</b> «Chiamate esterne» resta, e resta
    /// anche un'etichetta che contiene il numero: toglierlo da lì vorrebbe dire
    /// indovinare come il centralino compone le proprie etichette, ed è una scelta da
    /// fare dopo averne visto un caso vero. Resta anche un nome fatto di cifre diverse
    /// dal numero — l'interno da cui una chiamata è stata inoltrata, per esempio —
    /// perché quello un'informazione la porta.
    /// </para>
    /// </remarks>
    public string? MeaningfulName => MeaningfulNameOf(DisplayName, Number);

    /// <summary>
    /// Il nome, oppure <c>null</c> se è vuoto o se è soltanto il numero scritto
    /// un'altra volta.
    /// </summary>
    /// <param name="name">Nome come arriva dalla segnalazione, dalla rubrica o dalla coda.</param>
    /// <param name="number">Numero a cui il nome si accompagna.</param>
    /// <returns>Il nome invariato, oppure <c>null</c>.</returns>
    /// <remarks>
    /// Serve a chi costruisce una controparte, per non metterci il numero come nome fin
    /// dall'inizio. La regola è quella di <see cref="MeaningfulName"/>.
    /// </remarks>
    public static string? MeaningfulNameOf(string? name, string? number)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        return IsSameNumber(name, number) ? null : name;
    }

    private static bool IsSameNumber(string name, string? number)
    {
        if (string.IsNullOrWhiteSpace(number) || name.Any(char.IsLetter)) return false;

        var cifre = NationalDigits(name);
        return cifre.Length > 0 && string.Equals(cifre, NationalDigits(number), StringComparison.Ordinal);
    }

    /// <summary>
    /// Le cifre di un numero, senza prefisso internazionale e senza il 39 dell'Italia.
    /// </summary>
    private static string NationalDigits(string value)
    {
        var trimmed = value.Trim();
        var cifre = new string([.. trimmed.Where(char.IsAsciiDigit)]);

        var internazionale = trimmed.StartsWith('+');
        if (!internazionale && cifre.StartsWith("00", StringComparison.Ordinal))
        {
            cifre = cifre[2..];
            internazionale = true;
        }

        return internazionale && cifre.StartsWith("39", StringComparison.Ordinal) ? cifre[2..] : cifre;
    }
}
