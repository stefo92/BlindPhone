using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica quali pulsanti compaiono nella barra dei comandi, stato per stato.
/// </summary>
/// <remarks>
/// <para>
/// I pulsanti sono per chi vede, e proprio per questo vanno collaudati qui invece che
/// a occhio: chi sviluppa apre la finestra, vede la barra popolata e considera la
/// cosa fatta, senza accorgersi che «Conferenza» compare quando non c'è nessuno da
/// unire o che «Registra» resta anche dopo che la registrazione è partita.
/// </para>
/// <para>
/// La regola che questi test difendono è una sola: <b>un pulsante visibile è un
/// pulsante che funziona</b>. Chi guarda legge la barra come l'elenco di ciò che si
/// può fare adesso, e un pulsante che c'è ma non fa niente costringe a chiedersi
/// perché — mentre la sua assenza non pone domande.
/// </para>
/// </remarks>
public sealed class CallActionCatalogTests
{
    private static IReadOnlyList<CallAction> Azioni(CallActionContext context)
        => [.. CallActionCatalog.Available(context).Select(b => b.Action)];

    /// <summary>Ritrova un pulsante fra quelli disponibili, fallendo se non c'è.</summary>
    private static CallActionButton Pulsante(CallActionContext context, CallAction action)
    {
        var pulsante = CallActionCatalog.Available(context).FirstOrDefault(b => b.Action == action);

        Assert.Equal(action, pulsante.Action);
        return pulsante;
    }

    private static CallActionContext InConversazione => new(HasActiveCall: true);

    // -----------------------------------------------------------------------
    // Chiamata entrante
    // -----------------------------------------------------------------------

    [Fact]
    public void MentreSquillaSiPuoRispondereERifiutare()
    {
        var azioni = Azioni(new CallActionContext(HasRingingCall: true));

        Assert.Contains(CallAction.Answer, azioni);
        Assert.Contains(CallAction.Reject, azioni);
    }

    [Fact]
    public void MentreSquillaNonSiPuoTrasferire()
    {
        // Non c'e' ancora nessuno da trasferire: il pulsante prometterebbe una cosa
        // che il centralino rifiuterebbe.
        var azioni = Azioni(new CallActionContext(HasRingingCall: true));

        Assert.DoesNotContain(CallAction.TransferStart, azioni);
        Assert.DoesNotContain(CallAction.Hold, azioni);
    }

    [Fact]
    public void SenzaChiamateNonCompareNessunComandoDiChiamata()
    {
        var azioni = Azioni(default);

        Assert.DoesNotContain(CallAction.Answer, azioni);
        Assert.DoesNotContain(CallAction.HangUp, azioni);
        Assert.DoesNotContain(CallAction.Hold, azioni);
        Assert.DoesNotContain(CallAction.RecordStart, azioni);
    }

    // -----------------------------------------------------------------------
    // Conversazione
    // -----------------------------------------------------------------------

    [Fact]
    public void InConversazioneCiSonoIComandiDellaChiamata()
    {
        var azioni = Azioni(InConversazione);

        Assert.Contains(CallAction.HangUp, azioni);
        Assert.Contains(CallAction.Hold, azioni);
        Assert.Contains(CallAction.TransferStart, azioni);
        Assert.Contains(CallAction.RecordStart, azioni);
        Assert.Contains(CallAction.DtmfOn, azioni);
        Assert.Contains(CallAction.MuteOn, azioni);
    }

    [Fact]
    public void ConUnaInAttesaEUnaAttivaSiAlternaESiFaConferenza()
    {
        var azioni = Azioni(InConversazione with { HeldCount = 1 });

        Assert.Contains(CallAction.Swap, azioni);
        Assert.Contains(CallAction.ConferenceStart, azioni);

        // "Riprendi" no: con una linea attiva il gesto giusto e' l'alternanza, che le
        // scambia invece di lasciarne due aperte con l'operatore su nessuna.
        Assert.DoesNotContain(CallAction.Resume, azioni);
    }

    [Fact]
    public void ConLaSolaChiamataInAttesaSiRiprende()
    {
        var azioni = Azioni(new CallActionContext(HeldCount: 1));

        Assert.Contains(CallAction.Resume, azioni);
        Assert.DoesNotContain(CallAction.Swap, azioni);
        Assert.DoesNotContain(CallAction.ConferenceStart, azioni);
    }

    [Fact]
    public void InConferenzaSiPuoSoloSciogliere()
    {
        var azioni = Azioni(new CallActionContext(IsConferenceActive: true));

        Assert.Contains(CallAction.ConferenceEnd, azioni);
        Assert.DoesNotContain(CallAction.ConferenceStart, azioni);
    }

    [Fact]
    public void InConferenzaRestanoIComandiDellaConversazione()
    {
        // In conferenza la chiamata attiva non esiste, ma l'operatore sta parlando:
        // guardare solo quella lascerebbe senza microfono.
        var azioni = Azioni(new CallActionContext(IsConferenceActive: true));

        Assert.Contains(CallAction.HangUp, azioni);
        Assert.Contains(CallAction.MuteOn, azioni);
    }

    [Fact]
    public void InConferenzaLaRegistrazioneSiVedeMaESpenta()
    {
        // Il codice che registra rifiuta la conferenza — due percorsi audio distinti
        // darebbero un file con una sola voce — e il pulsante deve dirlo invece di
        // far premere a vuoto.
        var registra = Pulsante(new CallActionContext(IsConferenceActive: true), CallAction.RecordStart);

        Assert.False(registra.IsEnabled);
        Assert.Contains("conferenza", registra.Description, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Chiamata in attesa: la barra non si smonta
    // -----------------------------------------------------------------------

    [Fact]
    public void ConLaChiamataInAttesaRestanoRiagganciaETrasferisci()
    {
        // E' il difetto segnalato dal campo: mettendo in attesa sparivano quasi tutti
        // i pulsanti. Sparivano a torto, per giunta: HangUpAsync e l'avvio del
        // trasferimento ripescano entrambi la chiamata in attesa da soli.
        var azioni = Azioni(new CallActionContext(HeldCount: 1));

        Assert.Contains(CallAction.HangUp, azioni);
        Assert.Contains(CallAction.TransferStart, azioni);
        Assert.Contains(CallAction.Resume, azioni);
    }

    [Fact]
    public void ConLaChiamataInAttesaGliInterruttoriRestanoMaSpenti()
    {
        // Restano perche' la barra non deve smontarsi mentre la chiamata e' ancora
        // li'; spenti perche' senza voce che passa non possono fare niente.
        var context = new CallActionContext(HeldCount: 1);

        foreach (var azione in new[] { CallAction.RecordStart, CallAction.DtmfOn, CallAction.MuteOn })
        {
            var pulsante = Pulsante(context, azione);

            Assert.False(pulsante.IsEnabled, $"{azione} dovrebbe essere spento in attesa");
            Assert.Contains("riprendi", pulsante.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FuoriDaUnaChiamataGliInterruttoriNonCiSonoAffatto()
    {
        // Spenti sarebbero rumore: non c'e' nessuna chiamata a cui possano riferirsi.
        var azioni = Azioni(default);

        Assert.DoesNotContain(CallAction.RecordStart, azioni);
        Assert.DoesNotContain(CallAction.DtmfOn, azioni);
        Assert.DoesNotContain(CallAction.MuteOn, azioni);
    }

    [Fact]
    public void UnaRegistrazioneInCorsoSiPuoSempreFermare()
    {
        // Anche con la chiamata in attesa: una registrazione che non si puo'
        // interrompere sarebbe la peggiore delle trappole.
        var pulsante = Pulsante(new CallActionContext(HeldCount: 1, IsRecording: true), CallAction.RecordStop);

        Assert.True(pulsante.IsEnabled);
    }

    // -----------------------------------------------------------------------
    // Trasferimento
    // -----------------------------------------------------------------------

    [Fact]
    public void SenzaDestinazioneNonSiPuoAncoraTrasferire()
    {
        var azioni = Azioni(new CallActionContext(TransferPhase: TransferPhase.EnteringDestination));

        Assert.DoesNotContain(CallAction.TransferBlind, azioni);
        Assert.DoesNotContain(CallAction.TransferAttended, azioni);

        // L'annullamento resta: e' l'unica via d'uscita da questa fase.
        Assert.Contains(CallAction.TransferCancel, azioni);
    }

    [Fact]
    public void ConLaDestinazioneScrittaLeDueModalitaSonoDuePulsanti()
    {
        // E' la ragione per cui la barra esiste: da tastiera le due modalita' si
        // distinguono per la velocita' della doppia pressione, qui sono una scelta.
        var azioni = Azioni(new CallActionContext(
            TransferPhase: TransferPhase.EnteringDestination, HasTypedDestination: true));

        Assert.Contains(CallAction.TransferBlind, azioni);
        Assert.Contains(CallAction.TransferAttended, azioni);
    }

    [Fact]
    public void DuranteLaConsultazioneSiCompletaOSiAnnulla()
    {
        var azioni = Azioni(new CallActionContext(
            HasActiveCall: true, TransferPhase: TransferPhase.Consulting));

        Assert.Contains(CallAction.TransferComplete, azioni);
        Assert.Contains(CallAction.TransferCancel, azioni);
        Assert.DoesNotContain(CallAction.TransferStart, azioni);
    }

    [Fact]
    public void DuranteIlTrasferimentoNonCompaionoAttesaEConferenza()
    {
        // Sono i comandi che rimescolerebbero le linee sotto la procedura in corso.
        var azioni = Azioni(new CallActionContext(
            HasActiveCall: true, HeldCount: 1, TransferPhase: TransferPhase.Consulting));

        Assert.DoesNotContain(CallAction.Hold, azioni);
        Assert.DoesNotContain(CallAction.ConferenceStart, azioni);
    }

    [Fact]
    public void DuranteLaConsultazioneSiPuoAlternare()
    {
        // L'ALTERNANZA E' L'ECCEZIONE FRA I COMANDI SULLE LINEE, e ci e' entrata per
        // una segnalazione dal campo: mentre si annuncia la chiamata serve tornare un
        // attimo dal chiamante e poi tornare dall'interno. Prima il pulsante non c'era
        // e il tasto rispondeva "nessuna chiamata in attesa da alternare", perche' il
        // chiamante non e' in attesa ma parcheggiato dentro la procedura.
        //
        // Non rimescola niente: la sessione di trasferimento resta aperta e la
        // governa l'orchestratore, che sa da quale lato completare.
        var azioni = Azioni(new CallActionContext(
            HasActiveCall: true, TransferPhase: TransferPhase.Consulting));

        Assert.Contains(CallAction.Swap, azioni);
    }

    [Fact]
    public void MentreLaDestinazioneSquillaNonSiPuoAlternare()
    {
        // Non c'e' ancora una seconda conversazione: c'e' un telefono che squilla.
        // Il pulsante prometterebbe qualcosa che non puo' fare.
        var azioni = Azioni(new CallActionContext(
            HasOutgoingCall: true, TransferPhase: TransferPhase.Consulting));

        Assert.DoesNotContain(CallAction.Swap, azioni);
    }

    [Fact]
    public void DuranteIlTrasferimentoSiPuoSempreRiagganciare()
    {
        var azioni = Azioni(new CallActionContext(TransferPhase: TransferPhase.EnteringDestination));

        Assert.Contains(CallAction.HangUp, azioni);
    }

    // -----------------------------------------------------------------------
    // Interruttori: mai le due facce insieme
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrazioneMostraSoloIlGestoPossibile(bool registrando)
    {
        var azioni = Azioni(InConversazione with { IsRecording = registrando });

        Assert.Contains(registrando ? CallAction.RecordStop : CallAction.RecordStart, azioni);
        Assert.DoesNotContain(registrando ? CallAction.RecordStart : CallAction.RecordStop, azioni);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModalitaToniMostraSoloIlGestoPossibile(bool attiva)
    {
        var azioni = Azioni(InConversazione with { IsDtmfMode = attiva });

        Assert.Contains(attiva ? CallAction.DtmfOff : CallAction.DtmfOn, azioni);
        Assert.DoesNotContain(attiva ? CallAction.DtmfOn : CallAction.DtmfOff, azioni);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MicrofonoMostraSoloIlGestoPossibile(bool silenziato)
    {
        var azioni = Azioni(InConversazione with { IsMuted = silenziato });

        Assert.Contains(silenziato ? CallAction.MuteOff : CallAction.MuteOn, azioni);
        Assert.DoesNotContain(silenziato ? CallAction.MuteOn : CallAction.MuteOff, azioni);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaPausaNonStaFraIPulsantiDellaBarra(bool inPausa)
    {
        // Vive accanto all'orologio del servizio, in cima alla finestra: e' l'unico
        // comando che riguarda il turno e non la chiamata in corso, e mescolarlo a
        // «rispondi» e «trasferisci» lo faceva cercare nel posto sbagliato.
        var azioni = Azioni(new CallActionContext(IsServicePaused: inPausa));

        Assert.DoesNotContain(CallAction.ServicePauseOn, azioni);
        Assert.DoesNotContain(CallAction.ServicePauseOff, azioni);
    }

    // -----------------------------------------------------------------------
    // Regole che valgono in ogni stato
    // -----------------------------------------------------------------------

    public static TheoryData<CallActionContext> TuttiGliStati =>
    [
        default,
        new(HasRingingCall: true),
        new(HasActiveCall: true),
        new(HasActiveCall: true, HeldCount: 1),
        new(HeldCount: 2),
        new(HasOutgoingCall: true),
        new(IsConferenceActive: true),
        new(HasActiveCall: true, TransferPhase: TransferPhase.EnteringDestination, HasTypedDestination: true),
        new(HasActiveCall: true, TransferPhase: TransferPhase.Consulting),
        new(HasActiveCall: true, IsRecording: true, IsDtmfMode: true, IsMuted: true),
        new(IsServicePaused: true),
    ];

    [Theory]
    [MemberData(nameof(TuttiGliStati))]
    public void ImpostazioniEAccountSipCiSonoSempre(CallActionContext context)
    {
        // Sono le due vie per rimettere in sesto una postazione che non funziona:
        // farle dipendere dallo stato significherebbe nasconderle proprio quando lo
        // stato e' quello sbagliato.
        var azioni = Azioni(context);

        Assert.Contains(CallAction.OpenSettings, azioni);
        Assert.Contains(CallAction.OpenSipAccounts, azioni);
    }

    [Theory]
    [MemberData(nameof(TuttiGliStati))]
    public void NessunPulsanteCompareDueVolte(CallActionContext context)
    {
        var azioni = Azioni(context);

        Assert.Equal(azioni.Count, azioni.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(TuttiGliStati))]
    public void OgniPulsanteHaEtichettaDescrizioneEPittogramma(CallActionContext context)
    {
        foreach (var pulsante in CallActionCatalog.Available(context))
        {
            Assert.False(string.IsNullOrWhiteSpace(pulsante.Label), $"{pulsante.Action} senza etichetta");
            Assert.False(string.IsNullOrWhiteSpace(pulsante.Description), $"{pulsante.Action} senza descrizione");
            Assert.False(string.IsNullOrWhiteSpace(pulsante.Icon), $"{pulsante.Action} senza pittogramma");
        }
    }

    [Theory]
    [MemberData(nameof(TuttiGliStati))]
    public void UnPulsanteSpentoSpiegaSempreIlPerche(CallActionContext context)
    {
        // Un pulsante spento senza spiegazione e' peggio di un pulsante assente:
        // chi guarda resta con la domanda e nessun posto dove trovare la risposta.
        foreach (var pulsante in CallActionCatalog.Available(context).Where(b => !b.IsEnabled))
        {
            Assert.True(
                pulsante.Description.Length > 20,
                $"{pulsante.Action} e' spento e la spiegazione e' troppo scarna: «{pulsante.Description}»");
        }
    }

    [Fact]
    public void IlPulsanteDellaPausaCambiaEtichettaEPittogrammaConLoStato()
    {
        // In servizio invita ad andare in pausa, in pausa invita a tornare: dice
        // sempre l'azione che si puo' fare, come tutti gli altri pulsanti.
        var inServizio = CallActionCatalog.ServicePauseButton(isServicePaused: false);
        var inPausa = CallActionCatalog.ServicePauseButton(isServicePaused: true);

        // I pittogrammi sono punti di codice di Segoe Fluent Icons: campanella barrata
        // per «la postazione non riceve», campanella per «ricomincia a ricevere». Il
        // valore atteso e' scritto per esteso perche' un glifo sbagliato non si nota
        // leggendo il codice — si nota solo guardando lo schermo.
        Assert.Equal("Pausa", inServizio.Label);
        Assert.Equal("\uE7ED", inServizio.Icon);

        Assert.Equal("Torna in servizio", inPausa.Label);
        Assert.Equal("\uEA8F", inPausa.Icon);
    }

    [Fact]
    public void IDuePittogrammiDellaPausaNonSonoLoStesso()
    {
        // E' il difetto segnalato dal campo: premendo la pausa il pulsante restava
        // con la tazza, e sembrava che il comando non fosse arrivato.
        var inServizio = CallActionCatalog.ServicePauseButton(isServicePaused: false);
        var inPausa = CallActionCatalog.ServicePauseButton(isServicePaused: true);

        Assert.NotEqual(inServizio.Icon, inPausa.Icon);
        Assert.NotEqual(inServizio.Action, inPausa.Action);
    }

    [Theory]
    [MemberData(nameof(TuttiGliStati))]
    public void LElencoDelleChiamateSiRaggiungeSempre(CallActionContext context)
    {
        // Il numero da richiamare si cerca proprio quando qualcosa e' appena andato
        // storto: farlo sparire durante una chiamata sarebbe toglierlo quando serve.
        Assert.Contains(CallAction.OpenCallHistory, Azioni(context));
    }

    [Theory]
    [MemberData(nameof(TuttiGliStati))]
    public void IPulsantiSonoRaggruppatiSenzaMescolarsi(CallActionContext context)
    {
        // I separatori a schermo si disegnano al cambio di gruppo: se una famiglia
        // comparisse spezzata in due tratti, i fili verticali diventerebbero due e
        // il raggruppamento perderebbe senso.
        var gruppi = CallActionCatalog.Available(context).Select(b => b.Group).ToList();
        var distinti = gruppi.Distinct().Count();

        var cambi = gruppi.Zip(gruppi.Skip(1)).Count(coppia => coppia.First != coppia.Second);

        Assert.Equal(distinti - 1, cambi);
    }

    [Theory]
    [MemberData(nameof(TuttiGliStati))]
    public void LeCoppieNonCompaionoMaiInsieme(CallActionContext context)
    {
        var azioni = Azioni(context);

        (CallAction, CallAction)[] coppie =
        [
            (CallAction.Hold, CallAction.Resume),
            (CallAction.RecordStart, CallAction.RecordStop),
            (CallAction.DtmfOn, CallAction.DtmfOff),
            (CallAction.MuteOn, CallAction.MuteOff),
            (CallAction.ServicePauseOn, CallAction.ServicePauseOff),
            (CallAction.ConferenceStart, CallAction.ConferenceEnd),
        ];

        foreach (var (prima, seconda) in coppie)
        {
            Assert.False(
                azioni.Contains(prima) && azioni.Contains(seconda),
                $"{prima} e {seconda} compaiono insieme");
        }
    }

    /// <summary>
    /// Ogni fase del trasferimento conta come chiamata esistente.
    /// </summary>
    /// <remarks>
    /// È la proprietà da cui dipende l'esistenza stessa di una via d'uscita: da
    /// <c>HasAnyCall</c> discendono sia i pulsanti della barra sia le azioni del
    /// tastierino. Quando escludeva il completamento, il chiamante restava dentro una
    /// procedura che non finiva e nessun comando lo raggiungeva.
    /// </remarks>
    [Theory]
    [InlineData(TransferPhase.EnteringDestination)]
    [InlineData(TransferPhase.Consulting)]
    [InlineData(TransferPhase.Completing)]
    public void DuranteIlTrasferimentoLaPostazioneNonRisultaMaiSenzaChiamate(TransferPhase fase)
    {
        var stato = new CallActionContext(TransferPhase: fase);

        Assert.True(stato.HasAnyCall);
        Assert.True(stato.IsTransferring);
    }

    [Fact]
    public void ConIlTrasferimentoInVoloRestaUnPulsantePerChiudereLaLinea()
    {
        var azioni = CallActionCatalog
            .Available(new CallActionContext(TransferPhase: TransferPhase.Completing))
            .Select(b => b.Action)
            .ToList();

        Assert.Contains(CallAction.HangUp, azioni);
    }
}
