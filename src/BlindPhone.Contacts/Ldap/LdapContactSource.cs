using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Contacts.Ldap;

/// <summary>
/// Legge la rubrica da una directory LDAP o da Active Directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>La directory è la fonte, non l'archivio di lavoro</b>, come per il gestionale su
/// SQL Server: i contatti letti finiscono nella copia locale cifrata, che resta ciò
/// che l'operatore interroga. Il nome del chiamante viene cercato mentre il telefono
/// squilla, e un giro sulla directory in quel momento — o una directory che non
/// risponde — lascerebbe l'operatore senza sapere chi sta chiamando proprio quando
/// deve decidere.
/// </para>
/// <para>
/// <b>Nella PA la directory è spesso la rubrica più aggiornata che c'è.</b> L'ufficio
/// del personale ci crea l'utenza il primo giorno e la disattiva l'ultimo: leggerla
/// da qui è il modo per non trasferire chiamate a chi se n'è andato da mesi.
/// </para>
/// </remarks>
public sealed class LdapContactSource : IContactSource
{
    /// <summary>
    /// Che cosa dire quando il collegamento è in chiaro.
    /// </summary>
    /// <remarks>
    /// Si dice a ogni prova del collegamento, non solo quando lo si sceglie: chi
    /// riapre le impostazioni mesi dopo deve risentirlo, perché è una scelta che si
    /// dimentica di aver fatto.
    /// </remarks>
    public const string PlainTextWarning =
        "Attenzione: il collegamento alla directory e' in chiaro. Nomi e numeri viaggiano leggibili "
        + "sulla rete, e con un'utenza dedicata anche la sua password.";

    private readonly LdapOptions _options;
    private readonly Func<ILdapEntryReader> _open;
    private readonly ILogger<LdapContactSource> _log;

    /// <summary>Crea la sorgente collegata al server.</summary>
    /// <param name="options">Parametri del collegamento.</param>
    /// <param name="password">Password dal deposito protetto, per l'utenza dedicata.</param>
    /// <param name="logger">Log diagnostico.</param>
    public LdapContactSource(LdapOptions options, string? password, ILogger<LdapContactSource>? logger = null)
        : this(options, () => new LdapDirectoryReader(options, password), logger)
    {
    }

    /// <summary>Crea la sorgente con un fornitore di voci qualsiasi.</summary>
    /// <param name="options">Parametri del collegamento: servono il nome e l'abbinamento.</param>
    /// <param name="open">Apre il lettore. Ogni chiamata ne restituisce uno nuovo.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <remarks>
    /// Serve a provare l'abbinamento degli attributi senza un server acceso, che è
    /// la parte in cui si decide quale numero verrà composto.
    /// </remarks>
    public LdapContactSource(LdapOptions options, Func<ILdapEntryReader> open, ILogger<LdapContactSource>? logger = null)
    {
        _options = options;
        _open = open;
        _log = logger ?? NullLogger<LdapContactSource>.Instance;
    }

    /// <inheritdoc />
    public string Name => $"Directory LDAP: {_options.Server.Trim()}";

    /// <inheritdoc />
    public Task<IReadOnlyList<Contact>> LoadAsync(CancellationToken cancellationToken)
        => Task.Run(() => Read(limit: null, cancellationToken), cancellationToken);

    /// <summary>Legge le prime voci, per l'anteprima nelle impostazioni.</summary>
    /// <param name="rows">Voci da leggere.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// Il limite conta le voci lette e non i contatti prodotti, come per le tabelle:
    /// l'anteprima serve a sentire come verranno lette le voci, non a contarle.
    /// </remarks>
    public Task<IReadOnlyList<Contact>> PreviewAsync(int rows, CancellationToken cancellationToken)
        => Task.Run(() => Read(rows, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<SourceTestResult> TestAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
            return new SourceTestResult(false, "Server e base della ricerca non sono ancora indicati");

        try
        {
            var contatti = await LoadAsync(cancellationToken).ConfigureAwait(false);

            var avviso = _options.Security == LdapSecurity.Plain ? " " + PlainTextWarning : string.Empty;

            return contatti.Count == 0
                ? new SourceTestResult(
                    false,
                    "Collegamento riuscito, ma il filtro non trova nessuna voce con un numero di telefono."
                    + " Controlla la base e il filtro della ricerca" + avviso,
                    0)
                : new SourceTestResult(
                    true,
                    $"Collegamento riuscito: {contatti.Count} contatti con almeno un numero." + avviso,
                    contatti.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Collegamento alla directory {Server} non riuscito", _options.Describe());
            return new SourceTestResult(false, DescribeFailure(ex, _options));
        }
    }

    private IReadOnlyList<Contact> Read(int? limit, CancellationToken cancellationToken)
    {
        using var lettore = _open();

        var contatti = new List<Contact>();
        var senzaNumero = 0;

        foreach (var voce in lettore.ReadEntries(limit, cancellationToken))
        {
            var contatto = BuildContact(voce, _options.Attributes, Name);

            // Una voce senza recapiti non serve a un centralino e occuperebbe
            // posizioni nell'elenco dei risultati.
            if (contatto is null)
            {
                senzaNumero++;
                continue;
            }

            contatti.Add(contatto);
        }

        if (senzaNumero > 0)
            _log.LogInformation("{Sorgente}: {Conteggio} voci ignorate perche' prive di numeri", Name, senzaNumero);

        if (_options.Security == LdapSecurity.Plain)
            _log.LogWarning("Rubrica letta da {Server} con un collegamento in chiaro", _options.Describe());

        return contatti;
    }

    /// <summary>
    /// Trasforma una voce della directory in un contatto.
    /// </summary>
    /// <param name="entry">Voce letta.</param>
    /// <param name="mapping">Quale attributo riempie quale campo.</param>
    /// <param name="sourceName">Nome della sorgente, sotto cui il contatto viene archiviato.</param>
    /// <returns>Il contatto, oppure <c>null</c> se la voce non ha numeri componibili.</returns>
    /// <remarks>
    /// <para>
    /// <b>L'interno viene prima di tutto</b>, e diventa il recapito predefinito: è
    /// quello che squilla sulla scrivania ed è l'unico su cui un trasferimento si
    /// chiude senza uscire dal centralino. Se la directory non ha un attributo per
    /// l'interno ma lo accoda al numero diretto — «+39 0521 703111 int. 4021» — lo si
    /// ricava da lì.
    /// </para>
    /// <para>
    /// Lo stesso numero dichiarato due volte — l'interno in <c>ipPhone</c> e di nuovo
    /// in coda a <c>telephoneNumber</c> — compare una volta sola: un recapito ripetuto
    /// è una voce in più da ascoltare quando si chiede l'elenco dei numeri.
    /// </para>
    /// </remarks>
    public static Contact? BuildContact(LdapEntry entry, LdapAttributeMapping mapping, string sourceName)
    {
        var numeri = new List<PhoneNumber>();
        var scartati = new List<string>();

        foreach (var valore in entry.All(mapping.Extension)) Aggiungi(valore, PhoneNumberKind.Extension);
        foreach (var valore in entry.All(mapping.WorkPhone)) Aggiungi(valore, PhoneNumberKind.Work);
        foreach (var valore in entry.All(mapping.MobilePhone)) Aggiungi(valore, PhoneNumberKind.Mobile);

        void Aggiungi(string valore, PhoneNumberKind tipo)
        {
            if (DirectoryNumber.Parse(valore) is not { } letto)
            {
                // Una nota nel campo del telefono non si compone, ma non si butta:
                // finisce fra le note, dove resta leggibile e ricercabile.
                scartati.Add(valore);
                return;
            }

            if (letto.Extension is { } interno) Inserisci(interno, PhoneNumberKind.Extension);
            if (letto.Number is { } numero) Inserisci(numero, tipo);
        }

        void Inserisci(string numero, PhoneNumberKind tipo)
        {
            if (numeri.Any(n => string.Equals(n.Number, numero, StringComparison.Ordinal))) return;

            numeri.Add(new PhoneNumber(numero, tipo, numeri.Count == 0));
        }

        if (numeri.Count == 0) return null;

        var nome = entry.First(mapping.FirstName);
        var cognome = entry.First(mapping.LastName);
        var visualizzato = entry.First(mapping.DisplayName);
        var haPersona = !string.IsNullOrWhiteSpace(nome) || !string.IsNullOrWhiteSpace(cognome);

        return new Contact
        {
            Id = BuildId(entry, mapping),
            FirstName = nome,
            LastName = cognome,
            DisplayName = visualizzato,
            Email = entry.First(mapping.Email),
            Organization = entry.First(mapping.Organization),
            Department = entry.First(mapping.Department),
            JobTitle = entry.First(mapping.JobTitle),
            Building = entry.First(mapping.Building),
            Notes = scartati.Count == 0 ? null : string.Join("; ", scartati),
            Numbers = numeri,

            // Senza nome e cognome la voce e' un servizio, e il suo nominativo e' la
            // descrizione da cui si riconosce un fax: «Fax Protocollo» e' un contatto
            // della directory come gli altri, ma comporlo non mette in linea nessuno.
            Kind = ContactKindRules.Derive(
                hasPersonName: haPersona,
                isShortCode: false,
                description: haPersona ? null : visualizzato),

            Source = sourceName,
            UpdatedAt = ParseGeneralizedTime(entry.First(mapping.UpdatedAt)) ?? DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Compone l'identificativo stabile del contatto.
    /// </summary>
    /// <remarks>
    /// Con l'identificativo della directory la voce resta la stessa anche quando la
    /// persona cambia ufficio o cognome, e con lei i preferiti e i numeri rapidi che
    /// l'operatore le ha assegnato. Il nome distinto è il ripiego: unico, ma cambia
    /// proprio in quei due casi.
    /// </remarks>
    private static Guid BuildId(LdapEntry entry, LdapAttributeMapping mapping)
    {
        var chiave = entry.First(mapping.EntryKey) is { } stabile
            ? $"ldap:#{stabile.ToLowerInvariant()}"
            : $"ldap:dn:{entry.DistinguishedName.Trim().ToLowerInvariant()}";

        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(chiave)));
    }

    /// <summary>
    /// Legge una data nel formato delle directory, per esempio <c>20261030101500.0Z</c>.
    /// </summary>
    /// <param name="value">Valore dell'attributo.</param>
    /// <returns>La data, oppure <c>null</c> se il valore non è una data riconoscibile.</returns>
    /// <remarks>
    /// È il formato «generalized time» di LDAP: anno, mese, giorno, ore, minuti e
    /// secondi di seguito, frazioni facoltative, e il fuso come <c>Z</c> o come
    /// scostamento. Un formato non riconosciuto non deve far perdere il contatto: si
    /// ripiega sul momento della lettura.
    /// </remarks>
    public static DateTimeOffset? ParseGeneralizedTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 14) return null;

        var testo = value.Trim();

        if (!DateTime.TryParseExact(
                testo[..14],
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var data))
        {
            return null;
        }

        var resto = testo[14..];
        var fuso = resto.IndexOfAny(['Z', 'z', '+', '-']);

        if (fuso < 0 || resto[fuso] is 'Z' or 'z') return new DateTimeOffset(data, TimeSpan.Zero);

        var scostamento = resto[(fuso + 1)..];
        if (scostamento.Length < 4
            || !int.TryParse(scostamento[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var ore)
            || !int.TryParse(scostamento[2..4], NumberStyles.None, CultureInfo.InvariantCulture, out var minuti))
        {
            return null;
        }

        var segno = resto[fuso] == '-' ? -1 : 1;
        return new DateTimeOffset(data, segno * new TimeSpan(ore, minuti, 0));
    }

    /// <summary>
    /// Traduce un errore della directory in una frase che dice cosa fare.
    /// </summary>
    /// <param name="exception">Errore ricevuto.</param>
    /// <param name="options">Collegamento tentato: alcune frasi dipendono da come lo si è tentato.</param>
    public static string DescribeFailure(Exception exception, LdapOptions options) => exception switch
    {
        LdapSourceException propria => propria.Message,

        // StartTLS rifiutato e' un caso a parte: il server risponde, ma non sa o non
        // vuole cifrare, e la mossa successiva e' un'altra scelta di protezione.
        TlsOperationException => "Il server non accetta StartTLS: prova con LDAPS, oppure chiedi "
                                 + "all'amministratore di installare un certificato sul server",

        LdapException ldap => DescribeLdapFailure(ldap.ErrorCode, ldap.ServerErrorMessage, options.Security, options.Bind),

        DirectoryOperationException operazione => DescribeLdapFailure(
            (int)(operazione.Response?.ResultCode ?? ResultCode.Other),
            operazione.Response?.ErrorMessage,
            options.Security,
            options.Bind),

        OperationCanceledException => "La directory non ha risposto entro il tempo previsto",

        _ => "Collegamento alla directory non riuscito",
    };

    /// <summary>
    /// Traduce il codice di risposta LDAP in una frase che dice cosa fare.
    /// </summary>
    /// <param name="code">
    /// Codice di risposta del server (RFC 4511) o del client di Windows, come 81 per
    /// «server non raggiungibile».
    /// </param>
    /// <param name="serverMessage">
    /// Il testo diagnostico del server. Active Directory ci scrive il motivo preciso di
    /// un'autenticazione rifiutata — password scaduta, utenza bloccata — come codice
    /// esadecimale dopo la parola <c>data</c>.
    /// </param>
    /// <param name="security">Come si è protetto il collegamento.</param>
    /// <param name="bind">Con quale identità ci si è presentati.</param>
    /// <remarks>
    /// <para>
    /// Prende i numeri e non l'eccezione perché le eccezioni di
    /// <c>System.DirectoryServices.Protocols</c> non si costruiscono con un codice a
    /// scelta: senza questo passaggio la traduzione non sarebbe verificabile con un
    /// test, e un test che ne riscrivesse la tabella verificherebbe la propria copia.
    /// </para>
    /// <para>
    /// «LDAP error 49» non dice niente a chi lo ascolta una volta sola; «il server ha
    /// rifiutato le credenziali» gli permette di chiamare l'amministratore sapendo
    /// cosa riferire. Le varianti di Active Directory valgono la fatica: «la password
    /// è scaduta» e «l'utenza è bloccata» si risolvono in due modi diversi, e senza
    /// distinguerle si prova a ridigitare la password, bloccando l'utenza davvero.
    /// </para>
    /// </remarks>
    public static string DescribeLdapFailure(int code, string? serverMessage, LdapSecurity security, LdapBind bind)
    {
        const string credenzialiRifiutate = "Il server ha rifiutato le credenziali: nome utente o password non validi";

        if (code == 49)
        {
            return ActiveDirectoryReason(serverMessage) switch
            {
                "532" => "La password dell'utenza della directory e' scaduta: va rinnovata dall'amministratore",
                "773" => "La password dell'utenza della directory va cambiata prima di poterla usare",
                "533" => "L'utenza della directory e' disattivata",
                "701" => "L'utenza della directory e' scaduta",
                "775" => "L'utenza della directory e' bloccata per troppi tentativi sbagliati: chiedi all'amministratore di sbloccarla",
                "530" or "531" => "L'utenza della directory non puo' accedere da questa postazione o a quest'ora",
                _ => credenzialiRifiutate,
            };
        }

        return code switch
        {
            // Active Directory risponde cosi' a chi cerca senza essersi presentato.
            1 when bind == LdapBind.Anonymous =>
                "Il server non permette di leggere la directory senza un'utenza: scegli l'utenza di Windows o un'utenza dedicata",

            3 => "La directory non ha finito la ricerca entro il tempo previsto: restringi la base o il filtro",
            4 => "La directory ha restituito solo una parte delle voci, perche' ne limita il numero",
            7 => "Il server non accetta questo modo di accesso: prova un'altra scelta alla voce accesso",
            8 or 13 => "Il server accetta solo collegamenti protetti: scegli LDAPS oppure StartTLS",
            10 => "La base della ricerca non sta su questo server, che rimanda a un altro: controlla il server e la base",
            32 => "La base della ricerca non esiste sul server: controlla come e' scritta",
            34 => "La base della ricerca non e' scritta correttamente: deve essere un nome distinto, con le parti separate da virgole",
            48 => "Il server non accetta l'accesso anonimo: indica un'utenza",
            50 => "L'utenza non ha il permesso di leggere la directory",
            51 or 52 => "La directory e' occupata o non disponibile: riprova piu' tardi",
            53 => "La directory ha rifiutato la richiesta",

            // Con LDAPS un certificato che la postazione non riconosce produce lo
            // stesso codice di un server spento: il client di Windows chiude la
            // connessione e non dice altro. Va detto, perche' e' il caso piu' comune.
            81 when security == LdapSecurity.Ldaps =>
                "Il server non risponde in modo protetto: verifica nome e porta, e che il certificato del server "
                + "sia riconosciuto da questa postazione",

            81 or 91 => "Il server non risponde: verifica il nome, la porta e che sia raggiungibile dalla rete",
            85 => "Il server non ha risposto entro il tempo previsto",
            87 => "Il filtro della ricerca non e' scritto correttamente: controlla le parentesi",

            _ => $"La directory ha risposto con l'errore {code}",
        };
    }

    /// <summary>
    /// Il motivo che Active Directory scrive dopo la parola <c>data</c>, se c'è.
    /// </summary>
    /// <remarks>
    /// Il testo ha la forma «80090308: LdapErr: DSID-0C09044E, comment:
    /// AcceptSecurityContext error, data 52e, v4563». È inglese e non va
    /// pronunciato, ma il codice dopo <c>data</c> è stabile e documentato.
    /// </remarks>
    private static string? ActiveDirectoryReason(string? serverMessage)
    {
        if (string.IsNullOrWhiteSpace(serverMessage)) return null;

        var posizione = serverMessage.IndexOf("data ", StringComparison.OrdinalIgnoreCase);
        if (posizione < 0) return null;

        var codice = new string([.. serverMessage[(posizione + 5)..].TakeWhile(char.IsAsciiHexDigit)]);
        return codice.Length == 0 ? null : codice.ToLowerInvariant();
    }
}
