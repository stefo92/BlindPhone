using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>Azione che la barra dei pulsanti può proporre all'operatore.</summary>
/// <remarks>
/// Non coincide con <see cref="HotkeyCommand"/> e non poteva: il trasferimento ha una
/// combinazione sola che cambia significato con la fase, mentre un pulsante deve dire
/// da sé cosa fa. Le tre voci di trasferimento qui separate sono i tre significati che
/// quel tasto assume, resi espliciti per chi guarda invece che per chi conta le
/// pressioni.
/// </remarks>
public enum CallAction
{
    /// <summary>Risponde alla chiamata in squillo.</summary>
    Answer,

    /// <summary>Rifiuta la chiamata in squillo.</summary>
    Reject,

    /// <summary>Chiude la chiamata in corso.</summary>
    HangUp,

    /// <summary>Mette in attesa la conversazione.</summary>
    Hold,

    /// <summary>Riprende una chiamata in attesa.</summary>
    Resume,

    /// <summary>Alterna fra le due linee.</summary>
    Swap,

    /// <summary>Unisce le due linee in conferenza a tre.</summary>
    ConferenceStart,

    /// <summary>Scioglie la conferenza a tre.</summary>
    ConferenceEnd,

    /// <summary>Avvia la procedura di trasferimento.</summary>
    TransferStart,

    /// <summary>Manda la chiamata all'interno senza annunciarla.</summary>
    TransferBlind,

    /// <summary>Chiama l'interno per annunciare la chiamata prima di passarla.</summary>
    TransferAttended,

    /// <summary>Passa la chiamata all'interno con cui si sta parlando.</summary>
    TransferComplete,

    /// <summary>Annulla il trasferimento e torna al chiamante.</summary>
    TransferCancel,

    /// <summary>
    /// Si include nella conversazione di un interno occupato, per segnalargli una
    /// chiamata urgente.
    /// </summary>
    BargeIn,

    /// <summary>Avvia la registrazione della conversazione.</summary>
    RecordStart,

    /// <summary>Interrompe la registrazione della conversazione.</summary>
    RecordStop,

    /// <summary>Entra in modalità toni.</summary>
    DtmfOn,

    /// <summary>Esce dalla modalità toni.</summary>
    DtmfOff,

    /// <summary>Silenzia il microfono.</summary>
    MuteOn,

    /// <summary>Riattiva il microfono.</summary>
    MuteOff,

    /// <summary>
    /// Mette la postazione in pausa.
    /// </summary>
    /// <remarks>
    /// Non compare fra i pulsanti della barra: vive accanto all'orologio del servizio,
    /// perché riguarda il turno e non la chiamata in corso.
    /// </remarks>
    ServicePauseOn,

    /// <summary>Rimette la postazione in servizio.</summary>
    ServicePauseOff,

    /// <summary>Apre l'elenco delle ultime chiamate.</summary>
    OpenCallHistory,

    /// <summary>Apre le impostazioni.</summary>
    OpenSettings,

    /// <summary>Apre l'elenco degli account SIP.</summary>
    OpenSipAccounts,

    /// <summary>Mostra l'aggiornamento disponibile sul server dell'ente.</summary>
    OpenUpdate,
}

/// <summary>Famiglia a cui appartiene un pulsante, per raggrupparli a schermo.</summary>
public enum CallActionGroup
{
    /// <summary>Comandi che agiscono sulla chiamata: rispondere, chiudere, passare.</summary>
    Call,

    /// <summary>Interruttori della conversazione: registrazione, toni, microfono.</summary>
    Conversation,

    /// <summary>Stato della postazione e finestre: pausa, impostazioni, account.</summary>
    Station,
}

/// <summary>
/// Fotografia dello stato della postazione, da cui dipende quali pulsanti abbiano
/// senso in questo momento.
/// </summary>
/// <param name="HasRingingCall">C'è una chiamata entrante che squilla.</param>
/// <param name="HasActiveCall">C'è una conversazione in corso.</param>
/// <param name="HasOutgoingCall">C'è una chiamata uscente in instaurazione.</param>
/// <param name="HeldCount">Quante chiamate sono in attesa.</param>
/// <param name="IsConferenceActive">È aperta una conferenza a tre.</param>
/// <param name="TransferPhase">Fase della procedura di trasferimento.</param>
/// <param name="HasTypedDestination">Nel campo di composizione c'è una destinazione.</param>
/// <param name="IsRecording">La conversazione è in registrazione.</param>
/// <param name="IsDtmfMode">La tastiera invia toni in linea.</param>
/// <param name="IsMuted">Il microfono è silenziato.</param>
/// <param name="IsServicePaused">La postazione è in pausa.</param>
/// <param name="HasUpdateAvailable">
/// Sul server dell'ente c'è una versione più recente di quella installata.
/// </param>
/// <param name="CanBargeIn">
/// L'interno a cui si sta passando la chiamata è occupato <b>e</b> il centralino ha
/// comunicato gli identificativi della sua conversazione, quindi l'inclusione è
/// eseguibile davvero. Le due condizioni vanno verificate insieme: senza gli
/// identificativi il comando non avrebbe nulla a cui unirsi, e proporlo significherebbe
/// promettere una cosa che fallisce quando serve.
/// </param>
public readonly record struct CallActionContext(
    bool HasRingingCall = false,
    bool HasActiveCall = false,
    bool HasOutgoingCall = false,
    int HeldCount = 0,
    bool IsConferenceActive = false,
    TransferPhase TransferPhase = TransferPhase.Idle,
    bool HasTypedDestination = false,
    bool IsRecording = false,
    bool IsDtmfMode = false,
    bool IsMuted = false,
    bool IsServicePaused = false,
    bool HasUpdateAvailable = false,
    bool CanBargeIn = false)
{
    /// <summary>C'è qualcuno in linea che sente l'operatore.</summary>
    public bool IsTalking => HasActiveCall || IsConferenceActive;

    /// <summary>
    /// Esiste una chiamata su cui i comandi possono agire, in qualunque stato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comprende le chiamate in attesa, ed è il punto in cui la barra sbagliava:
    /// <c>HangUpAsync</c> e l'avvio del trasferimento accettano entrambi una chiamata
    /// in attesa — la ripescano con <c>HeldCalls.FirstOrDefault()</c> — mentre i
    /// pulsanti corrispondenti sparivano appena si premeva il tasto dell'attesa.
    /// </para>
    /// <para>
    /// <b>Comprende anche <see cref="TransferPhase.Completing"/>, e quella riga vale
    /// da sola l'intera proprietà.</b> Mentre il trasferimento è in volo il chiamante
    /// non è né attivo né in attesa: è parcheggiato dentro la procedura. Escludendo
    /// quella fase la postazione risultava senza chiamate, ogni comando diventava
    /// inerte — il tastierino restituiva <i>nessuna azione</i> anche al tasto di
    /// chiusura — e per liberare la linea non restava che terminare il programma.
    /// Finché esiste una chiamata dentro una procedura, deve esistere un modo di
    /// chiuderla.
    /// </para>
    /// </remarks>
    public bool HasAnyCall
        => HasActiveCall || HasOutgoingCall || HeldCount > 0 || IsConferenceActive
           || TransferPhase is TransferPhase.EnteringDestination
                            or TransferPhase.Consulting
                            or TransferPhase.Completing;

    /// <summary>Il trasferimento è in una delle sue fasi operative.</summary>
    /// <remarks>
    /// Il completamento è una fase operativa a tutti gli effetti: finché il centralino
    /// non ha risposto, le linee in gioco restano due e i comandi che ne presuppongono
    /// una sola non hanno modo di dire quale.
    /// </remarks>
    public bool IsTransferring
        => TransferPhase is TransferPhase.EnteringDestination
                         or TransferPhase.Consulting
                         or TransferPhase.Completing;
}

/// <summary>Un pulsante della barra: cosa fa, come si chiama, cosa promette.</summary>
/// <param name="Action">Azione da eseguire.</param>
/// <param name="Icon">
/// Pittogramma che accompagna l'etichetta. <b>Non porta informazione da solo</b>:
/// accanto c'è sempre il nome della funzione, e il nome accessibile è la sola
/// etichetta.
/// <para>
/// È un punto di codice di <b>Segoe Fluent Icons</b>, il font di icone installato con
/// Windows, scritto come sequenza di escape e non come carattere letterale. Prima
/// erano emoji: si vedevano a colori in mezzo a un'interfaccia monocromatica, non si
/// allineavano alla linea di base del testo e cambiavano disegno fra una versione di
/// Windows e l'altra. Un glifo di questo font eredita colore e corpo dal testo
/// accanto, quindi segue il tema chiaro, quello scuro e il contrasto elevato senza
/// che nessuno debba occuparsene.
/// </para>
/// <para>
/// Chi lo mostra deve dichiarare la famiglia di caratteri, altrimenti compare un
/// rettangolo vuoto: vedi il costruttore dei pulsanti nella finestra principale.
/// </para>
/// </param>
/// <param name="Label">Nome della funzione, sul pulsante.</param>
/// <param name="Description">
/// Frase che ne spiega l'effetto: finisce nel fumetto, insieme al tasto rapido, e nel
/// testo di aiuto per la tecnologia assistiva.
/// </param>
/// <param name="Group">Famiglia di appartenenza, per raggruppare i pulsanti a schermo.</param>
/// <param name="IsEnabled">
/// <c>false</c> quando la funzione appartiene alla chiamata in corso ma in questo
/// istante non può agire. Vedi <see cref="CallActionCatalog"/> per quando succede.
/// </param>
public readonly record struct CallActionButton(
    CallAction Action,
    string Icon,
    string Label,
    string Description,
    CallActionGroup Group,
    bool IsEnabled = true);

/// <summary>
/// Decide quali pulsanti mostrare, secondo lo stato della postazione.
/// </summary>
/// <remarks>
/// <para>
/// <b>I pulsanti sono per chi vede; la tastiera resta la via principale.</b> Ogni
/// azione di questo elenco ha già una combinazione di tasti, e il pulsante non la
/// sostituisce: la affianca per il collega vedente che affianca l'operatore, per il
/// responsabile che passa dalla postazione e per l'operatore ipovedente che lavora con
/// il mouse e l'ingrandimento.
/// </para>
/// <para>
/// <b>Durante una chiamata la barra tiene la sua forma.</b> È la correzione di un
/// difetto vero: mettendo in attesa sparivano di colpo quasi tutti i pulsanti, e per
/// chi guarda lo schermo la postazione sembrava essersi svuotata proprio mentre la
/// chiamata era ancora lì. Finché una chiamata esiste — attiva, in attesa, in
/// squillo altrove o dentro un trasferimento — i comandi che la riguardano restano al
/// loro posto e cambiano etichetta: «Metti in attesa» diventa «Riprendi», e non
/// scompare.
/// </para>
/// <para>
/// <b>Un pulsante visibile è un pulsante che funziona</b>, con una sola eccezione
/// dichiarata. Registrazione, toni e microfono hanno bisogno di voce che passa: con
/// la chiamata in attesa non possono fare nulla. Restano visibili — altrimenti la
/// barra si smonterebbe di nuovo — ma <b>spenti</b>, e il fumetto dice perché. È
/// l'unico caso in cui questa barra mostra qualcosa che non si può premere, e vale
/// solo mentre la chiamata è in attesa: fuori da una chiamata quei pulsanti non ci
/// sono proprio.
/// </para>
/// <para>
/// <b>Le coppie sono un pulsante solo.</b> Attesa e ripresa, registra e interrompi,
/// toni acceso e spento non compaiono mai insieme: il pulsante dice l'azione che si
/// può fare adesso, non lo stato in cui ci si trova. Lo stato lo raccontano le caselle
/// di riepilogo e gli annunci.
/// </para>
/// <para>
/// Vive nel core e non nel code-behind della finestra per la stessa ragione di
/// <see cref="Speech.CallBoardTexts"/>: è una regola sul comportamento visibile, e va
/// verificata con i test invece che a occhio, stato per stato.
/// </para>
/// </remarks>
public static class CallActionCatalog
{
    /// <summary>Motivo per cui un interruttore della conversazione è spento.</summary>
    private const string SoloMentreSiParla = "Disponibile mentre parli: riprendi prima la chiamata";

    /// <summary>
    /// Elenca i pulsanti da mostrare, nell'ordine in cui vanno disposti.
    /// </summary>
    /// <param name="context">Stato della postazione.</param>
    /// <remarks>
    /// L'ordine non è casuale ed è sempre lo stesso: prima ciò che riguarda la
    /// chiamata che squilla, poi la conversazione, poi il trasferimento, infine gli
    /// interruttori della postazione. Chi impara dove sta un pulsante lo ritrova lì.
    /// </remarks>
    public static IReadOnlyList<CallActionButton> Available(CallActionContext context)
    {
        var buttons = new List<CallActionButton>(12);

        // 1. La chiamata che squilla. Viene prima di tutto perche' e' l'unica cosa
        //    che ha una scadenza: mentre si guarda la barra, sta squillando.
        if (context.HasRingingCall)
        {
            buttons.Add(new(CallAction.Answer, "\uE717", "Rispondi",
                "Risponde alla chiamata che sta squillando", CallActionGroup.Call));

            buttons.Add(new(CallAction.Reject, "\uE711", "Rifiuta",
                "Rifiuta la chiamata senza rispondere", CallActionGroup.Call));
        }

        // 2. La chiamata in corso, in qualunque stato si trovi.
        if (context.HasAnyCall)
        {
            buttons.Add(new(CallAction.HangUp, "\uF405", "Riaggancia",
                "Chiude la chiamata in corso", CallActionGroup.Call));
        }

        AddHoldButtons(buttons, context);

        // 3. Conferenza: serve una conversazione e qualcuno in attesa da unirci.
        if (context.IsConferenceActive)
        {
            buttons.Add(new(CallAction.ConferenceEnd, "\uF406", "Sciogli conferenza",
                "Chiude la conferenza e resta in linea con il primo interlocutore", CallActionGroup.Call));
        }
        else if (context.HasActiveCall && context.HeldCount > 0 && !context.IsTransferring)
        {
            buttons.Add(new(CallAction.ConferenceStart, "\uE902", "Conferenza",
                "Unisce in conferenza a tre chi è in linea e chi è in attesa", CallActionGroup.Call));
        }

        // 4. Trasferimento: tre fasi, tre insiemi di pulsanti diversi.
        AddTransferButtons(buttons, context);

        // 5. Interruttori della conversazione.
        AddConversationButtons(buttons, context);

        // 6. Finestre. Sempre disponibili: sono l'unica parte della barra che non
        //    dipende da cosa sta succedendo, ed e' il motivo per cui stanno in fondo e
        //    non in mezzo agli altri.
        //
        //    LA PAUSA NON STA QUI, e non e' una dimenticanza: e' l'unico comando che
        //    riguarda il turno e non la chiamata, e vive accanto all'orologio del
        //    servizio, che e' l'altra cosa che parla del turno. Vedi la riga di stato
        //    della finestra principale.
        buttons.Add(new(CallAction.OpenCallHistory, "\uE8FD", "Elenco chiamate",
            "Apre l'elenco delle ultime chiamate", CallActionGroup.Station));

        buttons.Add(new(CallAction.OpenSettings, "\uE713", "Impostazioni",
            "Apre le impostazioni", CallActionGroup.Station));

        buttons.Add(new(CallAction.OpenSipAccounts, "\uE77B", "Account SIP",
            "Sceglie l'account con cui lavorare", CallActionGroup.Station));

        // L'aggiornamento e' l'unico pulsante che compare per una notizia e non per
        // una possibilita': sta in fondo perche' non ha fretta, e c'e' solo quando
        // c'e' davvero qualcosa da installare. Un pulsante «cerca aggiornamenti»
        // permanente direbbe «no» il novantanove per cento delle volte.
        if (context.HasUpdateAvailable)
        {
            buttons.Add(new(CallAction.OpenUpdate, "\uE896", "Aggiornamento disponibile",
                "Mostra la versione disponibile e le novità che introduce", CallActionGroup.Station));
        }

        return buttons;
    }

    /// <summary>
    /// Il pulsante della pausa, che vive fuori dalla barra accanto all'orologio.
    /// </summary>
    /// <param name="isServicePaused">Se la postazione è in pausa adesso.</param>
    /// <remarks>
    /// Sta qui, e non nel code-behind della finestra, perché è l'unica sorgente di
    /// verità su come quel pulsante si chiama e che pittogramma porta nei due stati:
    /// scritto nella finestra, il testo esisteva in un punto solo ma nessuno poteva
    /// verificarlo, e infatti il pittogramma è rimasto la tazza anche in pausa finché
    /// qualcuno non se n'è accorto guardando lo schermo.
    /// </remarks>
    public static CallActionButton ServicePauseButton(bool isServicePaused)
        => isServicePaused
            ? new(CallAction.ServicePauseOff, "\uEA8F", "Torna in servizio",
                "La postazione ricomincia a ricevere chiamate", CallActionGroup.Station)
            : new(CallAction.ServicePauseOn, "\uE7ED", "Pausa",
                "La postazione smette di ricevere chiamate", CallActionGroup.Station);


    /// <summary>
    /// Propone l'inclusione quando l'interno di destinazione è occupato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Compare solo se è davvero eseguibile.</b> Non basta che l'interno risulti
    /// occupato: serve che il centralino abbia comunicato gli identificativi della sua
    /// conversazione, perché è a quelli che l'invito si aggancia. Un pulsante che
    /// compare e poi fallisce è peggio di un pulsante assente, e su un'interfaccia
    /// sonora costa all'operatore un tentativo e un annuncio di errore nel momento in
    /// cui ha fretta.
    /// </para>
    /// <para>
    /// La descrizione dice cosa succede agli altri, non solo cosa fa il pulsante: chi
    /// lo preme sta per entrare nella conversazione di due persone, e deve sapere che
    /// se ne accorgeranno.
    /// </para>
    /// </remarks>
    private static void AddBargeInButton(List<CallActionButton> buttons, CallActionContext context)
    {
        if (!context.CanBargeIn) return;

        buttons.Add(new(
            CallAction.BargeIn,
            "\uE95B",
            "Inclusione",
            "Entra nella conversazione dell'interno occupato per segnalargli la chiamata. "
            + "Tutti i presenti sentono un segnale periodico che dichiara il tuo ascolto",
            CallActionGroup.Call));
    }

    /// <summary>Attesa, ripresa e alternanza: la stessa funzione in tre momenti.</summary>
    private static void AddHoldButtons(List<CallActionButton> buttons, CallActionContext context)
    {
        if (context.IsTransferring) return;

        // Con una linea attiva e una in attesa il gesto giusto e' l'alternanza, che
        // le scambia, invece della ripresa, che ne lascerebbe due aperte con
        // l'operatore su nessuna delle due.
        if (context.HeldCount > 0 && context.HasActiveCall)
        {
            buttons.Add(new(CallAction.Swap, "\uE8AB", "Alterna",
                "Passa all'altra linea e mette in attesa questa", CallActionGroup.Call));

            buttons.Add(new(CallAction.Hold, "\uE769", "Metti in attesa",
                "Mette in attesa chi è in linea", CallActionGroup.Call));

            return;
        }

        if (context.HasActiveCall || context.IsConferenceActive)
        {
            buttons.Add(new(CallAction.Hold, "\uE769", "Metti in attesa",
                "Mette in attesa chi è in linea", CallActionGroup.Call));

            return;
        }

        if (context.HeldCount > 0)
        {
            buttons.Add(new(CallAction.Resume, "\uE768", "Riprendi",
                "Torna a parlare con chi è in attesa", CallActionGroup.Call));
        }
    }

    /// <summary>Aggiunge i pulsanti del trasferimento previsti dalla fase corrente.</summary>
    /// <remarks>
    /// <para>
    /// È qui che i pulsanti dicono più della tastiera. Con la combinazione unica, la
    /// differenza fra trasferimento cieco e con annuncio sta nella <b>doppia pressione
    /// rapida</b>: una cosa che si impara e che va ricordata al momento giusto. Due
    /// pulsanti distinti la rendono una scelta invece che una destrezza.
    /// </para>
    /// <para>
    /// Il trasferimento si può avviare anche su una chiamata già in attesa, perché
    /// <c>BeginTransferAsync</c> la ripesca da sé: è il caso di chi mette in attesa il
    /// chiamante <b>per poi decidere</b> a chi passarlo.
    /// </para>
    /// </remarks>
    private static void AddTransferButtons(List<CallActionButton> buttons, CallActionContext context)
    {
        switch (context.TransferPhase)
        {
            case TransferPhase.Idle when context.HasActiveCall || context.HeldCount > 0:
                buttons.Add(new(CallAction.TransferStart, "\uE72A", "Trasferisci",
                    "Mette il chiamante in attesa e chiede a chi passarlo", CallActionGroup.Call));
                break;

            case TransferPhase.EnteringDestination:
                if (context.HasTypedDestination)
                {
                    buttons.Add(new(CallAction.TransferBlind, "\uE8AD", "Trasferisci subito",
                        "Manda il chiamante all'interno senza annunciarlo", CallActionGroup.Call));

                    buttons.Add(new(CallAction.TransferAttended, "\uE90A", "Annuncia e trasferisci",
                        "Chiama prima l'interno per annunciare la chiamata", CallActionGroup.Call));
                }

                AddBargeInButton(buttons, context);

                buttons.Add(new(CallAction.TransferCancel, "\uE72B", "Annulla trasferimento",
                    "Torna al chiamante senza trasferirlo", CallActionGroup.Call));
                break;

            case TransferPhase.Consulting:
                buttons.Add(new(CallAction.TransferComplete, "\uE73E", "Passa la chiamata",
                    "Collega il chiamante all'interno e chiude la tua linea", CallActionGroup.Call));

                // ALTERNANZA, e qui vale piu' che altrove.
                //
                // Mentre si annuncia la chiamata serve tornare un attimo dal chiamante
                // per chiedergli un nome, per dirgli che il collega arriva, e poi
                // tornare dall'interno. Il pulsante compare solo quando la destinazione
                // ha risposto: finche' squilla non c'e' una seconda conversazione da
                // alternare, e mostrarlo prima sarebbe un pulsante che non fa nulla.
                if (context.HasActiveCall)
                {
                    buttons.Add(new(CallAction.Swap, "\uE8AB", "Alterna",
                        "Passa all'altra linea e mette in attesa questa. Il trasferimento resta aperto",
                        CallActionGroup.Call));
                }

                AddBargeInButton(buttons, context);

                buttons.Add(new(CallAction.TransferCancel, "\uE72B", "Annulla trasferimento",
                    "Chiude la linea con l'interno e torna al chiamante", CallActionGroup.Call));
                break;
        }
    }

    /// <summary>
    /// Registrazione, toni e microfono: gli interruttori di ciò che passa in linea.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compaiono per tutta la durata della chiamata e si spengono quando non possono
    /// agire, invece di sparire. Le condizioni non sono inventate qui: le impone il
    /// codice che sta sotto. La registrazione vuole una chiamata attiva e <b>rifiuta
    /// la conferenza</b> — due percorsi audio distinti darebbero un file con una sola
    /// delle due voci — e i toni vogliono una chiamata attiva su cui mandarli.
    /// </para>
    /// <para>
    /// Il microfono fa eccezione e resta acceso anche in conferenza: silenziarlo è un
    /// gesto sulla postazione, non sulla singola linea, e in conferenza serve
    /// esattamente quanto altrove.
    /// </para>
    /// <para>
    /// «Interrompi registrazione» compare finché la registrazione è in corso, in
    /// qualunque stato si trovi la chiamata: una registrazione che non si può fermare
    /// sarebbe la peggiore delle trappole.
    /// </para>
    /// </remarks>
    private static void AddConversationButtons(List<CallActionButton> buttons, CallActionContext context)
    {
        if (!context.HasAnyCall) return;

        if (context.IsRecording)
        {
            buttons.Add(new(CallAction.RecordStop, "\uE71A", "Interrompi registrazione",
                "Chiude il file della registrazione", CallActionGroup.Conversation));
        }
        else
        {
            // In conferenza la registrazione non e' disponibile e il codice la
            // rifiuta: il pulsante lo dice invece di far premere a vuoto.
            var puoRegistrare = context.HasActiveCall && !context.IsConferenceActive;

            buttons.Add(new(CallAction.RecordStart, "\uE7C8", "Registra",
                puoRegistrare
                    ? "Registra la conversazione in un file"
                    : context.IsConferenceActive
                        ? "In conferenza la registrazione non è disponibile"
                        : SoloMentreSiParla,
                CallActionGroup.Conversation,
                IsEnabled: puoRegistrare));
        }

        var puoMandareToni = context.HasActiveCall;

        buttons.Add(context.IsDtmfMode
            ? new(CallAction.DtmfOff, "\uE75F", "Esci dai toni",
                "I tasti tornano a scrivere nel campo di composizione", CallActionGroup.Conversation)
            : new(CallAction.DtmfOn, "\uE75F", "Toni",
                puoMandareToni
                    ? "I tasti inviano toni in linea, per i menu automatici"
                    : SoloMentreSiParla,
                CallActionGroup.Conversation,
                IsEnabled: puoMandareToni));

        buttons.Add(context.IsMuted
            ? new(CallAction.MuteOff, "\uE720", "Riattiva microfono",
                "L'interlocutore torna a sentirti", CallActionGroup.Conversation,
                IsEnabled: context.IsTalking)
            : new(CallAction.MuteOn, "\uF781", "Silenzia microfono",
                context.IsTalking ? "L'interlocutore non ti sente più" : SoloMentreSiParla,
                CallActionGroup.Conversation,
                IsEnabled: context.IsTalking));
    }
}
