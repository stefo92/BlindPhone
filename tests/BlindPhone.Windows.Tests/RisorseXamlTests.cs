using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che ogni risorsa richiamata dalle finestre esista davvero, in tutti i temi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il compilatore non se ne accorge, e i due modi di sbagliare hanno sintomi
/// diversi.</b> Un <c>{StaticResource NomeSbagliato}</c> diventa un'eccezione quando
/// la finestra viene costruita: il posto operatore non si apre. Un
/// <c>{DynamicResource NomeSbagliato}</c> non solleva niente: la proprietà resta al
/// valore predefinito, e a schermo compare testo nero su fondo nero oppure un
/// riquadro invisibile. Il secondo è peggiore, perché nessun collaudo automatico lo
/// nota.
/// </para>
/// <para>
/// <b>E una chiave può esistere in un tema e mancare in un altro.</b> I colori vivono
/// in tre dizionari — chiaro, scuro e di sistema — e un pennello dichiarato solo nel
/// primo produce un difetto che si vede soltanto a chi ha scelto il tema scuro. Per
/// questo i tre elenchi di chiavi vengono confrontati fra loro.
/// </para>
/// <para>
/// Si leggono i file XAML come testo e non l'albero WPF: serve proprio a non dover
/// avviare l'interfaccia per sapere se regge.
/// </para>
/// </remarks>
public sealed class RisorseXamlTests
{
    /// <summary>Dichiarazioni: x:Key="NomeRisorsa".</summary>
    private static readonly Regex Definite = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);

    /// <summary>Richiami, di entrambe le forme.</summary>
    private static readonly Regex Richiamate =
        new(@"\{(?:Static|Dynamic)Resource\s+([^}]+)\}", RegexOptions.Compiled);

    /// <summary>Colori scritti a mano: Color="#RRGGBB".</summary>
    private static readonly Regex ColoriLetterali = new(@"Color=""#", RegexOptions.Compiled);

    private static readonly string[] Temi = ["Chiaro", "Scuro", "Sistema"];

    /// <summary>Cartella dell'applicazione, cercata risalendo fino alla soluzione.</summary>
    private static string CartellaFinestre()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlindPhone.sln")))
            {
                return Path.Combine(directory.FullName, "src", "BlindPhone.App");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Radice della soluzione non trovata a partire da " + AppContext.BaseDirectory);
    }

    private static string PercorsoTema(string nome)
        => Path.Combine(CartellaFinestre(), "Temi", nome + ".xaml");

    private static HashSet<string> Chiavi(string percorso)
        => [.. Definite.Matches(File.ReadAllText(percorso)).Select(m => m.Groups[1].Value)];

    private static IEnumerable<string> Riferimenti(string percorso)
        => Richiamate.Matches(File.ReadAllText(percorso))
            .Select(m => m.Groups[1].Value.Trim())

            // I richiami annidati come {DynamicResource {x:Static SystemColors...}}
            // non nominano una risorsa del prodotto: li risolve Windows.
            .Where(nome => !nome.StartsWith('{'))
            .Distinct(StringComparer.Ordinal);

    [Fact]
    public void OgniRisorsaRichiamataDalleFinestreEsiste()
    {
        var cartella = CartellaFinestre();

        // Disponibili ovunque: le forme di Temi\Stili piu' i pennelli del tema. Si
        // usa il tema chiaro come riferimento, e un'altra prova garantisce che gli
        // altri due dichiarino esattamente le stesse chiavi.
        var comuni = Chiavi(PercorsoTema("Stili"));
        comuni.UnionWith(Chiavi(PercorsoTema("Chiaro")));

        var mancanti = new List<string>();

        foreach (var finestra in Directory.GetFiles(cartella, "*.xaml"))
        {
            if (Path.GetFileName(finestra) == "App.xaml") continue;

            // Le risorse dichiarate dentro la finestra stessa valgono quanto quelle
            // comuni: molte finestre hanno stili propri in Window.Resources.
            var disponibili = new HashSet<string>(comuni, StringComparer.Ordinal);
            disponibili.UnionWith(Chiavi(finestra));

            mancanti.AddRange(
                Riferimenti(finestra)
                    .Where(nome => !disponibili.Contains(nome))
                    .Select(nome => $"{Path.GetFileName(finestra)} richiama {nome}"));
        }

        // Anche il dizionario delle forme richiama se' stesso e i pennelli del tema.
        mancanti.AddRange(
            Riferimenti(PercorsoTema("Stili"))
                .Where(nome => !comuni.Contains(nome))
                .Select(nome => $"Stili.xaml richiama {nome}"));

        Assert.Empty(mancanti);
    }

    [Fact]
    public void ITreTemiDichiaranoLeStesseChiavi()
    {
        // E' la prova che protegge dal difetto invisibile: un pennello aggiunto al
        // tema chiaro e dimenticato in quello scuro non rompe niente in compilazione
        // e non si vede finche' qualcuno non sceglie il tema scuro.
        var riferimento = Chiavi(PercorsoTema("Chiaro"));
        var differenze = new List<string>();

        foreach (var tema in Temi.Skip(1))
        {
            var chiavi = Chiavi(PercorsoTema(tema));

            differenze.AddRange(riferimento.Except(chiavi).Select(k => $"{tema} non dichiara {k}"));
            differenze.AddRange(chiavi.Except(riferimento).Select(k => $"{tema} dichiara {k} in piu'"));
        }

        Assert.Empty(differenze);
    }

    [Fact]
    public void ITemiDichiaranoTuttiIPennelliCheServono()
    {
        string[] attesi =
        [
            "SurfaceBrush", "CanvasBrush", "CardBrush", "HairlineBrush",
            "TextBrush", "MutedTextBrush", "DisabledTextBrush", "AccentBrush",
            "ControlBrush", "ControlTextBrush", "ControlBorderBrush",
            "ControlHoverBrush", "ControlPressedBrush", "ControlDisabledBrush",
            "FieldBrush", "FieldBorderBrush",
            "SelectionBrush", "SelectionTextBrush", "HoverRowBrush", "HeaderBrush",
        ];

        var chiavi = Chiavi(PercorsoTema("Chiaro"));

        Assert.All(attesi, nome => Assert.Contains(nome, chiavi));
    }

    [Fact]
    public void LeScaleComuniSonoTutteDichiarate()
    {
        // Sono le misure su cui poggia l'aspetto di tutte le finestre. Se una sparisce
        // in un riordino, questa prova lo dice prima che se ne accorga chi apre il
        // programma.
        var chiavi = Chiavi(PercorsoTema("Stili"));

        string[] attese =
        [
            "SpaceWindow", "SpaceCard", "SpaceStack", "SpaceStackTight",
            "RadiusCard", "RadiusControl",
            "FontCaption", "FontBody", "FontSection", "FontProminent",
            "FontBanner", "FontHeadline",
            "Card", "FocusVisual",
        ];

        Assert.All(attese, nome => Assert.Contains(nome, chiavi));
    }

    [Fact]
    public void IlTemaDiSistemaNonScriveNessunColoreAMano()
    {
        // E' il tema che regge il contrasto elevato: ogni colore deve venire da
        // Windows. Un valore scritto qui sarebbe esattamente cio' che rompe la
        // configurazione su misura di chi il contrasto elevato lo usa davvero.
        var testo = File.ReadAllText(PercorsoTema("Sistema"));

        Assert.DoesNotMatch(ColoriLetterali, testo);
    }

    [Fact]
    public void GliStiliComuniNonScrivonoColoriAMano()
    {
        // I colori stanno nei temi, le forme in Temi\Stili. Un colore scritto fra gli
        // stili sopravviverebbe al cambio di tema e resterebbe l'unico elemento fuori
        // posto, chiaro su fondo scuro o viceversa.
        var testo = File.ReadAllText(PercorsoTema("Stili"));

        Assert.DoesNotMatch(ColoriLetterali, testo);
    }
}
