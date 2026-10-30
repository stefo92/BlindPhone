# BlindPhone — Ingresso in riunione Zoom tramite Cloud Room Connector

**Documento tecnico complementare a [ANALISI-BLINDPHONE.md](ANALISI-BLINDPHONE.md), parallelo a [CISCO-INTEGRAZIONE.md](CISCO-INTEGRAZIONE.md), [3CX-INTEGRAZIONE.md](3CX-INTEGRAZIONE.md) e [PIATTAFORME-PA.md](PIATTAFORME-PA.md)**

| | |
|---|---|
| **Versione** | 1.2 — 23 settembre 2026 |
| **Ambito** | Far entrare l'operatore in una riunione Zoom dal posto operatore, come **funzionalità secondaria** |
| **Decisione** | **Strada SIP (Cloud Room Connector).** La strada Meeting SDK — che avrebbe dato chat, mano alzata ed elenco partecipanti — è stata **valutata e scartata per costo di licenza**: vedi §11 per che cosa si è lasciato e §11.8 per il conto che l'ha chiusa |
| **Stato** | Composizione, codec e riservatezza del codice d'accesso **implementati e collaudati** (§12). Resta il video (§3.1) e l'interfaccia |
| **Stato dei dati** | Documentazione pubblica Zoom (support.zoom.com, developers.zoom.us, library.zoom.com — consultata il 23 settembre 2026). **Nessuna verifica su licenza CRC reale.** I punti marcati **[DA VERIFICARE]** vanno provati su un account con licenza CRC prima di impegnare una stima |
| **Verifiche sul codice** | Fatte. `SipTelephonyEngine.BuildTargetUri`, `AudioCodecCatalog`, `SrtpPolicy`, modalità toni, conferenza a tre |

---

## 1. La conclusione, prima di tutto il resto

Sul **codice di rete non c'è quasi niente da fare**: BlindPhone sa già comporre un URI SIP verso un dominio esterno, e i codec audio che il Cloud Room Connector accetta sono **esattamente quelli che ha già**.

Il lavoro vero sta in tre punti che con le API non c'entrano:

1. **Il CRC è un gateway video e Zoom dichiara che non ammette chiamate solo audio** (§3). È il vincolo che decide se questa funzione esiste o no.
2. **L'ordine di preferenza dei codec predefinito è sbagliato per Zoom** (§4): BlindPhone offre G.729 per primo, e il CRC non lo elenca fra quelli supportati.
3. **Il valore per un operatore non vedente non è la qualità audio: è non dover navigare un risponditore automatico** (§6). Questo decide l'intera interazione.

E c'è un problema di riservatezza che nasce dalla forma stessa della composizione: **il codice d'accesso della riunione finisce nel numero composto**, quindi nel registro delle chiamate e negli annunci (§7.4).

---

## 2. Che cos'è il Cloud Room Connector, e che cosa non è

Il CRC (oggi *Conference Room Connector*) è il gateway che lascia entrare in una riunione Zoom gli apparati video standard SIP e H.323 — sale riunioni Cisco, Poly, Lifesize. Non è un'API di controllo chiamata: **è una destinazione da chiamare.**

Questa è la differenza sostanziale rispetto a Cisco e 3CX, e va capita subito per non impostare male il lavoro:

| | Cisco / 3CX / Yeastar | Zoom CRC |
|---|---|---|
| Che cosa si integra | Un'**API** che governa chiamate e stato | Un **numero da comporre** |
| Che cosa serve implementare | Un connettore: HTTP, WebSocket, autenticazione, modello eventi | Una stringa di composizione, e l'interfaccia per costruirla |
| Dove sta la difficoltà | Nel protocollo | Nell'interazione con l'operatore |

La *Conference Room Connector API* REST che Zoom pubblica per gli sviluppatori serve a **censire e amministrare gli apparati** di un parco sale (inventario, configurazione, dati d'uso) per conto di un account master. **Non offre controllo chiamata**, e per BlindPhone non serve: non stiamo amministrando sale riunioni.

### 2.1 La strada da non prendere: registrare BlindPhone al proxy SIP di Zoom

Il CRC ha una modalità *managed device*: l'apparato si **registra** al proxy SIP di Zoom e ottiene rubrica delle sale, calendario e chiamata in uscita governata da Zoom.

**Per BlindPhone è esclusa.** La postazione ha una sola registrazione SIP attiva alla volta — gli account multipli esistono, ma si cambiano, non convivono. Registrarsi a Zoom significherebbe **sregistrarsi dal centralino dell'ente**, cioè smettere di fare il mestiere principale per poter fare quello secondario. È il contrario della gerarchia che la funzione deve avere.

---

## 3. Il vincolo che decide tutto: il CRC non ammette chiamate solo audio

La documentazione Zoom è esplicita: *"The Cloud Room Connector does not support audio-only calls"*. Il CRC nasce per apparati video, e i codec che dichiara comprendono **H.264 e H.264 High Profile** per il video.

BlindPhone **non ha uno stack video**, e non è una mancanza da colmare: i suoi utenti non vedono. Aggiungere un codificatore H.264 significherebbe trascinare dentro una dipendenza nativa (FFmpeg o OpenH264), la sua licenza, il suo consumo di CPU e un intero percorso mediale — in un prodotto la cui installazione piccola e verificabile è un argomento di vendita nella PA. **Per una funzione secondaria è sproporzionato.**

Restano tre possibilità, in ordine di costo.

### 3.1 Offrire la linea video e non usarla — **[DA VERIFICARE], ed è la verifica che conta**

Dichiarare in SDP una seconda linea mediale video con H.264 in direzione `inactive` (o `recvonly`), non codificare nulla, e scartare l'eventuale RTP in arrivo. Nessun codificatore, nessuna dipendenza nativa: solo negoziazione.

**Funziona solo se il CRC ammette un partecipante che non trasmette video.** Molti gateway lo fanno; il divieto dichiarato da Zoom potrebbe riferirsi all'assenza completa della linea video, non a una linea inattiva. **Non è deducibile dalla documentazione.** Si risolve in mezza giornata con una licenza CRC e una cattura di pacchetti, e finché non è risolto **ogni stima di questa funzione è aria.**

> **È la prima cosa da fare, prima di scrivere una riga.** Se la risposta è no, il §3.3 non è un ripiego: è tutta la funzione.

### 3.2 Trasmettere un video finto

Un fotogramma fisso codificato in H.264. Risolve con certezza, e costa la dipendenza nativa che il §3 dichiara sproporzionata. **Da tenere solo se il cliente paga espressamente questa funzione** e la verifica §3.1 è fallita.

### 3.3 Non usare il CRC: numero telefonico della riunione

Ogni riunione Zoom pubblica dei numeri PSTN di accesso. L'operatore li compone come qualunque altro numero, e **questo funziona già oggi, senza scrivere niente.**

Si perde la banda larga (il PSTN è a banda stretta) e si guadagna un ostacolo: il risponditore automatico (§6.1). Ma è l'unica strada che **funziona con certezza**, e per riunioni ospitate da altri enti — cioè la maggioranza — è anche l'unica disponibile, perché la licenza CRC ce l'ha l'ospite, non l'operatore.

---

## 4. Codec: cosa integrare (niente) e cosa correggere (l'ordine)

### 4.1 Quello che il CRC accetta

Dalla documentazione Zoom sugli apparati supportati:

| | Codec dichiarati dal CRC | BlindPhone oggi |
|---|---|---|
| **Audio** | **G.711**, **G.722**, **Opus** (Opus solo su SIP) | G.722, G.711 A-law, G.711 μ-law, G.729 |
| **Video** | H.264, H.264 High Profile | — (vedi §3) |
| **Cifratura** | TLS 1.2 o superiore | TLS e SRTP già presenti |

**Sul lato audio non c'è nulla da aggiungere.** G.722 e G.711 coprono per intero quello che il CRC accetta, e la sovrapposizione è migliore di così: **G.722 è già il codec a banda larga di BlindPhone**, quello che soddisfa EN 301 549 §6.1. Una riunione Zoom via CRC si terrà quindi in banda larga, che è il livello di qualità che la norma europea chiede e che il PSTN non può dare.

### 4.2 Opus: no, e la ragione non è pigrizia

Opus sarebbe l'unico miglioramento possibile, e non vale il prezzo:

- lo stack SIP in uso **implementa quattro codec e basta** (G.722, G.711 nelle due varianti, G.729): Opus significa una dipendenza nuova, più la negoziazione, più la banda di prova;
- il guadagno rispetto a G.722 è reale ma sottile, e **su una riunione a molti partecipanti lo appiattisce il mixer di Zoom** prima che arrivi all'orecchio dell'operatore;
- G.722 soddisfa già il requisito normativo, che è la ragione per cui la banda larga sta nel prodotto.

**Su una funzione secondaria, un codec nuovo è la voce di costo peggiore**: si paga sull'intero percorso audio, che è il componente critico del prodotto, per un vantaggio che si sente solo nella funzione marginale.

### 4.3 Quello che invece va corretto: l'ordine di preferenza

Questo è un difetto vero, e sta nel codice di oggi.

L'ordine predefinito è **G.729, G.722, G.711 A-law** — con G.729 in testa perché è quello che i centralini italiani di riferimento negoziano nella pratica. Ma **il CRC non elenca G.729 fra i codec supportati.** Le conseguenze:

- nel caso migliore la trattativa spreca un giro e ripiega su G.722, e va bene;
- nel caso peggiore — una postazione dove l'amministratore ha ristretto l'elenco al solo G.729, che è una configurazione legittima su certi trunk — **la chiamata verso Zoom non si instaura affatto**, e il sintomo è una chiamata che non parte senza spiegazioni.

**Serve un profilo di codec per destinazione**, non un elenco unico. La forma minima: quando la destinazione è il CRC, l'offerta diventa **G.722, G.711 A-law** e basta — l'ordine che il gateway accetta, con la banda larga per prima perché qui non c'è nessun trunk che imponga la compressione.

È una modifica piccola e ben delimitata al catalogo dei codec, ed è **l'unico lavoro sui codec che questa funzione richiede.**

### 4.4 Cifratura

L'account Zoom può **imporre** la cifratura mediale agli apparati di terze parti (*Require media encryption for 3rd Party Endpoints*). Se il cliente ha quell'opzione attiva, BlindPhone deve presentarsi con **SIP su TLS e SRTP**.

Ci sono già entrambi. Ma la politica SRTP predefinita è **disattivata** — deliberatamente, perché accenderla d'ufficio cambierebbe il modo in cui si instaurano le chiamate su installazioni che oggi funzionano. Quindi: per questa funzione la cifratura **va accesa e provata**, e va scritto nella scheda di raccolta dati che è una domanda da fare all'IT del cliente (§8).

---

## 5. La stringa di composizione

È la sostanza tecnica della funzione. Formato SIP:

```
[ID riunione].[Codice].[Comando].[Chiave host].[Riservato].[Codice di composizione]@zoomcrc.com
```

Esempi dalla documentazione Zoom (con l'indirizzo del gateway al posto del dominio):

| Caso | Stringa |
|---|---|
| Riunione senza codice | `123456789@192.0.2.0` |
| Con codice d'accesso | `123456789.12345@192.0.2.0` |
| Con chiave host | `123456789.12345..123456@192.0.2.0` |
| Con codice di composizione | `123456789.12345....1a2bc4d@192.0.2.0` |

I campi vuoti si saltano lasciando il punto: è un formato posizionale, e **sbagliare il numero di punti manda in una riunione che non esiste.** La composizione va costruita dal programma, mai chiesta all'operatore.

Il *codice di composizione* merita una riga: i codici che iniziano per **1** saltano la sala d'attesa, consumano una licenza CRC da partecipante, identificano l'apparato come ospite e **abilitano la sincronizzazione del muto** — quest'ultima cosa conta parecchio (§7.3).

### 5.1 Il codice d'accesso non è quello che l'operatore ha ricevuto

Trappola pratica, e di quelle che fanno perdere un pomeriggio. Zoom distingue:

- il **codice d'accesso web**, che può contenere lettere ed è quello scritto nell'invito;
- l'`h323_password`, **numerico**, ed è l'unico che si può comporre da SIP, H.323 o telefono.

Sono due cose diverse per la stessa riunione. Se l'operatore prende il codice dall'invito e contiene lettere, **non entrerà mai**, e il messaggio di errore non glielo dirà. L'interfaccia deve accettare solo cifre nel campo del codice e dirlo, invece di lasciar comporre e fallire.

### 5.2 Quello che BlindPhone sa già fare

```csharp
var uri = destination.Contains('@')
    ? destination
    : $"sip:{destination}@{_account.Domain}";
```

`SipTelephonyEngine.BuildTargetUri` **usa la destinazione così com'è quando contiene una chiocciola.** Una stringa `123456789.12345@zoomcrc.com` digitata nel campo di composizione viene già oggi instradata come URI SIP: il trasporto non va inventato, va solo verificato end-to-end.

Resta da decidere **chi instrada la chiamata**:

| | Attraverso il centralino dell'ente | Direttamente da BlindPhone |
|---|---|---|
| Che cosa serve | Una rotta verso `zoomcrc.com` sul trunk del PBX | Aperture firewall sulla postazione dell'operatore |
| Chi la configura | L'IT dell'ente, una volta | L'IT dell'ente, su ogni postazione |
| Dove va il traffico | Dove va già tutto il resto | Fuori, per una via nuova |
| **Preferibile** | **Sì** | Solo se il PBX non può |

La prima è nettamente migliore: non tocca la postura di rete della postazione e lascia il governo del traffico dove l'ente lo ha già. **Se la seconda è inevitabile**, le aperture sono: SIP su TCP **5060-5061** e **443**, media su UDP **3000-4000**, verso un elenco di reti Zoom lungo (oltre cinquanta blocchi IPv4 più IPv6) che va preso dalla documentazione corrente e non copiato da qui.

---

## 6. L'interazione con l'operatore

### 6.1 Il valore vero: togliere di mezzo il risponditore

Per capire come va progettata l'interazione bisogna vedere che cosa distingue davvero le due strade, **per un operatore che non vede**.

Entrare via numero telefonico significa: comporre, ascoltare un messaggio registrato, digitare l'ID riunione, cancelletto, aspettare, digitare il codice, cancelletto, aspettare, sentirsi chiedere il numero di partecipante, capire che si può saltare con cancelletto. È un percorso a ostacoli **a tempo, tutto in ascolto**, dove un tono digitato troppo presto si perde e non c'è modo di sapere a che punto si è.

Entrare via CRC significa: **una composizione sola**, con tutto già dentro la stringa.

**Questa è la funzione.** Non la qualità audio — quella è un premio. Chi progetta la schermata deve avere in mente che sta eliminando un risponditore automatico, non aggiungendo una videoconferenza.

### 6.2 Tre principi, perché è una funzione secondaria

**Impronta zero sul percorso critico.** La modalità semplificata ha **quattro tasti** e li deve conservare: rispondere, riagganciare, confermare, svincolare. Nessun quinto tasto per Zoom. Nessun pulsante nuovo sulla barra delle azioni di chiamata. Chi fa il centralino otto ore al giorno non deve pagare niente per una funzione che usa una volta al mese.

**Una volta dentro, è una chiamata normale.** Appena la riunione risponde, deve valere tutto quello che l'operatore sa già: attesa, riaggancio, conferenza, toni, trasferimento, con gli stessi tasti. **Nessuna "modalità Zoom".** Se entrare in riunione crea uno stato nuovo con regole sue, la funzione secondaria si è appropriata del prodotto.

**Raggiungibile da dove l'operatore è già.** Non da un menu nuovo in cima all'interfaccia.

### 6.3 Le tre vie di accesso, dalla più economica

**a) La riunione ricorrente sta in rubrica — e questa da sola potrebbe bastare.**

Le riunioni che un centralino serve davvero sono quasi sempre le stesse: la giunta del lunedì, il consiglio, la commissione. Un contatto `Giunta settimanale` con destinazione `88012345678.4321@zoomcrc.com` **riusa tutto**: la ricerca, i preferiti, gli annunci, la composizione rapida. Costo di sviluppo: **zero.**

Un ostacolo però c'è, ed è nel codice di oggi: l'importazione della rubrica considera componibile solo un valore **fatto di cifre, senza lettere** (`TabularContactSource.IsDialable`), e un URI SIP finirebbe fra le note invece che fra i numeri. Serve un'eccezione dichiarata per gli URI SIP — **piccola, ma va fatta di proposito**, perché quel controllo esiste per una ragione buona (impedire che descrizioni e sigle finiscano composte).

**b) Dal campo di composizione, con un comando.**

L'operatore digita l'ID riunione nel campo che usa già, preme il comando *Entra in riunione Zoom*, e BlindPhone chiede il codice e compone. È la via per la riunione occasionale, e non aggiunge nessun elemento all'interfaccia: solo una voce nell'elenco dei comandi riassegnabili, che l'operatore può anche lasciare senza tasto.

**c) Una finestra piccola, come rubrica e ultime chiamate.**

Due campi — ID e codice, entrambi solo numerici e riletti a gruppi — e un pulsante *Entra*. Non modale e presente in ALT+TAB, come le altre tre finestre di lavoro: sono regole che il prodotto ha già stabilito e collaudato.

**Da fare solo se (a) e (b) non bastano**, e non prima di averlo chiesto a un operatore vero. Una finestra in più è una finestra in più da imparare.

### 6.4 Il caso che giustifica l'intera funzione

Un chiamante telefonico ha bisogno di partecipare alla riunione in corso.

L'operatore ha il chiamante in linea, entra nella riunione Zoom e **li mette in conferenza a tre**. Il centralino ha appena fatto da ponte fra la rete telefonica e Zoom — che è esattamente il mestiere di un centralino, e che **con la conferenza a tre BlindPhone sa già fare.**

Questo scenario non richiede codice nuovo oltre alla composizione: è il motivo per cui la funzione va pensata come *una destinazione in più*, e non come un client di videoconferenza in miniatura.

---

## 7. Quello che va detto a voce

Gli annunci non sono rifinitura, qui: sono la funzione. Un ingresso in riunione è **lento** — il gateway risponde, riproduce un messaggio, ammette — e in quel tempo l'unica differenza fra "sta funzionando" e "è fallito" è quello che la voce dice.

### 7.1 Gli stati da annunciare

| Momento | Perché non può restare muto |
|---|---|
| Composizione verso il gateway | Dice che la chiamata è partita e dove sta andando |
| Gateway che risponde | È il primo segnale che la riunione esiste davvero |
| **Sala d'attesa** | Silenzio indistinguibile da una chiamata caduta. Può durare minuti |
| Ammesso in riunione | Il momento in cui l'operatore può parlare |
| **Stato del microfono** | §7.3 |
| Espulso o riunione terminata dall'ospite | Altrimenti l'operatore parla a una linea chiusa |

### 7.2 Gli errori che hanno un nome

Tre fallimenti si distinguono e vanno detti per quello che sono, invece che come "chiamata non riuscita":

- **ID riunione inesistente** — l'errore più probabile, undici cifre digitate a orecchio;
- **codice errato** — quasi sempre perché si è usato quello web invece del numerico (§5.1), e l'annuncio deve dirlo;
- **nessuna licenza CRC disponibile** — le porte sono finite, o l'ospite non ne ha: non è un errore dell'operatore e non deve sembrarlo.

### 7.3 Il muto è il problema serio

In una riunione l'ospite può azzittire i partecipanti. Un operatore non vedente **non ha modo di accorgersene**: parla, e non succede niente. È il difetto peggiore che questa funzione può produrre, perché somiglia a un guasto dell'audio e porta a rimettere mano ai dispositivi durante una riunione.

Zoom governa il muto degli apparati di terze parti con i toni DTMF, e BlindPhone **ha già la modalità toni** — la stessa che serve per i menu automatici, con il tasto per entrarci e Esc per uscirne.

Il punto da chiarire in prova: **il codice di composizione che inizia per 1 abilita la sincronizzazione del muto** (§5), cioè allinea lo stato fra Zoom e l'apparato. Se funziona, BlindPhone può annunciare il muto invece di indovinarlo. **[DA VERIFICARE] quale sequenza DTMF commuta il muto sul CRC e se lo stato torna indietro.** Da provare sulla licenza, insieme al §3.1.

### 7.4 Il codice d'accesso non deve finire negli annunci né nel registro

Questo è il rilievo di riservatezza della funzione, e nasce dalla forma della composizione.

La stringa `123456789.4321@zoomcrc.com` **contiene il codice d'accesso della riunione**, e passa dal campo di composizione. Quindi finisce nel registro delle chiamate, nella ricomposizione dell'ultimo numero, e potenzialmente **letta a voce alta** in un centralino dove ci sono altre persone.

L'archivio è cifrato, quindi il file non è il problema. **Il problema è la voce.** Un codice d'accesso pronunciato in uno stanzone è un codice d'accesso diffuso, e la riunione di una giunta comunale non è una cosa che si lascia aperta.

**Regola da adottare**: il registro e gli annunci portano l'**ID della riunione e il nome se c'è** (`Giunta settimanale`), mai il codice. Il codice resta nella stringa che si compone e non compare in nessuna forma pronunciata o mostrata. È la stessa linea già tenuta per le password SIP nel riepilogo diagnostico, che le esclude e lo dichiara.

---

## 8. Scheda di raccolta dati dal cliente

Da compilare **prima** di quotare. Le prime due righe possono da sole chiudere il discorso.

| Domanda | Perché | Se la risposta è no |
|---|---|---|
| L'ente ha una licenza **CRC** sul proprio account Zoom? | Senza, si entra solo via numero telefonico | Funzione = §3.3, e costa quasi zero |
| Quante **porte** CRC? | Una licenza = una connessione contemporanea | Con una sola porta, due operatori non entrano insieme |
| Le riunioni da servire sono **dell'ente** o di altri? | Su riunioni altrui la licenza è dell'ospite | Se sono di altri, il CRC serve a poco |
| L'account **impone la cifratura** agli apparati di terze parti? | Decide se servono TLS e SRTP | Va acceso e provato (§4.4) |
| Le riunioni usano la **cifratura end-to-end**? | Con E2EE l'accesso telefonico e SIP **decade** | Nessuna delle due strade funziona. Va saputo prima |
| Il **PBX** può instradare verso `zoomcrc.com`? | Evita di aprire il firewall sulla postazione | Aperture per postazione (§5.2) |
| Quali riunioni sono **ricorrenti**? | Diventano contatti in rubrica (§6.3a) | Solo composizione occasionale |
| Esiste una **sala d'attesa** sulle riunioni tipiche? | Cambia gli annunci e i tempi | — |

---

## 9. Quello che non va fatto

- **Un client Zoom.** Niente elenco partecipanti, chat, reazioni, alzate di mano, disposizioni video.
  > **Superato il 23 settembre 2026 dalla richiesta descritta in §11**, che chiede proprio quelle funzioni. Resta valido per la strada CRC, dove non sono comunque ottenibili: la voce non è più una scelta ma un limite del gateway.
- **Registrare BlindPhone al proxy SIP di Zoom** (§2.1): significherebbe lasciare il centralino dell'ente.
- **Un codificatore video**, salvo esplicita richiesta pagata dopo il fallimento della verifica §3.1.
- **Opus** (§4.2).
- **Un tasto in modalità semplificata.** I quattro tasti sono quattro.
- **La Conference Room Connector API REST.** Amministra parchi di sale per conto di un account master: non è il nostro mestiere.

Una cosa che invece ha senso, ma **solo dopo**: l'API Meetings di Zoom può restituire, per le riunioni **dell'account dell'ente**, l'`h323_password` e i numeri di accesso. Servirebbe a presentare all'operatore *le riunioni di oggi* invece di fargli digitare undici cifre — che per chi non vede è un miglioramento concreto. Ma vale solo sulle riunioni proprie, richiede OAuth verso l'account dell'ente, e **non è il primo passo.**

---

## 10. Stima e ordine di lavoro

| # | Attività | Stima | Nota |
|---|---|---|---|
| 0 | **Verifica §3.1** su licenza CRC: il gateway ammette una linea video inattiva? | **mezza giornata** | **Blocca tutto il resto** |
| 1 | Profilo di codec per destinazione, G.722 + G.711 A-law verso il CRC (§4.3) | 1 giorno | Utile comunque, anche fuori da Zoom |
| 2 | Costruzione e validazione della stringa di composizione (§5), solo cifre, punti al posto giusto | 1 giorno | Con i test del caso |
| 3 | Comando *Entra in riunione Zoom* dal campo di composizione (§6.3b) | 1 giorno | |
| 4 | Annunci degli stati e degli errori con un nome (§7.1, §7.2) | 1-2 giorni | È la funzione, non la rifinitura |
| 5 | Codice d'accesso fuori da registro e annunci (§7.4) | mezza giornata | Non rinviabile |
| 6 | Eccezione agli URI SIP nell'importazione rubrica (§6.3a) | mezza giornata | |
| 7 | Cifratura TLS/SRTP provata contro il CRC (§4.4) | 1 giorno | Solo se l'account la impone |
| 8 | Finestra dedicata (§6.3c) | 2 giorni | **Solo se serve davvero** |

**Senza il punto 8 e senza il video: cinque-sette giorni**, condizionati per intero all'esito del punto 0.

È una stima da funzione secondaria, ed è coerente con quello che è: **BlindPhone non impara a fare videoconferenze. Impara a comporre un indirizzo in più, e a raccontare bene che cosa sta succedendo mentre lo fa.**

---

## 11. Se la funzione deve avere video, chat e mano alzata

Richiesta del 23 settembre 2026: telecamera, finestra dedicata con prova di audio e video **prima** di entrare, e in riunione i pulsanti per microfono, telecamera, mano alzata, chat *«e tutto ciò che le API prevedono»*.

### 11.1 La risposta breve: sì, ma non con il Cloud Room Connector

**Il CRC non è un'API: è una destinazione da chiamare** (§2). Quello che dà è un flusso audio, un flusso video e dei toni DTMF. Non ha una nozione di «mano alzata», non trasporta la chat, non espone l'elenco dei partecipanti, e non esiste una prova dei dispositivi perché non c'è nessun programma Zoom in esecuzione da questo lato: c'è una telefonata SIP.

Nessuna quantità di lavoro sul connettore cambia questo. **I pulsanti che hai descritto non sono implementabili sopra il CRC**, punto.

Quello che li ha è un prodotto diverso: il **Meeting SDK**, cioè il motore del client Zoom incorporato dentro la propria applicazione.

> **Conseguenza da mettere in chiaro subito: le due richieste si escludono.**
> Il codec video H.264 ha senso **solo** sulla strada CRC, perché è lo stack SIP di BlindPhone a negoziarlo. Il Meeting SDK **porta il proprio motore mediale**: audio, video e rete sono suoi, e lo stack SIP non viene nemmeno interpellato. Implementare H.264 in SIPSorcery e poi adottare il Meeting SDK significa **buttare quel lavoro**.

### 11.2 Le tre strade, per quello che davvero danno

| | **A — CRC + video** | **B — Meeting SDK Windows** | **C — Meeting SDK Web in WebView2** |
|---|---|---|---|
| Entrare in riunione | ✅ | ✅ | ✅ |
| Audio in banda larga | ✅ G.722 | ✅ motore Zoom | ✅ motore Zoom |
| Video in uscita | ✅ con codificatore H.264 | ✅ | ✅ |
| Microfono e telecamera on/off | ⚠️ solo locale, DTMF incerto | ✅ API | ✅ API |
| **Mano alzata** | ❌ | ✅ `RaiseHand` / `LowerHand` | ✅ |
| **Chat** | ❌ | ✅ `IMeetingChatController` | ✅ |
| **Elenco partecipanti** | ❌ | ✅ | ✅ |
| **Prova audio/video prima di entrare** | ❌ | ✅ | ⚠️ parziale |
| Licenza CRC dell'ente | **richiesta** | non richiesta | non richiesta |
| Funziona su riunioni di altri enti | solo con licenza dell'ospite | ✅ | ✅ |
| **Architettura arm64** | ✅ | ❌ **x86/x64 soltanto** | ✅ |
| Dipendenze native nuove | codificatore H.264 | SDK C++ Zoom + C++/CLI | WebView2 |
| Audio sui dispositivi scelti in BlindPhone | ✅ nativo | ⚠️ via API dell'SDK | ❌ va al motore del browser |
| **Accessibilità sotto il nostro controllo** | ✅ totale | ✅ solo in *custom UI* | ❌ è l'UI web di Zoom |

### 11.3 Quello che il Meeting SDK chiede in cambio

Sono fatti dalla documentazione Zoom, non impressioni.

**Il wrapper C# non è supportato.** Testuale: *«The C# wrapper is provided as a reference, and is not actively supported by Zoom.»* L'SDK vero è **C++**. Il wrapper può **non esporre** interfacce presenti nel nativo, e aggiungerle significa mettere le mani nel C++/CLI dell'involucro (`meeting_video_dotnet_wrap.cpp` e simili) e ricompilarlo. Su un prodotto che deve restare manutenibile da una persona sola, è una dipendenza con un costo ricorrente.

**Il wrapper targetta .NET Framework** (4.7.2 negli esempi). BlindPhone sta su **.NET 10**. Il ponte si può ricostruire, ma non è «aggiungere un pacchetto NuGet»: è ricompilare C++/CLI contro un runtime diverso, e sul forum ufficiale la domanda «supporta .NET 6/7?» è stata **chiusa senza risposta**.

**Niente arm64.** Il pacchetto dell'SDK contiene x64 o x86. BlindPhone **costruisce già l'installatore anche per arm64** — è una cosa fatta apposta, nel registro dei commit. Con l'SDK nativo, o quella banda perde la funzione, o si smette di costruirla.

**Custom UI è tutto o niente.** Testuale: *«if something is customizable, you must customize it»*. E la *default UI* è l'interfaccia del client Zoom incorporata nella nostra finestra.

**Credenziali.** Serve un'applicazione SDK sul Marketplace Zoom, un **JWT** per autenticarla, e — per entrare in riunioni fuori dall'account dello sviluppatore — un token **ZAK o OBF**. Quest'ultimo punto va verificato per il caso che ci interessa (entrare come ospite in una riunione altrui con ID e codice): **[DA VERIFICARE]**, e se la risposta fosse che serve un token legato a un utente Zoom, cambierebbe chi deve avere un account.

### 11.4 Il punto che decide, e non è tecnico

BlindPhone fa una promessa precisa: **ogni transizione di stato produce un annuncio**, e c'è un test in integrazione continua che fa fallire la build se una ne resta muta. La riga braille rispecchia gli annunci. La modalità semplificata ha quattro tasti. Niente si muove senza dirlo.

**Dentro un'interfaccia Zoom incorporata, niente di tutto questo vale.** Gli annunci non arrivano, il gate di copertura non guarda lì, la riga braille tace, e l'operatore passa da un programma costruito per lui a una superficie che non è stata verificata con NVDA e JAWS — e di cui Zoom non dichiara l'accessibilità da nessuna parte.

Per un prodotto che esiste **perché** i softphone ordinari non sono usabili da chi non vede, incorporare un'interfaccia di terze parti non collaudata è la decisione più delicata dell'intero progetto. Non è un veto: è la ragione per cui, se si va sul Meeting SDK, **si va in *custom UI***.

E la *custom UI* è esattamente quello che la richiesta descrive. *«L'utente deve avere i pulsanti a disposizione»* — **i nostri pulsanti**, nella nostra finestra, con i nostri annunci e i nostri tasti rapidi, sopra le API di Zoom. Non l'interfaccia di Zoom. La richiesta e l'architettura giusta coincidono; costa solo di più.

### 11.5 La prova dei dispositivi prima di entrare

Questa parte è **buona a prescindere dalla strada**, ed è la più accessibile delle richieste: sentirsi dire *«microfono: ti sento, livello buono; altoparlanti: hai sentito il segnale?»* prima di entrare in una riunione con venti persone è precisamente ciò che evita i due minuti di «non si sente niente» in cui l'operatore non vedente non ha modo di capire da che parte sia il problema.

E in buona parte **BlindPhone ce l'ha già**: sceglie i dispositivi, conosce i livelli, ha i contatori del percorso audio nel riepilogo diagnostico. Un pannello di prova che riusa quella roba è utile anche fuori da Zoom — all'inizio di ogni turno.

**Si può fare subito, su qualunque strada.** Se la decisione sull'architettura richiede tempo, questo è il pezzo che intanto non si spreca.

### 11.6 Stima delle tre strade

| | **A — CRC + video** | **B — Meeting SDK custom UI** | **C — Web SDK in WebView2** |
|---|---|---|---|
| Copre la richiesta | **no** (niente chat né mano alzata) | **sì** | sì, ma con l'UI di Zoom |
| Sviluppo | 2-3 settimane | **8-12 settimane** | 3-4 settimane |
| Dipendenze nuove | codificatore H.264 | SDK C++, C++/CLI, ponte .NET 10 | WebView2 |
| Costo ricorrente | nessuno | **wrapper non supportato da Zoom** | Zoom cambia la UI web quando vuole |
| Perdita arm64 | no | **sì** | no |
| Accessibilità | nostra | nostra (custom UI) | **non nostra** |
| Licenza CRC | **sì** | no | no |

Le stime di B e C sono **larghe di proposito**: su B pesa il ponte verso .NET 10, che nessuno ha documentato; su C pesa il fatto che l'accessibilità non dipende da noi, quindi il tempo vero è quello della prova con NVDA e JAWS, non quello dello sviluppo.

### 11.8 Esito: scartata

**La strada B è stata chiusa il 23 settembre 2026 per il costo delle licenze**, e il conto che l'ha decisa è la somma di tre voci, non di una:

- l'**applicazione pubblicata** sul Marketplace, obbligatoria per entrare in riunioni di altri enti (§12.2-bis), con revisione e audit di sicurezza;
- gli **obblighi che la pubblicazione porta con sé** — termini d'uso, informativa, documentazione, canale di assistenza — che sono impegni ricorrenti, non una pratica una tantum;
- il fatto che tutto questo peserebbe su una **funzione secondaria**, usata una volta al mese.

La decisione è coerente con l'analisi delle §1-10: per un operatore non vedente il valore non era mai la chat o l'elenco dei partecipanti, **era togliere di mezzo il risponditore automatico** (§6.1). Quello la strada SIP lo fa, e costa una frazione.

**Quello che si rinuncia ad avere**, e va detto a chi comprerà il prodotto invece di lasciarlo scoprire: chat, mano alzata, elenco dei partecipanti, prova dei dispositivi dentro Zoom, e il comando del microfono dal lato Zoom. In riunione l'operatore ha l'audio e i comandi del proprio telefono.

### 11.7 Raccomandazione (superata dalla §11.8)

**Strada B, in custom UI**, se la funzione deve avere chat e mano alzata come da richiesta. È l'unica che copre l'elenco mantenendo le promesse di accessibilità del prodotto, e i tre prezzi — wrapper non supportato, ponte verso .NET 10, perdita di arm64 — vanno messi sul tavolo **prima** di cominciare, perché nessuno dei tre si scopre a metà strada senza far saltare la pianificazione.

Se invece la funzione deve restare secondaria, la strada A con la sola aggiunta del video resta quella delle §1-10, e la richiesta va ridotta a quello che il CRC sa fare: entrare, parlare, vedere ed essere visti.

**Da non fare in nessun caso: cominciare dal codec video H.264.** Su B e su C non serve, e sarebbe la prima cosa buttata.

---

## 12. Stato dell'implementazione

Decisione presa il 23 settembre 2026: **solo la strada SIP.** Il codice scritto per la strada Meeting SDK è stato rimosso — non tenuto da parte — perché un'astrazione con cinque comandi di cui quattro rispondono sempre «non disponibile» è peso, non preveggenza.

### 12.1 Fatto e collaudato

Tutto quello che si poteva completare **senza una licenza CRC**, con **54 prove verdi** e zero avvisi. La funzione è usabile: si entra in riunione, si parla, si riattacca.

| File | Che cosa fa |
|---|---|
| `Core/Services/ZoomRoomConnector.cs` | Costruisce e convalida la composizione, separa identificativo e codice, riconosce un indirizzo di riunione, toglie il codice d'accesso, sceglie i codec |
| `Core/Services/CallSessionManager.cs` | La controparte annunciata e conservata è quella **senza codice**, e senza nome in rubrica diventa «riunione Zoom *numero*» |
| `App/MainWindow.xaml.cs` | Il comando *Entra in riunione Zoom*, su **Alt Z** |
| `Core/Services/HelpBook.cs` | Argomento «Entrare in una riunione Zoom» |
| `Telephony/SipTelephonyEngine.cs` | Passa la destinazione alla sessione media, così i codec seguono la chiamata |
| `Contacts/SqlBackup/TabularContactSource.cs` | Accetta gli indirizzi SIP fra i recapiti componibili |

**Come si usa.** Si scrive l'identificativo nel campo di composizione e si preme **Alt Z**; il codice d'accesso, se c'è, dopo un punto — `812345678.4321`. Gli spazi dell'invito non danno fastidio, e lo spazio **non** separa: `812 3456 7890` è tutto identificativo. Un indirizzo intero (`…@zoomcrc.com`), come arriva da un contatto in rubrica, viene composto così com'è.

Il tasto è locale e non globale: serve il campo di composizione, che esiste solo nella finestra principale. La modalità semplificata resta a quattro tasti e la barra delle azioni di chiamata non cambia.

Le tre decisioni che il codice incorpora, da rileggere prima di cambiarle:

**Verso il gateway si offrono G.722 e G.711 A-law, e basta.** La preferenza predefinita del prodotto mette G.729 per primo — giusto per i centralini italiani, sbagliato qui, perché Zoom non lo elenca. Su una postazione dove l'amministratore ha ristretto l'elenco al solo G.729 la chiamata **non si instaurerebbe affatto**, senza spiegazioni. Su ogni altra destinazione la preferenza configurata non viene toccata.

**Il codice d'accesso non entra in quello che si pronuncia e si conserva.** Viaggia dentro il numero composto, quindi finirebbe nel registro, nella ricomposizione e — il punto — letto a voce alta in uno stanzone. L'archivio è cifrato e il file non è il problema: il problema è la voce.

**Un valore con la chiocciola è componibile solo se è un indirizzo SIP intero.** La deroga serve a tenere in rubrica le riunioni ricorrenti come contatti normali; la stretta che la accompagna chiude un buco che era già lì, perché `812345678@` passava il controllo delle lettere ed era accettato.

### 12.2 Che cos'era «l'applicazione SDK», e i due ostacoli che hanno chiuso quella strada

Non è software da scaricare: è una **registrazione** che si crea su `marketplace.zoom.us` (Develop → Build App; il tipo *Meeting SDK* è confluito nel **General App**). Produce due credenziali — **Client ID** e **Client Secret** — che stanno sotto *App Credentials*. La vecchia coppia *SDK Key/Secret* è deprecata e la migrazione **è stata resa obbligatoria il 27 giugno 2026**: oggi esistono solo Client ID e Client Secret.

Il Client Secret **firma un JWT** che l'SDK presenta al momento di entrare in riunione. Da qui i due ostacoli.

**Primo: per entrare in riunioni ospitate da altri enti l'applicazione deve essere pubblicata.** Un'applicazione in sviluppo funziona **solo dentro l'account Zoom di chi l'ha creata**. La documentazione Zoom sulla distribuzione è esplicita: per raggiungere account diversi dal proprio l'applicazione va pubblicata sul Marketplace, e la pubblicazione passa da una revisione di Marketplace Operations **più un audit di sicurezza**, con obbligo di Terms of Use, informativa privacy, documentazione e canale di assistenza.

> **È la risposta alla domanda rimasta aperta in §11.3**, ed è peggiore di quella sperata. Il caso d'uso di BlindPhone è un centralinista che entra in riunioni **di altri enti**: quello è esattamente il caso che richiede la pubblicazione. **[DA VERIFICARE]** se la forma *unlisted* — non elencata pubblicamente ma approvata — basti allo scopo: sarebbe la via più leggera, e la documentazione pubblica non è conclusiva.

**Secondo: il Client Secret non può viaggiare dentro l'installatore.** Un segreto dentro un programma installato su decine di postazioni è un segreto estratto il giorno in cui qualcuno ci prova, e qui aprirebbe l'applicazione Zoom dell'ente. Le due strade:

| | **Segreto per ente, nel deposito protetto** | **Servizio di firma gestito da noi** |
|---|---|---|
| Come funziona | Ogni ente crea la propria applicazione; il segreto sta in DPAPI come la password SQL | Un nostro server firma i JWT; la postazione chiede la firma |
| Segreto nell'installatore | no | no |
| Riunioni di altri enti | **richiede comunque la pubblicazione** | richiede comunque la pubblicazione |
| Conseguenza | Ogni ente deve creare e mantenere un'applicazione Marketplace | **BlindPhone smette di essere un prodotto che parla solo con il centralino dell'ente** |

La seconda strada va guardata con attenzione prima di sceglierla: oggi il prodotto non chiama nessun servizio esterno gestito da noi, e nella PA questo è un argomento di vendita, non un dettaglio. Introdurre una dipendenza da un nostro server cambia l'analisi dei rischi che l'ente deve fare, e va detto in gara.

### 12.3 Da fare

| # | Attività | Dipende da |
|---|---|---|
| 1 | Linea video H.264 nella descrizione di sessione | **verifica §3.1, serve la licenza** |
| 2 | Cifratura TLS e SRTP provata contro il gateway | licenza + account che la impone |
| 3 | Gateway regionale come impostazione | — |

Sul punto 3: oggi la composizione costruita dal comando va sempre a `zoomcrc.com`. Gli enti che devono passare da un nodo regionale possono comunque farlo scrivendo l'indirizzo intero, o tenendolo in rubrica — ma su quel gateway **la ripulitura del codice d'accesso non scatta**, perché il programma non sa che quell'indirizzo è un gateway Zoom. Finché il gateway non è un'impostazione, chi usa un nodo regionale va avvisato.

### 12.4 Il video, e perché non l'ho fatto alla cieca

Zoom dichiara che il gateway **non ammette chiamate solo audio** (§3). La via economica è dichiarare in SDP una linea video H.264 `inactive`, senza codificare niente — nessuna dipendenza nativa, solo negoziazione.

**Che il gateway la accetti non è deducibile dalla documentazione**, e il posto dove va scritta quella riga è la costruzione della sessione media: il codice che porta **ogni telefonata del prodotto**, non solo le riunioni. Toccarlo per tentativi, senza poter verificare l'interoperabilità con il gateway vero, mette a rischio il percorso critico per una funzione secondaria.

Si risolve in mezza giornata con una licenza CRC e una cattura di pacchetti. Se la risposta è no, resta la via del numero telefonico (§3.3), che funziona già oggi senza scrivere niente — e allora questa funzione è tutta qui.

### 12.5 Quello che questa strada non darà mai

Da dire a chi comprerà il prodotto, non da lasciar scoprire in riunione: **niente chat, niente mano alzata, niente elenco dei partecipanti, niente comando del microfono dal lato Zoom.** Il gateway trasporta audio e video e riceve toni; tutto il resto è dell'applicazione Zoom, che qui non c'è.

In riunione l'operatore ha l'audio e **i comandi del proprio telefono** — attesa, conferenza, toni, riaggancio — con i tasti che già conosce. Che è anche il pregio di questa strada: non c'è una modalità nuova da imparare.

---

## Fonti

- [SIP/H.323 Room Connector Dial Strings](https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0065727) — formato della stringa di composizione
- [Conference Room Connector supported devices](https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0061702) — codec audio e video, requisito TLS 1.2
- [Using the Conference Room Connector](https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0060661) — modalità di ingresso, assenza del solo audio
- [Network Firewall Settings for CRC](https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0065748) — porte e intervalli di indirizzi
- [Requiring media encryption for SIP/H.323](https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0065781) — imposizione della cifratura mediale
- [Zoom Conference Room Connector (CRC) — Developer Docs](https://developers.zoom.us/docs/conference-room-connector/) e [CRC API](https://developers.zoom.us/docs/api/rest/crc-api/) — ambito dell'API REST
- [Web Controls and Dialing Procedures — Zoom Technical Library](https://library.zoom.com/advanced-enterprise-services/conference-room-connector/conference-room-connector-explainer/web-controls-and-dialing-procedures) — codice di abbinamento, controlli DTMF
- [Meetings APIs — Zoom Developer Docs](https://developers.zoom.us/docs/api/meetings/) — `h323_password`, numeri di accesso

Per la §11:

- [Zoom Meeting SDK for Windows](https://developers.zoom.us/docs/meeting-sdk/windows/) — default UI e custom UI, «tutto o niente»
- [Meeting SDK for Windows: download e prerequisiti](https://developers.zoom.us/docs/meeting-sdk/windows/get-started/download/) — architetture x86/x64, JWT, token ZAK/OBF
- [Meeting SDK for Windows: wrapper C#](https://developers.zoom.us/docs/meeting-sdk/windows/get-started/c-sharp-wrapper/) — «not actively supported by Zoom», .NET Framework
- [IMeetingParticipantsController](https://marketplacefront.zoom.us/sdk/meeting/windows/class_i_meeting_participants_controller.html) — `RaiseHand`, `LowerHand`
- [Zoom Meeting SDK for web — Component View](https://developers.zoom.us/docs/meeting-sdk/web/component-view/) — strada C
