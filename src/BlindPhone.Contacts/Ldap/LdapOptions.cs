namespace BlindPhone.Contacts.Ldap;

/// <summary>Come proteggere il collegamento alla directory.</summary>
public enum LdapSecurity
{
    /// <summary>LDAP su TLS dal primo byte, di norma sulla porta 636.</summary>
    Ldaps,

    /// <summary>Collegamento sulla porta ordinaria, cifrato subito dopo con StartTLS.</summary>
    StartTls,

    /// <summary>
    /// Nessuna cifratura. Si usa solo per scelta esplicita: nomi, numeri e — con
    /// un'utenza dedicata — la sua password viaggiano leggibili sulla rete.
    /// </summary>
    Plain,
}

/// <summary>Con quale identità presentarsi alla directory.</summary>
public enum LdapBind
{
    /// <summary>
    /// Con l'utenza di Windows già connessa alla postazione, via Kerberos o NTLM.
    /// </summary>
    Windows,

    /// <summary>Con un'utenza dedicata, nome e password.</summary>
    Simple,

    /// <summary>Senza identità: solo sulle directory che lo permettono.</summary>
    Anonymous,
}

/// <summary>Quanto in profondità cercare sotto la base.</summary>
public enum LdapScope
{
    /// <summary>La base e tutto ciò che sta sotto, a ogni livello.</summary>
    Subtree,

    /// <summary>Solo le voci appese direttamente alla base.</summary>
    OneLevel,
}

/// <summary>
/// Quale attributo della directory riempie quale campo del contatto.
/// </summary>
/// <remarks>
/// <para>
/// <b>I valori predefiniti sono quelli di Active Directory</b>, che nella PA è la
/// directory che si incontra quasi sempre. Gli stessi nomi valgono in gran parte anche
/// per OpenLDAP con lo schema <c>inetOrgPerson</c>; dove non valgono — <c>ipPhone</c>
/// e <c>department</c> sono estensioni di Microsoft — l'attributo semplicemente non
/// torna, e il campo resta vuoto.
/// </para>
/// <para>
/// <b>Un attributo lasciato vuoto non viene letto.</b> È il modo in cui un ente
/// decide di non portare sulla postazione un dato che la directory contiene: i
/// cellulari di servizio, per esempio, che non tutte le amministrazioni vogliono
/// vedere comparire nel centralino.
/// </para>
/// </remarks>
public sealed record LdapAttributeMapping
{
    /// <summary>Nominativo per esteso.</summary>
    public string? DisplayName { get; init; } = "displayName";

    /// <summary>Nome.</summary>
    public string? FirstName { get; init; } = "givenName";

    /// <summary>Cognome.</summary>
    public string? LastName { get; init; } = "sn";

    /// <summary>
    /// Interno del centralino.
    /// </summary>
    /// <remarks>
    /// In Active Directory è <c>ipPhone</c>, il campo «Telefono IP» della scheda
    /// Telefoni, che gli enti usano per l'interno perché <c>telephoneNumber</c> porta
    /// già il numero diretto pubblicato.
    /// </remarks>
    public string? Extension { get; init; } = "ipPhone";

    /// <summary>Telefono di ufficio, di solito il numero diretto.</summary>
    public string? WorkPhone { get; init; } = "telephoneNumber";

    /// <summary>Cellulare.</summary>
    public string? MobilePhone { get; init; } = "mobile";

    /// <summary>Posta elettronica.</summary>
    public string? Email { get; init; } = "mail";

    /// <summary>Ente o società.</summary>
    public string? Organization { get; init; } = "company";

    /// <summary>Reparto o ufficio.</summary>
    public string? Department { get; init; } = "department";

    /// <summary>Qualifica.</summary>
    public string? JobTitle { get; init; } = "title";

    /// <summary>
    /// Collocazione: edificio, sede o stanza.
    /// </summary>
    /// <remarks>
    /// <c>physicalDeliveryOfficeName</c> è il campo «Ufficio» della scheda generale
    /// di Active Directory, ed è l'unico che lo schema dedica a dove sta una persona.
    /// Va nel padiglione perché è lì che la rubrica cerca e pronuncia la collocazione:
    /// «dove si trova» è la domanda che un centralino riceve quanto «qual è il
    /// numero».
    /// </remarks>
    public string? Building { get; init; } = "physicalDeliveryOfficeName";

    /// <summary>
    /// Identificativo stabile della voce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>objectGUID</c> non cambia quando una persona viene spostata da un'unità
    /// organizzativa all'altra o cambia cognome, ed è quello che tiene preferiti e
    /// numeri rapidi attaccati alla voce giusta. Il nome distinto, al contrario,
    /// cambia proprio in quei casi.
    /// </para>
    /// <para>
    /// Se l'attributo non torna — OpenLDAP lo chiama <c>entryUUID</c> — si ripiega sul
    /// nome distinto, che è comunque unico nella directory.
    /// </para>
    /// </remarks>
    public string? EntryKey { get; init; } = "objectGUID";

    /// <summary>Data dell'ultima modifica nella directory.</summary>
    public string? UpdatedAt { get; init; } = "whenChanged";

    /// <summary>
    /// I nomi degli attributi da chiedere al server, senza ripetizioni.
    /// </summary>
    /// <remarks>
    /// Chiedere solo questi, e non tutti, conta per due ragioni: la quantità di dati
    /// che attraversa la rete e — più importante — il principio di minimizzazione. La
    /// directory di un ente contiene date di nascita, codici fiscali e indirizzi di
    /// casa che a un centralino non servono e che non devono arrivare sulla postazione
    /// nemmeno di passaggio.
    /// </remarks>
    public IReadOnlyList<string> RequestedAttributes()
    {
        string?[] tutti =
        [
            DisplayName, FirstName, LastName, Extension, WorkPhone, MobilePhone, Email,
            Organization, Department, JobTitle, Building, EntryKey, UpdatedAt,
        ];

        return [.. tutti
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>
/// Parametri del collegamento alla directory LDAP o Active Directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>La password non sta qui dentro.</b> Queste impostazioni finiscono in
/// <c>settings.json</c>, che è testo in chiaro sul disco della postazione: la
/// password vive nel deposito protetto di Windows, legata al server per cui è stata
/// inserita, e viene passata al momento dell'uso.
/// </para>
/// </remarks>
public sealed record LdapOptions
{
    /// <summary>
    /// Filtro predefinito per Active Directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prende due tipi di voce: gli utenti — persone, non account di computer o di
    /// servizio — e i contatti, che in Active Directory sono le persone esterne che
    /// l'ente pubblica nella propria rubrica. Degli utenti esclude quelli disattivati:
    /// la regola di confronto bit per bit sul campo <c>userAccountControl</c> è il
    /// modo standard di dire «senza il bit 2, account disabilitato».
    /// </para>
    /// <para>
    /// Pretende infine almeno un numero. Una voce senza recapiti non serve a un
    /// centralino, e farla tornare dal server per poi scartarla significa spostare
    /// sulla rete migliaia di schede inutili a ogni riallineamento.
    /// </para>
    /// </remarks>
    public const string ActiveDirectoryFilter =
        "(&(|(&(objectCategory=person)(objectClass=user)"
        + "(!(userAccountControl:1.2.840.113556.1.4.803:=2)))(objectClass=contact))"
        + "(|(telephoneNumber=*)(ipPhone=*)(mobile=*)))";

    /// <summary>Nome o indirizzo del server, oppure il nome del dominio.</summary>
    /// <remarks>
    /// Con Active Directory il nome del dominio — <c>ente.local</c> — basta: il
    /// sistema trova da sé un controller raggiungibile, e la rubrica non smette di
    /// riallinearsi il giorno in cui il server indicato a mano viene dismesso.
    /// </remarks>
    public string Server { get; init; } = string.Empty;

    /// <summary>Porta; zero per quella predefinita del tipo di protezione scelto.</summary>
    public int Port { get; init; }

    /// <summary>Come proteggere il collegamento.</summary>
    public LdapSecurity Security { get; init; } = LdapSecurity.Ldaps;

    /// <summary>Con quale identità presentarsi.</summary>
    public LdapBind Bind { get; init; } = LdapBind.Windows;

    /// <summary>Utenza dedicata, nella forma che la directory accetta.</summary>
    /// <remarks>
    /// Active Directory accetta il nome distinto, <c>utente@dominio</c> e
    /// <c>DOMINIO\utente</c>; le altre directory di solito solo il nome distinto.
    /// </remarks>
    public string? BindUser { get; init; }

    /// <summary>Chiave con cui la password è depositata nel deposito protetto.</summary>
    public string PasswordSecretKey { get; init; } = "contacts.ldap.password";

    /// <summary>Da dove cominciare a cercare, per esempio <c>OU=Personale,DC=ente,DC=local</c>.</summary>
    public string BaseDn { get; init; } = string.Empty;

    /// <summary>Quali voci prendere.</summary>
    public string Filter { get; init; } = ActiveDirectoryFilter;

    /// <summary>Quanto in profondità cercare.</summary>
    public LdapScope Scope { get; init; } = LdapScope.Subtree;

    /// <summary>Quale attributo riempie quale campo.</summary>
    public LdapAttributeMapping Attributes { get; init; } = new();

    /// <summary>
    /// Voci per pagina.
    /// </summary>
    /// <remarks>
    /// Active Directory non restituisce più di mille voci per richiesta, qualunque
    /// cosa si chieda: senza la lettura a pagine, un ente con milleduecento dipendenti
    /// ne vedrebbe in rubrica mille, e il motivo non comparirebbe da nessuna parte.
    /// Cinquecento sta largamente sotto quel tetto.
    /// </remarks>
    public int PageSize { get; init; } = 500;

    /// <summary>Secondi entro cui il server deve rispondere a ogni richiesta.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Ogni quanti minuti riallineare la copia locale. Zero per non farlo da sé.</summary>
    public int SyncIntervalMinutes { get; init; } = 60;

    /// <summary>Il collegamento è configurato quanto basta per tentarlo.</summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(BaseDn);

    /// <summary>La porta che verrà davvero usata.</summary>
    public int EffectivePort => Port > 0
        ? Port
        : Security == LdapSecurity.Ldaps ? 636 : 389;

    /// <summary>
    /// Descrive il collegamento senza rivelare la password né l'utenza.
    /// </summary>
    /// <remarks>
    /// Va nel registro diagnostico, che finisce nel pacchetto di assistenza.
    /// L'utenza non c'è perché in molte directory è il nome distinto di una persona —
    /// «CN=Mario Rossi,OU=Sistemi informativi» — cioè un dato personale che al
    /// registro non serve.
    /// </remarks>
    public string Describe()
    {
        var protezione = Security switch
        {
            LdapSecurity.Ldaps => "LDAPS",
            LdapSecurity.StartTls => "StartTLS",
            _ => "in chiaro",
        };

        var accesso = Bind switch
        {
            LdapBind.Windows => "utenza di Windows",
            LdapBind.Simple => "utenza dedicata",
            _ => "accesso anonimo",
        };

        return $"{Server}:{EffectivePort} ({protezione}, {accesso})";
    }
}
