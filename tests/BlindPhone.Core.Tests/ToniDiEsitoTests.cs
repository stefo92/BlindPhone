using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che una chiamata che non va a buon fine produca un tono in cuffia.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da una segnalazione dal campo: chiamando un numero fuori servizio
/// l'operatore non sentiva nulla in cuffia e restava disorientato. Il registro ha
/// mostrato perché — il centralino rifiuta con <c>480</c> e <c>Content-Length: 0</c>,
/// cioè senza mandare alcun audio — e su un telefono tradizionale quel tono lo genera
/// l'apparecchio, non la rete.
/// </para>
/// <para>
/// Per chi non vede il silenzio dopo una composizione non è un'informazione: è
/// indistinguibile da una cuffia scollegata, da un microfono spento e da un programma
/// bloccato. È la classe di difetto che fa perdere fiducia nello strumento invece che
/// nella chiamata.
/// </para>
/// </remarks>
public sealed class ToniDiEsitoTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 10, 0, 0, TimeSpan.FromHours(2));

    /// <summary>Porta una chiamata uscente fino al rifiuto, e restituisce l'annuncio.</summary>
    private static Announcement AnnuncioDelRifiuto(CallEndReason reason)
    {
        var call = new Call("c-1", CallDirection.Outbound, new CallParty("3494251406"), Now);

        call.TryApply(CallTrigger.OutgoingCall, Now, out _);
        call.SetEndReason(reason);
        call.TryApply(CallTrigger.RemoteRejected, Now, out var transizione);

        return new CallAnnouncementMapper().Map(transizione, call, Now);
    }

    [Fact]
    public void UnNumeroFuoriServizioProduceIlTonoRapido()
    {
        // E' il caso segnalato: 480 dal centralino, nessun audio dalla rete.
        Assert.Equal(EarconKind.LineUnavailable, AnnuncioDelRifiuto(CallEndReason.NoAnswer).Earcon);
    }

    [Fact]
    public void UnNumeroInesistenteProduceIlTonoRapido()
    {
        Assert.Equal(EarconKind.LineUnavailable, AnnuncioDelRifiuto(CallEndReason.NotFound).Earcon);
    }

    [Fact]
    public void UnaDestinazioneOccupataProduceIlTonoDiOccupato()
    {
        // La cadenza lenta dice «richiamo fra poco», la rapida «questo numero non
        // va»: sono due decisioni diverse, e l'orecchio le distingue prima delle
        // parole.
        Assert.Equal(EarconKind.LineBusy, AnnuncioDelRifiuto(CallEndReason.Busy).Earcon);
    }

    [Theory]
    [InlineData(CallEndReason.NotAuthorized)]
    [InlineData(CallEndReason.NetworkFailure)]
    [InlineData(CallEndReason.Rejected)]
    [InlineData(CallEndReason.Unknown)]
    public void QualunqueAltroEsitoNonLasciaLaCuffiaMuta(CallEndReason reason)
    {
        // La regola che conta non e' quale tono, ma che ce ne sia uno: nessun esito
        // deve finire in silenzio.
        var earcon = AnnuncioDelRifiuto(reason).Earcon;

        Assert.NotEqual(EarconKind.None, earcon);
    }

    [Fact]
    public void IlMotivoRestaDettoAParoleOltreAlTono()
    {
        // Il tono e' il primo riscontro, non l'unico: dice che qualcosa non e'
        // andato, la frase dice cosa.
        var annuncio = AnnuncioDelRifiuto(CallEndReason.NotFound);

        Assert.Equal("Numero inesistente", annuncio.Text);
    }

    [Theory]
    [InlineData(EarconKind.LineBusy)]
    [InlineData(EarconKind.LineUnavailable)]
    public void IToniEsconoDoveLOperatoreStaAscoltando(EarconKind kind)
    {
        // In cuffia, sulla via della conversazione: e' li' che si aspetta l'esito
        // della chiamata appena partita, non sugli altoparlanti della suoneria.
        Assert.Equal(AudioRoute.Conversation, EarconRouting.RouteFor(kind));
    }
}
