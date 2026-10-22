using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Traduce un certificato di sistema nella descrizione usata dal dominio.
/// </summary>
/// <remarks>
/// <para>
/// Esiste come classe a sé perché è l'unico punto in cui il prodotto tocca
/// <c>X509Certificate2</c>: tutto il resto — la decisione, i testi, il ricordo —
/// lavora sul record di dominio, e si collauda senza fabbricare certificati.
/// </para>
/// <para>
/// Sta nel core e non nell'infrastruttura perché la usano due progetti che non si
/// conoscono fra loro, la telefonia e il connettore del centralino, e perché
/// <c>X509Certificate2</c> è libreria di base e non codice specifico della
/// piattaforma: il core resta compilabile sulla banda senza <c>-windows</c>.
/// </para>
/// </remarks>
public static class CertificateDescriptionFactory
{
    /// <summary>Costruisce la descrizione di dominio.</summary>
    /// <param name="host">Server contattato, come configurato dall'operatore.</param>
    /// <param name="purpose">Descrizione del collegamento, per l'annuncio.</param>
    /// <param name="certificate">Certificato presentato, oppure <c>null</c>.</param>
    /// <param name="errors">Errori rilevati dalla verifica di sistema.</param>
    public static CertificateDescription Create(
        string host,
        string purpose,
        X509Certificate? certificate,
        SslPolicyErrors errors)
    {
        var problems = CertificateProblem.None;

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            problems |= CertificateProblem.UntrustedAuthority;

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            problems |= CertificateProblem.NameMismatch;

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) || certificate is null)
            problems |= CertificateProblem.NotAvailable;

        if (certificate is null)
        {
            return new CertificateDescription(
                host, purpose, "sconosciuto", "sconosciuto", string.Empty,
                DateTimeOffset.MinValue, DateTimeOffset.MinValue, problems);
        }

        // Il callback di validazione consegna un X509Certificate, non la sua
        // sottoclasse: quando non lo e' gia', si ricostruisce dai byte grezzi invece
        // di usare il costruttore obsoleto di X509Certificate2.
        var x509 = certificate as X509Certificate2
                   ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

        return new CertificateDescription(
            host,
            purpose,
            Readable(x509.Subject),
            Readable(x509.Issuer),
            x509.Thumbprint.ToUpperInvariant(),
            x509.NotBefore,
            x509.NotAfter,
            problems);
    }

    /// <summary>
    /// Estrae dal nome distinto la parte che una persona riconosce.
    /// </summary>
    /// <remarks>
    /// Un nome distinto completo — <c>CN=pbx.ente.it, OU=IT, O=Ente, L=Roma, C=IT</c> —
    /// letto dalla sintesi è un elenco di sigle in cui la parte utile è la prima. Si
    /// prende il common name e si lascia cadere il resto: chi deve confrontare
    /// l'identità del server guarda quello, e chi vuole il dettaglio ha l'impronta.
    /// </remarks>
    private static string Readable(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName)) return "sconosciuto";

        foreach (var parte in distinguishedName.Split(',', StringSplitOptions.TrimEntries))
        {
            if (parte.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return parte[3..].Trim();
            }
        }

        return distinguishedName;
    }
}
