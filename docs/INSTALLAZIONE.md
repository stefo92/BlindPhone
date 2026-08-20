# Pacchetto di installazione

Come si costruisce il pacchetto `.msi` di BlindPhone, che cosa fa quando viene
eseguito, e come distribuirlo su più postazioni.

---

## 1. Costruirlo

```powershell
powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1
```

Servono l'SDK .NET 9 e WiX 5, che si installa una volta sola:

```powershell
dotnet tool install --global wix --version 5.*
```

```powershell
wix extension add -g WixToolset.UI.wixext/5.0.2
```

```powershell
wix extension add -g WixToolset.Util.wixext/5.0.2
```

Lo script pubblica l'applicazione in `artifacts\pubblicazione-<architettura>` e
scrive il pacchetto in `dist\BlindPhone-<versione>-<architettura>.msi`. Circa
**62 MB**, per **170 MB** installati.

Prima di costruire il pacchetto lo script verifica che nella cartella di
pubblicazione non sia finito nulla di personale, e **si ferma** se ne trova. Il
controllo guarda tre cose: nomi di file noti (`settings.json`, `secrets.dat`,
`contacts.db`, `history.db`, `certificati-fidati.json`), estensioni a rischio
(`.log`, `.wav`, `.csv`, `.bak`, `.zip`) e **cartelle intere** — `log\` ed
`errori\`, i cui file hanno nomi generati che nessun elenco potrebbe prevedere.

È bloccante di proposito: un pacchetto distribuito non si richiama indietro, e i
dati di traffico telefonico sono dati personali.

La versione si legge da `Directory.Build.props`, così binari e pacchetto non
possono divergere. Per costruire una versione diversa senza toccare il file:

```powershell
powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1 -Versione 1.0.0
```

Con `-SaltaPubblicazione` si riusa la pubblicazione esistente: serve per provare
modifiche al solo `installer\BlindPhone.wxs` senza ricompilare tutto. La
pubblicazione riusata è quella dell'architettura richiesta, che sta in una cartella
propria: così non può capitare di impacchettare binari x64 dentro un pacchetto
dichiarato arm64.

### Due architetture, due pacchetti

Senza `-Architettura` si costruisce per **x64**, che è la postazione normale. Per
Windows on ARM — i portatili con processore Snapdragon, e le macchine virtuali ARM
su Mac Apple Silicon:

```powershell
powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1 -Architettura arm64
```

I due pacchetti si distinguono dal nome e **non sono intercambiabili**. Il
pacchetto x64 si installa anche su Windows on ARM, dove l'emulazione lo fa
funzionare: è la via di ripiego, e costa emulazione su un programma che deve stare
dietro all'audio in tempo reale. Il contrario non vale — un pacchetto arm64 su una
postazione x64 viene rifiutato da Windows Installer.

**Attenzione alla cartella degli aggiornamenti**, se la si usa per distribuire. Chi
cerca un aggiornamento guarda il numero di versione e non l'architettura: due
pacchetti della stessa versione nella stessa cartella remota si escludono a
vicenda, e una postazione può ritrovarsi proposto il pacchetto dell'architettura
sbagliata. Le due architetture vanno in **due cartelle distinte**, ognuna con il
proprio file delle novità, e ogni postazione punta alla sua.

Il collegamento diretto a NVDA c'è su entrambe le architetture, e non per caso: il
client controller di NV Access è codice nativo, e un processo arm64 non carica la
copia x64. Le due copie stanno in `lib\nvda\x64\` e `lib\nvda\arm64\`, e la
compilazione ne porta accanto all'eseguibile **una sola**, quella
dell'architettura in costruzione. Chi aggiorna la libreria le aggiorni entrambe:
`lib\nvda\LEGGIMI.md` riporta impronte e versioni, e spiega perché la variante
`arm64ec` del pacchetto di NV Access non è quella che serve.

### Il pacchetto include il motore di esecuzione .NET

È la ragione dei 62 MB, ed è una scelta. Un pacchetto che pretende .NET Desktop
Runtime 9 già installato pesa una decina di megabyte, ma può fermarsi con un
errore di prerequisito — e per chi installa da solo senza vedere lo schermo quel
messaggio è un muro.

Dove il motore di esecuzione è gestito centralmente (SCCM, Intune, criteri di
gruppo) conviene il pacchetto leggero:

```powershell
powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1 -Autonomo $false
```

Il file prodotto si chiama `BlindPhone-<versione>-<architettura>-richiede-dotnet.msi`,
e il nome è esplicito apposta: i due pacchetti non vanno confusi in distribuzione.

---

## 2. Che cosa fa

La procedura guidata ha quattro pagine: benvenuto, scelta della cartella,
conferma, esito. Non c'è la pagina del contratto di licenza — il prodotto non ha
ancora un testo di licenza, e mostrarne uno inventato sarebbe peggio che non
mostrarne alcuno (vedi §6 per rimetterla).

L'installazione è **per tutti gli utenti della postazione** e richiede quindi
l'elevazione dei privilegi: Windows chiede conferma all'avvio.

| Cosa | Dove |
|---|---|
| Programma | `C:\Program Files\BlindPhone` |
| Collegamento nel menu Start | direttamente fra i programmi, senza cartella |
| Collegamento sul desktop | creato salvo `DESKTOP=0` |
| Dati dell'operatore | `%APPDATA%\BlindPhone` (per utente) |

Nella cartella dei dati l'applicazione crea, alla prima esecuzione e poi via via che
servono:

| File o cartella | Contenuto |
|---|---|
| `settings.json` | Configurazione, **senza segreti**: esportabile e condivisibile |
| `secrets.dat` | Password SIP di ogni account e Client Secret, cifrati con DPAPI |
| `hotkeys.json` | Profili dei tasti rapidi |
| `contacts.db` | Rubrica, **cifrata** (SQLCipher) |
| `history.db` | Ultime chiamate, **cifrate** |
| `certificati-fidati.json` | Certificati del centralino approvati dall'operatore |
| `log\` | Registri diagnostici, conservati per i giorni impostati |
| `errori\` | Rapporti degli errori gravi, in attesa di essere segnalati |

`certificati-fidati.json` sta **fuori** da `settings.json` di proposito: quel file è
documentato come esportabile fra postazioni, e una decisione di fiducia su un
certificato che viaggiasse con la configurazione verrebbe applicata su macchine il cui
operatore non l'ha mai presa.

Sull'ultima pagina una casella già spuntata avvia BlindPhone alla chiusura della
procedura. Il programma parte con l'utenza dell'operatore e non con quella
amministrativa usata per installare: senza questo accorgimento leggerebbe e
scriverebbe la configurazione nel profilo sbagliato.

### I dati dell'operatore non vengono mai toccati

`%APPDATA%\BlindPhone` non è dichiarato da nessuna parte in `BlindPhone.wxs`, e la
cosa è voluta. L'installatore di Windows rimuove ciò che ha scritto: non
dichiarare quella cartella significa che **né un aggiornamento né una
disinstallazione possono cancellare la rubrica dell'operatore**, le sue
impostazioni o i registri diagnostici.

Vale per l'intera cartella e non per l'elenco di file del paragrafo precedente: ogni
file che l'applicazione crea lì dentro in futuro è protetto dalla stessa regola,
senza che serva ricordarsi di aggiungerlo da qualche parte.

Per rimuoverli davvero va cancellata a mano.

---

## 2-bis. Che cosa cambia aggiornando da una versione precedente

La configurazione viene **migrata alla lettura**, al primo avvio dopo
l'aggiornamento, senza che nessuno debba aprire le impostazioni.

| Da | A | Effetto |
|---|---|---|
| Schema 1 | Schema 2 | L'account SIP diventa il primo di un elenco. **La password non si perde**: il primo account conserva la chiave storica nel deposito protetto |
| Schema 2 | Schema 3 | Il registro passa a «tutto» e la modalità debug si accende, per la durata della beta |

Il secondo passaggio avviene **una volta sola**. Chi abbassa poi il dettaglio dal
pannello se lo tiene abbassato: la migrazione non è una preferenza che torna da sola
a ogni avvio, ed è quello che permette di installare la beta anche dove la policy di
conservazione dei dati è stretta.

> **Da sapere prima di distribuire la beta a più postazioni.** Con il registro su
> «tutto» il file cresce di decine di megabyte per turno e riporta i numeri chiamati e
> chiamanti per intero. Su una distribuzione ampia conviene decidere in anticipo i
> giorni di conservazione — *Impostazioni → Diagnostica* — e allinearli alla policy
> dell'ente invece di lasciare il predefinito di 14 giorni.

---

## 3. Aggiornare

Si installa la versione nuova sopra la vecchia: la sostituisce da sola, senza
disinstallare prima e senza lasciare due voci nell'elenco delle app installate.
Reinstallare la stessa versione — cosa che capita durante le prove sul campo —
funziona allo stesso modo.

Installare una versione **più vecchia** di quella presente viene invece rifiutato
con un messaggio esplicito. Per farlo va disinstallata prima quella corrente.

---

## 4. Distribuire su più postazioni

Installazione senza alcuna interazione:

```powershell
msiexec /i BlindPhone-0.1.0-x64.msi /qn
```

Senza collegamento sul desktop:

```powershell
msiexec /i BlindPhone-0.1.0-x64.msi /qn DESKTOP=0
```

In una cartella diversa:

```powershell
msiexec /i BlindPhone-0.1.0-x64.msi /qn INSTALLFOLDER="D:\Applicazioni\BlindPhone"
```

Con registro dell'installazione, da allegare a una segnalazione:

```powershell
msiexec /i BlindPhone-0.1.0-x64.msi /qn /l*v %TEMP%\blindphone-install.log
```

Disinstallazione senza interazione:

```powershell
msiexec /x BlindPhone-0.1.0-x64.msi /qn
```

Il pacchetto è distribuibile per criteri di gruppo (assegnazione al computer) e
tramite Intune o SCCM senza confezionamento aggiuntivo.

### Verificare un pacchetto senza installarlo

L'installazione amministrativa estrae tutti i file in una cartella senza toccare
il sistema, il registro o l'elenco delle app installate:

```powershell
msiexec /a BlindPhone-0.1.0-x64.msi /qn TARGETDIR=%TEMP%\verifica
```

È il modo per controllare il contenuto di un pacchetto, o per far provare
l'applicazione a qualcuno che non ha i privilegi di amministratore.

---

## 5. Accessibilità della procedura

Le finestre della procedura sono quelle standard dell'installatore di Windows:
NVDA e JAWS le leggono, il percorso con Tab e Invio funziona, e le pagine sono
quattro invece delle sei abituali.

Resta un limite che non dipende da noi: **l'installatore di Windows non annuncia
da solo il testo di una pagina al cambio di pagina.** Chi installa sente il
pulsante su cui arriva il focus, non il paragrafo che lo precede, e per leggerlo
deve usare il cursore di lettura dello screen reader. È una differenza rispetto a
BlindPhone stesso, dove ogni passaggio parla da solo.

Dove questo è un problema, le due vie che lo evitano del tutto sono
l'installazione senza interazione del §4 e l'estrazione amministrativa qui sopra.

---

## 6. Modifiche ricorrenti

### Rimettere la pagina del contratto di licenza

Servono un file RTF con il testo e due modifiche a `installer\BlindPhone.wxs`:
togliere le due righe `Publish` che saltano `LicenseAgreementDlg`, e aggiungere

```xml
<WixVariable Id="WixUILicenseRtf" Value="percorso\licenza.rtf" />
```

Il testo va deciso prima: vedi [CONFORMITA-PA-ITALIANA.md §3.2](../CONFORMITA-PA-ITALIANA.md),
perché la scelta della licenza incide sulla valutazione comparativa dell'art. 68
CAD.

### Cambiare la grafica

Striscia superiore e riquadro laterale si rigenerano dall'immagine sorgente
`icona.png` insieme all'icona dell'applicazione:

```powershell
powershell -ExecutionPolicy Bypass -File tools\genera-icona.ps1
```

Produce `src\BlindPhone.App\blindphone.ico` a sette risoluzioni e le due immagini
in `installer\`. Va rieseguito solo se cambia l'immagine di partenza.

### Aggiungere una regola del firewall

Non c'è, di proposito: alla prima chiamata è Windows a chiedere l'autorizzazione,
e una regola creata in silenzio dall'installatore è una decisione di sicurezza
presa al posto di chi amministra la rete. Dove serve prepararla in anticipo si usa
l'estensione `WixToolset.Firewall.wixext`.

Le porte da aprire dipendono dal trasporto configurato: UDP o TCP 5060 per la
segnalazione in chiaro, **TCP 5061 per quella su TLS**, più l'intervallo RTP che il
centralino richiede. BlindPhone apre le connessioni verso il centralino e non ne
accetta di entranti sul canale TLS, quindi in uscita basta permettere l'eseguibile.

### Preparare una postazione senza toccarla

Vedi il §8, che è la via completa.

---

## 8. Preconfigurare le postazioni

Configurare una postazione a mano significa percorrere otto schede, una cinquantina
di campi e un profilo di tasti rapidi. Su venti postazioni è una giornata, e almeno
una avrà un parametro diverso dalle altre — di solito quello che poi produce la
segnalazione che nessuno riesce a riprodurre altrove.

### Il pacchetto di configurazione

*Impostazioni → Configurazione → Esporta la configurazione*

Produce un file `.bpcfg` che contiene le sezioni scelte, i profili dei tasti rapidi e
il criterio di blocco. **Non contiene password**, e non è una limitazione: i segreti
vivono sotto DPAPI, legati all'utente e alla macchina, e metterli in un file li
renderebbe leggibili altrove — che è esattamente ciò da cui DPAPI protegge.

**Le sezioni si scelgono**, ed è la parte che rende il pacchetto usabile davvero.
Lasciando fuori *account SIP* si ottiene una configurazione applicabile anche a
postazioni già in servizio, che manterranno il proprio interno. L'esportazione
propone già quella scelta.

| Cosa | Effetto |
|---|---|
| Sezioni spuntate | Le uniche che verranno sovrascritte sulla postazione di destinazione |
| Profili dei tasti | Sostituiscono gli omonimi; i profili con altri nomi restano |
| Criterio di blocco | Vale come criterio dell'utente, non di postazione (vedi sotto) |
| Revisione | Un pacchetto viene applicato solo se ha revisione **più alta** di quella già applicata |

La revisione è il numero che permette di aggiornare una configurazione già
distribuita: si esporta con revisione 2, le postazioni la applicano al primo avvio, e
quelle che l'hanno già la ignorano. Senza, l'unica alternativa sarebbe riapplicare a
ogni avvio, cancellando le regolazioni fatte dall'operatore nelle sezioni lasciate
libere.

### Applicarlo

Tre vie, in ordine di quanto poco lavoro richiedono.

**Con l'installatore**, che lo copia al posto giusto:

```powershell
msiexec /i BlindPhone-1.0.0-x64.msi /qn CONFIGURAZIONE="\\server\deploy\configurazione-predefinita.bpcfg"
```

**A mano**, copiando il file in `%ProgramData%\BlindPhone\configurazione-predefinita.bpcfg`:
BlindPhone lo cerca lì a ogni avvio e lo applica se la revisione è più alta di quella
già applicata. Funziona anche su postazioni già installate, e un criterio di gruppo
che aggiorna quel file aggiorna tutte le postazioni.

**Dal pannello**, con *Impostazioni → Configurazione → Importa una configurazione*.
Prima di applicare mostra che cosa contiene e quali sezioni sovrascriverà.

> Resta un solo passaggio manuale su ogni postazione, e non è aggirabile: **le
> password** — SIP e Client Secret dell'API. Vivono sotto DPAPI e sono leggibili solo
> dall'account Windows che le ha scritte su quella macchina.

---

## 9. Bloccare le impostazioni

Due livelli, e la differenza va conosciuta prima di sceglierne uno.

| Livello | Dove vive | Che cosa garantisce |
|---|---|---|
| **Dell'utente** | `settings.json` in `%APPDATA%` | Protegge dalla modifica **accidentale**. Chi sa dov'è il file può cancellare tre righe |
| **Di postazione** | `HKLM\SOFTWARE\Policies\BlindPhone` | Un utente senza privilegi amministrativi non lo tocca. È l'unico vincolo vero |

Il secondo ha sempre la precedenza sul primo, **anche quando è meno restrittivo**: un
criterio di postazione che dichiara il pannello aperto è una decisione di chi
amministra la rete, e un file nella cartella dell'utente non deve poterla
contraddire in nessuna direzione.

Fingere che il primo livello sia una barriera di sicurezza sarebbe peggio che non
averlo: chi amministra prenderebbe una decisione basandosi su una garanzia che non
esiste.

### Le tre modalità

| Modalità | Comportamento |
|---|---|
| **Aperto** | L'operatore modifica quello che vuole |
| **Consultabile** | Le schede si aprono e si leggono, il salvataggio è rifiutato |
| **Chiuso** | Il pannello non si apre senza la password dell'amministratore |

**«Consultabile» è quella che serve nella maggior parte delle installazioni.** Un
pannello che non si apre affatto toglie all'operatore anche la possibilità di
*leggere* come è configurata la sua postazione — e quando chiama l'assistenza, la
prima domanda che gli viene fatta è esattamente quella.

Le sezioni si bloccano separatamente: il caso frequente non è «blocca tutto» ma
«account SIP e centralino li decide l'ufficio IT, il resto lo regola l'operatore».
Voce, dispositivi audio e tasti rapidi sono la parte più personale del prodotto, e
bloccarli insieme ai parametri di rete costringe a chiamare l'assistenza per alzare
il volume di un earcon.

### Distribuire il criterio di postazione

Con l'installatore:

```powershell
msiexec /i BlindPhone-1.0.0-x64.msi /qn PANNELLO=SoloLettura SEZIONIBLOCCATE=SipAccounts,Pbx,Diagnostics MESSAGGIO="Configurazione gestita dall'ufficio IT, interno 4000"
```

Con un criterio di gruppo, scrivendo sotto `HKLM\SOFTWARE\Policies\BlindPhone`:

| Valore | Tipo | Contenuto |
|---|---|---|
| `Pannello` | `REG_SZ` | `Aperto`, `SoloLettura` o `Bloccato` |
| `SezioniBloccate` | `REG_SZ` | `SipAccounts,Pbx,Audio,Accessibility,Calls,Hotkeys,Contacts,Diagnostics,Tray` |
| `Messaggio` | `REG_SZ` | Testo mostrato all'operatore |
| `PasswordHash` | `REG_SZ` | Impronta della password di sblocco |
| `PasswordSalt` | `REG_SZ` | Sale corrispondente |

**Compila `Messaggio`.** «Impostazione bloccata» lascia l'operatore senza sapere a
chi rivolgersi; «Configurazione gestita dall'ufficio IT, interno 4000» gli dice cosa
fare. È l'unica parte del criterio che sente pronunciare, e per un operatore non
vedente è tutto quello che c'è.

### La password di sblocco

Si genera dal pannello — *Impostazioni → Configurazione → Password di sblocco* — che
ne scrive la sola impronta: PBKDF2 con SHA-256, duecentomila iterazioni e sale
casuale. La password non viene conservata da nessuna parte, nemmeno cifrata.

**Non passarla sulla riga di comando dell'installatore**: finirebbe nei registri di
distribuzione e nella cronologia della shell. Si distribuiscono `PASSWORDHASH` e
`PASSWORDSALT`, che sono già l'impronta.

Lo sblocco vale per la **sessione corrente** e non viene scritto: chi sblocca per
correggere un parametro non lascia la postazione sbloccata per sempre, e non deve
doverla ribloccare ricordandosene. Il pulsante *Sblocca o riblocca* la riblocca
subito.

> **Senza password configurata il pannello chiuso non si apre.** È deliberato:
> altrimenti bloccare dimenticando la password lo renderebbe apribile da chiunque
> premesse Invio su un campo vuoto. Chi si trova in quella condizione passa dal
> criterio di postazione o dal file di configurazione — è scomodo, ed è giusto che lo
> sia.

---

## 10. Firma digitale

Il pacchetto **non è firmato**. Windows SmartScreen mostrerà quindi un avviso alla
prima esecuzione su una postazione, e alcune configurazioni della PA rifiutano i
pacchetti non firmati.

Per firmarlo servono un certificato di firma del codice e, dopo la costruzione:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 dist\BlindPhone-0.1.0-x64.msi
```

La marca temporale (`/tr`) non è facoltativa in pratica: senza, il pacchetto
smette di risultare firmato alla scadenza del certificato.
