using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che la scelta dei codec stia nella scheda a cui appartiene.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non è pignoleria sull'ordine delle schede: decide se il comando si può usare
/// o no.</b> Il pannello si blocca per sezioni, e il blocco lavora sul contenuto
/// della scheda. In WPF <c>IsEnabled</c> si eredita: dentro un contenitore
/// disattivato un figlio resta disattivato comunque, e non c'è modo di riabilitarlo
/// dall'interno.
/// </para>
/// <para>
/// L'ordine dei codec viene salvato in <c>Sip.CodecPreference</c>, insieme
/// all'account: è una impostazione dell'account SIP. Finché è stato disegnato nella
/// scheda Audio, una postazione con la sezione «audio» bloccata e quella degli
/// account aperta si è ritrovata con quel comando inerte — l'operatore poteva
/// cambiare l'account ma non come viaggia la voce. È successo dal 9 agosto 2026,
/// quando la postazione dell'ente ha ricevuto quel criterio.
/// </para>
/// </remarks>
public sealed class PannelloCodecTests
{
    private static string Pannello()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidato = Path.Combine(
                directory.FullName, "src", "BlindPhone.App", "SettingsWindow.xaml");

            if (File.Exists(candidato)) return File.ReadAllText(candidato);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("SettingsWindow.xaml non trovato");
    }

    /// <summary>Intervallo di testo occupato da una scheda del pannello.</summary>
    private static (int Inizio, int Fine) Scheda(string xaml, string nome)
    {
        var inizio = xaml.IndexOf($"x:Name=\"{nome}\"", StringComparison.Ordinal);
        Assert.True(inizio > 0, $"Scheda {nome} non trovata");

        var successiva = xaml.IndexOf("<TabItem", inizio, StringComparison.Ordinal);
        return (inizio, successiva < 0 ? xaml.Length : successiva);
    }

    [Fact]
    public void LOrdineDeiCodecStaNellaSchedaDellAccountSip()
    {
        var xaml = Pannello();
        var (inizio, fine) = Scheda(xaml, "SipTab");

        var codec = xaml.IndexOf("x:Name=\"CodecList\"", StringComparison.Ordinal);

        Assert.True(codec > inizio && codec < fine,
            "L'elenco dei codec deve stare nella scheda Account SIP: e' li' che l'impostazione viene "
            + "salvata, ed e' il criterio di quella sezione che deve poterla bloccare o aprire.");
    }

    [Fact]
    public void NellaSchedaAudioRestanoSoloIDispositivi()
    {
        var xaml = Pannello();
        var (inizio, fine) = Scheda(xaml, "AudioTab");
        var audio = xaml[inizio..fine];

        Assert.DoesNotContain("CodecList", audio, StringComparison.Ordinal);
        Assert.DoesNotContain("CodecUpButton", audio, StringComparison.Ordinal);
        Assert.DoesNotContain("CodecResetButton", audio, StringComparison.Ordinal);
    }

    [Fact]
    public void ITreComandiDellOrdineCiSonoTutti()
    {
        // Spuntare un codec non basta: la gerarchia si costruisce spostandolo, ed e'
        // la parte che il centralino usa davvero — sceglie il primo dell'elenco che
        // sa gestire.
        var xaml = Pannello();

        foreach (var nome in new[] { "CodecList", "CodecUpButton", "CodecDownButton", "CodecResetButton" })
        {
            Assert.Contains($"x:Name=\"{nome}\"", xaml, StringComparison.Ordinal);
        }

        // Gli spostamenti devono essere raggiungibili da tastiera senza il mouse:
        // Alt piu' freccia, dichiarato nel testo di aiuto dell'elenco.
        Assert.Contains("Alt freccia su e Alt freccia giu'", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void OgniPulsanteDellOrdineHaLaSuaScorciatoia()
    {
        // Il trattino basso nell'etichetta e' cio' che rende il pulsante raggiungibile
        // con Alt: su un pannello che si usa a tastiera, un pulsante senza non esiste.
        var xaml = Pannello();

        var etichette = Regex.Matches(xaml, @"x:Name=""Codec(Up|Down|Reset)Button""[^>]*?Content=""([^""]+)""",
            RegexOptions.Singleline);

        Assert.Equal(3, etichette.Count);
        Assert.All(etichette, m => Assert.Contains('_', m.Groups[2].Value));
    }

    [Fact]
    public void LElencoDeiCodecNonHaUnAltezzaFissa()
    {
        // Un'altezza in pixel vale finche' non cambia l'altezza delle righe. E'
        // cambiata ridisegnando i controlli: le righe sono cresciute, la quarta voce
        // non e' piu' entrata nei 150 pixel dichiarati ed e' comparsa una barra di
        // scorrimento. Chi guardava vedeva il primo codec numerato «2» e concludeva,
        // ragionevolmente, che il numero uno fosse sparito.
        //
        // Un elenco di quattro voci non deve scorrere: si dimensiona sul contenuto,
        // con un tetto massimo come rete di sicurezza per chi ingrandisce il testo.
        var xaml = Pannello();

        var elenco = xaml.IndexOf("x:Name=\"CodecList\"", StringComparison.Ordinal);
        var fine = xaml.IndexOf('>', elenco);
        var apertura = xaml[elenco..fine];

        Assert.DoesNotContain("Height=\"", apertura.Replace("MaxHeight=\"", string.Empty), StringComparison.Ordinal);
        Assert.Contains("MaxHeight=", apertura, StringComparison.Ordinal);
    }
}
