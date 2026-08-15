namespace BlindPhone.Core.Domain;

/// <summary>Motivo per cui un certificato non è stato accettato automaticamente.</summary>
/// <remarks>
/// Sono i tre casi che <c>SslPolicyErrors</c> distingue, tradotti nel vocabolario
/// del prodotto. La distinzione conta perché le conseguenze sono diverse: un
/// certificato scaduto su un centralino interno è quasi sempre una dimenticanza
/// dell'amministratore, mentre un nome che non corrisponde è il sintomo che si sta
/// parlando con un'altra macchina.
/// </remarks>
[Flags]
public enum CertificateProblem
{
    /// <summary>Nessun problema: il certificato è valido e verificato.</summary>
    None = 0,

    /// <summary>La catena di firma non risale a un'autorità riconosciuta dal sistema.</summary>
    UntrustedAuthority = 1,

    /// <summary>Il nome nel certificato non corrisponde a quello del server contattato.</summary>
    NameMismatch = 2,

    /// <summary>Il server non ha presentato alcun certificato.</summary>
    NotAvailable = 4,
}

/// <summary>
/// Certificato presentato da un server, ridotto a ciò che serve per decidere.
/// </summary>
/// <param name="Host">Server che lo ha presentato, come l'operatore lo ha configurato.</param>
/// <param name="Purpose">A cosa serve il collegamento: «centralino» o «telefono».</param>
/// <param name="Subject">Intestatario.</param>
/// <param name="Issuer">Autorità emittente.</param>
/// <param name="Thumbprint">Impronta SHA-1, in maiuscolo e senza separatori.</param>
/// <param name="NotBefore">Inizio validità.</param>
/// <param name="NotAfter">Fine validità.</param>
/// <param name="Problems">Motivi per cui non è stato accettato automaticamente.</param>
/// <remarks>
/// <para>
/// È un record del dominio e non un <c>X509Certificate2</c> per una ragione
/// pratica: la decisione su cosa dire all'operatore e su cosa ricordare è la parte
/// che vale la pena collaudare, e collaudarla non deve richiedere di fabbricare
/// certificati veri.
/// </para>
/// </remarks>
public sealed record CertificateDescription(
    string Host,
    string Purpose,
    string Subject,
    string Issuer,
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    CertificateProblem Problems)
{
    /// <summary>Indica se il certificato è scaduto o non ancora valido.</summary>
    /// <param name="now">Istante corrente.</param>
    public bool IsOutsideValidity(DateTimeOffset now) => now < NotBefore || now > NotAfter;

    /// <summary>
    /// Chiave con cui il certificato viene ricordato: impronta più server.
    /// </summary>
    /// <remarks>
    /// <b>L'impronta da sola non basta.</b> Fidarsi di un certificato significa
    /// fidarsi di quel certificato <i>su quel server</i>: legare la decisione alla sola
    /// impronta la renderebbe valida ovunque quel certificato ricompaia, che è
    /// esattamente lo scenario in cui un certificato legittimamente ottenuto per una
    /// macchina viene riusato per un'altra.
    /// </remarks>
    public string TrustKey => $"{Host.Trim().ToLowerInvariant()}|{Thumbprint}";
}

/// <summary>Risposta dell'operatore davanti a un certificato non verificabile.</summary>
public enum CertificateTrustDecision
{
    /// <summary>Non fidarsi: il collegamento non viene stabilito.</summary>
    Reject = 0,

    /// <summary>
    /// Fidarsi per questa sessione soltanto: alla riapertura di BlindPhone la
    /// domanda torna.
    /// </summary>
    /// <remarks>
    /// È la scelta giusta quando si sta provando una configurazione: consente di
    /// andare avanti senza lasciare dietro una decisione che nessuno ricorda di aver
    /// preso.
    /// </remarks>
    TrustForThisSession = 1,

    /// <summary>Fidarsi sempre: l'impronta viene ricordata su questa postazione.</summary>
    TrustAlways = 2,
}

/// <summary>Esito della valutazione di un certificato.</summary>
/// <param name="Accepted">Se il collegamento può proseguire.</param>
/// <param name="Reason">Motivo, in linguaggio naturale, per il registro e per l'annuncio.</param>
/// <param name="AskedOperator">Se la decisione ha richiesto una domanda all'operatore.</param>
public sealed record CertificateVerdict(bool Accepted, string Reason, bool AskedOperator = false);
