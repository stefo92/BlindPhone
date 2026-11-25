using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Scarica un aggiornamento mentre l'operatore continua a lavorare, per installarlo al
/// prossimo avvio.
/// </summary>
/// <remarks>
/// <para>
/// <b>È la strada di «Scarica e installa successivamente».</b> La finestra
/// dell'aggiornamento si chiude subito, e lo scaricamento prosegue qui: sessanta
/// megabyte su una linea lenta sono minuti, e tenere aperta una finestra modale per
/// tutto quel tempo vorrebbe dire non poter rispondere al telefono. Al termine il
/// pacchetto resta nella cartella degli scaricamenti con la sua firma e le sue novità,
/// e all'avvio successivo <see cref="UpdateService.FindDownloaded"/> lo ritrova.
/// </para>
/// <para>
/// <b>Uno per processo</b>, perché lo scaricamento sopravvive alla finestra che l'ha
/// chiesto: può partire dal pannello delle impostazioni, che poi si chiude. Due
/// scaricamenti dello stesso pacchetto scriverebbero lo stesso file a metà.
/// </para>
/// <para>
/// Si usa dal thread dell'interfaccia, e gli annunci arrivano su quello: non c'è altro
/// da sincronizzare.
/// </para>
/// </remarks>
public sealed class BackgroundUpdateDownloader
{
    private readonly IAnnouncer _announcer;
    private readonly ILogger<BackgroundUpdateDownloader> _log;

    private Task<string>? _download;
    private Version? _downloadingVersion;

    /// <summary>
    /// Qualcuno ha chiesto di installare subito il pacchetto che stava arrivando in
    /// sottofondo: l'annuncio «te lo propongo al prossimo avvio» sarebbe falso.
    /// </summary>
    private bool _joinedByInstallNow;

    /// <summary>Crea il servizio.</summary>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public BackgroundUpdateDownloader(IAnnouncer announcer, ILogger<BackgroundUpdateDownloader> logger)
    {
        _announcer = announcer;
        _log = logger;
    }

    /// <summary>Se uno scaricamento è in corso.</summary>
    public bool IsDownloading => _download is { IsCompleted: false };

    /// <summary>Se sta arrivando proprio questa versione.</summary>
    /// <param name="version">Versione da confrontare.</param>
    public bool IsDownloadingVersion(Version version) => IsDownloading && _downloadingVersion == version;

    /// <summary>
    /// Fa partire lo scaricamento in sottofondo e torna subito.
    /// </summary>
    /// <param name="updates">Servizio con cui scaricare, con il server già scelto.</param>
    /// <param name="update">Aggiornamento da scaricare.</param>
    /// <param name="notes">Le sue novità, con l'impronta da verificare.</param>
    /// <remarks>
    /// Annuncia l'inizio e la fine. La fine non interrompe: arriva quando arriva, magari
    /// in mezzo a una chiamata, e dice una cosa che può aspettare. Un fallimento non è
    /// un allarme: l'aggiornamento si ripropone al prossimo avvio come se l'operatore
    /// avesse premuto «Annulla».
    /// </remarks>
    public void Start(UpdateService updates, AvailableUpdate update, UpdateNotes notes)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(update);

        if (IsDownloading)
        {
            _announcer.Announce(Announcement.CommandResponse(
                _downloadingVersion == update.Version
                    ? "Lo scaricamento dell'aggiornamento e' gia' in corso: al termine te lo dico."
                    : "E' gia' in corso lo scaricamento di un altro aggiornamento: riprova quando e' finito.",
                DateTimeOffset.Now));
            return;
        }

        _joinedByInstallNow = false;

        _announcer.Announce(Announcement.CommandResponse(
            $"Scaricamento della versione {update.DisplayVersion} avviato in sottofondo: puoi continuare a lavorare. "
            + "Al prossimo avvio di BlindPhone ti chiedero' se installarla.",
            DateTimeOffset.Now));

        _ = RunAsync(Begin(updates, update, notes, progress: null), update);
    }

    /// <summary>
    /// Scarica subito, per installare subito.
    /// </summary>
    /// <param name="updates">Servizio con cui scaricare.</param>
    /// <param name="update">Aggiornamento da scaricare.</param>
    /// <param name="notes">Le sue novità.</param>
    /// <param name="progress">Percentuale di avanzamento.</param>
    /// <returns>Il percorso del pacchetto verificato.</returns>
    /// <remarks>
    /// Se la stessa versione sta già arrivando in sottofondo non si ricomincia: si
    /// aspetta quella, e l'avanzamento non si riceve. Ricominciare scriverebbe due volte
    /// lo stesso file.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Sta arrivando un'altra versione, oppure lo scaricamento non è riuscito.
    /// </exception>
    public Task<string> DownloadNowAsync(
        UpdateService updates, AvailableUpdate update, UpdateNotes notes, IProgress<int>? progress)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(update);

        if (IsDownloadingVersion(update.Version))
        {
            _joinedByInstallNow = true;
            return _download!;
        }

        if (IsDownloading)
        {
            throw new InvalidOperationException(
                "E' gia' in corso lo scaricamento di un altro aggiornamento: riprova quando e' finito.");
        }

        return Begin(updates, update, notes, progress);
    }

    private Task<string> Begin(
        UpdateService updates, AvailableUpdate update, UpdateNotes notes, IProgress<int>? progress)
    {
        _downloadingVersion = update.Version;
        _download = updates.DownloadAsync(update, notes, progress, CancellationToken.None);
        return _download;
    }

    private async Task RunAsync(Task<string> download, AvailableUpdate update)
    {
        try
        {
            var pacchetto = await download.ConfigureAwait(true);

            _log.LogInformation(
                "Aggiornamento {Versione} scaricato in sottofondo: {Pacchetto}", update.DisplayVersion, pacchetto);

            if (_joinedByInstallNow) return;

            // In coda, come le altre notizie sugli aggiornamenti: arriva quando arriva,
            // e non deve tagliare quello che si sta dicendo. Vedi Announcement.Sequential.
            _announcer.Announce(Announcement.Normal(
                $"Aggiornamento alla versione {update.DisplayVersion} scaricato e verificato. "
                + "Te lo propongo al prossimo avvio di BlindPhone; per installarlo prima, usa il pulsante "
                + "Aggiornamento disponibile nella barra dei comandi.",
                DateTimeOffset.Now,
                coalesceKey: "update:downloaded") with { Sequential = true });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scaricamento in sottofondo dell'aggiornamento {Versione} non riuscito", update.DisplayVersion);

            // Chi aveva chiesto di installare subito il fallimento lo sente dalla sua
            // finestra, che sta aspettando lo stesso scaricamento.
            if (_joinedByInstallNow) return;

            _announcer.Announce(Announcement.Normal(
                $"Scaricamento dell'aggiornamento non riuscito. {ex.Message} Te lo ripropongo al prossimo avvio.",
                DateTimeOffset.Now,
                coalesceKey: "update:downloaded") with { Sequential = true });
        }
    }
}
