using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che cosa si dice di un indirizzo del server degli aggiornamenti che non
/// può funzionare.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da una postazione con l'indirizzo <c>sftp://</c>: il registro diceva
/// «canale non cifrato», che per SFTP è falso, e mandava a cercare la cifratura su un
/// server che l'aveva già. Il motivo vero era un altro — SFTP è un protocollo diverso,
/// che BlindPhone non parla — e all'avvio il controllo fallito non dice niente a voce:
/// la frase del registro era l'unica traccia, e doveva essere quella giusta.
/// </para>
/// <para>
/// Il controllo con un indirizzo sbagliato si ferma prima di aprire qualunque
/// collegamento, quindi le prove su <see cref="UpdateService.CheckAsync"/> non usano la
/// rete.
/// </para>
/// </remarks>
public sealed class IndirizzoAggiornamentiTests
{
    [Theory]
    [InlineData("sftp://curtiphone.example.org/files/")]
    [InlineData("SFTP://curtiphone.example.org:2222/files/")]
    [InlineData("ssh://curtiphone.example.org/files/")]
    public void SftpSiChiamaPerNomeENonCanaleNonCifrato(string indirizzo)
    {
        var frase = UpdateService.DescribeAddressProblem(indirizzo);

        Assert.NotNull(frase);
        Assert.Contains("SFTP", frase);
        Assert.Contains("ftps://", frase);
        Assert.DoesNotContain("non cifrato", frase);
        Assert.DoesNotContain("in chiaro", frase);
    }

    [Fact]
    public void FtpInChiaroSiDiceInChiaro()
    {
        var frase = UpdateService.DescribeAddressProblem("ftp://aggiornamenti.example.org/blindphone/");

        Assert.NotNull(frase);
        Assert.Contains("in chiaro", frase);
        Assert.Contains("ftps://", frase);
    }

    [Theory]
    [InlineData("ftps://aggiornamenti.example.org/blindphone/")]
    [InlineData("FTPS://aggiornamenti.example.org:2121/blindphone/")]
    [InlineData("  ftps://aggiornamenti.example.org/blindphone/  ")]
    public void FtpsVaBene(string indirizzo)
    {
        Assert.Null(UpdateService.DescribeAddressProblem(indirizzo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NessunIndirizzoNonEUnProblema(string? indirizzo)
    {
        // Nessun aggiornamento automatico e' una scelta legittima: chi non ha scritto
        // niente non deve sentirsi dire che ha sbagliato qualcosa.
        Assert.Null(UpdateService.DescribeAddressProblem(indirizzo));
    }

    [Theory]
    [InlineData("https://aggiornamenti.example.org/blindphone/")]
    [InlineData("http://aggiornamenti.example.org/blindphone/")]
    [InlineData("aggiornamenti.example.org/blindphone/")]
    [InlineData("smb://server/condivisione/")]
    public void OgniAltroIndirizzoDiceCheServeFtps(string indirizzo)
    {
        var frase = UpdateService.DescribeAddressProblem(indirizzo);

        Assert.NotNull(frase);
        Assert.Contains("ftps://", frase);
    }

    [Fact]
    public async Task IlControlloConUnIndirizzoSftpRiportaLaFraseGiusta()
    {
        // E' la frase che sente chi preme Cerca aggiornamenti, e quella che finisce nel
        // registro all'avvio.
        var servizio = new UpdateService(
            new UpdateSettings { ServerUrl = "sftp://curtiphone.example.org/files/" },
            password: string.Empty,
            installed: new Version(1, 0, 20, 1));

        var esito = await servizio.CheckAsync();

        Assert.True(esito.Failed);
        Assert.False(esito.HasUpdate);
        Assert.Contains("SFTP", esito.Error);
        Assert.DoesNotContain("non cifrato", esito.Error);
    }
}
