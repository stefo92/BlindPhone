namespace BlindPhone.Infrastructure;

/// <summary>Da dove BlindPhone prende i contatti della rubrica.</summary>
public enum ContactsMode
{
    /// <summary>Solo quello che è stato importato a mano da file.</summary>
    LocalFile,

    /// <summary>Da una tabella di Microsoft SQL Server, riallineata periodicamente.</summary>
    SqlServer,

    /// <summary>Da una directory LDAP o Active Directory, riallineata periodicamente.</summary>
    Ldap,

    /// <summary>Dalle cartelle Contatti di Outlook classico sulla postazione.</summary>
    Outlook,
}

/// <summary>Come proteggere il collegamento alla directory.</summary>
public enum ContactsLdapSecurity
{
    /// <summary>LDAP su TLS dal primo byte, di norma sulla porta 636.</summary>
    Ldaps,

    /// <summary>Porta ordinaria, cifrata subito dopo con StartTLS.</summary>
    StartTls,

    /// <summary>Nessuna cifratura: solo per scelta esplicita, con un avviso.</summary>
    Plain,
}

/// <summary>Con quale identità presentarsi alla directory.</summary>
public enum ContactsLdapBind
{
    /// <summary>Con l'utenza di Windows già connessa alla postazione.</summary>
    Windows,

    /// <summary>Con un'utenza dedicata, nome e password.</summary>
    Simple,

    /// <summary>Senza identità.</summary>
    Anonymous,
}

/// <summary>
/// Provenienza della rubrica e collegamento al gestionale dell'ente.
/// </summary>
/// <remarks>
/// <para>
/// <b>La copia locale esiste in entrambi i casi.</b> Con il collegamento attivo il
/// server è la fonte, non l'archivio di lavoro: i contatti vengono riversati
/// nell'archivio cifrato della postazione, che resta ciò che l'operatore interroga.
/// </para>
/// <para>
/// La ragione è operativa e non architetturale. Il nome del chiamante viene cercato
/// <i>mentre il telefono squilla</i>: un giro di rete in quel momento, o una rete che
/// non c'è, lascerebbe l'operatore senza sapere chi lo sta chiamando proprio nei
/// secondi in cui deve decidere. Preferiti, numeri rapidi e stato degli interni
/// restano locali per lo stesso motivo, e perché sono personali di quella postazione.
/// </para>
/// </remarks>
public sealed record ContactsSettings
{
    /// <summary>Provenienza della rubrica.</summary>
    public ContactsMode Mode { get; init; } = ContactsMode.LocalFile;

    /// <summary>Collegamento al server, usato quando la modalità lo prevede.</summary>
    public ContactsSqlServerSettings SqlServer { get; init; } = new();

    /// <summary>Collegamento alla directory, usato quando la modalità lo prevede.</summary>
    public ContactsLdapSettings Ldap { get; init; } = new();

    /// <summary>Lettura dei contatti di Outlook, usata quando la modalità lo prevede.</summary>
    public ContactsOutlookSettings Outlook { get; init; } = new();

    /// <summary>
    /// Campi della rubrica mostrati accanto ai contatti proposti sopra il campo di
    /// composizione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nominativo e numero ci sono sempre e non compaiono qui: sono la riga stessa.
    /// Le chiavi ammesse sono quelle di <c>DialerFieldCatalog</c>; quelle che non
    /// riconosce vengono ignorate, così una configurazione scritta a mano o arrivata
    /// da un pacchetto di provisioning non lascia il posto operatore senza elenco.
    /// </para>
    /// <para>
    /// L'elenco vuoto è una scelta legittima e significa «solo nominativo e numero»:
    /// per questo non si ricade sul valore predefinito quando è vuoto, ma solo quando
    /// non è mai stato scritto.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? DialerFields { get; init; }
}

/// <summary>
/// Collegamento alla rubrica su Microsoft SQL Server.
/// </summary>
/// <remarks>
/// <b>La password non sta qui.</b> Questo oggetto finisce in <c>settings.json</c>,
/// che è testo in chiaro sul disco della postazione: si conserva la sola chiave con
/// cui ritrovare la password nel deposito protetto di Windows.
/// </remarks>
public sealed record ContactsSqlServerSettings
{
    /// <summary>Nome o indirizzo del server, con l'istanza se non è quella predefinita.</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>Banca dati che contiene la rubrica.</summary>
    public string Database { get; init; } = string.Empty;

    /// <summary>Accedere con l'utenza di Windows già connessa alla postazione.</summary>
    public bool UseWindowsAuthentication { get; init; } = true;

    /// <summary>Utenza di SQL Server, quando non si usa quella di Windows.</summary>
    public string? Username { get; init; }

    /// <summary>Chiave della password nel deposito protetto.</summary>
    public string PasswordSecretKey { get; init; } = "contacts.sqlserver.password";

    /// <summary>Tabella o vista da cui leggere.</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>Accetta il certificato del server anche se non verificabile.</summary>
    public bool TrustServerCertificate { get; init; }

    /// <summary>Ogni quanti minuti riallineare la copia locale. Zero per non farlo da sé.</summary>
    public int SyncIntervalMinutes { get; init; } = 60;

    /// <summary>Abbinamento fra colonne del server e campi del contatto.</summary>
    public ContactsColumnMapping Mapping { get; init; } = new();

    /// <summary>Il collegamento è configurato quanto basta per tentarlo.</summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(Server)
           && !string.IsNullOrWhiteSpace(Database)
           && !string.IsNullOrWhiteSpace(Table);
}

/// <summary>
/// Quale colonna del server riempie quale campo del contatto.
/// </summary>
/// <remarks>
/// <para>
/// Le prime voci sono le stesse della procedura guidata dei file: numero, secondo
/// numero, relativo, descrizione, nome, cognome, reparto, padiglione, piano. Tenere
/// gli stessi campi significa che chi ha imparato a importare un file sa già
/// collegare un server.
/// </para>
/// <para>
/// <b>Le ultime quattro non compaiono nella procedura guidata</b>, e non è una
/// dimenticanza: non sono scelte dell'operatore ma fatti della sorgente — come si
/// identifica una voce, come si riconosce un numero breve, come si riconosce una voce
/// dismessa, dove sta la data di aggiornamento. Le riempie il profilo riconosciuto,
/// e chiederle a chi importa significherebbe pretendere che conosca lo schema del
/// gestionale dell'ente.
/// </para>
/// </remarks>
public sealed record ContactsColumnMapping
{
    /// <summary>Colonna dell'interno.</summary>
    public string? Extension { get; init; }

    /// <summary>
    /// Colonna del secondo recapito.
    /// </summary>
    /// <remarks>
    /// Nelle rubriche di centralino è il secondo apparecchio della stessa postazione,
    /// o il numero urbano diretto. Mancava, e mancava per costruzione: le impostazioni
    /// non avevano dove metterlo, quindi dal server si poteva importare un solo numero
    /// per contatto anche quando la tabella ne dichiarava due. Su una rubrica vera
    /// erano millequattrocento recapiti irraggiungibili.
    /// </remarks>
    public string? WorkPhone { get; init; }

    /// <summary>Colonna dell'interno di riferimento.</summary>
    public string? RelatedTo { get; init; }

    /// <summary>Colonna della descrizione della voce.</summary>
    public string? Category { get; init; }

    /// <summary>Colonna del nome.</summary>
    public string? FirstName { get; init; }

    /// <summary>Colonna del cognome.</summary>
    public string? LastName { get; init; }

    /// <summary>Colonna del reparto.</summary>
    public string? Department { get; init; }

    /// <summary>Colonna del padiglione.</summary>
    public string? Building { get; init; }

    /// <summary>Colonna del piano.</summary>
    public string? Floor { get; init; }

    /// <summary>Colonna che identifica la voce nella sorgente, in modo stabile.</summary>
    public string? EntryKey { get; init; }

    /// <summary>Colonna che marca la voce come codice abbreviato.</summary>
    public string? ShortCodeFlag { get; init; }

    /// <summary>Colonna che marca la voce come non più in uso.</summary>
    public string? InactiveFlag { get; init; }

    /// <summary>Colonna con la data dell'ultima modifica nella sorgente.</summary>
    public string? UpdatedAt { get; init; }
}

/// <summary>
/// Collegamento alla rubrica su una directory LDAP o Active Directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>La password non sta qui.</b> Questo oggetto finisce in <c>settings.json</c>, che
/// è testo in chiaro sul disco della postazione: si conserva la sola chiave con cui
/// ritrovarla nel deposito protetto di Windows, dove è legata al server per cui è
/// stata inserita. Cambiare il server in questo file non la manda altrove.
/// </para>
/// <para>
/// I valori predefiniti sono quelli di Active Directory e coincidono con quelli della
/// sorgente, <c>LdapOptions</c>: una prova lo verifica, perché le due definizioni
/// stanno in due progetti che non si vedono.
/// </para>
/// </remarks>
public sealed record ContactsLdapSettings
{
    /// <summary>Nome o indirizzo del server, oppure il nome del dominio.</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>Porta; zero per quella predefinita del tipo di protezione scelto.</summary>
    public int Port { get; init; }

    /// <summary>Come proteggere il collegamento.</summary>
    public ContactsLdapSecurity Security { get; init; } = ContactsLdapSecurity.Ldaps;

    /// <summary>Con quale identità presentarsi.</summary>
    public ContactsLdapBind Bind { get; init; } = ContactsLdapBind.Windows;

    /// <summary>Utenza dedicata, quando non si usa quella di Windows.</summary>
    public string? BindUser { get; init; }

    /// <summary>Chiave della password nel deposito protetto.</summary>
    public string PasswordSecretKey { get; init; } = "contacts.ldap.password";

    /// <summary>Da dove cominciare a cercare.</summary>
    public string BaseDn { get; init; } = string.Empty;

    /// <summary>Quali voci prendere: utenti attivi e contatti con almeno un numero.</summary>
    public string Filter { get; init; } =
        "(&(|(&(objectCategory=person)(objectClass=user)"
        + "(!(userAccountControl:1.2.840.113556.1.4.803:=2)))(objectClass=contact))"
        + "(|(telephoneNumber=*)(ipPhone=*)(mobile=*)))";

    /// <summary>Cercare solo al primo livello sotto la base, invece che in tutto il sottoalbero.</summary>
    public bool OneLevelOnly { get; init; }

    /// <summary>Ogni quanti minuti riallineare la copia locale. Zero per non farlo da sé.</summary>
    public int SyncIntervalMinutes { get; init; } = 60;

    /// <summary>Quale attributo riempie quale campo del contatto.</summary>
    public ContactsLdapAttributes Attributes { get; init; } = new();

    /// <summary>Il collegamento è configurato quanto basta per tentarlo.</summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(BaseDn);
}

/// <summary>
/// Quale attributo della directory riempie quale campo del contatto.
/// </summary>
/// <remarks>
/// Un attributo lasciato vuoto non viene letto: è il modo in cui un ente decide di
/// non portare sulla postazione un dato che la directory contiene.
/// </remarks>
public sealed record ContactsLdapAttributes
{
    /// <summary>Nominativo per esteso.</summary>
    public string? DisplayName { get; init; } = "displayName";

    /// <summary>Nome.</summary>
    public string? FirstName { get; init; } = "givenName";

    /// <summary>Cognome.</summary>
    public string? LastName { get; init; } = "sn";

    /// <summary>Interno del centralino.</summary>
    public string? Extension { get; init; } = "ipPhone";

    /// <summary>Telefono di ufficio.</summary>
    public string? WorkPhone { get; init; } = "telephoneNumber";

    /// <summary>Cellulare.</summary>
    public string? MobilePhone { get; init; } = "mobile";

    /// <summary>Posta elettronica.</summary>
    public string? Email { get; init; } = "mail";

    /// <summary>Ente o società.</summary>
    public string? Organization { get; init; } = "company";

    /// <summary>Reparto.</summary>
    public string? Department { get; init; } = "department";

    /// <summary>Qualifica.</summary>
    public string? JobTitle { get; init; } = "title";

    /// <summary>Collocazione.</summary>
    public string? Building { get; init; } = "physicalDeliveryOfficeName";

    /// <summary>Identificativo stabile della voce: un fatto della directory, non una scelta.</summary>
    public string? EntryKey { get; init; } = "objectGUID";

    /// <summary>Data dell'ultima modifica: un fatto della directory, non una scelta.</summary>
    public string? UpdatedAt { get; init; } = "whenChanged";
}

/// <summary>
/// Lettura dei contatti di Outlook classico sulla postazione.
/// </summary>
public sealed record ContactsOutlookSettings
{
    /// <summary>Leggere anche le sottocartelle delle cartelle Contatti.</summary>
    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>
    /// Ogni quanti minuti riallineare la copia locale. Zero per non farlo da sé.
    /// </summary>
    /// <remarks>
    /// Ogni quattro ore e non ogni ora: i contatti di Outlook cambiano di rado, e ogni
    /// lettura sveglia Outlook — o lo avvia, se era chiuso — sulla postazione su cui
    /// l'operatore sta lavorando.
    /// </remarks>
    public int SyncIntervalMinutes { get; init; } = 240;
}
