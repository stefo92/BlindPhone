namespace BlindPhone.Core.Domain;

/// <summary>
/// Comandi assegnabili a una scorciatoia da tastiera.
/// </summary>
/// <remarks>
/// L'enumerazione è la sorgente di verità del pannello tasti rapidi: ogni voce
/// compare nell'elenco configurabile, e aggiungerne una qui la rende
/// automaticamente rimappabile senza altre modifiche.
/// </remarks>
public enum HotkeyCommand
{
    // Gestione chiamata
    /// <summary>Risponde alla chiamata in squillo.</summary>
    Answer,

    /// <summary>Riaggancia la chiamata attiva o rifiuta quella in squillo.</summary>
    HangUp,

    /// <summary>Mette in attesa o riprende.</summary>
    ToggleHold,

    /// <summary>Alterna fra l'interlocutore interno e quello esterno.</summary>
    Swap,

    /// <summary>Avvia una conferenza a tre.</summary>
    Conference,

    /// <summary>Tasto unico del trasferimento, cieco e con annuncio.</summary>
    Transfer,

    /// <summary>
    /// Si include nella conversazione dell'interno occupato a cui si sta cercando di
    /// passare la chiamata.
    /// </summary>
    /// <remarks>
    /// Serve a segnalare a un collega gia' al telefono che c'e' una chiamata urgente
    /// per lui. Tutti i presenti sentono un segnale periodico che dichiara l'ascolto:
    /// non si puo' spegnere, ed e' cio' che distingue questa funzione da
    /// un'intercettazione.
    /// </remarks>
    BargeIn,

    /// <summary>Silenzia o riattiva il microfono.</summary>
    ToggleMute,

    /// <summary>
    /// Avvia o interrompe la registrazione della conversazione.
    /// </summary>
    /// <remarks>
    /// Registra le due direzioni mescolate in un unico file nella cartella Documenti
    /// dell'operatore. All'avvio e alla chiusura l'interlocutore sente un avviso
    /// vocale, che è l'adempimento che rende lecita la registrazione: si può
    /// disattivare dalle impostazioni, e in quel caso informarlo tocca all'operatore.
    /// </remarks>
    ToggleCallRecording,

    /// <summary>Richiama l'ultimo numero composto.</summary>
    RedialLast,

    /// <summary>
    /// Attiva o disattiva la modalità toni, per rispondere ai menu automatici.
    /// </summary>
    /// <remarks>
    /// Finché è attiva, le cifre digitate vengono inviate in linea come toni invece di
    /// finire nel campo di composizione. È la risposta al conflitto fra i due usi
    /// della stessa tastiera durante una conversazione.
    /// </remarks>
    ToggleDtmfMode,

    /// <summary>
    /// Invia come toni quello che è scritto nel campo di composizione.
    /// </summary>
    /// <remarks>
    /// Consente di preparare la sequenza con calma, rileggendola, e di mandarla in un
    /// gesto solo: dentro un menu automatico digitare in diretta significa sbagliare e
    /// ricominciare.
    /// </remarks>
    SendDialerAsDtmf,

    // Navigazione
    /// <summary>Sposta il cursore sul campo di composizione.</summary>
    FocusDialer,

    /// <summary>Sposta il focus sull'elenco delle chiamate in coda.</summary>
    FocusQueue,

    /// <summary>Sposta il focus sull'elenco dei colleghi in turno.</summary>
    FocusColleagues,

    /// <summary>Apre la rubrica.</summary>
    OpenContacts,

    /// <summary>Apre il pannello delle ultime chiamate.</summary>
    OpenCallHistory,

    /// <summary>Apre le impostazioni.</summary>
    OpenSettings,

    /// <summary>Apre la diagnostica.</summary>
    OpenDiagnostics,

    /// <summary>
    /// Prepara e invia all'assistenza il pacchetto diagnostico.
    /// </summary>
    /// <remarks>
    /// Ha una combinazione propria e non vive dentro le impostazioni perché serve
    /// proprio quando le cose vanno male: in quel momento navigare fino a una scheda,
    /// scegliere una cartella e ritrovare un file per allegarlo a una mail è una
    /// sequenza che non si porta a termine.
    /// </remarks>
    SendSupportPackage,

    /// <summary>
    /// Apre l'elenco degli account SIP, per passare da uno all'altro.
    /// </summary>
    /// <remarks>
    /// Serve a chi copre due enti o lavora su una postazione condivisa a turni:
    /// il cambio è una pressione più un Invio, invece di riscrivere quattro campi
    /// e una password.
    /// </remarks>
    OpenSipAccounts,

    /// <summary>Apre la guida in linea.</summary>
    OpenHelp,

    /// <summary>
    /// Entra in una riunione Zoom, componendo quello che c'è nel campo di
    /// composizione.
    /// </summary>
    /// <remarks>
    /// Funzione secondaria, e la collocazione lo rispetta: non aggiunge un tasto alla
    /// modalità semplificata, non aggiunge un pulsante alla barra delle azioni di
    /// chiamata, e chi non la usa non paga niente.
    /// </remarks>
    JoinMeeting,

    /// <summary>Riporta il focus sulla chiamata attiva.</summary>
    FocusActiveCall,

    /// <summary>
    /// Riporta in primo piano la finestra riposta nell'area di notifica.
    /// </summary>
    /// <remarks>
    /// Ha senso quasi solo come scorciatoia globale: se la finestra è nascosta, i
    /// comandi locali non arrivano.
    /// </remarks>
    ShowWindow,

    /// <summary>Chiude davvero BlindPhone, invece di riporlo nell'area di notifica.</summary>
    QuitApplication,

    // Informazione
    /// <summary>Annuncia il numero di chiamate in coda.</summary>
    AnnounceQueueCount,

    /// <summary>Legge l'elenco completo dei chiamanti in coda.</summary>
    AnnounceQueueList,

    /// <summary>Annuncia il riepilogo completo dello stato.</summary>
    AnnounceStatusSummary,

    /// <summary>Annuncia la durata della chiamata in corso.</summary>
    AnnounceCallDuration,

    /// <summary>Annuncia la qualità della linea e la banda audio.</summary>
    AnnounceLineQuality,

    /// <summary>Ripete l'ultimo annuncio.</summary>
    RepeatLastAnnouncement,

    /// <summary>Apre lo storico degli annunci.</summary>
    OpenAnnouncementHistory,

    /// <summary>Legge l'elenco di tutte le scorciatoie.</summary>
    AnnounceHotkeyList,

    /// <summary>Interrompe la voce in corso.</summary>
    SilenceSpeech,

    // Coda e stato agente
    /// <summary>Collega o scollega l'operatore dalle code.</summary>
    ToggleAgentLogin,

    /// <summary>Mette in pausa o riprende il servizio.</summary>
    ToggleAgentPause,

    /// <summary>
    /// Mette la postazione fuori servizio, o la riporta in servizio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Da non confondere con <see cref="ToggleAgentPause"/>, che riguarda le sole code
    /// e passa dall'API del centralino. Questa è la pausa della <b>postazione</b>: fa
    /// rifiutare ogni chiamata entrante, da coda o diretta, e funziona anche dove
    /// l'API non c'è, perché agisce sulla segnalazione SIP.
    /// </para>
    /// <para>
    /// È il comando che si preme alzandosi dalla sedia, ed è per questo che ha un
    /// tasto che non si preme per sbaglio.
    /// </para>
    /// </remarks>
    ToggleServicePause,

    /// <summary>Annuncia lo stato dell'agente.</summary>
    AnnounceAgentState,

    /// <summary>
    /// Annuncia lo stato dell'interno indicato: libero, occupato, fuori servizio.
    /// </summary>
    /// <remarks>
    /// È la domanda diretta prima di passare una chiamata. Si applica al numero
    /// digitato nel campo di composizione o al contatto evidenziato.
    /// </remarks>
    AnnounceExtensionState,

    /// <summary>Preleva la chiamata selezionata in coda.</summary>
    PickSelectedQueueCall,

    /// <summary>Annulla l'operazione in corso.</summary>
    Cancel,
}

/// <summary>Categoria di appartenenza di un comando, usata per raggruppare l'elenco.</summary>
public enum HotkeyCategory
{
    /// <summary>Gestione della chiamata.</summary>
    CallControl,

    /// <summary>Navigazione fra pannelli.</summary>
    Navigation,

    /// <summary>Richieste di informazione e accessibilità.</summary>
    Information,

    /// <summary>Coda e stato agente.</summary>
    Queue,
}

/// <summary>Descrizione di un comando per il pannello tasti rapidi.</summary>
/// <param name="Command">Comando descritto.</param>
/// <param name="Category">Categoria di appartenenza.</param>
/// <param name="Label">Etichetta leggibile e pronunciabile.</param>
/// <param name="Description">Spiegazione estesa, letta nella guida.</param>
public sealed record HotkeyCommandInfo(
    HotkeyCommand Command,
    HotkeyCategory Category,
    string Label,
    string Description);
