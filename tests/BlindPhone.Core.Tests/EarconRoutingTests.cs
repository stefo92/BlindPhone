using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche su quale dispositivo suona ciascun segnale acustico.
/// </summary>
/// <remarks>
/// <para>
/// È la regola che rende utile l'audio a quattro vie, e un errore qui non si vede
/// leggendo il codice né compilando: si manifesta come un suono che arriva dalla parte
/// sbagliata della scrivania. Chi lo scrive, con una cuffia sola in testa, non se ne
/// accorge affatto.
/// </para>
/// <para>
/// Il caso che ha originato questi test: la suoneria usciva dal dispositivo della
/// conversazione, cioè dalla cuffia, mentre il pannello prometteva il dispositivo
/// chiamato «suoneria».
/// </para>
/// </remarks>
public sealed class EarconRoutingTests
{
    [Fact]
    public void LaSuoneriaEsceDalDispositivoDellaSuoneria()
    {
        // Su un posto operatore la chiamata successiva arriva mentre la precedente e'
        // ancora in corso: uno squillo nell'orecchio coprirebbe l'interlocutore.
        Assert.Equal(AudioRoute.Ringer, EarconRouting.RouteFor(EarconKind.IncomingCall));
    }

    [Theory]
    [InlineData(EarconKind.QueueCallArrived)]
    [InlineData(EarconKind.QueueEmptied)]
    [InlineData(EarconKind.QueueWaitThresholdExceeded)]
    public void GliEventiDiCodaEsconoDagliAltoparlanti(EarconKind kind)
    {
        // Arrivano senza che l'operatore abbia fatto nulla, e spesso mentre parla.
        Assert.Equal(AudioRoute.Ringer, EarconRouting.RouteFor(kind));
    }

    [Theory]
    [InlineData(EarconKind.RegistrationLost)]
    [InlineData(EarconKind.CommandRejected)]
    public void GliAllarmiDiSistemaEsconoDagliAltoparlanti(EarconKind kind)
        => Assert.Equal(AudioRoute.Ringer, EarconRouting.RouteFor(kind));

    [Theory]
    [InlineData(EarconKind.CallAnswered)]
    [InlineData(EarconKind.CallEnded)]
    [InlineData(EarconKind.CallHeld)]
    [InlineData(EarconKind.CallResumed)]
    public void IRiscontriDiUnGestoRestanoDoveSiAscolta(EarconKind kind)
    {
        // Sono la risposta a qualcosa che l'operatore ha appena fatto: vanno dove sta
        // ascoltando, non dall'altra parte della scrivania.
        Assert.Equal(AudioRoute.Conversation, EarconRouting.RouteFor(kind));
    }

    [Fact]
    public void OgniSegnaleHaUnaViaAssegnata()
    {
        // Un segnale senza via finirebbe sul predefinito di sistema, cioe' ignorando
        // la configurazione dell'operatore proprio come faceva il prodotto prima.
        foreach (var kind in Enum.GetValues<EarconKind>())
        {
            if (kind == EarconKind.None) continue;

            var route = EarconRouting.RouteFor(kind);

            Assert.True(
                route is AudioRoute.Ringer or AudioRoute.Conversation,
                $"Il segnale {kind} finirebbe sulla via {route}, che non riproduce suoni");
        }
    }
}
