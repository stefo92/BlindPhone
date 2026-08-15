# BlindPhone

Posto operatore di centralino telefonico progettato per essere usato senza vista.

Non è un softphone a cui è stata aggiunta l'accessibilità: l'interazione primaria è
la tastiera e l'interfaccia primaria è l'audio. PBX di riferimento **Yeastar
P-Series**, mercato di riferimento la **Pubblica Amministrazione italiana**.

## Documenti di progetto

| Documento | Contenuto |
|---|---|
| [ANALISI-BLINDPHONE.md](ANALISI-BLINDPHONE.md) | Analisi tecnico-funzionale completa |
| [YEASTAR-INTEGRAZIONE.md](YEASTAR-INTEGRAZIONE.md) | Connettore Yeastar: API, eventi, checklist pre-vendita |
| [CONFORMITA-PA-ITALIANA.md](CONFORMITA-PA-ITALIANA.md) | Normativa, requisiti cogenti, percorso di acquisto |
| [SCHEDA-TASTI-RAPIDI.md](SCHEDA-TASTI-RAPIDI.md) | Scheda tasti per screen reader e stampa braille |
| [docs/ARCHITETTURA.md](docs/ARCHITETTURA.md) | Mappa dei progetti e punti di estensione |
| [docs/CONTRIBUIRE-ACCESSIBILITA.md](docs/CONTRIBUIRE-ACCESSIBILITA.md) | Regole vincolanti per chi tocca il codice |
| [docs/INSTALLAZIONE.md](docs/INSTALLAZIONE.md) | Pacchetto `.msi`: costruzione, distribuzione, aggiornamento |

## Requisiti di sviluppo

- Windows 10 22H2 o Windows 11
- .NET SDK 10.0 (la soluzione targetta `net10.0` e `net10.0-windows`)
  - per ricompilare sulla banda precedente basta `-p:BandaDotNet=net9.0`, che richiede l'SDK 9.0

## Compilare ed eseguire

```bash
dotnet build BlindPhone.sln
```

```bash
dotnet test tests/BlindPhone.Core.Tests/BlindPhone.Core.Tests.csproj
```

```bash
dotnet run --project src/BlindPhone.App/BlindPhone.App.csproj
```

Senza account SIP configurato l'applicazione si avvia in **modalità simulata**: le
scorciatoie e le sequenze di trasferimento funzionano tutte, nessuna telefonata
parte. È la modalità con cui addestrare un operatore appena assunto senza occupare
una linea di servizio.

## Pacchetto di installazione

```powershell
powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1
```

Produce `dist\BlindPhone-<versione>-x64.msi`: circa 62 MB, senza prerequisiti
perché include il motore di esecuzione .NET. Installa in `C:\Program Files\BlindPhone`
per tutti gli utenti, crea i collegamenti nel menu Start e sul desktop, e si
aggiorna installando la versione nuova sopra la vecchia.

La cartella dei dati dell'operatore (`%APPDATA%\BlindPhone`: configurazione,
rubrica, registri) non viene toccata né dall'aggiornamento né dalla
disinstallazione.

Serve WiX 5 una volta sola, e la distribuzione su più postazioni si fa senza
interazione: entrambe le cose sono in
[docs/INSTALLAZIONE.md](docs/INSTALLAZIONE.md).

## Struttura della soluzione

```
src/
  BlindPhone.Core            dominio, macchine a stati, pipeline di annuncio (net10.0)
  BlindPhone.Accessibility   bridge NVDA e JAWS, voce interna
  BlindPhone.Telephony       ITelephonyEngine su SIPSorcery, motore simulato
  BlindPhone.Audio           WASAPI a quattro vie, earcon a sintesi
  BlindPhone.Pbx.Yeastar     IQueueProvider e rubrica su Open API Yeastar
  BlindPhone.Contacts        archivio SQLite, importazione CSV
  BlindPhone.Infrastructure  impostazioni, segreti DPAPI, scorciatoie globali
  BlindPhone.App             shell WPF
tests/
  BlindPhone.Core.Tests      623 test sul core, incluso il gate di copertura annunci
  BlindPhone.Windows.Tests   110 test su impostazioni, archivi cifrati e instradamento voce
```

## Configurazione

Alla prima esecuzione viene creata la cartella `%APPDATA%\BlindPhone` con:

| File | Contenuto |
|---|---|
| `settings.json` | Configurazione, **senza segreti**, esportabile e condivisibile |
| `hotkeys.json` | Profili dei tasti rapidi e nome di quello attivo |
| `secrets.dat` | Password SIP, Client Secret dell'API e chiave degli archivi, cifrati con DPAPI ambito utente |
| `contacts.db` | Archivio locale della rubrica, **cifrato** |
| `history.db` | Elenco delle ultime chiamate, **cifrato**, con la conservazione scelta dall'operatore |
| `certificati-fidati.json` | Certificati del centralino approvati dall'operatore. **Deliberatamente fuori da `settings.json`**, che è condivisibile |
| `errori/` | Rapporti degli errori gravi, in attesa di essere segnalati |

### Archivi cifrati a riposo

I due archivi locali contengono dati personali: la rubrica di un ente — nomi, ruoli,
collocazioni interne — e i dati di traffico telefonico, cioè chi ha chiamato chi e
quando. Stanno su una postazione che può essere rubata, prestata o dismessa, quindi
sono cifrati con **SQLCipher (AES-256)**, file intero e non singoli campi: senza la
chiave non si legge nemmeno il nome delle tabelle.

La chiave è casuale a 256 bit, generata alla prima esecuzione e custodita in
`secrets.dat`, che è protetto da **DPAPI con ambito utente**: leggerla richiede
l'account Windows dell'operatore su quella macchina. Un disco estratto e montato
altrove non la restituisce, ed è esattamente lo scenario per cui gli archivi vengono
cifrati.

Un archivio creato da una versione precedente **viene convertito al primo avvio**: si
scrive una copia cifrata a fianco, la si riapre con la chiave per verificarla, e solo
allora si sostituisce l'originale, che viene eliminato. Non resta una copia di
sicurezza in chiaro — sarebbe prudente per i dati e insensato per la riservatezza.

Il rovescio va detto: **se `secrets.dat` va perso o il profilo Windows viene
ricreato, la chiave sparisce e gli archivi non sono più leggibili da nessuno.** La
rubrica si reimporta dal centralino o da CSV; il registro delle chiamate no. È il
prezzo della cifratura locale senza un server che custodisca le chiavi, ed è il
compromesso giusto per una postazione di lavoro.

In quel caso BlindPhone **non si rifiuta di partire**: è il telefono di chi non
vede, e una finestra di errore al posto della schermata di lavoro lo lascerebbe senza
modo di rispondere. L'archivio illeggibile viene spostato in
`<nome>.db.non-leggibile-<data>` — spostato, non cancellato: se `secrets.dat` torna
da un backup, quei dati tornano con lui — e si riparte da un archivio vuoto. Il fatto
finisce nel registro e nel riepilogo diagnostico, perché «stamattina la rubrica era
vuota» altrimenti sembrerebbe un guasto inspiegabile.

Se la conversione non riesce l'archivio resta in chiaro e il programma continua a
funzionare — un centralinista senza rubrica non lavora — ma il caso **non passa
inosservato**: finisce nel registro e nella sezione *Archivi locali* del riepilogo
diagnostico, e la conversione viene ritentata al riavvio.

Per collegarsi a un PBX Yeastar servono: piano **Enterprise o Ultimate**, firmware
37.7.0.16 o superiore, funzione API abilitata con Client ID e Client Secret
generati. La checklist completa di verifica pre-vendita è in
[YEASTAR-INTEGRAZIONE.md §13](YEASTAR-INTEGRAZIONE.md).

## Audio a quattro vie

*Impostazioni → Audio*

Quattro vie indipendenti, ciascuna con dispositivo e volume propri:

| Via | Cosa ci esce | Tipicamente |
|---|---|---|
| **Microfono** | La voce dell'operatore | Cuffia |
| **Conversazione** | La voce dell'interlocutore, e i riscontri di ciò che l'operatore ha appena fatto (risposta, attesa, trasferimento) | Cuffia |
| **Suoneria** | **Il segnale di chiamata in arrivo**, gli eventi di coda, gli allarmi di sistema | Altoparlanti |
| **Annunci** | La voce interna di BlindPhone | Altoparlanti, o cuffia se non si usa uno screen reader |

È la configurazione che fa più differenza nell'uso quotidiano: separare suoneria e
conversazione evita che uno squillo arrivi nell'orecchio impegnato mentre si parla.
Per lo stesso motivo il segnale di chiamata in arrivo esce dalla **suoneria** e non
dalla conversazione: su un posto operatore la chiamata successiva arriva mentre la
precedente è ancora in corso.

Dispositivi e volumi si applicano **all'avvio e a ogni salvataggio**, senza riavviare.
Fa eccezione il percorso audio di una chiamata già in corso, che prende i dispositivi
all'apertura e non si sposta a metà conversazione.

## Modalità semplificata: tre tasti del tastierino

*Impostazioni → Accessibilità → Modalità di interazione*

Per chi sta al centralino senza il tempo di imparare quattordici scorciatoie, tre tasti
del tastierino numerico governano l'intera chiamata:

| Tasto | Cosa fa |
|---|---|
| **`+`** | Risponde alla chiamata che squilla. Poi alterna: in linea mette in pausa, in pausa riprende |
| **`/`** | Riaggancia. Durante un trasferimento chiude la linea con l'interno e riporta al chiamante |
| **`Invio`** | Trasferisce. Fuori da una chiamata compone il numero scritto nel campo, come sempre |

**È la pausa a distinguere i due trasferimenti**, e non c'è altro da ricordare:

- **Chiamante ancora in linea** → `Invio` lo manda **subito** all'interno composto, senza
  annunciarlo. È il percorso rapido: `+` per rispondere, si digita l'interno, `Invio`.
- **Chiamante messo in pausa con `+`** → `Invio` chiama prima l'interno e lo fa
  **squillare**; un secondo `Invio` gli passa la chiamata, che abbia risposto o che stia
  ancora squillando.

Mentre una chiamata squilla il campo di composizione non accetta testo, così il `+` con
cui si risponde non può finirci dentro; torna a scrivere appena si è risposto, che è
quando serve. `+` e `/` non arrivano mai al campo, in nessuno stato, nemmeno quando in
quel momento non hanno significato: in quel caso suona il segnale di comando rifiutato.

**Il resto continua a funzionare**: la modalità semplificata aggiunge una via breve, non
ne toglie nessuna. Scorciatoie, barra dei pulsanti e annunci restano dov'erano. **La
riassegnazione dei tasti però si blocca**, ed è deliberato: la via breve ha senso se è
identica su tutte le postazioni, e un `+` che su una macchina risponde e su un'altra fa
altro è peggio di nessuna via breve, perché chi copre il turno di un collega si porta
dietro l'abitudine sbagliata.

## Aggiornamenti dal server dell'ente

BlindPhone può accorgersi da sé che è uscita una versione nuova, mostrarne le novità e
installarla. **Sul server serve una cartella con due file per versione**, con lo stesso
nome ed estensione diversa:

```
BlindPhone-1.2.0-x64.msi     il pacchetto prodotto dall'installatore
BlindPhone-1.2.0-x64.txt     le novità che introduce
```

L'abbinamento è il nome e nient'altro: nessun indice da tenere aggiornato, e pubblicare
una versione è copiare due file in una cartella. La versione si legge dal nome del
pacchetto, quindi il nome va lasciato quello che l'installatore produce.

Il file di testo si scrive a mano, una novità per riga:

```
# Le righe che cominciano con cancelletto sono commenti e non compaiono a schermo.
- Suoneria che squilla fino alla risposta
- Barra dei comandi per chi usa il mouse
- Trasferimento cieco e assistito su due pulsanti distinti
SHA256: 9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08
```

La riga `SHA256:` è **facoltativa ma consigliata**: quando c'è, il pacchetto scaricato
viene confrontato con quell'impronta prima di proporne l'installazione, e un file
arrivato a metà o sovrascritto durante il caricamento viene scartato invece che
installato. Si ottiene con `certutil -hashfile BlindPhone-1.2.0-x64.msi SHA256`.

**Dove si scrive l'indirizzo del server:** la costante `UpdateSettings.DefaultServerUrl`
in [AppSettings.cs](src/BlindPhone.Infrastructure/AppSettings.cs), che vale per tutte le
postazioni installate da quel momento. Su una postazione già installata si corregge in
`settings.json` sotto `Updates.ServerUrl`, che ha la precedenza. Deve essere l'indirizzo
di una **cartella** e finire con la barra.

**Usare `ftps://` e non `ftp://`.** Su FTP in chiaro la password e il pacchetto
attraversano la rete leggibili: chi si trova sul percorso vede la prima e può
sostituire il secondo con un altro programma, che l'operatore installerebbe con i
privilegi di amministratore. BlindPhone accetta `ftp://` — non tutti i server hanno
FTPS — ma lo segnala nel registro e lo scrive nella finestra dell'aggiornamento, perché
è una scelta che va fatta sapendo. La password del server non sta in `settings.json`: vive
nel deposito protetto insieme a quelle SIP.

**Come si comporta.** All'avvio legge l'elenco della cartella — poche centinaia di byte
— e se trova una versione più recente lo annuncia e fa comparire un pulsante nella barra
dei comandi. **Non scarica niente finché l'operatore non lo chiede**, e non installa
niente da sé: l'installazione è la stessa procedura della prima volta, con la richiesta
di consenso di Windows. Se il server non risponde la cosa finisce nel registro e basta.

## Barra dei comandi, per chi usa il mouse

Sotto la riga della chiamata in corso c'è una fila di pulsanti — *Rispondi*,
*Trasferisci*, *Registra*, *Toni*, *Pausa* ☕, *Impostazioni*, *Account SIP* e gli
altri — che esiste per chi guarda lo schermo: il collega che affianca l'operatore nelle
prime settimane, il responsabile che passa dalla postazione, l'operatore ipovedente che
lavora con l'ingrandimento. **Non toglie niente alla tastiera**: ogni pulsante esegue
lo stesso comando della sua scorciatoia, e la scorciatoia compare nel fumetto insieme
alla frase che dice cosa succede premendo. Sul pulsante resta il solo nome della
funzione, con un pittogramma davanti: i nomi lunghi accanto al tasto — *«Interrompi
registrazione (Alt R)»* — raddoppiavano la larghezza di ogni pulsante, e in una barra
da dodici voci quella lunghezza si paga tutta insieme.

**Due comandi non stanno nella barra, ed è deliberato.** La **pausa** vive in alto a
destra, a sinistra dell'orologio del servizio: riguarda il turno e non la chiamata in
corso, e mescolarla a *rispondi* e *trasferisci* la faceva cercare nel posto sbagliato.
L'**elenco delle chiamate** invece è nella barra e c'è sempre, anche durante una
conversazione: il numero da richiamare si cerca proprio quando qualcosa è appena andato
storto.

**Mentre parli, in cima alla finestra c'è un riquadro verde lampeggiante** con
`In linea con: Mario Rossi (4021)`. Prende il posto di quello blu della chiamata
entrante, nello stesso punto e con la stessa forma — cambia solo il colore, che è il
modo più rapido di accorgersi che si è passati da una cosa all'altra senza rileggere.
Sparisce mettendo in attesa: *«in linea con»* deve dire il vero anche a chi lo legge di
sfuggita passando dalla postazione.

**La barra tiene la sua forma per tutta la durata della chiamata.** Mettendo in attesa
non sparisce niente: *Metti in attesa* diventa *Riprendi*, e *Riaggancia* e
*Trasferisci* restano dov'erano — funzionano benissimo anche su una chiamata in attesa.
Registrazione, toni e microfono hanno invece bisogno di voce che passa: restano
visibili ma spenti, e il fumetto dice perché. È l'unico caso in cui la barra mostra
qualcosa che non si può premere, e vale solo mentre la chiamata è in attesa: fuori da
una chiamata quei pulsanti non ci sono affatto.

**Per il resto compaiono solo i pulsanti che funzionano.** La barra è l'elenco di ciò
che si può fare *adesso*: mentre una chiamata squilla ci sono *Rispondi* e *Rifiuta*,
in conferenza compare *Sciogli conferenza*. Le coppie sono un pulsante solo — *Registra*
e *Interrompi registrazione* non compaiono mai insieme — perché il pulsante dice
l'azione possibile e non lo stato in cui ci si trova.

**Il trasferimento è il punto in cui i pulsanti dicono più della tastiera.** Da
tastiera la differenza fra trasferimento cieco e con annuncio sta nella doppia
pressione rapida — una destrezza che si impara. Con il mouse sono due pulsanti
distinti, *Trasferisci subito* e *Annuncia e trasferisci*, che compaiono appena c'è una
destinazione scritta nel campo.

**Per chi non vede, la barra è una tappa sola** del giro di tabulazione, e lo resta
qualunque cosa contenga: dentro ci si muove con le frecce. Senza questa scelta il
numero di `Tab` per raggiungere il campo di composizione sarebbe cambiato sotto le dita
a ogni chiamata che arriva, che si risponde e che si chiude.

### Quanto squilla una chiamata entrante

La suoneria suona **finché la chiamata è in squillo**, con la cadenza telefonica
italiana — un segnale, quattro secondi di silenzio — e si ferma da sé alla risposta,
al rifiuto, alla rinuncia del chiamante e a qualunque altro esito. Un segnale suonato
una volta sola non direbbe nulla di quello che succede dopo, e su un posto operatore
non vedente il silenzio è indistinguibile da una chiamata finita.

Per tutto quel tempo, in cima alla finestra, un **riquadro blu lampeggiante** dice chi
sta chiamando — `Chiamata da: Mario Rossi (4021)` — e si spegne insieme alla suoneria.
Riporta le stesse informazioni dell'annuncio vocale, scritte per gli occhi: nome
anagrafico e numero in cifre invece del nome fonetico e delle cifre lette una per una.
Il numero compare accanto al nome solo per chi è in rubrica, come nell'annuncio: un
nome che arriva dalla segnalazione lo sceglie chi chiama, e mostrarlo con il numero a
fianco gli darebbe l'aspetto di una scheda verificata. Il lampeggio è a meno di un
lampo al secondo, largamente sotto il tetto di tre che le linee guida sulle crisi
fotosensibili fissano.

**Per quanti secondi squilli non lo decide BlindPhone: lo decide il centralino.** È
lui a chiudere la chiamata con un `CANCEL` quando scade il tempo di squillo
configurato per l'interno o per la coda, e da quel momento la postazione tace. Se una
chiamata smette di squillare troppo presto, il parametro da cambiare è quello del
centralino — su Yeastar, il *ring timeout* dell'interno o della coda.

## Codec audio

Lo stack SIP implementa quattro codec: **G.722**, **G.711 A-law (PCMA)**,
**G.729** e **G.711 μ-law (PCMU)**. I primi tre sono quelli di uso corrente in
Europa e sono selezionabili e ordinabili da *Impostazioni → Audio → Codec audio
della conversazione*; PCMU resta disponibile solo per le interconnessioni
internazionali che lo richiedano.

L'ordine predefinito è **G.729, G.722, G.711 A-law**.

| Codec | Banda | Occupazione | Quando conviene |
|---|---|---|---|
| **G.729** | 8 kHz, stretta | 8 kbit/s | Predefinito. È quello che i centralini di riferimento negoziano nella pratica. Voce più metallica, richiede DTMF RFC 4733 |
| **G.722** | 16 kHz, larga | 64 kbit/s | Voce più naturale. **Unico a banda larga: va mantenuto in elenco** (vedi sotto) |
| **G.711 A-law** | 8 kHz, stretta | 64 kbit/s | Riserva universale: funziona con qualunque apparato |

> **Sulla conformità EN 301 549 §6.1.** La norma richiede che l'apparato *sia in
> grado* di codificare e decodificare la banda larga, non che la negozi per prima.
> Riordinare l'elenco è quindi legittimo e non compromette nulla; **rimuovere G.722
> fa perdere la conformità**. La conseguenza pratica dell'ordine attuale è solo che
> la conversazione avverrà in banda stretta quando il centralino non offre di
> meglio. Un test automatico protegge questa distinzione.

Il G.729 ha due particolarità gestite dal percorso audio: codifica blocchi da
**10 ms** anziché lavorare campione per campione, e con l'Annex B invia frame di
comfort noise da 2 byte durante i silenzi. La diagnostica li conta separatamente
come "pacchetti scartati": con G.729 un valore diverso da zero è normale.

L'ordine conta: il centralino sceglie il primo codec dell'elenco che sa gestire.
Nell'elenco si usa la **barra spaziatrice** per attivare o disattivare un codec e
**Alt+freccia su / Alt+freccia giù** per spostarlo — il trascinamento con il mouse,
unico metodo previsto dalla maggior parte dei softphone, è esattamente il gesto che
un operatore non vedente non può fare.

Disattivando ogni codec a banda larga il pannello avvisa che la conformità a
EN 301 549 §6.1 non è più dimostrabile. L'avviso non blocca — ci sono centralini
che non offrono G.722 — ma la conseguenza va conosciuta, perché nel mercato della PA
quel requisito è contrattuale.

## Stato dell'implementazione

**Completo e collaudato**

- Dominio, macchina a stati della chiamata, pipeline di annuncio con priorità,
  accorpamento, anti-ripetizione e soppressione contestuale
- Trasferimento a combinazione unica con recupero automatico in tutti gli esiti
- Conferenza a tre con missaggio locale, indipendente dal centralino
- Account SIP multipli con predefinito e cambio rapido senza riavviare
- Preconfigurazione esportabile e blocco delle impostazioni a due livelli
- Segnalazione all'assistenza in un gesto, con cattura automatica degli errori gravi
- Segnalazione SIP su TLS con richiesta di fiducia sui certificati non verificabili
- Connettore Yeastar: token con rinnovo proattivo, WebSocket con heartbeat e
  riconnessione, interrogazione adattiva, correlazione SIP/API con degrado sicuro
- Segnalazione SIP: registrazione, chiamate, attesa, REFER cieco e con Replaces,
  DTMF, interpretazione dei NOTIFY di esito
- Scelta e ordinamento dei codec audio, con riordino da tastiera e avviso di
  conformità (vedi sotto)
- Audio a quattro vie con gestione del collegamento a caldo, earcon a sintesi con
  posizionamento stereo
- Bridge NVDA e JAWS, voce interna SAPI su dispositivo dedicato, disattivabile da
  *Impostazioni → Accessibilità* quando duplicherebbe lo screen reader
- Scorciatoie locali e globali, rilevamento conflitti a tre livelli
- Rubrica SQLite con ricerca testuale e T9, importazione CSV, sorgente Yeastar
- Elenco delle ultime chiamate su archivio separato, diviso in perse, ricevute ed
  effettuate, con conservazione configurabile e cancellazione manuale
- Archivi locali cifrati a riposo con SQLCipher, chiave sotto DPAPI, conversione
  automatica di quelli nati in chiaro
- Shell WPF accessibile con regioni dinamiche UI Automation

- **Percorso audio della conversazione**: `SipAudioEndPoint` implementa
  `IAudioSource` e `IAudioSink` su WASAPI, con ricampionamento alla frequenza del
  codec negoziato e framing a 20 ms. Scritto su misura invece di usare
  `SIPSorceryMedia.Windows`, che nella versione compatibile con SIPSorcery 10
  richiede .NET 10 e nella precedente porta una vulnerabilità nota
  ([GHSA-28gm-jrmw-xx93](https://github.com/advisories/GHSA-28gm-jrmw-xx93)) —
  inaccettabile per un prodotto destinato alla PA. Serviva comunque un endpoint
  proprio, perché quello standard non offre l'instradamento su dispositivi distinti.
- **Strumento diagnostico** a riga di comando (vedi sotto)

**Da completare**

- Storico annunci come finestra WPF: il comando è assegnato (`Ctrl+Shift+A`) ma
  risponde «funzione non ancora disponibile»
- Sorgenti rubrica LDAP e ripristino da backup SQL
- Parcheggio, risposta per assente
- Decisione su RTT (EN 301 549 §6.2): implementarlo o dichiarare lo scostamento
  nell'Accessibility Conformance Report

## Rubrica

**`F6`** apre il pannello rubrica; la configurazione e l'importazione stanno in
*Impostazioni → Rubrica* (`Ctrl+,`, poi `Ctrl+Tab` fino alla scheda).

### Consultazione (`F6`)

Il modello di interazione è: si apre, si digita, si scende con la freccia, si preme
Invio. Non c'è un pulsante «cerca» — sarebbe un passaggio in più su un'operazione
che si ripete centinaia di volte al giorno.

| Tasto | Azione |
|---|---|
| `Freccia giù` dal campo di ricerca | Scende nei risultati |
| `Freccia su` sulla prima riga | Torna al campo di ricerca |
| `Invio` | Chiama il numero predefinito |
| `Ctrl+Invio` | Legge tutti i recapiti del contatto |
| `F9` | Usa come destinazione del trasferimento in corso |
| `Ctrl+D` | Legge la scheda completa |
| `ESC` | Chiude |

Il testo già digitato nel campo di composizione viene portato dentro come ricerca
iniziale: chi inizia a scrivere un nome nel dialer e poi preme `F6` non deve
ribatterlo.

La ricerca parte **300 ms dopo l'ultima battitura**, non a ogni tasto: cercare a
ogni lettera produrrebbe un annuncio del conteggio per ciascuna, sovrapposto
all'eco delle lettere stesse. Il conteggio viene annunciato, e con un solo
risultato la voce lo nomina direttamente invece di dire «1 risultato».

Con un trasferimento in corso il comportamento cambia: `Invio` non chiama ma imposta
la destinazione, perché chiamare creerebbe una terza linea e confonderebbe lo stato.

### Importazione

L'importazione da CSV è in quattro passi visibili contemporaneamente, non a
procedura guidata: uno schermo solo si esplora in sequenza con le frecce, mentre
una successione di finestre costringe a ricostruire a memoria dove si è arrivati.

1. **File** — le colonne vengono riconosciute dai titoli; delimitatore e codifica
   sono rilevati e mostrati
2. **Abbinamento** — una casella per informazione, precompilata dal riconoscimento
3. **Anteprima** — le prime voci *come le leggerà lo screen reader*, per accorgersi
   di una colonna sbagliata prima di importare ottomila righe
4. **Importazione** — con conteggio finale annunciato

Oltre ai campi consueti, il modello contatto ha **Tipo**, **Relativo**,
**Padiglione** e **Piano**. Non sono note libere: sono ricercabili e vengono
pronunciati nella lettura del contatto, perché un centralino di struttura complessa
riceve tanto la domanda "qual è il numero" quanto "dove si trova". Digitando
`padiglione b` nella ricerca si ottengono tutte le voci di quell'edificio.

Una voce viene letta così:

```
Rossi Mario, Medico, padiglione B, piano 2, Ospedale San Giovanni,
riferimento Direzione Sanitaria
```

L'ordine è per utilità decrescente: chi ascolta a velocità alta interrompe appena ha
l'informazione che cercava.

Le righe **senza numero vengono ignorate**: su un'interfaccia sonora ogni voce
inutile nei risultati costa tempo di ascolto. Il conteggio delle righe scartate
compare nel log.

Per provare l'abbinamento su un file senza aprire l'applicazione:

```bash
dotnet run --project tools/BlindPhone.Diagnostics -- rubrica C:\rubrica.csv
```

## Ultime chiamate

**`F11`** apre l'elenco. Risponde alla domanda che un centralinista si fa più volte
al giorno — «chi mi ha cercato mentre ero occupato?» — e che per chi non vede non ha
altra risposta: non c'è nessuna icona lampeggiante da notare di sfuggita, quindi
senza questo elenco una chiamata persa sparisce del tutto.

**Tre pannelli e non un elenco unico con una colonna «tipo».** Le tre domande sono
diverse fra loro, e quella sulle perse è l'unica urgente: estrarla da un elenco
misto, riga per riga con la sintesi, costerebbe il tempo che serve a richiamare. Le
perse sono la prima scheda e ricevono il fuoco all'apertura; `Ctrl+Tab` passa alle
altre.

| Tasto | Azione |
|---|---|
| `Invio` | Richiama il numero della riga |
| `F9` | Usa la riga come destinazione del trasferimento in corso |
| `Ctrl+Tab` | Passa da perse a ricevute a effettuate |
| `F1` | Guida sull'elenco |
| `ESC` | Chiude |

Ogni pannello è una tabella come la rubrica, letta **una cella alla volta**: quando,
chi, numero, e una quarta colonna che cambia — coda di provenienza fra le perse,
durata fra le ricevute, esito fra le effettuate. Il numero viene letto cifra per
cifra e il momento in forma discorsiva («oggi alle 9 e 40», «ieri alle 16»): `09:40`
viene pronunciato in modi diversi da voci diverse, e una data compatta come `05/08`
alcune sintesi la leggono come una frazione. Si mostrano le 50 chiamate più recenti
per pannello.

La **durata si misura dalla risposta**, non dallo squillo, e una chiamata mai
risposta ha durata zero con il motivo al posto suo: «occupato», «nessuna risposta»,
«rifiutata».

### Conservazione

L'elenco contiene numeri chiamati e chiamanti, cioè dati di traffico. Sta in
`history.db`, **file separato da quello della rubrica**: svuotare l'uno non tocca
l'altro, e chi verifica la conformità ne guarda uno solo.

*Impostazioni → Chiamate* sceglie dopo quanto le chiamate escono dall'elenco — 1
giorno, 1 settimana, 1 mese (predefinito) o **mai** — e la pulizia avviene a ogni
avvio. «Mai» è la scelta corretta quando la conservazione è regolata da una procedura
dell'ente: il periodo lo decide quella. Un valore fuori elenco viene arrotondato alla
scelta più vicina **non più corta**, perché arrotondare per difetto cancellerebbe
chiamate che l'operatore si aspettava di trovare ancora.

La cancellazione manuale è in due punti: nella finestra, dove si sceglie fra il solo
pannello aperto e tutto l'elenco, e in *Impostazioni → Chiamate*, che elimina tutto.
Entrambe sono definitive e lo dicono prima.

## Conferenza a tre

**`F7`** unisce nella stessa conversazione l'interlocutore in linea e quello in
attesa. Non compone numeri: si costruisce sulle due chiamate che l'operatore ha già,
che è la situazione in cui ci si trova a metà di un trasferimento con annuncio quando
l'interno chiede di parlare anche lui con il chiamante.

| Gesto | Effetto |
|---|---|
| `F4`, poi il numero, poi `F7` | Apre la conferenza fra chi era in linea e chi era in attesa |
| `F7` in conferenza | La scioglie: resta in linea chi c'era prima, l'altro torna in attesa |
| `F3` in conferenza | Chiude **entrambe** le linee |
| `F4` in conferenza | Mette in attesa tutti e due insieme; `F4` o `F7` li riportano in linea |
| `Ctrl+M` in conferenza | Silenzia il solo microfono dell'operatore: gli altri due continuano a sentirsi |

L'annuncio di apertura nomina entrambi i partecipanti prima di dire cosa fanno i
tasti: una conferenza aperta sulla coppia sbagliata va sciolta subito, e per
accorgersene serve sentire con chi è stata aperta. Se uno dei due riaggancia, la
conferenza si scioglie da sola e la voce dice chi ha chiuso **e con chi si resta** —
le due metà sono la stessa domanda, perché su una linea silenziosa non c'è altro modo
di sapere se si sta ancora parlando con qualcuno.

I toni non si inviano durante una conferenza: andrebbero a una sola delle due linee
senza che si possa dire quale, e su un menu automatico una cifra nel ramo sbagliato
non si annulla. Il comando lo dice, invece di rispondere «nessuna chiamata in corso»
mentre due persone parlano.

### Il missaggio è locale, non del centralino

Il posto operatore fa da ponte: a ciascuna linea manda la voce dell'operatore sommata
a quella dell'altro partecipante. Il vantaggio è che **funziona su qualunque
centralino**, senza sale conferenza da configurare, senza codici di servizio diversi
per ogni marca e senza dipendere dal piano di licenza. La contropartita è che serve
un percorso audio reale: senza dispositivi il comando lo dichiara invece di aprire
una conferenza muta, che a orecchio è indistinguibile da una riuscita in cui nessuno
parla.

La regola del missaggio è che ogni linea riceve tutto **tranne se stessa**.
Rimandarle il proprio audio produrrebbe un'eco ritardata del giro di rete, che è il
difetto classico delle conferenze fatte in casa e rende la conversazione
impraticabile in pochi secondi. Le due linee possono aver negoziato codec diversi —
esterno in G.729 a 8 kHz, interno in G.722 a 16 kHz — quindi il contributo viene
ricampionato con filtro anti-alias, come ogni altra conversione del percorso audio.

Con una conferenza aperta il riepilogo diagnostico riporta due contatori in più per
linea: i pacchetti partiti con il missaggio e quelli partiti senza, perché dall'altra
gamba non era arrivato nulla da metterci dentro. Se il secondo cresce con lo stesso
ritmo dei pacchetti inviati, quel partecipante non sta sentendo l'altro.

## Toni per i menu automatici

Chiamando un ente che risponde con un centralino automatico — «digita 1 per l'ufficio
tecnico» — le cifre servono **in linea**, non nel campo di composizione. Ma lo stesso
campo serve a scrivere il numero che un chiamante sta dettando, senza che gli arrivi
in linea: è la sequenza di trasferimento più usata del prodotto. Le cifre non possono
andare in tutti e due i posti.

La risposta è una **modalità esplicita**, mai un'interpretazione automatica: un tasto
che a volte scrive e a volte suona in linea non è usabile da nessuno, e meno che mai
da chi non vede lo schermo.

| Gesto | Effetto |
|---|---|
| `Ctrl+Maiusc+D` | Entra o esce dalla modalità toni. Dentro, ogni cifra parte in linea e viene detta |
| `ESC` in modalità toni | Esce: è la via d'uscita che si prova per istinto |
| `Ctrl+Maiusc+Invio` | Manda in linea, uno dopo l'altro, i toni di quanto è scritto nel campo di composizione |

Ogni tono inviato viene **pronunciato**: i toni RFC 4733 viaggiano fuori dall'audio
della conversazione, quindi chi li invia non li sente, e su un menu automatico una
cifra sbagliata porta in un ramo da cui spesso non si torna indietro. Asterisco e
cancelletto si dicono per nome.

La modalità **si spegne da sola quando la chiamata finisce**: restarci senza saperlo
significherebbe mandare in linea le cifre del numero successivo. In modalità toni gli
altri comandi continuano a funzionare — riagganciare, mettere in attesa, chiedere lo
stato — perché altrimenti l'operatore resterebbe chiuso dentro un menu senza uscite.

Il tastierino numerico è compreso, ed è quello che si usa davvero: le dita sono già lì.

## Stato degli interni

L'operatore vede se un interno è **libero, occupato, squilla, in non disturbare,
assente o fuori servizio**, in tre punti: la colonna *Stato* della rubrica, la terza
colonna dei contatti proposti sotto il campo di composizione, e **`Ctrl+I`** che lo
chiede a voce sul numero digitato o sul contatto evidenziato. È testo, mai colore.

`Ctrl+I` è la domanda diretta prima di passare una chiamata: risponde «interno 4021,
occupato» e aggiunge «meglio non passargli una chiamata adesso» quando lo stato lo
sconsiglia. Se lo stato non è ancora noto — sulla via SIP si conosce solo dopo aver
sottoscritto quell'interno — sottoscrive e attende un secondo e mezzo la risposta del
centralino; se non arriva lo dice, invece di tacere.

Quando la ricerca per nome trova un solo contatto, la voce lo nomina con il suo
interno e **aggiunge lo stato solo se sconsiglia di passare la chiamata** — «Mario
Rossi, interno 4021, occupato». Sentirsi dire «libero» a ogni ricerca allungherebbe
l'annuncio più frequente del prodotto senza cambiare alcuna decisione.

«Fuori servizio» significa telefono spento o scollegato, ed è la destinazione peggiore
per un trasferimento: la chiamata squilla nel vuoto e il chiamante torna indietro dopo
il timeout, quando l'operatore lo ha già salutato. Lo stesso stato alimenta l'avviso
prima di un trasferimento verso una destinazione non disponibile.

### Due strade, secondo quello che il centralino offre

| Strada | Quando si usa | Come funziona |
|---|---|---|
| **API del centralino** | Piano di licenza con API abilitate | Eventi presenza (30009) e registrazione interno (30007): il PBX manda tutto da solo, per tutti gli interni |
| **Sottoscrizioni SIP** | Ripiego automatico quando l'API non c'è | `SUBSCRIBE` al pacchetto `dialog` (RFC 4235), la stessa strada dei tasti di occupato dei telefoni da tavolo |

La scelta è automatica all'avvio e non richiede configurazione. Il ripiego SIP non
è una versione ridotta: dà gli stessi stati, e funziona su qualunque centralino
perché non chiede nulla oltre al SIP.

L'unica differenza pratica è che ogni interno sorvegliato costa una sottoscrizione al
PBX, quindi **si sottoscrive solo ciò che l'operatore ha davanti** — i risultati della
ricerca in rubrica, i contatti proposti sotto il campo di composizione — fino a un
massimo di 24, e il resto si lascia andare. Sottoscrivere l'intera rubrica di un ente
significherebbe centinaia di sottoscrizioni per un posto operatore che alla volta ne
guarda cinque.

Lo stato **vive solo in memoria**: una presenza conservata fra un avvio e l'altro
sarebbe una presenza vecchia, e al primo avvio del mattino direbbe «libero» perché
così era ieri sera. La conseguenza è che dopo l'avvio si conosce lo stato solo degli
interni di cui è arrivata notizia; per gli altri la cella resta vuota, che è la verità.

Senza nessuna delle due strade — motore telefonico simulato, account SIP non
configurato — la funzione non esiste, e la rubrica lo dice all'apertura una volta sola
invece di lasciare una colonna vuota che sembra un guasto. Quale strada sia in uso lo
riporta il riepilogo diagnostico.

> **Verificato fin dove si poteva.** La traduzione dei dialoghi SIP in stato
> dell'interno è coperta da test, e il percorso completo — corpo `dialog-info` del
> centralino, parser RFC 4235, regola, parola pronunciata — è stato provato su cinque
> documenti realistici. Il comportamento del singolo firmware davanti a un `SUBSCRIBE`
> non è invece verificabile senza un PBX reale: ogni errore lì degrada verso «stato
> sconosciuto», mai verso un'informazione sbagliata, e non tocca la possibilità di
> telefonare.

## Account SIP multipli

*Impostazioni → Account SIP* per configurarli, **`Ctrl+Alt+A`** per passare da uno
all'altro.

Serve a chi copre due enti e a chi lavora su una postazione condivisa a turni con
colleghi che hanno interni diversi. Prima l'unico modo di cambiare era riscrivere
quattro campi e una password, con la postazione fuori servizio nel frattempo.

| Gesto | Effetto |
|---|---|
| `Ctrl+Alt+A` | Apre l'elenco: frecce per scorrerlo, `Invio` per attivare |
| *Usa all'avvio* | Decide con quale account BlindPhone parte domani mattina |
| *Impostazioni → Account SIP* | Aggiunge, modifica ed elimina |

**Attivare e rendere predefinito sono due gesti distinti**, e la distinzione non è
pedanteria: si può lavorare un pomeriggio sull'account di un secondo ente senza
volerlo ritrovare il giorno dopo. L'elenco ha due colonne separate, *In uso* e
*All'avvio*.

Il cambio **non funziona con una chiamata in corso**, e viene rifiutato invece che
chiesto in conferma: cambiare account chiude una registrazione e ne apre un'altra, e
con essa cadrebbe la conversazione. Un tasto premuto per abitudine non deve poter
far cadere una chiamata con un secondo tasto premuto altrettanto per abitudine.

Il motore telefonico si sostituisce **senza riavviare l'applicazione**: dietro
`ITelephonyEngine` c'è un involucro commutabile che tiene ferma la superficie su cui
mezzo prodotto è agganciato. Se il nuovo account non parte si torna sul precedente,
perché un cambio fallito che lasciasse la postazione senza motore la renderebbe muta
senza modo di rimediare.

Ogni account ha la propria password nel deposito protetto, sotto una chiave costruita
sul suo identificativo: rinominarlo non gliela fa perdere. **Una configurazione
scritta da una versione precedente viene migrata al primo avvio** conservando la
chiave storica del primo account — cambiarla avrebbe messo fuori servizio ogni
postazione già configurata.

## Segnalazione all'assistenza

**`Ctrl+Alt+I`** apre la segnalazione da qualunque punto del programma.

Il pacchetto serve quando le cose vanno male, e in quel momento la sequenza «apri le
impostazioni, trova la scheda, premi il pulsante, scegli una cartella, ritrova il file,
allegalo a una mail» non si porta a termine — meno che mai senza vedere lo schermo. Il
comando la riduce a un gesto: si descrive il problema con parole proprie e si preme
Invio.

| Cosa contiene | Perché |
|---|---|
| La descrizione scritta dall'operatore | È la parte più utile e l'unica che solo lui può scrivere |
| Riepilogo diagnostico | Versione, configurazione, stato di telefono e audio. **Senza credenziali** |
| Rapporti di errore registrati | Tipo, messaggio e traccia dello stack. **Senza numeri di telefono** |
| Ultimi sette file di registro | **Contengono numeri chiamati e chiamanti: dati di traffico** |

L'avviso sui dati personali compare **prima** del pulsante e non in una conferma
successiva: chi invia deve saperlo prima di premere, non dopo aver già deciso.

### Cattura automatica degli errori

Un guasto che si presenta una volta ogni tre giorni, su una postazione dove nessuno
guarda lo schermo, è irriproducibile per definizione: quando l'assistenza arriva la
finestra di errore è chiusa e l'unica descrizione disponibile è «a un certo punto non
funzionava più».

BlindPhone intercetta gli errori gravi da **tre sorgenti** — l'interfaccia, il
processo, e le attività di sfondo i cui errori nessuno ha atteso — e ne scrive un
rapporto nell'istante in cui accadono. Le attività di sfondo non sono una categoria
teorica: riconnessione al centralino, rinnovo dei token, flusso eventi e operazioni
audio girano tutte lì, e un errore in quei punti non si manifesta se non come una
funzione che smette di aggiornarsi.

Al riavvio successivo la voce dice **quanti** rapporti ci sono, **quando** è successo
e **con quale tasto** mandarli. Il rapporto resta sulla postazione finché l'operatore
non lo invia, e viene eliminato solo dopo un invio riuscito.

### Dove finisce il pacchetto

*Impostazioni → Diagnostica → Segnalazione all'assistenza*

| Configurazione | Comportamento |
|---|---|
| Indirizzo HTTPS | Caricamento diretto. Solo HTTPS: su HTTP il pacchetto viaggerebbe in chiaro con dentro i numeri chiamati |
| Indirizzo di posta | Messaggio preparato con l'allegato, che **parte solo quando lo manda l'operatore** |
| Nessuno dei due | File sul desktop e cartella aperta con il file già selezionato |

**Nessun recapito è fornito con il prodotto**, e non è una dimenticanza: un softphone
che spedisce da solo i registri di traffico a un indirizzo scelto dal fornitore è un
trasferimento di dati personali che nessuno ha autorizzato. Lo configura l'ente.

## Segnalazione cifrata e certificati

*Impostazioni → Account SIP → Trasporto → TLS*

Con TLS la segnalazione SIP viaggia cifrata: credenziali, numeri chiamati e chiamanti
non sono più leggibili da chi sta sulla rete. Il canale è **client puro** — BlindPhone
apre connessioni verso il centralino e non ne accetta di entranti — quindi non serve
un certificato proprio.

> **Correzione di un difetto grave.** Fino a questa versione il ramo TLS registrava un
> avviso e apriva un canale **TCP in chiaro**: chi configurava «TLS» otteneva una
> segnalazione non cifrata, e l'unico modo di accorgersene era leggere il registro o
> mettere uno sniffer sulla rete. Adesso il canale è reale, e se non si apre la
> registrazione fallisce con un messaggio esplicito invece di ripiegare — ripiegare
> significherebbe trasmettere in chiaro le credenziali di chi ha chiesto il contrario.

Aprire il canale non basta: lo stack sceglie il trasporto in base a quello **dichiarato
nell'indirizzo**, e un indirizzo senza parametro vale UDP. Gli indirizzi del registrar e
delle destinazioni portano ora `transport=tls`, altrimenti il canale cifrato resterebbe
aperto e mai usato.

### Quando il certificato non è verificabile

Nella PA italiana il centralino espone quasi sempre un certificato autofirmato o emesso
da un'autorità interna all'ente. Le due risposte estreme sono entrambe sbagliate:
rifiutare senza appello rende TLS inutilizzabile, accettare qualunque cosa toglie a TLS
la parte che lo rende utile — senza verificare chi c'è dall'altra parte, la cifratura
protegge la conversazione con chiunque si sia messo in mezzo.

BlindPhone mostra il certificato e chiede. Tre scelte, nell'ordine in cui compaiono:

| Scelta | Quando |
|---|---|
| **Non fidarti** *(predefinita)* | Non ti aspettavi il messaggio. Il collegamento non viene stabilito |
| **Fidati solo adesso** | Stai provando una configurazione. La domanda torna al prossimo avvio |
| **Fidati sempre** | Hai confrontato l'impronta con chi amministra il centralino |

La descrizione dice **prima cosa non torna** e poi di chi si tratta, perché è
l'informazione che decide. L'impronta si legge **a gruppi di quattro caratteri**: è
l'unica verifica che un operatore può fare davvero — farsela dettare e confrontarla — e
quaranta caratteri esadecimali di fila non si confrontano con niente.

Ogni decisione vale per **quel certificato su quel server**: non esiste «fidati di
questo centralino qualunque cosa presenti», che sarebbe disattivare la verifica con un
nome più rassicurante. La stessa politica vale per SIP, per l'API REST e per il
WebSocket degli eventi — sono la stessa macchina, e chiedere tre volte insegnerebbe solo
a rispondere di sì senza ascoltare.

Le approvazioni vivono in `certificati-fidati.json`, **separato da `settings.json`**:
quel file è esportabile e condivisibile, e una decisione di fiducia che viaggiasse con
la configurazione verrebbe applicata su macchine il cui operatore non l'ha mai presa.

## Preconfigurare le postazioni

*Impostazioni → Configurazione*

Configurare una postazione a mano significa percorrere otto schede e una cinquantina
di campi. Su venti postazioni è una giornata, e almeno una avrà un parametro diverso
dalle altre — di solito quello che poi produce la segnalazione che nessuno riesce a
riprodurre altrove.

L'esportazione produce un file `.bpcfg` con le sezioni scelte, i profili dei tasti
rapidi e il criterio di blocco. Messo in `%ProgramData%\BlindPhone`, viene applicato
al primo avvio: si installa il programma, si lascia il file, la postazione è
configurata.

**Le sezioni si scelgono, ed è la parte che lo rende usabile davvero.** Lasciando
fuori *account SIP* si ottiene una configurazione applicabile anche a postazioni già
in servizio, che mantengono il proprio interno: l'ufficio IT distribuisce audio,
accessibilità e tasti rapidi senza cancellare l'interno di venti operatori.

Il **numero di revisione** permette di aggiornare una configurazione già distribuita:
un pacchetto viene applicato solo se ha revisione più alta di quella già applicata.
Senza, l'unica alternativa sarebbe riapplicarlo a ogni avvio, cancellando le
regolazioni fatte dall'operatore nelle sezioni deliberatamente lasciate libere.

**Nessuna password viaggia nel pacchetto.** I segreti vivono sotto DPAPI, legati
all'utente e alla macchina: metterli in un file li renderebbe leggibili altrove, che
è esattamente ciò da cui DPAPI protegge. Restano l'unico passaggio manuale su ogni
postazione.

Il dettaglio operativo — installazione senza interazione, criteri di gruppo — è in
[docs/INSTALLAZIONE.md §8](docs/INSTALLAZIONE.md).

## Bloccare le impostazioni

Due livelli, e la differenza va conosciuta prima di sceglierne uno.

| Livello | Dove vive | Che cosa garantisce |
|---|---|---|
| **Dell'utente** | `settings.json` | Protegge dalla modifica **accidentale**: chi sa dov'è il file può cancellare tre righe |
| **Di postazione** | `HKLM\SOFTWARE\Policies\BlindPhone` | Un utente senza privilegi amministrativi non lo tocca. È l'unico vincolo vero |

Il secondo vince sempre sul primo, **anche quando è meno restrittivo**: un criterio di
postazione che dichiara il pannello aperto è una decisione di chi amministra la rete,
e un file nella cartella dell'utente non deve poterla contraddire in nessuna
direzione.

> Fingere che il primo livello sia una barriera di sicurezza sarebbe peggio che non
> averlo: chi amministra prenderebbe una decisione basandosi su una garanzia che non
> esiste.

| Modalità | Comportamento |
|---|---|
| **Aperto** | L'operatore modifica quello che vuole |
| **Consultabile** | Le schede si leggono, il salvataggio è rifiutato |
| **Chiuso** | Il pannello non si apre senza la password dell'amministratore |

**«Consultabile» è quella che serve quasi sempre.** Un pannello che non si apre
affatto toglie all'operatore anche la possibilità di *leggere* come è configurata la
sua postazione — e quando chiama l'assistenza, la prima domanda che gli viene fatta è
esattamente quella. Le schede bloccate restano raggiungibili e la loro etichetta lo
dice: «Account SIP (bloccata)».

Le sezioni si bloccano separatamente, perché il caso frequente non è «blocca tutto»
ma «account SIP e centralino li decide l'ufficio IT, il resto lo regola l'operatore».
Voce, dispositivi audio e tasti rapidi sono la parte più personale del prodotto.

Il **messaggio dell'amministratore** va compilato: è l'unica parte del blocco che
l'operatore sente pronunciare. «Impostazione bloccata» lo lascia senza sapere a chi
rivolgersi, «Configurazione gestita dall'ufficio IT, interno 4000» gli dice cosa fare.

Lo sblocco con password vale per la **sessione corrente** e non viene scritto: chi
sblocca per correggere un parametro non lascia la postazione sbloccata per sempre.
La password è conservata come sola impronta — PBKDF2 con SHA-256, duecentomila
iterazioni, sale casuale — e senza una password configurata il pannello chiuso non si
apre affatto, perché altrimenti basterebbe premere Invio su un campo vuoto.

## Diagnostica

*Impostazioni → Diagnostica*, oppure **`Ctrl+Alt+D`** che ci porta direttamente.

> **In questa versione di prova il registro parte su «tutto».** Modalità debug attiva
> e dettaglio al massimo, messaggi SIP scambiati con il centralino compresi. È il
> rovesciamento della scelta precedente, e la ragione è il contesto: una beta viene
> installata per trovare difetti, e un difetto che si presenta una volta ogni tre
> giorni — su una postazione dove nessuno guarda lo schermo — si indaga soltanto se il
> registro c'era già quando è successo. Accenderlo dopo la segnalazione significa
> chiedere di riprodurre un guasto che non si riproduce a comando.
>
> **Il prezzo va conosciuto:** il file cresce di decine di megabyte per turno e riporta
> i numeri chiamati e chiamanti per intero — dati personali. Dove la policy dell'ente
> lo richiede, il dettaglio si abbassa dal pannello e **la scelta viene conservata**:
> la migrazione che porta a «tutto» avviene una volta sola, non a ogni avvio. Alla
> fine della beta il predefinito torna a *Normale*.

Il pannello ricorda a ogni apertura che la modalità debug è accesa, e dice che in
questa versione è così di proposito: senza quella precisazione, chi trova il dettaglio
al massimo lo scambia per una dimenticanza e lo abbassa, togliendo alla beta l'unica
cosa che le serve.

| Funzione | A cosa serve |
|---|---|
| Modalità debug | Alza il dettaglio e aggiunge i contatori del percorso audio. **Attiva in beta** |
| Dettaglio del registro | Cinque livelli, da «solo errori» a «tutto». **Vale da subito, senza riavviare** |
| Giorni di conservazione | Da allineare alla policy di conservazione dell'ente |
| Riepilogo diagnostico | Versione, configurazione, telefono, audio, contatori. Senza credenziali |
| Copia negli appunti / Salva su file | Il riepilogo pronto da incollare in una segnalazione |
| Apri la cartella dei registri | Esplora risorse sulla cartella dei log |
| Pacchetto per l'assistenza | Uno `.zip` con riepilogo e ultimi 7 registri, previa conferma |
| Segnala un problema adesso | La segnalazione completa, con descrizione e invio (`Ctrl+Alt+I`) |
| Recapiti dell'assistenza | Indirizzo di posta e indirizzo HTTPS di caricamento |
| Errori registrati | Quanti ce ne sono in attesa, e il comando per eliminarli senza inviarli |
| Certificati approvati | Quali sono e il comando per dimenticarli, quando il certificato del centralino cambia |

Il livello del registro si applica **mentre lo si cambia**, perché il momento in cui
serve alzarlo è quello in cui il guasto si sta manifestando — con qualcuno in linea, e
un riavvio farebbe cadere la chiamata. Chiudendo con *Annulla* il livello torna
com'era.

Con una chiamata in corso il riepilogo riporta i contatori del percorso audio:
pacchetti inviati e ricevuti, scartati, svuotamenti del buffer, millisecondi di
silenzio inserito e la quota di silenzio sul flusso riprodotto. Sono i numeri con cui
in questo progetto si sono diagnosticati i guasti audio: se il silenzio inserito è una
frazione consistente della durata, la voce che manca non è mai arrivata al
dispositivo, e il problema sta prima dell'orecchio dell'operatore.

## Guida in linea

**`F1`** apre la guida, e la apre **sull'argomento pertinente**: dal campo di
composizione sulla composizione, dall'elenco della coda sulla coda, dalla rubrica
sulla rubrica, dalle ultime chiamate sulle ultime chiamate, dalla scheda Tasti rapidi
su come riassegnarli.

Diciassette argomenti: la logica del posto operatore, la gestione della chiamata, la
sequenza del trasferimento, la conferenza a tre, composizione e ricerca per nome, coda
e stato agente, rubrica, ultime chiamate, comandi informativi, voce e segnali acustici,
area di notifica, tasti rapidi, impostazioni, cosa fare quando qualcosa non funziona,
gli account SIP multipli, cosa non c'è ancora, e l'elenco completo delle combinazioni.

Con una conferenza in corso `F1` apre l'argomento della conferenza a prescindere da
dove sia il cursore: chi chiede aiuto mentre due persone sono in linea ha una domanda
su quella situazione, non sul pannello che ha davanti.

Frecce per scegliere l'argomento, `Tab` per passare al testo, frecce per leggerlo riga
per riga, `Esc` per chiudere. Il pulsante *Esporta in un file di testo* produce
l'intera guida in righe corte, per la lettura continua o la stampa braille.

**Le combinazioni citate nel testo sono quelle configurate**, non quelle predefinite:
la guida si costruisce dal profilo attivo a ogni apertura. Una guida che dice «premi
F9» a chi ha rimappato il trasferimento manda a premere un tasto che non fa nulla,
proprio mentre un chiamante aspetta.

## Area di notifica e funzionamento in secondo piano

*Impostazioni → Accessibilità → Finestra e area di notifica*

Chiudere la finestra di un softphone significa smettere di ricevere chiamate, e i
chiamanti squillano a vuoto senza che nessuno lo sappia — a schermo non c'è nulla che
lo mostri, e il silenzio è indistinguibile da un momento senza traffico. Per questo la
chiusura della finestra **mette via** BlindPhone invece di chiuderlo: il telefono
resta registrato al centralino e l'icona resta nell'area di notifica.

| Azione | Effetto |
|---|---|
| Chiudi la finestra (`Alt+F4` o la X) | BlindPhone resta in servizio nell'area di notifica, e lo annuncia |
| `Ctrl+Maiusc+B` (globale) | Riapre la finestra da qualunque programma |
| `Windows+B`, poi menu contestuale | Raggiunge l'icona da tastiera: *Apri BlindPhone*, *Esci* |
| `Ctrl+Maiusc+Q` (con la finestra aperta) | Esce davvero. Con una chiamata in corso chiede conferma |
| Chiamata entrante | La finestra torna in primo piano da sola |

**Una copia sola.** Riaprendo il collegamento mentre BlindPhone è già in esecuzione
non parte un secondo processo: quello attivo torna in primo piano e lo annuncia. Due
copie registrerebbero lo stesso interno presso il centralino — con le chiamate che
arrivano a una sola delle due, non sempre la stessa — si contenderebbero le
scorciatoie globali e scriverebbero nella stessa configurazione. Il caso è diventato
normale da quando la finestra si ripone nell'area di notifica: non vedendola,
l'operatore riapre il collegamento. Chiudendo e riavviando subito, invece, il nuovo
processo attende un secondo e mezzo che il precedente liberi il posto e poi parte
normalmente.

Il ritorno in primo piano all'arrivo di una chiamata è l'**unica deroga** alla regola
per cui il focus si sposta solo su azione dell'operatore, e vale solo per le chiamate
entranti che iniziano a squillare: con la finestra riposta non ci sarebbe nulla su cui
agire e i comandi locali non arriverebbero. Chi lavora stabilmente in un altro
programma può disattivarla e rispondere con le scorciatoie globali, che funzionano
senza cambiare finestra.

Tutte e tre le opzioni — icona, chiusura che mette via, ritorno in primo piano — si
disattivano singolarmente, e si applicano subito senza riavviare.

## Tasti rapidi

*Impostazioni → Tasti rapidi* (`Ctrl+,`, poi `Ctrl+Tab` fino alla scheda)

Ogni comando è riassegnabile, in due ambiti indipendenti: **locale** (BlindPhone in
primo piano) e **globale** (funziona anche in secondo piano, registrata presso
Windows). L'elenco è una tabella: le frecce verticali cambiano comando, quelle
orizzontali passano fra le due combinazioni, e la colonna su cui si trova il cursore
decide su quale si agisce.

| Tasto | Azione |
|---|---|
| `Invio` | Riassegna la combinazione della colonna corrente |
| `Canc` | Toglie la combinazione della colonna corrente |

La combinazione non si scrive, **si preme**: si apre un'area di cattura che
intercetta tutti i tasti — compresi `Invio` ed `ESC`, che sono assegnazioni
legittime — e li rilegge a voce a ogni pressione. L'unica via d'uscita è `Tab`, che
per questo è l'unico tasto non assegnabile.

Prima di accettare, la combinazione passa dal rilevamento conflitti a tre livelli:
riservata da Windows (rifiutata), già assegnata a un altro comando di BlindPhone
(sostituzione su conferma, con la combinazione tolta al comando precedente), usata da
NVDA o JAWS (avviso, scelta all'operatore). Le globali già occupate da un altro
programma le rifiuta il sistema, e lo si può sapere solo provando: l'esito viene
annunciato al momento dell'attivazione.

### Profili e salvataggio

Un profilo è una mappa completa dei tasti. `Standard` e `Solo primo piano` sono
forniti con il prodotto e non si sovrascrivono: modificandoli si salva una copia.

Il pulsante **Salva la configurazione dei tasti** offre quattro destinazioni:

| Scelta | Effetto |
|---|---|
| Aggiorna il profilo corrente | Le combinazioni diventano attive **subito**, senza riavviare |
| Salva come nuovo profilo | Crea un profilo con le modifiche e lo attiva, lasciando intatto quello di partenza |
| Esporta su file | Produce un `.json` da importare su un'altra postazione: solo la mappa dei tasti, nessuna credenziale |
| Esporta la scheda dei tasti | Produce un `.txt` in righe corte, per la lettura continua o la stampa braille |

L'applicazione a caldo è deliberata: una scorciatoia si verifica provandola, e
chiedere un riavvio a ogni tentativo renderebbe la configurazione impraticabile.

## Regolazione del buffer audio

*Impostazioni → Audio → Regolazione del buffer audio*

Il valore giusto dipende dalla rete e **non è determinabile in anticipo**: la
configurazione che funziona su rete locale cablata produce interruzioni su Wi-Fi.

| Parametro | Predefinito | Effetto |
|---|---|---|
| **Cuscinetto (jitter)** | 100 ms | Audio accumulato prima di iniziare. Più alto assorbe le irregolarità della rete, ma aggiunge ritardo alla voce |
| **Latenza dispositivo** | 100 ms | Sotto i 50 ms alcune cuffie USB producono scatti a prescindere dalla rete |
| **Capienza massima** | 500 ms | Oltre questa soglia i pacchetti in eccesso vengono scartati, per evitare che il ritardo cresca progressivamente |
| **Durata pacchetti** | 20 ms | Standard della telefonia IP: da cambiare solo se il centralino lo richiede |

Tre preimpostazioni coprono la quasi totalità dei casi: **rete locale** (50 ms),
**prudente** (100 ms), **rete disturbata** (200 ms).

Il compromesso da capire: oltre **150 ms di ritardo complessivo** si comincia a
sovrapporsi all'interlocutore. Su un posto operatore che gestisce centinaia di
chiamate al giorno è un costo concreto, quindi conviene alzare il cuscinetto solo
quanto serve a far scendere a zero il contatore degli svuotamenti.

### Regolare sui numeri, non a orecchio

```bash
dotnet run --project tools/BlindPhone.Diagnostics -- chiamata 200
```

Riporta, secondo per secondo, quanto audio c'è in coda e quante volte il buffer si
è svuotato, e a fine chiamata suggerisce il valore successivo da provare:

| Svuotamenti in 40 s | Lettura |
|---|---|
| 0 | Nessuna interruzione. Si può provare a ridurre per guadagnare prontezza |
| 1-4 | Cuscinetto quasi sufficiente: +50 ms |
| 5-29 | Troppo basso per questa rete: raddoppiare |
| 30+ | Non è la dimensione del buffer: i pacchetti non arrivano con regolarità, verificare la rete |

## Diagnostica

Quando una chiamata non funziona, la domanda è sempre la stessa: è il softphone o è
il centralino? Lo strumento risponde misurando il flusso RTP nelle due direzioni.

```bash
dotnet run --project tools/BlindPhone.Diagnostics -- audio
```

```bash
dotnet run --project tools/BlindPhone.Diagnostics -- sip
```

```bash
dotnet run --project tools/BlindPhone.Diagnostics -- pbx
```

```bash
dotnet run --project tools/BlindPhone.Diagnostics -- chiamata 200
```

Il comando `chiamata` compone il numero, osserva per 40 secondi e conta i pacchetti
inviati e ricevuti. I quattro esiti possibili puntano a cause diverse:

| Inviati | Ricevuti | Dove guardare |
|---|---|---|
| > 0 | > 0 | Il percorso RTP funziona. Se non si sente, è instradamento sul dispositivo sbagliato |
| > 0 | 0 | Il softphone trasmette: NAT, firewall sulle porte RTP, o configurazione del trunk sul PBX |
| 0 | > 0 | Cattura locale: microfono occupato da un altro programma o permesso negato nelle impostazioni privacy di Windows |
| 0 | 0 | Nessun flusso. La chiamata cadrà per timeout di inattività |

Le credenziali non si digitano mai sulla riga di comando: lo strumento legge il
deposito protetto con DPAPI creato dall'applicazione, accessibile solo allo stesso
utente Windows sulla stessa macchina.

## Licenza

Da definire. La scelta ha conseguenze concrete: incide sullo stack SIP e sulla
risposta alla valutazione comparativa dell'art. 68 CAD, che nelle gare della PA
arriva sempre. Vedi [CONFORMITA-PA-ITALIANA.md §3.2](CONFORMITA-PA-ITALIANA.md).
