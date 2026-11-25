using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che gli annunci in coda si dicano uno alla volta, ognuno intero.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da una segnalazione: <i>«quando BlindPhone viene aggiornato gli annunci di
/// registrazione, cuffia e aggiornamenti si accavallano e non si capisce ciò che
/// succede»</i>. All'avvio le tre notizie cadevano nello stesso secondo. La
/// registrazione persa era critica e interrompeva; le altre arrivavano allo screen
/// reader come notifiche «la più recente», e NVDA, ricevendone una, zittisce quella in
/// corso. Restavano spezzoni di tre frasi e nessuna intera.
/// </para>
/// <para>
/// La durata di una frase si stima dalla sua lunghezza. Qui la velocità è di dieci
/// caratteri al secondo e senza pausa, perché i conti si leggano a occhio: «Telefono
/// registrato» sono diciannove caratteri, quindi un secondo e nove decimi.
/// </para>
/// </remarks>
public sealed class AnnunciInCodaTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 25, 8, 0, 0, TimeSpan.FromHours(1));

    private static (AnnouncementService Service, RecordingSpeechOutput Speech,
        RecordingEarconPlayer Earcons, ManualClock Clock) Build(TimeSpan? maxWait = null)
    {
        var speech = new RecordingSpeechOutput();
        var earcons = new RecordingEarconPlayer();
        var clock = new ManualClock(Start);

        var options = new AnnouncementOptions
        {
            SpeechCharactersPerSecond = 10,
            SequencePause = TimeSpan.Zero,
            SequenceMaxWait = maxWait ?? TimeSpan.FromSeconds(30),
        };

        var service = new AnnouncementService(speech, earcons, new AnnouncementContext(), options, clock);
        return (service, speech, earcons, clock);
    }

    private static Announcement InCoda(Announcement announcement) => announcement with { Sequential = true };

    private static void Avanza(AnnouncementService service, ManualClock clock, double milliseconds)
    {
        clock.AdvanceMilliseconds(milliseconds);
        service.Pump(clock.Now);
    }

    // -----------------------------------------------------------------------
    // Il difetto segnalato
    // -----------------------------------------------------------------------

    [Fact]
    public void LeNotizieDellAvvioSiDiconoUnaAllaVoltaSenzaInterrompersi()
    {
        var (service, speech, _, clock) = Build();

        service.Announce(InCoda(Announcement.Normal("Cuffia Jabra pronta", clock.Now, coalesceKey: "cuffia:avvio")));
        service.Announce(InCoda(Announcement.High(
            "Telefono registrato", clock.Now, EarconKind.RegistrationRestored, coalesceKey: "sip:registration")));
        service.Announce(InCoda(Announcement.Normal("Aggiornamento disponibile", clock.Now, coalesceKey: "update:available")));

        Avanza(service, clock, 900);

        // Una sola, la piu' importante: le altre aspettano il loro turno.
        Assert.Single(speech.Spoken);
        Assert.Equal("Telefono registrato", speech.Spoken[0].Text);

        // Diciannove caratteri, un secondo e nove decimi: prima non parla nessun altro.
        Avanza(service, clock, 1800);
        Assert.Single(speech.Spoken);

        Avanza(service, clock, 100);
        Assert.Equal(2, speech.Spoken.Count);
        Assert.Equal("Cuffia Jabra pronta", speech.Spoken[1].Text);

        Avanza(service, clock, 1900);
        Assert.Equal(3, speech.Spoken.Count);
        Assert.Equal("Aggiornamento disponibile", speech.Spoken[2].Text);

        // Nessuna frase ha tagliato quella prima.
        Assert.All(speech.Spoken, frase => Assert.False(frase.Interrupt));
    }

    [Fact]
    public void LaRegistrazionePersaInCodaNonTagliaLaFraseInCorso()
    {
        var (service, speech, earcons, clock) = Build();

        service.Announce(InCoda(Announcement.Normal("Cuffia Jabra collegata", clock.Now)));
        service.Pump(clock.Now);
        Assert.Single(speech.Spoken);

        clock.AdvanceMilliseconds(500);
        service.Announce(InCoda(Announcement.Critical(
            "Attenzione: telefono non registrato", clock.Now, EarconKind.RegistrationLost,
            coalesceKey: "sip:registration", braille: "NON REG")));

        // Fuori coda un critico parlerebbe adesso, senza aspettare il pump, e
        // interrompendo. In coda aspetta: e il suono aspetta con lui, perche' sotto la
        // frase della cuffia non spiegherebbe niente.
        Assert.Single(speech.Spoken);
        Assert.DoesNotContain(EarconKind.RegistrationLost, earcons.Played);

        // «Cuffia Jabra collegata», ventidue caratteri: finisce a due secondi e due.
        Avanza(service, clock, 1600);
        Assert.Single(speech.Spoken);

        Avanza(service, clock, 200);
        Assert.Equal(2, speech.Spoken.Count);
        Assert.Equal("Attenzione: telefono non registrato", speech.Spoken[1].Text);
        Assert.False(speech.Spoken[1].Interrupt);
        Assert.Contains(EarconKind.RegistrationLost, earcons.Played);
    }

    [Fact]
    public void RegistrazioneInCorsoSeguitaDaRegistratoSiDiceUnaVoltaSola()
    {
        var (service, speech, earcons, clock) = Build();

        service.Announce(InCoda(Announcement.Critical(
            "Registrazione in corso", clock.Now, EarconKind.RegistrationLost, coalesceKey: "sip:registration")));

        clock.AdvanceMilliseconds(300);
        service.Announce(InCoda(Announcement.High(
            "Telefono registrato", clock.Now, EarconKind.RegistrationRestored, coalesceKey: "sip:registration")));

        // La scadenza resta quella del primo: sotto una raffica di cambi di stato
        // l'annuncio non deve slittare all'infinito.
        Avanza(service, clock, 500);

        Assert.Single(speech.Spoken);
        Assert.Equal("Telefono registrato", speech.Spoken[0].Text);

        // Solo il suono della notizia vera: quello della registrazione persa, sentito
        // all'avvio di una postazione che funziona, insegnerebbe a ignorarlo.
        Assert.Equal([EarconKind.RegistrationRestored], earcons.Played);

        Avanza(service, clock, 10_000);
        Assert.Single(speech.Spoken);
    }

    // -----------------------------------------------------------------------
    // Quello che la coda non cambia
    // -----------------------------------------------------------------------

    [Fact]
    public void UnaChiamataInArrivoInterrompeAncheLaCodaEIlTurnoDopoAspettaLei()
    {
        var (service, speech, _, clock) = Build();

        service.Announce(InCoda(Announcement.Normal("Cuffia Jabra collegata", clock.Now)));
        service.Announce(InCoda(Announcement.Normal("Telefono registrato", clock.Now)));
        service.Pump(clock.Now);
        Assert.Single(speech.Spoken);

        clock.AdvanceMilliseconds(500);
        service.Announce(Announcement.Critical("Chiamata da Mario Rossi", clock.Now));

        // La chiamata non aspetta nessuno.
        Assert.Equal(2, speech.Spoken.Count);
        Assert.True(speech.Spoken[1].Interrupt);

        // La cuffia avrebbe finito a due secondi e due, ma e' stata interrotta: il
        // turno dopo aspetta la fine della chiamata in arrivo, a due secondi e otto.
        Avanza(service, clock, 2000);
        Assert.Equal(2, speech.Spoken.Count);

        Avanza(service, clock, 300);
        Assert.Equal(3, speech.Spoken.Count);
        Assert.Equal("Telefono registrato", speech.Spoken[2].Text);
    }

    [Fact]
    public void LaCodaSiDichiaraFermaSoloQuandoHaFinitoDiParlare()
    {
        var (service, _, _, clock) = Build();
        Assert.True(service.IsSequenceIdle);

        service.Announce(InCoda(Announcement.Normal("Telefono registrato", clock.Now)));
        Assert.False(service.IsSequenceIdle);

        service.Pump(clock.Now);

        // Detto, ma ancora in corso: la finestra dell'aggiornamento aperta adesso lo
        // taglierebbe, perche' passando in primo piano zittisce lo screen reader.
        Assert.False(service.IsSequenceIdle);

        clock.AdvanceMilliseconds(1900);
        Assert.True(service.IsSequenceIdle);
    }

    [Fact]
    public void ChiZittisceLaPostazioneSvuotaAncheLaCoda()
    {
        var (service, speech, _, clock) = Build();

        service.Announce(InCoda(Announcement.Normal("Cuffia Jabra collegata", clock.Now)));
        service.Announce(InCoda(Announcement.Normal("Telefono registrato", clock.Now)));
        service.Pump(clock.Now);

        service.Silence();

        Assert.True(service.IsSequenceIdle);

        Avanza(service, clock, 10_000);
        Assert.Single(speech.Spoken);
    }

    [Fact]
    public void UnTestoLungoNonTieneFermaLaCodaOltreIlTetto()
    {
        var (service, speech, _, clock) = Build(maxWait: TimeSpan.FromSeconds(3));

        // Duecento caratteri sarebbero venti secondi: il tetto ne concede tre.
        service.Announce(InCoda(Announcement.Normal(new string('a', 200), clock.Now)));
        service.Announce(InCoda(Announcement.Normal("Cuffia scollegata", clock.Now)));

        service.Pump(clock.Now);
        Assert.Single(speech.Spoken);

        Avanza(service, clock, 2900);
        Assert.Single(speech.Spoken);

        Avanza(service, clock, 100);
        Assert.Equal(2, speech.Spoken.Count);
        Assert.Equal("Cuffia scollegata", speech.Spoken[1].Text);
    }

    [Fact]
    public void UnAnnuncioInCodaScartatoInIngressoFaSentireSubitoIlSuono()
    {
        var (service, speech, earcons, clock) = Build();

        service.Announce(InCoda(Announcement.High(
            "Telefono registrato", clock.Now, EarconKind.RegistrationRestored, coalesceKey: "sip:registration")));
        Avanza(service, clock, 900);
        Assert.Single(earcons.Played);

        // Identico e ravvicinato: la voce lo scarta, il suono no, e non ha un turno
        // da aspettare.
        clock.AdvanceMilliseconds(200);
        service.Announce(InCoda(Announcement.High(
            "Telefono registrato", clock.Now, EarconKind.RegistrationRestored, coalesceKey: "sip:registration")));

        Assert.Equal(2, earcons.Played.Count);
        Assert.Single(speech.Spoken);
    }
}
