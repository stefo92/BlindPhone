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

        DisableInstallIfUnverifiable();

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
        if (!_updates.IsSecureChannel)
        {
            return "Il server degli aggiornamenti usa un canale NON cifrato: l'aggiornamento non viene "
                   + "scaricato. Chiedi a chi amministra il server di abilitare FTPS.";
        }

        if (!UpdateSignature.IsConfigured)
        {
            return "Questa versione di BlindPhone non puo' verificare la firma degli aggiornamenti, quindi "
                   + "non li installa da sola. L'aggiornamento va fatto a mano da chi amministra la postazione.";
        }

        var impronta = _notes.Sha256 is null
            ? "Le note non dichiarano l'impronta del pacchetto: serve la riga SHA256 nel file delle novita'."
            : "L'integrita' del pacchetto sara' verificata anche con l'impronta dichiarata nelle note.";

        return "Il pacchetto arriva da un canale cifrato e la sua firma viene verificata prima "
               + $"dell'installazione. {impronta}";
    }

    /// <summary>
    /// Toglie il pulsante di installazione quando l'aggiornamento non è installabile.
    /// </summary>
    /// <remarks>
    /// Un pulsante che c'è e fallisce sempre è peggio di un pulsante che non c'è: per
    /// chi non vede lo schermo, premerlo e sentire un errore è l'unico modo di
    /// scoprire che non era disponibile. Il perché sta già in
    /// <see cref="DescribeChannel"/>, che viene letto ad alta voce.
    /// </remarks>
    private void DisableInstallIfUnverifiable()
    {
        if (_updates.IsSecureChannel && UpdateSignature.IsConfigured) return;

        InstallButton.IsEnabled = false;
        InstallButton.ToolTip = "L'aggiornamento non puo' essere verificato su questa postazione";
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
    /// <b>Fra la verifica e l'avvio non c'è una finestra da sfruttare.</b> Prima
    /// c'era: il pacchetto restava in <c>%TEMP%</c> — cartella su cui l'utente ha
    /// pieno controllo — senza che nessuno lo trattenesse, e fra il controllo
    /// dell'impronta e l'avvio di <c>msiexec</c> un altro processo poteva
    /// sostituirlo. Quello che partiva era l'altro, con i privilegi di
    /// amministratore. Ora il file viene aperto in modo da negarne la scrittura e la
    /// cancellazione, la firma si riverifica <b>su quel flusso</b> e il blocco resta
    /// fino a quando <c>msiexec</c> non ha preso in mano il pacchetto.
    /// </para>
    /// <para>
    /// L'installazione <b>non</b> è silenziosa. L'operatore vede la stessa procedura
    /// della prima installazione, con la richiesta di consenso di Windows: è quello che
    /// permette di fermarsi a metà se qualcosa non torna, ed è l'ultima occasione in
    /// cui un pacchetto sbagliato può ancora essere rifiutato.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Il pacchetto non è più quello verificato, oppure la sua firma non si trova:
    /// l'installazione non parte e il motivo viene annunciato.
    /// </exception>
    private void LaunchInstaller(string packagePath)
    {
        // La lettura resta concessa: msiexec deve poter aprire il pacchetto. Quello
        // che il blocco nega e' scriverlo e cancellarlo.
        using var blocco = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var firma = ReadSignature(packagePath);

        // Si verificano i byte TENUTI APERTI, non quelli che il percorso
        // restituirebbe riaprendolo: riaprire il file vanificherebbe il blocco,
        // perche' fra le due aperture ci sarebbe di nuovo spazio per una sostituzione.
        if (!UpdateSignature.Verify(blocco, firma))
        {
            _log.LogError(
                "Firma del pacchetto {Pacchetto} non valida al momento dell'installazione: annullata",
                packagePath);

            throw new InvalidOperationException(
                "Il pacchetto e' cambiato dopo la verifica: l'installazione e' stata annullata. "
                + "Riprova, e se il problema resta avvisa chi amministra la postazione.");
        }

        var avvio = new ProcessStartInfo
        {
            // PERCORSO ASSOLUTO, E NON "msiexec.exe".
            //
            // Con il solo nome, la risoluzione passa per le regole di ricerca di
            // Windows: la cartella di lavoro e le voci di PATH vengono guardate, e
            // una cartella scrivibile dall'utente che contenga un file con quel nome
            // vincerebbe sul programma di sistema. Si chiede quello in
            // %SystemRoot%\System32, che e' l'unico che si intende avviare.
            FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            Arguments = $"/i \"{packagePath}\"",
            UseShellExecute = true,
        };

        _log.LogInformation("Avvio dell'installazione di {Pacchetto}", packagePath);

        using var processo = Process.Start(avvio);

        // Si attende che msiexec sia pronto, così il blocco cade quando il pacchetto
        // è già nelle sue mani. Non tutte le configurazioni hanno una finestra da
        // attendere — un'installazione senza interfaccia non ne ha — e un'attesa
        // impossibile non è un guasto: il pacchetto è stato verificato un istante
        // prima e il processo è già partito.
        //
        // L'attesa e' breve di proposito. Blocca il thread dell'interfaccia, cioe'
        // anche la coda degli annunci, e su un posto operatore un programma muto per
        // dieci secondi e' indistinguibile da un programma bloccato. msiexec apre il
        // pacchetto nei primi istanti, molto prima di mostrare la sua prima finestra:
        // cinque secondi sono un tetto, non un'attesa prevista.
        try
        {
            processo?.WaitForInputIdle(5_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.LogDebug(ex, "Attesa dell'avvio di msiexec non disponibile: si prosegue");
        }

        DialogResult = true;
        Close();

        // La chiusura dell'applicazione passa dal percorso normale: le chiamate in
        // corso vengono chiuse come si deve e la registrazione SIP viene ritirata.
        Application.Current.Shutdown();
    }

    /// <summary>Legge la firma pubblicata accanto al pacchetto scaricato.</summary>
    /// <exception cref="InvalidOperationException">La firma non è leggibile.</exception>
    private byte[] ReadSignature(string packagePath)
    {
        var percorso = packagePath + UpdateSignature.SignatureExtension;

        try
        {
            return File.ReadAllBytes(percorso);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Firma {Percorso} non leggibile al momento dell'installazione", percorso);

            throw new InvalidOperationException(
                "La firma del pacchetto non si trova piu': l'installazione non parte. "
                + "Riprova a scaricare l'aggiornamento.",
                ex);
        }
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        DialogResult = false;
        Close();
    }
}
