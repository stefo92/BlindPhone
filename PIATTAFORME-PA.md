# BlindPhone — Quali centralini integrare: le infrastrutture telefoniche della PA italiana

**Documento di indirizzo, complementare a [CONFORMITA-PA-ITALIANA.md](CONFORMITA-PA-ITALIANA.md) e a [ANALISI-BLINDPHONE.md](ANALISI-BLINDPHONE.md) §9.3**

| | |
|---|---|
| **Versione** | 1.0 — 5 settembre 2026 |
| **Ambito** | Quali piattaforme di centralino conviene integrare, in base a che cosa la PA italiana ha davvero installato e a come lo compra |
| **Stato dei dati** | Fonti pubbliche: Consip, acquistinretepa, stampa di settore, documentazione dei produttori (settembre 2026). **Nessun dato di installato certificato**: la ripartizione per brand non è pubblicata da nessuno, e le stime qui dentro sono ricostruite dal canale d'acquisto |
| **Documenti collegati** | [YEASTAR-INTEGRAZIONE.md](YEASTAR-INTEGRAZIONE.md) · [CISCO-INTEGRAZIONE.md](CISCO-INTEGRAZIONE.md) · [3CX-INTEGRAZIONE.md](3CX-INTEGRAZIONE.md) |

---

## 1. La domanda va rovesciata

La domanda naturale è *«quale centralino ha l'API migliore?»*. È la domanda sbagliata, e lo si vede confrontando i tre connettori già studiati: il migliore tecnicamente (Yeastar) è quello che una PA di medie dimensioni **non può comprare per via ordinaria**.

La domanda giusta è: **che cosa si trova la PA in casa quando arriva il centralinista non vedente?** E per rispondere bisogna guardare come la PA acquista, perché in Italia l'acquisto è centralizzato e quindi l'installato è prevedibile.

---

## 2. Come la PA italiana compra un centralino

Il canale è l'**Accordo Quadro Consip «Centrali Telefoniche»**, giunto alla nona edizione. Non è un canale fra i tanti: per la PA è *la* strada, perché consente l'affidamento diretto senza gara.

### 2.1 L'edizione in corso — CT9

| | |
|---|---|
| **Attivazione** | Stipula 13 maggio 2024, pubblicazione 25 giugno 2024 |
| **Scadenza** | 26 giugno 2026, con proroga tecnica possibile fino a dicembre 2026 |
| **Struttura** | **Lotto unico** |
| **Valore** | **145 milioni di euro** |
| **Durata dei contratti attuativi** | Fino a **4 anni**, quindi installazioni CT9 vive fino al 2030 |
| **Fornitori aggiudicatari** | RTI **Vodafone Italia–Sielte** · **Telecom Italia (TIM)** · **Fastweb** · RTI **WindTre–Delo Instruments** |
| **Brand ammessi** | **Undici** secondo acquistinretepa, **tredici** secondo la stampa di settore — vedi §2.2 |

Oggetto dell'accordo: fornitura, messa in esercizio e manutenzione di centrali telefoniche, **terminali e apparati correlati compresi i posti operatore**, installazione, configurazione, dismissione dell'esistente, help desk, addestramento e manutenzione.

> **«Posti operatore» è scritto nell'oggetto dell'accordo.** Non è una forzatura interpretativa dire che BlindPhone appartiene alla merceologia di CT9: appartiene alla riga esatta. La strada commerciale più corta non è vendere a un ente, è **entrare nell'offerta di uno dei quattro aggiudicatari** come posto operatore accessibile — cosa che richiede un accordo con TIM, Fastweb, Vodafone-Sielte o WindTre-Delo, non con il singolo comune.

### 2.2 I brand ammessi

Nominati esplicitamente dalle fonti pubbliche:

| Brand | Natura | Interfaccia di controllo |
|---|---|---|
| **Alcatel-Lucent Enterprise** | OmniPCX Enterprise / Office | **CSTA** |
| **Avaya** | Aura / IP Office | **TSAPI** (Aura), **TAPI** (IP Office), DMCC via AES |
| **Cisco** | Unified CM | CTI/TAPI, UDS, Serviceability — vedi [CISCO-INTEGRAZIONE.md](CISCO-INTEGRAZIONE.md) |
| **Mitel** | MiVoice 5000 / Business / Office 400 / MX-ONE | **CSTA** (5000, Office 400), **TAPI** (Business, MX-ONE) |
| **VoiSmart** | Orchestra NG — italiano | API proprietaria documentata |
| **Nethesis** | NethVoice — italiano, **Asterisk + FreePBX** | **Asterisk AMI** |
| **Sangoma** | FreePBX / Switchvox — **Asterisk** | **Asterisk AMI / ARI** |
| **VPCom** | Esclusiva Delo Instruments | **[DA VERIFICARE]** |

**[DA VERIFICARE] — mancano da tre a cinque brand.** Le fonti pubbliche non pubblicano l'elenco completo, e le due che lo contano non concordano (undici contro tredici). L'elenco definitivo sta nel **configuratore Consip** su acquistinretepa, accessibile con le credenziali di un ente o di un fornitore abilitato. **È la prima cosa da recuperare**, perché tre brand ignoti su undici sono un quarto del mercato di riferimento.

Composizione nota per fornitore: **Delo Instruments** offre Alcatel-Lucent e VPCom in esclusiva, più Avaya. **TIM** porta Avaya, e nell'edizione precedente (CT8) portava anche VoiSmart, Sangoma e VoIPpartners. **Fastweb** compare in adesioni con Mitel.

### 2.3 Due movimenti in corso da tenere d'occhio

1. **CT9 scade a giugno 2026** (dicembre con proroga). **CT10 è la finestra**: se BlindPhone deve comparire nell'offerta di un aggiudicatario, il momento di parlarne è mentre i fornitori preparano la risposta alla gara, non dopo.
2. **Vodafone Italia è confluita in Fastweb il 1° gennaio 2026.** Due dei quattro aggiudicatari sono ora lo stesso gruppo: la concentrazione riduce il numero di interlocutori commerciali da presidiare, ma anche le alternative.

---

## 3. La scoperta scomoda: Yeastar e 3CX non ci sono

Nessuna delle fonti pubbliche su CT9 nomina **Yeastar** o **3CX** fra i brand ammessi.

Questo non significa che non siano installati nella PA: i comuni piccoli acquistano su **MePA** con affidamento diretto, dove il catalogo è quello dei rivenditori e Yeastar e 3CX ci sono eccome. Significa però che:

- **il connettore primario di BlindPhone serve il segmento che ha meno posti operatore.** Un comune da 5.000 abitanti con un Yeastar non ha un centralinista non vedente in organico; l'obbligo della Legge 113/1985 morde negli enti con un posto operatore vero — ASL, aziende ospedaliere, università, province, comuni capoluogo, ministeri — che sono esattamente quelli che comprano su CT9;
- **il mercato dove la legge crea la domanda e il mercato dove BlindPhone oggi si collega sono due mercati diversi.** È il disallineamento più importante di tutto questo documento.

> Non è un errore di strategia: Yeastar era la scelta giusta per **costruire** il prodotto, perché ha l'API migliore, un laboratorio gratuito e un cliente pilota. È la scelta sbagliata per **venderlo alla PA grande**, e le due cose possono convivere purché la seconda non si scopra alla prima gara persa.

---

## 4. La mappa che conta: quante piattaforme copre un connettore

Qui sta la risposta operativa alla domanda «quali altre piattaforme integrare». **Non conviene ragionare per marchio, ma per interfaccia**, perché alcune interfacce sono standard e ne coprono molti in un colpo solo.

| Connettore | Piattaforme coperte | Brand CT9 coperti | Standard |
|---|---|---|---|
| **CSTA / uaCSTA** (ECMA-269/323, uaCSTA su SIP) | Alcatel-Lucent OmniPCX · Mitel MiVoice 5000 e Office 400 · Unify OpenScape Business e Voice · Panasonic · Innovaphone · telefoni SIP generici | **Alcatel-Lucent, Mitel** | ECMA / ETSI / ISO-IEC |
| **Asterisk AMI / ARI** | Sangoma FreePBX e Switchvox · Nethesis NethVoice · Kalliope · FreePBX in genere · installazioni su misura | **Sangoma, Nethesis** | *de facto* |
| **Cisco CTI + UDS + Serviceability** | Cisco Unified CM, UCCX/UCCE | **Cisco** | proprietario |
| **Avaya TSAPI / DMCC via AES** | Avaya Aura, IP Office | **Avaya** | proprietario |
| **3CX Call Control API** | 3CX V20+ | — | proprietario |
| **Yeastar Open API** | Yeastar P-Series | — | proprietario |
| **BroadWorks XSI** | Centralini in cloud degli operatori | *indiretto* | *de facto* |
| **Microsoft Graph** | Teams Phone | — | proprietario |

**Il conto che decide le priorità:** con **due connettori standard** — CSTA e Asterisk AMI — si coprono **quattro degli otto brand CT9 noti**. Con il connettore Cisco già progettato si arriva a cinque. Resta scoperto Avaya, che è probabilmente il più diffuso di tutti.

---

## 5. I candidati, in ordine

### 5.1 Asterisk AMI / ARI — priorità alta

**Copre:** Sangoma (FreePBX, Switchvox), Nethesis NethVoice, Kalliope, e ogni installazione su misura basata su Asterisk, che nella PA italiana sono molte più di quante compaiano nei cataloghi.

**Perché conviene per primo:**

- è **già in piano** nell'analisi generale §9.3, priorità 3, quindi non è una nuova direzione ma un'accelerazione;
- `QueueStatus`, `QueueSummary` e gli eventi `QueueCallerJoin` / `QueueCallerLeave` / `QueueCallerAbandon` / `AgentCalled` / `AgentConnect` danno il **livello A completo** — elenco dei chiamanti, tempo di attesa, posizione — cioè più di quanto dia Cisco e quanto dà Yeastar;
- `Redirect` risolve il **prelievo selettivo dalla coda**, che su Yeastar è rimasto un punto aperto;
- il laboratorio costa zero: Asterisk in container, come già previsto per i collaudi SIP;
- **due brand CT9 in un colpo solo**, entrambi italiani o con presenza italiana forte.

**Il costo:** AMI è un protocollo testuale su TCP con eventi non versionati, che cambiano fra le versioni di Asterisk e fra le distribuzioni. La stessa `QueueStatus` non restituisce gli stessi campi su FreePBX 16 e su NethVoice. **La variabilità fra distribuzioni è il rischio, non il protocollo.**

**Stima: ≈ 20 giornate**, più 5 per ciascuna distribuzione da certificare.

### 5.2 CSTA / uaCSTA — priorità alta, ma verificare prima

**Copre:** Alcatel-Lucent OmniPCX, Mitel MiVoice 5000 e Office 400, Unify OpenScape, Panasonic, Innovaphone. È l'unico connettore **standardizzato** (ECMA-269 per i servizi, ECMA-323 per la codifica XML, uaCSTA per il trasporto su SIP), quindi l'unico in cui il lavoro non va rifatto da capo per ogni marchio.

**Perché conviene:** copre la fascia dei centralini «tradizionali» delle grandi PA — quelli installati dieci anni fa e ancora in esercizio, che sono precisamente gli impianti dove il posto operatore esiste ed è presidiato da un centralinista di ruolo.

**Il costo e il rischio:** CSTA è uno standard ampio e **implementato in modo disomogeneo**. Ogni costruttore ne realizza un sottoinsieme, con dati privati propri (Unify dichiara esplicitamente uno schema *Private Data* su ECMA-323/354). Un connettore CSTA che funziona su OmniPCX non funziona automaticamente su OpenScape.

> **Verifica preliminare obbligatoria:** stabilire se convenga parlare CSTA direttamente o appoggiarsi a un **middleware commerciale** già scritto — il più noto è *estos ECSTA*, che espone i vari CSTA come TAPI e ha una matrice di compatibilità pubblica. Comprare il middleware costa una licenza per postazione ma azzera l'onere di interoperabilità; scriverlo costa più giornate ma niente licenze. **È una decisione da prendere con i numeri, non per principio.**

**Stima: ≈ 30 giornate** per il primo costruttore, 8–12 per ciascuno dei successivi. Con middleware: ≈ 15 giornate più il costo di licenza.

### 5.3 Avaya — priorità media, costo alto

È il brand più citato nelle adesioni CT9 (offerto sia da TIM sia da Delo), quindi commercialmente il più rilevante. Ma:

- **Aura** si controlla con **TSAPI** o **DMCC** attraverso **Application Enablement Services (AES)**, un componente server separato con licenze proprie;
- **IP Office** si controlla con **TAPI**, quindi con lo stesso onere di interop già descritto per Cisco;
- le due famiglie **non condividono l'interfaccia**: sono due connettori, non uno.

**Stima: ≈ 40 giornate** per Aura via AES, più il nodo delle licenze AES a carico del cliente. Da vincolare a un pilota reale, come il CTI Cisco.

### 5.4 Il cloud degli operatori — priorità da chiarire

Una quota crescente di CT9 è **centralino in cloud** dell'operatore: *NeXXt Communication* e *Centralino Virtuale* di Fastweb, le offerte cloud di TIM, *OneNet* di Vodafone. Per l'ente non c'è più un PBX in sede.

**Il problema è che non si sa su cosa girano.** Le piattaforme di centralino in cloud degli operatori europei sono storicamente **BroadSoft BroadWorks** o **Metaswitch**, entrambe con interfacce documentate (**XSI** per BroadWorks), ma nessuno dei tre operatori italiani lo dichiara pubblicamente.

**[DA VERIFICARE] — è la ricerca a più alto valore e più basso costo di tutto questo elenco.** Una telefonata a un system integrator che lavora con Fastweb o TIM chiarisce in mezz'ora se dietro c'è BroadWorks. Se la risposta è sì, **un solo connettore XSI copre il segmento cloud di più operatori insieme** — cioè la direzione in cui il mercato si sta muovendo, non quella da cui viene.

### 5.5 Microsoft Teams Phone — da valutare a parte

Molte PA hanno già Microsoft 365 e stanno spostando la telefonia su **Teams Phone**, con o senza Direct Routing verso il centralino esistente.

Due considerazioni opposte:

- **A favore:** Teams **non ha un posto operatore nativo**. Le console per centralinisti su Teams sono tutte di terze parti, e l'accessibilità di quelle console è una domanda aperta. Se c'è un vuoto simile a quello Yeastar, è qui.
- **Contro:** l'audio in Teams passa dal client Teams. BlindPhone dovrebbe controllare Teams dall'esterno via **Microsoft Graph**, cioè il «modello B» già descritto per Cisco, con tutte le sue conseguenze sull'identità del prodotto — e su una piattaforma che cambia API più in fretta di quanto un prodotto certificato possa inseguire.

**Non è una decisione tecnica ma di prodotto**, e va presa dopo aver risposto alla domanda del §5.4: se il mercato PA va sul cloud dell'operatore, Teams è un fronte diverso; se va su Teams, è il fronte principale.

---

## 6. Cosa non integrare

| Cosa | Perché no |
|---|---|
| **Un connettore per marchio** senza passare dagli standard | Otto brand CT9 × 40 giornate è un programma pluriennale. CSTA e AMI esistono per non farlo |
| **Centralini fuori supporto** (Siemens HiPath 3000, Avaya CS 1000, Mitel 100) | Sono nell'installato ma in dismissione, e lo *switch-off* del rame li sta spegnendo da solo |
| **TAPI generico come strategia** | Ha senso come *conseguenza* di CSTA via middleware, non come scelta autonoma: è tecnologia del 1997 e ogni TSP si comporta a modo suo |
| **Piattaforme senza posto operatore** | Se un centralino non prevede un posto operatore, non ricade nella Legge 113/1985 e non c'è domanda creata dalla legge |

---

## 7. Le verifiche da fare, in ordine di rapporto valore/costo

| # | Verifica | Costo | Perché conta |
|---|---|---|---|
| 1 | **Elenco completo dei brand CT9** dal configuratore Consip | Mezza giornata | Tre-cinque brand ignoti su undici: è un quarto del mercato |
| 2 | **Su quale piattaforma girano i centralini in cloud** di Fastweb, TIM e WindTre (§5.4) | Una telefonata | Se è BroadWorks, un connettore copre il segmento in crescita |
| 3 | **Quali console operatore accessibili esistono già** per Alcatel-Lucent, Avaya, Mitel | 2 giornate | Come per Cisco (§9 di quel documento): decide se c'è un vuoto o un incumbent |
| 4 | **Quando esce CT10 e quando i fornitori preparano l'offerta** | Mezza giornata | È la finestra per entrare come posto operatore nell'offerta di un aggiudicatario |
| 5 | Compatibilità reale di AMI fra FreePBX, NethVoice e Kalliope | 3 giornate | Dice se il connettore Asterisk è uno o tre |
| 6 | Costo di licenza di un middleware CSTA per postazione | Mezza giornata | Decide fra comprare e scrivere (§5.2) |

---

## 8. Raccomandazione

**Ordine proposto, dopo i connettori già in piano:**

1. **Asterisk AMI/ARI** — 20 giornate, due brand CT9, livello A completo, laboratorio gratuito, già previsto dall'analisi. **È il miglior rapporto copertura/costo dell'intero elenco.**
2. **Verifica §7.2 sul cloud degli operatori** — costo quasi nullo, può riordinare tutto il resto.
3. **CSTA**, preceduto dalla decisione fra middleware e implementazione propria — copre la fascia dove il posto operatore esiste davvero.
4. **Avaya** e **Cisco CTI** solo con un pilota reale che li paghi.
5. **Teams Phone** dopo aver capito dove va il mercato.

**E una raccomandazione che non è tecnica.** Il canale che conta non è il singolo ente: è **l'accordo con uno dei quattro aggiudicatari CT9** perché includa BlindPhone come posto operatore accessibile nella propria offerta. La conformità EN 301 549 e l'obbligo della Legge 113/1985 sono argomenti che a un fornitore che deve rispondere a una gara Consip **servono più che all'ente**, perché gli permettono di offrire qualcosa che i concorrenti non hanno. Vale la pena spenderli lì.

---

## Fonti

Consultate a settembre 2026.

**Canale d'acquisto della PA**
- [Consip — AQ Centrali telefoniche (ed. 9)](https://www.consip.it/bandi/aq-centrali-telefoniche-ed-9)
- [Consip — Aggiudicato in via efficace l'AQ Centrali telefoniche (ed. 9)](https://www.consip.it/notizie-bandi/aggiudicato-in-via-efficace-laq-centrali-telefoniche-ed-9)
- [acquistinretepa — Centrali Telefoniche 9](https://www.acquistinretepa.it/opencms/opencms/scheda_bando.html?idBando=befd91b5788ba7cb)
- [acquistinretepa — Nuove centrali telefoniche, approfondimenti](https://www.acquistinretepa.it/opencms/opencms/programma_approfondimenti_centrali_telefoniche.html) *(elenco parziale dei brand)*
- [Consip — Accordo Quadro CT9 completo, RTI WindTre–Delo Instruments (PDF)](https://www.consip.it/sites/default/files/bandi/documentazione_firmata/ID%202496%20-%20AQ%20CT%20ed.%209%20-%20Accordo%20Quadro%20Completo%20-%20RTI%20WindTre-DeloInstruments.pdf)
- [CorCom — Centrali telefoniche per la PA, i vincitori della gara Consip](https://www.corrierecomunicazioni.it/digital-economy/centrali-telefoniche-per-la-pa-ecco-i-vincitori-della-gara-consip-da-90-milioni/)
- [ForumPA — Accordo Quadro Centrali telefoniche 9: le opportunità di innovazione](https://www.forumpa.it/pa-digitale/accordo-quadro-centrali-telefoniche-9-ecco-quali-sono-le-opportunita-di-innovazione/)
- [Delo Instruments — CT9, marchi offerti](http://www.delo.it/index.php/2024/07/11/ct9/)
- [VoiSmart — Al via la convenzione Consip CT9](https://voismart.it/it/al-via-la-convenzione-consip-ct9/)
- [Comune di Genova — adesione AQ CT8, brand VoiSmart-Avaya-Sangoma-VoIPpartners](https://www2.comune.genova.it/content/dd-2021-1260133-l%E2%80%99adesione-all%E2%80%99accordo-quadro-consip-%E2%80%9Ccentrali-telefoniche-8%E2%80%9D-lotto-unico-ce)
- [Università di Genova — acquisto telefoni Mitel in CT9](https://trasparenza.unige.it/bandi/procneg/13563)

**Interfacce e standard**
- [ECMA — CSTA, panoramica (PDF Unify)](https://wiki.unify.com/images/3/3e/CSTA_introduction_and_overview.pdf)
- [Unify OpenScape Experts Wiki — CSTA](https://wiki.unify.com/wiki/CSTA) · [uaCSTA](https://wiki.unify.com/wiki/uaCSTA)
- [ECMA TR/87 — uaCSTA](https://ecma-international.org/publications-and-standards/technical-reports/ecma-tr-87)
- [estos — matrice di compatibilità ECSTA per centralino](https://support.estos.de/de/ecsta/uebersicht-ecsta-middleware-und-unterstuetzte-telefonanlagen-kompatibilitaetsliste)

**Produttori**
- [VoiSmart Orchestra NG](https://www.voismart.it/products/voce/)
- [Nethesis NethVoice — documentazione](https://docs.nethvoice.com/it/docs/administrator-manual)
- [Kalliope PBX](https://www.kalliope.com/pbx/)
- [Fastweb NeXXt Communication](https://www.fastweb.it/piccole-medie-imprese/schede/centralino-ucc/) · [Centralino Virtuale](http://cloud.fastweb.it/grandi-aziende/cloud/scheda-prodotto/centralino-virtuale/index.html)
- [Microsoft — Configurare Teams Phone](https://learn.microsoft.com/it-it/microsoftteams/setting-up-your-phone-system)
