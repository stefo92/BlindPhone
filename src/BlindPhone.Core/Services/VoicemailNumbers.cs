using System.Text.RegularExpressions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Regole sul numero da chiamare per ascoltare la segreteria.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il numero lo decide l'operatore, il centralino lo può solo proporre.</b> Molti
/// centralini scrivono nelle notifiche l'indirizzo della casella —
/// <c>Message-Account: sip:*97@centralino</c> — e su quegli impianti è proprio il codice
/// da comporre. Ma lo stesso campo, altrove, contiene il nome del servizio
/// (<c>sip:asterisk@…</c>) o l'interno stesso dell'operatore (<c>sip:200@…</c>): comporlo
/// senza chiedere farebbe partire una chiamata verso un nome che non si compone, o verso
/// la propria casella dal lato di chi lascia un messaggio. Per questo il valore del
/// centralino finisce soltanto come proposta nelle impostazioni, e il comando chiama il
/// numero che l'operatore ha confermato.
/// </para>
/// </remarks>
public static partial class VoicemailNumbers
{
    /// <summary>
    /// Ricava dall'indirizzo della casella un numero da proporre.
    /// </summary>
    /// <param name="messageAccount">
    /// Il valore di <c>Message-Account</c>: <c>sip:*97@host</c>, <c>sips:…</c>,
    /// <c>tel:…</c>, con o senza parentesi angolari e parametri.
    /// </param>
    /// <param name="ownExtension">Interno dell'account, che non va mai proposto.</param>
    /// <returns>
    /// Il numero, per esempio <c>*97</c>, oppure <c>null</c> se l'indirizzo non contiene
    /// qualcosa che si possa comporre.
    /// </returns>
    /// <remarks>
    /// Si propone soltanto una sequenza di cifre, asterischi, cancelletti e del più
    /// iniziale: è quello che un telefono sa comporre, e scarta da solo i nomi di
    /// servizio. L'asterisco può arrivare codificato come <c>%2A</c>, ed è il motivo per
    /// cui l'indirizzo viene decodificato prima del confronto.
    /// </remarks>
    public static string? ProposalFrom(string? messageAccount, string? ownExtension)
    {
        if (string.IsNullOrWhiteSpace(messageAccount)) return null;

        var indirizzo = messageAccount.Trim().TrimStart('<').TrimEnd('>').Trim();

        var dueP = indirizzo.IndexOf(':');
        if (dueP < 0) return null;

        var schema = indirizzo[..dueP].ToLowerInvariant();
        var resto = indirizzo[(dueP + 1)..];

        string utente;

        switch (schema)
        {
            case "sip":
            case "sips":
                var chiocciola = resto.IndexOf('@');
                if (chiocciola <= 0) return null;
                utente = resto[..chiocciola];
                break;

            case "tel":
                utente = resto;
                break;

            default:
                return null;
        }

        // I parametri dell'utente (";phone-context=...") non fanno parte del numero.
        var parametri = utente.IndexOf(';');
        if (parametri >= 0) utente = utente[..parametri];

        string numero;

        try
        {
            numero = Uri.UnescapeDataString(utente).Trim();
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (!DialablePattern().IsMatch(numero)) return null;

        // La propria casella vista da fuori: comporla lascia un messaggio, non lo ascolta.
        if (!string.IsNullOrWhiteSpace(ownExtension)
            && string.Equals(numero, ownExtension.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return numero;
    }

    /// <summary>
    /// Ripulisce il numero scritto dall'operatore nelle impostazioni.
    /// </summary>
    /// <param name="typed">Testo del campo.</param>
    /// <returns>Il numero senza spazi ai lati, oppure stringa vuota.</returns>
    /// <remarks>
    /// Non si tolgono gli asterischi e i cancelletti, che a differenza del numero
    /// presentato in uscita sono proprio il contenuto: <c>*97</c> senza asterisco è un
    /// altro numero.
    /// </remarks>
    public static string Normalize(string? typed) => typed?.Trim() ?? string.Empty;

    [GeneratedRegex(@"^\+?[0-9*#]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex DialablePattern();
}
