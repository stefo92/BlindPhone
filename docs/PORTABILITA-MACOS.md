# Portabilità su macOS, Apple Silicon

Piano di lavoro. **Non è stato eseguito**: qui c'è cosa fare, in che ordine, e dove
sono i punti in cui il lavoro può fermarsi.

Il documento è scritto per essere ripreso fra settimane o mesi, quindi dice anche
*perché* ogni passo sta dove sta.

---

## 1. La domanda da sciogliere prima di tutte

BlindPhone esiste per una ragione sola: **un centralinista non vedente lavora alla
velocità di un centralinista vedente**. Non è un programma che «supporta
l'accessibilità»: è un programma la cui interfaccia è l'ascolto.

Su Windows quella promessa poggia su tre cose:

1. la pipeline di annuncio **del prodotto**, che decide cosa dire, con che priorità e
   cosa tacere;
2. NVDA e JAWS, raggiunti da un ponte nativo, che leggono i controlli standard;
3. UI Automation di WPF, che espone nomi, ruoli e stati a chi legge.

Su macOS la seconda e la terza cambiano interlocutore: c'è VoiceOver, che si parla
attraverso NSAccessibility, e **non si pilota** come si pilota NVDA. La prima invece è
già nostra e già portabile.

**Prima di impegnare mesi su una porta, va risposto a questa domanda: su macOS un
operatore non vedente riesce a rispondere, trasferire e cercare in rubrica alla stessa
velocità?** Se la risposta è no, il resto del piano non ha valore, per quanto il codice
compili. Il passo 0 serve esattamente a rispondere, e costa pochi giorni invece di mesi.

---

## 2. Da dove si parte davvero: la misura

Righe di codice C# effettivamente versionate, misurate oggi.

| Progetto | Righe | Target | Stato rispetto alla porta |
|---|---:|---|---|
| `BlindPhone.Core` | 16.072 | `net10.0` | **Già portabile.** Unica dipendenza: `Microsoft.Extensions.Logging.Abstractions` |
| `BlindPhone.Pbx.Yeastar` | 2.418 | `net10.0` | **Già portabile.** HTTP e JSON |
| `BlindPhone.Contacts` | 3.538 | `net10.0-windows` | **Portabile quasi gratis**: nessuna riga usa API di Windows |
| `BlindPhone.Telephony` | 4.143 | `net10.0-windows` | **Portabile con poco**: SIPSorcery è multipiattaforma, il vincolo è un riferimento diretto a `NAudioEngine` |
| `BlindPhone.Infrastructure` | 5.194 | `net10.0-windows` | Misto: la maggior parte è portabile, quattro file no |
| `BlindPhone.Audio` | 3.573 | `net10.0-windows` | **Da riscrivere**: è WASAPI |
| `BlindPhone.Accessibility` | 757 | `net10.0-windows` | **Da riscrivere**: è NVDA, JAWS e SAPI |
| `BlindPhone.App` | 15.217 + 4.897 di XAML | `net10.0-windows` | **La domanda grossa**: è WPF |

E i collaudi:

| | Righe | |
|---|---:|---|
| `BlindPhone.Core.Tests` | 9.067 | `net10.0`: **girano su Mac senza toccarli** |
| `BlindPhone.Windows.Tests` | 5.495 | Legati alla piattaforma |

**Portabile subito: 18.490 righe. Portabile con poco lavoro: altre 7.681.** Insieme sono
più della metà del prodotto, ed è merito di una decisione presa all'inizio: il core
targetta `net10.0` e non `net10.0-windows`, quindi un riferimento a WPF o a Win32 non
compila. Quella regola, che finora è sembrata solo igiene, è ciò che rende questa porta
discutibile invece che impensabile.

### L'interoperabilità nativa, per intero

Diciotto dichiarazioni in otto file. È tutta la superficie da rifare:

| Libreria | Quante | Dove | Sostituto su macOS |
|---|---:|---|---|
| `user32.dll` | 12 | primo piano, scorciatoie globali, istanza unica, tema | AppKit, Carbon, `NSDistributedNotificationCenter` |
| `nvdaControllerClient64.dll` | 4 | ponte NVDA | VoiceOver via `NSAccessibility` |
| `kernel32.dll` | 1 | identificativo del thread | non serve |
| `MAPI32.DLL` | 1 | invio della segnalazione per posta | `NSSharingService` o `mailto:` |

---

## 3. Le quattro decisioni da prendere prima di scrivere codice

### 3.1 Con che cosa si disegna l'interfaccia

WPF non esiste su macOS, e non esisterà. Tre strade, e nessuna è indolore.

**Avalonia UI** — XAML molto vicino a quello di WPF, stessa idea di stili, template e
binding. È la strada che riusa di più: buona parte delle 4.897 righe di XAML si
traduce, e il code-behind ancora di più. Il rischio sta nell'accessibilità: Avalonia
espone NSAccessibility, ma è una superficie più giovane di quella di WPF, e questo
prodotto la usa fino in fondo — tabelle navigate cella per cella, nomi accessibili
calcolati, regioni dinamiche annunciate. **Va verificata sul campo, non sulla
documentazione.**

**.NET MAUI su Mac Catalyst** — sconsigliata. Deriva da UIKit, cioè da un modello
pensato per il dito, mentre qui tutto è tastiera; e l'accessibilità di Catalyst è più
debole di quella di AppKit.

**AppKit nativo** — è il comportamento VoiceOver migliore possibile, perché è la
piattaforma stessa. Ma è la riscrittura totale dell'interfaccia, con molto meno aiuto
dall'ecosistema .NET.

**Raccomandazione: Avalonia, ma solo se il passo 0 lo conferma.** Se il prototipo mostra
che VoiceOver non legge una tabella Avalonia in modo utilizzabile, l'alternativa seria è
AppKit, non MAUI — e a quel punto il progetto è un altro progetto, e va deciso come tale.

### 3.2 Che cosa fa parlare il programma

Buona notizia, ed è la più importante di tutto il documento: **2.679 righe di pipeline di
annuncio stanno nel core e sono già portabili.** Cosa dire, con che priorità, cosa tacere
durante la conversazione, cosa non ripetere: è tutto codice nostro e resta.

Quello che cambia è l'ultimo metro: chi pronuncia. Su Windows sono SAPI, NVDA e JAWS
(757 righe). Su macOS c'è `AVSpeechSynthesizer` per la voce del prodotto, e VoiceOver
per la lettura dei controlli. La voce interna è la parte facile.

### 3.3 Dove passa l'audio

`BlindPhone.Audio` è WASAPI dalla prima riga all'ultima: instradamento a quattro vie,
buffer di jitter, earcon, ponte della conferenza, registrazione. Su macOS il
corrispettivo è CoreAudio, raggiungibile via AVFoundation.

Il lavoro **non** è tradurre riga per riga: è reimplementare `IAudioEngine`, che è già
l'interfaccia giusta. Tre parti si salvano quasi intatte perché sono logica pura e non
API di sistema — buffer di jitter, riempimento del silenzio, generazione dei toni — e
hanno già dodici prove automatiche che le descrivono (`CatenaAudioTests`).

### 3.4 Come si distribuisce

L'installatore WiX è Windows. Su macOS servono: bundle `.app`, firma con un certificato
Apple Developer ID, **notarizzazione** (obbligatoria, non è un dettaglio burocratico:
senza, Gatekeeper non lo apre), hardened runtime, e i permessi dichiarati —
`NSMicrophoneUsageDescription` per il microfono e l'autorizzazione Accessibilità per le
scorciatoie globali, che l'operatore deve concedere a mano la prima volta.

---

## 4. I passi

### Passo 0 — Il prototipo che decide se andare avanti  ⟵ *da fare per primo*

Pochi giorni, e serve a rispondere alla domanda della sezione 1 prima di spendere il
resto.

1. Su un Mac Apple Silicon con .NET 10: `dotnet test` su `BlindPhone.Core.Tests`.
   Sono 9.067 righe di collaudi che non toccano la piattaforma: **devono passare senza
   modifiche**. Se passano, metà prodotto è già dimostrata viva su macOS.
2. Una finestrella Avalonia con dentro **solo** le tre cose che qui contano davvero:
   una tabella navigabile cella per cella, un campo di composizione con elenco di
   proposte, una regione dinamica che annuncia.
3. **Provarla con VoiceOver, a occhi chiusi, con un centralinista.** Non con la
   documentazione di Avalonia: con VoiceOver e una persona che quel lavoro lo fa.

**Criterio di uscita:** se la tabella non si percorre cella per cella e gli annunci non
arrivano, ci si ferma qui e si riporta il risultato. È un esito, non un fallimento: aver
speso pochi giorni invece di mesi è esattamente il valore di questo passo.

### Passo 1 — Liberare ciò che è già libero

Nessuna riga di macOS, solo pulizia che serve anche a Windows.

- `BlindPhone.Contacts` → `net10.0`. Non contiene nulla di Windows: il target va
  corretto e basta.
- `BlindPhone.Telephony` → `net10.0`. Il vincolo è un parametro `NAudioEngine` nel
  costruttore di `SipTelephonyEngine`: va sostituito con `IAudioEngine`, che è
  l'interfaccia che quel codice usa davvero. **È un miglioramento anche sulla versione
  Windows**, perché toglie una dipendenza concreta da un dettaglio di piattaforma.
- Separare da `BlindPhone.Infrastructure` i quattro file legati a Windows — DPAPI,
  registro, scorciatoie globali, posta MAPI — dietro interfacce, come già sono
  `IAudioEngine` e le altre.

Verifica: la soluzione compila e i 952 collaudi passano come prima.

### Passo 2 — Prova che si telefona, senza interfaccia

Un programma da riga di comando su Mac che registra l'account SIP e fa una chiamata,
con l'audio su CoreAudio. Nessuna finestra.

È il secondo momento di verità: dimostra SIPSorcery, la rete, i codec e l'audio in un
colpo solo, e lo fa **prima** che l'interfaccia costi qualcosa. Se l'audio non passa,
si scopre qui.

### Passo 3 — Il motore audio

Reimplementare `IAudioEngine` su CoreAudio: enumerazione dei dispositivi, instradamento
a quattro vie, avvisi di cambio dispositivo (la cuffia scollegata durante una chiamata),
attenuazione.

Le prove della catena audio esistenti valgono anche qui, perché misurano la logica e non
le API.

### Passo 4 — La voce e lo screen reader

`ISpeechOutput` su `AVSpeechSynthesizer` con voci italiane; `IScreenReaderBridge` su
VoiceOver. Da verificare presto: quando l'operatore silenzia la voce, VoiceOver e la
voce del prodotto non devono accavallarsi — su Windows quel comportamento è regolato, e
la regolazione va rifatta.

### Passo 5 — L'interfaccia

La parte lunga: 15.217 righe e 4.897 di XAML. Nell'ordine in cui l'operatore le usa,
non in quello in cui sono comode da scrivere:

1. finestra principale, risposta e chiusura;
2. campo di composizione con la ricerca in rubrica;
3. trasferimento;
4. ultime chiamate e rubrica;
5. impostazioni, che è la più grande ma la meno usata.

Dopo ciascuna, la prova con VoiceOver. Non alla fine.

### Passo 6 — Le cose di sistema

Scorciatoie globali (permesso Accessibilità), istanza unica, icona nella barra dei menu
al posto dell'area di notifica, segreti nel Keychain al posto di DPAPI, criterio di
postazione in un plist o via MDM al posto del registro.

### Passo 7 — Pacchetto

`osx-arm64`, bundle `.app`, firma, notarizzazione, `.dmg`. E un documento di
installazione che spieghi i due permessi da concedere: microfono e Accessibilità.

---

## 5. Che cosa può fermare il lavoro

| Rischio | Quando si scopre | Cosa fare |
|---|---|---|
| VoiceOver non legge le tabelle Avalonia in modo utilizzabile | Passo 0 | Fermarsi. Valutare AppKit, che è un altro progetto |
| L'audio CoreAudio ha latenza o interruzioni | Passo 2 | Si scopre presto e su un programma piccolo, non dentro l'interfaccia |
| Le scorciatoie globali richiedono un permesso che l'ente non concede | Passo 6 | Verificarlo **con l'ente prima del passo 5**, non dopo |
| La notarizzazione richiede un account Apple Developer a pagamento e intestato | Passo 7 | Sistemarlo all'inizio: è burocrazia, e la burocrazia non si comprime |

---

## 6. Quello che questo piano **non** dice

Non dice quanto dura. Le righe si contano, il tempo no: dipende da quanto Avalonia
regge sull'accessibilità, che è precisamente ciò che il passo 0 misura. Chiunque dia una
stima in mesi prima di aver fatto il passo 0 la sta inventando.

Non dice se convenga. Una postazione di centralino in un ente pubblico italiano è quasi
sempre Windows: la porta su macOS ha senso se c'è una richiesta reale, non perché il
codice se lo merita.

E non tocca la manutenzione della versione Windows, che resta il prodotto in esercizio.
Ogni riga scritta per macOS che passa dal core è una riga che serve a entrambe; ogni riga
scritta due volte è debito che si paga due volte, per sempre.
