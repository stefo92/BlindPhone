using System.Windows;
using System.Windows.Input;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Infrastructure;

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
/// <para>
/// <b>Le scorciatoie globali sono sospese finché la finestra resta aperta.</b> Una
/// combinazione registrata presso Windows non arriva alla finestra in primo piano:
/// Windows la consegna a chi l'ha chiesta. Senza sospensione Control Maiusc F2, che
/// è la risposta globale, non si potrebbe catturare per spostarla: premendolo si
/// risponderebbe alla chiamata. La sospensione la prende e la rilascia la finestra
/// stessa, così quello che dice all'operatore è vero per costruzione.
/// </para>
/// <para>
/// <b>Le combinazioni degli altri programmi restano fuori portata</b>, e non c'è modo
/// di prenderle senza un aggancio di tastiera di basso livello, che BlindPhone
/// esclude per non interferire con gli screen reader. Quello che la finestra può fare
/// è accorgersene: di un tasto preso altrove dovrebbe arrivare il rilascio ma non la
/// pressione. Senza questo controllo l'area resta muta, e l'ultima cosa sentita è
/// «Control da solo non è una combinazione», che manda a cercare l'errore nel posto
/// sbagliato. Se in qualche configurazione nemmeno il rilascio arriva, resta
/// l'istruzione a schermo, che dice cosa significa una pressione non riletta.
/// </para>
/// </remarks>
public partial class HotkeyCaptureWindow : Window
{
    private readonly IAnnouncer _announcer;

    /// <summary>Pressioni e rilasci visti dall'area, per riconoscere le combinazioni prese altrove.</summary>
    private readonly PressioniMancate _missed = new();

    private IDisposable? _suspension;

    /// <summary>Crea la finestra di cattura.</summary>
    /// <param name="commandLabel">Etichetta del comando da riassegnare.</param>
    /// <param name="scope">Ambito dell'assegnazione.</param>
    /// <param name="current">Combinazione attualmente assegnata, se presente.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="globalHotkeys">
    /// Registratore delle scorciatoie globali, da sospendere mentre la finestra è
    /// aperta. Senza, le combinazioni globali di BlindPhone non si possono catturare.
    /// </param>
    public HotkeyCaptureWindow(
        string commandLabel,
        HotkeyScope scope,
        HotkeyBinding? current,
        IAnnouncer announcer,
        GlobalHotkeyRegistrar? globalHotkeys = null)
    {
        _announcer = announcer;

        InitializeComponent();

        Closed += (_, _) =>
        {
            _suspension?.Dispose();
            _suspension = null;
        };

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
            _suspension = globalHotkeys?.Suspend();

            // Keyboard.IsKeyDown rifiuta i valori oltre OemClear, come DeadCharProcessed.
            // System e ImeProcessed stanno prima, ma sono segnaposto e non tasti: si
            // saltano anche loro.
            _missed.Opened(Enum.GetValues<Key>()
                .Where(key => key is > Key.None and <= Key.OemClear and not (Key.System or Key.ImeProcessed)
                              && Keyboard.IsKeyDown(key))
                .Select(key => HotkeyKeyNames.Normalize(key.ToString())));

            CaptureBox.Focus();

            var currentText = current is null
                ? "Nessuna combinazione assegnata finora"
                : $"Combinazione attuale {current.SpeechText}";

            var suspendedText = _suspension is null
                ? string.Empty
                : ". Le scorciatoie globali di BlindPhone sono sospese finche' la finestra resta aperta";

            _announcer.Announce(Announcement.CommandResponse(
                $"Assegnazione per {commandLabel}. {currentText}. Premi ora la combinazione desiderata, "
                + $"poi Tab per confermare{suspendedText}",
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
        // La normalizzazione riporta a Pausa il tasto che con Control arriva come
        // Cancel: senza, Control Alt Pausa veniva salvata sotto un nome che la
        // finestra principale non cerca mai, e il comando non partiva.
        var keyName = KeyName(e);

        if (!HotkeyKeyNames.IsAssignable(keyName, out var reason))
        {
            // Non e' un errore dell'operatore: sta tenendo premuto un modificatore e
            // non ha ancora aggiunto il tasto. Si dice cosa manca, senza toni di
            // rifiuto e senza cancellare quello che aveva gia' catturato.
            LiveRegion.Update(HintText, reason);
            return;
        }

        _missed.Pressed(keyName);

        CapturedKey = keyName;
        CapturedModifiers = TastiModificatori.DaWpf(Keyboard.Modifiers);

        // Il comando e l'ambito non contano qui: serve solo comporre il testo della
        // combinazione, che dipende da tasto e modificatori. Il legame con il comando
        // vero lo fa il pannello, dopo la verifica dei conflitti.
        var binding = new HotkeyBinding(HotkeyCommand.Answer, keyName, CapturedModifiers, HotkeyScope.Local);
        CaptureBox.Text = binding.DisplayText;

        LiveRegion.Update(HintText, "Premi Tab per uscire dall'area e confermare con Invio.");

        _announcer.Announce(Announcement.CommandResponse(
            $"Rilevato {binding.SpeechText}", DateTimeOffset.Now));
    }

    /// <summary>
    /// Riconosce una combinazione premuta che non è mai arrivata all'area di cattura.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La regola sta in <see cref="PressioniMancate"/>. Qui si aggiunge solo che la
    /// combinazione si può nominare per intero, perché quando si rilascia il tasto i
    /// modificatori sono ancora giù, e che i rilasci dei soli modificatori non contano:
    /// quelli arrivano sempre, anche per le combinazioni prese altrove.
    /// </para>
    /// </remarks>
    private void OnCapturePreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Tab) return;

        var keyName = KeyName(e);

        if (!HotkeyKeyNames.IsAssignable(keyName, out _)) return;
        if (!_missed.ReleasedWithoutPress(keyName)) return;

        e.Handled = true;

        var missed = new HotkeyBinding(
            HotkeyCommand.Answer, keyName, TastiModificatori.DaWpf(Keyboard.Modifiers), HotkeyScope.Local);

        var message = $"{missed.SpeechText} non e' arrivata a BlindPhone: la tiene Windows, lo screen reader "
                      + "o un altro programma. Scegline un'altra";

        LiveRegion.Update(HintText, message + ".");
        _announcer.Announce(Announcement.CommandResponse(message, DateTimeOffset.Now));
    }

    /// <summary>Nome del tasto di un evento, con il tasto vero al posto di System e Pausa normalizzata.</summary>
    private static string KeyName(KeyEventArgs e)
        => HotkeyKeyNames.Normalize(HotkeyKeyNames.ResolveSystemKey(e.Key.ToString(), e.SystemKey.ToString()));

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
}
