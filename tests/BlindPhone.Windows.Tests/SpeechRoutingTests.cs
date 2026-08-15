using BlindPhone.Accessibility;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifiche sulla regola che decide chi pronuncia gli annunci.
/// </summary>
/// <remarks>
/// <para>
/// I due modi in cui questa regola può sbagliare sono entrambi invisibili a chi
/// guarda lo schermo, e opposti fra loro. <b>Parlare due volte</b>: la voce di
/// BlindPhone si sovrappone allo screen reader che sta leggendo la stessa frase, e il
/// risultato è meno comprensibile di una voce sola. <b>Non parlare affatto</b>: su una
/// postazione senza screen reader il posto operatore diventa muto, e un operatore non
/// vedente non ha modo di accorgersi che è arrivata una chiamata.
/// </para>
/// <para>
/// Il caso che ha originato questi test: la modalità automatica considerava "screen
/// reader presente" solo quando funzionava il bridge diretto, che richiede
/// <c>nvdaControllerClient64.dll</c> accanto all'eseguibile. Dove quella libreria non
/// c'è — cioè nel pacchetto distribuito — concludeva "nessuno screen reader" e
/// parlava con voce propria mentre NVDA leggeva la notifica UI Automation.
/// </para>
/// </remarks>
public sealed class SpeechRoutingTests
{
    [Fact]
    public void InModalitaAutomaticaLaVocePropriaTaceSeParlaGiaLoScreenReader()
        => Assert.False(CompositeSpeechOutput.ShouldSpeakInternally(
            SpeechChannelMode.Automatic, internalVoiceEnabled: true, handledByAssistiveTech: true));

    [Fact]
    public void InModalitaAutomaticaSenzaScreenReaderLaVocePropriaParla()
        => Assert.True(CompositeSpeechOutput.ShouldSpeakInternally(
            SpeechChannelMode.Automatic, internalVoiceEnabled: true, handledByAssistiveTech: false));

    [Theory]
    [InlineData(SpeechChannelMode.Automatic, true)]
    [InlineData(SpeechChannelMode.Automatic, false)]
    [InlineData(SpeechChannelMode.InternalVoiceOnly, true)]
    [InlineData(SpeechChannelMode.InternalVoiceOnly, false)]
    [InlineData(SpeechChannelMode.Both, true)]
    [InlineData(SpeechChannelMode.Both, false)]
    [InlineData(SpeechChannelMode.UiaOnly, true)]
    [InlineData(SpeechChannelMode.ScreenReaderOnly, false)]
    public void LInterruttoreSpentoZittisceLaVocePropriaInOgniModalita(
        SpeechChannelMode modalita, bool gestitoDaTecnologiaAssistiva)
        => Assert.False(CompositeSpeechOutput.ShouldSpeakInternally(
            modalita, internalVoiceEnabled: false, gestitoDaTecnologiaAssistiva));

    [Theory]
    [InlineData(SpeechChannelMode.InternalVoiceOnly)]
    [InlineData(SpeechChannelMode.Both)]
    public void LeModalitaCheChiedonoLaVocePropriaLaUsanoAncheConLoScreenReader(SpeechChannelMode modalita)
        => Assert.True(CompositeSpeechOutput.ShouldSpeakInternally(
            modalita, internalVoiceEnabled: true, handledByAssistiveTech: true));

    [Theory]
    [InlineData(SpeechChannelMode.UiaOnly)]
    [InlineData(SpeechChannelMode.ScreenReaderOnly)]
    public void LeModalitaRiservateAlloScreenReaderNonFannoMaiParlareBlindPhone(SpeechChannelMode modalita)
        => Assert.False(CompositeSpeechOutput.ShouldSpeakInternally(
            modalita, internalVoiceEnabled: true, handledByAssistiveTech: false));

    [Fact]
    public void ConLaVocePropriaAccesaEsisteSempreUnCanaleCheParla()
    {
        // La configurazione predefinita di una postazione senza screen reader: se
        // questa combinazione tacesse, il posto operatore sarebbe muto e nessuno se
        // ne accorgerebbe fino alla prima chiamata persa.
        Assert.True(CompositeSpeechOutput.ShouldSpeakInternally(
            SpeechChannelMode.Automatic, internalVoiceEnabled: true, handledByAssistiveTech: false));
    }
}
