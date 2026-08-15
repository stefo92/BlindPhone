using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sull'invio dei toni ai menu automatici.
/// </summary>
/// <remarks>
/// <para>
/// I toni RFC 4733 viaggiano fuori dall'audio della conversazione: chi li invia non li
/// sente. Il solo riscontro che l'operatore ha è quello che gli dice il programma, e
/// su un menu automatico una cifra sbagliata porta in un ramo da cui spesso non si
/// torna indietro.
/// </para>
/// <para>
/// Per questo qui si verifica non solo che il tono parta, ma che parta <b>solo</b>
/// quando c'è una conversazione, e che ogni esito venga detto.
/// </para>
/// </remarks>
public sealed class DtmfTests
{
    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public CallSessionManager Calls { get; }

        public Harness()
            => Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), new AnnouncementContext());

        /// <summary>Porta il gestore ad avere una conversazione in corso.</summary>
        public async Task<string> ConversazioneInCorsoAsync()
        {
            var id = Engine.SimulateIncoming("0212345678", "Mario Rossi");
            await Task.Delay(20);

            await Calls.AnswerAsync();
            await Task.Delay(20);

            return id;
        }

        public IEnumerable<string> Toni => Engine.Operations.Where(o => o.StartsWith("dtmf:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IlTonoParteSoloDuranteUnaConversazione()
    {
        var h = new Harness();

        var esito = await h.Calls.SendDtmfAsync('5');

        Assert.False(esito);
        Assert.Empty(h.Toni);

        // Un comando muto sarebbe indistinguibile da un tono partito.
        Assert.Contains(h.Announcer.Announcements, a =>
            a.Text.Contains("Nessuna chiamata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OgniTonoInviatoVieneDetto()
    {
        var h = new Harness();
        await h.ConversazioneInCorsoAsync();

        Assert.True(await h.Calls.SendDtmfAsync('7'));

        Assert.Contains(h.Toni, t => t.EndsWith(":7", StringComparison.Ordinal));
        Assert.Contains(h.Announcer.Announcements, a => a.Text == "7");
    }

    [Theory]
    [InlineData('*', "asterisco")]
    [InlineData('#', "cancelletto")]
    public async Task AsteriscoECancellettoSiDiconoPerNome(char tono, string atteso)
    {
        var h = new Harness();
        await h.ConversazioneInCorsoAsync();

        Assert.True(await h.Calls.SendDtmfAsync(tono));

        // Pronunciati come simboli, la sintesi li rende in modi diversi fra una voce e
        // l'altra: e sono proprio i due tasti con cui i menu confermano e annullano.
        Assert.Contains(h.Announcer.Announcements, a =>
            a.Text.Contains(atteso, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnCarattereCheNonEUnTonoVieneRifiutato()
    {
        var h = new Harness();
        await h.ConversazioneInCorsoAsync();

        Assert.False(await h.Calls.SendDtmfAsync('x'));
        Assert.Empty(h.Toni);
    }

    [Fact]
    public async Task LaSequenzaParteTuttaENellOrdineScritto()
    {
        var h = new Harness();
        await h.ConversazioneInCorsoAsync();

        var inviati = await h.Calls.SendDtmfSequenceAsync("12#");

        Assert.Equal(3, inviati);
        Assert.Equal(3, h.Toni.Count());
        Assert.EndsWith(":1", h.Toni.ElementAt(0), StringComparison.Ordinal);
        Assert.EndsWith(":2", h.Toni.ElementAt(1), StringComparison.Ordinal);
        Assert.EndsWith(":#", h.Toni.ElementAt(2), StringComparison.Ordinal);

        Assert.Contains(h.Announcer.Announcements, a =>
            a.Text.Contains("Inviati 3 toni", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NellaSequenzaICaratteriNonValidiVengonoIgnorati()
    {
        var h = new Harness();
        await h.ConversazioneInCorsoAsync();

        // Un numero copiato da una mail puo' contenere spazi, trattini o parentesi:
        // rifiutare tutto costringerebbe a ribatterlo a mano.
        var inviati = await h.Calls.SendDtmfSequenceAsync("1-2 (3)");

        Assert.Equal(3, inviati);
        Assert.Equal(3, h.Toni.Count());
    }

    [Fact]
    public async Task UnaSequenzaVuotaNonDisturba()
    {
        var h = new Harness();
        await h.ConversazioneInCorsoAsync();

        Assert.Equal(0, await h.Calls.SendDtmfSequenceAsync("   "));
        Assert.Empty(h.Toni);

        Assert.DoesNotContain(h.Announcer.Announcements, a =>
            a.Text.Contains("Inviati", StringComparison.OrdinalIgnoreCase));
    }
}
