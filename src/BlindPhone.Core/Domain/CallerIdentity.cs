namespace BlindPhone.Core.Domain;

/// <summary>
/// Il numero che il posto operatore presenta a chi chiama.
/// </summary>
/// <remarks>
/// <para>
/// Esiste per un problema che si manifesta lentamente e non si riesce più a
/// correggere: chi riceve una chiamata dal centralino vede l'interno del
/// centralinista, lo salva in rubrica e da lì in poi richiama <b>quello</b>,
/// scavalcando il numero pubblico dell'ente. Nel giro di qualche mese la postazione
/// ha un recapito diretto che nessuno ha deciso di pubblicare, non filtrato e
/// scoperto quando il centralinista non c'è.
/// </para>
/// </remarks>
public static class CallerIdentity
{
    /// <summary>
    /// Riduce un recapito alla forma che può stare dentro un'intestazione SIP.
    /// </summary>
    /// <param name="value">Numero come è stato scritto o importato.</param>
    /// <returns>Le sole cifre, con il più iniziale se c'era; stringa vuota se non resta nulla.</returns>
    /// <remarks>
    /// <para>
    /// Un numero copiato da un documento, da una firma di posta o da un foglio di
    /// calcolo arriva con spazi, punti, barre o parentesi. Quei caratteri dentro l'URI
    /// di un'intestazione SIP la rendono malformata, e il centralino <b>rifiuta la
    /// chiamata</b> invece di farla partire con il numero indicato.
    /// </para>
    /// <para>
    /// Il modo in cui si manifesterebbe è la ragione per cui la pulizia sta qui e non
    /// nel solo pannello: tutte le chiamate uscenti smettono di funzionare, e nulla
    /// collega il guasto a un punto di troppo scritto settimane prima in un campo di
    /// configurazione. Passando da qui, anche una configurazione modificata a mano o
    /// distribuita da un pacchetto resta innocua.
    /// </para>
    /// <para>
    /// Il più iniziale si conserva perché è significativo: distingue un numero in
    /// forma internazionale da uno locale, e toglierlo cambierebbe la destinazione a
    /// cui le richiamate arrivano.
    /// </para>
    /// </remarks>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var trimmed = value.Trim();
        var prefisso = trimmed.StartsWith('+') ? "+" : string.Empty;
        var cifre = new string([.. trimmed.Where(char.IsAsciiDigit)]);

        return cifre.Length == 0 ? string.Empty : prefisso + cifre;
    }
}
