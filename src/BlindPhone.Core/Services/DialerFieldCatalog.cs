using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Un campo della rubrica mostrabile fra i contatti proposti sotto il campo di
/// composizione.
/// </summary>
/// <param name="Id">Chiave stabile, salvata nella configurazione.</param>
/// <param name="Label">Intestazione della colonna.</param>
/// <param name="Description">A che cosa serve, per chi sceglie nel pannello.</param>
/// <param name="Value">Come si ricava il valore da un contatto.</param>
public sealed record DialerField(
    string Id,
    string Label,
    string Description,
    Func<Contact, string?> Value);

/// <summary>
/// Campi che l'operatore può far comparire accanto ai contatti proposti.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nominativo e numero non sono qui, e non è una dimenticanza.</b> Una riga senza
/// nome non si riconosce e una senza numero non si chiama: sono la riga stessa, non
/// una scelta. Questo catalogo contiene solo ciò che si può aggiungere o togliere.
/// </para>
/// <para>
/// <b>Perché la scelta serve.</b> In un ospedale i nomi si assomigliano e gli
/// ambulatori pure: a un centralinista che cerca «cardiologia» serve vedere il
/// reparto, a un altro serve il padiglione perché deve dire al chiamante dove
/// presentarsi. Sono due postazioni con esigenze diverse e nessun valore
/// predefinito le accontenta entrambe.
/// </para>
/// <para>
/// <b>Non è solo questione di spazio a schermo.</b> Ogni colonna in più è una
/// freccia destra in più per chi legge la riga con lo screen reader, moltiplicata
/// per ogni contatto scorso in una giornata. Chi non vede paga le colonne inutili
/// più di chi guarda.
/// </para>
/// </remarks>
public static class DialerFieldCatalog
{
    private static readonly DialerField[] Catalog =
    [
        new("Presence", "Stato",
            "Libero, occupato o fuori servizio, quando il centralino lo comunica. E' il campo che "
            + "evita di passare una chiamata a un telefono gia' impegnato.",
            c => null),

        new("Description", "Descrizione",
            "Il campo descrizione della rubrica: di solito il nome dell'ambulatorio o del servizio. "
            + "Utile dove i nominativi si assomigliano.",
            c => c.Category),

        new("Department", "Reparto",
            "Reparto o unita' operativa di appartenenza.",
            c => c.Department),

        new("Location", "Collocazione",
            "Padiglione e piano, nella forma breve. Serve quando al chiamante bisogna dire dove "
            + "presentarsi, non solo a chi passare la chiamata.",
            c => c.LocationSpeech),

        new("RelatedTo", "Relativo a",
            "A chi o a che cosa il contatto fa riferimento, quando la rubrica lo indica.",
            c => c.RelatedTo),
    ];

    /// <summary>Tutti i campi disponibili, nell'ordine in cui si propongono.</summary>
    public static IReadOnlyList<DialerField> All => Catalog;

    /// <summary>
    /// Campi mostrati quando non è stato scelto niente.
    /// </summary>
    /// <remarks>
    /// Lo stato da solo: è quello che cambia una decisione — se l'interno è occupato
    /// l'operatore fa un'altra cosa — mentre gli altri campi aiutano a riconoscere il
    /// contatto giusto e servono solo dove i nominativi si assomigliano.
    /// </remarks>
    public static IReadOnlyList<string> Default => ["Presence"];

    /// <summary>
    /// Traduce le chiavi salvate nei campi corrispondenti, scartando le sconosciute.
    /// </summary>
    /// <param name="ids">Chiavi come compaiono nella configurazione, nell'ordine scelto.</param>
    /// <remarks>
    /// <para>
    /// <b>L'ordine è quello della configurazione, non quello del catalogo.</b> Conta
    /// perché chi legge la riga con lo screen reader la percorre da sinistra a destra
    /// e si ferma quando ha saputo quello che gli serve: il campo che decide — di
    /// solito lo stato, o il reparto dove i nominativi si assomigliano — deve stare
    /// subito dopo il numero, non in fondo dietro due campi che a quella postazione
    /// non dicono niente.
    /// </para>
    /// <para>
    /// La stabilità che serve davvero è quella nel tempo, non fra postazioni diverse:
    /// una volta scelto, l'ordine non cambia più da solo, e le frecce destra da
    /// contare restano sempre le stesse.
    /// </para>
    /// <para>
    /// Una chiave ripetuta produce una colonna sola: due colonne identiche sarebbero
    /// una freccia destra sprecata su ogni contatto scorso.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DialerField> Resolve(IEnumerable<string>? ids)
    {
        if (ids is null) return [];

        List<DialerField> scelti = [];

        foreach (var id in ids)
        {
            if (Find(id?.Trim()) is not { } campo) continue;
            if (scelti.Any(g => string.Equals(g.Id, campo.Id, StringComparison.Ordinal))) continue;

            scelti.Add(campo);
        }

        return scelti;
    }

    /// <summary>Descrizione di un campo, se il catalogo lo conosce.</summary>
    /// <param name="id">Chiave del campo.</param>
    public static DialerField? Find(string? id)
        => Catalog.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
}
