# CONTESTO ED OBIETTIVO
Dobbiamo implementare la funzionalità BLF (Busy Lamp Field) all'interno del nostro server di segnalazione SIP proprietario sviluppato in **.NET 9.0** (C# 13). 
La libreria di rete e segnalazione core utilizzata è **SIPSorcery (versione 10)**.

L'obiettivo è gestire le richieste `SUBSCRIBE` in ingresso dai telefoni IP (Watcher) e inviare i relativi aggiornamenti `NOTIFY` quando lo stato dei dialoghi/chiamate degli interni monitorati (Presentity) cambia.

# SPECIFICHE E STANDARD DA SEGUIRE
1. SIP Event Notification Framework (RFC 6665)
2. SIP Dialog Event Package (RFC 4235): Utilizzo del pacchetto eventi `dialog` e del content-type `application/dialog-info+xml`.

# COMPONENTI E REQUISITI ARCHITETTURALI (.NET 9 / SIPSorcery)

## 1. Gestione Inbound SUBSCRIBE (`dialog` event package)
- Configurare un gestore di eventi o estendere il meccanismo di SIPSorcery per intercettare le richieste `SUBSCRIBE` in ingresso con header `Event: dialog`.
- Convalidare la richiesta: restituire `489 Bad Event` se l'evento non è supportato, o `404 Not Found` se l'interno richiesto non esiste.
- Registrare la sottoscrizione in un archivio persistente o in-memory (filo-diretto thread-safe, es. `ConcurrentDictionary`), estraendo il valore dall'header `Expires`.
- Rispondere con un `200 OK` appropriato impostando la durata corretta della sottoscrizione.
- Gestire il rinnovo periodico e la rimozione in caso di scadenza o se arriva una `SUBSCRIBE` con `Expires: 0`.

## 2. Generazione e Invio dei NOTIFY (Outbound)
- Inviare un `NOTIFY` immediato (payload iniziale) non appena la sottoscrizione viene accettata (`200 OK`), come prescritto da RFC 6665.
- Implementare un meccanismo di notifica asincrono (tramite `System.Threading.Channels` o pattern basato su eventi) che, al variare dello stato di un interno, recuperi tutti i "Watcher" attivi e invii loro un `NOTIFY` con il payload XML aggiornato.
- Popolare correttamente gli header SIP del `NOTIFY`, in particolare `Subscription-State` (`active`, `pending`, o `terminated; reason=timeout`).

## 3. Generazione XML RFC 4235 (`application/dialog-info+xml`)
- Creare una classe di utility per generare il corpo XML (`application/dialog-info+xml`) in modo efficiente, sfruttando le funzionalità moderne di .NET 9.
- Mappare gli stati della chiamata di SIPSorcery (o gli stati interni del nostro PBX) sui valori RFC 4235 nell'elemento `<state>` del tag `<dialog>`:
  - **Idle / Libero**: `<dialog-info state="full">` vuoto o con dialoghi terminati.
  - **Ringing / Squillo**: `<state>early</state>` (indicando la `direction` corretta, es. `initiator` o `recipient`).
  - **In Chiamata / Connesso**: `<state>confirmed</state>`.
  - **Terminato**: `<state>terminated</state>`.

# COSA DEVI FARE (TASK)
1. **BLFSubscriptionManager:** Scrivi una classe C# thread-safe per gestire il ciclo di vita delle sottoscrizioni BLF integrate con il server SIP di SIPSorcery (gestione dei dialoghi SIP, timer di expiry).
2. **XmlPayloadBuilder:** Crea un generatore di stringhe XML robusto e formattato secondo lo schema RFC 4235 per `dialog-info+xml`.
3. **Integrazione Segnalazione:** Mostra un esempio di codice chiaro su come agganciare questa logica al `SIPTransport` o al `SIPUserAgent` di SIPSorcery per intercettare la `SUBSCRIBE` e inviare i `NOTIFY` asincroni tramite il canale di trasporto corretto.
4. **Best Practices C# 13 / .NET 9:** Utilizza codice asincrono (`async/await`), `Task`, una corretta gestione delle eccezioni e logging integrato tramite `ILogger`.

Fornisci il codice completo, modulare, pronto per l'ambiente di produzione e privo di segnaposto o logiche fittizie.