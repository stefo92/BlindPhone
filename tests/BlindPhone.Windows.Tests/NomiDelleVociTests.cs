using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che nessuna voce di elenco o riga di tabella si presenti allo screen
/// reader con il nome della propria classe.
/// </summary>
/// <remarks>
/// <para>
/// WPF non mette un nome accessibile sul contenitore di una voce: quando non lo
/// trova, ricade sul risultato di <c>ToString</c>, che di suo restituisce il nome
/// completo del tipo. È il difetto per cui, spostandosi fra i campi della rubrica,
/// l'operatore si sentiva annunciare «BlindPhone punto App punto Dialer Field Entry»
/// prima del nome di ogni campo.
/// </para>
/// <para>
/// Misurato sull'albero di accessibilità con la stessa interfaccia usata da NVDA e
/// JAWS: prima <c>ListItem nome: "BlindPhone.App.DialerFieldEntry"</c>, dopo
/// <c>ListItem nome: "Stato, mostrato, posizione 1"</c>.
/// </para>
/// </remarks>
public sealed class NomiDelleVociTests
{
    private static string Sorgente(string nome)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(directory.FullName, "src", "BlindPhone.App", nome);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{nome} non trovato");
    }

    /// <summary>Classi che finiscono dentro un elenco o una tabella, con il file che le contiene.</summary>
    public static TheoryData<string, string> Voci => new()
    {
        { "SettingsWindow.xaml.cs", "HotkeyRow" },
        { "SettingsWindow.xaml.cs", "CodecEntry" },
        { "SettingsWindow.xaml.cs", "DialerFieldEntry" },
        { "MainWindow.xaml.cs", "DialerSuggestion" },
        { "ContactsWindow.xaml.cs", "ContactRow" },
        { "CallHistoryWindow.xaml.cs", "CallHistoryRow" },
    };

    [Theory]
    [MemberData(nameof(Voci))]
    public void OgniVoceDiceQualcosaDiSensatoAlPostoDelNomeDellaClasse(string file, string classe)
    {
        var codice = Sorgente(file);

        // Alcune voci sono record e altre classi: cambia la sintassi, non il difetto.
        var dichiarazione = Regex.Match(codice, $@"\b(class|record) {classe}\b");
        Assert.True(dichiarazione.Success, $"Tipo {classe} non trovato in {file}");

        var inizio = dichiarazione.Index;

        // Fino al tipo successivo, oppure alla fine del file.
        var successiva = Regex.Match(codice[(inizio + 1)..], @"\b(class|record) \w");
        var fine = successiva.Success ? inizio + 1 + successiva.Index : codice.Length;

        Assert.Contains("override string ToString()", codice[inizio..fine], StringComparison.Ordinal);
    }

    [Fact]
    public void LaCasellaDentroLaVoceNonRipeteIlNomeDellaVoce()
    {
        // Il nome per esteso sta sulla voce dell'elenco, che e' l'elemento su cui
        // arriva il cursore. Dichiararlo anche sulla casella lo faceva pronunciare
        // due volte: prima la voce, poi la casella dentro.
        var xaml = Sorgente("SettingsWindow.xaml");

        foreach (var elenco in new[] { "CodecList", "DialerFieldList" })
        {
            var inizio = xaml.IndexOf($"x:Name=\"{elenco}\"", StringComparison.Ordinal);
            var fine = xaml.IndexOf("</ListBox>", inizio, StringComparison.Ordinal);

            Assert.DoesNotContain(
                "AutomationProperties.Name=\"{Binding AccessibleName}\"",
                xaml[inizio..fine],
                StringComparison.Ordinal);
        }
    }
}
