using BlindPhone.Core.Accessibility;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica quali segnali acustici tacciono togliendo la spunta «Riproduci i segnali
/// acustici».
/// </summary>
/// <remarks>
/// <para>
/// La spunta c'era, si salvava e non la leggeva nessuno: toglierla non spegneva
/// niente. Collegarla apre il rischio opposto, che è peggiore: spegnere con i segnali
/// di commento anche la suoneria, e lasciare la postazione a squillare in silenzio.
/// </para>
/// <para>
/// Per questo la tabella si verifica nei due sensi: ciò che deve restare resta, e ciò
/// che accompagna soltanto un annuncio tace davvero.
/// </para>
/// </remarks>
public sealed class InterruttoreDeiSegnaliTests
{
    [Fact]
    public void LaSuoneriaSuonaAncheConISegnaliSpenti()
    {
        // Una chiamata che entra senza farsi sentire e' il guasto peggiore possibile.
        Assert.True(EarconSwitch.ShouldPlay(EarconKind.IncomingCall, earconsEnabled: false));
    }

    [Theory]
    [InlineData(EarconKind.RemoteRinging)]
    [InlineData(EarconKind.LineBusy)]
    [InlineData(EarconKind.LineUnavailable)]
    public void IToniDellaLineaRestano(EarconKind kind)
    {
        // La rete spesso non li manda: senza, il silenzio dopo una composizione non si
        // distingue da una cuffia guasta.
        Assert.True(EarconSwitch.ShouldPlay(kind, earconsEnabled: false));
    }

    [Theory]
    [InlineData(EarconKind.RegistrationLost)]
    [InlineData(EarconKind.HoldReminder)]
    [InlineData(EarconKind.CommandRejected)]
    public void GliAllarmiRestano(EarconKind kind)
        => Assert.True(EarconSwitch.ShouldPlay(kind, earconsEnabled: false));

    [Theory]
    [InlineData(EarconKind.QueueCallArrived)]
    [InlineData(EarconKind.QueueEmptied)]
    [InlineData(EarconKind.QueueWaitThresholdExceeded)]
    [InlineData(EarconKind.CallAnswered)]
    [InlineData(EarconKind.CallEnded)]
    [InlineData(EarconKind.CallHeld)]
    [InlineData(EarconKind.CallResumed)]
    [InlineData(EarconKind.ConferenceStarted)]
    [InlineData(EarconKind.ConferenceEnded)]
    [InlineData(EarconKind.TransferSucceeded)]
    [InlineData(EarconKind.TransferFailed)]
    [InlineData(EarconKind.RegistrationRestored)]
    [InlineData(EarconKind.PbxLinkDegraded)]
    [InlineData(EarconKind.PbxLinkRestored)]
    public void ISegnaliCheAccompagnanoUnAnnuncioTacciono(EarconKind kind)
    {
        // E' la ragione della spunta: senza almeno questi, toglierla non cambierebbe
        // niente, che era proprio il difetto.
        Assert.False(EarconSwitch.ShouldPlay(kind, earconsEnabled: false));
    }

    [Fact]
    public void ConISegnaliAccesiSuonaTutto()
    {
        foreach (var kind in Enum.GetValues<EarconKind>())
        {
            if (kind == EarconKind.None) continue;

            Assert.True(EarconSwitch.ShouldPlay(kind, earconsEnabled: true), $"{kind} non suona a segnali accesi");
        }
    }

    [Fact]
    public void NessunSegnaleNonSuonaMai()
    {
        Assert.False(EarconSwitch.ShouldPlay(EarconKind.None, earconsEnabled: true));
        Assert.False(EarconSwitch.ShouldPlay(EarconKind.None, earconsEnabled: false));
    }

    [Fact]
    public void OgniSegnaleEClassificato()
    {
        // Un segnale nuovo che finisce fra gli accessori senza che nessuno l'abbia
        // deciso tacerebbe a spunta tolta: questo elenco va aggiornato insieme
        // all'enumerazione, ed e' il momento di chiedersi da che parte sta.
        EarconKind[] essenziali =
        [
            EarconKind.IncomingCall,
            EarconKind.RemoteRinging,
            EarconKind.LineBusy,
            EarconKind.LineUnavailable,
            EarconKind.RegistrationLost,
            EarconKind.HoldReminder,
            EarconKind.CommandRejected,
        ];

        EarconKind[] accessori =
        [
            EarconKind.QueueCallArrived,
            EarconKind.QueueEmptied,
            EarconKind.QueueWaitThresholdExceeded,
            EarconKind.CallAnswered,
            EarconKind.CallEnded,
            EarconKind.CallHeld,
            EarconKind.CallResumed,
            EarconKind.ConferenceStarted,
            EarconKind.ConferenceEnded,
            EarconKind.TransferSucceeded,
            EarconKind.TransferFailed,
            EarconKind.RegistrationRestored,
            EarconKind.PbxLinkDegraded,
            EarconKind.PbxLinkRestored,
        ];

        foreach (var kind in Enum.GetValues<EarconKind>())
        {
            if (kind == EarconKind.None) continue;

            Assert.True(
                essenziali.Contains(kind) ^ accessori.Contains(kind),
                $"{kind} non e' classificato: va messo fra gli essenziali o fra gli accessori");

            Assert.Equal(essenziali.Contains(kind), EarconSwitch.IsEssential(kind));
        }
    }
}
