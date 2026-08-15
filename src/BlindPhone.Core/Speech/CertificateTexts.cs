using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Speech;

/// <summary>
/// Testi con cui si descrive un certificato a chi non può guardarlo.
/// </summary>
/// <remarks>
/// <para>
/// Un browser mostra un lucchetto e, se si insiste, una schermata fitta di campi.
/// Qui non c'è nessuna delle due cose: la decisione va presa ascoltando, e ascoltare
/// costa tempo. Da qui tre scelte di scrittura.
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Prima cosa non torna, poi di chi si tratta.</b> È l'informazione che decide:
///   un certificato autofirmato su un centralino interno è normale, un nome che non
///   corrisponde no.
///   </description></item>
///   <item><description>
///   <b>L'impronta si legge a gruppi di quattro caratteri.</b> Quaranta caratteri
///   esadecimali letti di fila non si confrontano con nulla; a gruppi si possono
///   seguire mentre l'amministratore li detta al telefono, che è il solo modo in cui
///   una verifica dell'impronta avviene davvero.
///   </description></item>
///   <item><description>
///   <b>Nessun gergo.</b> Non «catena di certificazione non valida» ma «non è stato
///   emesso da un'autorità che il computer riconosce».
///   </description></item>
/// </list>
/// </remarks>
public static class CertificateTexts
{
    /// <summary>Riassunto in una frase di cosa non torna.</summary>
    /// <param name="certificate">Certificato in esame.</param>
    /// <param name="now">Istante corrente, per la scadenza.</param>
    public static string Problem(CertificateDescription certificate, DateTimeOffset now)
    {
        var motivi = new List<string>();

        if (certificate.Problems.HasFlag(CertificateProblem.UntrustedAuthority))
        {
            motivi.Add("non e' stato emesso da un'autorita' che questo computer riconosce");
        }

        if (certificate.Problems.HasFlag(CertificateProblem.NameMismatch))
        {
            motivi.Add($"e' intestato a un nome diverso da {certificate.Host}");
        }

        if (now > certificate.NotAfter)
        {
            motivi.Add($"e' scaduto il {certificate.NotAfter:d MMMM yyyy}");
        }
        else if (now < certificate.NotBefore)
        {
            motivi.Add($"non e' ancora valido: lo diventa il {certificate.NotBefore:d MMMM yyyy}");
        }

        return motivi.Count switch
        {
            0 => "Il certificato non e' stato verificato",
            1 => $"Il certificato {motivi[0]}",
            _ => $"Il certificato {string.Join(", e ", motivi)}",
        };
    }

    /// <summary>Descrizione completa, da leggere nella finestra di conferma.</summary>
    /// <param name="certificate">Certificato in esame.</param>
    /// <param name="now">Istante corrente.</param>
    public static string Describe(CertificateDescription certificate, DateTimeOffset now) =>
        $"""
        {Problem(certificate, now)}.

        Collegamento: {certificate.Purpose} {certificate.Host}
        Intestato a: {certificate.Subject}
        Emesso da: {certificate.Issuer}
        Valido dal {certificate.NotBefore:d MMMM yyyy} al {certificate.NotAfter:d MMMM yyyy}
        Impronta: {Fingerprint(certificate.Thumbprint)}
        """;

    /// <summary>
    /// Impronta digitale a gruppi di quattro caratteri, per poterla confrontare a voce.
    /// </summary>
    /// <param name="thumbprint">Impronta, in esadecimale e senza separatori.</param>
    /// <remarks>
    /// È l'unica verifica che un operatore può fare davvero su un certificato
    /// autofirmato: farsi dettare l'impronta da chi amministra il centralino e
    /// confrontarla. Quaranta caratteri di fila non si confrontano; a gruppi si', e
    /// il gruppo di quattro è quello che si tiene a mente fra un ascolto e l'altro.
    /// </remarks>
    public static string Fingerprint(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return "non disponibile";

        var pulita = thumbprint.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        var gruppi = new List<string>();

        for (var i = 0; i < pulita.Length; i += 4)
        {
            gruppi.Add(pulita.Substring(i, Math.Min(4, pulita.Length - i)));
        }

        return string.Join(' ', gruppi);
    }

    /// <summary>Istruzioni sulle scelte disponibili, lette dopo la descrizione.</summary>
    /// <remarks>
    /// L'ordine non è casuale: la scelta prudente viene per prima e resta quella
    /// predefinita. Su una domanda di sicurezza posta a chi sta lavorando, il
    /// predefinito è la risposta che si otterrà nella maggioranza dei casi.
    /// </remarks>
    public static string Choices =>
        "Non fidarti se non ti aspettavi questo messaggio: il collegamento non verra' stabilito. "
        + "Fidati solo per adesso se stai provando una configurazione. "
        + "Fidati sempre solo dopo aver confrontato l'impronta con chi amministra il centralino.";

    /// <summary>Esito: fiducia permanente.</summary>
    public static string TrustedAlways =>
        "Certificato approvato: non verra' piu' chiesto su questa postazione";

    /// <summary>Esito: fiducia per la sola sessione.</summary>
    public static string TrustedForSession =>
        "Certificato approvato per questa sessione: la domanda tornera' al prossimo avvio";

    /// <summary>Esito: rifiuto.</summary>
    public static string Rejected =>
        "Certificato rifiutato: il collegamento non verra' stabilito";

    /// <summary>Rifiuto automatico, quando non c'è un operatore a cui chiedere.</summary>
    public static string RejectedWithoutOperator =>
        "Certificato non verificabile e nessuna interfaccia per chiedere conferma: collegamento rifiutato";

    /// <summary>Avviso pronunciato quando un collegamento cade per il certificato.</summary>
    /// <param name="purpose">Che collegamento era.</param>
    public static string ConnectionRefused(string purpose) =>
        $"Collegamento {purpose} non stabilito: certificato non accettato";
}
