using System.Net;
using System.Net.Sockets;
using BlindPhone.Telephony;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.SIP;

namespace BlindPhone.Telephony;

/// <summary>Un'occorrenza di un record DNS SRV.</summary>
/// <param name="Host">Nome del server indicato dal record.</param>
/// <param name="Port">Porta su cui il servizio ascolta.</param>
/// <param name="Priority">Priorita': si prova prima il valore piu' basso.</param>
/// <param name="Weight">Peso, per ripartire il carico fra server di pari priorita'.</param>
public readonly record struct SrvTarget(string Host, int Port, int Priority, int Weight);

/// <summary>Le due domande DNS che servono a trovare un server SIP.</summary>
/// <remarks>
/// Separate dal resolver perche' la rete non si collauda: con questa interfaccia il
/// comportamento (ordine di prova, ripiego, porte predefinite) si verifica con
/// risposte scritte a mano.
/// </remarks>
public interface IDnsLookup
{
    /// <summary>Interroga i record SRV di un nome, per esempio <c>_sip._udp.proxy.ente.it</c>.</summary>
    Task<IReadOnlyList<SrvTarget>> QuerySrvAsync(string name, CancellationToken cancellationToken);

    /// <summary>Risolve un nome in indirizzi IP (A e AAAA).</summary>
    Task<IReadOnlyList<IPAddress>> QueryAddressesAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IDnsLookup"/> sui server DNS del sistema.
/// </summary>
public sealed class SystemDnsLookup : IDnsLookup
{
    private readonly LookupClient _client = new(new LookupClientOptions { UseCache = true, Timeout = TimeSpan.FromSeconds(3), Retries = 1 });

    /// <inheritdoc />
    public async Task<IReadOnlyList<SrvTarget>> QuerySrvAsync(string name, CancellationToken cancellationToken)
    {
        var risposta = await _client.QueryAsync(name, QueryType.SRV, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return [.. risposta.Answers.OfType<SrvRecord>()
            .Select(r => new SrvTarget(r.Target.Value.TrimEnd('.'), r.Port, r.Priority, r.Weight))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> QueryAddressesAsync(string host, CancellationToken cancellationToken)
    {
        // Il resolver di sistema, non DnsClient: rispetta il file hosts e i suffissi di
        // ricerca, che su una rete aziendale sono spesso l'unico modo di raggiungere un
        // nome interno.
        var indirizzi = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        // IPv4 per primo: i proxy degli operatori italiani sono raggiungibili su IPv4,
        // e una postazione con un IPv6 locale ma senza rotta perderebbe tempo.
        return [.. indirizzi
            .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];
    }
}

/// <summary>
/// Trasforma l'indirizzo di un server SIP — il proxy in uscita, tipicamente — in un
/// elenco ordinato di indirizzi IP da provare, seguendo RFC 3263.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perche' esiste.</b> Alcuni operatori, Telecom Italia/TIM fra questi, non danno un
/// indirizzo del proxy in uscita ma un <i>nome di servizio</i>: l'indirizzo vero e la
/// porta stanno in un record DNS SRV (<c>_sip._udp.nome</c>), spesso con piu' server di
/// priorita' diversa per il ripiego. Una postazione che sa solo leggere <c>IP:porta</c>
/// non si registra mai su quella linea.
/// </para>
/// <para>
/// <b>Regole</b> (RFC 3263 §4): un indirizzo IP, o un nome con porta esplicita, non si
/// interroga — si usa com'e'. Un nome senza porta si cerca come SRV per il trasporto
/// scelto (<c>_sip._udp</c>, <c>_sip._tcp</c>, <c>_sips._tcp</c> per TLS); i server si
/// provano per priorita' crescente e, a pari priorita', in ordine casuale pesato. Se
/// non esiste alcun SRV si ricade sul nome come host semplice, porta 5060 (5061 in TLS).
/// </para>
/// </remarks>
public sealed class SipServerResolver
{
    private const int MaxCandidates = 8;

    private readonly IDnsLookup _dns;
    private readonly Random _random;
    private readonly ILogger _log;

    /// <summary>Crea il resolver.</summary>
    /// <param name="dns">Sorgente DNS. Di norma quella del sistema.</param>
    /// <param name="random">Sorgente casuale per il peso dei record SRV; fissabile nei test.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SipServerResolver(IDnsLookup? dns = null, Random? random = null, ILogger? logger = null)
    {
        _dns = dns ?? new SystemDnsLookup();
        _random = random ?? Random.Shared;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Risolve un indirizzo di server nell'elenco di endpoint da provare, in ordine.
    /// </summary>
    /// <param name="server">
    /// <c>host</c>, <c>host:porta</c>, <c>IP</c>, <c>IP:porta</c>, <c>[IPv6]:porta</c>, con
    /// o senza prefisso <c>sip:</c> e parametro <c>;transport=</c>.
    /// </param>
    /// <param name="defaultTransport">Trasporto dell'account, se l'indirizzo non ne dichiara uno.</param>
    /// <param name="cancellationToken">Annullamento.</param>
    /// <returns>Gli endpoint da provare, il migliore per primo. Vuoto se il nome non si risolve.</returns>
    public async Task<IReadOnlyList<SIPEndPoint>> ResolveAsync(
        string server,
        SipTransportKind defaultTransport,
        CancellationToken cancellationToken)
    {
        if (!TryParse(server, defaultTransport, out var host, out var port, out var transport)) return [];

        var protocol = ToProtocol(transport);

        // Indirizzo IP: nessuna domanda al DNS.
        if (IPAddress.TryParse(host, out var literal))
        {
            return [new SIPEndPoint(protocol, literal, port ?? DefaultPort(transport))];
        }

        if (port is { } explicitPort)
        {
            return await ResolveHostAsync(host, explicitPort, protocol, cancellationToken).ConfigureAwait(false);
        }

        var srvName = $"{ServicePrefix(transport)}.{host}";
        IReadOnlyList<SrvTarget> records;

        try
        {
            records = await _dns.QuerySrvAsync(srvName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Interrogazione SRV {Nome} non riuscita: si prova il nome come host semplice", srvName);
            records = [];
        }

        var ordered = Order(records, _random);

        if (ordered.Count == 0)
        {
            _log.LogInformation("Nessun record SRV per {Nome}: si usa {Host} sulla porta {Porta}",
                srvName, host, DefaultPort(transport));

            return await ResolveHostAsync(host, DefaultPort(transport), protocol, cancellationToken)
                .ConfigureAwait(false);
        }

        var result = new List<SIPEndPoint>();

        foreach (var target in ordered)
        {
            var endpoints = await ResolveHostAsync(target.Host, target.Port, protocol, cancellationToken)
                .ConfigureAwait(false);

            _log.LogInformation(
                "SRV {Nome}: {Host}:{Porta} (priorita' {Priorita}, peso {Peso}) -> {Indirizzi}",
                srvName, target.Host, target.Port, target.Priority, target.Weight,
                endpoints.Count == 0 ? "non risolvibile" : string.Join(", ", endpoints));

            result.AddRange(endpoints);
            if (result.Count >= MaxCandidates) break;
        }

        return result;
    }

    /// <summary>
    /// Ordina i record SRV come prescrive RFC 2782: priorita' crescente e, a pari
    /// priorita', estrazione casuale pesata.
    /// </summary>
    /// <remarks>
    /// Un record con target "." significa "servizio non offerto" e viene scartato. I record
    /// di peso zero restano nell'elenco: hanno una probabilita' minima, non nulla.
    /// </remarks>
    public static IReadOnlyList<SrvTarget> Order(IEnumerable<SrvTarget> records, Random random)
    {
        var result = new List<SrvTarget>();

        var groups = records
            .Where(r => !string.IsNullOrWhiteSpace(r.Host) && r.Host != ".")
            .GroupBy(r => r.Priority)
            .OrderBy(g => g.Key);

        foreach (var group in groups)
        {
            var rest = group.ToList();

            while (rest.Count > 0)
            {
                // Un peso zero vale 1 su 'somma+1': come nell'RFC, pesa pochissimo
                // ma non e' escluso.
                var total = rest.Sum(r => r.Weight + 1);
                var pick = random.Next(total);
                var index = 0;

                while (pick >= rest[index].Weight + 1)
                {
                    pick -= rest[index].Weight + 1;
                    index++;
                }

                result.Add(rest[index]);
                rest.RemoveAt(index);
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<SIPEndPoint>> ResolveHostAsync(
        string host,
        int port,
        SIPProtocolsEnum protocol,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return [new SIPEndPoint(protocol, literal, port)];

        try
        {
            var addresses = await _dns.QueryAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return [.. addresses.Select(a => new SIPEndPoint(protocol, a, port))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Risoluzione del nome {Host} non riuscita", host);
            return [];
        }
    }

    /// <summary>Scompone l'indirizzo nei suoi pezzi. Falso se e' vuoto o illeggibile.</summary>
    internal static bool TryParse(
        string server,
        SipTransportKind defaultTransport,
        out string host,
        out int? port,
        out SipTransportKind transport)
    {
        host = string.Empty;
        port = null;
        transport = defaultTransport;

        if (string.IsNullOrWhiteSpace(server)) return false;

        var text = server.Trim();

        if (text.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            transport = SipTransportKind.Tls;
            text = text[5..];
        }
        else if (text.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }

        var semicolon = text.IndexOf(';');

        if (semicolon >= 0)
        {
            foreach (var parameter in text[(semicolon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = parameter.Split('=', 2);

                if (pair.Length == 2 && pair[0].Trim().Equals("transport", StringComparison.OrdinalIgnoreCase))
                {
                    transport = pair[1].Trim().ToLowerInvariant() switch
                    {
                        "tcp" => SipTransportKind.Tcp,
                        "tls" => SipTransportKind.Tls,
                        "udp" => SipTransportKind.Udp,
                        _ => transport,
                    };
                }
            }

            text = text[..semicolon];
        }

        text = text.Trim().TrimEnd('/');

        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) return false;

            host = text[1..close];
            if (close + 1 < text.Length && text[close + 1] == ':' && !TryPort(text[(close + 2)..], out port)) return false;
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':');
            host = text[..colon];
            if (!TryPort(text[(colon + 1)..], out port)) return false;
        }
        else
        {
            // Zero due punti: nome o IPv4. Piu' di uno: IPv6 senza parentesi, senza porta.
            host = text;
        }

        host = host.Trim().TrimEnd('.');
        return host.Length > 0;
    }

    private static bool TryPort(string text, out int? port)
    {
        port = null;
        if (!int.TryParse(text, out var value) || value is < 1 or > 65535) return false;

        port = value;
        return true;
    }

    private static string ServicePrefix(SipTransportKind transport) => transport switch
    {
        SipTransportKind.Tcp => "_sip._tcp",
        SipTransportKind.Tls => "_sips._tcp",
        _ => "_sip._udp",
    };

    private static int DefaultPort(SipTransportKind transport) => transport == SipTransportKind.Tls ? 5061 : 5060;

    private static SIPProtocolsEnum ToProtocol(SipTransportKind transport) => transport switch
    {
        SipTransportKind.Tcp => SIPProtocolsEnum.tcp,
        SipTransportKind.Tls => SIPProtocolsEnum.tls,
        _ => SIPProtocolsEnum.udp,
    };
}
