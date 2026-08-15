namespace BlindPhone.Infrastructure;

/// <summary>Da dove BlindPhone prende i contatti della rubrica.</summary>
public enum ContactsMode
{
    /// <summary>Solo quello che è stato importato a mano da file.</summary>
    LocalFile,

    /// <summary>Da una tabella di Microsoft SQL Server, riallineata periodicamente.</summary>
    SqlServer,
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

    /// <summary>
    /// Campi della rubrica mostrati accanto ai contatti proposti sotto il campo di
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
/// Sono le stesse voci della procedura guidata dei file: numero, relativo,
/// descrizione, nome, cognome, reparto, padiglione, piano. Tenere gli stessi campi
/// significa che chi ha imparato a importare un file sa già collegare un server.
/// </remarks>
public sealed record ContactsColumnMapping
{
    /// <summary>Colonna dell'interno.</summary>
    public string? Extension { get; init; }

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
}
