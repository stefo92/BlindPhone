using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Il livello di dettaglio degli annunci: Principiante, Standard, Esperto.
/// </summary>
/// <remarks>
/// <para>
/// Fino a novembre 2026 la scelta si salvava e non cambiava niente: il valore arrivava
/// al mappatore e il mappatore non lo leggeva. Questi test fissano che cosa fa ogni
/// livello, perché un'opzione che l'operatore non può verificare a schermo deve fare
/// esattamente quello che dice.
/// </para>
/// <para>
/// Principiante suggerisce i tasti in tre momenti: mentre squilla, appena in linea, in
/// pausa. Esperto affida al solo segnale le conferme di routine, e solo se i segnali
/// sono accesi.
/// </para>
/// </remarks>
public sealed class LivelloDiDettaglioTests
{
    private static readonly DateTimeOffset Adesso = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(1));

    private static readonly HotkeyProfile Predefinito = HotkeyProfile.CreateDefault();

    private static CallAnnouncementMapper Mappatore(
        VerbosityLevel livello,
        bool segnali = true,
        bool semplificata = false)
        => new(
            DigitGrouping.Single,
            livello,
            segnali,
            comando => CallKeyHints.KeyName(Predefinito, semplificata, comando));

    /// <summary>Porta una chiamata attraverso una sequenza di eventi e restituisce l'ultimo annuncio.</summary>
    private static Announcement Annuncio(CallAnnouncementMapper mappatore, CallDirection verso, params CallTrigger[] eventi)
    {
        var chiamata = new Call("c-1", verso, new CallParty("4021", "Mario Rossi"), Adesso);
        Announcement? ultimo = null;

        foreach (var evento in eventi)
        {
            Assert.True(chiamata.TryApply(evento, Adesso, out var transizione), $"{evento} non applicabile");
            ultimo = mappatore.Map(transizione, chiamata, Adesso);
        }

        return ultimo!;
    }

    // -----------------------------------------------------------------------
    // Principiante
    // -----------------------------------------------------------------------

    [Fact]
    public void PrincipianteSenteITastiMentreSquilla()
    {
        var annuncio = Annuncio(Mappatore(VerbosityLevel.Beginner), CallDirection.Inbound, CallTrigger.IncomingCall);

        Assert.EndsWith(". F2 risponde, F3 rifiuta", annuncio.Text);
        Assert.False(annuncio.EarconOnly);
    }

    [Fact]
    public void PrincipianteSenteITastiAppenaInLinea()
    {
        var annuncio = Annuncio(
            Mappatore(VerbosityLevel.Beginner), CallDirection.Inbound,
            CallTrigger.IncomingCall, CallTrigger.Answer);

        Assert.Equal("In linea con Mario Rossi. F4 attesa, F9 trasferisce, F3 chiude", annuncio.Text);
    }

    [Fact]
    public void PrincipianteSenteITastiQuandoRispondeChiHaChiamato()
    {
        var annuncio = Annuncio(
            Mappatore(VerbosityLevel.Beginner), CallDirection.Outbound,
            CallTrigger.OutgoingCall, CallTrigger.RemoteAnswered);

        Assert.EndsWith(". F4 attesa, F9 trasferisce, F3 chiude", annuncio.Text);
    }

    [Fact]
    public void PrincipianteSenteComeRiprenderLaChiamataInPausa()
    {
        var annuncio = Annuncio(
            Mappatore(VerbosityLevel.Beginner), CallDirection.Inbound,
            CallTrigger.IncomingCall, CallTrigger.Answer, CallTrigger.Hold);

        Assert.Equal("Chiamata in pausa. F4 la riprende", annuncio.Text);
    }

    [Fact]
    public void PrincipianteNonSenteTastiDoveNonServono()
    {
        var annuncio = Annuncio(
            Mappatore(VerbosityLevel.Beginner), CallDirection.Inbound,
            CallTrigger.IncomingCall, CallTrigger.Answer, CallTrigger.Hold, CallTrigger.Resume);

        Assert.Equal("Chiamata ripresa", annuncio.Text);
    }

    [Fact]
    public void InModalitaSemplificataSiNominanoITastiDelTastierino()
    {
        var mappatore = Mappatore(VerbosityLevel.Beginner, semplificata: true);

        var squilla = Annuncio(mappatore, CallDirection.Inbound, CallTrigger.IncomingCall);
        var inLinea = Annuncio(mappatore, CallDirection.Inbound, CallTrigger.IncomingCall, CallTrigger.Answer);

        Assert.EndsWith(". più risponde, diviso rifiuta", squilla.Text);

        // Il trasferimento in modalita' semplificata e' comporre l'interno e poi
        // Invio: un nome di tasto solo lo descriverebbe male, e non si suggerisce.
        Assert.Equal("In linea con Mario Rossi. più attesa, diviso chiude", inLinea.Text);
    }

    [Fact]
    public void IlTastoSuggeritoSegueIlProfiloAttivo()
    {
        var profilo = HotkeyProfile.CreateDefault();
        var mappatore = new CallAnnouncementMapper(
            DigitGrouping.Single,
            VerbosityLevel.Beginner,
            earconsEnabled: true,
            comando => CallKeyHints.KeyName(profilo, simplified: false, comando));

        profilo.Set(new HotkeyBinding(HotkeyCommand.Answer, "F11", HotkeyModifiers.None, HotkeyScope.Local));

        var annuncio = Annuncio(mappatore, CallDirection.Inbound, CallTrigger.IncomingCall);

        Assert.EndsWith(". F11 risponde, F3 rifiuta", annuncio.Text);
    }

    [Fact]
    public void LaRigaBrailleNonPortaITasti()
    {
        var principiante = Annuncio(Mappatore(VerbosityLevel.Beginner), CallDirection.Inbound, CallTrigger.IncomingCall);
        var standard = Annuncio(Mappatore(VerbosityLevel.Standard), CallDirection.Inbound, CallTrigger.IncomingCall);

        Assert.Equal(standard.BrailleText, principiante.BrailleText);
    }

    // -----------------------------------------------------------------------
    // Standard
    // -----------------------------------------------------------------------

    [Fact]
    public void StandardNonSuggerisceTastiENonTace()
    {
        var mappatore = Mappatore(VerbosityLevel.Standard);

        var squilla = Annuncio(mappatore, CallDirection.Inbound, CallTrigger.IncomingCall);
        var pausa = Annuncio(mappatore, CallDirection.Inbound, CallTrigger.IncomingCall, CallTrigger.Answer, CallTrigger.Hold);

        Assert.DoesNotContain("risponde", squilla.Text);
        Assert.Equal("Chiamata in pausa", pausa.Text);
        Assert.False(pausa.EarconOnly);
    }

    // -----------------------------------------------------------------------
    // Esperto
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(CallDirection.Inbound, new[] { CallTrigger.IncomingCall, CallTrigger.Answer, CallTrigger.Hold })]
    [InlineData(CallDirection.Inbound, new[] { CallTrigger.IncomingCall, CallTrigger.Answer, CallTrigger.Hold, CallTrigger.Resume })]
    [InlineData(CallDirection.Inbound, new[] { CallTrigger.IncomingCall, CallTrigger.Answer })]
    [InlineData(CallDirection.Inbound, new[] { CallTrigger.IncomingCall, CallTrigger.HangUp })]
    [InlineData(CallDirection.Outbound, new[] { CallTrigger.OutgoingCall, CallTrigger.HangUp })]
    [InlineData(CallDirection.Outbound, new[] { CallTrigger.OutgoingCall, CallTrigger.RemoteAnswered })]
    public void EspertoSenteSoloIlSegnaleDelleConfermeDiRoutine(CallDirection verso, CallTrigger[] eventi)
    {
        var annuncio = Annuncio(Mappatore(VerbosityLevel.Expert), verso, eventi);

        Assert.True(annuncio.EarconOnly);
        Assert.NotEqual(EarconKind.None, annuncio.Earcon);

        // Il testo resta: finisce nel registro e si puo' far ripetere a voce.
        Assert.False(string.IsNullOrWhiteSpace(annuncio.Text));
    }

    [Theory]
    [InlineData(CallDirection.Inbound, new[] { CallTrigger.IncomingCall })]
    [InlineData(CallDirection.Inbound, new[] { CallTrigger.IncomingCall, CallTrigger.RemoteHangUp })]
    [InlineData(CallDirection.Outbound, new[] { CallTrigger.OutgoingCall, CallTrigger.RemoteRejected })]
    [InlineData(CallDirection.Inbound, new[] { CallTrigger.IncomingCall, CallTrigger.Answer, CallTrigger.RemoteHangUp })]
    public void EspertoSenteSempreAVoceCioCheArrivaDaFuori(CallDirection verso, CallTrigger[] eventi)
    {
        var annuncio = Annuncio(Mappatore(VerbosityLevel.Expert), verso, eventi);

        Assert.False(annuncio.EarconOnly);
    }

    [Fact]
    public void ConISegnaliSpentiEspertoTornaAVoce()
    {
        var annuncio = Annuncio(
            Mappatore(VerbosityLevel.Expert, segnali: false), CallDirection.Inbound,
            CallTrigger.IncomingCall, CallTrigger.Answer, CallTrigger.Hold);

        Assert.False(annuncio.EarconOnly);
        Assert.Equal("Chiamata in pausa", annuncio.Text);
    }

    [Fact]
    public void IlLivelloCambiaACaldo()
    {
        var mappatore = Mappatore(VerbosityLevel.Standard);

        mappatore.UpdateOptions(DigitGrouping.Single, VerbosityLevel.Expert, earconsEnabled: true);
        var esperto = Annuncio(mappatore, CallDirection.Inbound, CallTrigger.IncomingCall, CallTrigger.Answer, CallTrigger.Hold);

        mappatore.UpdateOptions(DigitGrouping.Single, VerbosityLevel.Beginner, earconsEnabled: true);
        var principiante = Annuncio(mappatore, CallDirection.Inbound, CallTrigger.IncomingCall, CallTrigger.Answer, CallTrigger.Hold);

        Assert.True(esperto.EarconOnly);
        Assert.Equal("Chiamata in pausa. F4 la riprende", principiante.Text);
    }

    [Fact]
    public void NessunaTransizioneDiventaMutaInNessunLivello()
    {
        // La stessa garanzia di AnnouncementCoverageTests, estesa ai tre livelli: ogni
        // transizione si sente, a voce o con il suo segnale.
        foreach (var livello in Enum.GetValues<VerbosityLevel>())
        {
            foreach (var segnali in new[] { true, false })
            {
                var mappatore = Mappatore(livello, segnali);

                foreach (var transizione in CallStateMachine.Transitions)
                {
                    var chiamata = new Call("c-1", CallDirection.Inbound, new CallParty("4021", "Mario Rossi"), Adesso);
                    var annuncio = mappatore.Map(transizione, chiamata, Adesso);

                    Assert.False(string.IsNullOrWhiteSpace(annuncio.Text));
                    if (annuncio.EarconOnly) Assert.NotEqual(EarconKind.None, annuncio.Earcon);
                    if (!segnali) Assert.False(annuncio.EarconOnly);
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Il servizio degli annunci
    // -----------------------------------------------------------------------

    [Fact]
    public void UnAnnuncioSoloSegnaleSuonaAggiornaIlBrailleETaceLaVoce()
    {
        var voce = new RecordingSpeechOutput();
        var segnali = new RecordingEarconPlayer();
        var servizio = new AnnouncementService(voce, segnali, new AnnouncementContext(), clock: new ManualClock(Adesso));

        servizio.Announce(Announcement.High("Chiamata in pausa", Adesso, EarconKind.CallHeld, braille: "pausa")
            with { EarconOnly = true });
        servizio.Pump(Adesso.AddSeconds(1));

        Assert.Empty(voce.Spoken);
        Assert.Contains("pausa", voce.BrailleMessages);
        Assert.Contains(EarconKind.CallHeld, segnali.Played);
        Assert.Equal("Chiamata in pausa", Assert.Single(servizio.History).Text);
    }

    [Fact]
    public void UnAnnuncioSoloSegnaleSiPuoFarRipetereAVoce()
    {
        var voce = new RecordingSpeechOutput();
        var servizio = new AnnouncementService(voce, new RecordingEarconPlayer(), new AnnouncementContext(), clock: new ManualClock(Adesso));

        servizio.Announce(Announcement.High("Chiamata in pausa", Adesso, EarconKind.CallHeld) with { EarconOnly = true });
        servizio.RepeatLast();

        Assert.Equal("Chiamata in pausa", Assert.Single(voce.Spoken).Text);
    }
}
