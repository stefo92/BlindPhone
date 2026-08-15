using System.Windows;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Chiede all'operatore se fidarsi di un certificato che il sistema non ha
/// verificato.
/// </summary>
/// <remarks>
/// <para>
/// È la traduzione, per chi non vede lo schermo, della schermata che un browser
/// mostra davanti a un certificato non valido. Le differenze rispetto a quella sono
/// tre, e discendono tutte dal fatto che qui si ascolta invece di guardare.
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Il problema si dice per primo</b>, prima di chi ha emesso cosa: è
///   l'informazione che decide.
///   </description></item>
///   <item><description>
///   <b>L'impronta si legge a gruppi di quattro</b>, perché l'unico controllo vero
///   che un operatore può fare è farsela dettare da chi amministra il centralino e
///   confrontarla.
///   </description></item>
///   <item><description>
///   <b>Il tutto viene pronunciato all'apertura.</b> Una finestra che compare e tace
///   è un collegamento fermo su una domanda che nessuno sa di dover rispondere.
///   </description></item>
/// </list>
/// </remarks>
public partial class CertificateTrustWindow : Window
{
    private CertificateTrustDecision _decision = CertificateTrustDecision.Reject;

    /// <summary>Crea la finestra.</summary>
    /// <param name="certificate">Certificato su cui decidere.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="now">Istante corrente, per valutare la scadenza.</param>
    public CertificateTrustWindow(
        CertificateDescription certificate,
        IAnnouncer announcer,
        DateTimeOffset now)
    {
        InitializeComponent();

        var problema = CertificateTexts.Problem(certificate, now);

        TitleText.Text = $"Il centralino {certificate.Host} ha presentato un certificato non verificato";
        DetailText.Text = CertificateTexts.Describe(certificate, now);
        ChoicesText.Text = HelpTextLayout.Wrap(CertificateTexts.Choices);

        Loaded += (_, _) =>
        {
            // Il fuoco sul rifiuto, che e' anche il pulsante predefinito: chi preme
            // Invio per istinto ottiene la risposta prudente.
            RejectButton.Focus();

            announcer.Announce(Announcement.Critical(
                $"Attenzione, certificato non verificato. {problema}. "
                + $"Intestato a {certificate.Subject}, emesso da {certificate.Issuer}. "
                + $"Impronta {CertificateTexts.Fingerprint(certificate.Thumbprint)}. "
                + CertificateTexts.Choices,
                DateTimeOffset.Now,
                EarconKind.RegistrationLost));
        };
    }

    /// <summary>Decisione presa dall'operatore.</summary>
    public CertificateTrustDecision Decision => _decision;

    private void OnRejectClick(object sender, RoutedEventArgs e) => Finish(CertificateTrustDecision.Reject);

    private void OnSessionClick(object sender, RoutedEventArgs e)
        => Finish(CertificateTrustDecision.TrustForThisSession);

    private void OnAlwaysClick(object sender, RoutedEventArgs e) => Finish(CertificateTrustDecision.TrustAlways);

    private void Finish(CertificateTrustDecision decision)
    {
        _decision = decision;
        DialogResult = decision != CertificateTrustDecision.Reject;
        Close();
    }
}

/// <summary>
/// Porta la richiesta di fiducia sull'interfaccia, da qualunque thread arrivi.
/// </summary>
/// <remarks>
/// <para>
/// La domanda nasce sul thread che sta stabilendo il collegamento — quello di
/// SIPSorcery, o quello che apre il WebSocket verso il centralino — e le finestre
/// WPF vivono solo sul thread dell'interfaccia. Questo adattatore fa il passaggio e
/// riporta indietro la risposta, in modo che la logica di fiducia non debba sapere
/// nulla di dispatcher.
/// </para>
/// <para>
/// <b>Risponde sempre, anche quando non può chiedere.</b> Se l'interfaccia non c'è
/// ancora, se è in chiusura o se la finestra non si apre, la risposta è «non
/// fidarti»: bloccarsi in attesa terrebbe fermo il collegamento al centralino senza
/// che nulla lo dica, e accettare senza chiedere sarebbe la decisione che questa
/// classe esiste per evitare.
/// </para>
/// </remarks>
public sealed class DispatcherCertificateTrustPrompt : ICertificateTrustPrompt
{
    private readonly Func<IAnnouncer?> _announcer;
    private readonly IClock _clock;
    private readonly ILogger<DispatcherCertificateTrustPrompt> _log;

    /// <summary>Crea l'adattatore.</summary>
    /// <param name="announcer">
    /// Come raggiungere la pipeline di annuncio. È una funzione e non un riferimento
    /// perché il servizio di fiducia viene costruito prima della pipeline: risolverla
    /// al momento dell'uso evita una dipendenza circolare nel contenitore.
    /// </param>
    /// <param name="clock">Sorgente del tempo.</param>
    /// <param name="logger">Log diagnostico.</param>
    public DispatcherCertificateTrustPrompt(
        Func<IAnnouncer?> announcer,
        IClock clock,
        ILogger<DispatcherCertificateTrustPrompt> logger)
    {
        _announcer = announcer;
        _clock = clock;
        _log = logger;
    }

    /// <inheritdoc />
    public CertificateTrustDecision Ask(CertificateDescription certificate)
    {
        var application = Application.Current;

        if (application?.Dispatcher is null || application.Dispatcher.HasShutdownStarted)
        {
            _log.LogWarning(
                "Certificato di {Host} non verificabile e interfaccia non disponibile: si rifiuta",
                certificate.Host);

            return CertificateTrustDecision.Reject;
        }

        try
        {
            return application.Dispatcher.Invoke(() =>
            {
                var announcer = _announcer();
                if (announcer is null) return CertificateTrustDecision.Reject;

                var window = new CertificateTrustWindow(certificate, announcer, _clock.Now);

                // Owner solo se c'e' una finestra principale gia' visibile: senza
                // questo controllo l'apertura fallisce durante l'avvio, che e'
                // proprio quando il primo collegamento al centralino avviene.
                if (application.MainWindow is { IsLoaded: true, IsVisible: true } main)
                {
                    window.Owner = main;
                }

                window.ShowDialog();
                return window.Decision;
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della richiesta di fiducia non riuscita: si rifiuta");
            return CertificateTrustDecision.Reject;
        }
    }
}
