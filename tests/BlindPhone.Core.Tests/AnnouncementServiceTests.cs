using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica le regole di dosaggio della voce.
/// </summary>
/// <remarks>
/// Sono le regole che decidono se il prodotto è usabile per un turno intero o se
/// dopo mezz'ora l'operatore lo spegne. Un annuncio in più durante una
/// conversazione costa più di dieci annunci mancati in un momento di calma.
/// </remarks>
public sealed class AnnouncementServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(2));

    private static (AnnouncementService Service, RecordingSpeechOutput Speech,
        RecordingEarconPlayer Earcons, AnnouncementContext Context, ManualClock Clock) Build(
        AnnouncementOptions? options = null)
    {
        var speech = new RecordingSpeechOutput();
        var earcons = new RecordingEarconPlayer();
        var context = new AnnouncementContext();
        var clock = new ManualClock(Start);

        var service = new AnnouncementService(speech, earcons, context, options, clock);
        return (service, speech, earcons, context, clock);
    }

    [Fact]
    public void GliAnnunciCriticiParlanoSubitoSenzaAspettareIlPump()
    {
        var (service, speech, _, _, clock) = Build();

        service.Announce(Announcement.Critical("Chiamata da Mario Rossi", clock.Now));

        // Nessun Pump: un annuncio critico che aspetta la finestra di accorpamento
        // arriverebbe dopo che il telefono ha gia' squillato.
        Assert.Single(speech.Spoken);
        Assert.True(speech.Spoken[0].Interrupt);
    }

    [Fact]
    public void TreEventiRavvicinatiConLaStessaChiaveProduconoUnSoloAnnuncio()
    {
        var (service, speech, _, _, clock) = Build();

        service.Announce(Announcement.Normal("1 chiamata in coda", clock.Now, coalesceKey: "queue:count"));
        clock.AdvanceMilliseconds(150);
        service.Announce(Announcement.Normal("2 chiamate in coda", clock.Now, coalesceKey: "queue:count"));
        clock.AdvanceMilliseconds(150);
        service.Announce(Announcement.Normal("3 chiamate in coda", clock.Now, coalesceKey: "queue:count"));

        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        // Sopravvive solo l'ultimo, che e' l'unico ancora vero.
        Assert.Single(speech.Spoken);
        Assert.Equal("3 chiamate in coda", speech.Spoken[0].Text);
    }

    [Fact]
    public void LAccorpamentoNonRinviaAllInfinitoSottoUnaRafficaContinua()
    {
        var (service, speech, _, _, clock) = Build();

        // Un evento ogni 100 ms per un secondo e mezzo: se la finestra si
        // riavviasse a ogni evento, l'operatore non sentirebbe mai nulla.
        for (var i = 1; i <= 15; i++)
        {
            service.Announce(Announcement.Normal($"{i} in coda", clock.Now, coalesceKey: "queue:count"));
            clock.AdvanceMilliseconds(100);
            service.Pump(clock.Now);
        }

        Assert.NotEmpty(speech.Spoken);
    }

    [Fact]
    public void UnAnnuncioIdenticoRavvicinatoVieneScartato()
    {
        var (service, speech, _, _, clock) = Build();

        service.Announce(Announcement.Normal("Coda vuota", clock.Now, coalesceKey: "queue:count"));
        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        service.Announce(Announcement.Normal("Coda vuota", clock.Now, coalesceKey: "queue:count"));
        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        Assert.Single(speech.Spoken);
    }

    [Fact]
    public void DuranteLaConversazioneGliAnnunciSiSentono()
    {
        // E' il comportamento predefinito da quando e' arrivata la segnalazione «gli
        // annunci cessano da quando squilla la chiamata a quando termina»: fra la
        // risposta e la chiusura l'operatore non vedente restava senza nessuna
        // notizia, e le cose che accadono mentre si parla sono proprio quelle che
        // contano — una seconda chiamata, un trasferimento fallito, la registrazione
        // che si ferma.
        var (service, speech, _, context, clock) = Build();
        context.IsConversationActive = true;

        service.Announce(Announcement.Normal(
            "Nuova chiamata in coda", clock.Now, coalesceKey: "queue:count"));

        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        Assert.Single(speech.Spoken);
    }

    [Fact]
    public void ChiVuoleIlSilenzioLoRiattivaEAlloraRestaSoloLEarcon()
    {
        // L'interruttore c'e' ancora, in Impostazioni: chi preferisce il silenzio
        // assoluto durante la conversazione lo ritrova, e continua a funzionare come
        // prima — la voce tace, il segnale acustico passa lo stesso.
        var (service, speech, earcons, context, clock) = Build(
            new AnnouncementOptions { SilenceDuringConversation = true });

        context.IsConversationActive = true;

        service.Announce(Announcement.Normal(
            "Nuova chiamata in coda",
            clock.Now,
            EarconKind.QueueCallArrived,
            coalesceKey: "queue:count"));

        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        // La voce non copre la conversazione, ma l'informazione arriva comunque.
        Assert.Empty(speech.Spoken);
        Assert.Contains(EarconKind.QueueCallArrived, earcons.Played);
    }

    [Fact]
    public void LaRispostaAUnComandoParlaAncheDuranteLaConversazione()
    {
        var (service, speech, _, context, clock) = Build();
        context.IsConversationActive = true;

        // L'operatore preme il tasto del conteggio mentre parla: e' esattamente il
        // momento in cui gli serve la risposta.
        service.Announce(Announcement.CommandResponse("3 chiamate in coda", clock.Now));
        service.Pump(clock.Now);

        Assert.Single(speech.Spoken);
        Assert.Equal("3 chiamate in coda", speech.Spoken[0].Text);
    }

    [Fact]
    public void DueRispostePerDuePressioniDelloStessoTasto()
    {
        var (service, speech, _, _, clock) = Build();

        // Senza chiave di accorpamento l'anti-ripetizione non si applica: chi preme
        // due volte deve sentire due conferme, altrimenti crede che la seconda
        // pressione non sia passata e preme una terza volta.
        service.Announce(Announcement.CommandResponse("Nessuna chiamata in coda", clock.Now));
        service.Pump(clock.Now);
        service.Announce(Announcement.CommandResponse("Nessuna chiamata in coda", clock.Now));
        service.Pump(clock.Now);

        Assert.Equal(2, speech.Spoken.Count);
    }

    [Fact]
    public void DuranteLaDigitazioneGliAnnunciNonCriticiSonoSoppressi()
    {
        var (service, speech, _, context, clock) = Build();
        context.IsOperatorTyping = true;

        service.Announce(Announcement.Normal("Nuova chiamata in coda", clock.Now, coalesceKey: "queue:count"));
        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        Assert.Empty(speech.Spoken);

        // Una chiamata entrante passa comunque: va annunciata anche mentre si
        // compone un numero.
        service.Announce(Announcement.Critical("Chiamata da Bianchi", clock.Now));
        Assert.Single(speech.Spoken);
    }

    [Fact]
    public void GliAnnunciPiuImportantiParlanoPrima()
    {
        var (service, speech, _, _, clock) = Build();

        service.Announce(Announcement.Normal("Nuova chiamata in coda", clock.Now, coalesceKey: "queue:count"));
        service.Announce(Announcement.High("Trasferimento completato", clock.Now, coalesceKey: "transfer:result"));

        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        Assert.Equal(2, speech.Spoken.Count);
        Assert.Equal("Trasferimento completato", speech.Spoken[0].Text);
    }

    [Fact]
    public void LoStoricoConservaGliUltimiAnnunciDalPiuRecente()
    {
        var (service, _, _, _, clock) = Build(new AnnouncementOptions { HistorySize = 3 });

        for (var i = 1; i <= 5; i++)
        {
            service.Announce(Announcement.CommandResponse($"messaggio {i}", clock.Now));
            service.Pump(clock.Now);
            clock.AdvanceMilliseconds(3000);
        }

        var history = service.History;
        Assert.Equal(3, history.Count);
        Assert.Equal("messaggio 5", history[0].Text);
    }

    [Fact]
    public void SilenzioSvuotaLaCodaEInterrompeLaVoce()
    {
        var (service, speech, _, _, clock) = Build();

        service.Announce(Announcement.Normal("in arrivo", clock.Now, coalesceKey: "queue:count"));
        service.Silence();

        clock.AdvanceMilliseconds(900);
        service.Pump(clock.Now);

        Assert.Empty(speech.Spoken);
        Assert.Equal(1, speech.StopCount);
    }

    [Fact]
    public void IlMotivoDelloScartoVieneSegnalato()
    {
        var (service, _, _, context, clock) = Build(
            new AnnouncementOptions { SilenceDuringConversation = true });

        context.IsConversationActive = true;

        AnnouncementDropReason? reason = null;
        service.Dropped += (_, e) => reason = e.Reason;

        service.Announce(Announcement.Normal("qualcosa", clock.Now, coalesceKey: "x"));

        // Serve durante la messa a punto con operatori reali: alla domanda
        // "perche' non me l'ha detto?" si risponde senza rileggere il codice.
        Assert.Equal(AnnouncementDropReason.ConversationActive, reason);
    }
}
