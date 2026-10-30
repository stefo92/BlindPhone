using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>Un argomento della guida in linea.</summary>
/// <param name="Id">Identificativo stabile, usato per aprire la guida sul punto giusto.</param>
/// <param name="Title">Titolo, mostrato nell'elenco degli argomenti e pronunciato.</param>
/// <param name="Text">Testo dell'argomento, già impaginato in righe corte.</param>
public sealed record HelpTopic(string Id, string Title, string Text);

/// <summary>
/// Contenuto della guida in linea.
/// </summary>
/// <remarks>
/// <para>
/// <b>Le combinazioni citate nel testo sono quelle configurate adesso</b>, non quelle
/// predefinite. È il motivo per cui la guida si costruisce a partire dal profilo
/// attivo invece di essere un documento fisso: una guida che dice "premi F9" a chi ha
/// rimappato il trasferimento è peggio di nessuna guida, perché manda a premere
/// qualcosa che non fa nulla mentre un chiamante aspetta.
/// </para>
/// <para>
/// Sta nel core, dove non c'è WPF, per due ragioni. La prima è che il testo si può
/// verificare con dei test: che ogni argomento esista, che nessuno sia vuoto, che le
/// combinazioni citate seguano il profilo. La seconda è che la stessa fonte alimenta
/// sia la finestra della guida sia i documenti esportabili in braille — due testi che
/// dicessero cose diverse sullo stesso prodotto sarebbero peggio di uno solo.
/// </para>
/// <para>
/// <b>Sul tono.</b> Il testo è pensato per essere ascoltato in sequenza, non
/// consultato a colpo d'occhio: una informazione per riga, il verbo prima del
/// dettaglio, nessuna tabella. Chi legge con la sintesi non può saltare con lo sguardo
/// alla parte che gli serve. L'impaginazione in righe corte avviene a runtime, perché
/// i nomi delle combinazioni hanno lunghezza variabile: vedi
/// <see cref="HelpTextLayout"/>.
/// </para>
/// </remarks>
public static class HelpBook
{
    /// <summary>Argomento aperto per primo quando non ne viene indicato uno.</summary>
    public const string DefaultTopicId = "posto-operatore";

    /// <summary>Argomento della rubrica.</summary>
    public const string ContactsTopicId = "rubrica";

    /// <summary>Argomento delle impostazioni.</summary>
    public const string SettingsTopicId = "impostazioni";

    /// <summary>Argomento della composizione del numero.</summary>
    public const string DialerTopicId = "composizione";

    /// <summary>Argomento della coda.</summary>
    public const string QueueTopicId = "coda";

    /// <summary>Argomento dei colleghi in turno.</summary>
    public const string ColleaguesTopicId = "colleghi";

    /// <summary>Argomento dei tasti rapidi.</summary>
    public const string HotkeysTopicId = "tasti-rapidi";

    /// <summary>Argomento dell'elenco delle ultime chiamate.</summary>
    public const string CallHistoryTopicId = "ultime-chiamate";

    /// <summary>Argomento della conferenza a tre.</summary>
    public const string ConferenceTopicId = "conferenza";

    /// <summary>Argomento degli account SIP multipli.</summary>
    public const string AccountsTopicId = "account";

    /// <summary>Argomento della modalità semplificata da tastierino numerico.</summary>
    public const string KeypadTopicId = "tastierino";

    /// <summary>
    /// Costruisce la guida sulle combinazioni attualmente assegnate.
    /// </summary>
    /// <param name="profile">Profilo di scorciatoie attivo.</param>
    /// <param name="generatedAt">Momento della generazione, riportato nella scheda dei tasti.</param>
    public static IReadOnlyList<HelpTopic> Build(HotkeyProfile profile, DateTimeOffset generatedAt) =>
    [
        Topic(DefaultTopicId, "Il posto operatore", PostoOperatore(profile)),
        Topic("chiamata", "Rispondere e gestire una chiamata", Chiamata(profile)),
        Topic(KeypadTopicId, "Il tastierino: la modalita' semplificata", Tastierino(profile)),
        Topic("trasferimento", "Trasferire una chiamata", Trasferimento(profile)),
        Topic(ConferenceTopicId, "Parlare in tre: la conferenza", Conferenza(profile)),
        Topic(DialerTopicId, "Comporre un numero o cercare un nome", Composizione(profile)),
        Topic(QueueTopicId, "La coda e lo stato agente", Coda(profile)),
        Topic(ColleaguesTopicId, "Sapere chi e' in turno", Colleghi(profile)),
        Topic(ContactsTopicId, "La rubrica", Rubrica(profile)),
        Topic(CallHistoryTopicId, "Le ultime chiamate", UltimeChiamate(profile)),
        Topic("riunioni", "Entrare in una riunione Zoom", Riunioni(profile)),
        Topic("stato", "Sapere come sta andando", Stato(profile)),
        Topic("voce", "La voce, i segnali acustici e il braille", Voce(profile)),
        Topic("finestra", "Chiudere, riaprire, secondo piano", Finestra(profile)),
        Topic(HotkeysTopicId, "Cambiare i tasti rapidi", CambiareTasti(profile)),
        Topic(SettingsTopicId, "Impostazioni e prima configurazione", Impostazioni(profile)),
        Topic("problemi", "Quando qualcosa non funziona", Problemi(profile)),
        Topic("lavoro-agile", "Lavorare da casa: router, VPN e voce che non arriva", LavoroAgile(profile)),
        Topic(AccountsTopicId, "Piu' account SIP sulla stessa postazione", Account(profile)),
        Topic("mancante", "Cosa non c'è ancora", Mancante()),

        // L'elenco arriva gia' impaginato dal generatore della scheda dei tasti, che
        // e' lo stesso usato per l'esportazione in braille: si prende com'e' invece di
        // reimpaginarlo, cosi' i due documenti non possono divergere.
        new("elenco-tasti", "Elenco completo delle scorciatoie", HotkeyCardWriter.Build(profile, generatedAt)),
    ];

    private static HelpTopic Topic(string id, string title, string text)
        => new(id, title, HelpTextLayout.Wrap(text));

    /// <summary>
    /// Combinazione assegnata a un comando, in forma pronunciabile.
    /// </summary>
    /// <remarks>
    /// Un comando senza combinazione non viene taciuto: si dice che è libero. Sapere
    /// che una funzione esiste ed è senza tasto è l'informazione che porta a
    /// configurarla; farla sparire dalla guida la renderebbe invisibile.
    /// </remarks>
    private static string K(HotkeyProfile profile, HotkeyCommand command)
        => profile.Get(command, HotkeyScope.Local)?.SpeechText
           ?? profile.Get(command, HotkeyScope.Global)?.SpeechText
           ?? "nessun tasto assegnato";

    private static string G(HotkeyProfile profile, HotkeyCommand command)
        => profile.Get(command, HotkeyScope.Global)?.SpeechText ?? "nessun tasto assegnato";

    private static string PostoOperatore(HotkeyProfile profile) =>
        $"""
        BlindPhone è un posto operatore di centralino pensato per essere usato senza guardare lo schermo. Tutto quello che serve si fa da tastiera, e ogni cosa che succede viene detta a voce.

        Le tre idee da cui discende tutto il resto.

        1. Un tasto, un gesto. I comandi che si usano centinaia di volte al giorno sono tasti singoli: rispondere costa una pressione, non tre dita.

        2. Nessun comando muto. Ogni comando produce un effetto e un riscontro. Se non senti nulla, il comando non è arrivato: non è successo in silenzio.

        3. Il cursore non si sposta da solo. Mentre scrivi una nota, una chiamata in arrivo viene annunciata ma non ti porta via il punto in cui stai scrivendo. L'unica eccezione è la finestra, che torna davanti quando il telefono squilla.

        I quattro tasti da imparare per primi:

        - Rispondi: {K(profile, HotkeyCommand.Answer)}
        - Riaggancia o rifiuta: {K(profile, HotkeyCommand.HangUp)}
        - Trasferisci: {K(profile, HotkeyCommand.Transfer)}
        - Campo di composizione: {K(profile, HotkeyCommand.FocusDialer)}

        Se ti perdi: {K(profile, HotkeyCommand.AnnounceStatusSummary)} legge tutto lo stato in una volta, e {K(profile, HotkeyCommand.AnnounceHotkeyList)} rilegge l'elenco delle scorciatoie.

        Se le scorciatoie ti sembrano troppe, c'è una via più corta: la modalità semplificata governa l'intera chiamata con quattro tasti del tastierino numerico. Ha un argomento suo in questa guida.

        Nella finestra trovi due caselle di sola lettura, affiancate: a sinistra «Collegato a», con nome e numero di chi hai in linea; a destra «In attesa», con nome, numero e da quanto tempo aspetta. Servono soprattutto a chi guarda lo schermo — un collega che ti affianca, chi passa dalla postazione — e fanno per lui quello che le spie luminose fanno su un centralino tradizionale. Sono caselle di testo e non etichette proprio perché il numero che c'è scritto si possa selezionare e copiare. Non parlano da sole: il tempo di attesa cambia ogni secondo e sentirlo ripetere ti coprirebbe la conversazione. Quello che devi sapere continua ad arrivarti dagli annunci.

        Sotto la riga della chiamata in corso c'è una fila di pulsanti, che compaiono solo quando la funzione è disponibile: mentre squilla ci sono «rispondi» e «rifiuta», in conversazione l'attesa, il trasferimento e la registrazione. Servono a chi usa il mouse e non tolgono niente alla tastiera: ogni pulsante esegue lo stesso comando della sua scorciatoia. Per chi si muove con il tabulatore la fila conta come una tappa sola, qualunque cosa contenga, e dentro ci si sposta con le frecce: così il numero di tabulazioni per arrivare al campo di composizione non cambia mentre le chiamate vanno e vengono.

        Questa guida si riapre in qualunque momento con {K(profile, HotkeyCommand.OpenHelp)}, e si apre sull'argomento che riguarda il punto in cui ti trovi.
        """;

    private static string Tastierino(HotkeyProfile profile) =>
        $"""
        La modalità semplificata governa l'intera chiamata con quattro tasti del tastierino numerico. Si attiva da Impostazioni, scheda Accessibilità, alla voce «modalità di interazione».

        Serve a chi sta al centralino senza il tempo di imparare le scorciatoie, e a chi copre un turno ogni tanto. Non toglie niente: tutte le combinazioni continuano a funzionare, e questa è una via più corta che si affianca a quelle.

        I quattro tasti, sotto la mano destra su qualunque tastiera:

        - PIÙ risponde alla chiamata che squilla. Dopo, mette in pausa chi è in linea e lo riprende: è lo stesso tasto, e fa l'una o l'altra cosa secondo dove sei.
        - DIVISO chiude. Chiude la chiamata in corso, rifiuta quella che squilla, e durante un trasferimento chiude la linea con l'interno riportandoti al chiamante.
        - INVIO chiama l'interno a cui vuoi passare il chiamante.
        - MENO svincola: passa il chiamante all'interno.

        Il trasferimento è sempre lo stesso, e sono tre gesti nell'ordine in cui vengono naturali.

        Uno. Rispondi con PIÙ e parla con chi ha chiamato.

        Due. Componi l'interno e premi INVIO. Il chiamante va in attesa e tu senti cosa fa l'interno: se squilla, se è occupato, se non risponde nessuno. Se il collega risponde, puoi annunciargli la chiamata.

        Tre. Premi MENO e il chiamante passa all'interno. Lo puoi fare sia dopo che il collega ha risposto, sia mentre sta ancora squillando: decidi tu, perché hai sentito.

        Se l'interno è occupato o non risponde, non premere MENO: premi DIVISO. Chiude la linea con il collega e ti riporta al chiamante, che è ancora lì ad aspettare e a cui puoi dire com'è andata.

        È il motivo per cui il trasferimento passa sempre da INVIO prima che da MENO. Mandare il chiamante direttamente all'interno costerebbe un tasto in meno, ma su un interno occupato non ci sarebbe modo di accorgersene: il chiamante finirebbe su un telefono che non può rispondergli, e tu non lo sapresti.

        Se premi INVIO quando l'interno è già in linea, BlindPhone te lo dice e ti ricorda che il tasto per passargli la chiamata è MENO. Non succede nulla di male: è solo il tasto sbagliato per quel momento.

        Mentre una chiamata squilla il campo di composizione non accetta testo, così il PIÙ con cui rispondi non ci finisce dentro per sbaglio. Torna a scrivere appena hai risposto, che è quando ti serve per comporre l'interno.

        PIÙ, DIVISO e MENO non arrivano mai al campo di composizione, in nessun momento: se in quello stato non hanno significato senti il segnale di comando rifiutato e nel numero non compare nulla.

        Fuori da una chiamata INVIO torna a essere quello di sempre: compone il numero che hai scritto.

        In questa modalità i tasti rapidi restano attivi ma non si possono riassegnare. È voluto: la via breve serve se è identica su tutte le postazioni, e un PIÙ che su una macchina risponde e su un'altra fa altro sarebbe peggio di nessuna via breve per chi sostituisce un collega.

        Con due chiamate insieme il tastierino non basta: PIÙ risponde alla seconda mettendo in pausa la prima, ma per alternare fra le due servono {K(profile, HotkeyCommand.Swap)} o i pulsanti. È il caso in cui torna utile sapere che le scorciatoie ci sono ancora.
        """;

    private static string Chiamata(HotkeyProfile profile) =>
        $"""
        REGISTRARE LA CONVERSAZIONE.

        {K(profile, HotkeyCommand.ToggleCallRecording)} avvia la registrazione mentre sei in linea, e la stessa combinazione la ferma. Il file contiene entrambe le voci e finisce nella cartella Documenti, sottocartella «Registrazione Chiamate», con un nome che comincia da data e ora: così l'elenco è già in ordine cronologico e riconosci quella che cerchi sentendone il nome.

        L'interlocutore sente «avvio della registrazione» quando cominci e «registrazione interrotta», con la durata, quando fermi. Non è una cortesia: informare chi è in linea prima che la raccolta cominci è ciò che rende lecita la registrazione. L'avviso si può togliere da Impostazioni, scheda Chiamate, e in quel caso informarlo tocca a te — BlindPhone te lo ricorda a ogni avvio.

        Alla fine senti la durata registrata, e poco dopo il nome del file: la conversione richiede qualche secondo su una conversazione lunga, e nel frattempo il posto operatore resta libero.

        In conferenza la registrazione non è disponibile: le due linee sono due percorsi audio distinti e ne uscirebbe un file con una voce sola.

        Quando arriva una chiamata la senti squillare e la voce ti dice chi è. La suoneria continua finché non rispondi: se smette da sola, la chiamata non c'è più — è il centralino che l'ha ritirata, perché è scaduto il tempo di squillo che ha configurato lui. In cima alla finestra compare anche un riquadro blu lampeggiante con il nome e il numero di chi chiama, per chi guarda lo schermo.

        Se il numero risulta in rubrica senti il nome e il recapito da cui chiama: «Chiamata da Mario Rossi, interno quattro zero due uno». Il numero viene detto anche avendo il nome, perché è quello che ti serve subito dopo, per richiamare o per riferire a chi passi la chiamata. Si dice «interno» solo quando quel numero è davvero l'interno del contatto: se lo stesso contatto ti sta chiamando dal cellulare senti «numero», che è un'altra cosa e cambia il modo di rispondere.

        Se il numero non è in rubrica senti il solo numero, letto cifra per cifra così com'è, pronto da trascrivere o da ricomporre.

        - Rispondi: {K(profile, HotkeyCommand.Answer)}
        - Rifiuta, oppure chiudi la conversazione in corso: {K(profile, HotkeyCommand.HangUp)}
        - Metti in attesa o riprendi: {K(profile, HotkeyCommand.ToggleHold)}
        - Alterna fra chi è in linea e chi è in attesa: {K(profile, HotkeyCommand.Swap)}
        - Fai parlare tutti e due insieme con te: {K(profile, HotkeyCommand.Conference)}
        - Microfono muto o attivo: {K(profile, HotkeyCommand.ToggleMute)}
        - Richiama l'ultimo numero composto: {K(profile, HotkeyCommand.RedialLast)}

        Con il microfono muto l'interlocutore resta in linea e non ti sente: serve per parlare con un collega senza chiudere e senza mettere in attesa.

        Con due chiamate aperte hai tre possibilità, e conviene tenerle distinte: alternare significa parlare a uno per volta mentre l'altro aspetta; la conferenza li mette insieme e sentono tutti; il trasferimento ti fa uscire e la linea non è più tua. La conferenza ha un argomento suo in questa guida.

        Rispondere a un menu automatico. Quando chiami un ente che risponde con un centralino automatico — «digita 1 per l'ufficio tecnico» — le cifre della tastiera servono in linea, non nel campo di composizione. Due modi:

        - {K(profile, HotkeyCommand.ToggleDtmfMode)} attiva la modalità toni: da quel momento ogni cifra che premi parte in linea e ti viene detta. La stessa combinazione, oppure {K(profile, HotkeyCommand.Cancel)}, esce. Si spegne da sola quando la chiamata finisce.
        - {K(profile, HotkeyCommand.SendDialerAsDtmf)} manda in linea quello che hai già scritto nel campo di composizione, un tono dopo l'altro. È la via comoda quando la sequenza la conosci: la scrivi con calma, te la rileggi, e parte in un gesto solo.

        Fuori dalla modalità toni le cifre continuano a finire nel campo di composizione, come sempre: serve a scrivere il numero che il chiamante ti sta dettando senza che gli arrivi in linea.

        Durante la conversazione gli annunci non urgenti vengono taciuti, così la voce del sistema non si sovrappone a quella dell'interlocutore. Restano i segnali acustici, che sono brevi e non coprono il parlato. Il comportamento si cambia da Impostazioni, scheda Accessibilità.

        Se ti serve un'informazione mentre parli, chiedila: la durata della chiamata con {K(profile, HotkeyCommand.AnnounceCallDuration)}, la qualità della linea con {K(profile, HotkeyCommand.AnnounceLineQuality)}.
        """;

    private static string Trasferimento(HotkeyProfile profile)
    {
        var key = K(profile, HotkeyCommand.Transfer);

        return $"""
        Il trasferimento usa una combinazione sola, {key}, che cambia significato a seconda del punto in cui sei. Non devi scegliere in anticipo fra trasferimento cieco e con annuncio: lo decidi strada facendo.

        Con una chiamata in corso:

        1. Premi {key}. L'esterno va in attesa e ti viene chiesta la destinazione.
        2. Digita l'interno, oppure alcune lettere del nome per cercarlo in rubrica.
        3. Da qui hai due strade.

        Strada rapida, per smaltire la coda: premi Invio. La chiamata parte subito, alla cieca, senza altre conferme. Digita, Invio, avanti il prossimo.

        Con il mouse le due strade sono due pulsanti distinti, «trasferisci subito» e «annuncia e trasferisci», che compaiono appena hai scritto una destinazione: lì non serve ricordare quale gesto scelga quale strada, perché il pulsante lo dice.

        Strada con annuncio, per presentare il chiamante: premi {key}. Chiami l'interno e gli parli. Poi:

        - {key} di nuovo completa il trasferimento e ti libera
        - {K(profile, HotkeyCommand.Swap)} torna un attimo dal chiamante e mette l'interno in attesa. Ripremuto ti riporta dall'interno. Il trasferimento resta aperto: la destinazione è ancora in memoria e {key} completa da tutte e due le parti
        - {K(profile, HotkeyCommand.HangUp)} chiude con l'interno che non vuole la chiamata e ti riporta in conversazione con il chiamante
        - {K(profile, HotkeyCommand.Cancel)} annulla tutto e riprende l'esterno
        - {K(profile, HotkeyCommand.Conference)} apre invece una conferenza a tre, quando l'interno chiede di parlare anche lui con il chiamante e tu devi restare

        L'alternanza serve più spesso di quanto sembri: il collega chiede un dato che ha solo il chiamante, e senza di lei l'unica strada era annullare il trasferimento e ricominciare. Finché l'interno non ha risposto non c'è niente da alternare, e la voce te lo dice invece di lasciarti in dubbio.

        Se l'interno è occupato o non risponde, il chiamante torna a te da solo: non resta appeso in attesa senza che nessuno se ne accorga.

        L'interno è occupato e la chiamata è urgente. {K(profile, HotkeyCommand.BargeIn)} ti fa entrare nella conversazione che il collega sta già facendo, per dirgli che c'è una chiamata per lui. Il chiamante resta in attesa dov'è: quando esci con {K(profile, HotkeyCommand.HangUp)} lo trovi lì, e la destinazione è ancora in memoria per completare il trasferimento.

        Tutti sentono che ci sei. Ogni cinque secondi parte un segnale breve che dichiara il tuo ascolto, e lo sentono il collega e la persona con cui sta parlando. Non si può spegnere, e non è una svista: è la differenza fra una funzione da posto operatore e un ascolto di nascosto. Presentati appena entri, come faresti aprendo la porta di una stanza.

        Il comando compare solo quando si può fare davvero: serve che l'interno risulti occupato e che il centralino comunichi i dati della conversazione in corso. Se il tuo centralino non li fornisce, o non sostiene questa funzione, la voce te lo dice invece di lasciarti premere a vuoto.

        Quando è il chiamante a dettarti il numero, non annotarlo da nessuna parte. Mentre lo ascolti premi {K(profile, HotkeyCommand.FocusDialer)} e scrivilo: il chiamante non ti sente digitare e nessuna cifra gli arriva in linea. Poi premi {key} una volta sola: il numero che hai già scritto diventa la destinazione, il chiamante va in attesa e l'interno viene chiamato subito.

        Sono due pressioni in tutto, {key} per chiamare l'interno e {key} per passargli il chiamante. La pressione che serviva a confermare il numero appena riletto non c'è più: l'avevi scritto tu un istante prima, e ripeterla su ogni chiamata del turno costava tempo senza aggiungere nessuna verifica.

        Il numero ti viene riletto cifra per cifra mentre l'interno squilla. Se una cifra è sbagliata sei ancora in tempo: {K(profile, HotkeyCommand.Cancel)} chiude la chiamata all'interno e ti riporta il chiamante. Finché non premi {key} la seconda volta stai parlando tu con l'interno, e il chiamante è ancora tuo.

        Scegliere fra due omonimi. Digitando un nome compare sopra il campo l'elenco dei contatti che corrispondono: freccia giu' scende dentro l'elenco, le frecce su e giu' scorrono i contatti, destra e sinistra leggono i campi di quello su cui ti trovi, Invio chiama. Freccia su dal primo contatto riporta al campo.

        Quali campi vedi accanto a ogni contatto lo decidi tu, in Impostazioni, scheda Rubrica: nominativo e interno ci sono sempre, gli altri - descrizione, reparto, collocazione, stato - si spuntano uno per uno con la barra spaziatrice. Tienine pochi: ogni campo in piu' e' una freccia destra in piu' da percorrere su ogni contatto.

        Anche l'ordine lo decidi tu, nello stesso elenco: Alt con freccia su e Alt con freccia giu' spostano il campo selezionato, e la voce ti dice in che posizione e' finito. Metti per primo quello che ti fa decidere - di solito lo stato, oppure il reparto se i nominativi si assomigliano - cosi' lo trovi subito dopo l'interno e sugli altri non devi arrivare. Sono gli stessi tasti dell'elenco dei codec.

        Il campo non lo devi svuotare mai. Quando rispondi, quando chiudi e quando il trasferimento finisce, il cursore ci torna da solo e quello che era rimasto scritto risulta già selezionato: la prima cifra che digiti prende il posto del numero vecchio. Se invece quel numero ti serve ancora — l'interno era occupato e vuoi riprovare — non toccarlo e premi {key}: è sempre lì.
        """;
    }

    private static string Conferenza(HotkeyProfile profile)
    {
        var key = K(profile, HotkeyCommand.Conference);

        return $"""
        La conferenza a tre mette te e due interlocutori nella stessa conversazione: tutti sentono tutti. Serve quando il chiamante e il collega devono parlarsi e tu devi restare, per esempio per prendere nota di quello che si dicono.

        Non è un trasferimento. Nel trasferimento esci dalla conversazione e la linea non è più tua; in conferenza resti dentro, e chiudendo tu si chiude tutto.

        Come si apre. La conferenza usa le due chiamate che hai già, non ne compone di nuove:

        1. Con il chiamante in linea, mettilo in attesa: {K(profile, HotkeyCommand.ToggleHold)}
        2. Chiama il secondo interlocutore: {K(profile, HotkeyCommand.FocusDialer)}, il numero, Invio
        3. Quando risponde, premi {key}

        La voce ti nomina tutti e due: è la verifica che la conferenza sia partita sulle persone giuste. Se hai aperto quella sbagliata, {key} di nuovo la scioglie subito.

        Ci si arriva anche a metà di un trasferimento con annuncio: hai chiamato l'interno per presentargli la chiamata, lui chiede di parlare anche lui con il chiamante, e da lì la conferenza è una pressione sola.

        Che cosa fanno i tasti mentre la conferenza è aperta:

        - {key} scioglie la conferenza. Resti in linea con chi stavi parlando quando l'hai aperta, e l'altro torna in attesa. Nessuno viene chiuso.
        - {K(profile, HotkeyCommand.HangUp)} chiude entrambe le linee. Fai da ponte fra i due: uscendo tu, smetterebbero comunque di sentirsi. Per congedarne uno solo sciogli prima la conferenza, poi chiudi la linea che vuoi chiudere.
        - {K(profile, HotkeyCommand.ToggleHold)} mette in attesa tutti e due insieme, non uno solo. La stessa combinazione, oppure {key}, li riporta in linea.
        - {K(profile, HotkeyCommand.ToggleMute)} silenzia il tuo microfono e basta: gli altri due continuano a sentirsi fra loro.
        - I toni non si inviano: andrebbero a una sola delle due linee senza che si possa dire quale.

        Se uno dei due riaggancia, la conferenza si scioglie da sola e la voce ti dice chi ha chiuso e con chi sei rimasto. Non ti ritrovi a parlare a due persone di cui una non c'è più.

        Come funziona, e perché conta saperlo. Il missaggio lo fa questa postazione, non il centralino: sei tu il ponte fra le due linee, e a ciascuno arriva la tua voce sommata a quella dell'altro. Il vantaggio è che funziona su qualunque centralino, senza configurazione. La conseguenza è che serve un percorso audio funzionante: se i dispositivi non ci sono, il comando lo dice invece di aprire una conferenza muta.

        Con il motore telefonico simulato la sequenza si prova tutta, come le altre: nessuna telefonata parte davvero e non c'è audio da unire.
        """;
    }

    private static string Composizione(HotkeyProfile profile) =>
        $"""
        Il campo di composizione si raggiunge con {K(profile, HotkeyCommand.FocusDialer)}.

        Ogni cifra che digiti viene pronunciata. Invio compone. {K(profile, HotkeyCommand.Cancel)} cancella quello che hai scritto e annulla la composizione.

        Nel campo le cifre sono un numero e le lettere sono un nome. Se scrivi delle lettere, la rubrica cerca da sola e propone i contatti che corrispondono.

        - Quanti sono te lo dice la voce quando smetti di digitare. Se è uno solo, te lo nomina subito con il suo interno, e ti avverte se quell'interno è occupato o fuori servizio.
        - Freccia giù scende fra i contatti proposti. Frecce su e giù per cambiare contatto, destra e sinistra per leggere nominativo, interno e stato.
        - Invio chiama il contatto su cui ti trovi. Vale anche se resti nel campo: chiama il primo dell'elenco.
        - {K(profile, HotkeyCommand.Transfer)} usa quel contatto come destinazione del trasferimento in corso.
        - Freccia su dal primo contatto, oppure {K(profile, HotkeyCommand.Cancel)}, torna al campo senza cancellare quello che hai scritto.

        La ricerca parte tre decimi di secondo dopo l'ultima lettera, non a ogni tasto: altrimenti sentiresti un conteggio per ogni carattere, sovrapposto all'eco delle lettere stesse. Se premi Invio prima che sia partita, la ricerca viene completata subito e non perdi la chiamata.

        Se cerchi un nome e non c'è nessuno, te lo dice, invece di lasciarti comporre delle lettere.

        Anche un interno viene cercato, ma in un altro modo. Un nome si cerca per pezzi, un interno no: le cifre devono essere quelle di un interno in rubrica, tutte e nell'ordine giusto. Scrivendo 40 non compare nessuno dei 4021 o dei 1402; compare solo chi ha proprio l'interno 40.

        - Se c'è una voce sola, la voce ti dice di chi è, senza ripetere l'interno che hai appena scritto, e ti avverte se è occupato o fuori servizio.
        - Se l'interno è condiviso da più voci - succede con le stanze e gli ambulatori - ti dice quante sono, e con la freccia giù le scorri come i contatti trovati per nome.
        - Se nessuno ha quell'interno la voce tace: stai componendo un numero, e non serve sentirsi dire ogni volta che non è in rubrica.
        - Trovare un interno non cambia quello che fa Invio: compone le cifre che hai scritto, come sempre.
        """;

    private static string Coda(HotkeyProfile profile) =>
        $"""
        La coda è l'elenco di chi sta aspettando di parlare con il centralino. Richiede il collegamento all'API del centralino: senza, il telefono funziona lo stesso ma la coda non c'è.

        Quando il collegamento è disattivato nelle impostazioni, la sezione della coda viene tolta dalla finestra principale: non è un guasto, è la finestra che non ti fa attraversare un elenco che resterebbe vuoto per sempre. I comandi qui sotto continuano a rispondere e ti dicono che il collegamento non è attivo.

        - Quante chiamate aspettano: {K(profile, HotkeyCommand.AnnounceQueueCount)}
        - Chi sono, uno per uno: la stessa combinazione premuta due volte di seguito, oppure {K(profile, HotkeyCommand.AnnounceQueueList)}
        - Vai all'elenco: {K(profile, HotkeyCommand.FocusQueue)}
        - Preleva la chiamata su cui ti trovi: Invio sull'elenco

        Il prelievo di una chiamata specifica dipende da cosa consente il centralino: se non è possibile, te lo dice invece di non fare nulla.

        Stato agente, cioè se sei collegato alla coda e ricevi chiamate:

        - Entra o esci dalla coda: {K(profile, HotkeyCommand.ToggleAgentLogin)}
        - Pausa o ripresa: {K(profile, HotkeyCommand.ToggleAgentPause)}
        - Come sto adesso: {K(profile, HotkeyCommand.AnnounceAgentState)}

        In pausa resti collegato ma non ti vengono assegnate nuove chiamate. È la cosa da fare prima di allontanarti, invece di lasciare squillare.

        LE PAUSE SONO DUE E NON VANNO CONFUSE.

        La pausa in coda, qui sopra, riguarda le sole code e passa dall'API del centralino: senza collegamento non fa nulla.

        La pausa della postazione, {K(profile, HotkeyCommand.ToggleServicePause)}, mette fuori servizio il posto operatore per intero. Ogni chiamata entrante — di coda o diretta — viene rifiutata nell'istante in cui arriva, e il centralino la passa subito a un collega invece di lasciarla squillare per venti secondi su una sedia vuota. Funziona anche senza API, perché agisce sulla segnalazione telefonica.

        Mentre sei fuori servizio il campo di composizione è bloccato e un avviso rosso lampeggia nella finestra. Se provi a digitare, la voce ti ricorda da quanto sei in pausa e con quale tasto rientrare. Le chiamate che avevi già in corso non vengono toccate: mettersi in pausa significa non prenderne di nuove, non far cadere quella che hai in mano.

        Oltre una certa attesa scatta un avviso, per non lasciare che qualcuno resti in linea dimenticato. La soglia si regola da Impostazioni, scheda Accessibilità.
        """;

    private static string Colleghi(HotkeyProfile profile) =>
        $"""
        L'elenco dei colleghi dice chi e' alla propria postazione in questo momento. Si consulta con {K(profile, HotkeyCommand.FocusColleagues)}: la voce dice subito quanti sono in turno e nomina chi non c'e', poi con le frecce scorri l'elenco riga per riga. Invio chiama il collega su cui ti trovi; se hai un trasferimento in corso, Invio lo sceglie come destinazione invece di comporre una seconda chiamata.

        Ogni riga dice prima lo stato e poi chi e': e' l'ordine giusto per chi sta scorrendo l'elenco a cercare qualcuno, perche' evita di ascoltare il nome di dieci persone per sapere che non ci sono.

        Quali interni sorvegliare lo decidi tu, in Impostazioni, scheda Colleghi: una riga per collega, prima l'interno e poi il nominativo se vuoi scriverlo a mano. Senza nominativo il nome viene cercato in rubrica. Una riga che comincia con il cancelletto viene ignorata, cosi' puoi mettere da parte chi e' in ferie senza cancellarlo. Finche' l'elenco e' vuoto la sezione non compare nella finestra e non parte nessuna verifica.

        Da dove arriva l'informazione, e perche' conta saperlo.

        Con il collegamento all'API del centralino attivo lo stato e' completo: dice anche se il collega sta parlando, ed e' la strada migliore.

        Senza API, BlindPhone chiede al centralino se l'interno risponde. E' la stessa domanda che il centralino gira a questa postazione ogni due minuti per sapere se e' viva, e funziona su qualunque impianto. Sa una cosa sola pero': se il telefono c'e' o non c'e'. Per questo la colonna dice "in turno" e non "disponibile" — un collega in turno puo' benissimo essere gia' al telefono.

        Se il centralino risponde di si' a qualunque interno, anche a uno che non esiste, BlindPhone se ne accorge e lo dichiara invece di mostrare tutti presenti. Un elenco che dice il falso e' peggio di un elenco che dice di non sapere, perche' ci si agisce sopra: si passerebbe una chiamata a una scrivania vuota.

        La verifica si ripete da sola a intervalli, un minuto per impostazione predefinita, e riparte anche ogni volta che apri l'elenco con il tasto rapido. L'intestazione dice l'ora dell'ultima verifica riuscita: se resta indietro, il centralino non sta rispondendo.
        """;

    private static string Rubrica(HotkeyProfile profile) =>
        $"""
        La rubrica si apre con {K(profile, HotkeyCommand.OpenContacts)}. Si apre già pronta a cercare: digiti e basta, non c'è un pulsante «cerca».

        Se avevi scritto qualcosa nel campo di composizione, quel testo entra come ricerca iniziale e non devi ribatterlo.

        I risultati sono una tabella e si leggono una cella alla volta:

        - Freccia giù dal campo di ricerca scende nei risultati
        - Frecce su e giù cambiano contatto
        - Frecce destra e sinistra percorrono i campi: nominativo, numero, tipo, collocazione, struttura, stato
        - Freccia su dal primo contatto torna al campo di ricerca

        La colonna «stato» dice se l'interno è libero, occupato, squilla, in non disturbare, assente o fuori servizio. «Fuori servizio» significa telefono spento o scollegato: è la destinazione peggiore per un trasferimento, perché la chiamata squilla nel vuoto e il chiamante torna indietro quando lo hai già salutato. La cella resta vuota quando lo stato non è noto: lo si sa solo per gli interni di cui il centralino ha segnalato un cambiamento da quando BlindPhone è acceso.

        Lo stato arriva dal centralino per due strade, scelte da sole: le API quando ci sono, altrimenti le sottoscrizioni SIP, che sono la stessa strada con cui i telefoni da tavolo accendono i tasti di occupato e funzionano su qualunque centralino. Se non è disponibile nessuna delle due — per esempio con l'account SIP non ancora configurato — la colonna resta vuota per tutti e BlindPhone lo dice all'apertura della rubrica, una volta sola.

        Sul contatto su cui ti trovi:

        - Invio chiama il numero predefinito
        - Control Invio legge gli altri recapiti
        - {K(profile, HotkeyCommand.Transfer)} lo usa come destinazione del trasferimento in corso
        - Control D legge la scheda completa

        La ricerca guarda tutte le colonne, non il solo nominativo: nome, cognome, numero, descrizione, reparto, collocazione e interno di riferimento. Serve perche' in una rubrica di ospedale la maggior parte delle voci non ha un nome — ambulatori, sale, guardiole — e si trovano per descrizione.

        Le parole si possono scrivere in qualunque ordine e possono stare in campi diversi: «ambulatorio cardiologia» trova «Ambulatorio di Cardiologia», e trova anche una voce descritta «Ambulatorio» che sta nel reparto di Cardiologia. Ogni parola in piu' restringe il risultato invece di allargarlo, quindi se i risultati sono troppi basta aggiungere una parola che ricordi.

        I contatti arrivano dal centralino, se collegato, da file importati e dal gestionale dell'ente: si sceglie da Impostazioni, scheda Rubrica, alla voce «da dove arrivano i contatti».

        Con la rubrica collegata a Microsoft SQL Server, BlindPhone legge una tabella o una vista del gestionale e ne copia il contenuto nell'archivio cifrato della postazione, riallineandolo ogni tanto. Il server non viene mai modificato: si legge soltanto.

        La copia locale non e' un ripiego, e' il motivo per cui il collegamento si puo' usare: il nome di chi chiama viene cercato mentre il telefono squilla, e se quella ricerca dovesse attraversare la rete un rallentamento ti lascerebbe senza sapere chi risponde. Con la copia, a rete caduta la rubrica resta quella dell'ultimo riallineamento riuscito e continui a lavorare. Preferiti e numeri rapidi restano tuoi e restano sulla postazione.

        La procedura e' la stessa dei file: ci si collega, si sceglie la tabella dallo stesso elenco, si abbinano le stesse colonne e si controlla l'anteprima. Il pulsante «Riallinea adesso» aggiorna la copia quando serve; il riallineamento automatico non parte durante una chiamata.

        Si importa da file CSV e da backup di banca dati: file SQLite, oppure esportazioni SQL in formato testo prodotte da MySQL, MariaDB, PostgreSQL e SQL Server. Il formato viene riconosciuto dal contenuto e non dall'estensione, quindi non conta come si chiama il file: punto sql, punto bak, punto bkp, punto dmp o senza estensione, se dentro c'e' un backup leggibile viene letto.

        Molte esportazioni non hanno la riga dei titoli: la prima riga e' gia' un contatto. BlindPhone se ne accorge da solo e attiva la casella «il file non ha la riga dei titoli», che puoi comunque correggere. Con quella attiva le colonne si abbinano per posizione e ognuna porta un esempio preso dal file — «Colonna 6, esempio: GIULIA» — cosi' riconosci quale sia senza vedere il file. Serve anche a non perdere nessuno: senza, la prima riga verrebbe presa per un'intestazione e quel contatto sparirebbe dalla rubrica a ogni importazione.

        Se il file e' aperto in Excel non si puo' leggere, e BlindPhone te lo dice: Excel tiene i file aperti in modo che nessun altro programma li apra. Chiudi la finestra e riprova.

        Con un backup compare un passo in più, «Tabella da importare»: un backup contiene tutte le tabelle del gestionale e nessuna regola dice quale sia la rubrica. Accanto a ogni nome trovi quante colonne e quante righe contiene, che è il modo più rapido per riconoscerla ascoltando, e viene proposta quella che ha più l'aria di essere la rubrica. La proposta va sempre confermata: una tabella sbagliata produce una rubrica in cui i numeri appartengono alle persone sbagliate, e te ne accorgeresti passando una chiamata a chi non c'entra.

        Il file viene solo letto, mai eseguito, e non viene modificato: un backup resta identico a com'era.

        Un backup nativo di SQL Server, quello con estensione punto bak prodotto dal comando di backup, non si può leggere: è un formato binario che solo SQL Server sa aprire. In quel caso chiedi a chi te l'ha dato un'esportazione in formato testo oppure un CSV della sola tabella della rubrica.
        """;

    private static string UltimeChiamate(HotkeyProfile profile) =>
        $"""
        L'elenco si apre con {K(profile, HotkeyCommand.OpenCallHistory)} ed è diviso in tre pannelli: perse, ricevute, effettuate. Control Tab passa dall'uno all'altro.

        Le perse vengono per prime, e il cursore ci arriva già sopra: sono la ragione per cui questo elenco esiste. Una chiamata persa mentre eri impegnato su un'altra linea non lascia altra traccia.

        Ogni pannello è una tabella e si legge una cella alla volta:

        - Frecce su e giù cambiano chiamata
        - Frecce destra e sinistra percorrono i campi: quando, chi, numero, e poi la durata, l'esito o la coda a seconda del pannello
        - Invio richiama il numero
        - F9 lo usa come destinazione del trasferimento in corso: serve quando il chiamante chiede di essere passato alla persona che lo aveva cercato poco prima

        Quante ne vedi. All'apertura ogni pannello mostra le cinquanta piu' recenti, e la riga di riepilogo in fondo dice quante sono in tutto: per esempio «Perse: 50 di 312». Il pulsante «Mostra le precedenti» ne aggiunge altre cinquanta al pannello aperto, e il cursore resta dov'era.

        Il registro non si riempie e non smette mai di registrare: le chiamate escono solo allo scadere della conservazione oppure quando le elimini tu.

        Il momento della chiamata viene detto in forma discorsiva — «oggi alle 9 e 40», «ieri alle 15» — perché un orario in cifre viene letto in modi diversi da voci diverse.

        Quanto a lungo si conservano. Da Impostazioni, scheda Chiamate, si sceglie fra un giorno, una settimana, un mese, oppure mai: «mai» conserva tutto ed è la scelta giusta quando la conservazione è regolata dall'ente. L'elenco contiene numeri chiamati e chiamanti, cioè dati di traffico: il periodo va allineato alla procedura in vigore, non scelto per comodità.

        Il pulsante «Svuota l'elenco» chiede se eliminare solo il pannello aperto o tutto quanto, e dice quante voci sta per eliminare. Lo stesso si può fare dalle impostazioni, senza aprire l'elenco.
        """;

    private static string Stato(HotkeyProfile profile) =>
        $"""
        Sono i comandi con cui si riprende il filo, per esempio dopo essersi allontanati dalla postazione. Non cambiano nulla: chiedono soltanto.

        - Riepilogo completo: {K(profile, HotkeyCommand.AnnounceStatusSummary)}. Dice registrazione, chiamata attiva, chiamate in attesa, coda e stato agente.
        - Durata della chiamata: {K(profile, HotkeyCommand.AnnounceCallDuration)}
        - Qualità della linea e cifratura: {K(profile, HotkeyCommand.AnnounceLineQuality)}
        - Chiamate in coda: {K(profile, HotkeyCommand.AnnounceQueueCount)}
        - Stato agente: {K(profile, HotkeyCommand.AnnounceAgentState)}
        - Ripeti l'ultimo annuncio: {K(profile, HotkeyCommand.RepeatLastAnnouncement)}
        - Zittisci la voce senza annullare nulla: {K(profile, HotkeyCommand.SilenceSpeech)}
        - Stato di un interno: {K(profile, HotkeyCommand.AnnounceExtensionState)}
        - Elenco delle scorciatoie: {K(profile, HotkeyCommand.AnnounceHotkeyList)}

        «Stato di un interno» risponde sul numero che hai digitato nel campo di composizione o sul contatto evidenziato: dice se è libero, occupato o fuori servizio, e ti avverte quando è meglio non passargli una chiamata. È la domanda da fare prima di trasferire. Se lo stato non è ancora noto attende un istante la risposta del centralino, e se non arriva te lo dice invece di tacere.

        «Qualità della linea» dice due cose sulla cifratura, e sono due cose diverse. «Segnalazione cifrata TLS» riguarda chi chiama chi, quando e verso quale interno: è il trasporto configurato nell'account. «Voce cifrata» riguarda il contenuto della conversazione, e si ottiene con SRTP, che è un'altra cosa e si negozia separatamente. Si può avere la prima senza la seconda, ed è anzi la situazione più comune.

        La cifratura della voce si accende da Impostazioni, scheda Account SIP, voce «Cifratura della voce». Tre scelte: «Disabled» lascia la voce in chiaro; «Preferred» la cifra dove la controparte lo consente e, se rifiuta, fa ripartire la chiamata in chiaro dicendotelo, così nessuna telefonata va persa; «Required» non fa la chiamata se la cifratura non è disponibile, né in uscita né in entrata. Parte disattivata: accenderla cambia il modo in cui le chiamate si instaurano, e va provata su una telefonata prima di adottarla in un turno.

        «Ripeti l'ultimo annuncio» serve quando un rumore copre la voce, ed è più utile di quanto sembri: evita di dover ricostruire cosa è successo.

        «Durata della chiamata», {K(profile, HotkeyCommand.AnnounceCallDuration)}, risponde alla domanda «da quanto» e cambia oggetto secondo la situazione: mentre una chiamata squilla ti dice da quanto sta squillando, mentre parli da quanto dura la conversazione contata dalla risposta, senza nessuno in linea da quanto sei in servizio. Non è un tasto con tre significati: è la stessa domanda applicata a quello che hai davanti.

        Lo squillo viene prima di tutto il resto, ed è l'unico tempo che scade: una chiamata che squilla da tre secondi e una che squilla da venticinque non chiedono la stessa cosa, perché la seconda sta per essere ritirata dal centralino o abbandonata da chi chiama. Se stai già parlando e ne arriva un'altra, senti tutti e due i tempi, ciascuno con la sua parola davanti.

        In pausa non serve chiederlo: qualunque tasto ti dice già da quanto dura la pausa.

        Il tempo in servizio è al netto delle pause, e si vede anche a schermo in alto a destra. Durante la pausa quell'orologio si ferma e ne parte un altro, che conta lo stacco.
        """;

    private static string Voce(HotkeyProfile profile) =>
        $"""
        BlindPhone parla in due modi diversi, e vale la pena distinguerli.

        Con uno screen reader in esecuzione, gli annunci arrivano tramite le notifiche di sistema e li pronuncia lui, con la tua voce e la tua velocità.

        Senza screen reader, BlindPhone parla con una voce propria, che si può mandare su un dispositivo audio diverso da quello della conversazione: è la configurazione che evita di sentire la voce del sistema nello stesso orecchio in cui parla l'interlocutore.

        Se ti capita di sentire ogni annuncio due volte, da due voci diverse, togli la spunta a «Fai parlare BlindPhone con la sua voce» in Impostazioni, scheda Accessibilità. Il cambiamento vale subito, senza riavviare.

        Oltre alla voce ci sono i segnali acustici, brevi e riconoscibili: chiamata in arrivo, chiamata risposta, comando rifiutato, collegamento perduto. Passano anche quando la voce tace, che è il motivo per cui esistono. Si possono posizionare nello spazio stereo per famiglia di evento: coda a sinistra, chiamata al centro, linea a destra.

        Se hai un display braille, la riga di stato viene aggiornata con la stessa informazione degli annunci, in forma compatta.

        Ripeti l'ultimo annuncio con {K(profile, HotkeyCommand.RepeatLastAnnouncement)}, interrompi la voce con {K(profile, HotkeyCommand.SilenceSpeech)}.
        """;

    private static string Finestra(HotkeyProfile profile) =>
        $"""
        Chiudendo la finestra con la X BlindPhone non si spegne: resta in servizio nell'area di notifica, continua a ricevere chiamate e resta registrato al centralino. È deliberato, perché un softphone chiuso per sbaglio si manifesta come chiamanti che squillano a vuoto, e non c'è nulla che te lo faccia notare.

        Alt F4 invece chiude davvero, come in qualunque altro programma. Il gesto che conosci fa quello che ti aspetti: se vuoi spegnere BlindPhone, quello è il tasto.

        - Chiudi BlindPhone: Alt F4, oppure {K(profile, HotkeyCommand.QuitApplication)}
        - Riapri la finestra da qualunque programma: {G(profile, HotkeyCommand.ShowWindow)}
        - Raggiungi l'icona da tastiera: Windows B, poi il tasto applicazione oppure Maiusc F10 per il menu, con «Apri BlindPhone» ed «Esci»

        Se esci mentre una chiamata è in corso ti viene chiesta conferma, perché uscendo cadrebbe. Vale per tutti e due i modi di uscire.

        Se non trovi la finestra e riapri il collegamento sul desktop non parte una seconda copia: torna davanti quella già in esecuzione, e la voce te lo dice. Due copie registrerebbero lo stesso interno sul centralino, e le chiamate arriverebbero a una sola delle due, non sempre la stessa.

        Quando arriva una chiamata la finestra torna davanti da sola. È l'unico caso in cui si muove senza che tu glielo abbia chiesto, e serve perché su una finestra riposta i comandi locali non arrivano. Se preferisci non essere interrotto mentre lavori in un altro programma, disattivalo da Impostazioni, scheda Accessibilità, sezione Finestra e area di notifica: per rispondere ti restano le scorciatoie globali.

        Le scorciatoie globali funzionano anche con BlindPhone in secondo piano. Se una non risponde, un altro programma l'ha già registrata nel sistema: BlindPhone lo segnala quando succede, e puoi assegnarne un'altra.
        """;

    private static string CambiareTasti(HotkeyProfile profile) =>
        $"""
        Ogni comando è riassegnabile, da Impostazioni, scheda Tasti rapidi.

        Ci sono due ambiti indipendenti. Locale vale con BlindPhone in primo piano. Globale vale anche quando stai lavorando in un altro programma.

        Nell'elenco dei comandi:

        - Frecce su e giù cambiano comando
        - Frecce destra e sinistra passano fra combinazione locale e globale
        - Invio riassegna quella della colonna su cui ti trovi
        - Canc la toglie

        La combinazione non si scrive, si preme: si apre un'area che cattura tutti i tasti e li rilegge a ogni pressione, così senti subito cosa hai registrato. Per uscire dall'area e confermare si preme Tab, che per questo è l'unico tasto non assegnabile.

        Prima di accettare, BlindPhone controlla tre cose: che la combinazione non sia riservata da Windows, che non sia già assegnata a un altro suo comando, e che non sia una di quelle che NVDA o JAWS usano di continuo. Nei casi superabili decidi tu.

        Le modifiche valgono solo dopo «Salva la configurazione dei tasti», che offre quattro strade: aggiornare il profilo in uso, crearne uno nuovo, esportarlo su file per un'altra postazione, oppure produrre la scheda dei tasti in testo, da leggere o stampare in braille.

        Un profilo è una mappa completa dei tasti. Serve a due cose: turni diversi sulla stessa postazione, e chi arriva da un altro softphone e vuole ritrovare le proprie abitudini. «Standard» e «Solo primo piano» sono forniti con il prodotto e non si sovrascrivono: modificandoli si salva una copia.

        L'elenco completo delle combinazioni assegnate è l'ultimo argomento di questa guida. A voce lo leggi con {K(profile, HotkeyCommand.AnnounceHotkeyList)}.
        """;

    private static string Impostazioni(HotkeyProfile profile) =>
        $"""
        Le impostazioni si aprono con {K(profile, HotkeyCommand.OpenSettings)}. Control Tab cambia scheda, Alt più la lettera sottolineata porta direttamente a un campo.

        Account SIP. È quello che fa funzionare il telefono: interno, password e indirizzo del centralino, forniti da chi amministra il centralino. Finché non è compilato, BlindPhone parte in modalità simulata: tutti i comandi funzionano ma nessuna telefonata parte davvero. Viene detto all'avvio, perché è l'unico caso in cui tutto sembra a posto e non lo è.

        Centralino. Il collegamento all'API serve a vedere le chiamate in coda, la presenza degli interni e la rubrica del centralino. Se il centralino non espone l'API, togli la spunta: telefonare, rispondere e trasferire continuano a funzionare, e il sistema smette di segnalare un guasto che non esiste.

        Audio. Quattro vie separate: microfono, conversazione, suoneria, annunci. Assegnare dispositivi diversi è la configurazione che fa più differenza nell'uso quotidiano, perché evita che la voce degli annunci e la suoneria finiscano nello stesso orecchio dell'interlocutore. Dalla via «suoneria» escono il segnale di chiamata in arrivo, gli eventi di coda e gli allarmi; dalla «conversazione» escono la voce dell'interlocutore e i riscontri di quello che hai appena fatto. Dispositivi e volumi valgono da subito, senza riavviare — solo una chiamata già in corso resta sui dispositivi con cui è cominciata. Qui stanno anche la suoneria personalizzata, la regolazione del buffer audio e l'ordine dei codec.

        Accessibilità. Modalità di interazione — standard o semplificata da tastierino, vedi l'argomento dedicato — voce, quantità di dettaglio, lettura dei numeri, segnali acustici, braille, e la sezione Finestra e area di notifica.

        Chiamate. Modalità del tasto di trasferimento, tempo di attesa della consultazione, conferme.

        Aggiornamenti, nella scheda Assistenza. Se chi amministra la postazione ha indicato un server, all'avvio BlindPhone guarda se è uscita una versione più recente. Quando la trova te lo dice e compare un pulsante nella barra dei comandi: aprendolo leggi che versione è e cosa porta di nuovo. Non scarica e non installa niente finché non lo chiedi tu, e l'installazione è la stessa procedura della prima volta, con la richiesta di consenso di Windows. Se il server non risponde non succede nulla e la cosa resta nel registro.

        Tasti rapidi. Vedi l'argomento dedicato.

        Rubrica. Importazione da file CSV e da backup di banca dati, con scelta della tabella, abbinamento delle colonne e anteprima.

        Voce e tasti rapidi si applicano subito. Account SIP, centralino e dispositivi audio richiedono il riavvio, e viene chiesto al salvataggio: se hai una chiamata in corso rispondi No, perché riavviando cadrebbe.
        """;

    private static string Problemi(HotkeyProfile profile) =>
        $"""
        Il telefono non squilla e non parte nessuna chiamata. Controlla la registrazione con {K(profile, HotkeyCommand.AnnounceStatusSummary)}. Se dice «modalità simulata», l'account SIP non è configurato: apri le impostazioni con {K(profile, HotkeyCommand.OpenSettings)}, scheda Account SIP.

        L'interlocutore mi sente a scatti, o io sento a scatti. Impostazioni, scheda Audio, sezione Regolazione del buffer audio: aumenta il cuscinetto. I tre valori preimpostati coprono quasi tutti i casi: rete locale, prudente, rete disturbata. Se invece ti accorgi di sovrapporti all'interlocutore, il cuscinetto è troppo alto.

        L'interlocutore mi sente piano, oppure sente il rumore dietro di me. Impostazioni, scheda Audio, sezione Elaborazione del microfono, sotto la scelta del microfono. Il controllo automatico del livello alza la voce se parli piano e la abbassa se parli forte. La riduzione del rumore ambientale attenua ventole, ronzii e aria condizionata. Sono spente di partenza e si applicano subito, anche sulla chiamata in corso: accendile una alla volta e chiedi all'interlocutore se sente la differenza. La riduzione agisce soprattutto fra una parola e l'altra, quindi mentre parli il rumore resta, e ai livelli alti la voce diventa meno naturale: parti da leggera. Aggiunge circa sedici millisecondi di ritardo a quello che dici.

        Sento gli annunci due volte. Impostazioni, scheda Accessibilità: togli la spunta a «Fai parlare BlindPhone con la sua voce». Gli annunci continuano ad arrivare dallo screen reader.

        Una impostazione non si lascia cambiare. La postazione è configurata dall'amministratore: le schede bloccate lo dicono nel titolo — «Account SIP (bloccata)» — e restano leggibili, così puoi comunque riferire come è configurata quando chiami l'assistenza. Il messaggio che senti aprendo le impostazioni dice a chi rivolgersi. Se hai la password dell'amministratore, il pulsante «Sblocca o riblocca» le apre per questa sessione soltanto.

        Chi chiamo mi richiama sulla postazione invece che al centralino. In Impostazioni, scheda Account SIP, c'è «Numero presentato in uscita»: indica lì il recapito pubblico dell'ente e chi riceve le tue chiamate vedrà quello al posto del tuo interno. Verifica con una chiamata di prova verso un telefono di cui vedi il display, perché l'ultima parola è del centralino: quasi tutti sostituiscono il numero dichiarato da un interno con quello configurato per quell'interno, ed è una protezione contro la falsificazione del chiamante, non un difetto. Se la prova mostra ancora l'interno, l'autorizzazione va data dall'amministratore del centralino.

        Un tasto non fa niente. Verifica che sia ancora assegnato: {K(profile, HotkeyCommand.AnnounceHotkeyList)} legge le combinazioni attive. Se è una combinazione globale, può essere occupata da un altro programma. Se è un tasto singolo senza modificatori, mentre il cursore è nel campo di composizione serve a comporre il numero e non esegue il comando: è normale, e in quel caso vale la versione con i modificatori.

        La coda non c'è. Richiede il collegamento all'API del centralino, che a sua volta richiede un piano di licenza adeguato. Se il collegamento è disattivato nelle impostazioni la sezione viene tolta dalla finestra: è una scelta di configurazione e non un guasto, e si rimette spuntando «Collega BlindPhone all'API del centralino» in Impostazioni, scheda Centralino.

        Mi chiede se fidarmi di un certificato. Succede quando il centralino presenta un certificato che Windows non riesce a verificare: nella maggior parte degli enti è normale, perché il certificato è emesso internamente e non da un'autorità pubblica. Prima di scegliere «fidati sempre», fatti dettare l'impronta da chi amministra il centralino e confrontala con quella che ti viene letta: è l'unico controllo vero che si può fare. Se non ti aspettavi il messaggio, o se l'impronta non corrisponde, scegli «non fidarti». Se il certificato del centralino è cambiato per un motivo legittimo, da Impostazioni, scheda Diagnostica, c'è «Dimentica i certificati approvati» che riporta la domanda.

        Segnalare un problema. {K(profile, HotkeyCommand.SendSupportPackage)} apre la segnalazione: scrivi con parole tue che cosa è successo, e BlindPhone prepara un solo file con la tua descrizione, il riepilogo dello stato, gli errori registrati e i registri recenti. Poi lo manda al recapito dell'assistenza configurato, oppure — se non ce n'è uno — lo salva sul desktop e apre la cartella.

        La descrizione è la parte più utile e l'unica che solo tu puoi scrivere. «Succede quando trasferisco mentre ho una seconda chiamata in attesa» indirizza la ricerca in un modo che nessun contatore può fare. Anche una riga basta.

        Attenzione ai dati personali: i registri riportano i numeri chiamati e chiamanti. Sono dati di traffico e vanno mandati solo a chi è autorizzato a trattarli. Il riepilogo invece non contiene password né credenziali.

        Se BlindPhone si è chiuso da solo o ha segnalato un errore, il rapporto viene registrato da solo e resta sulla postazione. Al riavvio successivo la voce te lo dice e ti ricorda con quale tasto mandarlo: è quello che permette di segnalare un guasto capitato ieri pomeriggio, di cui altrimenti non resterebbe traccia.

        Da riferire all'assistenza senza inviare nulla. Apri {K(profile, HotkeyCommand.OpenDiagnostics)}: il pannello di diagnostica prepara il riepilogo dello stato — versione, configurazione, telefono, audio e contatori — che si copia negli appunti o si salva su file.

        Il registro è già al massimo dettaglio. In questa versione di prova BlindPhone parte con la modalità debug attiva e il registro su «tutto»: annota il dettaglio interno, i contatori del percorso audio e i messaggi scambiati con il centralino. È voluto — un difetto che si presenta una volta ogni tre giorni si indaga soltanto se il registro c'era già quando è successo, e accenderlo dopo la segnalazione significa chiedere di riprodurre un guasto che non si riproduce a comando.

        Il prezzo, e come cambiarlo. A questo livello il file cresce di decine di megabyte per turno e contiene i numeri chiamati e chiamanti per intero. Se la policy dell'ente lo richiede, da Impostazioni, scheda Diagnostica, si abbassa il dettaglio e si spegne la modalità debug: la scelta viene conservata e non torna indietro da sola. Regola anche i giorni di conservazione nello stesso pannello.

        Dove stanno i registri. Nella cartella dei dati dell'applicazione, sotto «log», conservati per il numero di giorni impostato nel pannello. Contengono i comandi premuti e lo stato su cui hanno agito, ed è quello che permette di distinguere «il tasto non è arrivato» da «è arrivato e non c'era nulla da fare». Contengono anche i numeri chiamati, quindi vanno trattati come documenti con dati personali.

        Il disco si sta riempiendo. Nella stessa scheda Diagnostica, in fondo, «Calcola lo spazio recuperabile» ti dice quanto occupano i file che BlindPhone può buttare, e «Libera spazio su disco» li elimina dopo averteli elencati uno per uno. Spariscono i registri dei giorni scorsi, le copie anonimizzate, i rapporti di errore già segnalati e i pacchetti di aggiornamento già scaricati. Non si toccano la configurazione, la rubrica, l'elenco delle chiamate, le registrazioni e il registro di oggi.

        Pensaci prima di premerlo dopo una segnalazione: i registri eliminati non tornano, e se il guasto è di venerdì il registro di venerdì è l'unica traccia che ne resta. Il pulsante serve quando il disco è pieno, non come pulizia di routine — a quella pensano già i giorni di conservazione.
        """;

    private static string LavoroAgile(HotkeyProfile profile) =>
        $"""
        Da casa la postazione lavora dietro il router, e a volte dietro una VPN che porta in ufficio solo una parte del traffico. Le chiamate funzionano come in sede, con due differenze possibili.

        La voce dell'interlocutore non arriva. Tu parli e dall'altra parte ti sentono, ma tu senti silenzio. Dopo sei secondi di conversazione così BlindPhone te lo dice a voce: «non arriva la voce dell'interlocutore». Non è l'interlocutore che tace, è la sua voce che si perde fra il centralino e il tuo router. Conviene dirlo all'interlocutore, richiamarlo da un altro telefono se la chiamata è importante, e segnalarlo all'assistenza con {K(profile, HotkeyCommand.SendSupportPackage)}: i dati che servono sono già pronti.

        Le chiamate smettono di arrivare dopo un po' che non telefoni. La postazione risulta registrata, ma il router ha chiuso il passaggio da cui il centralino ti cerca. Per evitarlo BlindPhone manda un piccolo segnale al centralino ogni venticinque secondi. È già acceso: lo trovi in Impostazioni, scheda Account SIP, sezione Lavoro agile, alla voce «Segnale di mantenimento». Non spegnerlo se lavori da casa.

        Nella stessa sezione c'è «Server STUN per l'indirizzo pubblico». Serve quando la voce dell'interlocutore non arriva: BlindPhone chiede a quel server con quale indirizzo pubblico ti vede, e lo comunica al centralino. Il nome del server lo decide l'ente e te lo dà chi amministra il centralino: non ce n'è uno già scritto, perché mandare traffico a un server esterno è una scelta dell'ente. In sede il campo non fa nulla, quindi puoi lasciarlo compilato.

        Per riferire all'assistenza come stanno le cose, apri {K(profile, HotkeyCommand.OpenDiagnostics)}: le righe «NAT fra postazione e centralino», «Indirizzo pubblico scoperto» e, durante una chiamata, «Verso dell'audio» dicono se c'è un router in mezzo e se la voce viaggia nelle due direzioni.
        """;

    private static string Account(HotkeyProfile profile) =>
        $"""
        Su una stessa postazione possono convivere più account SIP. Serve a chi copre due enti, e a chi lavora su una postazione condivisa a turni con colleghi che hanno interni diversi.

        - Apri l'elenco degli account: {K(profile, HotkeyCommand.OpenSipAccounts)}
        - Frecce per scorrerlo, Invio per attivare quello su cui ti trovi
        - «Usa all'avvio» decide con quale account BlindPhone parte domani mattina

        Attivare e rendere predefinito sono due cose diverse, e conviene tenerle distinte: puoi lavorare un pomeriggio sull'account di un secondo ente senza volerlo ritrovare il giorno dopo.

        Il cambio non funziona con una chiamata in corso, e non è una limitazione tecnica: cambiare account chiude la registrazione a un centralino e ne apre una a un altro, e con essa cadrebbe la conversazione. Il comando lo dice invece di chiedere conferma, perché un tasto premuto per abitudine non deve poter far cadere una chiamata con un secondo tasto premuto altrettanto per abitudine.

        Dopo il cambio la registrazione al nuovo centralino richiede qualche secondo. La voce te lo dice quando è pronta.

        Per aggiungere, modificare o eliminare un account: Impostazioni, scheda Account SIP. In cima c'è l'elenco a discesa che sceglie quale account stai modificando — controlla sempre di essere su quello giusto prima di scrivere, perché i campi sotto cambiano tutti insieme.

        Ogni account ha la sua password, conservata separatamente e cifrata. Rinominare un account non gliela fa perdere.
        """;

    private static string Riunioni(HotkeyProfile profile) =>
        $"""
        Si entra in una riunione Zoom dal campo di composizione: scrivi l'identificativo e premi {K(profile, HotkeyCommand.JoinMeeting)}.

        Se la riunione ha un codice d'accesso, scrivilo dopo un punto: 812345678 punto 4321. L'identificativo lo puoi scrivere a gruppi come sta nell'invito, gli spazi non danno fastidio.

        Attenzione al codice, perché Zoom ne ha due per la stessa riunione. Quello scritto nell'invito può contenere lettere e vale per chi entra dal sito. Quello che serve qui è il codice numerico, lo stesso che si userebbe entrando per telefono. Se scrivi quello con le lettere il programma te lo dice invece di lasciarti provare.

        Una volta dentro è una chiamata come tutte le altre: attesa, conferenza, toni e riaggancio funzionano con i tasti di sempre. Non c'è una modalità riunione da imparare.

        Da qui viene anche la cosa più utile che un centralino può fare con una riunione: se hai un chiamante in linea che deve partecipare, entri in riunione e poi mettete tutti insieme con {K(profile, HotkeyCommand.Conference)}. Il centralino fa da ponte fra il telefono e la riunione.

        Le riunioni che si ripetono — la giunta, il consiglio, la commissione — conviene metterle in rubrica come contatti normali, con tutto l'indirizzo. Da quel momento si chiamano per nome come qualunque altro contatto, e non devi più ricordare undici cifre.

        Il codice d'accesso non viene mai riletto a voce e non resta nelle ultime chiamate: in un centralino dove ci sono altre persone, un codice pronunciato ad alta voce è un codice diffuso.

        Quello che non c'è, perché passa da Zoom e non dal telefono: la chat, la mano alzata, l'elenco dei partecipanti e il comando del microfono dal lato riunione. Hai l'audio e i comandi del tuo telefono.
        """;

    private static string Mancante() =>
        """
        Alcune voci esistono come comando ma non fanno ancora nulla: premendole senti «funzione non ancora disponibile». È deliberato che lo dicano, invece di restare in silenzio.

        - Storico degli annunci

        Nel frattempo, per rileggere un annuncio appena passato c'è «ripeti l'ultimo annuncio».

        Anche parcheggio della chiamata, risposta per assente e conversazione in tempo reale, quella che si digita mentre l'altro legge, non sono ancora disponibili.
        """;
}
