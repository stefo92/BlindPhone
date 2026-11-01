# BlindPhone — Conformità normativa e accesso al mercato della Pubblica Amministrazione italiana

**Documento complementare a [ANALISI-BLINDPHONE.md](ANALISI-BLINDPHONE.md), sezioni 4.7 e 18**

| | |
|---|---|
| **Versione** | 1.0 — 3 agosto 2026 |
| **Ambito** | Quadro normativo, requisiti tecnici cogenti, percorso di acquisto, deliverable per la gara |
| **Avvertenza** | Documento di analisi tecnica, non parere legale. Le valutazioni su nullità contrattuale, soglie e procedure vanno confermate dall'ufficio legale o dal consulente in materia di appalti prima dell'uso in offerta |

---

## 1. Il punto di partenza: il mercato è creato dalla legge

Questo è l'aspetto più rilevante della scelta di mercato, e va compreso prima di ogni considerazione tecnica.

### 1.1 Legge 113/1985 — l'obbligo di assumere

La **Legge 29 marzo 1985, n. 113** disciplina il collocamento e il rapporto di lavoro dei centralinisti telefonici non vedenti. I suoi due cardini:

- **Art. 3, comma 1** — definisce l'ambito: sono soggetti alla legge i centralini *"per i quali le norme tecniche prevedano l'impiego di uno o più posti-operatore o che comunque siano dotati di uno o più posti-operatore"*. Il criterio è la **presenza effettiva di un posto operatore**, non la tecnologia impiegata: un centralino VoIP moderno con un posto operatore ricade nell'obbligo esattamente come un centralino elettromeccanico del 1985.
- **Art. 3, comma 2** — i **datori di lavoro pubblici** sono tenuti ad assumere, *per ogni ufficio, sede o stabilimento dotato di centralino telefonico*, un centralinista iscritto all'albo professionale nazionale, **anche in deroga alle norme che limitano le assunzioni e anche se il posto non è previsto in pianta organica**.
- Per i **datori privati** l'obbligo scatta per ogni centralino con **almeno cinque linee urbane**.

L'albo professionale nazionale dei centralinisti telefonici non vedenti è articolato su base regionale e gestito dagli uffici regionali del lavoro.

**Conseguenza:** la platea di operatori non vedenti presso le PA italiane non è un mercato da creare con azioni di convincimento. **Esiste per obbligo di legge**, è censita negli albi regionali, ed è distribuita su ogni ufficio, sede o stabilimento pubblico dotato di posto operatore.

### 1.2 Legge 4/2004, art. 4 comma 4 — l'obbligo di attrezzare

L'assunzione genera un secondo obbligo, che è quello che finanzia BlindPhone. La **Legge 9 gennaio 2004, n. 4** (Legge Stanca), art. 4 comma 4, dispone:

> *"I datori di lavoro pubblici e privati pongono a disposizione del dipendente con disabilità la strumentazione hardware e software e la tecnologia assistiva adeguata alla specifica disabilità, anche in caso di telelavoro, in relazione alle mansioni effettivamente svolte."*

Tre elementi da notare:

1. **L'obbligo è del datore di lavoro**, pubblico *e* privato. Non è una facoltà né una misura di welfare aziendale.
2. Copre esplicitamente **software**, non solo hardware e tecnologia assistiva.
3. È commisurato alle **mansioni effettivamente svolte**. Per un centralinista, la mansione è gestire il centralino: uno screen reader generico su un softphone inaccessibile non soddisfa l'obbligo, perché non rende la mansione eseguibile.

### 1.3 La sintesi commerciale

```
Legge 113/1985  →  la PA DEVE assumere un centralinista non vedente
                          ↓
Legge 4/2004    →  la PA DEVE fornirgli software adeguato alla mansione
       art. 4 c. 4        ↓
                   BlindPhone
```

Due obblighi normativi indipendenti che convergono su un unico bisogno. È una posizione di mercato inusualmente solida, e va usata come apertura in ogni interlocuzione con la PA — con la precisazione onesta che *l'obbligo è di fornire uno strumento adeguato*, non di comprare questo specifico prodotto.

### 1.4 Il mercato privato è la stessa cosa

L'art. 4 comma 4 vale identicamente per i datori privati, e la Legge 113/1985 li obbliga per ogni centralino con almeno cinque linee urbane. Il mercato privato non richiede quindi un riposizionamento del prodotto: richiede solo un canale di vendita diverso (rivenditori Yeastar, system integrator) e nessuna procedura di gara.

### 1.5 Possibili contributi all'adattamento della postazione

L'adattamento del posto di lavoro per lavoratori con disabilità può accedere a contributi pubblici — tipicamente attraverso il Fondo regionale per l'occupazione delle persone con disabilità (Legge 68/1999) e, per le disabilità da infortunio o malattia professionale, attraverso INAIL. **Entità, requisiti e procedure variano per regione e cambiano nel tempo: vanno verificati caso per caso** con il servizio di collocamento mirato competente. È un argomento di vendita utile — riduce o azzera il costo per l'ente — ma non va mai promesso senza verifica.

---

## 2. Requisiti tecnici cogenti

### 2.1 Le Linee guida AgID coprono esattamente questo prodotto

Le **Linee guida sull'accessibilità degli strumenti informatici** di AgID non si limitano a siti web e app mobili. Il loro ambito comprende esplicitamente **la strumentazione hardware e il software messi a disposizione del dipendente con disabilità e la sua postazione di lavoro**, oltre ai documenti non web e alla documentazione e ai servizi di supporto.

Il riferimento tecnico è la norma europea **EN 301 549**, con il **capitolo 11 "Software"** come riferimento specifico per il software.

> **Verifica necessaria in fase di offerta:** le Linee guida hanno richiamato nel tempo versioni diverse della norma (v2.1.2 e successive; la versione corrente della EN 301 549 è la v3.2.1). **Va sempre controllata la versione richiamata nel capitolato specifico**, perché le differenze non sono cosmetiche: in particolare i requisiti sul testo in tempo reale (§2.3) sono cambiati sensibilmente tra le versioni. In assenza di indicazione, conviene dichiarare conformità alla versione più recente.

### 2.2 Capitoli EN 301 549 applicabili a BlindPhone

| Capitolo | Applicabilità | Impatto progettuale |
|---|---|---|
| **5 — Requisiti generici** | Sì | Attivazione delle funzioni di accessibilità, conservazione delle informazioni di accessibilità, parti operabili, ripetizione tasti, accettazione doppia battuta, azioni simultanee. Già coperti dai principi P1–P8 dell'analisi |
| **6 — ICT con comunicazione vocale bidirezionale** | **Sì — è il capitolo dimenticato** | Vedi §2.3: contiene requisiti con conseguenze dirette sull'architettura audio |
| **11 — Software** | **Sì — capitolo principale** | Corrisponde ai criteri WCAG trasposti al software, più §11.5 interoperabilità con la tecnologia assistiva, §11.6 documentazione delle funzioni di accessibilità, §11.7 preferenze utente |
| **12 — Documentazione e servizi di supporto** | **Sì** | Il manuale e l'assistenza devono essere a loro volta accessibili e devono descrivere le funzioni di accessibilità del prodotto |
| 13 — Servizi di relay ed emergenza | Da valutare | Rilevante se il centralino gestisce numerazioni di emergenza |
| 7, 8, 9, 10 | No | Media con sottotitoli, hardware, web, documenti non web: fuori ambito per l'applicativo desktop |

### 2.3 Capitolo 6 — i requisiti che nessuno si aspetta

Il capitolo 6 si applica a qualsiasi ICT che fornisca comunicazione vocale bidirezionale. Un posto operatore di centralino vi rientra pienamente. Tre punti hanno conseguenze concrete:

**§6.1 — Larghezza di banda audio per il parlato.** La norma indica il supporto di una gamma di frequenze con limite superiore di **almeno 7000 Hz** (audio a banda larga). In pratica significa **G.722 o Opus**, non solo G.711.

> **Conseguenza:** nell'analisi generale (§7.2) il G.722 figurava tra i codec supportati come miglioria di qualità. **Diventa un requisito di conformità**, e va verificato che sia abilitato anche sugli interni del PBX Yeastar (voce 12 della checklist nel documento di integrazione). Va inoltre esposto nell'interfaccia: l'operatore deve poter sapere, con una scorciatoia, che la chiamata in corso è a banda larga — è un'informazione che oggi manca in tutti i softphone.

**§6.2 — Testo in tempo reale (RTT).** La norma richiede, per l'ICT che fornisce comunicazione vocale bidirezionale, il supporto del testo in tempo reale. **Questo è un potenziale scostamento di conformità da affrontare consapevolmente.**

Tre considerazioni:
- Il requisito è formulato al livello del sistema di comunicazione. Per un endpoint di centralino è discutibile se ricada sul client o sull'infrastruttura PBX/operatore telefonico.
- L'implementazione RTT (RFC 4103, T.140 su RTP) è tecnicamente fattibile ma non banale, e richiede supporto lato PBX e lato controparte per essere realmente utile.
- **Va deciso in anticipo, non in sede di gara.** Le due opzioni oneste sono: (a) implementare RTT e dichiarare conformità piena; (b) non implementarlo e **dichiararlo esplicitamente come scostamento motivato** nell'Accessibility Conformance Report, con la motivazione dell'ambito.

Raccomandazione: opzione (b) per la v1.0, con RTT in roadmap. Un ACR che dichiara con precisione uno scostamento motivato è più credibile in gara di uno che tace, e la reticenza su un punto verificabile compromette la fiducia su tutto il resto del documento.

**§6.3 — Identificazione del chiamante.** Dove l'ICT fornisce l'identificazione del chiamante, questa deve essere disponibile in forma testuale e programmaticamente determinabile. **Già pienamente soddisfatto** dal progetto (§9.4 dell'analisi).

### 2.4 Capitolo 11 — i punti che decidono l'esito del collaudo

I criteri del capitolo 11 sono già coperti dall'impianto dell'analisi, ma due meritano attenzione perché sono quelli su cui i collaudi si fermano più spesso:

- **§11.5 — Interoperabilità con la tecnologia assistiva.** Richiede che il software esponga correttamente nome, ruolo, stato, proprietà e valore di ogni elemento ai servizi di accessibilità della piattaforma. È esattamente il livello 1 dello stack descritto in §4.2 dell'analisi (UI Automation completa). **È il motivo per cui la scelta di WPF con controlli standard e `AutomationPeer` espliciti non è negoziabile**, e per cui il gate di CI che blocca gli elementi privi di `AutomationProperties.Name` è un investimento di conformità, non un vezzo ingegneristico.
- **§11.7 — Preferenze utente.** Il software deve rispettare le preferenze di accessibilità della piattaforma (contrasto elevato, dimensione testo, colori). Già previsto in §4.6.

### 2.5 Capitolo 12 — la documentazione è parte del prodotto

Sottovalutato con regolarità, e causa frequente di rilievi in collaudo. Richiede che:

- la **documentazione di prodotto** sia disponibile in formato accessibile e **descriva le funzioni di accessibilità** del prodotto;
- i **servizi di supporto** (assistenza, help desk, formazione) siano accessibili e sappiano rispondere sulle funzioni di accessibilità.

Il secondo punto ha una conseguenza organizzativa: **chi risponde al telefono dell'assistenza deve saper guidare un operatore non vedente** senza dire "clicchi sull'icona in alto a destra". Va previsto nella formazione del supporto e va scritto nel contratto di manutenzione.

I deliverable già previsti in §18.3 dell'analisi (manuale in HTML strutturato, TXT, EPUB/DAISY, scheda tasti in BRF) coprono il primo punto. Il secondo va aggiunto.

### 2.6 Sull'obbligo di accessibilità nei contratti — precisazione doverosa

Va distinta la portata effettiva delle due previsioni della Legge 4/2004, per non sovrastimare l'argomento in offerta:

- **Art. 4 comma 1** — nella valutazione delle offerte per l'acquisto di beni e servizi informatici, la rispondenza ai requisiti di accessibilità costituisce **motivo di preferenza a parità di ogni altra condizione**. Vale quindi per l'acquisto di software come BlindPhone.
- **Art. 4 comma 2** — la sanzione della **nullità del contratto** è riferita ai contratti per la realizzazione e modifica di **siti web e applicazioni mobili**. Un applicativo desktop non vi rientra direttamente.

**Non va quindi affermato che "un contratto per un softphone inaccessibile è nullo".** L'argomento corretto e sufficientemente forte è un altro: l'obbligo dell'art. 4 comma 4 di fornire al dipendente con disabilità strumenti adeguati alla mansione, combinato con il criterio di preferenza dell'art. 4 comma 1 e con l'ambito delle Linee guida AgID che include espressamente la postazione di lavoro del dipendente con disabilità.

---

## 3. Percorso di acquisto

### 3.1 Canali

| Canale | Quando | Note |
|---|---|---|
| **MEPA / Acquisti in Rete (Consip)** | Canale principale per la PA | Richiede abilitazione come fornitore e inserimento del prodotto a catalogo nel bando merceologico pertinente (informatica, elettronica, telecomunicazioni). **È il primo investimento commerciale da fare**, prima ancora di avere il prodotto finito |
| **Affidamento diretto sotto soglia** | Importi contenuti, tipici di una singola postazione | Percorso più rapido. Soglie e modalità secondo il Codice dei contratti pubblici vigente: **da verificare con l'ufficio acquisti dell'ente**, sono cambiate di recente e cambiano ancora |
| **Trattativa diretta su MEPA** | Fornitore individuato, prodotto a catalogo | Compromesso frequente: velocità dell'affidamento diretto con la tracciabilità della piattaforma |
| **Inserimento in gara più ampia** | Rinnovo del centralino, gara di fornitura VoIP | Canale ad alto potenziale: **BlindPhone come lotto o voce del capitolato in una gara Yeastar**. Richiede presidio dei system integrator che rispondono a quelle gare |
| **Canale rivenditori Yeastar** | Privato e PA via integratore | Il partner ha già il rapporto con l'ente e installa il PBX |

Ogni affidamento richiede **CIG**; per importi rilevanti si aggiungono gli adempimenti ordinari (DURC, tracciabilità, eventuale DGUE).

### 3.2 CAD art. 68 — valutazione comparativa

L'art. 68 del Codice dell'Amministrazione Digitale impone alle PA una valutazione comparativa prima di acquisire software, con preferenza per soluzioni a riuso e open source. Per un prodotto proprietario questo va anticipato, non subito.

**La risposta più efficace è fattuale:** se sul mercato non esiste una soluzione accessibile equivalente — e allo stato, per il posto operatore Yeastar, l'alternativa ufficiale è un pannello drag-and-drop inutilizzabile senza vista — la valutazione comparativa si risolve favorevolmente da sé. Conviene preparare una **scheda di analisi delle alternative** da allegare, che elenchi le soluzioni esaminate e il motivo tecnico dell'esclusione. È un documento che l'ente apprezza perché gli risolve un adempimento.

Da valutare come opzione strategica: una **licenza open source per il nucleo** con servizi professionali a pagamento. Neutralizzerebbe del tutto l'art. 68 e sarebbe coerente con la missione del prodotto, ma va deciso presto perché condiziona la scelta dello stack SIP (§6.2 dell'analisi: SIPSorcery MIT rende la strada percorribile, PJSIP in GPL la complicherebbe).

### 3.3 Tempistica commerciale

Le PA pubblicano annualmente i propri **obiettivi di accessibilità**, con scadenza fissata al **31 marzo**. Un ente che ha centralinisti non vedenti inserisce tipicamente l'adeguamento della postazione tra questi obiettivi.

**Conseguenza pratica sul ciclo di vendita:** la finestra utile per farsi trovare è **da ottobre a febbraio**, quando gli enti predispongono obiettivi e bilancio di previsione. Una proposta che arriva a maggio trova il capitolo di spesa già assegnato.

### 3.4 On-premise, non cloud

Raccomandazione operativa: mantenere BlindPhone **interamente on-premise** (applicativo desktop + PBX del cliente), senza componenti SaaS.

Motivo: qualsiasi servizio cloud erogato alla PA ricade nel regime di qualificazione dei servizi cloud per la PA, con oneri, tempi e costi di certificazione sproporzionati rispetto al valore del prodotto. Un applicativo desktop che parla solo con il PBX dell'ente **evita interamente questo percorso**. È una scelta architetturale con motivazione commerciale, e conviene esplicitarla nella documentazione tecnica come punto di forza.

Se in futuro servisse una componente di aggiornamento o telemetria centralizzata, va progettata come **opzionale e disattivabile**, con l'installazione PA che gira di default senza. Le statistiche d'uso seguono questa regola: spente di partenza, e mute finché nessuno indica un server. Che cosa deve fare l'ente prima di accenderle sta nel §7.

---

## 4. Deliverable per la gara

Documenti da produrre, con l'indicazione di quando servono.

| Documento | Contenuto | Quando |
|---|---|---|
| **Dichiarazione di conformità EN 301 549** | Conformità dichiarata capitolo per capitolo (5, 6, 11, 12), con gli scostamenti motivati (§2.3 RTT) | Prima della prima offerta |
| **Accessibility Conformance Report (VPAT 2.5, formato EU)** | Tabella criterio per criterio: Supports / Partially Supports / Does Not Support / Not Applicable, con note | Prima della prima offerta |
| **Scheda tecnica per capitolato** | Descrizione delle funzionalità in linguaggio da capitolato, riusabile dall'ente per redigere la richiesta | Prima della prima offerta |
| **Analisi comparativa delle alternative** | Risposta preconfezionata all'art. 68 CAD | Prima della prima offerta |
| **Manuale utente accessibile** | HTML strutturato, TXT, EPUB/DAISY, scheda tasti in BRF | Al rilascio |
| **Guida amministratore** | SIP, connettore Yeastar, distribuzione GPO/Intune, ADMX | Al rilascio |
| **Informativa e analisi privacy** | Trattamento dati rubrica, registro chiamate, log, statistiche d'uso (§7); supporto al titolare per l'eventuale DPIA | Al rilascio |
| **Rapporto dei test con utenti non vedenti** | Metodologia, campione, metriche, esiti | Al rilascio |
| **Validazione di terza parte** | Endorsement UICI e/o I.Ri.Fo.R. | Auspicabile prima della prima gara |
| **Piano di formazione e supporto accessibile** | Copre il requisito EN 301 549 §12.2 | Al rilascio |

### 4.1 Sulla validazione di terza parte

Un endorsement dell'**UICI** (Unione Italiana dei Ciechi e degli Ipovedenti) o di **I.Ri.Fo.R.** ha, nel contesto italiano, un peso sproporzionato rispetto al suo costo. Motivi: l'UICI gestisce storicamente la formazione dei centralinisti non vedenti, è interlocutore riconosciuto delle PA sul tema, e la sua rete territoriale coincide con la distribuzione geografica degli operatori.

**Va perseguito presto**, idealmente coinvolgendo l'associazione già nella fase di test con utenti (§17.3 dell'analisi) anziché chiedendo un avallo a prodotto finito.

---

## 5. Impatto sull'analisi principale

Modifiche da recepire nei documenti di progetto:

| Sezione | Modifica |
|---|---|
| §4.7 Conformità | EN 301 549 diventa **requisito contrattuale**, non riferimento. Aggiungere i capitoli 6 e 12 all'ambito |
| §7.2 Codec | **G.722 obbligatorio** per EN 301 549 §6.1. Aggiungere l'indicazione all'operatore dello stato banda larga della chiamata |
| §7.2 / roadmap | Valutare **RTT (RFC 4103)** — decisione da prendere prima di F6 |
| §13 Impostazioni | Aggiungere il rispetto delle preferenze di accessibilità di piattaforma (EN §11.7), già in §4.6 ma da formalizzare come requisito |
| §17 Test | Il collaudo EN 301 549 diventa **criterio di uscita bloccante**, con evidenze documentali per ogni criterio |
| §18.3 Deliverable | Aggiungere ACR/VPAT, scheda per capitolato, analisi alternative, piano di supporto accessibile |
| §18 Roadmap | Anticipare l'abilitazione MEPA e il contatto UICI: sono attività commerciali con tempi lunghi, da avviare in parallelo a F0 |
| §19 Rischi | Nuovo rischio: scostamento RTT rilevato in gara. Mitigazione: dichiararlo in anticipo nell'ACR |
| §15 Sicurezza | Nessuna componente cloud nella configurazione PA (§3.4) |

---

## 6. Le tre azioni da avviare subito

Indipendenti dallo sviluppo, con tempi lunghi, e per questo da far partire ora e non a prodotto pronto:

1. **Abilitazione MEPA come fornitore.** Procedura amministrativa con tempi propri. Senza, la PA non può comprare, per quanto buono sia il prodotto.
2. **Contatto con UICI / I.Ri.Fo.R.** Serve sia per reclutare i testatori previsti in §17.3 sia per costruire la validazione di §4.1. Entrambe le cose funzionano meglio se l'associazione è coinvolta dall'inizio come partner e non interpellata alla fine come certificatore.
3. **Decisione sull'RTT e sul modello di licenza.** Sono le due scelte che, se prese tardi, costringono a rifare lavoro: l'RTT tocca l'architettura media, la licenza tocca lo stack SIP.

---

## 7. Statistiche d'uso anonime: che cosa deve fare l'ente prima di accenderle

BlindPhone può mandare statistiche d'uso a un server PostHog: quante postazioni ci sono, con quale versione e quale screen reader, quali funzioni sono accese, quali comandi si usano e da dove. Come si prepara il server e dove si scrivono indirizzo e chiave sta in [docs/INSTALLAZIONE.md](docs/INSTALLAZIONE.md), §11. **Sono spente di partenza**, e una postazione su cui nessuno ha indicato un server non manda niente nemmeno con la casella spuntata.

Questa nota serve all'ente che decide di accenderle. Vale l'avvertenza in testa al documento: è un'analisi tecnica, non un parere legale, e va letta con il responsabile della protezione dei dati (RPD) dell'ente.

### 7.1 Sono dati personali?

Non contengono dati personali in chiaro, ma **conviene trattarli come dati personali pseudonimi**, per tre ragioni:

- l'identificativo dell'installazione è casuale, ma è stabile: distingue una postazione dalle altre per tutto il tempo in cui le statistiche restano accese;
- la connessione porta al server l'indirizzo IP della postazione, che la giurisprudenza europea considera un dato personale quando chi lo riceve ha mezzi ragionevoli per collegarlo a una persona (Corte di giustizia, causa C-582/14, *Breyer*), e un ente conosce la propria rete;
- su una postazione assegnata a un operatore, «la postazione» e «la persona» sono la stessa cosa.

Trattarli come pseudonimi non costa molto, ed evita di costruire l'adempimento su una premessa — «sono anonimi» — che un controllo potrebbe smentire.

### 7.2 Chi è il titolare, e su quale base

**Il consenso nella scheda Diagnostica non è la base giuridica.** È un comando: dice al programma se mandare o no. Nel rapporto di lavoro il consenso del dipendente difficilmente è libero, perché chi lo chiede è il datore di lavoro (Comitato europeo per la protezione dei dati, Linee guida 05/2020 sul consenso), e un ente pubblico non può fondare su quello un trattamento che riguarda i propri dipendenti. La casella serve a un'altra cosa: a far sì che nulla parta per caso, e che l'operatore possa sempre vedere se parte.

I ruoli dipendono da **chi ospita il server**:

| Server | Ruoli | Che cosa serve |
|---|---|---|
| **Dell'ente** | L'ente è titolare; la finalità è la gestione del parco postazioni e della loro accessibilità | Base giuridica nell'esecuzione dei compiti dell'ente (art. 6, par. 1, lett. e, del Regolamento UE 2016/679, con l'art. 2-ter del Codice privacy), da verificare con l'RPD |
| **Del fornitore** | Va deciso e scritto: il fornitore come responsabile del trattamento per conto dell'ente (art. 28 del Regolamento), oppure come titolare autonomo delle statistiche sul proprio prodotto | Una clausola nel contratto di fornitura o di assistenza che dica quale delle due, con le istruzioni o l'informativa del fornitore |
| **PostHog Cloud UE** | PostHog Inc. è responsabile del trattamento (art. 28) di chi apre l'account e firma il suo DPA. Titolare e base giuridica restano quelli delle due righe sopra: se l'account è dell'ente, PostHog è responsabile dell'ente; se è del fornitore, PostHog è sub-responsabile dell'ente quando il fornitore è suo responsabile, e allora l'ente lo deve autorizzare (art. 28, par. 2), oppure responsabile del fornitore titolare autonomo | Il DPA di PostHog firmato, e quello che il §7.6 chiede in più per il cloud |

Con un server dell'ente o del fornitore il server non è un servizio che l'ente acquista o usa per lavorare: non dovrebbe ricadere nella qualificazione dei servizi cloud del §3.4, ma con un server del fornitore la conclusione va confermata dall'ufficio legale. **Con PostHog Cloud la domanda va posta esplicitamente**: è un servizio cloud vero, e se l'account è dell'ente va verificato con l'ufficio legale se ricada nel regime del §3.4.

L'identificativo è un'informazione archiviata sull'apparecchio dell'utente, il caso dell'art. 122 del Codice privacy: la casella spenta di partenza e la decisione esplicita vanno nella direzione di quella regola. Quando a decidere per tutte le postazioni è l'ente, con il pacchetto di configurazione, va chiarito con l'RPD se basta la decisione dell'ente proprietario delle postazioni o se serve anche la scelta del singolo operatore: nel secondo caso il pacchetto distribuisce indirizzo e chiave con il consenso spento, e la sezione Diagnostica non si blocca.

### 7.3 Lo Statuto dei lavoratori: perché non ci sono conteggi né orari

L'art. 4 della legge 300 del 1970 ammette gli strumenti da cui può derivare un controllo a distanza dell'attività dei lavoratori solo per esigenze organizzative, produttive, di sicurezza o di tutela del patrimonio, e solo con un accordo sindacale o un'autorizzazione dell'Ispettorato del lavoro. L'eccezione per «gli strumenti utilizzati dal lavoratore per rendere la prestazione» copre il softphone, non le statistiche, che al lavoro non servono.

Per questo le statistiche sono costruite in modo da **non misurare il lavoro**:

- nessun conteggio: si sa che un comando è stato usato in un giorno, non quante volte, e quindi non quante chiamate ha preso la postazione;
- nessun orario: ogni evento porta il giorno, e l'uso dei comandi arriva il giorno dopo;
- nessun evento legato alla singola chiamata: non c'è «chiamata ricevuta», «chiamata persa», durata o attesa in coda.

Un ente che volesse di più — quante chiamate per postazione, a che ora — avrebbe in mano un controllo dell'attività, e la procedura dell'art. 4 andrebbe fatta prima. **Il prodotto non lo offre**, e una prova automatica impedisce che il catalogo degli eventi si allarghi senza che qualcuno lo dichiari.

### 7.4 L'informativa agli operatori

Prima di accendere, l'ente informa gli operatori (art. 13 del Regolamento, e art. 4, comma 3, dello Statuto per l'informazione sugli strumenti): chi è il titolare, perché, che cosa parte, verso chi, per quanto tempo, come si esercitano i diritti, e come si spegne.

**Che cosa parte** lo dice l'elenco della §11 di INSTALLAZIONE.md, e lo dice all'operatore la guida in linea, argomento «Le statistiche d'uso anonime»: è scritta per essere ascoltata con lo screen reader, e legge le descrizioni dallo stesso catalogo che decide che cosa può partire. Non sostituisce l'informativa, ma ne è la parte accessibile, e dice la verità per costruzione.

### 7.5 Minimizzazione: che cosa fa già il prodotto

| Misura | Dove |
|---|---|
| Elenco chiuso di eventi e proprietà, ogni valore un sì o no, una voce di un elenco o una versione | `TelemetryCatalog`, con le prove che lo sorvegliano |
| Nessun numero di telefono, nome, interno, indirizzo SIP, contenuto della rubrica, nome del centralino o testo di errore | Non esiste una forma di valore che li possa contenere; una prova lo verifica sui byte inviati a un server di prova |
| Nessuna sequenza di sei cifre in nessun valore | Verificato a ogni invio |
| Giorno e non ora, nessun conteggio | §7.3 |
| Identificativo casuale, senza nome macchina, utente o indirizzo di rete; dimenticato quando si spegne | `TelemetryDiary` |
| Nessun profilo di persona, nessuna geolocalizzazione | `$process_person_profile: false` e `$geoip_disable: true` su ogni evento |
| Solo HTTPS, nessun rinvio seguito, chiavi personali rifiutate | `TelemetrySettings`, `PostHogClient` |
| Spento di partenza; annunciato a voce quando un pacchetto di configurazione lo accende | Scheda Diagnostica, pacchetto di configurazione |

### 7.6 Che cosa resta da fare a chi gestisce il server

Su tutte e due le strade:

- **Scartare l'IP** nelle impostazioni del progetto, e spegnere la trasformazione GeoIP.
- **Accessi** al progetto con utenze personali e verifica in due passaggi.
- **Registro dei trattamenti** (art. 30 del Regolamento) aggiornato con il trattamento.
- **Valutazione d'impatto**: con le misure del §7.5 il trattamento non misura l'attività dei dipendenti, e questo dovrebbe escluderla; la decisione resta dell'ente, sentito l'RPD.

Solo con un'istanza propria:

- **Server nell'Unione europea**, senza trasferimenti fuori dallo Spazio economico europeo.
- **Decidere la conservazione, e applicarla**: un'istanza PostHog ospitata da sé tiene gli eventi finché qualcuno non li cancella.
- **Registri del proxy brevi**: sono l'unico posto in cui compaiono IP e ora di una postazione.

Solo con PostHog Cloud UE, sulla base del DPA e delle pagine di PostHog citate nelle fonti:

- **Firmare il DPA** generato dall'organizzazione PostHog, che è disponibile con qualunque piano. È il contratto dell'art. 28: senza, PostHog riceve dati senza un accordo che lo vincoli.
- **Regione UE**, scelta alla creazione dell'account: gli eventi stanno in Germania. La regione non si cambia dopo, salvo una migrazione fatta da PostHog sui piani più alti.
- **Trasferimenti fuori dallo Spazio economico europeo: ci sono, e vanno valutati.** La regione UE dice dove stanno gli eventi, non dove vengono trattati. Il DPA dice che PostHog tratta i dati anche negli Stati Uniti e negli altri paesi del suo elenco dei sub-responsabili, e chi lo firma autorizza quei trasferimenti. Le garanzie dichiarate sono l'adesione all'EU-US Data Privacy Framework e le clausole contrattuali tipo (decisione di esecuzione UE 2021/914, modulo 2). L'RPD valuta se bastano; se non bastano, la strada è l'istanza propria.
- **Sub-responsabili**: autorizzati in via generale dal DPA, con un preavviso di quattordici giorni su ogni cambio dell'elenco e sette giorni per opporsi. Qualcuno deve leggere quegli avvisi.
- **Funzioni di intelligenza artificiale spente**: portano sub-responsabili negli Stati Uniti, e BlindPhone non ne ha bisogno.
- **Conservazione fissata dal piano**, un anno con quello gratuito e sette con quelli a pagamento, e **non si accorcia**: va scritta così nell'informativa e nel registro. Un periodo più breve si ottiene solo cancellando il progetto.

### 7.7 In ordine, prima di accendere

1. Decidere chi ospita il server — l'ente, il fornitore o PostHog Cloud UE — e scriverlo nel contratto con i ruoli del §7.2. Con il cloud, firmare il DPA di PostHog.
2. Preparare il server o il progetto con le impostazioni della §11 di INSTALLAZIONE.md.
3. Dare l'informativa agli operatori (§7.4).
4. Aggiornare il registro dei trattamenti, sentito l'RPD.
5. Accendere: con il pacchetto di configurazione, bloccando la sezione Diagnostica se la decisione è dell'ente, oppure lasciando la casella agli operatori.

---

## Fonti

- [Legge 29 marzo 1985, n. 113 — testo](https://handylex.org/legge-29-marzo-1985-n-113/)
- [Legge 113/1985 art. 3 comma 1 — caratteristiche tecniche dei centralini (UICI)](https://giornale.uici.it/legge-1131985-art-3-comma1-caratteristiche-tecniche/)
- [Ministero del Lavoro — norme sul collocamento delle persone non vedenti](https://www.lavoro.gov.it/temi-e-priorita/disabilita-e-non-autosufficienza/focus-on/albi-professionali-persone-non-vedenti/pagine/principali-norme-sul-collocamento-dei-disabili)
- [Legge 9 gennaio 2004, n. 4 — testo](https://handylex.org/legge-9-gennaio-2004-n-4/)
- [Legge 4/2004 — testo su parlamento.it](https://www.parlamento.it/parlam/leggi/04004l.htm)
- [AgID — Linee guida sull'accessibilità degli strumenti informatici (PA)](https://www.agid.gov.it/sites/agid/files/2024-06/Linee%20Guida%20sull'accessibilit%C3%A0%20degli%20strumenti%20informatici%20-%20PA.pdf)
- [Linee guida AgID — requisiti tecnici per l'accessibilità degli strumenti informatici](https://docs.italia.it/AgID/documenti-in-consultazione/lg-accessibilita-docs/it/stabile/requisiti-tecnici-accessibilita-strumenti-informatici.html)
- [Regolamento (UE) 2016/679 — testo](https://eur-lex.europa.eu/eli/reg/2016/679/oj)
- [Codice in materia di protezione dei dati personali, d.lgs. 196/2003 — Normattiva](https://www.normattiva.it/uri-res/N2Ls?urn:nir:stato:decreto.legislativo:2003-06-30;196)
- [Legge 20 maggio 1970, n. 300 (Statuto dei lavoratori) — Normattiva](https://www.normattiva.it/uri-res/N2Ls?urn:nir:stato:legge:1970-05-20;300)
- [Comitato europeo per la protezione dei dati — Linee guida 05/2020 sul consenso](https://www.edpb.europa.eu/our-work-tools/our-documents/guidelines/guidelines-052020-consent-under-regulation-2016679_en)
- [Corte di giustizia UE, causa C-582/14, Breyer](https://curia.europa.eu/juris/liste.jsf?num=C-582/14)
- [PostHog — Self-host](https://posthog.com/docs/self-host)
- [PostHog — Controlling data storage](https://posthog.com/docs/privacy/data-storage)
- [PostHog — Data Processing Agreement](https://posthog.com/dpa)
- [PostHog — Subprocessors](https://posthog.com/subprocessors)
- [PostHog — GDPR compliance](https://posthog.com/docs/privacy/gdpr-compliance)
- [PostHog — Privacy, «How do I get a signed DPA?»](https://posthog.com/docs/privacy)
- [PostHog — Events data retention](https://posthog.com/docs/data/events-retention)
- [PostHog — Capture API, indirizzi delle regioni](https://posthog.com/docs/api/capture)
- [PostHog — Projects, spostamento fra regioni](https://posthog.com/docs/settings/projects)
