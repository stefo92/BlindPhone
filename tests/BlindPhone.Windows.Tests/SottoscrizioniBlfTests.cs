using BlindPhone.Telephony;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la sorveglianza dello stato degli interni (BLF, lato osservatore).
/// </summary>
/// <remarks>
/// <para>
/// BlindPhone e' il <b>watcher</b> del BLF: manda un SUBSCRIBE al pacchetto di eventi
/// <c>dialog</c> (RFC 4235) per ogni interno che l'operatore ha davanti e legge i
/// NOTIFY che il centralino gli rimanda. Il lato opposto — accettare sottoscrizioni e
/// generare notifiche — spetta al centralino.
/// </para>
/// <para>
/// <b>Queste prove nascono da un guasto in esercizio.</b> Fra il 10 e l'11 agosto 2026
/// il registro conta 1.589 sottoscrizioni respinte contro 57 accettate: 803 con
/// <c>404 Not Found</c> e 786 con <c>400 Bad Request</c>. Le richieste partivano in
/// chiaro su UDP mentre l'account era registrato in TLS, perche' l'indirizzo della
/// risorsa veniva composto senza dichiarare il trasporto.
/// </para>
/// </remarks>
public sealed class SottoscrizioniBlfTests
{
    private static SipAccountOptions Account(SipTransportKind trasporto) => new()
    {
        Username = "4001",
        Domain = "aopr.ras.uccpbx.com",
        Transport = trasporto,
    };

    [Fact]
    public void ConAccountInTlsGliIndirizziDichiaranoIlTrasporto()
    {
        // E' la riga che mancava: senza, la libreria sceglie UDP e la sottoscrizione
        // parte da un canale che il centralino non riconosce come autenticato.
        Assert.Equal(";transport=tls", Account(SipTransportKind.Tls).TransportParameter);
    }

    [Fact]
    public void ConAccountInTcpIlTrasportoEDichiaratoLoStesso()
        => Assert.Equal(";transport=tcp", Account(SipTransportKind.Tcp).TransportParameter);

    [Fact]
    public void ConAccountInUdpNonSiAggiungeNiente()
    {
        // UDP e' il valore predefinito del protocollo: dichiararlo sarebbe rumore, e
        // alcuni apparati trattano un parametro superfluo come una richiesta anomala.
        Assert.Equal(string.Empty, Account(SipTransportKind.Udp).TransportParameter);
    }

    [Fact]
    public void LIndirizzoDellaRisorsaSorvegliataPortaIlTrasporto()
    {
        // La forma completa, come finisce nella riga di richiesta del SUBSCRIBE.
        var account = Account(SipTransportKind.Tls);
        var risorsa = $"4021@{account.Domain}{account.TransportParameter}";

        Assert.Equal("4021@aopr.ras.uccpbx.com;transport=tls", risorsa);
    }
}
