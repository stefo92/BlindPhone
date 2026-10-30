using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica la traduzione dei pulsanti della cuffia in comandi, e dello stato della
/// postazione nelle spie della cuffia.
/// </summary>
/// <remarks>
/// <para>
/// Il pulsante della cuffia si preme senza guardare e spesso senza volerlo,
/// sistemandosi la cuffia sulla testa. Un errore qui non è un tasto che non risponde:
/// è una chiamata chiusa in faccia a qualcuno che stava aspettando.
/// </para>
/// <para>
/// La tabella completa sta qui, come quella del tastierino in
/// <see cref="SimplifiedKeypadTests"/>, e si legge tutta insieme.
/// </para>
/// </remarks>
public sealed class PulsantiDellaCuffiaTests
{
    private static HotkeyCommand? Gancio(CallActionContext stato)
        => HeadsetButtons.Resolve(HeadsetButton.HookSwitch, stato);

    private static HotkeyCommand? Muto(CallActionContext stato)
        => HeadsetButtons.Resolve(HeadsetButton.PhoneMute, stato);

    private static HotkeyCommand? Flash(CallActionContext stato)
        => HeadsetButtons.Resolve(HeadsetButton.Flash, stato);

    private static HotkeyCommand? Richiamata(CallActionContext stato)
        => HeadsetButtons.Resolve(HeadsetButton.Redial, stato);

    // -----------------------------------------------------------------------
    // Il pulsante della chiamata
    // -----------------------------------------------------------------------

    [Fact]
    public void IlPulsanteDellaChiamataRispondeAllaChiamataCheSquilla()
    {
        Assert.Equal(HotkeyCommand.Answer, Gancio(new CallActionContext(HasRingingCall: true)));
    }

    [Fact]
    public void ConUnaConversazioneApertaIlPulsanteRispondeAllaSecondaChiamataComeF2()
    {
        // Rispondere mette in attesa chi e' in linea: e' quello che fa F2 su un posto
        // operatore, dove la seconda chiamata si prende senza buttare giu' la prima.
        Assert.Equal(HotkeyCommand.Answer, Gancio(new CallActionContext(
            HasRingingCall: true, HasActiveCall: true)));
    }

    [Fact]
    public void IlPulsanteDellaChiamataChiudeLaConversazione()
    {
        Assert.Equal(HotkeyCommand.HangUp, Gancio(new CallActionContext(HasActiveCall: true)));
    }

    [Fact]
    public void IlPulsanteDellaChiamataChiudeLaConferenza()
    {
        Assert.Equal(HotkeyCommand.HangUp, Gancio(new CallActionContext(IsConferenceActive: true)));
    }

    [Fact]
    public void IlPulsanteDellaChiamataRinunciaAllaChiamataCheStaPartendo()
    {
        Assert.Equal(HotkeyCommand.HangUp, Gancio(new CallActionContext(HasOutgoingCall: true)));
    }

    [Fact]
    public void DuranteUnaConsultazioneIlPulsanteFaQuelloCheFaF3()
    {
        // Chiude la linea con l'interno e riporta al chiamante: la decisione la
        // prende l'orchestratore del trasferimento, che riceve lo stesso comando di F3.
        Assert.Equal(HotkeyCommand.HangUp, Gancio(new CallActionContext(
            HasActiveCall: true, TransferPhase: TransferPhase.Consulting)));
    }

    [Fact]
    public void ConLaSolaChiamataInAttesaIlPulsanteLaRiprendeInveceDiChiuderla()
    {
        // Il pulsante si tocca anche per sbaglio. Chi e' in attesa sta aspettando
        // l'operatore: chiudergli il telefono in faccia per un tocco e' il danno
        // peggiore che questo pulsante possa fare.
        Assert.Equal(HotkeyCommand.ToggleHold, Gancio(new CallActionContext(HeldCount: 1)));
    }

    [Fact]
    public void ARiposoIlPulsanteDellaChiamataNonFaNiente()
    {
        Assert.Null(Gancio(default));
    }

    [Fact]
    public void IlTastoPlayEPausaFaQuelloCheFaIlPulsanteDellaChiamata()
    {
        // E' il ripiego per le cuffie senza pulsanti telefonici: deve fare la stessa
        // cosa, altrimenti l'operatore che cambia cuffia deve reimparare il gesto.
        CallActionContext[] stati =
        [
            default,
            new(HasRingingCall: true),
            new(HasActiveCall: true),
            new(HeldCount: 1),
            new(HasOutgoingCall: true),
        ];

        foreach (var stato in stati)
        {
            Assert.Equal(Gancio(stato), HeadsetButtons.Resolve(HeadsetButton.PlayPause, stato));
        }
    }

    // -----------------------------------------------------------------------
    // Il tasto del microfono
    // -----------------------------------------------------------------------

    [Fact]
    public void IlTastoDelMicrofonoSilenziaDuranteLaConversazione()
    {
        Assert.Equal(HotkeyCommand.ToggleMute, Muto(new CallActionContext(HasActiveCall: true)));
    }

    [Fact]
    public void IlTastoDelMicrofonoFunzionaAncheConLaChiamataInAttesa()
    {
        Assert.Equal(HotkeyCommand.ToggleMute, Muto(new CallActionContext(HeldCount: 1)));
    }

    [Fact]
    public void ARiposoIlTastoDelMicrofonoNonFaNiente()
    {
        // Silenziare a riposo farebbe partire muta la chiamata successiva, e
        // l'operatore se ne accorgerebbe solo dal chiamante che non lo sente. Capita
        // davvero: la stessa cuffia serve anche a un altro programma di telefonia, che
        // riceve lo stesso tasto.
        Assert.Null(Muto(default));
    }

    // -----------------------------------------------------------------------
    // Flash e richiamata
    // -----------------------------------------------------------------------

    [Fact]
    public void FlashRispondeAllaSecondaChiamata()
    {
        Assert.Equal(HotkeyCommand.Answer, Flash(new CallActionContext(
            HasRingingCall: true, HasActiveCall: true)));
    }

    [Fact]
    public void FlashAlternaFraLaChiamataInLineaEQuellaInAttesa()
    {
        Assert.Equal(HotkeyCommand.Swap, Flash(new CallActionContext(HasActiveCall: true, HeldCount: 1)));
    }

    [Fact]
    public void FlashMetteInAttesaERiprende()
    {
        Assert.Equal(HotkeyCommand.ToggleHold, Flash(new CallActionContext(HasActiveCall: true)));
        Assert.Equal(HotkeyCommand.ToggleHold, Flash(new CallActionContext(HeldCount: 1)));
    }

    [Fact]
    public void DuranteUnTrasferimentoFlashNonFaNiente()
    {
        // Le linee sono due e "metti in attesa" non direbbe quale: e' la stessa
        // ragione per cui il + del tastierino tace in quella fase.
        Assert.Null(Flash(new CallActionContext(
            HasActiveCall: true, TransferPhase: TransferPhase.Consulting)));
    }

    [Fact]
    public void LaRichiamataFunzionaSoloARiposo()
    {
        Assert.Equal(HotkeyCommand.RedialLast, Richiamata(default));

        // Con una chiamata aperta comporre ne aprirebbe una seconda in parallelo.
        Assert.Null(Richiamata(new CallActionContext(HasActiveCall: true)));
        Assert.Null(Richiamata(new CallActionContext(HeldCount: 1)));
        Assert.Null(Richiamata(new CallActionContext(HasRingingCall: true)));
        Assert.Null(Richiamata(new CallActionContext(IsServicePaused: true)));
    }

    [Fact]
    public void OgniPulsanteHaUnNomeInItaliano()
    {
        foreach (var pulsante in Enum.GetValues<HeadsetButton>())
        {
            var nome = HeadsetButtons.Describe(pulsante);

            Assert.False(string.IsNullOrWhiteSpace(nome));
            Assert.DoesNotContain(pulsante.ToString(), nome, StringComparison.Ordinal);
        }
    }

    // -----------------------------------------------------------------------
    // Le spie
    // -----------------------------------------------------------------------

    [Fact]
    public void ARiposoLaCuffiaHaTutteLeSpieSpente()
    {
        Assert.True(HeadsetButtons.Indicators(default).IsIdle);
    }

    [Fact]
    public void LaSpiaDelloSquilloSegueLaChiamataEntrante()
    {
        var spie = HeadsetButtons.Indicators(new CallActionContext(HasRingingCall: true));

        Assert.True(spie.Ring);
        Assert.False(spie.OffHook);
    }

    [Fact]
    public void InConversazioneLaCuffiaEInLinea()
    {
        Assert.True(HeadsetButtons.Indicators(new CallActionContext(HasActiveCall: true)).OffHook);
        Assert.True(HeadsetButtons.Indicators(new CallActionContext(IsConferenceActive: true)).OffHook);
        Assert.True(HeadsetButtons.Indicators(new CallActionContext(HasOutgoingCall: true)).OffHook);
    }

    [Fact]
    public void ConLaSolaChiamataInAttesaLaCornettaEAbbassataELaSpiaDellAttesaAccesa()
    {
        // E' lo stato in cui il pulsante riprende invece di chiudere: la cuffia deve
        // essere d'accordo, altrimenti la pressione successiva la manderebbe nella
        // direzione sbagliata.
        var spie = HeadsetButtons.Indicators(new CallActionContext(HeldCount: 1));

        Assert.False(spie.OffHook);
        Assert.True(spie.Hold);
    }

    [Fact]
    public void LaSpiaDelMicrofonoMutoSiAccendeSoloDuranteUnaChiamata()
    {
        Assert.True(HeadsetButtons.Indicators(new CallActionContext(HasActiveCall: true, IsMuted: true)).Mute);
        Assert.False(HeadsetButtons.Indicators(new CallActionContext(IsMuted: true)).Mute);
    }

    [Fact]
    public void SiAccendonoSoloLeSpieCheLaCuffiaHa()
    {
        var soloLinea = new HashSet<ushort> { HidTelephonyUsages.OffHookLed };
        var spie = new HeadsetIndicators(Ring: true, OffHook: true, Mute: true);

        Assert.Equal(new[] { HidTelephonyUsages.OffHookLed }, HidTelephonyUsages.LitLeds(spie, soloLinea));
    }

    // -----------------------------------------------------------------------
    // Dagli usage HID ai pulsanti
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0x0B, 0x20, HeadsetButton.HookSwitch)]
    [InlineData(0x0B, 0x2F, HeadsetButton.PhoneMute)]
    [InlineData(0x0B, 0x21, HeadsetButton.Flash)]
    [InlineData(0x0B, 0x24, HeadsetButton.Redial)]
    [InlineData(0x0C, 0xCD, HeadsetButton.PlayPause)]
    public void GliUsageDelloStandardDiventanoIPulsantiDellaCuffia(int pagina, int usage, HeadsetButton atteso)
    {
        Assert.Equal(atteso, HidTelephonyUsages.ButtonFor((ushort)pagina, (ushort)usage));
    }

    [Theory]
    [InlineData(0x0B, 0x06)] // tastierino telefonico
    [InlineData(0x0B, 0x2B)] // altoparlante
    [InlineData(0x08, 0x20)] // la spia dell'attesa ha lo stesso numero del gancio, ma e' un'altra pagina
    [InlineData(0x0C, 0xE9)] // volume su
    public void GliAltriUsageSonoIgnorati(int pagina, int usage)
    {
        Assert.Null(HidTelephonyUsages.ButtonFor((ushort)pagina, (ushort)usage));
    }

    [Fact]
    public void LaDescrizioneDiceSeLaCuffiaPuoRispondere()
    {
        var completa = new HeadsetCapabilities(
            new HashSet<HeadsetButton> { HeadsetButton.HookSwitch, HeadsetButton.PhoneMute },
            HookSwitchIsAbsolute: true,
            new HashSet<ushort> { HidTelephonyUsages.RingLed, HidTelephonyUsages.OffHookLed });

        var soloMuto = new HeadsetCapabilities(
            new HashSet<HeadsetButton> { HeadsetButton.PhoneMute },
            HookSwitchIsAbsolute: false,
            new HashSet<ushort>());

        Assert.Equal("pulsanti: risposta e chiusura, microfono; spie: squillo, linea", completa.Describe());
        Assert.Equal("pulsanti: microfono; nessuna spia comandabile", soloMuto.Describe());
        Assert.True(completa.CanAnswer);
        Assert.False(soloMuto.CanAnswer);
    }
}
