using BlindPhone.Audio;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica il percorso delle impostazioni dell'elaborazione del microfono: dal file
/// al motore audio, e dentro al pacchetto di configurazione.
/// </summary>
/// <remarks>
/// <para>
/// Il difetto che queste prove impediscono è già accaduto a un'altra impostazione audio:
/// salvata correttamente, mostrata correttamente nel pannello, <b>non applicata da
/// nessuno</b>. Per questo ogni passaggio è una prova a sé — salvataggio, rilettura,
/// applicazione al motore, pacchetto — e nessuna dà per buono quello che sta a monte.
/// </para>
/// </remarks>
public sealed class ImpostazioniElaborazioneMicrofonoTests : IDisposable
{
    private readonly SettingsService _impostazioni = SettingsService.CreateTemporary("elaborazione-microfono");
    private readonly List<NAudioEngine> _motori = [];

    public void Dispose()
    {
        foreach (var motore in _motori) motore.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private NAudioEngine NuovoMotore()
    {
        var motore = new NAudioEngine();
        _motori.Add(motore);
        return motore;
    }

    // -----------------------------------------------------------------------
    // Salvataggio e rilettura
    // -----------------------------------------------------------------------

    [Fact]
    public void LaSceltaSiSalvaESiRileggeDalFile()
    {
        _impostazioni.Load();

        var scelta = new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Medium };
        Assert.True(_impostazioni.Save(_impostazioni.Current with { MicrophoneProcessing = scelta }));

        // Un servizio nuovo sulla stessa cartella: legge dal file, non dalla memoria.
        var riletto = new SettingsService(_impostazioni.DataDirectory).Load();

        Assert.Equal(scelta, riletto.MicrophoneProcessing);
    }

    [Fact]
    public void IlFileDiUnaVersionePrecedenteNonAccendeNiente()
    {
        _impostazioni.Load();
        Assert.True(_impostazioni.Save(_impostazioni.Current));

        // Si toglie dal file la proprieta', come se lo avesse scritto una versione che
        // non la conosceva.
        var testo = File.ReadAllText(_impostazioni.SettingsPath);
        Assert.Contains("microphoneProcessing", testo, StringComparison.Ordinal);

        var senza = System.Text.Json.Nodes.JsonNode.Parse(testo)!.AsObject();
        senza.Remove("microphoneProcessing");
        File.WriteAllText(_impostazioni.SettingsPath, senza.ToJsonString());

        var riletto = new SettingsService(_impostazioni.DataDirectory).Load();

        Assert.False(riletto.MicrophoneProcessing.IsActive);
    }

    [Fact]
    public void UnaPostazioneAppenaInstallataParteSenzaElaborazione()
        => Assert.False(new AppSettings().MicrophoneProcessing.IsActive);

    // -----------------------------------------------------------------------
    // Applicazione al motore
    // -----------------------------------------------------------------------

    [Fact]
    public void IlMotoreParteSenzaElaborazione()
        => Assert.False(NuovoMotore().MicrophoneProcessing.IsActive);

    [Fact]
    public void LaSceltaArrivaAlMotoreAudio()
    {
        var motore = NuovoMotore();
        var scelta = new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Light };

        AudioSettingsApplier.Apply(motore, new AppSettings { MicrophoneProcessing = scelta });

        Assert.Equal(scelta, motore.MicrophoneProcessing);
    }

    [Fact]
    public void TogliendoLaSceltaIlMotoreTornaAPassareLaVoceComEra()
    {
        var motore = NuovoMotore();

        AudioSettingsApplier.Apply(motore, new AppSettings
        {
            MicrophoneProcessing = new MicrophoneProcessing { AutoGain = true },
        });

        AudioSettingsApplier.Apply(motore, new AppSettings());

        Assert.False(motore.MicrophoneProcessing.IsActive);
    }

    [Fact]
    public void RisalvareLeStesseImpostazioniNonFaRicostruireLaCatenaDelleChiamateInCorso()
    {
        var motore = NuovoMotore();
        var impostazioni = new AppSettings
        {
            MicrophoneProcessing = new MicrophoneProcessing { NoiseSuppression = NoiseSuppressionLevel.Medium },
        };

        AudioSettingsApplier.Apply(motore, impostazioni);
        var primo = motore.MicrophoneProcessing;

        // L'operatore salva il pannello dopo aver toccato tutt'altro. Una chiamata in
        // corso confronta il riferimento a ogni pacchetto e, vedendolo diverso,
        // ricostruirebbe la catena: il livello raggiunto dal controllo automatico
        // ripartirebbe da zero nel mezzo di una frase.
        AudioSettingsApplier.Apply(motore, impostazioni with { RingtonePath = "altro.wav" });

        Assert.Same(primo, motore.MicrophoneProcessing);
    }

    [Fact]
    public void UnaSceltaDiversaSostituisceIlRiferimento()
    {
        var motore = NuovoMotore();

        motore.SetMicrophoneProcessing(new MicrophoneProcessing { AutoGain = true });
        var primo = motore.MicrophoneProcessing;

        motore.SetMicrophoneProcessing(new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Light });

        Assert.NotSame(primo, motore.MicrophoneProcessing);
    }

    [Fact]
    public void UnLivelloCheNonEsisteArrivaAlMotoreComeSpento()
    {
        var motore = NuovoMotore();

        motore.SetMicrophoneProcessing(new MicrophoneProcessing { NoiseSuppression = (NoiseSuppressionLevel)99 });

        Assert.False(motore.MicrophoneProcessing.IsActive);
    }

    // -----------------------------------------------------------------------
    // Pacchetto di configurazione
    // -----------------------------------------------------------------------

    [Fact]
    public void LaSezioneAudioDelPacchettoPortaLElaborazioneDelMicrofono()
    {
        _impostazioni.Load();

        var servizio = new ConfigurationBundleService(_impostazioni, new HotkeyProfileStore(_impostazioni.DataDirectory));
        var scelta = new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Light };

        var pacchetto = new ConfigurationBundle
        {
            Formato = ConfigurationBundle.FormatMarker,
            Revisione = 1,
            Sezioni = [SettingsSection.Audio],
            Impostazioni = new AppSettings { MicrophoneProcessing = scelta },
        };

        Assert.True(servizio.Apply(pacchetto).Success);

        // Un reparto che ha trovato il livello adatto ai propri uffici lo distribuisce
        // insieme ai dispositivi: senza questa riga la sezione Audio lo ignorava.
        Assert.Equal(scelta, _impostazioni.Current.MicrophoneProcessing);
    }

    [Fact]
    public void UnPacchettoSenzaLaSezioneAudioNonToccaLElaborazioneDelMicrofono()
    {
        _impostazioni.Load();

        var propria = new MicrophoneProcessing { NoiseSuppression = NoiseSuppressionLevel.Strong };
        _impostazioni.Save(_impostazioni.Current with { MicrophoneProcessing = propria });

        var servizio = new ConfigurationBundleService(_impostazioni, new HotkeyProfileStore(_impostazioni.DataDirectory));

        var pacchetto = new ConfigurationBundle
        {
            Formato = ConfigurationBundle.FormatMarker,
            Revisione = 2,
            Sezioni = [SettingsSection.Accessibility],
            Impostazioni = new AppSettings
            {
                MicrophoneProcessing = new MicrophoneProcessing { AutoGain = true },
            },
        };

        Assert.True(servizio.Apply(pacchetto).Success);

        Assert.Equal(propria, _impostazioni.Current.MicrophoneProcessing);
    }
}
