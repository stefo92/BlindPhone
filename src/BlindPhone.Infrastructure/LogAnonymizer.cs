using System.Text;
using System.Text.RegularExpressions;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Sostituisce recapiti telefonici e nominativi in un registro diagnostico,
/// lasciando intatto tutto il resto.
/// </summary>
/// <remarks>
/// <para>
/// <b>I registri di un posto operatore sono dati personali.</b> Contengono chi ha
/// chiamato, da quale numero, a che ora e con chi ha parlato: sono dati di traffico
/// telefonico ai sensi del GDPR, prodotti su una postazione di ente pubblico. Il
/// pacchetto per l'assistenza viaggia via posta elettronica, quindi esce dall'ente:
/// deve uscire già anonimo.
/// </para>
/// <para>
/// <b>Non si sostituisce alla cieca ogni sequenza di cifre.</b> Un registro SIP è
/// pieno di numeri che non sono recapiti — porte, identificativi di ramo, CSeq, SSRC,
/// conteggi di pacchetti, millisecondi — e cancellarli renderebbe il registro inutile
/// proprio per la diagnosi a cui serve. Si interviene nelle sole posizioni in cui un
/// numero <i>è</i> un recapito, e una passata finale toglie i recapiti già
/// riconosciuti anche dalle frasi della libreria SIP, che sono in inglese e
/// cambiano fra una versione e l'altra.
/// </para>
/// <para>
/// <b>Le etichette sono stabili e dicono di che tipo di recapito si tratta.</b> Lo
/// stesso numero diventa sempre la stessa etichetta, e interno resta distinto da
/// esterno: senza queste due proprietà un registro non si potrebbe più leggere,
/// perché seguire una chiamata significa riconoscere lo stesso recapito in venti
/// righe diverse e sapere se chi chiama è dentro la struttura o fuori.
/// </para>
/// </remarks>
public sealed partial class LogAnonymizer
{
    /// <summary>Sotto le tre cifre un recapito non identifica nessuno.</summary>
    private const int MinimumDigits = 3;

    /// <summary>Fino a cinque cifre si tratta di un interno.</summary>
    private const int InternalDigits = 5;

    private readonly Dictionary<string, string> _contacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    private int _externalCount;
    private int _internalCount;

    private Regex? _sweep;

    /// <summary>
    /// Etichette che il centralino usa per le linee e non per le persone.
    /// </summary>
    /// <remarks>
    /// Non sono dati personali, e sostituirle toglierebbe contesto senza proteggere
    /// nessuno: sapere che una chiamata è arrivata dalle linee esterne serve alla
    /// diagnosi e non dice nulla su chi stesse telefonando.
    /// </remarks>
    private static readonly string[] GenericLabels =
    [
        "Chiamate esterne",
        "Centralino-Chiamate-Interne",
        "Anonymous",
        "unknown",
        "Restricted",
    ];

    /// <summary>Recapiti distinti incontrati finora.</summary>
    public int ContactCount => _contacts.Count;

    /// <summary>Nominativi distinti incontrati finora.</summary>
    public int NameCount => _names.Count;

    /// <summary>
    /// Anonimizza un registro intero.
    /// </summary>
    /// <param name="lines">Righe del registro.</param>
    /// <returns>Le righe anonimizzate, nello stesso ordine.</returns>
    /// <remarks>
    /// <b>Due passate, e servono entrambe.</b> La prima riconosce i recapiti nelle
    /// posizioni sicure di tutto il file, la seconda li toglie ovunque compaiano. Con
    /// una sola passata un recapito che appare in una frase della libreria
    /// <i>prima</i> di comparire in un URI resterebbe in chiaro su quella riga.
    /// </remarks>
    public IReadOnlyList<string> Anonymize(IReadOnlyList<string> lines)
    {
        var prima = new List<string>(lines.Count);
        foreach (var riga in lines) prima.Add(AnonymizeLine(riga));

        BuildSweep();

        var risultato = new List<string>(prima.Count);
        foreach (var riga in prima) risultato.Add(RemoveResidualContacts(riga));

        return risultato;
    }

    /// <summary>
    /// Anonimizza una riga nelle posizioni in cui un numero è un recapito.
    /// </summary>
    /// <param name="line">Riga del registro.</param>
    /// <remarks>
    /// I controlli rapidi in testa non sono un'ottimizzazione facoltativa: un
    /// registro in modalità diagnostica è fatto per nove decimi di pacchetti RTP e
    /// contatori, dove un recapito non può esserci, e applicare a tutte quelle righe
    /// quattro espressioni regolari costa minuti su un registro da centomila righe —
    /// cioè proprio su quelli che si mandano in assistenza.
    /// </remarks>
    public string AnonymizeLine(string line)
    {
        if (line.Length < MinimumDigits) return line;
        if (!line.Any(char.IsAsciiDigit)) return line;

        var risultato = line;

        // 1. Parte utente di un URI SIP. Copre From, To, Contact, Refer-To e le
        //    righe del programma. Il dominio resta: serve a riconoscere il centralino.
        if (risultato.Contains("sip:", StringComparison.Ordinal)
            || risultato.Contains("sips:", StringComparison.Ordinal))
        {
            risultato = SipUserPart().Replace(risultato, m => LabelFor(m.Groups[1].Value));
        }

        // 2. Nome visualizzato fra virgolette: nei registri porta tanto numeri quanto
        //    nominativi di persone fisiche.
        if (risultato.Contains('"'))
        {
            risultato = QuotedName().Replace(risultato, m => '"' + TranslateDisplayName(m.Groups[1].Value) + '"');
        }

        // 3. Frasi del programma che annunciano un recapito.
        if (AnnouncementPhrases().IsMatch(risultato))
        {
            risultato = AnnouncedContact().Replace(risultato, m => LabelFor(m.Groups[1].Value));
        }

        return risultato;
    }

    private string TranslateDisplayName(string content)
    {
        // Il centralino compone i nomi come "Etichetta:Dettaglio", dove il dettaglio
        // e' tanto un numero quanto il nominativo di una persona.
        var pezzi = content.Split(':');

        for (var i = 0; i < pezzi.Length; i++)
        {
            var pulito = pezzi[i].Trim();

            if (pulito.Length == 0) continue;
            if (GenericLabels.Contains(pulito, StringComparer.OrdinalIgnoreCase)) continue;

            pezzi[i] = OnlyDigitsAndPlus().IsMatch(pulito) ? LabelFor(pulito) : NameLabelFor(pulito);
        }

        return string.Join(':', pezzi);
    }

    /// <summary>Etichetta stabile per un recapito.</summary>
    /// <param name="value">Recapito come compare nel registro.</param>
    private string LabelFor(string value)
    {
        var cifre = new string([.. value.Where(char.IsAsciiDigit)]);
        if (cifre.Length < MinimumDigits) return value;

        if (_contacts.TryGetValue(cifre, out var esistente)) return esistente;

        var etichetta = cifre.Length <= InternalDigits
            ? $"INTERNO-{++_internalCount:D2}"
            : $"ESTERNO-{++_externalCount:D2}";

        _contacts[cifre] = etichetta;
        return etichetta;
    }

    private string NameLabelFor(string value)
    {
        if (_names.TryGetValue(value, out var esistente)) return esistente;

        var etichetta = $"NOMINATIVO-{_names.Count + 1:D2}";
        _names[value] = etichetta;
        return etichetta;
    }

    /// <summary>
    /// Prepara la ricerca dei recapiti residui.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Una sola espressione, non una per numero.</b> Con trecento recapiti e
    /// centomila righe, ripetere una sostituzione per ciascuno significa trenta
    /// milioni di passaggi.
    /// </para>
    /// <para>
    /// I più lunghi per primi: altrimenti un interno di quattro cifre contenuto in un
    /// numero lungo verrebbe sostituito per primo, spezzandolo.
    /// </para>
    /// </remarks>
    private void BuildSweep()
    {
        if (_contacts.Count == 0)
        {
            _sweep = null;
            return;
        }

        var alternanza = string.Join(
            '|',
            _contacts.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));

        // I CONFINI ESCLUDONO LE LETTERE, NON SOLO LE CIFRE.
        //
        // Con il confine sulle sole cifre la sostituzione entrava dentro gli
        // identificativi esadecimali — "tag=d08a-c6d6d01d4712f59c" contiene una
        // sequenza preceduta da una lettera — e dentro i Call-ID, corrompendo campi
        // che alla diagnosi servono interi. Non è una fuga di dati: è il registro
        // rovinato, cioè l'opposto di ciò che questa classe promette.
        //
        // I confini strutturali tengono fuori le porte scritte dopo i due punti
        // (:5061), i parametri dopo l'uguale (rport=52743) e gli indirizzi IP.
        //
        // Restano i numeri scritti in chiaro dentro le frasi, dove la posizione non
        // dice niente: si escludono i casi che compaiono davvero nei registri, cioè il
        // contatore di transazione dopo "CSeq: ", le porte annunciate dalla libreria
        // come "on port 50154" e i CONTATORI DEL PERCORSO AUDIO. Un interno di tre
        // cifre e un conteggio di pacchetti hanno la stessa forma.
        //
        // I contatori non sono un dettaglio: sono la riga con cui si distingue una
        // chiamata muta da una che funziona. Il 12 agosto 2026 la riga di una chiamata
        // riuscita diceva «Inviati INTERNO-247, ricevuti 1639» — il primo numero era
        // stato scambiato per un interno perché quel giorno esisteva davvero un
        // interno 247, e la diagnosi si è dovuta appoggiare all'altro numero. Un
        // registro anonimo che perde i contatori è anonimo e inutile.
        _sweep = new Regex(
            $@"(?<!CSeq: )(?<!port )(?<!porta )(?<!Inviati )(?<!ricevuti )(?<!scartati )(?<!inserito )(?<!buffer )(?<!Pacchetti )(?<![\w:=\.])({alternanza})(?![\w\.])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private string RemoveResidualContacts(string line)
    {
        if (_sweep is null) return line;

        return _sweep.Replace(line, m => _contacts[m.Groups[1].Value]);
    }

    /// <summary>
    /// Corrispondenza fra etichette e recapiti veri.
    /// </summary>
    /// <remarks>
    /// <b>Non finisce mai nel pacchetto per l'assistenza.</b> Insieme al registro
    /// anonimo ricostruisce esattamente i dati che l'anonimizzazione doveva
    /// proteggere: tenuti separati non dicono nulla, messi insieme dicono tutto.
    /// Serve a chi amministra la postazione per risalire a chi era «ESTERNO-03»
    /// quando l'analisi lo nomina.
    /// </remarks>
    public string BuildMap()
    {
        var testo = new StringBuilder();

        testo.AppendLine("# CORRISPONDENZA FRA ETICHETTE E RECAPITI VERI");
        testo.AppendLine("#");
        testo.AppendLine("# QUESTO FILE NON VA INVIATO A NESSUNO.");
        testo.AppendLine("#");
        testo.AppendLine("# Serve a risalire a chi era \"ESTERNO-03\" quando la diagnosi lo nomina.");
        testo.AppendLine("# Insieme al registro anonimo ricostruisce i dati originali.");
        testo.AppendLine();

        foreach (var (numero, etichetta) in _contacts) testo.AppendLine($"{etichetta} = {numero}");
        foreach (var (nome, etichetta) in _names) testo.AppendLine($"{etichetta} = {nome}");

        return testo.ToString();
    }

    [GeneratedRegex(@"(?<=sips?:)(\+?\d[\d\-\.]{1,19})(?=@)")]
    private static partial Regex SipUserPart();

    [GeneratedRegex("\"([^\"]{1,120})\"")]
    private static partial Regex QuotedName();

    [GeneratedRegex(@"Chiamante |con |: da | verso |chiamata a ")]
    private static partial Regex AnnouncementPhrases();

    [GeneratedRegex(@"(?<=Chiamante |Incoming con |Outgoing con |Missed con |: da | verso |chiamata a )(\+?\d[\d\s]{1,19}\d)")]
    private static partial Regex AnnouncedContact();

    [GeneratedRegex(@"^\+?[\d\s\-\.]{3,}$")]
    private static partial Regex OnlyDigitsAndPlus();
}
