using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sulla mappa dei tasti configurabile.
/// </summary>
/// <remarks>
/// <para>
/// Da quando ogni combinazione è riassegnabile dall'operatore, la mappa dei tasti è
/// un dato che entra ed esce da un file, viene confrontato per trovare conflitti e
/// tradotto in parole da pronunciare. Ognuno di questi passaggi può sbagliare in modo
/// silenzioso, e il sintomo è sempre lo stesso: un tasto che non fa niente.
/// </para>
/// <para>
/// Il primo test è un gate di CI come quello degli annunci: un comando aggiunto
/// all'enumerazione e dimenticato nel catalogo comparirebbe nel pannello con il suo
/// nome inglese di codice, che per chi ascolta non significa nulla.
/// </para>
/// </remarks>
public sealed class HotkeyConfigurationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 9, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void OgniComandoHaEtichettaEDescrizione()
    {
        var mancanti = new List<string>();

        foreach (var command in Enum.GetValues<HotkeyCommand>())
        {
            var info = HotkeyCommandCatalog.All.FirstOrDefault(e => e.Command == command);

            if (info is null
                || string.IsNullOrWhiteSpace(info.Label)
                || string.IsNullOrWhiteSpace(info.Description))
            {
                mancanti.Add(command.ToString());
            }
        }

        Assert.True(
            mancanti.Count == 0,
            "Comandi senza voce nel catalogo: comparirebbero nel pannello con il nome interno.\n"
            + string.Join('\n', mancanti));
    }

    [Fact]
    public void IlProfiloPredefinitoNonHaConflittiInterni()
    {
        var profilo = HotkeyProfile.CreateDefault();

        var duplicati = profilo.Bindings
            .GroupBy(b => b.ConflictKey)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(b => b.Command))}")
            .ToList();

        Assert.True(
            duplicati.Count == 0,
            "Combinazioni assegnate a piu' comandi: quale parte non sarebbe deterministico.\n"
            + string.Join('\n', duplicati));
    }

    [Fact]
    public void LaRubricaNonStaSuUnTastoRiservatoDaWindows()
    {
        var profilo = HotkeyProfile.CreateDefault();

        // F10 da solo apre la barra dei menu della finestra: assegnato alla rubrica
        // non apriva nulla e portava il cursore sul titolo. La rubrica sta su F6, che
        // e' anche il tasto piu' comodo dei due; la coda, che si apre molte meno volte
        // al giorno, sta su Control F10, dove il modificatore evita il menu.
        var rubrica = profilo.Get(HotkeyCommand.OpenContacts, HotkeyScope.Local);
        Assert.Equal("F6", rubrica?.Key);
        Assert.Equal(HotkeyModifiers.None, rubrica?.Modifiers);

        var coda = profilo.Get(HotkeyCommand.FocusQueue, HotkeyScope.Local);
        Assert.Equal("F10", coda?.Key);
        Assert.Equal(HotkeyModifiers.Control, coda?.Modifiers);

        // Nessun comando deve restare su F10 nudo.
        Assert.DoesNotContain(
            profilo.Bindings,
            b => b.Scope == HotkeyScope.Local
                 && b.Modifiers == HotkeyModifiers.None
                 && b.Key.Equals("F10", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("System", "F10", "F10")]
    [InlineData("System", "D", "D")]
    [InlineData("F9", "None", "F9")]
    [InlineData("System", "", "System")]
    public void IlNomeVeroDiUnTastoDiSistemaVieneRecuperato(string tasto, string tastoDiSistema, string atteso)
        => Assert.Equal(atteso, HotkeyKeyNames.ResolveSystemKey(tasto, tastoDiSistema));

    [Fact]
    public void IlProfiloSoloPrimoPianoNonRegistraNullaNelSistema()
    {
        var profilo = HotkeyProfile.CreateForegroundOnly();
        var predefinito = HotkeyProfile.CreateDefault();

        Assert.DoesNotContain(profilo.Bindings, b => b.Scope == HotkeyScope.Global);

        // Le locali restano tutte: il posto operatore in primo piano non cambia.
        Assert.Equal(
            predefinito.Bindings.Count(b => b.Scope == HotkeyScope.Local),
            profilo.Bindings.Count(b => b.Scope == HotkeyScope.Local));
    }

    [Fact]
    public void UnaCombinazioneGiaAssegnataProduceUnConflittoSuperabile()
    {
        var profilo = HotkeyProfile.CreateDefault();
        var rilevatore = new HotkeyConflictDetector();

        // F2 e' la risposta alla chiamata: assegnarla al riaggancio e' il caso in cui
        // un conflitto non rilevato farebbe chiudere le chiamate invece di risponderci.
        var candidata = new HotkeyBinding(
            HotkeyCommand.HangUp, "F2", HotkeyModifiers.None, HotkeyScope.Local);

        var conflitto = rilevatore.Check(candidata, profilo, HotkeyCommandCatalog.Labels);

        Assert.Equal(HotkeyConflictKind.OtherBlindPhoneCommand, conflitto.Kind);
        Assert.True(conflitto.CanOverride);
        Assert.Contains("Rispondi", conflitto.Message);
    }

    [Fact]
    public void UnaCombinazioneRiservataDaWindowsNonSiPuoAssegnare()
    {
        var rilevatore = new HotkeyConflictDetector();

        var candidata = new HotkeyBinding(
            HotkeyCommand.HangUp, "F4", HotkeyModifiers.Alt, HotkeyScope.Local);

        var conflitto = rilevatore.Check(candidata, HotkeyProfile.CreateDefault(), HotkeyCommandCatalog.Labels);

        Assert.Equal(HotkeyConflictKind.ReservedByWindows, conflitto.Kind);
        Assert.False(conflitto.CanOverride);
    }

    [Fact]
    public void ILProfiloSopravviveAlSalvataggioEAllaRilettura()
    {
        var originale = HotkeyProfile.CreateDefault();
        originale.Set(new HotkeyBinding(
            HotkeyCommand.Conference, "OemComma",
            HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyScope.Local));

        var json = HotkeyProfileSerializer.Serialize([originale, HotkeyProfile.CreateForegroundOnly()], "Standard");
        var riletto = HotkeyProfileSerializer.Deserialize(json);

        Assert.Equal(2, riletto.Profiles.Count);
        Assert.Equal("Standard", riletto.ActiveProfile);
        Assert.Equal(0, riletto.IgnoredBindings);

        var profilo = riletto.Profiles[0];
        Assert.Equal(originale.Bindings.Count, profilo.Bindings.Count);

        var conferenza = profilo.Get(HotkeyCommand.Conference, HotkeyScope.Local);
        Assert.NotNull(conferenza);
        Assert.Equal("OemComma", conferenza!.Key);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, conferenza.Modifiers);
    }

    [Fact]
    public void UnaRigaIllegibileNonFaPerdereIlRestoDelProfilo()
    {
        // Il caso reale e' un file corretto a mano dall'ufficio IT, oppure un comando
        // tolto da una versione futura. Perdere l'intera mappa dei tasti per una riga
        // sbagliata metterebbe la postazione fuori uso.
        const string json = """
            {
              "schemaVersion": 1,
              "activeProfile": "Turno mattina",
              "profiles": [
                {
                  "name": "Turno mattina",
                  "bindings": [
                    { "command": "Answer", "key": "F2", "modifiers": "None", "scope": "Local" },
                    { "command": "ComandoInesistente", "key": "F6", "modifiers": "None", "scope": "Local" },
                    { "command": "HangUp", "key": "F3", "modifiers": "Sbagliato", "scope": "Local" },
                    { "command": "Transfer", "key": "F9", "modifiers": "None", "scope": "Local" }
                  ]
                }
              ]
            }
            """;

        var risultato = HotkeyProfileSerializer.Deserialize(json);

        Assert.Equal(2, risultato.IgnoredBindings);
        Assert.Single(risultato.Profiles);
        Assert.Equal(2, risultato.Profiles[0].Bindings.Count);
        Assert.NotNull(risultato.Profiles[0].Get(HotkeyCommand.Transfer, HotkeyScope.Local));
    }

    [Fact]
    public void IlProfiloAttivoCambiaContenutoSenzaCambiareIstanza()
    {
        // E' il punto su cui si regge l'applicazione a caldo: la finestra principale
        // tiene un riferimento preso all'avvio, e se venisse sostituito continuerebbe
        // a usare la mappa vecchia.
        var attivo = HotkeyProfile.CreateDefault();
        var riferimento = attivo;

        var nuovo = new HotkeyProfile("Turno sera");
        nuovo.Set(new HotkeyBinding(HotkeyCommand.Answer, "F1", HotkeyModifiers.None, HotkeyScope.Local));

        attivo.CopyFrom(nuovo);

        Assert.Same(riferimento, attivo);
        Assert.Equal("Turno sera", riferimento.Name);
        Assert.Single(riferimento.Bindings);
        Assert.Equal(HotkeyCommand.Answer, riferimento.Resolve("F1", HotkeyModifiers.None, HotkeyScope.Local));
    }

    [Fact]
    public void LaBozzaNonToccaIlProfiloDiPartenza()
    {
        var salvato = HotkeyProfile.CreateDefault();
        var bozza = salvato.Clone("Bozza");

        bozza.Remove(HotkeyCommand.Answer, HotkeyScope.Local);

        Assert.NotNull(salvato.Get(HotkeyCommand.Answer, HotkeyScope.Local));
        Assert.Null(bozza.Get(HotkeyCommand.Answer, HotkeyScope.Local));
    }

    [Theory]
    [InlineData("OemComma", "virgola")]
    [InlineData("D5", "5")]
    [InlineData("NumPad7", "7 del tastierino")]
    [InlineData("Prior", "Pagina su")]
    [InlineData("Return", "Invio")]
    [InlineData("F9", "F9")]
    public void INomiDeiTastiSonoPronunciabili(string tasto, string atteso)
        => Assert.Equal(atteso, HotkeyKeyNames.Speech(tasto));

    [Fact]
    public void LaLetteraEffeNonEUnTastoFunzione()
    {
        // Il controllo sull'iniziale del nome trattava "F" come tasto funzione: con la
        // mappa configurabile significherebbe che la lettera F assegnata a un comando
        // non si puo' piu' digitare nel numero da comporre.
        Assert.True(HotkeyKeyNames.IsFunctionKey("F9"));
        Assert.True(HotkeyKeyNames.IsFunctionKey("F12"));
        Assert.False(HotkeyKeyNames.IsFunctionKey("F"));
        Assert.False(HotkeyKeyNames.IsFunctionKey("F25"));
    }

    [Fact]
    public void IModificatoriDaSoliNonSonoAssegnabili()
    {
        Assert.False(HotkeyKeyNames.IsAssignable("LeftCtrl", out var motivo));
        Assert.False(string.IsNullOrWhiteSpace(motivo));

        Assert.False(HotkeyKeyNames.IsAssignable("Tab", out _));
        Assert.True(HotkeyKeyNames.IsAssignable("F7", out _));
        Assert.True(HotkeyKeyNames.IsAssignable("Escape", out _));
    }

    [Fact]
    public void LaSchedaDeiTastiElencaTuttiIComandi()
    {
        var profilo = HotkeyProfile.CreateDefault();
        profilo.Remove(HotkeyCommand.Conference, HotkeyScope.Local);
        profilo.Remove(HotkeyCommand.Conference, HotkeyScope.Global);

        var scheda = HotkeyCardWriter.Build(profilo, Now);

        foreach (var info in HotkeyCommandCatalog.All)
        {
            Assert.Contains(info.Label, scheda, StringComparison.Ordinal);
        }

        // I comandi liberi restano in elenco: sapere che una funzione esiste ed e'
        // senza tasto e' cio' che porta ad assegnarla.
        Assert.Contains("Conferenza a tre: non assegnato", scheda, StringComparison.Ordinal);

        // Forma pronunciabile, non quella con i segni piu'.
        Assert.Contains("Control Maiusc F2", scheda, StringComparison.Ordinal);
    }
}
