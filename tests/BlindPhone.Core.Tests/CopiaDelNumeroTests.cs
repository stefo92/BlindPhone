using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sulla copia negli appunti del numero del chiamante.
/// </summary>
/// <remarks>
/// <para>
/// Chi non vede lo schermo non può controllare quale chiamata è «selezionata»: deve
/// poter prevedere quale numero finisce negli appunti. Le prove fissano la regola —
/// chi è in linea, chi squilla, chi si sta chiamando, chi è in attesa, l'ultima
/// chiamata ricevuta — e il fatto che un numero nascosto non faccia copiare quello di
/// un'altra persona.
/// </para>
/// </remarks>
public sealed class CopiaDelNumeroTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.FromHours(1));

    private static Call Chiamata(
        string numero,
        string? nome = null,
        CallDirection verso = CallDirection.Inbound,
        int minutiFa = 0)
        => new($"c-{numero}-{minutiFa}", verso, new CallParty(numero, nome), Now.AddMinutes(-minutiFa));

    // -----------------------------------------------------------------------
    // Quale chiamata
    // -----------------------------------------------------------------------

    [Fact]
    public void ChiEInLineaVinceSuTutto()
    {
        var inLinea = Chiamata("0612345678", "Mario Rossi");

        var scelta = CallerNumberCopy.Choose(
            [inLinea], Chiamata("3331112222"), Chiamata("4021", verso: CallDirection.Outbound),
            [Chiamata("3394445555")], Chiamata("0298765432"));

        Assert.Same(inLinea, scelta?.Call);
        Assert.Equal(CallerNumberSource.InConversation, scelta?.Source);
    }

    [Fact]
    public void SenzaNessunoInLineaSiCopiaChiStaChiamando()
    {
        // Il caso di chi vuole cercare il numero prima di rispondere.
        var squilla = Chiamata("3331112222");

        var scelta = CallerNumberCopy.Choose(
            [], squilla, null, [Chiamata("3394445555")], Chiamata("0298765432"));

        Assert.Same(squilla, scelta?.Call);
        Assert.Equal(CallerNumberSource.Ringing, scelta?.Source);
    }

    [Fact]
    public void PoiChiSiStaChiamando()
    {
        var uscente = Chiamata("4021", verso: CallDirection.Outbound);

        var scelta = CallerNumberCopy.Choose([], null, uscente, [Chiamata("3394445555")], null);

        Assert.Same(uscente, scelta?.Call);
        Assert.Equal(CallerNumberSource.Outgoing, scelta?.Source);
    }

    [Fact]
    public void FraLeChiamateInAttesaSiPreferisceChiHaChiamatoEPoiLaPiuRecente()
    {
        // Durante un trasferimento con consultazione il chiamante e' in attesa e
        // l'interno chiamato pure, se l'operatore li ha messi entrambi in attesa: il
        // comando copia il numero del chiamante, non quello del collega.
        var collega = Chiamata("4021", "Ufficio protocollo", CallDirection.Outbound, minutiFa: 1);
        var vecchio = Chiamata("3394445555", minutiFa: 9);
        var recente = Chiamata("3471234567", minutiFa: 3);

        var scelta = CallerNumberCopy.Choose([], null, null, [collega, vecchio, recente], null);

        Assert.Same(recente, scelta?.Call);
        Assert.Equal(CallerNumberSource.Waiting, scelta?.Source);
    }

    [Fact]
    public void InConferenzaSiCopiaChiHaChiamatoENonIlCollegaAggiunto()
    {
        var chiamante = Chiamata("0612345678", "Mario Rossi", minutiFa: 5);
        var collega = Chiamata("4021", "Ufficio protocollo", CallDirection.Outbound, minutiFa: 1);

        var scelta = CallerNumberCopy.Choose([collega, chiamante], null, null, [], null);

        Assert.Same(chiamante, scelta?.Call);
    }

    [Fact]
    public void DopoAverRiagganciatoSiCopiaLUltimaChiamataRicevuta()
    {
        // E' il gesto piu' frequente: si riaggancia, e poi si annota.
        var ultima = Chiamata("0298765432", "Comune di Lodi");

        var scelta = CallerNumberCopy.Choose([], null, null, [], ultima);

        Assert.Same(ultima, scelta?.Call);
        Assert.Equal(CallerNumberSource.LastReceived, scelta?.Source);
    }

    [Fact]
    public void SenzaChiamateNonSiCopiaNulla()
    {
        Assert.Null(CallerNumberCopy.Choose([], null, null, [], null));
        Assert.Contains("Nessun numero da copiare", CallerNumberCopy.NothingToCopy);
    }

    // -----------------------------------------------------------------------
    // Che cosa si copia
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("+39 06 1234 5678", "+390612345678")]
    [InlineData("06-1234.5678", "0612345678")]
    [InlineData("4021", "4021")]
    [InlineData("anonymous", null)]
    [InlineData("", null)]
    public void SiCopiaIlNumeroComeSiCompone(string ricevuto, string? atteso)
    {
        // Spazi e trattini andrebbero tolti a mano prima di incollarlo in un campo
        // che accetta solo cifre; "anonymous" incollato nel gestionale sembrerebbe un
        // dato e non lo e'.
        Assert.Equal(atteso, CallerNumberCopy.CopyText(new CallParty(ricevuto)));
    }

    [Fact]
    public void UnNumeroNascostoNonFaCopiareQuelloDiUnAltraPersona()
    {
        var anonimo = Chiamata("anonymous");

        var scelta = CallerNumberCopy.Choose([anonimo], null, null, [Chiamata("3394445555")], null);

        Assert.NotNull(scelta);
        Assert.Same(anonimo, scelta.Call);
        Assert.Null(CallerNumberCopy.CopyText(scelta.Call.RemoteParty));

        var risposta = CallerNumberCopy.HiddenNumber(scelta);
        Assert.Contains("in linea", risposta);
        Assert.Contains("nascosto", risposta);
        Assert.Contains("appunti non sono cambiati", risposta);
    }

    // -----------------------------------------------------------------------
    // Che cosa si dice
    // -----------------------------------------------------------------------

    [Fact]
    public void LaConfermaDiceDiChiEIlNumeroERileggeLeCifre()
    {
        var scelta = new CallerNumberCandidate(Chiamata("0612345678", "Mario Rossi"), CallerNumberSource.InConversation);

        var conferma = CallerNumberCopy.Confirmation(scelta, "0612345678", DigitGrouping.Single);

        Assert.Contains("Mario Rossi", conferma);
        Assert.Contains(SpeechFormatter.Digits("0612345678"), conferma);
        Assert.StartsWith("Copiato il numero", conferma);
    }

    [Fact]
    public void LaConfermaDiceDaQualeChiamataVieneIlNumero()
    {
        var chiamata = Chiamata("0298765432");

        Assert.Contains("ultima chiamata ricevuta", CallerNumberCopy.Confirmation(
            new CallerNumberCandidate(chiamata, CallerNumberSource.LastReceived), "0298765432", DigitGrouping.Single));
        Assert.Contains("in attesa", CallerNumberCopy.Confirmation(
            new CallerNumberCandidate(chiamata, CallerNumberSource.Waiting), "0298765432", DigitGrouping.Single));
        Assert.Contains("sta chiamando", CallerNumberCopy.Confirmation(
            new CallerNumberCandidate(chiamata, CallerNumberSource.Ringing), "0298765432", DigitGrouping.Single));
    }

    [Fact]
    public void SenzaNomeLaConfermaNonRipeteIlNumeroComeSeFosseUnNome()
    {
        var scelta = new CallerNumberCandidate(
            Chiamata("0612345678", "0612345678"), CallerNumberSource.InConversation);

        var conferma = CallerNumberCopy.Confirmation(scelta, "0612345678", DigitGrouping.Single);

        Assert.Equal($"Copiato il numero: {SpeechFormatter.Digits("0612345678")}", conferma);
    }

    // -----------------------------------------------------------------------
    // La combinazione
    // -----------------------------------------------------------------------

    [Fact]
    public void LaCopiaStaSuAltCSoloInPrimoPiano()
    {
        var profilo = HotkeyProfile.CreateDefault();

        var locale = profilo.Get(HotkeyCommand.CopyCallerNumber, HotkeyScope.Local);
        Assert.NotNull(locale);
        Assert.Equal("C", locale.Key);
        Assert.Equal(HotkeyModifiers.Alt, locale.Modifiers);

        // Come globale, Alt C la toglierebbe a ogni altro programma, dove e' spesso il
        // tasto di accesso di Chiudi o Cerca: la globale la sceglie l'operatore.
        Assert.Null(profilo.Get(HotkeyCommand.CopyCallerNumber, HotkeyScope.Global));
    }

    [Fact]
    public void AltCNonEntraInConflittoConNullaDiNoto()
    {
        var profilo = HotkeyProfile.CreateDefault();
        var altC = profilo.Get(HotkeyCommand.CopyCallerNumber, HotkeyScope.Local)!;
        profilo.Remove(HotkeyCommand.CopyCallerNumber, HotkeyScope.Local);

        var esito = new HotkeyConflictDetector().Check(altC, profilo, HotkeyCommandCatalog.Labels);

        Assert.True(esito.IsClear, esito.Message);
    }

    [Fact]
    public void LePostazioniGiaInServizioRicevonoAltCSeELibera()
    {
        // Un profilo salvato prima che il comando esistesse: alla lettura la
        // migrazione gli da' il tasto predefinito.
        var profilo = HotkeyProfile.CreateDefault();
        profilo.Remove(HotkeyCommand.CopyCallerNumber, HotkeyScope.Local);
        profilo.KnownCommands.Remove(HotkeyCommand.CopyCallerNumber);

        Assert.Contains(HotkeyCommand.CopyCallerNumber, profilo.AddMissingDefaults());
        Assert.Equal("C", profilo.Get(HotkeyCommand.CopyCallerNumber, HotkeyScope.Local)?.Key);
    }

    [Fact]
    public void LaMigrazioneNonRubaAltCAChiLaUsaGia()
    {
        var profilo = HotkeyProfile.CreateDefault();
        profilo.Remove(HotkeyCommand.CopyCallerNumber, HotkeyScope.Local);
        profilo.KnownCommands.Remove(HotkeyCommand.CopyCallerNumber);
        profilo.Set(new HotkeyBinding(HotkeyCommand.FocusColleagues, "C", HotkeyModifiers.Alt, HotkeyScope.Local));

        profilo.AddMissingDefaults();

        Assert.Null(profilo.Get(HotkeyCommand.CopyCallerNumber, HotkeyScope.Local));
        Assert.Equal(
            HotkeyCommand.FocusColleagues,
            profilo.Resolve("C", HotkeyModifiers.Alt, HotkeyScope.Local));
    }
}
