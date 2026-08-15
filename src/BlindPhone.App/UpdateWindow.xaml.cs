using System.Diagnostics;
using System.Windows;
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
/// <b>Niente parte senza che l'operatore lo chieda.</b> La finestra si apre già
/// sapendo che una versione nuova c'è — il controllo è una lettura dell'elenco della
/// cartella remota, fatta altrove — ma non ha scaricato niente. Lo scaricamento parte
/// col pulsante, e l'installazione è un programma di Windows che chiede a sua volta il
/// consenso di amministratore.
/// </para>
/// <para>
/// <b>Il pulsante predefinito è «Più tardi».</b> Un aggiornamento non è un'emergenza e
/// la finestra può comparire mentre squilla il telefono: un Invio dato per togliersela
/// di torno non deve far partire un'installazione che chiude l'applicazione con una
/// chiamata in corso.
/// </para>
/// <para>
/// Tutto quello che compare a schermo viene anche annunciato. Una finestra che si apre
/// e non parla, per chi non vede, è un programma che si è bloccato.
/// </para>
/// </remarks>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updates;
    private readonly IAnnouncer _announcer;
    private readonly ILogger<UpdateWindow> _log;
    private readonly AvailableUpdate _update;
    private readonly UpdateNotes _notes;

    private bool _busy;

    /// <summary>Crea la finestra.</summary>
    /// <param name="updates">Servizio degli aggiornamenti.</param>
    /// <param name="result">Esito del controllo, con l'aggiornamento e le sue novità.</param>
    /// <param name="installed">Versione installata su questa postazione.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public UpdateWindow(
        UpdateService updates,
        UpdateCheckResult result,
        string installed,
        IAnnouncer announcer,
        ILogger<UpdateWindow> logger)
    {
        InitializeComponent();

        _updates = updates;
        _announcer = announcer;
        _log = logger;
        _update = result.Update ?? throw new ArgumentException("Nessun aggiornamento da mostrare", nameof(result));
        _notes = result.Notes;

        VersionText.Text = $"Versione installata: {installed}    →    versione disponibile: {_update.DisplayVersion}";
        NotesText.Text = DescribeNotes();
        ChannelText.Text = DescribeChannel();

        Loaded += (_, _) =>
        {
            LaterButton.Focus();

            _announcer.Announce(Announcement.High(
                $"Aggiornamento disponibile: versione {_update.DisplayVersion}. "
                + "Premi Tab per leggere le novita', Invio per aggiornare piu' tardi.",
                DateTimeOffset.Now,
                coalesceKey: "update:available"));
        };
    }

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

    /// <summary>
    /// Dice da dove arriva il pacchetto e quanto ci si può fidare del percorso.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È l'informazione che rende la scelta consapevole invece che automatica. Su un
    /// canale in chiaro chi sta sul percorso di rete può sostituire il pacchetto con
    /// un altro programma, che verrebbe installato con i privilegi di amministratore:
    /// è il rischio più grosso dell'intera funzione, e va scritto dove si preme il
    /// pulsante, non solo nel registro.
    /// </para>
    /// <para>
    /// L'impronta dichiarata nelle note non elimina quel rischio — chi può cambiare il
    /// pacchetto può cambiare anche le note — ma copre il caso più frequente, che è il
    /// file arrivato incompleto o sovrascritto a metà caricamento.
    /// </para>
    /// </remarks>
    private string DescribeChannel()
    {
        var canale = _updates.IsSecureChannel
            ? "Il pacchetto arriva da un canale cifrato."
            : "ATTENZIONE: il pacchetto arriva da un canale NON cifrato. Chi si trova sul percorso di rete "
              + "potrebbe sostituirlo. Chiedi a chi amministra il server di abilitare FTPS.";

        var impronta = _notes.Sha256 is null
            ? "Le note non dichiarano l'impronta del pacchetto: l'integrita' non potra' essere verificata."
            : "L'integrita' del pacchetto sara' verificata con l'impronta dichiarata nelle note.";

        return $"{canale} {impronta}";
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        InstallButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var avanzamento = new Progress<int>(percentuale =>
            {
                DownloadProgress.Value = percentuale;
                ProgressText.Text = $"Scaricamento in corso: {percentuale} per cento";
            });

            ProgressText.Text = "Scaricamento in corso";

            _announcer.Announce(Announcement.CommandResponse(
                "Scaricamento dell'aggiornamento avviato", DateTimeOffset.Now));

            var pacchetto = await _updates
                .DownloadAsync(_update, _notes, avanzamento, CancellationToken.None)
                .ConfigureAwait(true);

            ProgressText.Text = "Pacchetto scaricato e verificato. Avvio dell'installazione";

            _announcer.Announce(Announcement.High(
                "Aggiornamento scaricato e verificato. L'installazione sta per partire e BlindPhone verra' chiuso.",
                DateTimeOffset.Now));

            LaunchInstaller(pacchetto);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Aggiornamento alla versione {Versione} non riuscito", _update.DisplayVersion);

            ProgressText.Text = ex.Message;
            InstallButton.IsEnabled = true;
            _busy = false;

            _announcer.Announce(Announcement.High(
                $"Aggiornamento non riuscito. {ex.Message}", DateTimeOffset.Now));
        }
    }

    /// <summary>
    /// Avvia l'installatore di Windows sul pacchetto scaricato.
    /// </summary>
    /// <param name="packagePath">Percorso del pacchetto verificato.</param>
    /// <remarks>
    /// <para>
    /// <b>Si lancia <c>msiexec</c> e si chiude l'applicazione</b>, in quest'ordine e
    /// non nell'altro: un pacchetto che deve sostituire i file di un programma in
    /// esecuzione chiederebbe un riavvio della macchina, e su una postazione di
    /// servizio quello è un costo che si può evitare uscendo prima.
    /// </para>
    /// <para>
    /// L'installazione <b>non</b> è silenziosa. L'operatore vede la stessa procedura
    /// della prima installazione, con la richiesta di consenso di Windows: è quello che
    /// permette di fermarsi a metà se qualcosa non torna, ed è l'ultima occasione in
    /// cui un pacchetto sbagliato può ancora essere rifiutato.
    /// </para>
    /// </remarks>
    private void LaunchInstaller(string packagePath)
    {
        var avvio = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{packagePath}\"",
            UseShellExecute = true,
        };

        _log.LogInformation("Avvio dell'installazione di {Pacchetto}", packagePath);

        Process.Start(avvio);

        DialogResult = true;
        Close();

        // La chiusura dell'applicazione passa dal percorso normale: le chiamate in
        // corso vengono chiuse come si deve e la registrazione SIP viene ritirata.
        Application.Current.Shutdown();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        DialogResult = false;
        Close();
    }
}
