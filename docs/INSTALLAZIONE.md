# Pacchetto di installazione

Come si costruisce il pacchetto `.msi` di BlindPhone, che cosa fa quando viene
eseguito, e come distribuirlo su più postazioni.

---

## 1. Costruirlo

```powershell
powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1
```

Servono l'SDK .NET 10 e WiX 5, che si installa una volta sola:

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
Runtime 10 già installato pesa una decina di megabyte, ma può fermarsi con un
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
conferma, esito. Non c'è la pagina del contratto di licenza, ed è una scelta: il
prodotto è GPL-3.0-only e il testo sta in [LICENSE](../LICENSE), ma la GPL non è un
contratto da accettare per installare — non pone condizioni sull'uso, solo sulla
ridistribuzione — e una pagina in meno è una pagina in meno da ascoltare per chi
installa senza vedere lo schermo (vedi §6 per rimetterla comunque).

L'installazione è **per tutti gli utenti della postazione** e richiede quindi
l'elevazione dei privilegi: Windows chiede conferma all'avvio.

| Cosa | Dove |
|---|---|
| Programma | `C:\Program Files\BlindPhone` |
| Collegamento nel menu Start | direttamente fra i programmi, senza cartella |
| Collegamento sul desktop | creato salvo `DESKTOP=0` |
| Candidatura per i link `tel:`, `sip:`, `sips:`, `callto:` | Registro di sistema, vedi §7 |
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

### Aggiornando alla 1.0.17

Tre comportamenti cambiano, e sono tre protezioni che prima non c'erano. Chi
amministra deve conoscerli **prima** di distribuire, perché una postazione può
smettere di fare qualcosa che prima faceva.

| Cosa cambia | Effetto su una postazione in servizio | Che fare |
|---|---|---|
| **Gli aggiornamenti devono essere firmati** | Se nella cartella del server non c'è il file `.msi.sig`, l'aggiornamento automatico viene rifiutato e annunciato | Pubblicare i tre file prodotti da `tools\crea-installatore.ps1` (§10) |
| **`ftp://` in chiaro viene rifiutato** | Prima era un avviso nel registro; ora il controllo degli aggiornamenti non parte | Passare il server a `ftps://` o `https://` |
| **La cartella del provisioning deve essere protetta** | Un `.bpcfg` in `%ProgramData%\BlindPhone` con i permessi ereditati viene ignorato | L'installatore protegge la cartella da sé; per il file copiato a mano vedi §8 |

Le credenziali già inserite **non vanno reinserite**: al primo avvio vengono legate
al server configurato in quel momento, e da lì in poi un cambio di server le richiede
di nuovo. Vale per la password SIP, il Client Secret dell'API, la password del server
degli aggiornamenti e quella del server della rubrica.

> `secrets.dat` viene riscritto in un formato nuovo. Una versione precedente alla
> 1.0.17 non lo sa leggere: tornando indietro, le credenziali vanno reinserite una
> volta. Gli archivi cifrati non sono interessati.

---

## 3. Aggiornare

Si installa la versione nuova sopra la vecchia: la sostituisce da sola, senza
disinstallare prima e senza lasciare due voci nell'elenco delle app installate.
Reinstallare la stessa versione — cosa che capita durante le prove sul campo —
funziona allo stesso modo.

Installare una versione **più vecchia** di quella presente viene invece rifiutato
con un messaggio esplicito. Per farlo va disinstallata prima quella corrente.

### Aggiornamento automatico dalla cartella del server

BlindPhone sa cercare da sé una versione nuova in una cartella remota, e tre cose
vanno sapute prima di contare su quella via:

- **Il server deve essere su `ftps://` o `https://`.** Su `ftp://` in chiaro il
  controllo viene rifiutato, non segnalato: chi sta sul percorso di rete potrebbe
  sostituire il pacchetto con un altro programma, che verrebbe installato con i
  privilegi di amministratore.
- **Nella cartella vanno tre file per versione**, non due: il `.msi`, il `.txt` delle
  novità e il `.msi.sig` con la firma. Li produce
  `tools\crea-installatore.ps1`; senza il `.sig` l'aggiornamento viene rifiutato.
- **La copia installata deve portare la chiave pubblica di firma.** Se non la porta
  lo dice all'operatore e non installa niente da sé.

Il §10 spiega come si generano le chiavi e come si firma.

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

### Rubrica dalla directory o da Outlook

Il pacchetto non installa niente in più per queste due provenienze, ma la postazione
deve offrire alcune cose che il pacchetto non può portare con sé.

| Provenienza | Che cosa serve sulla postazione |
|---|---|
| **Directory con LDAPS** | Il certificato del server (o dell'autorità di certificazione dell'ente) fra quelli attendibili di Windows. Sulle postazioni nel dominio con Servizi certificati di Active Directory c'è già; altrimenti va distribuito per criteri di gruppo. Senza, il collegamento fallisce con «il server non risponde in modo protetto» |
| **Directory con StartTLS** | Lo stesso certificato, e la porta 389 aperta verso il server |
| **Utenza di Windows** | Postazione nel dominio e operatore che accede con un'utenza di dominio: è Kerberos a presentarlo alla directory, senza password da conservare |
| **Utenza dedicata** | Un'utenza di sola lettura sulla directory. La password si inserisce dalle impostazioni e resta nel deposito protetto di quel profilo, legata al server |
| **Contatti di Outlook** | Outlook classico (Microsoft 365 Apps o Office 2016 e successivi) con un profilo configurato per l'operatore. L'architettura non conta: BlindPhone a 64 bit legge anche un Outlook a 32 bit. Il nuovo Outlook non basta, perché non si lascia leggere da altri programmi |

Porte in uscita verso il server della directory: **636** per LDAPS, **389** per
StartTLS. La scelta «in chiaro» esiste per le directory che non offrono altro, ma
manda sulla rete nomi, numeri e — con un'utenza dedicata — la sua password: in
un ente che deve dimostrare la cifratura dei dati personali in transito non va
usata.

BlindPhone e Outlook devono girare **con gli stessi privilegi**: se Outlook è
aperto come amministratore e BlindPhone no, Windows non li lascia parlare e la
lettura fallisce con un invito a riaprire Outlook normalmente.

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

Il testo c'è già ed è quello di [LICENSE](../LICENSE), la GPL-3.0: va convertito in
RTF, perché WiX non accetta il testo semplice. Vale la pena ricordare che la pagina
non è obbligatoria — la GPL non chiede che l'utente accetti nulla per installare — e
che aggiungerla significa una pagina in più da ascoltare.

### Cambiare la grafica

Striscia superiore e riquadro laterale si rigenerano dall'immagine sorgente
vettoriale `BlindPhoneLogo.svg` insieme all'icona dell'applicazione:

```powershell
powershell -ExecutionPolicy Bypass -File tools\genera-icona.ps1
```

Produce `src\BlindPhone.App\blindphone.ico` a sette risoluzioni e le due immagini
in `installer\`. Va rieseguito solo se cambia l'immagine di partenza. Lo SVG viene
convertito con Microsoft Edge senza finestra, quindi sulla macchina serve Edge; con
`-Sorgente` si può indicare in alternativa un PNG quadrato.

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

## 7. Link di chiamata: `tel:`, `sip:` e `callto:`

Nei gestionali, nelle pagine web e nei messaggi di posta i numeri possono essere
link. BlindPhone li gestisce, con una regola che non ha eccezioni: **il link non
compone mai da solo**. Il numero va nel campo di composizione, la finestra viene
portata davanti e la voce lo legge — «Numero da link: zero sei, uno due, …
Premi Invio per chiamare» — e la chiamata parte quando l'operatore preme Invio.

È una regola di sicurezza. Chiunque può scrivere una pagina con un link di
chiamata, e una pagina può far aprire un link senza che nessuno lo clicchi: se il
link componesse, una pagina web potrebbe far partire telefonate dal centralino
dell'ente.

### Che cosa registra l'installatore

| Cosa | Dove |
|---|---|
| ProgID `BlindPhone.LinkChiamata`, con il comando `BlindPhone.App.exe "%1"` | `HKLM\SOFTWARE\Classes\BlindPhone.LinkChiamata` |
| Capacità: `tel`, `sip`, `sips`, `callto` | `HKLM\SOFTWARE\BlindPhone\Capabilities\URLAssociations` |
| Voce nelle App predefinite | `HKLM\SOFTWARE\RegisteredApplications`, valore `BlindPhone` |
| Chiave dello schema, **solo se manca** | `HKLM\SOFTWARE\Classes\tel`, `sip`, `sips`, `callto` |

BlindPhone si **candida**, non si impone: Windows 10 e 11 non permettono a un
programma di diventare da solo il gestore predefinito di un tipo di link, e la
scelta resta all'operatore o a chi amministra la postazione (paragrafi seguenti).

La chiave dello schema (`HKLM\SOFTWARE\Classes\tel` e le altre) serve a Windows per
riconoscere `tel:` come un tipo di link. Su quasi tutte le postazioni esiste già,
creata da Teams, da Collegamento al telefono o dal softphone usato prima, e in quel
caso **non viene toccata**. Dove manca, l'installatore la crea senza alcun comando
dentro — nessuno diventa predefinito per il solo fatto di essere stato installato —
e la marca con un valore `BlindPhone` per riconoscerla agli aggiornamenti.

### Che cosa succede quando l'operatore clicca un link

1. Windows avvia `BlindPhone.App.exe` con il link sulla riga di comando.
2. Se BlindPhone è già acceso, ed è il caso normale, il nuovo avvio consegna il link
   alla copia aperta ed esce: non si apre una seconda finestra. Il passaggio avviene
   su una pipe con nome **propria della sessione di Windows e dell'utente**, quindi su
   un terminal server il link cliccato da un operatore non arriva mai a un altro.
3. La copia aperta interpreta il link, porta la finestra davanti, mette il numero
   nel campo di composizione e lo annuncia, dicendo che cosa farà Invio in quel
   momento.

Con una chiamata in corso **nessuno viene messo in attesa**: il numero resta nel
campo e l'annuncio dice come procedere. Durante un trasferimento il numero diventa
la destinazione, come se fosse stato digitato. In pausa, e in modalità semplificata
mentre squilla una chiamata, il link non entra nel campo e la voce lo spiega.

Vengono **rifiutati**, con l'annuncio del motivo: i codici di servizio del centralino
(asterisco e cancelletto, con cui si attivano per esempio le deviazioni), i caratteri
di controllo e le codifiche malformate, i numeri con lettere, i numeri locali
dichiarati di un altro paese. Dei link `sip:` si tengono utente e dominio: le
intestazioni dopo il `?` e i parametri come `maddr` o `transport` si ignorano,
perché deciderebbero al posto della configurazione dell'account dove e come far
passare la chiamata. Un indirizzo verso il proprio centralino si riduce al solo
numero, e un numero italiano in forma internazionale torna nazionale: `+39 06 1234`
diventa `06 1234`.

### Come lo sceglie l'operatore

Su Windows 11: **Impostazioni** (Windows+I), **App**, **App predefinite**. Nella
casella di ricerca delle applicazioni si scrive *BlindPhone* e si apre la sua
pagina: compaiono le voci `TEL`, `SIP`, `SIPS` e `CALLTO`, e su ciascuna si sceglie
BlindPhone. In fondo alla pagina App predefinite c'è anche la scelta **per tipo di
collegamento**, che elenca i tipi uno per uno: lì si cercano `TEL`, `SIP`, `SIPS` e
`CALLTO`.

Su Windows 10 il percorso è **Impostazioni**, **App**, **App predefinite**,
**Scegli le app predefinite per protocollo**.

C'è anche la strada più breve: al primo clic su un link `tel:` Windows chiede con
quale programma aprirlo, se nessuno è stato scelto, e BlindPhone è fra quelli
proposti. Si sceglie BlindPhone e si conferma che la scelta valga sempre, non una
volta sola.

I browser chiedono conferma prima di aprire un programma esterno, una volta per
sito. Edge e Chrome offrono la casella *Consenti sempre*; chi amministra può
evitare la domanda per i soli indirizzi del gestionale con il criterio
`AutoLaunchProtocolsFromOrigins`, che esiste con lo stesso nome in entrambi:

```json
[{ "protocol": "tel", "allowed_origins": ["https://gestionale.ente.example.it"] }]
```

È un permesso che si può concedere a cuor leggero proprio perché il link non
compone: il peggio che una pagina può ottenere è un numero letto ad alta voce.

### Come lo imposta un amministratore

Per tutte le postazioni insieme si usa un **file di associazioni predefinite**. Basta
che contenga le quattro righe che riguardano BlindPhone:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<DefaultAssociations>
  <Association Identifier="tel"    ProgId="BlindPhone.LinkChiamata" ApplicationName="BlindPhone" />
  <Association Identifier="sip"    ProgId="BlindPhone.LinkChiamata" ApplicationName="BlindPhone" />
  <Association Identifier="sips"   ProgId="BlindPhone.LinkChiamata" ApplicationName="BlindPhone" />
  <Association Identifier="callto" ProgId="BlindPhone.LinkChiamata" ApplicationName="BlindPhone" />
</DefaultAssociations>
```

Le strade per distribuirlo sono tre.

- **Criterio di gruppo.** Configurazione computer, Modelli amministrativi,
  Componenti di Windows, Esplora file, **Imposta un file di configurazione delle
  associazioni predefinite**: si attiva e si indica il percorso del file, locale o di
  rete (`\\server\deploy\associazioni-blindphone.xml`). Windows lo applica
  all'accesso dell'utente, **a ogni accesso**: una scelta diversa fatta
  dall'operatore torna com'era al successivo. È il comportamento giusto su una
  postazione gestita, e va saputo prima di ricevere la segnalazione «l'ho cambiato e
  si è rimesso da solo».
- **Intune.** Il catalogo delle impostazioni ha la voce *Default Associations
  Configuration*, che vuole lo stesso file codificato in Base64.
- **Immagine di sistema.** `Dism /Online /Import-DefaultAppAssociations:C:\percorso\associazioni-blindphone.xml`
  vale per i profili utente creati **dopo**: chi ha già un profilo sulla postazione
  non ne è toccato.

Il file funziona solo dove BlindPhone è installato: un ProgID che non esiste viene
ignorato, e Windows lascia l'associazione com'era.

### Verificarlo

Dopo l'installazione, da PowerShell:

```powershell
reg query HKLM\SOFTWARE\RegisteredApplications /v BlindPhone
reg query HKLM\SOFTWARE\BlindPhone\Capabilities\URLAssociations
Start-Process "tel:0612345678"
```

L'ultima riga fa esattamente quello che fa il clic su un link: con BlindPhone scelto
come predefinito, il numero arriva nel campo di composizione e la voce lo legge, e
non parte nessuna chiamata. Il registro diagnostico riporta da che schema arrivava
il link e che cosa ne è stato; il numero non c'è, perché un numero preso da una
pratica è un dato personale che il registro non ha bisogno di conservare.

### Che cosa toglie la disinstallazione

Tutto quello che l'installatore ha scritto: il ProgID, le capacità, la voce in
RegisteredApplications e le chiavi degli schemi che aveva creato lui, riconosciute
dalla marca `BlindPhone`. Le chiavi degli schemi che appartenevano già a un altro
programma non sono mai state toccate e restano.

Restano anche, perché non sono dell'installatore, la scelta fatta dall'operatore —
sta nel suo profilo e punta a un ProgID che non esiste più: Windows la tratta come
assente, e al clic successivo chiede di nuovo con che cosa aprire il link — e il
file di associazioni del criterio di gruppo, che va tolto dal criterio.

C'è un solo caso limite, e riguarda la ricerca delle chiavi esistenti: l'installatore
di Windows sa leggere il valore predefinito di una chiave ma non sa chiedere se la
chiave esiste. Un programma che avesse creato `HKLM\SOFTWARE\Classes\tel` senza il
valore predefinito — la convenzione è `URL:tel`, e la seguono tutti quelli
conosciuti — si vedrebbe aggiungere la marca di BlindPhone e, alla disinstallazione,
togliere il valore `URL Protocol`.

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
che aggiorna quel file aggiorna tutte le postazioni. Richiede un passaggio in più —
vedi *La cartella deve essere protetta*, qui sotto.

**Dal pannello**, con *Impostazioni → Configurazione → Importa una configurazione*.
Prima di applicare mostra che cosa contiene e quali sezioni sovrascriverà. È una
scelta esplicita dell'operatore su un file che ha indicato lui, quindi non è
soggetta alla verifica dei permessi.

> Resta un solo passaggio manuale su ogni postazione, e non è aggirabile: **le
> password** — SIP e Client Secret dell'API. Vivono sotto DPAPI e sono leggibili solo
> dall'account Windows che le ha scritte su quella macchina.

### La cartella deve essere protetta

**BlindPhone applica un pacchetto distribuito solo se la cartella che lo contiene è
modificabile unicamente dagli amministratori, e appartiene a loro.** Altrimenti lo
ignora, lo scrive nel registro e lo annuncia all'operatore.

Il motivo è che un pacchetto distribuito vale per tutti i profili della postazione:
sposta l'indirizzo del centralino, cambia il registrar SIP, dirotta l'indirizzo a cui
vanno i pacchetti di assistenza e può installare un criterio con password che
impedisce all'operatore di rimettere le cose a posto. Con i permessi che
`%ProgramData%` ha di suo, un utente standard può creare quella sottocartella — e
diventarne proprietario — oppure scriverci dentro se l'ha creata chi non ha
interrotto l'ereditarietà. Su una postazione condivisa fra turni, tanto basta a un
operatore qualsiasi per riconfigurare quella di tutti gli altri.

**Con l'installatore non serve fare niente**: la cartella viene creata e protetta a
ogni installazione, e il pacchetto passato con `CONFIGURAZIONE` risulta scritto da
`SYSTEM`. Verifica così:

```powershell
icacls "%ProgramData%\BlindPhone"
```

Devono comparire soltanto `SYSTEM` e `BUILTIN\Administrators` con `(F)`, e
`BUILTIN\Users` con `(RX)`, senza voci ereditate.

**Copiando il file a mano** il proprietario diventa l'amministratore che ha fatto la
copia, e BlindPhone non ha modo di distinguere un account amministrativo da uno
qualsiasi guardando solo il suo identificativo. Da un prompt amministrativo, dopo la
copia:

```powershell
icacls "%ProgramData%\BlindPhone\configurazione-predefinita.bpcfg" /setowner "*S-1-5-32-544"
```

**Su una postazione installata con una versione precedente alla 1.0.17** la cartella
può avere i permessi ereditati. Si sistema una volta, da un prompt amministrativo:

```powershell
icacls "%ProgramData%\BlindPhone" /inheritance:r /grant "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX"
```

```powershell
icacls "%ProgramData%\BlindPhone" /setowner "*S-1-5-32-544" /t
```

Si usano gli identificativi numerici e non i nomi perché i nomi dei gruppi integrati
cambiano con la lingua di Windows: `*S-1-5-18` è `SYSTEM`, `*S-1-5-32-544` è il
gruppo degli amministratori, `*S-1-5-32-545` quello degli utenti.

> **La cartella dei dati dell'operatore non è questa.** `%APPDATA%\BlindPhone`
> contiene configurazione, rubrica e registri di un singolo profilo e resta
> scrivibile da quel profilo: è la cartella di chi ci lavora. Quella protetta è
> `%ProgramData%\BlindPhone`, che vale per tutti.

### Percorsi di rete nei pacchetti

Un pacchetto non può più far puntare la suoneria a un percorso di rete
(`\\host\share\suoneria.wav`, o una lettera di unità mappata). Il valore viene
scartato e registrato, e resta quello che c'era.

Non è una limitazione formale: aprire quel file fa partire un'autenticazione verso
`host`, che riceve così un tentativo di accesso con l'identità dell'operatore. A chi
ha scritto il pacchetto è bastata una riga. La suoneria va copiata sul disco della
postazione — il pacchetto di configurazione non la trasporta, e non lo faceva
nemmeno prima.

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

Sono **due firme diverse** e servono a due cose diverse. Vanno distinte, perché
confonderle è il modo di credersi protetti senza esserlo.

| | Firma applicativa (`.msi.sig`) | Firma Authenticode |
|---|---|---|
| Chi la verifica | BlindPhone, prima di installare un aggiornamento | Windows: UAC, SmartScreen |
| Che cosa serve | Una coppia di chiavi generata in casa | Un certificato di firma del codice acquistato |
| Senza di essa | L'aggiornamento automatico **viene rifiutato** | L'operatore vede «Editore sconosciuto» |
| Stato oggi | Obbligatoria | Da attivare quando il certificato c'è |

### La firma applicativa, ed è quella obbligatoria

Senza di lei nessun aggiornamento automatico viene installato. Il motivo è che
l'impronta `SHA256:` nel file delle novità **non dimostra la provenienza**: sta sullo
stesso server e sullo stesso canale del pacchetto, quindi chi può sostituire il
pacchetto può sostituire anche l'impronta, e il confronto torna. La firma no: per
produrla serve una chiave privata che sul server non c'è.

**Una volta sola**, sulla macchina di build, si genera la coppia di chiavi:

```powershell
openssl ecparam -name prime256v1 -genkey -noout -out blindphone-update.key
```

```powershell
openssl ec -in blindphone-update.key -pubout -outform DER | openssl base64 -A
```

Il secondo comando stampa la chiave **pubblica**: va incollata in
`UpdateSignature.PublicKeySpki`, dentro
`src/BlindPhone.Infrastructure/UpdateSignature.cs`. Finché quella costante è vuota,
BlindPhone **non installa aggiornamenti da sé** e lo dice all'operatore: non è un
ripiego prudente, è il comportamento corretto, perché l'alternativa — accettare un
pacchetto che non si riesce a verificare — è la falla che la firma chiude.

> **La chiave privata non entra nel repository, nel pacchetto o in un backup
> condiviso.** È l'unica cosa che un attaccante non si procura controllando il
> server degli aggiornamenti, e perderne il controllo annulla tutto il resto.
> `tools\crea-installatore.ps1` si rifiuta di usare una chiave che si trovi dentro
> la cartella del repository.

**A ogni rilascio** la firma la produce lo script di costruzione:

```powershell
powershell -ExecutionPolicy Bypass -File tools\crea-installatore.ps1 -ChiaveFirma C:\chiavi\blindphone-update.key
```

In alternativa si imposta una volta la variabile d'ambiente
`BLINDPHONE_CHIAVE_FIRMA` e il parametro si omette. Lo script produce tre file in
`dist\`, e **vanno pubblicati tutti e tre** nella cartella degli aggiornamenti:

| File | Contenuto |
|---|---|
| `BlindPhone-1.0.17-x64.msi` | il pacchetto |
| `BlindPhone-1.0.17-x64.msi.sig` | la firma, circa settanta byte |
| `BlindPhone-1.0.17-x64.txt` | le novità, con la riga `SHA256:` scritta dallo script |

Se manca il `.sig`, le postazioni rifiutano l'aggiornamento e lo annunciano. Per
una prova locale `-SaltaFirma` costruisce un pacchetto non firmato, che si installa
a mano ma non si distribuisce.

**Sostituire la chiave è un cambio di fiducia, non una correzione.** Dal momento in
cui la costante cambia, i pacchetti firmati con la chiave precedente non vengono più
accettati: le postazioni ancora alla versione vecchia vanno aggiornate a mano una
volta.

### La firma Authenticode

Il pacchetto **non è ancora firmato** con un certificato di firma del codice.
Windows SmartScreen mostra quindi un avviso alla prima esecuzione, e alcune
configurazioni della PA rifiutano i pacchetti non firmati. Conta anche per
l'aggiornamento: la richiesta di consenso di Windows mostra «Editore sconosciuto»
sia per il pacchetto vero sia per uno contraffatto, e l'operatore — che in quel
momento sta concedendo i privilegi di amministratore — non ha modo di distinguerli.

Per firmarlo servono un certificato di firma del codice e, dopo la costruzione:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 dist\BlindPhone-1.0.17-x64.msi
```

La marca temporale (`/tr`) non è facoltativa in pratica: senza, il pacchetto
smette di risultare firmato alla scadenza del certificato.

Appena il certificato esiste, va dichiarata anche a BlindPhone l'impronta del
certificato atteso, in `AuthenticodeVerifier.ExpectedPublisherThumbprints`:

```powershell
certutil -hashfile certificato.cer SHA256
```

Da quel momento i due controlli sono **entrambi** obbligatori, e «firmato da
qualcuno» non basta più: il certificato deve essere quello. L'elenco accetta più
impronte perché un rinnovo le cambia — durante il passaggio ci stanno dentro sia il
certificato uscente sia quello entrante, e il vecchio si toglie quando non
circolano più pacchetti firmati con quello.
