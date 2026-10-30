using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Speech;

/// <summary>
/// Tutti i testi pronunciati all'operatore, raccolti in un unico punto.
/// </summary>
/// <remarks>
/// <para>
/// La centralizzazione ha tre scopi pratici. Primo: questi testi sono
/// l'<b>interfaccia utente</b> del prodotto, e vanno riletti e rifiniti da chi si
/// occupa di accessibilità e da operatori reali, che non leggono codice sorgente
/// sparso. Secondo: la localizzazione futura passa da qui e non da una caccia alle
/// stringhe. Terzo: la coerenza terminologica, che in un'interfaccia solo sonora
/// pesa più che in una visiva, perché l'operatore non può confrontare due schermate.
/// </para>
/// <para>
/// Criteri di scrittura seguiti, ricavati dall'analisi:
/// </para>
/// <list type="bullet">
///   <item><description>L'informazione più importante va all'inizio della frase: chi ascolta a 500 parole al minuto interrompe appena ha capito.</description></item>
///   <item><description>Niente gergo tecnico: "interno occupato", non "risposta 486".</description></item>
///   <item><description>Ogni azione conferma il proprio esito, nessun comando resta muto.</description></item>
///   <item><description>Quando si entra in una modalità si dice cosa fanno i tasti adesso, non solo cosa è appena successo.</description></item>
/// </list>
/// </remarks>
public static class AnnouncementTexts
{
    // ---------------------------------------------------------------------
    // Chiamate
    // ---------------------------------------------------------------------

    /// <summary>Chiamata entrante.</summary>
    /// <param name="party">Controparte.</param>
    /// <param name="queueName">Coda di provenienza, se nota.</param>
    /// <param name="grouping">Raggruppamento per la lettura del numero.</param>
    public static string IncomingCall(CallParty party, string? queueName, DigitGrouping grouping)
    {
        var who = CallerIdentity(party, grouping);
        return string.IsNullOrWhiteSpace(queueName)
            ? $"Chiamata da {who}"
            : $"Chiamata da {who}, coda {queueName}";
    }

    /// <summary>
    /// Identificazione del chiamante: nome e interno se il numero è in rubrica, il
    /// solo numero se non lo è.
    /// </summary>
    /// <param name="party">Controparte, già passata dalla risoluzione in rubrica.</param>
    /// <param name="grouping">Raggruppamento per la lettura del numero.</param>
    /// <remarks>
    /// <para>
    /// Tre casi, in ordine di quanto si sa di chi chiama.
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   <b>Nessun nome</b>: resta il solo numero, letto cifra per cifra. È il caso di
    ///   chi non risulta in rubrica.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Nome dichiarato dalla segnalazione ma numero non in rubrica</b>: si dicono
    ///   entrambi. Prima si diceva il solo nome, per non dare a un nome non verificato
    ///   l'aria di una scheda di rubrica — un ragionamento che il campo ha smentito il
    ///   10 agosto 2026. Il centralino dell'ente annuncia quasi tutte le chiamate
    ///   esterne come <c>"Chiamate esterne"</c>, senza numero dentro: l'operatore
    ///   sentiva «Chiamata da Chiamate esterne» e <b>non riceveva il numero da nessuna
    ///   parte</b>, né a voce né a schermo.
    ///   </description></item>
    ///   <item><description>
    ///   Il ragionamento era anche rovesciato: fra i due dati, il nome è quello che
    ///   chi chiama sceglie e il numero è quello che la rete porta. Nascondere il
    ///   secondo per prudenza sul primo toglieva l'unico su cui si possa fare
    ///   affidamento, e senza il quale non si richiama e non si trasferisce.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Numero ritrovato in rubrica</b>: nome e recapito insieme.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Perché nel terzo caso il numero si dice anche avendo il nome.</b> Il nome da
    /// solo risponde a «chi è» ma non a «come lo richiamo», e sono due domande che
    /// l'operatore si pone nello stesso momento: la seconda arriva subito dopo, quando
    /// il chiamante chiede di essere passato a qualcuno o quando la linea cade. Prima
    /// bisognava aprire la rubrica e ricercare il nome appena sentito, cioè rifare a
    /// mano il lavoro che il programma aveva già fatto un istante prima.
    /// </para>
    /// <para>
    /// <b>Perché la parola cambia.</b> «Interno 4021» e «numero 4021» non sono la
    /// stessa informazione: la prima dice che chi chiama è dentro la struttura, e
    /// cambia il modo di rispondere e ciò che si può dare per scontato. Si dice
    /// «interno» solo quando il numero <b>è</b> l'interno del contatto, o quando la
    /// segnalazione lo dichiara tale: un contatto in rubrica che sta chiamando dal
    /// proprio cellulare resta un numero esterno, e annunciarlo come interno sarebbe
    /// un'informazione falsa che l'operatore non ha modo di verificare.
    /// </para>
    /// <para>
    /// <b>Il nome si legge da <see cref="CallParty.HasName"/> e non da
    /// <see cref="CallParty.SpeechName"/>.</b> Quest'ultimo, quando il nome manca,
    /// ricade sul numero: passato a <see cref="SpeechFormatter.Party"/> come se fosse
    /// un nome, faceva prendere il ramo "c'è un nome" e il numero usciva senza essere
    /// scomposto in cifre. Un chiamante sconosciuto veniva annunciato come
    /// «tre miliardi quattrocentonovantaquattro milioni...», cioè in una forma che non
    /// si può trascrivere né ricomporre, che è esattamente il difetto per cui esiste
    /// <see cref="SpeechFormatter"/>.
    /// </para>
    /// <para>
    /// <b>Lo stesso difetto rientrava da un'altra porta</b>: il numero scritto al posto
    /// del nome, dal centralino o da una voce di rubrica senza nominativo. Contava come
    /// nome, e l'annuncio diventava «Chiamata da 3491234567, numero tre, quattro,
    /// nove…», con la prima metà letta come numero intero. <see cref="CallParty.HasName"/>
    /// guarda <see cref="CallParty.MeaningfulName"/>, e un nome così non conta.
    /// </para>
    /// </remarks>
    public static string CallerIdentity(CallParty party, DigitGrouping grouping)
    {
        if (!party.HasName) return SpeechFormatter.Party(null, party.Number, grouping);

        // Il numero si dice sempre, quando c'e'. Che il nome venga dalla rubrica o
        // dalla segnalazione cambia quanto ci si fida del nome, non se il numero
        // serva: quello serve comunque, ed e' l'unico dato con cui si richiama.
        if (string.IsNullOrWhiteSpace(party.Number)) return party.SpeechName;

        var label = party.IsInternal || SameNumber(party.Extension, party.Number)
            ? "interno"
            : "numero";

        return $"{party.SpeechName}, {label} {SpeechFormatter.Digits(party.Number, grouping)}";
    }

    /// <summary>
    /// Confronta due recapiti guardando le sole cifre.
    /// </summary>
    /// <remarks>
    /// La rubrica conserva i numeri come sono stati scritti o importati: lo stesso
    /// interno può comparire come "4021", "40 21" o "int. 4021". Un confronto
    /// testuale li considererebbe tre numeri diversi, e la parola "interno"
    /// scomparirebbe dall'annuncio proprio sui contatti importati da file.
    /// </remarks>
    private static bool SameNumber(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        var a = new string([.. left.Where(char.IsDigit)]);
        var b = new string([.. right.Where(char.IsDigit)]);

        return a.Length > 0 && string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>Chiamata risposta.</summary>
    /// <param name="party">Controparte.</param>
    public static string CallAnswered(CallParty party) => $"In linea con {party.SpeechName}";

    /// <summary>Chiamata terminata, con il motivo quando è informativo.</summary>
    /// <param name="party">Controparte.</param>
    /// <param name="reason">Motivo di chiusura.</param>
    /// <param name="talkDuration">Durata della conversazione.</param>
    public static string CallEnded(CallParty party, CallEndReason? reason, TimeSpan talkDuration)
    {
        var who = string.IsNullOrWhiteSpace(party.SpeechName) ? "chiamata" : party.SpeechName;

        return reason switch
        {
            CallEndReason.Busy => $"{who} occupato",
            CallEndReason.NoAnswer => $"{who} non risponde",
            CallEndReason.Rejected => $"Chiamata rifiutata da {who}",
            CallEndReason.NotFound => "Numero inesistente",
            CallEndReason.DoNotDisturb => $"{who} in non disturbare",
            CallEndReason.NotAuthorized => "Chiamata non consentita",
            CallEndReason.EncryptionUnavailable
                => "Chiamata interrotta: la linea non accetta la voce cifrata, che le impostazioni "
                   + "richiedono. Cambia la cifratura della voce in Impostazioni, scheda Account SIP",
            CallEndReason.NetworkFailure => "Chiamata caduta per problema di rete",
            CallEndReason.Transferred => "Chiamata trasferita",
            CallEndReason.RemoteHangUp when talkDuration > TimeSpan.Zero
                => $"{who} ha chiuso, durata {SpeechFormatter.Duration(talkDuration)}",
            _ when talkDuration > TimeSpan.Zero
                => $"Chiamata terminata, durata {SpeechFormatter.Duration(talkDuration)}",
            _ => "Chiamata terminata",
        };
    }

    /// <summary>Chiamata uscente in corso.</summary>
    /// <param name="destination">Destinazione da pronunciare.</param>
    public static string Dialing(string destination) => $"Chiamata a {destination}";

    /// <summary>Chiamata messa in attesa.</summary>
    /// <param name="party">
    /// Controparte. Non compare nella frase: vedi il commento qui sotto.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Due parole, e il nome non c'è.</b> Il gesto di mettere in pausa è quello che
    /// l'operatore compie più spesso e sempre di proposito: sa chi ha in linea, l'ha
    /// appena sentito nominare rispondendo, e ripeterglielo a ogni pressione allunga
    /// l'attesa prima di poter fare la cosa successiva — comporre l'interno a cui
    /// passare la chiamata. Qui serve la conferma che il gesto è arrivato, non un
    /// riepilogo.
    /// </para>
    /// <para>
    /// Il nome resta dove serve davvero: nel promemoria periodico di chi è stato
    /// dimenticato in attesa, nell'annuncio dell'alternanza fra due linee — dove
    /// sapere <b>quale</b> delle due è l'informazione — e nelle caselle di riepilogo,
    /// che si possono rileggere quando si vuole.
    /// </para>
    /// </remarks>
    public static string CallHeld(CallParty party) => "Chiamata in pausa";

    /// <summary>Chiamata ripresa dall'attesa.</summary>
    /// <param name="party">Controparte. Non compare nella frase, come in <see cref="CallHeld"/>.</param>
    public static string CallResumed(CallParty party) => "Chiamata ripresa";

    /// <summary>Da quanto tempo una chiamata entrante sta squillando.</summary>
    /// <param name="ringingFor">Tempo trascorso dall'inizio dello squillo.</param>
    /// <remarks>
    /// <para>
    /// Risponde a una domanda che l'operatore si pone davvero, e sotto pressione: una
    /// chiamata che squilla da tre secondi e una che squilla da venticinque non
    /// chiedono la stessa cosa — la seconda dice che il chiamante sta per rinunciare,
    /// o che il centralino sta per ritirarla.
    /// </para>
    /// <para>
    /// Il verbo viene prima del numero perché è il verbo a dire di <b>cosa</b> si
    /// parla: chiedendo la durata con una conversazione in corso e una chiamata che
    /// squilla si sentono due tempi di fila, e senza le parole non si saprebbe quale
    /// è quale.
    /// </para>
    /// </remarks>
    public static string RingingFor(TimeSpan ringingFor)
        => $"Squilla da {SpeechFormatter.Duration(ringingFor)}";

    /// <summary>Da quanto tempo si sta parlando, contato dalla risposta.</summary>
    /// <param name="talkingFor">Durata della conversazione.</param>
    /// <remarks>
    /// <para>
    /// <b>Il verbo davanti al numero non è ornamento.</b> Lo stesso comando risponde
    /// «da quanto» su tre cose diverse — lo squillo, la conversazione, il turno — e
    /// dicendo il solo numero l'operatore sentirebbe «due minuti e dieci» senza sapere
    /// di che cosa. Prima erano tre situazioni che non si presentavano mai insieme;
    /// con lo squillo che ha la precedenza possono arrivare due tempi di fila, e a
    /// quel punto le parole sono l'unica cosa che li distingue.
    /// </para>
    /// <para>
    /// Si conta dalla risposta e non dall'inizio della chiamata: è il tempo che
    /// l'operatore riferisce a chi gli chiede quanto è durata, ed è quello che finisce
    /// nell'elenco delle chiamate.
    /// </para>
    /// </remarks>
    public static string InCallFor(TimeSpan talkingFor)
        => $"In linea da {SpeechFormatter.Duration(talkingFor)}";

    /// <summary>Promemoria di chiamata dimenticata in attesa.</summary>
    /// <param name="party">Controparte.</param>
    /// <param name="holdDuration">Da quanto tempo è in attesa.</param>
    public static string HoldReminder(CallParty party, TimeSpan holdDuration)
        => $"{party.SpeechName} in attesa da {SpeechFormatter.Duration(holdDuration)}";

    /// <summary>Alternanza fra due chiamate.</summary>
    /// <param name="nowTalkingTo">Con chi si sta parlando adesso.</param>
    /// <param name="nowOnHold">Chi resta in attesa.</param>
    public static string Swapped(CallParty nowTalkingTo, CallParty? nowOnHold)
        => nowOnHold is null
            ? $"In linea con {nowTalkingTo.SpeechName}"
            : $"In linea con {nowTalkingTo.SpeechName}. {nowOnHold.SpeechName} in attesa";

    /// <summary>
    /// Alternanza fra il chiamante e la destinazione durante un trasferimento con
    /// annuncio.
    /// </summary>
    /// <param name="nowTalkingTo">Con chi si sta parlando adesso.</param>
    /// <param name="nowOnHold">Chi resta in attesa.</param>
    /// <param name="transferKeyName">Nome pronunciabile del tasto di trasferimento.</param>
    /// <remarks>
    /// Ricalca <see cref="Swapped"/> e aggiunge una cosa sola: il tasto che completa.
    /// Serve perché qui l'alternanza non è uno stato di riposo ma un passaggio dentro
    /// una procedura aperta, e l'operatore che torna dal chiamante deve sapere che il
    /// trasferimento è ancora lì e come chiuderlo — altrimenti la via che trova è
    /// annullare e ricominciare.
    /// </remarks>
    public static string SwappedDuringTransfer(
        CallParty nowTalkingTo,
        CallParty nowOnHold,
        string transferKeyName)
        => $"In linea con {nowTalkingTo.SpeechName}. {nowOnHold.SpeechName} in attesa. "
           + $"{transferKeyName} completa il trasferimento";

    /// <summary>
    /// L'alternanza è stata chiesta mentre la destinazione sta ancora squillando.
    /// </summary>
    /// <param name="destination">Destinazione del trasferimento, già pronunciabile.</param>
    /// <remarks>
    /// Non c'è ancora una seconda conversazione da alternare: c'è un telefono che
    /// squilla. Dirlo per esteso evita l'unica lettura sbagliata possibile — che il
    /// comando non abbia funzionato — e ricorda la via d'uscita, perché è il momento
    /// in cui l'operatore sta decidendo se aspettare ancora.
    /// </remarks>
    public static string SwapWhileDestinationRinging(string destination)
        => $"{destination} sta ancora squillando: non c'è una seconda linea da alternare. "
           + "ESC torna al chiamante";

    /// <summary>
    /// L'alternanza è stata chiesta mentre si sta ancora scegliendo la destinazione.
    /// </summary>
    /// <remarks>
    /// Qui non è stato chiamato nessuno: la seconda linea non esiste proprio. Dire il
    /// tasto che riporta al chiamante è l'unica parte utile della frase, perché è la
    /// cosa che l'operatore stava cercando di fare.
    /// </remarks>
    public const string SwapBeforeConsultation =
        "Non hai ancora chiamato nessuno: non c'è una seconda linea da alternare. "
        + "ESC torna al chiamante e annulla il trasferimento";

    // ---------------------------------------------------------------------
    // Conferenza a tre
    // ---------------------------------------------------------------------

    /// <summary>Conferenza aperta fra l'operatore e due interlocutori.</summary>
    /// <param name="first">Primo partecipante.</param>
    /// <param name="second">Secondo partecipante.</param>
    /// <param name="conferenceKeyName">Nome pronunciabile del tasto di conferenza configurato.</param>
    /// <remarks>
    /// <para>
    /// I nomi vengono prima delle istruzioni, e questo è l'ordine che conta: la
    /// domanda a cui l'operatore ha bisogno di risposta immediata è <i>chi sente
    /// adesso chi</i>. Una conferenza aperta sulla coppia sbagliata va sciolta subito,
    /// e per accorgersene serve sentire i due nomi.
    /// </para>
    /// <para>
    /// Le istruzioni ci sono comunque, perché entrare in conferenza cambia il
    /// significato di due tasti: quello di conferenza scioglie invece di aprire, e
    /// quello di riaggancio chiude <b>entrambe</b> le linee anziché una. Il secondo è
    /// il motivo per cui l'annuncio non può limitarsi ai nomi.
    /// </para>
    /// </remarks>
    public static string ConferenceStarted(CallParty first, CallParty second, string conferenceKeyName)
        => $"Conferenza a tre con {first.SpeechName} e {second.SpeechName}. "
           + $"{conferenceKeyName} scioglie la conferenza, riagganciare chiude entrambe le linee";

    /// <summary>Conferenza sciolta su richiesta dell'operatore.</summary>
    /// <param name="nowTalkingTo">Con chi resta la conversazione.</param>
    /// <param name="nowOnHold">Chi passa in attesa.</param>
    /// <remarks>
    /// Ricalca deliberatamente <see cref="Swapped"/>: lo stato in cui si finisce è lo
    /// stesso — uno in linea e uno in attesa — e usare parole diverse per lo stesso
    /// esito costringerebbe a impararle due volte.
    /// </remarks>
    public static string ConferenceEnded(CallParty nowTalkingTo, CallParty? nowOnHold)
        => nowOnHold is null
            ? $"Conferenza chiusa. In linea con {nowTalkingTo.SpeechName}"
            : $"Conferenza chiusa. In linea con {nowTalkingTo.SpeechName}, "
              + $"{nowOnHold.SpeechName} in attesa";

    /// <summary>Un partecipante ha riagganciato e la conferenza si è sciolta da sola.</summary>
    /// <param name="whoLeft">Chi ha chiuso.</param>
    /// <param name="whoRemains">Chi resta in linea con l'operatore.</param>
    /// <remarks>
    /// Le due informazioni stanno nella stessa frase perché sono una sola domanda: se
    /// si dicesse soltanto chi ha chiuso, l'operatore non saprebbe se sta ancora
    /// parlando con qualcuno o con nessuno, e su una linea silenziosa non ha modo di
    /// scoprirlo se non parlando nel vuoto.
    /// </remarks>
    public static string ConferenceParticipantLeft(CallParty whoLeft, CallParty whoRemains)
        => $"{whoLeft.SpeechName} ha chiuso. Conferenza terminata, in linea con {whoRemains.SpeechName}";

    /// <summary>Conferenza chiusa dall'operatore su entrambe le linee.</summary>
    /// <param name="participants">Partecipanti congedati.</param>
    public static string ConferenceHungUp(IReadOnlyList<CallParty> participants)
        => participants.Count == 0
            ? "Conferenza chiusa"
            : $"Conferenza chiusa con {string.Join(" e ", participants.Select(p => p.SpeechName))}";

    /// <summary>
    /// Rifiuto del comando di conferenza per mancanza di una seconda linea.
    /// </summary>
    /// <param name="holdKeyName">Tasto con cui mettere in attesa quello in linea.</param>
    /// <param name="dialerKeyName">Tasto con cui raggiungere il campo di composizione.</param>
    /// <remarks>
    /// Non basta dire che il comando non è disponibile: la conferenza è l'unico
    /// comando del prodotto che richiede una condizione che l'operatore deve
    /// costruirsi prima, e senza dirgli come si costruisce il rifiuto sembra un
    /// guasto. Sono i due gesti che mancano, nell'ordine in cui vanno fatti.
    /// </remarks>
    public static string ConferenceNeedsSecondCall(string holdKeyName, string dialerKeyName)
        => "La conferenza richiede due chiamate. "
           + $"Metti in attesa con {holdKeyName}, chiama il secondo interlocutore con {dialerKeyName}, poi riprova";

    /// <summary>Conferenza messa in attesa: entrambi i partecipanti sono parcheggiati.</summary>
    /// <param name="conferenceKeyName">Tasto con cui riportarla in linea.</param>
    /// <remarks>
    /// L'attesa su una conferenza parcheggia <b>tutti</b>, e va detto: mettere in
    /// attesa una linea sola lascerebbe l'altra a parlare da sola senza che nessuno
    /// glielo dica.
    /// </remarks>
    public static string ConferenceParked(string conferenceKeyName)
        => $"Conferenza in attesa. {conferenceKeyName} la riporta in linea";

    /// <summary>Il motore telefonico in uso non sa unire due chiamate.</summary>
    /// <remarks>
    /// Il caso reale è la mancanza di un percorso audio: senza dispositivi la
    /// conferenza si aprirebbe come segnalazione e resterebbe muta, che è
    /// esattamente il tipo di comando a metà che questo prodotto non vuole avere.
    /// </remarks>
    public static string ConferenceNotSupported
        => "Conferenza non disponibile su questa configurazione audio";

    // ---------------------------------------------------------------------
    // Trasferimento
    // ---------------------------------------------------------------------

    /// <summary>
    /// Ingresso nella modalità trasferimento. Dice cosa fanno i tasti, non solo
    /// che la modalità è iniziata.
    /// </summary>
    /// <param name="transferKeyName">Nome pronunciabile del tasto di trasferimento configurato.</param>
    /// <remarks>
    /// L'ordine delle istruzioni segue la frequenza d'uso, non la completezza:
    /// prima il gesto che si fa quasi sempre, poi quello che si fa quando serve
    /// annunciare la chiamata, infine la via d'uscita. La doppia pressione del tasto
    /// di trasferimento continua a funzionare ma non viene più elencata: Invio fa la
    /// stessa cosa senza dipendere dalla velocità con cui si preme, e un elenco di
    /// quattro possibilità diventa un monologo da ascoltare a ogni trasferimento.
    /// </remarks>
    public static string TransferStarted(string transferKeyName)
        => $"Trasferimento. Digita numero o nome. INVIO trasferisce subito, "
           + $"{transferKeyName} chiama prima l'interno, ESC annulla";

    // TOLTO: l'annuncio "Trasferimento a ... INVIO trasferisce subito, F9 chiama
    // prima l'interno, ESC annulla".
    //
    // Descriveva una pressione che non esiste piu'. Con il numero gia' nel campo il
    // tasto di trasferimento chiama subito l'interno invece di fermarsi a farsi
    // confermare la destinazione: fra la digitazione e quella conferma non
    // succedeva niente che potesse far cambiare idea all'operatore, e in centralino
    // la pressione in piu' si pagava su ogni chiamata del turno.
    //
    // La rilettura cifra per cifra non si e' persa: la fa ConsultationStarted qui
    // sotto, mentre l'interno squilla. E resta il tempo di rimediare, perche' fino
    // alla pressione che completa il trasferimento e' l'operatore a parlare con
    // l'interno e il chiamante e' ancora suo.

    /// <summary>Avvio della consultazione verso la destinazione.</summary>
    /// <param name="destination">Destinazione.</param>
    /// <remarks>
    /// <b>È diventato la rilettura di verifica</b>, e non solo l'avviso che l'interno
    /// squilla: quando il numero era già scritto nel campo, questo è il primo momento
    /// in cui l'operatore risente le cifre che ha digitato sotto dettatura.
    /// </remarks>
    public static string ConsultationStarted(string destination) => $"Chiamata a {destination}, in corso";

    /// <summary>La destinazione della consultazione ha risposto.</summary>
    /// <param name="destination">Destinazione.</param>
    /// <param name="transferKeyName">Nome del tasto che completa il trasferimento.</param>
    public static string ConsultationAnswered(string destination, string transferKeyName)
        => $"{destination} ha risposto. {transferKeyName} per completare il trasferimento";

    /// <summary>Trasferimento cieco inviato al centralino.</summary>
    /// <param name="destination">
    /// Destinazione già pronunciabile: il nome di rubrica oppure le cifre, mai il numero
    /// intero, che la sintesi leggerebbe come una quantità.
    /// </param>
    public static string BlindTransferSent(string destination)
        => $"Trasferimento cieco a {destination} inviato";

    /// <summary>Trasferimento completato con successo.</summary>
    /// <param name="caller">Chiamante trasferito.</param>
    /// <param name="destination">
    /// Destinazione già pronunciabile: il nome di rubrica, oppure l'interno cifra per
    /// cifra. Lasciato intero, «4021» si sentiva «quattromilaventuno», e fuori rubrica
    /// l'operatore non riconosceva l'interno a cui aveva appena passato il chiamante.
    /// </param>
    /// <remarks>
    /// Se il chiamante non è identificabile con certezza, chi chiama passa
    /// <c>null</c> e il messaggio resta generico. È la regola di degrado sicuro:
    /// meglio un annuncio meno informativo che uno riferito alla chiamata sbagliata.
    /// </remarks>
    public static string TransferCompleted(string? caller, string destination)
        => caller is null
            ? $"Trasferimento a {destination} completato"
            : $"{caller} trasferito a {destination}";

    /// <summary>Trasferimento fallito, con recupero del chiamante.</summary>
    /// <param name="outcome">Motivo del fallimento.</param>
    /// <param name="destination">Destinazione tentata.</param>
    /// <param name="recoveredCaller">Chiamante ripreso in linea.</param>
    public static string TransferFailed(TransferOutcome outcome, string destination, CallParty? recoveredCaller)
    {
        var cause = outcome switch
        {
            TransferOutcome.DestinationBusy => $"{destination} occupato",
            TransferOutcome.DestinationNoAnswer => $"{destination} non risponde",
            TransferOutcome.DestinationRejected => $"{destination} ha rifiutato",
            TransferOutcome.RejectedByPbx => "Trasferimento rifiutato dal centralino",
            TransferOutcome.CancelledByOperator => "Trasferimento annullato",
            TransferOutcome.OriginalCallerHungUp => "Il chiamante ha riagganciato",
            _ => "Trasferimento non riuscito",
        };

        if (outcome == TransferOutcome.OriginalCallerHungUp) return cause;

        return recoveredCaller is null
            ? cause
            : $"{cause}. Ripresa chiamata con {recoveredCaller.SpeechName}";
    }

    /// <summary>Avviso preventivo su una destinazione non disponibile.</summary>
    /// <param name="destination">Destinazione.</param>
    /// <param name="presence">Stato di presenza rilevato.</param>
    /// <param name="transferKeyName">Tasto per procedere comunque.</param>
    /// <remarks>
    /// <para>
    /// <b>«Fuori servizio» e non «non registrato»</b>, ed è una correzione nata da una
    /// segnalazione. «Telefono non registrato» è la frase con cui la postazione annuncia
    /// di <i>aver perso lei</i> la registrazione al centralino, cioè un guasto che
    /// impedisce di telefonare: sentirsi dire «non registrato» durante un trasferimento
    /// la faceva leggere come quella, e la segnalazione arrivava come «il software dice
    /// che il telefono non è registrato mentre invece funziona».
    /// </para>
    /// <para>
    /// La parola è anche la stessa che la colonna dello stato usa nell'elenco dei
    /// contatti, <see cref="Services.PresenceMonitorService.Describe"/>: uno stato solo
    /// deve avere un nome solo, altrimenti chi ascolta crede che siano due.
    /// </para>
    /// </remarks>
    public static string TransferDestinationWarning(string destination, PresenceState presence, string transferKeyName)
    {
        var state = presence switch
        {
            PresenceState.Busy => "occupato",
            PresenceState.DoNotDisturb => "in non disturbare",
            PresenceState.Offline => "fuori servizio",
            PresenceState.Away => "assente",
            _ => "non disponibile",
        };

        return $"Attenzione: {destination} {state}. {transferKeyName} per procedere comunque";
    }

    /// <summary>Richiesta di conferma per un trasferimento verso un numero esterno.</summary>
    /// <param name="destination">Destinazione, letta cifra per cifra.</param>
    /// <param name="transferKeyName">Tasto di conferma.</param>
    public static string ExternalTransferConfirmation(string destination, string transferKeyName)
        => $"Destinazione esterna {destination}. {transferKeyName} per confermare, ESC per annullare";

    /// <summary>Fallback dal trasferimento cieco alla consultazione.</summary>
    public static string BlindTransferNotSupported
        => "Trasferimento cieco non supportato dal centralino, avvio consultazione";

    /// <summary>
    /// Ricorda qual è il tasto che svincola il chiamante, nella modalità semplificata.
    /// </summary>
    /// <param name="releaseKeyName">Nome parlato del tasto che svincola.</param>
    /// <remarks>
    /// <para>
    /// Si pronuncia quando l'operatore preme, con l'interno già in linea, il tasto che
    /// fino alla versione precedente completava il trasferimento. È l'unico punto in
    /// cui la modalità semplificata risponde a un tasto insegnandone un altro, e la
    /// ragione è che quel gesto era corretto fino a ieri: il segnale di comando
    /// rifiutato, da solo, si interpreta come un guasto del programma.
    /// </para>
    /// <para>
    /// La frase dice <b>anche l'alternativa</b>, perché è il momento in cui serve: se
    /// l'operatore ha appena sentito l'interno occupato, il tasto che gli serve non è
    /// quello che svincola ma quello che chiude e riporta il chiamante.
    /// </para>
    /// </remarks>
    public static string ReleaseKeyReminder(string releaseKeyName)
        => $"L'interno e' gia' in linea. {releaseKeyName} per passargli la chiamata, "
           + "diviso per riprendere il chiamante";

    // ---------------------------------------------------------------------
    // Coda
    // ---------------------------------------------------------------------

    /// <summary>Risposta al tasto rapido di conteggio della coda.</summary>
    /// <param name="snapshot">Fotografia corrente.</param>
    /// <param name="now">Istante corrente.</param>
    public static string QueueCount(QueueSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.WaitingCount == 0) return "Nessuna chiamata in coda";

        var count = SpeechFormatter.Count(snapshot.WaitingCount, "chiamata in coda", "chiamate in coda");
        var longest = snapshot.LongestWait(now);

        return longest < TimeSpan.FromSeconds(5)
            ? count
            : $"{count}, attesa massima {SpeechFormatter.Duration(longest)}";
    }

    /// <summary>Annuncio automatico di variazione della coda.</summary>
    /// <param name="waitingCount">Chiamate in attesa dopo la variazione.</param>
    public static string QueueChanged(int waitingCount)
        => waitingCount == 0
            ? "Coda vuota"
            : $"Nuova chiamata in coda, totale {waitingCount}";

    /// <summary>Voce dell'elenco dei chiamanti in coda.</summary>
    /// <param name="caller">Chiamante.</param>
    /// <param name="now">Istante corrente.</param>
    /// <param name="grouping">Raggruppamento per la lettura del numero.</param>
    /// <param name="includePosition">Se includere la posizione in coda.</param>
    public static string QueuedCallerLine(
        QueuedCaller caller,
        DateTimeOffset now,
        DigitGrouping grouping,
        bool includePosition)
    {
        var who = SpeechFormatter.Party(caller.CallerName, caller.CallerNumber, grouping);
        var wait = SpeechFormatter.Duration(caller.WaitDuration(now));

        var prefix = includePosition && caller.Position is { } pos ? $"{pos}. " : string.Empty;
        var flags = caller switch
        {
            { IsEmergency: true } => "emergenza, ",
            { IsVip: true } => "VIP, ",
            _ => string.Empty,
        };

        return $"{prefix}{flags}{who}, in attesa da {wait}";
    }

    /// <summary>Superamento della soglia di attesa.</summary>
    /// <param name="threshold">Soglia superata.</param>
    public static string QueueWaitThresholdExceeded(TimeSpan threshold)
        => $"Attesa oltre {SpeechFormatter.Duration(threshold)}";

    /// <summary>Il dettaglio della coda non è disponibile.</summary>
    public static string QueueDetailUnavailable
        => "Dettaglio coda non disponibile, collegamento al centralino interrotto";

    /// <summary>Il collegamento al centralino è disattivato nelle impostazioni.</summary>
    /// <remarks>
    /// Distinto da <see cref="QueueDetailUnavailable"/> perché descrive una scelta
    /// e non un guasto. La differenza conta: la prima frase invita a cercare un
    /// problema che non esiste, e ripetuta a ogni pressione del tasto diventa un
    /// ostacolo invece di un'informazione.
    /// </remarks>
    public static string QueueLinkDisabled
        => "Collegamento al centralino non attivo. Le chiamate in coda non sono visibili";

    /// <summary>Il dettaglio della coda è tornato disponibile.</summary>
    public static string QueueDetailRestored => "Collegamento al centralino ripristinato";

    // ---------------------------------------------------------------------
    // Stato agente
    // ---------------------------------------------------------------------

    /// <summary>Cambio di stato dell'agente.</summary>
    /// <param name="state">Nuovo stato.</param>
    public static string AgentStateChanged(AgentState state) => state.Status switch
    {
        AgentStatus.LoggedOut => "Disconnesso dalla coda",
        AgentStatus.Available => "Connesso alla coda, disponibile",
        AgentStatus.Paused when state.WasAutomatic
            => $"Attenzione: messo in pausa dal centralino{FormatReason(state)}. Non ricevi piu' chiamate",
        AgentStatus.Paused => $"In pausa{FormatReason(state)}",
        AgentStatus.WrapUp => "Lavorazione post chiamata",
        AgentStatus.Busy => "Occupato",
        _ => "Stato agente aggiornato",
    };

    private static string FormatReason(AgentState state)
        => string.IsNullOrWhiteSpace(state.ReasonLabel) ? string.Empty : $", {state.ReasonLabel}";

    // ---------------------------------------------------------------------
    // Registrazione della conversazione
    // ---------------------------------------------------------------------

    /// <summary>
    /// Avviso che l'<b>interlocutore</b> sente quando la registrazione comincia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// È rivolto a chi è in linea, non all'operatore, e questo governa ogni scelta:
    /// frase brevissima, nessun riferimento a tasti o a BlindPhone, nessuna parola che
    /// presupponga di sapere come funziona il centralino. Chi la sente sta parlando con
    /// un ufficio, non con un programma.
    /// </para>
    /// <para>
    /// È anche l'adempimento che rende lecita la registrazione: l'interessato va
    /// informato prima che la raccolta cominci, e questa frase è quell'informazione.
    /// </para>
    /// </remarks>
    public const string RecordingStartedForRemote = "Avvio della registrazione";

    /// <summary>
    /// Avviso che l'<b>interlocutore</b> sente quando la registrazione finisce.
    /// </summary>
    /// <param name="duration">Durata registrata.</param>
    /// <remarks>
    /// La durata si dice perché è la sola cosa che permette a chi era in linea di
    /// sapere <b>quanto</b> è stato registrato: senza, l'informazione «si stava
    /// registrando» resta senza confini.
    /// </remarks>
    public static string RecordingStoppedForRemote(TimeSpan duration)
        => $"Registrazione interrotta, {SpeechFormatter.Duration(duration)}";

    /// <summary>Riscontro all'operatore all'avvio della registrazione.</summary>
    /// <param name="announcedToRemote">Se l'interlocutore è stato avvisato.</param>
    /// <param name="recordingKeyName">Combinazione che ferma la registrazione.</param>
    /// <remarks>
    /// Dice <b>se l'interlocutore lo sa</b>, e non è un dettaglio: con l'avviso
    /// disattivato la responsabilità di informarlo passa all'operatore, che deve
    /// saperlo nel momento in cui la registrazione parte e non dopo.
    /// </remarks>
    public static string RecordingStartedForOperator(bool announcedToRemote, string recordingKeyName)
        => announcedToRemote
            ? $"Registrazione avviata, l'interlocutore e' stato avvisato. {recordingKeyName} per fermarla"
            : "Registrazione avviata SENZA avviso all'interlocutore: informalo tu. "
              + $"{recordingKeyName} per fermarla";

    /// <summary>Riscontro all'operatore alla chiusura della registrazione.</summary>
    /// <param name="duration">Durata registrata.</param>
    /// <param name="fileName">Nome del file prodotto.</param>
    public static string RecordingStoppedForOperator(TimeSpan duration, string fileName)
        => $"Registrazione interrotta, {SpeechFormatter.Duration(duration)}. Salvata come {fileName}";

    /// <summary>Rifiuto del comando di registrazione quando non c'è nulla da registrare.</summary>
    public const string RecordingNeedsCall
        = "Nessuna conversazione in corso: la registrazione si avvia mentre si parla con qualcuno";

    // ---------------------------------------------------------------------
    // Turno e pausa della postazione
    // ---------------------------------------------------------------------

    /// <summary>Da quanto la postazione è in servizio.</summary>
    /// <param name="inService">Tempo passato alla postazione, al netto delle pause.</param>
    /// <param name="totalPaused">Tempo complessivo passato in pausa.</param>
    /// <remarks>
    /// Il totale delle pause si dice solo se c'è stato: aggiungere «e nessuna pausa» a
    /// ogni interrogazione allungherebbe una risposta che si chiede proprio per averla
    /// corta.
    /// </remarks>
    public static string ServiceTime(TimeSpan inService, TimeSpan totalPaused)
    {
        var testo = $"In servizio da {SpeechFormatter.Duration(inService)}";

        return totalPaused < TimeSpan.FromSeconds(30)
            ? testo
            : $"{testo}, piu' {SpeechFormatter.Duration(totalPaused)} di pausa";
    }

    /// <summary>Ingresso in pausa: la postazione smette di ricevere chiamate.</summary>
    /// <param name="pauseKeyName">Combinazione che riporta in servizio.</param>
    /// <remarks>
    /// <para>
    /// Si dice <b>che cosa succede alle chiamate</b>, non solo che la pausa è attiva.
    /// È l'unico modo per sapere che il chiamante non sentirà squillare a vuoto ma
    /// verrà passato subito a un collega: senza, un operatore prudente resterebbe alla
    /// postazione a controllare, cioè non andrebbe in pausa.
    /// </para>
    /// <para>
    /// La via di uscita si nomina subito. È il momento in cui serve saperla, non
    /// quello in cui è comodo dirla, e per giunta è la sola combinazione che continuerà
    /// a rispondere sul campo di composizione.
    /// </para>
    /// </remarks>
    public static string ServicePauseStarted(string pauseKeyName)
        => "Postazione fuori servizio. Le chiamate entranti vengono rifiutate e passate a un collega. "
           + $"Il campo di composizione e' bloccato. Premi {pauseKeyName} per tornare in servizio";

    /// <summary>Rientro in servizio, con la durata della pausa appena conclusa.</summary>
    /// <param name="pauseDuration">Quanto è durata la pausa.</param>
    public static string ServicePauseEnded(TimeSpan pauseDuration)
        => $"Postazione in servizio. Pausa durata {SpeechFormatter.Duration(pauseDuration)}. "
           + "Le chiamate entranti tornano ad arrivare";

    /// <summary>
    /// Risposta a chi prova a digitare con la postazione in pausa.
    /// </summary>
    /// <param name="pauseDuration">Da quanto dura la pausa.</param>
    /// <param name="pauseKeyName">Combinazione che riporta in servizio.</param>
    public static string TypingWhilePaused(TimeSpan pauseDuration, string pauseKeyName)
        => $"Postazione in pausa da {SpeechFormatter.Duration(pauseDuration)}. "
           + $"Premi {pauseKeyName} per tornare in servizio";

    // ---------------------------------------------------------------------
    // Segreteria telefonica
    // ---------------------------------------------------------------------

    /// <summary>Messaggi nuovi arrivati in segreteria.</summary>
    /// <param name="summary">Stato della casella riportato dal centralino.</param>
    /// <remarks>
    /// <para>
    /// Il numero viene per primo, perché è l'informazione che decide se interrompere
    /// quello che si sta facendo: «2 nuovi messaggi in segreteria» si capisce anche
    /// fermando la voce alla terza parola.
    /// </para>
    /// <para>
    /// Quando il centralino accende la spia senza dire quanti sono, si dice che ci sono
    /// e basta: un numero inventato sarebbe peggio di nessun numero.
    /// </para>
    /// </remarks>
    public static string VoicemailArrived(MessageSummary summary)
    {
        if (summary.NewToAnnounce is not { } nuovi) return "Nuovi messaggi in segreteria";
        if (nuovi == 0) return "Nessun messaggio nuovo in segreteria";

        var (messaggi, urgenti) = NewMessagesPhrase(nuovi, summary.UrgentToAnnounce);
        return $"{messaggi} in segreteria{urgenti}";
    }

    /// <summary>«2 nuovi messaggi» e, a parte, «, di cui 1 urgente».</summary>
    /// <remarks>
    /// Le due parti restano separate perché il luogo va fra l'una e l'altra:
    /// «2 nuovi messaggi in segreteria, di cui 1 urgente», non «di cui 1 urgente in
    /// segreteria».
    /// </remarks>
    private static (string Messages, string Urgent) NewMessagesPhrase(int nuovi, int urgenti)
    {
        // Tutti urgenti: lo si dice nel sostantivo, invece di «2 nuovi messaggi, di
        // cui 2 urgenti», che costringe ad ascoltare il numero due volte.
        if (urgenti == nuovi)
        {
            return (nuovi == 1 ? "1 nuovo messaggio urgente" : $"{nuovi} nuovi messaggi urgenti", string.Empty);
        }

        var messaggi = nuovi == 1 ? "1 nuovo messaggio" : $"{nuovi} nuovi messaggi";

        return urgenti > 0
            ? (messaggi, $", di cui {SpeechFormatter.Count(urgenti, "urgente", "urgenti")}")
            : (messaggi, string.Empty);
    }

    /// <summary>
    /// Riga di stato della segreteria, per la finestra principale.
    /// </summary>
    /// <param name="summary">Stato della casella riportato dal centralino.</param>
    /// <remarks>
    /// Comincia sempre con «Segreteria», perché si legge con il cursore virtuale fuori
    /// da ogni contesto: «nessun messaggio nuovo» da solo non direbbe di che cosa.
    /// </remarks>
    public static string VoicemailStatus(MessageSummary summary)
    {
        var vecchi = summary.OldMessages is > 0 ? summary.OldMessages.Value : 0;
        var giaAscoltati = vecchi == 0
            ? string.Empty
            : $". {SpeechFormatter.Count(vecchi, "messaggio già ascoltato", "messaggi già ascoltati")}";

        if (summary.NewToAnnounce is not { } nuovi) return $"Segreteria: nuovi messaggi da ascoltare{giaAscoltati}";
        if (nuovi == 0) return $"Segreteria: nessun messaggio nuovo{giaAscoltati}";

        var (messaggi, urgenti) = NewMessagesPhrase(nuovi, summary.UrgentToAnnounce);
        return $"Segreteria: {messaggi}{urgenti}{giaAscoltati}";
    }

    /// <summary>
    /// Indicazione breve per il testo dell'icona nell'area di notifica.
    /// </summary>
    /// <param name="summary">Stato della casella, se il centralino l'ha riportato.</param>
    /// <returns>Il testo, oppure <c>null</c> se non c'è niente da ascoltare.</returns>
    /// <remarks>
    /// Breve per necessità: Windows tronca il testo dell'icona a 63 caratteri, e deve
    /// starci accanto lo stato della registrazione.
    /// </remarks>
    public static string? VoicemailTrayHint(MessageSummary? summary) => summary?.NewToAnnounce switch
    {
        null when summary is not null => "messaggi in segreteria",
        null or 0 => null,
        var nuovi => SpeechFormatter.Count(nuovi.Value, "messaggio in segreteria", "messaggi in segreteria"),
    };

    /// <summary>Il comando della segreteria non sa che numero chiamare.</summary>
    /// <param name="proposal">Numero indicato dal centralino, se ce n'è uno componibile.</param>
    /// <remarks>
    /// Dice dove si imposta, e se il centralino ha suggerito qualcosa lo dice: è la
    /// risposta che risparmia una telefonata all'amministratore del centralino.
    /// </remarks>
    public static string VoicemailNumberMissing(string? proposal) => proposal is null
        ? "Numero della segreteria non impostato. Si scrive in Impostazioni, scheda Account SIP."
        : $"Numero della segreteria non impostato. Il centralino indica {SpeechFormatter.Digits(proposal)}: "
          + "per usarlo confermalo in Impostazioni, scheda Account SIP.";

    // ---------------------------------------------------------------------
    // Sistema
    // ---------------------------------------------------------------------

    /// <summary>Cambio di stato della registrazione SIP.</summary>
    /// <param name="state">Nuovo stato.</param>
    public static string RegistrationChanged(SipRegistrationState state) => state.Status switch
    {
        RegistrationStatus.Registered => "Telefono registrato",
        RegistrationStatus.Registering => "Registrazione in corso",
        RegistrationStatus.Failed when state.FailureReason is { Length: > 0 }
            => $"Attenzione: telefono non registrato. {state.FailureReason}",
        RegistrationStatus.Failed => "Attenzione: telefono non registrato",
        _ => "Telefono disconnesso",
    };

    /// <summary>Cambio di dispositivo audio a caldo.</summary>
    /// <param name="args">Dati del cambio.</param>
    public static string AudioDeviceChanged(Abstractions.AudioDeviceChangedEventArgs args)
        => args.WasUnplugged
            ? $"{args.PreviousDeviceName} scollegato, audio su {args.NewDeviceName}"
            : $"Audio su {args.NewDeviceName}";

    // ---------------------------------------------------------------------
    // Pulsanti della cuffia
    // ---------------------------------------------------------------------

    /// <summary>Una cuffia con i pulsanti telefonici è stata collegata.</summary>
    /// <param name="name">Nome della cuffia, come lo dichiara il produttore.</param>
    /// <param name="canAnswer">La cuffia ha il pulsante con cui rispondere e chiudere.</param>
    /// <remarks>
    /// Dice che cosa si può fare adesso, non solo che cosa è successo: chi ha appena
    /// infilato la cuffia vuole sapere se il pulsante risponde. Una cuffia con il solo
    /// tasto del microfono lo dice, perché premerla mentre squilla non farebbe niente.
    /// </remarks>
    public static string HeadsetConnected(string name, bool canAnswer)
        => canAnswer
            ? $"Cuffia collegata: {name}. Il pulsante della cuffia risponde e chiude le chiamate"
            : $"Cuffia collegata: {name}. Dalla cuffia funziona il tasto del microfono, per rispondere usa la tastiera";

    /// <summary>La cuffia con i pulsanti telefonici è stata scollegata.</summary>
    /// <param name="name">Nome della cuffia.</param>
    /// <remarks>
    /// Il rimedio sta nella stessa frase. Una cuffia staccata durante una chiamata
    /// lascia l'operatore con un pulsante che non c'è più, e il tasto che lo sostituisce
    /// è quello che gli serve nel giro di pochi secondi.
    /// </remarks>
    public static string HeadsetDisconnected(string name)
        => $"Cuffia scollegata: {name}. Per rispondere e chiudere usa la tastiera";

    /// <summary>
    /// Il tasto Play e pausa non si può ascoltare perché lo ha già preso un altro
    /// programma.
    /// </summary>
    /// <remarks>
    /// Si dice nel momento in cui servirebbe, cioè con il telefono che squilla: è
    /// l'unico modo in cui l'operatore scopre che il ripiego che ha acceso non c'è.
    /// </remarks>
    public static string HeadsetMediaKeyUnavailable
        => "Tasto Play e pausa occupato da un altro programma: rispondi dalla tastiera";

    /// <summary>
    /// La destinazione ha rifiutato la cifratura e la chiamata riparte in chiaro.
    /// </summary>
    /// <param name="destination">Destinazione, già in forma pronunciabile.</param>
    /// <remarks>
    /// <para>
    /// Si dice <b>mentre la chiamata parte</b>, non dopo. È l'unico momento in cui
    /// l'operatore può ancora decidere di non dire al telefono quello che stava per
    /// dire: una volta che l'interlocutore ha risposto, l'informazione arriva tardi.
    /// </para>
    /// <para>
    /// Priorità alta e non normale: sopravvive al silenzio durante la conversazione,
    /// perché una protezione che si spegne in silenzio lascia credere di averla.
    /// </para>
    /// </remarks>
    public static string SrtpDowngraded(string destination)
        => $"Attenzione: {destination} non accetta la voce cifrata. La chiamata riparte in chiaro";

    /// <summary>
    /// La voce dell'operatore parte, quella dell'interlocutore non arriva.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dice le due metà del fatto, nell'ordine in cui servono: prima che cosa succede,
    /// perché l'operatore lo sta vivendo come un silenzio e deve sapere che non lo è;
    /// poi che l'interlocutore lo sente, perché è quello che decide che cosa dirgli.
    /// La causa probabile e il posto dove guardare chiudono la frase, così chi richiama
    /// l'assistenza sa già dove sono i dati.
    /// </para>
    /// <para>
    /// Niente sigle: «NAT» detto da una voce sintetica è una parola che non si capisce.
    /// </para>
    /// </remarks>
    public static string OneWayAudio
        => "Attenzione: non arriva la voce dell'interlocutore. La tua invece parte, e probabilmente "
           + "dall'altra parte ti sentono. Di solito dipende dal router o dal firewall: i dettagli sono "
           + "nella scheda Diagnostica delle impostazioni";

    /// <summary>Comando non valido nello stato corrente.</summary>
    public static string CommandNotAvailable => "Comando non disponibile adesso";

    /// <summary>Riepilogo completo dello stato, risposta al comando di orientamento.</summary>
    /// <param name="activeCall">Chiamata attiva, se presente.</param>
    /// <param name="heldCalls">Chiamate in attesa.</param>
    /// <param name="queue">Stato della coda.</param>
    /// <param name="agent">Stato dell'agente.</param>
    /// <param name="registration">Stato della registrazione.</param>
    /// <param name="now">Istante corrente.</param>
    /// <param name="conferencedCalls">
    /// Partecipanti alla conferenza a tre, se aperta. Ha la precedenza sulla
    /// chiamata attiva: in conferenza la domanda «con chi sono in linea» ha due
    /// risposte, e darne una sola sarebbe la metà sbagliata.
    /// </param>
    /// <param name="voicemail">
    /// Stato della segreteria, se il centralino l'ha riportato. Entra nel riepilogo
    /// solo quando ci sono messaggi nuovi: «nessun messaggio» detto a ogni riepilogo
    /// sarebbe una frase in più da ascoltare per non sapere niente.
    /// </param>
    /// <remarks>
    /// È il comando più usato dopo il conteggio della coda: serve a ricostruire in
    /// una frase il quadro che un operatore vedente ricava con un'occhiata allo
    /// schermo. L'ordine delle informazioni è quello di urgenza decrescente.
    /// </remarks>
    public static string StatusSummary(
        Call? activeCall,
        IReadOnlyList<Call> heldCalls,
        QueueSnapshot queue,
        AgentState agent,
        SipRegistrationState registration,
        DateTimeOffset now,
        IReadOnlyList<Call>? conferencedCalls = null,
        MessageSummary? voicemail = null)
    {
        var parts = new List<string>();

        if (conferencedCalls is { Count: > 0 })
        {
            var who = string.Join(" e ", conferencedCalls.Select(c => c.RemoteParty.SpeechName));
            var since = conferencedCalls.Max(c => c.TalkDuration(now));

            parts.Add($"Conferenza a tre con {who} da {SpeechFormatter.Duration(since)}");
        }
        else if (activeCall is not null)
        {
            parts.Add($"In linea con {activeCall.RemoteParty.SpeechName} da "
                      + SpeechFormatter.Duration(activeCall.TalkDuration(now)));
        }
        else
        {
            parts.Add("Nessuna chiamata attiva");
        }

        if (heldCalls.Count > 0)
        {
            var held = string.Join(", ", heldCalls.Select(c => c.RemoteParty.SpeechName));
            parts.Add($"{SpeechFormatter.Count(heldCalls.Count, "chiamata in attesa", "chiamate in attesa")}: {held}");
        }

        parts.Add(QueueCount(queue, now));

        parts.Add(agent.Status switch
        {
            AgentStatus.Available => "Stato agente: disponibile",
            AgentStatus.Paused => "Stato agente: in pausa",
            AgentStatus.LoggedOut => "Stato agente: disconnesso",
            _ => "Stato agente: occupato",
        });

        if (voicemail is not null && voicemail.NewToAnnounce != 0) parts.Add(VoicemailArrived(voicemail));

        if (!registration.IsReachable) parts.Add("Attenzione: telefono non registrato");

        return string.Join(". ", parts);
    }

    /// <summary>Informazione sulla qualità della linea.</summary>
    /// <param name="call">Chiamata in corso.</param>
    /// <param name="signallingEncrypted">
    /// Se la segnalazione viaggia cifrata, cioè se il trasporto SIP è TLS.
    /// </param>
    /// <remarks>
    /// <para>
    /// L'indicazione della banda larga non è una curiosità tecnica: EN 301 549 §6.1
    /// richiede il supporto della banda larga, e in collaudo va dimostrato sulla
    /// chiamata reale e non solo nella configurazione dei codec.
    /// </para>
    /// <para>
    /// <b>I due strati si dicono separatamente, ed è una correzione.</b> Prima la
    /// frase finiva con «chiamata protetta» o «chiamata non protetta», che sono
    /// affermazioni su tutta la telefonata mentre il dato riguardava il solo audio. Chi
    /// aveva configurato il trasporto TLS e sentiva «chiamata non protetta» concludeva
    /// che il TLS non stesse funzionando, e andava a cercare un guasto che non c'era.
    /// </para>
    /// <para>
    /// Sono due canali diversi e vanno detti come tali: <b>TLS</b> protegge la
    /// segnalazione — chi chiama chi, quando, verso quale interno — e <b>SRTP</b>
    /// protegge la voce. Si può avere l'una senza l'altra, ed è anzi la situazione più
    /// comune. Una frase sola che le riassuma non esiste senza mentire su una delle
    /// due.
    /// </para>
    /// </remarks>
    public static string LineQuality(Call call, bool signallingEncrypted)
    {
        var band = call.IsWideband ? "banda larga" : "banda stretta";
        var codec = string.IsNullOrWhiteSpace(call.NegotiatedCodec) ? string.Empty : $", codec {call.NegotiatedCodec}";

        var voce = call.IsMediaEncrypted ? "voce cifrata" : "voce in chiaro";
        var segnalazione = signallingEncrypted ? "segnalazione cifrata TLS" : "segnalazione in chiaro";

        return $"Audio in {band}{codec}. {voce}, {segnalazione}";
    }
}
