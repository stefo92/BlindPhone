using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica la decisione sui certificati del centralino.
/// </summary>
/// <remarks>
/// <para>
/// La regola da proteggere è che un certificato non verificabile non passi <b>mai</b>
/// senza che qualcuno abbia detto di sì. Il modo di sbagliarla è insidioso: si
/// manifesta come un collegamento che funziona, ed è indistinguibile da un
/// collegamento sicuro finché qualcuno non si mette in mezzo.
/// </para>
/// <para>
/// L'altra metà, altrettanto importante, è che la domanda non venga posta più del
/// necessario: il collegamento al centralino si ritenta da solo, e una finestra
/// modale ogni pochi secondi su una postazione usata senza vista è un guasto quanto
/// il collegamento che non parte.
/// </para>
/// </remarks>
public sealed class CertificateTrustTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 10, 0, 0, TimeSpan.FromHours(2));

    private static CertificateDescription Certificato(
        CertificateProblem problemi = CertificateProblem.UntrustedAuthority,
        string host = "pbx.ente.it",
        string impronta = "AABBCCDDEEFF00112233445566778899AABBCCDD") =>
        new(
            host,
            "al centralino",
            "pbx.ente.it",
            "Autorita' interna dell'ente",
            impronta,
            Now.AddYears(-1),
            Now.AddYears(1),
            problemi);

    private sealed class Store : ICertificateTrustStore
    {
        private readonly Dictionary<string, CertificateDescription> _voci = [];

        public IReadOnlyList<CertificateDescription> Trusted => [.. _voci.Values];

        public bool IsTrusted(string trustKey) => _voci.ContainsKey(trustKey);

        public void Trust(CertificateDescription certificate) => _voci[certificate.TrustKey] = certificate;

        public void Revoke(string trustKey) => _voci.Remove(trustKey);
    }

    private sealed class Prompt(CertificateTrustDecision decisione) : ICertificateTrustPrompt
    {
        public int Domande { get; private set; }

        public CertificateTrustDecision Ask(CertificateDescription certificate)
        {
            Domande++;
            return decisione;
        }
    }

    [Fact]
    public void UnCertificatoValidoPassaSenzaDisturbareLOperatore()
    {
        var prompt = new Prompt(CertificateTrustDecision.Reject);
        var servizio = new CertificateTrustService(new Store(), prompt, new ManualClock(Now));

        var esito = servizio.Evaluate(Certificato(CertificateProblem.None));

        Assert.True(esito.Accepted);
        Assert.Equal(0, prompt.Domande);
    }

    [Fact]
    public void SenzaOperatoreACuiChiedere_SiRifiuta()
    {
        // E' il caso dello strumento diagnostico a riga di comando. La risposta
        // prudente e' l'unica possibile: non c'e' nessuno che possa approvare.
        var servizio = new CertificateTrustService(new Store(), prompt: null, new ManualClock(Now));

        var esito = servizio.Evaluate(Certificato());

        Assert.False(esito.Accepted);
    }

    [Fact]
    public void ServerSenzaCertificato_SiRifiutaSenzaChiedere()
    {
        // Non c'e' niente di cui fidarsi e niente da mostrare: sottoporlo
        // all'operatore sarebbe una domanda senza contenuto.
        var prompt = new Prompt(CertificateTrustDecision.TrustAlways);
        var servizio = new CertificateTrustService(new Store(), prompt, new ManualClock(Now));

        var esito = servizio.Evaluate(Certificato(CertificateProblem.NotAvailable));

        Assert.False(esito.Accepted);
        Assert.Equal(0, prompt.Domande);
    }

    [Fact]
    public void FiduciaPermanente_VieneRicordataEnonSiRichiede()
    {
        var store = new Store();
        var prompt = new Prompt(CertificateTrustDecision.TrustAlways);
        var servizio = new CertificateTrustService(store, prompt, new ManualClock(Now));

        Assert.True(servizio.Evaluate(Certificato()).Accepted);
        Assert.Single(store.Trusted);

        // Un secondo servizio, come dopo un riavvio: la decisione vale ancora e la
        // domanda non torna.
        var dopoRiavvio = new Prompt(CertificateTrustDecision.Reject);
        var nuovo = new CertificateTrustService(store, dopoRiavvio, new ManualClock(Now));

        Assert.True(nuovo.Evaluate(Certificato()).Accepted);
        Assert.Equal(0, dopoRiavvio.Domande);
    }

    [Fact]
    public void FiduciaPerLaSolaSessione_NonSopravviveAlRiavvio()
    {
        var store = new Store();
        var prompt = new Prompt(CertificateTrustDecision.TrustForThisSession);
        var servizio = new CertificateTrustService(store, prompt, new ManualClock(Now));

        Assert.True(servizio.Evaluate(Certificato()).Accepted);

        // Nella stessa sessione non si richiede.
        Assert.True(servizio.Evaluate(Certificato()).Accepted);
        Assert.Equal(1, prompt.Domande);

        // Ma niente e' stato scritto: al riavvio la domanda torna.
        Assert.Empty(store.Trusted);
    }

    [Fact]
    public void UnRifiutoNonVieneRiproposto_AOgniTentativoDiRiconnessione()
    {
        // Il collegamento al centralino si ritenta da solo. Senza questa memoria
        // l'operatore si vedrebbe aprire una finestra modale ogni pochi secondi, su
        // una postazione che non guarda.
        var prompt = new Prompt(CertificateTrustDecision.Reject);
        var servizio = new CertificateTrustService(new Store(), prompt, new ManualClock(Now));

        for (var i = 0; i < 5; i++)
        {
            Assert.False(servizio.Evaluate(Certificato()).Accepted);
        }

        Assert.Equal(1, prompt.Domande);
    }

    [Fact]
    public void DimenticareLeDecisioniDiSessione_RiapreLaDomanda()
    {
        // Serve dopo aver sistemato il certificato sul centralino: senza, l'unico
        // modo di ripresentare la domanda sarebbe riavviare, cioe' far cadere le
        // chiamate in corso per rispondere a una domanda.
        var prompt = new Prompt(CertificateTrustDecision.Reject);
        var servizio = new CertificateTrustService(new Store(), prompt, new ManualClock(Now));

        servizio.Evaluate(Certificato());
        servizio.ForgetSessionDecisions();
        servizio.Evaluate(Certificato());

        Assert.Equal(2, prompt.Domande);
    }

    [Fact]
    public void LaFiduciaELegataAlServerEnonAllaSolaImpronta()
    {
        // Fidarsi di un certificato significa fidarsi di quel certificato SU QUEL
        // SERVER. Legandola alla sola impronta, un certificato ottenuto per una
        // macchina varrebbe ovunque ricompaia.
        var store = new Store();
        var prompt = new Prompt(CertificateTrustDecision.TrustAlways);
        var servizio = new CertificateTrustService(store, prompt, new ManualClock(Now));

        servizio.Evaluate(Certificato(host: "pbx.ente.it"));
        Assert.Equal(1, prompt.Domande);

        // Stessa impronta, server diverso: si chiede di nuovo.
        servizio.Evaluate(Certificato(host: "altro.ente.it"));
        Assert.Equal(2, prompt.Domande);
    }

    [Fact]
    public void UnCertificatoScadutoNonPassaPerValido()
    {
        var prompt = new Prompt(CertificateTrustDecision.Reject);
        var servizio = new CertificateTrustService(new Store(), prompt, new ManualClock(Now));

        var scaduto = Certificato(CertificateProblem.None) with { NotAfter = Now.AddDays(-1) };

        Assert.False(servizio.Evaluate(scaduto).Accepted);
        Assert.Equal(1, prompt.Domande);
    }

    [Fact]
    public void ConLaVerificaDisattivata_TuttoPassaSenzaDomande()
    {
        var prompt = new Prompt(CertificateTrustDecision.Reject);

        var servizio = new CertificateTrustService(new Store(), prompt, new ManualClock(Now))
        {
            AcceptAnyCertificate = true,
        };

        Assert.True(servizio.Evaluate(Certificato()).Accepted);
        Assert.Equal(0, prompt.Domande);
    }

    [Fact]
    public void UnErroreNellInterfacciaSiTraduceInRifiuto()
    {
        // Un errore nell'apertura della finestra non deve tradursi in un collegamento
        // accettato senza che nessuno l'abbia approvato.
        var servizio = new CertificateTrustService(new Store(), new PromptRotto(), new ManualClock(Now));

        Assert.False(servizio.Evaluate(Certificato()).Accepted);
    }

    private sealed class PromptRotto : ICertificateTrustPrompt
    {
        public CertificateTrustDecision Ask(CertificateDescription certificate)
            => throw new InvalidOperationException("interfaccia non disponibile");
    }

    // -------------------------------------------------------------------
    // Testi
    // -------------------------------------------------------------------

    [Fact]
    public void LImprontaSiLeggeAGruppiDiQuattro()
    {
        // E' l'unica verifica che un operatore puo' fare davvero: farsela dettare da
        // chi amministra il centralino e confrontarla. Quaranta caratteri di fila non
        // si confrontano con niente.
        var letta = CertificateTexts.Fingerprint("AABBCCDDEEFF0011");

        Assert.Equal("AABB CCDD EEFF 0011", letta);
    }

    [Fact]
    public void LImprontaTolleraISeparatoriEIlMinuscolo()
    {
        Assert.Equal("AABB CCDD", CertificateTexts.Fingerprint("aa:bb:cc:dd"));
    }

    [Fact]
    public void IlProblemaVieneDettoSenzaGergo()
    {
        var testo = CertificateTexts.Problem(Certificato(), Now);

        Assert.Contains("autorita'", testo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chain", testo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SslPolicyErrors", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void PiuProblemiInsiemeVengonoDettiTutti()
    {
        var testo = CertificateTexts.Problem(
            Certificato(CertificateProblem.UntrustedAuthority | CertificateProblem.NameMismatch),
            Now);

        Assert.Contains("autorita'", testo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nome diverso", testo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaScadenzaCompareFraIProblemi()
    {
        var scaduto = Certificato(CertificateProblem.None) with { NotAfter = Now.AddDays(-30) };
        var testo = CertificateTexts.Problem(scaduto, Now);

        Assert.Contains("scaduto", testo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaDescrizioneContieneImprontaEmittenteEValidita()
    {
        var testo = CertificateTexts.Describe(Certificato(), Now);

        Assert.Contains("Impronta", testo, StringComparison.Ordinal);
        Assert.Contains("Emesso da", testo, StringComparison.Ordinal);
        Assert.Contains("Valido dal", testo, StringComparison.Ordinal);
        Assert.Contains("AABB CCDD", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LeIstruzioniNominanoLeTreScelteInOrdineDiPrudenza()
    {
        var testo = CertificateTexts.Choices;

        var nonFidarti = testo.IndexOf("Non fidarti", StringComparison.OrdinalIgnoreCase);
        var soloAdesso = testo.IndexOf("solo per adesso", StringComparison.OrdinalIgnoreCase);
        var sempre = testo.IndexOf("Fidati sempre", StringComparison.OrdinalIgnoreCase);

        Assert.True(nonFidarti >= 0 && soloAdesso > nonFidarti && sempre > soloAdesso);
    }
}
