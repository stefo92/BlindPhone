using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Decide se accettare un certificato presentato dal centralino.
/// </summary>
/// <remarks>
/// <para>
/// È il punto che sostituisce la scelta secca fra «verifica tutto» e «accetta
/// qualunque cosa». La prima rende inutilizzabile il prodotto sui moltissimi
/// centralini che espongono un certificato autofirmato — che nella PA italiana sono
/// la norma e non l'eccezione. La seconda toglie alla cifratura la parte che la
/// rende utile: senza verificare chi c'è dall'altra parte, TLS protegge la
/// conversazione con chiunque si sia messo in mezzo.
/// </para>
/// <para>
/// La via di mezzo è la stessa di un browser: si mostra il certificato, si dice
/// esattamente cosa non torna, e si lascia decidere. La differenza è che qui la
/// decisione va <b>ascoltata</b>, quindi la descrizione è pensata per essere letta a
/// voce e non guardata.
/// </para>
/// <para>
/// <b>Ogni decisione è per un certificato su un server.</b> Non esiste «fidati di
/// questo centralino qualunque certificato presenti»: sarebbe la stessa cosa che
/// disattivare la verifica, con un nome più rassicurante.
/// </para>
/// </remarks>
public sealed class CertificateTrustService
{
    private readonly ICertificateTrustStore _store;
    private readonly ICertificateTrustPrompt? _prompt;
    private readonly IClock _clock;
    private readonly ILogger<CertificateTrustService> _log;

    private readonly Lock _gate = new();

    /// <summary>Decisioni valide per la sola sessione corrente.</summary>
    private readonly HashSet<string> _sessionTrust = [];

    /// <summary>
    /// Rifiuti già pronunciati, per non richiedere la stessa decisione a ogni tentativo.
    /// </summary>
    /// <remarks>
    /// Il collegamento al centralino si ritenta da solo, con la riconnessione
    /// automatica. Senza questa memoria, un certificato rifiutato una volta
    /// riproporrebbe la domanda a ogni tentativo — cioè una finestra modale ogni
    /// pochi secondi, su una postazione che l'operatore non vede.
    /// </remarks>
    private readonly HashSet<string> _sessionRejections = [];

    /// <summary>Crea il servizio.</summary>
    /// <param name="store">Deposito delle decisioni permanenti.</param>
    /// <param name="prompt">
    /// Come chiedere all'operatore. Se assente — motore diagnostico da riga di
    /// comando, test — un certificato non verificabile viene <b>rifiutato</b>: è la
    /// risposta prudente, e l'unica possibile quando non c'è nessuno a cui chiedere.
    /// </param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    public CertificateTrustService(
        ICertificateTrustStore store,
        ICertificateTrustPrompt? prompt = null,
        IClock? clock = null,
        ILogger<CertificateTrustService>? logger = null)
    {
        _store = store;
        _prompt = prompt;
        _clock = clock ?? SystemClock.Instance;
        _log = logger ?? NullLogger<CertificateTrustService>.Instance;
    }

    /// <summary>
    /// Se accettare qualunque certificato senza verificare né chiedere.
    /// </summary>
    /// <remarks>
    /// Resta per i laboratori e per la retrocompatibilità con le postazioni che
    /// l'avevano già attivata. Quando è acceso il fatto <b>viene registrato a ogni
    /// collegamento</b> e compare nel riepilogo diagnostico: una postazione che
    /// accetta qualunque certificato deve poterlo dimostrare a chi la verifica, e
    /// soprattutto non deve poterlo nascondere.
    /// </remarks>
    public bool AcceptAnyCertificate { get; set; }

    /// <summary>Numero di certificati accettati dopo una domanda all'operatore.</summary>
    public int AskedCount { get; private set; }

    /// <summary>Valuta un certificato e decide se il collegamento può proseguire.</summary>
    /// <param name="certificate">Certificato presentato dal server.</param>
    /// <returns>L'esito, con il motivo già in forma pronunciabile.</returns>
    public CertificateVerdict Evaluate(CertificateDescription certificate)
    {
        if (AcceptAnyCertificate)
        {
            // Deliberatamente a livello Warning e non Debug: e' una postazione che ha
            // rinunciato a verificare con chi sta parlando, e la riga deve saltare
            // all'occhio di chi legge il registro.
            _log.LogWarning(
                "Verifica dei certificati disattivata: accettato {Subject} da {Host} senza controlli",
                certificate.Subject, certificate.Host);

            return new CertificateVerdict(true, "Verifica dei certificati disattivata nelle impostazioni");
        }

        if (certificate.Problems == CertificateProblem.None
            && !certificate.IsOutsideValidity(_clock.Now))
        {
            return new CertificateVerdict(true, "Certificato verificato");
        }

        // Il server che non presenta nulla non e' un caso da sottoporre all'operatore:
        // non c'e' niente di cui fidarsi, e nessuna informazione da mostrargli.
        if (certificate.Problems.HasFlag(CertificateProblem.NotAvailable))
        {
            _log.LogError("Il server {Host} non ha presentato alcun certificato", certificate.Host);
            return new CertificateVerdict(false, "Il centralino non ha presentato alcun certificato");
        }

        var key = certificate.TrustKey;

        lock (_gate)
        {
            if (_sessionTrust.Contains(key))
            {
                return new CertificateVerdict(true, "Certificato accettato per questa sessione");
            }

            if (_sessionRejections.Contains(key))
            {
                // Gia' rifiutato: si risponde senza ripresentare la domanda.
                return new CertificateVerdict(false, "Certificato rifiutato in precedenza");
            }
        }

        if (_store.IsTrusted(key))
        {
            _log.LogInformation(
                "Certificato di {Host} accettato: impronta gia' approvata dall'operatore", certificate.Host);

            return new CertificateVerdict(true, "Certificato gia' approvato su questa postazione");
        }

        if (_prompt is null)
        {
            _log.LogWarning(
                "Certificato di {Host} non verificabile e nessuno a cui chiedere: collegamento rifiutato",
                certificate.Host);

            return new CertificateVerdict(false, CertificateTexts.RejectedWithoutOperator);
        }

        return AskOperator(certificate, key);
    }

    private CertificateVerdict AskOperator(CertificateDescription certificate, string key)
    {
        _log.LogWarning(
            "Certificato di {Host} non verificabile: {Problems}. Intestato a {Subject}, emesso da {Issuer}, "
            + "impronta {Thumbprint}",
            certificate.Host,
            certificate.Problems,
            certificate.Subject,
            certificate.Issuer,
            certificate.Thumbprint);

        CertificateTrustDecision decision;

        try
        {
            decision = _prompt!.Ask(certificate);
        }
        catch (Exception ex)
        {
            // Se la domanda non si riesce nemmeno a porre, la risposta e' no. Un
            // errore nell'interfaccia non deve tradursi in un collegamento accettato
            // senza che nessuno l'abbia approvato.
            _log.LogError(ex, "Richiesta di fiducia sul certificato non riuscita: si rifiuta");
            decision = CertificateTrustDecision.Reject;
        }

        AskedCount++;

        switch (decision)
        {
            case CertificateTrustDecision.TrustAlways:
                _store.Trust(certificate);

                lock (_gate) _sessionTrust.Add(key);

                _log.LogWarning(
                    "L'operatore ha approvato in modo permanente il certificato di {Host}, impronta {Thumbprint}",
                    certificate.Host, certificate.Thumbprint);

                return new CertificateVerdict(true, CertificateTexts.TrustedAlways, AskedOperator: true);

            case CertificateTrustDecision.TrustForThisSession:
                lock (_gate) _sessionTrust.Add(key);

                _log.LogWarning(
                    "L'operatore ha approvato il certificato di {Host} per la sola sessione corrente",
                    certificate.Host);

                return new CertificateVerdict(true, CertificateTexts.TrustedForSession, AskedOperator: true);

            default:
                lock (_gate) _sessionRejections.Add(key);

                _log.LogWarning("L'operatore ha rifiutato il certificato di {Host}", certificate.Host);

                return new CertificateVerdict(false, CertificateTexts.Rejected, AskedOperator: true);
        }
    }

    /// <summary>
    /// Dimentica i rifiuti di questa sessione, perché l'operatore possa riprovare
    /// dopo aver sistemato il certificato sul centralino.
    /// </summary>
    /// <remarks>
    /// Senza, l'unico modo per ripresentare la domanda sarebbe riavviare BlindPhone,
    /// cioè cadere dalle chiamate in corso per rispondere a una domanda.
    /// </remarks>
    public void ForgetSessionDecisions()
    {
        lock (_gate)
        {
            _sessionTrust.Clear();
            _sessionRejections.Clear();
        }

        _log.LogInformation("Decisioni sui certificati di questa sessione dimenticate");
    }
}
