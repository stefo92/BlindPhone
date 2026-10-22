using System.Text.RegularExpressions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica i comandi con cui si scelgono e si ordinano i campi mostrati fra i
/// contatti proposti sopra il campo di composizione.
/// </summary>
/// <remarks>
/// <para>
/// <b>L'ordine è la parte che conta.</b> Chi legge la riga con lo screen reader la
/// percorre da sinistra a destra e si ferma quando ha saputo quello che gli serve:
/// il campo che decide — lo stato dell'interno, o il reparto dove i nominativi si
/// assomigliano — deve stare subito dopo il numero. Spuntare i campi senza poterli
/// spostare lascia quella scelta al catalogo invece che all'operatore.
/// </para>
/// <para>
/// <b>E deve essere raggiungibile da tastiera.</b> Il riordino per trascinamento è
/// il gesto previsto dalla maggior parte dei softphone ed è esattamente quello che
/// qui nessuno può fare: senza i tasti, la funzione non esiste per chi la usa.
/// </para>
/// </remarks>
public sealed class PannelloCampiRicercaTests
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

    [Fact]
    public void LaSceltaDeiCampiStaNellaSchedaRubrica()
    {
        // E' dove l'operatore va a cercarla: sono campi della rubrica, e la scheda
        // Rubrica e' l'unica che parla di quali dati i contatti portano con se'.
        var xaml = Pannello();

        var scheda = xaml.IndexOf("x:Name=\"ContactsTab\"", StringComparison.Ordinal);
        Assert.True(scheda > 0, "Scheda Rubrica non trovata");

        var successiva = xaml.IndexOf("<TabItem", scheda, StringComparison.Ordinal);
        var fine = successiva < 0 ? xaml.Length : successiva;

        var elenco = xaml.IndexOf("x:Name=\"DialerFieldList\"", StringComparison.Ordinal);

        Assert.True(elenco > scheda && elenco < fine,
            "L'elenco dei campi mostrati nella ricerca rapida deve stare nella scheda Rubrica.");
    }

    [Fact]
    public void ITreComandiDellOrdineCiSonoTutti()
    {
        // Spuntare un campo non basta: quello che cambia la fatica di lettura e'
        // dove il campo finisce nella riga.
        var xaml = Pannello();

        foreach (var nome in new[]
                 {
                     "DialerFieldList",
                     "DialerFieldUpButton",
                     "DialerFieldDownButton",
                     "DialerFieldResetButton",
                 })
        {
            Assert.Contains($"x:Name=\"{nome}\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OgniPulsanteDellOrdineHaLaSuaScorciatoia()
    {
        // Il trattino basso nell'etichetta e' cio' che rende il pulsante
        // raggiungibile con Alt: su un pannello che si usa a tastiera, un pulsante
        // senza non esiste.
        var xaml = Pannello();

        var etichette = Regex.Matches(
            xaml,
            @"x:Name=""DialerField(Up|Down|Reset)Button""[^>]*?Content=""([^""]+)""",
            RegexOptions.Singleline);

        Assert.Equal(3, etichette.Count);
        Assert.All(etichette, m => Assert.Contains('_', m.Groups[2].Value));
    }

    [Fact]
    public void LElencoRispondeAiTastiEDichiaraQuali()
    {
        // I tasti sono quelli dell'elenco dei codec: due elenchi che governano cose
        // diverse ma si usano allo stesso modo. E vanno dichiarati nel testo di
        // aiuto, altrimenti restano una funzione che c'e' ma non si scopre.
        var xaml = Pannello();

        var elenco = xaml.IndexOf("x:Name=\"DialerFieldList\"", StringComparison.Ordinal);
        var apertura = xaml[elenco..xaml.IndexOf('>', elenco)];

        Assert.Contains("KeyDown=\"OnDialerFieldListKeyDown\"", apertura, StringComparison.Ordinal);
        Assert.Contains("Alt freccia su e Alt freccia giu'", apertura, StringComparison.Ordinal);
        Assert.Contains("Barra spaziatrice", apertura, StringComparison.Ordinal);
    }

    [Fact]
    public void LElencoDeiCampiNonHaUnAltezzaFissa()
    {
        // Stessa lezione dell'elenco dei codec: un'altezza in pixel vale finche' non
        // cambia l'altezza delle righe, e quando e' cambiata e' comparsa una barra di
        // scorrimento che nascondeva la prima voce - con la numerazione che
        // cominciava da due e sembrava un campo sparito.
        var xaml = Pannello();

        var elenco = xaml.IndexOf("x:Name=\"DialerFieldList\"", StringComparison.Ordinal);
        var apertura = xaml[elenco..xaml.IndexOf('>', elenco)];

        Assert.DoesNotContain("Height=\"", apertura.Replace("MaxHeight=\"", string.Empty), StringComparison.Ordinal);
        Assert.Contains("MaxHeight=", apertura, StringComparison.Ordinal);
    }

    [Fact]
    public void LaVoceMostraLaPosizioneNonSoloLEtichetta()
    {
        // «Reparto» da solo non dice se il reparto si leggera' prima o dopo lo stato,
        // e chi ascolta non ha modo di dedurlo dalla riga: il numero di posizione fa
        // parte di cio' che il pannello deve dire.
        //
        // A schermo lo dice il contenuto della casella, che porta la posizione davanti
        // all'etichetta. Alla voce lo dice il nome accessibile, che pero' non si
        // dichiara piu' qui: arriva dalla voce dell'elenco, vedi NomiDelleVociTests.
        var xaml = Pannello();

        var elenco = xaml.IndexOf("x:Name=\"DialerFieldList\"", StringComparison.Ordinal);
        var chiusura = xaml.IndexOf("</ListBox>", elenco, StringComparison.Ordinal);
        var blocco = xaml[elenco..chiusura];

        Assert.Contains("{Binding Display}", blocco, StringComparison.Ordinal);
    }
}
