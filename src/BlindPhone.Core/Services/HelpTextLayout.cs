using System.Text;

namespace BlindPhone.Core.Services;

/// <summary>
/// Impagina un testo in righe corte, adatte all'ascolto e al braille.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché l'impaginazione avviene qui e non nel testo sorgente.</b> Le frasi della
/// guida contengono i nomi delle combinazioni configurate, che hanno lunghezza
/// variabile: "F2" occupa due caratteri, "Control Maiusc Pagina giu'" ne occupa
/// ventisei. Un testo spezzato a mano nel codice sorgente risulta ordinato con i
/// valori predefiniti e sfilacciato con qualunque altra configurazione, e produce
/// righe che finiscono a metà frase.
/// </para>
/// <para>
/// Per chi legge con la sintesi la riga è l'unità di lettura: la freccia giù ne legge
/// una per volta. Una riga che finisce a metà concetto costringe a premere ancora per
/// capire, e a ricostruire la frase a memoria. Per chi legge in braille la larghezza è
/// un vincolo fisico: un display comune ha quaranta celle, e il testo che le supera va
/// fatto scorrere.
/// </para>
/// <para>
/// Gli elenchi puntati mantengono il trattino sulla prima riga e rientrano di due
/// spazi sulle successive: è la convenzione che rende riconoscibile all'ascolto dove
/// finisce una voce e dove comincia la prossima.
/// </para>
/// </remarks>
public static class HelpTextLayout
{
    /// <summary>Larghezza predefinita, un compromesso fra display braille e schermo.</summary>
    public const int DefaultWidth = 72;

    /// <summary>
    /// Impagina un testo, rispettando righe vuote ed elenchi.
    /// </summary>
    /// <param name="text">Testo con un paragrafo per riga.</param>
    /// <param name="width">Larghezza massima delle righe prodotte.</param>
    public static string Wrap(string text, int width = DefaultWidth)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var result = new StringBuilder();
        var paragraphs = text.Replace("\r\n", "\n").Split('\n');

        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.TrimEnd();

            if (trimmed.Length == 0)
            {
                result.AppendLine();
                continue;
            }

            foreach (var line in WrapParagraph(trimmed, width))
            {
                result.AppendLine(line);
            }
        }

        return result.ToString().TrimEnd() + Environment.NewLine;
    }

    private static IEnumerable<string> WrapParagraph(string paragraph, int width)
    {
        var indent = ContinuationIndent(paragraph);
        var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var line = new StringBuilder();

        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";

            if (candidate.Length <= width)
            {
                line.Clear().Append(candidate);
                continue;
            }

            // Una parola più lunga della riga (un percorso, un indirizzo) non si
            // spezza: si lascia sporgere. Spezzarla renderebbe illeggibile proprio
            // l'informazione che va trascritta con esattezza.
            if (line.Length > 0) yield return line.ToString();

            line.Clear().Append(indent).Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }

    /// <summary>
    /// Rientro delle righe successive alla prima, per le voci di elenco.
    /// </summary>
    private static string ContinuationIndent(string paragraph)
    {
        if (paragraph.StartsWith("- ", StringComparison.Ordinal)) return "  ";

        // Elenchi numerati: "1. ", "2. " e così via.
        if (paragraph.Length > 2 && char.IsAsciiDigit(paragraph[0]) && paragraph[1] == '.' && paragraph[2] == ' ')
            return "   ";

        return string.Empty;
    }
}
