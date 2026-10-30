using System.IO.Pipes;
using BlindPhone.App;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifiche sul canale con cui un secondo avvio consegna il link di chiamata alla
/// copia di BlindPhone già accesa.
/// </summary>
/// <remarks>
/// <para>
/// Il caso normale è proprio questo: l'operatore ha BlindPhone acceso tutto il giorno
/// e clicca un numero nel gestionale. Se il canale non consegna, la finestra torna
/// davanti per la strada vecchia ma il numero non arriva, e l'operatore sente «era
/// già in esecuzione» al posto del numero.
/// </para>
/// <para>
/// Ogni prova usa una pipe con un nome tutto suo, così non tocca la copia di
/// BlindPhone che potrebbe essere accesa sulla macchina che esegue i test.
/// </para>
/// </remarks>
public sealed class CanaleLinkDiChiamataTests
{
    private static readonly TimeSpan Attesa = TimeSpan.FromSeconds(5);

    private static string NuovoNome() => $"BlindPhone.Prove.LinkChiamata.{Guid.NewGuid():N}";

    [Fact]
    public async Task IlLinkArrivaAllaCopiaInAscolto()
    {
        var nome = NuovoNome();
        var ricevuto = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var canale = CallLinkChannel.Listen(l => ricevuto.TrySetResult(l), pipeName: nome);

        Assert.True(CallLinkChannel.TrySend("tel:+39-06-1234-5678;ext=12", Attesa, nome));
        Assert.Equal("tel:+39-06-1234-5678;ext=12", await ricevuto.Task.WaitAsync(Attesa));
    }

    [Fact]
    public async Task IlTestoNonAsciiArrivaIntatto()
    {
        // Il canale non interpreta: un nome con accenti, che il parser poi rifiutera',
        // deve arrivare com'e' perche' il rifiuto sia quello giusto.
        var nome = NuovoNome();
        var ricevuto = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var canale = CallLinkChannel.Listen(l => ricevuto.TrySetResult(l), pipeName: nome);

        Assert.True(CallLinkChannel.TrySend("sip:niccolò@comune.example.it", Attesa, nome));
        Assert.Equal("sip:niccolò@comune.example.it", await ricevuto.Task.WaitAsync(Attesa));
    }

    [Fact]
    public async Task PiuLinkDiSeguitoArrivanoTutti()
    {
        // L'operatore clicca un numero, si accorge che era quello sbagliato e clicca
        // quello accanto: la pipe ha un posto solo e deve riaprirsi dopo ogni link.
        var nome = NuovoNome();
        var ricevuti = new List<string>();
        var tutti = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var canale = CallLinkChannel.Listen(
            l =>
            {
                lock (ricevuti)
                {
                    ricevuti.Add(l);
                    if (ricevuti.Count == 3) tutti.TrySetResult();
                }
            },
            pipeName: nome);

        Assert.True(CallLinkChannel.TrySend("tel:201", Attesa, nome));
        Assert.True(CallLinkChannel.TrySend("tel:202", Attesa, nome));
        Assert.True(CallLinkChannel.TrySend("tel:203", Attesa, nome));

        await tutti.Task.WaitAsync(Attesa);
        Assert.Equal(["tel:201", "tel:202", "tel:203"], ricevuti);
    }

    [Fact]
    public async Task UnLinkEnormeArrivaTroncatoPercheIlParserLoRifiutiDicendoPerche()
    {
        var nome = NuovoNome();
        var ricevuto = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var canale = CallLinkChannel.Listen(l => ricevuto.TrySetResult(l), pipeName: nome);

        var enorme = "tel:" + new string('1', CallLinkParser.MaxLength * 50);
        Assert.True(CallLinkChannel.TrySend(enorme, Attesa, nome));

        var arrivato = await ricevuto.Task.WaitAsync(Attesa);

        Assert.Equal(CallLinkParser.MaxLength + 1, arrivato.Length);
        Assert.False(CallLinkParser.TryParse(arrivato, out _, out var errore));
        Assert.Contains("troppo lungo", errore, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaNessunoInAscoltoLaConsegnaNonRiesceEChiChiamaRipiega()
        => Assert.False(CallLinkChannel.TrySend("tel:204", TimeSpan.FromMilliseconds(200), NuovoNome()));

    [Fact]
    public void DopoLaChiusuraIlCanaleNonAccettaPiuLink()
    {
        // In chiusura la porta deve essere gia' chiusa: chi manda il link ripiega e
        // lo prende in carico lui appena il posto si libera.
        var nome = NuovoNome();

        var canale = CallLinkChannel.Listen(_ => { }, pipeName: nome);
        canale.Dispose();

        Assert.False(CallLinkChannel.TrySend("tel:204", TimeSpan.FromMilliseconds(300), nome));
    }

    [Fact]
    public async Task UnErroreNelGestoreNonChiudeIlCanale()
    {
        var nome = NuovoNome();
        var secondo = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primo = true;

        using var canale = CallLinkChannel.Listen(
            l =>
            {
                if (primo)
                {
                    primo = false;
                    throw new InvalidOperationException("difetto simulato nel gestore");
                }

                secondo.TrySetResult(l);
            },
            pipeName: nome);

        Assert.True(CallLinkChannel.TrySend("tel:201", Attesa, nome));
        Assert.True(CallLinkChannel.TrySend("tel:202", Attesa, nome));

        Assert.Equal("tel:202", await secondo.Task.WaitAsync(Attesa));
    }

    [Fact]
    public async Task ChiSiCollegaETaceNonBloccaIlCanale()
    {
        // La pipe ha un posto solo: un processo che si collega e non scrive niente la
        // terrebbe occupata per sempre, e i link veri non arriverebbero piu'.
        var nome = NuovoNome();
        var ricevuto = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var canale = CallLinkChannel.Listen(l => ricevuto.TrySetResult(l), pipeName: nome);

        await using var muto = new NamedPipeClientStream(".", nome, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        await muto.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.True(CallLinkChannel.TrySend("tel:204", TimeSpan.FromSeconds(10), nome));
        Assert.Equal("tel:204", await ricevuto.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
