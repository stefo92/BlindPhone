using System.Text;

namespace BlindPhone.Contacts;

/// <summary>
/// Un recapito letto da una rubrica scritta a mano da persone, ridotto alla forma
/// che si può comporre.
/// </summary>
/// <param name="Number">
/// Il numero, con il solo più iniziale e le cifre; <c>null</c> se il campo
/// conteneva soltanto un interno.
/// </param>
/// <param name="Extension">
/// L'interno dichiarato nel campo — «int. 4021» in coda al numero, o da solo — se
/// c'era.
/// </param>
/// <remarks>
/// <para>
/// <b>Serve alle sorgenti in cui il numero lo scrive una persona</b>, cioè la
/// directory dell'ente e i contatti di Outlook. Lì lo stesso recapito compare come
/// «+39 0521 703111», «(0521) 703-111», «0521/703111» o «+39 0521 703111 int. 4021»,
/// a seconda di chi l'ha inserito e di quale programma l'ha formattato.
/// </para>
/// <para>
/// <b>La forma pulita non è una questione estetica.</b> Il numero scelto in rubrica
/// arriva al motore telefonico così com'è e finisce dentro l'indirizzo SIP della
/// chiamata: uno spazio o una parentesi lo rendono malformato, e il centralino
/// rifiuta la chiamata. Chi lo sente è l'operatore, con il chiamante in attesa, e
/// niente gli dice che il guasto sta in una parentesi scritta in Outlook anni prima.
/// </para>
/// </remarks>
public sealed record DirectoryNumber(string? Number, string? Extension)
{
    /// <summary>
    /// Le parole con cui un interno viene indicato, in minuscolo.
    /// </summary>
    /// <remarks>
    /// Le forme italiane prima: sono quelle che si trovano nelle directory degli
    /// enti. Le inglesi restano perché Outlook e i gestionali le propongono da sé, e
    /// <c>;ext=</c> è la forma dello standard dei numeri telefonici negli indirizzi
    /// (RFC 3966) che alcune directory scrivono tale e quale. L'ordine conta: le
    /// forme lunghe vanno provate prima di quelle che ne sono l'inizio.
    /// </remarks>
    private static readonly string[] ExtensionMarkers =
        [";ext=", "interno", "int.", "int", "ext.", "ext", "x"];

    /// <summary>
    /// Interpreta il valore di un campo telefonico.
    /// </summary>
    /// <param name="value">Il valore com'è scritto nella sorgente.</param>
    /// <returns>
    /// Il recapito componibile, oppure <c>null</c> se il campo non contiene un numero
    /// che si possa comporre.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Le regole sono quelle delle tabelle dei gestionali</b>, e la ragione è la
    /// stessa: un valore che non è un numero non va offerto come destinazione di un
    /// trasferimento. Una nota infilata nel campo del telefono — «chiedere in
    /// segreteria», due numeri nello stesso campo — finisce fra le note del contatto,
    /// dove resta leggibile e ricercabile senza essere componibile.
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   Spazi, trattini, punti, barre e parentesi sono separatori visivi e si tolgono.
    ///   </description></item>
    ///   <item><description>
    ///   Il più vale solo in testa: è ciò che distingue un numero internazionale.
    ///   </description></item>
    ///   <item><description>
    ///   «(0)» dopo un prefisso internazionale è la convenzione con cui si scrive lo
    ///   zero da non comporre dall'estero, e si toglie — tranne che per l'Italia e San
    ///   Marino, dove lo zero fa parte del numero e toglierlo porterebbe da un'altra
    ///   parte.
    ///   </description></item>
    ///   <item><description>
    ///   Una lettera rimasta dopo aver tolto l'interno, una virgola, due spazi di fila
    ///   o una barra staccata dalle cifre — «0521 703111 / 703112» — dicono che nel
    ///   campo ci sono due numeri. Fonderli ne comporrebbe un terzo che non esiste.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static DirectoryNumber? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var testo = value.Trim();

        // Alcune directory scrivono il numero come indirizzo dello standard dei
        // numeri telefonici: il prefisso dello schema non si compone.
        if (testo.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) testo = testo[4..].Trim();

        // «Int. 4021» da solo, nel campo del telefono, e' il modo in cui molte
        // directory di ente scrivono chi ha soltanto l'interno. Rifiutarlo per via
        // delle lettere toglierebbe a quelle persone il loro unico recapito.
        if (LeadingExtension(testo) is { } soloInterno)
        {
            return Clean(soloInterno) is { } interno && !interno.StartsWith('+')
                ? new DirectoryNumber(null, interno)
                : null;
        }

        var (principale, accodato) = SplitExtension(testo);

        var numero = Clean(principale);
        if (numero is null) return null;

        // Un interno che non e' fatto di cifre non e' un interno: meglio perdere
        // l'interno che perdere il numero a cui e' attaccato.
        var internoPulito = accodato is null ? null : Clean(accodato);
        if (internoPulito is not null && internoPulito.StartsWith('+')) internoPulito = null;

        return new DirectoryNumber(numero, internoPulito);
    }

    /// <summary>
    /// Ha senso dire che un valore sta nelle note e non fra i recapiti.
    /// </summary>
    /// <param name="value">Il valore com'è scritto nella sorgente.</param>
    /// <remarks>
    /// Un campo vuoto non è un valore scartato: tacerlo evita note fatte di niente.
    /// </remarks>
    public static bool IsDiscarded(string? value)
        => !string.IsNullOrWhiteSpace(value) && Parse(value) is null;

    /// <summary>Il resto del testo, se comincia con la parola dell'interno.</summary>
    private static string? LeadingExtension(string testo)
    {
        var minuscolo = testo.ToLowerInvariant();

        foreach (var parola in ExtensionMarkers)
        {
            if (!minuscolo.StartsWith(parola, StringComparison.Ordinal)) continue;
            if (!EndsWord(minuscolo, parola, parola.Length)) continue;

            return testo[parola.Length..].Trim().TrimStart(':', '.').Trim();
        }

        return null;
    }

    /// <summary>
    /// Separa il numero dall'interno accodato.
    /// </summary>
    /// <remarks>
    /// Si cerca la parola dell'interno solo <b>dopo</b> la prima cifra e solo come
    /// parola intera: la «x» di «fax» non è un interno, e nemmeno l'«int» di
    /// «interessato».
    /// </remarks>
    private static (string Principale, string? Interno) SplitExtension(string testo)
    {
        var minuscolo = testo.ToLowerInvariant();
        var primaCifra = minuscolo.IndexOfAny("0123456789".ToCharArray());
        if (primaCifra < 0) return (testo, null);

        foreach (var parola in ExtensionMarkers)
        {
            var posizione = minuscolo.IndexOf(parola, primaCifra, StringComparison.Ordinal);

            while (posizione >= 0)
            {
                var prima = minuscolo[posizione - 1];
                var fine = posizione + parola.Length;

                var inizioParola = parola.StartsWith(';') || !char.IsLetter(prima);

                if (inizioParola && EndsWord(minuscolo, parola, fine))
                {
                    var resto = testo[fine..].Trim().TrimStart(':', '.').Trim();
                    return (testo[..posizione].Trim(), resto.Length == 0 ? null : resto);
                }

                posizione = minuscolo.IndexOf(parola, posizione + 1, StringComparison.Ordinal);
            }
        }

        return (testo, null);
    }

    /// <summary>La parola trovata finisce lì e non continua in un'altra.</summary>
    private static bool EndsWord(string testo, string parola, int fine)
        => parola.EndsWith('.') || parola.EndsWith('=') || fine >= testo.Length || !char.IsLetter(testo[fine]);

    /// <summary>Toglie i separatori visivi; <c>null</c> se resta qualcosa che non si compone.</summary>
    private static string? Clean(string testo)
    {
        var lavoro = testo.Trim();
        if (lavoro.Length == 0) return null;

        // Due spazi di fila o una barra staccata dalle cifre separano due numeri,
        // non le parti dello stesso: «2876   2877», «0521 703111 / 703112».
        if (lavoro.Contains("  ", StringComparison.Ordinal)
            || lavoro.Contains(" /", StringComparison.Ordinal)
            || lavoro.Contains("/ ", StringComparison.Ordinal))
        {
            return null;
        }

        var internazionale = lavoro.StartsWith('+');
        if (internazionale) lavoro = lavoro[1..];

        // «(0)» va trattato prima di togliere le parentesi, altrimenti diventa uno
        // zero qualsiasi e non si distingue piu' da quello che fa parte del numero.
        var trunk = lavoro.IndexOf("(0)", StringComparison.Ordinal);
        if (internazionale && trunk >= 0)
        {
            var prefisso = new string([.. lavoro[..trunk].Where(char.IsAsciiDigit)]);
            var tieniZero = prefisso is "39" or "378";

            lavoro = lavoro[..trunk] + (tieniZero ? "0" : string.Empty) + lavoro[(trunk + 3)..];
        }

        var cifre = new StringBuilder(lavoro.Length);

        foreach (var c in lavoro)
        {
            if (char.IsAsciiDigit(c) || c is '*' or '#')
            {
                cifre.Append(c);
                continue;
            }

            // Separatori visivi: spazi di ogni tipo, trattini, punti, barre e
            // parentesi. Il trattino lungo e lo spazio indivisibile arrivano dai
            // documenti di Word e dalle firme di posta copiate in Outlook.
            if (char.IsWhiteSpace(c) || c is '-' or '–' or '‑' or '.' or '/' or '(' or ')') continue;

            // Qualunque altra cosa — una lettera, una virgola fra due numeri, un
            // secondo piu' — dice che il campo non contiene un numero solo.
            return null;
        }

        if (!cifre.ToString().Any(char.IsAsciiDigit)) return null;

        return internazionale ? "+" + cifre : cifre.ToString();
    }
}
