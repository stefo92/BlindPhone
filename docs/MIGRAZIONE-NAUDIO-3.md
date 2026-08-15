# Migrazione a NAudio 3.0

Piano di lavoro. Non è ancora stato eseguito: la soluzione resta su **NAudio 2.3.0**.

## 1. Perché farla, e perché non di fretta

NAudio 2.3.0 non ha vulnerabilità note — `dotnet list package --vulnerable` è pulito —
e funziona. Non c'è nessuna urgenza: questa migrazione è manutenzione, non una
riparazione.

C'è però una ragione per non rimandarla all'infinito. La 2.x è l'ultima riga della
serie precedente: le correzioni future andranno sulla 3.x, e più si aspetta più il
salto si accumula insieme ad altri. Meglio farla in una giornata scelta che in una
imposta.

E c'è una ragione precisa per **non** infilarla in un rilascio insieme ad altro: è
l'unica parte del prodotto che nessun test automatico copre, ed è quella che, se si
rompe, si rompe in reparto mentre qualcuno sta parlando.

## 2. Che cosa cambia davvero

Misurato compilando il progetto contro NAudio 3.0.0, non dedotto dalle note di
rilascio. Sono tre cose, e solo tre.

### 2.1 I pacchetti si sono divisi

Il pacchetto `NAudio` non porta più con sé `NAudio.CoreAudioApi` (WASAPI) né
`NAudio.MediaFoundation` (codifica delle registrazioni). Vivono in **`NAudio.Wasapi`**,
che va aggiunto esplicitamente.

Quanto pesa: **da 38 errori di compilazione a 4**, aggiungendo quel solo riferimento.
Trentaquattro dei trentotto errori erano soltanto questo.

### 2.2 I provider passano a `Span`

Le due interfacce su cui si regge tutto il percorso audio hanno cambiato firma, e la
versione con l'array **non esiste più**: non è un'aggiunta, è una sostituzione.

| Prima | Adesso |
|---|---|
| `IWaveProvider.Read(byte[] buffer, int offset, int count)` | `IWaveProvider.Read(Span<byte> buffer)` |
| `ISampleProvider.Read(float[] buffer, int offset, int count)` | `ISampleProvider.Read(Span<float> buffer)` |

### 2.3 `IMMNotificationClient` non è più accessibile

L'interfaccia COM con cui `NAudioEngine` riceve gli avvisi di cambio dispositivo è
diventata `internal`. Al suo posto NAudio 3.0 espone una classe pubblica,
`MMDeviceNotificationClient`, che si ottiene da
`MMDeviceEnumerator.CreateNotificationClient(bool)` e offre cinque eventi:

    DeviceStateChanged    DeviceStateChangedEventArgs
    DeviceAdded           DeviceNotificationEventArgs
    DeviceRemoved         DeviceNotificationEventArgs
    DefaultDeviceChanged  DefaultDeviceChangedEventArgs
    PropertyValueChanged  DevicePropertyChangedEventArgs

È `IDisposable`: la sottoscrizione va sciolta alla chiusura del motore audio.

## 3. La superficie di rottura, file per file

Tutta dentro `BlindPhone.Audio`. `BlindPhone.Telephony` non è toccato.

| File | Che cosa cambia | Dimensione |
|---|---|---|
| `NAudioEngine.cs` | Non implementa più `IMMNotificationClient`: si sottoscrive ai cinque eventi di `MMDeviceNotificationClient`. I cinque metodi attuali (`OnDeviceStateChanged`, `OnDeviceAdded`, `OnDeviceRemoved`, `OnDefaultDeviceChanged`, `OnPropertyValueChanged`) diventano gestori di eventi. | La parte che richiede pensiero |
| `JitterBufferProvider.cs` | Due implementazioni di `Read` — `JitterBufferProvider` e `SilencePaddedProvider` — passano a `Span<byte>`, e con loro le due chiamate a `_source.Read(...)` | Meccanica |
| `ToneEarconPlayer.cs` | `StereoToneProvider.Read` passa a `Span<float>` | Meccanica |
| `SipAudioEndPoint.cs` | Solo tipi WASAPI (`MMDevice`, `DataFlow`): si risolve con il pacchetto, nessuna modifica al codice | Nessuna |
| `CallRecorder.cs` | Solo `NAudio.MediaFoundation`: idem | Nessuna |

In tutto: **tre implementazioni di `Read`, due chiamate interne, una sottoscrizione da
riscrivere.** Il resto è un riferimento a pacchetto.

## 4. Il punto che rende il lavoro delicato

**Nessuno dei 936 test tocca queste classi.** `JitterBufferProvider`,
`ToneEarconPlayer`, `NAudioEngine` e `SipAudioEndPoint` non hanno copertura: la suite
resterà verde per tutta la migrazione senza dire assolutamente nulla su di essa.

Il rischio non è nella compilazione — quella o passa o no. È nel comportamento a
runtime, dove un errore si manifesta come audio che si interrompe, un earcon che
gracchia, o una cuffia scollegata che non viene annunciata. Tutte cose che si vedono
solo con un dispositivo vero e un centralino vero.

Va aggiunto che **questa copia di lavoro non è sotto controllo di versione**: non c'è
un ramo su cui isolare il lavoro né un commit a cui tornare. La rete di sicurezza, per
ora, è il pacchetto 1.0.10 già costruito.

## 5. I passi, in ordine

### Passo 0 — La rete di sicurezza, prima di toccare qualunque cosa

- Mettere la cartella sotto controllo di versione, oppure copiarla per intero.
- Conservare `dist\BlindPhone-1.0.10-x64.msi` e le sue note: è la versione a cui si
  torna se la migrazione va male, ed è già provata.
- Tenere **almeno una postazione sulla 1.0.10** finché la nuova non ha superato la
  verifica in reparto.

### Passo 1 — Scrivere i test che oggi mancano, *prima* di migrare

È l'unica rete automatica ottenibile, e va costruita mentre il comportamento è ancora
quello buono: si scrivono su NAudio 2.3.0, devono passare lì, e devono continuare a
passare dopo. Le parti verificabili senza dispositivi sono logica pura:

- **`JitterBufferProvider`**: riempimento, svuotamento, conteggio degli underrun,
  silenzio inserito. Si alimenta con un sorgente finto e si misurano i byte in uscita.
- **`SilencePaddedProvider`**: quanto silenzio aggiunge quando il sorgente non basta.
- **`StereoToneProvider`**: forma d'onda, ampiezza, panoramica destra/sinistra, durata.
  Il tono è deterministico: si può confrontare campione per campione.

Sono i tre punti in cui il passaggio a `Span` può introdurre un errore di offset, ed è
esattamente l'errore che a orecchio si sente come un fruscio.

### Passo 2 — Il riferimento al pacchetto

Aggiungere `NAudio.Wasapi` e portare entrambi a 3.0.0. Verifica: gli errori scendono
da 38 a 4, e i quattro rimasti sono quelli previsti al punto 3.

### Passo 3 — Le tre implementazioni di `Read`

Conversione meccanica, ma con due attenzioni:

- l'`offset` sparisce: dove prima si scriveva a partire da `offset`, ora si affetta lo
  `Span` con `buffer[..n]` o `buffer.Slice(...)`;
- il valore restituito resta il numero di byte (o di campioni) effettivamente scritti,
  e chi chiama continua a fidarsene: un ritorno sbagliato non produce un errore, produce
  audio.

I test del passo 1 devono passare senza essere modificati. Se per farli passare bisogna
cambiarli, la conversione è sbagliata.

### Passo 4 — Gli avvisi di cambio dispositivo

Riscrivere la sottoscrizione con `MMDeviceNotificationClient`. Verifica a banco, senza
centralino: si avvia il motore audio, si scollega la cuffia e si controlla nel registro
che l'evento arrivi e che l'audio migri sul dispositivo di riserva.

È la funzione descritta in `NAudioEngine` come «la cuffia scollegata durante una
chiamata»: l'operatore deve sapere che sta parlando dagli altoparlanti **prima** di
dire ad alta voce qualcosa di riservato. Se questa parte non funziona, la migrazione
non è finita, per quanto tutto il resto compili.

### Passo 5 — Verifica a banco

Con `dotnet run` sul prodotto, senza centralino:

- enumerazione dei quattro dispositivi (microfono, conversazione, suoneria, annunci);
- earcon di ogni tipo, con la panoramica destra/sinistra;
- registrazione di una chiamata simulata e riapertura del file prodotto
  (è la parte che passa da MediaFoundation);
- attenuazione della voce quando parla la sintesi.

### Passo 6 — Verifica in reparto

Con centralino e cuffia veri. La lista al punto 7.

### Passo 7 — Pacchetto

Nuova versione **1.0.11**, note di versione, impronta SHA-256 rifatta. La 1.0.10 resta
in `dist\` come via di ritorno.

## 6. Che cosa può andare storto, e come ci si accorge

| Sintomo | Dove guardare |
|---|---|
| Fruscio o scoppiettio in conversazione | Offset sbagliato in `JitterBufferProvider.Read` |
| Earcon distorto o su un solo canale | `StereoToneProvider.Read` |
| Audio che si interrompe dopo qualche secondo | Valore restituito da `Read` quando il buffer non è pieno |
| Cuffia scollegata senza annuncio | Sottoscrizione a `MMDeviceNotificationClient` |
| Registrazione che non si apre | `NAudio.MediaFoundation` dentro `NAudio.Wasapi` |
| Elenco dispositivi vuoto in Impostazioni | `MMDeviceEnumerator` / `DataFlow` |

## 7. Verifica in reparto: la lista

Da spuntare **prima** di sostituire la 1.0.10 sulle postazioni:

- [ ] Chiamata entrante: suoneria sul dispositivo giusto, voce sul suo
- [ ] Conversazione di almeno due minuti, in entrambi i versi, senza interruzioni
- [ ] Cuffia scollegata durante la conversazione: l'audio migra e la voce lo annuncia
- [ ] Cuffia ricollegata: torna al dispositivo scelto
- [ ] Trasferimento con annuncio: si sente l'interno, il chiamante non sente
- [ ] Conferenza a tre: tutti si sentono
- [ ] Inclusione su interno occupato: il segnale ogni cinque secondi si sente
- [ ] Registrazione di una chiamata, e riascolto del file
- [ ] Earcon di risposta, chiusura, attesa e comando rifiutato
- [ ] Attenuazione: la voce della sintesi non copre il chiamante
- [ ] Cambio di dispositivo dalle Impostazioni, con una chiamata in corso

## 8. Stima

Il codice è mezza giornata: la superficie è piccola e misurata. I test del passo 1
sono un'altra mezza giornata, e sono la parte che conviene di più — restano lì anche
dopo, e oggi quella zona non ne ha nessuno.

La verifica in reparto è la voce che decide il calendario, non le altre due.

## 9. Quando non farla

- Non insieme a un altro cambiamento: se qualcosa si rompe si deve sapere da dove viene.
- Non il giorno prima di un turno scoperto.
- Non senza aver prima rimesso la cartella sotto controllo di versione (passo 0).
