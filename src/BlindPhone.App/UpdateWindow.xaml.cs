using System.Windows;
using System.Windows.Automation;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Finestra che propone un aggiornamento, ne mostra le novità e lo installa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tre scelte.</b> «Scarica e installa ora» scarica il pacchetto qui, con la barra
/// di avanzamento, e avvia l'installazione: BlindPhone si chiude. «Scarica e installa
/// successivamente» chiude la finestra e lascia lo scaricamento a
/// <see cref="BackgroundUpdateDownloader"/>: al prossimo avvio la stessa finestra
/// chiede se installare. «Annulla» non scarica niente, e il controllo all'avvio
/// successivo ripropone l'aggiornamento da sé.
/// </para>
/// <para>
/// <b>Il pulsante predefinito è «Scarica e installa successivamente».</b> Un
/// aggiornamento non è un'emergenza e la finestra può comparire mentre squilla il
/// telefono: un Invio dato per togliersela di torno non deve far partire
/// un'installazione che chiude l'applicazione con una chiamata in corso. Scaricare in
/// sottofondo non chiude niente.
/// </para>
/// <para>
/// <b>Quando il pacchetto c'è già, la finestra fa la domanda del riavvio.</b> Se nella
/// cartella degli scaricamenti c'è la stessa versione, o una più recente, non ha senso
/// proporre di scaricarla: titolo e pulsanti diventano «Sì, installa adesso» e «No, più
/// tardi», e il secondo prende Invio ed Esc. Così la domanda è la stessa da qualunque
/// parte si arrivi — l'avvio, il pulsante della barra, l'area di notifica, le
/// impostazioni.
/// </para>
/// <para>
/// Tutto quello che compare a schermo viene anche annunciato. Una finestra che si apre
/// e non parla, per chi non vede, è un programma che si è bloccato.
/// </para>
/// </remarks>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updates;
    private readonly BackgroundUpdateDownloader _downloader;
    private readonly IAnnouncer _announcer;
    private readonly ILogger<UpdateWindow> _log;
    private readonly AvailableUpdate _update;
    private readonly UpdateNotes _notes;

    /// <summary>Il pacchetto già scaricato da installare, se la finestra fa la domanda del riavvio.</summary>
    private readonly DownloadedUpdate? _ready;

    private bool _busy;

    /// <summary>Crea la finestra.</summary>
    /// <param name="updates">Servizio degli aggiornamenti.</param>
    /// <param name="result">Esito del controllo, con l'aggiornamento e le sue novità.</param>
    /// <param name="installed">Versione installata su questa postazione.</param>
    /// <param name="downloader">Scaricamento in sottofondo, condiviso da tutta l'applicazione.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public UpdateWindow(
        UpdateService updates,
        UpdateCheckResult result,
        string installed,
        BackgroundUpdateDownloader downloader,
        IAnnouncer announcer,
        ILogger<UpdateWindow> logger)
    {
        InitializeComponent();

        _updates = updates;
        _downloader = downloader;
        _announcer = announcer;
        _log = logger;

        var trovato = result.Update ?? throw new ArgumentException("Nessun aggiornamento da mostrare", nameof(result));

        // Uno scaricamento ancora in corso ha soltanto il .part: non e' pronto, e
        // FindDownloaded non lo vede.
        _ready = updates.FindDownloaded() is { } scaricato && scaricato.Update.Version >= trovato.Version
            ? scaricato
            : null;

        _update = _ready?.Update ?? trovato;

        // Le novita' sono quelle del pacchetto che si installa. Quelle lette dal server
        // valgono al posto di quelle salvate solo se sono della stessa versione e le
        // salvate mancano.
        _notes = _ready switch
        {
            null => result.Notes,
            { Notes.Features.Count: 0 } pronto when pronto.Update.Version == trovato.Version => result.Notes,
            var pronto => pronto.Notes,
        };

        if (_ready is not null) ShowAsReadyToInstall(installed);
        else VersionText.Text = $"Versione installata: {installed}    →    versione disponibile: {_update.DisplayVersion}";

        NotesText.Text = DescribeNotes();
        ChannelText.Text = DescribeChannel();

        DisableInstallIfUnverifiable();

        // Il nome della casella dice di quale versione sono le novita': e' la prima cosa
        // che lo screen reader legge arrivandoci, cioe' all'apertura.
        AutomationProperties.SetName(NotesText, $"Novità della versione {_update.DisplayVersion}");
        AutomationProperties.SetHelpText(NotesText, $"Frecce su e giu' per leggere riga per riga. {DescribeKeys()}");

        Loaded += (_, _) =>
        {
            // Il cursore va sulle novita', in cima: lo screen reader legge il nome della
            // casella e la prima novita', e le altre si leggono con le frecce. Vedi il
            // commento in testa a UpdateWindow.xaml.
            NotesText.CaretIndex = 0;
            NotesText.Focus();

            // Non interrompe: arriva dopo la lettura della casella, e dice quello che la
            // casella non dice - da quale versione si parte, da dove arriva il pacchetto,
            // e come si sceglie.
            var apertura = _ready is null
                ? $"Aggiornamento disponibile: versione {_update.DisplayVersion}, installata la {installed}. "
                : $"Aggiornamento pronto da installare: la versione {_update.DisplayVersion} e' gia' scaricata, "
                  + $"installata la {installed}. ";

            // In coda: non deve tagliare la notizia della registrazione o della cuffia
            // che arriva nello stesso momento, all'avvio. Vedi Announcement.Sequential.
            _announcer.Announce(Announcement.High(
                apertura + $"{CountNotes()} {DescribeKeys()} {ChannelText.Text}",
                DateTimeOffset.Now,
                coalesceKey: "update:available") with { Sequential = true });
        };
    }

    /// <summary>
    /// Trasforma la finestra nella domanda del riavvio: il pacchetto c'è già, resta da
    /// decidere se installarlo.
    /// </summary>
    private void ShowAsReadyToInstall(string installed)
    {
        Title = "BlindPhone - aggiornamento pronto da installare";
        TitleText.Text = "Aggiornamento pronto da installare";
        AutomationProperties.SetName(TitleText, "Aggiornamento pronto da installare");

        VersionText.Text = $"Versione installata: {installed}    →    versione già scaricata: {_update.DisplayVersion}";

        InstallNowButton.Content = "_Sì, installa adesso";
        AutomationProperties.SetName(InstallNowButton, "Sì, installa adesso");
        AutomationProperties.SetHelpText(InstallNowButton,
            "Verifica di nuovo la firma del pacchetto già scaricato e avvia l'installazione. "
            + "BlindPhone verrà chiuso: nessuna chiamata deve essere in corso.");

        // Senza «successivamente» il predefinito passa al no: Invio ed Esc non
        // installano e non chiudono niente.
        InstallLaterButton.IsDefault = false;
        InstallLaterButton.Visibility = Visibility.Collapsed;

        CancelButton.Content = "_No, più tardi";
        CancelButton.IsDefault = true;
        AutomationProperties.SetName(CancelButton, "No, più tardi");
        AutomationProperties.SetHelpText(CancelButton,
            "Non installa adesso. Il pacchetto resta scaricato e te lo richiedo al prossimo avvio di BlindPhone.");
    }

    /// <summary>Quante novità ci sono, detto in una frase.</summary>
    private string CountNotes() => _notes.Features.Count switch
    {
        0 => "Per questa versione non sono state pubblicate note sulle novita'.",
        1 => "Una novita', nella casella.",
        var n => $"{n} novita', nella casella: frecce su e giu' per leggerle.",
    };

    /// <summary>
    /// Compone il testo delle novità.
    /// </summary>
    /// <remarks>
    /// Un file di novità mancante o vuoto non è un errore da mostrare come tale: il
    /// pacchetto esiste comunque e l'aggiornamento si può fare. Si dice che le novità
    /// non sono state pubblicate, che è un'informazione, e non «errore nella lettura
    /// del file», che è un rimprovero a qualcuno che non è l'operatore.
    /// </remarks>
    private string DescribeNotes()
    {
        if (_notes.Features.Count == 0)
        {
            return "Per questa versione non sono state pubblicate note sulle novita'.";
        }

        var righe = _notes.Features.Select(f => $"• {f}");
        return string.Join(Environment.NewLine, righe);
    }

    /// <summary>I tasti della finestra, detti come si premono.</summary>
    private string DescribeKeys()
    {
        if (!InstallNowButton.IsEnabled)
        {
            return "Esc per chiudere.";
        }

        return _ready is null
            ? "Invio per scaricarlo adesso e installarlo al prossimo avvio, Alt O per scaricarlo e installarlo "
              + "subito chiudendo BlindPhone, Esc per annullare: te lo ripropongo al prossimo avvio."
            : "Alt S per installarlo adesso: BlindPhone verra' chiuso. Invio o Esc per non installarlo ora: "
              + "te lo richiedo al prossimo avvio.";
    }

    /// <summary>
    /// Dice da dove arriva il pacchetto e quanto ci si può fidare del percorso.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Un canale in chiaro non è più una scelta da dichiarare qui: l'aggiornamento non
    /// parte affatto, e il testo dice perché. Chi stava sul percorso di rete poteva
    /// sostituire il pacchetto con un altro programma, che veniva installato con i
    /// privilegi di amministratore, e un avviso in questa casella non è mai stata una
    /// difesa — lo leggeva chi non poteva farci niente.
    /// </para>
    /// <para>
    /// Quello che resta da dire è <b>che cosa viene verificato</b>. La firma dimostra
    /// la provenienza; l'impronta dichiarata nelle note, quando c'è, aggiunge la
    /// verifica dell'integrità del trasferimento. Sono due cose diverse e l'operatore
    /// sente entrambe.
    /// </para>
    /// </remarks>
    private string DescribeChannel()
    {
        if (!UpdateSignature.IsConfigured)
        {
            return "Questa versione di BlindPhone non puo' verificare la firma degli aggiornamenti, quindi "
                   + "non li installa da sola. L'aggiornamento va fatto a mano da chi amministra la postazione.";
        }

        if (_ready is not null)
        {
            return "Il pacchetto e' gia' stato scaricato e verificato. La sua firma viene controllata di nuovo "
                   + "un istante prima dell'installazione, e durante l'installazione BlindPhone viene chiuso.";
        }

        if (!_updates.IsSecureChannel)
        {
            return "Il server degli aggiornamenti usa un canale NON cifrato: l'aggiornamento non viene "
                   + "scaricato. Chiedi a chi amministra il server di abilitare FTPS.";
        }

        var impronta = _notes.Sha256 is null
            ? "Le note non dichiarano l'impronta del pacchetto: serve la riga SHA256 nel file delle novita'."
            : "L'integrita' del pacchetto sara' verificata anche con l'impronta dichiarata nelle note.";

        return "Il pacchetto arriva da un canale cifrato e la sua firma viene verificata prima "
               + $"dell'installazione. {impronta}";
    }

    /// <summary>
    /// Toglie i pulsanti di scaricamento quando l'aggiornamento non è installabile.
    /// </summary>
    /// <remarks>
    /// Un pulsante che c'è e fallisce sempre è peggio di un pulsante che non c'è: per
    /// chi non vede lo schermo, premerlo e sentire un errore è l'unico modo di
    /// scoprire che non era disponibile. Il perché sta già in
    /// <see cref="DescribeChannel"/>, che viene letto ad alta voce. Invio passa ad
    /// «Annulla», perché un predefinito spento non risponde a niente.
    /// </remarks>
    private void DisableInstallIfUnverifiable()
    {
        var installabile = UpdateSignature.IsConfigured && (_ready is not null || _updates.IsSecureChannel);
        if (installabile) return;

        InstallNowButton.IsEnabled = false;
        InstallLaterButton.IsEnabled = false;
        InstallLaterButton.IsDefault = false;
        CancelButton.IsDefault = true;

        const string motivo = "L'aggiornamento non puo' essere verificato su questa postazione";
        InstallNowButton.ToolTip = motivo;
        InstallLaterButton.ToolTip = motivo;
    }

    private async void OnInstallNowClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        InstallNowButton.IsEnabled = false;
        InstallLaterButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var pacchetto = _ready is { } pronto
                ? pronto.PackagePath
                : await DownloadForInstallAsync().ConfigureAwait(true);

            ProgressText.Text = "Pacchetto verificato. Avvio dell'installazione";

            _announcer.Announce(Announcement.High(
                _ready is null
                    ? "Aggiornamento scaricato e verificato. L'installazione sta per partire e BlindPhone verra' chiuso."
                    : "L'installazione sta per partire e BlindPhone verra' chiuso.",
                DateTimeOffset.Now));

            UpdateInstaller.Launch(pacchetto, _log);

            DialogResult = true;
            Close();

            // La chiusura dell'applicazione passa dal percorso normale: le chiamate in
            // corso vengono chiuse come si deve e la registrazione SIP viene ritirata.
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Aggiornamento alla versione {Versione} non riuscito", _update.DisplayVersion);

            var messaggio = ex.Message;

            // Un pacchetto gia' scaricato che non passa la verifica non si
            // riproporra' mai bene: va tolto, e al prossimo avvio si riparte dallo
            // scaricamento.
            if (_ready is { } scartato && ex is InvalidOperationException)
            {
                _updates.DiscardDownloaded(scartato);
                messaggio += " Il pacchetto scaricato e' stato eliminato: al prossimo avvio ti ripropongo lo scaricamento.";
            }
            else
            {
                InstallNowButton.IsEnabled = true;
                InstallLaterButton.IsEnabled = true;
            }

            ProgressText.Text = messaggio;
            _busy = false;

            _announcer.Announce(Announcement.High(
                $"Aggiornamento non riuscito. {messaggio}", DateTimeOffset.Now));
        }
    }

    /// <summary>Scarica il pacchetto qui, con la barra di avanzamento.</summary>
    private async Task<string> DownloadForInstallAsync()
    {
        if (_downloader.IsDownloadingVersion(_update.Version))
        {
            // Lo stesso pacchetto sta gia' arrivando in sottofondo: si aspetta quello,
            // senza percentuale da mostrare.
            DownloadProgress.IsIndeterminate = true;
            ProgressText.Text = "Lo scaricamento in sottofondo e' gia' in corso: attendi che finisca";

            _announcer.Announce(Announcement.CommandResponse(
                "Lo scaricamento e' gia' in corso in sottofondo: al termine parte l'installazione", DateTimeOffset.Now));
        }
        else
        {
            ProgressText.Text = "Scaricamento in corso";

            _announcer.Announce(Announcement.CommandResponse(
                "Scaricamento dell'aggiornamento avviato", DateTimeOffset.Now));
        }

        var avanzamento = new Progress<int>(percentuale =>
        {
            DownloadProgress.Value = percentuale;
            ProgressText.Text = $"Scaricamento in corso: {percentuale} per cento";
        });

        return await _downloader
            .DownloadNowAsync(_updates, _update, _notes, avanzamento)
            .ConfigureAwait(true);
    }

    private void OnInstallLaterClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _downloader.Start(_updates, _update, _notes);

        DialogResult = false;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        DialogResult = false;
        Close();
    }
}
