using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sull'elenco delle ultime chiamate.
/// </summary>
/// <remarks>
/// <para>
/// La classificazione è la parte che conta: una chiamata finita nell'elenco sbagliato
/// è una chiamata persa che nessuno richiama. Per chi non vede non esiste l'icona
/// lampeggiante che la ricorda, quindi l'elenco è l'unica traccia che resta.
/// </para>
/// <para>
/// L'altra parte verificata è la conservazione: è una decisione sui dati personali, e
/// un errore di arrotondamento cancellerebbe chiamate che l'operatore si aspetta di
/// trovare.
/// </para>
/// </remarks>
public sealed class CallHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 9, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void UnaChiamataEntranteRispostaFiniscsFraLeRicevute()
    {
        var call = Entrante();
        call.TryApply(CallTrigger.Answer, Now.AddSeconds(4), out _);

        Assert.Equal(CallHistoryKind.Incoming, CallHistoryService.Classify(call));
    }

    [Fact]
    public void UnaChiamataEntranteMaiRispostaEPersa()
    {
        // Non importa perche' sia finita — occupato, rifiutata, il chiamante ha
        // riagganciato: chi deve richiamare vuole sapere che quella persona ha provato.
        Assert.Equal(CallHistoryKind.Missed, CallHistoryService.Classify(Entrante()));
    }

    [Fact]
    public void UnaChiamataUscitaRestaFraLeEffettuateAncheSenzaRisposta()
    {
        // "Perse" sono quelle che l'operatore ha subito, non quelle che ha fatto lui.
        var call = new Call("out-1", CallDirection.Outbound, new CallParty("4021"), Now);

        Assert.Equal(CallHistoryKind.Outgoing, CallHistoryService.Classify(call));
    }

    [Fact]
    public void LaDurataSiMisuraDallaRispostaENonDalloSquillo()
    {
        var call = Entrante();
        call.TryApply(CallTrigger.Answer, Now.AddSeconds(10), out _);
        call.TryApply(CallTrigger.HangUp, Now.AddSeconds(70), out _);

        // Dieci secondi di squillo non sono conversazione: sommarli gonfierebbe ogni
        // durata dell'elenco.
        Assert.Equal(TimeSpan.FromSeconds(60), CallHistoryService.DurationOf(call));
    }

    [Fact]
    public void UnaChiamataPersaHaDurataZero()
        => Assert.Equal(TimeSpan.Zero, CallHistoryService.DurationOf(Entrante()));

    [Theory]
    [InlineData(1, "1 giorno")]
    [InlineData(7, "1 settimana")]
    [InlineData(30, "1 mese")]
    [InlineData(0, "mai: conserva tutto")]
    public void OgniSceltaHaUnaDescrizioneComprensibile(int giorni, string atteso)
        => Assert.Equal(atteso, CallHistoryRetention.Describe(giorni));

    [Fact]
    public void MaiSignificaNonCancellareNulla()
        => Assert.Equal(TimeSpan.Zero, CallHistoryRetention.ToTimeSpan(CallHistoryRetention.Forever));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(30, 30)]
    [InlineData(0, 0)]
    public void LeScelteValideRestanoQuelleChesono(int giorni, int atteso)
        => Assert.Equal(atteso, CallHistoryRetention.Normalize(giorni));

    [Theory]
    [InlineData(3, 7)]
    [InlineData(14, 30)]
    [InlineData(90, CallHistoryRetention.Forever)]
    public void UnValoreFuoriElencoNonAccorciaLaConservazione(int giorni, int atteso)
    {
        // Arrotondare per difetto cancellerebbe chiamate che l'operatore si aspettava
        // di trovare ancora: si sceglie sempre la scelta piu' vicina non piu' corta.
        Assert.Equal(atteso, CallHistoryRetention.Normalize(giorni));
    }

    [Fact]
    public void IlNomeMancanteVieneSostituitoDalNumero()
    {
        var voce = new CallHistoryEntry(
            1, CallHistoryKind.Missed, "0212345678", null, Now, TimeSpan.Zero, null, CallEndReason.NoAnswer);

        // In un elenco letto a voce, una riga senza nome ne' numero non si distingue
        // dalle altre.
        Assert.Equal("0212345678", voce.EffectiveName);
        Assert.False(voce.WasAnswered);
    }

    /// <summary>
    /// Chiamata entrante che sta squillando.
    /// </summary>
    /// <remarks>
    /// Va portata fino ad Alerting con il suo trigger: una chiamata appena costruita è
    /// in Idle, e da lì la risposta non è una transizione valida. Saltare il passaggio
    /// produrrebbe una chiamata che il codice considera mai risposta, cioè una prova
    /// che verifica il contrario di quello che dichiara.
    /// </remarks>
    private static Call Entrante()
    {
        var call = new Call("in-1", CallDirection.Inbound, new CallParty("0212345678", "Mario Rossi"), Now);
        call.TryApply(CallTrigger.IncomingCall, Now, out _);

        return call;
    }
}
