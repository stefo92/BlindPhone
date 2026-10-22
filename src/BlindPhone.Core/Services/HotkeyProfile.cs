using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Un insieme completo di scorciatoie, salvabile e commutabile.
/// </summary>
/// <remarks>
/// I profili servono a due cose. La prima è personale: operatori diversi hanno
/// abitudini diverse e la stessa postazione può essere usata a turni alterni. La
/// seconda è di migrazione: chi arriva da un altro softphone porta con sé la
/// memoria muscolare di quel prodotto, e un profilo che ne imita la mappa riduce di
/// settimane il periodo in cui commette errori sotto pressione.
/// </remarks>
public sealed class HotkeyProfile
{
    private readonly Dictionary<(HotkeyCommand Command, HotkeyScope Scope), HotkeyBinding> _bindings = [];

    /// <summary>Crea un profilo vuoto.</summary>
    /// <param name="name">Nome del profilo, mostrato e pronunciato.</param>
    public HotkeyProfile(string name) => Name = name;

    /// <summary>Nome del profilo.</summary>
    public string Name { get; set; }

    /// <summary>
    /// Comandi che questo profilo ha già conosciuto, anche se non ne ha una
    /// combinazione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serve a distinguere due situazioni che altrimenti sono identiche: un comando
    /// <b>mai visto</b>, perché aggiunto da una versione successiva a quella in cui il
    /// profilo è stato salvato, e un comando che l'operatore ha <b>deliberatamente
    /// lasciato senza tasto</b>.
    /// </para>
    /// <para>
    /// Al primo si assegna la combinazione predefinita, altrimenti una funzione nuova
    /// resterebbe invisibile per sempre su tutte le postazioni già in servizio. Al
    /// secondo non si tocca nulla: rimettere un tasto che l'operatore ha tolto è un
    /// modo sicuro di fargli perdere fiducia nella configurazione.
    /// </para>
    /// </remarks>
    public HashSet<HotkeyCommand> KnownCommands { get; } = [];

    /// <summary>Tutte le assegnazioni del profilo.</summary>
    public IReadOnlyCollection<HotkeyBinding> Bindings => _bindings.Values;

    /// <summary>Assegna una combinazione a un comando, sostituendo l'eventuale precedente.</summary>
    /// <param name="binding">Assegnazione da registrare.</param>
    public void Set(HotkeyBinding binding) => _bindings[(binding.Command, binding.Scope)] = binding;

    /// <summary>Rimuove l'assegnazione di un comando in un dato ambito.</summary>
    /// <param name="command">Comando da liberare.</param>
    /// <param name="scope">Ambito interessato.</param>
    public void Remove(HotkeyCommand command, HotkeyScope scope) => _bindings.Remove((command, scope));

    /// <summary>Restituisce l'assegnazione di un comando, se presente.</summary>
    /// <param name="command">Comando cercato.</param>
    /// <param name="scope">Ambito.</param>
    public HotkeyBinding? Get(HotkeyCommand command, HotkeyScope scope)
        => _bindings.GetValueOrDefault((command, scope));

    /// <summary>Svuota il profilo.</summary>
    public void Clear() => _bindings.Clear();

    /// <summary>Sostituisce tutte le assegnazioni con quelle indicate.</summary>
    /// <param name="bindings">Nuove assegnazioni.</param>
    public void ReplaceAll(IEnumerable<HotkeyBinding> bindings)
    {
        _bindings.Clear();
        foreach (var binding in bindings) Set(binding);
    }

    /// <summary>Crea una copia indipendente del profilo.</summary>
    /// <param name="name">Nome della copia.</param>
    /// <remarks>
    /// Il pannello dei tasti rapidi lavora sempre su una copia: le modifiche non
    /// devono avere effetto finché non vengono salvate, altrimenti chi apre il
    /// pannello per guardare come è configurato un comando e cambia idea si ritrova
    /// la postazione riconfigurata. E le assegnazioni sono la cosa che un operatore
    /// non vedente verifica <b>usandole</b>, non guardandole.
    /// </remarks>
    public HotkeyProfile Clone(string? name = null)
    {
        var copy = new HotkeyProfile(name ?? Name);
        copy.ReplaceAll(_bindings.Values);
        copy.KnownCommands.UnionWith(KnownCommands);
        return copy;
    }

    /// <summary>
    /// Assume nome e assegnazioni di un altro profilo, restando la stessa istanza.
    /// </summary>
    /// <param name="other">Profilo da copiare.</param>
    /// <remarks>
    /// <b>L'istanza deve restare la stessa</b>: la finestra principale e il
    /// registratore delle scorciatoie globali ne tengono un riferimento preso
    /// all'avvio. Sostituire l'oggetto nel contenitore delle dipendenze lascerebbe
    /// quei due a lavorare sul profilo vecchio, con l'effetto che le nuove
    /// combinazioni funzionano nel pannello e non nel telefono.
    /// </remarks>
    public void CopyFrom(HotkeyProfile other)
    {
        Name = other.Name;
        ReplaceAll(other.Bindings);

        KnownCommands.Clear();
        KnownCommands.UnionWith(other.KnownCommands);
    }

    /// <summary>
    /// Assegna la combinazione predefinita ai comandi che il profilo non ha mai
    /// conosciuto.
    /// </summary>
    /// <returns>I comandi a cui è stata assegnata una combinazione.</returns>
    /// <remarks>
    /// <para>
    /// È la migrazione che serve quando una versione nuova aggiunge un comando. Senza,
    /// il comando resta senza tasto su tutte le postazioni già in servizio, e per
    /// l'operatore si manifesta nel modo peggiore: preme il tasto che la
    /// documentazione indica e non succede nulla. È esattamente quello che è successo
    /// con F1 e la guida in linea.
    /// </para>
    /// <para>
    /// Tre vincoli, tutti necessari perché la migrazione non faccia danni:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   Si tocca solo un comando che il profilo <b>non ha mai conosciuto</b>: se
    ///   l'operatore gli ha tolto il tasto di proposito, resta senza.
    ///   </description></item>
    ///   <item><description>
    ///   Si tocca solo un comando <b>senza alcuna combinazione</b>, in nessun ambito:
    ///   una scelta già fatta non viene mai sovrascritta.
    ///   </description></item>
    ///   <item><description>
    ///   Non si ruba una combinazione <b>già in uso</b> per un altro comando. Meglio
    ///   una funzione nuova senza tasto che un tasto che cambia significato sotto le
    ///   dita di chi lo usa da mesi.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<HotkeyCommand> AddMissingDefaults()
    {
        var defaults = CreateDefault();
        var added = new List<HotkeyCommand>();

        foreach (var command in Enum.GetValues<HotkeyCommand>())
        {
            if (!KnownCommands.Add(command)) continue;

            // Il comando era gia' presente in questo profilo con un suo tasto: la
            // registrazione fra i conosciuti basta, non c'e' nulla da assegnare.
            if (Get(command, HotkeyScope.Local) is not null) continue;
            if (Get(command, HotkeyScope.Global) is not null) continue;

            foreach (var scope in new[] { HotkeyScope.Local, HotkeyScope.Global })
            {
                if (defaults.Get(command, scope) is not { } candidate) continue;
                if (FindConflict(candidate) is not null) continue;

                Set(candidate);

                if (!added.Contains(command)) added.Add(command);
            }
        }

        return added;
    }

    /// <summary>
    /// Dichiara che il profilo conosce tutti i comandi esistenti.
    /// </summary>
    /// <remarks>
    /// Si usa sui profili creati dal codice, che per definizione sono aggiornati:
    /// senza, alla prima lettura la migrazione crederebbe che non conoscano nulla.
    /// </remarks>
    public void MarkAllCommandsKnown() => KnownCommands.UnionWith(Enum.GetValues<HotkeyCommand>());

    /// <summary>Cerca il comando associato a una combinazione.</summary>
    /// <param name="key">Nome del tasto.</param>
    /// <param name="modifiers">Modificatori premuti.</param>
    /// <param name="scope">Ambito in cui cercare.</param>
    public HotkeyCommand? Resolve(string key, HotkeyModifiers modifiers, HotkeyScope scope)
    {
        foreach (var binding in _bindings.Values)
        {
            if (binding.Scope != scope) continue;
            if (binding.Modifiers != modifiers) continue;
            if (!binding.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

            return binding.Command;
        }

        return null;
    }

    /// <summary>
    /// Verifica se una combinazione è già assegnata a un comando diverso.
    /// </summary>
    /// <param name="candidate">Combinazione da verificare.</param>
    /// <returns>Il comando che la occupa, oppure <c>null</c>.</returns>
    public HotkeyCommand? FindConflict(HotkeyBinding candidate)
    {
        foreach (var binding in _bindings.Values)
        {
            if (binding.Command == candidate.Command) continue;
            if (binding.ConflictKey == candidate.ConflictKey) return binding.Command;
        }

        return null;
    }

    /// <summary>
    /// Costruisce il profilo predefinito descritto in ANALISI-BLINDPHONE.md §10.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Due criteri hanno guidato la scelta dei tasti, e vale la pena ricordarli
    /// perché vincolano anche le modifiche future.
    /// </para>
    /// <para>
    /// <b>Velocità in primo piano.</b> I comandi usati centinaia di volte al giorno
    /// sono tasti funzione singoli: rispondere deve costare una pressione, non tre
    /// dita.
    /// </para>
    /// <para>
    /// <b>Nessun conflitto con gli screen reader.</b> Le versioni globali usano
    /// Control più Maiusc, evitando i modificatori riservati di NVDA (INS e Blocco
    /// maiuscole) e di JAWS (INS). Le combinazioni Control Alt più tasto funzione
    /// sono state scartate perché in passato alcuni driver grafici Intel le usavano
    /// per la rotazione dello schermo, e un operatore non vedente che ruota lo
    /// schermo senza accorgersene crea un problema a chi gli siede accanto.
    /// </para>
    /// </remarks>
    public static HotkeyProfile CreateDefault()
    {
        var profile = new HotkeyProfile("Standard");

        // Gestione chiamata: tasti funzione singoli in primo piano, Control Maiusc
        // per la versione globale.
        AddPair(profile, HotkeyCommand.Answer, "F2");
        AddPair(profile, HotkeyCommand.HangUp, "F3");
        AddPair(profile, HotkeyCommand.ToggleHold, "F4");
        AddPair(profile, HotkeyCommand.Swap, "F5");
        AddPair(profile, HotkeyCommand.Conference, "F7");
        AddPair(profile, HotkeyCommand.FocusDialer, "F8");
        AddPair(profile, HotkeyCommand.Transfer, "F9");

        // INCLUSIONE SU ALT+B, e non su un tasto funzione libero.
        //
        // Alt e una lettera e' una combinazione che non si preme per sbaglio, ed e'
        // esattamente cio' che serve a un comando che entra nella conversazione di
        // altri. La lettera e' l'iniziale di "barge-in", il nome con cui la funzione
        // compare su ogni centralino: e' quella che l'operatore trova nei manuali.
        AddPair(profile, HotkeyCommand.BargeIn, "Alt B");
        AddPair(profile, HotkeyCommand.OpenCallHistory, "F11");

        // RUBRICA SU F6, CODA SU CONTROL F10.
        //
        // La rubrica stava su F10 e la coda su F6. F10 e' pero' un tasto che Windows
        // riserva a se': da solo apre la barra dei menu della finestra. Il posto
        // operatore non ha una barra dei menu, quindi l'unico effetto visibile era il
        // cursore che finiva sul titolo della finestra invece di aprire la rubrica.
        //
        // Il difetto vero — i tasti di sistema letti sotto falso nome — e' corretto in
        // MainWindow, ma la rubrica resta su F6: e' il tasto piu' comodo dei due, e la
        // rubrica si apre molte piu' volte al giorno dell'elenco della coda. La coda
        // prende Control F10, che con il modificatore non attiva alcun menu.
        AddPair(profile, HotkeyCommand.OpenContacts, "F6");

        profile.Set(new HotkeyBinding(
            HotkeyCommand.FocusQueue, "F10", HotkeyModifiers.Control, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.FocusQueue, "F10", HotkeyModifiers.Control | HotkeyModifiers.Shift,
            HotkeyScope.Global));
        AddPair(profile, HotkeyCommand.AnnounceQueueCount, "F12");

        // COLLEGHI IN TURNO SU CONTROL ALT C.
        //
        // "C" come colleghi, accanto alle altre combinazioni Control Alt che riguardano
        // la postazione e non la chiamata in corso. Non e' un comando da usare di
        // fretta: si preme quando si sta decidendo a chi passare una chiamata, cioe'
        // con il chiamante gia' in attesa.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.FocusColleagues, "C", HotkeyModifiers.Control | HotkeyModifiers.Alt,
            HotkeyScope.Local));

        // Doppia pressione dello stesso tasto: gestita dal servizio, non da un
        // binding separato, perche' l'operatore la percepisce come un unico comando.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.AnnounceQueueList, "F12", HotkeyModifiers.Shift, HotkeyScope.Local));

        // Informazione e accessibilita'.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.AnnounceStatusSummary, "F5", HotkeyModifiers.Control, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.AnnounceStatusSummary, "F5", HotkeyModifiers.Control | HotkeyModifiers.Alt,
            HotkeyScope.Global));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.RepeatLastAnnouncement, "F12", HotkeyModifiers.Control, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.OpenAnnouncementHistory, "A", HotkeyModifiers.Control | HotkeyModifiers.Shift,
            HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.AnnounceCallDuration, "T", HotkeyModifiers.Control, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.AnnounceLineQuality, "Q", HotkeyModifiers.Control, HotkeyScope.Local));

        // Stato dell'interno che si sta per chiamare o a cui si sta per passare la
        // chiamata: "I" come informazione sull'interno.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.AnnounceExtensionState, "I", HotkeyModifiers.Control, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.SilenceSpeech, "Escape", HotkeyModifiers.Control, HotkeyScope.Local));

        // La lettura di tutte le scorciatoie era descritta nella scheda dei tasti ma
        // non assegnata: il comando esisteva e non c'era modo di richiamarlo. Se ne e'
        // accorto il pannello dei tasti rapidi, che elenca anche i comandi liberi.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.AnnounceHotkeyList, "K", HotkeyModifiers.Control, HotkeyScope.Local));

        // F1 e' il tasto della guida in ogni programma Windows: assegnarne un altro
        // significherebbe che chi lo preme per abitudine non trova niente.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.OpenHelp, "F1", HotkeyModifiers.None, HotkeyScope.Local));

        // Finestra riposta nell'area di notifica. La riapertura e' solo globale,
        // perche' e' il caso in cui la finestra non c'e' e i comandi locali non
        // arrivano; l'uscita e' solo locale, perche' un programma che si chiude da
        // una combinazione premuta altrove e' un guasto che nessuno sa spiegarsi.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.ShowWindow, "B", HotkeyModifiers.Control | HotkeyModifiers.Shift, HotkeyScope.Global));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.QuitApplication, "Q", HotkeyModifiers.Control | HotkeyModifiers.Shift,
            HotkeyScope.Local));

        // Comandi vari.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.ToggleMute, "M", HotkeyModifiers.Control, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.ToggleMute, "M", HotkeyModifiers.Control | HotkeyModifiers.Shift, HotkeyScope.Global));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.RedialLast, "R", HotkeyModifiers.Control, HotkeyScope.Local));

        // INGRESSO IN RIUNIONE SU ALT Z, e solo locale.
        //
        // Locale perche' non si entra in una riunione da dentro un altro programma:
        // serve il campo di composizione, che c'e' solo qui. Una combinazione globale
        // occuperebbe un tasto di sistema per una funzione che non puo' funzionare
        // senza avere davanti la finestra.
        //
        // Alt Z e' libera, e la Z sta per Zoom. La funzione e' secondaria e il tasto
        // lo rispecchia: fuori mano, non fra quelli che le dita trovano da sole.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.JoinMeeting, "Z", HotkeyModifiers.Alt, HotkeyScope.Local));

        // REGISTRAZIONE DELLA CHIAMATA SU ALT R.
        //
        // "R" come registra, accanto a Control R che richiama: due comandi vicini di
        // nome e lontani per gesto, il che va bene perche' nessuno dei due si preme
        // per sbaglio al posto dell'altro. Alt e non Control perche' Control R era gia'
        // occupato, e perche' la registrazione e' un gesto piu' raro.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.ToggleCallRecording, "R", HotkeyModifiers.Alt, HotkeyScope.Local));

        // Toni per i menu automatici. "D" come DTMF; la sequenza preparata nel campo
        // di composizione si manda con la stessa combinazione piu' Invio.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.ToggleDtmfMode, "D", HotkeyModifiers.Control | HotkeyModifiers.Shift,
            HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.SendDialerAsDtmf, "Return", HotkeyModifiers.Control | HotkeyModifiers.Shift,
            HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.OpenSettings, "OemComma", HotkeyModifiers.Control, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.OpenDiagnostics, "D", HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyScope.Local));

        // Control Alt I come "invia": sta accanto a Control Alt D della diagnostica,
        // perche' sono i due comandi che si usano nella stessa circostanza e nella
        // stessa sequenza — prima si guarda, poi si manda.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.SendSupportPackage, "I", HotkeyModifiers.Control | HotkeyModifiers.Alt,
            HotkeyScope.Local));

        // Control Alt A come "account". Non e' un comando da usare di fretta, quindi
        // non merita un tasto funzione: chi cambia account lo fa a inizio turno.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.OpenSipAccounts, "A", HotkeyModifiers.Control | HotkeyModifiers.Alt,
            HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.FocusActiveCall, "Home", HotkeyModifiers.Control, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.Cancel, "Escape", HotkeyModifiers.None, HotkeyScope.Local));

        // Coda e stato agente.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.ToggleAgentLogin, "L", HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.ToggleAgentPause, "P", HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.AnnounceAgentState, "S", HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyScope.Local));

        // POSTAZIONE FUORI SERVIZIO SU CONTROL ALT PAUSA.
        //
        // Il tasto Pausa e' l'unico della tastiera che non fa nulla in nessun
        // programma: non si preme per sbaglio, non lo usa nessuno screen reader, e il
        // nome dice gia' cosa fa senza doverlo imparare. Sta isolato in alto a destra,
        // lontano dai tasti che si premono di fretta.
        //
        // Anche in versione globale, e qui il motivo e' pratico: la pausa si preme
        // alzandosi dalla sedia, spesso con il cursore in un altro programma, e si
        // toglie rientrando, quando la finestra di BlindPhone puo' essere riposta
        // nell'area di notifica.
        profile.Set(new HotkeyBinding(
            HotkeyCommand.ToggleServicePause, "Pause", HotkeyModifiers.Control | HotkeyModifiers.Alt,
            HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            HotkeyCommand.ToggleServicePause, "Pause",
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
            HotkeyScope.Global));

        // Un profilo appena costruito dal codice conosce, per definizione, tutti i
        // comandi che esistono in questa versione.
        profile.MarkAllCommandsKnown();

        return profile;
    }

    /// <summary>
    /// Costruisce il profilo senza alcuna scorciatoia globale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serve a una situazione concreta e non rara: su una postazione dove un altro
    /// programma ha già registrato nel sistema le combinazioni globali, queste
    /// smettono di funzionare <b>senza dirlo</b> — Windows le consegna a chi le ha
    /// chieste per primo. L'operatore preme e non succede nulla, e non ha modo di
    /// sapere che il tasto è arrivato altrove.
    /// </para>
    /// <para>
    /// Partire da qui e riaggiungere solo le globali che servono è più rapido che
    /// toglierne una ventina a mano, e lascia il posto operatore pienamente
    /// funzionante: in primo piano cambia nulla.
    /// </para>
    /// </remarks>
    public static HotkeyProfile CreateForegroundOnly()
    {
        var profile = CreateDefault();
        profile.Name = "Solo primo piano";

        foreach (var binding in profile.Bindings.Where(b => b.Scope == HotkeyScope.Global).ToList())
        {
            profile.Remove(binding.Command, HotkeyScope.Global);
        }

        return profile;
    }

    /// <summary>Profili di partenza forniti con il prodotto.</summary>
    /// <remarks>
    /// Non sono modificabili sul posto: chi li cambia salva una copia con un nome
    /// proprio. Così un profilo predefinito resta sempre disponibile come punto di
    /// ritorno, che è ciò che serve quando una configurazione personale si rivela
    /// sbagliata durante un turno.
    /// </remarks>
    public static IReadOnlyList<HotkeyProfile> CreateBuiltIn() =>
    [
        CreateDefault(),
        CreateForegroundOnly(),
    ];

    /// <summary>Indica se un nome appartiene a un profilo fornito con il prodotto.</summary>
    /// <param name="name">Nome da verificare.</param>
    public static bool IsBuiltIn(string? name)
        => name is not null
           && CreateBuiltIn().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Assegna un tasto funzione in primo piano e il suo equivalente globale.
    /// </summary>
    private static void AddPair(HotkeyProfile profile, HotkeyCommand command, string key)
    {
        profile.Set(new HotkeyBinding(command, key, HotkeyModifiers.None, HotkeyScope.Local));
        profile.Set(new HotkeyBinding(
            command, key, HotkeyModifiers.Control | HotkeyModifiers.Shift, HotkeyScope.Global));
    }
}
