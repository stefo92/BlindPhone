using System.Text.Json;
using System.Text.Json.Serialization;
using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica la configurazione dell'elaborazione del microfono.
/// </summary>
/// <remarks>
/// <para>
/// Il valore di partenza è «nessuna elaborazione», e deve esserlo in ogni via per cui
/// una configurazione può arrivare: nuova, riletta da un file scritto prima che
/// esistesse, o modificata a mano. Un operatore che aggiorna il programma non deve
/// ritrovarsi la voce elaborata senza averlo chiesto.
/// </para>
/// </remarks>
public sealed class ElaborazioneMicrofonoDominioTests
{
    // Le stesse opzioni con cui SettingsService scrive e legge il file di configurazione.
    private static readonly JsonSerializerOptions OpzioniFile = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void IlValorePredefinitoENessunaElaborazione()
    {
        var predefinito = new MicrophoneProcessing();

        Assert.False(predefinito.AutoGain);
        Assert.Equal(NoiseSuppressionLevel.Off, predefinito.NoiseSuppression);
        Assert.False(predefinito.IsActive);
        Assert.Equal(predefinito, MicrophoneProcessing.None);
    }

    [Theory]
    [InlineData(false, NoiseSuppressionLevel.Off, false)]
    [InlineData(true, NoiseSuppressionLevel.Off, true)]
    [InlineData(false, NoiseSuppressionLevel.Light, true)]
    [InlineData(false, NoiseSuppressionLevel.Strong, true)]
    [InlineData(true, NoiseSuppressionLevel.Medium, true)]
    public void EAttivaSeAlmenoUnaDelleDueFunzioniLoE(bool livello, NoiseSuppressionLevel rumore, bool attesa)
        => Assert.Equal(attesa, new MicrophoneProcessing { AutoGain = livello, NoiseSuppression = rumore }.IsActive);

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(4)]
    public void UnLivelloCheNonEsisteEquivaleASpento(int numero)
    {
        var strano = new MicrophoneProcessing { NoiseSuppression = (NoiseSuppressionLevel)numero };

        // Sul percorso della voce e' meglio non fare niente che fare qualcosa di
        // imprevisto.
        Assert.Equal(NoiseSuppressionLevel.Off, strano.Level);
        Assert.False(strano.IsActive);
        Assert.Equal(NoiseSuppressionLevel.Off, strano.Sanitized().NoiseSuppression);
    }

    [Fact]
    public void UnLivelloValidoNonVieneToccato()
    {
        var valido = new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Medium };

        Assert.Equal(valido, valido.Sanitized());
    }

    // -----------------------------------------------------------------------
    // File di configurazione
    // -----------------------------------------------------------------------

    [Fact]
    public void ScriveSoloLeDueScelteEDaiNomiLeggibili()
    {
        var json = JsonSerializer.Serialize(
            new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Light },
            OpzioniFile);

        Assert.Contains("\"autoGain\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"noiseSuppression\": \"Light\"", json, StringComparison.Ordinal);

        // I due valori derivati non stanno nel file: scritti accanto a quelli veri
        // lascerebbero credere a chi lo modifica a mano che si possano cambiare, e non
        // si leggono comunque.
        Assert.DoesNotContain("isActive", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"level\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SiRileggeComeEraStatoScritto()
    {
        var originale = new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Strong };

        var riletto = JsonSerializer.Deserialize<MicrophoneProcessing>(
            JsonSerializer.Serialize(originale, OpzioniFile), OpzioniFile);

        Assert.Equal(originale, riletto);
    }

    [Fact]
    public void UnFileScrittoPrimaCheEsistesseLeggeNessunaElaborazione()
    {
        // Un settings.json di una versione precedente non ha la proprieta': l'oggetto
        // arriva vuoto, e vuoto significa spento.
        var riletto = JsonSerializer.Deserialize<MicrophoneProcessing>("{}", OpzioniFile);

        Assert.Equal(MicrophoneProcessing.None, riletto);
    }

    [Fact]
    public void UnNumeroScrittoAManoNelFileEUnLivelloCheNonEsisteEQuindiSpento()
    {
        var riletto = JsonSerializer.Deserialize<MicrophoneProcessing>(
            """{ "noiseSuppression": 99 }""", OpzioniFile)!;

        Assert.False(riletto.IsActive);
    }
}
