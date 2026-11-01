using System.Text.RegularExpressions;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>Da dove è partito un comando dell'operatore.</summary>
/// <remarks>
/// È la sola cosa che le statistiche d'uso dicono del modo di lavorare: se un comando
/// si dà da tastiera o dal pulsante. Serve a sapere quali pulsanti nessuno usa e quali
/// tasti nessuno ha imparato, che sono le due domande su cui si decide come cambiare
/// la finestra principale.
/// </remarks>
public enum CommandOrigin
{
    /// <summary>Scorciatoia locale, con la finestra in primo piano.</summary>
    Keyboard,

    /// <summary>Scorciatoia globale, da qualunque programma.</summary>
    GlobalHotkey,

    /// <summary>Pulsante della finestra principale.</summary>
    Button,

    /// <summary>Uno dei quattro tasti della modalità semplificata.</summary>
    Keypad,

    /// <summary>Pulsante della cuffia.</summary>
    Headset,
}

/// <summary>Da dove è arrivato un errore non gestito.</summary>
public enum ErrorOrigin
{
    /// <summary>Dal thread dell'interfaccia: assorbito, il programma continua.</summary>
    Interface,

    /// <summary>Dal processo: il programma sta terminando, o può terminare.</summary>
    Process,

    /// <summary>Da un'attività di sfondo i cui errori nessuno ha atteso.</summary>
    BackgroundTask,
}

/// <summary>Che forma può avere il valore di una proprietà.</summary>
/// <remarks>
/// <b>Non c'è una forma «testo».</b> È la regola che tiene fuori i dati personali: un
/// campo di testo libero è il posto in cui un giorno qualcuno mette il nome del
/// centralino «solo per capire meglio», e da quel momento esce. Qui ogni valore è un sì
/// o un no, una voce di un elenco scritto in questo file, o una forma fissa che un
/// numero di telefono, un nome o un indirizzo SIP non possono avere.
/// </remarks>
public enum TelemetryValueKind
{
    /// <summary>Sì o no.</summary>
    Flag,

    /// <summary>Una voce di un elenco chiuso.</summary>
    Choice,

    /// <summary>Un numero di versione: da due a quattro gruppi di cifre separati da punti.</summary>
    Version,

    /// <summary>Il nome di un tipo di eccezione, senza il testo e senza la traccia.</summary>
    TypeName,
}

/// <summary>Una proprietà che un evento può portare, con i valori che accetta.</summary>
/// <param name="Name">Nome della proprietà, come arriva al server.</param>
/// <param name="Kind">Forma del valore.</param>
/// <param name="Choices">Voci ammesse, per una proprietà a elenco chiuso.</param>
public sealed partial record TelemetryProperty(string Name, TelemetryValueKind Kind, IReadOnlyList<string> Choices)
{
    /// <summary>Indica se il valore può uscire con questa proprietà.</summary>
    /// <param name="value">Valore da verificare.</param>
    /// <remarks>
    /// <para>
    /// <b>Nessuna stringa con sei cifre di fila passa, qualunque sia la forma.</b> È una
    /// seconda rete sotto le altre: un numero di telefono ne ha almeno sei, una versione
    /// di BlindPhone o di Windows non più di cinque per gruppo, e un nome di tipo che ne
    /// avesse sei sarebbe comunque una stranezza da non mandare.
    /// </para>
    /// <para>
    /// Le versioni hanno un tetto di cinque cifre per gruppo per la stessa ragione: la
    /// compilazione di Windows più alta oggi è a cinque cifre, e una forma che ne
    /// accettasse di più accetterebbe anche un interno scritto con i punti.
    /// </para>
    /// </remarks>
    public bool Accepts(object? value)
    {
        if (value is bool) return Kind == TelemetryValueKind.Flag;
        if (value is not string testo) return false;
        if (TelemetryCatalog.HasPhoneLikeDigits(testo)) return false;

        return Kind switch
        {
            TelemetryValueKind.Choice => Choices.Contains(testo, StringComparer.Ordinal),
            TelemetryValueKind.Version => VersionForm().IsMatch(testo),
            TelemetryValueKind.TypeName => testo.Length <= 120 && TypeNameForm().IsMatch(testo),
            _ => false,
        };
    }

    /// <summary>Da due a quattro gruppi di cifre, ognuno di al massimo cinque.</summary>
    [GeneratedRegex(@"^\d{1,5}(\.\d{1,5}){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionForm();

    /// <summary>
    /// Identificatori separati da punti, l'ultimo dei quali finisce con
    /// <c>Exception</c>, con il solo suffisso dei tipi generici.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Niente spazi, niente due punti, niente chiocciola: un indirizzo SIP, un percorso o
    /// una frase non ci stanno.
    /// </para>
    /// <para>
    /// <b>Il suffisso <c>Exception</c> è obbligatorio</b>, ed è quello che tiene fuori i
    /// nomi di dominio: «pbx.asl.example.it» è fatto di identificatori separati da punti
    /// come un nome di tipo, e senza questa regola il nome del centralino avrebbe avuto
    /// una forma in cui entrare. Un'eccezione di terzi che non segue la convenzione di
    /// .NET semplicemente non viene detta.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*Exception(`[1-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeNameForm();
}

/// <summary>Un evento del catalogo, con le proprietà che può portare.</summary>
/// <param name="Name">Nome dell'evento, come arriva al server.</param>
/// <param name="Description">
/// Che cosa dice e quando parte, in una frase. È quella che la guida legge
/// all'operatore: vedi <see cref="HelpBook"/>.
/// </param>
/// <param name="Properties">Proprietà ammesse, una per una.</param>
public sealed record TelemetryEventDefinition(
    string Name,
    string Description,
    IReadOnlyList<TelemetryProperty> Properties)
{
    /// <summary>Proprietà con il nome dato, se l'evento la ammette.</summary>
    /// <param name="name">Nome della proprietà.</param>
    public TelemetryProperty? Find(string name)
        => Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>Un evento pronto da inviare.</summary>
/// <param name="Name">Nome dell'evento.</param>
/// <param name="Day">
/// Giorno a cui si riferisce. È tutto quello che si dice del momento: vedi
/// <see cref="TelemetryCatalog"/>.
/// </param>
/// <param name="Properties">Valori, solo sì o no e testi del catalogo.</param>
public sealed record TelemetryEvent(string Name, DateOnly Day, IReadOnlyDictionary<string, object> Properties);

/// <summary>
/// Quello che si sa della postazione nel momento in cui si compone un avvio.
/// </summary>
/// <remarks>
/// <para>
/// Ogni campo è già nella forma del catalogo: chi lo riempie traduce le impostazioni
/// nelle voci degli elenchi, e il catalogo scarta le voci che non conosce. Una
/// traduzione sbagliata produce una proprietà che manca, non un valore che esce.
/// </para>
/// <para>
/// <b>Non c'è niente che identifichi la postazione o la persona</b>: si dice che la
/// rubrica viene da una directory, non da quale; che c'è un'API del centralino, non
/// quale centralino; che esistono più account, non quanti né con quali interni.
/// </para>
/// </remarks>
public sealed record TelemetryFacts
{
    /// <summary>Versione di BlindPhone, nella forma numerica.</summary>
    public string Version { get; init; } = "0.0.0";

    /// <summary>Architettura del processo: x64, x86, arm64, altro.</summary>
    public string Architecture { get; init; } = "altro";

    /// <summary>Famiglia di Windows: 10, 11, server, altro.</summary>
    public string Windows { get; init; } = "altro";

    /// <summary>Versione completa di Windows, per esempio 10.0.26100.</summary>
    public string WindowsBuild { get; init; } = string.Empty;

    /// <summary>Screen reader rilevato: nvda, jaws, nvda_e_jaws, altro, nessuno.</summary>
    public string ScreenReader { get; init; } = "nessuno";

    /// <summary>Se Windows ha il contrasto elevato attivo.</summary>
    public bool HighContrast { get; init; }

    /// <summary>Tema scelto: sistema, chiaro, scuro.</summary>
    public string Theme { get; init; } = "sistema";

    /// <summary>Modalità di interazione: standard, semplificata.</summary>
    public string Interaction { get; init; } = "standard";

    /// <summary>Canale della voce: automatico, solo_uia, solo_screen_reader, solo_voce_interna, entrambi.</summary>
    public string SpeechChannel { get; init; } = "automatico";

    /// <summary>Se BlindPhone può parlare con la propria voce.</summary>
    public bool InternalVoice { get; init; }

    /// <summary>Se la riga braille viene aggiornata.</summary>
    public bool Braille { get; init; }

    /// <summary>Se il collegamento all'API del centralino è configurato e acceso.</summary>
    public bool PbxApi { get; init; }

    /// <summary>Da dove viene la rubrica: file, sql_server, ldap, outlook.</summary>
    public string Contacts { get; init; } = "file";

    /// <summary>Se i pulsanti della cuffia governano le chiamate.</summary>
    public bool Headset { get; init; }

    /// <summary>Se almeno un account usa un server STUN.</summary>
    public bool Stun { get; init; }

    /// <summary>Se la sorveglianza dei colleghi è accesa.</summary>
    public bool Colleagues { get; init; }

    /// <summary>Se l'account attivo ha il numero della segreteria.</summary>
    public bool Voicemail { get; init; }

    /// <summary>Se sulla postazione c'è più di un account SIP.</summary>
    public bool MultipleAccounts { get; init; }

    /// <summary>Trasporto dell'account attivo: udp, tcp, tls.</summary>
    public string SipTransport { get; init; } = "udp";

    /// <summary>Cifratura della voce dell'account attivo: disabilitata, preferita, obbligatoria.</summary>
    public string VoiceEncryption { get; init; } = "disabilitata";

    /// <summary>Se il controllo del livello o la riduzione del rumore sono accesi.</summary>
    public bool MicrophoneProcessing { get; init; }

    /// <summary>Se i tasti rapidi sono scritti sui pulsanti.</summary>
    public bool HotkeysOnButtons { get; init; }

    /// <summary>Se c'è un server degli aggiornamenti.</summary>
    public bool AutomaticUpdates { get; init; }

    /// <summary>Se la postazione ha ricevuto un pacchetto di configurazione.</summary>
    public bool DistributedConfiguration { get; init; }

    /// <summary>Da dove viene il criterio di blocco: nessuno, utente, postazione.</summary>
    public string Policy { get; init; } = "nessuno";

    /// <summary>Se la modalità debug è accesa.</summary>
    public bool DebugMode { get; init; }
}

/// <summary>
/// Che cosa può uscire dalla postazione con le statistiche d'uso, proprietà per
/// proprietà.
/// </summary>
/// <remarks>
/// <para>
/// <b>Questo file è l'elenco chiuso.</b> Un evento che non è qui non parte, e una
/// proprietà che non è elencata sotto il suo evento viene tolta prima dell'invio, da
/// <see cref="Create"/> quando si compone e da <see cref="Check"/> quando si spedisce.
/// Allargare le statistiche vuol dire cambiare questo file, e cambiarlo vuol dire
/// passare dalle prove che lo sorvegliano — non c'è un secondo posto da cui una
/// proprietà possa entrare.
/// </para>
/// <para>
/// <b>Niente numeri, nomi, indirizzi, interni, rubrica, centralino, testi di errore.</b>
/// Non sono esclusi caso per caso: non c'è una forma di valore che li possa contenere.
/// Vedi <see cref="TelemetryValueKind"/>.
/// </para>
/// <para>
/// <b>Niente conteggi e niente orari, e questa è una scelta di legge.</b> Quante
/// chiamate ha preso una postazione e a che ora si è accesa misurano il lavoro di chi
/// ci siede davanti, e uno strumento che lo fa è un controllo a distanza dell'attività
/// dei lavoratori (art. 4 della legge 300 del 1970): all'ente servirebbe un accordo
/// sindacale per accenderlo. Per questo ogni evento porta il giorno e non l'ora, e
/// l'uso dei comandi dice «usato quel giorno» e non «quante volte», e arriva il giorno
/// dopo. Le statistiche rispondono a «che cosa si usa del prodotto», non a «quanto
/// lavora chi lo usa».
/// </para>
/// </remarks>
public static partial class TelemetryCatalog
{
    // -----------------------------------------------------------------------
    // Proprieta'
    // -----------------------------------------------------------------------

    /// <summary>Versione di BlindPhone. La portano tutti gli eventi.</summary>
    public static readonly TelemetryProperty Versione = Version("versione");

    /// <summary>Architettura del processo.</summary>
    public static readonly TelemetryProperty Architettura = Choice("architettura", "x64", "x86", "arm64", "altro");

    /// <summary>Famiglia di Windows.</summary>
    public static readonly TelemetryProperty Windows = Choice("windows", "10", "11", "server", "altro");

    /// <summary>Versione completa di Windows.</summary>
    public static readonly TelemetryProperty WindowsBuild = Version("windows_build");

    /// <summary>Screen reader rilevato.</summary>
    public static readonly TelemetryProperty ScreenReader =
        Choice("screen_reader", "nvda", "jaws", "nvda_e_jaws", "altro", "nessuno");

    /// <summary>Contrasto elevato di Windows.</summary>
    public static readonly TelemetryProperty ContrastoElevato = Flag("contrasto_elevato");

    /// <summary>Tema dei colori.</summary>
    public static readonly TelemetryProperty Tema = Choice("tema", "sistema", "chiaro", "scuro");

    /// <summary>Modalità di interazione.</summary>
    public static readonly TelemetryProperty Modalita = Choice("modalita", "standard", "semplificata");

    /// <summary>Canale della voce degli annunci.</summary>
    public static readonly TelemetryProperty CanaleVoce = Choice(
        "canale_voce", "automatico", "solo_uia", "solo_screen_reader", "solo_voce_interna", "entrambi", "altro");

    /// <summary>Voce propria di BlindPhone.</summary>
    public static readonly TelemetryProperty VoceInterna = Flag("voce_interna");

    /// <summary>Riga braille.</summary>
    public static readonly TelemetryProperty Braille = Flag("braille");

    /// <summary>API del centralino.</summary>
    public static readonly TelemetryProperty ApiCentralino = Flag("api_centralino");

    /// <summary>Provenienza della rubrica.</summary>
    public static readonly TelemetryProperty Rubrica = Choice("rubrica", "file", "sql_server", "ldap", "outlook");

    /// <summary>Pulsanti della cuffia.</summary>
    public static readonly TelemetryProperty Cuffia = Flag("cuffia");

    /// <summary>Server STUN per il lavoro agile.</summary>
    public static readonly TelemetryProperty Stun = Flag("stun");

    /// <summary>Sorveglianza dei colleghi.</summary>
    public static readonly TelemetryProperty Colleghi = Flag("colleghi");

    /// <summary>Numero della segreteria.</summary>
    public static readonly TelemetryProperty Segreteria = Flag("segreteria");

    /// <summary>Più di un account SIP.</summary>
    public static readonly TelemetryProperty AccountMultipli = Flag("account_multipli");

    /// <summary>Trasporto della segnalazione.</summary>
    public static readonly TelemetryProperty TrasportoSip = Choice("trasporto_sip", "udp", "tcp", "tls", "altro");

    /// <summary>Cifratura della voce.</summary>
    public static readonly TelemetryProperty CifraturaVoce =
        Choice("cifratura_voce", "disabilitata", "preferita", "obbligatoria", "altro");

    /// <summary>Elaborazione del microfono.</summary>
    public static readonly TelemetryProperty ElaborazioneMicrofono = Flag("elaborazione_microfono");

    /// <summary>Tasti rapidi scritti sui pulsanti.</summary>
    public static readonly TelemetryProperty TastiSuiPulsanti = Flag("tasti_sui_pulsanti");

    /// <summary>Server degli aggiornamenti configurato.</summary>
    public static readonly TelemetryProperty AggiornamentiAutomatici = Flag("aggiornamenti_automatici");

    /// <summary>Pacchetto di configurazione applicato.</summary>
    public static readonly TelemetryProperty ConfigurazioneDistribuita = Flag("configurazione_distribuita");

    /// <summary>Origine del criterio di blocco.</summary>
    public static readonly TelemetryProperty Criterio = Choice("criterio", "nessuno", "utente", "postazione");

    /// <summary>Modalità debug.</summary>
    public static readonly TelemetryProperty ModalitaDebug = Flag("modalita_debug");

    /// <summary>Comando usato, con il suo nome interno.</summary>
    /// <remarks>
    /// L'elenco è quello di <see cref="HotkeyCommand"/>: un comando nuovo entra da sé, e
    /// un nome che non è un comando non passa.
    /// </remarks>
    public static readonly TelemetryProperty Comando = Choice("comando", Enum.GetNames<HotkeyCommand>());

    /// <summary>Da dove è partito il comando.</summary>
    public static readonly TelemetryProperty OrigineComando =
        Choice("origine", "tasto", "tasto_globale", "pulsante", "tastierino", "cuffia");

    /// <summary>Versione trovata sul server degli aggiornamenti.</summary>
    public static readonly TelemetryProperty VersioneNuova = Version("versione_nuova");

    /// <summary>Versione da cui si è aggiornato.</summary>
    public static readonly TelemetryProperty DaVersione = Version("da_versione");

    /// <summary>Tipo dell'eccezione.</summary>
    public static readonly TelemetryProperty Tipo = new("tipo", TelemetryValueKind.TypeName, []);

    /// <summary>Da dove è arrivato l'errore.</summary>
    public static readonly TelemetryProperty OrigineErrore =
        Choice("origine", "interfaccia", "processo", "attivita_di_sfondo");

    /// <summary>Se l'errore ha terminato il processo.</summary>
    public static readonly TelemetryProperty Fatale = Flag("fatale");

    // -----------------------------------------------------------------------
    // Eventi
    // -----------------------------------------------------------------------

    /// <summary>Prima volta che le statistiche partono da questa installazione.</summary>
    public static readonly TelemetryEventDefinition Installazione = new(
        "installazione",
        "La prima volta che le statistiche partono da questa postazione: la versione, l'architettura e la famiglia di Windows.",
        [Versione, Architettura, Windows]);

    /// <summary>Avvio del programma, o accensione delle statistiche.</summary>
    public static readonly TelemetryEventDefinition Avvio = new(
        "avvio",
        "A ogni avvio, e quando accendi le statistiche: la versione di BlindPhone e di Windows, lo screen reader che sta girando, il tema, la modalità di interazione, e quali funzioni sono accese, come la cuffia, la rubrica dalla directory o l'API del centralino. Di ciascuna si dice se c'è, non come è configurata.",
        [
            Versione, Architettura, Windows, WindowsBuild, ScreenReader, ContrastoElevato, Tema, Modalita,
            CanaleVoce, VoceInterna, Braille, ApiCentralino, Rubrica, Cuffia, Stun, Colleghi, Segreteria,
            AccountMultipli, TrasportoSip, CifraturaVoce, ElaborazioneMicrofono, TastiSuiPulsanti,
            AggiornamentiAutomatici, ConfigurazioneDistribuita, Criterio, ModalitaDebug,
        ]);

    /// <summary>Un giorno in cui il programma è stato in esecuzione.</summary>
    public static readonly TelemetryEventDefinition AttivitaGiornaliera = new(
        "attivita_giornaliera",
        "Una volta al giorno, se BlindPhone è acceso: la versione, lo screen reader e la modalità di interazione.",
        [Versione, ScreenReader, Modalita]);

    /// <summary>Un comando usato in un giorno, da una certa origine.</summary>
    public static readonly TelemetryEventDefinition ComandoUsato = new(
        "comando_usato",
        "Il giorno dopo, una riga per ogni comando che hai usato: quale comando, e se l'hai dato con un tasto, con un pulsante, dal tastierino o dalla cuffia. Non quante volte, e non a che ora.",
        [Versione, Comando, OrigineComando]);

    /// <summary>Una versione nuova trovata sul server degli aggiornamenti.</summary>
    public static readonly TelemetryEventDefinition AggiornamentoTrovato = new(
        "aggiornamento_trovato",
        "Quando BlindPhone trova una versione nuova sul server degli aggiornamenti: quale versione.",
        [Versione, VersioneNuova]);

    /// <summary>Il primo avvio dopo un aggiornamento.</summary>
    public static readonly TelemetryEventDefinition AggiornamentoInstallato = new(
        "aggiornamento_installato",
        "Al primo avvio dopo un aggiornamento: da quale versione si è passati.",
        [Versione, DaVersione]);

    /// <summary>Un errore non gestito.</summary>
    public static readonly TelemetryEventDefinition Errore = new(
        "errore",
        "Quando succede un errore non previsto, al massimo una volta al giorno per tipo: il nome tecnico del tipo di errore, e se ha chiuso il programma. Mai il testo dell'errore, che può contenere un numero.",
        [Versione, Tipo, OrigineErrore, Fatale]);

    /// <summary>Tutti gli eventi, nell'ordine in cui la guida li legge.</summary>
    public static IReadOnlyList<TelemetryEventDefinition> All { get; } =
    [
        Installazione, Avvio, AttivitaGiornaliera, ComandoUsato, AggiornamentoTrovato, AggiornamentoInstallato, Errore,
    ];

    /// <summary>Evento del catalogo con il nome dato.</summary>
    /// <param name="name">Nome dell'evento.</param>
    public static TelemetryEventDefinition? Find(string name)
        => All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));

    // -----------------------------------------------------------------------
    // Composizione
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compone un evento, tenendo solo i valori che il catalogo ammette.
    /// </summary>
    /// <param name="definition">Evento da comporre.</param>
    /// <param name="day">Giorno a cui si riferisce.</param>
    /// <param name="values">Proprietà e valori.</param>
    /// <remarks>
    /// Un valore rifiutato viene tolto, non fa fallire l'evento: un avvio senza il tema
    /// perché la traduzione del tema è sbagliata vale ancora, mentre un tema che uscisse
    /// con un valore inventato no. Le prove su questo file sono quelle che si accorgono
    /// della proprietà mancante.
    /// </remarks>
    public static TelemetryEvent Create(
        TelemetryEventDefinition definition,
        DateOnly day,
        params (TelemetryProperty Property, object? Value)[] values)
    {
        var proprieta = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var (property, value) in values)
        {
            if (!definition.Properties.Contains(property)) continue;
            if (value is null || !property.Accepts(value)) continue;

            proprieta[property.Name] = value;
        }

        return new TelemetryEvent(definition.Name, day, proprieta);
    }

    /// <summary>Prima volta che le statistiche partono da questa installazione.</summary>
    /// <param name="day">Giorno.</param>
    /// <param name="facts">Stato della postazione.</param>
    public static TelemetryEvent Installation(DateOnly day, TelemetryFacts facts) => Create(
        Installazione, day,
        (Versione, facts.Version), (Architettura, facts.Architecture), (Windows, facts.Windows));

    /// <summary>Avvio del programma, o accensione delle statistiche.</summary>
    /// <param name="day">Giorno.</param>
    /// <param name="facts">Stato della postazione.</param>
    public static TelemetryEvent Startup(DateOnly day, TelemetryFacts facts) => Create(
        Avvio, day,
        (Versione, facts.Version),
        (Architettura, facts.Architecture),
        (Windows, facts.Windows),
        (WindowsBuild, facts.WindowsBuild),
        (ScreenReader, facts.ScreenReader),
        (ContrastoElevato, facts.HighContrast),
        (Tema, facts.Theme),
        (Modalita, facts.Interaction),
        (CanaleVoce, facts.SpeechChannel),
        (VoceInterna, facts.InternalVoice),
        (Braille, facts.Braille),
        (ApiCentralino, facts.PbxApi),
        (Rubrica, facts.Contacts),
        (Cuffia, facts.Headset),
        (Stun, facts.Stun),
        (Colleghi, facts.Colleagues),
        (Segreteria, facts.Voicemail),
        (AccountMultipli, facts.MultipleAccounts),
        (TrasportoSip, facts.SipTransport),
        (CifraturaVoce, facts.VoiceEncryption),
        (ElaborazioneMicrofono, facts.MicrophoneProcessing),
        (TastiSuiPulsanti, facts.HotkeysOnButtons),
        (AggiornamentiAutomatici, facts.AutomaticUpdates),
        (ConfigurazioneDistribuita, facts.DistributedConfiguration),
        (Criterio, facts.Policy),
        (ModalitaDebug, facts.DebugMode));

    /// <summary>Un giorno in cui il programma è stato in esecuzione.</summary>
    /// <param name="day">Giorno.</param>
    /// <param name="facts">Stato della postazione.</param>
    public static TelemetryEvent DailyActivity(DateOnly day, TelemetryFacts facts) => Create(
        AttivitaGiornaliera, day,
        (Versione, facts.Version), (ScreenReader, facts.ScreenReader), (Modalita, facts.Interaction));

    /// <summary>Un comando usato in un giorno, da una certa origine.</summary>
    /// <param name="day">Giorno in cui il comando è stato usato.</param>
    /// <param name="version">Versione di BlindPhone.</param>
    /// <param name="command">Comando.</param>
    /// <param name="origin">Da dove è partito.</param>
    public static TelemetryEvent CommandUsed(DateOnly day, string version, HotkeyCommand command, CommandOrigin origin)
        => Create(
            ComandoUsato, day,
            (Versione, version), (Comando, command.ToString()), (OrigineComando, OriginName(origin)));

    /// <summary>Una versione nuova trovata sul server degli aggiornamenti.</summary>
    /// <param name="day">Giorno.</param>
    /// <param name="version">Versione installata.</param>
    /// <param name="newVersion">Versione trovata.</param>
    public static TelemetryEvent UpdateFound(DateOnly day, string version, string newVersion) => Create(
        AggiornamentoTrovato, day, (Versione, version), (VersioneNuova, newVersion));

    /// <summary>Il primo avvio dopo un aggiornamento.</summary>
    /// <param name="day">Giorno.</param>
    /// <param name="version">Versione installata adesso.</param>
    /// <param name="fromVersion">Versione di prima.</param>
    public static TelemetryEvent UpdateInstalled(DateOnly day, string version, string fromVersion) => Create(
        AggiornamentoInstallato, day, (Versione, version), (DaVersione, fromVersion));

    /// <summary>Un errore non gestito.</summary>
    /// <param name="day">Giorno.</param>
    /// <param name="version">Versione di BlindPhone.</param>
    /// <param name="typeName">Nome del tipo, da <see cref="TypeNameOf"/>.</param>
    /// <param name="origin">Da dove è arrivato.</param>
    /// <param name="fatal">Se ha terminato il processo.</param>
    public static TelemetryEvent Error(DateOnly day, string version, string typeName, ErrorOrigin origin, bool fatal)
        => Create(
            Errore, day,
            (Versione, version), (Tipo, typeName), (OrigineErrore, OriginName(origin)), (Fatale, fatal));

    /// <summary>
    /// Il nome del tipo di un'eccezione, l'unica cosa che di un errore può uscire.
    /// </summary>
    /// <param name="exception">Eccezione.</param>
    /// <remarks>
    /// <para>
    /// Spazio dei nomi e nome, e nient'altro. Non il testo: «Impossibile chiamare
    /// 0612345678» è un messaggio di errore plausibile. Non la traccia, che porta i
    /// percorsi della macchina. E non il nome completo di .NET, che per un tipo
    /// generico si porta dietro gli assembly con le loro versioni.
    /// </para>
    /// <para>
    /// Un errore di un'attività di sfondo arriva avvolto in un'eccezione aggregata:
    /// quella dice solo «qualcosa è andato storto in sfondo». Se dentro ce n'è una sola
    /// si dice quella, che è la domanda vera.
    /// </para>
    /// </remarks>
    public static string TypeNameOf(Exception exception)
    {
        if (exception is AggregateException { InnerExceptions.Count: 1 } aggregata)
        {
            exception = aggregata.InnerExceptions[0];
        }

        var tipo = exception.GetType();

        return string.IsNullOrEmpty(tipo.Namespace) ? tipo.Name : $"{tipo.Namespace}.{tipo.Name}";
    }

    // -----------------------------------------------------------------------
    // Verifica
    // -----------------------------------------------------------------------

    /// <summary>
    /// Le ragioni per cui un evento non può uscire; vuoto se può.
    /// </summary>
    /// <param name="telemetryEvent">Evento da verificare.</param>
    /// <remarks>
    /// È l'ultimo controllo prima della rete, e ripete quello di <see cref="Create"/>
    /// di proposito: gli eventi in attesa passano dal file della postazione, e un file
    /// lo può scrivere chiunque usi quell'utenza di Windows.
    /// </remarks>
    public static IReadOnlyList<string> Check(TelemetryEvent telemetryEvent)
    {
        var definizione = Find(telemetryEvent.Name);
        if (definizione is null) return [$"evento {telemetryEvent.Name} fuori dal catalogo"];

        var problemi = new List<string>();

        foreach (var (nome, valore) in telemetryEvent.Properties)
        {
            var proprieta = definizione.Find(nome);

            if (proprieta is null) problemi.Add($"proprieta' {nome} non ammessa in {definizione.Name}");
            else if (!proprieta.Accepts(valore)) problemi.Add($"valore non ammesso per {nome} in {definizione.Name}");
        }

        return problemi;
    }

    /// <summary>
    /// Indica se un testo contiene sei o più cifre di fila, come un numero di telefono.
    /// </summary>
    /// <param name="text">Testo da verificare.</param>
    public static bool HasPhoneLikeDigits(string text) => PhoneLikeDigits().IsMatch(text);

    /// <summary>Nome con cui un'origine di comando arriva al server.</summary>
    public static string OriginName(CommandOrigin origin) => origin switch
    {
        CommandOrigin.Keyboard => "tasto",
        CommandOrigin.GlobalHotkey => "tasto_globale",
        CommandOrigin.Button => "pulsante",
        CommandOrigin.Keypad => "tastierino",
        CommandOrigin.Headset => "cuffia",
        _ => string.Empty,
    };

    /// <summary>Nome con cui un'origine di errore arriva al server.</summary>
    public static string OriginName(ErrorOrigin origin) => origin switch
    {
        ErrorOrigin.Interface => "interfaccia",
        ErrorOrigin.Process => "processo",
        ErrorOrigin.BackgroundTask => "attivita_di_sfondo",
        _ => string.Empty,
    };

    [GeneratedRegex(@"\d{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneLikeDigits();

    private static TelemetryProperty Flag(string name) => new(name, TelemetryValueKind.Flag, []);

    private static TelemetryProperty Version(string name) => new(name, TelemetryValueKind.Version, []);

    private static TelemetryProperty Choice(string name, params string[] choices)
        => new(name, TelemetryValueKind.Choice, choices);
}
