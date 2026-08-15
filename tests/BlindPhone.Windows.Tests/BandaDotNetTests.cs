using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che la banda del motore di esecuzione resti governata da un solo
/// interruttore.
/// </summary>
/// <remarks>
/// <para>
/// La soluzione è passata a .NET 10, che è LTS, ma il ritorno alla 9 deve restare
/// praticabile: è la rete di sicurezza di un prodotto installato su postazioni di un
/// ente pubblico, dove un guasto scoperto in reparto non si risolve aspettando la
/// prossima versione.
/// </para>
/// <para>
/// Il ritorno funziona solo se <b>nessun progetto scrive il proprio target a mano</b>:
/// basta un <c>net10.0-windows</c> dimenticato in un csproj perché
/// <c>-p:BandaDotNet=net9.0</c> produca una soluzione mezza sulla 9 e mezza sulla 10,
/// che non si compone. Lo stesso vale per i pacchetti versionati insieme al motore di
/// esecuzione: la loro versione arriva da <c>BandaPacchetti</c>, e scriverla a mano
/// significherebbe montare assembly di una banda dentro un pacchetto dell'altra.
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
                + "con -p:BandaDotNet=net9.0 resterebbe indietro rispetto agli altri.");
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
    public void LeDueBandeSonoDichiarateEntrambe()
    {
        // Senza il ramo della 9 il ritorno indietro compilerebbe con i pacchetti
        // della 10 su un target net9.0, che e' proprio la mescolanza da evitare.
        var props = File.ReadAllText(Path.Combine(Soluzione().FullName, "Directory.Build.props"));

        Assert.Contains("<BandaDotNet Condition=", props, StringComparison.Ordinal);
        Assert.Contains("'$(BandaDotNet)' == 'net9.0'", props, StringComparison.Ordinal);
        Assert.Contains("net10.0", props, StringComparison.Ordinal);
    }
}
