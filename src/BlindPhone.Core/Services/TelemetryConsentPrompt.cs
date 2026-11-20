namespace BlindPhone.Core.Services;

/// <summary>
/// La domanda sulle statistiche d'uso che BlindPhone fa all'operatore al primo avvio:
/// quando farla, e che cosa dire.
/// </summary>
/// <remarks>
/// <para>
/// <b>La domanda è un comando, non la base giuridica dell'ente.</b> Vale quello che
/// dice <c>CONFORMITA-PA-ITALIANA.md</c>, §7.2: un ente non fonda un trattamento che
/// riguarda i propri dipendenti sul loro consenso. La domanda serve a due cose: che
/// niente parta senza una scelta esplicita di chi siede davanti alla postazione, e che
/// la scelta si faccia sapendo che cosa parte e verso chi. Con il server dell'autore è
/// la scelta dell'operatore verso l'autore, non verso il datore di lavoro.
/// </para>
/// <para>
/// <b>L'ente decide per primo, se vuole.</b> Una scheda Diagnostica bloccata — dal
/// pacchetto di configurazione o dal criterio di postazione — vuol dire che la
/// decisione è dell'ente, e la domanda non si fa. Una scheda lasciata libera vuol dire
/// che l'ente ha lasciato la scelta agli operatori, ed è il caso già previsto dal
/// §7.2.
/// </para>
/// <para>
/// <b>Né sì né no sono preselezionati.</b> All'apertura il fuoco sta nel testo e
/// nessun pulsante è quello predefinito: un Invio dato per abitudine non sceglie
/// niente. Dire no costa quanto dire sì — Alt N contro Alt S — ed Esc vale come no,
/// perché chiudere senza rispondere non è un consenso.
/// </para>
/// <para>
/// <b>Che cosa parte non è scritto qui</b>: lo legge dal catalogo, dalla frase che
/// accompagna ogni evento, come la guida in linea. La domanda, la guida e il programma
/// non possono dire cose diverse.
/// </para>
/// </remarks>
public static class TelemetryConsentPrompt
{
    /// <summary>Nome del server dell'autore di BlindPhone.</summary>
    /// <remarks>
    /// È il ponte che riceve le statistiche e le passa a PostHog Cloud nella regione UE.
    /// La domanda nomina l'autore come destinatario <b>solo</b> quando il server è
    /// questo: una copia compilata o configurata con un altro server dice soltanto il
    /// nome di quel server, e che l'informativa la dà chi lo gestisce.
    /// </remarks>
    public const string AuthorServerHost = "telemetry.curti.cloud";

    /// <summary>Titolo della finestra: è la prima cosa che lo screen reader legge.</summary>
    public const string Title = "BlindPhone - statistiche d'uso anonime";

    /// <summary>La domanda, in una frase.</summary>
    public const string Question = "Vuoi inviare statistiche d'uso anonime a chi sviluppa BlindPhone?";

    /// <summary>Indica se all'avvio va fatta la domanda.</summary>
    /// <param name="alreadyAsked">Se la domanda è già stata fatta su questa postazione, a questo operatore.</param>
    /// <param name="enabled">Se le statistiche sono già accese.</param>
    /// <param name="configured">Se indirizzo e chiave del server sono validi.</param>
    /// <param name="diagnosticsEditable">Se la scheda Diagnostica si può modificare, cioè se l'ente non l'ha bloccata.</param>
    /// <returns><c>true</c> solo se servono tutte e quattro le condizioni.</returns>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>
    ///   <b>Una volta sola</b>: chi ha già risposto, anche con Esc o dalla scheda
    ///   Diagnostica, non la sente più.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Non se sono già accese</b>: le ha accese l'operatore o l'ente, e chiedere
    ///   di nuovo vorrebbe dire rimettere in discussione una scelta già fatta.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Non senza un server valido</b>: un sì non manderebbe niente, e una domanda
    ///   senza effetto è una promessa falsa.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Non se l'ente ha bloccato la Diagnostica</b>: la decisione è sua, e la
    ///   casella non si potrebbe nemmeno cambiare.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static bool ShouldAsk(bool alreadyAsked, bool enabled, bool configured, bool diagnosticsEditable)
        => !alreadyAsked && !enabled && configured && diagnosticsEditable;

    /// <summary>
    /// Il testo della finestra, sotto la domanda: perché, che cosa parte, che cosa no,
    /// verso chi, come si cambia idea, quali tasti.
    /// </summary>
    /// <param name="serverHost">Nome del server a cui andrebbero le statistiche.</param>
    /// <param name="authorServer">Se quel server è quello dell'autore: vedi <see cref="AuthorServerHost"/>.</param>
    /// <returns>Un paragrafo per riga, con le voci dell'elenco che cominciano con un trattino.</returns>
    public static string Text(string serverHost, bool authorServer)
    {
        var destinatario = authorServer
            ? $"Dove vanno. A {serverHost}, il server di {HelpBook.AuthorName}, autore di BlindPhone, che le passa a PostHog Cloud, un servizio di statistiche, nella sua regione europea, senza l'indirizzo di rete della postazione. Le usa l'autore, per decidere che cosa migliorare. L'informativa completa, con il modo di chiedere che cosa c'è e di farlo cancellare, è nella guida in linea, argomento «Le statistiche d'uso anonime»."
            : $"Dove vanno. A {serverHost}, il server indicato da chi ha configurato questa postazione. Le riceve chi gestisce quel server, ed è lui che deve darti l'informativa.";

        return $"""
            Servono a sapere quante postazioni usano BlindPhone e quali funzioni servono davvero. Adesso sono spente, e partono solo se rispondi sì.

            Se rispondi sì, partono queste cose, e nessun'altra.

            {string.Join("\n", TelemetryCatalog.All.Select(evento => "- " + evento.Description))}

            Non partono mai numeri di telefono, nomi, interni, indirizzi SIP, il contenuto della rubrica, il nome del centralino, il testo degli errori. E nemmeno quante chiamate fai o a che ora: ogni cosa porta il giorno e non l'ora.

            {destinatario}

            Puoi cambiare idea quando vuoi: Impostazioni, scheda Diagnostica, casella «Invia statistiche d'uso anonime», Alt T. Spegnendole, la postazione dimentica il proprio identificativo e quello che non era ancora partito.

            Alt S risponde sì, Alt N risponde no. Esc vale come no. Questa domanda non verrà ripetuta.
            """;
    }

    /// <summary>Quello che lo screen reader legge all'apertura: titolo, domanda e testo.</summary>
    /// <param name="serverHost">Nome del server.</param>
    /// <param name="authorServer">Se è il server dell'autore.</param>
    public static string Spoken(string serverHost, bool authorServer)
        => $"{Title}. {Question}\n\n{Text(serverHost, authorServer)}";

    /// <summary>Quello che BlindPhone dice dopo la risposta.</summary>
    /// <param name="accepted">Se l'operatore ha risposto sì.</param>
    /// <remarks>
    /// Ogni azione conferma il proprio esito, e questa più delle altre: chi ha premuto
    /// Esc per sbrigarsi deve sapere che cosa ha scelto, e dove si cambia.
    /// </remarks>
    public static string Confirmation(bool accepted) => accepted
        ? "Statistiche d'uso accese. Puoi spegnerle quando vuoi da Impostazioni, scheda Diagnostica, Alt T."
        : "Statistiche d'uso spente. Puoi accenderle quando vuoi da Impostazioni, scheda Diagnostica, Alt T.";
}
