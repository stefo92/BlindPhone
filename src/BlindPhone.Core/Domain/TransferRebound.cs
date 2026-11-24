using System.Text.RegularExpressions;

namespace BlindPhone.Core.Domain;

/// <summary>
/// Riconosce la chiamata che il centralino restituisce all'operatore perché
/// l'interno a cui l'aveva trasferita non l'ha presa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non è una chiamata nuova, e non va annunciata come tale.</b> Yeastar
/// P-Series, con l'opzione <i>Transfer Bounce Back</i> accesa, riporta indietro un
/// trasferimento cieco o semi-cieco rimasto senza risposta, e lo fa riconoscere
/// solo dal nome: <c>Rebound Transfer Call: {nome del chiamante}</c>. Il numero
/// resta quello del chiamante.
/// </para>
/// <para>
/// Letto così com'è, l'operatore sentiva «Chiamata da Rebound Transfer Call:
/// Mario Rossi» in inglese, pronunciato dalla voce italiana, e la parte utile
/// arrivava per ultima. Ma è proprio l'informazione che gli serve per prima: il
/// chiamante che aveva già passato a un collega gli torna indietro, e va ripreso
/// sapendo che il collega non ha risposto.
/// </para>
/// <para>
/// Il riconoscimento è tollerante su maiuscole, spazi e separatore, e accetta il
/// prefisso anche senza la parola <c>Call</c>: è una dicitura scelta dal
/// centralino, e una versione diversa del firmware può scriverla un po' diversa.
/// Resta ancorato all'inizio del nome, perché un nome che contenga quelle parole
/// più avanti è un nome e basta.
/// </para>
/// </remarks>
public static class TransferRebound
{
    private static readonly Regex Prefisso = new(
        @"^\s*Rebound\s+Transfer(?:\s+Call)?(?:\s*[:\-–]\s*|\s+|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Dice se il nome dichiarato dal centralino è quello di una chiamata restituita
    /// da un trasferimento, e ne ricava il nome del chiamante.
    /// </summary>
    /// <param name="displayName">Nome come arriva dalla segnalazione SIP.</param>
    /// <param name="callerName">
    /// Il nome del chiamante senza il prefisso, oppure <c>null</c> se dopo il prefisso
    /// non resta niente: in quel caso il nome lo cerca la rubrica, dal numero.
    /// </param>
    /// <returns><c>true</c> se la chiamata torna da un trasferimento.</returns>
    public static bool TryStrip(string? displayName, out string? callerName)
    {
        callerName = null;
        if (string.IsNullOrWhiteSpace(displayName)) return false;

        var corrispondenza = Prefisso.Match(displayName);
        if (!corrispondenza.Success) return false;

        var resto = displayName[corrispondenza.Length..].Trim();
        callerName = resto.Length == 0 ? null : resto;
        return true;
    }
}
