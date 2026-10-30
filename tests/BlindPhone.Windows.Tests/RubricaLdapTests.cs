using BlindPhone.App;
using BlindPhone.Contacts.Ldap;
using BlindPhone.Core.Domain;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Rubrica letta da una directory LDAP o da Active Directory.
/// </summary>
/// <remarks>
/// <para>
/// Si verifica ciò che non richiede un server acceso: quale attributo diventa quale
/// campo, quale numero si compone e quale finisce nelle note, come vengono tradotti
/// gli errori e che cosa esce dal deposito protetto. Il collegamento vero — LDAPS,
/// StartTLS, la lettura a pagine contro un Active Directory con più di mille utenti —
/// si collauda sul campo, contro la directory dell'ente.
/// </para>
/// </remarks>
public sealed class RubricaLdapTests : IDisposable
{
    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "BlindPhone.Test", $"ldap-{Guid.NewGuid():N}");

    public RubricaLdapTests() => Directory.CreateDirectory(_cartella);

    public void Dispose()
    {
        try { Directory.Delete(_cartella, recursive: true); } catch (IOException) { }
    }

    private static LdapEntry Voce(string dn, params (string Attributo, string Valore)[] attributi)
        => new(
            dn,
            attributi
                .GroupBy(a => a.Attributo, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)[.. g.Select(a => a.Valore)],
                    StringComparer.OrdinalIgnoreCase));

    private static LdapEntry MarioRossi() => Voce(
        "CN=Mario Rossi,OU=Ragioneria,DC=ente,DC=local",
        ("objectGUID", "6f1c2a4e-1b2c-4d3e-9f00-112233445566"),
        ("displayName", "Rossi Mario"),
        ("givenName", "Mario"),
        ("sn", "Rossi"),
        ("ipPhone", "4021"),
        ("telephoneNumber", "+39 0521 703111"),
        ("mobile", "+39 333 123 4567"),
        ("mail", "m.rossi@ente.it"),
        ("department", "Ragioneria"),
        ("title", "Istruttore contabile"),
        ("physicalDeliveryOfficeName", "Palazzo comunale, stanza 12"),
        ("company", "Comune di Esempio"),
        ("whenChanged", "20261030101500.0Z"));

    private static ContactsSyncService Servizio(SecretStore segreti, ContactsSettings? rubrica = null)
        => new(
            new ContattiFinti(),
            segreti,
            () => new AppSettings { Contacts = rubrica ?? new ContactsSettings() },
            () => false,
            NullLogger<ContactsSyncService>.Instance,
            NullLogger<BlindPhone.Contacts.SqlServer.SqlServerContactSource>.Instance);

    // -----------------------------------------------------------------------
    // Abbinamento degli attributi
    // -----------------------------------------------------------------------

    [Fact]
    public void GliAttributiDiActiveDirectoryRiempionoICampiGiusti()
    {
        var contatto = LdapContactSource.BuildContact(MarioRossi(), new LdapAttributeMapping(), "Directory LDAP: ente.local");

        Assert.NotNull(contatto);
        Assert.Equal("Rossi Mario", contatto.DisplayName);
        Assert.Equal("Mario", contatto.FirstName);
        Assert.Equal("Rossi", contatto.LastName);
        Assert.Equal("m.rossi@ente.it", contatto.Email);
        Assert.Equal("Ragioneria", contatto.Department);
        Assert.Equal("Istruttore contabile", contatto.JobTitle);
        Assert.Equal("Palazzo comunale, stanza 12", contatto.Building);
        Assert.Equal("Comune di Esempio", contatto.Organization);
        Assert.Equal(ContactKind.Person, contatto.Kind);
        Assert.Equal("Directory LDAP: ente.local", contatto.Source);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 10, 15, 0, TimeSpan.Zero), contatto.UpdatedAt);
    }

    [Fact]
    public void LInternoEIlRecapitoPredefinitoEGliAltriSonoComponibili()
    {
        // L'interno e' quello che squilla sulla scrivania ed e' l'unico su cui un
        // trasferimento si chiude senza uscire dal centralino. Gli altri numeri
        // arrivano senza spazi: finiscono cosi' come sono nell'indirizzo SIP.
        var contatto = LdapContactSource.BuildContact(MarioRossi(), new LdapAttributeMapping(), "LDAP")!;

        Assert.Collection(
            contatto.Numbers,
            n => Assert.Equal(new PhoneNumber("4021", PhoneNumberKind.Extension, true), n),
            n => Assert.Equal(new PhoneNumber("+390521703111", PhoneNumberKind.Work, false), n),
            n => Assert.Equal(new PhoneNumber("+393331234567", PhoneNumberKind.Mobile, false), n));

        Assert.Equal("4021", contatto.ExtensionNumber);
    }

    [Fact]
    public void LInternoAccodatoAlNumeroDirettoDiventaUnInterno()
    {
        // Directory senza ipPhone: l'interno sta in coda al telefono di ufficio.
        var voce = Voce(
            "CN=Bianchi,DC=ente,DC=local",
            ("sn", "Bianchi"),
            ("telephoneNumber", "+39 0521 703111 int. 4022"));

        var contatto = LdapContactSource.BuildContact(voce, new LdapAttributeMapping(), "LDAP")!;

        Assert.Equal("4022", contatto.PrimaryNumber);
        Assert.Equal("4022", contatto.ExtensionNumber);
        Assert.Contains(contatto.Numbers, n => n.Number == "+390521703111" && n.Kind == PhoneNumberKind.Work);
    }

    [Fact]
    public void LoStessoInternoDichiaratoDueVolteCompareUnaVolta()
    {
        var voce = Voce(
            "CN=Verdi,DC=ente,DC=local",
            ("sn", "Verdi"),
            ("ipPhone", "4023"),
            ("telephoneNumber", "int. 4023"));

        var contatto = LdapContactSource.BuildContact(voce, new LdapAttributeMapping(), "LDAP")!;

        Assert.Single(contatto.Numbers);
    }

    [Fact]
    public void UnaNotaNelCampoDelTelefonoFinisceNelleNote()
    {
        // Non si compone, ma non si butta: resta leggibile e ricercabile.
        var voce = Voce(
            "CN=Neri,DC=ente,DC=local",
            ("sn", "Neri"),
            ("ipPhone", "4024"),
            ("mobile", "solo su richiesta della segreteria"));

        var contatto = LdapContactSource.BuildContact(voce, new LdapAttributeMapping(), "LDAP")!;

        Assert.Single(contatto.Numbers);
        Assert.Equal("solo su richiesta della segreteria", contatto.Notes);
    }

    [Fact]
    public void UnaVoceSenzaNumeriNonEntraInRubrica()
    {
        var voce = Voce("CN=Sala riunioni,DC=ente,DC=local", ("displayName", "Sala riunioni"));

        Assert.Null(LdapContactSource.BuildContact(voce, new LdapAttributeMapping(), "LDAP"));
    }

    [Fact]
    public void UnAttributoSvuotatoNonVieneLetto()
    {
        // E' il modo in cui un ente non porta sulla postazione i cellulari di
        // servizio: l'attributo non si chiede nemmeno al server.
        var abbinamento = new LdapAttributeMapping { MobilePhone = null };

        var contatto = LdapContactSource.BuildContact(MarioRossi(), abbinamento, "LDAP")!;

        Assert.DoesNotContain(contatto.Numbers, n => n.Kind == PhoneNumberKind.Mobile);
        Assert.DoesNotContain("mobile", abbinamento.RequestedAttributes(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SiChiedonoAlServerSoloGliAttributiAbbinati()
    {
        // Minimizzazione: la directory contiene date di nascita e indirizzi di casa,
        // che non devono attraversare la rete nemmeno di passaggio.
        var richiesti = new LdapAttributeMapping().RequestedAttributes();

        Assert.Equal(13, richiesti.Count);
        Assert.Contains("ipPhone", richiesti);
        Assert.Contains("objectGUID", richiesti);
        Assert.DoesNotContain("homePhone", richiesti);
        Assert.DoesNotContain("*", richiesti);
    }

    [Fact]
    public void LIdentitaSegueObjectGuidENonIlNomeDistinto()
    {
        // La persona cambia ufficio: il nome distinto cambia, i preferiti no.
        var prima = LdapContactSource.BuildContact(MarioRossi(), new LdapAttributeMapping(), "LDAP")!;
        var spostata = MarioRossi() with { DistinguishedName = "CN=Mario Rossi,OU=Tributi,DC=ente,DC=local" };
        var dopo = LdapContactSource.BuildContact(spostata, new LdapAttributeMapping(), "LDAP")!;

        Assert.Equal(prima.Id, dopo.Id);
    }

    [Fact]
    public void SenzaObjectGuidLIdentitaVieneDalNomeDistinto()
    {
        var a = Voce("CN=A,DC=ente,DC=local", ("sn", "A"), ("ipPhone", "4030"));
        var b = Voce("CN=B,DC=ente,DC=local", ("sn", "B"), ("ipPhone", "4030"));

        // Due persone sullo stesso interno restano due voci.
        Assert.NotEqual(
            LdapContactSource.BuildContact(a, new LdapAttributeMapping(), "LDAP")!.Id,
            LdapContactSource.BuildContact(b, new LdapAttributeMapping(), "LDAP")!.Id);
    }

    [Fact]
    public void UnContattoSenzaPersonaChiamatoFaxEUnFax()
    {
        // I contatti di Active Directory servono anche per i numeri di servizio, e
        // comporre un fax non mette in linea nessuno.
        var voce = Voce("CN=Fax Protocollo,DC=ente,DC=local", ("displayName", "Fax Protocollo"), ("telephoneNumber", "0521 703199"));

        Assert.Equal(ContactKind.Fax, LdapContactSource.BuildContact(voce, new LdapAttributeMapping(), "LDAP")!.Kind);
    }

    [Theory]
    [InlineData("20261030101500.0Z", 2026, 10, 30, 10, 15, 0)]
    [InlineData("20261030101500Z", 2026, 10, 30, 10, 15, 0)]
    [InlineData("20261030121500+0200", 2026, 10, 30, 10, 15, 2)]
    public void LeDateDellaDirectorySiLeggono(string valore, int anno, int mese, int giorno, int ora, int minuto, int fuso)
    {
        var letta = LdapContactSource.ParseGeneralizedTime(valore);

        Assert.NotNull(letta);
        Assert.Equal(new DateTimeOffset(anno, mese, giorno, ora + fuso, minuto, 0, TimeSpan.FromHours(fuso)), letta);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ieri")]
    [InlineData("2026-10-30")]
    public void UnaDataIrriconoscibileNonFaPerdereIlContatto(string? valore)
        => Assert.Null(LdapContactSource.ParseGeneralizedTime(valore));

    // -----------------------------------------------------------------------
    // Sorgente, senza server
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LaSorgenteLeggeTutteLeVociEScartaQuelleSenzaNumero()
    {
        var opzioni = new LdapOptions { Server = "ente.local", BaseDn = "DC=ente,DC=local" };
        var lettore = new LettoreFinto(
            MarioRossi(),
            Voce("CN=Stampante,DC=ente,DC=local", ("displayName", "Stampante piano 2")));

        var sorgente = new LdapContactSource(opzioni, () => lettore);

        var contatti = await sorgente.LoadAsync(CancellationToken.None);

        Assert.Single(contatti);
        Assert.Equal("Directory LDAP: ente.local", sorgente.Name);
        Assert.True(lettore.Chiuso, "Il collegamento alla directory deve essere chiuso dopo la lettura");
    }

    [Fact]
    public async Task LaProvaDiceQuantiContattiHaTrovato()
    {
        var opzioni = new LdapOptions { Server = "ente.local", BaseDn = "DC=ente,DC=local" };
        var sorgente = new LdapContactSource(opzioni, () => new LettoreFinto(MarioRossi()));

        var esito = await sorgente.TestAsync(CancellationToken.None);

        Assert.True(esito.Success);
        Assert.Equal(1, esito.ContactCount);
        Assert.DoesNotContain("chiaro", esito.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InChiaroLAvvisoSiSenteAOgniProva()
    {
        // E' una scelta che si dimentica di aver fatto: chi riapre le impostazioni
        // mesi dopo deve risentirla.
        var opzioni = new LdapOptions { Server = "ente.local", BaseDn = "DC=ente,DC=local", Security = LdapSecurity.Plain };
        var sorgente = new LdapContactSource(opzioni, () => new LettoreFinto(MarioRossi()));

        var esito = await sorgente.TestAsync(CancellationToken.None);

        Assert.True(esito.Success);
        Assert.Contains(LdapContactSource.PlainTextWarning, esito.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnErroreDelLettoreDiventaUnaFrase()
    {
        var opzioni = new LdapOptions { Server = "ente.local", BaseDn = "DC=ente,DC=local" };
        var sorgente = new LdapContactSource(
            opzioni,
            () => throw new LdapSourceException("Manca la password dell'utenza della directory"));

        var esito = await sorgente.TestAsync(CancellationToken.None);

        Assert.False(esito.Success);
        Assert.Equal("Manca la password dell'utenza della directory", esito.Message);
    }

    [Fact]
    public async Task UnCollegamentoIncompletoNonSiTenta()
    {
        var aperture = 0;
        var sorgente = new LdapContactSource(
            new LdapOptions { Server = "ente.local" },
            () => { aperture++; return new LettoreFinto(); });

        var esito = await sorgente.TestAsync(CancellationToken.None);

        Assert.False(esito.Success);
        Assert.Equal(0, aperture);
    }

    [Fact]
    public void LaPortaPredefinitaSegueLaProtezione()
    {
        Assert.Equal(636, new LdapOptions { Security = LdapSecurity.Ldaps }.EffectivePort);
        Assert.Equal(389, new LdapOptions { Security = LdapSecurity.StartTls }.EffectivePort);
        Assert.Equal(389, new LdapOptions { Security = LdapSecurity.Plain }.EffectivePort);
        Assert.Equal(3269, new LdapOptions { Security = LdapSecurity.Ldaps, Port = 3269 }.EffectivePort);
    }

    [Fact]
    public void LaDescrizioneNonContieneUtenzaNePassword()
    {
        // Va nel registro, che finisce nel pacchetto di assistenza: l'utenza e' spesso
        // il nome distinto di una persona.
        var descrizione = new LdapOptions
        {
            Server = "dc01.ente.local",
            Bind = LdapBind.Simple,
            BindUser = "CN=Mario Rossi,OU=Sistemi informativi,DC=ente,DC=local",
        }.Describe();

        Assert.Contains("dc01.ente.local", descrizione, StringComparison.Ordinal);
        Assert.DoesNotContain("Rossi", descrizione, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Errori in italiano
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(49, null, "rifiutato le credenziali")]
    [InlineData(49, "80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data 52e, v4563", "rifiutato le credenziali")]
    [InlineData(49, "80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data 532, v4563", "scaduta")]
    [InlineData(49, "80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data 775, v4563", "bloccata")]
    [InlineData(49, "80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data 533, v4563", "disattivata")]
    [InlineData(32, null, "non esiste")]
    [InlineData(50, null, "permesso")]
    [InlineData(8, null, "LDAPS oppure StartTLS")]
    [InlineData(81, null, "non risponde")]
    [InlineData(85, null, "tempo previsto")]
    [InlineData(87, null, "filtro")]
    [InlineData(48, null, "anonimo")]
    public void GliErroriDellaDirectoryVengonoDettiInItaliano(int codice, string? messaggio, string atteso)
    {
        // «LDAP error 49» non dice niente a chi lo ascolta una volta sola. E «password
        // scaduta» e «utenza bloccata» si risolvono in due modi diversi: senza
        // distinguerli si riprova la password, e l'utenza si blocca davvero.
        var frase = LdapContactSource.DescribeLdapFailure(codice, messaggio, LdapSecurity.StartTls, LdapBind.Simple);

        Assert.Contains(atteso, frase, StringComparison.Ordinal);
        Assert.DoesNotContain("LdapErr", frase, StringComparison.Ordinal);
    }

    [Fact]
    public void ConLdapsIlServerMutoFaPensareAncheAlCertificato()
    {
        // Il client di Windows chiude e basta quando non riconosce il certificato: lo
        // stesso codice di un server spento, ed e' il caso piu' comune.
        var frase = LdapContactSource.DescribeLdapFailure(81, null, LdapSecurity.Ldaps, LdapBind.Windows);

        Assert.Contains("certificato", frase, StringComparison.Ordinal);
    }

    [Fact]
    public void LAccessoAnonimoRifiutatoDaActiveDirectoryDiceCosaScegliere()
    {
        var frase = LdapContactSource.DescribeLdapFailure(1, null, LdapSecurity.Ldaps, LdapBind.Anonymous);

        Assert.Contains("utenza", frase, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Impostazioni e segreti
    // -----------------------------------------------------------------------

    [Fact]
    public void IPredefinitiDelleImpostazioniSonoQuelliDellaSorgente()
    {
        // Le due definizioni stanno in progetti che non si vedono: se divergono, una
        // postazione nuova leggerebbe attributi diversi da quelli documentati.
        var opzioni = ContactsSyncService.ToLdapOptions(new ContactsLdapSettings());
        var predefinite = new LdapOptions();

        Assert.Equal(predefinite.Attributes, opzioni.Attributes);
        Assert.Equal(LdapOptions.ActiveDirectoryFilter, opzioni.Filter);
        Assert.Equal(predefinite.Security, opzioni.Security);
        Assert.Equal(predefinite.Bind, opzioni.Bind);
        Assert.Equal(predefinite.PasswordSecretKey, opzioni.PasswordSecretKey);
    }

    [Fact]
    public void UnFiltroSvuotatoTornaQuelloProposto()
    {
        // Cercare tutto porterebbe in rubrica computer, gruppi e account di servizio.
        var opzioni = ContactsSyncService.ToLdapOptions(new ContactsLdapSettings { Filter = "  " });

        Assert.Equal(LdapOptions.ActiveDirectoryFilter, opzioni.Filter);
    }

    [Fact]
    public void LeImpostazioniSalvateDiventanoIParametriDelCollegamento()
    {
        var opzioni = ContactsSyncService.ToLdapOptions(new ContactsLdapSettings
        {
            Server = " dc01.ente.local ",
            Port = 3269,
            Security = ContactsLdapSecurity.StartTls,
            Bind = ContactsLdapBind.Simple,
            BindUser = "svc-rubrica@ente.local",
            BaseDn = "OU=Personale,DC=ente,DC=local",
            OneLevelOnly = true,
            Attributes = new ContactsLdapAttributes { Extension = "pager" },
        });

        Assert.Equal("dc01.ente.local", opzioni.Server);
        Assert.Equal(3269, opzioni.EffectivePort);
        Assert.Equal(LdapSecurity.StartTls, opzioni.Security);
        Assert.Equal(LdapBind.Simple, opzioni.Bind);
        Assert.Equal(LdapScope.OneLevel, opzioni.Scope);
        Assert.Equal("pager", opzioni.Attributes.Extension);
    }

    [Fact]
    public void LaPasswordDellaDirectoryNonPartePerUnServerDiverso()
    {
        // Il caso dei segreti legati all'host: spostare il server in settings.json non
        // deve bastare a mandare la password a una macchina che l'ente non ha scelto.
        var segreti = new SecretStore(_cartella);
        segreti.SetForHost("contacts.ldap.password", "segreta", "dc01.ente.local");

        var servizio = Servizio(segreti);

        var giusto = new ContactsLdapSettings { Server = "dc01.ente.local", Bind = ContactsLdapBind.Simple };
        var spostato = giusto with { Server = "ldap.altrove.example" };

        Assert.Equal("segreta", servizio.ResolveLdapPassword(giusto));
        Assert.Equal(string.Empty, servizio.ResolveLdapPassword(spostato));

        var negato = Assert.Single(segreti.Rejected);
        Assert.Contains(ContactsSyncService.LdapPasswordDescription, negato.Speech, StringComparison.Ordinal);
    }

    [Fact]
    public void ConLUtenzaDiWindowsLaPasswordNonSiLeggeNemmeno()
    {
        var segreti = new SecretStore(_cartella);
        segreti.SetForHost("contacts.ldap.password", "segreta", "dc01.ente.local");

        var servizio = Servizio(segreti);

        Assert.Null(servizio.ResolveLdapPassword(new ContactsLdapSettings { Server = "dc01.ente.local" }));
        Assert.Empty(segreti.Rejected);
    }

    [Fact]
    public void LaPasswordNonFinisceNelFileDelleImpostazioni()
    {
        // Il file e' testo in chiaro, esportabile e finisce nei backup: un collaudo di
        // sicurezza presso una PA controlla esattamente questo.
        var impostazioni = new SettingsService(_cartella);
        impostazioni.Secrets.SetForHost("contacts.ldap.password", "Segretissima-2026", "dc01.ente.local");

        Assert.True(impostazioni.Save(new AppSettings
        {
            Contacts = new ContactsSettings
            {
                Mode = ContactsMode.Ldap,
                Ldap = new ContactsLdapSettings
                {
                    Server = "dc01.ente.local",
                    BaseDn = "DC=ente,DC=local",
                    Bind = ContactsLdapBind.Simple,
                    BindUser = "svc-rubrica@ente.local",
                },
            },
        }));

        var testo = File.ReadAllText(impostazioni.SettingsPath);

        Assert.DoesNotContain("Segretissima-2026", testo, StringComparison.Ordinal);
        Assert.Contains("contacts.ldap.password", testo, StringComparison.Ordinal);

        var riletto = new SettingsService(_cartella).Load();
        Assert.Equal(ContactsMode.Ldap, riletto.Contacts.Mode);
        Assert.Equal("dc01.ente.local", riletto.Contacts.Ldap.Server);
    }

    [Fact]
    public void UnaDirectorySenzaBaseNonSiRiallinea()
    {
        var incompleta = new ContactsSettings { Mode = ContactsMode.Ldap, Ldap = new ContactsLdapSettings { Server = "ente.local" } };
        var completa = incompleta with { Ldap = incompleta.Ldap with { BaseDn = "DC=ente,DC=local" } };

        Assert.Contains("base della ricerca", ContactsSyncService.DescribeIncomplete(incompleta), StringComparison.Ordinal);
        Assert.Null(ContactsSyncService.DescribeIncomplete(completa));
    }

    [Fact]
    public async Task UnRiallineamentoFallitoDiceIlMotivoELasciaLaRubrica()
    {
        // Directory configurata ma irraggiungibile: nome riservato, che non si risolve.
        var rubrica = new ContactsSettings
        {
            Mode = ContactsMode.Ldap,
            Ldap = new ContactsLdapSettings
            {
                Server = "directory.invalid",
                BaseDn = "DC=ente,DC=local",
                Bind = ContactsLdapBind.Simple,
                BindUser = "svc-rubrica",
            },
        };

        using var servizio = Servizio(new SecretStore(_cartella), rubrica);

        var esito = await servizio.SyncAsync(CancellationToken.None);

        // Utenza dedicata senza password: si ferma prima di toccare la rete, e lo dice.
        Assert.False(esito.Success);
        Assert.Contains("Manca la password", esito.Message, StringComparison.Ordinal);
        Assert.Contains("resta quella di prima", esito.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Sostituti
    // -----------------------------------------------------------------------

    private sealed class LettoreFinto(params LdapEntry[] voci) : ILdapEntryReader
    {
        public bool Chiuso { get; private set; }

        public IEnumerable<LdapEntry> ReadEntries(int? limit, CancellationToken cancellationToken)
            => limit is { } n ? voci.Take(n) : voci;

        public void Dispose() => Chiuso = true;
    }

    private sealed class ContattiFinti : BlindPhone.Core.Abstractions.IContactRepository
    {
        public Task<IReadOnlyList<Contact>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Contact>>([]);

        public Task<IReadOnlyList<Contact>> ResolveByNumberAsync(string number, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Contact>>([]);

        public Task<Contact?> GetBySpeedDialAsync(int slot, CancellationToken cancellationToken)
            => Task.FromResult<Contact?>(null);

        public Task<int> UpsertAsync(IEnumerable<Contact> contacts, string sourceName, CancellationToken cancellationToken)
            => Task.FromResult(contacts.Count());

        public Task UpdatePresenceAsync(string extension, PresenceState presence, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> ClearAsync(string? sourceName, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
