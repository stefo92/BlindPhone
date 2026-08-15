using System.Globalization;
using System.Text;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Fotografia dello stato del posto operatore, da allegare a una segnalazione.
/// </summary>
/// <remarks>
/// Contiene ciò che serve a chi riceve la segnalazione per non dover fare domande:
/// versione, configurazione in uso, stato del telefono e contatori del percorso
/// audio. <b>Non contiene credenziali</b> — né la password SIP né il Client Secret
/// dell'API — perché il riepilogo nasce per essere incollato in un messaggio di posta
/// e inoltrato a chi capita.
/// </remarks>
public sealed record DiagnosticSnapshot
{
    /// <summary>Versione del prodotto.</summary>
    public string ProductVersion { get; init; } = "sconosciuta";

    /// <summary>Sistema operativo.</summary>
    public string OperatingSystem { get; init; } = string.Empty;

    /// <summary>Motore di esecuzione .NET.</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary>Cartella dei dati dell'operatore.</summary>
    public string DataDirectory { get; init; } = string.Empty;

    /// <summary>File di registro corrente.</summary>
    public string LogFilePath { get; init; } = string.Empty;

    /// <summary>Livello del registro in questo momento.</summary>
    public string LogLevel { get; init; } = string.Empty;

    /// <summary>Giorni di conservazione dei registri.</summary>
    public int LogRetentionDays { get; init; }

    /// <summary>Se la modalità diagnostica è attiva.</summary>
    public bool DebugMode { get; init; }

    /// <summary>Interno o utenza SIP.</summary>
    public string SipAccount { get; init; } = string.Empty;

    /// <summary>Dominio o indirizzo del centralino.</summary>
    public string SipDomain { get; init; } = string.Empty;

    /// <summary>Trasporto della segnalazione SIP.</summary>
    public string SipTransport { get; init; } = string.Empty;

    /// <summary>Stato della registrazione.</summary>
    public string RegistrationState { get; init; } = string.Empty;

    /// <summary>Se il motore telefonico è quello simulato.</summary>
    public bool Simulated { get; init; }

    /// <summary>Codec offerti, in ordine di preferenza.</summary>
    public IReadOnlyList<string> Codecs { get; init; } = [];

    /// <summary>Nome dell'account SIP in uso.</summary>
    public string SipAccountName { get; init; } = string.Empty;

    /// <summary>Quanti account SIP sono configurati sulla postazione.</summary>
    /// <remarks>
    /// Va nel riepilogo perché cambia il significato di tutte le righe che seguono:
    /// su una postazione con più account, «interno 4021 non registrato» va letto
    /// insieme alla domanda «quale dei tre account era attivo».
    /// </remarks>
    public int SipAccountCount { get; init; } = 1;

    /// <summary>
    /// Come vengono trattati i certificati non verificabili del centralino.
    /// </summary>
    /// <remarks>
    /// Una postazione che accetta qualunque certificato deve dichiararlo qui: questo
    /// è il documento che chi la verifica legge, ed è l'unico punto in cui quella
    /// scelta diventa visibile senza aprire il file di configurazione.
    /// </remarks>
    public string CertificateVerification { get; init; } = string.Empty;

    /// <summary>Rapporti di errore registrati e non ancora inviati.</summary>
    /// <remarks>
    /// Un valore diverso da zero dice a chi assiste che sul disco della postazione
    /// c'è già la traccia di un guasto, e che vale la pena farsela mandare prima di
    /// chiedere di riprodurre il problema.
    /// </remarks>
    public int PendingCrashReports { get; init; }

    /// <summary>Se il collegamento all'API del centralino è attivo.</summary>
    public bool PbxEnabled { get; init; }

    /// <summary>Indirizzo del centralino.</summary>
    public string PbxHost { get; init; } = string.Empty;

    /// <summary>Stato della coda e dell'agente, in parole.</summary>
    public string QueueState { get; init; } = string.Empty;

    /// <summary>
    /// Da quale strada arriva lo stato degli interni: API, sottoscrizioni SIP o
    /// nessuna.
    /// </summary>
    /// <remarks>
    /// Va nel riepilogo perché è la prima domanda davanti a una segnalazione del tipo
    /// «la colonna Stato è vuota»: senza saperlo, chi assiste non può distinguere un
    /// centralino senza API da una sottoscrizione rifiutata.
    /// </remarks>
    public string PresenceSource { get; init; } = string.Empty;

    /// <summary>Numero di interni di cui si conosce lo stato.</summary>
    public int PresenceKnownCount { get; init; }

    /// <summary>Dispositivo del microfono.</summary>
    public string Microphone { get; init; } = string.Empty;

    /// <summary>Dispositivo della conversazione.</summary>
    public string Conversation { get; init; } = string.Empty;

    /// <summary>Dispositivo della suoneria.</summary>
    public string Ringer { get; init; } = string.Empty;

    /// <summary>Dispositivo degli annunci.</summary>
    public string Announcements { get; init; } = string.Empty;

    /// <summary>Cuscinetto del buffer audio, in millisecondi.</summary>
    public int JitterBufferMs { get; init; }

    /// <summary>Latenza richiesta al dispositivo, in millisecondi.</summary>
    public int DeviceLatencyMs { get; init; }

    /// <summary>Capienza massima del buffer, in millisecondi.</summary>
    public int MaxBufferMs { get; init; }

    /// <summary>Durata di pacchettizzazione, in millisecondi.</summary>
    public int PacketMs { get; init; }

    /// <summary>Canale di uscita della voce.</summary>
    public string SpeechChannel { get; init; } = string.Empty;

    /// <summary>Se la voce propria di BlindPhone è consentita.</summary>
    public bool InternalVoiceEnabled { get; init; }

    /// <summary>Screen reader rilevato, oppure <c>null</c>.</summary>
    public string? ScreenReader { get; init; }

    /// <summary>Profilo di scorciatoie attivo.</summary>
    public string HotkeyProfile { get; init; } = string.Empty;

    /// <summary>Numero di combinazioni assegnate.</summary>
    public int HotkeyCount { get; init; }

    /// <summary>
    /// Se rubrica e registro delle chiamate sono cifrati sul disco.
    /// </summary>
    /// <remarks>
    /// Va nel riepilogo perché è la domanda di un collaudo di sicurezza, e perché
    /// esiste un caso in cui la risposta è no: la conversione di un archivio nato in
    /// chiaro può non riuscire, e allora il programma continua a funzionare — un
    /// centralinista senza rubrica non lavora — ma l'archivio resta leggibile. Quel
    /// caso non deve somigliare a un avvio normale.
    /// </remarks>
    public bool ArchivesEncrypted { get; init; } = true;

    /// <summary>
    /// Se un archivio è ripartito vuoto perché la chiave non lo apriva più.
    /// </summary>
    /// <remarks>
    /// È la spiegazione di una segnalazione che altrimenti sembrerebbe un guasto
    /// inspiegabile: «stamattina la rubrica era vuota». Senza questa riga, chi assiste
    /// cercherebbe il problema nell'importazione invece che nel deposito dei segreti.
    /// </remarks>
    public bool ArchiveWasReset { get; init; }

    /// <summary>Contatori del motore telefonico.</summary>
    public IReadOnlyList<DiagnosticCounter> Counters { get; init; } = [];

    /// <summary>Momento della fotografia.</summary>
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Compone il riepilogo diagnostico in testo semplice.
/// </summary>
/// <remarks>
/// <para>
/// È il documento che l'operatore incolla in una segnalazione. Sta nel core e non
/// nell'interfaccia perché si può verificare: che contenga i dati che servono, e
/// soprattutto che <b>non contenga mai una credenziale</b>. Un riepilogo che si porta
/// dietro la password SIP verrebbe inoltrato per posta a chiunque debba guardarlo, e
/// quella password apre il centralino dell'ente.
/// </para>
/// <para>
/// Il formato è a righe corte, etichetta e valore, come tutto il resto del prodotto:
/// va letto con la sintesi da chi lo sta generando, prima ancora che da chi lo
/// riceve.
/// </para>
/// </remarks>
public static class DiagnosticReport
{
    /// <summary>Compone il riepilogo.</summary>
    /// <param name="snapshot">Fotografia dello stato.</param>
    public static string Build(DiagnosticSnapshot snapshot)
    {
        var italiano = CultureInfo.GetCultureInfo("it-IT");
        var text = new StringBuilder();

        text.AppendLine("BlindPhone - Riepilogo diagnostico");
        text.AppendLine(italiano, $"Generato il {snapshot.GeneratedAt:dd/MM/yyyy} alle {snapshot.GeneratedAt:HH:mm:ss}");
        text.AppendLine();
        text.AppendLine("Questo riepilogo non contiene password ne' Client Secret.");
        text.AppendLine();

        Sezione(text, "Prodotto e sistema");
        Voce(text, "Versione", snapshot.ProductVersion);
        Voce(text, "Sistema operativo", snapshot.OperatingSystem);
        Voce(text, "Motore di esecuzione", snapshot.Runtime);
        Voce(text, "Cartella dati", snapshot.DataDirectory);

        Sezione(text, "Telefono");

        // La modalita' simulata va prima di tutto il resto: e' il solo caso in cui
        // ogni altro dato risulta a posto e nessuna telefonata parte davvero.
        if (snapshot.Simulated)
        {
            Voce(text, "ATTENZIONE", "modalita' simulata, nessuna telefonata reale");
        }

        // Su una postazione con piu' account il nome viene prima dell'interno: senza,
        // "interno 4021 non registrato" non dice quale dei tre account fosse attivo.
        if (snapshot.SipAccountCount > 1)
        {
            Voce(
                text,
                "Account in uso",
                $"{snapshot.SipAccountName} (di {snapshot.SipAccountCount} configurati)");
        }

        Voce(text, "Interno SIP", snapshot.SipAccount);
        Voce(text, "Centralino", snapshot.SipDomain);
        Voce(text, "Trasporto", snapshot.SipTransport);
        Voce(text, "Registrazione", snapshot.RegistrationState);
        Voce(text, "Codec offerti", string.Join(", ", snapshot.Codecs));

        Sezione(text, "Collegamento all'API del centralino");
        Voce(text, "Attivo", snapshot.PbxEnabled ? "si" : "no");
        Voce(text, "Indirizzo", snapshot.PbxHost);
        Voce(text, "Coda e agente", snapshot.QueueState);
        Voce(text, "Stato interni tramite", snapshot.PresenceSource);
        Voce(text, "Interni con stato noto", snapshot.PresenceKnownCount.ToString(CultureInfo.CurrentCulture));

        if (!string.IsNullOrWhiteSpace(snapshot.CertificateVerification))
        {
            Voce(text, "Verifica dei certificati", snapshot.CertificateVerification);
        }

        Sezione(text, "Audio");
        Voce(text, "Microfono", snapshot.Microphone);
        Voce(text, "Conversazione", snapshot.Conversation);
        Voce(text, "Suoneria", snapshot.Ringer);
        Voce(text, "Annunci", snapshot.Announcements);
        Voce(text, "Cuscinetto", $"{snapshot.JitterBufferMs} ms");
        Voce(text, "Latenza dispositivo", $"{snapshot.DeviceLatencyMs} ms");
        Voce(text, "Buffer massimo", $"{snapshot.MaxBufferMs} ms");
        Voce(text, "Pacchetti", $"{snapshot.PacketMs} ms");

        Sezione(text, "Accessibilita'");
        Voce(text, "Canale della voce", snapshot.SpeechChannel);
        Voce(text, "Voce propria di BlindPhone", snapshot.InternalVoiceEnabled ? "attiva" : "spenta");
        Voce(text, "Screen reader rilevato", snapshot.ScreenReader ?? "nessuno");
        Voce(text, "Profilo tasti rapidi", $"{snapshot.HotkeyProfile} ({snapshot.HotkeyCount} combinazioni)");

        Sezione(text, "Archivi locali");
        Voce(text, "Rubrica e registro chiamate", snapshot.ArchivesEncrypted
            ? "cifrati (SQLCipher, AES-256)"
            : "ATTENZIONE: IN CHIARO, cifratura non riuscita");

        if (snapshot.ArchiveWasReset)
        {
            Voce(text, "ATTENZIONE", "un archivio e' ripartito vuoto: la chiave non lo apriva piu'");
        }

        Sezione(text, "Registro diagnostico");

        if (snapshot.PendingCrashReports > 0)
        {
            // Prima del resto della sezione: dice a chi assiste che la traccia del
            // guasto esiste gia' sul disco, e che conviene farsela mandare prima di
            // chiedere all'operatore di riprodurre il problema.
            Voce(
                text,
                "Errori registrati da inviare",
                snapshot.PendingCrashReports.ToString(CultureInfo.CurrentCulture));
        }

        Voce(text, "Modalita' debug", snapshot.DebugMode ? "ATTIVA" : "spenta");
        Voce(text, "Dettaglio", snapshot.LogLevel);
        Voce(text, "Conservazione", $"{snapshot.LogRetentionDays} giorni");
        Voce(text, "File corrente", snapshot.LogFilePath);

        if (snapshot.Counters.Count > 0)
        {
            Sezione(text, "Contatori del percorso audio");

            foreach (var counter in snapshot.Counters)
            {
                Voce(text, counter.Label, counter.Value);
            }
        }

        return text.ToString();
    }

    private static void Sezione(StringBuilder text, string titolo)
    {
        text.AppendLine();
        text.AppendLine(titolo);
        text.AppendLine(new string('-', titolo.Length));
    }

    /// <summary>
    /// Scrive una voce, sostituendo i valori vuoti con una parola.
    /// </summary>
    /// <remarks>
    /// Un campo vuoto letto a voce è indistinguibile da un campo che non è stato
    /// scritto: dire "non configurato" toglie l'ambiguità a chi legge la segnalazione.
    /// </remarks>
    private static void Voce(StringBuilder text, string etichetta, string? valore)
        => text.AppendLine($"{etichetta}: {(string.IsNullOrWhiteSpace(valore) ? "non configurato" : valore)}");
}
