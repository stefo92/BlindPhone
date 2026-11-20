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
    /// <summary>Il titolo come è scritto, prima del maiuscolo.</summary>
    /// <remarks>
    /// <para>
    /// I titoli dei gruppi nell'illustrazione del sito sono in maiuscolo, piccoli e
    /// attenuati. WPF non ha un equivalente di <c>text-transform</c>, e scrivere i
    /// titoli in maiuscolo nella finestra delle impostazioni vorrebbe dire scriverli
    /// in un modo diverso da come compaiono nella guida e negli annunci. Il modello
    /// lega qui il titolo, e <see cref="TextBlock.Text"/> riceve quello da disegnare.
    /// </para>
    /// <para>
    /// UNA PROPRIETÀ SUA E NON UNA COERCIZIONE DI <c>Text</c>. Con la coercizione il
    /// titolo spariva: dentro un modello il valore legato arriva dopo, e la coercizione
    /// chiamata al cambio di <see cref="Maiuscolo"/> lo trovava ancora vuoto e lo
    /// fissava vuoto.
    /// </para>
    /// <para>
    /// Il maiuscolo è in italiano, non quello della lingua del sistema: un titolo con
    /// la «i» sarebbe diventato «İ» su una postazione configurata in turco.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty TestoProperty =
        DependencyProperty.Register(
            nameof(Testo),
            typeof(string),
            typeof(TitoloDecorativo),
            new PropertyMetadata(null, (d, _) => ((TitoloDecorativo)d).Aggiorna()));

    /// <summary>Se il titolo si disegna in maiuscolo.</summary>
    public static readonly DependencyProperty MaiuscoloProperty =
        DependencyProperty.Register(
            nameof(Maiuscolo),
            typeof(bool),
            typeof(TitoloDecorativo),
            new PropertyMetadata(false, (d, _) => ((TitoloDecorativo)d).Aggiorna()));

    private static readonly System.Globalization.CultureInfo Italiano =
        System.Globalization.CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Il titolo come è scritto, prima del maiuscolo.</summary>
    public string? Testo
    {
        get => (string?)GetValue(TestoProperty);
        set => SetValue(TestoProperty, value);
    }

    /// <summary>Se il titolo si disegna in maiuscolo.</summary>
    public bool Maiuscolo
    {
        get => (bool)GetValue(MaiuscoloProperty);
        set => SetValue(MaiuscoloProperty, value);
    }

    private void Aggiorna()
    {
        var testo = Testo ?? string.Empty;
        Text = Maiuscolo ? testo.ToUpper(Italiano) : testo;
    }

    /// <summary>Nessun elemento di automazione: il testo resta solo a schermo.</summary>
    /// <returns>Sempre <c>null</c>.</returns>
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
