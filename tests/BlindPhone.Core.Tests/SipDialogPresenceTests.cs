using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sulla lettura dello stato di un interno per via SIP.
/// </summary>
/// <remarks>
/// <para>
/// È la regola su cui si regge la sorveglianza degli interni sui centralini senza API,
/// cioè la maggioranza del parco installato: il centralino manda l'elenco dei dialoghi
/// in corso su un interno, e da quello si deduce se è libero, se squilla o se è
/// occupato.
/// </para>
/// <para>
/// È anche l'unica parte del percorso SIP che si possa verificare senza un centralino
/// reale, e per questo la copertura qui è più fitta del solito.
/// </para>
/// </remarks>
public sealed class SipDialogPresenceTests
{
    [Fact]
    public void NessunDialogoSignificaLibero()
        => Assert.Equal(PresenceState.Available, SipDialogPresence.FromDialogStates([]));

    [Fact]
    public void UnDialogoConfermatoSignificaOccupato()
        => Assert.Equal(PresenceState.Busy, SipDialogPresence.FromDialogStates(["confirmed"]));

    [Theory]
    [InlineData("early")]
    [InlineData("proceeding")]
    [InlineData("trying")]
    public void UnDialogoNonAncoraRispostoSignificaSquilla(string stato)
        => Assert.Equal(PresenceState.Ringing, SipDialogPresence.FromDialogStates([stato]));

    [Fact]
    public void UnDialogoChiusoNonOccupaLInterno()
    {
        // Un dialogo terminato compare nella notifica proprio per dire che e' finito:
        // contarlo come occupazione lascerebbe l'interno "occupato" per sempre.
        Assert.Equal(PresenceState.Available, SipDialogPresence.FromDialogStates(["terminated"]));
    }

    [Fact]
    public void ConPiuDialoghiVinceIlPiuImpegnativo()
    {
        // Una conversazione in corso e una seconda chiamata che squilla: l'interno e'
        // occupato, non squillante. E' l'ordine in cui l'operatore decide.
        Assert.Equal(PresenceState.Busy, SipDialogPresence.FromDialogStates(["early", "confirmed"]));
        Assert.Equal(PresenceState.Busy, SipDialogPresence.FromDialogStates(["confirmed", "terminated"]));
        Assert.Equal(PresenceState.Ringing, SipDialogPresence.FromDialogStates(["terminated", "early"]));
    }

    [Fact]
    public void LoStatoSiLeggeSenzaBadareAMaiuscoleESpazi()
    {
        Assert.Equal(PresenceState.Busy, SipDialogPresence.FromDialogStates(["CONFIRMED"]));
        Assert.Equal(PresenceState.Ringing, SipDialogPresence.FromDialogStates([" early "]));
    }

    [Fact]
    public void UnoStatoNonPrevistoSiContaComeOccupazione()
    {
        // Ipotesi prudente: meglio un "occupato" di troppo che una chiamata passata a
        // un telefono gia' impegnato.
        Assert.Equal(PresenceState.Busy, SipDialogPresence.FromDialogStates(["qualcosa-di-nuovo"]));
    }

    [Fact]
    public void UnDialogoSenzaStatoNonCambiaNulla()
        => Assert.Equal(PresenceState.Available, SipDialogPresence.FromDialogStates([null, string.Empty]));

    [Theory]
    [InlineData(404)]
    [InlineData(480)]
    [InlineData(604)]
    public void UnInternoInesistenteOIrraggiungibileEFuoriServizio(int codice)
        => Assert.Equal(PresenceState.Offline, SipDialogPresence.FromSubscriptionFailure(codice));

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(503)]
    public void UnRifiutoDiAutorizzazioneNonEUnoStatoDelTelefono(int codice)
    {
        // Spacciarlo per "fuori servizio" farebbe evitare all'operatore un interno che
        // funziona benissimo: e' un limite di configurazione, non del telefono.
        Assert.Null(SipDialogPresence.FromSubscriptionFailure(codice));
    }
}
