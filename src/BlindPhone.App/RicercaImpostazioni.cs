using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BlindPhone.App;

/// <summary>
/// Un'impostazione trovata dal campo di ricerca del pannello.
/// </summary>
/// <param name="Etichetta">Quello che c'è scritto accanto al campo, senza il trattino del tasto di accesso.</param>
/// <param name="Scheda">Nome della scheda che la contiene.</param>
/// <param name="Sezione">Gruppo o sezione apribile che la contiene, se c'è.</param>
/// <param name="Destinazione">Elemento che riceve il cursore.</param>
/// <param name="Testo">Etichetta e sezione già normalizzate, su cui si confronta la ricerca.</param>
public sealed record VoceDiRicerca(
    string Etichetta,
    string Scheda,
    string? Sezione,
    FrameworkElement Destinazione,
    string Testo)
{
    /// <summary>
    /// La frase che lo screen reader legge sulla proposta.
    /// </summary>
    /// <remarks>
    /// Il campo prima, perché è quello che si cerca, poi dove sta. Senza questa
    /// riscrittura l'elenco avrebbe letto il nome completo del tipo e tutte le
    /// proprietà del record, elemento di interfaccia compreso.
    /// </remarks>
    public override string ToString()
        => Sezione is null
            ? $"{Etichetta}, scheda {Scheda}"
            : $"{Etichetta}, scheda {Scheda}, {Sezione}";
}

/// <summary>
/// Ricerca di un'impostazione per nome, e navigazione fino al suo campo.
/// </summary>
/// <remarks>
/// <para>
/// <b>L'indice si ricava dalla finestra, non da un elenco scritto a mano.</b> Le voci
/// sono le etichette dei campi, il testo delle caselle e dei pulsanti, e per i
/// controlli senza etichetta il nome accessibile: le stesse parole che l'operatore
/// sente quando ci arriva. Un elenco parallelo invecchierebbe al primo campo aggiunto
/// senza che nessuno se ne accorga, e la ricerca smetterebbe di trovare proprio le
/// cose nuove, che sono quelle che si cercano.
/// </para>
/// <para>
/// <b>Si cerca senza accenti e senza maiuscole, e per parole intere della domanda in
/// qualunque ordine.</b> «Accessibilità» si scrive spesso senza accento, e chi cerca
/// «proxy uscita» deve trovare «Proxy in uscita».
/// </para>
/// <para>
/// La parte che non tocca l'interfaccia — normalizzazione, confronto, lettura del
/// tasto di accesso — è pubblica e statica per poterla provare senza aprire finestre.
/// </para>
/// </remarks>
public static class RicercaImpostazioni
{
    /// <summary>
    /// La lettera del tasto di accesso di un testo, se ce n'è una.
    /// </summary>
    /// <param name="testo">Testo di un'etichetta o di un pulsante, con il trattino basso.</param>
    /// <returns>La lettera in maiuscolo, oppure <c>null</c>.</returns>
    /// <remarks>Il doppio trattino basso è un trattino letterale, come in WPF.</remarks>
    public static char? TastoDiAccesso(string testo)
    {
        for (var i = 0; i < testo.Length - 1; i++)
        {
            if (testo[i] != '_') continue;

            if (testo[i + 1] == '_')
            {
                i++;
                continue;
            }

            if (char.IsLetterOrDigit(testo[i + 1])) return char.ToUpperInvariant(testo[i + 1]);
        }

        return null;
    }

    /// <summary>Il testo come compare a schermo: senza il trattino del tasto di accesso.</summary>
    /// <param name="testo">Testo con l'eventuale trattino basso.</param>
    public static string SenzaTastoDiAccesso(string testo)
    {
        var risultato = new StringBuilder(testo.Length);

        for (var i = 0; i < testo.Length; i++)
        {
            if (testo[i] == '_')
            {
                // Il doppio trattino diventa uno, il singolo sparisce.
                if (i + 1 < testo.Length && testo[i + 1] == '_')
                {
                    risultato.Append('_');
                    i++;
                }

                continue;
            }

            risultato.Append(testo[i]);
        }

        return risultato.ToString();
    }

    /// <summary>
    /// Minuscole, senza accenti, e ogni segno che non sia lettera o cifra diventa uno
    /// spazio.
    /// </summary>
    /// <param name="testo">Testo da normalizzare.</param>
    public static string Normalizza(string testo)
    {
        var scomposto = testo.Normalize(NormalizationForm.FormD);
        var risultato = new StringBuilder(scomposto.Length);
        var spazio = true;

        foreach (var carattere in scomposto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(carattere) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(carattere))
            {
                risultato.Append(char.ToLowerInvariant(carattere));
                spazio = false;
            }
            else if (!spazio)
            {
                risultato.Append(' ');
                spazio = true;
            }
        }

        return risultato.ToString().TrimEnd();
    }

    /// <summary>
    /// Vero se ogni parola della domanda compare nel testo, in qualunque ordine.
    /// </summary>
    /// <param name="testoNormalizzato">Testo della voce, già passato da <see cref="Normalizza"/>.</param>
    /// <param name="domanda">Quello che ha scritto l'operatore.</param>
    /// <remarks>Una domanda vuota non trova niente: elencare tutto non aiuta nessuno.</remarks>
    public static bool Corrisponde(string testoNormalizzato, string domanda)
    {
        var parole = Normalizza(domanda).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parole.Length > 0
               && parole.All(p => testoNormalizzato.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Raccoglie le impostazioni di tutte le schede, nell'ordine in cui compaiono.
    /// </summary>
    /// <param name="schede">Il controllo delle schede del pannello.</param>
    /// <remarks>
    /// L'ordine è quello delle schede e, dentro ciascuna, quello di lettura: le
    /// proposte arrivano nello stesso ordine in cui l'operatore le troverebbe a
    /// forza di Tab, e un elenco che cambia ordine a ogni lettera è un elenco in cui
    /// non ci si orienta.
    /// </remarks>
    public static IReadOnlyList<VoceDiRicerca> Indice(TabControl schede)
    {
        var voci = new List<VoceDiRicerca>();

        foreach (var scheda in schede.Items.OfType<TabItem>())
        {
            if (scheda.Content is not DependencyObject contenuto) continue;

            var nomeScheda = SenzaTastoDiAccesso(scheda.Header as string ?? string.Empty);
            var elementi = Discendenti(contenuto).ToList();

            // Prima le etichette, perche' danno il nome al campo che indicano.
            var etichette = new Dictionary<DependencyObject, string>();

            foreach (var etichetta in elementi.OfType<Label>())
            {
                if (etichetta.Target is DependencyObject campo && etichetta.Content is string testo)
                {
                    etichette.TryAdd(campo, SenzaTastoDiAccesso(testo));
                }
            }

            foreach (var elemento in elementi.OfType<FrameworkElement>())
            {
                if (NomeDellaVoce(elemento, etichette) is not { } nome) continue;

                var sezione = SezioneDi(elemento);
                voci.Add(new VoceDiRicerca(nome, nomeScheda, sezione, elemento, Normalizza(nome + " " + sezione)));
            }
        }

        return voci;
    }

    /// <summary>Il nome con cui una voce si presenta, o <c>null</c> se non è un'impostazione.</summary>
    private static string? NomeDellaVoce(FrameworkElement elemento, Dictionary<DependencyObject, string> etichette)
    {
        if (elemento is Label) return null;

        if (etichette.TryGetValue(elemento, out var etichetta)) return etichetta;

        // Caselle e pulsanti portano il proprio testo.
        if (elemento is ButtonBase { Content: string testo }) return SenzaTastoDiAccesso(testo);

        // I controlli senza etichetta visibile, come l'elenco dei codec: il nome
        // accessibile e' quello che lo screen reader legge arrivandoci.
        if (elemento is TextBox or PasswordBox or ComboBox or ListBox or DataGrid)
        {
            var nome = AutomationProperties.GetName(elemento);
            return string.IsNullOrWhiteSpace(nome) ? null : nome;
        }

        return null;
    }

    /// <summary>Il gruppo o la sezione apribile più vicini che contengono l'elemento.</summary>
    private static string? SezioneDi(DependencyObject elemento)
    {
        foreach (var antenato in Antenati(elemento))
        {
            if (antenato is TabItem) return null;
            if (antenato is HeaderedContentControl { Header: string titolo } and (GroupBox or Expander))
            {
                // Un'intestazione lunga, come quella di «Avanzate: server di
                // registrazione, ...», nella proposta si accorcia al suo nome.
                var duePunti = titolo.IndexOf(':', StringComparison.Ordinal);
                return duePunti > 0 ? titolo[..duePunti] : titolo;
            }
        }

        return null;
    }

    /// <summary>
    /// Vero se l'elemento si può raggiungere adesso: nessun contenitore fra lui e la
    /// scheda è nascosto.
    /// </summary>
    /// <param name="elemento">Campo da raggiungere.</param>
    /// <remarks>
    /// <para>
    /// Si guarda la proprietà <c>Visibility</c> lungo l'albero logico, e non
    /// <c>IsVisible</c>: il contenuto di una scheda non selezionata non è nell'albero
    /// visivo, e per WPF lì non si vede niente.
    /// </para>
    /// <para>
    /// Una sezione chiusa non nasconde il suo contenuto in questo senso — lo nasconde
    /// il modello dell'<see cref="Expander"/> — ed è giusto così: la ricerca la apre.
    /// Restano fuori invece i pannelli spenti di proposito, come quello di SQL Server
    /// quando la rubrica arriva da un file, o la password in chiaro quando non è
    /// richiesta: proporli porterebbe il cursore su un campo che non c'è.
    /// </para>
    /// </remarks>
    public static bool Raggiungibile(FrameworkElement elemento)
    {
        if (elemento.Visibility != Visibility.Visible) return false;

        foreach (var antenato in Antenati(elemento))
        {
            if (antenato is TabItem) return true;
            if (antenato is UIElement { Visibility: not Visibility.Visible }) return false;
        }

        return true;
    }

    /// <summary>I discendenti nell'albero logico, nell'ordine del documento.</summary>
    /// <param name="radice">Elemento da cui partire, escluso.</param>
    public static IEnumerable<DependencyObject> Discendenti(DependencyObject radice)
    {
        foreach (var figlio in LogicalTreeHelper.GetChildren(radice).OfType<DependencyObject>())
        {
            yield return figlio;

            foreach (var nipote in Discendenti(figlio)) yield return nipote;
        }
    }

    /// <summary>Gli antenati nell'albero logico, dal più vicino.</summary>
    /// <param name="elemento">Elemento da cui partire, escluso.</param>
    public static IEnumerable<DependencyObject> Antenati(DependencyObject elemento)
    {
        for (var genitore = LogicalTreeHelper.GetParent(elemento);
             genitore is not null;
             genitore = LogicalTreeHelper.GetParent(genitore))
        {
            yield return genitore;
        }
    }
}
