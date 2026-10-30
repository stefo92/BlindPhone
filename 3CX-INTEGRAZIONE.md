# BlindPhone — Progetto di dettaglio del connettore 3CX V20

**Documento tecnico complementare a [ANALISI-BLINDPHONE.md](ANALISI-BLINDPHONE.md), sezione 9, e parallelo a [YEASTAR-INTEGRAZIONE.md](YEASTAR-INTEGRAZIONE.md) e [CISCO-INTEGRAZIONE.md](CISCO-INTEGRAZIONE.md)**

| | |
|---|---|
| **Versione** | 1.0 — 5 settembre 2026 |
| **Ambito** | Implementazione di `IQueueProvider`, `IPresenceSource`, `IContactSource` e controllo chiamata ausiliario sulla **Call Control API** e sulla **Configuration API (XAPI)** di 3CX V20 |
| **Stato dei dati** | Ricavati dalla documentazione pubblica 3CX (3cx.com/docs, settembre 2026) e dal forum ufficiale. **Nessuna verifica su PBX reale.** I punti marcati **[DA VERIFICARE]** vanno confermati su un'istanza di prova prima della stima definitiva |
| **Documenti sorgente** | *Call Control API Configuration Guide* (aggiornato 13 maggio 2025) · *Call Control API Endpoint Specification* (aggiornato 19 marzo 2025) · *How to Use 3CX Configuration API* (aggiornato **23 aprile 2026**) |

---

## 1. Premessa: 3CX è il candidato migliore dopo Yeastar

L'analisi generale (§9.3) colloca 3CX fra i clienti del connettore **REST generico**, priorità 5, cioè in coda. **È una collocazione da rivedere.**

Con la V20 3CX ha pubblicato una **Call Control API REST + WebSocket** che ha la stessa forma di quella Yeastar, e in un punto è persino migliore: il canale WebSocket non serve solo a ricevere eventi, **accetta anche richieste**, quindi un'unica connessione copre interrogazione e comandi.

Soprattutto: **la filosofia dell'API coincide con l'architettura che BlindPhone ha già scelto.** 3CX dichiara esplicitamente che rispondere e mettere in attesa sono operazioni dell'*endpoint* e non del server — cioè esattamente la divisione di responsabilità del modello ibrido adottato per Yeastar ([YEASTAR-INTEGRAZIONE.md §3.1](YEASTAR-INTEGRAZIONE.md)): SIP nativo per il percorso critico, API per ciò che il SIP non può dare. Su Yeastar quella divisione è stata una decisione di progetto; su 3CX è imposta dall'API, e coincide.

Il prezzo da pagare è uno solo, ma è alto: **la licenza** (§3).

---

## 2. Le due API della V20

Sono due prodotti distinti, con due checkbox distinte nello stesso pannello e due scopi diversi.

| | **Call Control API** | **Configuration API (XAPI)** |
|---|---|---|
| A che serve | Stato delle chiamate in tempo reale, azioni sui partecipanti, chiamata uscente, streaming audio | Configurazione: utenti, gruppi, contatti, code, routing — quello che si fa dal pannello di amministrazione |
| Protocollo | REST + **WebSocket bidirezionale** | REST su **OData**, specifica OpenAPI |
| Percorso | `/callcontrol/...` | `/xapi/v1/...` |
| Specifica macchina | — | `https://<fqdn>/xapi/v1/swagger.yaml` |
| Licenza dichiarata | **8SC+ Enterprise** | **8SC+ AI** |
| Serve a BlindPhone per | Coda, presenza, trasferimento di riserva, chiamata da rubrica | Rubrica aziendale, elenco delle code, anagrafica degli interni |

> **[DA VERIFICARE] — la nomenclatura delle licenze è cambiata fra i due documenti.** La guida della Call Control API (maggio 2025) dice *Enterprise*; quella della Configuration API (aprile 2026) dice *AI*. 3CX ha rinominato i piani nel frattempo. Con ogni probabilità **oggi entrambe richiedono il piano di vertice**, ma va confermato sul listino corrente prima di quotare: è la voce che può da sola bloccare il progetto.

---

## 3. Prerequisiti e vincoli di piattaforma

Da verificare **in fase pre-vendita**, con checklist da consegnare all'IT del cliente.

| Requisito | Valore | Impatto se non soddisfatto |
|---|---|---|
| **Versione** | 3CX **V20** o successiva | Le V16/V18 avevano una Call Control API diversa, basata su assembly .NET: connettore differente, non riusabile |
| **Piano di licenza** | **8SC o superiore, piano di vertice** (Enterprise/AI secondo la nomenclatura corrente) | **Blocco totale.** Il piano Pro non include la Call Control API |
| **Simultaneous Calls** | Almeno 8 | Le licenze da 4SC sono escluse dall'API |
| **Applicazione API creata** | Pannello di amministrazione → **Integrations → API → Add**, con spunta *3CX Call Control API Access* | Configurazione, non ostacolo |
| **Client ID** | È il **DN del route point** attraverso cui l'applicazione accede | Va concordato con l'amministratore: è un numero interno dedicato |
| **Interni da sorvegliare** | Dichiarati nella stessa schermata: *"additional extensions you want to monitor using the API"* | **Senza, niente presenza degli interni** (§9) |
| **Interno SIP per l'operatore** | Un'estensione registrabile da client di terze parti | Vedi §6.3 |
| **Rete** | HTTPS e WSS verso il PBX dalla postazione | Regola firewall |

> **La chiave API viene mostrata una volta sola** alla creazione dell'applicazione. Va depositata subito nel deposito protetto di Windows, mai in `settings.json`, come già si fa per la password di SQL Server e per il Client Secret Yeastar.

---

## 4. Autenticazione

Uguale per le due API, con lo stesso pannello e lo stesso endpoint di token.

```
POST https://<fqdn>/connect/token
Content-Type: application/x-www-form-urlencoded

client_id=<DN del route point>&client_secret=<chiave API>&grant_type=client_credentials
```

Il token torna come Bearer da mettere nell'intestazione:

```
Authorization: Bearer <access_token>
```

| Vincolo documentato | Implementazione in BlindPhone |
|---|---|
| **Token valido 60 minuti** | Rinnovo proattivo a **50 minuti**, mai al primo 401. È il doppio della finestra Yeastar (30 min), quindi la logica esistente si riusa allentando il timer |
| Nessun refresh token documentato | Si rifà `client_credentials` a ogni rinnovo: più semplice del doppio token Yeastar |
| Nessun limite di token simultanei documentato | **[DA VERIFICARE]**: Yeastar ne ammette 8 per applicazione, e quel limite ha imposto un Client ID per postazione. Se 3CX ha un limite analogo va scoperto prima, non alla nona postazione |
| Prova rapida della connessione | `GET /xapi/v1/Defs?$select=Id` → 200 OK, e le intestazioni riportano la versione del sistema. **Utile per la diagnostica**: è il test da mettere nel pulsante "Verifica collegamento" |

---

## 5. La Call Control API in dettaglio

### 5.1 Il modello dei dati: DN, device, participant

Tre concetti, e vanno capiti prima degli endpoint perché l'API ci gira intorno.

- **DN** (*Direct Number*) — l'entità numerata del PBX: un interno, una coda, un ring group, un route point. È l'unità di monitoraggio.
- **Device** — un apparecchio registrato su un DN: `dn`, `device_id`, `user_agent`. Un DN può averne più di uno.
- **Participant** — una gamba di chiamata vista da un DN. È l'oggetto che porta l'informazione utile:

| Campo | Contenuto | Uso in BlindPhone |
|---|---|---|
| `id` | Identificativo del partecipante nel DN | Bersaglio delle azioni |
| `status` | `Dialing` · `Ringing` · `Connected` **[DA VERIFICARE]** i valori esatti nella V20 | Stato della chiamata; base della presenza |
| `dn` | Il DN a cui il partecipante appartiene | Correlazione |
| `party_caller_name` | Nome della controparte | **Nome annunciato**; se vuoto, risoluzione via rubrica |
| `party_caller_id` | Numero della controparte | **Numero letto cifra per cifra** |
| `party_dn` / `party_dn_type` | DN e tipo della controparte | Distingue interno / esterno / coda |
| `party_did` | DID chiamato | Su quale numero pubblico è entrata la chiamata |
| `device_id` | Apparecchio coinvolto | Correlazione con la registrazione SIP |
| `direct_control` | L'applicazione può comandare direttamente questo partecipante | **Decide se `answer` è ammesso** (§6) |
| `originated_by_dn` / `_type` | Chi ha originato | Ricostruzione del percorso |
| `referred_by_dn` / `_type` | Chi ha trasferito | *"Trasferita da Bianchi"* |
| `on_behalf_of_dn` / `_type` | Per conto di chi | Segreteria che chiama per il dirigente |
| `callid` / `legid` | Identificativi di chiamata e gamba | **Chiave di correlazione con il mondo SIP** (§12) |

> `referred_by_dn` e `on_behalf_of_dn` non hanno equivalente nell'API Yeastar e valgono più di quanto sembri: permettono all'annuncio di dire *da chi* arriva una chiamata trasferita, che per un operatore non vedente è il contesto che altrimenti va chiesto al chiamante.

### 5.2 Gli endpoint

| Endpoint | Verbo | Che cosa fa |
|---|---|---|
| `/callcontrol` | GET | Stato di **tutti** i DN visibili all'applicazione, con device e participant |
| `/callcontrol/ws` | GET | **Canale WebSocket** — eventi e richieste (§5.4) |
| `/callcontrol/{dn}` | GET | Stato di un DN |
| `/callcontrol/{dn}/devices` | GET | Apparecchi registrati sul DN |
| `/callcontrol/{dn}/devices/{deviceid}` | GET | Un apparecchio |
| `/callcontrol/{dn}/participants` | GET | **Gambe di chiamata del DN** — vedi §7 |
| `/callcontrol/{dn}/participants/{id}` | GET | Una gamba |
| `/callcontrol/{dn}/makecall` | POST | Chiamata dal DN. **Sconsigliata dalla documentazione stessa** se il DN ha più apparecchi: usa il servizio legacy e l'esito va ricostruito osservando i partecipanti |
| `/callcontrol/{dn}/devices/{deviceid}/makecall` | POST | **Forma da usare**: chiamata da un apparecchio preciso, con il partecipante iniziatore garantito nella risposta |
| `/callcontrol/{dn}/participants/{id}/stream` | GET / POST | Audio grezzo del partecipante, PCM 16 bit 8 kHz mono (§5.5) |
| `/callcontrol/{dn}/participants/{id}/{action}` | POST | **Le azioni** (§5.3) |

Corpo di `makecall`:

```json
{ "destination": "2094", "timeout": 30, "attacheddata": { "additionalProp1": "..." } }
```

### 5.3 Le azioni sui partecipanti

`POST /callcontrol/{dn}/participants/{id}/{action}` con corpo `{ reason, destination, timeout, attacheddata }`.

| Azione | Semantica documentata | Uso in BlindPhone |
|---|---|---|
| `drop` | Abbandona la partecipazione alla chiamata | **Riaggancio di riserva** se il BYE locale fallisce |
| `answer` | **Solo** su partecipanti con `direct_control` (uaCSTA) o su un RoutePoint | Vedi §6: non è la strada per il posto operatore |
| `divert` | Sostituisce un partecipante **che sta squillando** con una nuova destinazione. Fallisce se è già connesso | **Rifiuto con deviazione**: *"non rispondo, manda a…"* |
| `routeto` | Aggiunge destinazioni alternative a un partecipante **squillante o connesso**, che resta in chiamata finché la nuova destinazione non risponde | **Il prelievo dalla coda** — vedi §7.2 |
| `transferto` | Solo su partecipante **connesso**. L'altra parte va in attesa per il tempo del tentativo; se fallisce torna in chiamata | **Trasferimento di riserva** quando il `REFER` SIP viene rifiutato |
| `attach_participant_data` / `attach_party_data` | Attacca dati osservabili dagli altri componenti del PBX | Marcatura delle chiamate gestite da BlindPhone, utile in reportistica |

**Il campo `reason`** accetta valori dichiarati — `None`, `NoAnswer`, `PhoneBusy`, `PhoneNotRegistered`, `ForwardAll`, `BasedOnCallerID`, `BasedOnDID`, `OutOfOfficeHours`, `BreakTime`, `Holiday`, `OfficeHours`, `NoDestinations`, `Polling`, `CallbackRequested`, `Callback` — ed è **richiesto per la corretta visualizzazione nei report di storico chiamate**. Non è decorativo: se BlindPhone devia una chiamata senza dichiarare il motivo, quella chiamata risulta male nella reportistica del servizio, e chi guarda i numeri del centralino se ne accorge.

> **Attenzione al nome del campo.** Il `reason` della *richiesta* è il motivo della deviazione; il `reason` della *risposta* è il motivo del fallimento. Sono due cose diverse con lo stesso nome, e la documentazione lo segnala esplicitamente. Da tenere separati nei modelli, o si finisce per annunciare all'operatore un motivo di errore che è in realtà una causale di instradamento.

### 5.4 Il canale WebSocket

`GET /callcontrol/ws`, autenticato. È **bidirezionale**, e questa è la differenza principale con Yeastar.

**Richieste dell'applicazione** (`WebSocketRequest`):

| Campo | Contenuto |
|---|---|
| `RequestID` | Identificativo scelto dall'applicazione, per appaiare la risposta |
| `Path` | Lo stesso percorso della REST: `/callcontrol`, `/callcontrol/{dn}`, `/callcontrol/{dn}/participants/{id}` |
| `RequestData` | Obbligatorio per `makecall` e `divert`, facoltativo per le GET |

**Risposte e eventi** (`ExternalCallFlowAppHookEvent`):

```
{ EventType, Entity = <percorso richiesto>, AttachedData = <WebSocketResponse> }
```

| `EventType` | Significato | Uso |
|---|---|---|
| **0** | `Upsert` — entità aggiunta o aggiornata | Motore principale: nuova chiamata, cambio di stato |
| **1** | `Remove` — entità rimossa | Chiamata terminata, chiamante uscito dalla coda |
| **2** | `DTMFstring` — cifre dalla controparte. **Solo per le chiamate dell'applicazione stessa** | Non serve al posto operatore, serve agli IVR |
| **4** | `Response` — risposta a una richiesta inviata sul WebSocket | Appaiamento con `RequestID` |

> **[DA VERIFICARE] — la documentazione è incoerente su questo punto.** Il testo introduce il campo come `EventType=5` e nell'esempio scrive `EventType=0`, mentre la tabella elenca 0, 1, 2, 4 — **il valore 3 manca**. Prima di scrivere il deserializzatore va accertato l'insieme reale dei valori, e il modello va scritto per **tollerare un enum sconosciuto** invece di sollevare: un evento non previsto non deve far cadere il canale.

**Vincoli operativi da assumere finché non sono smentiti:**

- limite alla dimensione dei messaggi WebSocket, dichiarato dalla documentazione ma non quantificato → **[DA VERIFICARE]**: una `GET /callcontrol` su un PBX con molti DN potrebbe non passare dal WebSocket e richiedere la REST;
- nessun timeout di inattività documentato → si applica comunque **heartbeat e riconnessione con backoff**, come per Yeastar: costa poco e copre il caso non documentato;
- **alla riconnessione, rilettura completa dello stato**: gli eventi persi non vengono recapitati.

### 5.5 Lo streaming audio — utile, ma non per il posto operatore

`GET` e `POST` su `/callcontrol/{dn}/participants/{id}/stream` scambiano audio grezzo PCM 16 bit 8 kHz mono a circa 128 kbps.

**Non serve a BlindPhone** e non va usato: 8 kHz è banda stretta, mentre EN 301 549 §6.1 chiede la banda larga, e l'audio di BlindPhone passa già per il proprio motore con G.722. Va citato solo per completezza, e per dire perché è stato scartato — altrimenti qualcuno lo riscoprirà fra un anno pensando che sia una scorciatoia.

---

## 6. Il limite che conta: risposta e attesa non passano dall'API

### 6.1 Il fatto

L'elenco delle azioni **non contiene `hold` né `unhold` né `park`**, e `answer` è ammesso solo sui partecipanti con `direct_control` (cioè apparecchi che parlano uaCSTA) o sui RoutePoint.

La posizione di 3CX, riportata sul forum ufficiale, è che **rispondere e mettere in attesa sono operazioni dell'endpoint, non del server**: rispondere richiede di negoziare codec e porte, e quella negoziazione avviene sull'apparecchio.

### 6.2 Perché per BlindPhone non è un problema

Perché BlindPhone **è** l'endpoint. Il modello ibrido già adottato per Yeastar prevede:

- **SIP nativo** per rispondi, riaggancia, attesa, riprendi, DTMF, alternanza, audio — le azioni su cui l'operatore preme un tasto e si aspetta un riscontro in decine di millisecondi;
- **API** per ciò che il SIP non può dare — coda, presenza, rubrica, stato agente.

Su Yeastar quella divisione era una scelta motivata dalla latenza (~150–400 ms per un giro HTTPS). Su 3CX **è l'unica divisione possibile**, e coincide con quella già implementata. Il codice del percorso critico non cambia.

Va però scritto nero su bianco nella documentazione commerciale: **su 3CX non esiste il "modello B" puro** — un'applicazione che comanda un telefono altrui senza terminare l'audio — a meno che gli apparecchi non supportino uaCSTA. È l'opposto della situazione Cisco descritta in [CISCO-INTEGRAZIONE.md §3](CISCO-INTEGRAZIONE.md), dove il modello a controllo remoto è quello canonico.

### 6.3 Registrazione dell'interno operatore

3CX accetta client SIP di terze parti, ma con attenzioni di configurazione documentate dal forum:

- l'estensione va abilitata a registrarsi **fuori dal tunnel 3CX** (opzione da togliere);
- verificare che l'indirizzo della postazione non finisca nella **lista nera IP** del PBX dopo qualche tentativo fallito;
- su **3CX Hosted** non è possibile registrare apparecchi via STUN.

> **[DA VERIFICARE] con priorità alta**, perché è bloccante: la registrazione di BlindPhone come estensione, e il comportamento del PBX rispetto al `REFER` inviato da un client di terze parti. Se il `REFER` viene rifiutato, il trasferimento ricade su `transferto` (§5.3), che è la rete di sicurezza già prevista.

---

## 7. La coda: l'ipotesi da verificare per prima

### 7.1 Il fatto e l'ipotesi

**La Call Control API non ha endpoint di coda.** Non esiste un `queue/call_status` come su Yeastar.

Ma in 3CX **una coda è un DN**, e `GET /callcontrol/{dn}/participants` restituisce le gambe di chiamata di un DN. L'ipotesi è quindi:

> **I chiamanti in attesa in una coda sono i `participants` del DN della coda, con `status` di squillo o attesa.**

L'ipotesi non è campata in aria: nella vecchia API .NET della V15 il modo documentato di contare i chiamanti in coda era esattamente `GetDNByNumber(queueExtension).GetActiveConnections().Length`. La struttura concettuale è la stessa; cambia l'involucro.

Se l'ipotesi regge, si ottiene il **livello A** dell'analisi §9.1, con la mappatura:

| Campo 3CX | Campo `QueuedCaller` | Uso |
|---|---|---|
| numero di `participants` in attesa | conteggio | **Risposta al tasto `F12`**: *"3 chiamate in coda"* |
| `party_caller_name` | `CallerName` | Nome annunciato |
| `party_caller_id` | `CallerNumber` | Numero letto cifra per cifra |
| `id` + `callid` | `Id` | Chiave e bersaglio delle azioni |
| `party_did` | — | Su quale numero pubblico è entrata |
| ordine dell'array | `Position` | **[DA VERIFICARE]** che rispecchi l'ordine di servizio |
| — | `EnqueuedAt` | **Non c'è un tempo di attesa.** Si ricava lato client dal primo istante in cui il partecipante è comparso: preciso solo se BlindPhone era collegato quando è entrato |

> **Il tempo di attesa è il punto debole.** Yeastar lo fornisce (`duration`); 3CX no. Calcolarlo lato client significa che, riavviando BlindPhone con cinque chiamate già in coda, quelle cinque risultano appena arrivate. **La soluzione onesta è non annunciare un tempo che non si conosce**: per i chiamanti già presenti al collegamento si dice *"in attesa da almeno un minuto"* oppure non si dice nulla, mai un numero inventato. È la stessa regola per cui la posizione non si annuncia finché `WaitingListIsOrdered` non è confermato.

### 7.2 Il prelievo selettivo — qui 3CX sembra più forte di Yeastar

Su Yeastar il prelievo di una chiamata specifica dalla coda è rimasto un punto aperto ([YEASTAR-INTEGRAZIONE.md §8.1](YEASTAR-INTEGRAZIONE.md)), senza endpoint dedicato.

Su 3CX c'è un candidato preciso: **`routeto`**, documentato come *"aggiunge destinazioni alternative a un partecipante squillante o connesso"*, con il partecipante che resta in chiamata finché la nuova destinazione non risponde. Applicato al partecipante in coda con `destination` uguale all'interno dell'operatore, è semanticamente *"porta questa chiamata a me"*.

**[DA VERIFICARE] con priorità alta.** Se funziona, `CanPickSpecificCall` diventa `true` e il comando *"`Invio` sull'elenco coda preleva la chiamata selezionata"* dell'analisi §9.4 si realizza — cosa che su nessuno degli altri due centralini è ancora acquisita.

---

## 8. Stato agente: si fa senza API, ed è una buona notizia

3CX documenta quattro modi di entrare e uscire dalle code, e **due sono raggiungibili da BlindPhone senza toccare l'API**:

| Modo | Raggiungibile da BlindPhone | Come |
|---|---|---|
| **Codici di composizione** — `*62` login, `*63` logout (valori predefiniti) | **Sì, via SIP** | Composizione del codice come una chiamata qualunque |
| Stato del profilo utente (`Available` → in coda; `Away` / `Non disturbare` → fuori) | Sì, via XAPI | Cambio dello stato dell'utente |
| Pannello del Web Client, login per singola coda | No | Nessuna API documentata |
| Automatico su orario | No | Configurazione del PBX |

**`SetAgentLoginAsync` si implementa quindi con i codici di composizione**, e funziona **su qualunque piano di licenza**, anche senza la Call Control API. È il pezzo di `IQueueProvider` che si ottiene gratis.

Due avvertenze da mettere nel codice e nella documentazione:

1. **I codici sono configurabili dall'amministratore**: `*62` e `*63` sono i valori predefiniti, non una costante. Vanno esposti nelle impostazioni, non scritti nel programma.
2. **Il login globale non scavalca i logout espliciti per singola coda.** Un operatore uscito da una coda specifica dal Web Client resta fuori anche dopo `*62`. Se BlindPhone annunciasse *"collegato alle code"* sarebbe un'informazione falsa: l'annuncio corretto è *"comando di collegamento inviato"*, e lo stato reale va riletto.

**Non esistono codici motivo di pausa** come su Yeastar (`queue_pause_reason/list`). `GetPauseReasonsAsync` restituisce un elenco vuoto e la scelta del motivo non compare all'operatore — che è il comportamento giusto: un elenco vuoto è un dato, non un guasto.

---

## 9. Presenza degli interni: nativa, se dichiarata

Nella schermata di creazione dell'applicazione API si dichiarano gli **"additional extensions you want to monitor"**. Gli interni dichiarati compaiono in `GET /callcontrol` con i loro `participants`, e ogni cambiamento genera un evento `Upsert` sul WebSocket.

Mappatura su `PresenceState`:

| Situazione osservata | `PresenceState` |
|---|---|
| DN presente, nessun `participant` | `Available` |
| `participant` con `status` di squillo | `Ringing` |
| `participant` con `status` connesso | `Busy` |
| DN senza `devices` registrati | `Offline` |
| DN non dichiarato fra quelli sorvegliati | `Unknown` |

Quindi `IPresenceSource.RequiresExplicitWatch` è **`false`**: l'elenco si dichiara una volta nel PBX, non si sottoscrive interno per interno come nel SIP. Ma con una conseguenza pratica da documentare per l'IT del cliente:

> **L'elenco degli interni sorvegliati è configurazione del PBX, non della postazione.** Aggiungere un collega al quadro dei turni richiede l'intervento dell'amministratore del centralino. Va detto in fase di installazione, perché altrimenti diventa una richiesta di assistenza ricorrente che sembra un difetto del prodotto.

**[DA VERIFICARE]** se esiste un limite al numero di interni sorvegliabili, e se `Non disturbare` sia distinguibile dallo stato occupato: su un centralino di ente sono due situazioni che l'operatore tratta in modo diverso.

---

## 10. Rubrica: XAPI

XAPI espone utenti, gruppi e **contatti** in OData. Sono le due sorgenti che servono:

- **gli interni** — anagrafica delle estensioni, per la risoluzione del chiamante interno;
- **la Rubrica aziendale** (*Company Phonebook*) — i contatti esterni condivisi.

Essendo OData, la ricerca si esprime con `$filter`, `$select`, `$top`, `$skip` e `$expand`, quindi la paginazione è nativa e non va inventata.

> Il connettore si innesta su `IContactSource` accanto a quelli già scritti — CSV, backup, SQL Server. **La riprogettazione della rubrica fatta a settembre 2026 vale interamente**: identità stabile dalla chiave della sorgente, tipo di voce, secondo recapito, nominativo che ricade sulla descrizione. Un `ThreeCxContactSource` deve solo mappare i campi OData su `CsvColumnMapping`, e riusa `TabularContactSource`.

**[DA VERIFICARE]** il nome esatto dell'entità dei contatti nella V20 corrente e i campi restituiti. La documentazione pubblica degli endpoint XAPI è parziale: la fonte autorevole è lo **swagger dell'istanza del cliente**, `https://<fqdn>/xapi/v1/swagger.yaml`, che va scaricato e letto appena si ha accesso.

---

## 11. Mappatura sulle astrazioni esistenti

| Astrazione | Implementazione 3CX | Livello raggiunto |
|---|---|---|
| `IQueueProvider` | Call Control API: `GET /callcontrol/{coda}/participants` + WebSocket | **A** se l'ipotesi §7.1 regge, **B** altrimenti |
| ↳ stato agente | Codici di composizione via SIP (§8) | Login/logout sì, pausa con motivo no |
| ↳ prelievo selettivo | `routeto` verso il proprio interno (§7.2) | **[DA VERIFICARE]** |
| `IPresenceSource` | Call Control API, interni dichiarati nel PBX | Completo, `RequiresExplicitWatch = false` |
| `IContactSource` | XAPI OData: utenti + rubrica aziendale | Completo |
| `ITelephonyEngine` | **SIP nativo**, invariato | Completo |
| ↳ trasferimento di riserva | `transferto` / `divert` se il `REFER` è rifiutato | Completo |

Capacità dichiarate, nell'ipotesi ottimistica da confermare:

```csharp
new QueueProviderCapabilities(
    CanReadWaitingList:    true,
    CanReadCallerIdentity: true,
    CanReadWaitTime:       false,  // 3CX non lo fornisce: si stima lato client (§7.1)
    CanReadPosition:       false,  // finche' l'ordine dell'array non e' verificato
    CanPickSpecificCall:   false,  // finche' `routeto` non e' verificato (§7.2)
    CanManageAgentState:   true,   // login/logout via codici; nessun motivo di pausa
    CanReadPresence:       true)
```

---

## 12. Correlazione fra mondo SIP e mondo API

È lo stesso rischio tecnico del connettore Yeastar (§9 di quel documento), e la stessa strategia a tre livelli si applica. Su 3CX però il materiale è migliore:

| Livello | Su Yeastar | Su 3CX |
|---|---|---|
| Filtro sull'estensione | Evento 30008, già filtrato | `GET /callcontrol/{nostro dn}` — filtrato per costruzione |
| Identificativi | `call_id` + `channel_id` in formato `PJSIP/<est>-<hex>` | **`callid` + `legid` + `device_id`** |
| Controparte | `caller_number` | `party_caller_id` + `party_dn` + `party_did` |

Il `device_id` è l'elemento in più: identifica **l'apparecchio**, e siccome BlindPhone è l'unico apparecchio registrato sulla propria estensione, la corrispondenza è quasi sempre univoca senza euristiche temporali.

Resta valida la regola generale: **in caso di ambiguità residua si degrada in modo sicuro**, omettendo il riferimento specifico invece di rischiare di attribuire un esito alla chiamata sbagliata. Per un operatore che non può verificare a schermo, un'informazione sbagliata è peggio di un'informazione mancante.

Stima: **4 giornate**, contro le 5 di Yeastar.

---

## 13. Le funzionalità da implementare, in ordine

Stime indicative, da riconfermare su istanza di prova.

| # | Funzionalità | Tecnologia | Stima | Note |
|---|---|---|---|---|
| 1 | **`ThreeCxApiClient`** — token, rinnovo, certificati, diagnostica | REST | 4 gg | Riusa `CertificateTrustService` e il modello di `YeastarApiClient` |
| 2 | **`ThreeCxEventStream`** — WebSocket bidirezionale, riconnessione, riallineamento | WSS | 5 gg | Più complesso di Yeastar perché il canale porta anche le richieste |
| 3 | **`ThreeCxPresenceSource`** | Call Control | 4 gg | §9. Valore alto, rischio basso |
| 4 | **`ThreeCxQueueProvider`** | Call Control | 6 gg | §7. **Dipende dall'esito della verifica §7.1** |
| 5 | **Stato agente via codici** | SIP | 2 gg | §8. Funziona su ogni licenza |
| 6 | **`ThreeCxContactSource`** | XAPI OData | 5 gg | §10. Riusa `TabularContactSource` |
| 7 | **Trasferimento di riserva** (`transferto` / `divert`) | Call Control | 3 gg | Rete di sicurezza sul `REFER` |
| 8 | **Correlazione SIP ↔ API** | — | 4 gg | §12 |
| 9 | **Impostazioni e procedura guidata** | UI | 3 gg | Si innesta sul selettore di provider previsto in [CISCO-INTEGRAZIONE.md §6.1](CISCO-INTEGRAZIONE.md) |
| 10 | **Laboratorio e collaudo** | — | 6 gg | §14 |
| | **Totale** | | **≈ 42 gg** | |

**Sottoinsieme minimo utile — ≈ 15 gg:** voci 1, 2, 3, 5. Dà presenza degli interni, quadro dei turni e stato agente, cioè le funzioni che nessun softphone accessibile offre, **senza dipendere dall'esito delle verifiche sulla coda**.

---

## 14. Verifiche da fare, in ordine di priorità

Da eseguire su un'istanza di prova prima di chiudere il preventivo.

| # | Verifica | Se negativo |
|---|---|---|
| 1 | **Piano di licenza del cliente** ≥ 8SC e di vertice, con Call Control API attivabile | **Bloccante.** Costo a preventivo, o si resta al livello SIP puro |
| 2 | `GET /callcontrol/{dn coda}/participants` elenca i chiamanti in attesa (§7.1) | Coda al solo conteggio, o nessuna visibilità: **da comunicare al cliente prima della firma** |
| 3 | `routeto` verso il proprio interno preleva la chiamata dalla coda (§7.2) | `CanPickSpecificCall = false`, coda in sola lettura |
| 4 | Registrazione di BlindPhone come estensione SIP, fuori dal tunnel (§6.3) | **Bloccante** |
| 5 | `REFER` accettato dal PBX da client di terze parti | Trasferimento via `transferto` |
| 6 | Insieme reale dei valori di `EventType` sul WebSocket (§5.4) | Deserializzatore tollerante — da fare comunque |
| 7 | Valori reali di `participant.status` | Rimappare la presenza |
| 8 | L'ordine dell'array dei partecipanti rispecchia l'ordine di servizio | Non annunciare la posizione |
| 9 | Limite di dimensione dei messaggi WebSocket con molti DN | Interrogazioni grandi sulla REST |
| 10 | Esiste un limite di token o di applicazioni API simultanee | Client ID per postazione, come su Yeastar |
| 11 | Numero massimo di interni sorvegliabili (§9) | Ridurre il quadro dei turni |
| 12 | Codici di login/logout coda effettivi sul PBX del cliente (§8) | Configurabili nelle impostazioni |
| 13 | Nome dell'entità contatti e campi in `swagger.yaml` (§10) | Rimappare il connettore rubrica |
| 14 | G.722 disponibile sull'interno | Requisito EN 301 549 §6.1 |

**Laboratorio:** 3CX si installa su Debian o Windows e offre una licenza di prova; per le verifiche 1–3 serve però una licenza di vertice, che la prova gratuita **[DA VERIFICARE]** potrebbe non includere. È il primo ostacolo pratico da risolvere, prima ancora di scrivere codice.

---

## 15. Confronto fra i tre connettori

| | **Yeastar P-Series** | **Cisco CUCM** | **3CX V20** |
|---|---|---|---|
| Protocollo | REST + WebSocket | TAPI/CTI, SOAP, REST | REST + **WebSocket bidirezionale** |
| Elenco chiamanti in coda | **Sì**, documentato | **No** senza contact center | **Probabile**, da verificare (§7.1) |
| Tempo di attesa | **Sì** | No | **No**, stimato lato client |
| Prelievo selettivo | Punto aperto | No | **`routeto`**, da verificare (§7.2) |
| Presenza interni | Sì (evento 30009) | Solo via CTI | **Sì**, nativa |
| Stato agente | Sì, con motivi di pausa | Solo con Finesse | Login/logout sì, **motivi no** |
| Rubrica | Sì | UDS REST | XAPI OData |
| BLF SIP di riserva | Sì | **No** | **[DA VERIFICARE]** |
| Modello dell'audio | Ibrido per scelta | Ibrido o controllo remoto | **Ibrido per obbligo** — coincide con l'architettura esistente |
| Licenza | Enterprise / Ultimate | Nessun costo CTI aggiuntivo | **8SC+ piano di vertice** |
| Stima connettore | ≈ 40 gg | 42–47 gg (CUCM puro) | **≈ 42 gg**, minimo utile 15 |

**Conclusione.** 3CX è tecnicamente il connettore più vicino a quello Yeastar già scritto, e l'unico dei due nuovi in cui il modello dell'audio non richiede di ripensare il prodotto. Il rischio non è tecnico ma **commerciale**: la Call Control API sta dietro il piano di licenza più caro, e su una PA quel costo ricade sul progetto. La verifica §14.1 va fatta per prima, prima di qualunque laboratorio.

---

## Fonti

Consultate a settembre 2026. Nessuna verifica su PBX reale.

**Documentazione ufficiale 3CX**
- [3CX Call Control API — Configuration Guide](https://www.3cx.com/docs/call-control-api/) *(aggiornata 13 maggio 2025)*
- [3CX Call Control API — Endpoint Specification](https://www.3cx.com/docs/call-control-api-endpoints/) *(aggiornata 19 marzo 2025)*
- [How to Use 3CX Configuration API](https://www.3cx.com/docs/configuration-rest-api/) *(aggiornata 23 aprile 2026)*
- [Configuration REST API — Endpoint Specifications](https://www.3cx.com/docs/configuration-rest-api-endpoints/)
- [Call Control API for Windows](https://www.3cx.com/docs/call-api-windows/) · [per Linux](https://www.3cx.com/docs/call-api-linux/)
- [Managing agents' queue status](https://www.3cx.com/docs/pbx-queue-status/) *(codici `*62` / `*63`)*
- [Call Queues & Ring Groups V20](https://www.3cx.com/docs/manual/call-center-queues/)
- [Company Phonebook](https://www.3cx.com/docs/manual/phonebook/)
- [New V20 Call Control API — annuncio](https://www.3cx.com/blog/releases/v20-call-control-api/)

**Forum ufficiale — punti non coperti dalla documentazione**
- [Place call on "hold" and "answer" via API](https://www.3cx.com/community/threads/place-call-on-hold-and-answer-via-api.59963/) — hold e answer sono operazioni dell'endpoint
- [API function to show number of callers in queue](https://www.3cx.com/community/threads/api-function-to-show-number-of-callers-in-queue.54194/) — la coda come DN
- [Call Control API — how to tell which number is calling which?](https://www.3cx.com/community/threads/call-control-api-how-to-tell-which-number-is-calling-which.23947/) — valori di stato del partecipante
- [XAPI — Queues](https://www.3cx.com/community/threads/xapi-queues.135611/) — `$expand` sulle code
- [Direct SIP registration from 3rd party phone — V20](https://www.3cx.com/community/threads/direct-sip-registration-from-3rd-party-phone-v20.131755/)
- [Generic SIP working with v18 not with v20](https://www.3cx.com/community/threads/generic-sip-working-with-v18-not-with-v20.124194/)
- [Setup of Call Control API](https://www.3cx.com/community/threads/setup-of-call-control-api.131572/)

**Licenze**
- [3CX PRO Edition](https://www.3cx.com/phone-system/pro-edition/)
- [Understanding 3CX Licenses and Editions](https://www.callpro.com.au/understanding-3cx-licenses-and-editions/)

**Esempi di codice**
- [XAPI Tutorial — repository ufficiale](https://github.com/3cx/xapi-tutorial)
- [3CX XAPI examples — luxzg](https://github.com/luxzg/3CX-XAPI_examples)
