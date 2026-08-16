using System.Globalization;
using System.Text;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Contacts;

/// <summary>
/// Mappatura fra le colonne di un file CSV e i campi del contatto.
/// </summary>
/// <remarks>
/// La mappatura è salvabile come profilo riutilizzabile. Serve perché l'importazione
/// della rubrica è quasi sempre un'operazione ricorrente, non una tantum: l'ufficio
/// personale riesporta il file a ogni cambio di organico, e rifare la mappatura ogni
/// volta è il motivo per cui, nella pratica, le rubriche smettono di essere
/// aggiornate dopo pochi mesi.
/// </remarks>
public sealed record CsvColumnMapping
{
    /// <summary>Colonna del nome.</summary>
    public string? FirstName { get; init; }

    /// <summary>Colonna del cognome.</summary>
    public string? LastName { get; init; }

    /// <summary>Colonna del nome visualizzato.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Colonna dell'interno.</summary>
    public string? Extension { get; init; }

    /// <summary>Colonna del telefono di ufficio.</summary>
    public string? WorkPhone { get; init; }

    /// <summary>Colonna del cellulare.</summary>
    public string? MobilePhone { get; init; }

    /// <summary>Colonna della posta elettronica.</summary>
    public string? Email { get; init; }

    /// <summary>Colonna dell'organizzazione.</summary>
    public string? Organization { get; init; }

    /// <summary>Colonna del reparto.</summary>
    public string? Department { get; init; }

    /// <summary>Colonna della qualifica.</summary>
    public string? JobTitle { get; init; }

    /// <summary>Colonna della resa fonetica del nome.</summary>
    public string? PhoneticName { get; init; }

    /// <summary>Colonna del tipo di voce.</summary>
    public string? Category { get; init; }

    /// <summary>Colonna dell'entità di riferimento.</summary>
    public string? RelatedTo { get; init; }

    /// <summary>Colonna dell'edificio o padiglione.</summary>
    public string? Building { get; init; }

    /// <summary>Colonna del piano.</summary>
    public string? Floor { get; init; }

    /// <summary>
    /// Mappatura che tenta di indovinare le colonne dai nomi più comuni.
    /// </summary>
    /// <remarks>
    /// Il rilevamento automatico va sempre mostrato e confermato dall'operatore, mai
    /// applicato in silenzio: un file mappato male produce una rubrica in cui i
    /// numeri appartengono alle persone sbagliate, e l'errore si scopre solo
    /// trasferendo una chiamata alla persona sbagliata.
    /// </remarks>
    public static CsvColumnMapping Guess(IEnumerable<string> headers)
    {
        // LE INTESTAZIONI DI UNA RUBRICA VERA NON SONO UN INSIEME ORDINATO E DISTINTO.
        //
        // Capita che una colonna si ripeta, che qualcuna resti senza nome e che fra i
        // titoli finiscano residui dell'esportazione. Costruire un dizionario dando per
        // scontato il contrario faceva fallire l'importazione con un'eccezione, prima
        // ancora di mostrare la mappatura da confermare: l'operatore vedeva un errore
        // tecnico e non aveva modo di sapere che bastava rinominare una colonna.
        //
        // Vince la prima occorrenza, ed e' un ordine che si puo' spiegare: e' quella
        // che si incontra per prima scorrendo il file. Per questo si conserva l'ordine
        // delle colonne invece di affidarsi a quello di un dizionario, che non e'
        // garantito e renderebbe la scelta imprevedibile fra un'esecuzione e l'altra.
        var columns = new List<(string Key, string Header)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var header in headers)
        {
            var key = header?.Trim().ToLowerInvariant();

            // Una colonna senza nome non e' mappabile, e va scartata prima del
            // confronto parziale: la stringa vuota e' contenuta in qualunque
            // intestazione, quindi verrebbe accettata per il primo campo cercato e
            // mapperebbe una colonna a caso.
            if (string.IsNullOrEmpty(key)) continue;
            if (!seen.Add(key)) continue;

            columns.Add((key, header!));
        }

        string? Find(params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                foreach (var (key, header) in columns)
                {
                    if (string.Equals(key, candidate, StringComparison.Ordinal)) return header;
                }
            }

            foreach (var candidate in candidates)
            {
                foreach (var (key, header) in columns)
                {
                    if (key.Contains(candidate, StringComparison.Ordinal)) return header;
                }
            }

            return null;
        }

        return new CsvColumnMapping
        {
            // "nominativo" viene cercato per primo fra i nomi visualizzati perche'
            // e' l'intestazione tipica delle rubriche degli enti italiani, dove il
            // nome non e' spezzato in nome e cognome.
            DisplayName = Find("nominativo", "displayname", "display_name", "fullname", "denominazione"),
            FirstName = Find("nome", "firstname", "first_name", "given"),
            LastName = Find("cognome", "lastname", "last_name", "surname"),

            // "numero" copre il caso in cui la rubrica non distingua fra interno e
            // numero diretto, che e' la norma nelle esportazioni dai gestionali.
            Extension = Find("interno", "numero", "extension", "ext"),
            WorkPhone = Find("telefono", "phone", "business", "diretto"),
            MobilePhone = Find("cellulare", "mobile", "cell"),

            Email = Find("email", "mail", "posta"),
            Organization = Find("struttura", "azienda", "ente", "organization", "company"),
            Department = Find("reparto", "unita", "department", "servizio"),
            JobTitle = Find("qualifica", "ruolo", "title", "mansione"),
            PhoneticName = Find("fonetico", "phonetic", "pronuncia"),

            // "descrizione" e' il titolo con cui le rubriche degli enti indicano che
            // cos'e' una voce — "Ambulatorio", "Sala operatoria", "Reperibile" — ed e'
            // l'informazione che dice all'operatore chi gli rispondera'. Viene dopo
            // "tipo" e "categoria": dove ci sono entrambe le colonne, quella dedicata
            // vince su quella descrittiva.
            //
            // Non sta fra i titoli del nome visualizzato, e non e' una svista: il nome
            // visualizzato, quando c'e', vince sulla composizione di nome e cognome, e
            // una descrizione finita li' cancellerebbe "Mario Rossi" dalla rubrica.
            Category = Find("tipo", "categoria", "descrizione", "category", "kind"),
            RelatedTo = Find("relativo", "riferimento", "afferenza", "related"),
            Building = Find("padiglione", "edificio", "building", "blocco", "sede"),
            Floor = Find("piano", "floor", "livello"),
        };
    }
}

/// <summary>
/// Sorgente di contatti da file CSV.
/// </summary>
/// <remarks>
/// <para>
/// Prevista dal capitolato e mantenuta anche con Yeastar come sorgente primaria:
/// serve per i contatti esterni al centralino, che nella pratica di una PA sono
/// molti (altri enti, fornitori, numeri di servizio) e non vivono nel PBX.
/// </para>
/// <para>
/// Il rilevamento di delimitatore e codifica è automatico ma sempre sovrascrivibile:
/// i file esportati dai gestionali italiani usano quasi sempre il punto e virgola e
/// non di rado Windows-1252, e un rilevamento sbagliato produce una rubrica di
/// caratteri illeggibili che la sintesi vocale pronuncia in modo incomprensibile.
/// </para>
/// </remarks>
public sealed class CsvContactSource : IContactSource
{
    private readonly string _path;
    private readonly CsvColumnMapping _mapping;
    private readonly ILogger<CsvContactSource> _log;
    private readonly Encoding _encoding;
    private readonly string _delimiter;

    /// <summary>Crea la sorgente.</summary>
    /// <param name="path">Percorso del file CSV.</param>
    /// <param name="mapping">Mappatura delle colonne.</param>
    /// <param name="encoding">Codifica; se omessa viene rilevata.</param>
    /// <param name="delimiter">Delimitatore; se omesso viene rilevato.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="hasHeaderRecord">
    /// <c>false</c> se il file non ha la riga dei titoli: in quel caso la mappatura
    /// porta le posizioni delle colonne e la prima riga è un contatto.
    /// </param>
    public CsvContactSource(
        string path,
        CsvColumnMapping mapping,
        Encoding? encoding = null,
        string? delimiter = null,
        ILogger<CsvContactSource>? logger = null,
        bool hasHeaderRecord = true)
    {
        _path = path;
        _mapping = mapping;
        _log = logger ?? NullLogger<CsvContactSource>.Instance;
        _encoding = encoding ?? DetectEncoding(path);
        _delimiter = delimiter ?? DetectDelimiter(path, _encoding);
        _hasHeaderRecord = hasHeaderRecord;
    }

    /// <summary>
    /// Il file dichiara i titoli delle colonne nella prima riga.
    /// </summary>
    /// <remarks>
    /// Quando è <c>false</c> la mappatura non contiene nomi di colonna ma <b>posizioni
    /// in base uno</b>, e la prima riga del file è un contatto come tutti gli altri.
    /// </remarks>
    private readonly bool _hasHeaderRecord;

    /// <inheritdoc />
    public string Name => $"CSV: {Path.GetFileName(_path)}";

    /// <summary>
    /// Come si presentano le colonne di un file nella procedura guidata.
    /// </summary>
    /// <param name="Columns">
    /// Identificativo di ogni colonna: il titolo dichiarato, oppure la posizione in
    /// base uno quando il file non ha intestazioni.
    /// </param>
    /// <param name="Labels">
    /// Come la colonna va mostrata e pronunciata. Senza intestazioni porta un esempio
    /// preso dalla prima riga, perché «Colonna 6» da sola non dice niente mentre
    /// «Colonna 6, esempio GIULIA» dice tutto.
    /// </param>
    /// <param name="LooksHeaderless">
    /// La prima riga sembra un contatto e non una riga di titoli.
    /// </param>
    public sealed record CsvLayout(
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> Labels,
        bool LooksHeaderless);

    /// <summary>
    /// Esamina il file e prepara le colonne da abbinare.
    /// </summary>
    /// <param name="path">Percorso del file.</param>
    /// <param name="hasHeaderRecord">
    /// <c>true</c> per leggere la prima riga come titoli, <c>false</c> per trattarla
    /// come un contatto. Se omesso lo decide l'euristica, che resta una proposta da
    /// confermare.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Molte esportazioni non hanno la riga dei titoli</b>, e il danno non è solo
    /// che le colonne restano senza nome: la prima riga viene consumata come
    /// intestazione, cioè un contatto sparisce dalla rubrica a ogni importazione. Su
    /// duemila voci non si nota dal totale, e chi non c'è non risponde al telefono.
    /// </para>
    /// </remarks>
    public static CsvLayout Inspect(string path, bool? hasHeaderRecord = null)
    {
        var encoding = DetectEncoding(path);
        var delimiter = DetectDelimiter(path, encoding);

        var prime = ReadFirstRows(path, encoding, delimiter, 2);

        if (prime.Count == 0) return new CsvLayout([], [], false);

        var senzaTitoli = hasHeaderRecord is { } scelto
            ? !scelto
            : LooksLikeData(prime);

        if (!senzaTitoli)
        {
            var titoli = prime[0];
            return new CsvLayout(titoli, titoli, LooksHeaderless: false);
        }

        var colonne = new List<string>(prime[0].Count);
        var etichette = new List<string>(prime[0].Count);

        for (var i = 0; i < prime[0].Count; i++)
        {
            colonne.Add((i + 1).ToString(CultureInfo.InvariantCulture));

            var esempio = prime[0][i];
            etichette.Add(string.IsNullOrWhiteSpace(esempio)
                ? $"Colonna {i + 1} (vuota)"
                : $"Colonna {i + 1}, esempio: {esempio}");
        }

        return new CsvLayout(colonne, etichette, LooksHeaderless: true);
    }

    /// <summary>
    /// Decide se la prima riga contenga dati invece che titoli.
    /// </summary>
    /// <remarks>
    /// Il segnale è un campo di sole cifre nella stessa posizione in due righe di
    /// fila: i numeri di telefono e i codici stanno nei dati, mentre un titolo di
    /// colonna fatto di sole cifre è una rarità. Resta un'euristica, e per questo
    /// produce una proposta che l'operatore conferma, non una decisione presa da sola.
    /// </remarks>
    private static bool LooksLikeData(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2) return false;

        for (var i = 0; i < rows[0].Count && i < rows[1].Count; i++)
        {
            if (IsDigits(rows[0][i]) && IsDigits(rows[1][i])) return true;
        }

        return false;

        static bool IsDigits(string value)
            => !string.IsNullOrWhiteSpace(value) && value.Trim().All(char.IsAsciiDigit);
    }

    private static List<IReadOnlyList<string>> ReadFirstRows(
        string path,
        Encoding encoding,
        string delimiter,
        int count)
    {
        using var reader = new StreamReader(path, encoding);
        using var csv = new CsvReader(reader, BuildConfiguration(delimiter, hasHeaderRecord: false));

        var righe = new List<IReadOnlyList<string>>(count);

        while (righe.Count < count && csv.Read())
        {
            var riga = new List<string>();
            for (var i = 0; i < csv.Parser.Count; i++) riga.Add(csv.GetField(i) ?? string.Empty);

            righe.Add(riga);
        }

        return righe;
    }

    /// <summary>Legge le intestazioni del file, per la procedura guidata di mappatura.</summary>
    /// <param name="path">Percorso del file.</param>
    /// <returns>
    /// Le intestazioni trovate. L'elenco è vuoto <b>solo</b> se il file non ha una
    /// riga di intestazione.
    /// </returns>
    /// <exception cref="IOException">
    /// Il file non è leggibile: manca, è aperto in un altro programma, sta su una
    /// risorsa non raggiungibile.
    /// </exception>
    /// <remarks>
    /// <b>Gli errori di lettura non vengono più inghiottiti.</b> Prima qualunque
    /// eccezione diventava un elenco vuoto, e la procedura guidata concludeva che il
    /// file non avesse intestazioni: un file aperto in Excel produceva così una
    /// diagnosi sul formato, cioè un invito a cercare dalla parte sbagliata. Chi
    /// chiama traduce l'eccezione con <see cref="ContactFileErrors.Describe"/>.
    /// </remarks>
    public static IReadOnlyList<string> ReadHeaders(string path)
    {
        var encoding = DetectEncoding(path);
        var delimiter = DetectDelimiter(path, encoding);

        using var reader = new StreamReader(path, encoding);
        using var csv = new CsvReader(reader, BuildConfiguration(delimiter));

        try
        {
            csv.Read();
            csv.ReadHeader();
            return csv.HeaderRecord ?? [];
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            // Un file che si legge ma non ha una riga di intestazione utilizzabile:
            // qui l'elenco vuoto e' la risposta giusta, ed e' l'unico caso in cui lo e'.
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Contact>> LoadAsync(CancellationToken cancellationToken)
    {
        var contacts = new List<Contact>();

        using var reader = new StreamReader(_path, _encoding);
        using var csv = new CsvReader(reader, BuildConfiguration(_delimiter, _hasHeaderRecord));

        // SENZA INTESTAZIONE LA PRIMA RIGA E' UN CONTATTO, NON UN TITOLO.
        //
        // Consumarla come intestazione faceva sparire una persona dalla rubrica a
        // ogni importazione, in silenzio: un contatto in meno su duemila non si nota
        // guardando il totale, e chi non c'e' non risponde al telefono.
        if (_hasHeaderRecord)
        {
            await csv.ReadAsync().ConfigureAwait(false);
            csv.ReadHeader();
        }

        var skipped = 0;

        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contact = BuildContact(csv);

            // Un contatto senza recapiti telefonici non serve a un centralino e
            // occuperebbe posizioni nell'elenco dei risultati: su un'interfaccia
            // sonora ogni voce inutile costa tempo di ascolto.
            if (contact is null)
            {
                skipped++;
                continue;
            }

            contacts.Add(contact);
        }

        if (skipped > 0) _log.LogInformation("CSV: {Count} righe ignorate perche' prive di numeri", skipped);

        return contacts;
    }

    private Contact? BuildContact(CsvReader csv)
    {
        var numbers = new List<PhoneNumber>();

        AddNumber(csv, _mapping.Extension, PhoneNumberKind.Extension, numbers, primary: true);
        AddNumber(csv, _mapping.WorkPhone, PhoneNumberKind.Work, numbers, primary: numbers.Count == 0);
        AddNumber(csv, _mapping.MobilePhone, PhoneNumberKind.Mobile, numbers, primary: numbers.Count == 0);

        if (numbers.Count == 0) return null;

        var firstName = Read(csv, _mapping.FirstName);
        var lastName = Read(csv, _mapping.LastName);
        var displayName = Read(csv, _mapping.DisplayName);

        // L'identificativo e' deterministico: reimportare lo stesso file aggiorna i
        // contatti esistenti invece di duplicarli, e i preferiti e i numeri rapidi
        // assegnati dall'operatore restano attaccati alla persona giusta.
        //
        // La chiave include il nome visualizzato perche' in molte rubriche di enti
        // il nome non e' spezzato in nome e cognome: senza, tutte le voci con quei
        // due campi vuoti collasserebbero su pochi identificativi e si
        // sovrascriverebbero a vicenda.
        var key = $"{firstName}|{lastName}|{displayName}|{numbers[0].Number}";

        return new Contact
        {
            Id = DeterministicId(key),
            FirstName = firstName,
            LastName = lastName,
            DisplayName = displayName,
            PhoneticName = Read(csv, _mapping.PhoneticName),
            Email = Read(csv, _mapping.Email),
            Organization = Read(csv, _mapping.Organization),
            Department = Read(csv, _mapping.Department),
            JobTitle = Read(csv, _mapping.JobTitle),
            Category = Read(csv, _mapping.Category),
            RelatedTo = Read(csv, _mapping.RelatedTo),
            Building = Read(csv, _mapping.Building),
            Floor = Read(csv, _mapping.Floor),
            Numbers = numbers,
            Source = Name,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Legge le prime righe del file, per l'anteprima nella procedura di
    /// importazione.
    /// </summary>
    /// <param name="rows">Numero di righe da leggere.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>I contatti che verrebbero importati, nell'ordine del file.</returns>
    /// <remarks>
    /// L'anteprima è indispensabile in un'interfaccia sonora: chi non vede non può
    /// controllare a colpo d'occhio se le colonne sono finite nei campi giusti, e
    /// scoprire una mappatura sbagliata dopo aver importato ottomila voci significa
    /// dover ripulire l'archivio.
    /// </remarks>
    public async Task<IReadOnlyList<Contact>> PreviewAsync(int rows, CancellationToken cancellationToken)
    {
        var contacts = new List<Contact>();

        using var reader = new StreamReader(_path, _encoding);
        using var csv = new CsvReader(reader, BuildConfiguration(_delimiter, _hasHeaderRecord));

        if (_hasHeaderRecord)
        {
            await csv.ReadAsync().ConfigureAwait(false);
            csv.ReadHeader();
        }

        while (contacts.Count < rows && await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contact = BuildContact(csv);
            if (contact is not null) contacts.Add(contact);
        }

        return contacts;
    }

    /// <summary>Codifica rilevata o imposta, mostrata all'operatore.</summary>
    public string EncodingName => _encoding.WebName;

    /// <summary>Delimitatore rilevato o imposto, in forma leggibile.</summary>
    public string DelimiterDescription => _delimiter switch
    {
        ";" => "punto e virgola",
        "," => "virgola",
        "\t" => "tabulazione",
        "|" => "barra verticale",
        _ => _delimiter,
    };

    private void AddNumber(
        CsvReader csv,
        string? column,
        PhoneNumberKind kind,
        List<PhoneNumber> target,
        bool primary)
    {
        var value = Read(csv, column);
        if (string.IsNullOrWhiteSpace(value)) return;

        target.Add(new PhoneNumber(value.Trim(), kind, primary));
    }

    private string? Read(CsvReader csv, string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return null;

        try
        {
            // Senza intestazione la mappatura porta la posizione della colonna, in
            // base uno perche' e' cosi' che la si conta leggendo il file.
            var value = _hasHeaderRecord
                ? csv.GetField(column)
                : csv.GetField(int.Parse(column, CultureInfo.InvariantCulture) - 1);

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception)
        {
            // Colonna dichiarata nella mappatura ma assente in questa riga: capita
            // con i CSV a righe irregolari, e non deve interrompere l'importazione.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SourceTestResult> TestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new SourceTestResult(false, "File non trovato");

        try
        {
            var contacts = await LoadAsync(cancellationToken).ConfigureAwait(false);

            return contacts.Count == 0
                ? new SourceTestResult(false, "Nessun contatto con numero telefonico trovato nel file", 0)
                : new SourceTestResult(true, $"{contacts.Count} contatti trovati nel file", contacts.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lettura del file CSV non riuscita");
            return new SourceTestResult(false, "Il file non e' leggibile come CSV");
        }
    }

    private static CsvConfiguration BuildConfiguration(string delimiter, bool hasHeaderRecord = true) => new(CultureInfo.InvariantCulture)
    {
        Delimiter = delimiter,
        HasHeaderRecord = hasHeaderRecord,
        // I file esportati dai gestionali hanno spesso righe con un numero di campi
        // diverso dall'intestazione: fermarsi al primo produrrebbe un'importazione
        // parziale senza spiegazione.
        MissingFieldFound = null,
        BadDataFound = null,
        TrimOptions = TrimOptions.Trim,
    };

    /// <summary>
    /// Rileva la codifica del file.
    /// </summary>
    /// <remarks>
    /// I gestionali italiani esportano spesso in Windows-1252, e leggere quel file
    /// come UTF-8 produce cognomi con caratteri illeggibili che la sintesi vocale
    /// pronuncia in modo incomprensibile.
    /// </remarks>
    private static Encoding DetectEncoding(string path)
    {
        try
        {
            var buffer = new byte[4];
            using var stream = File.OpenRead(path);
            var read = stream.Read(buffer, 0, 4);

            if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return Encoding.UTF8;

            // Senza BOM si verifica se il contenuto e' UTF-8 valido; in caso
            // contrario si ricade sulla codifica ANSI occidentale.
            var content = File.ReadAllBytes(path);
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);

            try
            {
                strict.GetString(content);
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1252);
            }
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>Rileva il delimitatore contando le occorrenze nella riga di intestazione.</summary>
    private static string DetectDelimiter(string path, Encoding encoding)
    {
        try
        {
            using var reader = new StreamReader(path, encoding);
            var header = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(header)) return ";";

            var candidates = new[] { ";", ",", "\t", "|" };
            var best = candidates
                .Select(d => (Delimiter: d, Count: header.Split(d).Length))
                .OrderByDescending(x => x.Count)
                .First();

            return best.Count > 1 ? best.Delimiter : ";";
        }
        catch (Exception)
        {
            return ";";
        }
    }

    private static Guid DeterministicId(string key)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes($"csv:{key}"));
        return new Guid(bytes);
    }
}
