using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BlindPhone.App;

/// <summary>
/// Spostamento del focus dentro una <see cref="DataGrid"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selezionare non è dare il focus.</b> Impostare <c>SelectedIndex</c> colora una
/// riga ma non muove il cursore della tastiera, e la sintesi vocale legge
/// l'elemento che ha il focus. Senza questo passaggio l'operatore che scende
/// nell'elenco sente il nome della tabella e poi silenzio.
/// </para>
/// <para>
/// In una tabella l'elemento che ha il focus non è la riga ma la <b>cella</b>: è
/// quello che permette di leggere i campi uno per uno con le frecce orizzontali, ed
/// è il motivo per cui non basta chiamare <c>Focus()</c> sulla griglia.
/// </para>
/// </remarks>
public static class GridNavigation
{
    /// <summary>
    /// Fa uscire il tasto Tab dalla tabella invece di fargliene percorrere le celle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il difetto che corregge.</b> La <see cref="DataGrid"/> di WPF tratta Tab
    /// come spostamento fra celle: da una cella passa alla successiva, a fine riga
    /// scende alla prima cella della riga dopo, e lascia il controllo soltanto dopo
    /// l'ultima cella dell'ultima riga. Su una tabella di sei colonne e cinque righe
    /// sono trenta pressioni per arrivare al pulsante che sta sotto.
    /// </para>
    /// <para>
    /// Le conseguenze non erano di comodità ma di funzionamento. Chi usa la sola
    /// tastiera, per uscire dalla tabella, doveva attraversarla tutta: e siccome la
    /// selezione segue il cursore, all'uscita risultava scelta <b>sempre l'ultima
    /// riga</b>. Sull'elenco degli account questo significava che il primo account non
    /// si poteva attivare in alcun modo.
    /// </para>
    /// <para>
    /// Dentro la tabella ci si muove con le frecce, che è il gesto giusto e quello che
    /// ogni screen reader insegna; Tab fa quello che fa in tutto il resto di Windows,
    /// cioè porta al comando successivo.
    /// </para>
    /// <para>
    /// Si applica dallo stile globale delle tabelle in App.xaml, quindi vale per
    /// tutte — comprese quelle che verranno aggiunte in seguito, che è il motivo per
    /// cui è una proprietà allegata e non una riga di codice ripetuta in cinque
    /// finestre.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty TabLeavesTableProperty =
        DependencyProperty.RegisterAttached(
            "TabLeavesTable",
            typeof(bool),
            typeof(GridNavigation),
            new PropertyMetadata(false, OnTabLeavesTableChanged));

    /// <summary>Attiva o disattiva l'uscita dalla tabella con Tab.</summary>
    /// <param name="element">Tabella interessata.</param>
    /// <param name="value"><c>true</c> per far uscire il Tab dalla tabella.</param>
    public static void SetTabLeavesTable(DependencyObject element, bool value)
        => element.SetValue(TabLeavesTableProperty, value);

    /// <summary>Indica se il Tab esce dalla tabella.</summary>
    /// <param name="element">Tabella interessata.</param>
    public static bool GetTabLeavesTable(DependencyObject element)
        => (bool)element.GetValue(TabLeavesTableProperty);

    private static void OnTabLeavesTableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;

        // Sempre disiscritto prima: lo stile puo' riapplicare il valore, e due
        // sottoscrizioni farebbero saltare due elementi invece di uno.
        grid.PreviewKeyDown -= OnGridPreviewKeyDown;

        if (e.NewValue is true) grid.PreviewKeyDown += OnGridPreviewKeyDown;
    }

    /// <summary>
    /// Porta il fuoco all'elemento successivo della finestra, fuori dalla tabella.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si interviene in fase di anteprima perché la <see cref="DataGrid"/> gestisce
    /// Tab per conto proprio: lasciandolo arrivare al suo gestore, lo spostamento fra
    /// celle avverrebbe comunque e il nostro sarebbe in più, non al posto suo.
    /// </para>
    /// <para>
    /// <b>Lo spostamento parte dalla riga</b>, e il punto di partenza non è
    /// intercambiabile. Misurato sulla finestra vera, con la tabella dichiarata una
    /// tappa sola del giro di tabulazione:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   dalla <b>tabella</b>: la navigazione scende nei suoi figli e il fuoco resta
    ///   sulla stessa cella;
    ///   </description></item>
    ///   <item><description>
    ///   dalla <b>cella</b> che ha il fuoco: si va alla cella successiva, cioè
    ///   esattamente il comportamento che si sta correggendo;
    ///   </description></item>
    ///   <item><description>
    ///   dalla <b>riga</b>: il fuoco arriva sul primo comando dopo la tabella.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Se il fuoco non è dentro una riga — tabella vuota, oppure fuoco sulla tabella
    /// stessa — non si tocca niente: lì il comportamento predefinito di WPF esce già
    /// correttamente, e intervenire rischierebbe di intrappolare il cursore su un
    /// elenco senza righe.
    /// </para>
    /// </remarks>
    private static void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) return;
        if (sender is not DataGrid) return;
        if (FindRow(Keyboard.FocusedElement as DependencyObject) is not { } row) return;

        e.Handled = true;

        var direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? FocusNavigationDirection.Previous
            : FocusNavigationDirection.Next;

        row.MoveFocus(new TraversalRequest(direction));
    }

    /// <summary>Risale dall'elemento che ha il fuoco alla riga che lo contiene.</summary>
    private static DataGridRow? FindRow(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is DataGridRow row) return row;

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    /// <summary>
    /// Porta la selezione e il focus della tastiera su una cella.
    /// </summary>
    /// <param name="grid">Tabella interessata.</param>
    /// <param name="rowIndex">Indice della riga.</param>
    /// <param name="columnIndex">Indice della colonna.</param>
    /// <returns><c>true</c> se il focus è arrivato sulla cella.</returns>
    /// <remarks>
    /// <para>
    /// Se la cella non è raggiungibile si ripiega sul focus della tabella, che è
    /// comunque meglio di nessun focus: da lì le frecce funzionano e l'operatore
    /// non resta con il cursore in un punto imprecisato della finestra.
    /// </para>
    /// <para>
    /// <b>La selezione di riga si applica solo dove la tabella la consente.</b> Una
    /// <see cref="DataGrid"/> con <see cref="DataGrid.SelectionUnit"/> impostato su
    /// <see cref="DataGridSelectionUnit.Cell"/> rifiuta che venga selezionata una riga
    /// e solleva un'eccezione — non qui, ma più tardi, <b>dentro il calcolo del
    /// layout</b>, quando il contenitore della riga viene creato. Un'eccezione lì non
    /// si può recuperare: la finestra non completa la propria disposizione, resta nera,
    /// e il gestore degli errori la annuncia a ogni tentativo di ridisegno, con il suo
    /// segnale acustico. È il difetto che rendeva inutilizzabile l'elenco degli account
    /// SIP, e il controllo sta qui perché nessuna tabella futura possa ricascarci per
    /// una riga di XAML.
    /// </para>
    /// </remarks>
    public static bool FocusCell(DataGrid grid, int rowIndex, int columnIndex = 0)
    {
        if (rowIndex < 0 || rowIndex >= grid.Items.Count) return false;
        if (columnIndex < 0 || columnIndex >= grid.Columns.Count) return false;

        var item = grid.Items[rowIndex];
        var column = grid.Columns[columnIndex];

        // La cella corrente si imposta sempre: e' quella che governa il cursore, la
        // lettura dello screen reader e le frecce orizzontali. La selezione, invece,
        // solo nella forma che questa tabella accetta.
        if (grid.SelectionUnit == DataGridSelectionUnit.Cell)
        {
            grid.SelectedCells.Clear();
            grid.SelectedCells.Add(new DataGridCellInfo(item, column));
        }
        else
        {
            grid.SelectedIndex = rowIndex;
        }

        grid.CurrentCell = new DataGridCellInfo(item, column);
        grid.ScrollIntoView(item, column);

        // Il contenitore della cella può non esistere ancora: si forza il layout
        // prima di cercarlo.
        grid.UpdateLayout();

        if (FindCell(grid, rowIndex, columnIndex) is { } cell && cell.Focus()) return true;

        grid.Focus();
        return false;
    }

    /// <summary>Indice della riga su cui si trova il cursore, oppure -1.</summary>
    /// <param name="grid">Tabella interessata.</param>
    /// <remarks>
    /// <para>
    /// Si guarda prima la cella corrente e poi la selezione: con le frecce orizzontali
    /// le due possono divergere, e quella che conta per l'operatore è dove sta il
    /// cursore.
    /// </para>
    /// <para>
    /// <b>Il ripiego sulla selezione c'era già e non è mai entrato in funzione.</b>
    /// Quando la tabella perde il fuoco della tastiera — cioè quando l'operatore preme
    /// Tab per raggiungere un pulsante — WPF azzera la cella corrente, ma
    /// <c>CurrentCell.Item</c> non diventa <c>null</c>: diventa
    /// <see cref="DependencyProperty.UnsetValue"/>. Il controllo cercava <c>null</c>,
    /// quindi non scattava mai, e si finiva a chiedere l'indice di <c>UnsetValue</c>
    /// dentro l'elenco: <b>meno uno</b>.
    /// </para>
    /// <para>
    /// Per l'operatore era questo: si sceglie una riga, si preme Tab per arrivare al
    /// pulsante che agisce su quella riga, e il pulsante risponde «nessuna riga
    /// selezionata». La selezione c'era ancora — <c>SelectedIndex</c> non si era mosso
    /// — semplicemente nessuno la guardava. Con la sola tastiera la funzione era
    /// irraggiungibile.
    /// </para>
    /// </remarks>
    public static int CurrentRowIndex(DataGrid grid)
    {
        var current = grid.CurrentCell.Item;

        if (current is null || current == DependencyProperty.UnsetValue) return grid.SelectedIndex;

        var index = grid.Items.IndexOf(current);

        // L'elemento puo' non essere piu' nell'elenco se questo e' stato ricostruito
        // sotto il cursore: anche li' la selezione e' l'informazione superstite.
        return index >= 0 ? index : grid.SelectedIndex;
    }

    private static DataGridCell? FindCell(DataGrid grid, int rowIndex, int columnIndex)
    {
        if (grid.ItemContainerGenerator.ContainerFromIndex(rowIndex) is not DataGridRow row) return null;

        var presenter = FindVisualChild<DataGridCellsPresenter>(row);
        return presenter?.ItemContainerGenerator.ContainerFromIndex(columnIndex) as DataGridCell;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }

        return null;
    }
}
