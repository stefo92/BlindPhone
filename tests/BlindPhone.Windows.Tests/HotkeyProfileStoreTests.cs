using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifiche sul deposito dei profili di scorciatoie.
/// </summary>
/// <remarks>
/// Ogni prova lavora in una cartella temporanea propria, mai su
/// <c>%APPDATA%\BlindPhone</c>: quella cartella contiene la configurazione reale
/// della postazione, e un test che la tocca mette il posto operatore fuori servizio
/// in un modo che si scopre solo alla chiamata successiva.
/// </remarks>
public sealed class HotkeyProfileStoreTests : IDisposable
{
    private readonly string _cartella;

    /// <summary>Prepara una cartella dati isolata.</summary>
    public HotkeyProfileStoreTests()
    {
        _cartella = Path.Combine(Path.GetTempPath(), "BlindPhone.Test", $"tasti-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cartella);
    }

    /// <inheritdoc />
    public void Dispose()
    {
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
    public void AlPrimoAvvioIlFileVieneScrittoConIProfiliPredefiniti()
    {
        var deposito = new HotkeyProfileStore(_cartella);

        Assert.True(File.Exists(deposito.FilePath));
        Assert.Contains("Standard", deposito.ProfileNames);
        Assert.Contains("Solo primo piano", deposito.ProfileNames);
        Assert.Equal("Standard", deposito.Active.Name);
        Assert.Equal("F2", deposito.Active.Get(HotkeyCommand.Answer, HotkeyScope.Local)?.Key);
    }

    [Fact]
    public void IlProfiloAttivoCambiaContenutoSenzaCambiareIstanza()
    {
        var deposito = new HotkeyProfileStore(_cartella);
        var riferimento = deposito.Active;

        var eventi = 0;
        deposito.ActiveProfileChanged += (_, _) => eventi++;

        var bozza = deposito.Get("Standard")!;
        bozza.Name = "Turno mattina";
        bozza.Set(new HotkeyBinding(HotkeyCommand.Answer, "F1", HotkeyModifiers.None, HotkeyScope.Local));

        Assert.True(deposito.SaveAndActivate(bozza));

        // Se l'istanza cambiasse, la finestra principale continuerebbe a usare la
        // mappa vecchia: le modifiche sembrerebbero salvate e non avrebbero effetto.
        Assert.Same(riferimento, deposito.Active);
        Assert.Equal(1, eventi);
        Assert.Equal("Turno mattina", deposito.Active.Name);
        Assert.Equal("F1", deposito.Active.Get(HotkeyCommand.Answer, HotkeyScope.Local)?.Key);
    }

    [Fact]
    public void LaConfigurazioneSopravviveAlRiavvio()
    {
        var bozza = HotkeyProfile.CreateDefault().Clone("Turno sera");
        bozza.Set(new HotkeyBinding(HotkeyCommand.Transfer, "F4", HotkeyModifiers.Control, HotkeyScope.Local));
        new HotkeyProfileStore(_cartella).SaveAndActivate(bozza);

        var riaperto = new HotkeyProfileStore(_cartella);

        Assert.Equal("Turno sera", riaperto.Active.Name);
        Assert.Equal(0, riaperto.IgnoredBindings);

        var trasferimento = riaperto.Active.Get(HotkeyCommand.Transfer, HotkeyScope.Local);
        Assert.Equal("F4", trasferimento?.Key);
        Assert.Equal(HotkeyModifiers.Control, trasferimento?.Modifiers);
    }

    [Fact]
    public void IlProfiloAttivoScrittoNelFileVieneRipresoAllAvvio()
    {
        var primo = new HotkeyProfileStore(_cartella);
        Assert.True(primo.Activate("Solo primo piano"));

        var deposito = new HotkeyProfileStore(_cartella);

        Assert.Equal("Solo primo piano", deposito.Active.Name);
        Assert.DoesNotContain(deposito.Active.Bindings, b => b.Scope == HotkeyScope.Global);
    }

    [Fact]
    public void ModificareLaBozzaNonToccaIlProfiloSalvato()
    {
        var deposito = new HotkeyProfileStore(_cartella);

        var bozza = deposito.Get("Standard")!;
        bozza.Remove(HotkeyCommand.HangUp, HotkeyScope.Local);

        Assert.NotNull(deposito.Get("Standard")!.Get(HotkeyCommand.HangUp, HotkeyScope.Local));
    }

    [Fact]
    public void UnProfiloImportatoNonSovrascriveQuelloConLoStessoNome()
    {
        var deposito = new HotkeyProfileStore(_cartella);
        var file = Path.Combine(_cartella, "esportato.json");

        Assert.True(deposito.ExportProfile(deposito.Get("Standard")!, file));

        var importato = deposito.Import(file);

        Assert.NotNull(importato);
        Assert.Equal("Standard 2", importato!.Name);

        // L'importazione non attiva nulla: un profilo che arriva da un'altra
        // postazione va guardato prima di ritrovarselo sotto le dita durante un turno.
        Assert.Equal("Standard", deposito.Active.Name);
    }

    [Fact]
    public void UnFileNonLeggibileNonRestituisceUnProfilo()
    {
        var deposito = new HotkeyProfileStore(_cartella);
        var file = Path.Combine(_cartella, "spazzatura.json");
        File.WriteAllText(file, "questo non e' un profilo");

        Assert.Null(deposito.Import(file));
    }

    [Fact]
    public void LaSchedaDeiTastiRiportaLeCombinazioniDelProfilo()
    {
        var deposito = new HotkeyProfileStore(_cartella);
        var file = Path.Combine(_cartella, "scheda.txt");

        Assert.True(deposito.ExportCard(deposito.Active, file, DateTimeOffset.Now));

        var testo = File.ReadAllText(file);

        Assert.Contains("Rispondi: Locale F2", testo, StringComparison.Ordinal);
        Assert.Contains("Preleva la chiamata selezionata: non assegnato", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void IProfiliFornitiConIlProdottoNonSiEliminano()
    {
        var deposito = new HotkeyProfileStore(_cartella);

        var errore = deposito.Delete("Standard");

        Assert.NotNull(errore);
        Assert.Contains("Standard", deposito.ProfileNames);
    }

    [Fact]
    public void EliminandoIlProfiloAttivoSiTornaAlloStandard()
    {
        var deposito = new HotkeyProfileStore(_cartella);
        deposito.SaveAndActivate(HotkeyProfile.CreateDefault().Clone("Personale"));

        Assert.Null(deposito.Delete("Personale"));
        Assert.Equal("Standard", deposito.Active.Name);
        Assert.DoesNotContain("Personale", deposito.ProfileNames);
    }

    [Fact]
    public void UnComandoNuovoRaggiungeIProfiliSalvatiDaUnaVersionePrecedente()
    {
        // È il difetto vero trovato sul campo: la guida in linea era stata aggiunta
        // con F1 nel profilo predefinito, ma i profili gia' salvati non la conoscevano
        // e premendo F1 non succedeva nulla. Il file qui sotto e' fatto come quelli
        // scritti dalla versione precedente: nessun elenco di comandi conosciuti.
        File.WriteAllText(Path.Combine(_cartella, "hotkeys.json"), """
            {
              "schemaVersion": 1,
              "activeProfile": "Stefano",
              "profiles": [
                {
                  "name": "Stefano",
                  "bindings": [
                    { "command": "Answer", "key": "F2", "modifiers": "None", "scope": "Local" },
                    { "command": "HangUp", "key": "F3", "modifiers": "None", "scope": "Local" }
                  ]
                }
              ]
            }
            """);

        var deposito = new HotkeyProfileStore(_cartella);

        Assert.Equal("Stefano", deposito.Active.Name);
        Assert.Equal("F1", deposito.Active.Get(HotkeyCommand.OpenHelp, HotkeyScope.Local)?.Key);
        Assert.NotNull(deposito.Active.Get(HotkeyCommand.ShowWindow, HotkeyScope.Global));

        // Le assegnazioni che c'erano restano intatte.
        Assert.Equal("F2", deposito.Active.Get(HotkeyCommand.Answer, HotkeyScope.Local)?.Key);

        // E la migrazione viene registrata, cosi' non si ripete a ogni avvio.
        var riaperto = new HotkeyProfileStore(_cartella);
        Assert.Equal("F1", riaperto.Active.Get(HotkeyCommand.OpenHelp, HotkeyScope.Local)?.Key);
    }

    [Fact]
    public void UnComandoLasciatoSenzaTastoDiPropositoRestaSenza()
    {
        var deposito = new HotkeyProfileStore(_cartella);

        var mio = deposito.Get("Standard")!;
        mio.Name = "Personale";
        mio.Remove(HotkeyCommand.OpenHelp, HotkeyScope.Local);
        deposito.SaveAndActivate(mio);

        var riaperto = new HotkeyProfileStore(_cartella);

        // Rimettere un tasto che l'operatore ha tolto e' il modo piu' rapido di
        // fargli perdere fiducia nella configurazione.
        Assert.Null(riaperto.Active.Get(HotkeyCommand.OpenHelp, HotkeyScope.Local));
        Assert.Equal("Personale", riaperto.Active.Name);
    }

    [Fact]
    public void LaMigrazioneNonRubaUnaCombinazioneGiaInUso()
    {
        // F1 assegnato a mano a un altro comando: la guida deve restare senza tasto
        // invece di cambiare significato a un tasto usato da mesi.
        File.WriteAllText(Path.Combine(_cartella, "hotkeys.json"), """
            {
              "schemaVersion": 1,
              "activeProfile": "Personale",
              "profiles": [
                {
                  "name": "Personale",
                  "bindings": [
                    { "command": "AnnounceQueueCount", "key": "F1", "modifiers": "None", "scope": "Local" }
                  ]
                }
              ]
            }
            """);

        var deposito = new HotkeyProfileStore(_cartella);

        Assert.Null(deposito.Active.Get(HotkeyCommand.OpenHelp, HotkeyScope.Local));
        Assert.Equal("F1", deposito.Active.Get(HotkeyCommand.AnnounceQueueCount, HotkeyScope.Local)?.Key);
    }

    [Fact]
    public void IProfiliPredefinitiSeguonoIlCodiceEnonIlFile()
    {
        // Un file scritto da una versione precedente contiene uno "Standard" povero.
        // Va rigenerato: non e' modificabile dall'interfaccia, quindi non c'e' nulla
        // dell'operatore da conservare, e conservarlo terrebbe fuori i comandi nuovi.
        File.WriteAllText(Path.Combine(_cartella, "hotkeys.json"), """
            {
              "schemaVersion": 1,
              "activeProfile": "Standard",
              "profiles": [
                {
                  "name": "Standard",
                  "bindings": [
                    { "command": "Answer", "key": "F2", "modifiers": "None", "scope": "Local" }
                  ]
                }
              ]
            }
            """);

        var deposito = new HotkeyProfileStore(_cartella);

        Assert.Equal(
            HotkeyProfile.CreateDefault().Bindings.Count,
            deposito.Get("Standard")!.Bindings.Count);

        Assert.Equal("F1", deposito.Active.Get(HotkeyCommand.OpenHelp, HotkeyScope.Local)?.Key);
    }

    [Fact]
    public void UnFileCorrottoNonLasciaLaPostazioneSenzaTasti()
    {
        File.WriteAllText(Path.Combine(_cartella, "hotkeys.json"), "{ questo non e' json valido ");

        var deposito = new HotkeyProfileStore(_cartella);

        Assert.Equal("Standard", deposito.Active.Name);
        Assert.NotEmpty(deposito.Active.Bindings);

        // L'originale va conservato: senza, l'assistenza non puo' capire cosa aveva
        // configurato l'operatore prima del guasto.
        Assert.Single(Directory.GetFiles(_cartella, "hotkeys.json.corrotto-*"));
    }
}
