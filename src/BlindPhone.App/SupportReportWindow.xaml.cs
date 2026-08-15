using System.IO;
using System.Text;
using System.Windows;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Raccoglie una segnalazione e recapita il pacchetto diagnostico all'assistenza.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché una finestra propria e non un pulsante nelle impostazioni.</b> Il
/// pacchetto serve quando le cose vanno male, e in quel momento la sequenza «apri le
/// impostazioni, trova la scheda, premi il pulsante, scegli una cartella, ritrova il
/// file, allegalo a una mail» non si porta a termine — meno che mai senza vedere lo
/// schermo. Qui è un gesto solo, con una combinazione di tasti che lo raggiunge da
/// qualunque punto del programma.
/// </para>
/// <para>
/// <b>Il campo della descrizione ha il fuoco all'apertura.</b> È la parte più utile
/// del pacchetto e l'unica che solo l'operatore può scrivere: «succede quando
/// trasferisco mentre ho una seconda chiamata in attesa» indirizza la ricerca in un
/// modo che nessun contatore può fare.
/// </para>
/// </remarks>
public partial class SupportReportWindow : Window
{
    private readonly SupportPackageService _support;
    private readonly SettingsService _settingsService;
    private readonly CrashReporter _crashes;
    private readonly Func<string> _diagnosticSummary;
    private readonly IAnnouncer _announcer;
    private readonly ILogger<SupportReportWindow> _log;

    /// <summary>Crea la finestra.</summary>
    /// <param name="support">Servizio che costruisce e recapita il pacchetto.</param>
    /// <param name="settingsService">Configurazione, per i recapiti dell'assistenza.</param>
    /// <param name="crashes">Rapporti di errore in attesa.</param>
    /// <param name="diagnosticSummary">Come ottenere il riepilogo diagnostico.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SupportReportWindow(
        SupportPackageService support,
        SettingsService settingsService,
        CrashReporter crashes,
        Func<string> diagnosticSummary,
        IAnnouncer announcer,
        ILogger<SupportReportWindow> logger)
    {
        InitializeComponent();

        _support = support;
        _settingsService = settingsService;
        _crashes = crashes;
        _diagnosticSummary = diagnosticSummary;
        _announcer = announcer;
        _log = logger;

        Loaded += (_, _) =>
        {
            Compose();
            NoteBox.Focus();

            _announcer.Announce(Announcement.CommandResponse(
                $"{HeaderText.Text}. Descrivi il problema, poi premi Invio per creare e inviare il pacchetto.",
                DateTimeOffset.Now));
        };
    }

    private void Compose()
    {
        var errori = _crashes.Pending();
        var supporto = _settingsService.Current.Support;

        HeaderText.Text = errori.Count switch
        {
            0 => "Segnala un problema all'assistenza",
            1 => "Segnala un problema. C'e' un errore registrato in attesa di essere inviato",
            _ => $"Segnala un problema. Ci sono {errori.Count} errori registrati in attesa di essere inviati",
        };

        var destinazione = DescribeDestination(supporto);

        var sb = new StringBuilder()
            .AppendLine("Il pacchetto conterra':")
            .AppendLine()
            .AppendLine("- il riepilogo diagnostico: versione, configurazione, stato del telefono e dell'audio")
            .AppendLine("- la descrizione che stai scrivendo")
            .AppendLine($"- i rapporti di errore registrati: {errori.Count}")
            .AppendLine("- gli ultimi sette file di registro")
            .AppendLine()
            .AppendLine("ATTENZIONE AI DATI PERSONALI. I file di registro riportano i numeri chiamati e")
            .AppendLine("chiamanti riga per riga: sono dati di traffico, cioe' dati personali, e vanno")
            .AppendLine("inviati soltanto a chi e' autorizzato a trattarli.")
            .AppendLine()
            .AppendLine("Il riepilogo non contiene password ne' credenziali.")
            .AppendLine()
            .AppendLine($"Destinazione: {destinazione}");

        ContentsText.Text = HelpTextLayout.Wrap(sb.ToString());
    }

    /// <summary>
    /// Descrive dove finirà il pacchetto.
    /// </summary>
    /// <remarks>
    /// Va detto prima e non dopo: «invia» senza sapere a chi è un gesto che nessuno
    /// dovrebbe compiere su un file che contiene dati di traffico.
    /// </remarks>
    private static string DescribeDestination(SupportSettings support)
    {
        if (!string.IsNullOrWhiteSpace(support.UploadUrl))
        {
            return $"caricamento sull'indirizzo configurato dall'ente, {support.UploadUrl}";
        }

        if (!string.IsNullOrWhiteSpace(support.Email))
        {
            return $"messaggio di posta a {support.Email}, che dovrai controllare e inviare tu";
        }

        return "nessun recapito configurato: il pacchetto verra' salvato sul desktop e la cartella si aprira'. "
               + "I recapiti dell'assistenza si impostano da Impostazioni, scheda Diagnostica.";
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = RunAsync(send: true);

    private void OnSaveClick(object sender, RoutedEventArgs e) => _ = RunAsync(send: false);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async Task RunAsync(bool send)
    {
        SendButton.IsEnabled = false;
        SaveButton.IsEnabled = false;

        try
        {
            Report("Creazione del pacchetto in corso");

            var path = Path.Combine(SupportPackageService.DefaultFolder, SupportPackageService.SuggestedFileName);
            var nota = NoteBox.Text;

            var esito = await Task.Run(() => _support.Build(path, _diagnosticSummary(), nota)).ConfigureAwait(true);

            if (!esito.Success)
            {
                Report(esito.Message);
                return;
            }

            if (!send)
            {
                _support.RevealInExplorer(esito.Path!);
                Report($"{esito.Message}. Il file e' sul desktop e la cartella si e' aperta");
                return;
            }

            Report(await DeliverAsync(esito.Path!, nota).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Segnalazione all'assistenza non riuscita");
            Report("Operazione non riuscita. Il registro ne ha conservato il dettaglio");
        }
        finally
        {
            SendButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Recapita il pacchetto secondo la configurazione dell'ente.
    /// </summary>
    /// <remarks>
    /// L'ordine è quello di automatismo decrescente: caricamento se l'ente espone un
    /// indirizzo, altrimenti messaggio di posta preparato, altrimenti il file sul
    /// desktop con la cartella aperta. L'ultimo non è un fallimento: è il ripiego che
    /// funziona su qualunque postazione, anche senza posta configurata e senza rete.
    /// </remarks>
    private async Task<string> DeliverAsync(string path, string nota)
    {
        var supporto = _settingsService.Current.Support;
        var riassunto = string.IsNullOrWhiteSpace(nota) ? "Nessuna descrizione fornita." : nota;

        if (!string.IsNullOrWhiteSpace(supporto.UploadUrl))
        {
            var esito = await _support.UploadAsync(path, supporto.UploadUrl).ConfigureAwait(true);

            if (esito.Success)
            {
                // I rapporti si eliminano solo dopo un invio andato a buon fine: una
                // pulizia anticipata butterebbe via l'unica traccia del guasto.
                var eliminati = _crashes.Clear();
                Compose();

                return eliminati > 0
                    ? $"{esito.Message}. I {eliminati} rapporti di errore sono stati archiviati"
                    : esito.Message;
            }

            _support.RevealInExplorer(path);
            return esito.Message;
        }

        if (!string.IsNullOrWhiteSpace(supporto.Email))
        {
            return _support.PrepareEmail(path, supporto.Email, riassunto).Message;
        }

        _support.RevealInExplorer(path);

        return "Nessun recapito configurato: il pacchetto e' sul desktop e la cartella si e' aperta. "
               + "Allegalo a un messaggio per l'assistenza";
    }

    private void Report(string message)
    {
        StatusText.Text = message;
        _announcer.Announce(Announcement.CommandResponse(message, DateTimeOffset.Now));
    }
}
