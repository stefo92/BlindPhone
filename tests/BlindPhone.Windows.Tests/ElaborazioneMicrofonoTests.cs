using System.Diagnostics;
using BlindPhone.Audio;
using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica il controllo del livello e la riduzione del rumore sul microfono.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cosa provano e cosa no.</b> Provano il comportamento misurabile della catena su
/// segnali di prova deterministici: una voce sintetica a sillabe con pause, e rumore
/// bianco o di ronzio. Dicono se il livello arriva dove deve, se il rumore scende fra
/// una parola e l'altra, se la voce resta, se il ritardo è quello dichiarato e se un
/// difetto dell'elaborazione lascia passare la voce com'è. <b>Non dicono come suona</b>:
/// la deformazione che una riduzione forte dà a una voce vera si giudica ascoltando, e
/// i valori scelti per i tre livelli vanno confermati con una cuffia vera.
/// </para>
/// <para>
/// Le soglie sono prese con margine dalle misure fatte per sceglierle — vedi i
/// commenti in <c>MicrophoneProcessor</c> — in modo che un cambio di versione della
/// libreria che peggiora davvero il risultato faccia cadere la prova, e una variazione
/// di un decibel no.
/// </para>
/// </remarks>
public sealed class ElaborazioneMicrofonoTests
{
    private static MicrophoneProcessing SoloLivello => new() { AutoGain = true };

    private static MicrophoneProcessing SoloRumore(NoiseSuppressionLevel livello)
        => new() { NoiseSuppression = livello };

    // -----------------------------------------------------------------------
    // Segnali di prova
    // -----------------------------------------------------------------------

    private static double Db(double lineare) => 20 * Math.Log10(Math.Max(lineare, 1e-12));

    /// <summary>
    /// Voce sintetica: armoniche di una fondamentale che oscilla, inviluppo sillabico a
    /// tre al secondo, mezzo secondo di parlato e mezzo di pausa.
    /// </summary>
    private static float[] Voce(int frequenza, int secondi, double rmsDb)
    {
        var n = frequenza * secondi;
        var y = new float[n];

        for (var i = 0; i < n; i++)
        {
            var t = (double)i / frequenza;
            var attivo = (int)(t * 2) % 2 == 0;
            var f0 = 140 + 25 * Math.Sin(2 * Math.PI * 0.7 * t);
            var inviluppo = attivo ? 0.5 * (1 + Math.Sin(2 * Math.PI * 3 * t - Math.PI / 2)) : 0;

            double s = 0;
            for (var h = 1; h <= 20; h++)
            {
                if (f0 * h < frequenza / 2.0 - 300) s += Math.Sin(2 * Math.PI * f0 * h * t) / Math.Pow(h, 0.9);
            }

            y[i] = (float)(s * inviluppo);
        }

        // Il livello si dichiara sul parlato, non su tutto: con le pause dentro il valore
        // efficace complessivo sarebbe tre decibel piu' basso e ogni soglia di prova
        // dipenderebbe da quanto dura la pausa.
        double somma = 0;
        var conteggio = 0;
        for (var i = 0; i < n; i++)
        {
            if (Math.Abs(y[i]) <= 1e-6) continue;
            somma += (double)y[i] * y[i];
            conteggio++;
        }

        var guadagno = (float)(Math.Pow(10, rmsDb / 20) / Math.Sqrt(somma / Math.Max(1, conteggio)));
        for (var i = 0; i < n; i++) y[i] *= guadagno;

        return y;
    }

    private static float[] RumoreBianco(int frequenza, int secondi, double rmsDb, int seme)
    {
        var casuale = new Random(seme);
        var n = frequenza * secondi;
        var y = new float[n];

        for (var i = 0; i < n; i++) y[i] = (float)(casuale.NextDouble() * 2 - 1);

        double somma = 0;
        foreach (var v in y) somma += (double)v * v;

        var guadagno = (float)(Math.Pow(10, rmsDb / 20) / Math.Sqrt(somma / n));
        for (var i = 0; i < n; i++) y[i] *= guadagno;

        return y;
    }

    private static float[] Somma(float[] a, float[] b)
    {
        var y = new float[a.Length];
        for (var i = 0; i < y.Length; i++) y[i] = a[i] + b[i];
        return y;
    }

    /// <summary>Elabora un segnale a pacchetti da venti millisecondi, come il percorso di cattura.</summary>
    private static float[] Elabora(MicrophoneProcessor processore, float[] ingresso, int frequenza)
    {
        var blocco = frequenza * 20 / 1000;
        var uscita = (float[])ingresso.Clone();

        for (var inizio = 0; inizio + blocco <= uscita.Length; inizio += blocco)
        {
            processore.Process(uscita.AsSpan(inizio, blocco));
        }

        return uscita;
    }

    /// <summary>
    /// Sposta l'uscita indietro del ritardo che l'elaborazione ha aggiunto, perche' si
    /// confronti campione per campione con l'ingresso.
    /// </summary>
    /// <remarks>
    /// Senza questo la misura sbaglia nel modo piu' fuorviante: nei primi sedici
    /// millisecondi di ogni pausa l'uscita contiene ancora la coda della parola
    /// appena finita, che e' molto piu' forte del rumore, e la «riduzione del rumore»
    /// risulta negativa — cioe' il rumore sembra aumentato — su un segnale che in
    /// realta' e' stato migliorato.
    /// </remarks>
    private static float[] Allineata(float[] uscita, MicrophoneProcessor processore, int frequenza)
    {
        var ritardo = (int)Math.Round(processore.LatencyMilliseconds * frequenza / 1000.0);
        var spostata = new float[uscita.Length];
        Array.Copy(uscita, ritardo, spostata, 0, uscita.Length - ritardo);
        return spostata;
    }

    /// <summary>Livello efficace in dBFS dei soli campioni dove la voce pulita e' presente o assente.</summary>
    private static double Livello(float[] segnale, float[] voce, int dalSecondo, int alSecondo, int frequenza, bool inParola)
    {
        double somma = 0;
        var conteggio = 0;

        for (var i = frequenza * dalSecondo; i < Math.Min(segnale.Length, frequenza * alSecondo); i++)
        {
            var presente = Math.Abs(voce[i]) > 1e-6;
            if (presente != inParola) continue;

            somma += (double)segnale[i] * segnale[i];
            conteggio++;
        }

        return Db(Math.Sqrt(somma / Math.Max(1, conteggio)));
    }

    private static MicrophoneProcessor Con(MicrophoneProcessing impostazione, int frequenza)
    {
        var processore = new MicrophoneProcessor(frequenza);
        processore.Apply(impostazione);
        return processore;
    }

    // -----------------------------------------------------------------------
    // Spenta: la voce passa com'e'
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    public void SenzaElaborazioneLaVocePassaIdentica(int frequenza)
    {
        var processore = Con(MicrophoneProcessing.None, frequenza);
        var ingresso = Somma(Voce(frequenza, 3, -25), RumoreBianco(frequenza, 3, -50, 1));

        var uscita = Elabora(processore, ingresso, frequenza);

        // Identica, non «quasi»: chi non ha acceso niente non deve ricevere un percorso
        // diverso da quello di prima.
        Assert.Equal(ingresso, uscita);
        Assert.False(processore.IsActive);
        Assert.Equal(0, processore.LatencyMilliseconds);
        Assert.Equal("nessuna", processore.Describe());
    }

    [Fact]
    public void UnLivelloInesistenteEquivaleASpento()
    {
        // Un file di configurazione modificato a mano puo' contenere qualunque numero.
        var processore = Con(new MicrophoneProcessing { NoiseSuppression = (NoiseSuppressionLevel)99 }, 8000);

        Assert.False(processore.IsActive);
        Assert.Equal(0, processore.LatencyMilliseconds);
    }

    // -----------------------------------------------------------------------
    // Controllo automatico del livello
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    public void UnaVoceDeboleVieneAlzataVersoIlLivelloObiettivo(int frequenza)
    {
        // Il microfono di una postazione misurata dava picchi a -23 dBFS, cioe' un valore
        // efficace intorno a -35: e' il caso per cui il controllo esiste.
        var voce = Voce(frequenza, 12, -35);
        var processore = Con(SoloLivello, frequenza);

        var uscita = Elabora(processore, voce, frequenza);

        var prima = Livello(voce, voce, 6, 12, frequenza, inParola: true);
        var dopo = Livello(uscita, voce, 6, 12, frequenza, inParola: true);

        Assert.InRange(prima, -36, -34);
        Assert.InRange(dopo, -23, -17);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    public void UnaVoceForteVieneAbbassataSenzaSaturare(int frequenza)
    {
        var voce = Voce(frequenza, 12, -8);
        var processore = Con(SoloLivello, frequenza);

        var uscita = Elabora(processore, voce, frequenza);

        var dopo = Livello(uscita, voce, 6, 12, frequenza, inParola: true);
        var picco = Db(uscita.Max(v => Math.Abs(v)));

        Assert.InRange(dopo, -23, -17);

        // Il limitatore sta a -1 dBFS: sopra, la conversione a sedici bit satura.
        Assert.True(picco <= -0.4, $"Picco a {picco:0.0} dBFS: la voce satura");
    }

    [Fact]
    public void NelleLunghePauseIlGuadagnoNonSale()
    {
        // Senza la rilevazione vocale il controllo, in cinque secondi di silenzio,
        // crescerebbe fino al guadagno massimo e alla prima parola la voce arriverebbe
        // schiacciata: e' il difetto classico di questo tipo di controllo.
        const int f = 8000;

        var parlato = Somma(Voce(f, 8, -35), RumoreBianco(f, 8, -62, 2));
        var pausa = RumoreBianco(f, 6, -62, 3);
        var ingresso = parlato.Concat(pausa).ToArray();

        var uscita = Elabora(Con(SoloLivello, f), ingresso, f);

        var vocePulita = Voce(f, 8, -35).Concat(new float[f * 6]).ToArray();

        var guadagnoInParola = Livello(uscita, vocePulita, 4, 8, f, inParola: true)
                               - Livello(ingresso, vocePulita, 4, 8, f, inParola: true);

        var guadagnoInPausa = Livello(uscita, vocePulita, 11, 14, f, inParola: false)
                              - Livello(ingresso, vocePulita, 11, 14, f, inParola: false);

        Assert.True(
            guadagnoInPausa <= guadagnoInParola + 3,
            $"Guadagno in parola {guadagnoInParola:0.0} dB, dopo cinque secondi di pausa {guadagnoInPausa:0.0} dB");

        // E in ogni caso mai oltre il massimo dichiarato.
        Assert.True(guadagnoInPausa <= 25, $"Guadagno di {guadagnoInPausa:0.0} dB nelle pause");
    }

    // -----------------------------------------------------------------------
    // Riduzione del rumore
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    public void LaRiduzioneLeggeraAbbassaIlRumoreFraUnaParolaELAltra(int frequenza)
    {
        var voce = Voce(frequenza, 14, -26);
        var rumoroso = Somma(voce, RumoreBianco(frequenza, 14, -42, 4));

        var processore = Con(SoloRumore(NoiseSuppressionLevel.Light), frequenza);
        var uscita = Allineata(Elabora(processore, rumoroso, frequenza), processore, frequenza);

        var prima = Livello(rumoroso, voce, 5, 14, frequenza, inParola: false);
        var dopo = Livello(uscita, voce, 5, 14, frequenza, inParola: false);

        // Misurato fra 7,7 e 8,6 dB sul rumore bianco. Cinque lascia il margine di una
        // variazione della libreria che non peggiora niente di udibile.
        Assert.True(prima - dopo >= 5, $"Rumore nelle pause da {prima:0.0} a {dopo:0.0} dBFS: solo {prima - dopo:0.0} dB");
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    public void PiuAltoELivelloPiuIlRumoreScende(int frequenza)
    {
        var voce = Voce(frequenza, 14, -26);
        var rumoroso = Somma(voce, RumoreBianco(frequenza, 14, -42, 5));

        double Riduzione(NoiseSuppressionLevel livello)
        {
            var processore = Con(SoloRumore(livello), frequenza);
            var uscita = Allineata(Elabora(processore, rumoroso, frequenza), processore, frequenza);

            return Livello(rumoroso, voce, 5, 14, frequenza, inParola: false)
                   - Livello(uscita, voce, 5, 14, frequenza, inParola: false);
        }

        var leggera = Riduzione(NoiseSuppressionLevel.Light);
        var media = Riduzione(NoiseSuppressionLevel.Medium);
        var forte = Riduzione(NoiseSuppressionLevel.Strong);

        // Se i tre livelli non fossero ordinati, la scelta nel pannello — «leggera»,
        // «media», «forte» — direbbe una cosa e farebbe un'altra.
        Assert.True(leggera < media, $"Leggera {leggera:0.0} dB, media {media:0.0} dB");
        Assert.True(media < forte, $"Media {media:0.0} dB, forte {forte:0.0} dB");
    }

    [Theory]
    [InlineData(NoiseSuppressionLevel.Light)]
    [InlineData(NoiseSuppressionLevel.Medium)]
    [InlineData(NoiseSuppressionLevel.Strong)]
    public void LaRiduzioneNonSpegneLaVoce(NoiseSuppressionLevel livello)
    {
        const int f = 8000;

        var voce = Voce(f, 14, -26);
        var rumoroso = Somma(voce, RumoreBianco(f, 14, -45, 6));

        var processore = Con(SoloRumore(livello), f);

        // Il confronto tiene conto del ritardo: senza, lo sfasamento si leggerebbe come
        // perdita di livello.
        var allineata = Allineata(Elabora(processore, rumoroso, f), processore, f);

        var prima = Livello(rumoroso, voce, 5, 14, f, inParola: true);
        var dopo = Livello(allineata, voce, 5, 14, f, inParola: true);

        // Misurato fra -0,4 e -1,3 dB. Due decibel e' il punto in cui la voce comincia
        // a suonare piu' piccola di com'e'.
        Assert.True(dopo - prima > -2, $"La voce passa da {prima:0.0} a {dopo:0.0} dBFS");
    }

    // -----------------------------------------------------------------------
    // Ritardo
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(8000, 128)]
    [InlineData(16000, 256)]
    [InlineData(48000, 1024)]
    [InlineData(4000, 64)]
    public void IlFrameDiAnalisiDuraCircaSediciMillisecondiEUnaPotenzaDiDue(int frequenza, int atteso)
        => Assert.Equal(atteso, MicrophoneProcessor.NoiseFrameSize(frequenza));

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    public void LaRiduzioneDelRumoreAggiungeCircaSediciMillisecondi(int frequenza)
    {
        // E' il costo che il pannello dichiara: il valore predefinito della libreria
        // darebbe 64 millisecondi a 8 kHz, quattro volte tanto.
        foreach (var livello in new[] { NoiseSuppressionLevel.Light, NoiseSuppressionLevel.Medium, NoiseSuppressionLevel.Strong })
        {
            var ritardo = Con(SoloRumore(livello), frequenza).LatencyMilliseconds;
            Assert.InRange(ritardo, 15.5, 16.5);
        }
    }

    [Fact]
    public void ILimitatoreDelControlloDelLivelloAggiungePocoPiuDiDueMillisecondi()
    {
        var ritardo = Con(SoloLivello, 8000).LatencyMilliseconds;

        Assert.InRange(ritardo, 1.5, 3.0);
    }

    // -----------------------------------------------------------------------
    // Guasti: la voce passa comunque
    // -----------------------------------------------------------------------

    [Fact]
    public void SeLElaborazioneProduceValoriNonValidiSiEsclude()
    {
        const int f = 8000;

        var processore = Con(new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Medium }, f);

        // Qualche pacchetto normale, cosi' la catena e' a regime.
        var normale = Voce(f, 2, -30);
        Elabora(processore, normale, f);
        Assert.True(processore.IsActive);

        var guasto = new float[160];
        Array.Fill(guasto, 0.1f);
        guasto[10] = float.NaN;

        processore.Process(guasto);

        Assert.NotNull(processore.Fault);
        Assert.False(processore.IsActive);

        // Da qui in poi la voce passa com'e': un pacchetto di rumore al posto della voce
        // e' un difetto che l'interlocutore sente e l'operatore non sa di avere.
        var voce = new float[160];
        Array.Fill(voce, 0.25f);
        var copia = (float[])voce.Clone();

        processore.Process(voce);

        Assert.Equal(copia, voce);
    }

    [Fact]
    public void UnaNuovaConfigurazioneRiprovaDopoUnGuasto()
    {
        const int f = 8000;

        var processore = Con(SoloLivello, f);
        var guasto = new float[160];
        guasto[0] = float.NaN;
        processore.Process(guasto);

        Assert.NotNull(processore.Fault);

        processore.Apply(SoloRumore(NoiseSuppressionLevel.Light));

        Assert.Null(processore.Fault);
        Assert.True(processore.IsActive);
    }

    // -----------------------------------------------------------------------
    // Cambio a caldo
    // -----------------------------------------------------------------------

    [Fact]
    public void CambiareConfigurazioneAMetaChiamataNonRompeLaVoce()
    {
        const int f = 8000;

        var voce = Voce(f, 8, -30);
        var processore = new MicrophoneProcessor(f);

        // Spenta, poi livello, poi livello e riduzione forte, poi solo riduzione, poi spenta:
        // ogni cambio avviene fra un pacchetto e l'altro, come dal pannello.
        var passi = new[]
        {
            MicrophoneProcessing.None,
            SoloLivello,
            new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Strong },
            SoloRumore(NoiseSuppressionLevel.Light),
            MicrophoneProcessing.None,
        };

        var blocco = f * 20 / 1000;
        var uscita = (float[])voce.Clone();
        var pacchetti = uscita.Length / blocco;

        for (var p = 0; p < pacchetti; p++)
        {
            processore.Apply(passi[p * passi.Length / pacchetti]);
            processore.Process(uscita.AsSpan(p * blocco, blocco));
        }

        Assert.All(uscita, v => Assert.True(float.IsFinite(v)));
        Assert.True(uscita.Max(v => Math.Abs(v)) <= 1.0f, "Un cambio di configurazione ha prodotto un picco oltre il fondo scala");
        Assert.Null(processore.Fault);
    }

    // -----------------------------------------------------------------------
    // Costo
    // -----------------------------------------------------------------------

    [Fact]
    public void LaCatenaCompletaCostaPocoPerPacchetto()
    {
        const int f = 8000;
        const int pacchetti = 1500;

        var processore = Con(
            new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Strong }, f);

        var ingresso = Somma(Voce(f, 30, -30), RumoreBianco(f, 30, -50, 7));
        var blocco = f * 20 / 1000;

        // Riscaldamento: la prima chiamata paga la compilazione.
        Elabora(processore, ingresso.AsSpan(0, blocco * 20).ToArray(), f);

        var cronometro = Stopwatch.StartNew();
        for (var p = 0; p < pacchetti; p++)
        {
            processore.Process(ingresso.AsSpan((p * blocco) % (ingresso.Length - blocco), blocco));
        }

        cronometro.Stop();

        var medioMs = cronometro.Elapsed.TotalMilliseconds / pacchetti;

        // Misurato circa 0,05 ms su un pacchetto che dura 20. Due millisecondi sono il
        // dieci per cento del tempo disponibile: se si arriva li', l'elaborazione ha
        // smesso di essere trascurabile sul thread della scheda audio.
        Assert.True(medioMs < 2.0, $"{medioMs:0.000} ms per pacchetto da 20 ms");
    }

    // -----------------------------------------------------------------------
    // Descrizione per la diagnostica
    // -----------------------------------------------------------------------

    [Fact]
    public void LaDescrizioneDiceCosaERitardo()
    {
        var processore = Con(
            new MicrophoneProcessing { AutoGain = true, NoiseSuppression = NoiseSuppressionLevel.Medium }, 8000);

        var testo = processore.Describe();

        Assert.Contains("controllo del livello", testo, StringComparison.Ordinal);
        Assert.Contains("riduzione del rumore media", testo, StringComparison.Ordinal);
        Assert.Contains("ritardo aggiunto", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LaDescrizioneDiUnaCatenaEsclusaDiceIlMotivo()
    {
        var processore = Con(SoloLivello, 8000);
        var guasto = new float[160];
        guasto[0] = float.NaN;
        processore.Process(guasto);

        Assert.StartsWith("esclusa (", processore.Describe(), StringComparison.Ordinal);
    }
}
