using BlindPhone.Contacts;
using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifiche sull'archivio delle ultime chiamate.
/// </summary>
/// <remarks>
/// L'archivio contiene dati di traffico e ha un periodo di conservazione da
/// dimostrare: qui si verifica che la cancellazione automatica elimini davvero il
/// vecchio e <b>solo</b> il vecchio, e che «mai» non cancelli nulla.
/// </remarks>
public sealed class CallHistoryRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 9, 0, 0, TimeSpan.FromHours(2));

    private readonly string _cartella;
    private readonly SqliteCallHistoryRepository _archivio;

    /// <summary>Prepara un archivio isolato e cifrato, come in esercizio.</summary>
    public CallHistoryRepositoryTests()
    {
        _cartella = Path.Combine(Path.GetTempPath(), "BlindPhone.Test", $"chiamate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cartella);

        _archivio = new SqliteCallHistoryRepository(Path.Combine(_cartella, "history.db"), "chiave-di-prova");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _archivio.Dispose();

        try
        {
            if (Directory.Exists(_cartella)) Directory.Delete(_cartella, recursive: true);
        }
        catch (IOException)
        {
            // Una cartella temporanea rimasta non e' un fallimento della prova.
        }
    }

    [Fact]
    public async Task UnaChiamataRegistrataSiRitrovaNelSuoElenco()
    {
        await _archivio.AddAsync(Voce(CallHistoryKind.Missed, "0212345678", Now), CancellationToken.None);

        var perse = await _archivio.GetAsync(CallHistoryKind.Missed, 50, CancellationToken.None);
        var ricevute = await _archivio.GetAsync(CallHistoryKind.Incoming, 50, CancellationToken.None);

        Assert.Single(perse);
        Assert.Equal("0212345678", perse[0].Number);
        Assert.Equal("Mario Rossi", perse[0].DisplayName);
        Assert.Empty(ricevute);
    }

    [Fact]
    public async Task LePiuRecentiVengonoPrima()
    {
        await _archivio.AddAsync(Voce(CallHistoryKind.Incoming, "111", Now.AddHours(-3)), CancellationToken.None);
        await _archivio.AddAsync(Voce(CallHistoryKind.Incoming, "222", Now.AddHours(-1)), CancellationToken.None);
        await _archivio.AddAsync(Voce(CallHistoryKind.Incoming, "333", Now.AddHours(-2)), CancellationToken.None);

        var elenco = await _archivio.GetAsync(CallHistoryKind.Incoming, 50, CancellationToken.None);

        // Chi apre l'elenco cerca l'ultima chiamata, non la prima: l'ordine inverso
        // costringerebbe a percorrerlo tutto con la sintesi.
        Assert.Equal(["222", "333", "111"], elenco.Select(v => v.Number));
    }

    [Fact]
    public async Task LaConservazioneEliminaSoloIlVecchio()
    {
        await _archivio.AddAsync(Voce(CallHistoryKind.Missed, "vecchia", Now.AddDays(-40)), CancellationToken.None);
        await _archivio.AddAsync(Voce(CallHistoryKind.Missed, "recente", Now.AddDays(-2)), CancellationToken.None);

        var rimosse = await _archivio.PurgeAsync(TimeSpan.FromDays(30), Now, CancellationToken.None);

        Assert.Equal(1, rimosse);

        var rimaste = await _archivio.GetAsync(CallHistoryKind.Missed, 50, CancellationToken.None);
        Assert.Single(rimaste);
        Assert.Equal("recente", rimaste[0].Number);
    }

    [Fact]
    public async Task MaiConservaTutto()
    {
        await _archivio.AddAsync(Voce(CallHistoryKind.Missed, "antica", Now.AddYears(-3)), CancellationToken.None);

        // "Mai" e' una scelta legittima quando la conservazione e' regolata dall'ente:
        // cancellare lo stesso sarebbe disobbedire a una decisione presa altrove.
        Assert.Equal(0, await _archivio.PurgeAsync(TimeSpan.Zero, Now, CancellationToken.None));
        Assert.Equal(1, await _archivio.CountAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task SiPuoSvuotareUnSoloElenco()
    {
        await _archivio.AddAsync(Voce(CallHistoryKind.Missed, "1", Now), CancellationToken.None);
        await _archivio.AddAsync(Voce(CallHistoryKind.Incoming, "2", Now), CancellationToken.None);
        await _archivio.AddAsync(Voce(CallHistoryKind.Outgoing, "3", Now), CancellationToken.None);

        var rimosse = await _archivio.ClearAsync(CallHistoryKind.Missed, CancellationToken.None);

        Assert.Equal(1, rimosse);
        Assert.Equal(2, await _archivio.CountAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task SiPuoSvuotareTutto()
    {
        await _archivio.AddAsync(Voce(CallHistoryKind.Missed, "1", Now), CancellationToken.None);
        await _archivio.AddAsync(Voce(CallHistoryKind.Outgoing, "2", Now), CancellationToken.None);

        Assert.Equal(2, await _archivio.ClearAsync(null, CancellationToken.None));
        Assert.Equal(0, await _archivio.CountAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task IlFileSuDiscoNonSiLeggeInChiaro()
    {
        await _archivio.AddAsync(Voce(CallHistoryKind.Missed, "0212345678", Now), CancellationToken.None);
        _archivio.Dispose();

        // Condivisione piena: il pool di SQLite tiene il file aperto in scrittura
        // anche dopo la Dispose, e una lettura esclusiva fallirebbe a intermittenza.
        await using var lettura = new FileStream(
            Path.Combine(_cartella, "history.db"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var bytes = new byte[lettura.Length];
        await lettura.ReadExactlyAsync(bytes);

        var testo = System.Text.Encoding.ASCII.GetString(bytes);

        // Chi copia il file da una postazione dismessa non deve trovarci dentro né i
        // numeri né la forma dell'archivio: la cifratura è dell'intero file, quindi
        // sparisce anche l'intestazione che lo dichiarava un database SQLite.
        Assert.DoesNotContain("0212345678", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("Mario Rossi", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("call_history", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite format 3", testo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurataEMotivoSopravvivonoAlSalvataggio()
    {
        await _archivio.AddAsync(
            new CallHistoryEntry(
                0, CallHistoryKind.Outgoing, "4021", null, Now, TimeSpan.FromSeconds(95), null, CallEndReason.Busy),
            CancellationToken.None);

        var voce = (await _archivio.GetAsync(CallHistoryKind.Outgoing, 1, CancellationToken.None))[0];

        Assert.Equal(TimeSpan.FromSeconds(95), voce.Duration);
        Assert.Equal(CallEndReason.Busy, voce.EndReason);
        Assert.Null(voce.DisplayName);
    }

    // -----------------------------------------------------------------------
    // Il registro non ha un tetto
    // -----------------------------------------------------------------------
    //
    // Segnalato dal campo: «il registro chiamate si riempie dopo 50 chiamate». Non
    // era l'archivio a fermarsi - queste prove lo dimostrano - ma la finestra, che ne
    // chiedeva cinquanta e non diceva che ce n'erano altre. L'archivio si svuota solo
    // in due modi: allo scadere della conservazione o su richiesta dell'operatore.

    [Fact]
    public async Task OltreCinquantaChiamate_VengonoRegistrateTutte()
    {
        for (var i = 0; i < 120; i++)
        {
            await _archivio.AddAsync(
                Voce(CallHistoryKind.Missed, $"02123456{i:00}", Now.AddMinutes(-i)), CancellationToken.None);
        }

        Assert.Equal(120, await _archivio.CountAsync(CallHistoryKind.Missed, CancellationToken.None));
    }

    [Fact]
    public async Task OltreCinquantaChiamate_SiPossonoRileggereTutte()
    {
        for (var i = 0; i < 120; i++)
        {
            await _archivio.AddAsync(
                Voce(CallHistoryKind.Missed, $"02123456{i:00}", Now.AddMinutes(-i)), CancellationToken.None);
        }

        // Il tetto della lettura non deve fermarsi a una pagina: chi chiede tutto
        // deve poter ottenere tutto.
        var tutte = await _archivio.GetAsync(CallHistoryKind.Missed, 500, CancellationToken.None);

        Assert.Equal(120, tutte.Count);

        // E la prima pagina resta la piu' recente, non una qualunque.
        var pagina = await _archivio.GetAsync(CallHistoryKind.Missed, 50, CancellationToken.None);

        Assert.Equal(50, pagina.Count);
        Assert.Equal(tutte[0].Number, pagina[0].Number);
    }

    [Fact]
    public async Task IlConteggioSiPuoChiedereSoloPerUnElenco()
    {
        // Serve alla finestra per dire «cinquanta di trecentododici» invece di
        // fermarsi in silenzio.
        await _archivio.AddAsync(Voce(CallHistoryKind.Missed, "021", Now), CancellationToken.None);
        await _archivio.AddAsync(Voce(CallHistoryKind.Missed, "022", Now), CancellationToken.None);
        await _archivio.AddAsync(Voce(CallHistoryKind.Incoming, "023", Now), CancellationToken.None);

        Assert.Equal(2, await _archivio.CountAsync(CallHistoryKind.Missed, CancellationToken.None));
        Assert.Equal(1, await _archivio.CountAsync(CallHistoryKind.Incoming, CancellationToken.None));
        Assert.Equal(0, await _archivio.CountAsync(CallHistoryKind.Outgoing, CancellationToken.None));
        Assert.Equal(3, await _archivio.CountAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task NienteSiPerdeFinoAllaScadenzaDellaConservazione()
    {
        // Le due sole vie di uscita dall'archivio: la conservazione e la mano
        // dell'operatore. Nessun tetto sul numero di voci.
        for (var i = 0; i < 80; i++)
        {
            await _archivio.AddAsync(
                Voce(CallHistoryKind.Outgoing, $"333000{i:00}", Now.AddDays(-i)), CancellationToken.None);
        }

        Assert.Equal(80, await _archivio.CountAsync(CallHistoryKind.Outgoing, CancellationToken.None));

        var rimosse = await _archivio.PurgeAsync(TimeSpan.FromDays(30), Now, CancellationToken.None);

        // Le voci vanno da oggi a settantanove giorni fa: restano quelle dei trenta
        // giorni conservati, oggi compreso, cioe' trentuno.
        Assert.Equal(49, rimosse);
        Assert.Equal(31, await _archivio.CountAsync(CallHistoryKind.Outgoing, CancellationToken.None));
    }

    private static CallHistoryEntry Voce(CallHistoryKind kind, string numero, DateTimeOffset quando)
        => new(0, kind, numero, "Mario Rossi", quando, TimeSpan.Zero, null, CallEndReason.NoAnswer);
}
