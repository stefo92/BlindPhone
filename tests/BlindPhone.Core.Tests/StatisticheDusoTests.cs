using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Il catalogo delle statistiche d'uso e il diario dei giorni.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il vincolo che queste prove difendono è uno solo: dalla postazione non esce un
/// dato personale.</b> Lo difendono da due lati. Il primo è l'elenco chiuso: ogni
/// evento ha le sue proprietà scritte qui una per una, e chi ne aggiunge una deve
/// aggiungerla anche qui, cioè deve dire di averlo fatto. Il secondo è la forma dei
/// valori: un numero di telefono, un indirizzo SIP o una frase non entrano in nessuna
/// delle forme ammesse, qualunque proprietà si provi.
/// </para>
/// <para>
/// Il diario ha un vincolo suo, che viene dalla legge e non dalla tecnica: le
/// statistiche non devono dire quanto lavora chi siede alla postazione. Niente
/// conteggi, niente orari. Vedi <see cref="TelemetryCatalog"/>.
/// </para>
/// </remarks>
public sealed class StatisticheDusoTests
{
    private static readonly DateOnly Lunedi = new(2026, 11, 2);
    private static readonly DateOnly Martedi = new(2026, 11, 3);

    private static TelemetryFacts Postazione(string version = "1.0.19") => new()
    {
        Version = version,
        Architecture = "x64",
        Windows = "11",
        WindowsBuild = "10.0.26100",
        ScreenReader = "nvda",
        HighContrast = false,
        Theme = "scuro",
        Interaction = "standard",
        SpeechChannel = "automatico",
        InternalVoice = true,
        Braille = true,
        PbxApi = true,
        Contacts = "ldap",
        Headset = true,
        Stun = false,
        Colleagues = true,
        Voicemail = true,
        MultipleAccounts = false,
        SipTransport = "tls",
        VoiceEncryption = "obbligatoria",
        MicrophoneProcessing = false,
        HotkeysOnButtons = false,
        AutomaticUpdates = true,
        DistributedConfiguration = true,
        Policy = "postazione",
        DebugMode = true,
    };

    // -----------------------------------------------------------------------
    // Elenco chiuso
    // -----------------------------------------------------------------------

    /// <summary>
    /// Il gate dell'elenco chiuso: gli eventi e le loro proprietà, scritti per esteso.
    /// </summary>
    /// <remarks>
    /// Se questa prova fallisce perché qualcuno ha aggiunto una proprietà, la correzione
    /// non è aggiornare l'elenco qui sotto e basta: è chiedersi se quella proprietà
    /// possa dire qualcosa di una persona, scriverlo nel commento del catalogo, e poi
    /// aggiornare anche la nota per l'ente in CONFORMITA-PA-ITALIANA.md.
    /// </remarks>
    [Fact]
    public void GliEventiELeLoroProprietaSonoQuelliDichiarati()
    {
        var attesi = new Dictionary<string, string[]>
        {
            ["installazione"] = ["versione", "architettura", "windows"],
            ["avvio"] =
            [
                "versione", "architettura", "windows", "windows_build", "screen_reader", "contrasto_elevato",
                "tema", "modalita", "canale_voce", "voce_interna", "braille", "api_centralino", "rubrica",
                "cuffia", "stun", "colleghi", "segreteria", "account_multipli", "trasporto_sip",
                "cifratura_voce", "elaborazione_microfono", "tasti_sui_pulsanti", "aggiornamenti_automatici",
                "configurazione_distribuita", "criterio", "modalita_debug",
            ],
            ["attivita_giornaliera"] = ["versione", "screen_reader", "modalita"],
            ["comando_usato"] = ["versione", "comando", "origine"],
            ["aggiornamento_trovato"] = ["versione", "versione_nuova"],
            ["aggiornamento_installato"] = ["versione", "da_versione"],
            ["errore"] = ["versione", "tipo", "origine", "fatale"],
        };

        var catalogo = TelemetryCatalog.All.ToDictionary(e => e.Name, e => e.Properties.Select(p => p.Name).ToArray());

        Assert.Equal(attesi.Keys.Order(), catalogo.Keys.Order());

        foreach (var (evento, proprieta) in attesi)
        {
            Assert.Equal(proprieta, catalogo[evento]);
        }
    }

    [Fact]
    public void NessunaProprietaAccettaTestoLibero()
    {
        // Le quattro forme sono le sole che il codice conosce. Un elenco chiuso vuoto
        // accetterebbe niente, che va bene; uno che contenesse una voce con spazi o due
        // punti sarebbe un testo travestito da elenco.
        foreach (var proprieta in TelemetryCatalog.All.SelectMany(e => e.Properties))
        {
            Assert.Contains(proprieta.Kind, Enum.GetValues<TelemetryValueKind>());

            if (proprieta.Kind != TelemetryValueKind.Choice) continue;

            Assert.NotEmpty(proprieta.Choices);
            Assert.All(proprieta.Choices, voce => Assert.Matches("^[a-z0-9_]+$|^[A-Za-z]+$", voce));
        }
    }

    [Fact]
    public void UnaProprietaCheNonEInElencoVieneToltaQuandoSiCompone()
    {
        var estranea = new TelemetryProperty("numero", TelemetryValueKind.Choice, ["0612345678"]);

        var evento = TelemetryCatalog.Create(
            TelemetryCatalog.AttivitaGiornaliera, Lunedi,
            (TelemetryCatalog.Versione, "1.0.19"),
            (estranea, "0612345678"));

        Assert.Equal(["versione"], evento.Properties.Keys);
    }

    [Fact]
    public void UnEventoScrittoAManoConUnaProprietaInPiuNonPassaLaVerifica()
    {
        // E' la verifica che il client fa sull'ultima riga prima della rete: gli eventi
        // in attesa passano da un file, e il file lo puo' scrivere chiunque.
        var falso = new TelemetryEvent("comando_usato", Lunedi, new Dictionary<string, object>
        {
            ["versione"] = "1.0.19",
            ["comando"] = "Answer",
            ["origine"] = "tasto",
            ["chiamante"] = "0612345678",
        });

        Assert.Contains(TelemetryCatalog.Check(falso), p => p.Contains("chiamante", StringComparison.Ordinal));
        Assert.NotEmpty(TelemetryCatalog.Check(falso with { Name = "chiamata_ricevuta" }));
    }

    /// <summary>
    /// Le cose che non devono uscire mai, provate contro ogni proprietà del catalogo.
    /// </summary>
    [Theory]
    [InlineData("0612345678", "un numero fisso")]
    [InlineData("+39 333 1234567", "un cellulare con il prefisso")]
    [InlineData("3331234567", "un cellulare")]
    [InlineData("201", "un interno")]
    [InlineData("sip:201@centralino.comune.example.it", "un indirizzo SIP")]
    [InlineData("Mario Rossi", "un nome")]
    [InlineData("pbx.asl.example.it", "il nome del centralino")]
    [InlineData("Centralino.Asl.Exception.it", "un nome di dominio che contiene la parola")]
    [InlineData("Impossibile chiamare 0612345678: occupato", "il testo di un errore")]
    [InlineData("Ufficio Anagrafe, piano terra", "una voce di rubrica")]
    [InlineData("1.0.123456", "una versione con sei cifre di fila")]
    [InlineData("Errore0612345678", "un nome di tipo con un numero dentro")]
    public void NessunaProprietaAccettaUnDatoPersonale(string valore, string che)
    {
        var accettato = TelemetryCatalog.All
            .SelectMany(e => e.Properties.Select(p => (Evento: e.Name, Proprieta: p)))
            .Where(x => x.Proprieta.Accepts(valore))
            .Select(x => $"{x.Evento}.{x.Proprieta.Name}")
            .ToList();

        Assert.True(accettato.Count == 0, $"{che} passerebbe da: {string.Join(", ", accettato)}");
    }

    [Theory]
    [InlineData("1.0.18", true)]
    [InlineData("10.0.26100", true)]
    [InlineData("1.0.18 beta", false)]
    [InlineData("201", false)]
    [InlineData("1.0.18.4.2", false)]
    [InlineData("", false)]
    public void LeVersioniHannoUnaFormaFissa(string valore, bool atteso)
        => Assert.Equal(atteso, TelemetryCatalog.Versione.Accepts(valore));

    [Fact]
    public void UnSiOUnNoNonPassaPerUnTestoENonViceversa()
    {
        Assert.True(TelemetryCatalog.Cuffia.Accepts(true));
        Assert.False(TelemetryCatalog.Cuffia.Accepts("true"));
        Assert.False(TelemetryCatalog.Tema.Accepts(true));
        Assert.False(TelemetryCatalog.Tema.Accepts(42));
    }

    [Fact]
    public void DellErroreEsceIlTipoEMaiIlTesto()
    {
        var errore = new InvalidOperationException("Impossibile trasferire Mario Rossi al 0612345678");

        Assert.Equal("System.InvalidOperationException", TelemetryCatalog.TypeNameOf(errore));

        var evento = TelemetryCatalog.Error(
            Lunedi, "1.0.19", TelemetryCatalog.TypeNameOf(errore), ErrorOrigin.Interface, fatal: false);

        Assert.Empty(TelemetryCatalog.Check(evento));
        Assert.DoesNotContain(evento.Properties.Values, v => v is string s && s.Contains("Rossi", StringComparison.Ordinal));
    }

    [Fact]
    public void UnErroreDiSfondoSiDiceConLEccezioneCheContiene()
        => Assert.Equal(
            "System.IO.IOException",
            TelemetryCatalog.TypeNameOf(new AggregateException(new IOException("rete"))));

    [Fact]
    public void UnTipoGenericoNonPortaGliAssembly()
    {
        var nome = TelemetryCatalog.TypeNameOf(new ProvaException<string>());

        Assert.Equal("BlindPhone.Core.Tests.ProvaException`1", nome);
        Assert.True(TelemetryCatalog.Tipo.Accepts(nome));
    }

    [Fact]
    public void UnAvvioPortaTutteLeSueProprieta()
    {
        // Una traduzione sbagliata non fa uscire un valore inventato, toglie la
        // proprieta': questa prova e' quella che se ne accorge.
        var avvio = TelemetryCatalog.Startup(Lunedi, Postazione());

        Assert.Equal(TelemetryCatalog.Avvio.Properties.Count, avvio.Properties.Count);
        Assert.Empty(TelemetryCatalog.Check(avvio));
    }

    [Theory]
    [InlineData(CommandOrigin.Keyboard, "tasto")]
    [InlineData(CommandOrigin.GlobalHotkey, "tasto_globale")]
    [InlineData(CommandOrigin.Button, "pulsante")]
    [InlineData(CommandOrigin.Keypad, "tastierino")]
    [InlineData(CommandOrigin.Headset, "cuffia")]
    public void OgniOrigineDiComandoHaLaSuaVoce(CommandOrigin origine, string atteso)
    {
        var evento = TelemetryCatalog.CommandUsed(Lunedi, "1.0.19", HotkeyCommand.Transfer, origine);

        Assert.Equal(atteso, evento.Properties["origine"]);
        Assert.Equal("Transfer", evento.Properties["comando"]);
    }

    [Fact]
    public void OgniComandoSiPuoDire()
    {
        foreach (var comando in Enum.GetValues<HotkeyCommand>())
        {
            var evento = TelemetryCatalog.CommandUsed(Lunedi, "1.0.19", comando, CommandOrigin.Keyboard);
            Assert.True(evento.Properties.ContainsKey("comando"), $"{comando} non passerebbe");
        }
    }

    // -----------------------------------------------------------------------
    // Identificativo
    // -----------------------------------------------------------------------

    [Fact]
    public void LIdentificativoECasualeEFattoDiSoleLettere()
    {
        var identificativi = Enumerable.Range(0, 2000).Select(_ => TelemetryDiary.NewInstallationId()).ToList();

        Assert.All(identificativi, id => Assert.True(TelemetryDiary.IsInstallationId(id), id));
        Assert.All(identificativi, id => Assert.DoesNotContain(id, char.IsDigit));
        Assert.Equal(identificativi.Count, identificativi.Distinct().Count());
    }

    [Fact]
    public void UnIdentificativoScrittoAManoNelFileNonVieneUsato()
    {
        var diario = new TelemetryDiary(new TelemetryState { InstallationId = "0612345678" }, "1.0.19");

        Assert.NotEqual("0612345678", diario.InstallationId);
        Assert.True(TelemetryDiary.IsInstallationId(diario.InstallationId));
    }

    [Fact]
    public void UnEventoInAttesaFuoriCatalogoSiScartaAllaLettura()
    {
        var stato = new TelemetryState
        {
            InstallationId = TelemetryDiary.NewInstallationId(),
            Pending =
            [
                TelemetryCatalog.UpdateFound(Lunedi, "1.0.19", "1.0.20"),
                new TelemetryEvent("rubrica_esportata", Lunedi, new Dictionary<string, object> { ["nome"] = "Mario Rossi" }),
            ],
        };

        var diario = new TelemetryDiary(stato, "1.0.19");

        Assert.Equal("aggiornamento_trovato", Assert.Single(diario.PeekBatch(10)).Name);
    }

    // -----------------------------------------------------------------------
    // Diario dei giorni
    // -----------------------------------------------------------------------

    [Fact]
    public void AlPrimoAvvioPartonoInstallazioneAvvioEAttivita()
    {
        var diario = new TelemetryDiary(null, "1.0.19");

        diario.Begin(Lunedi, Postazione());

        Assert.Equal(
            ["installazione", "avvio", "attivita_giornaliera"],
            diario.PeekBatch(10).Select(e => e.Name));
    }

    [Fact]
    public void DopoUnAggiornamentoSiDiceDaQualeVersione()
    {
        var stato = new TelemetryState { InstallationId = TelemetryDiary.NewInstallationId(), LastVersion = "1.0.18" };
        var diario = new TelemetryDiary(stato, "1.0.19");

        diario.Begin(Lunedi, Postazione());

        var installato = diario.PeekBatch(10).Single(e => e.Name == "aggiornamento_installato");

        Assert.Equal("1.0.18", installato.Properties["da_versione"]);
        Assert.DoesNotContain(diario.PeekBatch(10), e => e.Name == "installazione");
    }

    [Fact]
    public void IComandiArrivanoIlGiornoDopoSenzaDireQuanteVolte()
    {
        var diario = new TelemetryDiary(null, "1.0.19");
        diario.Begin(Lunedi, Postazione());
        diario.Remove(diario.PendingCount);

        for (var i = 0; i < 40; i++) diario.RecordCommand(Lunedi, HotkeyCommand.Answer, CommandOrigin.Keyboard);
        diario.RecordCommand(Lunedi, HotkeyCommand.Answer, CommandOrigin.Headset);
        diario.RecordCommand(Lunedi, HotkeyCommand.Transfer, CommandOrigin.Button);

        // Lo stesso giorno non parte niente: un comando che arrivasse subito direbbe
        // l'ora della chiamata a cui si e' risposto.
        diario.Advance(Lunedi, () => Postazione());
        Assert.Equal(0, diario.PendingCount);

        diario.Advance(Martedi, () => Postazione());

        var comandi = diario.PeekBatch(50).Where(e => e.Name == "comando_usato").ToList();

        Assert.Equal(3, comandi.Count);
        Assert.All(comandi, c => Assert.Equal(Lunedi, c.Day));
        Assert.All(comandi, c => Assert.Equal(["comando", "origine", "versione"], c.Properties.Keys.Order()));
    }

    [Fact]
    public void LAttivitaGiornalieraParteUnaVoltaPerGiorno()
    {
        var diario = new TelemetryDiary(null, "1.0.19");
        diario.Begin(Lunedi, Postazione());

        for (var i = 0; i < 100; i++) diario.Advance(Lunedi, () => Postazione());
        diario.Advance(Martedi, () => Postazione());
        diario.Advance(Martedi, () => Postazione());

        Assert.Equal(
            [Lunedi, Martedi],
            diario.PeekBatch(50).Where(e => e.Name == "attivita_giornaliera").Select(e => e.Day));
    }

    [Fact]
    public void LoStatoDellaPostazioneSiChiedeSoloQuandoServe()
    {
        var diario = new TelemetryDiary(null, "1.0.19");
        diario.Begin(Lunedi, Postazione());

        var richieste = 0;
        for (var i = 0; i < 60; i++) diario.Advance(Lunedi, () => { richieste++; return Postazione(); });

        Assert.Equal(0, richieste);
    }

    [Fact]
    public void UnErroreRipetutoParteUnaVoltaAlGiorno()
    {
        // Il caso vero: un errore nel calcolo del layout, milleduecento volte in pochi
        // secondi.
        var diario = new TelemetryDiary(null, "1.0.19");

        var detti = Enumerable.Range(0, 1200)
            .Count(_ => diario.RecordError(Lunedi, "System.InvalidOperationException", ErrorOrigin.Interface, false));

        Assert.Equal(1, detti);
        Assert.True(diario.RecordError(Lunedi, "System.InvalidOperationException", ErrorOrigin.BackgroundTask, false));
        Assert.True(diario.RecordError(Martedi, "System.InvalidOperationException", ErrorOrigin.Interface, false));
    }

    [Fact]
    public void UnAggiornamentoTrovatoSiDiceUnaVoltaPerVersione()
    {
        var diario = new TelemetryDiary(null, "1.0.19");

        diario.RecordUpdateFound(Lunedi, "1.0.20");
        diario.RecordUpdateFound(Lunedi, "1.0.20");
        diario.RecordUpdateFound(Martedi, "1.0.20");
        diario.RecordUpdateFound(Martedi, "1.0.21");

        Assert.Equal(
            ["1.0.20", "1.0.21"],
            diario.PeekBatch(10).Select(e => (string)e.Properties["versione_nuova"]));
    }

    [Fact]
    public void IComandiDelGiornoSopravvivonoAUnRiavvioSenzaRaddoppiare()
    {
        // Il posto operatore si chiude e si riapre piu' volte al giorno: senza il
        // giorno salvato, ogni riapertura rimanderebbe gli stessi comandi.
        var primo = new TelemetryDiary(null, "1.0.19");
        primo.Begin(Lunedi, Postazione());
        primo.RecordCommand(Lunedi, HotkeyCommand.Answer, CommandOrigin.Keyboard);

        var secondo = new TelemetryDiary(primo.Snapshot() with { Pending = [] }, "1.0.19");
        secondo.Begin(Lunedi, Postazione());
        secondo.RecordCommand(Lunedi, HotkeyCommand.Answer, CommandOrigin.Keyboard);
        secondo.Advance(Martedi, () => Postazione());

        Assert.Single(secondo.PeekBatch(50), e => e.Name == "comando_usato");
        Assert.DoesNotContain(secondo.PeekBatch(50), e => e.Name == "installazione");
    }

    [Fact]
    public void GliEventiInAttesaHannoUnTetto()
    {
        var diario = new TelemetryDiary(null, "1.0.19");

        for (var giorno = 0; giorno < TelemetryDiary.MaxPending + 50; giorno++)
        {
            diario.RecordUpdateFound(Lunedi.AddDays(giorno), $"2.0.{giorno}");
        }

        Assert.Equal(TelemetryDiary.MaxPending, diario.PendingCount);

        // Si scartano i piu' vecchi.
        Assert.Equal("2.0.50", diario.PeekBatch(1)[0].Properties["versione_nuova"]);
    }
}

/// <summary>Un'eccezione generica, per vedere che il suo nome non si porta dietro gli assembly.</summary>
/// <typeparam name="T">Qualunque tipo.</typeparam>
public sealed class ProvaException<T> : Exception;
