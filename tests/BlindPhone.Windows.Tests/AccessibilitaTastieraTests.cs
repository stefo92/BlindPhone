using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Controlli permanenti sull'accessibilità da tastiera di tutte le finestre.
/// </summary>
/// <remarks>
/// <para>
/// Nascono da un audit fatto aprendo ogni finestra con il client UI Automation — lo
/// stesso strumento con cui NVDA e JAWS leggono l'interfaccia — e simulando il giro di
/// Tab. Quello che l'audit ha trovato è tutto invisibile a chi guarda lo schermo, e per
/// questo è qui sotto forma di prova: un difetto che nessuno vede non si corregge due
/// volte.
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>Tasti di accesso doppi.</b> Alt+V doveva essere «Salva» ma in molte schede
///   portava a una casella, perché le intestazioni delle schede e i campi usavano le
///   stesse lettere: erano 71 conflitti.
///   </description></item>
///   <item><description>
///   <b>Caselle combinate che leggono nomi inglesi.</b> Il testo di sei elenchi era il
///   nome interno del valore — «CountOnly», «ThresholdOnly», «Triples» — e lo screen
///   reader lo pronunciava così.
///   </description></item>
///   <item><description>
///   <b>Riquadri di scorrimento muti.</b> Un elemento focalizzabile senza nome per ogni
///   scheda delle impostazioni.
///   </description></item>
/// </list>
/// <para>
/// Il resto dell'audit — nomi, ruoli, tabelle, ordine di Tab — dava risultati corretti e
/// qui è protetto dalle prove già esistenti sulle tabelle e sui nomi.
/// </para>
/// </remarks>
public sealed class AccessibilitaTastieraTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string CartellaApp()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(directory.FullName, "src", "BlindPhone.App");
            if (Directory.Exists(candidato)) return candidato;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cartella dell'applicazione non trovata");
    }

    /// <summary>Le finestre, con il loro documento: tutte le XAML che dichiarano una <c>Window</c>.</summary>
    private static IEnumerable<(string File, XDocument Documento)> Finestre()
    {
        foreach (var percorso in Directory.EnumerateFiles(CartellaApp(), "*Window.xaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return (Path.GetFileName(percorso), XDocument.Load(percorso));
        }
    }

    private static bool InModello(XElement e)
        => e.Ancestors().Any(a => a.Name.LocalName is "ControlTemplate" or "Style" or "DataTemplate");

    /// <summary>La lettera del tasto di accesso di un testo, se ce n'è una (il doppio trattino basso è letterale).</summary>
    private static char? Tasto(string testo)
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

    /// <summary>Gli elementi con un tasto di accesso, con la scheda in cui stanno (o <c>null</c> se sono di finestra).</summary>
    private static IEnumerable<(XElement Elemento, string Testo, char Tasto, string? Scheda)> ConTasto(XDocument documento)
    {
        foreach (var e in documento.Descendants().Where(d => d.Name.Namespace == P && !InModello(d)))
        {
            // Un TextBlock non e' un tasto di accesso: lo sono Label, pulsanti, caselle e intestazioni.
            if (e.Name.LocalName == "TextBlock") continue;

            foreach (var nomeAttributo in new[] { "Content", "Header" })
            {
                if ((string?)e.Attribute(nomeAttributo) is not { } testo) continue;
                if (Tasto(testo) is not { } tasto) continue;

                // Il contenuto di una scheda esiste solo quando la scheda e' selezionata; la sua intestazione, sempre.
                var scheda = e.Name.LocalName == "TabItem"
                    ? null
                    : e.Ancestors(P + "TabItem").FirstOrDefault() is { } t ? (string?)t.Attribute(X + "Name") ?? "scheda" : null;

                yield return (e, testo, tasto, scheda);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Tasti di accesso
    // -----------------------------------------------------------------------

    [Fact]
    public void InNessunaFinestraDueElementiVisibiliInsiemeHannoLoStessoTastoDiAccesso()
    {
        // «Visibili insieme» e' il punto: il contenuto di una scheda esiste solo mentre la
        // scheda e' selezionata, quindi si confrontano i tasti di finestra con quelli di UNA
        // scheda alla volta. Con due elementi sullo stesso tasto Alt+lettera porta al primo
        // e non al secondo, e su un pulsante di salvataggio significa non salvare.
        var doppi = new List<string>();

        foreach (var (file, documento) in Finestre())
        {
            var voci = ConTasto(documento).ToList();
            var schede = voci.Select(v => v.Scheda).Where(s => s is not null).Distinct().ToList();

            // Una finestra senza schede e' una vista sola.
            var viste = schede.Count == 0 ? new List<string?> { null } : [.. schede];

            foreach (var scheda in viste)
            {
                // L'UNICA ECCEZIONE: «Sposta su» e «Sposta giu» condividono la U, perche' in
                // italiano non c'e' una lettera libera per due pulsanti che differiscono solo
                // da una sillaba, e le schede sono sature. Il progetto esige che ogni pulsante
                // dell'ordine abbia un tasto di accesso (vedi PannelloCodecTests); qui Alt+U li
                // alterna, e la strada diretta resta Alt+Freccia dentro l'elenco, dichiarata nel
                // testo di aiuto. Ogni altro elemento della vista deve avere un tasto suo.
                var inVista = voci
                    .Where(v => v.Scheda is null || v.Scheda == scheda)
                    .Where(v => !Regex.IsMatch((string?)v.Elemento.Attribute(X + "Name") ?? string.Empty, "(Up|Down)Button$"));

                foreach (var gruppo in inVista.GroupBy(v => v.Tasto).Where(g => g.Count() > 1))
                {
                    doppi.Add($"{file} [{scheda ?? "finestra"}] Alt+{gruppo.Key}: "
                              + string.Join(" / ", gruppo.Select(v => v.Testo)));
                }
            }
        }

        Assert.True(doppi.Count == 0, "Tasti di accesso doppi:\n" + string.Join('\n', doppi.Distinct()));
    }

    [Fact]
    public void LeIntestazioniDelleSchedeDelleImpostazioniNonHannoTastiDiAccesso()
    {
        // Le schede si cambiano con Control Tab, annunciato a ogni apertura. Le lettere
        // sottolineate servono ai campi — e' la promessa dell'annuncio «Alt piu' la
        // lettera» — e le intestazioni ne occupavano dieci su ventisei, in permanenza.
        var documento = Finestre().Single(f => f.File == "SettingsWindow.xaml").Documento;

        var conTasto = documento.Descendants(P + "TabItem")
            .Where(t => (string?)t.Attribute("Header") is { } h && Tasto(h) is not null)
            .Select(t => (string?)t.Attribute("Header"))
            .ToList();

        Assert.True(conTasto.Count == 0, "Intestazioni con tasto di accesso: " + string.Join(", ", conTasto));
    }

    [Fact]
    public void IPulsantiSempreVisibiliDelleImpostazioniNonSonoRiusatiDaiCampi()
    {
        // Salva, Annulla e Sblocca sono nella finestra e non in una scheda: ci sono
        // sempre. Il confronto qui sopra li include gia', ma questa prova dice
        // esplicitamente quali sono, cosi' chi lo rompe capisce che cosa ha rotto.
        var voci = ConTasto(Finestre().Single(f => f.File == "SettingsWindow.xaml").Documento).ToList();

        var sempre = voci.Where(v => v.Scheda is null).ToList();

        Assert.Contains(sempre, v => v.Elemento.Attribute(X + "Name")?.Value == "SaveButton");
        Assert.Contains(sempre, v => v.Elemento.Attribute(X + "Name")?.Value == "CancelButton");

        var lettere = sempre.Select(v => v.Tasto).ToHashSet();

        var riusati = voci
            .Where(v => v.Scheda is not null && lettere.Contains(v.Tasto))
            .Select(v => $"Alt+{v.Tasto}: {v.Testo} ({v.Scheda})")
            .ToList();

        Assert.True(riusati.Count == 0, "Campi che riusano i tasti di Salva, Annulla o Sblocca:\n" + string.Join('\n', riusati));
    }

    // -----------------------------------------------------------------------
    // Caselle combinate
    // -----------------------------------------------------------------------

    [Fact]
    public void LeVociDelleCaselleCombinateDichiaranoUnValoreEDiconoQualcosaInItaliano()
    {
        // Il valore interno sta nel Tag, il testo e' per chi ascolta. Un testo come
        // «CountOnly» viene pronunciato in inglese da una voce italiana, ed e' quello che
        // succedeva in sei elenchi.
        var nomiInterni = new Regex(@"^[A-Z][a-z0-9]+([A-Z][a-z0-9]+)*$", RegexOptions.Compiled);
        var difetti = new List<string>();

        foreach (var (file, documento) in Finestre())
        {
            foreach (var voce in documento.Descendants(P + "ComboBoxItem").Where(v => !InModello(v)))
            {
                var contenuto = (string?)voce.Attribute("Content") ?? string.Empty;

                if (voce.Attribute("Tag") is null) difetti.Add($"{file}: «{contenuto}» senza Tag");

                // Una parola sola che sembra un nome di valore, con la maiuscola di ogni parola
                // attaccata: ThresholdOnly, ProgressiveDoubleTap. Una sigla in maiuscolo — TCP — va bene.
                if (nomiInterni.IsMatch(contenuto) && contenuto != contenuto.ToUpperInvariant())
                {
                    difetti.Add($"{file}: «{contenuto}» e' un nome interno, non un testo da ascoltare");
                }
            }
        }

        Assert.True(difetti.Count == 0, string.Join('\n', difetti));
    }

    [Fact]
    public void ILettoriDelleCaselleCombinateLeggonoIlTagENonIlTesto()
    {
        // Il lato opposto del difetto: se il testo cambia e il codice continua a leggerlo,
        // il salvataggio smette di riconoscere la scelta senza nessun errore.
        var codice = File.ReadAllText(Path.Combine(CartellaApp(), "SettingsWindow.xaml.cs"));

        Assert.DoesNotContain("SelectedContent(", codice, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectByContent(", codice, StringComparison.Ordinal);
    }

    [Fact]
    public void OgniCasellaCombinataEOgniElencoDelleImpostazioniHaUnaSpiegazione()
    {
        // Il nome dice che cos'e', il testo di aiuto a che cosa serve la scelta: per chi non
        // vede e' l'unico modo di saperlo senza provare.
        var documento = Finestre().Single(f => f.File == "SettingsWindow.xaml").Documento;

        var senza = documento.Descendants()
            .Where(e => e.Name.LocalName is "ComboBox" or "ItemsControl" && !InModello(e))
            .Where(e => string.IsNullOrWhiteSpace((string?)e.Attribute("AutomationProperties.HelpText")))
            .Select(e => $"{e.Name.LocalName} '{(string?)e.Attribute(X + "Name")}'")
            .ToList();

        Assert.True(senza.Count == 0, "Senza testo di aiuto: " + string.Join(", ", senza));
    }

    // -----------------------------------------------------------------------
    // Elementi che non devono essere un punto d'arrivo muto
    // -----------------------------------------------------------------------

    [Fact]
    public void IRiquadriDiScorrimentoNonSonoElementiFocalizzabiliSenzaNome()
    {
        var difetti = Finestre()
            .SelectMany(f => f.Documento.Descendants(P + "ScrollViewer")
                .Where(s => !InModello(s))
                .Where(s => (string?)s.Attribute("Focusable") != "False"
                            && string.IsNullOrWhiteSpace((string?)s.Attribute("AutomationProperties.Name")))
                .Select(_ => f.File))
            .ToList();

        Assert.True(difetti.Count == 0, "ScrollViewer focalizzabili e senza nome in: " + string.Join(", ", difetti.Distinct()));
    }

    [Fact]
    public void NessunControlloInterattivoEEscluso()
    {
        string[] interattivi = ["Button", "CheckBox", "RadioButton", "ComboBox", "TextBox", "PasswordBox", "ListBox", "ListView", "DataGrid", "TabControl"];

        var esclusi = Finestre()
            .SelectMany(f => f.Documento.Descendants()
                .Where(e => e.Name.Namespace == P && interattivi.Contains(e.Name.LocalName) && !InModello(e))
                .Where(e => (string?)e.Attribute("Focusable") == "False" || (string?)e.Attribute("IsTabStop") == "False")
                .Select(e => $"{f.File}: {e.Name.LocalName} '{(string?)e.Attribute(X + "Name")}'"))
            .ToList();

        Assert.True(esclusi.Count == 0, "Controlli interattivi esclusi dalla tastiera: " + string.Join("; ", esclusi));
    }

    // -----------------------------------------------------------------------
    // Finestre
    // -----------------------------------------------------------------------

    [Fact]
    public void OgniFinestraHaUnTitolo()
    {
        // E' la prima cosa che lo screen reader legge all'apertura.
        var senza = Finestre()
            .Where(f => string.IsNullOrWhiteSpace((string?)f.Documento.Root!.Attribute("Title")))
            .Select(f => f.File)
            .ToList();

        Assert.True(senza.Count == 0, "Finestre senza titolo: " + string.Join(", ", senza));
    }

    [Fact]
    public void OgniFinestraSecondariaSiChiudeConEsc()
    {
        // Esc chiude la finestra solo se c'e' un pulsante di annullamento: senza, chi usa
        // la tastiera deve cercare il pulsante di chiusura, e chi non vede lo schermo non
        // lo trova.
        var senza = Finestre()
            .Where(f => f.File != "MainWindow.xaml")
            .Where(f => !f.Documento.Descendants(P + "Button").Any(b => (string?)b.Attribute("IsCancel") == "True"))
            .Select(f => f.File)
            .ToList();

        Assert.True(senza.Count == 0, "Finestre senza un pulsante IsCancel: " + string.Join(", ", senza));
    }

    [Fact]
    public void OgniDoppioClicHaLEquivalenteDaTastiera()
    {
        // Un'azione che si fa solo con il doppio clic non esiste per chi usa la tastiera.
        var senzaTastiera = new List<string>();

        foreach (var (file, documento) in Finestre())
        {
            foreach (var e in documento.Descendants().Where(d => d.Attribute("MouseDoubleClick") is not null))
            {
                var haTasti = e.Attribute("KeyDown") is not null || e.Attribute("PreviewKeyDown") is not null;

                if (!haTasti) senzaTastiera.Add($"{file}: {e.Name.LocalName} '{(string?)e.Attribute(X + "Name")}'");
            }
        }

        Assert.True(senzaTastiera.Count == 0, "Doppio clic senza gestore da tastiera: " + string.Join("; ", senzaTastiera));
    }
}
