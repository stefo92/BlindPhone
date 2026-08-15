using System.Drawing;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.App;

/// <summary>
/// Icona di BlindPhone nell'area di notifica di Windows.
/// </summary>
/// <remarks>
/// <para>
/// Serve a tenere il posto operatore in servizio con la finestra chiusa. Non è una
/// comodità estetica: chiudere la finestra di un softphone significa smettere di
/// ricevere chiamate, e su un centralino questo si traduce in chiamanti che squillano
/// a vuoto senza che nessuno lo sappia. Con l'icona, la chiusura diventa un "metti
/// via", e la registrazione al centralino resta attiva.
/// </para>
/// <para>
/// <b>Perché ha anche un menu.</b> L'area di notifica è raggiungibile da tastiera con
/// Windows più B, e da lì il menu contestuale si apre con il tasto applicazione o con
/// Maiusc F10: è la via di ritorno che funziona anche quando la scorciatoia globale
/// di riapertura risulta occupata da un altro programma. Le due voci sono deliberate
/// e sono solo due, perché un menu contestuale letto a voce si percorre una voce alla
/// volta.
/// </para>
/// <para>
/// L'unico tipo di Windows Forms usato nel prodotto vive qui, e non esce da questa
/// classe: gli eventi esposti sono eventi .NET normali, e chi li ascolta non sa da
/// dove arrivino.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly ILogger<TrayIcon> _log;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _openItem;

    private bool _disposed;

    /// <summary>Crea l'icona e la mostra nell'area di notifica.</summary>
    /// <param name="logger">Log diagnostico.</param>
    public TrayIcon(ILogger<TrayIcon>? logger = null)
    {
        _log = logger ?? NullLogger<TrayIcon>.Instance;

        _openItem = new System.Windows.Forms.ToolStripMenuItem("&Apri BlindPhone");
        _openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        // La voce di apertura e' in grassetto perche' e' l'azione predefinita, cioe'
        // quella che scatta con il doppio clic e con Invio sul menu.
        var font = _openItem.Font;
        _openItem.Font = new Font(font, FontStyle.Bold);

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("&Esci da BlindPhone");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "BlindPhone",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sollevato quando l'operatore chiede di riaprire la finestra.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Sollevato quando l'operatore chiede di uscire davvero.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Aggiorna il testo mostrato passando sopra l'icona.
    /// </summary>
    /// <param name="text">Testo di stato, al massimo 63 caratteri.</param>
    /// <remarks>
    /// Windows tronca oltre i 63 caratteri: si taglia qui, perché un testo troncato
    /// dalla shell perde la parte finale senza avvisare, e la parte finale è quella
    /// che porta lo stato.
    /// </remarks>
    public void UpdateStatus(string text)
    {
        if (_disposed) return;

        try
        {
            _icon.Text = text.Length <= 63 ? text : text[..60] + "...";
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Aggiornamento del testo dell'icona non riuscito");
        }
    }

    /// <summary>
    /// Carica l'icona dell'applicazione.
    /// </summary>
    /// <remarks>
    /// Si estrae dall'eseguibile invece di aggiungere una risorsa: così l'icona
    /// dell'area di notifica non può divergere da quella della barra delle
    /// applicazioni e dei collegamenti creati dall'installatore. Se l'estrazione non
    /// riesce si usa l'icona di sistema: un'icona generica è comunque meglio di
    /// nessuna icona, perché senza icona non c'è più modo di riaprire la finestra
    /// con il mouse.
    /// </remarks>
    private Icon LoadIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
            {
                if (Icon.ExtractAssociatedIcon(executable) is { } icon) return icon;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Icona dell'eseguibile non estraibile: si usa quella di sistema");
        }

        return SystemIcons.Application;
    }

    /// <inheritdoc />
    /// <remarks>
    /// L'icona va tolta esplicitamente: senza, resta nell'area di notifica dopo la
    /// chiusura del programma finché qualcuno non ci passa sopra il mouse. Su una
    /// postazione dove l'operatore non vede, quell'icona fantasma resterebbe lì per
    /// sempre.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _openItem.Font?.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Rimozione dell'icona dall'area di notifica non riuscita");
        }
    }
}
