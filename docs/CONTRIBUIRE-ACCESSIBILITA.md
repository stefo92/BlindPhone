# Regole vincolanti per chi tocca il codice di BlindPhone

Questo documento non contiene consigli. Contiene le regole che, se violate, rendono
il prodotto inutilizzabile dalle persone per cui è stato scritto — spesso senza che
chi ha scritto il codice se ne accorga, perché guarda lo schermo.

Va letto prima del primo contributo e riletto prima di ogni modifica all'interfaccia.

---

## 1. La regola che viene violata più spesso

> **Ogni cambiamento di stato deve essere comunicato.**

Chi sviluppa vede l'interfaccia aggiornarsi e considera l'informazione trasmessa.
Per un operatore non vedente non è successo nulla.

**Come è protetta.** `AnnouncementCoverageTests` enumera
`CallStateMachine.Transitions` e verifica che `CallAnnouncementMapper.Map`
restituisca un annuncio per ognuna. Se aggiungi una transizione alla tabella e non
la mappi, la build fallisce.

**Cosa devi fare.** Se aggiungi una transizione, aggiungi il ramo corrispondente nel
mapper. Se aggiungi uno stato nuovo, aggiungi anche il caso in
`AnnouncementCoverageTests.BuildCallInState`.

---

## 2. Il focus non si sposta da solo

Il focus si muove **solo** per azione esplicita dell'operatore.

Una chiamata in arrivo annuncia, non ruba il focus. Un aggiornamento della coda
aggiorna la lista, non ci porta dentro. Se il focus si spostasse da solo, un
operatore che sta scrivendo una nota si ritroverebbe a digitare altrove senza
accorgersene, e il testo finirebbe nel posto sbagliato senza alcun indizio.

Per far parlare lo screen reader senza spostare il focus si usa
`AutomationPeer.RaiseNotificationEvent`, come in
`MainWindow.OnAnnouncementSpoken`. Mai `Focus()` per attirare l'attenzione.

---

## 2-bis. Un controllo standard non è automaticamente accessibile

Questa regola nasce da due difetti reali trovati in questo codice, entrambi
invisibili a chi sviluppa perché **a schermo funzionava tutto**.

### `LiveSetting` da solo è inerte

```xml
<!-- NON basta: la regione resta muta -->
<TextBlock x:Name="StatusText" AutomationProperties.LiveSetting="Polite" />
```

WPF **non** solleva `LiveRegionChanged` quando cambia il `Text`. La proprietà
dichiara l'intenzione, l'evento la realizza. Usa sempre l'apposito aiutante:

```csharp
LiveRegion.Update(StatusText, "3 chiamate in coda");
```

Riguarda ogni valore che cambia da solo — contatore della coda, stato della
chiamata, conteggio dei risultati — cioè esattamente le informazioni che
l'operatore non può andare a rileggere, perché non sa che sono cambiate.

### Un elenco da consultare è una tabella, non una frase

Dove l'operatore **cerca** — i risultati della rubrica, i contatti proposti sotto
il campo di composizione — si usa una `DataGrid` e si lascia che lo screen reader
la esponga come griglia. Le frecce verticali cambiano contatto, quelle orizzontali
percorrono i campi, e **ogni cella si legge da sola**.

La conseguenza pratica: il contenitore della riga **non** deve avere un nome
accessibile. Una descrizione composta sulla riga verrebbe letta a ogni freccia,
prima del contenuto della cella, e raddoppierebbe il tempo di ogni spostamento su
un elenco che si percorre decine di volte per turno. Per lo stesso motivo la scheda
di dettaglio sotto la tabella non è una regione dinamica.

Restano due cose da fare a mano:

```csharp
// La resa predefinita di un record elencherebbe ogni proprietà, contatto
// annidato compreso: è ciò su cui lo screen reader ricade se non trova altro.
public override string ToString() => Name;
```

```xml
<!-- L'unica cella in cui il contenuto letterale non va bene: "4021" verrebbe
     pronunciato "quattromilaventuno". -->
<Style x:Key="SpokenNumberCell" TargetType="DataGridCell">
    <Setter Property="AutomationProperties.Name" Value="{Binding SpokenNumber}" />
</Style>
```

`SpokenNumber` usa `SpeechFormatter.SpacedDigits` e non `Digits`: qui il testo non
passa dalla voce interna ma finisce nella proprietà `Name` di UI Automation, e la
resa a parole occuperebbe mezza riga braille per un solo interno.

Dove invece l'operatore **sorveglia** senza cercare — l'elenco delle chiamate in
coda — la voce composta sul contenitore resta quella giusta: lì si vuole sentire
tutto il chiamante in una lettura sola, non frugare fra le colonne.

### Selezionare non è dare il focus

```csharp
// NON basta: la sintesi legge l'elemento che ha il focus, non la lista
grid.Focus();
grid.SelectedIndex = 0;
```

Il contenitore della tabella non è la riga, e in una tabella l'elemento che ha il
focus non è nemmeno la riga: è la **cella**. È quello che rende possibile la
lettura campo per campo. Serve `GridNavigation.FocusCell`, che imposta la cella
corrente, forza il layout e porta lì il focus della tastiera.

### La prova che conta

Nessuna di queste tre cose si vede a schermo. **L'unica verifica valida è aprire la
finestra con NVDA acceso, non guardarla.**

---

## 3. Controlli standard, sempre

Si usano `ListView`, `DataGrid`, `TextBox`, `Button` reali. **Mai** un
`ItemsControl` di `Border` travestito da lista.

Il motivo è EN 301 549 §11.5, che richiede l'esposizione corretta di nome, ruolo,
stato e valore ai servizi di accessibilità della piattaforma. Un finto elenco espone
un gruppo di rettangoli: lo screen reader non sa che è una lista, non annuncia "riga
3 di 12", non consente la navigazione per righe. È il criterio su cui i collaudi
della PA si fermano più spesso.

Se serve davvero un controllo custom, richiede un `AutomationPeer` esplicito e un
test che ne verifichi le proprietà UIA.

---

## 4. Ogni elemento interattivo ha un nome

```xml
<TextBox x:Name="DialerBox"
         AutomationProperties.Name="Campo di composizione: numero o nome da cercare"
         AutomationProperties.HelpText="Digita un numero e premi Invio per chiamare." />
```

Un `Name` assente produce un elemento che lo screen reader legge come "casella di
modifica" e basta. L'operatore sa che c'è un campo, non sa a cosa serve.

`HelpText` va usato per spiegare **cosa fanno i tasti in quel contesto**, non per
ripetere il nome.

---

## 5. Nessun comando muto

Ogni azione dell'operatore produce un riscontro: un annuncio, un earcon, o entrambi.

Un comando che non risponde è indistinguibile da un comando che non ha funzionato.
L'operatore lo ripete, e su un centralino ripetere "riaggancia" o "trasferisci"
peggiora la situazione.

Vale anche per i comandi **rifiutati**: se il tasto non è valido nello stato
corrente, si dice perché.

```csharp
_announcer.Announce(Announcement.CommandResponse(
    AnnouncementTexts.CommandNotAvailable, _clock.Now));
_announcer.PlayEarcon(EarconKind.CommandRejected);
```

---

## 6. Meglio tacere che dire una cosa sbagliata

Quando un dato non è certo, l'annuncio lo omette invece di indovinarlo.

Esempi implementati:

- **Correlazione ambigua** (`CallCorrelator`): con due chiamate compatibili si
  annuncia "Trasferimento completato" invece di "Trasferimento di Mario Rossi
  completato".
- **Posizione in coda** (`YeastarOptions.WaitingListIsOrdered`): finché non è
  verificato che l'ordine dell'elenco corrisponda all'ordine di servizio, la
  posizione non viene annunciata. Dire "lei è il primo" a chi è il terzo è peggio
  che non dire nulla.
- **Capacità del provider** (`QueueProviderCapabilities`): le funzioni non
  supportate vengono nascoste, non lasciate come comandi inerti.

Per chi non può verificare a schermo, **un'informazione sbagliata è peggio di
un'informazione mancante.**

---

## 7. Il silenzio durante la conversazione

Con una chiamata attiva gli annunci non critici sono soppressi e sostituiti da
earcon. Parlare sopra la conversazione dell'operatore è il modo più rapido per far
disinstallare il prodotto.

Se aggiungi un annuncio, scegli consapevolmente la priorità:

| Priorità | Quando |
|---|---|
| `Critical` | Il mancato ascolto comporta un danno operativo: chiamata entrante, linea caduta, registrazione persa, pausa automatica |
| `High` | Esito di un'azione: chiamata risposta, trasferimento riuscito o fallito |
| `Normal` | Variazioni di contesto: conteggio coda, presenza |
| `Low` | Informativo: sincronizzazione completata |

**Se tutto è critico, l'operatore smette di ascoltare** e la voce diventa rumore di
fondo su cui parlare al telefono.

Le risposte a un comando premuto usano `Announcement.CommandResponse`: parlano
sempre, anche durante la conversazione, perché le ha chieste l'operatore.

---

## 8. Earcon per ciò che deve passare anche nel silenzio

Ogni evento che cambia la disponibilità della linea ha un earcon, perché è il canale
che sopravvive alla soppressione della voce.

Il posizionamento stereo raggruppa per famiglia: coda a sinistra, chiamata al
centro, controllo linea a destra. Non inventare posizioni nuove: la mappa spaziale
funziona perché è stabile.

`AnnouncementCoverageTests.GliEventiDiChiamataPortanoUnEarcon` verifica la copertura.

---

## 9. Il tempo si inietta, non si legge

Nessun `DateTimeOffset.Now` nei servizi del core: si usa `IClock`.

Metà della logica di accessibilità è fatta di finestre temporali — accorpamento a
800 ms, anti-ripetizione a 2 s, doppia pressione a 400 ms, timeout consultazione,
promemoria di attesa. Con l'orologio di sistema i test sarebbero lenti e a
intermittenza, cioè test che prima o poi vengono disattivati.

I servizi non possiedono timer: espongono `Pump(now)`, e il ciclo vive in
`App.StartPumpLoop`.

---

## 10. Il core non conosce la piattaforma

`BlindPhone.Core` targetta `net10.0`, non `net10.0-windows`. Non è una preferenza
stilistica: rende la violazione un errore di compilazione.

Se ti serve WPF, Win32 o l'audio dentro al core, la soluzione è un'interfaccia nel
core e l'implementazione nell'infrastruttura, non un cambio di TFM.

---

## 11. I testi si scrivono in `AnnouncementTexts`

Nessuna stringa pronunciata sparsa nel codice. Quei testi sono **l'interfaccia
utente** del prodotto e vanno riletti da chi si occupa di accessibilità e da
operatori reali, che non leggono codice.

Criteri di scrittura:

- L'informazione più importante all'inizio: chi ascolta a 500 parole al minuto
  interrompe appena ha capito.
- Niente gergo tecnico: "interno occupato", non "risposta 486".
- Entrando in una modalità si dice **cosa fanno i tasti adesso**, non solo cosa è
  appena successo.
- Numeri letti cifra per cifra tramite `SpeechFormatter.Digits`, mai passati grezzi
  alla sintesi.

---

## 12. Il layout è stabile

Le aree non cambiano posizione né dimensione relativa. Gli elenchi crescono in coda,
non riordinano il contenuto sotto le dita dell'utente. La selezione va ripristinata
dopo un aggiornamento della lista (vedi `MainWindow.RefreshQueue`): su una coda che
si ricostruisce ogni tre secondi, perdere la posizione la rende impossibile da
scorrere.

Per chi naviga con il cursore virtuale, un'interfaccia che si riorganizza è come una
stanza in cui qualcuno sposta i mobili ogni giorno.

---

## 13. Prima di aprire una pull request

- [ ] `dotnet build BlindPhone.sln` — zero errori e **zero warning**
- [ ] `dotnet test` — tutti verdi
- [ ] Ogni nuovo elemento interattivo ha `AutomationProperties.Name`
- [ ] Ogni nuova regione dinamica si aggiorna con `LiveRegion.Update`, mai con `.Text =`
- [ ] Ogni nuovo elenco da consultare è una `DataGrid`, senza nome accessibile sulla riga e con `ToString()` esplicito
- [ ] Ogni spostamento programmatico di selezione porta il focus alla cella, non alla tabella
- [ ] Ogni nuova transizione di stato ha il suo annuncio
- [ ] Ogni nuovo comando risponde anche quando viene rifiutato
- [ ] I nuovi testi pronunciati sono in `AnnouncementTexts`
- [ ] Nessun `DateTimeOffset.Now` nel core
- [ ] Provato con NVDA **e** con JAWS, non solo con uno dei due

L'ultimo punto non è sostituibile da nessuna verifica automatica.

---

## 14. La verifica che conta davvero

Nessuna quantità di conformità formale sostituisce il collaudo con operatori non
vedenti reali. L'analisi prevede tre sessioni con almeno cinque operatori e un
consulente non vedente esperto di centralino **coinvolto dall'inizio e retribuito**,
non interpellato alla fine come certificatore.

Se stai per prendere una decisione di interazione e non sai come andrà, la risposta
non è in questo documento: è in una telefonata a un operatore.
