using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;

namespace BlindPhone.App;

/// <summary>
/// Guida in linea, aperta con F1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Si costruisce a ogni apertura</b> a partire dal profilo di scorciatoie attivo:
/// le combinazioni citate nel testo sono quelle configurate adesso. Una guida che
/// dicesse "premi F9" a chi ha rimappato il trasferimento manderebbe a premere
/// qualcosa che non fa nulla, e lo farebbe proprio nel momento in cui l'operatore è in
/// difficoltà e ha un chiamante in linea.
/// </para>
/// <para>
/// <b>Si apre sull'argomento pertinente</b>: dalla rubrica sulla rubrica, dalle
/// impostazioni sulle impostazioni, dal campo di composizione sulla composizione.
/// Costringere a cercare l'argomento in un elenco di quattordici voci quando il
/// contesto lo dice già è tempo tolto a chi sta chiedendo aiuto.
/// </para>
/// </remarks>
public partial class HelpWindow : Window
{
    private readonly IAnnouncer _announcer;
    private readonly IReadOnlyList<HelpTopic> _topics;

    /// <summary>Crea la guida.</summary>
    /// <param name="topics">Argomenti da mostrare.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="initialTopicId">
    /// Argomento da aprire per primo. Se non esiste si apre il primo dell'elenco.
    /// </param>
    public HelpWindow(IReadOnlyList<HelpTopic> topics, IAnnouncer announcer, string? initialTopicId = null)
    {
        _announcer = announcer;
        _topics = topics;

        InitializeComponent();

        foreach (var topic in _topics)
        {
            TopicList.Items.Add(new ListBoxItem { Content = topic.Title, Tag = topic.Id });
        }

        SelectTopic(initialTopicId ?? HelpBook.DefaultTopicId);

        Loaded += (_, _) =>
        {
            TopicList.Focus();

            var current = CurrentTopic();

            _announcer.Announce(Announcement.CommandResponse(
                $"Guida di BlindPhone, {_topics.Count} argomenti. "
                + $"Sei su {current?.Title}. Frecce per cambiare argomento, Tab per leggerlo, Esc per chiudere",
                DateTimeOffset.Now));
        };
    }

    /// <summary>
    /// Porta la guida su un argomento, anche se era già aperta.
    /// </summary>
    /// <param name="topicId">Argomento richiesto.</param>
    /// <remarks>
    /// Serve da quando la guida non è più modale e ne resta una sola: il tasto di aiuto
    /// premuto da un'altra parte del programma deve portare all'argomento di quella
    /// parte, non limitarsi a riportare avanti la pagina che era rimasta aperta. Il
    /// cursore va sull'elenco degli argomenti perché è da lì che ci si muove, ed è dove
    /// lo troverebbe chi apre la guida adesso.
    /// </remarks>
    public void ShowTopic(string? topicId)
    {
        SelectTopic(topicId ?? HelpBook.DefaultTopicId);
        TopicList.Focus();
    }

    private HelpTopic? CurrentTopic()
    {
        var id = (TopicList.SelectedItem as ListBoxItem)?.Tag as string;
        return _topics.FirstOrDefault(t => t.Id == id);
    }

    private void SelectTopic(string id)
    {
        foreach (ListBoxItem item in TopicList.Items)
        {
            if ((string?)item.Tag != id) continue;

            TopicList.SelectedItem = item;
            TopicList.ScrollIntoView(item);
            return;
        }

        if (TopicList.Items.Count > 0) TopicList.SelectedIndex = 0;
    }

    /// <remarks>
    /// Nessun annuncio al cambio di argomento: l'elenco è un controllo standard e lo
    /// screen reader legge già la voce su cui arriva il cursore. Un annuncio nostro
    /// arriverebbe subito dopo, dicendo la stessa cosa.
    /// </remarks>
    private void OnTopicSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentTopic() is not { } topic) return;

        TopicText.Text = topic.Text;
        TopicText.CaretIndex = 0;
        TopicText.ScrollToHome();

        // Il nome accessibile porta il titolo: quando il focus arriva sulla casella
        // con Tab, lo screen reader dice di quale argomento e' il testo invece di
        // annunciare una generica casella di sola lettura.
        AutomationProperties.SetName(TopicText, $"Testo dell'argomento {topic.Title}");
    }

    /// <summary>
    /// Scrive l'intera guida in un file di testo.
    /// </summary>
    /// <remarks>
    /// Stesso scopo della scheda dei tasti: un documento in righe corte che si legge
    /// con la sintesi in lettura continua, si manda a una stampante braille e si può
    /// consegnare a un operatore appena assunto. Le combinazioni riportate sono quelle
    /// del profilo attivo al momento dell'esportazione.
    /// </remarks>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Esporta la guida di BlindPhone",
            Filter = "Testo semplice (*.txt)|*.txt|Tutti i file|*.*",
            FileName = "BlindPhone-guida.txt",
            AddExtension = true,
            DefaultExt = ".txt",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var text = new StringBuilder();
            text.AppendLine("BlindPhone - Guida all'uso");
            text.AppendLine();

            foreach (var topic in _topics)
            {
                text.AppendLine(topic.Title);
                text.AppendLine(new string('-', topic.Title.Length));
                text.AppendLine();
                text.AppendLine(topic.Text);
                text.AppendLine();
            }

            File.WriteAllText(dialog.FileName, text.ToString(), Encoding.UTF8);

            _announcer.Announce(Announcement.CommandResponse(
                $"Guida esportata in {Path.GetFileName(dialog.FileName)}", DateTimeOffset.Now));
        }
        catch (Exception)
        {
            _announcer.Announce(Announcement.CommandResponse(
                "Esportazione non riuscita. Verifica i permessi sulla cartella scelta", DateTimeOffset.Now));
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
