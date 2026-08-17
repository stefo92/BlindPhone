# Client controller di NVDA

In questa cartella va **`nvdaControllerClient.dll` a 64 bit**, la libreria con cui
un'applicazione parla e scrive in braille attraverso NVDA.

Non è software di questo progetto e **non sta nel repository**: è un binario di terze
parti con una propria licenza, e va procurato a parte. Chi compila senza averlo ottiene
comunque un prodotto funzionante — vedi «Cosa cambia se manca» qui sotto.

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

## Prima di metterla nell'installer

Serve il **file di licenza** che accompagna il pacchetto, da conservare accanto alla
libreria e da citare fra le dipendenze in `publiccode.yml`.

BlindPhone è distribuito come **GPL-3.0-only**: la compatibilità con i termini di questa
libreria va verificata leggendo la licenza, non data per scontata. È una verifica che va
fatta prima della pubblicazione nel catalogo del riuso, non dopo.
