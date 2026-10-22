using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che la banda del motore di esecuzione resti governata da un solo
/// interruttore.
/// </summary>
/// <remarks>
/// <para>
/// La soluzione sta su <b>.NET 10 e basta</b>. La banda di ritorno alla 9 è stata
/// rimossa a settembre 2026: SIPSorcery ha smesso di multi-targettare dalla 10.0.16, e
/// tenerla aperta avrebbe significato restare indietro sullo stack SIP, cioè sul
/// componente che raccoglie gli aggiornamenti di sicurezza più rilevanti del prodotto.
/// </para>
/// <para>
/// <b>L'interruttore resta, e questi test con lui.</b> Nessun progetto scrive il
/// proprio target né la versione dei pacchetti legati al motore di esecuzione: li
/// prende da <c>Directory.Build.props</c>. Serve al prossimo salto di banda — la .NET
/// 11 — che così si fa cambiando due righe invece di undici file. Un
/// <c>net10.0-windows</c> scritto a mano in un csproj è il modo in cui quel salto si
/// trasforma in una soluzione mezza su una banda e mezza sull'altra.
/// </para>
/// </remarks>
public sealed class BandaDotNetTests
{
    private static DirectoryInfo Soluzione()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlindPhone.sln"))) return directory;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("BlindPhone.sln non trovata");
    }

    private static IEnumerable<(string Nome, string Testo)> Progetti()
    {
        var radice = Soluzione();

        foreach (var cartella in new[] { "src", "tests", "tools" })
        {
            var percorso = Path.Combine(radice.FullName, cartella);
            if (!Directory.Exists(percorso)) continue;

            foreach (var file in Directory.GetFiles(percorso, "*.csproj", SearchOption.AllDirectories))
            {
                yield return (Path.GetFileName(file), File.ReadAllText(file));
            }
        }
    }

    /// <summary>Pacchetti versionati insieme al motore di esecuzione.</summary>
    private static readonly string[] LegatiAlRuntime =
    [
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Logging.Console",
        "Microsoft.Extensions.Logging.Debug",
        "System.Speech",
        "System.DirectoryServices.Protocols",
        "System.Security.Cryptography.ProtectedData",
        "Microsoft.Data.Sqlite.Core",
    ];

    [Fact]
    public void CeNeSonoDaVerificare()
    {
        // Se la ricerca smettesse di trovare i progetti, i test qui sotto
        // passerebbero a vuoto.
        Assert.NotEmpty(Progetti());
    }

    [Fact]
    public void NessunProgettoScriveIlProprioTargetAMano()
    {
        Assert.All(Progetti(), p =>
        {
            var m = Regex.Match(p.Testo, @"<TargetFramework>([^<]+)</TargetFramework>");

            Assert.True(m.Success, $"{p.Nome} non dichiara un TargetFramework.");
            Assert.True(m.Groups[1].Value.StartsWith("$(BandaDotNet)", StringComparison.Ordinal),
                $"{p.Nome} scrive il target a mano ({m.Groups[1].Value}): "
                + "al prossimo cambio di banda resterebbe indietro rispetto agli altri.");
        });
    }

    [Fact]
    public void IPacchettiLegatiAlRuntimeSeguonoLaBanda()
    {
        Assert.All(Progetti(), p =>
        {
            foreach (Match m in Regex.Matches(
                         p.Testo, @"PackageReference Include=""([^""]+)"" Version=""([^""]+)"""))
            {
                if (!LegatiAlRuntime.Contains(m.Groups[1].Value, StringComparer.Ordinal)) continue;

                Assert.True(m.Groups[2].Value == "$(BandaPacchetti)",
                    $"{p.Nome} fissa {m.Groups[1].Value} alla versione {m.Groups[2].Value}: "
                    + "e' un pacchetto che segue il motore di esecuzione e deve seguire la banda.");
            }
        });
    }

    [Fact]
    public void LaBandaEDichiarataInUnPostoSolo()
    {
        var props = File.ReadAllText(Path.Combine(Soluzione().FullName, "Directory.Build.props"));

        Assert.Contains("<BandaDotNet Condition=", props, StringComparison.Ordinal);
        Assert.Contains("<BandaPacchetti Condition=", props, StringComparison.Ordinal);
        Assert.Contains("net10.0", props, StringComparison.Ordinal);
    }

    /// <summary>
    /// Della banda .NET 9 non deve restare traccia nella configurazione di build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non è pedanteria: un ramo condizionale dimenticato in un csproj continuerebbe a
    /// essere valutato, e chi lo trovasse fra sei mesi avrebbe ragione di credere che
    /// il ritorno alla 9 sia ancora praticabile. Non lo è — SIPSorcery dalla 10.0.16
    /// pretende .NET 10 — e scoprirlo provandolo costa una giornata.
    /// </para>
    /// <para>
    /// Il controllo guarda la sola configurazione di build. I documenti e i commenti
    /// che <i>raccontano</i> la rimozione possono e devono nominarla.
    /// </para>
    /// </remarks>
    [Fact]
    public void DellaBandaNoveNonRestaTracciaNeiProgetti()
    {
        var props = File.ReadAllText(Path.Combine(Soluzione().FullName, "Directory.Build.props"));

        Assert.DoesNotContain("net9.0", SenzaCommenti(props), StringComparison.OrdinalIgnoreCase);

        Assert.All(Progetti(), p =>
            Assert.False(
                SenzaCommenti(p.Testo).Contains("net9.0", StringComparison.OrdinalIgnoreCase),
                $"{p.Nome} nomina ancora la banda net9.0, che non esiste piu'."));
    }

    /// <summary>
    /// Toglie i commenti XML, lasciando il solo MSBuild che viene valutato.
    /// </summary>
    /// <param name="testo">Contenuto di un file di progetto o di proprietà.</param>
    /// <remarks>
    /// Serve al controllo qui sopra, ed è la ragione per cui quel controllo è
    /// applicabile: i commenti <b>devono</b> poter nominare la banda rimossa, perché
    /// senza raccontare perché non c'è più si ripresenterebbe alla prima occasione.
    /// Quello che non deve restare è un ramo condizionale che MSBuild valuta davvero.
    /// </remarks>
    private static string SenzaCommenti(string testo)
        => Regex.Replace(testo, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
}
