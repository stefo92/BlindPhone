using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>Una voce di rubrica trovata digitando il suo interno.</summary>
/// <param name="Contact">La voce.</param>
/// <param name="Number">
/// L'interno così come è scritto in rubrica, che può avere spazi o trattini che
/// l'operatore non ha digitato.
/// </param>
public sealed record ExtensionMatch(Contact Contact, string Number);

/// <summary>
/// Ricerca in rubrica per interno digitato nel campo di composizione.
/// </summary>
/// <remarks>
/// <para>
/// <b>La corrispondenza è esatta, non per somiglianza.</b> La ricerca per nome
/// trova «rossi» dentro «Rossi Mario» e dentro «Crossing», ed è giusto: chi cerca un
/// nome ne ricorda un pezzo. Un interno non si ricorda a pezzi. Con la stessa regola,
/// digitando «40» comparirebbero tutti gli interni che contengono quella coppia di
/// cifre, in qualunque posizione — e peggio ancora, perché sulle cifre la ricerca per
/// nome guarda anche la tastiera telefonica (T9), quindi «40» troverebbe anche i nomi
/// che quelle due cifre possono comporre. Un elenco così non aiuta a comporre, e
/// obbliga a scorrere voci che non c'entrano per scoprire che non c'è quella cercata.
/// </para>
/// <para>
/// <b>Solo gli interni.</b> Un cellulare o un numero d'ufficio uguale alle cifre
/// digitate non è una risposta a «chi ha questo interno»: l'archivio li confronta
/// tutti allo stesso modo, e qui si scartano.
/// </para>
/// <para>
/// <b>Il confronto riparte dalle cifre scritte, non da quelle normalizzate.</b>
/// L'archivio riduce i numeri alle ultime nove cifre per riconoscere lo stesso
/// recapito sotto prefissi diversi — è ciò che serve a identificare chi chiama — ma
/// per un interno sarebbe un'approssimazione: due numeri lunghi che finiscono nello
/// stesso modo risulterebbero uguali. Si usa l'archivio per restringere in fretta, e
/// il confronto esatto lo si rifà qui.
/// </para>
/// </remarks>
public static class ExtensionSearch
{
    /// <summary>
    /// Se il testo del campo di composizione è, per intero, un possibile interno.
    /// </summary>
    /// <param name="text">Testo del campo.</param>
    /// <remarks>
    /// Solo cifre. Un segno più, un asterisco, un cancelletto o una lettera fanno di
    /// quel testo un numero esterno, un codice di servizio o un nome: in nessuno di
    /// questi casi c'è un interno da cercare.
    /// </remarks>
    public static bool IsExtensionQuery(string? text)
    {
        var typed = text?.Trim();

        return !string.IsNullOrEmpty(typed) && typed.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// L'interno della voce uguale alle cifre digitate, oppure <c>null</c> se non ce n'è.
    /// </summary>
    /// <param name="contact">Voce di rubrica.</param>
    /// <param name="digits">Cifre digitate, senza altro.</param>
    public static string? MatchingExtension(Contact contact, string digits)
        => contact.Numbers
            .Where(n => n.Kind == PhoneNumberKind.Extension)
            .Select(n => n.Number)
            .FirstOrDefault(number => DigitsOf(number) == digits);

    /// <summary>
    /// Cerca le voci che hanno esattamente l'interno digitato.
    /// </summary>
    /// <param name="contacts">Archivio dei contatti.</param>
    /// <param name="typed">Testo del campo di composizione.</param>
    /// <param name="limit">Numero massimo di voci da restituire.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>
    /// Le voci che dichiarano quell'interno, nell'ordine dell'archivio (preferiti e
    /// persone prima); elenco vuoto se il testo non è un interno o nessuno lo ha.
    /// </returns>
    /// <remarks>
    /// Più voci sullo stesso interno sono normali: lo stesso apparecchio serve
    /// l'accettazione e la stanza infermieri, l'ambulatorio e l'ufficio. Sono tutte
    /// risposte giuste alla stessa domanda, quindi vengono tutte restituite.
    /// </remarks>
    public static async Task<IReadOnlyList<ExtensionMatch>> FindAsync(
        IContactRepository contacts,
        string typed,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsExtensionQuery(typed)) return [];

        var digits = typed.Trim();

        var candidates = await contacts.ResolveByNumberAsync(digits, cancellationToken)
            .ConfigureAwait(false);

        var found = new List<ExtensionMatch>();

        foreach (var contact in candidates)
        {
            if (MatchingExtension(contact, digits) is not { } extension) continue;

            found.Add(new ExtensionMatch(contact, extension));
            if (found.Count == limit) break;
        }

        return found;
    }

    private static string DigitsOf(string number)
        => new([.. number.Where(char.IsAsciiDigit)]);
}
