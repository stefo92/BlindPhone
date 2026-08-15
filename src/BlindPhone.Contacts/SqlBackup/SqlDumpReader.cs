using System.Text;

namespace BlindPhone.Contacts.SqlBackup;

/// <summary>
/// Legge un dump testuale di istruzioni SQL, estraendone tabelle e righe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nessuna istruzione viene eseguita: vengono lette.</b> Il file è
/// un'esportazione che arriva da un gestionale altrui, e passarlo a <c>mysql</c> o a
/// <c>sqlcmd</c> significherebbe eseguire tutto ciò che contiene con i privilegi di
/// chi lo lancia. Qui si riconoscono due sole forme — <c>CREATE TABLE</c> per sapere
/// come si chiamano le colonne e <c>INSERT</c> per prendere i valori — e ogni altra
/// istruzione viene scartata senza guardarla.
/// </para>
/// <para>
/// <b>Un dialetto solo non basterebbe.</b> MySQL virgoletta gli identificativi con
/// gli apici inversi, PostgreSQL con le virgolette doppie, SQL Server con le
/// parentesi quadre: sono tre modi di scrivere la stessa cosa e chi consegna il file
/// non sa quale ci aspettiamo. Si accettano tutti e tre.
/// </para>
/// <para>
/// <b>Il file si attraversa in flusso, mai tutto in memoria.</b> Il backup di una
/// rubrica di ente sta sulle decine o centinaia di megabyte, e caricarlo intero per
/// leggerne una tabella farebbe esaurire la memoria proprio sulle postazioni più
/// modeste.
/// </para>
/// </remarks>
public sealed class SqlDumpReader : ISqlBackupReader
{
    private readonly string _path;
    private readonly Dictionary<string, List<string>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _rowCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Analizza il dump.</summary>
    /// <param name="path">Percorso del file.</param>
    public SqlDumpReader(string path)
    {
        _path = path;

        foreach (var statement in ReadStatements(path))
        {
            if (TryReadCreateTable(statement, out var tabella, out var colonne))
            {
                _columns[tabella] = colonne;
                _rowCounts.TryAdd(tabella, 0);
                continue;
            }

            if (!TryReadInsertHeader(statement, out var destinazione, out var colonneInsert)) continue;

            // Un dump di soli dati non porta le CREATE TABLE: in quel caso i nomi
            // delle colonne sono quelli dichiarati dall'INSERT, che è l'unica fonte
            // disponibile.
            if (!_columns.ContainsKey(destinazione) && colonneInsert.Count > 0)
                _columns[destinazione] = colonneInsert;

            var righe = CountTuples(statement);
            _rowCounts[destinazione] = _rowCounts.GetValueOrDefault(destinazione) + righe;
        }

        Tables = [.. _columns
            .Select(kv => new SqlTableInfo(kv.Key, kv.Value, _rowCounts.GetValueOrDefault(kv.Key)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <inheritdoc />
    public string FormatDescription => "Esportazione SQL in formato testo";

    /// <inheritdoc />
    public IReadOnlyList<SqlTableInfo> Tables { get; }

    /// <inheritdoc />
    public IEnumerable<IReadOnlyDictionary<string, string?>> ReadRows(
        string table,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!_columns.TryGetValue(table, out var colonneTabella)) yield break;

        var lette = 0;

        foreach (var statement in ReadStatements(_path))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryReadInsertHeader(statement, out var destinazione, out var colonneInsert)) continue;
            if (!string.Equals(destinazione, table, StringComparison.OrdinalIgnoreCase)) continue;

            // Le colonne dell'INSERT hanno la precedenza su quelle della CREATE
            // TABLE: un dump può inserire solo un sottoinsieme delle colonne, e
            // usare l'elenco sbagliato sposterebbe tutti i valori di una posizione.
            var intestazioni = colonneInsert.Count > 0 ? colonneInsert : colonneTabella;

            foreach (var valori in ReadTuples(statement))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var riga = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < intestazioni.Count && i < valori.Count; i++)
                {
                    riga[intestazioni[i]] = valori[i];
                }

                yield return riga;

                if (limit is { } n && ++lette >= n) yield break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Scansione del file
    // -----------------------------------------------------------------------

    /// <summary>
    /// Scorre il file restituendo una istruzione alla volta.
    /// </summary>
    /// <remarks>
    /// Il punto e virgola separa le istruzioni <b>solo quando non è dentro qualcosa</b>:
    /// dentro una stringa, dentro un identificativo virgolettato o dentro un commento
    /// è un carattere come un altro. Ignorarlo produrrebbe istruzioni tagliate a metà
    /// ogni volta che un indirizzo contiene un punto e virgola.
    /// </remarks>
    private static IEnumerable<string> ReadStatements(string path)
    {
        using var lettore = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var corrente = new StringBuilder();
        char? delimitatore = null;   // apice, virgoletta, apice inverso o quadra aperta
        var commentoDiRiga = false;
        var commentoABlocco = false;
        var precedente = '\0';

        int letto;
        while ((letto = lettore.Read()) >= 0)
        {
            var c = (char)letto;

            if (commentoDiRiga)
            {
                if (c is '\n') commentoDiRiga = false;
                precedente = c;
                continue;
            }

            if (commentoABlocco)
            {
                if (precedente == '*' && c == '/') commentoABlocco = false;
                precedente = c;
                continue;
            }

            if (delimitatore is { } chiusura)
            {
                corrente.Append(c);

                // La barra rovesciata protegge il carattere successivo nei dump
                // MySQL. Non vale dentro gli identificativi, ma lì non compare.
                if (c == '\\' && chiusura == '\'')
                {
                    if (lettore.Read() is var successivo && successivo >= 0) corrente.Append((char)successivo);
                    precedente = '\0';
                    continue;
                }

                if (c == chiusura) delimitatore = null;

                precedente = c;
                continue;
            }

            switch (c)
            {
                case '-' when precedente == '-':
                    corrente.Length = Math.Max(0, corrente.Length - 1);
                    commentoDiRiga = true;
                    precedente = c;
                    continue;

                case '#':
                    commentoDiRiga = true;
                    precedente = c;
                    continue;

                case '*' when precedente == '/':
                    corrente.Length = Math.Max(0, corrente.Length - 1);
                    commentoABlocco = true;
                    precedente = c;
                    continue;

                case '\'' or '"' or '`':
                    delimitatore = c;
                    break;

                case '[':
                    delimitatore = ']';
                    break;

                case ';':
                    {
                        var istruzione = corrente.ToString().Trim();
                        corrente.Clear();
                        precedente = c;

                        if (istruzione.Length > 0) yield return istruzione;
                        continue;
                    }
            }

            corrente.Append(c);
            precedente = c;
        }

        var ultima = corrente.ToString().Trim();
        if (ultima.Length > 0) yield return ultima;
    }

    // -----------------------------------------------------------------------
    // Riconoscimento delle istruzioni
    // -----------------------------------------------------------------------

    private static bool TryReadCreateTable(string statement, out string table, out List<string> columns)
    {
        table = string.Empty;
        columns = [];

        if (!StartsWithWords(statement, "CREATE", "TABLE")) return false;

        var apertura = statement.IndexOf('(');
        if (apertura < 0) return false;

        var intestazione = statement[..apertura];
        table = LastIdentifier(intestazione);
        if (table.Length == 0) return false;

        var corpo = EstraiParentesi(statement, apertura);

        foreach (var definizione in SplitTopLevel(corpo))
        {
            var nome = FirstIdentifier(definizione);
            if (nome.Length == 0) continue;

            // Le righe che dichiarano vincoli e indici non sono colonne, e
            // prenderle produrrebbe voci fantasma nell'elenco da mappare.
            if (IsConstraintKeyword(nome)) continue;

            columns.Add(nome);
        }

        return columns.Count > 0;
    }

    private static bool TryReadInsertHeader(string statement, out string table, out List<string> columns)
    {
        table = string.Empty;
        columns = [];

        if (!StartsWithWords(statement, "INSERT")) return false;

        var values = IndexOfWord(statement, "VALUES");
        if (values < 0) return false;

        var intestazione = statement[..values];

        // L'elenco delle colonne, se c'e', sta fra l'ultima parentesi aperta prima
        // di VALUES e la sua chiusura.
        var apertura = intestazione.LastIndexOf('(');

        if (apertura >= 0)
        {
            foreach (var colonna in SplitTopLevel(EstraiParentesi(intestazione, apertura)))
            {
                var nome = FirstIdentifier(colonna);
                if (nome.Length > 0) columns.Add(nome);
            }

            intestazione = intestazione[..apertura];
        }

        table = LastIdentifier(intestazione);
        return table.Length > 0;
    }

    private static int CountTuples(string statement) => ReadTuples(statement).Count();

    /// <summary>Legge le ennuple di valori che seguono <c>VALUES</c>.</summary>
    private static IEnumerable<List<string?>> ReadTuples(string statement)
    {
        var posizione = IndexOfWord(statement, "VALUES");
        if (posizione < 0) yield break;

        posizione += "VALUES".Length;

        while (posizione < statement.Length)
        {
            var apertura = statement.IndexOf('(', posizione);
            if (apertura < 0) yield break;

            var corpo = EstraiParentesi(statement, apertura);

            yield return [.. SplitTopLevel(corpo).Select(ParseValue)];

            posizione = apertura + corpo.Length + 2;
        }
    }

    private static string? ParseValue(string raw)
    {
        var valore = raw.Trim();

        if (valore.Length == 0) return null;
        if (string.Equals(valore, "NULL", StringComparison.OrdinalIgnoreCase)) return null;

        if (valore[0] == '\'' && valore.Length >= 2 && valore[^1] == '\'')
        {
            var interno = valore[1..^1];
            var risultato = new StringBuilder(interno.Length);

            for (var i = 0; i < interno.Length; i++)
            {
                var c = interno[i];

                // Due apici di fila sono un apice solo: e' la forma standard.
                if (c == '\'' && i + 1 < interno.Length && interno[i + 1] == '\'')
                {
                    risultato.Append('\'');
                    i++;
                    continue;
                }

                // La barra rovesciata dei dump MySQL protegge il carattere
                // successivo, e le sequenze note vanno riportate al carattere vero.
                if (c == '\\' && i + 1 < interno.Length)
                {
                    var successivo = interno[++i];

                    risultato.Append(successivo switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '0' => '\0',
                        _ => successivo,
                    });

                    continue;
                }

                risultato.Append(c);
            }

            return risultato.ToString();
        }

        return valore;
    }

    // -----------------------------------------------------------------------
    // Ausiliari di analisi lessicale
    // -----------------------------------------------------------------------

    private static readonly string[] ConstraintKeywords =
    [
        "PRIMARY", "KEY", "UNIQUE", "CONSTRAINT", "INDEX", "FOREIGN", "CHECK", "FULLTEXT", "SPATIAL",
    ];

    private static bool IsConstraintKeyword(string word)
        => ConstraintKeywords.Contains(word, StringComparer.OrdinalIgnoreCase);

    private static bool StartsWithWords(string statement, params string[] words)
    {
        var posizione = 0;

        foreach (var parola in words)
        {
            while (posizione < statement.Length && char.IsWhiteSpace(statement[posizione])) posizione++;

            if (posizione + parola.Length > statement.Length) return false;
            if (!statement.AsSpan(posizione, parola.Length).Equals(parola, StringComparison.OrdinalIgnoreCase))
                return false;

            posizione += parola.Length;

            // Dopo la parola ci deve essere un separatore, altrimenti "INSERTED"
            // verrebbe scambiato per "INSERT".
            if (posizione < statement.Length && (char.IsLetterOrDigit(statement[posizione]) || statement[posizione] == '_'))
                return false;
        }

        return true;
    }

    /// <summary>Trova una parola intera, fuori da stringhe e identificativi.</summary>
    private static int IndexOfWord(string text, string word)
    {
        for (var i = 0; i + word.Length <= text.Length; i++)
        {
            var c = text[i];

            if (c is '\'' or '"' or '`' or '[')
            {
                var chiusura = c == '[' ? ']' : c;
                i++;

                while (i < text.Length && text[i] != chiusura)
                {
                    if (text[i] == '\\' && chiusura == '\'') i++;
                    i++;
                }

                continue;
            }

            if (!text.AsSpan(i, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase)) continue;

            var primaLibera = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
            var dopo = i + word.Length;
            var dopoLibera = dopo >= text.Length || !(char.IsLetterOrDigit(text[dopo]) || text[dopo] == '_');

            if (primaLibera && dopoLibera) return i;
        }

        return -1;
    }

    /// <summary>Estrae il contenuto della parentesi che si apre alla posizione indicata.</summary>
    private static string EstraiParentesi(string text, int openIndex)
    {
        var profondita = 0;

        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '\'' or '"' or '`' or '[')
            {
                var chiusura = c == '[' ? ']' : c;
                i++;

                while (i < text.Length && text[i] != chiusura)
                {
                    if (text[i] == '\\' && chiusura == '\'') i++;
                    i++;
                }

                continue;
            }

            if (c == '(') profondita++;
            else if (c == ')' && --profondita == 0) return text[(openIndex + 1)..i];
        }

        return text[(openIndex + 1)..];
    }

    /// <summary>Divide sulle virgole di primo livello, ignorando quelle annidate.</summary>
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var profondita = 0;
        var inizio = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '\'' or '"' or '`' or '[')
            {
                var chiusura = c == '[' ? ']' : c;
                i++;

                while (i < text.Length && text[i] != chiusura)
                {
                    if (text[i] == '\\' && chiusura == '\'') i++;
                    i++;
                }

                continue;
            }

            if (c == '(') profondita++;
            else if (c == ')') profondita--;
            else if (c == ',' && profondita == 0)
            {
                yield return text[inizio..i];
                inizio = i + 1;
            }
        }

        if (inizio <= text.Length) yield return text[inizio..];
    }

    /// <summary>Primo identificativo di una definizione, spogliato delle virgolette.</summary>
    private static string FirstIdentifier(string text)
    {
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length) return string.Empty;

        var c = text[i];

        if (c is '"' or '`' or '[')
        {
            var chiusura = c == '[' ? ']' : c;
            var fine = text.IndexOf(chiusura, i + 1);

            return fine < 0 ? string.Empty : text[(i + 1)..fine];
        }

        var inizio = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;

        return text[inizio..i];
    }

    /// <summary>
    /// Ultimo identificativo di una intestazione, cioè il nome della tabella.
    /// </summary>
    /// <remarks>
    /// Si prende l'ultimo perché il nome può arrivare qualificato — <c>dbo.Contatti</c>,
    /// <c>public.contatti</c>, <c>rubrica`.`contatti</c> — e ciò che serve è la
    /// tabella, non lo schema di una banca dati che qui non esiste.
    /// </remarks>
    private static string LastIdentifier(string text)
    {
        var identificativi = new List<string>();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '"' or '`' or '[')
            {
                var chiusura = c == '[' ? ']' : c;
                var fine = text.IndexOf(chiusura, i + 1);
                if (fine < 0) break;

                identificativi.Add(text[(i + 1)..fine]);
                i = fine;
                continue;
            }

            if (!char.IsLetter(c) && c != '_') continue;

            var inizio = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;

            identificativi.Add(text[inizio..i]);
            i--;
        }

        // Le parole chiave iniziali non sono il nome della tabella.
        var parole = new[] { "CREATE", "TABLE", "INSERT", "INTO", "IF", "NOT", "EXISTS", "OR", "REPLACE", "IGNORE", "DELAYED", "LOW_PRIORITY", "HIGH_PRIORITY" };

        for (var i = identificativi.Count - 1; i >= 0; i--)
        {
            if (!parole.Contains(identificativi[i], StringComparer.OrdinalIgnoreCase))
                return identificativi[i];
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nessuna risorsa trattenuta: il file viene aperto e chiuso a ogni scansione,
        // perche' tenerlo aperto bloccherebbe un backup che qualcuno potrebbe voler
        // spostare mentre la procedura guidata e' a meta'.
    }
}
