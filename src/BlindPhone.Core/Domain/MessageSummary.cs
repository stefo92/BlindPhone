namespace BlindPhone.Core.Domain;

/// <summary>
/// Stato della segreteria telefonica di un account, come lo riporta il centralino.
/// </summary>
/// <param name="MessagesWaiting">
/// Se il centralino dichiara che ci sono messaggi da ascoltare: è la riga
/// <c>Messages-Waiting</c> del corpo, quella con cui i telefoni da tavolo accendono la
/// spia.
/// </param>
/// <param name="MessageAccount">
/// Indirizzo della casella, se il centralino lo indica: per esempio
/// <c>sip:*97@centralino.ente.it</c>. Su molti impianti è anche il numero da chiamare
/// per ascoltare i messaggi, ed è per questo che viene proposto nelle impostazioni.
/// </param>
/// <param name="NewMessages">Messaggi vocali nuovi, se il centralino li conta.</param>
/// <param name="OldMessages">Messaggi vocali già ascoltati, se il centralino li conta.</param>
/// <param name="UrgentNewMessages">Quanti dei nuovi sono urgenti, se il centralino lo dice.</param>
/// <param name="UrgentOldMessages">Quanti dei già ascoltati sono urgenti, se il centralino lo dice.</param>
/// <remarks>
/// <para>
/// <b>Ogni conteggio può mancare, e mancare non vuol dire zero.</b> L'RFC 3842 rende
/// obbligatoria la sola riga <c>Messages-Waiting</c>: un centralino che accende la spia
/// senza dire quanti messaggi ci sono rispetta la norma, ed è un caso frequente. Per
/// questo i conteggi sono annullabili, e chi li legge deve distinguere «nessun
/// messaggio» da «non si sa quanti».
/// </para>
/// <para>
/// I conteggi riguardano i soli messaggi <b>vocali</b>: il corpo ne può portare anche di
/// fax o di testo, che su un posto operatore non hanno nulla da dire.
/// </para>
/// </remarks>
public sealed record MessageSummary(
    bool MessagesWaiting,
    string? MessageAccount,
    int? NewMessages,
    int? OldMessages,
    int? UrgentNewMessages,
    int? UrgentOldMessages)
{
    /// <summary>
    /// Messaggi nuovi da annunciare: zero se non ce ne sono, <c>null</c> se ci sono ma
    /// il centralino non dice quanti.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>La spia comanda sui numeri.</b> <c>Messages-Waiting: no</c> vale zero anche se
    /// la riga dei conteggi dice altro: è la riga che il centralino usa per accendere e
    /// spegnere la spia dei telefoni da tavolo, quindi è quella che il resto
    /// dell'ufficio vede, e annunciare messaggi che la spia nega farebbe cercare
    /// all'operatore qualcosa che non c'è.
    /// </para>
    /// <para>
    /// Al contrario, <c>Messages-Waiting: yes</c> con zero messaggi vocali nuovi non è
    /// una contraddizione: la spia riguarda tutte le classi di messaggi, e può essere
    /// accesa da un fax. Si dice che ci sono novità, senza inventare un numero.
    /// </para>
    /// </remarks>
    public int? NewToAnnounce => !MessagesWaiting
        ? 0
        : NewMessages is > 0 ? NewMessages : null;

    /// <summary>Quanti dei messaggi nuovi sono urgenti, zero se non si sa.</summary>
    public int UrgentToAnnounce => MessagesWaiting && UrgentNewMessages is > 0 && NewToAnnounce is { } nuovi
        ? Math.Min(UrgentNewMessages.Value, nuovi)
        : 0;
}
