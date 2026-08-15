using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Rubrica finta: risolve un numero in un contatto guardando le sole cifre.
/// </summary>
/// <remarks>
/// Il confronto sulle cifre riproduce quello dell'archivio vero, dove lo stesso
/// interno può essere stato importato come "4021" o come "40 21".
/// </remarks>
internal sealed class FakeContactRepository : IContactRepository
{
    private readonly List<Contact> _contacts = [];

    /// <summary>Quante volte è stata interrogata: serve a provare che si consulta.</summary>
    public int ResolveCount { get; private set; }

    public FakeContactRepository Add(string name, params (string Number, PhoneNumberKind Kind)[] numbers)
    {
        _contacts.Add(new Contact
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            Numbers = [.. numbers.Select(n => new PhoneNumber(n.Number, n.Kind))],
        });

        return this;
    }

    public Task<Contact?> ResolveByNumberAsync(string number, CancellationToken cancellationToken)
    {
        ResolveCount++;

        var cercato = OnlyDigits(number);

        return Task.FromResult(_contacts
            .FirstOrDefault(c => c.Numbers.Any(n => OnlyDigits(n.Number) == cercato)));
    }

    private static string OnlyDigits(string value) => new([.. value.Where(char.IsDigit)]);

    public Task<IReadOnlyList<Contact>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Contact>>([]);

    public Task<Contact?> GetBySpeedDialAsync(int slot, CancellationToken cancellationToken)
        => Task.FromResult<Contact?>(null);

    public Task<int> UpsertAsync(IEnumerable<Contact> contacts, string sourceName, CancellationToken cancellationToken)
        => Task.FromResult(0);

    public Task UpdatePresenceAsync(string extension, PresenceState presence, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(_contacts.Count);

    public Task<int> ClearAsync(string? sourceName, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>
/// Verifica l'identificazione di chi chiama.
/// </summary>
/// <remarks>
/// È la prima informazione che l'operatore riceve e la sola su cui basa la decisione
/// di rispondere, di far aspettare o di passare la chiamata a qualcun altro. Un nome
/// sbagliato o un numero illeggibile qui non hanno rimedio a valle: il chiamante è
/// già in linea.
/// </remarks>
public sealed class CallerIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void UnContattoInRubricaSiAnnunciaConNomeEInterno()
    {
        var party = new CallParty("4021", "Mario Rossi")
        {
            IsKnownContact = true,
            Extension = "4021",
        };

        var text = AnnouncementTexts.IncomingCall(party, null, DigitGrouping.Single);

        Assert.Equal("Chiamata da Mario Rossi, interno quattro, zero, due, uno", text);
    }

    [Fact]
    public void UnNumeroNonInRubricaSiAnnunciaCifraPerCifraESenzaNome()
    {
        // Il difetto che questa prova blocca: SpeechName ricade sul numero quando il
        // nome manca, e passato come nome faceva pronunciare "tre miliardi
        // quattrocentonovantaquattro milioni...", cioe' un numero che non si puo'
        // trascrivere ne' richiamare.
        var party = new CallParty("3494251406");

        var text = AnnouncementTexts.IncomingCall(party, null, DigitGrouping.Single);

        Assert.Equal(
            "Chiamata da tre, quattro, nove, quattro, due, cinque, uno, quattro, zero, sei",
            text);

        Assert.DoesNotContain("interno", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnContattoDiRubricaCheChiamaDalCellulareNonDiventaUnInterno()
    {
        // Ha un interno sulla scrivania, ma sta chiamando da fuori: annunciarlo come
        // interno direbbe all'operatore una cosa falsa sul come rispondere, e non
        // avrebbe modo di verificarla.
        var party = new CallParty("3494251406", "Mario Rossi")
        {
            IsKnownContact = true,
            Extension = "4021",
        };

        var text = AnnouncementTexts.CallerIdentity(party, DigitGrouping.Single);

        Assert.StartsWith("Mario Rossi, numero ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("interno", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LInternoSiRiconosceAncheSeInRubricaEScrittoConGliSpazi()
    {
        // I contatti importati da file portano i numeri come li ha scritti chi ha
        // compilato il foglio: un confronto testuale farebbe sparire la parola
        // "interno" proprio sulle rubriche importate, che sono la maggioranza.
        var party = new CallParty("4021", "Mario Rossi")
        {
            IsKnownContact = true,
            Extension = "40 21",
        };

        Assert.Contains("interno", AnnouncementTexts.CallerIdentity(party, DigitGrouping.Single));
    }

    /// <summary>
    /// Un nome dichiarato dalla segnalazione non fa sparire il numero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prima si diceva il solo nome, per non dare a un nome non verificato l'aria di
    /// una scheda di rubrica. Il campo l'ha smentito il 10 agosto 2026: il centralino
    /// dell'ente annuncia quasi tutte le chiamate esterne come «Chiamate esterne»,
    /// senza numero dentro, e l'operatore non riceveva il numero da nessuna parte.
    /// </para>
    /// <para>
    /// Il ragionamento era pure rovesciato: il nome lo sceglie chi chiama, il numero
    /// lo porta la rete. Nascondere il secondo per prudenza sul primo toglieva
    /// l'unico dato su cui si possa fare affidamento.
    /// </para>
    /// </remarks>
    [Fact]
    public void UnNomeDichiaratoDallaSegnalazioneNonFaSparireIlNumero()
    {
        var party = new CallParty("3494251406", "Reception Hotel");

        var detto = AnnouncementTexts.CallerIdentity(party, DigitGrouping.Single);

        Assert.Contains("Reception Hotel", detto);
        Assert.Contains("numero", detto);
    }

    [Fact]
    public void IlNomeDiComodoDelCentralinoNonBastaDaSolo()
    {
        // Il caso vero: nove chiamate esterne su dieci arrivano cosi'. «Chiamata da
        // Chiamate esterne» non permette ne' di richiamare ne' di trasferire.
        var party = new CallParty("0521393111", "Chiamate esterne");

        var detto = AnnouncementTexts.CallerIdentity(party, DigitGrouping.Single);

        Assert.Contains("Chiamate esterne", detto);
        Assert.Contains("zero", detto);
    }

    [Fact]
    public void SenzaNumeroRestaIlSoloNome()
    {
        // Nessun numero da dire: la chiamata anonima esiste, e inventarne uno sarebbe
        // peggio che ammettere di non averlo.
        var party = new CallParty(string.Empty, "Reception Hotel");

        Assert.Equal("Reception Hotel", AnnouncementTexts.CallerIdentity(party, DigitGrouping.Single));
    }

    [Fact]
    public void SenzaNomeNeNumeroSiDiceSconosciuto()
    {
        Assert.Equal("sconosciuto", AnnouncementTexts.CallerIdentity(CallParty.Unknown, DigitGrouping.Single));
    }

    [Fact]
    public void LaCodaDiProvenienzaRestaInFondoAllAnnuncio()
    {
        var party = new CallParty("4021", "Mario Rossi") { IsKnownContact = true, Extension = "4021" };

        var text = AnnouncementTexts.IncomingCall(party, "Prenotazioni", DigitGrouping.Single);

        Assert.EndsWith(", coda Prenotazioni", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnaChiamataPersaNominaIlChiamanteComeAllArrivo()
    {
        // Stessa persona, stesso modo di nominarla: sentirla chiamare in due modi
        // diversi a quindici secondi di distanza fa dubitare che siano due chiamate.
        var mapper = new CallAnnouncementMapper();

        var party = new CallParty("4021", "Mario Rossi") { IsKnownContact = true, Extension = "4021" };
        var call = new Call("persa-1", CallDirection.Inbound, party, Now);
        call.TryApply(CallTrigger.IncomingCall, Now, out _);
        call.TryApply(CallTrigger.RemoteHangUp, Now, out var transition);

        var announcement = mapper.Map(transition, call, Now);

        Assert.Equal("Chiamata persa da Mario Rossi, interno quattro, zero, due, uno", announcement.Text);
    }

    // -----------------------------------------------------------------------
    // Percorso completo: dalla segnalazione all'annuncio, passando dalla rubrica
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IlNumeroEntranteVieneCercatoInRubricaEIlNomeFinisceNellAnnuncio()
    {
        var rubrica = new FakeContactRepository().Add("Mario Rossi", ("4021", PhoneNumberKind.Extension));
        var (engine, announcer) = Avvia(rubrica);

        engine.SimulateIncoming("4021");
        await Task.Delay(50);

        Assert.Equal(1, rubrica.ResolveCount);
        Assert.True(announcer.Said("Mario Rossi"));
        Assert.True(announcer.Said("interno quattro, zero, due, uno"));
    }

    [Fact]
    public async Task UnChiamanteAssenteDallaRubricaProduceIlSoloNumero()
    {
        var rubrica = new FakeContactRepository().Add("Mario Rossi", ("4021", PhoneNumberKind.Extension));
        var (engine, announcer) = Avvia(rubrica);

        engine.SimulateIncoming("3494251406");
        await Task.Delay(50);

        Assert.True(announcer.Said("tre, quattro, nove"));
        Assert.False(announcer.Said("Mario Rossi"));
    }

    [Fact]
    public async Task LaRubricaSiConsultaAncheQuandoLaSegnalazionePortaGiaUnNome()
    {
        // Il centralino fornisce quasi sempre un nome nell'intestazione SIP. Finche'
        // la sua presenza faceva saltare la ricerca, la rubrica non veniva mai
        // consultata: ne' l'interno ne' la resa fonetica del cognome arrivavano
        // all'annuncio, su nessuna chiamata.
        var rubrica = new FakeContactRepository().Add("Mario Rossi", ("4021", PhoneNumberKind.Extension));
        var (engine, announcer) = Avvia(rubrica);

        engine.SimulateIncoming("4021", "MARIO ROSSI");
        await Task.Delay(50);

        Assert.Equal(1, rubrica.ResolveCount);
        Assert.True(announcer.Said("interno"));
    }

    /// <summary>
    /// Monta il percorso vero: motore telefonico, gestore delle chiamate e rubrica.
    /// </summary>
    /// <remarks>
    /// Il gestore resta agganciato agli eventi del motore, che lo tiene in vita per
    /// tutta la prova: e' lui a ricevere la chiamata entrante e a interrogare la
    /// rubrica prima di annunciarla.
    /// </remarks>
    private static (FakeEngine Engine, RecordingAnnouncer Announcer) Avvia(IContactRepository rubrica)
    {
        var engine = new FakeEngine();
        var announcer = new RecordingAnnouncer();

        var calls = new CallSessionManager(
            engine,
            announcer,
            new CallAnnouncementMapper(),
            new AnnouncementContext(),
            rubrica);

        Assert.NotNull(calls);
        return (engine, announcer);
    }
}
