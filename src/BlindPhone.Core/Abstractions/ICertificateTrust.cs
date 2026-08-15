using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Abstractions;

/// <summary>
/// Chiede all'operatore se fidarsi di un certificato che il sistema non è riuscito
/// a verificare.
/// </summary>
/// <remarks>
/// <para>
/// Sta dietro un'interfaccia per due motivi. Il primo è il solito: la decisione va
/// collaudata senza aprire finestre. Il secondo è più specifico — la domanda arriva
/// su un thread qualsiasi, quello che sta stabilendo il collegamento, e chi la
/// implementa deve occuparsi di portarla sull'interfaccia e di riportare indietro
/// la risposta. Quel dettaglio non deve entrare nella logica di fiducia.
/// </para>
/// <para>
/// <b>Chi implementa deve rispondere sempre.</b> Un'implementazione che si blocca
/// in attesa di una risposta che non arriva tiene fermo il collegamento al
/// centralino senza che nulla lo dica: davanti a un dubbio va restituito
/// <see cref="CertificateTrustDecision.Reject"/>, che è la risposta prudente e
/// che comunque produce un messaggio.
/// </para>
/// </remarks>
public interface ICertificateTrustPrompt
{
    /// <summary>Chiede all'operatore cosa fare.</summary>
    /// <param name="certificate">Certificato presentato dal server.</param>
    /// <returns>La decisione presa.</returns>
    CertificateTrustDecision Ask(CertificateDescription certificate);
}

/// <summary>
/// Ricorda le impronte dei certificati che l'operatore ha deciso di accettare.
/// </summary>
/// <remarks>
/// <para>
/// Le decisioni <b>non</b> vivono in <c>settings.json</c>, ed è deliberato: quel
/// file è documentato come esportabile e condivisibile fra postazioni, e una
/// decisione di fiducia che viaggia insieme alla configurazione verrebbe applicata
/// su macchine il cui operatore non l'ha mai presa. In un collaudo di sicurezza
/// presso una PA è esattamente il tipo di rilievo che si prende.
/// </para>
/// </remarks>
public interface ICertificateTrustStore
{
    /// <summary>Indica se quell'impronta è già stata accettata per quel server.</summary>
    /// <param name="trustKey">Chiave restituita da <see cref="CertificateDescription.TrustKey"/>.</param>
    bool IsTrusted(string trustKey);

    /// <summary>Ricorda una decisione di fiducia permanente.</summary>
    /// <param name="certificate">Certificato accettato.</param>
    void Trust(CertificateDescription certificate);

    /// <summary>Dimentica una decisione, quando l'operatore la revoca.</summary>
    /// <param name="trustKey">Chiave della decisione da revocare.</param>
    void Revoke(string trustKey);

    /// <summary>Certificati attualmente accettati, per il pannello e la diagnostica.</summary>
    /// <remarks>
    /// Una decisione presa mesi prima e dimenticata è indistinguibile da un
    /// collegamento verificato: va potuta rileggere, ed è il motivo per cui l'elenco
    /// è esposto invece di restare un dettaglio del file.
    /// </remarks>
    IReadOnlyList<CertificateDescription> Trusted { get; }
}
