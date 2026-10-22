using System.Xml.Linq;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Ogni stile di cella assegnato a una colonna estende quello del tema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto.</b> Uno stile dato a <c>CellStyle</c> <i>sostituisce</i> quello
/// implicito del tema, non lo estende. Gli stili dei numeri e degli orari — quelli che
/// fanno leggere «4 0 2 1» invece di «quattromilaventuno» — erano dichiarati senza
/// <c>BasedOn</c>: la cella perdeva il trigger di selezione, e nella riga evidenziata il
/// numero restava chiaro, a testo nero, mentre tutte le altre celle diventavano blu. Era
/// proprio la cella con l'informazione che si cerca, e per un ipovedente la riga
/// selezionata smetteva di essere riconoscibile per intero.
/// </para>
/// <para>
/// Non produce nessun errore e non lo vede nessuno che non guardi una riga selezionata,
/// per questo la prova è sul file e non sulla finestra: ogni stile con chiave e destinato
/// a una cella deve dichiarare da quale parte.
/// </para>
/// </remarks>
public sealed class SpokenCellStyleTests
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

    /// <summary>Gli stili con chiave che hanno come destinazione la cella di una tabella.</summary>
    private static IEnumerable<(string File, string Chiave, XElement Stile)> StiliDiCella()
    {
        foreach (var percorso in Directory.EnumerateFiles(CartellaApp(), "*.xaml", SearchOption.AllDirectories))
        {
            var documento = XDocument.Load(percorso);

            foreach (var stile in documento.Descendants(P + "Style"))
            {
                if ((string?)stile.Attribute("TargetType") != "DataGridCell") continue;

                // Lo stile implicito del tema non ha chiave: e' quello che gli altri estendono.
                if ((string?)stile.Attribute(X + "Key") is not { } chiave) continue;

                yield return (Path.GetFileName(percorso), chiave, stile);
            }
        }
    }

    [Fact]
    public void CiSonoStiliDiCellaDaVerificare()
    {
        // Se il censimento restasse vuoto — un file spostato, una regola cambiata — le
        // prove qui sotto passerebbero senza guardare niente.
        Assert.True(StiliDiCella().Count() >= 6, "Gli stili di cella con chiave sono spariti o non si trovano piu'");
    }

    [Fact]
    public void OgniStileDiCellaEstendeQuelloDelTema()
    {
        var senza = StiliDiCella()
            .Where(s => (string?)s.Stile.Attribute("BasedOn") is not { } base_
                        || !base_.Contains("x:Type DataGridCell", StringComparison.Ordinal))
            .Select(s => $"{s.File}: {s.Chiave}")
            .ToList();

        Assert.True(
            senza.Count == 0,
            "Stili di cella senza BasedOn sullo stile del tema: la riga selezionata perde la cella. "
            + string.Join("; ", senza));
    }

    [Fact]
    public void LoStileDelTemaCheGliAltriEstendonoEsisteEHaLaSelezione()
    {
        var stili = XDocument.Load(Path.Combine(CartellaApp(), "Temi", "Stili.xaml"));

        var delTema = stili.Descendants(P + "Style")
            .Single(s => (string?)s.Attribute("TargetType") == "DataGridCell" && s.Attribute(X + "Key") is null);

        // E' quello che dice come appare una cella selezionata: se perdesse il trigger,
        // estenderlo non servirebbe a niente.
        Assert.Contains(
            delTema.Descendants(P + "Trigger"),
            t => (string?)t.Attribute("Property") == "IsSelected");
    }
}
