namespace BlindPhone.Contacts;

/// <summary>
/// Una rubrica di gestionale riconosciuta dalla forma delle sue colonne.
/// </summary>
/// <param name="Name">Come si chiama il profilo, per dirlo all'operatore.</param>
/// <param name="Description">
/// Che cosa il profilo ha riconosciuto e che cosa ne consegue. Viene pronunciata:
/// dice all'operatore perché gli abbinamenti sono già a posto e che cosa
/// controllare comunque.
/// </param>
/// <param name="Mapping">Abbinamento proposto, completo delle colonne strutturali.</param>
public sealed record ContactSourceProfile(
    string Name,
    string Description,
    CsvColumnMapping Mapping)
{
    /// <summary>
    /// Riconosce il profilo di una tabella dalle sue colonne.
    /// </summary>
    /// <param name="columns">Colonne della tabella, in qualunque ordine.</param>
    /// <returns>Il profilo riconosciuto, oppure <c>null</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Il riconoscimento pretende tutte le colonne dell'impronta</b>, non la
    /// maggioranza. Un profilo riconosciuto per sbaglio è peggio di nessun profilo:
    /// produrrebbe abbinamenti sicuri di sé su colonne che contengono altro, e
    /// l'operatore non ha modo di sapere che la sicurezza è mal riposta.
    /// </para>
    /// </remarks>
    public static ContactSourceProfile? Recognize(IReadOnlyList<string> columns)
    {
        var presenti = new HashSet<string>(
            columns.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var profilo in Known)
        {
            if (profilo.Fingerprint.All(presenti.Contains)) return profilo.Build();
        }

        return null;
    }

    /// <summary>
    /// La tabella corrisponde a un profilo noto.
    /// </summary>
    /// <param name="columns">Colonne della tabella.</param>
    public static bool IsKnown(IReadOnlyList<string> columns) => Recognize(columns) is not null;

    private sealed record Candidate(string[] Fingerprint, Func<ContactSourceProfile> Build);

    // -----------------------------------------------------------------------
    // Rubrica di centralino "SQL Server Phonebook"
    // -----------------------------------------------------------------------

    /// <summary>
    /// Colonne che identificano senza ambiguità la tabella <c>directory</c> di una
    /// rubrica di centralino "SQL Server Phonebook".
    /// </summary>
    /// <remarks>
    /// Sono i quattro nomi che nessun'altra tabella mette insieme: la chiave a due
    /// campi della voce, il cognome e l'interno. Bastano perché lo schema è quello
    /// generato dal prodotto, identico in ogni installazione.
    /// </remarks>
    private static readonly string[] SigmaFingerprint =
        ["_site_group_code", "_entry_number", "_surname", "_extn_num"];

    /// <summary>
    /// I profili riconosciuti, nell'ordine in cui si tentano.
    /// </summary>
    /// <remarks>
    /// Dichiarato dopo le impronte che usa: gli inizializzatori dei campi statici
    /// corrono in ordine di dichiarazione, e metterlo prima gli farebbe leggere
    /// un'impronta ancora nulla.
    /// </remarks>
    private static readonly Candidate[] Known =
    [
        new(SigmaFingerprint, BuildSigma),
    ];

    /// <summary>
    /// Abbinamento per la rubrica di centralino "SQL Server Phonebook".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Due colonne non contengono quello che il loro nome dice</b>, e sono proprio
    /// quelle su cui si regge la rubrica:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <c>_initials</c> non contiene iniziali: contiene la descrizione libera della
    ///   voce — «Studio medico», «Ambulatorio Diabetologico», «FAX Segreteria». È il
    ///   campo che dà un nome alle voci senza persona, che in una rubrica ospedaliera
    ///   sono la maggioranza.
    ///   </description></item>
    ///   <item><description>
    ///   <c>_user01</c> è uno dei venti campi liberi che il prodotto lascia all'ente, e
    ///   qui contiene il piano. Nessun nome di colonna può dirlo: lo dice il dato, e
    ///   per questo il profilo va confermato guardando l'anteprima.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Le colonne vuote non vengono abbinate.</b> Lo schema prevede cellulare, fax,
    /// posta elettronica, gruppi e cinque livelli di gerarchia; in questa rubrica sono
    /// tutti vuoti tranne il primo livello. Abbinarli comunque riempirebbe la
    /// procedura guidata di voci che non producono nulla, e chi la percorre
    /// ascoltando le pagherebbe una per una.
    /// </para>
    /// </remarks>
    private static ContactSourceProfile BuildSigma() => new(
        "Rubrica di centralino (SQL Server Phonebook)",
        "Riconosciuta una rubrica di centralino: gli abbinamenti sono gia' quelli dello schema, "
        + "compresa la descrizione della voce, che in questa rubrica sta nella colonna delle iniziali. "
        + "Il piano viene da un campo libero dell'ente: controlla l'anteprima, e' l'unica voce che nessuno "
        + "schema puo' garantire.",
        new CsvColumnMapping
        {
            Extension = "_extn_num",
            WorkPhone = "_alt_extn_num",
            LastName = "_surname",
            FirstName = "_forename",
            Category = "_initials",
            Department = "_hier1_name",
            Building = "_location",
            Floor = "_user01",

            EntryKey = "_entry_number",
            ShortCodeFlag = "_yesno01",
            InactiveFlag = "_inactive",
            UpdatedAt = "_modn_date",
        });
}
