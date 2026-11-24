using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sulla modifica delle combinazioni globali dal pannello dei tasti rapidi.
/// </summary>
/// <remarks>
/// <para>
/// Il pannello permetteva già di riassegnare le globali, ma tre cose le rendevano
/// inaffidabili, tutte invisibili a schermo. Una globale sugli stessi tasti della locale
/// di un altro comando la spegneva, perché Windows consegna la globale anche con
/// BlindPhone in primo piano. Una combinazione già presa da un altro programma si
/// scopriva solo dopo il salvataggio. E Control Alt Pausa veniva salvata con un nome
/// che né la finestra principale né Windows riconoscevano.
/// </para>
/// <para>
/// La prova presso Windows non si può fare qui, dove il dominio non vede il sistema:
/// si sostituisce con una funzione che dice cosa risponderebbe. Quella vera è
/// collaudata in <c>BlindPhone.Windows.Tests</c>.
/// </para>
/// </remarks>
public sealed class TastiGlobaliModificabiliTests
{
    private const HotkeyModifiers ControlMaiusc = HotkeyModifiers.Control | HotkeyModifiers.Shift;

    private static readonly HotkeyConflictDetector Rilevatore = new();

    // -----------------------------------------------------------------------
    // Conflitti fra ambiti
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaGlobaleSuiTastiDellaLocaleDiUnAltroComandoEUnConflittoSuperabile()
    {
        // Control Maiusc D, locale, accende i toni per i menu automatici. Come globale
        // di Rispondi la prenderebbe Windows anche con BlindPhone in primo piano, e i
        // toni smetterebbero di accendersi senza che il pannello lo mostri.
        var candidata = new HotkeyBinding(HotkeyCommand.Answer, "D", ControlMaiusc, HotkeyScope.Global);

        var esito = Rilevatore.Check(candidata, HotkeyProfile.CreateDefault(), HotkeyCommandCatalog.Labels);

        Assert.Equal(HotkeyConflictKind.OtherBlindPhoneCommand, esito.Kind);
        Assert.True(esito.CanOverride);
        Assert.Contains("combinazione locale di Modalita' toni", esito.Message);
        Assert.Contains("primo piano", esito.Message);
    }

    [Fact]
    public void UnaLocaleSuiTastiDellaGlobaleDiUnAltroComandoEUnConflittoSuperabile()
    {
        // Control Maiusc F2 e' la risposta globale: come locale del riaggancio non
        // arriverebbe mai, perche' Windows la consegna prima a Rispondi.
        var candidata = new HotkeyBinding(HotkeyCommand.HangUp, "F2", ControlMaiusc, HotkeyScope.Local);

        var esito = Rilevatore.Check(candidata, HotkeyProfile.CreateDefault(), HotkeyCommandCatalog.Labels);

        Assert.Equal(HotkeyConflictKind.OtherBlindPhoneCommand, esito.Kind);
        Assert.True(esito.CanOverride);
        Assert.Contains("combinazione globale di Rispondi", esito.Message);
    }

    [Fact]
    public void LoStessoComandoConGliStessiTastiNeiDueAmbitiNonEUnConflitto()
    {
        // F2 da solo come globale di Rispondi, che ha F2 anche come locale: qualunque
        // delle due scatti, risponde. Non c'e' niente da chiedere all'operatore.
        var candidata = new HotkeyBinding(HotkeyCommand.Answer, "F2", HotkeyModifiers.None, HotkeyScope.Global);

        var esito = Rilevatore.Check(candidata, HotkeyProfile.CreateDefault(), HotkeyCommandCatalog.Labels);

        Assert.True(esito.IsClear, esito.Message);
    }

    [Fact]
    public void LaSostituzioneTogliePureLaCombinazioneDellAltroAmbito()
    {
        var profilo = HotkeyProfile.CreateDefault();
        var candidata = new HotkeyBinding(HotkeyCommand.Answer, "D", ControlMaiusc, HotkeyScope.Global);

        var daTogliere = profilo.FindCollisions(candidata);

        // Se ne restasse anche una, quale comando parte dipenderebbe da chi arriva
        // prima fra Windows e la finestra: cioe' non si saprebbe.
        var unica = Assert.Single(daTogliere);
        Assert.Equal(HotkeyCommand.ToggleDtmfMode, unica.Command);
        Assert.Equal(HotkeyScope.Local, unica.Scope);
    }

    [Fact]
    public void IlProfiloPredefinitoNonHaConflittiFraAmbiti()
    {
        var profilo = HotkeyProfile.CreateDefault();

        var conflitti = profilo.Bindings
            .Where(b => profilo.FindOtherScopeConflict(b) is not null)
            .Select(b => $"{b.Command} {b.Scope} {b.SpeechText}")
            .ToList();

        Assert.True(
            conflitti.Count == 0,
            "Combinazioni globali che coincidono con locali di altri comandi: la locale non scatterebbe mai.\n"
            + string.Join('\n', conflitti));
    }

    [Fact]
    public void LaMigrazioneNonAggiungeUnaGlobaleCheSpegneUnaLocale()
    {
        // Un profilo salvato da una versione che non conosceva Rispondi, dove
        // l'operatore ha messo il riaggancio su Control Maiusc F2 locale. La globale
        // predefinita di Rispondi e' proprio quella: aggiungerla toglierebbe il tasto
        // a un comando che l'operatore usa da mesi.
        var profilo = HotkeyProfile.CreateDefault();
        profilo.Remove(HotkeyCommand.Answer, HotkeyScope.Local);
        profilo.Remove(HotkeyCommand.Answer, HotkeyScope.Global);
        profilo.KnownCommands.Remove(HotkeyCommand.Answer);
        profilo.Set(new HotkeyBinding(HotkeyCommand.HangUp, "F2", ControlMaiusc, HotkeyScope.Local));

        var aggiunti = profilo.AddMissingDefaults();

        Assert.Contains(HotkeyCommand.Answer, aggiunti);
        Assert.NotNull(profilo.Get(HotkeyCommand.Answer, HotkeyScope.Local));
        Assert.Null(profilo.Get(HotkeyCommand.Answer, HotkeyScope.Global));
    }

    // -----------------------------------------------------------------------
    // Prova presso Windows
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaGlobaleTenutaDaUnAltroProgrammaVieneRifiutataPrimaDiOgniConferma()
    {
        // Control Maiusc F3 e' anche il riaggancio globale, ma qui non deve contare:
        // chiedere se toglierla al riaggancio e poi dire che tanto non funzionerebbe
        // costringerebbe a una decisione inutile.
        var candidata = new HotkeyBinding(HotkeyCommand.Answer, "F3", ControlMaiusc, HotkeyScope.Global);

        var esito = Rilevatore.Check(
            candidata, HotkeyProfile.CreateDefault(), HotkeyCommandCatalog.Labels, takenElsewhere: _ => true);

        Assert.Equal(HotkeyConflictKind.TakenByAnotherApplication, esito.Kind);
        Assert.False(esito.CanOverride);
        Assert.Contains("Control Maiusc F3", esito.Message);
        Assert.Contains("altro programma", esito.Message);
    }

    [Fact]
    public void LaProvaPressoWindowsSiFaSoloPerLeCombinazioniGlobali()
    {
        var prove = new List<HotkeyBinding>();
        bool Prova(HotkeyBinding b)
        {
            prove.Add(b);
            return true;
        }

        var profilo = HotkeyProfile.CreateDefault();
        var locale = new HotkeyBinding(HotkeyCommand.Answer, "F12", HotkeyModifiers.Alt, HotkeyScope.Local);
        var globale = locale with { Scope = HotkeyScope.Global };

        // Una locale non si registra presso il sistema: provarla darebbe rifiuti che
        // non la riguardano, come quello di F12 da solo, riservato al debugger.
        Assert.True(Rilevatore.Check(locale, profilo, HotkeyCommandCatalog.Labels, Prova).IsClear);
        Assert.Empty(prove);

        Assert.Equal(
            HotkeyConflictKind.TakenByAnotherApplication,
            Rilevatore.Check(globale, profilo, HotkeyCommandCatalog.Labels, Prova).Kind);
        Assert.Equal(globale, Assert.Single(prove));
    }

    [Fact]
    public void UnaGlobaleLiberaPressoWindowsPassa()
    {
        var candidata = new HotkeyBinding(HotkeyCommand.Answer, "F2", HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyScope.Global);

        var esito = Rilevatore.Check(
            candidata, HotkeyProfile.CreateDefault(), HotkeyCommandCatalog.Labels, takenElsewhere: _ => false);

        Assert.True(esito.IsClear, esito.Message);
    }

    // -----------------------------------------------------------------------
    // Pausa
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Pause", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "Cancel")]
    [InlineData("Pause", HotkeyModifiers.Control, "Cancel")]
    [InlineData("Cancel", HotkeyModifiers.Control | HotkeyModifiers.Alt, "Cancel")]
    [InlineData("Pause", HotkeyModifiers.None, "Pause")]
    [InlineData("Pause", HotkeyModifiers.Alt | HotkeyModifiers.Shift, "Pause")]
    [InlineData("F9", HotkeyModifiers.Control, "F9")]
    public void ConControlPausaSiRegistraComeInterr(string tasto, HotkeyModifiers modificatori, string atteso)
    {
        // Con Control giu' la tastiera non manda Pausa ma Interr: una globale
        // dichiarata con il codice di Pausa non scatterebbe mai. E' il caso della
        // postazione fuori servizio, Control Alt Maiusc Pausa.
        Assert.Equal(atteso, HotkeyKeyNames.RegistrationKey(tasto, modificatori));
    }

    [Fact]
    public void PausaEInterrSonoLaStessaCombinazione()
    {
        var pausa = new HotkeyBinding(HotkeyCommand.Answer, "Pause", HotkeyModifiers.Control, HotkeyScope.Local);
        var interr = pausa with { Key = "Cancel" };

        Assert.Equal(pausa.ConflictKey, interr.ConflictKey);
        Assert.Equal(pausa.ChordKey, interr.ChordKey);
    }

    [Fact]
    public void UnProfiloSalvatoConCancelTornaAPausaAllaLettura()
    {
        // Prima della correzione la cattura salvava Control Alt Pausa come "Cancel", e
        // in primo piano il comando non si trovava piu': li' il tasto premuto si cerca
        // sempre come "Pause". La lettura ripara i file gia' sulle postazioni.
        var profilo = new HotkeyProfile("Turno di notte");
        profilo.Set(new HotkeyBinding(
            HotkeyCommand.ToggleServicePause, "Cancel", HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyScope.Local));

        var riletto = HotkeyProfileSerializer.DeserializeSingle(HotkeyProfileSerializer.SerializeSingle(profilo));

        var tasto = riletto?.Get(HotkeyCommand.ToggleServicePause, HotkeyScope.Local);
        Assert.Equal("Pause", tasto?.Key);
        Assert.Equal(
            HotkeyCommand.ToggleServicePause,
            riletto?.Resolve("Pause", HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyScope.Local));
    }

    // -----------------------------------------------------------------------
    // Ritorno al predefinito
    // -----------------------------------------------------------------------

    [Fact]
    public void IlPredefinitoDiUnaCellaEQuelloDelProfiloStandard()
    {
        var risposta = HotkeyProfile.DefaultFor(HotkeyCommand.Answer, HotkeyScope.Global);

        Assert.NotNull(risposta);
        Assert.Equal("F2", risposta.Key);
        Assert.Equal(ControlMaiusc, risposta.Modifiers);

        // Le impostazioni si aprono solo con la finestra davanti: nessuna globale
        // predefinita, e tornare al predefinito significa toglierla.
        Assert.Null(HotkeyProfile.DefaultFor(HotkeyCommand.OpenSettings, HotkeyScope.Global));
    }

    [Fact]
    public void IlPredefinitoDiUnaCellaPassaDagliStessiControlliDiUnaCombinazionePremuta()
    {
        // L'operatore ha dato Control Maiusc F2 al riaggancio globale e ora vuole
        // rimettere Rispondi al suo posto: il predefinito e' occupato, e va detto.
        var profilo = HotkeyProfile.CreateDefault();
        profilo.Remove(HotkeyCommand.Answer, HotkeyScope.Global);
        profilo.Set(new HotkeyBinding(HotkeyCommand.HangUp, "F2", ControlMaiusc, HotkeyScope.Global));

        var predefinito = HotkeyProfile.DefaultFor(HotkeyCommand.Answer, HotkeyScope.Global)!;
        var esito = Rilevatore.Check(predefinito, profilo, HotkeyCommandCatalog.Labels, _ => false);

        Assert.Equal(HotkeyConflictKind.OtherBlindPhoneCommand, esito.Kind);
        Assert.Contains("Riaggancia", esito.Message);
    }
}
