using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;

namespace BlindPhone.App;

/// <summary>
/// La domanda sulle statistiche d'uso, al primo avvio.
/// </summary>
/// <remarks>
/// <para>
/// Quando compare lo decide <see cref="TelemetryConsentPrompt.ShouldAsk"/>, e che cosa
/// dice lo compone <see cref="TelemetryConsentPrompt.Text"/>, dal catalogo degli
/// eventi. Qui c'è solo la finestra: come si legge, come si risponde.
/// </para>
/// <para>
/// <b>Il testo viene pronunciato all'apertura</b>, con la stessa strada dell'avviso di
/// versione: l'annuncio, e la notifica UI Automation sulla casella che porta il testo.
/// A questo punto dell'avvio la finestra principale non esiste ancora, ed era lei sola
/// a sollevare quella notifica: vedi <see cref="BetaNoticeWindow"/>, dove il difetto è
/// raccontato per intero.
/// </para>
/// <para>
/// <b>Il fuoco sta nel testo, non su un pulsante.</b> Così né sì né no sono
/// preselezionati, e il testo si rilegge subito con le frecce. Si risponde con Alt S o
/// Alt N, oppure con Tab fino al pulsante e Invio o barra spaziatrice.
/// </para>
/// </remarks>
public partial class TelemetryConsentWindow : Window
{
    private readonly AnnouncementService _announcements;

    /// <summary>Crea la finestra.</summary>
    /// <param name="announcements">
    /// Servizio di annuncio, intero: da qui si annuncia, e si ascolta ciò che viene
    /// pronunciato per portarlo a UI Automation.
    /// </param>
    /// <param name="serverHost">Nome del server a cui andrebbero le statistiche.</param>
    /// <param name="authorServer">Se è il server dell'autore di BlindPhone.</param>
    public TelemetryConsentWindow(AnnouncementService announcements, string serverHost, bool authorServer)
    {
        InitializeComponent();

        _announcements = announcements;

        QuestionText.Text = TelemetryConsentPrompt.Question;
        ExplanationText.Text = HelpTextLayout.Wrap(TelemetryConsentPrompt.Text(serverHost, authorServer));

        Loaded += (_, _) =>
        {
            // Il fuoco nel testo, con il cursore all'inizio: nessun pulsante scelto in
            // anticipo, e la prima freccia giu' legge la prima riga.
            ExplanationText.CaretIndex = 0;
            ExplanationText.Focus();

            // La sottoscrizione precede l'annuncio, come nell'avviso di versione:
            // altrimenti il primo - cioe' proprio questo - verrebbe emesso quando
            // nessuno e' ancora in ascolto.
            _announcements.Spoken += OnAnnouncementSpoken;

            _announcements.Announce(Announcement.Critical(
                TelemetryConsentPrompt.Spoken(serverHost, authorServer),
                DateTimeOffset.Now));
        };

        Closed += (_, _) => _announcements.Spoken -= OnAnnouncementSpoken;
    }

    /// <summary>
    /// La risposta: <c>true</c> per sì, <c>false</c> per no, <c>null</c> se la finestra
    /// è stata chiusa senza rispondere.
    /// </summary>
    /// <remarks>
    /// Chi la apre tratta <c>null</c> come un no: chiudere con Alt F4 o con la crocetta
    /// non è un consenso, esattamente come Esc.
    /// </remarks>
    public bool? Accepted { get; private set; }

    /// <summary>Porta l'annuncio a UI Automation, sulla casella che contiene il testo.</summary>
    private void OnAnnouncementSpoken(object? sender, Announcement announcement)
        => Dispatcher.Invoke(() =>
        {
            var peer = UIElementAutomationPeer.FromElement(ExplanationText)
                       ?? UIElementAutomationPeer.CreatePeerForElement(ExplanationText);

            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.ImportantAll,
                announcement.Text,
                Guid.NewGuid().ToString());
        });

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
        Close();
    }

    private void OnNoClick(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
        Close();
    }
}
