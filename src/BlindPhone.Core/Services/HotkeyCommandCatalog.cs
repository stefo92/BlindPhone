using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Descrizione di tutti i comandi assegnabili a una scorciatoia.
/// </summary>
/// <remarks>
/// <para>
/// È la sorgente unica di verità del pannello dei tasti rapidi: l'elenco che
/// l'operatore percorre, le etichette pronunciate negli avvisi di conflitto e le
/// righe della scheda dei tasti vengono tutte da qui. Prima queste tre cose vivevano
/// in tre punti diversi — l'enumerazione, un <c>switch</c> nella finestra principale
/// e un documento scritto a mano — e nessuno dei tre sapeva degli altri: aggiungere
/// un comando significava dimenticarne almeno uno.
/// </para>
/// <para>
/// La descrizione estesa non è un doppione dell'etichetta. L'etichetta identifica il
/// comando in una lista letta a voce e deve stare in tre parole; la descrizione dice
/// <b>quando</b> serve, ed è quella che permette a un operatore appena assunto di
/// decidere se vale la pena assegnargli un tasto comodo.
/// </para>
/// <para>
/// Il test <c>HotkeyCatalogCoverageTests</c> verifica che ogni voce
/// dell'enumerazione compaia qui. Un comando senza descrizione comparirebbe nel
/// pannello con il suo nome inglese di codice, che per chi ascolta non significa
/// nulla.
/// </para>
/// </remarks>
public static class HotkeyCommandCatalog
{
    private static readonly IReadOnlyList<HotkeyCommandInfo> Entries =
    [
        // ------------------------------------------------------------------
        // Gestione della chiamata
        // ------------------------------------------------------------------
        new(HotkeyCommand.Answer, HotkeyCategory.CallControl, "Rispondi",
            "Risponde alla chiamata che sta squillando. Se ce n'e' piu' di una, risponde alla prima arrivata."),

        new(HotkeyCommand.HangUp, HotkeyCategory.CallControl, "Riaggancia o rifiuta",
            "Chiude la conversazione in corso. Se il telefono sta squillando, rifiuta la chiamata. "
            + "Durante una consultazione di trasferimento chiude la linea con l'interno e riprende il chiamante."),

        new(HotkeyCommand.ToggleHold, HotkeyCategory.CallControl, "Attesa o riprendi",
            "Mette in attesa l'interlocutore, oppure lo riprende se e' gia' in attesa."),

        new(HotkeyCommand.Swap, HotkeyCategory.CallControl, "Alterna fra le due chiamate",
            "Passa dall'interlocutore in linea a quello in attesa e viceversa."),

        new(HotkeyCommand.Conference, HotkeyCategory.CallControl, "Conferenza a tre",
            "Unisce nella stessa conversazione l'interlocutore in linea e quello in attesa."),

        new(HotkeyCommand.Transfer, HotkeyCategory.CallControl, "Trasferisci",
            "Tasto unico del trasferimento: avvia la consultazione, la completa, e con due pressioni "
            + "rapide trasferisce alla cieca. E' il comando piu' usato di un posto operatore."),

        new(HotkeyCommand.BargeIn, HotkeyCategory.CallControl, "Inclusione su interno occupato",
            "Entra nella conversazione dell'interno occupato a cui stai passando la chiamata, per "
            + "segnalargli che ce n'e' una urgente. Tutti sentono un segnale periodico che dichiara "
            + "la tua presenza, e non si puo' spegnere."),

        new(HotkeyCommand.ToggleMute, HotkeyCategory.CallControl, "Microfono muto o attivo",
            "Silenzia il microfono senza chiudere la chiamata. L'interlocutore resta in linea e non ti sente."),

        new(HotkeyCommand.ToggleCallRecording, HotkeyCategory.CallControl, "Registra la chiamata",
            "Avvia o interrompe la registrazione della conversazione. Il file finisce nella cartella "
            + "Documenti, sottocartella Registrazione Chiamate. All'avvio e alla fine l'interlocutore sente "
            + "un avviso vocale, che si puo' disattivare da Impostazioni, scheda Chiamate."),

        new(HotkeyCommand.RedialLast, HotkeyCategory.CallControl, "Richiama l'ultimo numero",
            "Ricompone l'ultimo numero chiamato."),

        new(HotkeyCommand.ToggleDtmfMode, HotkeyCategory.CallControl, "Modalita' toni",
            "Attiva o disattiva l'invio dei toni: finche' e' attiva, le cifre che digiti vanno in linea per "
            + "rispondere ai menu automatici invece di finire nel campo di composizione. Si spegne da sola "
            + "quando la chiamata finisce."),

        new(HotkeyCommand.SendDialerAsDtmf, HotkeyCategory.CallControl, "Invia il numero digitato come toni",
            "Manda in linea, uno dopo l'altro, i toni corrispondenti a quello che hai scritto nel campo di "
            + "composizione. Serve a preparare la sequenza con calma e mandarla in un gesto solo."),

        // ------------------------------------------------------------------
        // Navigazione
        // ------------------------------------------------------------------
        new(HotkeyCommand.FocusDialer, HotkeyCategory.Navigation, "Vai al campo di composizione",
            "Porta il cursore nel campo dove si digita il numero. Serve anche a scrivere un numero "
            + "che il chiamante sta dettando: le cifre non gli arrivano in linea."),

        new(HotkeyCommand.FocusQueue, HotkeyCategory.Navigation, "Vai all'elenco della coda",
            "Porta il cursore sull'elenco delle chiamate in attesa."),

        new(HotkeyCommand.OpenContacts, HotkeyCategory.Navigation, "Apri la rubrica",
            "Apre la rubrica con la ricerca gia' attiva. Il testo eventualmente digitato nel campo di "
            + "composizione viene portato dentro come ricerca iniziale."),

        new(HotkeyCommand.OpenCallHistory, HotkeyCategory.Navigation, "Apri le ultime chiamate",
            "Apre l'elenco delle chiamate ricevute ed effettuate."),

        new(HotkeyCommand.OpenSettings, HotkeyCategory.Navigation, "Apri le impostazioni",
            "Apre questo pannello di configurazione."),

        new(HotkeyCommand.OpenDiagnostics, HotkeyCategory.Navigation, "Apri la diagnostica",
            "Mostra lo stato del collegamento, la qualita' della linea e i contatori dell'audio. "
            + "Sono i dati da riferire all'assistenza."),

        new(HotkeyCommand.SendSupportPackage, HotkeyCategory.Navigation, "Segnala un problema all'assistenza",
            "Raccoglie in un solo file il riepilogo diagnostico, i registri recenti e gli errori registrati, "
            + "e lo manda al recapito dell'assistenza configurato. Ha un tasto proprio perche' serve quando "
            + "qualcosa non funziona, cioe' quando cercarlo dentro le impostazioni e' l'ultima cosa da fare."),

        new(HotkeyCommand.OpenSipAccounts, HotkeyCategory.Navigation, "Cambia account SIP",
            "Apre l'elenco degli account configurati e permette di passare da uno all'altro. Serve a chi copre "
            + "due enti o lavora su una postazione condivisa a turni. Non funziona con una chiamata in corso: "
            + "cambiare account fa cadere la conversazione."),

        new(HotkeyCommand.FocusActiveCall, HotkeyCategory.Navigation, "Torna alla chiamata attiva",
            "Riporta l'attenzione sulla conversazione in corso e ne annuncia lo stato."),

        new(HotkeyCommand.OpenHelp, HotkeyCategory.Navigation, "Apri la guida in linea",
            "Apre la guida, con la logica del posto operatore e l'elenco delle combinazioni assegnate in questo "
            + "momento. Si apre sull'argomento che riguarda il punto in cui ti trovi."),

        new(HotkeyCommand.ShowWindow, HotkeyCategory.Navigation, "Riapri la finestra di BlindPhone",
            "Riporta in primo piano la finestra riposta nell'area di notifica. Assegnala come combinazione "
            + "globale: quando la finestra e' nascosta, i comandi locali non arrivano."),

        new(HotkeyCommand.QuitApplication, HotkeyCategory.Navigation, "Esci da BlindPhone",
            "Chiude davvero il programma invece di riporlo nell'area di notifica. Con una chiamata in corso "
            + "viene chiesta conferma: uscire la farebbe cadere."),

        // ------------------------------------------------------------------
        // Informazione
        // ------------------------------------------------------------------
        new(HotkeyCommand.AnnounceQueueCount, HotkeyCategory.Information, "Quante chiamate in coda",
            "Annuncia il numero di chiamanti in attesa. Premuto due volte di seguito, elenca chi sono."),

        new(HotkeyCommand.AnnounceQueueList, HotkeyCategory.Information, "Elenco dei chiamanti in coda",
            "Legge l'elenco completo di chi e' in attesa, con il tempo di attesa di ciascuno."),

        new(HotkeyCommand.AnnounceStatusSummary, HotkeyCategory.Information, "Riepilogo dello stato",
            "Legge tutto in una volta: registrazione, chiamata attiva, chiamate in attesa, coda e stato agente. "
            + "E' il comando con cui si riprende il filo dopo essersi allontanati dalla postazione."),

        new(HotkeyCommand.AnnounceCallDuration, HotkeyCategory.Information, "Durata della chiamata",
            "Annuncia da quanto tempo e' in corso la conversazione."),

        new(HotkeyCommand.AnnounceLineQuality, HotkeyCategory.Information, "Qualita' della linea",
            "Annuncia la qualita' audio e il codec in uso. Serve quando l'interlocutore lamenta di sentire male."),

        new(HotkeyCommand.RepeatLastAnnouncement, HotkeyCategory.Information, "Ripeti l'ultimo annuncio",
            "Ripete l'ultima cosa detta dalla voce. Utile quando un rumore la copre."),

        new(HotkeyCommand.OpenAnnouncementHistory, HotkeyCategory.Information, "Storico degli annunci",
            "Apre l'elenco degli ultimi annunci, per rileggerli con calma."),

        new(HotkeyCommand.AnnounceHotkeyList, HotkeyCategory.Information, "Leggi tutte le scorciatoie",
            "Legge l'elenco delle combinazioni assegnate. Sostituisce il foglietto attaccato al monitor."),

        new(HotkeyCommand.SilenceSpeech, HotkeyCategory.Information, "Zitta la voce",
            "Interrompe l'annuncio in corso senza annullare nulla. Serve quando l'interlocutore comincia a "
            + "parlare mentre la voce sta ancora leggendo."),

        // ------------------------------------------------------------------
        // Coda e stato agente
        // ------------------------------------------------------------------
        new(HotkeyCommand.ToggleAgentLogin, HotkeyCategory.Queue, "Entra o esci dalla coda",
            "Collega o scollega l'operatore dalle code del centralino."),

        new(HotkeyCommand.ToggleAgentPause, HotkeyCategory.Queue, "Pausa o ripresa in coda",
            "Sospende l'assegnazione di nuove chiamate senza uscire dalla coda. Richiede l'API del centralino."),

        // L'etichetta e' corta di proposito: nell'elenco dei tasti compare seguita da
        // due combinazioni per esteso, e su una riga da ottanta caratteri — la
        // larghezza di un display braille — "Postazione fuori servizio" mandava la
        // riga a capo nel mezzo di una combinazione.
        new(HotkeyCommand.ToggleServicePause, HotkeyCategory.Queue, "Fuori servizio",
            "Mette la postazione in pausa: ogni chiamata entrante viene rifiutata subito e riparte verso un "
            + "collega, invece di squillare a vuoto. Il campo di composizione si blocca e un avviso rosso "
            + "lampeggia finche' non si rientra in servizio con la stessa combinazione. A differenza della pausa "
            + "in coda, funziona anche senza API del centralino."),

        new(HotkeyCommand.AnnounceAgentState, HotkeyCategory.Queue, "Stato dell'agente",
            "Annuncia se sei collegato alla coda, in pausa o disconnesso."),

        new(HotkeyCommand.AnnounceExtensionState, HotkeyCategory.Information, "Stato dell'interno",
            "Dice se l'interno digitato nel campo di composizione, o quello del contatto evidenziato, e' libero, "
            + "occupato o fuori servizio. E' la domanda da fare prima di passare una chiamata."),

        new(HotkeyCommand.PickSelectedQueueCall, HotkeyCategory.Queue, "Preleva la chiamata selezionata",
            "Prende in carico la chiamata su cui si trova il cursore nell'elenco della coda. "
            + "Funziona gia' con Invio quando il cursore e' sull'elenco, anche senza combinazione "
            + "assegnata qui: assegnarne una lo rende raggiungibile da qualunque punto della finestra."),

        new(HotkeyCommand.Cancel, HotkeyCategory.Queue, "Annulla l'operazione in corso",
            "Interrompe un trasferimento avviato e riprende in linea il chiamante esterno. "
            + "E' la via di uscita da qualunque sequenza iniziata per sbaglio."),
    ];

    /// <summary>Tutti i comandi, nell'ordine in cui vanno presentati.</summary>
    public static IReadOnlyList<HotkeyCommandInfo> All => Entries;

    /// <summary>Etichette leggibili, per i messaggi di conflitto.</summary>
    public static IReadOnlyDictionary<HotkeyCommand, string> Labels { get; }
        = Entries.ToDictionary(e => e.Command, e => e.Label);

    /// <summary>Descrizione di un comando.</summary>
    /// <param name="command">Comando cercato.</param>
    /// <remarks>
    /// Un comando privo di voce nel catalogo ricade sul proprio nome tecnico invece
    /// di sollevare un'eccezione: un pannello che non si apre è peggio di una riga
    /// scritta male.
    /// </remarks>
    public static HotkeyCommandInfo Describe(HotkeyCommand command)
        => Entries.FirstOrDefault(e => e.Command == command)
           ?? new HotkeyCommandInfo(command, HotkeyCategory.Information, command.ToString(), string.Empty);

    /// <summary>Etichetta di un comando.</summary>
    /// <param name="command">Comando cercato.</param>
    public static string Label(HotkeyCommand command) => Describe(command).Label;

    /// <summary>Comandi di una categoria, nell'ordine di presentazione.</summary>
    /// <param name="category">Categoria richiesta.</param>
    public static IEnumerable<HotkeyCommandInfo> InCategory(HotkeyCategory category)
        => Entries.Where(e => e.Category == category);

    /// <summary>Nome pronunciabile di una categoria.</summary>
    /// <param name="category">Categoria da nominare.</param>
    public static string CategoryLabel(HotkeyCategory category) => category switch
    {
        HotkeyCategory.CallControl => "Gestione della chiamata",
        HotkeyCategory.Navigation => "Navigazione e pannelli",
        HotkeyCategory.Information => "Informazioni sullo stato",
        HotkeyCategory.Queue => "Coda e stato agente",
        _ => category.ToString(),
    };

    /// <summary>Categorie nell'ordine in cui vanno presentate.</summary>
    public static IReadOnlyList<HotkeyCategory> Categories { get; } =
    [
        HotkeyCategory.CallControl,
        HotkeyCategory.Navigation,
        HotkeyCategory.Information,
        HotkeyCategory.Queue,
    ];
}
