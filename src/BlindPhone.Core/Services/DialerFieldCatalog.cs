using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Un campo della rubrica mostrabile fra i contatti proposti sopra il campo di
/// composizione.
/// </summary>
/// <param name="Id">Chiave stabile, salvata nella configurazione.</param>
/// <param name="Label">Intestazione della colonna.</param>
/// <param name="Description">A che cosa serve il campo.</param>
/// <param name="Value">Come si ricava il valore da un contatto.</param>
public sealed record DialerField(
    string Id,
    string Label,
    string Description,
    Func<Contact, string?> Value);

/// <summary>
/// Campi che possono comparire accanto ai contatti proposti.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nominativo e numero non sono qui, e non è una dimenticanza.</b> Una riga senza
/// nome non si riconosce e una senza numero non si chiama: sono la riga stessa, non
/// un'aggiunta. Questo catalogo contiene solo ciò che può esserci o mancare.
/// </para>
/// <para>
/// <b>Quali compaiono lo decide la rubrica, non un secondo pannello.</b> I campi si
/// scelgono già una volta, abbinando le colonne all'importazione: chi non abbina il
/// reparto non ha reparti in rubrica, e una colonna «Reparto» sempre vuota sarebbe
/// solo una freccia destra in più. Chiedere di nuovo la stessa scelta in un elenco a
/// parte costringeva a farla due volte, e a tenerle allineate a mano.
/// </para>
/// <para>
/// <b>Non è solo questione di spazio a schermo.</b> Ogni colonna in più è una
/// freccia destra in più per chi legge la riga con lo screen reader, moltiplicata
/// per ogni contatto scorso in una giornata. Chi non vede paga le colonne inutili
/// più di chi guarda.
/// </para>
/// <para>
/// <b>Due campi restano deliberatamente vuoti dove ripeterebbero</b>, ed è la
/// conseguenza diretta della regola qui sopra. «Descrizione» tace sulle voci senza
/// persona, dove è già il nominativo della riga; «Tipo» tace quando il nominativo
/// contiene già la parola, cosa che sui fax accade quasi sempre perché è proprio da
/// quella parola che sono stati riconosciuti. Una cella vuota costa comunque una
/// freccia destra, ma molto meno di una che ripete quello che si è appena sentito.
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

        new("Kind", "Tipo",
            "Fax, emergenza o numero breve, quando la voce e' una di queste e il nominativo non lo "
            + "dice gia'. E' il campo che evita di comporre un fax credendo di chiamare un ufficio, "
            + "e che riconosce un codice abbreviato del centralino.",
            c => c.KindNotice),

        new("Description", "Descrizione",
            "Il campo descrizione della rubrica: di solito il nome dell'ambulatorio o del servizio. "
            + "Utile dove i nominativi si assomigliano. Sulle voci senza persona resta vuoto, perche' "
            + "li' la descrizione e' gia' il nominativo.",
            DescriptionUnlessItIsTheName),

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

    /// <summary>
    /// La descrizione, a meno che non sia già il nominativo della riga.
    /// </summary>
    /// <param name="contact">Contatto proposto.</param>
    /// <remarks>
    /// <para>
    /// <b>Da quando le voci senza persona si chiamano come la loro descrizione, la
    /// colonna le ripeteva parola per parola.</b> Il nominativo di
    /// «Ambulatorio Diabetologico» è quella stessa stringa, e la riga si leggeva
    /// «Ambulatorio Diabetologico, 2948, Ambulatorio Diabetologico»: una freccia destra
    /// spesa per sentire due volte quello che si era appena sentito, su ogni voce di
    /// servizio scorsa in una giornata. In una rubrica ospedaliera sono la maggioranza
    /// delle voci.
    /// </para>
    /// <para>
    /// La colonna resta però utile e non va tolta dal catalogo: sulle <i>persone</i> il
    /// nominativo è il cognome e la descrizione porta la qualifica — «Studio medico»,
    /// «Coordinatore Infermieristico» — che è esattamente quello che distingue due
    /// omonimi. Vuota dove ripeterebbe, piena dove aggiunge.
    /// </para>
    /// </remarks>
    private static string? DescriptionUnlessItIsTheName(Contact contact)
        => string.Equals(contact.Category, contact.EffectiveDisplayName, StringComparison.OrdinalIgnoreCase)
            ? null
            : contact.Category;

    /// <summary>Tutti i campi disponibili, nell'ordine in cui si leggono.</summary>
    public static IReadOnlyList<DialerField> All => Catalog;

    /// <summary>
    /// Campi da mostrare per una rubrica: lo stato sempre, gli altri se almeno una voce
    /// li porta.
    /// </summary>
    /// <param name="contacts">Tutte le voci della rubrica.</param>
    /// <remarks>
    /// <para>
    /// <b>Lo stato c'è sempre</b> perché non viene dalla rubrica ma dal centralino, e
    /// nessun abbinamento di colonne lo può dire. È anche il campo che cambia una
    /// decisione: se l'interno è occupato l'operatore fa un'altra cosa.
    /// </para>
    /// <para>
    /// <b>Gli altri seguono i dati, quindi l'abbinamento fatto all'importazione.</b>
    /// Basta una voce che porti il campo perché la colonna compaia: una soglia diversa
    /// farebbe sparire il reparto proprio dalla rubrica in cui lo hanno solo i pochi
    /// che servono, e non si saprebbe spiegare a chi ascolta perché.
    /// </para>
    /// <para>
    /// <b>L'ordine è sempre quello del catalogo</b>, che mette prima ciò che decide e
    /// poi ciò che aiuta a riconoscere. Conta perché chi legge la riga con lo screen
    /// reader la percorre da sinistra a destra e si ferma quando ha saputo quello che
    /// gli serve, e perché le frecce destra da contare restano le stesse finché la
    /// rubrica non cambia.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DialerField> Present(IEnumerable<Contact> contacts)
    {
        var mancanti = Catalog.Where(f => f.Id != "Presence").ToList();

        foreach (var contatto in contacts)
        {
            mancanti.RemoveAll(f => !string.IsNullOrWhiteSpace(f.Value(contatto)));

            // Su qualche migliaio di voci i campi si trovano quasi sempre nelle prime
            // righe: inutile scorrere il resto quando non manca piu' niente.
            if (mancanti.Count == 0) break;
        }

        return [.. Catalog.Where(f => !mancanti.Contains(f))];
    }

    /// <summary>Descrizione di un campo, se il catalogo lo conosce.</summary>
    /// <param name="id">Chiave del campo.</param>
    public static DialerField? Find(string? id)
        => Catalog.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
}
