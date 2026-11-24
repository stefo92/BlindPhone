using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace BlindPhone.Core.Services;

/// <summary>Tipo di un candidato ICE della postazione, RFC 8445 §5.1.1.</summary>
public enum IceCandidateKind
{
    /// <summary>Indirizzo di una scheda di rete della postazione, VPN comprese.</summary>
    Host,

    /// <summary>Indirizzo pubblico con cui il server STUN vede il socket audio.</summary>
    ServerReflexive,
}

/// <summary>Tipo del candidato del centralino su cui si è fermato l'audio.</summary>
public enum IceRemoteCandidateKind
{
    /// <summary>Indirizzo di una scheda del centralino.</summary>
    Host,

    /// <summary>Indirizzo pubblico del centralino visto da un server STUN.</summary>
    ServerReflexive,

    /// <summary>Indirizzo scoperto durante le verifiche, che il centralino non aveva annunciato.</summary>
    PeerReflexive,

    /// <summary>Indirizzo di un relè TURN del centralino.</summary>
    Relay,
}

/// <summary>
/// Un candidato ICE della postazione, come finisce nella riga <c>a=candidate</c>.
/// </summary>
/// <param name="Kind">Tipo del candidato.</param>
/// <param name="EndPoint">Indirizzo e porta da annunciare.</param>
/// <param name="Priority">Priorità calcolata come vuole la RFC 8445 §5.1.2.1.</param>
/// <param name="Foundation">Fondamento, uguale solo per candidati della stessa origine.</param>
/// <param name="Related">
/// Per un candidato riflesso, l'indirizzo locale da cui è nato; <c>null</c> per un candidato host.
/// </param>
public sealed record IceLocalCandidate(
    IceCandidateKind Kind,
    IPEndPoint EndPoint,
    uint Priority,
    string Foundation,
    IPEndPoint? Related = null)
{
    /// <summary>
    /// Il valore dell'attributo <c>candidate</c>, senza il prefisso <c>a=candidate:</c>.
    /// </summary>
    /// <remarks>
    /// Componente 1 sempre: l'RTCP viaggia sullo stesso socket dell'RTP, vedi
    /// <see cref="IceCandidatePlan"/>. La grammatica è quella della RFC 8839 §5.1.
    /// </remarks>
    public string ToSdpValue()
    {
        var indirizzo = MediaAddressPolicy.Normalize(EndPoint.Address).ToString();
        var porta = EndPoint.Port.ToString(CultureInfo.InvariantCulture);
        var priorita = Priority.ToString(CultureInfo.InvariantCulture);

        if (Kind == IceCandidateKind.Host || Related is null)
            return $"{Foundation} {IceCandidatePlan.RtpComponent} udp {priorita} {indirizzo} {porta} typ host";

        return $"{Foundation} {IceCandidatePlan.RtpComponent} udp {priorita} {indirizzo} {porta} typ srflx "
               + $"raddr {MediaAddressPolicy.Normalize(Related.Address)} rport {Related.Port.ToString(CultureInfo.InvariantCulture)}";
    }
}

/// <summary>
/// Sceglie quali candidati ICE annunciare al centralino, e in che ordine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il caso per cui esiste è il centralino raggiunto in VPN.</b> Senza ICE la
/// postazione scrive nella descrizione di sessione un indirizzo solo, e deve indovinarlo:
/// <see cref="MediaAddressPolicy"/> lo fa bene nei casi che conosce, ma con un tunnel
/// che traduce gli indirizzi, con un centralino che ne annuncia uno pubblico mentre la
/// voce passa dal tunnel, o con due VPN accese, l'indirizzo giusto non si deduce
/// dall'esterno. Con ICE la postazione li annuncia tutti, e sono le verifiche STUN fra
/// le due parti a dire quale funziona davvero.
/// </para>
/// <para>
/// <b>Tutte le schede, tunnel compresi.</b> La libreria SIP, lasciata fare, annuncia la
/// sola scheda che porta a Internet: è la raccomandazione dei browser, che proteggono
/// la riservatezza di chi naviga, e con una VPN parziale è proprio la scheda sbagliata.
/// Qui si annunciano gli indirizzi di tutte le schede accese, tolti quelli che non
/// possono portare la voce da nessuna parte: il ritorno su se stessi e gli indirizzi
/// che Windows si assegna da solo quando non trova un server DHCP.
/// </para>
/// <para>
/// <b>L'indirizzo verso il centralino ha la priorità più alta.</b> È quello che la
/// postazione scrive comunque nelle righe <c>c=</c> e <c>m=</c>, cioè il candidato
/// predefinito, e deve comparire anche fra i candidati: un centralino che non lo trova
/// conclude che in mezzo c'è un apparato che riscrive le descrizioni di sessione e
/// abbandona ICE (RFC 8839 §5.1, <c>ice-mismatch</c>).
/// </para>
/// <para>
/// <b>Un solo componente.</b> L'RTCP viaggia sul socket dell'RTP (<c>a=rtcp-mux</c>,
/// RFC 5761): l'agente ICE della libreria verifica un socket solo, e un secondo
/// componente richiederebbe un secondo agente per un flusso — i rapporti RTCP — di cui
/// una chiamata può fare a meno.
/// </para>
/// </remarks>
public static class IceCandidatePlan
{
    /// <summary>Componente RTP, l'unico annunciato.</summary>
    public const int RtpComponent = 1;

    /// <summary>Preferenza di tipo dei candidati host, RFC 8445 §5.1.2.2.</summary>
    public const int HostTypePreference = 126;

    /// <summary>Preferenza di tipo dei candidati riflessi dal server, RFC 8445 §5.1.2.2.</summary>
    public const int ServerReflexiveTypePreference = 100;

    /// <summary>Preferenza locale massima, riservata all'indirizzo verso il centralino.</summary>
    public const int TopLocalPreference = 65535;

    /// <summary>
    /// Priorità di un candidato, RFC 8445 §5.1.2.1:
    /// <c>2^24 × tipo + 2^8 × preferenza locale + (256 − componente)</c>.
    /// </summary>
    /// <param name="kind">Tipo del candidato.</param>
    /// <param name="localPreference">Preferenza locale, da 0 a 65535.</param>
    public static uint Priority(IceCandidateKind kind, int localPreference)
    {
        var tipo = kind == IceCandidateKind.Host ? HostTypePreference : ServerReflexiveTypePreference;
        var locale = Math.Clamp(localPreference, 0, TopLocalPreference);

        return ((uint)tipo << 24) + ((uint)locale << 8) + (uint)(256 - RtpComponent);
    }

    /// <summary>
    /// Dice se un indirizzo della postazione può fare da candidato host.
    /// </summary>
    /// <remarks>
    /// Fuori il ritorno su se stessi (RFC 8445 §5.1.1.1 lo esclude esplicitamente), gli
    /// indirizzi senza valore, quelli locali al collegamento — 169.254.0.0/16 per IPv4,
    /// fe80::/10 per IPv6 — e gli IPv6 di sito, ormai abbandonati. Le reti private
    /// restano tutte: sono proprio quelle di sede e delle VPN.
    /// </remarks>
    public static bool IsUsableHostAddress(IPAddress address)
    {
        var a = MediaAddressPolicy.Normalize(address);

        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return false;

        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return !(b[0] == 169 && b[1] == 254) && b[0] != 0 && b[0] < 224;
        }

        return a.AddressFamily == AddressFamily.InterNetworkV6
               && !a.IsIPv6LinkLocal
               && !a.IsIPv6SiteLocal
               && !a.IsIPv6Multicast;
    }

    /// <summary>
    /// Costruisce l'elenco dei candidati da annunciare, dal più preferito al meno.
    /// </summary>
    /// <param name="hostAddresses">Indirizzi delle schede di rete accese.</param>
    /// <param name="preferredHost">
    /// Indirizzo della postazione scritto nelle righe <c>c=</c> e <c>m=</c>, se è un
    /// indirizzo locale: diventa il candidato con la priorità più alta.
    /// </param>
    /// <param name="port">Porta del socket audio.</param>
    /// <param name="serverReflexive">
    /// Indirizzo pubblico scoperto con STUN per il socket audio, se ce n'è uno.
    /// </param>
    /// <remarks>
    /// Si annuncia una sola famiglia di indirizzi, quella del candidato predefinito: un
    /// centralino IPv4 non verifica candidati IPv6, e ogni candidato in più è una coppia
    /// in più da provare prima di arrivare a quella buona.
    /// </remarks>
    public static IReadOnlyList<IceLocalCandidate> Build(
        IEnumerable<IPAddress> hostAddresses,
        IPAddress? preferredHost,
        int port,
        IPEndPoint? serverReflexive)
    {
        var preferito = preferredHost is null ? null : MediaAddressPolicy.Normalize(preferredHost);
        if (preferito is not null && !IsUsableHostAddress(preferito)) preferito = null;

        var schede = hostAddresses
            .Select(MediaAddressPolicy.Normalize)
            .Where(IsUsableHostAddress)
            .ToList();

        AddressFamily? famiglia = preferito?.AddressFamily;

        if (famiglia is null && serverReflexive is not null)
            famiglia = MediaAddressPolicy.Normalize(serverReflexive.Address).AddressFamily;

        famiglia ??= schede.Any(a => a.AddressFamily == AddressFamily.InterNetwork)
            ? AddressFamily.InterNetwork
            : schede.FirstOrDefault()?.AddressFamily;

        var ordinati = new List<IPAddress>();
        if (preferito is not null) ordinati.Add(preferito);

        foreach (var a in schede)
        {
            if (a.AddressFamily == famiglia && !ordinati.Contains(a)) ordinati.Add(a);
        }

        var candidati = new List<IceLocalCandidate>(ordinati.Count + 1);

        for (var i = 0; i < ordinati.Count; i++)
        {
            candidati.Add(new IceLocalCandidate(
                IceCandidateKind.Host,
                new IPEndPoint(ordinati[i], port),
                Priority(IceCandidateKind.Host, TopLocalPreference - i),
                $"H{(i + 1).ToString(CultureInfo.InvariantCulture)}"));
        }

        if (serverReflexive is not null && ordinati.Count > 0)
        {
            var pubblico = new IPEndPoint(MediaAddressPolicy.Normalize(serverReflexive.Address), serverReflexive.Port);

            // Un indirizzo pubblico uguale a uno locale non e' un candidato in piu': e'
            // la postazione senza NAT, e la RFC 8445 §5.1.3 chiede di toglierlo.
            if (pubblico.AddressFamily == famiglia
                && !candidati.Any(c => c.EndPoint.Address.Equals(pubblico.Address) && c.EndPoint.Port == pubblico.Port))
            {
                candidati.Add(new IceLocalCandidate(
                    IceCandidateKind.ServerReflexive,
                    pubblico,
                    Priority(IceCandidateKind.ServerReflexive, TopLocalPreference),
                    "S1",
                    new IPEndPoint(ordinati[0], port)));
            }
        }

        return candidati;
    }
}

/// <summary>
/// Il ruolo della postazione nelle verifiche ICE.
/// </summary>
/// <remarks>
/// Conta più di quanto sembri: è l'agente che controlla a scegliere la coppia su cui
/// passerà la voce. Se entrambe le parti si credono controllate nessuna sceglie, le
/// verifiche riescono e la chiamata resta muta; se entrambe si credono al comando,
/// ciascuna sceglie la propria.
/// </remarks>
public static class IceRoles
{
    /// <summary>
    /// Dice se la postazione deve controllare la scelta della coppia, RFC 8445 §6.1.1.
    /// </summary>
    /// <param name="generatedInitialOffer">
    /// Se la prima descrizione di sessione della chiamata l'ha scritta la postazione,
    /// cioè se la chiamata è uscente.
    /// </param>
    /// <param name="remoteIsLite">
    /// Se il centralino si dichiara <c>a=ice-lite</c>: risponde alle verifiche ma non ne
    /// fa, e non sceglie niente.
    /// </param>
    /// <remarks>
    /// La postazione è sempre un agente completo. Con un centralino completo controlla
    /// chi ha offerto per primo; con un centralino «lite» controlla la postazione, sempre,
    /// perché l'altro non sa farlo.
    /// </remarks>
    public static bool IsControlling(bool generatedInitialOffer, bool remoteIsLite)
        => remoteIsLite || generatedInitialOffer;
}

/// <summary>Dove sono arrivate le verifiche ICE di una chiamata.</summary>
public enum IceProgress
{
    /// <summary>La postazione ha annunciato i suoi candidati e aspetta quelli del centralino.</summary>
    Waiting,

    /// <summary>Il centralino ha risposto senza ICE: vale l'indirizzo della sua descrizione di sessione.</summary>
    NotUsedByPeer,

    /// <summary>Verifiche in corso.</summary>
    Checking,

    /// <summary>Una coppia è stata verificata e scelta: l'audio passa di lì.</summary>
    Connected,

    /// <summary>Nessuna coppia ha risposto alle verifiche.</summary>
    Failed,

    /// <summary>La coppia scelta ha smesso di rispondere ai controlli di consenso.</summary>
    Lost,
}

/// <summary>
/// Lo stato ICE di una chiamata, per il registro e per la scheda Diagnostica.
/// </summary>
/// <param name="Progress">Dove sono arrivate le verifiche.</param>
/// <param name="Controlling">Se la postazione controlla la scelta della coppia.</param>
/// <param name="RemoteCandidates">Quanti candidati del centralino sono in verifica.</param>
/// <param name="Remote">Indirizzo del centralino sulla coppia scelta, quando c'è.</param>
/// <param name="RemoteKind">Tipo del candidato del centralino sulla coppia scelta.</param>
/// <param name="RemoteIsLite">Se il centralino si è dichiarato <c>ice-lite</c>.</param>
public sealed record IceStatus(
    IceProgress Progress,
    bool Controlling = false,
    int RemoteCandidates = 0,
    IPEndPoint? Remote = null,
    IceRemoteCandidateKind? RemoteKind = null,
    bool RemoteIsLite = false)
{
    /// <summary>Candidati annunciati, risposta non ancora arrivata.</summary>
    public static IceStatus Waiting { get; } = new(IceProgress.Waiting);

    /// <summary>Il centralino non usa ICE.</summary>
    public static IceStatus NotUsedByPeer { get; } = new(IceProgress.NotUsedByPeer);

    /// <summary>Lo stato detto per intero, come si legge nella scheda Diagnostica.</summary>
    public string Describe() => Progress switch
    {
        IceProgress.Waiting => "candidati annunciati, in attesa di quelli del centralino",
        IceProgress.NotUsedByPeer
            => "il centralino non usa ICE: l'audio va all'indirizzo scritto nella sua descrizione di sessione",
        IceProgress.Checking => $"verifica dei percorsi in corso su {Candidati()}, {Ruolo()}",
        IceProgress.Connected when Remote is not null
            => $"percorso verificato: l'audio va a {MediaAddressPolicy.Describe(Remote)}, {Tipo()}, {Ruolo()}",
        IceProgress.Connected => $"percorso verificato, {Ruolo()}",
        IceProgress.Lost when Remote is not null
            => $"il percorso verso {MediaAddressPolicy.Describe(Remote)} ha smesso di rispondere alle verifiche di consenso",
        IceProgress.Lost => "il percorso verificato ha smesso di rispondere alle verifiche di consenso",
        _ => $"nessun percorso ha risposto alle verifiche su {Candidati()}: "
             + "l'audio resta sull'indirizzo scritto nella descrizione di sessione del centralino",
    };

    private string Candidati() => RemoteCandidates == 1
        ? "un candidato del centralino"
        : $"{RemoteCandidates.ToString(CultureInfo.CurrentCulture)} candidati del centralino";

    private string Ruolo()
    {
        var ruolo = Controlling ? "la postazione sceglie la coppia" : "la coppia la sceglie il centralino";
        return RemoteIsLite ? $"{ruolo} (centralino ICE lite)" : ruolo;
    }

    private string Tipo() => RemoteKind switch
    {
        IceRemoteCandidateKind.Host => "indirizzo proprio del centralino",
        IceRemoteCandidateKind.ServerReflexive => "indirizzo pubblico del centralino",
        IceRemoteCandidateKind.PeerReflexive => "indirizzo scoperto durante le verifiche",
        IceRemoteCandidateKind.Relay => "relè del centralino",
        _ => "candidato del centralino",
    };
}

/// <summary>Letture minime di una descrizione di sessione ancora in forma di testo.</summary>
public static class IceSdp
{
    /// <summary>
    /// Dice se una descrizione di sessione porta le credenziali ICE.
    /// </summary>
    /// <param name="body">Corpo SDP, per esempio quello di un INVITE in arrivo.</param>
    /// <remarks>
    /// Basta la riga <c>a=ice-ufrag</c>: senza, le verifiche non si possono autenticare, e
    /// per la RFC 8839 la controparte non sta usando ICE. Serve a decidere, prima ancora
    /// di aprire il socket audio, se una chiamata entrante merita l'agente ICE.
    /// </remarks>
    public static bool DeclaresIce(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        foreach (var riga in body.Split('\n'))
        {
            if (riga.Trim().StartsWith("a=ice-ufrag:", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
