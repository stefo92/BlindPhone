using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace BlindPhone.Core.Services;

/// <summary>
/// Quale indirizzo annunciare al centralino per ricevere l'audio, e perché.
/// </summary>
/// <param name="PublicEndPoint">
/// Indirizzo e porta pubblici da scrivere nella descrizione di sessione, oppure
/// <c>null</c> se va annunciato l'indirizzo della postazione.
/// </param>
/// <param name="Reason">La ragione della scelta, detta per intero.</param>
public sealed record MediaAddressDecision(IPEndPoint? PublicEndPoint, string Reason)
{
    /// <summary>Se la descrizione di sessione porta l'indirizzo pubblico.</summary>
    public bool UsesPublicAddress => PublicEndPoint is not null;
}

/// <summary>
/// Decide se scrivere nella descrizione di sessione l'indirizzo pubblico scoperto con
/// STUN al posto di quello della postazione.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il guasto che risolve è l'audio a senso unico dal lavoro agile.</b> Da casa la
/// postazione sta dietro il router e il suo indirizzo è privato, per esempio
/// 192.168.1.20: è quello che finisce nelle righe <c>c=</c> e <c>m=</c> dell'offerta, e
/// il centralino manda la voce lì, cioè da nessuna parte. La voce dell'operatore invece
/// arriva, perché esce dal router. L'operatore sente silenzio, l'interlocutore lo sente
/// benissimo.
/// </para>
/// <para>
/// <b>Riscrivere a sproposito rompe quello che oggi funziona,</b> ed è il motivo per cui
/// la regola è più lunga di «se c'è STUN usa l'indirizzo pubblico». In sede il
/// centralino è sulla stessa rete e un indirizzo pubblico lo manderebbe a fare il giro
/// di Internet; con una VPN parziale il centralino si raggiunge dal tunnel mentre il
/// server STUN dalla linea di casa, e l'indirizzo che il server vede non vale per il
/// centralino. In tutti e due i casi l'indirizzo giusto è quello della postazione. Il
/// commento della libreria SIP sullo stesso argomento lo dice senza mezzi termini:
/// toccare l'indirizzo dell'SDP causa più guai di quanti ne risolva, se lo si fa sempre.
/// </para>
/// <para>
/// Ogni esito ha la sua ragione scritta in italiano, perché finisce nella scheda
/// Diagnostica: chi indaga una chiamata muta deve poter leggere non solo che cosa è
/// stato annunciato, ma perché.
/// </para>
/// </remarks>
public static class MediaAddressPolicy
{
    /// <summary>
    /// Sceglie l'indirizzo da annunciare.
    /// </summary>
    /// <param name="stun">
    /// Esito dell'interrogazione STUN fatta dal socket audio della chiamata, oppure
    /// <c>null</c> se l'account non ha un server STUN.
    /// </param>
    /// <param name="peer">
    /// Indirizzo del centralino: quello da cui arriva l'offerta sulle chiamate entranti,
    /// quello a cui si è registrati sulle uscenti. <c>null</c> se non è ancora noto.
    /// </param>
    /// <param name="localTowardPeer">Indirizzo della postazione sul collegamento verso il centralino.</param>
    /// <param name="localTowardStun">Indirizzo della postazione sul collegamento verso il server STUN.</param>
    public static MediaAddressDecision Decide(
        StunBindingResult? stun,
        IPAddress? peer,
        IPAddress? localTowardPeer,
        IPAddress? localTowardStun)
    {
        const string Locale = "si annuncia l'indirizzo della postazione";

        if (stun is null) return Keep($"server STUN non configurato: {Locale}");

        if (peer is null) return Keep($"centralino non ancora individuato: {Locale}");

        var centralino = Normalize(peer);

        // Prima di tutto la rete interna: e' il caso della sede e della VPN, cioe' di
        // tutte le postazioni che oggi funzionano. Nessuna risposta STUN deve poterle
        // cambiare, e per questo il controllo viene prima di guardarla.
        if (IsInternal(centralino))
            return Keep($"centralino in rete interna o raggiunto in VPN, l'indirizzo pubblico non servirebbe: {Locale}");

        if (stun.Outcome != StunOutcome.Mapped || stun.Mapped is null)
            return Keep($"{stun.Describe()}: {Locale}");

        var pubblico = new IPEndPoint(Normalize(stun.Mapped.Address), stun.Mapped.Port);

        if (pubblico.AddressFamily != centralino.AddressFamily)
            return Keep($"il server STUN ha risposto con un indirizzo di un'altra famiglia rispetto al centralino: {Locale}");

        if (IsInternal(pubblico.Address))
            return Keep($"il server STUN è in rete privata e non vede l'indirizzo pubblico: {Locale}");

        if (localTowardPeer is not null
            && localTowardStun is not null
            && !Normalize(localTowardPeer).Equals(Normalize(localTowardStun)))
        {
            return Keep(
                "il centralino e il server STUN si raggiungono da collegamenti diversi, per esempio con una "
                + $"VPN parziale, e l'indirizzo pubblico non vale per il centralino: {Locale}");
        }

        var locale = localTowardStun ?? localTowardPeer;

        if (locale is not null && Normalize(locale).Equals(pubblico.Address))
            return Keep($"nessun NAT, il server STUN vede l'indirizzo della postazione: {Locale}");

        return new MediaAddressDecision(
            pubblico,
            $"postazione dietro NAT: si annuncia l'indirizzo pubblico {Describe(pubblico)}");
    }

    private static MediaAddressDecision Keep(string reason) => new(null, reason);

    /// <summary>
    /// Dice se ha senso interrogare il server STUN per una chiamata con questo centralino.
    /// </summary>
    /// <param name="peer">Indirizzo del centralino, se noto.</param>
    /// <remarks>
    /// Con il centralino in rete interna la risposta non cambierebbe la scelta, e
    /// chiederla costerebbe due cose: un pacchetto verso il server per ogni chiamata
    /// fatta dalla sede, e fino a un secondo e mezzo di ritardo il giorno in cui il server
    /// non risponde. In quel caso si passa a <see cref="Decide"/> un esito
    /// <see cref="StunOutcome.NoResponse"/> qualunque: la ragione che ne esce è quella
    /// del centralino interno, che viene prima.
    /// </remarks>
    public static bool ShouldQuery(IPAddress? peer) => peer is not null && !IsInternal(peer);

    /// <summary>
    /// Dice se un indirizzo appartiene a una rete interna e non a Internet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Oltre alle reti private della RFC 1918 ci sono quelle che una postazione incontra
    /// davvero fuori sede: lo spazio condiviso 100.64.0.0/10 della RFC 6598, che usano
    /// gli operatori con il NAT di rete e diverse VPN, e gli indirizzi locali al
    /// collegamento, che Windows si assegna da solo quando non trova un server DHCP.
    /// Per IPv6 gli indirizzi locali unici fc00::/7 e quelli di collegamento fe80::/10.
    /// </para>
    /// </remarks>
    public static bool IsInternal(IPAddress address)
    {
        var a = Normalize(address);

        if (IPAddress.IsLoopback(a)) return true;

        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();

            return b[0] == 10
                   || (b[0] == 172 && b[1] is >= 16 and <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 100 && b[1] is >= 64 and <= 127)
                   || (b[0] == 169 && b[1] == 254)
                   || b[0] == 0;
        }

        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = a.GetAddressBytes();

            return a.IsIPv6LinkLocal
                   || a.IsIPv6SiteLocal
                   || (b[0] & 0xFE) == 0xFC
                   || a.Equals(IPAddress.IPv6Any);
        }

        return false;
    }

    /// <summary>
    /// Riporta alla forma IPv4 un indirizzo IPv4 scritto come IPv6.
    /// </summary>
    /// <remarks>
    /// I socket a doppio protocollo descrivono 192.0.2.1 come ::ffff:192.0.2.1, e senza
    /// questa conversione lo stesso indirizzo risulterebbe diverso da se stesso.
    /// </remarks>
    public static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Indirizzo e porta in una forma che si legge bene ad alta voce.</summary>
    /// <remarks>
    /// «93.184.216.34 porta 40000» invece di «93.184.216.34:40000»: lo screen reader i
    /// due punti li dice, ma «due punti» in mezzo a un indirizzo fa pensare a un errore.
    /// </remarks>
    public static string Describe(IPEndPoint endPoint)
        => $"{Normalize(endPoint.Address)} porta {endPoint.Port.ToString(CultureInfo.InvariantCulture)}";
}
