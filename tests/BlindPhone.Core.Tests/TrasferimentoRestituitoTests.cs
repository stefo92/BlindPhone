using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// La chiamata che il centralino restituisce perché l'interno non ha preso il
/// trasferimento.
/// </summary>
/// <remarks>
/// Yeastar P-Series con <i>Transfer Bounce Back</i> la riporta all'operatore con il
/// nome «Rebound Transfer Call: {nome del chiamante}». Letta così com'è, la voce
/// italiana diceva «Chiamata da Rebound Transfer Call: Mario Rossi», con la notizia
/// in inglese e il nome per ultimo.
/// </remarks>
public sealed class TrasferimentoRestituitoTests
{
    // -----------------------------------------------------------------------
    // Riconoscimento del prefisso
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Rebound Transfer Call: Mario Rossi", "Mario Rossi")]
    [InlineData("Rebound Transfer Call:Mario Rossi", "Mario Rossi")]
    [InlineData("rebound transfer call: Mario Rossi", "Mario Rossi")]
    [InlineData("  Rebound  Transfer  Call -  Mario Rossi ", "Mario Rossi")]
    [InlineData("Rebound Transfer: Mario Rossi", "Mario Rossi")]
    [InlineData("Rebound Transfer Call Mario Rossi", "Mario Rossi")]
    [InlineData("Rebound Transfer Call: Chiamate esterne", "Chiamate esterne")]
    public void IlPrefissoSiToglieEResteIlNomeDelChiamante(string nome, string atteso)
    {
        Assert.True(TransferRebound.TryStrip(nome, out var chiamante));
        Assert.Equal(atteso, chiamante);
    }

    [Theory]
    [InlineData("Rebound Transfer Call:")]
    [InlineData("Rebound Transfer Call")]
    [InlineData("Rebound Transfer Call:   ")]
    public void SenzaNomeDopoIlPrefissoIlNomeLoCercaLaRubrica(string nome)
    {
        Assert.True(TransferRebound.TryStrip(nome, out var chiamante));
        Assert.Null(chiamante);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mario Rossi")]
    [InlineData("Chiamate esterne")]
    [InlineData("Ufficio Rebound Transfer Call")]
    [InlineData("Rebounder")]
    public void UnNomeQualsiasiNonEUnTrasferimentoRestituito(string? nome)
    {
        Assert.False(TransferRebound.TryStrip(nome, out var chiamante));
        Assert.Null(chiamante);
    }

    // -----------------------------------------------------------------------
    // La chiamata e il suo annuncio
    // -----------------------------------------------------------------------

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public RecordingAnnouncer Announcer { get; } = new();

        public CallSessionManager Calls { get; }

        public Harness()
        {
            Calls = new CallSessionManager(
                Engine, Announcer, new CallAnnouncementMapper(), new AnnouncementContext());
        }
    }

    [Fact]
    public async Task LaVoceDiceCheTornaDalTrasferimentoEPoiChiChiama()
    {
        var h = new Harness();

        h.Engine.SimulateIncoming("0211111111", "Rebound Transfer Call: Mario Rossi");
        await Task.Delay(20);

        var annuncio = h.Announcer.Announcements.First();
        Assert.StartsWith("Torna dal trasferimento non risposto: Mario Rossi", annuncio.Text);
        Assert.DoesNotContain("Rebound", annuncio.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AnnouncementPriority.Critical, annuncio.Priority);
        Assert.Equal(">RIT Mario Rossi", annuncio.BrailleText);
    }

    [Fact]
    public async Task LaChiamataPortaIlNomeDelChiamanteEIlSegnoDelRitorno()
    {
        // Il nome pulito e' quello che finisce a schermo, nell'elenco delle chiamate e
        // in ogni annuncio successivo: «in linea con Mario Rossi», non con il prefisso.
        var h = new Harness();

        h.Engine.SimulateIncoming("0211111111", "Rebound Transfer Call: Mario Rossi");
        await Task.Delay(20);

        var squilla = Assert.IsType<Call>(h.Calls.RingingCall);
        Assert.True(squilla.IsTransferRebound);
        Assert.Equal("Mario Rossi", squilla.RemoteParty.DisplayName);
        Assert.Equal("0211111111", squilla.RemoteParty.Number);
    }

    [Fact]
    public async Task SenzaNomeDopoIlPrefissoSiDiceIlNumero()
    {
        var h = new Harness();

        h.Engine.SimulateIncoming("4021", "Rebound Transfer Call:");
        await Task.Delay(20);

        var annuncio = h.Announcer.Announcements.First();
        Assert.StartsWith("Torna dal trasferimento non risposto:", annuncio.Text);
        Assert.Null(h.Calls.RingingCall?.RemoteParty.DisplayName);
    }

    [Fact]
    public async Task UnaChiamataQualsiasiSiAnnunciaComePrima()
    {
        var h = new Harness();

        h.Engine.SimulateIncoming("0211111111", "Mario Rossi");
        await Task.Delay(20);

        Assert.StartsWith("Chiamata da Mario Rossi", h.Announcer.Announcements.First().Text);
        Assert.False(h.Calls.RingingCall?.IsTransferRebound);
    }

    [Fact]
    public async Task RispondendoSiSenteIlNomeSenzaPrefisso()
    {
        var h = new Harness();
        h.Engine.SimulateIncoming("0211111111", "Rebound Transfer Call: Mario Rossi");
        await Task.Delay(20);
        h.Announcer.Clear();

        await h.Calls.AnswerAsync();

        Assert.Equal("In linea con Mario Rossi", h.Announcer.Announcements.Single().Text);
    }

    // -----------------------------------------------------------------------
    // La riga a schermo
    // -----------------------------------------------------------------------

    [Fact]
    public void LaRigaAScermoDiceLoStessoDellaVoce()
    {
        var party = new CallParty("0211111111", "Mario Rossi");

        Assert.Equal(
            "Torna dal trasferimento non risposto: Mario Rossi (0211111111)",
            CallBoardTexts.IncomingCall(party, null, transferRebound: true));

        Assert.Equal(
            "Chiamata da: Mario Rossi (0211111111)",
            CallBoardTexts.IncomingCall(party, null, transferRebound: false));
    }
}
