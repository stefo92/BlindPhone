using BlindPhone.Contacts;
using BlindPhone.Contacts.SqlBackup;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Lettore finto: restituisce righe già pronte, come farebbe un server.
/// </summary>
internal sealed class LettoreFinto : ISqlBackupReader
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string?>> _righe;

    public LettoreFinto(params IReadOnlyDictionary<string, string?>[] righe) => _righe = righe;

    public string FormatDescription => "Prova";

    public IReadOnlyList<SqlTableInfo> Tables => [new("dbo.directory", [], _righe.Count)];

    public IEnumerable<IReadOnlyDictionary<string, string?>> ReadRows(
        string table,
        int? limit,
        CancellationToken cancellationToken)
        => limit is { } n ? _righe.Take(n) : _righe;

    public void Dispose() { }
}

/// <summary>Sorgente di prova costruita sopra il lettore finto.</summary>
internal sealed class SorgenteFinta(
    CsvColumnMapping mappatura,
    params IReadOnlyDictionary<string, string?>[] righe)
    : TabularContactSource("dbo.directory", mappatura, "prova", null)
{
    private readonly IReadOnlyDictionary<string, string?>[] _righe = righe;

    public override string Name => "Gestionale di prova";

    protected override ISqlBackupReader OpenReader() => new LettoreFinto(_righe);

    public override Task<SourceTestResult> TestAsync(CancellationToken cancellationToken)
        => Task.FromResult(new SourceTestResult(true, "prova"));

    public IReadOnlyList<Contact> Leggi() => Read(limit: null, CancellationToken.None);
}

/// <summary>
/// Lettura di una rubrica di centralino, come sta davvero nel gestionale di un ente.
/// </summary>
/// <remarks>
/// <para>
/// Le righe di prova riproducono la tabella <c>directory</c> di una rubrica di
/// centralino "SQL Server Phonebook", con i nomi di colonna veri e i contenuti veri:
/// la descrizione dentro il campo che si chiama «iniziali», il piano dentro un campo
/// libero, i codici abbreviati mescolati ai contatti e le annotazioni infilate nel
/// campo del secondo numero.
/// </para>
/// <para>
/// Sono i casi su cui la lettura sbagliava, e ciascuno sbagliava in silenzio: voci
/// che si sovrascrivevano a vicenda, recapiti che non arrivavano, righe di
/// intestazione importate come contatti.
/// </para>
/// </remarks>
public sealed class RubricaDiGestionaleTests
{
    private static readonly string[] ColonneDirectory =
    [
        "_site_group_code", "_entry_number", "_inactive", "_surname", "_forename",
        "_initials", "_extn_num", "_alt_extn_num", "_hier1_name", "_location",
        "_user01", "_yesno01", "_modn_date",
    ];

    private static Dictionary<string, string?> Riga(
        string entry,
        string? cognome = null,
        string? nome = null,
        string? descrizione = null,
        string? interno = null,
        string? secondo = null,
        string? struttura = null,
        string? padiglione = null,
        string? piano = null,
        string breve = "0",
        string inattivo = "0")
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["_site_group_code"] = "1",
            ["_entry_number"] = entry,
            ["_inactive"] = inattivo,
            ["_surname"] = cognome,
            ["_forename"] = nome,
            ["_initials"] = descrizione,
            ["_extn_num"] = interno,
            ["_alt_extn_num"] = secondo,
            ["_hier1_name"] = struttura,
            ["_location"] = padiglione,
            ["_user01"] = piano,
            ["_yesno01"] = breve,
            ["_modn_date"] = "2025-10-20T20:18:00",
        };

    private static CsvColumnMapping Profilo() => CsvColumnMapping.Guess(ColonneDirectory);

    // -----------------------------------------------------------------------
    // Riconoscimento del profilo
    // -----------------------------------------------------------------------

    [Fact]
    public void LoSchemaDelGestionaleVieneRiconosciutoInvecediIndovinato()
    {
        var profilo = ContactSourceProfile.Recognize(ColonneDirectory);

        Assert.NotNull(profilo);
        Assert.Contains("centralino", profilo.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IlProfiloAbbinaLeDueColonneCheIlNomeNonDescrive()
    {
        var mappatura = Profilo();

        // "_initials" non contiene iniziali ma la descrizione della voce, e "_user01"
        // e' un campo libero che questo ente usa per il piano. Sono i due abbinamenti
        // che nessun riconoscimento per nome puo' azzeccare.
        Assert.Equal("_initials", mappatura.Category);
        Assert.Equal("_user01", mappatura.Floor);
    }

    [Fact]
    public void SenzaIlProfiloIlRiconoscimentoPerNomeAbbinerebbeLaColonnaSbagliata()
    {
        // Fissa il motivo per cui il profilo esiste: fra le colonne che contengono
        // "ext" ce n'e' una che non e' un numero, e l'euristica pescherebbe quella.
        string[] senzaImpronta = ["_extn_primary_user", "_extn_site", "_extn_num", "_surname"];

        Assert.Null(ContactSourceProfile.Recognize(senzaImpronta));
        Assert.Equal("_extn_primary_user", CsvColumnMapping.Guess(senzaImpronta).Extension);
    }

    [Fact]
    public void UnaTabellaQualunqueNonVieneScambiataPerUnaRubricaDiCentralino()
    {
        // Il riconoscimento pretende tutte le colonne dell'impronta: un profilo
        // riconosciuto per sbaglio produrrebbe abbinamenti sicuri di se' su colonne
        // che contengono altro.
        Assert.Null(ContactSourceProfile.Recognize(["_surname", "_extn_num"]));
        Assert.Null(ContactSourceProfile.Recognize(["nome", "cognome", "numero"]));
    }

    // -----------------------------------------------------------------------
    // Identita' delle voci
    // -----------------------------------------------------------------------

    [Fact]
    public void DueVociSulLoStessoInternoRestanoDueVoci()
    {
        // IL CASO CHE FACEVA SPARIRE CINQUANTUNO VOCI SU DUEMILA.
        //
        // Senza nome di persona, due righe con lo stesso interno producevano la stessa
        // identita' e si sovrascrivevano a vicenda: l'ultima letta restava, l'altra
        // spariva dalla rubrica senza che nulla lo segnalasse.
        var contatti = new SorgenteFinta(
            Profilo(),
            Riga("1247", descrizione: "accettazione", interno: "2091"),
            Riga("1248", descrizione: "Medicina Interna deg.:accettazione", interno: "2091")).Leggi();

        Assert.Equal(2, contatti.Count);
        Assert.NotEqual(contatti[0].Id, contatti[1].Id);
    }

    [Fact]
    public void LIdentitaNonCambiaSeCambiaLaDescrizione()
    {
        // I preferiti e i numeri rapidi assegnati dall'operatore sono attaccati
        // all'identificativo: se cambiasse a ogni correzione del testo in rubrica,
        // andrebbero persi senza che nessuno colleghi le due cose.
        var prima = new SorgenteFinta(
            Profilo(), Riga("1247", descrizione: "accettazione", interno: "2091")).Leggi();

        var dopo = new SorgenteFinta(
            Profilo(), Riga("1247", descrizione: "Accettazione prenotazioni", interno: "2094")).Leggi();

        Assert.Equal(prima[0].Id, dopo[0].Id);
    }

    // -----------------------------------------------------------------------
    // Recapiti
    // -----------------------------------------------------------------------

    [Fact]
    public void IlSecondoNumeroArrivaInRubrica()
    {
        // Prima non c'era dove metterlo nelle impostazioni, quindi dal server si
        // importava un solo numero per contatto: su una rubrica vera erano
        // millequattrocento recapiti irraggiungibili.
        var voce = Assert.Single(new SorgenteFinta(
            Profilo(),
            Riga("1006", cognome: "CRISCUOLI", descrizione: "Segreteria",
                 interno: "033265", secondo: "2517")).Leggi());

        Assert.Equal(2, voce.Numbers.Count);
        Assert.Equal("033265", voce.PrimaryNumber);
        Assert.Contains(voce.Numbers, n => n.Number == "2517" && n.Kind == PhoneNumberKind.Work);
    }

    [Theory]
    [InlineData("breve 6583")]
    [InlineData("nr breve 6041")]
    [InlineData("2876   2877")]
    public void UnAnnotazioneNelCampoDelNumeroFinisceNelleNoteENonFraIRecapiti(string annotazione)
    {
        // Metterla fra i recapiti significherebbe offrirla come destinazione di un
        // trasferimento, cioe' comporre qualcosa che non e' un numero mentre un
        // chiamante e' in attesa.
        var voce = Assert.Single(new SorgenteFinta(
            Profilo(),
            Riga("2247", descrizione: "Coordinatore", interno: "2247", secondo: annotazione)).Leggi());

        Assert.Equal("2247", Assert.Single(voce.Numbers).Number);
        Assert.Equal(annotazione, voce.Notes);
    }

    [Theory]
    [InlineData("0521.033533")]
    [InlineData("+39 0521 703111")]
    [InlineData("#03393763815")]
    [InlineData("**910")]
    public void UnNumeroScrittoConLeSpaziatureDUsoRestaUnNumero(string numero)
    {
        // Rifiutarlo toglierebbe al contatto il suo recapito, e un contatto senza
        // recapiti sparisce dalla rubrica: un danno silenzioso, che si scopre quando
        // qualcuno non si riesce piu' a chiamare.
        var voce = Assert.Single(new SorgenteFinta(
            Profilo(), Riga("2180", descrizione: "Segreteria", interno: numero)).Leggi());

        Assert.Equal(numero, voce.PrimaryNumber);
        Assert.Null(voce.Notes);
    }

    [Fact]
    public void LaRigaDiIntestazioneRimastaFraIDatiNonDiventaUnContatto()
    {
        // Nella rubrica consegnata c'e' davvero: qualcuno ha importato il file
        // compresa la sua prima riga, e in rubrica compariva una voce chiamata
        // "Description" con numero "Number".
        var contatti = new SorgenteFinta(
            Profilo(),
            Riga("10000", descrizione: "Description", interno: "Number", secondo: "Expansion", breve: "1"),
            Riga("10001", descrizione: "MILANO DITTA ORTO", interno: "6447", secondo: "00264742370", breve: "1"))
            .Leggi();

        Assert.Equal("6447", Assert.Single(contatti).PrimaryNumber);
    }

    // -----------------------------------------------------------------------
    // Righe da non importare
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaVoceDisattivataNonEntraInRubrica()
    {
        // Resta in tabella perche' il gestionale ne conserva lo storico. In rubrica
        // sarebbe un numero che non risponde piu', indistinguibile dagli altri finche'
        // non lo si compone.
        var contatti = new SorgenteFinta(
            Profilo(),
            Riga("1500", descrizione: "Ufficio chiuso", interno: "2500", inattivo: "1"),
            Riga("1501", descrizione: "Ufficio aperto", interno: "2501")).Leggi();

        Assert.Equal("2501", Assert.Single(contatti).PrimaryNumber);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("True")]
    [InlineData("true")]
    [InlineData("Si")]
    [InlineData("YES")]
    public void IlContrassegnoSiRiconosceInTutteLeFormeInCuiArriva(string vero)
    {
        // Lo stesso bit arriva come "1" da SQL Server, come "True" dal lettore che lo
        // converte in testo e come "Si" da un CSV compilato a mano. Riconoscerne una
        // sola significherebbe importare come contatti tutti i numeri brevi.
        var voce = Assert.Single(new SorgenteFinta(
            Profilo(),
            Riga("10001", descrizione: "MILANO POLICLINICO", interno: "6019", breve: vero)).Leggi());

        Assert.Equal(ContactKind.ShortCode, voce.Kind);
    }

    // -----------------------------------------------------------------------
    // Le due rubriche dentro la stessa tabella
    // -----------------------------------------------------------------------

    [Fact]
    public void INumeriBreviRestanoUnaSorgenteASe()
    {
        // Sono l'elenco esterno dell'ente e in una rubrica ospedaliera sono spesso piu'
        // numerosi dei contatti veri: mescolarli significa che chi cerca un reparto se
        // li ritrova in mezzo ai risultati.
        var sorgente = new SorgenteFinta(
            Profilo(),
            Riga("1005", cognome: "CERESINI", descrizione: "Responsabile SSD", interno: "033194"),
            Riga("10001", descrizione: "MILANO DITTA ORTO", interno: "6447", secondo: "00264742370", breve: "1"));

        var contatti = sorgente.Leggi();

        Assert.Equal(sorgente.Name, contatti[0].Source);
        Assert.Equal(sorgente.ShortCodeSourceName, contatti[1].Source);

        // Entrambi i nomi vanno svuotati a ogni riallineamento, anche quello che questa
        // lettura non ha prodotto.
        Assert.Equal([sorgente.Name, sorgente.ShortCodeSourceName], sorgente.SourceNames);
    }

    // -----------------------------------------------------------------------
    // Come la voce si presenta a chi ascolta
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaVoceSenzaPersonaSiChiamaComeLaSuaDescrizione()
    {
        var voce = Assert.Single(new SorgenteFinta(
            Profilo(),
            Riga("2071", descrizione: "Ambulatorio Diabetologico", interno: "2948",
                 struttura: "Trattamento Intensivo del Diabete", padiglione: "Torre delle Medicine",
                 piano: "Terra")).Leggi());

        Assert.Equal("Ambulatorio Diabetologico", voce.EffectiveDisplayName);
        Assert.Equal(ContactKind.Service, voce.Kind);

        // In questa rubrica i padiglioni sono scritti col solo nome — "Torre delle
        // Medicine", "Rasori" — quindi la parola viene anteposta; dove il dato la
        // porta gia', non si ripete.
        Assert.Equal("padiglione Torre delle Medicine, piano Terra", voce.LocationSpeech);
    }

    [Fact]
    public void UnFaxVieneRiconosciutoDallaDescrizione()
    {
        var voce = Assert.Single(new SorgenteFinta(
            Profilo(), Riga("1824", descrizione: "Fax Accettazione DH", interno: "2678")).Leggi());

        Assert.Equal(ContactKind.Fax, voce.Kind);

        // La parola non si ripete: il nominativo la dice gia', perche' e' proprio da
        // quella che il tipo e' stato ricavato.
        Assert.Equal("Fax Accettazione DH", voce.FullSpeechDescription);
    }

    [Fact]
    public void UnNumeroBreveSiAnnunciaComeTale()
    {
        // Qui la parola non e' nel dato: viene dal contrassegno della sorgente, quindi
        // l'avvertimento e' l'unico modo per saperlo.
        var voce = Assert.Single(new SorgenteFinta(
            Profilo(),
            Riga("10005", descrizione: "MILANO POLICLINICO", interno: "6019",
                 secondo: "00255031", breve: "1")).Leggi());

        Assert.StartsWith("numero breve, MILANO POLICLINICO", voce.FullSpeechDescription);
    }

    [Fact]
    public void LaDataDiAggiornamentoVieneDalGestionaleENonDallImportazione()
    {
        // Il momento dell'importazione dice quando BlindPhone ha letto, non quando
        // l'ente ha aggiornato.
        var voce = Assert.Single(new SorgenteFinta(
            Profilo(), Riga("1005", cognome: "CERESINI", interno: "033194")).Leggi());

        Assert.Equal(new DateTime(2025, 10, 20, 20, 18, 0), voce.UpdatedAt.DateTime);
    }

    // -----------------------------------------------------------------------
    // Scelta dell'oggetto da cui leggere
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaVistaVuotaSiRiconosceDallElenco()
    {
        // LA VISTA CHE HA FATTO NASCERE TUTTO QUESTO.
        //
        // Pubblicata per i sistemi esterni, univa la rubrica alla segreteria di
        // ciascuna voce; in quell'ente nessuna voce aveva una segreteria, e
        // restituiva zero righe su duemila. Aveva le colonne giuste e il nome giusto,
        // e nell'elenco era indistinguibile dalle altre.
        var vista = new SqlTableInfo("dbo.speechattendant", ["ID", "firstName"], 0, IsView: true);

        Assert.StartsWith("VUOTA", vista.Description);
        Assert.Contains("vista", vista.Description);
    }

    [Fact]
    public void UnaVistaCheNonCompilaSiRiconosceDallElenco()
    {
        var rotta = new SqlTableInfo("dbo.display_directory", ["a"], null, IsView: true, IsReadable: false);

        Assert.StartsWith("NON LEGGIBILE", rotta.Description);
    }

    [Fact]
    public void UnaTabellaPopolataSiDescriveComeSempre()
    {
        var tabella = new SqlTableInfo("dbo.directory", ["a", "b"], 2058);

        Assert.Equal("dbo.directory — tabella, 2 colonne, 2058 righe", tabella.Description);
    }
}
