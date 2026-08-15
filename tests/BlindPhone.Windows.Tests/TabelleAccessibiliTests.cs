using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica le due dichiarazioni senza le quali una tabella WPF diventa ostile a chi
/// la percorre con lo screen reader.
/// </summary>
/// <remarks>
/// <para>
/// <b>Colonne generate in automatico.</b> Se non lo si vieta, WPF aggiunge alle
/// colonne dichiarate una colonna per ogni proprietà pubblica della riga: nella
/// tabella dei tasti rapidi comparivano anche <c>Info</c>, <c>Command</c>,
/// <c>Description</c>, <c>LocalSpeech</c>, <c>GlobalSpeech</c>, con i nomi interni
/// del codice come intestazione. Chi guarda vede colonne di troppo; chi si muove a
/// tastiera le attraversa una per una, e si sente ripetere la stessa combinazione
/// prima in forma compatta e poi in forma pronunciabile.
/// </para>
/// <para>
/// <b>Celle modificabili.</b> Una tabella non dichiarata in sola lettura espone ogni
/// cella come campo di testo modificabile. Misurato con un client di accessibilità —
/// la stessa interfaccia che usano NVDA e JAWS — le celle risultavano
/// <c>[VALORE MODIFICABILE]</c> con azione di attivazione; dichiarando
/// <c>IsReadOnly</c> diventano <c>[valore in sola lettura]</c>. È la differenza fra
/// uno screen reader che entra in modalità modulo e prova a scrivere nella cella, e
/// uno che legge il valore e lascia passare Invio all'applicazione: la riassegnazione
/// delle scorciatoie si fa aprendo la finestra di cattura, non scrivendo nella cella.
/// </para>
/// </remarks>
public sealed class TabelleAccessibiliTests
{
    private static IEnumerable<(string File, string Apertura)> Tabelle()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var cartella = Path.Combine(directory.FullName, "src", "BlindPhone.App");
            if (Directory.Exists(cartella))
            {
                foreach (var file in Directory.GetFiles(cartella, "*.xaml"))
                {
                    var xaml = File.ReadAllText(file);

                    // Solo l'elemento DataGrid: non DataGrid.Columns ne' DataGridTextColumn.
                    foreach (Match m in Regex.Matches(xaml, @"<DataGrid[ \r\n][^>]*?>", RegexOptions.Singleline))
                    {
                        yield return (Path.GetFileName(file), m.Value);
                    }
                }

                yield break;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/BlindPhone.App non trovata");
    }

    [Fact]
    public void CeNeSonoDaVerificare()
    {
        // Se la ricerca smettesse di trovare le tabelle, i test qui sotto
        // passerebbero a vuoto.
        Assert.NotEmpty(Tabelle());
    }

    [Fact]
    public void NessunaTabellaGeneraLeColonneDaSola()
    {
        Assert.All(Tabelle(), t => Assert.True(
            t.Apertura.Contains("AutoGenerateColumns=\"False\"", StringComparison.Ordinal),
            $"La tabella in {t.File} non vieta le colonne generate in automatico: "
            + "chi si muove a tastiera se le ritrova tutte da attraversare."));
    }

    [Fact]
    public void NessunaTabellaEspoLeCelleComeModificabili()
    {
        Assert.All(Tabelle(), t => Assert.True(
            t.Apertura.Contains("IsReadOnly=\"True\"", StringComparison.Ordinal),
            $"La tabella in {t.File} non e' dichiarata in sola lettura: lo screen reader "
            + "espone le celle come campi da compilare e non lascia passare Invio."));
    }

    [Fact]
    public void NessunaTabellaMostraLaRigaVuotaInFondo()
    {
        // La riga segnaposto per l'inserimento e' una riga vuota in coda alla tabella:
        // chi guarda la ignora, chi arriva con le frecce ci finisce dentro e non
        // capisce che cosa sia.
        Assert.All(Tabelle(), t => Assert.True(
            t.Apertura.Contains("CanUserAddRows=\"False\"", StringComparison.Ordinal),
            $"La tabella in {t.File} lascia la riga segnaposto per l'inserimento."));
    }
}
