using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica l'orologio del turno e la pausa della postazione.
/// </summary>
/// <remarks>
/// Il tempo qui è iniettato e mai letto dall'orologio di sistema: è ciò che permette
/// di collaudare in millisecondi comportamenti che altrimenti si potrebbero
/// verificare solo restando seduti due ore.
/// </remarks>
public sealed class DutyStatusTests
{
    private static readonly DateTimeOffset Inizio = new(2026, 8, 8, 8, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void IlTempoInServizioScorreDallAperturaDellaPostazione()
    {
        var duty = new DutyStatusService();
        duty.Restart(Inizio);

        Assert.Equal(TimeSpan.FromMinutes(45), duty.InService(Inizio.AddMinutes(45)));
        Assert.False(duty.IsPaused);
    }

    [Fact]
    public void DuranteLaPausaIlTempoInServizioSiFerma()
    {
        // E' la differenza fra un tempo di servizio e un tempo di accensione: il
        // secondo non risponde alla domanda che l'operatore si pone.
        var duty = new DutyStatusService();
        duty.Restart(Inizio);

        duty.Pause(Inizio.AddMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(30), duty.InService(Inizio.AddMinutes(50)));
        Assert.Equal(TimeSpan.FromMinutes(20), duty.CurrentPause(Inizio.AddMinutes(50)));
    }

    [Fact]
    public void DopoLaPausaIlTempoInServizioRiprendeDaDoveEraRimasto()
    {
        var duty = new DutyStatusService();
        duty.Restart(Inizio);

        duty.Pause(Inizio.AddMinutes(30));
        duty.Resume(Inizio.AddMinutes(45));

        // Un'ora di orologio, quarantacinque minuti di servizio.
        Assert.Equal(TimeSpan.FromMinutes(45), duty.InService(Inizio.AddMinutes(60)));
        Assert.Equal(TimeSpan.FromMinutes(15), duty.TotalPaused(Inizio.AddMinutes(60)));
        Assert.Equal(TimeSpan.Zero, duty.CurrentPause(Inizio.AddMinutes(60)));
    }

    [Fact]
    public void LePauseSiSommanoSulTurno()
    {
        var duty = new DutyStatusService();
        duty.Restart(Inizio);

        duty.Pause(Inizio.AddMinutes(10));
        duty.Resume(Inizio.AddMinutes(15));
        duty.Pause(Inizio.AddMinutes(40));
        duty.Resume(Inizio.AddMinutes(50));

        Assert.Equal(TimeSpan.FromMinutes(15), duty.TotalPaused(Inizio.AddMinutes(60)));
        Assert.Equal(TimeSpan.FromMinutes(45), duty.InService(Inizio.AddMinutes(60)));
    }

    [Fact]
    public void UnaSecondaPausaNonAzzeraQuellaInCorso()
    {
        // Il tasto premuto due volte per nervosismo non deve far ripartire il
        // conteggio da zero: l'operatore crederebbe di essersi appena fermato.
        var duty = new DutyStatusService();
        duty.Restart(Inizio);

        Assert.True(duty.Pause(Inizio.AddMinutes(10)));
        Assert.False(duty.Pause(Inizio.AddMinutes(12)));

        Assert.Equal(TimeSpan.FromMinutes(5), duty.CurrentPause(Inizio.AddMinutes(15)));
    }

    [Fact]
    public void RiprendereQuandoNonSiEInPausaNonCambiaNulla()
    {
        var duty = new DutyStatusService();
        duty.Restart(Inizio);

        Assert.False(duty.Resume(Inizio.AddMinutes(10)));
        Assert.Equal(TimeSpan.Zero, duty.TotalPaused(Inizio.AddMinutes(10)));
    }

    [Fact]
    public void IlCambioDiStatoVieneSegnalato()
    {
        var duty = new DutyStatusService();
        duty.Restart(Inizio);

        var eventi = new List<bool>();
        duty.PauseChanged += (_, paused) => eventi.Add(paused);

        duty.Pause(Inizio.AddMinutes(5));
        duty.Pause(Inizio.AddMinutes(6));   // gia' in pausa: nessun evento
        duty.Resume(Inizio.AddMinutes(10));

        Assert.Equal([true, false], eventi);
    }

    [Fact]
    public void UnoScartoDiOrologioNonProduceTempiNegativi()
    {
        var duty = new DutyStatusService();
        duty.Restart(Inizio);

        Assert.Equal(TimeSpan.Zero, duty.InService(Inizio.AddSeconds(-5)));
        Assert.Equal(TimeSpan.Zero, duty.TotalPaused(Inizio.AddSeconds(-5)));
    }

    // -----------------------------------------------------------------------
    // Come si legge e come si dice
    // -----------------------------------------------------------------------

    [Fact]
    public void AschermoLOrologioDelTurnoCambiaFormaInPausa()
    {
        // Due stati che si distinguessero per una sola parola si confonderebbero a
        // colpo d'occhio, che e' l'unico modo in cui questa riga viene letta.
        Assert.Equal(
            "In servizio: 2:14:07",
            CallBoardTexts.ServiceClock(new TimeSpan(2, 14, 7), TimeSpan.Zero));

        Assert.Equal(
            "IN PAUSA da 0:04:31",
            CallBoardTexts.ServiceClock(new TimeSpan(2, 14, 7), new TimeSpan(0, 4, 31)));
    }

    [Fact]
    public void IlTempoInServizioSiPronunciaSenzaCitareLePauseCheNonCiSonoState()
    {
        var testo = AnnouncementTexts.ServiceTime(TimeSpan.FromMinutes(45), TimeSpan.Zero);

        Assert.Equal("In servizio da 45 minuti", testo);
        Assert.DoesNotContain("pausa", testo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConPauseAlleSpalleIlTotaleVieneDetto()
    {
        var testo = AnnouncementTexts.ServiceTime(TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(15));

        Assert.Contains("45 minuti", testo, StringComparison.Ordinal);
        Assert.Contains("15 minuti di pausa", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LAnnuncioDiPausaDiceCheFineFannoLeChiamateEComeUscirne()
    {
        var testo = AnnouncementTexts.ServicePauseStarted("Control Alt Pausa");

        // Senza sapere che il chiamante viene passato a un collega, un operatore
        // prudente resterebbe alla postazione: cioe' non andrebbe in pausa.
        Assert.Contains("rifiutate", testo, StringComparison.Ordinal);
        Assert.Contains("collega", testo, StringComparison.Ordinal);
        Assert.Contains("Control Alt Pausa", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void AchiProvaADigitareInPausaSiDiceDaQuantoEComeTornareInServizio()
    {
        var testo = AnnouncementTexts.TypingWhilePaused(TimeSpan.FromMinutes(4), "Control Alt Pausa");

        Assert.Equal("Postazione in pausa da 4 minuti. Premi Control Alt Pausa per tornare in servizio", testo);
    }

    [Fact]
    public void IlRientroInServizioDiceQuantoEDuratoLoStacco()
    {
        var testo = AnnouncementTexts.ServicePauseEnded(TimeSpan.FromMinutes(12));

        Assert.Contains("12 minuti", testo, StringComparison.Ordinal);
        Assert.Contains("tornano ad arrivare", testo, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Il tasto
    // -----------------------------------------------------------------------

    [Fact]
    public void IlTastoPausaEAssegnatoAControlAltPausa()
    {
        var profilo = HotkeyProfile.CreateDefault();
        var binding = profilo.Get(HotkeyCommand.ToggleServicePause, HotkeyScope.Local);

        Assert.NotNull(binding);
        Assert.Equal("Pause", binding.Key);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, binding.Modifiers);
    }

    [Fact]
    public void ControlAltPausaVieneRiconosciutoAncheQuandoArrivaComeCancel()
    {
        // Windows consegna il tasto Pausa come "Cancel" quando c'e' Control premuto:
        // senza la riduzione a un nome solo, il comando non partirebbe mai.
        var profilo = HotkeyProfile.CreateDefault();

        var risolto = profilo.Resolve(
            HotkeyKeyNames.Normalize("Cancel"),
            HotkeyModifiers.Control | HotkeyModifiers.Alt,
            HotkeyScope.Local);

        Assert.Equal(HotkeyCommand.ToggleServicePause, risolto);
    }

    [Fact]
    public void IlTastoPausaSiPronunciaPausa()
    {
        Assert.Equal("Pausa", HotkeyKeyNames.Speech("Pause"));
        Assert.Equal("Pausa", HotkeyKeyNames.Speech("Cancel"));
    }

    [Fact]
    public void LaPausaDellaPostazioneNonSiSovrapponeAQuellaInCoda()
    {
        // Sono due funzioni diverse — una passa dal SIP, l'altra dall'API — e devono
        // restare su due tasti diversi, altrimenti una delle due diventa irraggiungibile.
        var profilo = HotkeyProfile.CreateDefault();

        var postazione = profilo.Get(HotkeyCommand.ToggleServicePause, HotkeyScope.Local);
        var coda = profilo.Get(HotkeyCommand.ToggleAgentPause, HotkeyScope.Local);

        Assert.NotNull(postazione);
        Assert.NotNull(coda);
        Assert.NotEqual(postazione.ConflictKey, coda.ConflictKey);
    }
}
