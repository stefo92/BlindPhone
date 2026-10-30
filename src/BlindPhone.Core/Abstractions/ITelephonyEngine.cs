using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Abstractions;

/// <summary>
/// Motore telefonico: registrazione SIP, gestione delle chiamate, trasferimenti.
/// </summary>
/// <remarks>
/// <para>
/// L'interfaccia esiste per due ragioni concrete, non per simmetria architetturale.
/// La prima: consente di sostituire SIPSorcery con PJSIP se l'interoperabilità con
/// un centralino specifico lo richiedesse, senza toccare il core. La seconda, più
/// importante nel quotidiano: consente di collaudare le macchine a stati della
/// chiamata e del trasferimento con un motore finto, su scenari che con un
/// centralino reale sarebbero lenti da riprodurre e impossibili da rendere
/// deterministici.
/// </para>
/// </remarks>
public interface ITelephonyEngine : IAsyncDisposable
{
    /// <summary>Stato corrente della registrazione SIP.</summary>
    SipRegistrationState RegistrationState { get; }

    /// <summary>
    /// Contatori diagnostici del motore, in coppie già leggibili.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Servono al pannello di diagnostica e alla segnalazione all'assistenza. Sono
    /// coppie di stringhe e non un tipo strutturato di proposito: ogni motore ha
    /// contatori suoi, e un'interfaccia che li elencasse per nome costringerebbe a
    /// cambiarla ogni volta che se ne aggiunge uno.
    /// </para>
    /// <para>
    /// Su questo prodotto sono i numeri che contano davvero: pacchetti inviati e
    /// ricevuti, svuotamenti del buffer e millisecondi di silenzio inserito. La somma
    /// del silenzio inserito e dell'audio reale deve dare la durata della chiamata, ed
    /// è il controllo di coerenza più rapido davanti a una segnalazione di audio
    /// disturbato.
    /// </para>
    /// </remarks>
    IReadOnlyList<DiagnosticCounter> DiagnosticCounters { get; }

    /// <summary>
    /// Pacchetti audio di una chiamata nelle due direzioni, se il motore li conta.
    /// </summary>
    /// <param name="callId">Chiamata da interrogare.</param>
    /// <returns>I contatori, oppure <c>null</c> se la chiamata non ha un percorso audio reale.</returns>
    /// <remarks>
    /// Ha un corpo qui, e non è una scorciatoia: il motore simulato e quelli dei collaudi
    /// non hanno pacchetti da contare, e «nessun dato» è la loro risposta onesta. Chi li
    /// conta davvero — il motore SIP — la sostituisce, e chi ne avvolge un altro la
    /// inoltra.
    /// </remarks>
    MediaFlowSnapshot? GetMediaFlow(string callId) => null;

    /// <summary>
    /// Sorgente dello stato degli interni per via SIP, se il motore la offre.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È il ripiego per i centralini senza API: la stessa strada che usano i telefoni
    /// da tavolo per accendere i tasti di occupato. Vale <c>null</c> sui motori che non
    /// la implementano — quello simulato — e finché il motore non è avviato, perché
    /// prima non esiste un trasporto SIP su cui sottoscrivere.
    /// </para>
    /// <para>
    /// Sta sull'interfaccia e non sul tipo concreto perché la scelta fra API e SIP la
    /// fa un servizio del core, che non conosce SIPSorcery.
    /// </para>
    /// </remarks>
    IPresenceSource? PresenceSource { get; }

    /// <summary>
    /// Sondaggio di raggiungibilità sugli interni, quando il motore lo offre.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È la terza strada per sapere come stanno i colleghi, e la sola che non chieda
    /// all'impianto niente oltre il rispetto della norma: un <c>OPTIONS</c> per interno,
    /// la stessa domanda che il centralino gira a noi ogni due minuti.
    /// </para>
    /// <para>
    /// Vale <c>null</c> sui motori che non la implementano — quello simulato — e finché
    /// il motore non è avviato, perché prima non esiste un trasporto SIP su cui
    /// chiedere.
    /// </para>
    /// </remarks>
    IReachabilityProbe? ReachabilityProbe { get; }

    /// <summary>Sollevato al cambio di stato della registrazione.</summary>
    event EventHandler<SipRegistrationState>? RegistrationStateChanged;

    /// <summary>Sollevato all'arrivo di una chiamata entrante.</summary>
    event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <summary>
    /// Sollevato quando una chiamata ripiega in chiaro perché la destinazione ha
    /// rifiutato la cifratura.
    /// </summary>
    /// <remarks>
    /// Porta la destinazione. Esiste perché il ripiego <b>va detto</b>: con la
    /// politica «cifra se si può», una protezione che si spegne da sola in silenzio
    /// lascia l'operatore a credere di averla, ed è la peggiore delle tre situazioni
    /// possibili — peggio di non averla e saperlo.
    /// </remarks>
    event EventHandler<string>? SrtpDowngraded;

    /// <summary>Sollevato quando lo stato di una chiamata cambia per cause remote.</summary>
    event EventHandler<CallProgressEventArgs>? CallProgress;

    /// <summary>Sollevato all'arrivo dell'esito di un trasferimento richiesto.</summary>
    /// <remarks>
    /// Alimentato dai NOTIFY con corpo <c>message/sipfrag</c> che seguono un REFER
    /// (RFC 3515). È l'unico modo di sapere se un trasferimento cieco è andato a
    /// buon fine: senza, l'operatore invierebbe la chiamata nel vuoto senza
    /// saperlo.
    /// </remarks>
    event EventHandler<TransferProgressEventArgs>? TransferProgress;

    /// <summary>Avvia il motore e la registrazione presso il registrar.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Arresta il motore, chiudendo le chiamate attive in modo ordinato.</summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>Compone un numero.</summary>
    /// <param name="destination">Numero o interno di destinazione.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Identificativo locale della chiamata creata.</returns>
    Task<string> DialAsync(string destination, CancellationToken cancellationToken);

    /// <summary>
    /// Si include in una conversazione già in corso su un interno occupato.
    /// </summary>
    /// <param name="dialog">Conversazione da raggiungere, come la riporta la presenza.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Identificativo locale della chiamata creata.</returns>
    /// <remarks>
    /// <para>
    /// <b>Meccanismo: l'intestazione <c>Join</c> dell'RFC 3911.</b> Si manda all'interno
    /// un normale invito che porta con sé il nome del dialogo a cui vuole unirsi; il
    /// centralino, se lo sostiene, mescola le tre parti invece di far squillare un
    /// secondo apparecchio. È lo stesso meccanismo con cui i telefoni da tavolo
    /// realizzano l'inclusione, e non richiede codici di servizio propri del
    /// produttore.
    /// </para>
    /// <para>
    /// <b>Il centralino può non sostenerlo, ed è il caso normale da gestire.</b>
    /// L'RFC 3911 è opzionale: un PBX che non lo implementa risponde con un errore, e
    /// va riferito all'operatore in italiano invece di lasciarlo davanti a una
    /// chiamata che non parte. Non esiste ripiego automatico — comporre l'interno
    /// senza <c>Join</c> lo farebbe squillare durante una conversazione, che è
    /// l'opposto di quello che l'operatore ha chiesto.
    /// </para>
    /// <para>
    /// <b>Chi si include deve farsi sentire.</b> Il segnale periodico che avverte i
    /// presenti non lo emette il motore telefonico ma chi governa la chiamata: vedi
    /// <see cref="Accessibility.IntrusionTone"/>. Il motore si limita ad aprire la
    /// linea.
    /// </para>
    /// </remarks>
    Task<string> BargeInAsync(DialogReference dialog, CancellationToken cancellationToken);

    /// <summary>Risponde a una chiamata entrante.</summary>
    /// <param name="callId">Identificativo locale della chiamata.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task AnswerAsync(string callId, CancellationToken cancellationToken);

    /// <summary>Riaggancia o rifiuta una chiamata.</summary>
    /// <param name="callId">Identificativo locale della chiamata.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task HangUpAsync(string callId, CancellationToken cancellationToken);

    /// <summary>Mette una chiamata in attesa.</summary>
    /// <param name="callId">Identificativo locale della chiamata.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task HoldAsync(string callId, CancellationToken cancellationToken);

    /// <summary>Riprende una chiamata dall'attesa.</summary>
    /// <param name="callId">Identificativo locale della chiamata.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task ResumeAsync(string callId, CancellationToken cancellationToken);

    /// <summary>
    /// Esegue un trasferimento cieco: invia un REFER verso la destinazione senza
    /// consultarla.
    /// </summary>
    /// <param name="callId">Chiamata da trasferire.</param>
    /// <param name="destination">Destinazione.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task BlindTransferAsync(string callId, string destination, CancellationToken cancellationToken);

    /// <summary>
    /// Completa un trasferimento con annuncio, collegando fra loro le due chiamate.
    /// </summary>
    /// <param name="callId">Chiamata originale, in attesa.</param>
    /// <param name="consultationCallId">Chiamata di consultazione, già instaurata.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// Realizzato con un REFER che porta l'header <c>Replaces</c> (RFC 3891) del
    /// dialogo di consultazione.
    /// </remarks>
    Task AttendedTransferAsync(string callId, string consultationCallId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>true</c> se il motore sa unire due chiamate in una conferenza.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Va compilato con onestà, come le capacità del connettore di centralino: la
    /// conferenza si regge su un percorso audio reale, e su un motore che non ne ha
    /// uno la segnalazione riuscirebbe lasciando i due interlocutori a non sentirsi.
    /// Per un operatore non vedente una conferenza muta è indistinguibile da una
    /// riuscita in cui nessuno parla, ed è il modo peggiore di sbagliare.
    /// </para>
    /// <para>
    /// Dichiarare <c>false</c> non toglie il comando: lo fa rispondere con un rifiuto
    /// parlato, che è l'unico esito accettabile fra i due.
    /// </para>
    /// </remarks>
    bool SupportsConference { get; }

    /// <summary>
    /// Unisce due chiamate già instaurate in una conferenza a tre.
    /// </summary>
    /// <param name="callIds">Chiamate da unire; devono essere tutte fuori dall'attesa.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// <para>
    /// Il missaggio è <b>locale</b>: il posto operatore fa da ponte fra le due linee,
    /// mandando a ciascuna la propria voce sommata a quella dell'altra. È la scelta
    /// che funziona su qualunque centralino, perché non chiede nulla oltre a due
    /// chiamate contemporanee — cosa che il prodotto sa già fare per il trasferimento
    /// con annuncio.
    /// </para>
    /// <para>
    /// La regola del missaggio è che ogni linea riceve tutto <b>tranne se stessa</b>.
    /// Rimandare a una linea il proprio audio produrrebbe un'eco ritardata, che è il
    /// modo classico di rendere inutilizzabile una conferenza fatta in casa.
    /// </para>
    /// </remarks>
    Task StartConferenceAsync(IReadOnlyList<string> callIds, CancellationToken cancellationToken);

    /// <summary>
    /// Scioglie la conferenza, lasciando le chiamate aperte e indipendenti.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// Smonta il solo ponte audio. Chi resta in linea e chi va in attesa lo decide il
    /// gestore delle chiamate, con i comandi ordinari di attesa e ripresa.
    /// </remarks>
    Task StopConferenceAsync(CancellationToken cancellationToken);

    /// <summary>Invia una cifra DTMF sulla chiamata.</summary>
    /// <param name="callId">Chiamata su cui inviare.</param>
    /// <param name="digit">Cifra da inviare.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    Task SendDtmfAsync(string callId, char digit, CancellationToken cancellationToken);

    /// <summary>Attiva o disattiva il silenziamento del microfono.</summary>
    /// <param name="muted"><c>true</c> per silenziare.</param>
    void SetMuted(bool muted);

    /// <summary>Indica se il microfono è attualmente silenziato.</summary>
    bool IsMuted { get; }

    /// <summary>
    /// Dichiara al centralino che l'interno non accetta chiamate.
    /// </summary>
    /// <param name="enabled"><c>true</c> per rifiutare le chiamate entranti.</param>
    /// <remarks>
    /// <para>
    /// <b>Non basta smettere di rispondere.</b> Un posto operatore che lascia squillare
    /// tiene la chiamata occupata per tutta la durata dello squillo: in coda il
    /// chiamante resta assegnato a un interno che non risponderà, e il centralino lo
    /// passa al successivo solo allo scadere del timeout. Rifiutando subito, la
    /// chiamata riparte verso un collega nell'istante in cui arriva.
    /// </para>
    /// <para>
    /// <b>Non tocca le chiamate già in corso</b>, né quella che sta squillando in
    /// questo momento: mettersi in pausa significa non prenderne di nuove, non far
    /// cadere quella che si ha in mano.
    /// </para>
    /// </remarks>
    void SetDoNotDisturb(bool enabled);

    /// <summary>Indica se l'interno sta rifiutando le chiamate entranti.</summary>
    bool IsDoNotDisturb { get; }

    /// <summary>
    /// <c>true</c> se la segnalazione viaggia cifrata, cioè se il trasporto è TLS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Non dice nulla sull'audio</b>, e la distinzione è la sola cosa che conta qui.
    /// TLS protegge la <b>segnalazione</b>: chi chiama chi, quando, verso quale
    /// interno. Il flusso audio viaggia su RTP, che è un altro protocollo e un altro
    /// canale, e si cifra con SRTP — una cosa che si negozia separatamente e che può
    /// benissimo mancare su una linea con la segnalazione cifrata.
    /// </para>
    /// <para>
    /// Sono esposte separatamente perché confonderle è il modo più facile di credersi
    /// protetti quando non lo si è. Un operatore che ha configurato TLS e sente dire
    /// «chiamata protetta» conclude, ragionevolmente, che nessuno può ascoltare la
    /// conversazione: e sarebbe falso.
    /// </para>
    /// </remarks>
    bool IsSignallingEncrypted { get; }

    /// <summary>
    /// Avvia la registrazione di una conversazione, nelle due direzioni.
    /// </summary>
    /// <param name="callId">Chiamata da registrare.</param>
    /// <param name="path">File da produrre.</param>
    /// <returns><c>true</c> se la registrazione è partita.</returns>
    /// <remarks>
    /// Restituisce <c>false</c> anche quando il motore non sa registrare — quello
    /// simulato — invece di sollevare: chi chiama ha già il compito di dirlo
    /// all'operatore, e un'eccezione lo costringerebbe a dirlo due volte in due modi.
    /// </remarks>
    bool StartCallRecording(string callId, string path);

    /// <summary>
    /// Chiude la registrazione di una conversazione.
    /// </summary>
    /// <param name="callId">Chiamata registrata.</param>
    /// <returns>
    /// Durata registrata e attesa sul file definitivo, oppure <c>null</c> se non era
    /// in corso alcuna registrazione. La durata torna subito perché va annunciata
    /// nell'istante in cui la registrazione finisce; il file arriva dopo, perché la
    /// conversione dura.
    /// </returns>
    (TimeSpan Duration, Task<string> File)? StopCallRecording(string callId);

    /// <summary>
    /// Fa sentire all'interlocutore un avviso vocale, sovrapposto alla conversazione.
    /// </summary>
    /// <param name="callId">Chiamata su cui far sentire l'avviso.</param>
    /// <param name="pcm">Campioni a 16 bit dell'avviso, monofonici.</param>
    /// <param name="sampleRate">Frequenza di campionamento dei campioni forniti.</param>
    /// <remarks>
    /// I campioni arrivano già sintetizzati: il motore telefonico li riporta alla
    /// frequenza del codec e li mette in linea, ma non sa nulla di sintesi vocale.
    /// È la stessa separazione per cui non sa nulla di rubrica.
    /// </remarks>
    void PlayToRemote(string callId, short[] pcm, int sampleRate);

    /// <summary>
    /// <c>true</c> se il motore è simulato: le chiamate si comportano come vere ma
    /// nessuna telefonata parte davvero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deve essere esposto e comunicato con insistenza. Un operatore che crede di
    /// essere in servizio quando non lo è resta seduto ad aspettare chiamate che
    /// non arriveranno mai, e non ha alcun modo di accorgersene: il silenzio della
    /// modalità simulata è indistinguibile da un momento di calma.
    /// </para>
    /// <para>
    /// La modalità serve alla formazione di un operatore appena assunto, e come
    /// ripiego quando l'account SIP non è configurato — caso in cui va segnalato
    /// come un problema, non come una modalità scelta.
    /// </para>
    /// </remarks>
    bool IsSimulated { get; }
}

/// <summary>Dati di una chiamata entrante.</summary>
/// <param name="CallId">Identificativo locale assegnato dal motore.</param>
/// <param name="RemoteParty">Controparte, come ricavata dalla segnalazione.</param>
/// <param name="SipCallId">Call-ID SIP del dialogo.</param>
/// <param name="QueueHint">
/// Nome della coda, se la segnalazione lo indica (per esempio nell'header
/// <c>Alert-Info</c> o nel display name del chiamato).
/// </param>
/// <param name="At">Istante di ricezione.</param>
public sealed record IncomingCallEventArgs(
    string CallId,
    CallParty RemoteParty,
    string SipCallId,
    string? QueueHint,
    DateTimeOffset At);

/// <summary>Avanzamento di una chiamata per cause remote.</summary>
/// <param name="CallId">Identificativo locale della chiamata.</param>
/// <param name="Trigger">Evento da applicare alla macchina a stati.</param>
/// <param name="EndReason">Motivo di chiusura, se l'evento la comporta.</param>
/// <param name="NegotiatedCodec">Codec negoziato, quando noto.</param>
/// <param name="IsWideband">Se il codec negoziato è a banda larga.</param>
/// <param name="IsEncrypted">Se il media è protetto da SRTP.</param>
/// <param name="At">Istante dell'evento.</param>
/// <param name="HasEarlyMedia">
/// Se la controparte sta già inviando audio prima della risposta. Significativo
/// solo con <see cref="CallTrigger.RemoteRinging"/>: distingue il caso in cui il
/// tono di libero arriva dalla rete da quello in cui va generato localmente,
/// altrimenti si sentirebbero due toni sfasati oppure nessuno.
/// </param>
public sealed record CallProgressEventArgs(
    string CallId,
    CallTrigger Trigger,
    CallEndReason? EndReason,
    string? NegotiatedCodec,
    bool IsWideband,
    bool IsEncrypted,
    DateTimeOffset At,
    bool HasEarlyMedia = false);

/// <summary>Avanzamento di un trasferimento richiesto.</summary>
/// <param name="CallId">Chiamata trasferita.</param>
/// <param name="Outcome">
/// Esito, oppure <c>null</c> se si tratta di un avanzamento intermedio (la
/// destinazione sta squillando) e non ancora di un esito definitivo.
/// </param>
/// <param name="Detail">Dettaglio in linguaggio naturale, per l'annuncio.</param>
/// <param name="At">Istante dell'evento.</param>
public sealed record TransferProgressEventArgs(
    string CallId,
    TransferOutcome? Outcome,
    string? Detail,
    DateTimeOffset At);
