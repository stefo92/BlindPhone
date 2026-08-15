using BlindPhone.Contacts;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Importazione di un'esportazione priva della riga dei titoli.
/// </summary>
/// <remarks>
/// <para>
/// Le righe usate qui vengono dall'esportazione vera consegnata il 10 agosto 2026,
/// che non ha alcuna riga di titoli: la prima riga è già un contatto. Il danno non
/// era solo l'abbinamento alla cieca — <b>quel primo contatto veniva consumato come
/// intestazione e spariva dalla rubrica a ogni importazione</b>. Su duemila voci non
/// si nota dal totale, e chi non c'è non risponde al telefono.
/// </para>
/// </remarks>
public sealed class RubricaSenzaIntestazioniTests : IDisposable
{
    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "blindphone-noheader-" + Guid.NewGuid().ToString("N"));

    public RubricaSenzaIntestazioniTests() => Directory.CreateDirectory(_cartella);

    public void Dispose()
    {
        try { Directory.Delete(_cartella, recursive: true); } catch (IOException) { }
    }

    private const string EsportazioneReale =
        "0000,3381112380,Area amministrativa supporto ai processi,False,True,ALESSIA,FERRARA,"
        + "Formazione e Sviluppo delle Competenze,Padiglione Clinica Medica,Piano Primo,0,\n"
        + "033002,,Segreteria Medicina Legale,False,False,,,Enti e associazioni - AOPR,,,0,\n"
        + "033096,033075,Laboratorio Presso Biblioteca,False,False,,,Medicina del Lavoro,,,0,\n";

    private string Scrivi(string contenuto)
    {
        var percorso = Path.Combine(_cartella, "export-phones.csv");
        File.WriteAllText(percorso, contenuto);
        return percorso;
    }

    [Fact]
    public void UnaPrimaRigaDiDatiVieneRiconosciutaComeTale()
    {
        var layout = CsvContactSource.Inspect(Scrivi(EsportazioneReale));

        Assert.True(layout.LooksHeaderless);
        Assert.Equal(12, layout.Columns.Count);
    }

    [Fact]
    public void UnFileConITitoliNonVieneScambiatoPerSenzaTitoli()
    {
        // La contromisura non deve rovinare i file fatti bene.
        var layout = CsvContactSource.Inspect(Scrivi("numero,nome,cognome\n4021,Mario,Rossi\n"));

        Assert.False(layout.LooksHeaderless);
        Assert.Equal(["numero", "nome", "cognome"], layout.Columns);
    }

    [Fact]
    public void LeColonneSenzaTitoloPortanoUnEsempioDalFile()
    {
        // "Colonna 6" da sola non dice niente a chi ascolta; con l'esempio si
        // riconosce la colonna senza vedere il file.
        var layout = CsvContactSource.Inspect(Scrivi(EsportazioneReale));

        Assert.Equal("1", layout.Columns[0]);
        Assert.Equal("Colonna 6, esempio: ALESSIA", layout.Labels[5]);
        Assert.Equal("Colonna 7, esempio: FERRARA", layout.Labels[6]);
    }

    [Fact]
    public async Task NessunContattoVienePerdutoComeIntestazione()
    {
        var percorso = Scrivi(EsportazioneReale);

        var perPosizione = new CsvColumnMapping
        {
            Extension = "1", RelatedTo = "2", Category = "3",
            FirstName = "6", LastName = "7", Department = "8",
            Building = "9", Floor = "10",
        };

        var contatti = await new CsvContactSource(percorso, perPosizione, hasHeaderRecord: false)
            .LoadAsync(CancellationToken.None);

        // Tre righe nel file, tre contatti in rubrica: prima erano due.
        Assert.Equal(3, contatti.Count);

        var alessia = contatti[0];
        Assert.Equal("0000", alessia.PrimaryNumber);
        Assert.Equal("ALESSIA FERRARA", alessia.EffectiveDisplayName);
        Assert.Equal("3381112380", alessia.RelatedTo);
        Assert.Equal("Area amministrativa supporto ai processi", alessia.Category);
        Assert.Equal("Formazione e Sviluppo delle Competenze", alessia.Department);
        Assert.Equal("Padiglione Clinica Medica, Piano Primo", alessia.LocationSpeech);
    }

    [Fact]
    public async Task LeVociSenzaNomeRestanoImportabili()
    {
        var percorso = Scrivi(EsportazioneReale);

        var perPosizione = new CsvColumnMapping
        {
            Extension = "1", Category = "3", FirstName = "6", LastName = "7", Department = "8",
        };

        var contatti = await new CsvContactSource(percorso, perPosizione, hasHeaderRecord: false)
            .LoadAsync(CancellationToken.None);

        var segreteria = contatti[1];

        Assert.Equal("033002", segreteria.PrimaryNumber);
        Assert.Equal("Segreteria Medicina Legale", segreteria.Category);

        // Senza nome ne' cognome il nominativo ripiega sul numero: e' il
        // comportamento noto, fissato qui perche' cambiarlo sia una scelta.
        Assert.Equal("033002", segreteria.EffectiveDisplayName);
    }

    [Fact]
    public async Task LAnteprimaMostraLaPrimaRigaCheAltrimentiSarebbePersa()
    {
        var percorso = Scrivi(EsportazioneReale);

        var perPosizione = new CsvColumnMapping { Extension = "1", FirstName = "6", LastName = "7" };

        var anteprima = await new CsvContactSource(percorso, perPosizione, hasHeaderRecord: false)
            .PreviewAsync(1, CancellationToken.None);

        Assert.Equal("ALESSIA FERRARA", Assert.Single(anteprima).EffectiveDisplayName);
    }
}
