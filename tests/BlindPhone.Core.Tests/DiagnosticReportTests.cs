using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifiche sul riepilogo diagnostico.
/// </summary>
/// <remarks>
/// Il riepilogo nasce per essere incollato in un messaggio di posta e inoltrato a chi
/// deve guardarlo. Da qui i due requisiti che questi test difendono: deve contenere
/// tutto quello che evita un giro di domande, e <b>non deve contenere credenziali</b>.
/// Una password SIP finita in un riepilogo apre il centralino dell'ente a chiunque
/// riceva quel messaggio.
/// </remarks>
public sealed class DiagnosticReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 11, 30, 0, TimeSpan.FromHours(2));

    private static DiagnosticSnapshot Fotografia() => new()
    {
        GeneratedAt = Now,
        ProductVersion = "0.1.0",
        OperatingSystem = "Microsoft Windows NT 10.0.26200.0",
        Runtime = "10.0.12",
        DataDirectory = @"C:\Users\operatore\AppData\Roaming\BlindPhone",
        LogFilePath = @"C:\Users\operatore\AppData\Roaming\BlindPhone\log\blindphone-20260805.log",
        LogLevel = "Normale: comandi, chiamate, stato",
        LogRetentionDays = 14,
        DebugMode = false,
        SipAccount = "200",
        SipDomain = "pbx.ente.it",
        SipTransport = "Udp",
        RegistrationState = "registrato",
        Simulated = false,
        Codecs = ["G729", "G722", "PCMA"],
        PbxEnabled = true,
        PbxHost = "192.168.1.10",
        QueueState = "3 in attesa, agente disponibile",
        Microphone = "Microfono (EPOS PC 7 USB)",
        Conversation = "Altoparlanti (EPOS PC 7 USB)",
        Ringer = "Altoparlanti (Realtek)",
        Announcements = "Altoparlanti (Realtek)",
        JitterBufferMs = 100,
        DeviceLatencyMs = 100,
        MaxBufferMs = 500,
        PacketMs = 20,
        SpeechChannel = "Automatic",
        InternalVoiceEnabled = false,
        ScreenReader = "NVDA",
        HotkeyProfile = "Turno mattina",
        HotkeyCount = 44,
        Counters =
        [
            new DiagnosticCounter("Pacchetti inviati", "1.608"),
            new DiagnosticCounter("Svuotamenti del buffer", "2"),
        ],
    };

    [Fact]
    public void IlRiepilogoNonContieneMaiUnaCredenziale()
    {
        // Nessun campo della fotografia porta credenziali, ed e' il punto: se un
        // domani qualcuno ne aggiungesse uno, questo test non basterebbe a
        // impedirlo, ma la prima riga del riepilogo dichiara la promessa e la
        // dichiarazione va verificata insieme al resto.
        var testo = DiagnosticReport.Build(Fotografia());

        Assert.Contains("non contiene password", testo, StringComparison.OrdinalIgnoreCase);

        var minuscolo = testo.ToLowerInvariant();

        foreach (var parola in new[] { "password", "secret", "segreto" })
        {
            // Compare solo nella frase che dichiara la loro assenza.
            var occorrenze = minuscolo.Split(parola).Length - 1;
            Assert.True(occorrenze <= 1, $"La parola {parola} compare {occorrenze} volte nel riepilogo");
        }
    }

    [Fact]
    public void IlRiepilogoRiportaCioCheEvitaUnGiroDiDomande()
    {
        var testo = DiagnosticReport.Build(Fotografia());

        foreach (var atteso in new[]
                 {
                     "0.1.0",                       // versione
                     "pbx.ente.it",                 // centralino
                     "registrato",                  // stato del telefono
                     "G729",                        // codec offerti
                     "EPOS PC 7 USB",               // dispositivo audio in uso
                     "100 ms",                      // regolazione del buffer
                     "NVDA",                        // screen reader rilevato
                     "Turno mattina",               // profilo dei tasti
                     "blindphone-20260805.log",     // dove sta il registro
                     "Pacchetti inviati",           // contatori del percorso audio
                 })
        {
            Assert.Contains(atteso, testo, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GliArchiviCifratiVengonoDichiarati()
    {
        var testo = DiagnosticReport.Build(Fotografia());

        Assert.Contains("cifrati", testo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IN CHIARO", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void UnArchivioRimastoInChiaroVieneSegnalato()
    {
        // E' il caso che non deve somigliare a un avvio normale: la conversione non e'
        // riuscita, il posto operatore funziona lo stesso, e rubrica e registro delle
        // chiamate sono leggibili da chiunque copi il file.
        var testo = DiagnosticReport.Build(Fotografia() with { ArchivesEncrypted = false });

        Assert.Contains("IN CHIARO", testo, StringComparison.Ordinal);
        Assert.Contains("ATTENZIONE", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void LaModalitaSimulataVienePrimaDiTuttoIlResto()
    {
        var testo = DiagnosticReport.Build(Fotografia() with { Simulated = true });

        var avviso = testo.IndexOf("modalita' simulata", StringComparison.OrdinalIgnoreCase);
        var interno = testo.IndexOf("Interno SIP", StringComparison.Ordinal);

        Assert.True(avviso >= 0, "L'avviso di modalita' simulata deve comparire");

        // E' il solo caso in cui ogni altro dato risulta a posto e nessuna telefonata
        // parte davvero: chi legge la segnalazione deve incontrarlo per primo.
        Assert.True(avviso < interno, "L'avviso deve precedere i dati dell'account");
    }

    [Fact]
    public void LaModalitaDebugAttivaSiVede()
    {
        Assert.Contains("ATTIVA", DiagnosticReport.Build(Fotografia() with { DebugMode = true }),
            StringComparison.Ordinal);

        Assert.Contains("spenta", DiagnosticReport.Build(Fotografia()), StringComparison.Ordinal);
    }

    [Fact]
    public void UnCampoVuotoDiceCheNonEConfigurato()
    {
        // Un campo vuoto letto a voce e' indistinguibile da un campo dimenticato.
        var testo = DiagnosticReport.Build(Fotografia() with { PbxHost = string.Empty });

        Assert.Contains("Indirizzo: non configurato", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaContatoriLaSezioneNonCompare()
    {
        var testo = DiagnosticReport.Build(Fotografia() with { Counters = [] });

        Assert.DoesNotContain("Contatori del percorso audio", testo, StringComparison.Ordinal);
    }
}
