namespace BlindPhone.Core.Domain;

/// <summary>Un collega da sorvegliare: il suo interno e il nome con cui compare.</summary>
/// <param name="Number">Interno, nella forma digitata dall'operatore.</param>
/// <param name="Name">
/// Nome scritto accanto all'interno nelle impostazioni, oppure <c>null</c> se va
/// cercato in rubrica.
/// </param>
public sealed record ColleagueEntry(string Number, string? Name)
{
    /// <summary>Interno ridotto alle sole cifre, per i confronti.</summary>
    public string Key => ColleagueRoster.Normalize(Number);
}

/// <summary>
/// L'elenco dei colleghi che il centralinista vuole tenere d'occhio.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché un testo e non una tabella con i pulsanti.</b> L'elenco si compila una
/// volta, all'installazione o quando cambia il personale, e poi non si tocca per mesi.
/// Una tabella con «aggiungi» e «rimuovi» costa a chi non vede un giro di tabulazioni
/// per riga; una casella di testo si compila, si rilegge e si corregge con i gesti che
/// l'operatore già conosce, e si incolla da un foglio che l'ente ha già.
/// </para>
/// <para>
/// <b>Il nome è facoltativo.</b> Scritto accanto all'interno vince su tutto, perché è
/// quello che l'operatore ha deciso di sentirsi dire. Se manca si cerca in rubrica. Se
/// non c'è nemmeno lì resta il solo numero, che è comunque l'unico dato di cui si
/// risponda.
/// </para>
/// <para>
/// Il formato accetta le tre forme che una persona scrive spontaneamente: solo il
/// numero, il numero e il nome separati da spazio, oppure separati da un segno
/// (<c>4021 - Mario Rossi</c>, <c>4021, Mario Rossi</c>, <c>4021: Mario Rossi</c>).
/// Le righe vuote e quelle che cominciano con <c>#</c> si saltano, così si può
/// commentare l'elenco o metterne una da parte senza cancellarla.
/// </para>
/// </remarks>
public static class ColleagueRoster
{
    /// <summary>
    /// Quanti interni al massimo si possono sorvegliare.
    /// </summary>
    /// <remarks>
    /// Ogni interno costa un sondaggio per ciclo. Cinquanta colleghi con un ciclo da un
    /// minuto sono meno di un messaggio al secondo, cioè meno di quanto il centralino
    /// mandi a noi. Oltre, non si sta più guardando una squadra: si sta interrogando
    /// l'organigramma, e conviene collegare l'API del centralino.
    /// </remarks>
    public const int MaxEntries = 50;

    /// <summary>Legge l'elenco scritto dall'operatore.</summary>
    /// <param name="text">Contenuto della casella, una riga per collega.</param>
    /// <returns>Le voci valide, senza duplicati, nell'ordine in cui sono scritte.</returns>
    /// <remarks>
    /// Le righe che non contengono cifre si scartano in silenzio: sono intestazioni,
    /// note, o resti di un incollaggio. Segnalarle costringerebbe a correggere un
    /// elenco che per il resto funziona.
    /// </remarks>
    public static IReadOnlyList<ColleagueEntry> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var visti = new HashSet<string>(StringComparer.Ordinal);
        var voci = new List<ColleagueEntry>();

        foreach (var riga in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pulita = riga.Trim();

            if (pulita.Length == 0 || pulita[0] == '#') continue;
            if (ParseLine(pulita) is not { } voce) continue;
            if (!visti.Add(voce.Key)) continue;

            voci.Add(voce);

            if (voci.Count >= MaxEntries) break;
        }

        return voci;
    }

    /// <summary>Riscrive l'elenco nella forma che si salva nelle impostazioni.</summary>
    /// <param name="entries">Voci da scrivere.</param>
    public static string Format(IEnumerable<ColleagueEntry> entries)
        => string.Join(
            Environment.NewLine,
            entries.Select(e => string.IsNullOrWhiteSpace(e.Name) ? e.Number : $"{e.Number} {e.Name}"));

    /// <summary>
    /// Riduce un interno alla forma con cui viene confrontato.
    /// </summary>
    /// <param name="number">Numero nella forma in cui è arrivato.</param>
    /// <remarks>
    /// Solo cifre, come fa il resto del prodotto: il centralino può riportare l'interno
    /// con spazi o punteggiatura e la rubrica può averlo memorizzato in un'altra forma
    /// ancora. Senza questa riduzione lo stato arriverebbe e non verrebbe attribuito a
    /// nessuno.
    /// </remarks>
    public static string Normalize(string? number)
        => number is null ? string.Empty : new string([.. number.Where(char.IsAsciiDigit)]);

    /// <summary>Legge una riga sola, o restituisce <c>null</c> se non ne esce niente.</summary>
    /// <remarks>
    /// <b>Dove finisce il numero e comincia il nome</b> è l'unica decisione difficile di
    /// questa classe. Prendere il primo gruppo di cifre e fermarsi sarebbe sbagliato:
    /// «40 21 Mario R.» diventerebbe l'interno 40, cioè un interno diverso, sorvegliato
    /// al posto di quello voluto e senza che niente lo segnali. Uno spazio fa parte del
    /// numero <b>solo quando dopo di lui arriva un'altra cifra</b>; qualunque altro segno
    /// chiude il numero e apre il nome.
    /// </remarks>
    private static ColleagueEntry? ParseLine(string line)
    {
        var cifre = new System.Text.StringBuilder();
        var i = 0;

        while (i < line.Length)
        {
            if (char.IsAsciiDigit(line[i]))
            {
                cifre.Append(line[i]);
                i++;
                continue;
            }

            // Uno spazio in mezzo alle cifre le tiene insieme: "40 21" e' un interno
            // solo. Uno spazio prima di una lettera invece chiude il numero.
            if (line[i] == ' ' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1]))
            {
                i++;
                continue;
            }

            break;
        }

        if (cifre.Length == 0) return null;

        var resto = line[i..].TrimStart(' ', '\t', '-', ',', ':', ';', '\u2013', '\u2014');
        var nome = resto.Trim();

        return new ColleagueEntry(cifre.ToString(), nome.Length > 0 ? nome : null);
    }
}
