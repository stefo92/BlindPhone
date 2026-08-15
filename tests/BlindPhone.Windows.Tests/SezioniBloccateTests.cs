using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che una sezione bloccata dal criterio si faccia riconoscere come tale.
/// </summary>
/// <remarks>
/// <para>
/// Il contenuto di una scheda bloccata viene disattivato, e un contenitore
/// disattivato in Windows non risponde né al TAB né al mouse. Senza una parola che lo
/// dica, il blocco è indistinguibile da un guasto: è il motivo per cui la tabella dei
/// tasti rapidi risultava «non accessibile nemmeno col puntatore».
/// </para>
/// <para>
/// La parola «(bloccata)» nell'etichetta della scheda non basta: la si sente
/// scorrendo le schede con Control TAB, non arrivando alla scheda da un'altra strada
/// e non provando a usarla.
/// </para>
/// </remarks>
public sealed class SezioniBloccateTests
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

    [Fact]
    public void IlCambioDiSchedaVieneOsservato()
    {
        var xaml = Sorgente("SettingsWindow.xaml");

        var tabs = xaml.IndexOf("x:Name=\"Tabs\"", StringComparison.Ordinal);
        var apertura = xaml[tabs..xaml.IndexOf('>', tabs)];

        Assert.Contains("SelectionChanged=\"OnTabsSelectionChanged\"", apertura, StringComparison.Ordinal);
    }

    [Fact]
    public void LAnnuncioDiceCheEBloccataEComeSbloccarla()
    {
        // Dire che non risponde senza dire come rimediare lascia l'operatore davanti
        // a un pannello muto: la via d'uscita e' il pulsante di sblocco, e va nominato.
        var codice = Sorgente("SettingsWindow.xaml.cs");

        var inizio = codice.IndexOf("private void OnTabsSelectionChanged", StringComparison.Ordinal);
        Assert.True(inizio > 0, "Gestore del cambio di scheda non trovato");

        var corpo = codice[inizio..(inizio + 2000)];

        Assert.Contains("bloccata dall'amministratore", corpo, StringComparison.Ordinal);
        Assert.Contains("Sblocca o riblocca", corpo, StringComparison.Ordinal);

        // Il messaggio dell'amministratore dice a chi rivolgersi: e' la parte utile.
        Assert.Contains("Note", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public void GliElenchiDentroLeSchedeNonFannoPartireLAnnuncio()
    {
        // Un TabControl riceve il SelectionChanged di ogni ComboBox e di ogni elenco
        // che contiene. Misurato con un banco di prova: cambiando scheda l'origine e'
        // il TabControl, scegliendo una voce in una casella e' la ComboBox. Senza il
        // filtro, l'annuncio partirebbe a ogni scelta di un dispositivo audio.
        var codice = Sorgente("SettingsWindow.xaml.cs");

        var inizio = codice.IndexOf("private void OnTabsSelectionChanged", StringComparison.Ordinal);
        var corpo = codice[inizio..(inizio + 2000)];

        Assert.Contains("ReferenceEquals(e.OriginalSource", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public void LAnnuncioNonPartePerLeSezioniLibere()
    {
        // Su una postazione senza criterio - il caso normale - il cambio di scheda
        // non deve dire niente: sarebbe una frase a ogni Control TAB.
        var codice = Sorgente("SettingsWindow.xaml.cs");

        var inizio = codice.IndexOf("private void OnTabsSelectionChanged", StringComparison.Ordinal);
        var corpo = codice[inizio..(inizio + 2000)];

        Assert.Contains("if (_policy.CanEdit(sezione)) return;", corpo, StringComparison.Ordinal);
    }
}
