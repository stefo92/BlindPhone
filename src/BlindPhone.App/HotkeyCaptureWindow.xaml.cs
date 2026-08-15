using System.Windows;
using System.Windows.Input;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;

namespace BlindPhone.App;

/// <summary>
/// Finestra di cattura di una combinazione di tasti.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché una finestra e non un campo nella tabella.</b> Catturare la
/// combinazione dentro la tabella dei comandi significherebbe che ogni tasto premuto
/// mentre si scorre l'elenco viene interpretato come assegnazione: le frecce con cui
/// si naviga diventerebbero la scorciatoia del comando su cui ci si trova. Una
/// finestra separata rende esplicito quando la cattura è attiva, e lo dice
/// all'apertura.
/// </para>
/// <para>
/// <b>Il caso Alt.</b> Con Alt premuto, WPF non consegna il tasto in
/// <c>e.Key</c> ma in <c>e.SystemKey</c>, lasciando nel primo il valore
/// <c>Key.System</c>. Chi lo ignora ottiene che tutte le combinazioni con Alt
/// risultano identiche fra loro, e per giunta non assegnabili. È un difetto che a
/// schermo non si vede: la finestra si apre, si preme, e viene registrata la
/// combinazione sbagliata.
/// </para>
/// </remarks>
public partial class HotkeyCaptureWindow : Window
{
    private readonly IAnnouncer _announcer;

    /// <summary>Crea la finestra di cattura.</summary>
    /// <param name="commandLabel">Etichetta del comando da riassegnare.</param>
    /// <param name="scope">Ambito dell'assegnazione.</param>
    /// <param name="current">Combinazione attualmente assegnata, se presente.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    public HotkeyCaptureWindow(
        string commandLabel,
        HotkeyScope scope,
        HotkeyBinding? current,
        IAnnouncer announcer)
    {
        _announcer = announcer;

        InitializeComponent();

        var scopeLabel = scope == HotkeyScope.Global
            ? "combinazione globale, attiva anche con BlindPhone in secondo piano"
            : "combinazione locale, attiva con BlindPhone in primo piano";

        CommandText.Text = $"{commandLabel} - {scopeLabel}";

        if (current is not null)
        {
            CapturedKey = current.Key;
            CapturedModifiers = current.Modifiers;
            CaptureBox.Text = current.DisplayText;
        }

        Loaded += (_, _) =>
        {
            CaptureBox.Focus();

            var currentText = current is null
                ? "Nessuna combinazione assegnata finora"
                : $"Combinazione attuale {current.SpeechText}";

            _announcer.Announce(Announcement.CommandResponse(
                $"Assegnazione per {commandLabel}. {currentText}. Premi ora la combinazione desiderata, "
                + "poi Tab per confermare",
                DateTimeOffset.Now));
        };
    }

    /// <summary>Tasto catturato, oppure <c>null</c> se non è stato premuto nulla.</summary>
    public string? CapturedKey { get; private set; }

    /// <summary>Modificatori catturati.</summary>
    public HotkeyModifiers CapturedModifiers { get; private set; }

    private void OnCapturePreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Tab resta l'unica via d'uscita dall'area: non va intercettato, altrimenti
        // il focus non puo' piu' raggiungere i pulsanti.
        if (e.Key is Key.Tab) return;

        e.Handled = true;

        // Con Alt premuto il tasto vero sta in SystemKey: vedi le note della classe.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var keyName = key.ToString();

        if (!HotkeyKeyNames.IsAssignable(keyName, out var reason))
        {
            // Non e' un errore dell'operatore: sta tenendo premuto un modificatore e
            // non ha ancora aggiunto il tasto. Si dice cosa manca, senza toni di
            // rifiuto e senza cancellare quello che aveva gia' catturato.
            LiveRegion.Update(HintText, reason);
            return;
        }

        CapturedKey = keyName;
        CapturedModifiers = ToHotkeyModifiers(Keyboard.Modifiers);

        // Il comando e l'ambito non contano qui: serve solo comporre il testo della
        // combinazione, che dipende da tasto e modificatori. Il legame con il comando
        // vero lo fa il pannello, dopo la verifica dei conflitti.
        var binding = new HotkeyBinding(HotkeyCommand.Answer, keyName, CapturedModifiers, HotkeyScope.Local);
        CaptureBox.Text = binding.DisplayText;

        LiveRegion.Update(HintText, "Premi Tab per uscire dall'area e confermare con Invio.");

        _announcer.Announce(Announcement.CommandResponse(
            $"Rilevato {binding.SpeechText}", DateTimeOffset.Now));
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CapturedKey))
        {
            LiveRegion.Update(HintText, "Nessuna combinazione rilevata: premila nell'area di cattura.");
            _announcer.Announce(Announcement.CommandResponse(
                "Nessuna combinazione rilevata. Torna nell'area di cattura con Maiusc Tab e premi i tasti",
                DateTimeOffset.Now));
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static HotkeyModifiers ToHotkeyModifiers(ModifierKeys keys)
    {
        var result = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (keys.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (keys.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Windows;
        return result;
    }
}
