using Microsoft.Data.SqlClient;

namespace BlindPhone.Contacts.SqlServer;

/// <summary>Come autenticarsi al server.</summary>
public enum SqlServerAuthentication
{
    /// <summary>Con l'utenza di Windows già connessa alla postazione.</summary>
    Windows,

    /// <summary>Con un'utenza di SQL Server, nome e password.</summary>
    SqlLogin,
}

/// <summary>
/// Parametri del collegamento alla rubrica su Microsoft SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// <b>La password non sta qui dentro.</b> Queste impostazioni finiscono in
/// <c>settings.json</c>, che è testo in chiaro sul disco della postazione: la
/// password vive nel deposito protetto di Windows e viene passata al momento
/// dell'uso, come già accade per il server degli aggiornamenti.
/// </para>
/// </remarks>
public sealed record SqlServerOptions
{
    /// <summary>Nome o indirizzo del server, con l'istanza se non è quella predefinita.</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>Nome della banca dati che contiene la rubrica.</summary>
    public string Database { get; init; } = string.Empty;

    /// <summary>Come autenticarsi.</summary>
    public SqlServerAuthentication Authentication { get; init; } = SqlServerAuthentication.Windows;

    /// <summary>Utenza di SQL Server, quando l'autenticazione non è quella di Windows.</summary>
    public string? Username { get; init; }

    /// <summary>Chiave con cui la password è depositata nel deposito protetto.</summary>
    public string PasswordSecretKey { get; init; } = "contacts.sqlserver.password";

    /// <summary>Tabella o vista da cui leggere i contatti.</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>Corrispondenza fra colonne del server e campi del contatto.</summary>
    public CsvColumnMapping Mapping { get; init; } = new();

    /// <summary>
    /// Accetta il certificato del server anche se non verificabile.
    /// </summary>
    /// <remarks>
    /// Molte installazioni interne usano il certificato autofirmato che SQL Server
    /// genera da sé. Resta un'opzione esplicita e non un valore predefinito: chi la
    /// attiva sta dichiarando di fidarsi della propria rete, e va che lo sappia.
    /// </remarks>
    public bool TrustServerCertificate { get; init; }

    /// <summary>Secondi entro cui il collegamento deve stabilirsi.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>Ogni quanti minuti riallineare la copia locale. Zero per non farlo da sé.</summary>
    public int SyncIntervalMinutes { get; init; } = 60;

    /// <summary>Il collegamento è configurato quanto basta per tentarlo.</summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Database);

    /// <summary>
    /// Compone la stringa di collegamento.
    /// </summary>
    /// <param name="password">Password letta dal deposito protetto, se serve.</param>
    /// <remarks>
    /// <para>
    /// <b>Il traffico è cifrato per impostazione predefinita.</b> Fra la postazione e
    /// il server passano nomi, numeri interni e reparti di persone identificabili: è
    /// un trattamento di dati personali, e su una rete di ente vale la pena spendere
    /// il costo della cifratura anche quando la rete è considerata sicura.
    /// </para>
    /// <para>
    /// <b>Il nome dell'applicazione viaggia con la connessione.</b> Non è cortesia:
    /// quando l'amministratore del database vede una sessione che non riconosce, il
    /// nome è ciò che gli evita di chiuderla per prudenza.
    /// </para>
    /// </remarks>
    public string BuildConnectionString(string? password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            ConnectTimeout = ConnectTimeoutSeconds,
            Encrypt = true,
            TrustServerCertificate = TrustServerCertificate,
            ApplicationName = "BlindPhone",

            // Sola lettura dichiarata: su un gruppo di disponibilita' AlwaysOn il
            // server puo' cosi' mandarci su una replica secondaria invece che sul
            // nodo che regge il gestionale in produzione.
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };

        if (Authentication == SqlServerAuthentication.Windows)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = Username ?? string.Empty;
            builder.Password = password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Descrive il collegamento senza rivelare la password.
    /// </summary>
    /// <remarks>
    /// Va nel registro diagnostico, che finisce nel pacchetto di assistenza: la
    /// stringa di collegamento intera non ci deve mai arrivare.
    /// </remarks>
    public string Describe()
    {
        var accesso = Authentication == SqlServerAuthentication.Windows
            ? "utenza di Windows"
            : $"utenza {Username}";

        return $"{Server}/{Database} ({accesso})";
    }
}
