using System.Windows;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Le finestre dei messaggi: una per conversazione, più l'elenco delle conversazioni.
/// </summary>
/// <remarks>
/// <para>
/// <b>Una finestra per collega, e una sola.</b> È la stessa regola di
/// <see cref="FinestreNonModali"/>, applicata alla conversazione invece che al tipo:
/// scrivere due volte allo stesso interno riporta avanti la finestra che c'è già, con la
/// sua storia, invece di aprirne una seconda con lo stesso titolo. Due finestre con lo
/// stesso nome in ALT+TAB sono il caso in cui chi non vede non sa più dove ha scritto.
/// </para>
/// <para>
/// <b>Tutte di proprietà della finestra principale</b>, e non l'una dell'altra: chiudendo
/// l'elenco delle conversazioni restano aperte quelle avviate da lì, e chiudendo la
/// postazione si chiudono tutte.
/// </para>
/// <para>
/// Vive sul thread dell'interfaccia, tranne <see cref="InstantMessagingService.VisibilityOf"/>,
/// che il servizio chiama dal thread su cui arriva il messaggio e che qui passa al
/// dispatcher prima di guardare le finestre.
/// </para>
/// </remarks>
public sealed class FinestreDeiMessaggi
{
    private readonly Dictionary<string, ChatWindow> _aperte = new(StringComparer.OrdinalIgnoreCase);
    private readonly InstantMessagingService _messaggi;
    private readonly IContactRepository _rubrica;
    private readonly IAnnouncer _annunci;
    private readonly HotkeyProfile _tasti;
    private readonly SettingsService _impostazioni;
    private readonly ILogger<ChatWindow> _log;

    /// <summary>Crea il gestore e lo presenta al servizio dei messaggi.</summary>
    /// <param name="messaggi">Conversazioni.</param>
    /// <param name="rubrica">Rubrica, per scrivere a un collega cercandolo per nome.</param>
    /// <param name="annunci">Pipeline di annuncio.</param>
    /// <param name="tasti">Profilo dei tasti rapidi, per nominarli.</param>
    /// <param name="impostazioni">Configurazione, letta al momento per il raggruppamento delle cifre.</param>
    /// <param name="log">Log diagnostico.</param>
    public FinestreDeiMessaggi(
        InstantMessagingService messaggi,
        IContactRepository rubrica,
        IAnnouncer annunci,
        HotkeyProfile tasti,
        SettingsService impostazioni,
        ILogger<ChatWindow> log)
    {
        _messaggi = messaggi;
        _rubrica = rubrica;
        _annunci = annunci;
        _tasti = tasti;
        _impostazioni = impostazioni;
        _log = log;

        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        _messaggi.VisibilityOf = interno =>
        {
            try
            {
                return dispatcher.CheckAccess() ? VisibilityOf(interno) : dispatcher.Invoke(() => VisibilityOf(interno));
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or InvalidOperationException)
            {
                // L'applicazione si sta chiudendo: nessuna finestra da guardare.
                return ConversationVisibility.Closed;
            }
        };
    }

    /// <summary>Finestra a cui appartengono le finestre dei messaggi: la principale.</summary>
    public Window? Owner { get; set; }

    /// <summary>
    /// Come chiamare un interno: lo decide la finestra principale, che sa se c'è un
    /// trasferimento in corso o qualcuno in linea.
    /// </summary>
    public Func<string, Task>? Dial { get; set; }

    /// <summary>
    /// Apre la conversazione con un interno, o riporta avanti la sua finestra.
    /// </summary>
    /// <param name="interno">Interno o numero.</param>
    /// <param name="nome">Nome del collega, se chi apre lo conosce.</param>
    /// <returns>La finestra della conversazione.</returns>
    public ChatWindow? Show(string interno, string? nome = null)
    {
        try
        {
            var conversazione = _messaggi.Open(interno, nome);

            if (_aperte.TryGetValue(conversazione.Peer, out var esistente))
            {
                // Ridotta a icona non si attiva da sola: resterebbe una finestra che
                // risponde ai tasti e non si vede da nessuna parte.
                if (esistente.WindowState == WindowState.Minimized) esistente.WindowState = WindowState.Normal;

                esistente.Activate();
                esistente.FocusCompose();
                return esistente;
            }

            var finestra = new ChatWindow(
                _messaggi,
                conversazione,
                _annunci,
                _tasti,
                () => _impostazioni.Current.Accessibility.DigitGrouping,
                ChiamaDallaFinestraAsync,
                _log);

            if (Owner is { IsLoaded: true } proprietaria) finestra.Owner = proprietaria;

            // Si dimentica alla chiusura: riattivare una finestra chiusa solleverebbe
            // un'eccezione alla pressione successiva.
            finestra.Closed += (_, _) => _aperte.Remove(conversazione.Peer);

            _aperte[conversazione.Peer] = finestra;
            finestra.Show();

            return finestra;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura della conversazione con {Interno} non riuscita", interno);
            _annunci.Announce(Announcement.CommandResponse(
                "Impossibile aprire la conversazione", DateTimeOffset.Now));

            return null;
        }
    }

    /// <summary>Apre l'elenco delle conversazioni, o lo riporta avanti.</summary>
    public void ShowList()
    {
        try
        {
            FinestreNonModali.MostraOAttiva(
                () => new MessagesWindow(_messaggi, this, _rubrica, _annunci, _tasti, _impostazioni),
                Owner ?? Application.Current.MainWindow!);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apertura dell'elenco dei messaggi non riuscita");
            _annunci.Announce(Announcement.CommandResponse(
                "Impossibile aprire i messaggi", DateTimeOffset.Now));
        }
    }

    /// <summary>
    /// Chiama un interno da una finestra dei messaggi, e riporta avanti la postazione.
    /// </summary>
    /// <remarks>
    /// La postazione torna in primo piano perché è lì che vivono i tasti della
    /// chiamata: riagganciare, mettere in attesa, trasferire. Restando nella finestra
    /// della conversazione, l'operatore si troverebbe in linea senza nessuno di quei tasti
    /// a disposizione.
    /// </remarks>
    public async Task ChiamaDallaFinestraAsync(string interno)
    {
        if (Dial is null) return;

        Owner?.Activate();
        await Dial(interno).ConfigureAwait(true);
    }

    /// <summary>Dove sta la finestra di una conversazione. Va chiamata sul thread dell'interfaccia.</summary>
    /// <param name="interno">Interno della conversazione.</param>
    public ConversationVisibility VisibilityOf(string interno)
    {
        var chiave = InstantMessagingService.NormalizePeer(interno);

        if (!_aperte.TryGetValue(chiave, out var finestra)) return ConversationVisibility.Closed;

        return finestra.IsActive && finestra.WindowState != WindowState.Minimized
            ? ConversationVisibility.InFront
            : ConversationVisibility.Background;
    }
}
