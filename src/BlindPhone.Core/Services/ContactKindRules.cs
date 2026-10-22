using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Ricava dal dato importato che cosa risponde a una voce della rubrica.
/// </summary>
/// <remarks>
/// <para>
/// <b>Le rubriche degli enti non hanno una colonna «tipo».</b> Hanno una descrizione
/// libera, scritta da chi teneva il centralino, e dentro quella descrizione
/// l'informazione c'è: «FAX Accettazione», «TELEFONO EMERGENZA IN CASO DI
/// INTERRUZIONE TELEFONIA». Riconoscerla è l'unico modo per dire all'operatore che
/// cosa sta per chiamare, e su una rubrica di duemila voci nessuno la classificherà a
/// mano.
/// </para>
/// <para>
/// <b>Le regole stanno qui e non dentro la sorgente</b> perché sono una decisione
/// discutibile: vanno lette, contestate e corrette guardando una rubrica vera, non
/// scoperte dentro un ciclo di importazione. Sono deliberatamente poche e prudenti —
/// una parola che sbaglia a classificare cinquanta voci fa più danno delle cinquanta
/// che resterebbero senza tipo.
/// </para>
/// </remarks>
public static class ContactKindRules
{
    /// <summary>
    /// Parola con cui una rubrica dichiara che la riga è un apparecchio fax.
    /// </summary>
    /// <remarks>
    /// <b>Si cerca solo in testa alla descrizione</b>, non in mezzo. «FAX Accettazione»
    /// è un fax; «Ambulatorio Azzurro Terapia Antalgica e Fax» è un ambulatorio che ha
    /// anche un fax, e marcarlo come fax scoraggerebbe dal chiamare l'unico numero a
    /// cui qualcuno risponde. La distinzione è netta nei dati veri, e vale la rinuncia
    /// ai pochi casi ambigui.
    /// </remarks>
    private const string Fax = "fax";

    /// <summary>
    /// Parola con cui una rubrica dichiara un recapito di emergenza.
    /// </summary>
    /// <remarks>
    /// Qui si cerca ovunque nella descrizione, perché la parola compare tanto in testa
    /// («EMERGENZA PULIZIE») quanto in mezzo («SIRAM Emergenza Ascensori»), e non ha
    /// altri usi: nessuno chiama «emergenza» un ambulatorio ordinario. Non si cerca
    /// «urgenza», che nei nomi delle strutture indica un reparto — «Medicina d'urgenza»
    /// — e non un recapito da usare solo nella circostanza per cui esiste.
    /// </remarks>
    private const string Emergency = "emergenza";

    /// <summary>
    /// Decide che cosa risponde a una voce.
    /// </summary>
    /// <param name="hasPersonName">La voce porta un nome o un cognome di persona.</param>
    /// <param name="isShortCode">La sorgente marca la voce come codice abbreviato.</param>
    /// <param name="description">Descrizione libera della voce, come sta in rubrica.</param>
    /// <remarks>
    /// <para>
    /// L'ordine delle regole è quello della certezza decrescente, e conta:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   Il contrassegno di numero breve viene dalla sorgente, non da un'interpretazione:
    ///   è l'unico dato esplicito e vince su tutto.
    ///   </description></item>
    ///   <item><description>
    ///   Fax ed emergenza vincono sulla persona, e non è un errore. Una riga «Fax —
    ///   Responsabile s.s.d.» con tanto di cognome resta un apparecchio fax: il numero
    ///   è quello dell'apparecchio, non della persona, e chi lo compone sente un
    ///   portante qualunque sia il nome scritto accanto.
    ///   </description></item>
    ///   <item><description>
    ///   Il nome di persona decide il caso ordinario; senza, resta il servizio.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static ContactKind Derive(bool hasPersonName, bool isShortCode, string? description)
    {
        if (isShortCode) return ContactKind.ShortCode;

        var testo = description?.Trim() ?? string.Empty;

        if (StartsWithWord(testo, Fax)) return ContactKind.Fax;
        if (testo.Contains(Emergency, StringComparison.OrdinalIgnoreCase)) return ContactKind.Emergency;

        return hasPersonName ? ContactKind.Person : ContactKind.Service;
    }

    /// <summary>
    /// Il testo comincia con la parola indicata, e non con una parola che la contiene.
    /// </summary>
    /// <param name="text">Testo da esaminare.</param>
    /// <param name="word">Parola cercata, in minuscolo.</param>
    /// <remarks>
    /// Il controllo sul carattere successivo evita che «Faxsimile» o un cognome che
    /// cominci per quelle lettere venga preso per un apparecchio. Una descrizione
    /// composta dalla sola parola è comunque un fax: è il modo in cui molte rubriche
    /// scrivono la riga del fax di reparto.
    /// </remarks>
    private static bool StartsWithWord(string text, string word)
    {
        if (!text.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Length == word.Length) return true;

        return !char.IsLetterOrDigit(text[word.Length]);
    }
}
