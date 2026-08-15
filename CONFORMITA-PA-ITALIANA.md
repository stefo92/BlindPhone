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

Se in futuro servisse una componente di aggiornamento o telemetria centralizzata, va progettata come **opzionale e disattivabile**, con l'installazione PA che gira di default senza.

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
| **Informativa e analisi privacy** | Trattamento dati rubrica, registro chiamate, log; supporto al titolare per l'eventuale DPIA | Al rilascio |
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

## Fonti

- [Legge 29 marzo 1985, n. 113 — testo](https://handylex.org/legge-29-marzo-1985-n-113/)
- [Legge 113/1985 art. 3 comma 1 — caratteristiche tecniche dei centralini (UICI)](https://giornale.uici.it/legge-1131985-art-3-comma1-caratteristiche-tecniche/)
- [Ministero del Lavoro — norme sul collocamento delle persone non vedenti](https://www.lavoro.gov.it/temi-e-priorita/disabilita-e-non-autosufficienza/focus-on/albi-professionali-persone-non-vedenti/pagine/principali-norme-sul-collocamento-dei-disabili)
- [Legge 9 gennaio 2004, n. 4 — testo](https://handylex.org/legge-9-gennaio-2004-n-4/)
- [Legge 4/2004 — testo su parlamento.it](https://www.parlamento.it/parlam/leggi/04004l.htm)
- [AgID — Linee guida sull'accessibilità degli strumenti informatici (PA)](https://www.agid.gov.it/sites/agid/files/2024-06/Linee%20Guida%20sull'accessibilit%C3%A0%20degli%20strumenti%20informatici%20-%20PA.pdf)
- [Linee guida AgID — requisiti tecnici per l'accessibilità degli strumenti informatici](https://docs.italia.it/AgID/documenti-in-consultazione/lg-accessibilita-docs/it/stabile/requisiti-tecnici-accessibilita-strumenti-informatici.html)
