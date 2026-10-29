using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica il rifiuto dei percorsi di rete nei valori di configurazione.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché un percorso di rete in una configurazione è un problema.</b> Un valore
/// come <c>\\host\share\suoneria.wav</c> non è soltanto un file che potrebbe non
/// esserci: aprirlo fa partire un'autenticazione verso <c>host</c>, che riceve così
/// un tentativo di accesso con l'identità dell'operatore. Chi controlla quell'host lo
/// raccoglie senza dover fare altro, e gli è bastato scrivere una riga in un
/// pacchetto di configurazione o in <c>settings.json</c>.
/// </para>
/// <para>
/// Lo stesso percorso si scrive in più modi, ed è il motivo per cui questa classe
/// elenca tanti casi: una difesa che riconosce solo <c>\\host\share</c> è una difesa
/// che si aggira scrivendo <c>//host/share</c>.
/// </para>
/// </remarks>
public sealed class PercorsiDiReteTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnValoreAssenteNonEUnPercorsoDaRifiutare(string? percorso)
    {
        // "Nessuna suoneria scelta" non e' un percorso di rete: rifiutarlo
        // trasformerebbe un campo vuoto in un avviso su ogni avvio.
        Assert.True(ProtectedLocation.IsLocalPath(percorso));
    }

    [Theory]
    [InlineData(@"C:\suoni\suoneria.wav")]
    [InlineData(@"C:\Program Files\BlindPhone\squillo.wav")]
    public void UnPercorsoLocaleVieneAccettato(string percorso)
    {
        Assert.True(ProtectedLocation.IsLocalPath(percorso));
    }

    [Theory]
    [InlineData(@"\\host\share\suoneria.wav")]
    [InlineData("//host/share/suoneria.wav")]
    [InlineData(@"\\?\UNC\host\share\suoneria.wav")]
    [InlineData(@"\\127.0.0.1\c$\suoneria.wav")]
    [InlineData(@"\\host")]
    public void UnPercorsoDiReteVieneRifiutato(string percorso)
    {
        Assert.False(ProtectedLocation.IsLocalPath(percorso));
    }

    [Theory]
    [InlineData("http://host/suoneria.wav")]
    [InlineData("https://host/suoneria.wav")]
    [InlineData("ftp://host/suoneria.wav")]
    [InlineData("file://host/share/suoneria.wav")]
    public void UnIndirizzoNonEUnPercorsoLocale(string percorso)
    {
        // Non e' un caso teorico: un campo che accetta un percorso accetta anche
        // quello che l'operatore vi incolla, e un pacchetto vi scrive quello che
        // vuole.
        Assert.False(ProtectedLocation.IsLocalPath(percorso));
    }

    [Fact]
    public void UnPercorsoLocaleScrittoComeIndirizzoVieneAccettato()
    {
        // file:///C:/... indica un file locale: rifiutarlo sarebbe severita' senza
        // guadagno.
        Assert.True(ProtectedLocation.IsLocalPath("file:///C:/suoni/suoneria.wav"));
    }

    [Fact]
    public void UnPercorsoNonInterpretabileVieneRifiutato()
    {
        // Un percorso che non si riesce nemmeno a normalizzare non e' un percorso
        // locale di cui ci si possa fidare: in dubbio si risponde "no".
        Assert.False(ProtectedLocation.IsLocalPath("C:\\suoni\\suoneria\0.wav"));
    }
}
