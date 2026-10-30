using BlindPhone.Contacts;
using BlindPhone.Contacts.Ldap;
using BlindPhone.Contacts.Outlook;
using BlindPhone.Contacts.SqlBackup;
using BlindPhone.Contacts.SqlServer;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>Esito di un riallineamento della rubrica dal server.</summary>
/// <param name="Success">Se il riallineamento è riuscito.</param>
/// <param name="Message">Messaggio in linguaggio naturale, pronunciabile.</param>
/// <param name="Written">Contatti scritti nella copia locale.</param>
public sealed record ContactsSyncResult(bool Success, string Message, int Written = 0);

/// <summary>
/// Tiene allineata la copia locale della rubrica con la sua fonte: una tabella di SQL
/// Server, una directory LDAP o i contatti di Outlook.
/// </summary>
/// <remarks>
/// <para>
/// <b>Un riallineamento fallito non è un guasto della postazione.</b> Se il server non
/// risponde la rubrica resta quella dell'ultimo allineamento riuscito, e il
/// centralinista continua a lavorare: è tutto il motivo per cui la copia locale
/// esiste. Il fallimento finisce nel registro e nella scheda delle impostazioni, non
/// in un allarme che interrompe il turno.
/// </para>
/// <para>
/// <b>Non si riallinea durante una chiamata.</b> Riversare qualche migliaio di
/// contatti nell'archivio cifrato impegna il disco, e farlo mentre l'operatore sta
/// parlando è il modo per introdurre un'esitazione nell'audio proprio quando non
/// serve. Se c'è una chiamata in corso il turno successivo se ne riparla.
/// </para>
/// </remarks>
public sealed class ContactsSyncService : IDisposable
{
    private readonly IContactRepository _contacts;
    private readonly SecretStore _secrets;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _isBusyWithCall;
    private readonly ILogger<ContactsSyncService> _log;
    private readonly ILogger<SqlServerContactSource> _sourceLog;
    private readonly ILogger<LdapContactSource>? _ldapLog;
    private readonly ILogger<OutlookContactSource>? _outlookLog;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;

    /// <summary>Crea il servizio.</summary>
    /// <param name="contacts">Archivio locale da riallineare.</param>
    /// <param name="secrets">Deposito protetto da cui leggere la password.</param>
    /// <param name="settings">Impostazioni correnti, rilette a ogni giro.</param>
    /// <param name="isBusyWithCall">Dice se c'è una chiamata in corso.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="sourceLogger">Log della sorgente SQL Server.</param>
    /// <param name="ldapLogger">Log della sorgente LDAP.</param>
    /// <param name="outlookLogger">Log della sorgente Outlook.</param>
    public ContactsSyncService(
        IContactRepository contacts,
        SecretStore secrets,
        Func<AppSettings> settings,
        Func<bool> isBusyWithCall,
        ILogger<ContactsSyncService> logger,
        ILogger<SqlServerContactSource> sourceLogger,
        ILogger<LdapContactSource>? ldapLogger = null,
        ILogger<OutlookContactSource>? outlookLogger = null)
    {
        _contacts = contacts;
        _secrets = secrets;
        _settings = settings;
        _isBusyWithCall = isBusyWithCall;
        _log = logger;
        _sourceLog = sourceLogger;
        _ldapLog = ldapLogger;
        _outlookLog = outlookLogger;
    }

    /// <summary>Avvia il riallineamento periodico, se configurato.</summary>
    public void Start()
    {
        var impostazioni = _settings().Contacts;

        if (DescribeIncomplete(impostazioni) is { } motivo)
        {
            _log.LogInformation("Rubrica locale: nessun riallineamento automatico ({Motivo})", motivo);
            return;
        }

        var minuti = IntervalOf(impostazioni);

        // Il primo allineamento parte dopo mezzo minuto e non subito: all'avvio la
        // postazione sta gia' registrandosi al centralino e aprendo i dispositivi
        // audio, e quelle due cose vengono prima.
        _timer = new Timer(
            async _ => await SyncAsync(CancellationToken.None).ConfigureAwait(false),
            null,
            TimeSpan.FromSeconds(30),
            minuti > 0 ? TimeSpan.FromMinutes(minuti) : Timeout.InfiniteTimeSpan);

        _log.LogInformation(
            "Rubrica collegata a {Fonte}, riallineamento ogni {Minuti} minuti",
            DescribeOrigin(impostazioni), minuti);
    }

    /// <summary>
    /// Perché la provenienza configurata non si può riallineare, oppure <c>null</c>.
    /// </summary>
    /// <param name="settings">Impostazioni della rubrica.</param>
    /// <remarks>
    /// Una frase sola per il riallineamento periodico e per il pulsante: le due strade
    /// devono rifiutare le stesse configurazioni con le stesse parole.
    /// </remarks>
    public static string? DescribeIncomplete(ContactsSettings settings) => settings.Mode switch
    {
        ContactsMode.SqlServer when !settings.SqlServer.IsConfigured => "Il collegamento al server non e' completo",
        ContactsMode.Ldap when !settings.Ldap.IsConfigured =>
            "Il collegamento alla directory non e' completo: servono il server e la base della ricerca",
        ContactsMode.SqlServer or ContactsMode.Ldap or ContactsMode.Outlook => null,
        _ => "La rubrica non e' collegata a un server",
    };

    /// <summary>Ogni quanti minuti riallineare, secondo la provenienza scelta.</summary>
    private static int IntervalOf(ContactsSettings settings) => settings.Mode switch
    {
        ContactsMode.Ldap => settings.Ldap.SyncIntervalMinutes,
        ContactsMode.Outlook => settings.Outlook.SyncIntervalMinutes,
        _ => settings.SqlServer.SyncIntervalMinutes,
    };

    /// <summary>La provenienza, per il registro: mai credenziali, mai utenze.</summary>
    private static string DescribeOrigin(ContactsSettings settings) => settings.Mode switch
    {
        ContactsMode.Ldap => ToLdapOptions(settings.Ldap).Describe(),
        ContactsMode.Outlook => "Outlook",
        _ => settings.SqlServer.Server,
    };

    /// <summary>
    /// Riallinea la copia locale adesso.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    public async Task<ContactsSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var impostazioni = _settings().Contacts;

        if (DescribeIncomplete(impostazioni) is { } motivo)
            return new ContactsSyncResult(false, motivo);

        if (_isBusyWithCall())
        {
            _log.LogInformation("Riallineamento rimandato: chiamata in corso");
            return new ContactsSyncResult(false, "Riallineamento rimandato: c'e' una chiamata in corso");
        }

        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
            return new ContactsSyncResult(false, "Un riallineamento e' gia' in corso");

        try
        {
            var sorgente = BuildConfiguredSource(impostazioni);

            var contatti = await sorgente.LoadAsync(cancellationToken).ConfigureAwait(false);

            if (contatti.Count == 0)
            {
                // Non si svuota la rubrica su una lettura vuota: una tabella
                // temporaneamente vuota, o un permesso revocato che restituisce zero
                // righe, lascerebbe il centralino senza rubrica per un guasto altrui.
                _log.LogWarning("La fonte ha restituito zero contatti: la copia locale resta invariata");
                return new ContactsSyncResult(false, "La fonte non ha restituito contatti: la rubrica resta quella di prima");
            }

            var scritti = await WriteAsync(sorgente, contatti, replace: true, cancellationToken)
                .ConfigureAwait(false);

            _log.LogInformation("Rubrica riallineata da {Fonte}: {Conteggio} contatti", sorgente.Name, scritti);

            return new ContactsSyncResult(true, $"Rubrica riallineata: {Describe(contatti)}", scritti);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Riallineamento della rubrica non riuscito");

            // Il motivo si dice, quando lo si sa: «riallineamento non riuscito» e
            // basta manda a cercare il guasto ovunque, mentre «la password
            // dell'utenza e' scaduta» dice gia' chi chiamare.
            return new ContactsSyncResult(
                false,
                DescribeFailure(ex, impostazioni) is { } perche
                    ? $"Riallineamento non riuscito. {perche}. La rubrica resta quella di prima"
                    : "Riallineamento non riuscito: la rubrica resta quella di prima");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Scrive nella copia locale i contatti letti da una sorgente.
    /// </summary>
    /// <param name="source">Sorgente da cui provengono.</param>
    /// <param name="contacts">Contatti letti.</param>
    /// <param name="replace">
    /// Svuotare prima le voci già presenti della stessa sorgente, invece di
    /// aggiungersi a quelle.
    /// </param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Quanti contatti sono stati scritti.</returns>
    /// <remarks>
    /// <para>
    /// <b>Sta qui e non nei due punti che scrivono</b> — il riallineamento periodico e
    /// il pulsante di importazione — perché le due regole che seguono sono facili da
    /// applicare in un posto e dimenticare nell'altro, e la differenza non si vede
    /// contando i contatti: si vede mesi dopo, come voci che nessuno riesce più a
    /// togliere dalla rubrica.
    /// </para>
    /// <para>
    /// <b>Ogni contatto va sotto la sorgente che dichiara.</b> Una lettura si divide in
    /// rubrica e numeri brevi, e i due gruppi hanno nomi di sorgente diversi. Scriverli
    /// tutti sotto il nome della sorgente li appiattirebbe in uno solo, e la
    /// separazione esisterebbe solo durante la lettura.
    /// </para>
    /// <para>
    /// <b>Si svuotano tutte le sotto-sorgenti, non solo quelle tornate adesso.</b>
    /// Svuotare i soli gruppi appena letti lascerebbe sulla postazione i numeri brevi
    /// di ieri il giorno in cui l'ente li toglie dalla tabella: voci che non esistono
    /// più e che nessuna lettura successiva nominerebbe, quindi che nessun
    /// riallineamento potrebbe più rimuovere.
    /// </para>
    /// </remarks>
    public async Task<int> WriteAsync(
        IContactSource source,
        IReadOnlyList<Contact> contacts,
        bool replace,
        CancellationToken cancellationToken)
    {
        if (replace)
        {
            foreach (var nome in SourceNamesOf(source))
            {
                await _contacts.ClearAsync(nome, cancellationToken).ConfigureAwait(false);
            }
        }

        var scritti = 0;

        foreach (var gruppo in contacts.GroupBy(c => c.Source, StringComparer.Ordinal))
        {
            scritti += await _contacts
                .UpsertAsync(gruppo, gruppo.Key, cancellationToken)
                .ConfigureAwait(false);
        }

        return scritti;
    }

    /// <summary>Tutti i nomi di sorgente che una lettura può produrre.</summary>
    /// <param name="source">Sorgente interrogata.</param>
    public static IReadOnlyList<string> SourceNamesOf(IContactSource source)
        => source is TabularContactSource tabellare ? tabellare.SourceNames : [source.Name];

    /// <summary>
    /// Descrive che cosa è stato letto, distinguendo i numeri brevi dai contatti.
    /// </summary>
    /// <param name="contacts">Contatti letti.</param>
    /// <remarks>
    /// Senza la ripartizione, un totale di duemila voci su una rubrica che ne ha
    /// milleduecento sembrerebbe un errore di importazione; con la ripartizione è
    /// invece la conferma che il riconoscimento ha funzionato.
    /// </remarks>
    public static string Describe(IReadOnlyList<Contact> contacts)
    {
        var brevi = contacts.Count(c => c.Kind == ContactKind.ShortCode);

        return brevi > 0
            ? $"{contacts.Count - brevi} contatti e {brevi} numeri brevi"
            : $"{contacts.Count} contatti";
    }

    /// <summary>La sorgente che corrisponde alla provenienza configurata.</summary>
    /// <param name="settings">Impostazioni della rubrica, già verificate complete.</param>
    public IContactSource BuildConfiguredSource(ContactsSettings settings) => settings.Mode switch
    {
        ContactsMode.Ldap => BuildLdapSource(settings.Ldap),
        ContactsMode.Outlook => BuildOutlookSource(settings.Outlook),
        _ => BuildSource(settings.SqlServer),
    };

    /// <summary>
    /// Traduce l'errore di un riallineamento in una frase, quando la si sa dire.
    /// </summary>
    /// <param name="exception">Errore ricevuto.</param>
    /// <param name="settings">Impostazioni con cui si è tentato.</param>
    /// <returns>La frase, oppure <c>null</c> se l'errore non è di quelli riconosciuti.</returns>
    public static string? DescribeFailure(Exception exception, ContactsSettings settings) => settings.Mode switch
    {
        ContactsMode.Ldap => LdapContactSource.DescribeFailure(exception, ToLdapOptions(settings.Ldap)),
        ContactsMode.Outlook => OutlookContactSource.DescribeFailure(exception),
        _ when exception is Microsoft.Data.SqlClient.SqlException sql => SqlServerContactSource.DescribeSqlFailure(sql),
        _ => null,
    };

    /// <summary>Come si chiama la password della directory quando va reinserita.</summary>
    public const string LdapPasswordDescription = "la password della directory della rubrica";

    /// <summary>Costruisce la sorgente LDAP dalle impostazioni salvate.</summary>
    /// <param name="settings">Collegamento configurato.</param>
    public LdapContactSource BuildLdapSource(ContactsLdapSettings settings)
        => new(ToLdapOptions(settings), ResolveLdapPassword(settings), _ldapLog);

    /// <summary>
    /// La password dell'utenza della directory, solo se vale per il server configurato.
    /// </summary>
    /// <param name="settings">Collegamento configurato.</param>
    /// <returns>
    /// La password, oppure <c>null</c> con l'utenza di Windows o l'accesso anonimo, che
    /// non ne usano; stringa vuota se manca o appartiene a un altro server.
    /// </returns>
    /// <remarks>
    /// <para>
    /// È il meccanismo dei segreti legati all'host della 1.0.17, e qui conta più che
    /// altrove: con l'accesso semplice la password parte verso il server così com'è,
    /// e su un collegamento in chiaro chiunque sia in ascolto la legge. Spostare il
    /// nome del server in <c>settings.json</c> non deve bastare a mandarla a una
    /// macchina che l'ente non ha scelto.
    /// </para>
    /// <para>
    /// Una password negata produce un errore di accesso che dice che cosa manca, e
    /// l'annuncio dei segreti negati dice perché: è la stessa strada delle altre
    /// credenziali, vedi <see cref="SecretStore.GetForHost"/>.
    /// </para>
    /// </remarks>
    public string? ResolveLdapPassword(ContactsLdapSettings settings)
        => settings.Bind != ContactsLdapBind.Simple
            ? null
            : _secrets.GetForHost(settings.PasswordSecretKey, settings.Server, LdapPasswordDescription);

    /// <summary>Costruisce la sorgente Outlook dalle impostazioni salvate.</summary>
    /// <param name="settings">Lettura configurata.</param>
    public OutlookContactSource BuildOutlookSource(ContactsOutlookSettings settings)
        => new(settings.IncludeSubfolders, _outlookLog);

    /// <summary>Traduce le impostazioni salvate nei parametri del collegamento alla directory.</summary>
    /// <param name="settings">Collegamento configurato.</param>
    public static LdapOptions ToLdapOptions(ContactsLdapSettings settings) => new()
    {
        Server = settings.Server.Trim(),
        Port = settings.Port,
        Security = settings.Security switch
        {
            ContactsLdapSecurity.StartTls => LdapSecurity.StartTls,
            ContactsLdapSecurity.Plain => LdapSecurity.Plain,
            _ => LdapSecurity.Ldaps,
        },
        Bind = settings.Bind switch
        {
            ContactsLdapBind.Simple => LdapBind.Simple,
            ContactsLdapBind.Anonymous => LdapBind.Anonymous,
            _ => LdapBind.Windows,
        },
        BindUser = settings.BindUser,
        PasswordSecretKey = settings.PasswordSecretKey,
        BaseDn = settings.BaseDn.Trim(),

        // Un filtro cancellato non vuol dire «tutto»: vuol dire che non lo si e'
        // voluto cambiare. Cercare tutto porterebbe in rubrica computer, gruppi e
        // account di servizio.
        Filter = string.IsNullOrWhiteSpace(settings.Filter) ? LdapOptions.ActiveDirectoryFilter : settings.Filter.Trim(),
        Scope = settings.OneLevelOnly ? LdapScope.OneLevel : LdapScope.Subtree,
        SyncIntervalMinutes = settings.SyncIntervalMinutes,
        Attributes = new LdapAttributeMapping
        {
            DisplayName = settings.Attributes.DisplayName,
            FirstName = settings.Attributes.FirstName,
            LastName = settings.Attributes.LastName,
            Extension = settings.Attributes.Extension,
            WorkPhone = settings.Attributes.WorkPhone,
            MobilePhone = settings.Attributes.MobilePhone,
            Email = settings.Attributes.Email,
            Organization = settings.Attributes.Organization,
            Department = settings.Attributes.Department,
            JobTitle = settings.Attributes.JobTitle,
            Building = settings.Attributes.Building,
            EntryKey = settings.Attributes.EntryKey,
            UpdatedAt = settings.Attributes.UpdatedAt,
        },
    };

    /// <summary>Costruisce la sorgente dalle impostazioni salvate.</summary>
    /// <param name="settings">Collegamento configurato.</param>
    /// <remarks>
    /// La password si legge per il server a cui sta per essere inviata: con
    /// l'autenticazione SQL, spostare il nome del server nella configurazione
    /// manderebbe altrimenti la credenziale del gestionale dell'ente a una macchina
    /// che l'ente non ha scelto. Vedi <see cref="SecretStore.GetForHost"/>.
    /// </remarks>
    public SqlServerContactSource BuildSource(ContactsSqlServerSettings settings)
    {
        var password = settings.UseWindowsAuthentication
            ? null
            : _secrets.GetForHost(
                settings.PasswordSecretKey,
                settings.Server,
                "la password del server della rubrica");

        return new SqlServerContactSource(ToOptions(settings), password, _sourceLog);
    }

    /// <summary>Traduce le impostazioni salvate nei parametri del collegamento.</summary>
    /// <param name="settings">Collegamento configurato.</param>
    public static SqlServerOptions ToOptions(ContactsSqlServerSettings settings) => new()
    {
        Server = settings.Server,
        Database = settings.Database,
        Authentication = settings.UseWindowsAuthentication
            ? SqlServerAuthentication.Windows
            : SqlServerAuthentication.SqlLogin,
        Username = settings.Username,
        PasswordSecretKey = settings.PasswordSecretKey,
        Table = settings.Table,
        TrustServerCertificate = settings.TrustServerCertificate,
        SyncIntervalMinutes = settings.SyncIntervalMinutes,
        Mapping = new CsvColumnMapping
        {
            Extension = settings.Mapping.Extension,
            WorkPhone = settings.Mapping.WorkPhone,
            RelatedTo = settings.Mapping.RelatedTo,
            Category = settings.Mapping.Category,
            FirstName = settings.Mapping.FirstName,
            LastName = settings.Mapping.LastName,
            Department = settings.Mapping.Department,
            Building = settings.Mapping.Building,
            Floor = settings.Mapping.Floor,

            EntryKey = settings.Mapping.EntryKey,
            ShortCodeFlag = settings.Mapping.ShortCodeFlag,
            InactiveFlag = settings.Mapping.InactiveFlag,
            UpdatedAt = settings.Mapping.UpdatedAt,
        },
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }
}
