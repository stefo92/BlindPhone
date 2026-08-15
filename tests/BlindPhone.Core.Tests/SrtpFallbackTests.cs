using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che una chiamata senza la cifratura attesa non prosegua mai muta.
/// </summary>
/// <remarks>
/// <para>
/// È la prova nata da un guasto reale. Con la cifratura «preferita» e una linea
/// esterna, il centralino accettava l'INVITE e rispondeva 200 OK negoziando l'audio
/// <b>senza</b> crypto: nessun rifiuto da intercettare, chiamata risultante attiva, e
/// lo stack che scartava i pacchetti nelle due direzioni. Nel registro: 1.105
/// pacchetti consegnati, zero ricevuti, cento per cento di silenzio inventato.
/// </para>
/// <para>
/// Il difetto stava tutto nell'avere previsto il solo rifiuto — il caso raro — e non
/// l'accettazione senza cifratura, che è quello comune. Ed è il guasto peggiore
/// possibile su un posto operatore, perché <b>sembra</b> una conversazione riuscita:
/// due persone parlano nel vuoto senza che nulla lo segnali.
/// </para>
/// </remarks>
public sealed class SrtpFallbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 15, 17, 0, TimeSpan.FromHours(2));

    [Fact]
    public void LaChiusuraPerCifraturaMancanteHaUnMotivoTuttoSuo()
    {
        // Distinta da "chiamata non consentita": li' manca un permesso sul numero, qui
        // manca il supporto alla cifratura sulla linea, e il rimedio sta altrove.
        Assert.NotEqual(CallEndReason.NotAuthorized, CallEndReason.EncryptionUnavailable);
    }

    [Fact]
    public void LAnnuncioDiChiusuraDiceCosaFareEDove()
    {
        var testo = AnnouncementTexts.CallEnded(
            new CallParty("3498234041"), CallEndReason.EncryptionUnavailable, TimeSpan.Zero);

        Assert.Contains("voce cifrata", testo, StringComparison.Ordinal);
        Assert.Contains("Impostazioni", testo, StringComparison.Ordinal);
        Assert.Contains("Account SIP", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LAnnuncioDiChiusuraNonSiConfondeConUnRifiutoDelChiamato()
    {
        // "Non consentita" o "ha rifiutato" manderebbero l'operatore a cercare il
        // problema sul numero chiamato invece che nelle proprie impostazioni.
        var testo = AnnouncementTexts.CallEnded(
            new CallParty("3498234041"), CallEndReason.EncryptionUnavailable, TimeSpan.Zero);

        Assert.DoesNotContain("rifiutata", testo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("occupato", testo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IlRipiegoInChiaroVieneAnnunciatoAllOperatore()
    {
        // Il motore solleva l'evento quando richiama in chiaro: la pipeline deve
        // trasformarlo in un annuncio, altrimenti la protezione si spegne in silenzio.
        var engine = new FakeEngine();
        var announcer = new RecordingAnnouncer();

        var calls = new CallSessionManager(
            engine, announcer, new CallAnnouncementMapper(), new AnnouncementContext());

        Assert.NotNull(calls);

        engine.SimulateSrtpDowngrade("3498234041");
        await Task.Delay(20);

        Assert.True(announcer.Said("in chiaro"));
        Assert.True(announcer.Said("Attenzione"));
    }

    [Fact]
    public async Task IlRipiegoArrivaConPrioritaAltaPerSopravvivereAlSilenzio()
    {
        // Durante una conversazione gli annunci normali vengono soppressi. Questo no:
        // e' l'ultimo istante in cui l'operatore puo' decidere di non dire al telefono
        // quello che stava per dire.
        var engine = new FakeEngine();
        var announcer = new RecordingAnnouncer();

        var calls = new CallSessionManager(
            engine, announcer, new CallAnnouncementMapper(), new AnnouncementContext());

        Assert.NotNull(calls);

        engine.SimulateSrtpDowngrade("3498234041");
        await Task.Delay(20);

        var annuncio = announcer.Announcements.Single();
        Assert.True(annuncio.Priority >= AnnouncementPriority.High);
    }

    [Fact]
    public void IlNumeroDelRipiegoSiLeggeCifraPerCifra()
    {
        // Serve a richiamare: pronunciato come intero non si puo' ricomporre.
        var testo = AnnouncementTexts.SrtpDowngraded(SpeechFormatter.Digits("349"));

        Assert.Contains("tre, quattro, nove", testo, StringComparison.Ordinal);
    }
}
