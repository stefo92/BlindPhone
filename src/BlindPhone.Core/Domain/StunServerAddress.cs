using System.Globalization;
using System.Net;

namespace BlindPhone.Core.Domain;

/// <summary>
/// Server STUN di un account SIP, come lo scrive chi configura la postazione.
/// </summary>
/// <param name="Host">Nome o indirizzo del server.</param>
/// <param name="Port">Porta UDP, 3478 se non indicata.</param>
/// <remarks>
/// <para>
/// <b>Nessun server predefinito, e non per dimenticanza.</b> Ogni softphone ne propone
/// uno pubblico, di solito di Google, e per un ente pubblico vuol dire mandare a un
/// terzo, a ogni telefonata, l'indirizzo della postazione e l'ora della chiamata. È una
/// scelta che spetta all'ente — un server suo, oppure uno esterno deciso e messo per
/// iscritto — e non a un valore di fabbrica che nessuno ha guardato.
/// </para>
/// <para>
/// Si accettano tre scritture, perché circolano tutte e tre nelle istruzioni dei
/// fornitori: il nome da solo, il nome con la porta, e la forma con lo schema
/// <c>stun:</c> della RFC 7064. Le forme <c>stuns:</c> e <c>turn:</c> si rifiutano
/// invece di ignorarle in silenzio: la postazione interroga il server solo su UDP e
/// non sa fare da client TURN, e un campo che accetta quello che poi non usa lascia
/// credere che il relè ci sia.
/// </para>
/// </remarks>
public sealed record StunServerAddress(string Host, int Port)
{
    /// <summary>Porta STUN standard, RFC 5389 §18.4.</summary>
    public const int DefaultPort = 3478;

    /// <summary>Esempio da citare nei messaggi, lo stesso del testo di aiuto.</summary>
    private const string Esempio = "per esempio stun.ente.it oppure stun.ente.it:3478";

    /// <summary>
    /// Legge il server scritto nelle impostazioni.
    /// </summary>
    /// <param name="text">Testo del campo. Vuoto significa nessun server.</param>
    /// <param name="address">Server letto, oppure <c>null</c> se il campo è vuoto o sbagliato.</param>
    /// <param name="error">Motivo del rifiuto, pronunciabile; vuoto se il testo va bene.</param>
    /// <returns><c>true</c> se il testo è vuoto oppure valido.</returns>
    /// <remarks>
    /// Un campo vuoto è valido: è la configurazione di partenza e significa che la
    /// postazione non interroga nessuno.
    /// </remarks>
    public static bool TryParse(string? text, out StunServerAddress? address, out string error)
    {
        address = null;
        error = string.Empty;

        var testo = text?.Trim() ?? string.Empty;
        if (testo.Length == 0) return true;

        if (testo.StartsWith("stuns:", StringComparison.OrdinalIgnoreCase)
            || testo.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
            || testo.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
        {
            error = "Il server STUN si interroga solo in chiaro su UDP: indica un indirizzo stun, non stuns o turn";
            return false;
        }

        if (testo.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)) testo = testo[5..];

        if (testo.Contains("//", StringComparison.Ordinal)
            || testo.Contains('/')
            || testo.Contains('@')
            || testo.Any(char.IsWhiteSpace))
        {
            error = $"Il server STUN deve essere un nome o un indirizzo, {Esempio}";
            return false;
        }

        string host;
        var porta = DefaultPort;

        if (testo.StartsWith('['))
        {
            // Indirizzo IPv6 fra parentesi quadre, con la porta eventuale dopo.
            var chiusa = testo.IndexOf(']');
            if (chiusa < 0)
            {
                error = $"Il server STUN ha una parentesi quadra non chiusa, {Esempio}";
                return false;
            }

            host = testo[1..chiusa];
            var resto = testo[(chiusa + 1)..];

            if (resto.Length > 0 && (!resto.StartsWith(':') || !TryPort(resto[1..], out porta)))
            {
                error = "La porta del server STUN deve essere un numero fra 1 e 65535";
                return false;
            }
        }
        else if (testo.Count(c => c == ':') > 1)
        {
            // Un indirizzo IPv6 scritto senza parentesi non puo' avere una porta: i due
            // punti fanno gia' parte dell'indirizzo.
            host = testo;
        }
        else if (testo.IndexOf(':') is var due and >= 0)
        {
            host = testo[..due];

            if (!TryPort(testo[(due + 1)..], out porta))
            {
                error = "La porta del server STUN deve essere un numero fra 1 e 65535";
                return false;
            }
        }
        else
        {
            host = testo;
        }

        if (host.Length == 0 || (host.Contains(':') && !IPAddress.TryParse(host, out _)))
        {
            error = $"Il server STUN deve essere un nome o un indirizzo, {Esempio}";
            return false;
        }

        address = new StunServerAddress(host, porta);
        return true;
    }

    private static bool TryPort(string text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary>Forma scritta, la stessa che si legge nella diagnostica.</summary>
    /// <remarks>La porta standard si tace: «stun.ente.it» si dice meglio di «stun.ente.it:3478».</remarks>
    public override string ToString()
    {
        var host = Host.Contains(':') ? $"[{Host}]" : Host;
        return Port == DefaultPort ? host : $"{host}:{Port.ToString(CultureInfo.InvariantCulture)}";
    }
}
