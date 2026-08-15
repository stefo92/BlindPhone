using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;

namespace BlindPhone.Infrastructure;

/// <summary>Impostazioni di un dispositivo audio assegnato a una via.</summary>
/// <param name="DeviceId">Identificativo stabile del dispositivo.</param>
/// <param name="DeviceName">
/// Nome descrittivo memorizzato insieme all'identificativo.
/// </param>
/// <param name="Volume">Volume, da 0 a 1.</param>
/// <remarks>
/// Il nome viene conservato accanto all'identificativo perché serve a due cose che
/// l'identificativo da solo non consente: dire all'operatore quale dispositivo era
/// configurato quando non lo si trova più, e riconoscerlo se cambia identificativo
/// dopo un aggiornamento di driver.
/// </remarks>
public sealed record AudioRouteSettings(string? DeviceId, string? DeviceName, float Volume = 1f);

/// <summary>Impostazioni dell'account SIP, senza la password.</summary>
/// <remarks>
/// La password non compare in questo oggetto e non finisce mai nel file JSON:
/// viene conservata separatamente tramite DPAPI, con ambito utente. Vale lo stesso
/// per il Client Secret dell'API Yeastar.
/// </remarks>
public sealed record SipSettings
{
    /// <summary>
    /// Identificativo stabile dell'account, invariante ai cambi di nome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serve a due cose che il nome non può fare. La prima è legare la password nel
    /// deposito protetto: la chiave del segreto è costruita su questo, quindi
    /// rinominare «Principale» in «Centralino sede» non fa perdere la password. La
    /// seconda è indicare l'account predefinito senza che due account omonimi
    /// diventino ambigui.
    /// </para>
    /// <para>
    /// Vuoto significa «non ancora assegnato»: capita sulle configurazioni scritte
    /// prima che gli account fossero più di uno, e viene riempito dalla migrazione.
    /// </para>
    /// </remarks>
    public string Id { get; init; } = string.Empty;

    /// <summary>Nome dell'account.</summary>
    public string Name { get; init; } = "Principale";

    /// <summary>Nome visualizzato inviato alla controparte.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Numero da presentare a chi si chiama, al posto dell'interno del centralinista.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serve a non aprire una linea diretta verso la postazione. Chi riceve una
    /// chiamata dal centralino vede l'interno del centralinista e da quel momento
    /// richiama quello, scavalcando il numero pubblico dell'ente: nel giro di qualche
    /// mese la postazione si ritrova un recapito che nessuno ha deciso di pubblicare,
    /// non filtrato e scoperto quando il centralinista non c'è.
    /// </para>
    /// <para>
    /// Vuoto significa nessuna alterazione. Va indicato un numero di cui l'ente
    /// disponga davvero — tipicamente il centralino pubblico — perché è lì che le
    /// richiamate andranno a finire.
    /// </para>
    /// </remarks>
    public string OutboundCallerId { get; init; } = string.Empty;

    /// <summary>
    /// Cifratura della voce: Disabled, Preferred, Required.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Riguarda il <b>contenuto</b> della conversazione, ed è una cosa diversa dal
    /// trasporto TLS: quello protegge la segnalazione — chi chiama chi, quando, verso
    /// quale interno — e si sceglie in <see cref="Transport"/>. Si può avere l'uno
    /// senza l'altra.
    /// </para>
    /// <para>
    /// Il predefinito è <c>Disabled</c> perché fino a oggi la cifratura della voce non
    /// veniva applicata: accenderla da sola a un aggiornamento cambierebbe il modo in
    /// cui le chiamate si instaurano su postazioni che funzionano, e verso un
    /// centralino che non la espone significherebbe chiamate che smettono di partire
    /// senza che nessuno abbia toccato nulla. Si accende qui, e si prova.
    /// </para>
    /// </remarks>
    public string Srtp { get; init; } = "Disabled";

    /// <summary>Interno o utenza SIP.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Identificativo di autenticazione, se diverso dall'utenza.</summary>
    public string? AuthId { get; init; }

    /// <summary>Dominio o realm SIP.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Server di registrazione.</summary>
    public string? Registrar { get; init; }

    /// <summary>Proxy in uscita.</summary>
    public string? OutboundProxy { get; init; }

    /// <summary>Trasporto: Udp, Tcp o Tls.</summary>
    public string Transport { get; init; } = "Udp";

    /// <summary>Porta locale. Zero per la selezione automatica.</summary>
    public int LocalPort { get; init; }

    /// <summary>Durata della registrazione in secondi.</summary>
    public int RegistrationExpiry { get; init; } = 300;

    /// <summary>
    /// Ordine di preferenza dei codec, dal più desiderato al meno.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sono i tre di uso corrente in Europa: G.729 compresso, G.722 a banda larga,
    /// G.711 A-law come riserva universale.
    /// </para>
    /// <para>
    /// G.729 è primo perché è quello che i centralini di riferimento negoziano
    /// nella pratica. G.722 resta nell'elenco anche se raramente scelto: EN 301 549
    /// §6.1 richiede la <i>capacità</i> di banda larga, non che venga negoziata per
    /// prima, quindi la conformità dipende dalla sua presenza e non dalla sua
    /// posizione. Toglierlo la farebbe perdere.
    /// </para>
    /// <para>
    /// PCMU (G.711 mu-law) è deliberatamente assente: è la variante nordamericana,
    /// in Europa non serve. Resta selezionabile dalle impostazioni.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> CodecPreference { get; init; } = ["G729", "G722", "PCMA"];

    /// <summary>Chiave con cui la password di questo account vive nel deposito protetto.</summary>
    /// <remarks>
    /// Costruita sull'identificativo e non sul nome, per la ragione detta in
    /// <see cref="Id"/>. Il primo account conserva la chiave storica
    /// <c>sip.password</c>: cambiarla avrebbe fatto perdere la password a ogni
    /// postazione già configurata, che significa una postazione fuori servizio al
    /// primo aggiornamento.
    /// </remarks>
    public string PasswordKey => string.IsNullOrWhiteSpace(Id)
        ? SecretStore.SipPasswordKey
        : $"{SecretStore.SipPasswordKey}.{Id}";

    /// <summary>Se l'account ha i dati minimi per tentare una registrazione.</summary>
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Domain);

    /// <summary>Descrizione breve dell'account, pronunciabile.</summary>
    /// <remarks>
    /// Nome e interno insieme: due account sullo stesso centralino si distinguono per
    /// l'interno, due account con lo stesso interno su centralini diversi si
    /// distinguono per il dominio. Leggere il solo nome li renderebbe indistinguibili
    /// proprio nell'elenco che serve a sceglierli.
    /// </remarks>
    public string SpeechLabel => string.IsNullOrWhiteSpace(Username)
        ? Name
        : $"{Name}, interno {Username}"
          + (string.IsNullOrWhiteSpace(Domain) ? string.Empty : $" su {Domain}");
}

/// <summary>
/// Come recapitare all'assistenza il pacchetto diagnostico.
/// </summary>
/// <remarks>
/// <para>
/// Nessun recapito è fornito con il prodotto, e non è una dimenticanza: un softphone
/// che spedisce da solo i registri di traffico a un indirizzo scelto dal fornitore
/// è un trasferimento di dati personali che nessuno ha autorizzato. L'ente configura
/// il proprio, oppure il pacchetto resta un file sul disco che l'operatore consegna
/// come preferisce.
/// </para>
/// </remarks>
public sealed record SupportSettings
{
    /// <summary>Indirizzo di posta dell'assistenza.</summary>
    /// <remarks>
    /// Quando c'è, il pacchetto si allega a un messaggio già compilato che parte solo
    /// quando l'operatore preme invia.
    /// </remarks>
    public string? Email { get; init; }

    /// <summary>
    /// Indirizzo HTTPS su cui caricare il pacchetto, se l'ente ne espone uno.
    /// </summary>
    /// <remarks>
    /// Solo HTTPS: su HTTP il pacchetto viaggerebbe in chiaro con dentro i numeri
    /// chiamati, e l'indirizzo viene rifiutato prima di provarci.
    /// </remarks>
    public string? UploadUrl { get; init; }

    /// <summary>
    /// Se raccogliere automaticamente un rapporto quando si verifica un errore grave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attivo per impostazione predefinita. Il rapporto contiene il tipo di errore e
    /// la traccia dello stack, <b>non</b> numeri di telefono né nomi di contatti:
    /// resta sulla postazione e non parte da nessuna parte finché l'operatore non lo
    /// invia.
    /// </para>
    /// <para>
    /// Il valore aggiunto sta tutto nell'automatismo. Un guasto che si presenta una
    /// volta ogni tre giorni è irriproducibile su richiesta, e senza raccolta
    /// automatica l'unica descrizione disponibile resta «a un certo punto non
    /// funzionava più».
    /// </para>
    /// </remarks>
    public bool CollectCrashReports { get; init; } = true;
}

/// <summary>
/// Da dove BlindPhone scarica i propri aggiornamenti.
/// </summary>
/// <remarks>
/// <para>
/// <b>QUI SI SCRIVE L'INDIRIZZO DEL SERVER.</b> Il valore predefinito è
/// <see cref="DefaultServerUrl"/>, poche righe più sotto: cambiando quella costante
/// tutte le postazioni installate da quel momento puntano al server giusto senza
/// toccare nient'altro. Su una postazione già installata l'indirizzo si può
/// correggere in <c>settings.json</c> sotto <c>Updates.ServerUrl</c>, che ha la
/// precedenza sulla costante.
/// </para>
/// <para>
/// <b>Sul server servono due file per versione</b>, con lo stesso nome ed estensione
/// diversa: <c>BlindPhone-1.2.0-x64.msi</c> e <c>BlindPhone-1.2.0-x64.txt</c>. Il
/// primo è il pacchetto prodotto dall'installatore, il secondo è il file di testo con
/// le novità — una per riga — che BlindPhone mostra prima di proporre
/// l'aggiornamento. L'abbinamento è il nome e nient'altro: nessun indice da tenere
/// aggiornato.
/// </para>
/// </remarks>
public sealed record UpdateSettings
{
    /// <summary>
    /// Indirizzo predefinito della cartella che contiene i pacchetti.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>È questa la riga da cambiare prima di compilare.</b> Deve essere l'indirizzo
    /// di una <b>cartella</b> e non di un file, e finire con la barra:
    /// <c>ftps://aggiornamenti.example.it/blindphone/</c>.
    /// </para>
    /// <para>
    /// Sono ammessi tre schemi. <c>ftps://</c> è FTP con la connessione cifrata
    /// (AUTH TLS) ed è quello da usare: le credenziali e il pacchetto viaggiano
    /// protetti. <c>ftp://</c> è FTP in chiaro, che questa applicazione accetta ma
    /// segnala, perché chi sta sul percorso di rete vede la password e può sostituire
    /// il pacchetto con un altro. <c>https://</c> vale se un giorno i file venissero
    /// pubblicati su un sito invece che su un server FTP.
    /// </para>
    /// <para>
    /// Vuoto significa «nessun aggiornamento automatico»: è il valore con cui parte
    /// una copia compilata senza aver scelto un server, e in quel caso il controllo
    /// non viene nemmeno tentato.
    /// </para>
    /// </remarks>
    public const string DefaultServerUrl = "";

    /// <summary>Cartella remota che contiene i pacchetti e le note.</summary>
    public string ServerUrl { get; init; } = DefaultServerUrl;

    /// <summary>Utente con cui accedere al server. Vuoto per l'accesso anonimo.</summary>
    public string? Username { get; init; }

    /// <summary>
    /// Chiave del segreto che contiene la password del server.
    /// </summary>
    /// <remarks>
    /// La password non sta qui: <c>settings.json</c> è un file di testo che viaggia
    /// nel pacchetto di configurazione e nei backup. Vive nel deposito protetto,
    /// insieme a quelle SIP.
    /// </remarks>
    public string PasswordKey => "update.password";

    /// <summary>Se controllare la presenza di aggiornamenti all'avvio.</summary>
    /// <remarks>
    /// Il controllo è una lettura dell'elenco della cartella remota: qualche
    /// centinaio di byte, e non scarica nulla finché l'operatore non lo chiede. Se il
    /// server non risponde la cosa finisce nel registro e basta — un posto operatore
    /// non deve rallentare l'avvio per un aggiornamento.
    /// </remarks>
    public bool CheckOnStartup { get; init; } = true;

    /// <summary>
    /// Se accettare un pacchetto la cui impronta non è dichiarata nelle note.
    /// </summary>
    /// <remarks>
    /// Con la riga <c>SHA256:</c> nel file delle novità, il pacchetto scaricato viene
    /// confrontato con l'impronta dichiarata prima di proporne l'installazione. Senza
    /// quella riga non c'è nulla da confrontare: l'impostazione decide se procedere
    /// avvisando o rifiutare. Il valore predefinito procede, perché la riga è
    /// facoltativa e una postazione non deve restare indietro per una dimenticanza di
    /// chi pubblica.
    /// </remarks>
    public bool AllowUnverifiedPackages { get; init; } = true;
}

/// <summary>Impostazioni di collegamento al centralino Yeastar.</summary>
public sealed record PbxSettings
{
    /// <summary>Se tentare il collegamento all'API del centralino.</summary>
    /// <remarks>
    /// <para>
    /// Va disattivato quando il centralino non espone l'API, per piano di licenza o
    /// per scelta dell'amministratore. Senza questo interruttore l'applicazione
    /// tenta comunque il collegamento e ogni interrogazione della coda risponde
    /// "collegamento al centralino interrotto": una diagnosi corretta di una
    /// situazione che non è un guasto, e che quindi non si può risolvere.
    /// </para>
    /// <para>
    /// Il valore predefinito è <c>true</c> per non disattivare il collegamento sulle
    /// postazioni già configurate e funzionanti, dove il campo manca dal file di
    /// configurazione e verrebbe letto come predefinito.
    /// </para>
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>Nome host o indirizzo del PBX.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Porta dell'API.</summary>
    public int Port { get; init; } = 8088;

    /// <summary>Se usare HTTPS e WSS.</summary>
    public bool UseTls { get; init; } = true;

    /// <summary>Client ID generato nel portale del PBX.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Identificativi delle code da sorvegliare.</summary>
    public IReadOnlyList<int> QueueIds { get; init; } = [];

    /// <summary>
    /// Se l'ordine dell'elenco di attesa corrisponde all'ordine di servizio.
    /// </summary>
    /// <remarks>
    /// Da attivare solo dopo la verifica su PBX reale. Finché è disattivo la
    /// posizione in coda non viene annunciata, perché dire "lei è il primo" a chi è
    /// il terzo è peggio che non dire nulla.
    /// </remarks>
    public bool WaitingListIsOrdered { get; init; }

    /// <summary>Se abilitare il prelievo selettivo di una chiamata dalla coda.</summary>
    /// <remarks>
    /// Ipotesi implementativa non ancora verificata: vedi
    /// YEASTAR-INTEGRAZIONE.md §8.1.
    /// </remarks>
    public bool EnableExperimentalQueuePick { get; init; }

    /// <summary>
    /// Se accettare qualunque certificato senza verificarlo né chiederlo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Da usare soltanto in laboratorio. Con la richiesta di fiducia interattiva
    /// questa opzione ha perso quasi ogni ragione d'essere: un certificato
    /// autofirmato viene mostrato all'operatore, che può approvarlo una volta per
    /// tutte dopo averne confrontato l'impronta. Resta per le installazioni
    /// automatizzate dove non c'è nessuno a rispondere.
    /// </para>
    /// <para>
    /// Quando è attiva il fatto <b>compare nel riepilogo diagnostico e a ogni
    /// collegamento nel registro</b>: una postazione che ha rinunciato a verificare
    /// con chi sta parlando deve poterlo dimostrare a chi la verifica, e soprattutto
    /// non deve poterlo nascondere.
    /// </para>
    /// </remarks>
    public bool AcceptAnyCertificate { get; init; }
}

/// <summary>Impostazioni di accessibilità.</summary>
public sealed record AccessibilitySettings
{
    /// <summary>
    /// Instradamento della voce: Automatic, UiaOnly, ScreenReaderOnly,
    /// InternalVoiceOnly, Both.
    /// </summary>
    public string SpeechChannel { get; init; } = "Automatic";

    /// <summary>
    /// Se BlindPhone può pronunciare gli annunci con la propria voce sintetica.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vince su <see cref="SpeechChannel"/>: a <c>false</c> la voce interna tace in
    /// qualunque modalità, e gli annunci restano affidati allo screen reader tramite
    /// le notifiche UI Automation.
    /// </para>
    /// <para>
    /// Il valore predefinito è <c>true</c> perché su una postazione senza screen
    /// reader la voce interna è l'unico canale sonoro: partire spenti renderebbe il
    /// posto operatore muto per chi non ha ancora configurato nulla, che è la
    /// situazione del primo avvio.
    /// </para>
    /// </remarks>
    public bool InternalVoiceEnabled { get; init; } = true;

    /// <summary>Livello di dettaglio degli annunci.</summary>
    public VerbosityLevel Verbosity { get; init; } = VerbosityLevel.Standard;

    /// <summary>Modalità di annuncio automatico della coda.</summary>
    public QueueAnnouncementMode QueueMode { get; init; } = QueueAnnouncementMode.CountOnly;

    /// <summary>Come leggere i numeri telefonici.</summary>
    public DigitGrouping DigitGrouping { get; init; } = DigitGrouping.Single;

    /// <summary>Se sopprimere gli annunci non critici durante la conversazione.</summary>
    /// <remarks>
    /// Spento per impostazione predefinita: fra la risposta e la chiusura l'operatore
    /// non vedente restava senza nessuna notizia, e le cose che accadono mentre si
    /// parla sono proprio quelle che contano. La sovrapposizione resta governata dal
    /// dispositivo separato degli annunci e dall'attenuazione. Vedi
    /// <see cref="Core.Accessibility.AnnouncementOptions.SilenceDuringConversation"/>.
    /// </remarks>
    public bool SilenceDuringConversation { get; init; } = false;

    /// <summary>Soglia di attesa oltre la quale scatta l'allerta, in secondi.</summary>
    public int QueueWaitThresholdSeconds { get; init; } = 60;

    /// <summary>Intervallo del promemoria di chiamata in attesa, in secondi.</summary>
    public int HoldReminderSeconds { get; init; } = 30;

    /// <summary>Se riprodurre gli earcon.</summary>
    public bool EarconsEnabled { get; init; } = true;

    /// <summary>Volume degli earcon, da 0 a 1.</summary>
    public float EarconVolume { get; init; } = 0.6f;

    /// <summary>Se posizionare gli earcon nello spazio stereo per famiglia di evento.</summary>
    public bool EarconSpatialPanning { get; init; } = true;

    /// <summary>Se aggiornare la riga di stato del display braille.</summary>
    public bool BrailleEnabled { get; init; } = true;

    /// <summary>Nome della voce interna da usare.</summary>
    public string? InternalVoiceName { get; init; }

    /// <summary>Velocità della voce interna, da -10 a 10.</summary>
    public int InternalVoiceRate { get; init; } = 2;

    /// <summary>Finestra della doppia pressione, in millisecondi.</summary>
    public int DoubleTapWindowMs { get; init; } = 400;

    /// <summary>Tema dei colori dell'interfaccia.</summary>
    /// <remarks>
    /// Sta fra le impostazioni di accessibilità e non fra quelle di aspetto perché è
    /// lì che serve: il turno di notte si copre spesso in penombra, e per chi ha una
    /// ipovisione la scelta fra fondo chiaro e fondo scuro non è un gusto ma la
    /// differenza fra leggere lo schermo e non leggerlo.
    /// </remarks>
    public AppTheme Theme { get; init; } = AppTheme.FollowSystem;
}

/// <summary>
/// Tema dei colori dell'interfaccia.
/// </summary>
/// <remarks>
/// <b>Con il contrasto elevato attivo la scelta non viene applicata</b>, in nessuno
/// dei tre valori: comandano i colori di Windows. Chi usa il contrasto elevato lo ha
/// configurato su misura per la propria vista, e un tema scritto dal prodotto sarebbe
/// l'unica finestra della macchina a ignorarlo.
/// </remarks>
public enum AppTheme
{
    /// <summary>Segue il tema chiaro o scuro impostato in Windows.</summary>
    FollowSystem,

    /// <summary>Fondo chiaro, sempre.</summary>
    Light,

    /// <summary>Fondo scuro, sempre.</summary>
    Dark,
}

/// <summary>
/// Regolazione del percorso audio della conversazione.
/// </summary>
/// <remarks>
/// Valori esposti all'operatore perché quello giusto dipende dalla rete e non è
/// determinabile in anticipo: la configurazione che funziona su rete locale cablata
/// produce interruzioni su Wi-Fi o su collegamento geografico. La diagnostica
/// riporta il numero di svuotamenti del buffer, che è il dato su cui regolarsi
/// invece di procedere a orecchio.
/// </remarks>
public sealed record AudioTuning
{
    /// <summary>
    /// Cuscinetto accumulato prima di iniziare a riprodurre, in millisecondi.
    /// </summary>
    /// <remarks>
    /// Decide fra interruzioni e ritardo. Indicativamente: 40-60 su rete locale,
    /// 80-120 come compromesso prudente, 150-250 su reti disturbate. Oltre 150 il
    /// ritardo comincia a produrre sovrapposizioni con l'interlocutore.
    /// </remarks>
    public int JitterBufferMs { get; init; } = 100;

    /// <summary>Capienza massima del buffer di riproduzione, in millisecondi.</summary>
    public int MaxBufferMs { get; init; } = 500;

    /// <summary>Latenza richiesta al dispositivo audio, in millisecondi.</summary>
    /// <remarks>
    /// Sotto i 50 millisecondi alcune schede USB producono scatti a prescindere
    /// dallo stato della rete.
    /// </remarks>
    public int DeviceLatencyMs { get; init; } = 100;

    /// <summary>Durata di pacchettizzazione dell'audio trasmesso, in millisecondi.</summary>
    public int PacketMs { get; init; } = 20;
}

/// <summary>
/// Comportamento della finestra e dell'icona nell'area di notifica.
/// </summary>
/// <remarks>
/// Su un posto operatore chiudere la finestra di un softphone significa smettere di
/// ricevere chiamate, e i chiamanti squillano a vuoto senza che nessuno se ne
/// accorga. Con l'icona nell'area di notifica la chiusura diventa un "metti via" e la
/// registrazione al centralino resta attiva.
/// </remarks>
public sealed record TraySettings
{
    /// <summary>Se mostrare l'icona nell'area di notifica.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Se chiudendo la finestra BlindPhone deve restare in esecuzione.
    /// </summary>
    /// <remarks>
    /// Vale solo con l'icona attiva: senza icona, nascondere la finestra lascerebbe
    /// il programma in esecuzione senza alcun modo visibile di riaprirlo.
    /// </remarks>
    public bool MinimizeToTrayOnClose { get; init; } = true;

    /// <summary>
    /// Se riportare in primo piano la finestra all'arrivo di una chiamata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È l'unica deroga alla regola per cui il focus si sposta solo su azione
    /// dell'operatore, e la deroga è necessaria: con la finestra riposta nell'area di
    /// notifica non c'è nulla su cui agire, e i comandi locali non arrivano.
    /// </para>
    /// <para>
    /// Si può disattivare perché su una postazione dove l'operatore prende appunti in
    /// un altro programma la finestra che salta davanti interrompe la digitazione. In
    /// quel caso restano le scorciatoie globali, che rispondono senza cambiare
    /// finestra.
    /// </para>
    /// </remarks>
    public bool RestoreOnIncomingCall { get; init; } = true;
}

/// <summary>Impostazioni relative alle chiamate e al trasferimento.</summary>
public sealed record CallSettings
{
    /// <summary>
    /// Modalità del tasto unico di trasferimento: ProgressiveDoubleTap,
    /// ProgressiveOnly, ExplicitChoice.
    /// </summary>
    public string TransferMode { get; init; } = "ProgressiveDoubleTap";

    /// <summary>Timeout della consultazione, in secondi.</summary>
    public int ConsultationTimeoutSeconds { get; init; } = 20;

    /// <summary>Se chiedere conferma prima di trasferire verso un numero esterno.</summary>
    public bool ConfirmExternalTransfers { get; init; } = true;

    /// <summary>Se avvisare quando la destinazione risulta non disponibile.</summary>
    public bool WarnOnUnavailableDestination { get; init; } = true;

    /// <summary>Lunghezza massima di un numero perché sia considerato interno.</summary>
    public int MaxInternalNumberLength { get; init; } = 5;

    /// <summary>
    /// Se far sentire all'interlocutore l'avviso vocale di inizio e fine
    /// registrazione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Attivo per impostazione predefinita, ed è il valore che va lasciato.</b>
    /// L'avviso non è una cortesia: informare chi è in linea prima che la raccolta
    /// cominci è ciò che rende lecita la registrazione di una conversazione, e su un
    /// centralino di ente pubblico è l'adempimento su cui i controlli si fermano per
    /// primi.
    /// </para>
    /// <para>
    /// Si può togliere perché esistono postazioni in cui l'informativa è già data
    /// altrove — un messaggio registrato all'ingresso della coda, un cartello, una
    /// clausola accettata in fase di contatto — e ripeterla a ogni registrazione
    /// sarebbe un doppione. Toglierlo <b>non</b> toglie l'obbligo di informare: sposta
    /// solo il momento in cui lo si fa, e BlindPhone lo dice all'operatore ogni volta
    /// che la registrazione parte senza avviso.
    /// </para>
    /// </remarks>
    public bool AnnounceRecording { get; init; } = true;
}

/// <summary>Configurazione completa dell'applicazione.</summary>
/// <remarks>
/// Serializzata in <c>%APPDATA%\BlindPhone\settings.json</c>. Non contiene alcun
/// segreto: password SIP e Client Secret dell'API vivono nel deposito protetto
/// gestito da <see cref="SecretStore"/>.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Versione dello schema, per le future migrazioni.</summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>2 — gli account SIP sono diventati un elenco.</description></item>
    ///   <item><description>3 — il registro passa a «tutto» per la durata della beta.</description></item>
    ///   <item><description>
    ///   4 — durante la conversazione gli annunci non tacciono più.
    ///   </description></item>
    /// </list>
    /// Vedi <see cref="Migrate"/>.
    /// </remarks>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Versione di schema prodotta da questa versione di BlindPhone.</summary>
    public const int CurrentSchemaVersion = 4;

    /// <summary>
    /// Account SIP configurato, il primo dell'elenco.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Resta per compatibilità e non è più la sorgente di verità.</b> Le
    /// configurazioni scritte prima degli account multipli hanno questo campo e non
    /// <see cref="SipAccounts"/>: la migrazione lo travasa nell'elenco al primo
    /// avvio. Da quel momento in poi vale l'elenco, e questo campo continua a
    /// riportare l'account predefinito perché una configurazione esportata resti
    /// leggibile da una versione precedente.
    /// </para>
    /// </remarks>
    public SipSettings Sip { get; init; } = new();

    /// <summary>
    /// Tutti gli account SIP configurati sulla postazione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Servono a un caso concreto e frequente: un centralinista che copre due enti,
    /// o una postazione condivisa a turni fra persone con interni diversi. Prima
    /// l'unico modo era riscrivere l'account a mano a ogni cambio, cioè quattro campi
    /// e una password in un pannello, con la postazione fuori servizio nel frattempo.
    /// </para>
    /// <para>
    /// Le password non stanno qui: ciascuna vive nel deposito protetto sotto la
    /// chiave dell'account. Vedi <see cref="SipSettings.PasswordKey"/>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SipSettings> SipAccounts { get; init; } = [];

    /// <summary>
    /// Identificativo dell'account con cui BlindPhone si registra all'avvio.
    /// </summary>
    /// <remarks>
    /// Se vuoto o se punta a un account che non esiste più, vale il primo
    /// dell'elenco: una postazione senza account predefinito valido deve comunque
    /// partire registrata, perché la sola alternativa è un telefono che non squilla.
    /// </remarks>
    public string? DefaultSipAccountId { get; init; }

    /// <summary>Recapiti e comportamento della segnalazione all'assistenza.</summary>
    public SupportSettings Support { get; init; } = new();

    /// <summary>
    /// Criterio che limita ciò che l'operatore può modificare.
    /// </summary>
    /// <remarks>
    /// Vive qui, cioè in un file su cui l'utente ha pieno controllo: <b>protegge
    /// dalla modifica accidentale, non dalla volontà</b>. Il vincolo non aggirabile è
    /// quello di postazione, che sta nel registro di Windows sotto una chiave
    /// riservata agli amministratori e ha la precedenza su questo. Vedi
    /// <see cref="ConfigurationPolicy"/>.
    /// </remarks>
    public ConfigurationPolicy Policy { get; init; } = new();

    /// <summary>
    /// Revisione dell'ultimo pacchetto di configurazione applicato.
    /// </summary>
    /// <remarks>
    /// Permette all'amministratore di aggiornare una configurazione già distribuita:
    /// un pacchetto con revisione più alta viene applicato da solo al primo avvio,
    /// uno con revisione uguale o più bassa viene ignorato. Senza, l'unica
    /// alternativa sarebbe riapplicarlo a ogni avvio, cancellando le regolazioni che
    /// l'operatore ha fatto nelle sezioni deliberatamente lasciate libere.
    /// </remarks>
    public int AppliedConfigurationRevision { get; init; }

    /// <summary>Impostazioni del centralino.</summary>
    public PbxSettings Pbx { get; init; } = new();

    /// <summary>Impostazioni di accessibilità.</summary>
    public AccessibilitySettings Accessibility { get; init; } = new();

    /// <summary>Impostazioni delle chiamate.</summary>
    public CallSettings Calls { get; init; } = new();

    /// <summary>Finestra e icona nell'area di notifica.</summary>
    public TraySettings Tray { get; init; } = new();

    /// <summary>Aggiornamenti scaricati dal server dell'ente.</summary>
    public UpdateSettings Updates { get; init; } = new();

    /// <summary>Da dove arriva la rubrica.</summary>
    public ContactsSettings Contacts { get; init; } = new();

    /// <summary>
    /// Come l'operatore comanda la postazione: scorciatoie complete o tastierino.
    /// </summary>
    /// <remarks>
    /// La modalità semplificata <b>aggiunge</b> i tre tasti del tastierino e blocca la
    /// rimappatura delle scorciatoie; non toglie nulla di quello che c'era.
    /// </remarks>
    public InteractionMode Interaction { get; init; } = InteractionMode.Standard;

    /// <summary>Dispositivo e volume del microfono.</summary>
    public AudioRouteSettings Microphone { get; init; } = new(null, null);

    /// <summary>Dispositivo e volume della conversazione.</summary>
    public AudioRouteSettings Conversation { get; init; } = new(null, null);

    /// <summary>Dispositivo e volume della suoneria.</summary>
    public AudioRouteSettings Ringer { get; init; } = new(null, null, 0.8f);

    /// <summary>
    /// File audio da usare come suoneria al posto del segnale sintetizzato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vuoto significa usare il segnale generato internamente, che resta il
    /// comportamento predefinito: non richiede file da installare né da mantenere
    /// allineati fra le postazioni.
    /// </para>
    /// <para>
    /// Se il file indicato non esiste più — spostato, rinominato, su una unità di
    /// rete non raggiungibile — si torna al segnale sintetizzato invece di restare
    /// muti. Una suoneria che non suona è il guasto peggiore possibile su un posto
    /// operatore, e va evitato anche a costo di ignorare la preferenza espressa.
    /// </para>
    /// </remarks>
    public string? RingtonePath { get; init; }

    /// <summary>Dispositivo e volume degli annunci vocali.</summary>
    public AudioRouteSettings Announcements { get; init; } = new(null, null);

    /// <summary>Regolazione del buffer audio della conversazione.</summary>
    public AudioTuning Audio { get; init; } = new();

    // Il profilo di scorciatoie attivo NON sta qui.
    //
    // C'era, e conviveva con la stessa informazione dentro hotkeys.json: i due file
    // finivano per contraddirsi — visto succedere, con settings.json fermo su
    // "Standard" e hotkeys.json su "Standard personalizzato" — e all'avvio vinceva
    // quello sbagliato, riportando l'operatore a un profilo che non aveva scelto.
    // L'unico proprietario e' HotkeyProfileStore.

    /// <summary>Lingua dell'interfaccia.</summary>
    public string Language { get; init; } = "it-IT";

    /// <summary>
    /// Giorni di conservazione dell'elenco delle ultime chiamate.
    /// </summary>
    /// <remarks>
    /// Valori previsti dall'interfaccia: 1 giorno, 7 una settimana, 30 un mese, 0
    /// «mai», cioè conserva tutto. Un valore diverso, scritto a mano nel file, viene
    /// ricondotto alla scelta più vicina non più corta — accorciare il periodo
    /// significherebbe cancellare chiamate che l'operatore si aspetta di trovare.
    /// <para>
    /// L'elenco contiene dati di traffico, quindi dati personali: il valore
    /// predefinito è un mese e va allineato alla policy di conservazione dell'ente.
    /// </para>
    /// </remarks>
    public int CallHistoryRetentionDays { get; init; } = 30;

    /// <summary>Giorni di conservazione dei file di registro diagnostico.</summary>
    /// <remarks>
    /// I registri contengono i numeri chiamati e chiamanti, quindi dati personali:
    /// il valore va allineato alla policy di conservazione dell'ente. Due settimane
    /// bastano a indagare una segnalazione arrivata dal campo, che è il solo motivo
    /// per cui quei file esistono.
    /// </remarks>
    public int DiagnosticLogRetentionDays { get; init; } = 14;

    /// <summary>
    /// Se la modalità diagnostica è attiva.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Attiva per impostazione predefinita finché il prodotto è in versione
    /// beta.</b> È il rovesciamento di una scelta precedente, e va motivato perché la
    /// motivazione di prima non era sbagliata: il registro dettagliato contiene i
    /// numeri chiamati e chiamanti riga per riga, quindi dati personali, e su una
    /// postazione in servizio significa accumulare dati di traffico che nessuno ha
    /// chiesto di conservare.
    /// </para>
    /// <para>
    /// Quello che è cambiato è il contesto. Una beta viene installata per trovare
    /// difetti, e un difetto che si presenta una volta ogni tre giorni su una
    /// postazione dove nessuno guarda lo schermo si indaga soltanto se il registro
    /// c'era già quando è successo. Accenderla dopo la segnalazione significa
    /// chiedere di riprodurre un guasto che non si riproduce a comando.
    /// </para>
    /// <para>
    /// <b>Va rimessa a <c>false</c> quando il prodotto esce dalla beta</b>, insieme a
    /// <see cref="DiagnosticLogLevel"/>. Il pannello di diagnostica continua a dire a
    /// ogni apertura che è accesa, e continua a permettere di spegnerla: chi installa
    /// la beta in un ente con una policy di conservazione stretta deve poterlo fare.
    /// </para>
    /// </remarks>
    public bool DebugModeEnabled { get; init; } = true;

    /// <summary>
    /// Dettaglio del registro diagnostico: Trace, Debug, Information, Warning, Error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Predefinito a <c>Trace</c> finché il prodotto è in versione beta</b>, cioè
    /// «tutto, anche il dettaglio interno»: comprende le transizioni di stato
    /// rifiutate, i pacchetti audio scartati e i messaggi SIP scambiati con il
    /// centralino — le righe che spiegano perché un comando «non ha fatto niente»,
    /// e le uniche che permettono di distinguere un difetto di BlindPhone da un
    /// comportamento del centralino.
    /// </para>
    /// <para>
    /// <b>Il prezzo va conosciuto.</b> A questo livello il file cresce di decine di
    /// megabyte per turno, e i messaggi SIP contengono i numeri per intero. La
    /// conservazione predefinita di <see cref="DiagnosticLogRetentionDays"/> giorni
    /// va allineata alla policy dell'ente, e alla fine della beta questo valore torna
    /// a <c>Information</c>.
    /// </para>
    /// </remarks>
    public string DiagnosticLogLevel { get; init; } = "Trace";

    /// <summary>
    /// Account SIP con cui registrarsi, scelto fra quelli configurati.
    /// </summary>
    /// <remarks>
    /// Applica la regola di ripiego: predefinito se valido, altrimenti il primo
    /// utilizzabile dell'elenco, altrimenti quello storico. Restituisce sempre
    /// qualcosa, anche non configurato, perché chi chiama distingue «non configurato»
    /// da «assente» guardando <see cref="SipSettings.IsUsable"/>.
    /// </remarks>
    public SipSettings ActiveSipAccount
    {
        get
        {
            if (SipAccounts.Count == 0) return Sip;

            var predefinito = SipAccounts.FirstOrDefault(a => a.Id == DefaultSipAccountId);
            if (predefinito is not null && predefinito.IsUsable) return predefinito;

            return SipAccounts.FirstOrDefault(a => a.IsUsable) ?? SipAccounts[0];
        }
    }

    /// <summary>
    /// Porta la configurazione allo schema corrente.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il caso che questa migrazione deve non rompere</b> è una postazione in
    /// servizio che si aggiorna: ha un account funzionante in <c>Sip</c> e la
    /// password nel deposito sotto la chiave storica. Travasarlo nell'elenco
    /// assegnandogli un identificativo nuovo cambierebbe la chiave del segreto, e la
    /// password risulterebbe persa — cioè una postazione muta al primo riavvio, con
    /// l'operatore che non ha modo di capire perché.
    /// </para>
    /// <para>
    /// Per questo il primo account migrato conserva l'identificativo vuoto, che
    /// <see cref="SipSettings.PasswordKey"/> traduce nella chiave storica. Gli account
    /// aggiunti dopo ne ricevono uno proprio.
    /// </para>
    /// <para>
    /// La migrazione è idempotente: rieseguirla su una configurazione già migrata non
    /// cambia nulla.
    /// </para>
    /// </remarks>
    public AppSettings Migrate()
    {
        var migrata = MigrateSipAccounts();
        migrata = migrata.MigrateBetaLogging();
        migrata = migrata.MigrateConversationSilence();

        return migrata.SchemaVersion == CurrentSchemaVersion
            ? migrata
            : migrata with { SchemaVersion = CurrentSchemaVersion };
    }

    /// <summary>Schema 2: l'account SIP diventa il primo di un elenco.</summary>
    private AppSettings MigrateSipAccounts()
    {
        if (SipAccounts.Count > 0)
        {
            // Gia' migrata. Si assicura solo che il predefinito punti a qualcosa che
            // esiste: un identificativo orfano — account eliminato a mano dal file —
            // lascerebbe la postazione non registrata senza dire perche'.
            return SipAccounts.Any(a => a.Id == DefaultSipAccountId)
                ? this
                : this with { DefaultSipAccountId = SipAccounts[0].Id };
        }

        // Nessun account nell'elenco: si travasa quello storico, identificativo vuoto
        // compreso, perche' e' quello che tiene insieme la password gia' salvata.
        return this with
        {
            SipAccounts = [Sip],
            DefaultSipAccountId = Sip.Id,
        };
    }

    /// <summary>
    /// Schema 3: il registro passa a «tutto» sulle postazioni già configurate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cambiare il solo valore predefinito non sarebbe bastato: una postazione già
    /// in uso ha <c>settings.json</c> scritto, quindi il predefinito non la tocca e
    /// resterebbe con il registro al livello normale — cioè proprio la postazione su
    /// cui la beta sta girando e da cui devono arrivare le segnalazioni.
    /// </para>
    /// <para>
    /// <b>Avviene una volta sola.</b> Dopo il primo salvataggio la versione di schema
    /// è quella corrente, e l'operatore che abbassa il dettaglio se lo tiene abbassato:
    /// chi installa la beta in un ente con una policy di conservazione stretta deve
    /// poter decidere diversamente, e questa migrazione non deve trasformarsi in una
    /// preferenza che torna da sola a ogni avvio.
    /// </para>
    /// </remarks>
    private AppSettings MigrateBetaLogging()
    {
        if (SchemaVersion >= 3) return this;

        // nameof e non la stringa "Trace": un refuso produrrebbe un livello che
        // LogLevelSwitch.Parse non riconosce, e davanti a un nome irriconoscibile
        // quello ricade sul livello normale — cioe' esattamente il contrario di
        // quello che questa migrazione vuole ottenere, e in silenzio.
        return this with
        {
            DiagnosticLogLevel = nameof(LogLevel.Trace),
            DebugModeEnabled = true,
        };
    }

    /// <summary>
    /// Schema 4: durante la conversazione gli annunci non tacciono più.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cambiare il solo valore predefinito non basterebbe, ed è la stessa ragione
    /// della migrazione precedente: una postazione già in uso ha <c>settings.json</c>
    /// scritto, con dentro <c>true</c>, e continuerebbe a tacere. Ma quel <c>true</c>
    /// non è quasi mai una scelta: è il valore che il prodotto ci ha messo da solo
    /// quando quella era l'impostazione predefinita.
    /// </para>
    /// <para>
    /// La segnalazione che l'ha resa necessaria: «gli annunci cessano da quando
    /// squilla la chiamata a quando termina». Fra la risposta e la chiusura
    /// l'operatore non vedente restava senza nessuna notizia — una seconda chiamata
    /// che entra, un trasferimento che fallisce, la registrazione che si interrompe —
    /// e sono proprio i minuti in cui quelle notizie contano di più.
    /// </para>
    /// <para>
    /// <b>Avviene una volta sola.</b> Chi il silenzio lo vuole davvero lo riattiva da
    /// Impostazioni e se lo tiene: dopo il primo salvataggio la versione di schema è
    /// quella corrente, e questa migrazione non torna a togliergli la scelta a ogni
    /// avvio.
    /// </para>
    /// </remarks>
    private AppSettings MigrateConversationSilence()
    {
        if (SchemaVersion >= 4) return this;

        return this with
        {
            Accessibility = Accessibility with { SilenceDuringConversation = false },
        };
    }
}
