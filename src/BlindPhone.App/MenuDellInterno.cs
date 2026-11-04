using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BlindPhone.App;

/// <summary>Un interno su cui agire: il numero e, se lo si conosce, il nome.</summary>
/// <param name="Number">Interno o numero, in cifre.</param>
/// <param name="Name">Nominativo, se noto.</param>
internal sealed record Interno(string Number, string? Name)
{
    /// <summary>Come lo si nomina in un menu: «Mario Rossi, interno 201», oppure «interno 201».</summary>
    public string Description
        => string.IsNullOrWhiteSpace(Name) ? $"interno {Number}" : $"{Name}, interno {Number}";
}

/// <summary>
/// Il menu contestuale degli elenchi di interni: chiama, oppure scrivi un messaggio.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lo stesso menu in ogni elenco</b> — colleghi, contatti proposti, rubrica,
/// conversazioni — con le stesse voci nello stesso ordine e le stesse lettere: chi lo
/// impara in un elenco lo ritrova negli altri senza doverlo cercare.
/// </para>
/// <para>
/// <b>Si apre anche da tastiera</b>, con il tasto Applicazioni o con Maiusc F10, ed è
/// la ragione per cui esiste accanto ai tasti rapidi: un menu si esplora a voce, una
/// combinazione va saputa. Per chi lavora con il mouse è il clic destro sulla riga, che
/// la seleziona prima di aprire il menu.
/// </para>
/// <para>
/// <b>Le voci nominano l'interno</b>: «Chiama Mario Rossi, interno 201». Il menu si
/// apre sulla riga selezionata, e dire su chi agirà è l'unico modo, per chi non vede,
/// di accorgersi che la selezione era rimasta su un'altra riga.
/// </para>
/// <para>
/// Si costruisce da codice e non nel XAML perché è lo stesso per quattro elenchi in tre
/// finestre, e le voci cambiano con la riga su cui si apre.
/// </para>
/// </remarks>
internal static class MenuDellInterno
{
    /// <summary>Aggiunge il menu a un elenco.</summary>
    /// <param name="elenco">Elenco o tabella degli interni.</param>
    /// <param name="scelto">Interno della riga selezionata, oppure <c>null</c> se non ce n'è.</param>
    /// <param name="chiama">Cosa fare per chiamarlo.</param>
    /// <param name="scrivi">Cosa fare per scrivergli.</param>
    /// <param name="tastoChiama">Tasto che fa la stessa cosa di «Chiama» in quell'elenco, da mostrare accanto alla voce.</param>
    /// <param name="tastoScrivi">Tasto dei messaggi, letto al momento: l'operatore può averlo cambiato.</param>
    public static void Collega(
        Control elenco,
        Func<Interno?> scelto,
        Func<Interno, Task> chiama,
        Action<Interno> scrivi,
        string? tastoChiama,
        Func<string?> tastoScrivi)
    {
        var menu = new ContextMenu();
        AutomationProperties.SetName(menu, "Azioni sull'interno");
        elenco.ContextMenu = menu;

        elenco.ContextMenuOpening += (_, e) =>
        {
            // Senza una riga selezionata non c'e' nessuno su cui agire: un menu con due
            // voci che non fanno niente sarebbe peggio di nessun menu.
            if (scelto() is not { } interno || string.IsNullOrWhiteSpace(interno.Number))
            {
                e.Handled = true;
                return;
            }

            menu.Items.Clear();
            menu.Items.Add(Voce($"_Chiama {Letterale(interno.Description)}", tastoChiama, async () => await chiama(interno)));
            menu.Items.Add(Voce($"Invia _messaggio a {Letterale(interno.Description)}", tastoScrivi(), () =>
            {
                scrivi(interno);
                return Task.CompletedTask;
            }));
        };
    }

    private static MenuItem Voce(string intestazione, string? tasto, Func<Task> azione)
    {
        var voce = new MenuItem
        {
            Header = intestazione,
            InputGestureText = tasto ?? string.Empty,
        };

        // Il gestore e' asincrono e non si attende: il menu si chiude subito, e
        // quello che segue — una chiamata che parte, una finestra che si apre — ha i
        // suoi annunci.
        voce.Click += async (_, _) => await azione();
        return voce;
    }

    /// <summary>Raddoppia i trattini bassi di un nome, che nell'intestazione indicherebbero un tasto di accesso.</summary>
    private static string Letterale(string testo) => testo.Replace("_", "__", StringComparison.Ordinal);
}
