using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica l'annuncio di qualità della linea.
/// </summary>
/// <remarks>
/// La parte sulla cifratura è l'unica informazione di sicurezza che il posto operatore
/// pronuncia, e l'operatore non ha modo di verificarla altrove: se dice la cosa
/// sbagliata, nessuno se ne accorge finché non se ne accorge qualcun altro.
/// </remarks>
public sealed class LineQualityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.FromHours(2));

    private static Call Chiamata(string? codec, bool wideband, bool mediaCifrato)
    {
        var call = new Call("q-1", CallDirection.Inbound, new CallParty("4021", "Mario Rossi"), Now);

        call.TryApply(CallTrigger.IncomingCall, Now, out _);
        call.TryApply(CallTrigger.Answer, Now, out _);
        call.SetMediaProfile(codec, wideband, mediaCifrato);

        return call;
    }

    [Fact]
    public void SegnalazioneCifrataEVoceInChiaroSiDiconoSeparatamente()
    {
        // E' la configurazione piu' comune: trasporto TLS e RTP in chiaro. Prima la
        // frase finiva con "chiamata non protetta", e chi aveva configurato il TLS
        // concludeva che non stesse funzionando, andando a cercare un guasto assente.
        var testo = AnnouncementTexts.LineQuality(Chiamata("G722", true, false), signallingEncrypted: true);

        Assert.Equal("Audio in banda larga, codec G722. voce in chiaro, segnalazione cifrata TLS", testo);
    }

    [Fact]
    public void SenzaTlsLaSegnalazioneRisultaInChiaro()
    {
        var testo = AnnouncementTexts.LineQuality(Chiamata("PCMA", false, false), signallingEncrypted: false);

        Assert.Contains("segnalazione in chiaro", testo, StringComparison.Ordinal);
        Assert.Contains("voce in chiaro", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void ConSrtpAttivoLaVoceRisultaCifrata()
    {
        // Il giorno in cui SRTP verra' attivato l'annuncio deve seguirlo da solo:
        // e' la ragione per cui il valore va misurato e non dichiarato.
        var testo = AnnouncementTexts.LineQuality(Chiamata("G722", true, true), signallingEncrypted: true);

        Assert.Contains("voce cifrata", testo, StringComparison.Ordinal);
        Assert.Contains("segnalazione cifrata TLS", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LeQuattroCombinazioniProduconoQuattroFrasiDiverse()
    {
        // Nessuno dei due strati puo' essere assorbito dall'altro: se due combinazioni
        // producessero la stessa frase, una delle due sarebbe una bugia.
        var frasi = new[]
        {
            AnnouncementTexts.LineQuality(Chiamata("G722", true, false), false),
            AnnouncementTexts.LineQuality(Chiamata("G722", true, false), true),
            AnnouncementTexts.LineQuality(Chiamata("G722", true, true), false),
            AnnouncementTexts.LineQuality(Chiamata("G722", true, true), true),
        };

        Assert.Equal(4, frasi.Distinct().Count());
    }

    [Fact]
    public void LaFraseNonDiceMaiCheLaChiamataEProtettaSenzaDireInCheSenso()
    {
        // "Chiamata protetta" era l'affermazione ambigua da cui e' nato l'equivoco:
        // parlava di tutta la telefonata mentre il dato riguardava il solo audio.
        foreach (var segnalazione in new[] { true, false })
        {
            foreach (var voce in new[] { true, false })
            {
                var testo = AnnouncementTexts.LineQuality(Chiamata("G722", true, voce), segnalazione);

                Assert.DoesNotContain("chiamata protetta", testo, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("chiamata non protetta", testo, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void SenzaCodecNegoziatoLaFraseRestaSensata()
    {
        var testo = AnnouncementTexts.LineQuality(Chiamata(null, false, false), signallingEncrypted: true);

        Assert.Equal("Audio in banda stretta. voce in chiaro, segnalazione cifrata TLS", testo);
    }
}
