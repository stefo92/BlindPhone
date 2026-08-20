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
| **Versione** | 2026.1.1.55980 | 2026.1.1.55980 |
| **Dimensione** | 262.808 byte | 239.256 byte |
| **SHA-256** | `2FE60CF00BE929AAE32E95C1E1507A20ADA4902C8FEC273B3CC2D3BF5472932A` | `1A2755C7864701E0EA6805499F1955B45758BA53DEA46A3FA83311F8589D2AF5` |

Le funzioni esportate sono le stesse nelle due copie, e sono quelle che il bridge
chiama: `nvdaController_testIfRunning`, `nvdaController_speakText`,
`nvdaController_speakSsml`, `nvdaController_brailleMessage`,
`nvdaController_cancelSpeech`, `nvdaController_getProcessId`,
`nvdaController_setOnSsmlMarkReachedCallback`.

Il numero di versione segue la numerazione delle release di NVDA, quindi le librerie
provengono dalla **2026.1.1**: è il dato con cui verificare di avere il client abbinato
alla versione di NVDA installata sulle postazioni. Le due copie vanno tenute alla stessa
versione — sono la stessa libreria compilata due volte, e una postazione arm64 con un
client più vecchio di quello x64 diventerebbe un caso da spiegare.

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

## Licenza: cosa manca ancora

La libreria è dichiarata fra le dipendenze in `publiccode.yml`, ed è inclusa nel pacchetto
di installazione per decisione del titolare del progetto. Con la copia arm64 i binari di
terze parti nel repository sono **due**: stessa libreria, stessa versione, stesso
pacchetto di origine, quindi la verifica di licenza resta una sola — ma va chiusa per
entrambe le architetture distribuite.

**Il file di licenza che accompagna il pacchetto di NV Access non è ancora stato
depositato qui**, e finché manca la verifica di compatibilità non è fatta: BlindPhone è
distribuito come **GPL-3.0-only**, e i termini di questa libreria vanno letti, non dati
per scontati. Fra le licenze libere ci sono combinazioni che non stanno insieme — la
GPL v2 *only* e la GPL v3 sono il caso tipico.

È una verifica che va chiusa **prima della pubblicazione nel catalogo del riuso**, dove
il software viene offerto ad altre amministrazioni che lo ridistribuiranno a loro volta.
Depositare qui il file `license` o `copying` del pacchetto originale, e citarne i termini
in questa pagina, è tutto ciò che serve.
