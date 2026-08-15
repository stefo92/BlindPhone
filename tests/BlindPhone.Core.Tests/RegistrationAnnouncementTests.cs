using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che la registrazione SIP si annunci quando cambia, e solo allora.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da una segnalazione dal campo: <i>«a volte sento il suono che comunica
/// l'autenticazione al server SIP»</i>. Non era un guasto della linea ma il rinnovo
/// periodico della registrazione — trentasette volte in un giorno sulla postazione di
/// prova — trattato come se fosse un cambiamento di stato.
/// </para>
/// <para>
/// Il difetto si sentiva più di quanto si vedesse: il segnale acustico di un annuncio
/// viene suonato <b>prima</b> di ogni valutazione sulla voce e non è sopprimibile,
/// quindi durante una conversazione arrivava il suono senza la frase che lo
/// spiegava. Un segnale che non significa niente è peggio di nessun segnale, perché
/// insegna a ignorarne uno che un giorno significherà «il telefono non riceve più
/// chiamate».
/// </para>
/// </remarks>
public sealed class RegistrationAnnouncementTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 9, 15, 0, 0, TimeSpan.FromHours(2));

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public CallSessionManager Calls { get; }

        public Harness()
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), new AnnouncementContext(),
                contacts: null, options: null, clock: Clock, logger: null);
        }

        public int AnnunciDiRegistrazione => Announcer.Announcements
            .Count(a => a.Earcon is EarconKind.RegistrationRestored or EarconKind.RegistrationLost);
    }

    // -----------------------------------------------------------------------
    // Il difetto segnalato
    // -----------------------------------------------------------------------

    [Fact]
    public void UnRinnovoCheNonCambiaStatoNonProduceAnnuncio()
    {
        var h = new Harness();

        h.Engine.SimulateRegistration(RegistrationStatus.Registered);
        Assert.Equal(1, h.AnnunciDiRegistrazione);

        // Il rinnovo periodico: stessa situazione, notificata di nuovo. Sulla
        // postazione di prova succedeva ogni dieci-quattordici minuti.
        h.Engine.SimulateRegistration(RegistrationStatus.Registered);
        h.Engine.SimulateRegistration(RegistrationStatus.Registered);
        h.Engine.SimulateRegistration(RegistrationStatus.Registered);

        Assert.Equal(1, h.AnnunciDiRegistrazione);
    }

    [Fact]
    public void NemmenoIlSegnaleAcusticoEsceSuUnRinnovo()
    {
        // E' la parte che l'operatore sentiva davvero: il segnale precede la voce e
        // non e' sopprimibile, quindi passava anche durante la conversazione.
        var h = new Harness();

        h.Engine.SimulateRegistration(RegistrationStatus.Registered);
        var dopoIlPrimo = h.Announcer.Earcons.Count;

        h.Engine.SimulateRegistration(RegistrationStatus.Registered);

        Assert.Equal(dopoIlPrimo, h.Announcer.Earcons.Count);
    }

    // -----------------------------------------------------------------------
    // Quello che invece deve continuare ad arrivare
    // -----------------------------------------------------------------------

    [Fact]
    public void LaPerditaDellaRegistrazioneSiAnnunciaSempre()
    {
        var h = new Harness();

        h.Engine.SimulateRegistration(RegistrationStatus.Registered);
        h.Announcer.Clear();

        h.Engine.SimulateRegistration(RegistrationStatus.Failed, "Credenziali rifiutate dal centralino");

        Assert.Contains(EarconKind.RegistrationLost, h.Announcer.Earcons);
    }

    [Fact]
    public void IlRipristinoDopoUnaCadutaSiAnnuncia()
    {
        // E' il caso per cui l'allarme esiste: senza questo annuncio l'operatore
        // resterebbe a credersi irraggiungibile mentre le chiamate ricominciano ad
        // arrivare.
        var h = new Harness();

        h.Engine.SimulateRegistration(RegistrationStatus.Registered);
        h.Engine.SimulateRegistration(RegistrationStatus.Failed, "Centralino non raggiungibile");
        h.Announcer.Clear();

        h.Engine.SimulateRegistration(RegistrationStatus.Registered);

        Assert.Contains(EarconKind.RegistrationRestored, h.Announcer.Earcons);
    }

    [Fact]
    public void DueGuastiDiversiSiAnnuncianoEntrambi()
    {
        // Il motivo fa parte dello stato: passare da "credenziali rifiutate" a
        // "centralino non raggiungibile" e' una notizia diversa, e chi chiama
        // l'assistenza deve riferire l'ultima.
        var h = new Harness();

        h.Engine.SimulateRegistration(RegistrationStatus.Failed, "Credenziali rifiutate dal centralino");
        h.Announcer.Clear();

        h.Engine.SimulateRegistration(RegistrationStatus.Failed, "Centralino non raggiungibile");

        Assert.Equal(1, h.AnnunciDiRegistrazione);
    }

    [Fact]
    public void LoStessoGuastoRipetutoSiAnnunciaUnaVoltaSola()
    {
        var h = new Harness();

        h.Engine.SimulateRegistration(RegistrationStatus.Failed, "Centralino non raggiungibile");
        h.Announcer.Clear();

        h.Engine.SimulateRegistration(RegistrationStatus.Failed, "Centralino non raggiungibile");

        Assert.Equal(0, h.AnnunciDiRegistrazione);
    }

    // -----------------------------------------------------------------------
    // La regola di confronto
    // -----------------------------------------------------------------------

    [Fact]
    public void LIstanteNonFaPartetteDelConfronto()
    {
        // ChangedAt dice quando lo stato e' CAMBIATO: un rinnovo che non cambia
        // niente non deve spostarlo in avanti, e non deve nemmeno far sembrare
        // diverso uno stato identico.
        var prima = new SipRegistrationState(RegistrationStatus.Registered, "Comune", null, Start);
        var dopo = new SipRegistrationState(
            RegistrationStatus.Registered, "Comune", null, Start.AddMinutes(11));

        Assert.True(prima.RepresentsSameStateAs(dopo));
    }

    [Fact]
    public void CambiareAccountEUnCambiamentoDaAnnunciare()
    {
        // Registrato prima e registrato dopo, ma e' un'altra postazione telefonica:
        // chi copre due enti deve sentirsi dire su quale dei due si trova.
        var primo = new SipRegistrationState(RegistrationStatus.Registered, "Comune", null, Start);
        var secondo = new SipRegistrationState(RegistrationStatus.Registered, "Ospedale", null, Start);

        Assert.False(primo.RepresentsSameStateAs(secondo));
    }

    [Fact]
    public void UnoStatoAssenteNonEUguagliabile()
    {
        var stato = new SipRegistrationState(RegistrationStatus.Registered, "Comune", null, Start);

        Assert.False(stato.RepresentsSameStateAs(null));
    }
}
