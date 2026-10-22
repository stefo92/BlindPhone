using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Contacts.SqlBackup;

/// <summary>
/// Costruisce contatti a partire da una tabella, qualunque cosa la fornisca.
/// </summary>
/// <remarks>
/// <para>
/// <b>Un backup su disco e un server sono la stessa cosa vista da qui:</b> righe con
/// colonne. Tenere una sola implementazione significa che la regola su cosa scartare,
/// il modo di comporre l'identificativo e l'anteprima restano identici fra le due
/// procedure — e che una correzione fatta per una vale per l'altra senza doversene
/// ricordare.
/// </para>
/// </remarks>
public abstract class TabularContactSource : IContactSource
{
    private readonly CsvColumnMapping _mapping;
    private readonly string _idPrefix;
    private readonly ILogger _log;

    /// <summary>Crea la sorgente.</summary>
    /// <param name="table">Tabella da leggere.</param>
    /// <param name="mapping">Corrispondenza fra colonne e campi del contatto.</param>
    /// <param name="idPrefix">
    /// Spazio dei nomi dell'identificativo: distingue la provenienza, così la stessa
    /// persona presente in due archivi resta due voci invece di fondersi in silenzio.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    protected TabularContactSource(
        string table,
        CsvColumnMapping mapping,
        string idPrefix,
        ILogger? logger)
    {
        Table = table;
        _mapping = mapping;
        _idPrefix = idPrefix;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Tabella da cui si leggono i contatti.</summary>
    protected string Table { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>
    /// Nome sotto cui vengono archiviati i numeri brevi di questa sorgente.
    /// </summary>
    /// <remarks>
    /// <para>
    /// I codici abbreviati del centralino stanno nella stessa tabella dei contatti
    /// interni, ma non sono la stessa rubrica: sono l'elenco esterno dell'ente —
    /// altri ospedali, ditte, cellulari di reperibili — e in una rubrica ospedaliera
    /// sono spesso più numerosi dei contatti veri. Tenerli sotto una sorgente distinta
    /// permette di svuotarli, contarli e cercarli separatamente, senza che chi cerca
    /// un reparto se li ritrovi in mezzo ai risultati.
    /// </para>
    /// </remarks>
    public string ShortCodeSourceName => $"{Name} (numeri brevi)";

    /// <summary>
    /// Tutti i nomi di sorgente che questa lettura può produrre.
    /// </summary>
    /// <remarks>
    /// Serve a chi riallinea la copia locale: vanno svuotati <b>tutti</b>, non solo
    /// quelli tornati dall'ultima lettura. Se i numeri brevi spariscono dalla tabella
    /// dell'ente devono sparire anche dalla postazione, e svuotare solo ciò che è
    /// tornato li lascerebbe lì per sempre.
    /// </remarks>
    public IReadOnlyList<string> SourceNames => [Name, ShortCodeSourceName];

    /// <summary>Apre il lettore. Ogni chiamata ne restituisce uno nuovo.</summary>
    protected abstract ISqlBackupReader OpenReader();

    /// <inheritdoc />
    public Task<IReadOnlyList<Contact>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(Read(limit: null, cancellationToken));

    /// <summary>
    /// Legge le prime righe, per l'anteprima della procedura guidata.
    /// </summary>
    /// <param name="rows">Righe da leggere.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// L'anteprima è indispensabile in un'interfaccia sonora: chi non vede non può
    /// controllare a colpo d'occhio se le colonne sono finite nei campi giusti, e una
    /// mappatura sbagliata si scoprirebbe altrimenti trasferendo una chiamata alla
    /// persona sbagliata.
    /// </remarks>
    public Task<IReadOnlyList<Contact>> PreviewAsync(int rows, CancellationToken cancellationToken)
        => Task.FromResult(Read(rows, cancellationToken));

    /// <inheritdoc />
    public abstract Task<SourceTestResult> TestAsync(CancellationToken cancellationToken);

    /// <summary>Legge la tabella e ne ricava i contatti.</summary>
    /// <param name="limit">Righe da leggere al massimo; <c>null</c> per tutte.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// <b>Il limite dell'anteprima conta le righe lette, non i contatti prodotti.</b>
    /// Le prime righe di una tabella possono essere tutte scartabili — è il caso della
    /// riga di intestazione finita per errore fra i dati — e insistere finché non se
    /// ne trovano cinque valide farebbe attraversare l'intera tabella per un'anteprima.
    /// </remarks>
    protected IReadOnlyList<Contact> Read(int? limit, CancellationToken cancellationToken)
    {
        using var lettore = OpenReader();

        var contatti = new List<Contact>();
        var senzaNumero = 0;
        var disattivate = 0;

        foreach (var riga in lettore.ReadRows(Table, limit, cancellationToken))
        {
            if (IsInactive(riga))
            {
                disattivate++;
                continue;
            }

            var contatto = Build(riga);

            // Una riga senza recapiti telefonici non serve a un centralino e
            // occuperebbe posizioni nell'elenco dei risultati: su un'interfaccia
            // sonora ogni voce inutile costa tempo di ascolto.
            if (contatto is null)
            {
                senzaNumero++;
                continue;
            }

            contatti.Add(contatto);
        }

        if (senzaNumero > 0)
            _log.LogInformation("{Sorgente}: {Conteggio} righe ignorate perche' prive di numeri", Name, senzaNumero);

        if (disattivate > 0)
            _log.LogInformation("{Sorgente}: {Conteggio} righe ignorate perche' disattivate", Name, disattivate);

        return contatti;
    }

    /// <summary>La riga è marcata come non più in uso nella sorgente.</summary>
    private bool IsInactive(IReadOnlyDictionary<string, string?> riga)
        => IsTrue(ReadColumn(riga, _mapping.InactiveFlag));

    private Contact? Build(IReadOnlyDictionary<string, string?> riga)
    {
        string? Leggi(string? colonna) => ReadColumn(riga, colonna);

        var numeri = new List<PhoneNumber>();
        var scartati = new List<string>();

        Aggiungi(Leggi(_mapping.Extension), PhoneNumberKind.Extension);
        Aggiungi(Leggi(_mapping.WorkPhone), PhoneNumberKind.Work);
        Aggiungi(Leggi(_mapping.MobilePhone), PhoneNumberKind.Mobile);

        void Aggiungi(string? valore, PhoneNumberKind tipo)
        {
            if (string.IsNullOrWhiteSpace(valore)) return;

            var pulito = valore.Trim();

            // UN CAMPO DEL NUMERO CHE NON CONTIENE UN NUMERO NON VA COMPOSTO.
            //
            // Nelle rubriche vere quel campo ospita annotazioni — "breve 6583", due
            // interni separati da spazi, la riga di intestazione rimasta fra i dati.
            // Metterle fra i recapiti significa offrirle come destinazione di un
            // trasferimento, cioe' comporre qualcosa che non e' un numero mentre un
            // chiamante e' in attesa. Vanno nelle note, dove restano leggibili e
            // ricercabili senza essere componibili.
            if (!IsDialable(pulito))
            {
                scartati.Add(pulito);
                return;
            }

            numeri.Add(new PhoneNumber(pulito, tipo, numeri.Count == 0));
        }

        if (numeri.Count == 0) return null;

        var nome = Leggi(_mapping.FirstName);
        var cognome = Leggi(_mapping.LastName);
        var visualizzato = Leggi(_mapping.DisplayName);
        var descrizione = Leggi(_mapping.Category);

        var numeroBreve = IsTrue(Leggi(_mapping.ShortCodeFlag));

        var tipo = ContactKindRules.Derive(
            hasPersonName: !string.IsNullOrWhiteSpace(nome) || !string.IsNullOrWhiteSpace(cognome),
            isShortCode: numeroBreve,
            description: descrizione);

        return new Contact
        {
            Id = BuildId(riga, nome, cognome, visualizzato, numeri[0].Number),
            FirstName = nome,
            LastName = cognome,
            DisplayName = visualizzato,
            PhoneticName = Leggi(_mapping.PhoneticName),
            Email = Leggi(_mapping.Email),
            Organization = Leggi(_mapping.Organization),
            Department = Leggi(_mapping.Department),
            JobTitle = Leggi(_mapping.JobTitle),
            Category = descrizione,
            RelatedTo = Leggi(_mapping.RelatedTo),
            Building = Leggi(_mapping.Building),
            Floor = Leggi(_mapping.Floor),
            Notes = scartati.Count == 0 ? null : string.Join("; ", scartati),
            Numbers = numeri,
            Kind = tipo,
            Source = numeroBreve ? ShortCodeSourceName : Name,
            UpdatedAt = ReadUpdatedAt(Leggi(_mapping.UpdatedAt)),
        };
    }

    /// <summary>
    /// Compone l'identificativo stabile della voce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Quando la sorgente ha una chiave, si usa quella.</b> L'identificativo è
    /// deterministico perché risincronizzare deve aggiornare i contatti esistenti
    /// invece di duplicarli, e i preferiti e i numeri rapidi assegnati dall'operatore
    /// devono restare attaccati alla voce giusta.
    /// </para>
    /// <para>
    /// <b>La chiave composta da nome e numero non basta, e il modo in cui non basta è
    /// silenzioso.</b> In una rubrica ospedaliera più voci condividono lo stesso
    /// apparecchio — l'accettazione e la stanza infermieri, l'ambulatorio e l'ufficio
    /// contabilità — e senza nome di persona quelle righe producono la stessa chiave:
    /// l'ultima letta sovrascrive le altre, che spariscono dalla rubrica senza che
    /// nulla lo segnali. Su una rubrica vera di duemila voci sono cinquantuno.
    /// </para>
    /// <para>
    /// La chiave composta resta per le sorgenti che non ne dichiarano una — un CSV
    /// esportato a mano — dove è comunque meglio di un identificativo casuale, che
    /// duplicherebbe l'intera rubrica a ogni importazione.
    /// </para>
    /// </remarks>
    private Guid BuildId(
        IReadOnlyDictionary<string, string?> riga,
        string? nome,
        string? cognome,
        string? visualizzato,
        string primoNumero)
    {
        if (ReadColumn(riga, _mapping.EntryKey) is { } chiave)
            return DeterministicId($"{_idPrefix}:{Table}:#{chiave}");

        return DeterministicId($"{_idPrefix}:{Table}:{nome}|{cognome}|{visualizzato}|{primoNumero}");
    }

    /// <summary>
    /// Legge la data di ultima modifica dichiarata dalla sorgente.
    /// </summary>
    /// <param name="valore">Valore della colonna, come stringa.</param>
    /// <remarks>
    /// Il momento dell'importazione è la risposta sbagliata a «quanto è vecchia questa
    /// voce»: dice quando BlindPhone ha letto, non quando l'ente ha aggiornato. Resta
    /// il ripiego quando la sorgente non dichiara nulla o dichiara qualcosa che non è
    /// una data — un formato non riconosciuto non deve far perdere il contatto.
    /// </remarks>
    private static DateTimeOffset ReadUpdatedAt(string? valore)
    {
        if (string.IsNullOrWhiteSpace(valore)) return DateTimeOffset.Now;

        return DateTimeOffset.TryParse(
            valore,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var letta)
            ? letta
            : DateTimeOffset.Now;
    }

    private static string? ReadColumn(IReadOnlyDictionary<string, string?> riga, string? colonna)
    {
        if (string.IsNullOrWhiteSpace(colonna)) return null;
        if (!riga.TryGetValue(colonna, out var valore)) return null;

        return string.IsNullOrWhiteSpace(valore) ? null : valore.Trim();
    }

    /// <summary>
    /// Interpreta come vero un contrassegno letto da una tabella.
    /// </summary>
    /// <param name="valore">Valore della colonna, come stringa.</param>
    /// <remarks>
    /// Lo stesso contrassegno arriva come <c>1</c> da SQL Server, come <c>True</c>
    /// dal lettore che converte i bit in testo e come <c>Sì</c> da un CSV compilato a
    /// mano. Riconoscere una sola di queste forme significherebbe importare come
    /// contatti gli ottocento numeri brevi di una rubrica, o tenere in elenco le voci
    /// disattivate: due errori che si notano contando le voci, non guardandole.
    /// </remarks>
    private static bool IsTrue(string? valore)
    {
        if (string.IsNullOrWhiteSpace(valore)) return false;

        var v = valore.Trim();

        return v is "1"
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("si", StringComparison.OrdinalIgnoreCase)
            || v.Equals("sì", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v.Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Il valore è componibile su un telefono.
    /// </summary>
    /// <param name="valore">Contenuto del campo, già ripulito degli spazi ai bordi.</param>
    /// <remarks>
    /// <para>
    /// <b>Le tre regole sono deliberatamente permissive</b>, e il motivo è quale
    /// errore costa di più. Rifiutare un numero valido toglie al contatto il suo unico
    /// recapito, e un contatto senza recapiti sparisce dalla rubrica: un danno
    /// silenzioso, che si scopre quando qualcuno non si riesce più a chiamare.
    /// Accettare un valore malformato costa invece un tentativo di composizione che il
    /// centralino rifiuta subito, davanti a un operatore che se ne accorge.
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   <b>Almeno una cifra.</b> Scarta la riga di intestazione rimasta fra i dati,
    ///   dove nella colonna del numero c'è scritto «Number».
    ///   </description></item>
    ///   <item><description>
    ///   <b>Nessuna lettera.</b> È la regola che sposta nelle note le annotazioni che
    ///   le rubriche vere infilano nel campo del secondo numero: «breve 6583»,
    ///   «nr breve 6041».
    ///   </description></item>
    ///   <item><description>
    ///   <b>Nessuno spazio doppio.</b> Distingue i due numeri stipati in un campo solo
    ///   — «2876   2877» — da un numero scritto con le spaziature d'uso, «+39 0521
    ///   703111», che nessuno scrive con due spazi di fila. Un solo spazio resta
    ///   ammesso, perché rifiutarlo escluderebbe metà dei numeri scritti a mano.
    ///   </description></item>
    /// </list>
    /// </remarks>
    private static bool IsDialable(string valore)
    {
        // UN VALORE CON LA CHIOCCIOLA E' COMPONIBILE SOLO SE E' UN INDIRIZZO SIP
        // INTERO, e le due meta' di questa regola servono a due cose diverse.
        //
        // Ammettere gli indirizzi SIP serve a tenere in rubrica le riunioni ricorrenti
        // — la giunta del lunedi', il consiglio — come contatti normali, con la
        // ricerca, i preferiti e gli annunci che gia' esistono. Senza, finirebbero
        // fra le note e non si potrebbero comporre.
        //
        // Pretendere che sia intero chiude un buco che era gia' li': "812345678@",
        // senza dominio, passava il controllo delle lettere e veniva accettato. Prima
        // era solo un numero strano; da quando un valore con la chiocciola viene
        // composto come URI SIP, e' una chiamata che non puo' arrivare da nessuna
        // parte.
        if (valore.Contains('@', StringComparison.Ordinal)) return IsSipUri(valore);

        if (!valore.Any(char.IsAsciiDigit)) return false;
        if (valore.Any(char.IsLetter)) return false;

        for (var i = 1; i < valore.Length; i++)
        {
            if (char.IsWhiteSpace(valore[i]) && char.IsWhiteSpace(valore[i - 1])) return false;
        }

        return true;
    }

    /// <summary>
    /// Riconosce un indirizzo SIP componibile, per esempio
    /// <c>88012345678.4321@zoomcrc.com</c> oppure <c>201@pbx.ente.it</c>.
    /// </summary>
    /// <param name="valore">Valore letto dalla rubrica di origine.</param>
    /// <remarks>
    /// <para>
    /// La regola è volutamente stretta: <b>una sola chiocciola</b>, a sinistra solo
    /// cifre e i separatori che una composizione può contenere, a destra un nome di
    /// dominio con almeno un punto. Serve a non riaprire la porta che il controllo
    /// principale chiude — le annotazioni che le rubriche vere infilano nel campo del
    /// numero («breve 6583», «int. 4 – stanza 12») non passano, perché nessuna ha
    /// questa forma.
    /// </para>
    /// <para>
    /// La parte a sinistra deve cominciare con una cifra: esclude gli indirizzi di
    /// posta elettronica, che in una colonna di numeri ci finiscono per errore e che
    /// componidoli non porterebbero da nessuna parte.
    /// </para>
    /// </remarks>
    private static bool IsSipUri(string valore)
    {
        var pezzi = valore.Split('@');
        if (pezzi.Length != 2) return false;

        var utente = pezzi[0];
        var host = pezzi[1];

        if (utente.Length == 0 || !char.IsAsciiDigit(utente[0])) return false;
        if (!utente.All(c => char.IsAsciiDigit(c) || c is '.' or '+' or '*' or '#')) return false;

        if (!host.Contains('.', StringComparison.Ordinal)) return false;

        return host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
               && !host.StartsWith('.')
               && !host.EndsWith('.');
    }

    private static Guid DeterministicId(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
