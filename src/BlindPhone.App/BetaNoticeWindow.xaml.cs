using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;

namespace BlindPhone.App;

/// <summary>
/// Avviso di versione beta, mostrato a ogni avvio prima della finestra principale.
/// </summary>
/// <remarks>
/// <para>
/// Dichiara due cose che l'operatore ha diritto di sapere prima di mettersi a
/// rispondere al telefono con questo programma: che è una versione in prova, e che
/// chi lo ha scritto non risponde di quello che succede usandolo.
/// </para>
/// <para>
/// <b>Perché non si può spegnere.</b> Un avviso con una casella «non mostrare più» è
/// un avviso che dopo il primo giorno non esiste, mentre la condizione che descrive —
/// software in prova su un posto telefonico di servizio — vale tutti i giorni. Il
/// costo è una pressione di Invio: il pulsante di conferma ha già il fuoco.
/// </para>
/// <para>
/// Il testo viene anche pronunciato. Una finestra che compare e non parla, per chi non
/// vede, è un programma che non parte: resterebbe lì in attesa di una conferma che
/// nessuno sa di dover dare.
/// </para>
/// <para>
/// <b>E per un po' non ha parlato davvero.</b> L'annuncio partiva, ma non arrivava a
/// destinazione: vedi <see cref="OnAnnouncementSpoken"/>.
/// </para>
/// </remarks>
public partial class BetaNoticeWindow : Window
{
    private readonly AnnouncementService _announcements;

    /// <summary>Crea la finestra.</summary>
    /// <param name="announcements">
    /// Servizio di annuncio. Serve intero e non come sola
    /// <see cref="IAnnouncer"/>: da qui si annuncia, ma soprattutto si <b>ascolta</b>
    /// ciò che viene pronunciato, per portarlo a UI Automation.
    /// </param>
    public BetaNoticeWindow(AnnouncementService announcements)
    {
        InitializeComponent();

        _announcements = announcements;

        var versione = Versione();

        TitleText.Text = $"BlindPhone {versione} - versione di prova";
        NoticeText.Text = HelpTextLayout.Wrap(Testo(versione));

        Loaded += (_, _) =>
        {
            // Il fuoco resta sul pulsante di conferma, ed e' deliberato: chi il
            // programma lo conosce parte con un Invio senza ascoltare fino in fondo.
            // L'avviso lo raggiunge lo stesso, perche' arriva come notifica e non
            // perche' il cursore ci passa sopra.
            AcceptButton.Focus();

            // La sottoscrizione precede l'annuncio: altrimenti il primo — cioe'
            // proprio questo — verrebbe emesso quando nessuno e' ancora in ascolto.
            _announcements.Spoken += OnAnnouncementSpoken;

            _announcements.Announce(Announcement.Critical(
                $"{TitleText.Text}. {Testo(versione)} Premi Invio per accettare e avviare, Esc per uscire.",
                DateTimeOffset.Now));
        };

        Closed += (_, _) => _announcements.Spoken -= OnAnnouncementSpoken;
    }

    /// <summary>
    /// Porta l'annuncio a UI Automation, che è l'unica strada per cui uno screen
    /// reader lo può sentire mentre questa finestra è aperta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il difetto che questo metodo corregge, e perché era invisibile.</b> L'avviso
    /// veniva annunciato regolarmente, e non lo sentiva nessuno. La catena, verificata
    /// sul registro di una postazione con NVDA in esecuzione:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   il bridge diretto verso NVDA non è disponibile — richiede
    ///   <c>nvdaControllerClient64.dll</c> accanto all'eseguibile, e dove manca si
    ///   dichiara correttamente assente;
    ///   </description></item>
    ///   <item><description>
    ///   il contrassegno di sistema dice però che uno screen reader <b>c'è</b>, quindi
    ///   la voce interna tace di proposito, per non pronunciare due volte ciò che si
    ///   presume lo screen reader stia già leggendo dalla notifica UI Automation;
    ///   </description></item>
    ///   <item><description>
    ///   ma quella notifica la sollevava <b>solo la finestra principale</b>, che a
    ///   quel punto dell'avvio non è ancora stata costruita.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Ogni anello si comportava correttamente per conto suo, e il risultato era
    /// silenzio totale sulla prima finestra che un operatore cieco incontra: nessun
    /// riscontro dell'avvio, e un avviso di responsabilità accettato senza averlo mai
    /// sentito. Il testo si poteva raggiungere solo indovinando di dover premere
    /// freccia giù.
    /// </para>
    /// <para>
    /// La notifica si solleva sulla casella che contiene l'avviso e non sulla finestra:
    /// è l'elemento che <b>porta</b> quel testo, e uno screen reader che segue la
    /// notifica ci trova sotto il contenuto per esteso, rileggibile con le frecce.
    /// </para>
    /// </remarks>
    private void OnAnnouncementSpoken(object? sender, Announcement announcement)
        => Dispatcher.Invoke(() =>
        {
            var peer = UIElementAutomationPeer.FromElement(NoticeText)
                       ?? UIElementAutomationPeer.CreatePeerForElement(NoticeText);

            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.ImportantAll,
                announcement.Text,
                Guid.NewGuid().ToString());
        });

    /// <summary>
    /// Versione leggibile del prodotto.
    /// </summary>
    /// <remarks>
    /// Si prende quella informativa, che porta l'etichetta «beta», e non quella
    /// numerica dell'assembly: l'installatore di Windows accetta solo tre numeri, e
    /// mostrare "1.0.0" in un avviso che parla di versione di prova toglierebbe
    /// all'avviso proprio la parola che conta.
    /// </remarks>
    private static string Versione()
    {
        var assembly = typeof(BetaNoticeWindow).Assembly;

        var informativa = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informativa))
        {
            // Il compilatore puo' appendere l'identificativo della revisione dopo un
            // segno piu': all'operatore non serve.
            var piu = informativa.IndexOf('+');
            return piu > 0 ? informativa[..piu] : informativa;
        }

        return assembly.GetName().Version?.ToString() ?? "sconosciuta";
    }

    private static string Testo(string versione) =>
        $"""
        Questa è la versione {versione} di BlindPhone: una versione di prova, non ancora dichiarata pronta per l'uso in servizio.

        Che cosa significa in pratica. Alcune funzioni sono complete e collaudate, altre sono presenti ma non verificate su tutti i centralini, altre ancora non ci sono. La guida in linea, che si apre con F1, ha un argomento intitolato «Cosa non c'è ancora».

        Nessuna garanzia e nessuna responsabilità. Il software viene fornito così com'è, senza garanzia di alcun tipo, esplicita o implicita, compresa quella di commerciabilità o di idoneità a uno scopo particolare. Chi lo ha sviluppato non si assume alcuna responsabilità per malfunzionamenti, chiamate perse, interruzioni del servizio, perdita di dati o qualunque danno diretto o indiretto derivante dall'uso del programma.

        Non usarlo come unico mezzo per chiamate di emergenza. Un posto operatore in prova non è un apparato di soccorso: tieni disponibile un telefono che non dipenda da questo programma.

        Continuando dichiari di aver letto e compreso questo avviso.
        """;

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnQuitClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
