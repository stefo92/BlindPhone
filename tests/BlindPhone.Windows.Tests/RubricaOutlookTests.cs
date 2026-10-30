using System.Runtime.InteropServices;
using BlindPhone.Contacts.Outlook;
using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Rubrica importata dai contatti di Outlook classico.
/// </summary>
/// <remarks>
/// <para>
/// Si verifica senza Outlook: come un contatto diventa una voce di rubrica, quale
/// numero si compone, e che cosa si dice all'operatore quando Outlook non c'è o c'è
/// solo il nuovo. La lettura vera attraverso COM si collauda su una postazione con
/// Outlook classico e un profilo configurato.
/// </para>
/// </remarks>
public sealed class RubricaOutlookTests
{
    private static OutlookContactData Bianchi() => new()
    {
        EntryId = "00000000ABCDEF",
        FullName = "Anna Bianchi",
        FirstName = "Anna",
        LastName = "Bianchi",
        FileAs = "Bianchi, Anna",
        CompanyName = "Studio tecnico Bianchi",
        Department = "Impianti",
        JobTitle = "Perito",
        OfficeLocation = "Via Roma 1",
        BusinessPhone = "+39 (0521) 703111 int. 12",
        MobilePhone = "+39 333 765 4321",
        LastModified = new DateTimeOffset(2026, 9, 1, 8, 30, 0, TimeSpan.FromHours(2)),
    };

    [Fact]
    public void ICampiDiOutlookRiempionoQuelliDellaRubrica()
    {
        var contatto = OutlookContactSource.BuildContact(Bianchi(), OutlookContactSource.SourceName);

        Assert.NotNull(contatto);
        Assert.Equal("Anna Bianchi", contatto.DisplayName);
        Assert.Equal("Anna", contatto.FirstName);
        Assert.Equal("Bianchi", contatto.LastName);
        Assert.Equal("Studio tecnico Bianchi", contatto.Organization);
        Assert.Equal("Impianti", contatto.Department);
        Assert.Equal("Perito", contatto.JobTitle);
        Assert.Equal("Via Roma 1", contatto.Building);
        Assert.Equal(ContactKind.Person, contatto.Kind);
        Assert.Equal("Contatti di Outlook", contatto.Source);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 8, 30, 0, TimeSpan.FromHours(2)), contatto.UpdatedAt);
    }

    [Fact]
    public void LInternoAccodatoDaOutlookEIlRecapitoPredefinito()
    {
        // Outlook non ha un campo per l'interno: chi lo scrive lo accoda al numero di
        // ufficio. E Outlook formatta i numeri con spazi e parentesi, che nell'indirizzo
        // SIP farebbero rifiutare la chiamata.
        var contatto = OutlookContactSource.BuildContact(Bianchi(), OutlookContactSource.SourceName)!;

        Assert.Collection(
            contatto.Numbers,
            n => Assert.Equal(new PhoneNumber("12", PhoneNumberKind.Extension, true), n),
            n => Assert.Equal(new PhoneNumber("+390521703111", PhoneNumberKind.Work, false), n),
            n => Assert.Equal(new PhoneNumber("+393337654321", PhoneNumberKind.Mobile, false), n));
    }

    [Fact]
    public void IlPrimoNumeroEIlPredefinitoAncheSenzaInterno()
    {
        var contatto = OutlookContactSource.BuildContact(
            new OutlookContactData { LastName = "Gialli", MobilePhone = "333 111 2222" },
            OutlookContactSource.SourceName)!;

        var unico = Assert.Single(contatto.Numbers);
        Assert.True(unico.IsPrimary);
        Assert.Equal("3331112222", unico.Number);
    }

    [Fact]
    public void UnaSocietaHaComeNominativoIlSuoNome()
    {
        // Senza, la voce comparirebbe in elenco con il solo numero: l'informazione che
        // l'operatore ha gia' davanti, e che non gli dice chi e'.
        var contatto = OutlookContactSource.BuildContact(
            new OutlookContactData { CompanyName = "Idraulica Rapida", CompanyMainPhone = "0521 999000" },
            OutlookContactSource.SourceName)!;

        Assert.Equal("Idraulica Rapida", contatto.EffectiveDisplayName);
        Assert.Equal(ContactKind.Service, contatto.Kind);
    }

    [Fact]
    public void UnContattoSenzaNumeriNonEntraInRubrica()
    {
        Assert.Null(OutlookContactSource.BuildContact(
            new OutlookContactData { FullName = "Solo posta", LastName = "Posta" },
            OutlookContactSource.SourceName));
    }

    [Fact]
    public void UnaNotaNelCampoDelTelefonoFinisceNelleNote()
    {
        var contatto = OutlookContactSource.BuildContact(
            new OutlookContactData { LastName = "Viola", BusinessPhone = "0521 703111", MobilePhone = "chiedere a Luca" },
            OutlookContactSource.SourceName)!;

        Assert.Single(contatto.Numbers);
        Assert.Equal("chiedere a Luca", contatto.Notes);
    }

    [Fact]
    public void LIdentitaSegueLIdentificativoDiOutlook()
    {
        // Il numero cambia, la persona no: preferiti e numeri rapidi restano attaccati.
        var prima = OutlookContactSource.BuildContact(Bianchi(), OutlookContactSource.SourceName)!;
        var dopo = OutlookContactSource.BuildContact(
            Bianchi() with { BusinessPhone = "0521 703222", MobilePhone = null },
            OutlookContactSource.SourceName)!;

        Assert.Equal(prima.Id, dopo.Id);
    }

    [Fact]
    public async Task LaSorgenteContaSoloIContattiConUnNumero()
    {
        IReadOnlyList<OutlookContactData> letti =
        [
            Bianchi(),
            new OutlookContactData { FullName = "Senza numeri", LastName = "Numeri" },
        ];

        var sorgente = new OutlookContactSource((_, _) => Task.FromResult(letti));

        var esito = await sorgente.TestAsync(CancellationToken.None);

        Assert.True(esito.Success);
        Assert.Equal(1, esito.ContactCount);
    }

    [Fact]
    public async Task UnOutlookAssenteSiDiceConLaSuaFrase()
    {
        var frase = OutlookContactReader.Explain(OutlookAvailability.NotInstalled)!;
        var sorgente = new OutlookContactSource((_, _) => throw new OutlookUnavailableException(frase));

        var esito = await sorgente.TestAsync(CancellationToken.None);

        Assert.False(esito.Success);
        Assert.Equal(frase, esito.Message);
    }

    // -----------------------------------------------------------------------
    // Quale Outlook c'e'
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, OutlookAvailability.Classic)]
    [InlineData(true, true, OutlookAvailability.Classic)]
    [InlineData(false, true, OutlookAvailability.OnlyNewOutlook)]
    [InlineData(false, false, OutlookAvailability.NotInstalled)]
    public void LaDisponibilitaSiDecideDaQuelloCheSiTrova(bool classico, bool nuovo, OutlookAvailability attesa)
    {
        // Con entrambi installati vince il classico: e' la sua interfaccia che si
        // usa, qualunque finestra l'utente tenga aperta.
        Assert.Equal(attesa, OutlookContactReader.Decide(classico, nuovo));
    }

    [Fact]
    public void IlNuovoOutlookSiNominaEDiceLAlternativa()
    {
        // Chi ha solo il nuovo Outlook non deve sentirsi dire «non installato», che
        // lo manderebbe a cercare un guasto che non c'e'.
        var frase = OutlookContactReader.Explain(OutlookAvailability.OnlyNewOutlook)!;

        Assert.Contains("nuovo Outlook", frase, StringComparison.Ordinal);
        Assert.Contains("Outlook classico", frase, StringComparison.Ordinal);
        Assert.Contains("CSV", frase, StringComparison.Ordinal);
    }

    [Fact]
    public void OutlookNonInstallatoSiDiceCosi()
    {
        Assert.Contains("non e' installato", OutlookContactReader.Explain(OutlookAvailability.NotInstalled), StringComparison.Ordinal);
        Assert.Null(OutlookContactReader.Explain(OutlookAvailability.Classic));
    }

    [Fact]
    public void UnErroreDiOutlookDiceDoveGuardare()
    {
        // Outlook si ferma spesso su una finestra che chi non vede non sa che e'
        // comparsa: la frase deve mandarlo li'.
        var frase = OutlookContactSource.DescribeFailure(new COMException("RPC_E_CALL_REJECTED", unchecked((int)0x80010001)));

        Assert.Contains("finestra", frase, StringComparison.Ordinal);
    }
}
