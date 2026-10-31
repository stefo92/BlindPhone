using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Speech;

/// <summary>
/// I testi della scheda della chiamata nella finestra principale: con chi è collegato
/// l'operatore, chi sta squillando, chi sta aspettando in attesa, e i cronometri.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sta accanto a <see cref="AnnouncementTexts"/> per la stessa ragione per cui
/// quella classe esiste</b>, ma dice le cose in un altro modo. Gli annunci si
/// ascoltano una volta e passano: rispondono a «che cosa è appena successo». Queste
/// due righe si guardano e restano: rispondono a «come sono messo adesso». Sono
/// l'equivalente delle spie di linea di un centralino tradizionale, e servono a chi
/// vede lo schermo — il collega che affianca l'operatore nelle prime settimane, il
/// responsabile che passa dalla postazione, l'operatore ipovedente che lavora con
/// l'ingrandimento.
/// </para>
/// <para>
/// <b>Le differenze dai testi parlati non sono stilistiche.</b> Qui i numeri restano
/// numeri, perché si leggono con gli occhi e la lettura cifra per cifra occuperebbe
/// mezza riga senza aggiungere nulla. I nomi sono quelli anagrafici e non la resa
/// fonetica, che a schermo comparirebbe come un errore di ortografia. E le durate
/// hanno la forma di un orologio invece che di una frase.
/// </para>
/// <para>
/// Vivono nel core e non nel code-behind della finestra perché sono testo mostrato
/// all'utente, che in questo prodotto sta tutto in un posto solo e si verifica con
/// i test invece che a occhio.
/// </para>
/// </remarks>
public static class CallBoardTexts
{
    /// <summary>Testo della casella del nome, quando non c'è nessuno in linea.</summary>
    public const string NoConnectedCall = "Nessuna chiamata";

    /// <summary>Testo della casella delle attese, quando non c'è nessuno in attesa.</summary>
    public const string NoHeldCall = "In attesa: nessuna chiamata";

    /// <summary>
    /// Orologio del turno, per la riga di intestazione della finestra.
    /// </summary>
    /// <param name="inService">Tempo passato alla postazione, al netto delle pause.</param>
    /// <param name="currentPause">
    /// Durata della pausa in corso, oppure <see cref="TimeSpan.Zero"/> se la postazione
    /// è in servizio.
    /// </param>
    /// <returns>"In servizio: 2:14:07" oppure "IN PAUSA da 0:04:31".</returns>
    /// <remarks>
    /// In pausa il testo cambia del tutto invece di aggiungere una parola: chi guarda
    /// lo schermo di sfuggita legge la forma della riga prima del suo contenuto, e due
    /// stati che si distinguono per un dettaglio si confondono. Le maiuscole fanno il
    /// resto, insieme al colore del riquadro — che da solo non basterebbe mai, perché
    /// nessuna informazione può stare nel solo colore.
    /// </remarks>
    public static string ServiceClock(TimeSpan inService, TimeSpan currentPause)
        => currentPause > TimeSpan.Zero
            ? $"IN PAUSA da {ClockDuration(currentPause)}"
            : $"In servizio: {ClockDuration(inService)}";

    /// <summary>
    /// Con chi è collegato l'operatore in questo momento: il nome grande della scheda
    /// della chiamata.
    /// </summary>
    /// <param name="active">Chiamata in conversazione, se c'è.</param>
    /// <param name="conferenced">
    /// Partecipanti alla conferenza a tre, se aperta. Ha la precedenza sulla chiamata
    /// attiva: in conferenza <c>active</c> vale <c>null</c> pur essendoci due persone
    /// in linea, e guardare solo quella mostrerebbe «nessuna chiamata» nel momento di
    /// massimo impegno della postazione.
    /// </param>
    /// <param name="outgoing">
    /// Chiamata uscente che sta ancora squillando dall'altra parte. Conta solo se non
    /// c'è nessuno in linea: è la linea che l'operatore ha appena impegnato.
    /// </param>
    /// <returns>Per esempio "Mario Rossi - 4021", oppure <see cref="NoConnectedCall"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Niente più «Collegato a:» davanti.</b> Prima la stessa persona compariva tre
    /// volte — nel riquadro verde in cima, nella riga grande della scheda e in questa
    /// casella — e la casella aveva bisogno dell'etichetta per distinguersi dalle altre
    /// due. Adesso è l'unica, è lei il nome grande, e la relazione la dice la
    /// didascalia sopra (<see cref="ConnectedCaption"/>). Allo screen reader la dice il
    /// nome accessibile della casella, «chiamata in conversazione», che non cambia.
    /// </para>
    /// <para>
    /// <b>Dice soltanto chi è in linea</b>, mai chi squilla o chi aspetta: la casella si
    /// chiama «chiamata in conversazione», e chi ci arriva con il tabulatore deve poter
    /// credere a quello che sente. Chi squilla e chi aspetta hanno ciascuno il loro
    /// posto nella stessa scheda.
    /// </para>
    /// <para>
    /// Il numero resta nel testo e non solo nella riga sotto: la casella esiste perché
    /// quel numero si possa selezionare e copiare.
    /// </para>
    /// </remarks>
    public static string Connected(Call? active, IReadOnlyList<Call>? conferenced = null, Call? outgoing = null)
    {
        if (conferenced is { Count: > 0 })
        {
            return string.Join(" e ", conferenced.Select(c => Identity(c.RemoteParty)));
        }

        var party = (active ?? outgoing)?.RemoteParty;

        return party is null ? NoConnectedCall : Identity(party);
    }

    /// <summary>
    /// Didascalia sopra il nome grande: in che rapporto si è con chi c'è scritto.
    /// </summary>
    /// <param name="active">Chiamata in conversazione, se c'è.</param>
    /// <param name="conferenced">Partecipanti alla conferenza a tre, se aperta.</param>
    /// <param name="outgoing">Chiamata uscente che sta squillando dall'altra parte.</param>
    /// <remarks>
    /// Distingue la chiamata uscente che squilla ancora da quella in cui si parla: il
    /// nome è lo stesso, ma chi passa dalla postazione e legge «in linea con» crede che
    /// la conversazione sia cominciata.
    /// </remarks>
    public static string ConnectedCaption(Call? active, IReadOnlyList<Call>? conferenced = null, Call? outgoing = null)
    {
        if (conferenced is { Count: > 0 }) return "In conferenza a tre con";
        if (active is not null) return "In linea con";

        return outgoing is not null ? "Chiamata in uscita, squilla da" : "Chiamata in corso";
    }

    /// <summary>
    /// Riga sotto il nome grande: l'ufficio di chi è in linea e la coda da cui è arrivato.
    /// </summary>
    /// <param name="active">Chiamata in conversazione, se c'è.</param>
    /// <param name="outgoing">Chiamata uscente che sta squillando dall'altra parte.</param>
    /// <returns>Per esempio "Ufficio tecnico · coda Anagrafe", oppure stringa vuota.</returns>
    /// <remarks>
    /// Il numero non c'è perché sta già nella casella sopra, dove si può copiare. In
    /// conferenza la riga resta vuota: due uffici in fila non dicono a chi appartengono.
    /// </remarks>
    public static string ConnectedDetail(Call? active, Call? outgoing = null)
    {
        var call = active ?? outgoing;
        if (call is null) return string.Empty;

        var parti = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(call.RemoteParty.Department)) parti.Add(call.RemoteParty.Department!.Trim());
        if (!string.IsNullOrWhiteSpace(call.QueueName)) parti.Add($"coda {call.QueueName}");

        return string.Join(" · ", parti);
    }

    /// <summary>
    /// Chi è in attesa, con il numero e da quanto tempo.
    /// </summary>
    /// <param name="held">Chiamate in attesa.</param>
    /// <param name="now">Istante corrente.</param>
    /// <returns>Per esempio "In attesa: Mario Rossi - 4021 - 0:01:23".</returns>
    /// <remarks>
    /// <para>
    /// <b>Si mostra per esteso chi aspetta da più tempo</b>, perché è quello che si
    /// rischia di dimenticare, ed è la riga su cui si agisce.
    /// </para>
    /// <para>
    /// <b>Le altre si contano invece di elencarle.</b> La casella sta in fondo alla
    /// scheda della chiamata: un'attesa per esteso per ogni chiamata la allungherebbe a
    /// ogni messa in attesa, e con la finestra stretta spingerebbe giù la coda e i
    /// colleghi proprio mentre si cerca a chi passare qualcuno. Il conteggio invece ci
    /// sta sempre e dice che esistono. Chi vuole l'elenco completo ha il comando di
    /// riepilogo dello stato, che le nomina tutte.
    /// </para>
    /// </remarks>
    public static string OnHold(IReadOnlyList<Call> held, DateTimeOffset now)
    {
        if (held.Count == 0) return NoHeldCall;

        var prima = held.OrderByDescending(c => c.HoldDuration(now)).First();
        var testo = $"In attesa: {Identity(prima.RemoteParty)} - {ClockDuration(prima.HoldDuration(now))}";

        if (held.Count == 1) return testo;

        var altre = held.Count - 1;
        return $"{testo}   (+{altre} in attesa)";
    }

    /// <summary>
    /// Chi sta chiamando, per chi guarda: la riga che compare nella scheda della
    /// chiamata finché squilla.
    /// </summary>
    /// <param name="party">Chiamante, già passato dalla risoluzione in rubrica.</param>
    /// <param name="queueName">Coda da cui arriva la chiamata, se ne arriva da una.</param>
    /// <returns>Per esempio "Chiamata da: Mario Rossi (4021)".</returns>
    /// <remarks>
    /// <para>
    /// <b>Dice le stesse cose di <see cref="AnnouncementTexts.IncomingCall"/>, e nello
    /// stesso ordine</b>, perché l'operatore che vede e il collega che ascolta devono
    /// avere davanti la stessa chiamata. Cambia solo la resa: il nome è quello
    /// anagrafico e non la sua trascrizione fonetica, e il numero resta in cifre
    /// invece di essere letto una per una.
    /// </para>
    /// <para>
    /// <b>Il numero compare accanto a qualunque nome</b>, venga dalla rubrica o
    /// dall'intestazione SIP, ed è la stessa regola dell'annuncio parlato: il perché
    /// sta in <see cref="IncomingIdentity"/>. Chi non ha un nome compare con il solo
    /// numero, e non ha un nome nemmeno chi ha il numero scritto al posto del nome.
    /// </para>
    /// </remarks>
    public static string IncomingCall(CallParty party, string? queueName = null)
    {
        var chi = IncomingIdentity(party);

        return string.IsNullOrWhiteSpace(queueName)
            ? $"Chiamata da: {chi}"
            : $"Chiamata da: {chi}  -  coda {queueName}";
    }

    /// <summary>
    /// Chi c'è dall'altra parte, con il numero fra parentesi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il numero compare sempre, quando c'è.</b> Prima veniva mostrato solo per i
    /// contatti in rubrica: con un nome dichiarato dal centralino e un numero
    /// sconosciuto restava il solo nome. Sul campo quel caso è la regola e non
    /// l'eccezione — il centralino annuncia le chiamate esterne come
    /// <c>"Chiamate esterne"</c> — e il riquadro diceva «Chiamata da: Chiamate
    /// esterne», cioè nulla di utilizzabile.
    /// </para>
    /// <para>
    /// <b>Il nome è <see cref="CallParty.MeaningfulName"/>, non quello grezzo.</b> La
    /// regola qui sopra, in vigore dalla 1.0.7, metteva il numero accanto a qualunque
    /// nome senza chiedersi se il nome fosse già il numero: quando il centralino scrive
    /// il numero al posto del nome, o una voce di rubrica senza nominativo presta il
    /// proprio, il riquadro diceva «Chiamata da: 3491234567 (3491234567)».
    /// </para>
    /// </remarks>
    private static string IncomingIdentity(CallParty party)
    {
        var nome = party.MeaningfulName;
        var hasNumber = !string.IsNullOrWhiteSpace(party.Number);

        if (nome is null)
        {
            return hasNumber ? party.Number : "numero sconosciuto";
        }

        return hasNumber ? $"{nome} ({party.Number})" : nome;
    }

    /// <summary>
    /// Nome e numero di una controparte, nella forma che si legge a schermo.
    /// </summary>
    /// <param name="party">Controparte.</param>
    /// <returns>"Mario Rossi - 4021", oppure il solo dato disponibile.</returns>
    /// <remarks>
    /// <para>
    /// Si usa il nome visualizzato e non <see cref="CallParty.SpeechName"/>:
    /// quest'ultimo può contenere la resa fonetica del cognome, che serve alla sintesi
    /// vocale e a schermo comparirebbe come un errore di ortografia — "Cekerini" al
    /// posto di "Ceccherini".
    /// </para>
    /// <para>
    /// Del nome visualizzato conta <see cref="CallParty.MeaningfulName"/>, come nel
    /// riquadro della chiamata: un numero scritto al posto del nome produceva
    /// «Collegato a: 3491234567 - 3491234567».
    /// </para>
    /// </remarks>
    public static string Identity(CallParty party)
    {
        var nome = party.MeaningfulName;
        var hasNumber = !string.IsNullOrWhiteSpace(party.Number);

        if (nome is not null && hasNumber) return $"{nome} - {party.Number}";
        if (nome is not null) return nome;
        if (hasNumber) return party.Number;

        return "sconosciuto";
    }

    /// <summary>
    /// Durata breve, per un tempo che si misura in secondi: minuti e secondi.
    /// </summary>
    /// <param name="duration">Durata da mostrare.</param>
    /// <returns>Per esempio "0:12", oppure "1:05".</returns>
    /// <remarks>
    /// Serve al cronometro dello squillo, nella fascia di stato della scheda della
    /// chiamata. Lì <see cref="ClockDuration"/> mostrerebbe "0:00:12": tre gruppi di
    /// cifre per un numero che non supera quasi mai il minuto. Le ore non compaiono
    /// perché uno squillo che duri un'ora non esiste: se comparisse un numero enorme,
    /// sarebbe comunque più leggibile di "1:00:00" travestito da normalità.
    /// </remarks>
    public static string ShortDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        return $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }

    /// <summary>
    /// Durata per il cronometro della fascia di stato: minuti e secondi, con le ore
    /// davanti solo quando ci sono.
    /// </summary>
    /// <param name="duration">Durata da mostrare.</param>
    /// <returns>Per esempio "3:42", oppure "1:02:07".</returns>
    /// <remarks>
    /// La fascia misura cose diverse — uno squillo, una conversazione, un'attesa, una
    /// pausa — e solo le ultime tre arrivano all'ora. Sotto l'ora "0:03:42" avrebbe un
    /// gruppo di cifre che non dice niente; sopra, "62:07" costringerebbe a fare il
    /// conto. Le due forme non si confondono perché la prima ha un solo due punti.
    /// </remarks>
    public static string BandDuration(TimeSpan duration)
        => duration >= TimeSpan.FromHours(1) ? ClockDuration(duration) : ShortDuration(duration);

    /// <summary>
    /// Durata in forma di orologio: ore, minuti e secondi separati dai due punti.
    /// </summary>
    /// <param name="duration">Durata da mostrare.</param>
    /// <returns>Per esempio "0:01:23".</returns>
    /// <remarks>
    /// <para>
    /// Forma numerica e non parlata: qui si legge con gli occhi, e "0:01:23" si
    /// afferra con un'occhiata mentre "1 minuto e 23" va letto per intero. La versione
    /// parlata resta <see cref="SpeechFormatter.Duration"/>, che si usa negli annunci.
    /// </para>
    /// <para>
    /// Le ore non si troncano a ventiquattro e non compare il numero di giorni: una
    /// chiamata dimenticata in attesa dalla sera prima deve mostrare un tempo
    /// palesemente anomalo, non ricominciare da zero come farebbe la formattazione
    /// standard di <see cref="TimeSpan"/>.
    /// </para>
    /// </remarks>
    public static string ClockDuration(TimeSpan duration)
    {
        // Uno scarto di orologio non deve mai produrre "meno un secondo": e' il
        // valore che fa dubitare di tutto il resto della riga.
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
