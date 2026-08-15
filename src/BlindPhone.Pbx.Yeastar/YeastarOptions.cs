namespace BlindPhone.Pbx.Yeastar;

/// <summary>
/// Parametri di collegamento al PBX Yeastar P-Series.
/// </summary>
/// <remarks>
/// <para>
/// Prerequisiti da verificare sul PBX del cliente prima di quotare il progetto
/// (checklist completa in YEASTAR-INTEGRAZIONE.md §13):
/// </para>
/// <list type="bullet">
///   <item><description>Modello P550, P560 o P570 per l'Appliance Edition, oppure edizione Software/Cloud.</description></item>
///   <item><description>Firmware 37.7.0.16 o superiore.</description></item>
///   <item><description><b>Piano Enterprise o Ultimate</b>: il piano Standard non include l'API. È l'unico prerequisito realmente bloccante.</description></item>
///   <item><description>Funzione API abilitata nel portale web, con Client ID e Client Secret generati.</description></item>
/// </list>
/// </remarks>
public sealed record YeastarOptions
{
    /// <summary>Nome host o indirizzo IP del PBX.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Porta dell'API. Negli esempi della documentazione Yeastar è la 8088.</summary>
    public int Port { get; init; } = 8088;

    /// <summary>Se usare HTTPS e WSS. Da lasciare attivo salvo laboratori di prova.</summary>
    public bool UseTls { get; init; } = true;

    /// <summary>
    /// Se accettare certificati non validati.
    /// </summary>
    /// <remarks>
    /// Da usare soltanto in laboratorio, con PBX dotati di certificato autofirmato.
    /// In produzione presso una PA va lasciato disattivo: il collaudo di sicurezza
    /// lo verifica.
    /// </remarks>
    public bool AllowUntrustedCertificate { get; init; }

    /// <summary>
    /// Client ID generato nel portale del PBX.
    /// </summary>
    /// <remarks>
    /// <b>Un Client ID per postazione.</b> Yeastar limita a otto gli access token
    /// simultanei per applicazione: con più di otto postazioni BlindPhone che
    /// condividono le stesse credenziali, la nona invaliderebbe il token di
    /// un'altra. Il Client ID è quindi un parametro di postazione, non di prodotto.
    /// </remarks>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Client Secret generato nel portale del PBX.
    /// </summary>
    /// <remarks>
    /// Non va mai scritto in chiaro nel file di configurazione: viene conservato
    /// tramite DPAPI o Windows Credential Manager, come le credenziali SIP.
    /// </remarks>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>Interno SIP dell'operatore, usato per correlare gli eventi.</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>Identificativi delle code da sorvegliare.</summary>
    public IReadOnlyList<int> QueueIds { get; init; } = [];

    /// <summary>Intervallo di interrogazione quando non ci sono chiamate in coda.</summary>
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Intervallo di interrogazione quando ci sono chiamate in attesa.
    /// </summary>
    /// <remarks>
    /// Più frequente perché con la coda popolata il tempo di attesa è
    /// l'informazione che cambia più in fretta ed è quella su cui l'operatore
    /// decide la priorità.
    /// </remarks>
    public TimeSpan ActivePollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Ritardo con cui si riallinea dopo un evento, per lasciare al PBX il tempo di
    /// aggiornare il proprio stato interno.
    /// </summary>
    public TimeSpan PostEventRefreshDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Intervallo dell'heartbeat sul WebSocket.
    /// </summary>
    /// <remarks>
    /// La documentazione Yeastar indica che la connessione viene chiusa dopo 60
    /// secondi di inattività. Venti secondi lasciano un margine di tre tentativi
    /// prima della chiusura, sufficiente ad assorbire una latenza di rete anomala
    /// senza far cadere il collegamento.
    /// </remarks>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Anticipo con cui rinnovare l'access token rispetto alla sua scadenza.
    /// </summary>
    /// <remarks>
    /// Il token dura 1800 secondi. Rinnovando cinque minuti prima si evita di
    /// scoprire la scadenza da un 401 nel mezzo di un trasferimento.
    /// </remarks>
    public TimeSpan TokenRenewalMargin { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Timeout delle richieste REST.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Ritardo massimo fra due tentativi di riconnessione.</summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Se abilitare il tentativo di prelievo selettivo di una chiamata dalla coda.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disattivo per impostazione predefinita. La documentazione pubblica Yeastar
    /// non espone un endpoint dedicato al prelievo di una chiamata specifica dalla
    /// coda; l'ipotesi implementativa è usare <c>call/transfer</c> con il
    /// <c>channel_id</c> della chiamata in attesa e destinazione l'interno
    /// dell'operatore, che è semanticamente il gesto del trascinamento nel Queue
    /// Panel.
    /// </para>
    /// <para>
    /// <b>Va verificato su PBX reale prima di attivarlo</b>
    /// (YEASTAR-INTEGRAZIONE.md §8.1). Finché la verifica non è fatta, il provider
    /// dichiara di non supportare la funzione e l'interfaccia non offre il comando,
    /// invece di offrirne uno che non fa nulla.
    /// </para>
    /// </remarks>
    public bool EnableExperimentalQueuePick { get; init; }

    /// <summary>
    /// Se l'ordine dell'elenco di attesa restituito dal PBX corrisponde all'ordine
    /// di servizio della coda.
    /// </summary>
    /// <remarks>
    /// Disattivo per impostazione predefinita, per lo stesso motivo del prelievo
    /// selettivo: la documentazione non lo garantisce. Finché non è verificato, la
    /// posizione non viene annunciata. Dire "lei è il primo" a chi è il terzo è
    /// peggio che non dire nulla.
    /// </remarks>
    public bool WaitingListIsOrdered { get; init; }

    /// <summary>Radice degli endpoint REST.</summary>
    public string BaseUrl => $"{(UseTls ? "https" : "http")}://{Host}:{Port}/openapi/v1.0";

    /// <summary>Endpoint WebSocket per la sottoscrizione agli eventi.</summary>
    public string WebSocketUrl => $"{(UseTls ? "wss" : "ws")}://{Host}:{Port}/openapi/v1.0/subscribe";

    /// <summary>Verifica che i parametri obbligatori siano valorizzati.</summary>
    /// <param name="error">Messaggio di errore in linguaggio naturale, pronunciabile.</param>
    /// <returns><c>true</c> se la configurazione è utilizzabile.</returns>
    public bool Validate(out string error)
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            error = "Indirizzo del centralino non configurato";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            error = "Credenziali API del centralino non configurate";
            return false;
        }

        if (QueueIds.Count == 0)
        {
            error = "Nessuna coda da sorvegliare configurata";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
