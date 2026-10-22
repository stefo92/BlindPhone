using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sulla guida in linea.
/// </summary>
/// <remarks>
/// <para>
/// Il difetto che questi test impediscono è una guida che invecchia: le combinazioni
/// citate nel testo devono essere quelle assegnate <b>adesso</b>, non quelle
/// predefinite. Una guida che dice "premi F9" a chi ha rimappato il trasferimento
/// manda a premere un tasto che non fa nulla, e lo fa proprio quando l'operatore è in
/// difficoltà con un chiamante in linea.
/// </para>
/// <para>
/// L'altro difetto è l'argomento vuoto: si aprirebbe una finestra che non dice
/// niente, e chi ascolta non ha modo di distinguerla da un guasto della sintesi.
/// </para>
/// </remarks>
public sealed class HelpBookTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 0, 0, TimeSpan.FromHours(2));

    /// <summary>
    /// Riunisce il testo della guida in una riga sola.
    /// </summary>
    /// <remarks>
    /// Il testo viene impaginato in righe corte, quindi una frase cercata può trovarsi
    /// a cavallo di due righe: cercarla nel testo grezzo darebbe un test che passa o
    /// fallisce a seconda di dove cade l'a capo, cioè a seconda della lunghezza del
    /// nome del tasto configurato.
    /// </remarks>
    private static string Piatto(HotkeyProfile profilo)
    {
        var righe = HelpBook.Build(profilo, Now)
            .SelectMany(t => t.Text.Split('\n'))
            .Select(r => r.Trim());

        return string.Join(' ', righe);
    }

    [Fact]
    public void NessunArgomentoEVuoto()
    {
        var vuoti = HelpBook.Build(HotkeyProfile.CreateDefault(), Now)
            .Where(t => string.IsNullOrWhiteSpace(t.Id)
                        || string.IsNullOrWhiteSpace(t.Title)
                        || string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Id)
            .ToList();

        Assert.True(vuoti.Count == 0, "Argomenti senza contenuto: " + string.Join(", ", vuoti));
    }

    [Fact]
    public void GliArgomentiRaggiungibiliDaiTastiEsistono()
    {
        var guida = HelpBook.Build(HotkeyProfile.CreateDefault(), Now);

        // Sono gli argomenti su cui la guida si apre da sola secondo il punto in cui
        // si trova il cursore: se un identificativo non esiste piu', F1 aprirebbe la
        // guida sull'indice senza dire perche'.
        string[] richiesti =
        [
            HelpBook.DefaultTopicId,
            HelpBook.ContactsTopicId,
            HelpBook.SettingsTopicId,
            HelpBook.DialerTopicId,
            HelpBook.QueueTopicId,
            HelpBook.HotkeysTopicId,
            HelpBook.ConferenceTopicId,
        ];

        foreach (var id in richiesti)
        {
            Assert.Contains(guida, t => t.Id == id);
        }
    }

    [Fact]
    public void GliIdentificativiSonoUnici()
    {
        var guida = HelpBook.Build(HotkeyProfile.CreateDefault(), Now);

        Assert.Equal(guida.Count, guida.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void LaGuidaCitaLeCombinazioniConfigurateNonQuellePredefinite()
    {
        var profilo = HotkeyProfile.CreateDefault();

        // Un operatore che ha spostato risposta e trasferimento su altri tasti.
        profilo.Set(new HotkeyBinding(HotkeyCommand.Answer, "F5", HotkeyModifiers.None, HotkeyScope.Local));
        profilo.Set(new HotkeyBinding(
            HotkeyCommand.Transfer, "T", HotkeyModifiers.Control, HotkeyScope.Local));

        var testo = Piatto(profilo);

        Assert.Contains("Rispondi: F5", testo, StringComparison.Ordinal);
        Assert.Contains("Trasferisci: Control T", testo, StringComparison.Ordinal);

        // E non deve piu' mandare a premere i tasti predefiniti per quei due comandi.
        Assert.DoesNotContain("Rispondi: F2", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("Trasferisci: Locale F9", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void UnComandoSenzaTastoVieneDettoInvecheTaciuto()
    {
        var profilo = HotkeyProfile.CreateDefault();
        profilo.Remove(HotkeyCommand.RedialLast, HotkeyScope.Local);

        // Sapere che una funzione esiste ed e' senza tasto e' cio' che porta ad
        // assegnarla: farla sparire dalla guida la renderebbe invisibile.
        Assert.Contains("nessun tasto assegnato", Piatto(profilo), StringComparison.Ordinal);
    }

    [Fact]
    public void LaGuidaContieneLElencoCompletoDelleScorciatoie()
    {
        var guida = HelpBook.Build(HotkeyProfile.CreateDefault(), Now);
        var elenco = guida.Single(t => t.Id == "elenco-tasti").Text;

        // E' la stessa scheda che si esporta in braille: se le due divergessero,
        // sarebbero due documenti che dicono cose diverse sullo stesso prodotto.
        foreach (var info in HotkeyCommandCatalog.All)
        {
            Assert.Contains(info.Label, elenco, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LaGuidaSpiegaComeSiApre()
        => Assert.Contains("F1", Piatto(HotkeyProfile.CreateDefault()), StringComparison.Ordinal);

    [Fact]
    public void LaModalitaSemplificataHaUnArgomentoCheSpiegaIQuattroTasti()
    {
        // Una modalita' di lavoro intera che non compare nella guida esiste solo per
        // chi era presente quando e' stata aggiunta.
        var argomento = HelpBook.Build(HotkeyProfile.CreateDefault(), Now)
            .Single(t => t.Id == HelpBook.KeypadTopicId).Text;

        foreach (var tasto in new[] { "PIÙ", "DIVISO", "INVIO", "MENO" })
        {
            Assert.Contains(tasto, argomento, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// La guida deve spiegare perché il trasferimento passa dall'ascolto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non basta elencare i tasti: chi ha usato la versione precedente ha imparato che
    /// un solo Invio bastava, e senza una riga che dica perché ora ce ne vogliono due
    /// la modifica si legge come un peggioramento e si cerca il modo di aggirarla.
    /// </para>
    /// <para>
    /// Le tre cose che devono esserci sono l'interno occupato, il tasto che riprende il
    /// chiamante, e il fatto che si possa svincolare anche mentre l'interno squilla.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("occupato", "non e' detto cosa fare quando l'interno e' occupato")]
    [InlineData("squillando", "non e' detto che si puo' svincolare prima che risponda")]
    [InlineData("riporta al chiamante", "non e' detto come si riprende chi sta aspettando")]
    public void LaGuidaSpiegaPercheIlTrasferimentoPassaDallAscolto(string frammento, string perche)
    {
        var argomento = HelpBook.Build(HotkeyProfile.CreateDefault(), Now)
            .Single(t => t.Id == HelpBook.KeypadTopicId).Text;

        Assert.True(argomento.Contains(frammento, StringComparison.OrdinalIgnoreCase), perche);
    }

    [Theory]
    [InlineData("tastierino", "la modalita' semplificata non e' citata da nessuna parte")]
    [InlineData("aggiornament", "gli aggiornamenti dal server non sono spiegati")]
    [InlineData("pulsanti", "la barra dei pulsanti non e' spiegata")]
    [InlineData("Alt F4", "non e' detto come si chiude davvero BlindPhone")]
    [InlineData("riunione Zoom", "non e' spiegato come si entra in una riunione")]
    [InlineData("codice numerico", "non e' detto quale dei due codici di Zoom serve, che e' l'errore piu' probabile")]
    [InlineData("nell'ordine giusto", "non e' detto che un interno digitato si cerca per uguaglianza e non per pezzi")]
    [InlineData("la voce tace", "non e' detto che con un numero senza voci in rubrica la voce resta in silenzio")]
    [InlineData("Elaborazione del microfono", "non e' detto dove si accendono il controllo del livello e la riduzione del rumore")]
    [InlineData("anche sulla chiamata in corso", "non e' detto che le due funzioni del microfono valgono subito")]
    [InlineData("finché non rispondi", "non e' detto che la suoneria dura fino alla risposta")]
    public void LaGuidaCopreLeFunzioniIntrodotteDopoIlPrimoRilascio(string frammento, string perche)
    {
        // Gate di copertura, come quello degli annunci: una funzione che arriva senza
        // una riga di guida resta invisibile a chi non era presente quando e' stata
        // scritta, ed e' esattamente chi la guida serve.
        Assert.True(
            Piatto(HotkeyProfile.CreateDefault()).Contains(frammento, StringComparison.OrdinalIgnoreCase),
            perche);
    }

    [Fact]
    public void IlTestoEImpaginatoInRigheCorte()
    {
        var lunghe = HelpBook.Build(HotkeyProfile.CreateDefault(), Now)
            .SelectMany(t => t.Text.Split('\n').Select(r => (t.Id, Riga: r.TrimEnd())))
            .Where(x => x.Riga.Length > 80)
            .Select(x => $"{x.Id}: {x.Riga}")
            .ToList();

        Assert.True(lunghe.Count == 0, "Righe troppo lunghe per un display braille:\n" + string.Join('\n', lunghe));
    }
}
