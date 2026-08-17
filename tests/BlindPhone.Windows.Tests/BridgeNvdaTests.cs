using BlindPhone.Accessibility;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica il bridge diretto verso NVDA, per la parte che non dipende dalla presenza
/// della libreria.
/// </summary>
/// <remarks>
/// <para>
/// <b>Che cosa si può provare in collaudo automatico e che cosa no.</b> La libreria di
/// NV Access non sta nel repository — è un binario di terze parti con licenza propria —
/// quindi qui accanto non c'è, e non si può provare che il bridge parli. Si prova
/// l'altra metà, che conta altrettanto: che l'assenza non faccia danni. Su un posto
/// operatore per non vedenti, un'eccezione mentre si cerca lo screen reader significa
/// una postazione che non si avvia.
/// </para>
/// <para>
/// La verifica che il bridge funzioni davvero si fa sul campo, in un modo solo: con la
/// libreria accanto all'eseguibile e NVDA in esecuzione, il registro dice
/// «Client controller NVDA disponibile». Vedi lib/nvda/LEGGIMI.md.
/// </para>
/// </remarks>
public sealed class BridgeNvdaTests
{
    [Fact]
    public void SenzaLaLibreriaIlBridgeSiDichiaraAssenteSenzaEccezioni()
    {
        var bridge = new NvdaScreenReaderBridge();

        // Non si asserisce che sia indisponibile: se chi esegue le prove ha la libreria
        // accanto all'assembly e NVDA aperto, disponibile e' la risposta giusta. Si
        // asserisce che la domanda si possa fare senza far cadere niente.
        _ = bridge.IsAvailable;

        Assert.Equal("NVDA", bridge.Name);
    }

    [Fact]
    public void ParlareConIlBridgeIndisponibileRestituisceFalsoInvecediSollevare()
    {
        var bridge = new NvdaScreenReaderBridge();

        if (bridge.IsAvailable) return;

        Assert.False(bridge.Speak("prova", interrupt: true));
        Assert.False(bridge.Braille("prova"));
        Assert.False(bridge.StopSpeech());
    }

    [Fact]
    public void CostruirloDueVolteNonSollevaNiente()
    {
        // IL RISOLUTORE SI REGISTRA UNA VOLTA SOLA PER ASSEMBLY.
        //
        // SetDllImportResolver solleva InvalidOperationException alla seconda
        // registrazione. Sta nel costruttore statico, che gira una volta: questa prova
        // e' qui perche' spostarlo altrove — nel costruttore di istanza, in un metodo di
        // avvio — reintrodurrebbe il guasto, e sarebbe un guasto totale, perche' il tipo
        // non si inizializzerebbe piu' e con lui non partirebbe la voce.
        var primo = new NvdaScreenReaderBridge();
        var secondo = new NvdaScreenReaderBridge();

        Assert.NotSame(primo, secondo);
        Assert.Equal(primo.IsAvailable, secondo.IsAvailable);
    }
}
