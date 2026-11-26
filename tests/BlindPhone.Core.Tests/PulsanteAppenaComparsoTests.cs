using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che un clic su Riaggancia o Rifiuta, appena comparsi al posto di un altro
/// pulsante, non chiuda la chiamata.
/// </summary>
/// <remarks>
/// Le barre sono quelle vere del catalogo, divise come le divide la finestra
/// principale: è il catalogo a mettere Riaggancia dove un istante prima c'era
/// Rispondi, e le prove devono accorgersene se un giorno smette di farlo.
/// </remarks>
public sealed class PulsanteAppenaComparsoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 15, 33, 50, TimeSpan.FromHours(1));

    private static DateTimeOffset Dopo(int millisecondi) => T0.AddMilliseconds(millisecondi);

    private static readonly int Assestamento = (int)CommandBarSwapGuard.SettleTime.TotalMilliseconds;

    private static readonly CallActionContext Riposo = new();
    private static readonly CallActionContext Squilla = new(HasRingingCall: true);
    private static readonly CallActionContext InLinea = new(HasActiveCall: true);

    /// <summary>La barra della chiamata, come la costruisce la finestra principale.</summary>
    private static IReadOnlyList<CallActionButton> Barra(CallActionContext contesto)
        => [.. CallActionCatalog.Available(contesto).Where(a => a.Group != CallActionGroup.Station)];

    /// <summary>Riposo, squillo e risposta: la sequenza di ogni chiamata entrante.</summary>
    private static CommandBarSwapGuard RispostaA(int millisecondi)
    {
        var guardia = new CommandBarSwapGuard();
        guardia.Shown(Barra(Riposo), T0);
        guardia.Shown(Barra(Squilla), Dopo(millisecondi - 2_000));
        guardia.Shown(Barra(InLinea), Dopo(millisecondi));
        return guardia;
    }

    [Fact]
    public void IlClicSuRiagganciaComparsoAlPostoDiRispondiVieneTrattenuto()
    {
        // Il caso del registro del 26 novembre: risposta, e un secondo clic nello
        // stesso punto novecento millisecondi dopo.
        var guardia = RispostaA(2_000);

        var trattenuto = guardia.Intercept(CallAction.HangUp, Dopo(2_900));

        Assert.NotNull(trattenuto);
        Assert.Equal("Riaggancia", trattenuto.Value.Label);
        Assert.Equal("Rispondi", trattenuto.Value.ReplacedLabel);
        Assert.Equal(TimeSpan.FromMilliseconds(900), trattenuto.Value.SinceArrival);
    }

    [Fact]
    public void IlSecondoClicChiudeComeSempre()
    {
        // Chi voleva davvero chiudere preme di nuovo, anche subito.
        var guardia = RispostaA(2_000);

        Assert.NotNull(guardia.Intercept(CallAction.HangUp, Dopo(2_900)));
        Assert.Null(guardia.Intercept(CallAction.HangUp, Dopo(3_100)));
    }

    [Fact]
    public void PassatoIlTempoDiAssestamentoIlPrimoClicChiude()
    {
        var guardia = RispostaA(2_000);

        Assert.Null(guardia.Intercept(CallAction.HangUp, Dopo(2_000 + Assestamento + 100)));
    }

    [Fact]
    public void UnaRicostruzioneCheLasciaRiagganciaAlSuoPostoNonRinnovaIlTempo()
    {
        // Silenziare il microfono rifà la barra, ma Riaggancia resta dov'era: il tempo
        // si conta da quando e' comparso.
        var guardia = RispostaA(2_000);
        guardia.Shown(Barra(InLinea with { IsMuted = true }), Dopo(3_500));

        Assert.Null(guardia.Intercept(CallAction.HangUp, Dopo(2_000 + Assestamento + 100)));
    }

    [Fact]
    public void GliAltriPulsantiCheCambianoPostoNonVengonoTrattenuti()
    {
        // "Metti in attesa" prende il posto di "Rifiuta", ma non fa cadere nessuno.
        var guardia = RispostaA(2_000);

        Assert.Null(guardia.Intercept(CallAction.Hold, Dopo(2_300)));
        Assert.Null(guardia.Intercept(CallAction.MuteOn, Dopo(2_300)));
    }

    [Fact]
    public void RiagganciaAlPostoDiComponiNonVieneTrattenuto()
    {
        // Chiamata in uscita dal campo di composizione: chi chiude subito ha visto il
        // numero sbagliato, e il clic deve chiudere al primo colpo.
        var guardia = new CommandBarSwapGuard();
        guardia.Shown(Barra(Riposo), T0);
        guardia.Shown(Barra(new CallActionContext(HasOutgoingCall: true)), Dopo(10_000));

        Assert.Null(guardia.Intercept(CallAction.HangUp, Dopo(10_800)));
    }

    [Fact]
    public void RifiutaComparsoAlPostoDellAttesaVieneTrattenuto()
    {
        // In linea con qualcuno, squilla una seconda chiamata proprio mentre si clicca
        // per mettere in attesa: al posto di "Metti in attesa" c'e' adesso "Rifiuta".
        var guardia = new CommandBarSwapGuard();
        guardia.Shown(Barra(InLinea), T0);
        guardia.Shown(Barra(InLinea with { HasRingingCall = true }), Dopo(60_000));

        var trattenuto = guardia.Intercept(CallAction.Reject, Dopo(60_300));

        Assert.NotNull(trattenuto);
        Assert.Equal("Rifiuta", trattenuto.Value.Label);
        Assert.Equal("Metti in attesa", trattenuto.Value.ReplacedLabel);
    }

    [Fact]
    public void RiagganciaRicompaiaAllaChiamataSuccessivaETrattenutoDaCapo()
    {
        // Il primo clic della chiamata precedente non ha consumato niente: la chiamata
        // e' finita, Riaggancia e' sparito, e alla risposta seguente vale di nuovo la
        // stessa protezione.
        var guardia = RispostaA(2_000);
        guardia.Shown(Barra(Riposo), Dopo(60_000));
        Assert.Null(guardia.Intercept(CallAction.HangUp, Dopo(60_100)));

        guardia.Shown(Barra(Squilla), Dopo(70_000));
        guardia.Shown(Barra(InLinea), Dopo(72_000));

        Assert.NotNull(guardia.Intercept(CallAction.HangUp, Dopo(72_700)));
    }
}
