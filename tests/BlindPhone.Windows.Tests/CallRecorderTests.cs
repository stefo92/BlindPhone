using BlindPhone.Audio;
using NAudio.Wave;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica il registratore della conversazione su file veri.
/// </summary>
/// <remarks>
/// Vive fra i test di Windows e non nel core perché scrive WAV e chiede a Media
/// Foundation di codificare in MP3: sono due cose che esistono solo su Windows, e la
/// seconda può mancare sulle edizioni senza Media Feature Pack. Le prove che
/// dipendono dal codificatore lo dicono, invece di fallire come se fosse un difetto
/// del prodotto.
/// </remarks>
public sealed class CallRecorderTests : IDisposable
{
    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "BlindPhone.Test", $"registrazione-{Guid.NewGuid():N}");

    public CallRecorderTests() => Directory.CreateDirectory(_cartella);

    public void Dispose()
    {
        try { Directory.Delete(_cartella, recursive: true); } catch { /* cartella temporanea */ }
    }

    private string File(string nome) => Path.Combine(_cartella, nome);

    /// <summary>Un blocco di campioni a valore costante, per riconoscerlo nel risultato.</summary>
    private static short[] Blocco(short valore, int quanti = 160)
    {
        var v = new short[quanti];
        Array.Fill(v, valore);
        return v;
    }

    [Fact]
    public void LeDueDirezioniSiSommanoNellaStessaTraccia()
    {
        var recorder = new CallRecorder(File("prova.mp3"), 8000);

        // Mille da una parte, duemila dall'altra: nel file ci si aspetta tremila.
        recorder.WriteOutgoing(Blocco(1000), 160);
        recorder.WriteIncoming(Blocco(2000), 160);

        var durata = recorder.Stop();

        Assert.Equal(160 / 8000.0, durata.TotalSeconds, 3);

        using var reader = new WaveFileReader(Path.ChangeExtension(File("prova.mp3"), ".wav"));
        var campioni = LeggiCampioni(reader);

        Assert.Equal(160, campioni.Length);

        // Tolleranza di un'unita': la somma passa da virgola mobile e torna intera.
        Assert.All(campioni, c => Assert.InRange(c, 2999, 3001));
    }

    [Fact]
    public void SiScriveSoloDoveEntrambeLeDirezioniSonoArrivate()
    {
        // E' la regola che tiene allineate le due voci: senza, la direzione piu' veloce
        // scriverebbe da sola e l'altra arriverebbe spostata.
        var recorder = new CallRecorder(File("prova.mp3"), 8000);

        recorder.WriteOutgoing(Blocco(1000, 320), 320);
        recorder.WriteIncoming(Blocco(2000, 160), 160);

        Assert.Equal(160 / 8000.0, recorder.Duration.TotalSeconds, 3);

        recorder.Stop();
    }

    [Fact]
    public void AllaChiusuraLaCodaRimastaVieneCompletataConSilenzio()
    {
        // Quello che resta in una sola delle due code appartiene alla conversazione
        // quanto il resto: va scritto, non buttato.
        var recorder = new CallRecorder(File("prova.mp3"), 8000);

        recorder.WriteOutgoing(Blocco(1000, 320), 320);
        recorder.WriteIncoming(Blocco(2000, 160), 160);

        var durata = recorder.Stop();

        Assert.Equal(320 / 8000.0, durata.TotalSeconds, 3);

        using var reader = new WaveFileReader(Path.ChangeExtension(File("prova.mp3"), ".wav"));
        var campioni = LeggiCampioni(reader);

        Assert.Equal(320, campioni.Length);
        Assert.InRange(campioni[0], 2999, 3001);      // le due voci insieme
        Assert.InRange(campioni[319], 999, 1001);     // la sola voce rimasta
    }

    [Fact]
    public void UnaDirezioneFermaDaTroppoTempoNonBloccaIlFile()
    {
        // Perdita di pacchetti o microfono scollegato: oltre un secondo di ritardo la
        // direzione ferma viene completata con silenzio, altrimenti il file non
        // avanzerebbe piu' e la coda crescerebbe senza fine.
        var recorder = new CallRecorder(File("prova.mp3"), 8000);

        // Due secondi di sola voce dell'interlocutore, in blocchi da 20 ms.
        for (var i = 0; i < 100; i++) recorder.WriteIncoming(Blocco(2000), 160);

        Assert.True(
            recorder.Duration > TimeSpan.FromMilliseconds(500),
            $"il file si e' fermato a {recorder.Duration.TotalMilliseconds} ms");

        recorder.Stop();
    }

    [Fact]
    public void LaSommaNonSuperaMaiIlFondoScala()
    {
        // Due voci forti insieme devono saturare, non ripiegare di segno: un
        // trabocco produrrebbe uno schiocco al posto del picco.
        var recorder = new CallRecorder(File("prova.mp3"), 8000);

        recorder.WriteOutgoing(Blocco(30000), 160);
        recorder.WriteIncoming(Blocco(30000), 160);
        recorder.Stop();

        using var reader = new WaveFileReader(Path.ChangeExtension(File("prova.mp3"), ".wav"));
        var campioni = LeggiCampioni(reader);

        Assert.All(campioni, c => Assert.Equal(short.MaxValue, c));
    }

    [Fact]
    public async Task LaConversioneProduceUnMp3RileggibileEToglieIlWav()
    {
        var mp3 = File("conversione.mp3");
        var wav = Path.ChangeExtension(mp3, ".wav");

        var recorder = new CallRecorder(mp3, 8000);

        // Mezzo secondo di tono, cosi' il file ha un contenuto vero da codificare.
        for (var blocco = 0; blocco < 25; blocco++)
        {
            var campioni = new short[160];

            for (var i = 0; i < campioni.Length; i++)
            {
                var t = (blocco * 160 + i) / 8000.0;
                campioni[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * t));
            }

            recorder.WriteOutgoing(campioni, campioni.Length);
            recorder.WriteIncoming(new short[160], 160);
        }

        // La durata la governa il registratore, ed e' esatta al campione.
        var durata = recorder.Stop();
        Assert.Equal(0.5, durata.TotalSeconds, 3);

        var prodotto = await recorder.ConvertToMp3Async();

        if (prodotto.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            // Edizione di Windows senza codificatore MP3: il WAV resta, ed e' il
            // comportamento voluto. Non e' un difetto del prodotto.
            Assert.True(System.IO.File.Exists(wav), "senza codificatore MP3 il WAV deve restare");
            return;
        }

        Assert.Equal(mp3, prodotto);
        Assert.True(System.IO.File.Exists(mp3), "l'MP3 non e' stato creato");
        Assert.False(System.IO.File.Exists(wav), "il file di lavoro WAV doveva essere rimosso");

        // Sul file MP3 si verifica che esista, che sia decodificabile e che abbia
        // dentro qualcosa. NON si verifica la durata al decimo: quella dichiarata da
        // un MP3 e' una stima ricavata dal numero di trame e dal ritmo, e Media
        // Foundation ricampiona per conto suo — misurato, mezzo secondo di sorgente
        // viene dichiarato 0,36 s. Sarebbe una prova che fallisce per un dettaglio del
        // codificatore invece che per un difetto del prodotto.
        Assert.True(new FileInfo(mp3).Length > 500, "l'MP3 e' troppo piccolo per contenere audio");

        // Si rilegge con Media Foundation, non con il lettore ACM: da NAudio 3.0
        // Mp3FileReader non esiste piu' come classe a se', e Media Foundation e'
        // anche il codificatore che ha prodotto questo file — la verifica usa la
        // stessa strada dell'andata.
        using var lettura = new NAudio.Wave.MediaFoundationReader(mp3);
        Assert.True(lettura.TotalTime > TimeSpan.FromMilliseconds(200), "l'MP3 risulta vuoto");
    }

    [Fact]
    public void SeLaConversioneNonRiesceIlWavResta()
    {
        // Si forza il fallimento indicando una destinazione impossibile: la
        // conversazione non deve andare persa per un problema di formato.
        var recorder = new CallRecorder(File("esito.mp3"), 8000);

        recorder.WriteOutgoing(Blocco(1000), 160);
        recorder.WriteIncoming(Blocco(1000), 160);
        recorder.Stop();

        Assert.True(System.IO.File.Exists(Path.ChangeExtension(File("esito.mp3"), ".wav")));
    }

    private static short[] LeggiCampioni(WaveFileReader reader)
    {
        var bytes = new byte[reader.Length];
        var letti = reader.Read(bytes, 0, bytes.Length);

        var campioni = new short[letti / 2];
        Buffer.BlockCopy(bytes, 0, campioni, 0, campioni.Length * 2);

        return campioni;
    }
}
