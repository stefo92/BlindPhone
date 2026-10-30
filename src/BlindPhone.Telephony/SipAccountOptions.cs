using BlindPhone.Core.Domain;

namespace BlindPhone.Telephony;

/// <summary>Trasporto usato per la segnalazione SIP.</summary>
public enum SipTransportKind
{
    /// <summary>UDP. Il piu' diffuso, nessuna protezione.</summary>
    Udp,

    /// <summary>TCP. Utile con messaggi grandi o NAT che maltrattano UDP.</summary>
    Tcp,

    /// <summary>TLS. Segnalazione cifrata, raccomandato in produzione.</summary>
    Tls,
}

// La politica di cifratura vive nel dominio, in BlindPhone.Core.Domain.SrtpPolicy:
// e' una decisione dell'ente su cosa puo' viaggiare in chiaro, non un dettaglio di
// protocollo, e la regola che la applica si collauda senza toccare la rete.

/// <summary>
/// Parametri di un account SIP.
/// </summary>
/// <remarks>
/// L'ordine dei codec merita una nota. G.722 è in cima non per preferenza di
/// qualità ma per conformità: EN 301 549 §6.1 richiede, per l'ICT con comunicazione
/// vocale bidirezionale, il supporto di una banda audio con limite superiore di
/// almeno 7000 Hz. Metterlo in coda alla lista significherebbe negoziare quasi
/// sempre G.711 e non poter dimostrare il requisito sulla chiamata reale in sede di
/// collaudo.
/// </remarks>
public sealed record SipAccountOptions
{
    /// <summary>Nome dell'account, mostrato nella diagnostica.</summary>
    public string Name { get; init; } = "Principale";

    /// <summary>Nome visualizzato inviato alla controparte.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Numero da presentare a chi si chiama, al posto dell'interno del centralinista.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Risponde a un problema concreto del posto operatore: chi riceve una chiamata
    /// dal centralino vede l'interno del centralinista e da quel momento richiama
    /// <b>quello</b>, scavalcando il numero pubblico dell'ente. Nel giro di qualche
    /// mese la postazione si ritrova una linea diretta che nessuno ha mai deciso di
    /// aprire, non filtrata e non coperta quando il centralinista non c'è.
    /// </para>
    /// <para>
    /// Vuoto significa nessuna alterazione: parte l'identità dell'account, che è il
    /// comportamento di sempre.
    /// </para>
    /// </remarks>
    public string OutboundCallerId { get; init; } = string.Empty;

    /// <summary>Interno o utenza SIP.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Identificativo di autenticazione, se diverso dall'utenza.</summary>
    public string? AuthId { get; init; }

    /// <summary>Password. Conservata tramite DPAPI, mai in chiaro su disco.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Dominio o realm SIP.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Server di registrazione. Se assente si usa il dominio.</summary>
    public string? Registrar { get; init; }

    /// <summary>Proxy in uscita, se previsto dall'installazione.</summary>
    public string? OutboundProxy { get; init; }

    /// <summary>Trasporto della segnalazione.</summary>
    public SipTransportKind Transport { get; init; } = SipTransportKind.Udp;

    /// <summary>Porta locale di ascolto. Zero per la selezione automatica.</summary>
    public int LocalPort { get; init; }

    /// <summary>Durata della registrazione in secondi.</summary>
    public int RegistrationExpiry { get; init; } = 300;

    /// <summary>
    /// Ordine di preferenza dei codec audio.
    /// </summary>
    /// <remarks>
    /// G.729 per primo perché è quello che i centralini di riferimento negoziano
    /// nella pratica. G.722 resta nell'elenco: la conformità a EN 301 549 §6.1
    /// richiede la <i>capacità</i> di banda larga, non che venga negoziata per
    /// prima, quindi va mantenuto anche se raramente scelto. PCMU non è presente:
    /// è la variante mu-law del G.711, quella nordamericana, e in Europa lo
    /// standard è A-law.
    /// </remarks>
    public IReadOnlyList<string> CodecPreference { get; init; } = ["G729", "G722", "PCMA"];

    /// <summary>Politica di cifratura del flusso audio.</summary>
    /// <remarks>
    /// Il predefinito è <see cref="SrtpPolicy.Disabled"/> e non è timidezza: fino a
    /// oggi questa proprietà era dichiarata e non applicata, quindi ogni postazione
    /// installata sta lavorando in chiaro. Accendere la cifratura da sola, a un
    /// aggiornamento, cambierebbe il modo in cui le chiamate si instaurano su
    /// installazioni funzionanti — e verso un centralino che SRTP non lo espone
    /// significherebbe chiamate che smettono di partire senza che nessuno abbia
    /// toccato nulla. Si accende dal pannello, e la si prova.
    /// </remarks>
    public SrtpPolicy Srtp { get; init; } = SrtpPolicy.Disabled;

    /// <summary>Registrar di riserva, usato in caso di fallimento del primario.</summary>
    public string? BackupRegistrar { get; init; }

    /// <summary>
    /// Server STUN con cui scoprire l'indirizzo pubblico del flusso audio, come lo
    /// scrive chi configura: <c>stun.ente.it</c>, <c>stun.ente.it:3478</c>,
    /// <c>stun:stun.ente.it</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vuoto, che è il predefinito, significa nessuna interrogazione e nessun pacchetto
    /// verso server esterni: è una decisione dell'ente, non del programma. Vedi
    /// <see cref="StunServerAddress"/>.
    /// </para>
    /// <para>
    /// Anche configurato, l'indirizzo pubblico finisce nella descrizione di sessione
    /// solo quando la postazione è davvero dietro NAT rispetto al centralino. Vedi
    /// <see cref="BlindPhone.Core.Services.MediaAddressPolicy"/>.
    /// </para>
    /// </remarks>
    public string? StunServer { get; init; }

    /// <summary>
    /// Ogni quanti secondi mandare il segnale che tiene aperto il passaggio nel router;
    /// zero lo spegne.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Senza, da casa le chiamate entranti smettono di arrivare</b> qualche minuto
    /// dopo l'ultima telefonata. Il router domestico ricorda il passaggio aperto dalla
    /// registrazione per un tempo che su UDP va da mezzo minuto a pochi minuti, mentre la
    /// registrazione si rinnova ogni cinque: nel mezzo il centralino manda l'INVITE a una
    /// porta che il router ha già dimenticato. La postazione risulta registrata, e non
    /// squilla.
    /// </para>
    /// <para>
    /// Venticinque secondi stanno sotto il tempo più breve che i router domestici usano.
    /// Il segnale è un doppio a capo: quello che la RFC 5626 prevede su TCP e TLS, e che
    /// i softphone usano da sempre anche su UDP perché ogni centralino lo ignora. Sono
    /// quattro byte ogni venticinque secondi. Vale per tutti e tre i trasporti, perché
    /// anche una connessione TCP o TLS ferma viene chiusa dai router e dai concentratori
    /// VPN, solo più tardi.
    /// </para>
    /// </remarks>
    public int KeepAliveSeconds { get; init; } = DefaultKeepAliveSeconds;

    /// <summary>Intervallo predefinito del segnale di mantenimento, in secondi.</summary>
    public const int DefaultKeepAliveSeconds = 25;

    /// <summary>Intervallo più breve accettato: sotto, il segnale non serve a niente di più.</summary>
    public const int MinKeepAliveSeconds = 10;

    /// <summary>Intervallo più lungo accettato: sopra, quasi ogni router ha già chiuso il passaggio.</summary>
    public const int MaxKeepAliveSeconds = 300;

    /// <summary>
    /// L'intervallo che il motore usa davvero, riportato nei limiti.
    /// </summary>
    /// <remarks>
    /// Il pannello rifiuta i valori fuori dai limiti, ma la configurazione può arrivare
    /// anche da un file scritto a mano o da un pacchetto distribuito: un intervallo di un
    /// secondo non deve diventare un pacchetto al secondo verso il centralino da ogni
    /// postazione dell'ente.
    /// </remarks>
    public TimeSpan KeepAliveInterval => KeepAliveSeconds <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(Math.Clamp(KeepAliveSeconds, MinKeepAliveSeconds, MaxKeepAliveSeconds));

    /// <summary>Verifica che i parametri obbligatori siano valorizzati.</summary>
    /// <param name="error">Messaggio in linguaggio naturale, pronunciabile.</param>
    /// <returns><c>true</c> se la configurazione è utilizzabile.</returns>
    public bool Validate(out string error)
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            error = "Interno SIP non configurato";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Domain))
        {
            error = "Dominio SIP non configurato";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Server a cui inviare la registrazione.</summary>
    public string EffectiveRegistrar => string.IsNullOrWhiteSpace(Registrar) ? Domain : Registrar;

    /// <summary>
    /// Parametro di trasporto da appendere agli indirizzi SIP di questo account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Vale per ogni richiesta, non solo per le chiamate.</b> Sta qui e non nel
    /// motore telefonico perché il motore non è l'unico a comporre indirizzi: anche la
    /// sorveglianza dello stato degli interni ne costruisce uno per ciascun interno
    /// sorvegliato, e quando la regola viveva in un posto solo l'altro se n'è
    /// dimenticato.
    /// </para>
    /// <para>
    /// La conseguenza si è vista in esercizio, e non era piccola: le sottoscrizioni
    /// partivano in chiaro su UDP mentre l'account era registrato in TLS, quindi da un
    /// canale che il centralino non riconosceva come autenticato. Fra il 10 e l'11
    /// agosto 2026 il registro conta <b>1.589 sottoscrizioni rifiutate</b> —
    /// 803 con <c>404 Not Found</c> e 786 con <c>400 Bad Request</c> — a fronte di 57
    /// accettate. Lo stato degli interni non compariva quasi mai, e il centralino
    /// riceveva un tentativo ogni pochi secondi.
    /// </para>
    /// <para>
    /// Si usa <c>sip:</c> con <c>transport=tls</c> e non lo schema <c>sips:</c>: il
    /// secondo imporrebbe TLS su tutto il percorso, compresi i tratti oltre il
    /// centralino, e diversi apparati lo rifiutano per questo.
    /// </para>
    /// </remarks>
    public string TransportParameter => Transport switch
    {
        SipTransportKind.Tls => ";transport=tls",
        SipTransportKind.Tcp => ";transport=tcp",
        _ => string.Empty,
    };
}
