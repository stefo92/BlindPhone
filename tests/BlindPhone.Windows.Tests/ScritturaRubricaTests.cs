using BlindPhone.App;
using BlindPhone.Contacts;
using BlindPhone.Core.Domain;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Come i contatti letti finiscono nella copia locale.
/// </summary>
/// <remarks>
/// <para>
/// La scrittura sta in un punto solo perché i due percorsi che la usano — il
/// riallineamento periodico e il pulsante di importazione — sbagliavano in modi
/// diversi, e nessuno dei due sbagli si vede contando i contatti subito dopo. Si
/// vedono mesi dopo: come voci che nessuno riesce più a togliere dalla rubrica, o
/// come una separazione fra rubrica e numeri brevi che esisteva solo durante la
/// lettura.
/// </para>
/// </remarks>
public sealed class ScritturaRubricaTests : IDisposable
{
    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "blindphone-scrittura-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteContactRepository _rubrica;
    private readonly ContactsSyncService _servizio;

    public ScritturaRubricaTests()
    {
        Directory.CreateDirectory(_cartella);

        _rubrica = new SqliteContactRepository(Path.Combine(_cartella, "contacts.db"), "chiave-di-prova");

        _servizio = new ContactsSyncService(
            _rubrica,
            new SecretStore(_cartella),
            () => new AppSettings(),
            () => false,
            NullLogger<ContactsSyncService>.Instance,
            NullLogger<BlindPhone.Contacts.SqlServer.SqlServerContactSource>.Instance);
    }

    public void Dispose()
    {
        _servizio.Dispose();
        _rubrica.Dispose();
        try { Directory.Delete(_cartella, recursive: true); } catch (IOException) { }
    }

    private static string[] Colonne =>
    [
        "_site_group_code", "_entry_number", "_inactive", "_surname", "_forename",
        "_initials", "_extn_num", "_alt_extn_num", "_hier1_name", "_location",
        "_user01", "_yesno01", "_modn_date",
    ];

    private static Dictionary<string, string?> Riga(string entry, string descrizione, string interno, string breve)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["_site_group_code"] = "1",
            ["_entry_number"] = entry,
            ["_inactive"] = "0",
            ["_initials"] = descrizione,
            ["_extn_num"] = interno,
            ["_hier1_name"] = breve == "1" ? "NumeroBreve" : "Radiologia",
            ["_yesno01"] = breve,
            ["_modn_date"] = "2025-10-20T20:18:00",
        };

    private static SorgenteFinta Sorgente(params (string Entry, string Descrizione, string Interno, string Breve)[] righe)
        => new(
            CsvColumnMapping.Guess(Colonne),
            [.. righe.Select(r => (IReadOnlyDictionary<string, string?>)Riga(r.Entry, r.Descrizione, r.Interno, r.Breve))]);

    [Fact]
    public async Task OgniVoceVaSottoLaSorgenteCheDichiara()
    {
        // Scriverle tutte sotto il nome della sorgente le appiattirebbe in un gruppo
        // solo, e la separazione fra rubrica e numeri brevi esisterebbe soltanto
        // durante la lettura.
        var sorgente = Sorgente(
            ("1005", "Studio medico", "2094", "0"),
            ("10001", "MILANO POLICLINICO", "6019", "1"));

        var contatti = sorgente.Leggi();
        await _servizio.WriteAsync(sorgente, contatti, replace: true, CancellationToken.None);

        Assert.Equal(2, await _rubrica.CountAsync(CancellationToken.None));

        // Svuotare la sola sorgente dei numeri brevi non deve toccare la rubrica.
        Assert.Equal(1, await _rubrica.ClearAsync(sorgente.ShortCodeSourceName, CancellationToken.None));
        Assert.Equal(1, await _rubrica.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task INumeriBreviSparitiDallaTabellaSparisconoDallaPostazione()
    {
        // IL GUASTO CHE LA REGOLA EVITA.
        //
        // Svuotare i soli gruppi appena letti lascerebbe i numeri brevi di ieri il
        // giorno in cui l'ente li toglie: voci che nessuna lettura successiva nomina
        // piu', quindi che nessun riallineamento potrebbe piu' rimuovere.
        var conBrevi = Sorgente(("1005", "Studio medico", "2094", "0"), ("10001", "MILANO", "6019", "1"));
        await _servizio.WriteAsync(conBrevi, conBrevi.Leggi(), replace: true, CancellationToken.None);

        Assert.Equal(2, await _rubrica.CountAsync(CancellationToken.None));

        var senzaBrevi = Sorgente(("1005", "Studio medico", "2094", "0"));
        await _servizio.WriteAsync(senzaBrevi, senzaBrevi.Leggi(), replace: true, CancellationToken.None);

        Assert.Equal(1, await _rubrica.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SenzaSostituzioneLeVociPrecedentiRestano()
    {
        // E' la spunta "sostituisci i contatti importati in precedenza": tolta, una
        // seconda importazione si aggiunge invece di rimpiazzare.
        var prima = Sorgente(("1005", "Studio medico", "2094", "0"));
        await _servizio.WriteAsync(prima, prima.Leggi(), replace: true, CancellationToken.None);

        var seconda = Sorgente(("1006", "Segreteria", "2095", "0"));
        await _servizio.WriteAsync(seconda, seconda.Leggi(), replace: false, CancellationToken.None);

        Assert.Equal(2, await _rubrica.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RiscrivereLaStessaLetturaNonDuplicaNulla()
    {
        // L'identificativo stabile deve reggere anche qui: un'importazione ripetuta
        // aggiorna le voci esistenti invece di affiancarne una copia.
        var sorgente = Sorgente(("1005", "Studio medico", "2094", "0"), ("10001", "MILANO", "6019", "1"));

        await _servizio.WriteAsync(sorgente, sorgente.Leggi(), replace: true, CancellationToken.None);
        await _servizio.WriteAsync(sorgente, sorgente.Leggi(), replace: true, CancellationToken.None);

        Assert.Equal(2, await _rubrica.CountAsync(CancellationToken.None));
    }

    [Fact]
    public void LeSottoSorgentiDaSvuotareSonoQuelleDellaSorgente()
    {
        var sorgente = Sorgente(("1005", "Studio medico", "2094", "0"));

        Assert.Equal(
            [sorgente.Name, sorgente.ShortCodeSourceName],
            ContactsSyncService.SourceNamesOf(sorgente));
    }

    [Fact]
    public void LEsitoDistingueIContattiDaiNumeriBrevi()
    {
        // Senza la ripartizione, un totale di duemila voci su una rubrica che ne ha
        // milleduecento sembrerebbe un errore di importazione.
        var contatti = Sorgente(
            ("1005", "Studio medico", "2094", "0"),
            ("1006", "Segreteria", "2095", "0"),
            ("10001", "MILANO", "6019", "1")).Leggi();

        Assert.Equal("2 contatti e 1 numeri brevi", ContactsSyncService.Describe(contatti));
    }

    [Fact]
    public void SenzaNumeriBreviLEsitoNonNeParla()
    {
        var contatti = Sorgente(("1005", "Studio medico", "2094", "0")).Leggi();

        Assert.Equal("1 contatti", ContactsSyncService.Describe(contatti));
    }
}
