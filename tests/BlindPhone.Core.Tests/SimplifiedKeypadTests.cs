using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica i quattro tasti del tastierino della modalità semplificata.
/// </summary>
/// <remarks>
/// <para>
/// Qui un errore non produce un fastidio ma un chiamante nel posto sbagliato: se
/// <c>Invio</c> passasse il chiamante dove doveva solo chiamare l'interno, il collega
/// si troverebbe in linea uno sconosciuto senza sapere chi sia né perché — e su un
/// interno occupato il chiamante lo perderebbe del tutto.
/// </para>
/// <para>
/// La tabella completa sta qui e si legge tutta insieme: è la sola documentazione che
/// non può divergere dal comportamento.
/// </para>
/// </remarks>
public sealed class SimplifiedKeypadTests
{
    private static KeypadAction Piu(CallActionContext stato)
        => SimplifiedKeypad.Resolve(KeypadKey.Answer, stato);

    private static KeypadAction Diviso(CallActionContext stato)
        => SimplifiedKeypad.Resolve(KeypadKey.HangUp, stato);

    private static KeypadAction Invio(CallActionContext stato)
        => SimplifiedKeypad.Resolve(KeypadKey.Confirm, stato);

    private static KeypadAction Meno(CallActionContext stato)
        => SimplifiedKeypad.Resolve(KeypadKey.Release, stato);

    // -----------------------------------------------------------------------
    // Il tasto "+": rispondere, mettere in pausa, riprendere
    // -----------------------------------------------------------------------

    [Fact]
    public void PiuRispondeAllaChiamataCheSquilla()
    {
        Assert.Equal(KeypadAction.Answer, Piu(new CallActionContext(HasRingingCall: true)));
    }

    [Fact]
    public void PiuMetteInPausaChiEInLinea()
    {
        Assert.Equal(KeypadAction.Hold, Piu(new CallActionContext(HasActiveCall: true)));
    }

    [Fact]
    public void PiuRiprendeChiEInPausa()
    {
        // E' la via d'uscita se l'operatore ci ripensa o se l'interno e' occupato:
        // senza, dalla pausa non si tornerebbe piu' al chiamante.
        Assert.Equal(KeypadAction.Resume, Piu(new CallActionContext(HeldCount: 1)));
    }

    [Fact]
    public void PiuMetteInPausaAncheLaConferenza()
    {
        Assert.Equal(KeypadAction.Hold, Piu(new CallActionContext(IsConferenceActive: true)));
    }

    [Fact]
    public void DuranteUnTrasferimentoPiuNonFaNiente()
    {
        // Le linee sono due e "metti in pausa" non direbbe quale. Per tornare al
        // chiamante c'e' il diviso, che chiude la linea con l'interno.
        Assert.Equal(KeypadAction.None, Piu(new CallActionContext(
            HasActiveCall: true, TransferPhase: TransferPhase.Consulting)));
    }

    [Fact]
    public void SenzaChiamatePiuNonFaNiente()
    {
        Assert.Equal(KeypadAction.None, Piu(default));
    }

    // -----------------------------------------------------------------------
    // Il tasto "/": chiudere
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(StatiConUnaChiamata))]
    public void DivisoChiudeSempreQuandoCEUnaChiamata(CallActionContext stato)
    {
        Assert.Equal(KeypadAction.HangUp, Diviso(stato));
    }

    [Fact]
    public void SenzaChiamateDivisoNonFaNiente()
    {
        Assert.Equal(KeypadAction.None, Diviso(default));
    }

    // -----------------------------------------------------------------------
    // Il tasto "Invio": chiamare l'interno, mai passargli il chiamante
    // -----------------------------------------------------------------------

    /// <summary>
    /// Il trasferimento passa sempre dall'ascolto, anche con il chiamante in linea.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prima questo stato produceva un trasferimento alla cieca: un tasto solo, veloce,
    /// pensato per smaltire la coda. Su un interno occupato però il centralino se la
    /// sbrigava da sé e l'operatore non aveva modo di accorgersene — il chiamante
    /// finiva su un telefono che non poteva rispondergli, quando non cadeva del tutto.
    /// </para>
    /// <para>
    /// Chi non vede non ha nessun altro canale per scoprirlo: non c'è una spia che si
    /// accende, e il silenzio dopo un trasferimento riuscito è identico a quello dopo
    /// un trasferimento perso.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConIlChiamanteInLineaInvioChiamaLInternoENonTrasferisce()
    {
        var stato = new CallActionContext(HasActiveCall: true, HasTypedDestination: true);

        Assert.Equal(KeypadAction.StartConsultation, Invio(stato));
    }

    [Fact]
    public void ConIlChiamanteInPausaInvioChiamaLInterno()
    {
        // La pausa non e' piu' un bivio: e' soltanto la pausa. Da qui in poi il
        // percorso e' lo stesso del chiamante ancora in linea.
        var stato = new CallActionContext(HeldCount: 1, HasTypedDestination: true);

        Assert.Equal(KeypadAction.StartConsultation, Invio(stato));
    }

    [Fact]
    public void AncheInConferenzaInvioChiamaLInterno()
    {
        var stato = new CallActionContext(IsConferenceActive: true, HasTypedDestination: true);

        Assert.Equal(KeypadAction.StartConsultation, Invio(stato));
    }

    /// <summary>
    /// Con l'interno già in linea, Invio non completa più: lo dice e basta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È il punto su cui si regge l'intera modifica. Se <c>Invio</c> completasse anche
    /// qui, chi lo preme due volte di fila — e capita, vedi la segnalazione delle sei
    /// pressioni in un secondo e mezzo — passerebbe il chiamante senza aver ascoltato
    /// nulla: cioè il trasferimento alla cieca di prima, con un tasto in più.
    /// </para>
    /// <para>
    /// Non restituisce <c>None</c> perché il gesto era corretto fino alla versione
    /// precedente: il solo segnale di comando rifiutato si interpreta come un guasto
    /// del programma. Si risponde nominando il tasto giusto.
    /// </para>
    /// </remarks>
    [Fact]
    public void DuranteLaConsultazioneInvioRicordaIlTastoCheSvincola()
    {
        var stato = new CallActionContext(
            HasActiveCall: true, HeldCount: 1, HasTypedDestination: true,
            TransferPhase: TransferPhase.Consulting);

        Assert.Equal(KeypadAction.AnnounceReleaseKey, Invio(stato));
    }

    // -----------------------------------------------------------------------
    // Il tasto "-": svincolare il chiamante
    // -----------------------------------------------------------------------

    [Fact]
    public void DuranteLaConsultazioneMenoPassaLaChiamata()
    {
        var stato = new CallActionContext(
            HasActiveCall: true, HeldCount: 1, HasTypedDestination: true,
            TransferPhase: TransferPhase.Consulting);

        Assert.Equal(KeypadAction.CompleteTransfer, Meno(stato));
    }

    [Fact]
    public void MenoPassaLaChiamataAncheSeLInternoStaAncoraSquillando()
    {
        // E' la decisione dell'operatore, che ha sentito squillare e sa cosa sta
        // facendo: il centralino lo tratta come semi-cieco.
        var stato = new CallActionContext(
            HasOutgoingCall: true, HeldCount: 1, TransferPhase: TransferPhase.Consulting);

        Assert.Equal(KeypadAction.CompleteTransfer, Meno(stato));
    }

    /// <summary>Fuori dalla consultazione non c'è niente da svincolare.</summary>
    /// <remarks>
    /// Un meno premuto per sbaglio mentre si parla con il chiamante non deve produrre
    /// nulla: non c'è nessun interno a cui passarlo, e inventarne uno significherebbe
    /// mandarlo all'ultimo numero rimasto nel campo.
    /// </remarks>
    [Theory]
    [MemberData(nameof(StatiSenzaConsultazione))]
    public void FuoriDallaConsultazioneMenoNonFaNiente(CallActionContext stato)
    {
        Assert.Equal(KeypadAction.None, Meno(stato));
    }

    // IL TIPO SI SCRIVE PER ESTESO, E NON E' VERBOSITA'.
    //
    // Dentro un TheoryData di xUnit v3 un "new(...)" senza tipo si aggancia a
    // TheoryDataRow invece che al tipo della riga, e il codice non compila piu'.
    // Nominare il tipo toglie l'ambiguita' e vale su entrambe le versioni.
    public static TheoryData<CallActionContext> StatiSenzaConsultazione =>
    [
        default(CallActionContext),
        new CallActionContext(HasRingingCall: true),
        new CallActionContext(HasActiveCall: true, HasTypedDestination: true),
        new CallActionContext(HeldCount: 1, HasTypedDestination: true),
        new CallActionContext(IsConferenceActive: true),
        new CallActionContext(HeldCount: 1, TransferPhase: TransferPhase.EnteringDestination),
        new CallActionContext(TransferPhase: TransferPhase.Completing),
    ];

    [Fact]
    public void SenzaDestinazioneInvioNonTrasferisceNiente()
    {
        // Un trasferimento verso il vuoto perderebbe il chiamante.
        Assert.Equal(KeypadAction.None, Invio(new CallActionContext(HasActiveCall: true)));
        Assert.Equal(KeypadAction.None, Invio(new CallActionContext(HeldCount: 1)));
    }

    [Fact]
    public void MentreSquillaInvioNonTrasferisce()
    {
        // Non c'e' ancora nessuna linea da passare: prima si risponde.
        var stato = new CallActionContext(HasRingingCall: true, HasTypedDestination: true);

        Assert.Equal(KeypadAction.None, Invio(stato));
    }

    [Fact]
    public void FuoriDaOgniChiamataInvioComponeIlNumero()
    {
        // Una modalita' semplificata che impedisse di telefonare sarebbe semplificata
        // di troppo.
        Assert.Equal(KeypadAction.Dial, Invio(new CallActionContext(HasTypedDestination: true)));
        Assert.Equal(KeypadAction.Dial, Invio(default));
    }

    // -----------------------------------------------------------------------
    // Regole che valgono sempre
    // -----------------------------------------------------------------------

    public static TheoryData<CallActionContext> StatiConUnaChiamata =>
    [
        new CallActionContext(HasRingingCall: true),
        new CallActionContext(HasActiveCall: true),
        new CallActionContext(HeldCount: 1),
        new CallActionContext(HasOutgoingCall: true),
        new CallActionContext(IsConferenceActive: true),
        new CallActionContext(HasActiveCall: true, TransferPhase: TransferPhase.Consulting),
        new CallActionContext(HeldCount: 1, TransferPhase: TransferPhase.EnteringDestination),
    ];

    [Theory]
    [MemberData(nameof(StatiConUnaChiamata))]
    public void NessunTastoRestaSenzaRispostaQuandoCEUnaChiamata(CallActionContext stato)
    {
        // Almeno chiudere si deve poter fare sempre: e' la garanzia minima di una
        // postazione telefonica.
        Assert.NotEqual(KeypadAction.None, Diviso(stato));
    }

    [Fact]
    public void IlPercorsoCompletoDelloSchemaFunziona()
    {
        // Lo schema del requisito, passo per passo, con lo stato che cambia sotto.

        // 1. La chiamata arriva: "+" risponde.
        var stato = new CallActionContext(HasRingingCall: true);
        Assert.Equal(KeypadAction.Answer, Piu(stato));

        // 2. In linea, interno composto: Invio chiama l'interno.
        stato = new CallActionContext(HasActiveCall: true, HasTypedDestination: true);
        Assert.Equal(KeypadAction.StartConsultation, Invio(stato));

        // 3. L'interno squilla e l'operatore sente: "-" svincola il chiamante.
        stato = new CallActionContext(
            HeldCount: 1, HasOutgoingCall: true, TransferPhase: TransferPhase.Consulting);
        Assert.Equal(KeypadAction.CompleteTransfer, Meno(stato));
    }

    /// <summary>L'interno è occupato: il chiamante si riprende e non si perde.</summary>
    /// <remarks>
    /// È il percorso che la modifica esiste per rendere possibile. Prima, con il
    /// trasferimento alla cieca, questo bivio non si presentava nemmeno: la chiamata
    /// era già partita quando l'operatore avrebbe potuto accorgersi del problema.
    /// </remarks>
    [Fact]
    public void ConLInternoOccupatoIlChiamanteSiRiprendeConIlDiviso()
    {
        // 1. Risposto e composto: Invio chiama l'interno.
        var stato = new CallActionContext(HasActiveCall: true, HasTypedDestination: true);
        Assert.Equal(KeypadAction.StartConsultation, Invio(stato));

        // 2. L'operatore sente il tono di occupato. "/" chiude la linea con l'interno
        //    e riporta il chiamante, che e' ancora li' ad aspettare.
        stato = new CallActionContext(
            HeldCount: 1, HasOutgoingCall: true, TransferPhase: TransferPhase.Consulting);
        Assert.Equal(KeypadAction.HangUp, Diviso(stato));

        // 3. Ripreso il chiamante, "+" torna a metterlo in pausa: la postazione e'
        //    tornata al punto due dello schema, non in uno stato speciale.
        stato = new CallActionContext(HasActiveCall: true);
        Assert.Equal(KeypadAction.Hold, Piu(stato));
    }

    /// <summary>
    /// Mentre il trasferimento è in volo il tasto di chiusura deve funzionare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Segnalazione dal campo del 10 agosto 2026: durante un trasferimento il
    /// chiamante restava appeso e <b>nessun tasto lo raggiungeva più</b>. Per liberare
    /// la linea l'operatore ha dovuto terminare il programma, con il chiamante ancora
    /// collegato.
    /// </para>
    /// <para>
    /// La causa stava nella fotografia dello stato: durante il completamento il
    /// chiamante non è né attivo né in attesa — è parcheggiato dentro la procedura — e
    /// la postazione risultava senza chiamate. Il tastierino, coerentemente, non
    /// trovava niente da chiudere.
    /// </para>
    /// </remarks>
    [Fact]
    public void MentreIlTrasferimentoEInVoloSiPuoSempreChiudereLaLinea()
    {
        var inVolo = new CallActionContext(TransferPhase: TransferPhase.Completing);

        Assert.Equal(KeypadAction.HangUp, Diviso(inVolo));
    }

    [Fact]
    public void MentreIlTrasferimentoEInVoloInvioNonApreUnaTerzaLinea()
    {
        // Il numero e' rimasto nel campo di composizione: senza un controllo esplicito
        // verrebbe letto come una nuova chiamata da fare, e chi insiste su Invio
        // perche' "non succede niente" si ritrova una linea in piu'.
        var inVolo = new CallActionContext(
            TransferPhase: TransferPhase.Completing, HasTypedDestination: true);

        Assert.Equal(KeypadAction.None, Invio(inVolo));
    }

    [Fact]
    public void MentreIlTrasferimentoEInVoloIlPiuNonHaSignificato()
    {
        // Le linee in gioco sono due: "metti in pausa" non direbbe quale. La via
        // d'uscita e' il tasto di chiusura, che ora c'e'.
        var inVolo = new CallActionContext(TransferPhase: TransferPhase.Completing);

        Assert.Equal(KeypadAction.None, Piu(inVolo));
    }
}
