using System.Net;
using System.Net.Sockets;
using BlindPhone.Audio;
using BlindPhone.Contacts;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using BlindPhone.Infrastructure;
using BlindPhone.Pbx.Yeastar;
using BlindPhone.Telephony;
using Microsoft.Extensions.Logging;

// =============================================================================
// BlindPhone.Diagnostics
//
// Serve a rispondere a una domanda sola, quella che ci si pone davanti a una
// chiamata che non funziona: il problema e' nel softphone o nel centralino?
//
// Lo fa misurando i pacchetti RTP nelle due direzioni. Sono quattro i casi
// possibili, e ciascuno punta a una causa diversa:
//
//   inviati > 0, ricevuti > 0   audio bidirezionale, il percorso funziona
//   inviati > 0, ricevuti = 0   noi trasmettiamo, il centralino no: guardare NAT,
//                               firewall sulle porte RTP, o la configurazione del
//                               trunk lato PBX
//   inviati = 0, ricevuti > 0   riceviamo ma non trasmettiamo: microfono non
//                               disponibile o cattura non avviata
//   inviati = 0, ricevuti = 0   nessun flusso: la chiamata cadra' per timeout di
//                               inattivita', ed e' il sintomo classico
//
// Le credenziali non si digitano qui: vengono lette dal deposito protetto con
// DPAPI creato dall'applicazione, leggibile solo da questo utente su questa
// macchina.
// =============================================================================

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss.fff ";
    })
    .SetMinimumLevel(args.Contains("--verboso") ? LogLevel.Debug : LogLevel.Information));

var logger = loggerFactory.CreateLogger("diag");

var settingsService = new SettingsService(logger: loggerFactory.CreateLogger<SettingsService>());
var settings = settingsService.Load();

Console.WriteLine();
Console.WriteLine("BlindPhone - Diagnostica");
Console.WriteLine("========================");
Console.WriteLine($"Configurazione: {settingsService.SettingsPath}");
Console.WriteLine($"Registro      : {Path.Combine(settingsService.DataDirectory, "log")}");
Console.WriteLine();

var command = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "aiuto";

switch (command.ToLowerInvariant())
{
    case "audio":
        await TestAudioDevicesAsync();
        break;

    case "pbx":
        await TestPbxAsync();
        break;

    case "sip":
        await TestSipRegistrationAsync();
        break;

    case "nat":
        await TestNatAsync();
        break;

    case "chiamata":
        await TestCallAsync(args.SkipWhile(a => a != "chiamata").Skip(1).FirstOrDefault(a => !a.StartsWith("--")));
        break;

    case "rubrica":
        await TestCsvImportAsync(args.SkipWhile(a => a != "rubrica").Skip(1).FirstOrDefault(a => !a.StartsWith("--")));
        break;

    default:
        PrintHelp();
        break;
}

return;

// -----------------------------------------------------------------------------

void PrintHelp()
{
    Console.WriteLine("Comandi disponibili:");
    Console.WriteLine();
    Console.WriteLine("  audio                  elenca i dispositivi e verifica l'assegnazione delle quattro vie");
    Console.WriteLine("  sip                    verifica la registrazione presso il centralino");
    Console.WriteLine("  nat                    dice se la postazione e' dietro NAT e qual e' l'indirizzo pubblico");
    Console.WriteLine("  pbx                    verifica il collegamento all'Open API del centralino");
    Console.WriteLine("  chiamata <numero>      chiama un numero e misura il flusso RTP nelle due direzioni");
    Console.WriteLine("  rubrica <file.csv>     analizza un file CSV e mostra come verrebbe importato");
    Console.WriteLine();
    Console.WriteLine("Opzioni:");
    Console.WriteLine("  --verboso              mostra anche i messaggi di dettaglio");
    Console.WriteLine();
    Console.WriteLine("Esempio: blindphone-diag chiamata 200");
}

// -----------------------------------------------------------------------------
// Dispositivi audio
// -----------------------------------------------------------------------------

async Task TestAudioDevicesAsync()
{
    await using var engine = new NAudioEngine(loggerFactory.CreateLogger<NAudioEngine>());

    ApplyDeviceAssignments(engine, settings);

    Console.WriteLine("Dispositivi di acquisizione:");
    foreach (var d in engine.GetCaptureDevices())
    {
        Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.Name}");
    }

    Console.WriteLine();
    Console.WriteLine("Dispositivi di riproduzione:");
    foreach (var d in engine.GetPlaybackDevices())
    {
        Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.Name}");
    }

    Console.WriteLine();
    Console.WriteLine("Assegnazione delle quattro vie:");

    foreach (var route in Enum.GetValues<AudioRoute>())
    {
        var device = engine.GetAssignedDevice(route);
        Console.WriteLine($"  {route,-15} {device?.Name ?? "NON DISPONIBILE"}");
    }

    Console.WriteLine();

    var conversation = engine.GetAssignedDevice(AudioRoute.Conversation)?.Name;
    var announcements = engine.GetAssignedDevice(AudioRoute.Announcements)?.Name;

    if (conversation is not null && conversation == announcements)
    {
        Console.WriteLine("AVVISO: conversazione e annunci usano lo stesso dispositivo.");
        Console.WriteLine("        La voce del sistema si sovrapporra' a quella dell'interlocutore.");
        Console.WriteLine("        Assegna dispositivi diversi in Impostazioni, scheda Audio.");
    }

    if (engine.GetAssignedDevice(AudioRoute.Microphone) is null)
    {
        Console.WriteLine("ERRORE: nessun microfono disponibile. Le chiamate saranno mute in uscita.");
    }
}

// -----------------------------------------------------------------------------
// Registrazione SIP
// -----------------------------------------------------------------------------

async Task TestSipRegistrationAsync()
{
    var account = BuildAccount();
    if (account is null) return;

    Console.WriteLine($"Interno   : {account.Username}");
    Console.WriteLine($"Centralino: {account.Domain}");
    Console.WriteLine($"Trasporto : {account.Transport}");
    Console.WriteLine($"Codec     : {string.Join(", ", account.CodecPreference)}");
    Console.WriteLine();
    Console.WriteLine("Registrazione in corso, attendere fino a 15 secondi...");
    Console.WriteLine();

    await using var engine = new SipTelephonyEngine(
        account,
        audioEngine: null,
        clock: null,
        logger: loggerFactory.CreateLogger<SipTelephonyEngine>());

    var completed = new TaskCompletionSource<SipRegistrationState>();

    engine.RegistrationStateChanged += (_, state) =>
    {
        Console.WriteLine($"  stato: {state.Status}  {state.FailureReason}");
        if (state.Status is RegistrationStatus.Registered or RegistrationStatus.Failed)
        {
            completed.TrySetResult(state);
        }
    };

    await engine.StartAsync(CancellationToken.None);

    var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(15)));

    Console.WriteLine();

    if (finished != completed.Task)
    {
        Console.WriteLine("ESITO: nessuna risposta dal centralino entro 15 secondi.");
        Console.WriteLine("       Verifica indirizzo, porta e che il firewall non blocchi il traffico SIP.");
    }
    else
    {
        var state = await completed.Task;
        Console.WriteLine(state.IsReachable
            ? "ESITO: registrazione riuscita. Il posto operatore e' raggiungibile."
            : $"ESITO: registrazione non riuscita. {state.FailureReason}");

        if (state.IsReachable)
        {
            Console.WriteLine();
            PrintNat(engine);
        }
    }

    await engine.StopAsync(CancellationToken.None);
}

// -----------------------------------------------------------------------------
// NAT e indirizzo pubblico
// -----------------------------------------------------------------------------

// Risponde alla domanda che precede ogni chiamata muta dal lavoro agile: la postazione
// e' dietro un router, e il centralino lo sa? Le fonti sono due e si dicono separate,
// perche' non sono la stessa prova:
//
//   - la risposta alla registrazione: il centralino dice da quale indirizzo vede
//     arrivare la postazione (RFC 3581). Non serve nessun server esterno;
//   - il server STUN dell'account, se c'e': dice l'indirizzo pubblico visto da fuori.
//
// Il server STUN si interroga da un socket a parte, quindi la porta pubblica che torna
// non e' quella che avra' l'audio di una chiamata: l'indirizzo e la presenza del NAT
// invece si'. La porta vera si legge nella scheda Diagnostica durante una chiamata, o
// con 'blindphone-diag chiamata'.
async Task TestNatAsync()
{
    var account = BuildAccount();
    if (account is null) return;

    Console.WriteLine($"Interno    : {account.Username}");
    Console.WriteLine($"Centralino : {account.Domain}");
    Console.WriteLine($"Trasporto  : {account.Transport}");
    Console.WriteLine($"Server STUN: {(string.IsNullOrWhiteSpace(account.StunServer) ? "non configurato" : account.StunServer)}");
    Console.WriteLine();
    Console.WriteLine("Registrazione in corso, attendere fino a 15 secondi...");

    await using var engine = new SipTelephonyEngine(
        account,
        audioEngine: null,
        clock: null,
        logger: loggerFactory.CreateLogger<SipTelephonyEngine>());

    var registered = new TaskCompletionSource<bool>();

    engine.RegistrationStateChanged += (_, state) =>
    {
        if (state.Status == RegistrationStatus.Registered) registered.TrySetResult(true);
        else if (state.Status == RegistrationStatus.Failed) registered.TrySetResult(false);
    };

    await engine.StartAsync(CancellationToken.None);

    var ok = await Task.WhenAny(registered.Task, Task.Delay(TimeSpan.FromSeconds(15))) == registered.Task
             && await registered.Task;

    Console.WriteLine();

    if (!ok)
    {
        Console.WriteLine("Registrazione non riuscita: senza la risposta del centralino il NAT verso di lui");
        Console.WriteLine("non si puo' misurare. Esegui 'blindphone-diag sip' per il dettaglio.");
    }
    else
    {
        PrintNat(engine);
    }

    if (StunServerAddress.TryParse(account.StunServer, out var stun, out var stunError) && stun is not null)
    {
        Console.WriteLine();
        Console.WriteLine($"Interrogazione del server STUN {stun}...");

        var (esito, server) = await QueryStunAsync(stun);
        Console.WriteLine($"  {esito.Describe()}");

        var peer = engine.RegistrarEndPoint?.Address;
        var decisione = MediaAddressPolicy.Decide(
            esito,
            peer,
            peer is null ? null : SIPSorcery.Sys.NetServices.GetLocalAddressForRemote(peer),
            server is null ? null : SIPSorcery.Sys.NetServices.GetLocalAddressForRemote(server.Address));

        Console.WriteLine();
        Console.WriteLine("Su una chiamata verso questo centralino la postazione farebbe cosi':");
        Console.WriteLine($"  {decisione.Reason}");
    }
    else if (!string.IsNullOrWhiteSpace(stunError))
    {
        Console.WriteLine();
        Console.WriteLine($"Server STUN non utilizzabile: {stunError}");
    }

    await engine.StopAsync(CancellationToken.None);
}

/// <summary>Le righe della diagnostica che parlano di NAT e mantenimento.</summary>
static void PrintNat(SipTelephonyEngine engine)
{
    string[] righe =
    [
        "NAT fra postazione e centralino",
        "Indirizzo pubblico scoperto",
        "Server STUN",
        "Segnale di mantenimento",
        "Indirizzo annunciato per l'audio",
        "Verso dell'audio",
    ];

    foreach (var counter in engine.DiagnosticCounters.Where(c => righe.Contains(c.Label)))
    {
        Console.WriteLine($"{counter.Label}: {counter.Value}");
    }
}

/// <summary>
/// Una richiesta STUN da un socket nuovo, con tre tentativi in tre secondi.
/// </summary>
/// <remarks>
/// Piu' paziente di quella che il motore fa a ogni chiamata: qui nessuno sta aspettando
/// che una chiamata parta, e un «non risponde» deve voler dire che il server non c'e'.
/// </remarks>
static async Task<(StunBindingResult Result, IPEndPoint? Server)> QueryStunAsync(StunServerAddress stun)
{
    IPEndPoint server;

    try
    {
        var indirizzi = IPAddress.TryParse(stun.Host, out var diretto)
            ? [diretto]
            : await Dns.GetHostAddressesAsync(stun.Host);

        var scelto = indirizzi.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? indirizzi.FirstOrDefault();
        if (scelto is null) return (StunBindingResult.NoResponse($"il nome {stun.Host} non si risolve"), null);

        server = new IPEndPoint(scelto, stun.Port);
    }
    catch (SocketException)
    {
        return (StunBindingResult.NoResponse($"il nome {stun.Host} non si risolve"), null);
    }

    using var udp = new UdpClient(server.AddressFamily);
    using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(3));

    var transazione = StunBinding.NewTransactionId();
    var richiesta = StunBinding.CreateRequest(transazione);

    var invii = Task.Run(async () =>
    {
        foreach (var attesa in new[] { 0, 500, 1000 })
        {
            await Task.Delay(attesa, limite.Token);
            await udp.SendAsync(richiesta, server, limite.Token);
        }
    });

    try
    {
        while (true)
        {
            var arrivato = await udp.ReceiveAsync(limite.Token);
            var esito = StunBinding.Parse(arrivato.Buffer, transazione);

            if (esito.Outcome != StunOutcome.OtherTransaction) return (esito, server);
        }
    }
    catch (OperationCanceledException)
    {
        return (StunBindingResult.NoResponse("nessuna risposta in tre secondi"), server);
    }
    catch (SocketException ex)
    {
        return (StunBindingResult.NoResponse(ex.Message), server);
    }
    finally
    {
        // Arrivata la risposta, i tentativi che restano non servono piu'.
        limite.Cancel();

        try { await invii; }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
    }
}

// -----------------------------------------------------------------------------
// Open API del centralino
// -----------------------------------------------------------------------------

async Task TestPbxAsync()
{
    var options = new YeastarOptions
    {
        Host = settings.Pbx.Host,
        Port = settings.Pbx.Port,
        UseTls = settings.Pbx.UseTls,
        ClientId = settings.Pbx.ClientId,
        // Come l'applicazione: il segreto si legge per l'host a cui sta per essere
        // inviato. Uno strumento di diagnosi che aggira il legame mostrerebbe un
        // collegamento che funziona dove BlindPhone si rifiuta, che e' il modo
        // migliore di far cercare il guasto nel posto sbagliato.
        ClientSecret = settingsService.Secrets.GetForHost(
            SecretStore.PbxClientSecretKey,
            settings.Pbx.Host,
            "il Client Secret dell'API del centralino"),

        Extension = settings.Sip.Username,
        QueueIds = settings.Pbx.QueueIds,
    };

    if (!options.Validate(out var error))
    {
        Console.WriteLine($"ERRORE di configurazione: {error}");
        return;
    }

    Console.WriteLine($"Centralino: {options.BaseUrl}");
    Console.WriteLine($"Code      : {string.Join(", ", options.QueueIds)}");
    Console.WriteLine();

    using var client = new YeastarApiClient(options, logger: loggerFactory.CreateLogger<YeastarApiClient>());
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

    try
    {
        var source = new YeastarContactSource(client, loggerFactory.CreateLogger<YeastarContactSource>());
        var result = await source.TestAsync(timeout.Token);

        Console.WriteLine(result.Success
            ? $"ESITO: collegamento riuscito. {result.Message}"
            : $"ESITO: collegamento non riuscito. {result.Message}");

        if (!result.Success) return;

        Console.WriteLine();
        Console.WriteLine("Stato delle code:");

        foreach (var queueId in options.QueueIds)
        {
            var status = await client.GetAsync<YeastarQueueCallStatusResponse>(
                "queue/call_status",
                new Dictionary<string, string> { ["id"] = queueId.ToString() },
                timeout.Token);

            Console.WriteLine(
                $"  coda {queueId}: in attesa {status.WaitingCalls}, "
                + $"in squillo {status.RingingCalls}, attive {status.ActiveCalls}");

            foreach (var call in status.WaitingList)
            {
                Console.WriteLine(
                    $"      {call.CallerNumber} {call.CallerDisplayName} da {call.Duration}s");
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("ESITO: il centralino non ha risposto entro venti secondi.");
    }
    catch (YeastarApiException ex)
    {
        Console.WriteLine($"ESITO: {ex.Message} (codice {ex.ErrorCode})");
    }
}

// -----------------------------------------------------------------------------
// Chiamata di prova con misura del flusso RTP
// -----------------------------------------------------------------------------

async Task TestCallAsync(string? destination)
{
    if (string.IsNullOrWhiteSpace(destination))
    {
        Console.WriteLine("Indica il numero da chiamare. Esempio: blindphone-diag chiamata 200");
        return;
    }

    var account = BuildAccount();
    if (account is null) return;

    await using var audio = new NAudioEngine(loggerFactory.CreateLogger<NAudioEngine>());
    ApplyDeviceAssignments(audio, settings);

    await using var engine = new SipTelephonyEngine(
        account,
        audio,
        clock: null,
        logger: loggerFactory.CreateLogger<SipTelephonyEngine>(),
        audioEndPointLogger: loggerFactory.CreateLogger<SipAudioEndPoint>());

    var registered = new TaskCompletionSource<bool>();

    engine.RegistrationStateChanged += (_, state) =>
    {
        if (state.Status == RegistrationStatus.Registered) registered.TrySetResult(true);
        else if (state.Status == RegistrationStatus.Failed) registered.TrySetResult(false);
    };

    // La chiusura della chiamata ferma la misura. Senza questo, se l'interlocutore
    // riaggancia prima dei quaranta secondi il conteggio prosegue su una chiamata
    // che non esiste piu': la durata della registrazione viene confrontata con
    // quaranta secondi invece che con quelli davvero parlati, e una registrazione
    // completa sembra monca.
    var ended = new TaskCompletionSource<CallEndReason?>();

    // Da quando la chiamata e' in conversazione: l'avviso di audio a senso unico conta
    // da li', con la stessa regola dell'applicazione (OneWayAudio).
    DateTimeOffset? answeredAt = null;

    engine.CallProgress += (_, e) =>
    {
        Console.WriteLine($"  [{e.At:HH:mm:ss}] {e.Trigger}  {e.EndReason}  codec {e.NegotiatedCodec}");

        if (e.Trigger == CallTrigger.RemoteAnswered) answeredAt ??= DateTimeOffset.Now;
        if (e.EndReason is not null) ended.TrySetResult(e.EndReason);
    };

    await engine.StartAsync(CancellationToken.None);

    Console.WriteLine("Attesa della registrazione...");
    var ok = await Task.WhenAny(registered.Task, Task.Delay(TimeSpan.FromSeconds(15))) == registered.Task
             && await registered.Task;

    if (!ok)
    {
        Console.WriteLine("Registrazione non riuscita: la chiamata non puo' partire.");
        Console.WriteLine("Esegui prima 'blindphone-diag sip' per il dettaglio.");
        return;
    }

    Console.WriteLine($"Registrato. Chiamata a {destination}...");
    Console.WriteLine();

    var callId = await engine.DialAsync(destination, CancellationToken.None);

    // Si osserva per quaranta secondi: e' il doppio del tempo dopo cui si
    // manifestava la caduta, quindi copre il caso di timeout per inattivita' RTP.
    Console.WriteLine("Misura del flusso RTP, 40 secondi. Rispondi alla chiamata e parla.");
    Console.WriteLine();
    Console.WriteLine("  tempo   inviati  ricevuti  buffer  vuoti  stato");

    var endPoint = engine.GetAudioEndPoint(callId);

    // Si registra l'audio decodificato, PRIMA di qualunque ricampionamento. E' cio'
    // che separa "arriva gia' rovinato" da "lo rovina la riproduzione locale", due
    // ipotesi che a orecchio si confondono.
    //
    // La registrazione si predispone qui, ma il file nasce al primo pacchetto: in
    // questo momento il codec non e' ancora negoziato, e scrivere subito
    // l'intestazione la fisserebbe alla frequenza sbagliata. Vedi StartRecording.
    //
    // Il file va sul Desktop e NON nella cartella temporanea: un percorso come
    // AppData\Local\Temp e' irraggiungibile per chi naviga con lo screen reader
    // senza conoscerlo a memoria, e un nome con marca temporale non si puo'
    // indovinare. Il nome e' fisso, cosi' si ritrova sempre allo stesso posto.
    var recordingPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "BlindPhone-audio-ricevuto.wav");

    Console.WriteLine($"Registrazione in: {recordingPath}");
    Console.WriteLine();

    endPoint?.StartRecording(recordingPath);

    var observedSeconds = 0;
    var oneWayWarned = false;

    for (var second = 1; second <= 40; second++)
    {
        if (await Task.WhenAny(ended.Task, Task.Delay(1000)) == ended.Task)
        {
            Console.WriteLine($"  Chiamata chiusa dopo {second - 1} secondi: misura interrotta.");
            break;
        }

        observedSeconds = second;

        var sent = endPoint?.RtpFramesSent ?? 0;
        var received = endPoint?.RtpFramesReceived ?? 0;
        var buffered = endPoint?.BufferedMilliseconds ?? 0;
        var underruns = endPoint?.RenderUnderruns ?? 0;

        if (!oneWayWarned
            && answeredAt is { } risposta
            && OneWayAudio.IsOneWay(new MediaFlowSnapshot(0, 0), new MediaFlowSnapshot(sent, received), DateTimeOffset.Now - risposta))
        {
            // E' il momento in cui l'applicazione avviserebbe l'operatore a voce.
            oneWayWarned = true;
            Console.WriteLine($"  >>> AUDIO A SENSO UNICO: qui BlindPhone direbbe «{AnnouncementTexts.OneWayAudio}»");
        }

        if (second % 5 == 0 || second <= 3)
        {
            Console.WriteLine(
                $"  {second,4}s  {sent,8}  {received,8}  {buffered,4}ms  {underruns,5}  {Verdict(sent, received)}");
        }
    }

    endPoint?.StopRecording();

    var finalSent = endPoint?.RtpFramesSent ?? 0;
    var finalReceived = endPoint?.RtpFramesReceived ?? 0;

    Console.WriteLine();
    Console.WriteLine("=== ESITO ===");
    Console.WriteLine($"Codec offerti     : {string.Join(", ", account.CodecPreference)}");
    Console.WriteLine($"Codec negoziato   : {endPoint?.NegotiatedFormatName ?? "sconosciuto"}");
    Console.WriteLine($"Pacchetti inviati : {finalSent}");
    Console.WriteLine($"Pacchetti ricevuti: {finalReceived}");
    Console.WriteLine($"Pacchetti scartati: {endPoint?.RtpFramesDiscarded ?? 0}");
    Console.WriteLine($"Indirizzo audio   : {engine.GetMediaAddress(callId)?.Reason ?? "non scelto"}");
    Console.WriteLine();
    PrintNat(engine);
    Console.WriteLine();
    Console.WriteLine(Diagnosis(finalSent, finalReceived));

    if ((endPoint?.RtpFramesDiscarded ?? 0) > 0)
    {
        Console.WriteLine();
        Console.WriteLine("NOTA: alcuni pacchetti in arrivo non erano decodificabili con il codec");
        Console.WriteLine("      negoziato e sono stati resi come silenzio. Con G.729 e' normale:");
        Console.WriteLine("      sono i frame di comfort noise dell'Annex B inviati durante le pause.");
    }

    // ---- regolazione del buffer ----

    var jitter = endPoint?.JitterBufferMilliseconds ?? 0;
    var totalUnderruns = endPoint?.RenderUnderruns ?? 0;
    var silence = endPoint?.RenderSilenceMilliseconds ?? 0;
    var padded = endPoint?.ResamplerPaddedBytes ?? 0;

    Console.WriteLine();
    Console.WriteLine("=== BUFFER DI RIPRODUZIONE ===");
    Console.WriteLine($"Cuscinetto configurato : {jitter} ms");
    Console.WriteLine($"Svuotamenti            : {totalUnderruns}");
    Console.WriteLine($"Silenzio inserito      : {silence} ms");
    Console.WriteLine($"Letture corte coperte  : {padded} byte");
    Console.WriteLine();

    Console.WriteLine(totalUnderruns switch
    {
        0 =>
            "Il buffer non si e' mai svuotato: la voce non dovrebbe presentare interruzioni.\n"
            + $"Se avverti un ritardo eccessivo puoi provare a scendere sotto i {jitter} ms\n"
            + "da Impostazioni, scheda Audio.",

        < 5 =>
            $"Pochi svuotamenti su 40 secondi: il cuscinetto di {jitter} ms e' quasi sufficiente.\n"
            + $"Prova ad alzarlo a {jitter + 50} ms.",

        < 30 =>
            $"Svuotamenti frequenti: il cuscinetto di {jitter} ms e' troppo basso per questa rete.\n"
            + $"Prova {Math.Min(300, jitter * 2)} ms, oppure usa la preimpostazione \"rete disturbata\".",

        _ =>
            "Svuotamenti continui. Non e' un problema di dimensione del buffer:\n"
            + "i pacchetti non stanno arrivando con regolarita'. Verifica la rete fra\n"
            + "postazione e centralino, e controlla che il conteggio dei ricevuti cresca.",
    });

    if (padded > 0)
    {
        Console.WriteLine();
        Console.WriteLine("NOTA: il ricampionatore ha restituito letture parziali, completate con");
        Console.WriteLine("      silenzio. Senza questa protezione quei byte sarebbero stati");
        Console.WriteLine("      riprodotti come rumore bianco.");
    }

    var duplicates = endPoint?.DuplicateDeliveries ?? 0;

    if (duplicates > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"TROVATO: {duplicates} pacchetti consegnati due volte dallo stack SIP.");
        Console.WriteLine("      Ogni pacchetto veniva scritto due volte nella coda di riproduzione:");
        Console.WriteLine("      il buffer si riempiva al doppio della velocita' di consumo, scartava,");
        Console.WriteLine("      si svuotava e ricominciava. E' la voce che va e viene a ritmo regolare.");
        Console.WriteLine("      Ora la seconda consegna viene ignorata.");
    }

    // ---- la prova che separa le due ipotesi ----

    Console.WriteLine();
    Console.WriteLine("=== REGISTRAZIONE DELL'AUDIO RICEVUTO ===");

    if (File.Exists(recordingPath))
    {
        var info = new FileInfo(recordingPath);
        var recordedRate = endPoint?.NegotiatedClockRate ?? 8000;

        Console.WriteLine($"File   : {recordingPath}");
        Console.WriteLine($"Dimens.: {info.Length:N0} byte");
        Console.WriteLine($"Formato: {recordedRate} Hz, 16 bit, mono");
        var recorded = SecondsOf(info.Length, recordedRate);

        Console.WriteLine($"Durata : circa {recorded:F1} secondi di audio ricevuto");

        // Il confronto e' con i secondi realmente osservati, non con i quaranta
        // nominali: se l'interlocutore riaggancia prima, una registrazione completa
        // sembrerebbe monca.
        Console.WriteLine(observedSeconds > 0 && recorded < observedSeconds * 0.8
            ? $"         ATTENZIONE: su {observedSeconds} secondi di chiamata osservati. "
              + "Manca audio: il flusso arriva a tratti."
            : $"         (su {observedSeconds} secondi di chiamata osservati)");
        Console.WriteLine();
        Console.WriteLine("Lo trovi sul Desktop, si chiama BlindPhone-audio-ricevuto.wav");
        Console.WriteLine();
        Console.WriteLine("Contiene esattamente i campioni usciti dal decodificatore, prima di");
        Console.WriteLine("qualunque ricampionamento e di qualunque buffer. Riascoltalo:");
        Console.WriteLine();
        Console.WriteLine("  voce PULITA    -> l'audio arriva integro, il difetto e' a valle:");
        Console.WriteLine("                    buffer, ricampionatore o dispositivo di uscita");
        Console.WriteLine("  voce DISTURBATA-> il problema e' a monte, in rete o nella decodifica,");
        Console.WriteLine("                    e nessuna regolazione del buffer lo risolvera'");
    }
    else
    {
        Console.WriteLine("Nessuna registrazione prodotta: non e' arrivato audio da registrare.");
    }

    await engine.HangUpAsync(callId, CancellationToken.None);
    await engine.StopAsync(CancellationToken.None);
}

// -----------------------------------------------------------------------------
// Analisi di un file di rubrica
// -----------------------------------------------------------------------------

async Task TestCsvImportAsync(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        Console.WriteLine("Indica un file esistente. Esempio: blindphone-diag rubrica C:\\rubrica.csv");
        return;
    }

    var headers = CsvContactSource.ReadHeaders(path);

    if (headers.Count == 0)
    {
        Console.WriteLine("Il file non ha una riga di intestazione riconoscibile.");
        return;
    }

    Console.WriteLine($"Colonne trovate ({headers.Count}):");
    foreach (var header in headers) Console.WriteLine($"  {header}");

    var mapping = CsvColumnMapping.Guess(headers);
    var source = new CsvContactSource(path, mapping, logger: loggerFactory.CreateLogger<CsvContactSource>());

    Console.WriteLine();
    Console.WriteLine($"Delimitatore: {source.DelimiterDescription}");
    Console.WriteLine($"Codifica    : {source.EncodingName}");
    Console.WriteLine();

    Console.WriteLine("Abbinamento riconosciuto automaticamente:");
    Console.WriteLine($"  Numero          -> {mapping.Extension ?? "(nessuna)"}");
    Console.WriteLine($"  Nominativo      -> {mapping.DisplayName ?? "(nessuna)"}");
    Console.WriteLine($"  Tipo            -> {mapping.Category ?? "(nessuna)"}");
    Console.WriteLine($"  Relativo        -> {mapping.RelatedTo ?? "(nessuna)"}");
    Console.WriteLine($"  Struttura       -> {mapping.Organization ?? "(nessuna)"}");
    Console.WriteLine($"  Padiglione      -> {mapping.Building ?? "(nessuna)"}");
    Console.WriteLine($"  Piano           -> {mapping.Floor ?? "(nessuna)"}");
    Console.WriteLine();

    var contacts = await source.LoadAsync(CancellationToken.None);

    Console.WriteLine($"Voci importabili: {contacts.Count}");
    Console.WriteLine();
    Console.WriteLine("Come verrebbero lette dallo screen reader:");

    foreach (var contact in contacts.Take(10))
    {
        Console.WriteLine($"  {contact.FullSpeechDescription} - numero {contact.PrimaryNumber}");
    }
}

/// <summary>
/// Stima la durata di un WAV a 16 bit mono dalla sua dimensione.
/// </summary>
/// <remarks>
/// Serve a capire subito se la registrazione contiene davvero la conversazione o
/// solo pochi frammenti: una prova di 40 secondi che produce due secondi di audio
/// dice gia' che i pacchetti non arrivano, senza bisogno di riascoltarla.
///
/// La frequenza va passata e non data per scontata: G.722 campiona a 16 kHz e
/// produce il doppio dei byte degli altri tre codec a parita' di secondi. Dandola
/// per 8000 fissi, una registrazione di venti secondi ne dichiarerebbe quaranta e
/// il controllo direbbe "tutto a posto" proprio nel caso in cui meta' dell'audio
/// non e' arrivato.
/// </remarks>
static double SecondsOf(long bytes, int sampleRate)
    => Math.Max(0, bytes - 44) / (Math.Max(1, sampleRate) * 2.0);

static string Verdict(long sent, long received) => (sent, received) switch
{
    (0, 0) => "nessun flusso",
    (> 0, 0) => "solo in uscita",
    (0, > 0) => "solo in ingresso",
    _ => "bidirezionale",
};

static string Diagnosis(long sent, long received) => (sent, received) switch
{
    (0, 0) =>
        "NESSUN FLUSSO RTP in nessuna direzione.\n"
        + "La chiamata cadra' per timeout di inattivita'. Verifica che il microfono sia\n"
        + "disponibile ('blindphone-diag audio') e che le porte RTP non siano bloccate.",

    (> 0, 0) =>
        "TRASMETTIAMO ma NON RICEVIAMO.\n"
        + "Il softphone sta funzionando: il problema e' a valle. Guarda in quest'ordine:\n"
        + "  1. NAT e firewall sulle porte RTP verso questa postazione: da casa e' il caso\n"
        + "     piu' probabile. 'blindphone-diag nat' dice se il NAT c'e'; se c'e', serve un\n"
        + "     server STUN nell'account oppure la gestione del NAT sul centralino\n"
        + "  2. configurazione del trunk o dell'interno sul centralino\n"
        + "  3. eventuale politica di media relay del PBX",

    (0, > 0) =>
        "RICEVIAMO ma NON TRASMETTIAMO.\n"
        + "Il problema e' la cattura audio locale: microfono non disponibile, occupato da\n"
        + "un altro programma, o permesso negato nelle impostazioni privacy di Windows.\n"
        + "Esegui 'blindphone-diag audio'.",

    _ =>
        "AUDIO BIDIREZIONALE. Il percorso RTP funziona in entrambe le direzioni.\n"
        + "Se l'audio non si sente nonostante questo, il problema e' nell'instradamento\n"
        + "verso il dispositivo sbagliato: controlla la scheda Audio delle impostazioni.",
};

// -----------------------------------------------------------------------------
// Ausiliari
// -----------------------------------------------------------------------------

SipAccountOptions? BuildAccount()
{
    var sip = settings.Sip;

    var password = settingsService.Secrets.GetForHost(
        SecretStore.SipPasswordKey,
        sip.EffectiveRegistrar,
        $"la password dell'account SIP {sip.Name}");

    if (string.IsNullOrWhiteSpace(sip.Username) || string.IsNullOrWhiteSpace(sip.Domain))
    {
        Console.WriteLine("ERRORE: account SIP non configurato.");
        Console.WriteLine("        Aprire BlindPhone, premere Control virgola e compilare la scheda Account SIP.");
        return null;
    }

    if (string.IsNullOrWhiteSpace(password))
    {
        Console.WriteLine("AVVISO: nessuna password SIP nel deposito protetto.");
        Console.WriteLine("        Se il centralino la richiede, la registrazione fallira'.");
    }

    return new SipAccountOptions
    {
        Name = sip.Name,
        DisplayName = sip.DisplayName,
        Username = sip.Username,
        AuthId = sip.AuthId,
        Password = password,
        Domain = sip.Domain,
        Registrar = sip.Registrar,
        OutboundProxy = sip.OutboundProxy,
        Transport = Enum.TryParse<SipTransportKind>(sip.Transport, out var t) ? t : SipTransportKind.Udp,
        LocalPort = sip.LocalPort,
        RegistrationExpiry = sip.RegistrationExpiry,
        CodecPreference = sip.CodecPreference,
        StunServer = sip.StunServer,
        KeepAliveSeconds = sip.KeepAliveSeconds,
    };
}

static void ApplyDeviceAssignments(NAudioEngine engine, AppSettings settings)
{
    engine.AssignDevice(AudioRoute.Microphone, settings.Microphone.DeviceId);
    engine.AssignDevice(AudioRoute.Conversation, settings.Conversation.DeviceId);
    engine.AssignDevice(AudioRoute.Ringer, settings.Ringer.DeviceId);
    engine.AssignDevice(AudioRoute.Announcements, settings.Announcements.DeviceId);
}
