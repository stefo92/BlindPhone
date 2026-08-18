# Dichiarazione di conformità EN 301 549

**Bozza da completare prima dell'uso in offerta.** Vedi la sezione 6 per cosa manca.

| | |
|---|---|
| **Prodotto** | BlindPhone, posto operatore di centralino |
| **Versione** | 1.0.13 beta |
| **Data** | 18 agosto 2026 |
| **Norma di riferimento** | EN 301 549 — capitoli 5, 6, 11, 12 |
| **Redatto da** | Il fornitore, sulla base di verifiche interne |
| **Verifica di terza parte** | **Non ancora effettuata** |

## Come leggere le valutazioni

Le quattro voci sono quelle del modello VPAT:

| Voce | Significato |
|---|---|
| **Soddisfatto** | Il requisito è rispettato in tutte le funzioni del prodotto |
| **Parzialmente soddisfatto** | Rispettato in generale, con eccezioni indicate nelle note |
| **Non soddisfatto** | Non rispettato. La nota dice perché e cosa si intende fare |
| **Non applicabile** | Il prodotto non ha la funzione a cui il requisito si riferisce |

**Le note contano quanto la valutazione.** Un «soddisfatto» senza indicazione di come
è stato verificato non è un'informazione utile a chi deve collaudare.

---

## 1. Lo scostamento, dichiarato per primo

**§6.2 — Testo in tempo reale (RTT): NON SODDISFATTO.**

BlindPhone non implementa il testo in tempo reale secondo RFC 4103 e T.140. Non è
una svista: è una scelta dichiarata, e queste sono le ragioni.

- Il requisito è formulato al livello del **sistema** di comunicazione. Per un posto
  operatore che si appoggia al centralino dell'ente è discutibile se ricada sul
  client o sull'infrastruttura, e la risposta cambia da capitolato a capitolato.
- L'implementazione richiede **supporto lato centralino e lato controparte** per
  produrre un effetto reale. Un client che offre RTT verso un PBX che non lo instrada
  soddisfa la casella e non serve a nessuno.
- La funzione è in roadmap e non è stata scartata.

**Va detto in offerta, non taciuto.** Uno scostamento dichiarato con precisione è più
credibile di un silenzio, e la reticenza su un punto verificabile in collaudo
compromette la fiducia su tutto il resto del documento.

---

## 2. Capitolo 5 — Requisiti generici

| Requisito | Valutazione | Note e verifica |
|---|---|---|
| 5.2 Attivazione delle funzioni di accessibilità | Soddisfatto | Non c'è nulla da attivare: il prodotto è accessibile nella configurazione predefinita, e la voce parla dal primo avvio |
| 5.3 Accesso biometrico | Non applicabile | Nessuna funzione biometrica |
| 5.4 Conservazione delle informazioni di accessibilità | Soddisfatto | Le preferenze restano nella configurazione della postazione |
| 5.5 Parti operabili | Non applicabile | Software, nessuna parte fisica |
| 5.6 Stato di blocco o commutazione | Non applicabile | |
| 5.7 Ripetizione dei tasti | Soddisfatto | Le scorciatoie globali sono registrate con la ripetizione disattivata: tenere premuto non produce una raffica di comandi |
| 5.8 Accettazione della doppia battuta | Soddisfatto | La doppia pressione ha significato solo dove è dichiarata, con finestra configurabile |
| 5.9 Azioni simultanee | Soddisfatto | Nessun comando richiede la pressione contemporanea di più tasti che non sia una combinazione con modificatori, tutte riassegnabili |

---

## 3. Capitolo 6 — Comunicazione vocale bidirezionale

| Requisito | Valutazione | Note e verifica |
|---|---|---|
| **6.1 Larghezza di banda audio** | **Soddisfatto** | G.722 è offerto in negoziazione nell'elenco predefinito. Verificato da collaudo automatico che ne impedisce la rimozione. La banda effettiva della chiamata in corso è **annunciata a voce** su richiesta dell'operatore, indicazione che i softphone comuni non danno |
| **6.2 Testo in tempo reale** | **Non soddisfatto** | Vedi sezione 1 |
| **6.3 Identificazione del chiamante** | **Soddisfatto** | Numero e nominativo disponibili in forma testuale, esposti ai servizi di accessibilità e annunciati. Il numero compare anche quando il centralino accompagna la chiamata con un'etichetta di coda |
| 6.4 Interruzione di chiamata | Non applicabile | Funzione del centralino, non del posto operatore |
| 6.5 Comunicazione video | Non applicabile | Il prodotto è solo voce. Un flusso video eventualmente proposto dal centralino viene ignorato |

---

## 4. Capitolo 11 — Software

| Requisito | Valutazione | Note e verifica |
|---|---|---|
| 11.1–11.4 (WCAG trasposti al software) | Parzialmente soddisfatto | Vedi il dettaglio qui sotto |
| Contenuto non testuale | Soddisfatto | Ogni comando ha nome accessibile e testo di aiuto; i pittogrammi non portano informazione da soli |
| Uso della tastiera | Soddisfatto | **Nessuna funzione richiede il mouse.** È il principio su cui il prodotto è costruito, non una compatibilità aggiunta |
| Nessuna trappola per la tastiera | Soddisfatto | |
| Tempi regolabili | Soddisfatto | Le finestre temporali che contano — doppia pressione, promemoria di attesa, scadenza della consultazione — sono configurabili |
| Contrasto | Soddisfatto | Rapporto minimo 7:1, livello AAA, verificato con calcolo WCAG in collaudo automatico su tutti i temi |
| Ridimensionamento del testo | Soddisfatto | Ingrandimento di sistema fino al 300% senza perdita di funzioni |
| Focus visibile | Soddisfatto | Contorno da 3 pixel in tutte le finestre |
| Nome, ruolo, valore | Soddisfatto | Vedi §11.5 |
| **11.5 Interoperabilità con la tecnologia assistiva** | **Soddisfatto** | Controlli standard con proprietà di accessibilità dichiarate. Verificato ispezionando l'albero di accessibilità con un client UI Automation, la stessa interfaccia usata da NVDA e JAWS. Collaudi automatici impediscono il ritorno dei difetti trovati: voci che pronunciavano il nome interno della classe, celle esposte come campi da compilare, colonne generate automaticamente. Oltre alle notifiche UIA, che restano la via conforme e valida per qualunque tecnologia assistiva, il prodotto include il client controller di NVDA: consente di **interrompere** il parlato, cioè di far annunciare una chiamata entrante senza attendere la fine di ciò che lo screen reader sta leggendo |
| **11.6 Documentazione delle funzioni di accessibilità** | Parzialmente soddisfatto | La guida in linea è completa, navigabile da tastiera e costruita sulle combinazioni effettivamente assegnate. Manca la documentazione **esterna** nei formati previsti: vedi sezione 6 |
| **11.7 Preferenze utente** | **Soddisfatto** | Il contrasto elevato di Windows ha la precedenza sulla scelta del tema; dimensione del testo e colori di sistema sono rispettati |
| 11.8 Strumenti di authoring | Non applicabile | |

---

## 5. Capitolo 12 — Documentazione e servizi di supporto

| Requisito | Valutazione | Note e verifica |
|---|---|---|
| 12.1.1 Documentazione accessibile | **Parzialmente soddisfatto** | La guida dentro il prodotto è accessibile e la scheda dei tasti si esporta in un formato adatto a display e stampa braille. Mancano manuale e guida amministratore come documenti esterni |
| 12.1.2 Descrizione delle funzioni di accessibilità | Parzialmente soddisfatto | Descritte nella guida in linea; da riportare nel manuale esterno |
| **12.2.2 Informazioni sull'accessibilità dai servizi di supporto** | **Non soddisfatto** | Richiede che chi risponde all'assistenza sappia guidare un operatore non vedente senza dire «clicchi sull'icona in alto a destra». È un requisito **organizzativo**: va previsto nella formazione del supporto e scritto nel contratto di manutenzione |
| 12.2.3 Documentazione accessibile del supporto | Non soddisfatto | Come sopra |
| 12.2.4 Comunicazione accessibile con il supporto | Parzialmente soddisfatto | La segnalazione all'assistenza si crea dal prodotto, da tastiera, e allega registri anonimizzati |

---

## 6. Che cosa manca per usare questo documento in offerta

Elencato perché una dichiarazione che tace le proprie lacune non vale niente.

1. **Verifica con utenti non vedenti**, documentata: metodologia, campione, compiti,
   tempi, esiti. Oggi le verifiche sono interne e strumentali.
2. **Collaudo sulla postazione reale**, con il centralino dell'ente e lo screen reader
   in uso. Diverse voci qui sopra sono verificate a banco: sul campo possono cadere.
3. **Manuale utente e guida amministratore** come documenti esterni accessibili
   (HTML strutturato, testo semplice, EPUB), che è ciò che chiude §11.6 e §12.1.
4. **Formazione del supporto** e clausola nel contratto di manutenzione, che è ciò che
   chiude §12.2.
5. **Verifica della versione della norma richiamata dal capitolato**: fra le versioni
   della EN 301 549 i requisiti sul testo in tempo reale cambiano in modo
   sostanziale, e questo documento va riallineato a quella richiesta.
6. **Validazione di terza parte**, auspicabilmente con un'associazione riconosciuta
   nel settore.

---

## 7. Come sono state fatte le verifiche interne

Perché chi legge possa giudicare quanto valgono.

- **Strumentali**: ispezione dell'albero di accessibilità con un client UI Automation,
  cioè la stessa interfaccia che usano gli screen reader; calcolo del rapporto di
  contrasto secondo le formule WCAG sui colori dichiarati nei temi; misura del
  percorso del cursore fra i controlli.
- **Automatiche**: la suite di collaudo del prodotto — 1.011 prove — comprende
  verifiche di accessibilità che impediscono il ritorno di difetti già trovati.
- **Non ancora fatte**: prove d'uso con operatori non vedenti su compiti reali e
  cronometrati, che sono le uniche che dicono se il prodotto è **usabile** e non solo
  conforme. La differenza fra le due cose è il motivo per cui questo documento è una
  bozza.
