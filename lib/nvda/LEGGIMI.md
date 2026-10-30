# Client controller di NVDA

In questa cartella sta **`nvdaControllerClient.dll`**, la libreria con cui
un'applicazione parla e scrive in braille attraverso NVDA. Ce n'è **una copia per
architettura**, con lo stesso nome di file, in due sottocartelle:

```
lib\nvda\x64\nvdaControllerClient.dll
lib\nvda\arm64\nvdaControllerClient.dll
```

È codice nativo, e non basta che sia a 64 bit: un processo arm64 non carica la copia
x64, e viceversa. `src\BlindPhone.App\BlindPhone.App.csproj` sceglie la cartella in base
all'architettura per cui si pubblica e ne copia **una sola** accanto all'eseguibile.

Non è software di questo progetto: sono binari di terze parti, prodotti da NV Access.
Vengono distribuiti insieme a BlindPhone e la compilazione copia nella cartella di
uscita quello che serve.

| | x64 | arm64 |
|---|---|---|
| **Machine (PE)** | `0x8664` AMD64 | `0xAA64` ARM64 |
| **Metadati ibridi** | nessuno | nessuno |
| **Versione** | 2026.2.0.57664 | 2026.2.0.57664 |
| **Dimensione** | 263.832 byte | 251.544 byte |
| **SHA-256** | `598B7EC3DC469814F571275929F676CE73834C469FBDB359A06FD4DB4E0FC866` | `6FAAA1DD82BAE2AE953BD14F5206FEF63B8932D9A3CB4578FE027EC794F7918E` |

Provenienza: `nvda_2026.2_controllerClient.zip`, 4.795.290 byte, scaricato da
`https://download.nvaccess.org/releases/2026.2/` (SHA-256 del pacchetto
`510736F021AEFA33378076FA342A4524B586BC1C6B096DB9479AF42B28AAC649`). Il pacchetto è
quello pubblicato insieme a NVDA 2026.2 il 31 agosto 2026. Sostituisce la 2026.1.1
(x64 `2FE60CF0…472932A`, arm64 `1A2755C7…89D2AF5`): stesse sette funzioni esportate,
stessa architettura nell'intestazione PE.

Le funzioni esportate sono le stesse nelle due copie, e sono quelle che il bridge
chiama: `nvdaController_testIfRunning`, `nvdaController_speakText`,
`nvdaController_speakSsml`, `nvdaController_brailleMessage`,
`nvdaController_cancelSpeech`, `nvdaController_getProcessId`,
`nvdaController_setOnSsmlMarkReachedCallback`.

Il numero di versione segue la numerazione delle release di NVDA, quindi le librerie
provengono dalla **2026.2**: è il dato con cui verificare di avere il client abbinato
alla versione di NVDA installata sulle postazioni. Le due copie vanno tenute alla stessa
versione — sono la stessa libreria compilata due volte, e una postazione arm64 con un
client più vecchio di quello x64 diventerebbe un caso da spiegare.

**Compatibilità con NVDA più vecchio.** L'interfaccia è la versione 2.0 del client,
introdotta con NVDA 2024.1: `nvdaController_getProcessId` e `nvdaController_speakSsml`
funzionano da quella versione in poi, e su NVDA più vecchi rispondono con l'errore 1717
(`RPC_S_UNKNOWN_IF`). Il client non è quindi legato alla stessa versione di NVDA, ma a una
versione minima: una postazione con NVDA 2026.1.1 o precedente, fino alla 2024.1, funziona.
Il bridge usa le sette funzioni elencate sopra, e nessuna è nuova in questa versione.

**Manca però il blocco testuale della risorsa di versione**: né produttore, né nome del
prodotto, né copyright. Sono i campi che avrebbero aiutato sulla licenza, e non ci sono.
Le impronte qui sopra servono a questo — vanno confrontate con quelle del pacchetto
ufficiale di NV Access prima di sostituire un file, e vanno rifatte se si aggiorna.

## Dove prenderla

Nel pacchetto *NVDA Controller Client* che NV Access pubblica insieme a ogni versione di
NVDA. Le copie che servono stanno nelle cartelle `x64` e `arm64` del pacchetto; in quelli
storici la x64 è il file già chiamato `nvdaControllerClient64.dll`.

Vanno bene entrambe le forme. Il risolutore in
`src/BlindPhone.Accessibility/ScreenReaderBridges.cs` prova tutti e due i nomi, perché il
pacchetto ha cambiato convenzione: prima l'architettura stava nel nome del file, adesso
sta nella cartella. Non rinominare niente.

**La cartella `arm64ec` del pacchetto non si usa, ed è la scelta meno ovvia di questa
pagina.** ARM64EC è l'ABI di compatibilità con x64 di Windows on ARM: quella libreria si
dichiara `0x8664` (AMD64) nell'intestazione e porta i metadati ibridi CHPE, e viene
caricata dai processi x64 emulati e da quelli ARM64EC. BlindPhone per arm64 è pubblicato
come processo **arm64 puro**, e in un processo arm64 puro quella copia non entra: serve
la `arm64`. Avrebbe senso solo se un domani BlindPhone dovesse ospitare nel proprio
processo componenti x64 di terze parti, cioè il motivo per cui ARM64EC esiste.

**Attenzione all'architettura**, che è l'errore facile: il pacchetto contiene anche la
versione a 32 bit, con lo stesso nome, in un'altra cartella, oltre alla `arm64ec` di cui
sopra. Se in una delle due sottocartelle finisce la copia sbagliata il registro lo dice —
«architettura sbagliata» — invece di lasciare un'eccezione da interpretare. Il modo
rapido per riconoscerle è il campo *machine* dell'intestazione PE, riportato nella
tabella qui sopra: `0xAA64` è arm64, `0x8664` senza metadati CHPE è x64, `0x8664` con
metadati CHPE è arm64ec.

## Come verificare che sia entrata in servizio

Compila, avvia la postazione con NVDA in esecuzione e cerca nel registro:

```
Client controller NVDA disponibile (nvdaControllerClient.dll). NVDA in ascolto adesso: True
```

Se invece compare «Client controller NVDA non presente», la riga elenca i nomi cercati e
la cartella in cui li ha cercati.

A regime, la riga che dice quale canale ha pronunciato ciascun annuncio è
`Annuncio instradato (...): bridge diretto True, ...`.

## Cosa cambia se manca

Il prodotto funziona: gli annunci raggiungono lo screen reader come **notifiche UI
Automation**, che è il comportamento conforme alla EN 301 549 §11.5 e vale per qualunque
tecnologia assistiva.

Quello che si perde con il bridge diretto è il **controllo dell'interruzione**: quando
arriva una chiamata mentre lo screen reader sta leggendo altro, l'annuncio deve
interrompere e non accodarsi. Su un posto operatore questa è la differenza fra sapere chi
sta chiamando adesso e saperlo dopo la fine di una frase che non interessa più.

C'è anche una ragione meno evidente. In modalità automatica, quando il sistema segnala
che uno screen reader è in esecuzione, la voce interna **tace apposta** per non
sovrapporsi — dando per scontato che lo screen reader legga la notifica. Se quella
notifica non viene letta, non parla nessuno. Il bridge diretto toglie di mezzo
l'assunzione, perché dice se il testo è stato accettato.

## Licenza

La libreria è dichiarata fra le dipendenze in `publiccode.yml`, ed è inclusa nel pacchetto
di installazione per decisione del titolare del progetto. Con la copia arm64 i binari di
terze parti nel repository sono **due**: stessa libreria, stessa versione, stesso
pacchetto di origine, quindi la licenza è una sola per entrambe le architetture.

**Il testo di licenza è depositato qui**, accanto ai binari:
[`LICENSE-nvdaControllerClient-LGPL-2.1.txt`](LICENSE-nvdaControllerClient-LGPL-2.1.txt),
copia esatta del `license.txt` del pacchetto di NV Access. Il `readme.md` dello stesso
pacchetto dichiara: *«The NVDA Controller Client API is licensed under the GNU Lesser
General Public License (LGPL), version 2.1»*.

**Esito della verifica di compatibilità con la GPL-3.0-only del prodotto: compatibile.**
Il caso che preoccupava — GPL v2 *only* contro GPL v3 — non è questo: la LGPL 2.1
non è una GPL v2 *only*, ed è una licenza pensata proprio per le librerie usate da altri
programmi. Il suo articolo 3 consente anzi di applicare a una copia della libreria i
termini della GPL ordinaria, anche di una versione successiva alla 2 (*«If a newer
version than version 2 of the ordinary GNU General Public License has appeared, then you
can specify that version instead if you wish»*). Ma qui non serve nemmeno: è una libreria
a collegamento dinamico, una DLL separata che l'utente può sostituire, cioè il caso di
«programma che usa la libreria» che la LGPL regola e ammette negli articoli 5 e 6.

**Obblighi che ne discendono, e che il pacchetto di installazione deve rispettare:**
la DLL non viene modificata; il testo della licenza accompagna la distribuzione; la
provenienza e l'impronta sono quelle riportate sopra, così chiunque può verificare che
il file sia quello di NV Access.

Il secondo obbligo è rispettato dalla versione 1.0.16: `BlindPhone.App.csproj` copia
`LICENSE-nvdaControllerClient-LGPL-2.1.txt` nella cartella di uscita accanto alla DLL, e
da lì entra nel pacchetto di installazione (verificato nella tabella dei file dell'MSI).

Questa è la lettura dei testi, non un parere legale: per la pubblicazione nel catalogo
del riuso, dove il software viene offerto ad altre amministrazioni che lo
ridistribuiranno a loro volta, la conferma finale resta del titolare del progetto.
