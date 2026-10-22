using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Come si riconosce che cosa risponde a una voce di rubrica.
/// </summary>
/// <remarks>
/// <para>
/// Gli esempi non sono inventati: sono descrizioni prese dalla rubrica di centralino
/// di un'azienda ospedaliero-universitaria, dove su duemilacinquantotto voci
/// seicentottantuno sono persone e tutte le altre sono ambulatori, stanze, apparecchi
/// fax e telefoni che squillano solo quando la telefonia ordinaria è interrotta.
/// </para>
/// <para>
/// Le regole vanno collaudate su quelle stringhe e non su esempi puliti: è la
/// punteggiatura irregolare, le maiuscole a caso e le parole attaccate a essere il
/// caso difficile.
/// </para>
/// </remarks>
public sealed class TipoDiVoceTests
{
    [Theory]
    [InlineData("FAX Accettazione")]
    [InlineData("Fax Ambulatorio Allergologico")]
    [InlineData("FAX - Segreteria")]
    [InlineData("Fax  Resp. s.s.d.")]
    [InlineData("FAX")]
    public void UnApparecchioFaxSiRiconosceDallaDescrizione(string descrizione)
    {
        // Comporre un numero di fax produce un portante che chi non vede riconosce solo
        // dopo averlo sentito, e nel frattempo la chiamata in attesa e' rimasta ferma.
        Assert.Equal(ContactKind.Fax, ContactKindRules.Derive(false, false, descrizione));
    }

    [Fact]
    public void ILFaxVinceAncheQuandoLaVoceHaUnCognome()
    {
        // Una riga "Fax - Responsabile s.s.d." con tanto di cognome resta un
        // apparecchio: il numero e' quello dell'apparecchio, non della persona.
        Assert.Equal(ContactKind.Fax, ContactKindRules.Derive(true, false, "Fax  Resp. s.s.d."));
    }

    [Theory]
    [InlineData("Ambulatorio Azzurro Terapia Antalgica e Fax")]
    [InlineData("Coordinatore Tecnico e Fax")]
    public void UnaVoceCheHaAncheUnFaxNonEUnFax(string descrizione)
    {
        // La parola sta in mezzo, non in testa: e' un ambulatorio che possiede anche un
        // fax, e marcarlo come fax scoraggerebbe dal chiamare l'unico numero a cui
        // qualcuno risponde.
        Assert.Equal(ContactKind.Service, ContactKindRules.Derive(false, false, descrizione));
    }

    [Theory]
    [InlineData("TELEFONO EMERGENZA IN CASO DI INTERRUZIONE TELEFONIA")]
    [InlineData("SIRAM Emergenza Ascensori")]
    [InlineData("EMERGENZA PULIZIE    6 22")]
    [InlineData("SQUADRA EMERGENZA INTERNA")]
    public void UnRecapitoDiEmergenzaSiRiconosceOvunqueStiaLaParola(string descrizione)
    {
        Assert.Equal(ContactKind.Emergency, ContactKindRules.Derive(false, false, descrizione));
    }

    [Theory]
    [InlineData("Medicina d'urgenza")]
    [InlineData("Pronto Soccorso e Medicina d Urgenza")]
    public void UrgenzaNonEEmergenza(string descrizione)
    {
        // Nei nomi delle strutture "urgenza" indica un reparto, non un recapito da
        // usare solo nella circostanza per cui esiste. Confonderli marcherebbe come
        // emergenza un intero dipartimento.
        Assert.Equal(ContactKind.Service, ContactKindRules.Derive(false, false, descrizione));
    }

    [Fact]
    public void IlContrassegnoDellaSorgenteVinceSuQualunqueParola()
    {
        // E' l'unico dato esplicito: le altre regole leggono un testo libero, questa
        // legge una colonna che il gestionale compila apposta.
        Assert.Equal(
            ContactKind.ShortCode,
            ContactKindRules.Derive(hasPersonName: true, isShortCode: true, description: "FAX Accettazione"));
    }

    [Fact]
    public void ConUnNomeDiPersonaELaVoceDiUnaPersona()
    {
        Assert.Equal(ContactKind.Person, ContactKindRules.Derive(true, false, "Studio medico"));
    }

    [Theory]
    [InlineData("Ambulatorio Diabetologico")]
    [InlineData("Stanza Lavoro Infermieri")]
    [InlineData(null)]
    [InlineData("")]
    public void SenzaNomeDiPersonaRestaUnServizio(string? descrizione)
    {
        Assert.Equal(ContactKind.Service, ContactKindRules.Derive(false, false, descrizione));
    }

    [Fact]
    public void UnCognomeCheCominciaComeLaParolaNonDiventaUnApparecchio()
    {
        // Senza il controllo sul carattere successivo, "Faxsimile" o un cognome che
        // cominci per quelle lettere verrebbe preso per un fax.
        Assert.Equal(ContactKind.Person, ContactKindRules.Derive(true, false, "Faxsimile Editoria"));
    }
}
