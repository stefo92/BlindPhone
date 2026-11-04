using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Speech;

/// <summary>
/// Le frasi della messaggistica istantanea: annunci, voci degli elenchi, esiti.
/// </summary>
/// <remarks>
/// <para>
/// Stanno nel core per la stessa ragione degli altri testi parlati: si collaudano
/// senza interfaccia, e la stessa frase la dicono la voce, il braille e la riga
/// dell'elenco, che quindi non possono divergere.
/// </para>
/// <para>
/// <b>Prima chi, poi che cosa.</b> Ogni frase di un messaggio comincia dal mittente:
/// è l'informazione che decide se interrompersi per leggerlo, e chi ascolta la deve
/// avere prima delle parole del messaggio, non in coda.
/// </para>
/// </remarks>
public static class InstantMessageTexts
{
    /// <summary>
    /// Oltre questa lunghezza l'annuncio di un messaggio si tronca.
    /// </summary>
    /// <remarks>
    /// Un messaggio lungo letto per intero da una voce che non si può fermare senza
    /// perdere anche gli annunci che seguono è un muro. Trecento caratteri sono tre o
    /// quattro frasi; il resto è nella finestra della conversazione, dove si legge con il
    /// cursore e alla velocità che si vuole.
    /// </remarks>
    public const int MaxAnnouncedLength = 300;

    /// <summary>Destinazione mancante.</summary>
    public const string NoDestination = "manca il destinatario";

    /// <summary>Nessuna risposta entro il tempo concesso.</summary>
    public const string NoAnswer = "nessuna risposta dal centralino";

    /// <summary>Il messaggio non è nemmeno partito: rete o trasporto non disponibili.</summary>
    public const string NotSent = "il messaggio non è partito";

    /// <summary>Il motore in uso non sa mandare messaggi.</summary>
    public const string NotSupported = "la postazione non è collegata al centralino";

    /// <summary>Testo vuoto: non si manda niente.</summary>
    public const string EmptyMessage = "Scrivi il messaggio prima di inviarlo";

    /// <summary>Conferma di una consegna riuscita.</summary>
    public const string Delivered = "Messaggio consegnato";

    /// <summary>Nessuna conversazione ancora aperta, nell'elenco dei messaggi.</summary>
    public const string NoConversations = "Nessuna conversazione. Scrivi un interno e premi Invio per cominciarne una.";

    /// <summary>
    /// Chi ha scritto, nella forma da pronunciare: il nome se c'è, altrimenti il numero
    /// cifra per cifra.
    /// </summary>
    /// <param name="name">Nome del collega, se noto.</param>
    /// <param name="number">Interno o numero.</param>
    /// <param name="grouping">Raggruppamento delle cifre scelto dall'operatore.</param>
    public static string Who(string? name, string number, DigitGrouping grouping = DigitGrouping.Single)
        => SpeechFormatter.Party(name, number, grouping);

    /// <summary>
    /// Chi ha scritto, nella forma da leggere a schermo: «Mario Rossi (201)», oppure solo
    /// «201».
    /// </summary>
    /// <param name="name">Nome del collega, se noto.</param>
    /// <param name="number">Interno o numero.</param>
    public static string WhoShown(string? name, string number)
    {
        var nome = CallParty.MeaningfulNameOf(name, number);
        return nome is null ? number : $"{nome} ({number})";
    }

    /// <summary>
    /// Annuncio di un messaggio arrivato mentre l'operatore guarda altrove.
    /// </summary>
    /// <param name="name">Nome del mittente, se noto.</param>
    /// <param name="number">Interno o numero del mittente.</param>
    /// <param name="text">Testo del messaggio.</param>
    /// <param name="grouping">Raggruppamento delle cifre.</param>
    public static string Incoming(string? name, string number, string text, DigitGrouping grouping = DigitGrouping.Single)
        => $"Messaggio da {Who(name, number, grouping)}: {Shorten(text)}";

    /// <summary>
    /// Annuncio di un messaggio arrivato nella conversazione che l'operatore ha davanti.
    /// </summary>
    /// <param name="name">Nome del mittente, se noto.</param>
    /// <param name="number">Interno o numero del mittente.</param>
    /// <param name="text">Testo del messaggio.</param>
    /// <param name="grouping">Raggruppamento delle cifre.</param>
    /// <remarks>
    /// Senza «messaggio da»: la finestra dice già con chi si sta parlando, e le due
    /// parole in più a ogni battuta sono la cosa che rende lenta una conversazione
    /// ascoltata.
    /// </remarks>
    public static string IncomingInChat(string? name, string number, string text, DigitGrouping grouping = DigitGrouping.Single)
        => $"{Who(name, number, grouping)}: {Shorten(text)}";

    /// <summary>Riga braille di un messaggio arrivato.</summary>
    /// <param name="name">Nome del mittente, se noto.</param>
    /// <param name="number">Interno o numero del mittente.</param>
    /// <param name="text">Testo del messaggio.</param>
    public static string IncomingBraille(string? name, string number, string text)
        => $"msg {CallParty.MeaningfulNameOf(name, number) ?? number}: {text}";

    /// <summary>Annuncio di un messaggio non consegnato.</summary>
    /// <param name="reason">Motivo, già tradotto.</param>
    public static string NotDelivered(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "Messaggio non consegnato" : $"Messaggio non consegnato: {reason}";

    /// <summary>
    /// Traduce il codice di risposta a un <c>MESSAGE</c> nel motivo da dire all'operatore.
    /// </summary>
    /// <param name="statusCode">Codice SIP della risposta definitiva.</param>
    /// <remarks>
    /// <para>
    /// <b>Il caso che conta di più è il 405 e il 501</b>: il centralino non inoltra
    /// messaggi. Succede su impianti configurati solo per la voce, ed è l'unica risposta
    /// che dice all'operatore di smettere di provare con chiunque, non solo con quel
    /// collega.
    /// </para>
    /// <para>
    /// Il 480 è il telefono del collega spento o non registrato: lo stesso codice che
    /// una chiamata riceve quando l'interno non è raggiungibile, e la stessa parola che
    /// l'operatore sente in quel caso.
    /// </para>
    /// </remarks>
    public static string DeliveryFailure(int statusCode) => statusCode switch
    {
        401 or 407 => "il centralino ha rifiutato le credenziali",
        403 or 603 => "il centralino non lo consente",
        404 or 484 or 604 => "l'interno non esiste",
        405 or 501 => "il centralino non inoltra i messaggi",
        408 => NoAnswer,
        413 => "il messaggio è troppo lungo",
        415 or 488 or 606 => "il telefono del collega non accetta messaggi di testo",
        480 => "il collega non è raggiungibile",
        486 or 600 => "il collega non accetta messaggi in questo momento",
        >= 500 and < 600 => $"errore del centralino, codice {statusCode}",
        _ => $"rifiutato dal centralino, codice {statusCode}",
    };

    /// <summary>
    /// Voce dell'elenco dei messaggi di una conversazione, per lo screen reader.
    /// </summary>
    /// <param name="message">Messaggio.</param>
    /// <param name="peerName">Nome del collega, se noto.</param>
    /// <param name="grouping">Raggruppamento delle cifre.</param>
    /// <remarks>
    /// L'ora viene dopo il testo: chi scorre una conversazione vuole sapere chi ha
    /// detto che cosa, e l'ora la ascolta solo se resta in attesa. Lo stato della
    /// consegna viene in fondo, e c'è solo quando non è quello normale.
    /// </remarks>
    public static string Accessible(InstantMessage message, string? peerName, DigitGrouping grouping = DigitGrouping.Single)
    {
        var chi = message.IsOutgoing ? "Tu" : Who(peerName, message.Peer, grouping);
        var riga = $"{chi}: {message.Text}, alle {message.At:HH:mm}";

        return message.Delivery switch
        {
            InstantMessageDelivery.Sending => $"{riga}, invio in corso",
            InstantMessageDelivery.Failed => $"{riga}, {NotDelivered(message.FailureReason).ToLowerInvariant()}",
            _ => riga,
        };
    }

    /// <summary>Stato della consegna, per la riga a schermo; vuoto se è quello normale.</summary>
    /// <param name="message">Messaggio.</param>
    public static string DeliveryShown(InstantMessage message) => message.Delivery switch
    {
        InstantMessageDelivery.Sending => "invio in corso",
        InstantMessageDelivery.Failed => NotDelivered(message.FailureReason),
        _ => string.Empty,
    };

    /// <summary>
    /// Voce dell'elenco delle conversazioni: chi, quanti da leggere, l'ultimo messaggio.
    /// </summary>
    /// <param name="name">Nome del collega, se noto.</param>
    /// <param name="number">Interno o numero.</param>
    /// <param name="unread">Messaggi arrivati e non ancora visti.</param>
    /// <param name="last">Ultimo messaggio, se la conversazione ne ha.</param>
    /// <param name="grouping">Raggruppamento delle cifre.</param>
    /// <remarks>
    /// I messaggi da leggere vengono subito dopo il nome: è la ragione per cui si scorre
    /// questo elenco, e sentirla dopo l'ultimo messaggio per intero vorrebbe dire
    /// ascoltare ogni riga fino in fondo per sapere se fermarsi.
    /// </remarks>
    public static string ConversationAccessible(
        string? name,
        string number,
        int unread,
        InstantMessage? last,
        DigitGrouping grouping = DigitGrouping.Single)
    {
        var parti = new List<string>(3) { Who(name, number, grouping) };

        if (unread > 0) parti.Add(Unread(unread));

        if (last is not null)
        {
            var testo = Shorten(last.Text, 80, "…");
            parti.Add(last.IsOutgoing ? $"ultimo tuo: {testo}" : $"ultimo: {testo}");
        }

        return string.Join(", ", parti);
    }

    /// <summary>Messaggi da leggere, in parole: «un messaggio da leggere», «3 messaggi da leggere».</summary>
    /// <param name="count">Quanti.</param>
    public static string Unread(int count) => count switch
    {
        <= 0 => "nessun messaggio da leggere",
        1 => "un messaggio da leggere",
        _ => $"{count} messaggi da leggere",
    };

    /// <summary>Titolo della finestra di una conversazione.</summary>
    /// <param name="name">Nome del collega, se noto.</param>
    /// <param name="number">Interno o numero.</param>
    /// <remarks>
    /// Il titolo è la prima cosa che lo screen reader legge passando da una finestra
    /// all'altra con ALT+TAB, e con più conversazioni aperte è l'unico modo di sapere
    /// in quale si è arrivati.
    /// </remarks>
    public static string ChatTitle(string? name, string number) => $"Messaggi con {WhoShown(name, number)}";

    /// <summary>Accorcia un testo per la lettura, senza spezzare l'ultima parola.</summary>
    /// <param name="text">Testo da accorciare.</param>
    /// <param name="max">Lunghezza massima.</param>
    /// <param name="tail">
    /// Cosa dire al posto della parte tolta. Negli annunci dice dove trovare il resto;
    /// nelle voci di un elenco bastano i puntini, perché il resto è un Invio più in là.
    /// </param>
    public static string Shorten(string text, int max = MaxAnnouncedLength, string tail = "… continua nella finestra dei messaggi")
    {
        var unaRiga = text.ReplaceLineEndings(" ").Trim();
        if (unaRiga.Length <= max) return unaRiga;

        var taglio = unaRiga.LastIndexOf(' ', max);
        if (taglio < max / 2) taglio = max;

        return unaRiga[..taglio].TrimEnd() + tail;
    }
}
