using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// La domanda sulle statistiche d'uso del primo avvio: quando si fa, e che cosa dice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Quando si fa è una regola di quattro condizioni</b>, e le prove le girano tutte e
/// sedici: una volta sola, a statistiche spente, con un server valido, e solo se l'ente
/// non ha bloccato la scheda Diagnostica. Basta che ne manchi una e la domanda non
/// compare.
/// </para>
/// <para>
/// <b>Che cosa dice deve essere vero per costruzione.</b> L'elenco di che cosa parte lo
/// prende dal catalogo, come la guida: un evento aggiunto senza comparire nella domanda
/// partirebbe senza che nessuno ci abbia detto sì. E la domanda nomina l'autore solo
/// quando il server è il suo.
/// </para>
/// </remarks>
public sealed class DomandaSulleStatisticheTests
{
    private static string Piatto(string testo) => string.Join(' ', testo.Split('\n').Select(r => r.Trim()));

    // -----------------------------------------------------------------------
    // Quando si fa
    // -----------------------------------------------------------------------

    [Fact]
    public void SiChiedeSoloUnaVoltaASpentoConUnServerESenzaBlocco()
    {
        foreach (var giaChiesta in new[] { false, true })
        foreach (var accese in new[] { false, true })
        foreach (var server in new[] { false, true })
        foreach (var modificabile in new[] { false, true })
        {
            var atteso = !giaChiesta && !accese && server && modificabile;

            Assert.True(
                atteso == TelemetryConsentPrompt.ShouldAsk(giaChiesta, accese, server, modificabile),
                $"già chiesta {giaChiesta}, accese {accese}, server {server}, Diagnostica modificabile {modificabile}: "
                + (atteso ? "la domanda andava fatta" : "la domanda non andava fatta"));
        }
    }

    [Fact]
    public void UnaPostazioneNuovaConIlServerSenteLaDomanda()
        => Assert.True(TelemetryConsentPrompt.ShouldAsk(alreadyAsked: false, enabled: false, configured: true, diagnosticsEditable: true));

    [Fact]
    public void SeLEnteHaBloccatoLaDiagnosticaNonSiChiede()
        => Assert.False(TelemetryConsentPrompt.ShouldAsk(alreadyAsked: false, enabled: false, configured: true, diagnosticsEditable: false));

    [Fact]
    public void ChiHaGiaRispostoNonLaSenteDiNuovo()
        => Assert.False(TelemetryConsentPrompt.ShouldAsk(alreadyAsked: true, enabled: false, configured: true, diagnosticsEditable: true));

    // -----------------------------------------------------------------------
    // Che cosa dice
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaDomandaElencaTuttoQuelloCheParte(bool server)
    {
        var testo = Piatto(TelemetryConsentPrompt.Text(TelemetryConsentPrompt.AuthorServerHost, server));

        foreach (var evento in TelemetryCatalog.All)
        {
            Assert.True(
                testo.Contains(evento.Description, StringComparison.Ordinal),
                $"La domanda non dice quando parte l'evento {evento.Name}");
        }
    }

    [Theory]
    [InlineData("Non partono mai numeri di telefono", "non e' detto che cosa non parte mai")]
    [InlineData("Alt S risponde sì", "non e' detto come si risponde sì")]
    [InlineData("Alt N risponde no", "non e' detto come si risponde no")]
    [InlineData("Esc vale come no", "non e' detto che chiudere con Esc non e' un consenso")]
    [InlineData("Impostazioni, scheda Diagnostica", "non e' detto dove si cambia idea")]
    [InlineData("Alt T", "non e' detto il tasto della casella nella scheda Diagnostica")]
    [InlineData("non verrà ripetuta", "non e' detto che la domanda non torna")]
    [InlineData("partono solo se rispondi sì", "non e' detto che adesso sono spente")]
    public void LaDomandaDiceLeCoseCheServonoPerScegliere(string frammento, string perche)
    {
        Assert.True(
            Piatto(TelemetryConsentPrompt.Text(TelemetryConsentPrompt.AuthorServerHost, authorServer: true))
                .Contains(frammento, StringComparison.Ordinal),
            perche);
    }

    [Fact]
    public void ConIlServerDellAutoreLaDomandaDiceChiLeRiceveEDovELInformativa()
    {
        var testo = Piatto(TelemetryConsentPrompt.Text(TelemetryConsentPrompt.AuthorServerHost, authorServer: true));

        Assert.Contains(TelemetryConsentPrompt.AuthorServerHost, testo, StringComparison.Ordinal);
        Assert.Contains(HelpBook.AuthorName, testo, StringComparison.Ordinal);
        Assert.Contains("PostHog", testo, StringComparison.Ordinal);
        Assert.Contains("«Le statistiche d'uso anonime»", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void ConUnAltroServerLaDomandaNonNominaLAutore()
    {
        // Una copia compilata o configurata con il server dell'ente non deve dire
        // all'operatore che le statistiche vanno all'autore: direbbe il falso proprio
        // sulla cosa a cui sta dando il consenso.
        var testo = Piatto(TelemetryConsentPrompt.Text("statistiche.example.it", authorServer: false));

        Assert.Contains("statistiche.example.it", testo, StringComparison.Ordinal);
        Assert.DoesNotContain(HelpBook.AuthorName, testo, StringComparison.Ordinal);
        Assert.DoesNotContain(TelemetryConsentPrompt.AuthorServerHost, testo, StringComparison.Ordinal);
        Assert.Contains("informativa", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void AllAperturaSiSentonoTitoloDomandaETesto()
    {
        var detto = TelemetryConsentPrompt.Spoken(TelemetryConsentPrompt.AuthorServerHost, authorServer: true);

        Assert.StartsWith(TelemetryConsentPrompt.Title, detto, StringComparison.Ordinal);
        Assert.Contains(TelemetryConsentPrompt.Question, detto, StringComparison.Ordinal);
        Assert.Contains(
            TelemetryConsentPrompt.Text(TelemetryConsentPrompt.AuthorServerHost, authorServer: true),
            detto,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DopoLaRispostaSiDiceCheCosaSiESceltoEDoveSiCambia()
    {
        var si = TelemetryConsentPrompt.Confirmation(accepted: true);
        var no = TelemetryConsentPrompt.Confirmation(accepted: false);

        Assert.Contains("accese", si, StringComparison.Ordinal);
        Assert.Contains("spente", no, StringComparison.Ordinal);

        foreach (var conferma in new[] { si, no })
        {
            Assert.Contains("scheda Diagnostica", conferma, StringComparison.Ordinal);
            Assert.Contains("Alt T", conferma, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LaGuidaSpiegaLaDomandaEFaDaInformativa()
    {
        // La domanda rimanda alla guida per l'informativa: se la guida non la contiene,
        // il rimando porta nel vuoto.
        var argomento = Piatto(HelpBook.Build(HotkeyProfile.CreateDefault(), DateTimeOffset.Now)
            .Single(t => t.Id == HelpBook.UsageStatisticsTopicId).Text);

        Assert.Contains("La domanda del primo avvio", argomento, StringComparison.Ordinal);
        Assert.Contains("Informativa, per il server dell'autore", argomento, StringComparison.Ordinal);
        Assert.Contains(TelemetryConsentPrompt.AuthorServerHost, argomento, StringComparison.Ordinal);
        Assert.Contains(HelpBook.AuthorEmail, argomento, StringComparison.Ordinal);
        Assert.Contains("Garante per la protezione dei dati personali", argomento, StringComparison.Ordinal);
    }
}
