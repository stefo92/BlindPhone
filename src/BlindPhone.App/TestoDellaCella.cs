using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BlindPhone.App;

/// <summary>
/// Il nome accessibile di una cella di tabella: il testo che mostra a schermo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto che chiude.</b> Quando una cella non ha un nome, WPF gliene inventa
/// uno: «Elemento: », il testo dell'intera riga, «, indice visualizzazione colonna: »
/// e il numero della colonna. Capita a ogni cella vuota e a ogni casella di spunta, in
/// tutte le tabelle, e quando la riga non ha un testo suo al posto del testo c'è il
/// nome del tipo: lo screen reader diceva «Elemento: BlindPhone.App.SipAccountsWindow
/// più AccountRow, indice visualizzazione colonna: 0» su una cella che a schermo non
/// mostrava niente. Misurato con l'automazione di WPF: vedi CelleLeggibiliTests.
/// </para>
/// <para>
/// Si applica a tutte le celle dallo stile del tema, in <c>Temi\Stili.xaml</c>. Gli
/// stili di cella delle singole finestre lo estendono e, dove dichiarano un nome loro
/// — il numero detto cifra per cifra, per esempio — vince il loro.
/// </para>
/// <list type="bullet">
///   <item><description>Testo: il testo, così com'è.</description></item>
///   <item><description>Testo vuoto: «vuoto», come dicono gli screen reader nelle
///   altre applicazioni, perché si capisca che la freccia ha funzionato.</description></item>
///   <item><description>Casella di spunta: «selezionato» o «non selezionato», le
///   parole di NVDA.</description></item>
///   <item><description>Qualunque altro contenuto: nessun nome, e resta il
///   comportamento di WPF. Oggi nessuna tabella ne ha.</description></item>
/// </list>
/// </remarks>
public sealed class TestoDellaCella : IMultiValueConverter
{
    /// <summary>Parola detta su una cella che non mostra niente.</summary>
    public const string Vuoto = "vuoto";

    /// <summary>Parola detta su una casella di spunta selezionata.</summary>
    public const string Selezionato = "selezionato";

    /// <summary>Parola detta su una casella di spunta non selezionata.</summary>
    public const string NonSelezionato = "non selezionato";

    /// <summary>L'istanza usata dallo stile del tema.</summary>
    public static TestoDellaCella Instance { get; } = new();

    /// <summary>Compone il nome.</summary>
    /// <param name="values">
    /// Nell'ordine: il testo del contenuto, lo stato di spunta del contenuto, il
    /// contenuto stesso. I primi due mancano quando il contenuto non ha quella proprietà.
    /// </param>
    /// <param name="targetType">Non usato.</param>
    /// <param name="parameter">Non usato.</param>
    /// <param name="culture">Non usato.</param>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var contenuto = values.Length > 2 ? values[2] : null;

        if (contenuto is CheckBox)
        {
            return values.Length > 1 && values[1] is true ? Selezionato : NonSelezionato;
        }

        if (contenuto is TextBlock or null || contenuto == DependencyProperty.UnsetValue)
        {
            return values.Length > 0 && values[0] is string testo && !string.IsNullOrWhiteSpace(testo)
                ? testo
                : Vuoto;
        }

        // Un modello di colonna: il testo sta dentro, e un nome scritto da qui
        // coprirebbe quello che il modello dichiara da sé.
        return string.Empty;
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Il nome accessibile di una cella si legge soltanto.");
}
