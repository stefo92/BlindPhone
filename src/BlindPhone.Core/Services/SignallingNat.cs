using System.Globalization;
using System.Net;

namespace BlindPhone.Core.Services;

/// <summary>
/// Che cosa dice il centralino dell'indirizzo da cui vede arrivare la postazione.
/// </summary>
/// <param name="SeenAs">
/// Indirizzo, e porta se dichiarata, da cui il centralino riceve la segnalazione;
/// <c>null</c> se non lo dice.
/// </param>
/// <param name="BehindNat">
/// <c>true</c> se fra postazione e centralino c'è un NAT, <c>false</c> se non c'è,
/// <c>null</c> se non si può dire.
/// </param>
/// <param name="Description">La stessa cosa detta per intero, per la diagnostica.</param>
public sealed record SignallingNatObservation(IPEndPoint? SeenAs, bool? BehindNat, string Description)
{
    /// <summary>Nessuna registrazione riuscita, quindi nessuna risposta da leggere.</summary>
    public static SignallingNatObservation NotYet { get; } =
        new(null, null, "non ancora determinato: nessuna registrazione riuscita");
}

/// <summary>
/// Riconosce il NAT fra postazione e centralino dalla risposta alla registrazione,
/// RFC 3581.
/// </summary>
/// <remarks>
/// <para>
/// <b>Funziona senza server STUN, ed è il motivo per cui c'è.</b> Ogni richiesta che la
/// postazione manda porta nell'intestazione <c>Via</c> il parametro <c>rport</c> vuoto —
/// la libreria SIP lo aggiunge da sé — e il centralino che rispetta la RFC 3581 lo
/// riempie con la porta da cui ha visto arrivare la richiesta, mettendo l'indirizzo in
/// <c>received</c>. Confrontati con l'indirizzo del socket dicono se in mezzo c'è un
/// NAT: sulla postazione che l'ente non ha voluto mandare a interrogare server esterni
/// è l'unica prova disponibile, e non costa un pacchetto in più.
/// </para>
/// <para>
/// Riguarda la segnalazione e non l'audio: l'indirizzo è quello del socket SIP, e la
/// porta che il router assegna al socket audio può essere un'altra. Per questo nessuna
/// descrizione di sessione si riscrive su questa base; serve a dire all'assistenza che il
/// NAT c'è, ed è già metà della diagnosi di una chiamata muta.
/// </para>
/// </remarks>
public static class SignallingNat
{
    /// <summary>
    /// Confronta l'indirizzo del socket con quello che il centralino dice di vedere.
    /// </summary>
    /// <param name="local">Indirizzo e porta locali da cui è partita la registrazione.</param>
    /// <param name="received">Valore del parametro <c>received</c>, se presente.</param>
    /// <param name="rport">Valore del parametro <c>rport</c>, zero se assente o vuoto.</param>
    public static SignallingNatObservation Observe(IPEndPoint? local, string? received, int rport)
    {
        if (string.IsNullOrWhiteSpace(received) || !IPAddress.TryParse(received.Trim(), out var visto))
        {
            return new SignallingNatObservation(
                null, null, "non determinato: il centralino non dice da quale indirizzo vede la postazione");
        }

        visto = MediaAddressPolicy.Normalize(visto);
        var seenAs = new IPEndPoint(visto, rport > 0 ? rport : local?.Port ?? 0);
        var comeVisto = rport > 0 ? MediaAddressPolicy.Describe(seenAs) : visto.ToString();

        if (local is null
            || local.Address.Equals(IPAddress.Any)
            || local.Address.Equals(IPAddress.IPv6Any))
        {
            return new SignallingNatObservation(
                seenAs, null, $"non determinato: il centralino vede la postazione come {comeVisto}");
        }

        var locale = new IPEndPoint(MediaAddressPolicy.Normalize(local.Address), local.Port);

        // La porta conta quanto l'indirizzo: un router che lascia l'indirizzo e cambia la
        // porta sta comunque traducendo, e il passaggio aperto nel router e' quello.
        if (locale.Address.Equals(visto) && (rport == 0 || rport == locale.Port))
        {
            return new SignallingNatObservation(
                seenAs, false, $"assente: il centralino vede la postazione con il suo indirizzo, {comeVisto}");
        }

        return new SignallingNatObservation(
            seenAs,
            true,
            $"presente: il centralino vede la postazione come {comeVisto}, "
            + $"mentre la postazione ha l'indirizzo {MediaAddressPolicy.Describe(locale)}");
    }

    /// <summary>Legge il parametro <c>rport</c>, che può mancare o essere vuoto.</summary>
    public static int ParsePort(string? value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : 0;
}
