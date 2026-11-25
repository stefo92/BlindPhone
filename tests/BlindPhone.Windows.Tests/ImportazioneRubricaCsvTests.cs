using BlindPhone.Contacts;
using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Riconoscimento delle colonne di un file di rubrica reale.
/// </summary>
/// <remarks>
/// <para>
/// Le intestazioni usate qui sono quelle di un'esportazione vera, consegnata il
/// 10 agosto 2026: <c>numero, , descrizione, False, False, nome, cognome, reparto,
/// padiglione, piano, 0,</c>. Non è un file ordinato, ed è appunto il motivo per cui
/// vale come collaudo — le rubriche degli enti escono così dai gestionali, con
/// colonne senza nome e residui di esportazione fra i titoli.
/// </para>
/// </remarks>
public sealed class ImportazioneRubricaCsvTests
{
    private static readonly string[] RubricaReale =
    [
        "numero", "relativo", "descrizione", "False", "False",
        "nome", "cognome", "reparto", "padiglione", "piano", "0", "",
    ];

    /// <summary>Il file di struttura consegnato, intestazione e riga di esempio.</summary>
    private const string FileReale =
        "numero,relativo,descrizione,False,False,nome,cognome,reparto,padiglione,piano,0,\n"
        + "2127,3300,Stanza Lavoro Infermieri Degenza Internistica - Settore blu,False,False,,,"
        + "Lungodegenza Critica,Padiglione Barbieri,Piano Secondo,0,\n";

    [Fact]
    public void UnaRubricaConColonneRipetuteONonNominateVieneLettaLoStesso()
    {
        // Prima della correzione questa riga sollevava ArgumentException: la
        // costruzione del dizionario delle intestazioni non tollerava ne' le due
        // colonne senza nome ne' il "False" ripetuto, e l'importazione si fermava
        // prima ancora di proporre la mappatura.
        var mappatura = CsvColumnMapping.Guess(RubricaReale);

        Assert.NotNull(mappatura);
    }

    [Fact]
    public void UnaColonnaRipetutaNonFaFallireIlRiconoscimento()
    {
        // Nel file vero le ripetizioni sono i residui "False": qui si verifica il
        // caso generale, con una colonna significativa che compare due volte. Vince
        // la prima, perche' e' quella che si incontra per prima scorrendo il file.
        string[] intestazioni = ["numero", "reparto", "nome", "reparto"];

        var mappatura = CsvColumnMapping.Guess(intestazioni);

        Assert.Equal("reparto", mappatura.Department);
    }

    [Fact]
    public void LeColonneItalianeVengonoRiconosciute()
    {
        var mappatura = CsvColumnMapping.Guess(RubricaReale);

        Assert.Equal("nome", mappatura.FirstName);
        Assert.Equal("cognome", mappatura.LastName);
        Assert.Equal("padiglione", mappatura.Building);
        Assert.Equal("piano", mappatura.Floor);
        Assert.Equal("numero", mappatura.Extension);
        Assert.Equal("reparto", mappatura.Department);
        Assert.Equal("descrizione", mappatura.Category);
    }

    /// <summary>
    /// Il file di struttura vero, importato per intero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Documenta quello che l'operatore trova davvero in rubrica per una voce senza
    /// nome, che nella rubrica consegnata è il caso normale: la maggior parte delle
    /// voci sono luoghi e servizi, non persone.
    /// </para>
    /// <para>
    /// <b>Il nominativo è la descrizione.</b> Non ci sono né nome né cognome, e la
    /// composizione non ha nulla da unire: ripiega sulla descrizione, che è ciò che
    /// dà un nome a una stanza di reparto. Prima ripiegava sul numero, e un elenco di
    /// risultati si leggeva come una sequenza di cifre che l'operatore aveva già
    /// davanti. Il numero resta l'ultima risorsa, per le voci che non hanno nemmeno
    /// la descrizione.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IlFileDiStrutturaVieneImportatoPerIntero()
    {
        var percorso = Path.Combine(Path.GetTempPath(), $"struttura-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(percorso, FileReale);

        try
        {
            var mappatura = CsvColumnMapping.Guess(CsvContactSource.ReadHeaders(percorso));
            var contatti = await new CsvContactSource(percorso, mappatura).LoadAsync(CancellationToken.None);

            var voce = Assert.Single(contatti);

            Assert.Equal("2127", voce.PrimaryNumber);
            Assert.Equal("3300", voce.RelatedTo);
            Assert.Equal("Stanza Lavoro Infermieri Degenza Internistica - Settore blu", voce.Category);
            Assert.Equal("Lungodegenza Critica", voce.Department);
            // I dati portano gia' le parole "Padiglione" e "Piano": non si ripetono.
            Assert.Equal("Padiglione Barbieri, Piano Secondo", voce.LocationSpeech);

            // Senza nome ne' cognome il nominativo e' la descrizione della voce.
            Assert.Equal("Stanza Lavoro Infermieri Degenza Internistica - Settore blu", voce.EffectiveDisplayName);

            // E non si sente due volte: la descrizione, essendo gia' il nominativo,
            // non viene ripetuta nella scheda letta.
            Assert.Equal(
                "Stanza Lavoro Infermieri Degenza Internistica - Settore blu, Lungodegenza Critica, "
                + "Padiglione Barbieri, Piano Secondo, riferimento 3300",
                voce.FullSpeechDescription);
        }
        finally
        {
            File.Delete(percorso);
        }
    }

    [Fact]
    public void UnaColonnaTipoDedicataBatteLaDescrizione()
    {
        // Dove ci sono entrambe, quella dedicata vince: la descrizione e' un ripiego
        // per le rubriche che non hanno un campo apposta, non la scelta preferita.
        string[] intestazioni = ["numero", "nome", "descrizione", "tipo"];

        Assert.Equal("tipo", CsvColumnMapping.Guess(intestazioni).Category);
    }

    [Fact]
    public async Task LaDescrizioneViaggiaFinoAllaSchedaLetta()
    {
        // Verifica di percorso completo: dalla colonna del file fino alla frase che
        // lo screen reader pronuncia, dove la descrizione deve arrivare subito dopo
        // il nome e prima del reparto.
        var percorso = Path.Combine(Path.GetTempPath(), $"rubrica-{Guid.NewGuid():N}.csv");

        await File.WriteAllTextAsync(
            percorso,
            "numero,descrizione,nome,cognome,reparto\n4021,Ambulatorio,Mario,Rossi,Radiologia\n");

        try
        {
            var mappatura = CsvColumnMapping.Guess(CsvContactSource.ReadHeaders(percorso));
            var sorgente = new CsvContactSource(percorso, mappatura);

            var contatti = await sorgente.LoadAsync(CancellationToken.None);
            var contatto = Assert.Single(contatti);

            Assert.Equal("Ambulatorio", contatto.Category);
            Assert.Equal("Mario Rossi", contatto.EffectiveDisplayName);
            Assert.StartsWith("Mario Rossi, Ambulatorio, Radiologia", contatto.FullSpeechDescription);
        }
        finally
        {
            File.Delete(percorso);
        }
    }

    [Fact]
    public void LaColonnaDescrizioneNonVieneScambiataPerIlNominativo()
    {
        // È la verifica che protegge la fusione nome + cognome: se "descrizione"
        // finisse nel nome visualizzato, prenderebbe il posto di "Mario Rossi" —
        // il nome visualizzato, quando c'è, vince sulla composizione — e in rubrica
        // comparirebbe la descrizione del servizio al posto della persona.
        var mappatura = CsvColumnMapping.Guess(RubricaReale);

        Assert.Null(mappatura.DisplayName);
        Assert.Equal("nome", mappatura.FirstName);
        Assert.Equal("cognome", mappatura.LastName);
    }

    [Fact]
    public void UnaColonnaNumeroBreveConSiENoVieneRiconosciuta()
    {
        // E' la forma dell'esportazione dei numeri brevi del centralino: il codice
        // da comporre in "Numero", il contrassegno in una colonna a parte.
        var mappatura = CsvColumnMapping.Guess(["Numero", "Telefono", "Descrizione", "Numero breve"]);

        Assert.Equal("Numero breve", mappatura.ShortCodeFlag);
        Assert.Equal("Numero", mappatura.Extension);
        Assert.Equal("Telefono", mappatura.WorkPhone);
    }

    [Fact]
    public void LaColonnaNumeroBreveCheContieneIlCodiceNonDiventaUnContrassegno()
    {
        // Qui "Numero breve" e' il numero da comporre: e' l'unica colonna con quella
        // parola, e il riconoscimento parziale la sceglie come numero. Dire
        // all'operatore che il file distingue i numeri brevi sarebbe falso.
        var mappatura = CsvColumnMapping.Guess(["Numero breve", "Destinazione", "descrizione"]);

        Assert.Equal("Numero breve", mappatura.Extension);
        Assert.Null(mappatura.ShortCodeFlag);
    }

    [Fact]
    public void LaParolaBreveDentroUnAltroTitoloNonBasta()
    {
        // Il contrassegno cambia tipo e sorgente della voce: si riconosce solo dal
        // titolo intero, mai da una parola trovata in mezzo.
        var mappatura = CsvColumnMapping.Guess(["numero", "descrizione", "Osservazione breve"]);

        Assert.Null(mappatura.ShortCodeFlag);
    }

    /// <summary>
    /// Dal file fino a come la voce si annuncia.
    /// </summary>
    /// <remarks>
    /// Prima l'importazione da file non assegnava nessun tipo di voce: un numero
    /// breve si annunciava come un contatto qualunque e finiva nella stessa sorgente
    /// della rubrica. Le stesse regole valgono anche per fax e servizi, come per le
    /// tabelle, la directory e Outlook.
    /// </remarks>
    [Fact]
    public async Task INumeriBreviDiUnFileSiAnnuncianoComeTali()
    {
        var percorso = Path.Combine(Path.GetTempPath(), $"brevi-{Guid.NewGuid():N}.csv");

        await File.WriteAllTextAsync(
            percorso,
            "Numero;Telefono;Descrizione;Numero breve\n"
            + "6005;003767211;ASOLA OSPEDALE;Sì\n"
            + "2094;;Studio medico;No\n"
            + "2095;;FAX Accettazione;\n");

        try
        {
            var mappatura = CsvColumnMapping.Guess(CsvContactSource.ReadHeaders(percorso));
            var sorgente = new CsvContactSource(percorso, mappatura);

            var contatti = await sorgente.LoadAsync(CancellationToken.None);

            var breve = Assert.Single(contatti, c => c.PrimaryNumber == "6005");
            Assert.Equal(ContactKind.ShortCode, breve.Kind);
            Assert.Equal(sorgente.ShortCodeSourceName, breve.Source);
            Assert.Equal("numero breve, ASOLA OSPEDALE", breve.FullSpeechDescription);

            var studio = Assert.Single(contatti, c => c.PrimaryNumber == "2094");
            Assert.Equal(ContactKind.Service, studio.Kind);
            Assert.Equal(sorgente.Name, studio.Source);

            Assert.Equal(ContactKind.Fax, Assert.Single(contatti, c => c.PrimaryNumber == "2095").Kind);
        }
        finally
        {
            File.Delete(percorso);
        }
    }

    /// <summary>
    /// Il numero per esteso di un numero breve arriva fino alla voce.
    /// </summary>
    /// <remarks>
    /// Sono le prime righe dell'esportazione vera dei numeri brevi del centralino:
    /// il codice in «Numero», il numero per esteso in «Telefono». La colonna
    /// «Telefono», abbinata a «Secondo numero», deve restare un recapito della voce
    /// che non prende il posto del codice, ed essere quello che la rubrica mostra
    /// come secondo numero.
    /// </remarks>
    [Fact]
    public async Task IlTelefonoDiUnNumeroBreveDiventaIlSecondoNumero()
    {
        var percorso = Path.Combine(Path.GetTempPath(), $"brevi-{Guid.NewGuid():N}.csv");

        await File.WriteAllTextAsync(
            percorso,
            "﻿\"Numero\";\"Telefono\";\"Descrizione\";\"Numero breve\"\n"
            + "\"9\";\"7004\";\"\";\"Sì\"\n"
            + "\"6000\";\"03665635293\";\"MDG MEDICINA INTERNA DIP.EMER/UR\";\"Sì\"\n");

        try
        {
            var mappatura = CsvColumnMapping.Guess(CsvContactSource.ReadHeaders(percorso));

            Assert.Equal("Numero", mappatura.Extension);
            Assert.Equal("Telefono", mappatura.WorkPhone);
            Assert.Equal("Numero breve", mappatura.ShortCodeFlag);

            var contatti = await new CsvContactSource(percorso, mappatura).LoadAsync(CancellationToken.None);

            var medicina = Assert.Single(contatti, c => c.PrimaryNumber == "6000");
            Assert.Equal(ContactKind.ShortCode, medicina.Kind);
            Assert.Equal(["03665635293"], medicina.OtherNumbers);

            var senzaDescrizione = Assert.Single(contatti, c => c.PrimaryNumber == "9");
            Assert.Equal(["7004"], senzaDescrizione.OtherNumbers);
        }
        finally
        {
            File.Delete(percorso);
        }
    }

    /// <summary>
    /// Le colonne della tabella <c>Elenco_AOPR</c> di una rubrica ospedaliera su SQL
    /// Server, nell'ordine in cui il server le restituisce.
    /// </summary>
    private static readonly string[] ElencoConPrefisso =
    [
        "_NumeroInterno", "_Niumero_Alternativo", "_Cognome", "_Nome", "_Struttura",
        "_Padiglione", "_Piano", "_Destinazione_uso", "_Numero_esterno", "id",
        "_Padiglione_Piano", "_Cognome_Nome", "_Struttura_Destinazione_uso", "_summary",
    ];

    [Fact]
    public void IlNomeNonVienePescatoDentroIlCognome()
    {
        // Il difetto: con il prefisso nessun titolo era uguale a "nome", e il
        // confronto parziale trovava "nome" dentro "_Cognome", che veniva prima.
        // Nome e cognome finivano sulla stessa colonna.
        var mappatura = CsvColumnMapping.Guess(ElencoConPrefisso);

        Assert.Equal("_Nome", mappatura.FirstName);
        Assert.Equal("_Cognome", mappatura.LastName);
    }

    [Fact]
    public void UnaTabellaConIlPrefissoVieneAbbinataComeLAvrebbeAbbinataChiLaConosce()
    {
        // E' l'abbinamento che l'operatore aveva fatto a mano su questa tabella.
        var mappatura = CsvColumnMapping.Guess(ElencoConPrefisso);

        Assert.Equal("_NumeroInterno", mappatura.Extension);
        Assert.Equal("_Niumero_Alternativo", mappatura.WorkPhone);
        Assert.Equal("_Destinazione_uso", mappatura.Category);
        Assert.Equal("_Struttura", mappatura.Department);
        Assert.Equal("_Padiglione", mappatura.Building);
        Assert.Equal("_Piano", mappatura.Floor);
    }

    [Fact]
    public void UnaColonnaSolaPerNomeECognomeNonDiventaAncheIlNome()
    {
        var mappatura = CsvColumnMapping.Guess(["Interno", "Cognome"]);

        Assert.Equal("Cognome", mappatura.LastName);
        Assert.Null(mappatura.FirstName);
    }

    [Fact]
    public void LaStrutturaVaNelRepartoSoloSeIlRepartoNonHaUnaColonnaSua()
    {
        var soloStruttura = CsvColumnMapping.Guess(["Interno", "Struttura"]);
        Assert.Equal("Struttura", soloStruttura.Department);
        Assert.Null(soloStruttura.Organization);

        var entrambe = CsvColumnMapping.Guess(["Interno", "Struttura", "Reparto"]);
        Assert.Equal("Reparto", entrambe.Department);
        Assert.Equal("Struttura", entrambe.Organization);
    }

    [Fact]
    public void LaDestinazioneDaSolaNonEUnaDescrizione()
    {
        // In un elenco di numeri brevi "Destinazione" e' il numero a cui porta il
        // codice: farne la descrizione darebbe alle voci un numero come nome.
        Assert.Null(CsvColumnMapping.Guess(["Codice", "Destinazione"]).Category);
    }

    [Fact]
    public void LaStessaColonnaNonEInsiemeNumeroESecondoNumero()
    {
        // "Numero esterno" e' l'unica colonna di numeri: il confronto la trova sia
        // come numero sia come numero esterno.
        var mappatura = CsvColumnMapping.Guess(["Numero esterno", "Descrizione"]);

        Assert.Equal("Numero esterno", mappatura.Extension);
        Assert.Null(mappatura.WorkPhone);
    }

    [Fact]
    public void UnaColonnaSenzaNomeNonVieneMaiScelta()
    {
        // Il riconoscimento parziale cerca sottostringhe: senza un filtro, la stringa
        // vuota e' contenuta in qualunque intestazione e verrebbe accettata per il
        // primo campo cercato, mappando una colonna a caso.
        var mappatura = CsvColumnMapping.Guess(RubricaReale);

        Assert.NotEqual(string.Empty, mappatura.Email);
        Assert.NotEqual(string.Empty, mappatura.MobilePhone);
        Assert.NotEqual(string.Empty, mappatura.JobTitle);
    }
}
