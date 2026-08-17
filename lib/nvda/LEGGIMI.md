# Client controller di NVDA

In questa cartella sta **`nvdaControllerClient.dll` a 64 bit**, la libreria con cui
un'applicazione parla e scrive in braille attraverso NVDA.

Non è software di questo progetto: è un binario di terze parti, prodotto da NV Access.
Viene distribuito insieme a BlindPhone e la compilazione lo copia nella cartella di
uscita.

| | |
|---|---|
| **Architettura** | x64 (PE32+) |
| **SHA-256** | `2FE60CF00BE929AAE32E95C1E1507A20ADA4902C8FEC273B3CC2D3BF5472932A` |
| **Funzioni esportate** | `testIfRunning`, `speakText`, `speakSsml`, `brailleMessage`, `cancelSpeech`, `getProcessId`, `setOnSsmlMarkReachedCallback` |

**Il file non porta risorsa di versione**: non dichiara né produttore né numero di
versione, quindi la sua provenienza non è verificabile leggendolo. L'impronta qui sopra
serve a questo — va confrontata con quella del pacchetto ufficiale di NV Access prima di
sostituirlo, e va rifatta se lo si aggiorna.

## Dove prenderla

Nel pacchetto *NVDA Controller Client* che NV Access pubblica insieme a ogni versione di
NVDA. Serve la variante **a 64 bit**: nei pacchetti recenti è quella nella cartella
`x64`, in quelli storici è il file già chiamato `nvdaControllerClient64.dll`.

Vanno bene entrambe le forme. Il risolutore in
`src/BlindPhone.Accessibility/ScreenReaderBridges.cs` prova tutti e due i nomi, perché il
pacchetto ha cambiato convenzione: prima l'architettura stava nel nome del file, adesso
sta nella cartella. Non rinominare niente.

**Attenzione all'architettura**, che è l'errore facile: il pacchetto contiene anche la
versione a 32 bit, con lo stesso nome, in un'altra cartella. Se finisce qui quella
sbagliata il registro lo dice a chiare lettere — «architettura sbagliata: serve la
versione a 64 bit» — invece di lasciare un'eccezione da interpretare.

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
di installazione per decisione del titolare del progetto.

**Il file di licenza che accompagna il pacchetto di NV Access non è ancora stato
depositato qui**, e finché manca la verifica di compatibilità non è fatta: BlindPhone è
distribuito come **GPL-3.0-only**, e i termini di questa libreria vanno letti, non dati
per scontati. Fra le licenze libere ci sono combinazioni che non stanno insieme — la
GPL v2 *only* e la GPL v3 sono il caso tipico.

È una verifica che va chiusa **prima della pubblicazione nel catalogo del riuso**, dove
il software viene offerto ad altre amministrazioni che lo ridistribuiranno a loro volta.
Depositare qui il file `license` o `copying` del pacchetto originale, e citarne i termini
in questa pagina, è tutto ciò che serve.
