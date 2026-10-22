using System.Text;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Speech;

/// <summary>Come leggere un numero telefonico.</summary>
public enum DigitGrouping
{
    /// <summary>Cifra per cifra: "zero due uno due tre".</summary>
    Single,

    /// <summary>A gruppi di due: "zero due, uno due, tre quattro".</summary>
    Pairs,

    /// <summary>A gruppi di tre: "zero due uno, due tre quattro".</summary>
    Triples,

    /// <summary>Come numero intero, lasciando fare alla sintesi.</summary>
    Natural,
}

/// <summary>
/// Formattazione dei dati per la sintesi vocale italiana.
/// </summary>
/// <remarks>
/// <para>
/// Il problema che questa classe risolve non è estetico. Una sintesi vocale che
/// legge "0212345" come "duecentododicimilatrecentoquarantacinque" costringe
/// l'operatore a farsi ripetere il numero, e su un centralino il numero va
/// trascritto o ricomposto al primo ascolto. La lettura cifra per cifra, con pause
/// di raggruppamento, è ciò che rende un numero effettivamente utilizzabile.
/// </para>
/// <para>
/// Le pause sono ottenute con virgole e spazi invece che con marcatori SSML,
/// perché il testo passa attraverso screen reader di terze parti che non
/// interpretano SSML e lo leggerebbero come testo.
/// </para>
/// </remarks>
public static class SpeechFormatter
{
    private static readonly string[] DigitWords =
        ["zero", "uno", "due", "tre", "quattro", "cinque", "sei", "sette", "otto", "nove"];

    /// <summary>
    /// Rende un numero telefonico pronunciabile.
    /// </summary>
    /// <param name="number">Numero da leggere.</param>
    /// <param name="grouping">Modalità di raggruppamento.</param>
    /// <returns>Testo pronunciabile, oppure stringa vuota se il numero è vuoto.</returns>
    /// <remarks>
    /// Gli interni brevi (fino a 4 cifre) vengono sempre letti cifra per cifra a
    /// prescindere dall'impostazione: "quattro zero due uno" è più chiaro e più
    /// veloce di "quattromilaventuno", e soprattutto è come l'operatore lo dirà al
    /// telefono.
    /// </remarks>
    public static string Digits(string? number, DigitGrouping grouping = DigitGrouping.Single)
    {
        if (string.IsNullOrWhiteSpace(number)) return string.Empty;

        var digits = new StringBuilder();
        foreach (var c in number)
        {
            if (char.IsDigit(c)) digits.Append(c);
            else if (c is '+' or '*' or '#') digits.Append(c);
        }

        if (digits.Length == 0) return number;

        var clean = digits.ToString();

        var effective = clean.Length <= 4 ? DigitGrouping.Single : grouping;
        if (effective == DigitGrouping.Natural) return clean;

        var groupSize = effective switch
        {
            DigitGrouping.Pairs => 2,
            DigitGrouping.Triples => 3,
            _ => 1,
        };

        var result = new StringBuilder();
        for (var i = 0; i < clean.Length; i++)
        {
            if (i > 0)
            {
                // La virgola introduce la pausa di gruppo, lo spazio separa le cifre
                // dentro al gruppo.
                result.Append(i % groupSize == 0 ? ", " : " ");
            }

            result.Append(Spell(clean[i]));
        }

        return result.ToString();
    }

    /// <summary>
    /// Rende un numero leggibile cifra per cifra da uno screen reader esterno.
    /// </summary>
    /// <param name="number">Numero da rendere.</param>
    /// <returns>Per esempio "4 0 2 1", oppure stringa vuota se il numero è vuoto.</returns>
    /// <remarks>
    /// <para>
    /// Serve dove il testo non passa dalla voce interna ma finisce nella proprietà
    /// <c>Name</c> di UI Automation, cioè in ciò che lo screen reader legge da sé:
    /// le celle delle tabelle. Lì <see cref="Digits"/> non va bene, e per due
    /// motivi. La riga braille mostrerebbe "quattro zero due uno" al posto di
    /// quattro caratteri, e su un display da quaranta celle un solo interno
    /// occuperebbe mezza riga. E la lettura sarebbe quattro volte più lunga, su un
    /// campo che l'operatore attraversa decine di volte per trovare un contatto.
    /// </para>
    /// <para>
    /// Lo spazio fra le cifre basta: gli screen reader trattano ogni gruppo
    /// separato da spazio come un numero a sé, quindi "4 0 2 1" viene pronunciato
    /// cifra per cifra e in braille resta il numero che è.
    /// </para>
    /// </remarks>
    public static string SpacedDigits(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return string.Empty;

        var result = new StringBuilder();

        foreach (var c in number)
        {
            if (!char.IsDigit(c) && c is not ('+' or '*' or '#')) continue;

            if (result.Length > 0) result.Append(' ');
            result.Append(c);
        }

        // Un numero fatto di soli separatori non esiste, ma restituire una stringa
        // vuota produrrebbe una cella senza nome accessibile, cioè una cella che lo
        // screen reader legge come silenzio.
        return result.Length == 0 ? number : result.ToString();
    }

    private static string Spell(char c) => c switch
    {
        >= '0' and <= '9' => DigitWords[c - '0'],
        '+' => "più",
        '*' => "asterisco",
        '#' => "cancelletto",
        _ => c.ToString(),
    };

    /// <summary>
    /// Rende una durata in forma parlata italiana.
    /// </summary>
    /// <param name="duration">Durata da leggere.</param>
    /// <returns>Per esempio "1 minuto e 24 secondi", "45 secondi", "2 ore e 5 minuti".</returns>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        var totalSeconds = (int)duration.TotalSeconds;
        if (totalSeconds < 60)
        {
            return totalSeconds == 1 ? "1 secondo" : $"{totalSeconds} secondi";
        }

        if (totalSeconds < 3600)
        {
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            var minutePart = minutes == 1 ? "1 minuto" : $"{minutes} minuti";
            if (seconds == 0) return minutePart;
            return $"{minutePart} e {seconds}";
        }

        var hours = totalSeconds / 3600;
        var remainingMinutes = (totalSeconds % 3600) / 60;
        var hourPart = hours == 1 ? "1 ora" : $"{hours} ore";
        if (remainingMinutes == 0) return hourPart;
        return $"{hourPart} e {remainingMinutes} minuti";
    }

    /// <summary>
    /// Concorda un sostantivo con il numero che lo precede.
    /// </summary>
    /// <param name="count">Quantità.</param>
    /// <param name="singular">Forma singolare, per esempio "chiamata".</param>
    /// <param name="plural">Forma plurale, per esempio "chiamate".</param>
    /// <param name="zeroText">Testo per lo zero; se assente si usa il plurale.</param>
    /// <returns>Per esempio "3 chiamate", "1 chiamata", "nessuna chiamata".</returns>
    public static string Count(int count, string singular, string plural, string? zeroText = null)
    {
        if (count == 0 && zeroText is not null) return zeroText;
        return count == 1 ? $"1 {singular}" : $"{count} {plural}";
    }

    /// <summary>
    /// Compone l'identificazione parlata di una controparte: nome se disponibile,
    /// altrimenti numero letto cifra per cifra.
    /// </summary>
    /// <param name="displayName">Nome, eventualmente nella resa fonetica.</param>
    /// <param name="number">Numero.</param>
    /// <param name="grouping">Raggruppamento da usare per il numero.</param>
    /// <param name="includeNumberWithName">
    /// Se <c>true</c> e il nome è noto, legge anche il numero dopo il nome.
    /// </param>
    /// <returns>Testo pronunciabile, oppure "sconosciuto" se non c'è nulla da dire.</returns>
    /// <remarks>
    /// Un nome che è soltanto il numero non vale come nome — la regola è quella di
    /// <see cref="CallParty.MeaningfulName"/> — e il numero si legge cifra per cifra.
    /// Può arrivare così, per esempio, il nome di un chiamante in coda: preso per nome,
    /// la sintesi lo leggeva come numero intero.
    /// </remarks>
    public static string Party(
        string? displayName,
        string? number,
        DigitGrouping grouping = DigitGrouping.Single,
        bool includeNumberWithName = false)
    {
        var name = CallParty.MeaningfulNameOf(displayName, number);
        var hasNumber = !string.IsNullOrWhiteSpace(number);

        if (name is not null && hasNumber && includeNumberWithName)
            return $"{name}, {Digits(number, grouping)}";

        if (name is not null) return name;
        if (hasNumber) return Digits(number, grouping);

        return "sconosciuto";
    }
}
