using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace BlindPhone.App;

/// <summary>
/// Aggiorna le regioni dinamiche notificando davvero la tecnologia assistiva.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto che questa classe corregge.</b> Dichiarare
/// <c>AutomationProperties.LiveSetting</c> in XAML non basta: WPF <b>non</b> solleva
/// automaticamente l'evento <c>LiveRegionChanged</c> quando cambia il testo di un
/// <see cref="TextBlock"/>. La proprietà dichiara l'intenzione, l'evento la
/// realizza. Senza l'evento la regione resta muta, e il difetto è particolarmente
/// insidioso perché a schermo tutto funziona: chi sviluppa vede il testo
/// aggiornarsi e considera l'informazione trasmessa.
/// </para>
/// <para>
/// Vale per ogni regione dinamica dell'applicazione: contatore della coda, stato
/// della chiamata attiva, conteggio dei risultati di ricerca, esito delle
/// operazioni nelle impostazioni. Sono esattamente le informazioni che cambiano da
/// sole, cioè quelle che un operatore non vedente non può andare a rileggere
/// perché non sa che sono cambiate.
/// </para>
/// <para>
/// <b>Perché non si usa sempre RaiseNotificationEvent.</b> La notifica è adatta agli
/// eventi puntuali ("chiamata da Mario Rossi"); la regione dinamica è adatta ai
/// valori che permangono e che l'operatore può rileggere spostandoci sopra il
/// cursore virtuale. Servono entrambi i meccanismi, per scopi diversi.
/// </para>
/// </remarks>
public static class LiveRegion
{
    /// <summary>
    /// Imposta il testo di una regione dinamica e ne notifica il cambiamento.
    /// </summary>
    /// <param name="target">Elemento da aggiornare.</param>
    /// <param name="text">Nuovo testo.</param>
    /// <remarks>
    /// Se il testo non cambia non viene sollevato alcun evento: ripetere lo stesso
    /// valore farebbe parlare lo screen reader senza che sia successo nulla, e su
    /// una regione che si aggiorna a ogni secondo diventerebbe rumore continuo.
    /// </remarks>
    public static void Update(TextBlock target, string text)
    {
        if (target.Text == text) return;

        target.Text = text;
        Notify(target);
    }

    /// <summary>
    /// Imposta il testo di una casella di sola lettura usata come regione dinamica.
    /// </summary>
    /// <param name="target">Casella da aggiornare.</param>
    /// <param name="text">Nuovo testo.</param>
    public static void Update(TextBox target, string text)
    {
        if (target.Text == text) return;

        target.Text = text;
        Notify(target);
    }

    /// <summary>
    /// Solleva l'evento di cambiamento della regione dinamica.
    /// </summary>
    /// <param name="element">Elemento interessato.</param>
    /// <remarks>
    /// Il peer di automazione può non esistere ancora se l'elemento non è stato
    /// realizzato: in quel caso non c'è nulla da notificare, e la lettura avverrà
    /// comunque quando la tecnologia assistiva esplorerà l'albero.
    /// </remarks>
    private static void Notify(System.Windows.UIElement element)
    {
        var peer = UIElementAutomationPeer.FromElement(element)
                   ?? UIElementAutomationPeer.CreatePeerForElement(element);

        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
