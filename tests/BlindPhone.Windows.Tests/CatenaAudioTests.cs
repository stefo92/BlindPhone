using BlindPhone.Audio;
using NAudio.Wave;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Collauda i tre anelli della catena di riproduzione che riempiono un buffer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scritti prima della migrazione a NAudio 3.0, e per quella.</b> La 3.0 cambia la
/// firma dei provider da <c>Read(byte[] buffer, int offset, int count)</c> a
/// <c>Read(Span&lt;byte&gt;)</c>: sparisce l'offset, e con lui sparisce il posto in cui
/// si sbaglia un indice. Un errore lì non produce un'eccezione né un avviso del
/// compilatore — produce un fruscio sotto la voce, uno schiocco alla fine di un
/// earcon, o un'interruzione ogni pochi secondi.
/// </para>
/// <para>
/// Questi test devono passare <b>prima e dopo</b> la conversione. La firma della
/// chiamata cambia per forza — <c>Read(buffer)</c> al posto di
/// <c>Read(buffer, 0, buffer.Length)</c> — ma <b>nessuna asserzione è stata
/// toccata</b>: i valori attesi sono quelli misurati su NAudio 2.3.0. Se per farli
/// passare fosse servito cambiare un valore atteso, sarebbe stata la conversione a
/// essere sbagliata.
/// </para>
/// <para>
/// <b>L'invariante che li tiene insieme è una sola</b>: una lettura restituisce sempre
/// esattamente il numero di byte richiesti, e la parte non riempita con audio vero è
/// riempita con silenzio. WASAPI copia quello che riceve ma dichiara alla scheda audio
/// il conteggio pieno: quello che non viene scritto resta memoria non inizializzata e
/// viene riprodotto. È il rumore bianco che si sentiva sotto la conversazione.
/// </para>
/// </remarks>
public sealed class CatenaAudioTests
{
    private static WaveFormat Formato => new(8000, 16, 1);

    /// <summary>
    /// Il buffer sorgente configurato come lo configura il prodotto.
    /// </summary>
    /// <remarks>
    /// <b>ReadFully disattivato, ed e' il dettaglio che decide tutto.</b> Con il valore
    /// predefinito di NAudio - attivo - il buffer completa da solo con silenzio e
    /// restituisce sempre la lettura piena: il buffer di jitter non vedrebbe mai una
    /// lettura corta, non conterebbe mai uno svuotamento, e il collaudo misurerebbe una
    /// situazione che sulla postazione non esiste. La riga corrispondente del prodotto
    /// e' in SipAudioEndPoint, dove il buffer di riproduzione viene creato.
    /// </remarks>
    private static BufferedWaveProvider SorgenteComeInProduzione()
        => new(Formato) { ReadFully = false };

    /// <summary>Sorgente che restituisce quanto le si dice, per provocare letture corte.</summary>
    private sealed class SorgenteCorta(WaveFormat formato, int byteDaRestituire, byte riempimento) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = formato;

        public int Read(Span<byte> buffer)
        {
            var quanti = Math.Min(byteDaRestituire, buffer.Length);
            buffer[..quanti].Fill(riempimento);

            return quanti;
        }
    }

    // -----------------------------------------------------------------------
    // Buffer di jitter
    // -----------------------------------------------------------------------

    [Fact]
    public void IlBufferDiJitter_PrimaDiRiempirsiRestituisceSilenzioMaiUnaLetturaCorta()
    {
        // Restituire zero byte farebbe interpretare a WasapiOut la fine del flusso e
        // fermerebbe la riproduzione: durante il riempimento si restituisce silenzio,
        // non niente.
        var sorgente = SorgenteComeInProduzione();
        var jitter = new JitterBufferProvider(sorgente, targetMilliseconds: 100);

        var buffer = new byte[320];
        Array.Fill(buffer, (byte)0xAB);

        var letti = jitter.Read(buffer);

        Assert.Equal(buffer.Length, letti);
        Assert.All(buffer, b => Assert.Equal(0, b));
        Assert.Equal(0, jitter.Underruns);
    }

    [Fact]
    public void IlBufferDiJitter_RiempitoIlCuscinettoConsegnaLAudio()
    {
        var sorgente = SorgenteComeInProduzione();
        var jitter = new JitterBufferProvider(sorgente, targetMilliseconds: 20);

        // 20 ms a 8000 Hz, 16 bit, mono = 320 byte: si supera il cuscinetto.
        var audio = new byte[1600];
        Array.Fill(audio, (byte)0x7F);
        sorgente.AddSamples(audio, 0, audio.Length);

        var buffer = new byte[320];
        var letti = jitter.Read(buffer);

        Assert.Equal(buffer.Length, letti);
        Assert.All(buffer, b => Assert.Equal(0x7F, b));
    }

    [Fact]
    public void IlBufferDiJitter_QuandoSiSvuotaCompletaConSilenzioELoConta()
    {
        var sorgente = SorgenteComeInProduzione();
        var jitter = new JitterBufferProvider(sorgente, targetMilliseconds: 20);

        var audio = new byte[480];
        Array.Fill(audio, (byte)0x7F);
        sorgente.AddSamples(audio, 0, audio.Length);

        var buffer = new byte[640];
        Array.Fill(buffer, (byte)0xAB);

        var letti = jitter.Read(buffer);

        // Lettura piena comunque, audio davanti e silenzio dietro.
        Assert.Equal(buffer.Length, letti);
        Assert.Equal(0x7F, buffer[0]);
        Assert.Equal(0x7F, buffer[479]);
        Assert.Equal(0, buffer[480]);
        Assert.Equal(0, buffer[^1]);

        // Lo svuotamento e' il numero con cui si regola il cuscinetto: va contato.
        Assert.Equal(1, jitter.Underruns);
        Assert.True(jitter.SilenceMilliseconds > 0);
    }

    [Fact]
    public void IlBufferDiJitter_DopoUnoSvuotamentoTornaARiempirsi()
    {
        // Restare a cuscinetto esaurito produrrebbe un'interruzione a ogni pacchetto,
        // che e' il sintomo da cui e' nato questo componente.
        var sorgente = SorgenteComeInProduzione();
        var jitter = new JitterBufferProvider(sorgente, targetMilliseconds: 20);

        var audio = new byte[480];
        Array.Fill(audio, (byte)0x7F);
        sorgente.AddSamples(audio, 0, audio.Length);

        var buffer = new byte[640];
        jitter.Read(buffer);

        // Un solo pacchetto non basta a ricostruire il cuscinetto: si resta a silenzio.
        sorgente.AddSamples(new byte[160], 0, 160);
        Array.Fill(buffer, (byte)0xAB);

        Assert.Equal(buffer.Length, jitter.Read(buffer));
        Assert.All(buffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void IlBufferDiJitter_AzzeraIContatoriSuRichiesta()
    {
        var sorgente = SorgenteComeInProduzione();
        var jitter = new JitterBufferProvider(sorgente, targetMilliseconds: 20);

        jitter.Read(new byte[320]);
        Assert.True(jitter.SilenceMilliseconds > 0);

        jitter.ResetCounters();

        Assert.Equal(0, jitter.Underruns);
        Assert.Equal(0, jitter.SilenceMilliseconds);
    }

    // -----------------------------------------------------------------------
    // Guardia contro le letture parziali
    // -----------------------------------------------------------------------

    [Fact]
    public void LaGuardia_CompletaLaLetturaCortaConSilenzio()
    {
        var guardia = new SilencePaddedProvider(new SorgenteCorta(Formato, byteDaRestituire: 100, riempimento: 0x7F));

        var buffer = new byte[320];
        Array.Fill(buffer, (byte)0xAB);

        var letti = guardia.Read(buffer);

        Assert.Equal(buffer.Length, letti);
        Assert.Equal(0x7F, buffer[99]);
        Assert.Equal(0, buffer[100]);
        Assert.Equal(0, buffer[^1]);
        Assert.Equal(220, guardia.PaddedBytes);
    }

    [Fact]
    public void LaGuardia_NonToccaUnaLetturaGiaPiena()
    {
        var guardia = new SilencePaddedProvider(new SorgenteCorta(Formato, byteDaRestituire: 1000, riempimento: 0x7F));

        var buffer = new byte[320];
        var letti = guardia.Read(buffer);

        Assert.Equal(buffer.Length, letti);
        Assert.All(buffer, b => Assert.Equal(0x7F, b));
        Assert.Equal(0, guardia.PaddedBytes);
    }

    [Fact]
    public void LaGuardia_ConSorgenteEsauritaRestituisceSilenzioPieno()
    {
        var guardia = new SilencePaddedProvider(new SorgenteCorta(Formato, byteDaRestituire: 0, riempimento: 0x7F));

        var buffer = new byte[320];
        Array.Fill(buffer, (byte)0xAB);

        Assert.Equal(buffer.Length, guardia.Read(buffer));
        Assert.All(buffer, b => Assert.Equal(0, b));
        Assert.Equal(320, guardia.PaddedBytes);
    }

    // -----------------------------------------------------------------------
    // Tono stereo degli earcon
    // -----------------------------------------------------------------------

    private static ToneEarconPlayer.StereoToneProvider Tono(float[] campioni, float pan)
        => new(campioni, pan);

    [Fact]
    public void IlTono_AlCentroDistribuisceUgualmenteFraIDueCanali()
    {
        var tono = Tono([1f, 1f], pan: 0f);

        var buffer = new float[4];
        var letti = tono.Read(buffer);

        Assert.Equal(4, letti);

        // Legge di potenza costante: al centro entrambi i guadagni valgono radice di
        // un mezzo, cioe' circa 0,707. Sinistro e destro devono coincidere.
        Assert.Equal(buffer[0], buffer[1], 5);
        Assert.Equal(0.7071f, buffer[0], 3);
    }

    [Fact]
    public void IlTono_TuttoADestraLasciaMutoIlSinistro()
    {
        var tono = Tono([1f, 1f], pan: 1f);

        var buffer = new float[4];
        tono.Read(buffer);

        // Interleaving: pari a sinistra, dispari a destra.
        Assert.Equal(0f, buffer[0], 5);
        Assert.Equal(1f, buffer[1], 5);
        Assert.Equal(0f, buffer[2], 5);
        Assert.Equal(1f, buffer[3], 5);
    }

    [Fact]
    public void IlTono_FinitiICampioniCompletaConSilenzioInvecheDiAccorciare()
    {
        // E' la riga che evita lo schiocco a fine earcon: restituire meno di quanto
        // richiesto fa riprodurre a WASAPI la memoria non inizializzata in coda.
        var tono = Tono([1f], pan: 0f);

        var buffer = new float[8];
        Array.Fill(buffer, 42f);

        var letti = tono.Read(buffer);

        Assert.Equal(buffer.Length, letti);
        Assert.All(buffer[2..], v => Assert.Equal(0f, v, 5));
    }

    [Fact]
    public void IlTono_EsauritoRestituisceZero()
    {
        // Zero significa "flusso finito" e ferma la riproduzione: e' corretto solo
        // quando non c'e' davvero piu' niente, non a meta' earcon.
        var tono = Tono([1f, 1f], pan: 0f);

        var buffer = new float[4];
        tono.Read(buffer);

        Assert.Equal(0, tono.Read(buffer));
    }
}
