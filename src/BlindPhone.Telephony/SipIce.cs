using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using BlindPhone.Core.Services;
using SIPSorcery.Net;
using SIPSorcery.Sys;

namespace BlindPhone.Telephony;

/// <summary>
/// Il canale audio di una chiamata con ICE: il socket RTP con l'agente ICE della libreria.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché l'agente di <c>RTCPeerConnection</c> e non uno scritto qui.</b> L'agente di
/// SIPSorcery fa già le parti difficili della RFC 8445 — lista delle coppie ordinata per
/// priorità, ritrasmissioni, nomina, candidati scoperti durante le verifiche, controlli
/// di consenso a chiamata avviata — ed è quello che la libreria collauda con i browser.
/// Nasce per WebRTC, ma il suo legame con WebRTC sta tutto in <c>RTCPeerConnection</c>:
/// il canale in sé è un <see cref="RTPChannel"/> che risponde alle verifiche STUN, e una
/// sessione SIP lo può usare al posto di quello normale (vedi
/// <see cref="AudioOnlyMediaSession"/>).
/// </para>
/// <para>
/// <b>Due adattamenti, entrambi qui.</b>
/// </para>
/// <list type="number">
///   <item><description>
///   <b>L'RTCP esce dal socket dell'RTP.</b> Il canale ICE apre un socket solo, mentre la
///   sessione SIP, che non sa di ICE, chiede di spedire i rapporti RTCP su un socket di
///   controllo che non c'è: la libreria solleverebbe un'eccezione a ogni rapporto. Con
///   <c>a=rtcp-mux</c> accettato è esattamente il comportamento giusto; senza, il
///   centralino riceve i rapporti da una porta diversa da quella attesa, e i centralini li
///   accettano lo stesso.
///   </description></item>
///   <item><description>
///   <b>Il ruolo si imposta da fuori.</b> La libreria tiene <c>IsController</c> con la
///   scrittura riservata a <c>RTCPeerConnection</c>, e non gestisce i conflitti di ruolo
///   (l'errore 487 della RFC 8445 §7.3.1.1): il ruolo va quindi deciso giusto prima delle
///   verifiche, con <see cref="IceRoles.IsControlling"/>, e scritto con la riflessione.
///   La versione della libreria è fissata, e <c>CanaleIceTests</c> si accorge per primo se
///   un aggiornamento toglie la proprietà.
///   </description></item>
/// </list>
/// </remarks>
internal sealed class IceRtpChannel : RtpIceChannel
{
    private static readonly MethodInfo? RoleSetter = typeof(RtpIceChannel)
        .GetProperty(nameof(IsController), BindingFlags.Instance | BindingFlags.Public)
        ?.GetSetMethod(nonPublic: true);

    /// <summary>Se la libreria espone ancora la proprietà del ruolo da impostare.</summary>
    internal static bool CanSetRole => RoleSetter is not null;

    /// <param name="bindAddress">Indirizzo su cui aprire il socket; <c>null</c> per tutti.</param>
    /// <param name="bindPort">Porta locale; zero per una qualunque.</param>
    /// <param name="portRange">Intervallo di porte RTP, se configurato.</param>
    public IceRtpChannel(IPAddress? bindAddress, int bindPort, PortRange? portRange)
        : base(bindAddress, RTCIceComponent.rtp, null, RTCIceTransportPolicy.all, false, bindPort, portRange)
    {
    }

    /// <summary>
    /// Imposta il ruolo nelle verifiche. Va chiamato prima di consegnare i candidati del
    /// centralino: ogni coppia ricorda il ruolo con cui è stata creata.
    /// </summary>
    /// <returns><c>false</c> se la libreria non permette più di impostarlo.</returns>
    public bool SetControlling(bool controlling)
    {
        if (RoleSetter is null) return IsController == controlling;

        RoleSetter.Invoke(this, [controlling]);
        return true;
    }

    /// <inheritdoc />
    public override SocketError Send(RTPChannelSocketsEnum sendOn, IPEndPoint dstEndPoint, byte[] buffer)
        => base.Send(
            sendOn == RTPChannelSocketsEnum.Control ? RTPChannelSocketsEnum.RTP : sendOn,
            dstEndPoint,
            buffer);
}

/// <summary>
/// Quello che una descrizione di sessione del centralino dice di ICE.
/// </summary>
/// <param name="Ufrag">Nome utente ICE del centralino.</param>
/// <param name="Pwd">Password ICE del centralino.</param>
/// <param name="Lite">Se il centralino si dichiara <c>a=ice-lite</c>.</param>
/// <param name="RtcpMux">Se il centralino accetta l'RTCP sul socket dell'RTP.</param>
/// <param name="Candidates">Candidati del componente RTP su UDP, gli unici che si verificano.</param>
/// <param name="Ignored">Candidati scartati: RTCP separato, TCP, righe illeggibili.</param>
internal sealed record RemoteIceParameters(
    string Ufrag,
    string Pwd,
    bool Lite,
    bool RtcpMux,
    IReadOnlyList<RTCIceCandidate> Candidates,
    int Ignored)
{
    /// <summary>
    /// Legge ICE dalla descrizione di sessione; <c>null</c> se il centralino non lo usa.
    /// </summary>
    /// <remarks>
    /// Credenziali e candidati possono stare sulla riga del flusso o in testa alla
    /// descrizione (RFC 8839 §5.4): vale quella del flusso, se c'è. Si guarda il solo flusso
    /// audio, che è l'unico che la postazione offre e accetta.
    /// </remarks>
    public static RemoteIceParameters? Read(SDP description)
    {
        var audio = description.Media.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);

        var ufrag = audio?.IceUfrag ?? description.IceUfrag;
        var pwd = audio?.IcePwd ?? description.IcePwd;

        if (string.IsNullOrWhiteSpace(ufrag) || string.IsNullOrWhiteSpace(pwd)) return null;

        var righe = (audio?.IceCandidates ?? []).Concat(description.IceCandidates ?? []);
        var candidati = new List<RTCIceCandidate>();
        var scartati = 0;

        foreach (var riga in righe)
        {
            if (TryParse(riga) is { } candidato) candidati.Add(candidato);
            else scartati++;
        }

        var mux = audio?.ExtraMediaAttributes.Any(a =>
            a.Trim().Equals("a=rtcp-mux", StringComparison.OrdinalIgnoreCase)) == true;

        return new RemoteIceParameters(
            ufrag.Trim(), pwd.Trim(), description.IceImplementation == IceImplementationEnum.lite, mux, candidati, scartati);
    }

    /// <summary>
    /// Legge una riga <c>candidate</c>, tenendo solo quello che l'agente sa verificare.
    /// </summary>
    /// <remarks>
    /// La lettura della libreria distingue maiuscole e minuscole nel trasporto, e diversi
    /// centralini scrivono <c>UDP</c>: senza la conversione un candidato UDP passerebbe per
    /// uno di trasporto sconosciuto. Una riga che non si legge non deve far cadere la
    /// chiamata: si scarta, e il conto finisce nel registro.
    /// </remarks>
    internal static RTCIceCandidate? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var campi = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (campi.Length < 8) return null;

        if (!campi[1].Equals("1", StringComparison.Ordinal)) return null;
        if (!campi[2].Equals("udp", StringComparison.OrdinalIgnoreCase)) return null;
        if (!IPAddress.TryParse(campi[4], out var indirizzo)) return null;
        if (!ushort.TryParse(campi[5], out var porta) || porta == 0) return null;

        campi[2] = "udp";
        campi[7] = campi[7].ToLowerInvariant();

        try
        {
            var candidato = RTCIceCandidate.Parse(string.Join(' ', campi));

            // La libreria la ricava da sola alla prima verifica; scritta subito, il
            // candidato vale come origine riconosciuta anche per l'audio che arriva prima.
            candidato.SetDestinationEndPoint(new IPEndPoint(MediaAddressPolicy.Normalize(indirizzo), porta));
            return candidato;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Traduzioni fra i tipi della libreria e quelli del dominio.</summary>
internal static class IceTranslation
{
    /// <summary>Tipo di candidato del dominio.</summary>
    public static IceRemoteCandidateKind Kind(RTCIceCandidateType type) => type switch
    {
        RTCIceCandidateType.srflx => IceRemoteCandidateKind.ServerReflexive,
        RTCIceCandidateType.prflx => IceRemoteCandidateKind.PeerReflexive,
        RTCIceCandidateType.relay => IceRemoteCandidateKind.Relay,
        _ => IceRemoteCandidateKind.Host,
    };

    /// <summary>
    /// Gli indirizzi delle schede di rete accese, tunnel delle VPN compresi.
    /// </summary>
    /// <remarks>
    /// Non si filtra per tipo di scheda: le VPN si presentano come Ethernet virtuali, come
    /// collegamenti PPP o come schede di tipo sconosciuto, ed escluderne un tipo vorrebbe
    /// dire escludere proprio il tunnel che porta al centralino. Gli indirizzi inservibili
    /// li toglie <see cref="IceCandidatePlan.IsUsableHostAddress"/>.
    /// </remarks>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
