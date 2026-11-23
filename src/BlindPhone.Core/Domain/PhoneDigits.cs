namespace BlindPhone.Core.Domain;

/// <summary>
/// Le due regole con cui un numero di telefono si riduce per essere confrontato.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stanno in un posto solo perché devono restare uguali ovunque.</b> Prima di questa
/// classe la stessa riga era copiata in cinque file. Se una copia cambia — per esempio
/// per accettare l'asterisco dei codici di servizio — e le altre no, due parti del
/// prodotto confrontano lo stesso interno in due modi: lo stato di un collega arriva e
/// non viene attribuito a nessuno, e niente lo segnala.
/// </para>
/// <para>
/// <b>Solo cifre ASCII.</b> <see cref="char.IsDigit(char)"/> accetterebbe anche le cifre
/// di altri alfabeti, che in un numero di telefono non hanno senso e che la voce
/// pronuncerebbe in modo imprevedibile.
/// </para>
/// <para>
/// La forma per <b>comporre</b> un numero, che conserva il più iniziale, è un'altra
/// cosa e sta in <see cref="CallerIdentity.Normalize"/>.
/// </para>
/// </remarks>
public static class PhoneDigits
{
    /// <summary>Lunghezza della chiave di confronto restituita da <see cref="MatchKey"/>.</summary>
    public const int MatchKeyLength = 9;

    /// <summary>
    /// Le sole cifre di un numero: la forma con cui si confrontano gli interni.
    /// </summary>
    /// <param name="number">Numero nella forma in cui è arrivato.</param>
    /// <returns>Le cifre, nell'ordine; stringa vuota se non ce ne sono o se il numero manca.</returns>
    /// <remarks>
    /// Il centralino può riportare l'interno con spazi o punteggiatura, e la rubrica
    /// può averlo memorizzato in un'altra forma ancora. Confrontando le sole cifre,
    /// «4021» e «40 21» restano lo stesso interno.
    /// </remarks>
    public static string Only(string? number)
        => number is null ? string.Empty : new string([.. number.Where(char.IsAsciiDigit)]);

    /// <summary>
    /// Chiave di confronto di un numero: le sole cifre, al massimo le ultime nove.
    /// </summary>
    /// <param name="number">Numero nella forma in cui è arrivato.</param>
    /// <returns>La chiave, oppure <c>null</c> se il numero non contiene cifre.</returns>
    /// <remarks>
    /// Lo stesso chiamante può presentarsi con o senza prefisso di uscita, in formato
    /// internazionale o nazionale, come interno o come numero diretto. Le ultime cifre
    /// lo riconoscono in tutte queste forme. Nove bastano a rendere improbabile una
    /// collisione senza far fallire il confronto sugli interni brevi, che restano interi.
    /// </remarks>
    public static string? MatchKey(string? number)
    {
        var digits = Only(number);
        if (digits.Length == 0) return null;

        return digits.Length <= MatchKeyLength ? digits : digits[^MatchKeyLength..];
    }
}
