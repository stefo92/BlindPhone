using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Speech;

/// <summary>
/// I testi del quadro dei colleghi in turno: quelli che si leggono e quelli che si
/// ascoltano.
/// </summary>
/// <remarks>
/// <para>
/// Stessa divisione del resto del prodotto, e per la stessa ragione. A schermo i numeri
/// restano numeri, perché si leggono con gli occhi; negli annunci si leggono cifra per
/// cifra, perché altrimenti «quattromilaventuno» non si può né trascrivere né
/// ricomporre.
/// </para>
/// <para>
/// <b>La parola scelta per lo stato è «in turno», non «disponibile».</b> Dice quello che
/// il dato sa davvero: il telefono è registrato, qualcuno è alla postazione. Se dicesse
/// «disponibile» prometterebbe che la chiamata verrà presa, e non è un'informazione che
/// questa funzione possieda — il collega può essere già al telefono, o alla macchinetta
/// del caffè con il telefono acceso.
/// </para>
/// </remarks>
public static class ShiftBoardTexts
{
    /// <summary>Intestazione della sezione.</summary>
    public const string SectionTitle = "Colleghi in turno";

    /// <summary>Detto quando l'elenco non è ancora stato compilato.</summary>
    public const string NotConfigured =
        "Nessun collega da sorvegliare. Aggiungi gli interni in Impostazioni, scheda Colleghi";

    /// <summary>
    /// Detto quando il centralino risponde «raggiungibile» a qualunque indirizzo.
    /// </summary>
    /// <remarks>
    /// Non si mostrano stati e non si dice «sono tutti in turno»: si dice che non si può
    /// saperlo. È la stessa regola per cui una sottoscrizione rifiutata non diventa
    /// «fuori servizio» — un'informazione sbagliata è peggio di un'informazione mancante,
    /// perché ci si agisce sopra.
    /// </remarks>
    public const string CannotDistinguish =
        "Il centralino risponde per qualunque interno, anche per uno inesistente: "
        + "non è in grado di dire chi è in turno. Serve il collegamento all'API del centralino";

    /// <summary>Detto quando il centralino non tratta i sondaggi di raggiungibilità.</summary>
    public const string ProbeRefused =
        "Il centralino non risponde ai sondaggi sugli interni: "
        + "lo stato dei colleghi non è disponibile su questo impianto";

    /// <summary>Stato di una riga, nella forma che si legge a schermo.</summary>
    /// <param name="entry">Riga del quadro.</param>
    /// <returns>Per esempio "In turno, occupato" oppure "Non in turno".</returns>
    /// <remarks>
    /// Il dettaglio compare solo quando c'è: con il sondaggio non c'è mai, e ripetere
    /// «in turno, stato sconosciuto» su ogni riga aggiungerebbe una parola inutile a
    /// tutte per non dire niente su nessuna.
    /// </remarks>
    public static string Status(ShiftEntry entry)
    {
        var stato = entry.Duty switch
        {
            DutyState.OnDuty => "In turno",
            DutyState.OffDuty => "Non in turno",
            DutyState.NotConfigured => "Interno sconosciuto al centralino",
            _ => "Non rilevato",
        };

        if (entry.Duty != DutyState.OnDuty) return stato;

        var dettaglio = entry.Detail switch
        {
            PresenceState.Busy => "occupato",
            PresenceState.Ringing => "squilla",
            PresenceState.DoNotDisturb => "non disturbare",
            PresenceState.Away => "assente",
            _ => string.Empty,
        };

        return dettaglio.Length == 0 ? stato : $"{stato}, {dettaglio}";
    }

    /// <summary>
    /// Testo che lo screen reader legge quando il cursore arriva sulla riga.
    /// </summary>
    /// <param name="entry">Riga del quadro.</param>
    /// <param name="grouping">Come raggruppare le cifre del numero.</param>
    /// <remarks>
    /// <para>
    /// <b>Lo stato viene per primo</b>, prima del nome. È l'informazione per cui
    /// l'operatore sta percorrendo l'elenco: scorrendo dieci colleghi per trovare chi c'è,
    /// sentire prima il nome e poi lo stato costringe ad ascoltare due volte tanto.
    /// </para>
    /// <para>
    /// <b>Il numero si legge cifra per cifra</b>, come ovunque nel prodotto: è un numero
    /// che si compone, non una quantità.
    /// </para>
    /// </remarks>
    public static string Accessible(ShiftEntry entry, DigitGrouping grouping = DigitGrouping.Single)
    {
        var chi = string.IsNullOrWhiteSpace(entry.Name)
            ? SpeechFormatter.Digits(entry.Number, grouping)
            : $"{entry.Name}, interno {SpeechFormatter.Digits(entry.Number, grouping)}";

        return $"{Status(entry)}. {chi}";
    }

    /// <summary>
    /// Riassunto parlato del quadro: quanti ce ne sono, e chi manca.
    /// </summary>
    /// <param name="entries">Quadro corrente.</param>
    /// <param name="canTrust">Se agli stati si possa credere.</param>
    /// <remarks>
    /// <para>
    /// <b>Si nominano gli assenti, non i presenti.</b> Su una squadra di dodici persone
    /// l'elenco dei presenti è lungo e quello degli assenti è corto, ed è quest'ultimo a
    /// cambiare le decisioni: sapere che manca l'ufficio tecnico serve, sapere che ci
    /// sono gli altri undici non serve a niente.
    /// </para>
    /// <para>
    /// <b>Oltre quattro assenti si contano e basta.</b> Un elenco di nomi lungo quanto
    /// una frase intera non si ricorda, e chi lo voleva davvero ha la tabella a fianco.
    /// </para>
    /// </remarks>
    public static string Summary(IReadOnlyList<ShiftEntry> entries, bool canTrust = true)
    {
        if (entries.Count == 0) return NotConfigured;
        if (!canTrust) return CannotDistinguish;

        var inTurno = entries.Count(e => e.Duty == DutyState.OnDuty);
        var assenti = entries.Where(e => e.Duty == DutyState.OffDuty).ToList();
        var ignoti = entries.Count(e => e.Duty is DutyState.Unknown or DutyState.NotConfigured);

        var testa = inTurno == 1
            ? $"Un collega in turno su {entries.Count}"
            : $"{inTurno} colleghi in turno su {entries.Count}";

        var pezzi = new List<string> { testa };

        if (assenti.Count > 0)
        {
            pezzi.Add(assenti.Count <= 4
                ? "Non in turno: " + string.Join(", ", assenti.Select(a => a.DisplayName))
                : $"{assenti.Count} non in turno");
        }

        if (ignoti > 0)
        {
            pezzi.Add(ignoti == 1 ? "uno non rilevato" : $"{ignoti} non rilevati");
        }

        return string.Join(". ", pezzi);
    }

    /// <summary>
    /// Riga di intestazione della sezione, con il conteggio e l'ora dell'ultima verifica.
    /// </summary>
    /// <param name="entries">Quadro corrente.</param>
    /// <param name="updatedAt">Istante dell'ultima verifica, oppure <c>null</c>.</param>
    /// <param name="canTrust">Se agli stati si possa credere.</param>
    /// <remarks>
    /// <b>L'ora dell'ultima verifica non è un dettaglio tecnico.</b> Il quadro si
    /// aggiorna a intervalli, quindi mostra sempre il passato: senza dire di quanto, un
    /// elenco fermo per un guasto di rete è indistinguibile da un ufficio in cui non si è
    /// mosso nessuno.
    /// </remarks>
    public static string Header(
        IReadOnlyList<ShiftEntry> entries,
        DateTimeOffset? updatedAt,
        bool canTrust = true)
    {
        if (entries.Count == 0) return SectionTitle;
        if (!canTrust) return $"{SectionTitle}: stato non determinabile su questo centralino";

        var inTurno = entries.Count(e => e.Duty == DutyState.OnDuty);
        var testa = $"{SectionTitle}: {inTurno} su {entries.Count}";

        return updatedAt is { } quando
            ? $"{testa}  -  verificato alle {quando:HH:mm:ss}"
            : $"{testa}  -  in attesa della prima verifica";
    }
}
