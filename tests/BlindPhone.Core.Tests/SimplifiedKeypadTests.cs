using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica i tre tasti del tastierino della modalità semplificata.
/// </summary>
/// <remarks>
/// <para>
/// Qui un errore non produce un fastidio ma un chiamante nel posto sbagliato: se
/// <c>Invio</c> trasferisse alla cieca dove doveva annunciare, il collega si
/// troverebbe in linea uno sconosciuto senza sapere chi sia né perché; se facesse il
/// contrario, l'operatore resterebbe ad aspettare una risposta che non aveva chiesto
/// mentre il chiamante ascolta il silenzio.
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
    // Il tasto "Invio": il bivio del trasferimento
    // -----------------------------------------------------------------------

    [Fact]
    public void ConIlChiamanteInLineaInvioTrasferisceAllaCieca()
    {
        // Flusso rapido: rispondi, digita, Invio. E' quello che smaltisce la coda.
        var stato = new CallActionContext(HasActiveCall: true, HasTypedDestination: true);

        Assert.Equal(KeypadAction.BlindTransfer, Invio(stato));
    }

    [Fact]
    public void ConIlChiamanteInPausaInvioChiamaLInterno()
    {
        // La pausa e' la dichiarazione di voler annunciare la chiamata: e' l'unico
        // elemento che distingue i due trasferimenti, e sta tutto qui.
        var stato = new CallActionContext(HeldCount: 1, HasTypedDestination: true);

        Assert.Equal(KeypadAction.StartConsultation, Invio(stato));
    }

    [Fact]
    public void DuranteLaConsultazioneInvioPassaLaChiamata()
    {
        var stato = new CallActionContext(
            HasActiveCall: true, HeldCount: 1, HasTypedDestination: true,
            TransferPhase: TransferPhase.Consulting);

        Assert.Equal(KeypadAction.CompleteTransfer, Invio(stato));
    }

    [Fact]
    public void DuranteLaConsultazioneInvioPassaLaChiamataAncheSeLInternoStaSquillando()
    {
        // "Indipendentemente dal fatto che l'interno risponda o meno": il chiamante
        // viene passato lo stesso, e il centralino lo tratta come semi-cieco.
        var stato = new CallActionContext(
            HasOutgoingCall: true, HeldCount: 1, TransferPhase: TransferPhase.Consulting);

        Assert.Equal(KeypadAction.CompleteTransfer, Invio(stato));
    }

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
        new(HasRingingCall: true),
        new(HasActiveCall: true),
        new(HeldCount: 1),
        new(HasOutgoingCall: true),
        new(IsConferenceActive: true),
        new(HasActiveCall: true, TransferPhase: TransferPhase.Consulting),
        new(HeldCount: 1, TransferPhase: TransferPhase.EnteringDestination),
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

        // 2. In linea: "+" mette in pausa, "/" riaggancerebbe.
        stato = new CallActionContext(HasActiveCall: true);
        Assert.Equal(KeypadAction.Hold, Piu(stato));
        Assert.Equal(KeypadAction.HangUp, Diviso(stato));

        // 3. In pausa, numero composto: Invio chiama l'interno.
        stato = new CallActionContext(HeldCount: 1, HasTypedDestination: true);
        Assert.Equal(KeypadAction.StartConsultation, Invio(stato));

        // 4. L'interno squilla: Invio passa la chiamata.
        stato = new CallActionContext(
            HeldCount: 1, HasOutgoingCall: true, TransferPhase: TransferPhase.Consulting);
        Assert.Equal(KeypadAction.CompleteTransfer, Invio(stato));
    }

    [Fact]
    public void IlPercorsoDelTrasferimentoCiecoFunziona()
    {
        // 1. La chiamata arriva: "+" risponde.
        Assert.Equal(KeypadAction.Answer, Piu(new CallActionContext(HasRingingCall: true)));

        // 2. Senza mettere in pausa, si digita e si preme Invio: parte alla cieca.
        var stato = new CallActionContext(HasActiveCall: true, HasTypedDestination: true);
        Assert.Equal(KeypadAction.BlindTransfer, Invio(stato));
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
