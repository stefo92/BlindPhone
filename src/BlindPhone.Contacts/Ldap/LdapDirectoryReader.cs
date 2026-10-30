using System.DirectoryServices.Protocols;
using System.Net;

namespace BlindPhone.Contacts.Ldap;

/// <summary>
/// Una voce della directory, con i soli attributi chiesti.
/// </summary>
/// <param name="DistinguishedName">Nome distinto della voce.</param>
/// <param name="Attributes">
/// Valori di ogni attributo, già in forma di testo. Il confronto sui nomi non bada
/// alle maiuscole, come in LDAP.
/// </param>
public sealed record LdapEntry(
    string DistinguishedName,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes)
{
    /// <summary>Il primo valore non vuoto dell'attributo, già ripulito.</summary>
    /// <param name="attribute">Nome dell'attributo; <c>null</c> se non abbinato.</param>
    public string? First(string? attribute)
        => All(attribute).FirstOrDefault();

    /// <summary>Tutti i valori non vuoti dell'attributo, già ripuliti.</summary>
    /// <param name="attribute">Nome dell'attributo; <c>null</c> se non abbinato.</param>
    public IReadOnlyList<string> All(string? attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute)) return [];
        if (!Attributes.TryGetValue(attribute.Trim(), out var valori)) return [];

        return [.. valori.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())];
    }
}

/// <summary>
/// Chi fornisce le voci della directory.
/// </summary>
/// <remarks>
/// Esiste perché la parte che conta — quale attributo diventa quale campo, quale
/// numero si compone e quale finisce nelle note — si possa provare senza un server
/// acceso. La lettura vera sta in <see cref="LdapDirectoryReader"/>.
/// </remarks>
public interface ILdapEntryReader : IDisposable
{
    /// <summary>Legge le voci che il filtro seleziona.</summary>
    /// <param name="limit">Voci da leggere al massimo; <c>null</c> per tutte.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    IEnumerable<LdapEntry> ReadEntries(int? limit, CancellationToken cancellationToken);
}

/// <summary>
/// Un problema della sorgente LDAP che non è un errore del protocollo, già detto in
/// una frase che si può pronunciare.
/// </summary>
/// <param name="speech">Che cosa è successo e che cosa fare.</param>
public sealed class LdapSourceException(string speech) : Exception(speech);

/// <summary>
/// Legge le voci da un server LDAP o Active Directory, in sola lettura.
/// </summary>
/// <remarks>
/// <para>
/// <b>BlindPhone non scrive mai nella directory.</b> L'unica operazione che invia,
/// dopo l'autenticazione, è una ricerca con il filtro e gli attributi configurati.
/// </para>
/// <para>
/// <b>I rinvii ad altri server non si seguono.</b> Una ricerca dalla radice del
/// dominio fa rispondere ad Active Directory con dei rinvii verso le partizioni DNS e
/// i domini fidati; seguirli significa tentare collegamenti verso server che spesso
/// dalla postazione non sono raggiungibili, e aspettare il loro tempo massimo uno per
/// uno. Il riallineamento sembrerebbe bloccato, e lo sarebbe per minuti.
/// </para>
/// </remarks>
public sealed class LdapDirectoryReader : ILdapEntryReader
{
    private readonly LdapConnection _connection;
    private readonly LdapOptions _options;

    /// <summary>Apre il collegamento e si autentica.</summary>
    /// <param name="options">Parametri del collegamento.</param>
    /// <param name="password">Password dal deposito protetto, per l'utenza dedicata.</param>
    /// <exception cref="LdapException">Il server non risponde o rifiuta le credenziali.</exception>
    /// <exception cref="DirectoryOperationException">Il server rifiuta un'operazione.</exception>
    public LdapDirectoryReader(LdapOptions options, string? password)
    {
        _options = options;

        // Il nome puo' essere quello del dominio e non di un server: con Active
        // Directory il sistema trova da se' un controller raggiungibile.
        var identificativo = new LdapDirectoryIdentifier(
            options.Server.Trim(), options.EffectivePort, fullyQualifiedDnsHostName: false, connectionless: false);

        _connection = new LdapConnection(identificativo)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            AutoBind = false,
        };

        try
        {
            var sessione = _connection.SessionOptions;
            sessione.ProtocolVersion = 3;
            sessione.ReferralChasing = ReferralChasingOptions.None;

            if (options.Security == LdapSecurity.Ldaps) sessione.SecureSocketLayer = true;

            // StartTLS va chiesto prima dell'autenticazione: e' il punto in cui la
            // password smette di viaggiare in chiaro.
            if (options.Security == LdapSecurity.StartTls) sessione.StartTransportLayerSecurity(null);

            Bind(password);
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    private void Bind(string? password)
    {
        switch (_options.Bind)
        {
            case LdapBind.Windows:
                // Kerberos o NTLM con l'utenza che ha aperto BlindPhone: nessuna
                // password da conservare, ed e' la ragione per cui e' la scelta
                // predefinita su una postazione nel dominio.
                _connection.AuthType = AuthType.Negotiate;
                _connection.Bind();
                break;

            case LdapBind.Simple:
                // UNA PASSWORD VUOTA NON E' UNA PASSWORD SBAGLIATA.
                //
                // Per lo standard (RFC 4513, 5.1.2) un nome con la password vuota e'
                // un accesso anonimo, e molti server lo accettano senza dire niente:
                // il collegamento "riuscirebbe" con un'utenza diversa da quella
                // indicata, e la rubrica uscirebbe ridotta a cio' che la directory
                // mostra agli sconosciuti. Si ferma qui, con una frase che dice cosa
                // manca.
                if (string.IsNullOrEmpty(password))
                {
                    throw new LdapSourceException(
                        "Manca la password dell'utenza della directory: inseriscila nelle impostazioni della rubrica");
                }

                _connection.AuthType = AuthType.Basic;
                _connection.Bind(new NetworkCredential(_options.BindUser ?? string.Empty, password));
                break;

            default:
                _connection.AuthType = AuthType.Anonymous;
                _connection.Bind();
                break;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>La lettura è a pagine</b> perché Active Directory non restituisce più di
    /// mille voci per richiesta: senza, un ente più grande vedrebbe in rubrica le
    /// prime mille persone e nessun messaggio direbbe che mancano le altre.
    /// </para>
    /// <para>
    /// Il controllo delle pagine non è dichiarato indispensabile: un server che non
    /// lo conosce lo ignora e restituisce tutto in una volta, invece di rifiutare la
    /// ricerca. Ogni pagina ha il proprio tempo massimo, sia sul server sia qui.
    /// </para>
    /// </remarks>
    public IEnumerable<LdapEntry> ReadEntries(int? limit, CancellationToken cancellationToken)
    {
        var attributi = _options.Attributes.RequestedAttributes().ToArray();
        var tempo = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        var richiesta = new SearchRequest(
            _options.BaseDn.Trim(),
            _options.Filter.Trim(),
            _options.Scope == LdapScope.OneLevel ? SearchScope.OneLevel : SearchScope.Subtree,
            attributi)
        {
            TimeLimit = tempo,
        };

        var dimensione = limit is { } n ? Math.Clamp(n, 1, _options.PageSize) : _options.PageSize;
        var pagina = new PageResultRequestControl(dimensione) { IsCritical = false };
        richiesta.Controls.Add(pagina);

        var lette = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var risposta = (SearchResponse)_connection.SendRequest(richiesta, tempo);

            if (risposta.ResultCode is ResultCode.Referral or ResultCode.ReferralV2)
            {
                throw new LdapSourceException(
                    "La base della ricerca non sta su questo server, che rimanda a un altro dominio: "
                    + "controlla il server e la base della ricerca");
            }

            foreach (SearchResultEntry voce in risposta.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return Convert(voce);

                if (limit is { } massimo && ++lette >= massimo) yield break;
            }

            var seguito = risposta.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (seguito is null || seguito.Cookie is not { Length: > 0 }) yield break;

            pagina.Cookie = seguito.Cookie;
        }
    }

    /// <summary>Riduce una voce del server a testo.</summary>
    private static LdapEntry Convert(SearchResultEntry voce)
    {
        var attributi = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string nome in voce.Attributes.AttributeNames)
        {
            attributi[nome] = Values(nome, voce.Attributes[nome]);
        }

        return new LdapEntry(voce.DistinguishedName, attributi);
    }

    /// <summary>I valori di un attributo, in forma di testo.</summary>
    /// <remarks>
    /// <c>objectGUID</c> è binario: sedici byte che, letti come testo, darebbero una
    /// stringa diversa a seconda della codifica. Si converte nella forma canonica di
    /// un identificativo, che resta la stessa a ogni lettura.
    /// </remarks>
    private static string[] Values(string nome, DirectoryAttribute attributo)
    {
        try
        {
            if (string.Equals(nome, "objectGUID", StringComparison.OrdinalIgnoreCase))
            {
                return [.. attributo.GetValues(typeof(byte[]))
                    .Cast<byte[]>()
                    .Where(b => b.Length == 16)
                    .Select(b => new Guid(b).ToString("D"))];
            }

            return [.. attributo.GetValues(typeof(string)).Cast<string>()];
        }
        catch (ArgumentException)
        {
            // Un valore binario chiesto per sbaglio come testo: lo si perde, invece
            // di perdere la voce intera.
            return [];
        }
    }

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();
}
