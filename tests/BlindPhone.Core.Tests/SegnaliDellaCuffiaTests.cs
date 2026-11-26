using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica come si distinguono, nei valori che manda una cuffia, le pressioni vere
/// dell'operatore.
/// </summary>
/// <remarks>
/// <para>
/// Ogni prova racconta un modo in cui una cuffia reale si comporta. Le cuffie da
/// ufficio dichiarano il pulsante della chiamata come stato — cornetta alzata o
/// abbassata — e seguono la spia che il programma accende; altre lo dichiarano come
/// impulso; altre ancora lo dichiarano come stato e si comportano da impulso. La
/// stessa regola deve servirle tutte senza chiedere all'operatore che cuffia ha.
/// </para>
/// <para>
/// Il guasto da evitare è uno solo, e ha due facce: chiudere la chiamata a cui si è
/// appena risposto, oppure non chiuderla quando l'operatore preme per chiuderla.
/// </para>
/// </remarks>
public sealed class SegnaliDellaCuffiaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(1));

    private static DateTimeOffset Dopo(int millisecondi) => T0.AddMilliseconds(millisecondi);

    /// <summary>Cuffia da ufficio: gancio come stato, con le spie della linea.</summary>
    private static HeadsetSignalInterpreter CuffiaDaUfficio() => new(new HeadsetCapabilities(
        new HashSet<HeadsetButton> { HeadsetButton.HookSwitch, HeadsetButton.PhoneMute },
        HookSwitchIsAbsolute: true,
        new HashSet<ushort> { HidTelephonyUsages.RingLed, HidTelephonyUsages.OffHookLed, HidTelephonyUsages.MuteLed }));

    /// <summary>Cuffia semplice: gancio come stato, nessuna spia.</summary>
    private static HeadsetSignalInterpreter CuffiaSenzaSpie() => new(new HeadsetCapabilities(
        new HashSet<HeadsetButton> { HeadsetButton.HookSwitch },
        HookSwitchIsAbsolute: true,
        new HashSet<ushort>()));

    /// <summary>Cuffia che dichiara il gancio come impulso.</summary>
    private static HeadsetSignalInterpreter CuffiaAImpulsi() => new(new HeadsetCapabilities(
        new HashSet<HeadsetButton> { HeadsetButton.HookSwitch },
        HookSwitchIsAbsolute: false,
        new HashSet<ushort> { HidTelephonyUsages.OffHookLed }));

    // -----------------------------------------------------------------------
    // Valori ripetuti
    // -----------------------------------------------------------------------

    [Fact]
    public void UnValoreUgualeAlPrecedenteNonEUnaPressione()
    {
        // Ogni rapporto porta tutti i pulsanti insieme: premendo il microfono arriva
        // anche il gancio, uguale a prima. Reagire ai valori chiuderebbe la chiamata
        // nell'istante in cui l'operatore si silenzia.
        var cuffia = CuffiaDaUfficio();

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
        Assert.True(cuffia.Feed(HeadsetButton.PhoneMute, true, Dopo(5000)));
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(5000)));
    }

    [Fact]
    public void IlTastoDelMicrofonoContaAllAppoggioENonAlRilascio()
    {
        var cuffia = CuffiaDaUfficio();

        Assert.True(cuffia.Feed(HeadsetButton.PhoneMute, true, T0));
        Assert.False(cuffia.Feed(HeadsetButton.PhoneMute, false, Dopo(150)));

        // Tenuto premuto a lungo non conta due volte.
        Assert.True(cuffia.Feed(HeadsetButton.PhoneMute, true, Dopo(5000)));
        Assert.False(cuffia.Feed(HeadsetButton.PhoneMute, false, Dopo(8000)));
    }

    // -----------------------------------------------------------------------
    // Cuffia da ufficio: stato, con le spie
    // -----------------------------------------------------------------------

    [Fact]
    public void LaCuffiaDaUfficioRispondeAllAlzataEChiudeAllAbbassata()
    {
        var cuffia = CuffiaDaUfficio();

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(30_000)));
        Assert.False(cuffia.HookSwitchIsMomentary);
    }

    [Fact]
    public void LEcoDellaSpiaAccesaDaBlindPhoneNonChiudeLaChiamata()
    {
        // Si e' risposto da tastiera: BlindPhone accende la spia della linea, la
        // cuffia passa "in linea" e lo conferma con un rapporto. Quel rapporto non e'
        // l'operatore, e prenderlo per una pressione chiuderebbe la chiamata appena
        // risposta.
        var cuffia = CuffiaDaUfficio();

        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);

        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
    }

    [Fact]
    public void DopoLaRispostaDaTastieraBastaUnaPressionePerChiudere()
    {
        // La cuffia ha seguito la spia senza dirlo: molte non mandano nessun rapporto.
        // Il valore conosciuto va aggiornato lo stesso, altrimenti il "riagganciato"
        // che arriva alla pressione risulterebbe uguale a quello vecchio e andrebbe
        // perso: l'operatore premerebbe e la chiamata resterebbe aperta.
        var cuffia = CuffiaDaUfficio();

        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(3_000)));
    }

    [Fact]
    public void LaChiusuraDallInterlocutoreRiallineaLaCuffiaARiposo()
    {
        var cuffia = CuffiaDaUfficio();

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);

        // Ha chiuso l'interlocutore: la spia si spegne e la cuffia la segue.
        cuffia.Reflect(default, Dopo(60_000));
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(60_000)));

        // La chiamata successiva si prende con una pressione sola.
        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(120_000)));
    }

    // -----------------------------------------------------------------------
    // Cuffia senza spie
    // -----------------------------------------------------------------------

    [Fact]
    public void SenzaSpieOgniCambiamentoDelGancioEUnaPressione()
    {
        // La cuffia non sa che si e' risposto da tastiera e resta "a riposo": la
        // pressione successiva la porta "in linea", e per l'operatore e' il gesto con
        // cui chiude. Contare solo i "riagganciato" gli farebbe premere due volte.
        var cuffia = CuffiaSenzaSpie();

        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(20_000)));
    }

    // -----------------------------------------------------------------------
    // Cuffie a impulsi
    // -----------------------------------------------------------------------

    [Fact]
    public void LaCuffiaAImpulsiContaSoloLAppoggioDelDito()
    {
        var cuffia = CuffiaAImpulsi();

        Assert.True(cuffia.HookSwitchIsMomentary);
        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(200)));

        // La spia accesa non cambia il valore del pulsante di una cuffia a impulsi.
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);
        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(30_000)));
    }

    [Fact]
    public void UnImpulsoDichiaratoComeStatoNonChiudeAlRilascio()
    {
        // La cuffia dice "stato" e si comporta da impulso: 1 mentre il dito e' sul
        // tasto, 0 appena lo toglie. Letto come stato rispondeva e chiudeva nello
        // stesso quarto di secondo.
        var cuffia = CuffiaDaUfficio();

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);

        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(180)));
        Assert.True(cuffia.HookSwitchIsMomentary);
    }

    [Fact]
    public void RiconosciutoLImpulsoLaPressioneSuccessivaChiudeSubito()
    {
        // Riconosciuta la natura del pulsante, si chiude alla pressione e non al
        // rilascio: tenendo il dito sul tasto un secondo non succede due volte.
        var cuffia = CuffiaDaUfficio();

        cuffia.Feed(HeadsetButton.HookSwitch, true, T0);
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);
        cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(180));

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(40_000)));
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(41_500)));
    }

    [Fact]
    public void IlRilascioArrivatoPrimaDellAggiornamentoDellaSpiaERiconosciutoLoStesso()
    {
        // L'ordine fra il rilascio e l'aggiornamento della spia non e' garantito: la
        // risposta passa dal thread dell'interfaccia, il rilascio dal thread della
        // cuffia. Tutti e due gli ordini devono dare lo stesso risultato.
        var cuffia = CuffiaDaUfficio();

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(150)));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), Dopo(160));

        Assert.True(cuffia.HookSwitchIsMomentary);
        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(30_000)));
    }

    [Fact]
    public void UnRitornoOltreLaFinestraDiRilascioEUnaPressioneVera()
    {
        var cuffia = CuffiaDaUfficio();
        var oltre = (int)HeadsetSignalInterpreter.ReleaseWindow.TotalMilliseconds + 300;

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(oltre)));
        Assert.False(cuffia.HookSwitchIsMomentary);
    }

    // -----------------------------------------------------------------------
    // Spia che cambia due volte prima della conferma
    // -----------------------------------------------------------------------

    [Fact]
    public void LaSpiaSpentaUnIstanteAllAvvioDelTrasferimentoNonChiudeLaChiamata()
    {
        // In conversazione da dieci secondi, l'operatore avvia il trasferimento: il
        // chiamante passa in attesa e la spia della linea si spegne e si riaccende in
        // venti millisecondi. La cuffia conferma tutti e due i cambi, ma la prima
        // conferma arriva quando lo stato atteso e' gia' di nuovo "in linea".
        var cuffia = CuffiaDaUfficio();

        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);
        cuffia.Reflect(default, Dopo(10_000));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), Dopo(10_020));

        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(10_040)));
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(10_060)));

        // La pressione vera, a trasferimento in corso, chiude come sempre.
        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(40_000)));
    }

    [Fact]
    public void PassatoIlTempoDellEcoIlRitornoAllaSpiaSostituitaEUnaPressione()
    {
        var cuffia = CuffiaDaUfficio();
        var oltre = 10_020 + (int)HeadsetSignalInterpreter.EchoWindow.TotalMilliseconds + 100;

        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);
        cuffia.Reflect(default, Dopo(10_000));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), Dopo(10_020));

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(oltre)));
    }

    [Fact]
    public void UnaSpiaMostrataALungoNonFaScambiarePerEcoLaPressioneSuccessiva()
    {
        // Risposta da tastiera dopo un minuto di riposo. Passato il tempo dell'eco, un
        // "riagganciato" e' l'operatore che chiude: la finestra copre i rapporti
        // rimasti in viaggio, non la conversazione.
        var cuffia = CuffiaDaUfficio();
        var oltre = 60_000 + (int)HeadsetSignalInterpreter.EchoWindow.TotalMilliseconds + 100;

        cuffia.Reflect(default, T0);
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), Dopo(60_000));

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(oltre)));
    }

    // -----------------------------------------------------------------------
    // Rapporto scritto prima del cambio della spia
    // -----------------------------------------------------------------------

    [Fact]
    public void IlRapportoInRitardoDellaJabraNonChiudeLeChiamateSuccessive()
    {
        // La sequenza del registro del 26 novembre 2026, Jabra Evolve 30 II, con i
        // millisecondi veri. Risposta da tastiera; la cuffia conferma la linea accesa
        // subito e, seicento millisecondi dopo, manda un secondo rapporto per un altro
        // dei suoi valori. La chiamata viene chiusa proprio in mezzo: il secondo
        // rapporto arriva quattro millisecondi dopo lo spegnimento della spia, ancora
        // con la cornetta alzata.
        //
        // Preso per una pressione, il ritorno successivo faceva credere la cuffia a
        // impulsi, e da quel momento l'eco di ogni risposta chiudeva la chiamata in un
        // decimo di secondo, entranti e uscenti, fino al riavvio.
        var cuffia = CuffiaDaUfficio();

        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);              // 19:54:25.413
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(51)));   // 25.464 [0x20]

        cuffia.Reflect(default, Dopo(647));                                     // 26.060 chiusa
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(651)));  // 26.064 [0x97, 0x20]
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(898))); // 26.311 [0x97]
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(1_550))); // 26.963 []
        Assert.False(cuffia.HookSwitchIsMomentary);

        // La chiamata dopo, mezzo minuto piu' tardi: l'eco della risposta non la chiude.
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), Dopo(32_148));     // 57.561
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(32_199))); // 57.612 [0x20]

        // E la pressione vera dell'operatore la chiude come sempre.
        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(90_000)));
    }

    [Fact]
    public void IlRapportoInRitardoDopoLaRispostaNonChiudeLaChiamata()
    {
        // Lo stesso guasto nell'altro verso: l'interlocutore ha chiuso dopo dieci
        // minuti, squilla subito un'altra chiamata e l'operatore risponde da tastiera.
        // Il rapporto che la cuffia aveva scritto un istante prima porta ancora la
        // cornetta abbassata: e' lo stato di prima, non un dito sul tasto.
        var cuffia = CuffiaDaUfficio();

        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);
        cuffia.Reflect(default, Dopo(600_000));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), Dopo(603_000));

        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(603_004)));
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(603_050)));
    }

    [Fact]
    public void DopoUnRapportoInRitardoLaPressioneSuccessivaContaAncheSeLaCuffiaTace()
    {
        // Il rapporto in ritardo non dice dove si trova la cuffia. Se la cuffia poi
        // segue la spia senza mandare niente, la pressione con cui l'operatore
        // risponde alla chiamata seguente deve contare: restando sul valore vecchio,
        // risulterebbe uguale e andrebbe persa.
        var cuffia = CuffiaDaUfficio();

        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);
        cuffia.Reflect(default, Dopo(30_000));
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(30_004)));

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(50_000)));
    }

    [Fact]
    public void AncheLettaAImpulsiLaCuffiaCheSegueLaSpiaNonChiudeLaRisposta()
    {
        // Se una cuffia che segue la spia finisce letta come impulso, per qualunque
        // strada, l'eco della linea accesa e' un gancio che si alza da solo: contato
        // come appoggio del dito chiuderebbe ogni chiamata appena risposta.
        var cuffia = CuffiaDaUfficio();

        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, T0));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), T0);
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(180)));
        Assert.True(cuffia.HookSwitchIsMomentary);

        cuffia.Reflect(default, Dopo(60_000));
        cuffia.Reflect(new HeadsetIndicators(OffHook: true), Dopo(90_000));

        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(90_050)));
        Assert.False(cuffia.Feed(HeadsetButton.HookSwitch, false, Dopo(91_000)));

        // Passato il tempo dell'eco, l'appoggio del dito conta come sempre.
        Assert.True(cuffia.Feed(HeadsetButton.HookSwitch, true, Dopo(120_000)));
    }
}
