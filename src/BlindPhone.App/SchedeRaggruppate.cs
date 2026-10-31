using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace BlindPhone.App;

/// <summary>
/// Titolo del gruppo a cui appartiene una scheda dell'elenco laterale.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché una proprietà associata, e sul primo elemento del gruppo.</b> Le schede
/// delle impostazioni sono undici e stanno in un elenco verticale a sinistra; divise in
/// cinque gruppi — Collegamento, Dispositivi, Lavoro, Voce e tastiera, Assistenza — si
/// trovano a colpo d'occhio invece di scorrerle tutte. Ma il titolo di un gruppo non
/// può essere un elemento dell'elenco: in un <see cref="TabControl"/> ogni elemento
/// diventa una scheda, cioè una tappa di Control Tab e una voce «scheda 3 di 16» per
/// lo screen reader. Sarebbero cinque fermate finte in un giro che l'operatore fa
/// decine di volte, e il conteggio delle schede smetterebbe di essere vero.
/// </para>
/// <para>
/// Il titolo è quindi un dato della scheda che apre il gruppo, e lo disegna il suo
/// modello (lo stile <c>VoceDiSchedaLaterale</c> in Temi\Stili.xaml) sopra la scheda
/// stessa. L'elenco resta fatto di sole schede vere, e Control Tab, Control Pagina giù
/// e il modello di automazione Tab e TabItem restano quelli di sempre.
/// </para>
/// <para>
/// Le alternative scartate: il raggruppamento delle raccolte di WPF, che fra le schede
/// infilerebbe dei <c>GroupItem</c> — contenitori che lo screen reader annuncia come
/// gruppi e che non sono schede — e un pannello disegnato accanto all'elenco, che
/// dovrebbe inseguire a mano l'altezza di ogni scheda e si scollerebbe al primo testo
/// che va a capo.
/// </para>
/// </remarks>
public static class GruppoDiSchede
{
    /// <summary>Titolo del gruppo che comincia con questa scheda, o <c>null</c>.</summary>
    public static readonly DependencyProperty TitoloProperty =
        DependencyProperty.RegisterAttached(
            "Titolo",
            typeof(string),
            typeof(GruppoDiSchede),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Dichiara che la scheda apre un gruppo con questo titolo.</summary>
    /// <param name="element">Scheda interessata.</param>
    /// <param name="value">Titolo del gruppo, o <c>null</c> se la scheda non ne apre uno.</param>
    public static void SetTitolo(DependencyObject element, string? value)
        => element.SetValue(TitoloProperty, value);

    /// <summary>Titolo del gruppo che comincia con questa scheda, o <c>null</c>.</summary>
    /// <param name="element">Scheda interessata.</param>
    public static string? GetTitolo(DependencyObject element)
        => (string?)element.GetValue(TitoloProperty);
}

/// <summary>
/// Testo che si vede e che lo screen reader non legge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serve ai titoli dei gruppi di schede, ed è l'unico caso.</b> Quel titolo dice a
/// chi guarda dove finisce un gruppo e ne comincia un altro. A chi ascolta non
/// direbbe niente che non sappia già, e costerebbe una lettura in più: un testo dentro
/// la scheda finisce fra i figli dell'elemento «scheda» nell'albero di accessibilità,
/// e a seconda dello screen reader e della modalità di lettura verrebbe pronunciato
/// accanto al nome della scheda, solo su quella che apre il gruppo.
/// </para>
/// <para>
/// Un <see cref="TextBlock"/> dentro un modello già è escluso dalla vista «controlli»
/// di UI Automation, ma resta in quella «contenuto», che è quella che alcuni lettori
/// percorrono in modalità di esplorazione. Senza un <see cref="AutomationPeer"/> invece
/// l'elemento non c'è in nessuna vista: è lo stesso trattamento che WPF riserva a
/// <see cref="Border"/> e ai pannelli, che si vedono e non si leggono.
/// </para>
/// <para>
/// Non va usato per un testo che porta un'informazione. Lo screen reader non è un
/// lettore di serie B: se una cosa serve a chi guarda e non a chi ascolta, deve
/// esserci un motivo detto, come qui.
/// </para>
/// </remarks>
public sealed class TitoloDecorativo : TextBlock
{
    /// <summary>Nessun elemento di automazione: il testo resta solo a schermo.</summary>
    /// <returns>Sempre <c>null</c>.</returns>
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
