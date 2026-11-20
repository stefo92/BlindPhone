using System.Collections.Concurrent;
using BlindPhone.Accessibility;
using BlindPhone.Audio;
using BlindPhone.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che la voce di BlindPhone esca dal dispositivo scelto per gli annunci.
/// </summary>
/// <remarks>
/// <para>
/// La sintesi di sistema parlava sempre sul predefinito di Windows: la scelta «Voce
/// degli annunci» si salvava, si mostrava, e non spostava niente. Su un posto operatore
/// con la cuffia come predefinito la voce di BlindPhone finiva nell'orecchio
/// dell'interlocutore — cioè dove la quarta via esisteva per non mandarla.
/// </para>
/// <para>
/// Le prove usano un'uscita finta che annota quello che riceve, e quando la voce
/// potrebbe uscire davvero dal predefinito la tengono a volume zero: girano senza far
/// rumore anche su una postazione con le casse accese.
/// </para>
/// </remarks>
public sealed class VoceSulDispositivoDegliAnnunciTests
{
    private static readonly TimeSpan Attesa = TimeSpan.FromSeconds(15);

    /// <summary>Uscita finta: annota ogni frase ricevuta.</summary>
    private sealed class UscitaFinta : IAnnouncementAudioOutput
    {
        public bool HasDedicatedDevice { get; set; } = true;

        /// <summary>Quello che risponde: <c>false</c> simula un dispositivo che non suona.</summary>
        public bool Esito { get; set; } = true;

        /// <summary>Se ogni frase «suona» finché non viene interrotta.</summary>
        public bool SuonaFinoAllInterruzione { get; set; }

        public ConcurrentQueue<(byte[] Pcm, int Frequenza)> Frasi { get; } = new();

        public SemaphoreSlim Arrivate { get; } = new(0);

        public int Interrotte;

        public async Task<bool> PlaySpeechAsync(byte[] pcm, int sampleRate, CancellationToken cancellationToken)
        {
            Frasi.Enqueue((pcm, sampleRate));
            Arrivate.Release();

            if (SuonaFinoAllInterruzione)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref Interrotte);
                }
            }

            return Esito;
        }
    }

    /// <summary>Registro che conserva i messaggi, per vedere che cosa la voce ha deciso.</summary>
    private sealed class RegistroFinto : ILogger<CompositeSpeechOutput>
    {
        public ConcurrentQueue<string> Messaggi { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messaggi.Enqueue(formatter(state, exception));
    }

    private static CompositeSpeechOutput Voce(UscitaFinta uscita, int volume = 0, ILogger<CompositeSpeechOutput>? registro = null)
        => new([], registro, uscita)
        {
            // La sola voce interna: nessuno screen reader deve poter prendere la frase.
            Mode = SpeechChannelMode.InternalVoiceOnly,
            InternalVoiceVolume = volume,
        };

    private static short Picco(byte[] pcm)
    {
        short picco = 0;

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var campione = Math.Abs((int)BitConverter.ToInt16(pcm, i));
            if (campione > picco) picco = (short)Math.Min(campione, short.MaxValue);
        }

        return picco;
    }

    // -----------------------------------------------------------------------
    // Instradamento
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConUnDispositivoSceltoLaVoceArrivaAlMotoreAudio()
    {
        var uscita = new UscitaFinta();
        using var voce = Voce(uscita);

        voce.Speak("Chiamata in arrivo dall'interno quattro zero due uno", interrupt: false);

        Assert.True(await uscita.Arrivate.WaitAsync(Attesa), "La frase non e' arrivata al dispositivo degli annunci");

        var (pcm, frequenza) = Assert.Single(uscita.Frasi);
        Assert.Equal(CompositeSpeechOutput.SpeechSampleRate, frequenza);

        // Piu' di mezzo secondo di voce, in campioni a sedici bit: e' la frase intera,
        // non un frammento.
        Assert.True(pcm.Length > frequenza, $"Frase troppo corta: {pcm.Length} byte");
    }

    [Fact]
    public async Task SenzaUnDispositivoSceltoLaVoceRestaSulPredefinitoDiSistema()
    {
        // Il predefinito di sistema non e' il predefinito per le comunicazioni che usa il
        // motore: chi non ha mai scelto un dispositivo deve continuare a sentire la voce
        // dove la sentiva.
        var uscita = new UscitaFinta { HasDedicatedDevice = false };
        using var voce = Voce(uscita);

        voce.Speak("Prova", interrupt: false);

        Assert.False(await uscita.Arrivate.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Empty(uscita.Frasi);
    }

    [Fact]
    public async Task IlVolumeDellaVoceLoApplicaLaSintesiAncheSulDispositivoScelto()
    {
        // Il motore suona i campioni cosi' come arrivano: se il volume non fosse gia'
        // dentro, il cursore degli annunci non cambierebbe niente sul dispositivo scelto.
        const string frase = "Chiamata in arrivo dalla coda commerciale";

        var piena = new UscitaFinta();
        using (var voce = Voce(piena, volume: 100))
        {
            voce.Speak(frase, interrupt: false);
            Assert.True(await piena.Arrivate.WaitAsync(Attesa));
        }

        var bassa = new UscitaFinta();
        using (var voce = Voce(bassa, volume: 25))
        {
            voce.Speak(frase, interrupt: false);
            Assert.True(await bassa.Arrivate.WaitAsync(Attesa));
        }

        var picco100 = Picco(Assert.Single(piena.Frasi).Pcm);
        var picco25 = Picco(Assert.Single(bassa.Frasi).Pcm);

        Assert.True(picco100 > 0, "La frase a volume pieno e' muta");
        Assert.True(picco25 < picco100 / 2, $"Volume 25: picco {picco25}, volume 100: picco {picco100}");
    }

    // -----------------------------------------------------------------------
    // Coda e interruzioni
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LeFrasiArrivanoUnaAllaVoltaNellOrdine()
    {
        var uscita = new UscitaFinta();
        using var voce = Voce(uscita);

        voce.Speak("Prima frase, lunga abbastanza da durare qualche secondo intero", interrupt: false);
        voce.Speak("Seconda", interrupt: false);

        Assert.True(await uscita.Arrivate.WaitAsync(Attesa));
        Assert.True(await uscita.Arrivate.WaitAsync(Attesa));

        var frasi = uscita.Frasi.ToArray();
        Assert.Equal(2, frasi.Length);
        Assert.True(frasi[0].Pcm.Length > frasi[1].Pcm.Length, "Le frasi sono arrivate in un altro ordine");
    }

    [Fact]
    public async Task UnaFraseUrgenteInterrompeQuellaCheSuonaESaltaLaCoda()
    {
        var uscita = new UscitaFinta { SuonaFinoAllInterruzione = true };
        using var voce = Voce(uscita);

        voce.Speak("Uno", interrupt: false);
        Assert.True(await uscita.Arrivate.WaitAsync(Attesa), "La prima frase non e' partita");

        // In coda dietro la prima, e superata dall'urgente: non deve suonare mai.
        voce.Speak(
            "Una frase molto lunga rimasta in coda, che parla di molte cose diverse e che "
            + "nessuno deve sentire perche' nel frattempo e' arrivato un annuncio urgente",
            interrupt: false);

        voce.Speak("Tre", interrupt: true);
        Assert.True(await uscita.Arrivate.WaitAsync(Attesa), "La frase urgente non e' partita");

        // Il tempo per una frase in coda di arrivare, se ci fosse ancora.
        Assert.False(await uscita.Arrivate.WaitAsync(TimeSpan.FromMilliseconds(500)));

        voce.Stop();

        var frasi = uscita.Frasi.ToArray();
        Assert.Equal(2, frasi.Length);

        // La seconda arrivata e' la breve urgente, non quella lunga rimasta in coda:
        // meno di tre secondi di voce.
        Assert.True(frasi[1].Pcm.Length < 3 * CompositeSpeechOutput.SpeechSampleRate * 2,
            $"Seconda frase di {frasi[1].Pcm.Length} byte: e' quella in coda");

        Assert.True(Volatile.Read(ref uscita.Interrotte) >= 1, "La frase che suonava non e' stata interrotta");
    }

    [Fact]
    public async Task StopFermaLaFraseCheSuonaSulDispositivoScelto()
    {
        var uscita = new UscitaFinta { SuonaFinoAllInterruzione = true };
        using var voce = Voce(uscita);

        voce.Speak("Frase interrotta", interrupt: false);
        Assert.True(await uscita.Arrivate.WaitAsync(Attesa));

        voce.Stop();

        var limite = DateTime.UtcNow + Attesa;
        while (Volatile.Read(ref uscita.Interrotte) == 0 && DateTime.UtcNow < limite) await Task.Delay(20);

        Assert.Equal(1, Volatile.Read(ref uscita.Interrotte));
    }

    [Fact]
    public async Task SeIlDispositivoNonSuonaLaFraseSiRipeteDalPredefinito()
    {
        // Un annuncio perso e' un'informazione persa. A volume zero: la ripetizione
        // esce davvero dal predefinito, e qui non deve farsi sentire.
        var registro = new RegistroFinto();
        var uscita = new UscitaFinta { Esito = false };
        using var voce = Voce(uscita, volume: 0, registro: registro);

        voce.Speak("Chiamata in arrivo", interrupt: false);
        Assert.True(await uscita.Arrivate.WaitAsync(Attesa));

        var limite = DateTime.UtcNow + Attesa;
        while (!registro.Messaggi.Any(m => m.Contains("si ripete dal predefinito", StringComparison.Ordinal))
               && DateTime.UtcNow < limite)
        {
            await Task.Delay(20);
        }

        Assert.Contains(registro.Messaggi, m => m.Contains("si ripete dal predefinito", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Motore audio
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IlMotoreDiceSeGliAnnunciHannoUnDispositivoScelto()
    {
        var motore = new NAudioEngine();

        try
        {
            Assert.False(motore.HasDedicatedDevice);

            motore.AssignDevice(AudioRoute.Announcements, "{dispositivo-scelto}");
            Assert.True(motore.HasDedicatedDevice);

            motore.AssignDevice(AudioRoute.Announcements, null);
            Assert.False(motore.HasDedicatedDevice);
        }
        finally
        {
            await motore.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnDispositivoVeroAccettaIlFormatoDellaVoce()
    {
        // La voce arriva a 22050 Hz mono, e la scheda lavora quasi sempre a 48000 Hz
        // stereo: se la conversione non ci fosse, la voce non suonerebbe su nessun
        // dispositivo scelto e ripiegherebbe sempre sul predefinito. Si suona un quarto
        // di secondo di silenzio: la prova non si sente.
        //
        // Su una macchina senza dispositivi di riproduzione non c'e' niente da provare.
        var motore = new NAudioEngine();

        try
        {
            var dispositivo = motore.GetPlaybackDevices().FirstOrDefault();
            if (dispositivo is null) return;

            motore.AssignDevice(AudioRoute.Announcements, dispositivo.Id);

            var silenzio = new byte[CompositeSpeechOutput.SpeechSampleRate / 2];
            var suonata = await motore.PlaySpeechAsync(silenzio, CompositeSpeechOutput.SpeechSampleRate, CancellationToken.None);

            Assert.True(suonata, $"Il dispositivo {dispositivo.Name} non ha accettato la voce");
            Assert.True(motore.HasDedicatedDevice);
        }
        finally
        {
            await motore.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnDispositivoDegliAnnunciScomparsoRestituisceFalsoETornaAlPredefinito()
    {
        var motore = new NAudioEngine();

        try
        {
            AudioDeviceChangedEventArgs? cambio = null;
            motore.DeviceChanged += (_, e) => cambio = e;

            motore.AssignDevice(AudioRoute.Announcements, "{0.0.0.00000000}.{dispositivo-che-non-esiste}");

            var suonata = await motore.PlaySpeechAsync(new byte[4410], CompositeSpeechOutput.SpeechSampleRate, CancellationToken.None);

            // Falso, cosi' la voce ripete la frase altrove; e da quel momento gli annunci
            // tornano sul predefinito, invece di riprovare a ogni frase un dispositivo
            // che non c'e'.
            Assert.False(suonata);
            Assert.False(motore.HasDedicatedDevice);
            Assert.Equal(AudioRoute.Announcements, cambio?.Route);
        }
        finally
        {
            await motore.DisposeAsync();
        }
    }
}
