using System.Security.Cryptography;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Quello che la postazione ricorda delle proprie statistiche fra un avvio e l'altro.
/// </summary>
/// <remarks>
/// <para>
/// Esiste solo mentre le statistiche sono accese: spegnendole il file che lo contiene
/// viene cancellato, identificativo compreso, e riaccendendole la postazione riparte
/// da un identificativo nuovo. Non c'è modo di ricollegare i due periodi, ed è voluto.
/// </para>
/// <para>
/// I comandi e gli errori del giorno stanno qui e non in memoria perché il posto
/// operatore si chiude e si riapre più volte al giorno: senza, ogni riapertura
/// rimanderebbe gli stessi comandi, e l'uso di un giorno risulterebbe moltiplicato per
/// il numero di riavvii.
/// </para>
/// </remarks>
public sealed record TelemetryState
{
    /// <summary>Identificativo anonimo dell'installazione.</summary>
    /// <remarks>Vedi <see cref="TelemetryDiary.NewInstallationId"/>.</remarks>
    public string InstallationId { get; init; } = string.Empty;

    /// <summary>
    /// Versione con cui le statistiche sono partite l'ultima volta; vuota se non sono
    /// mai partite.
    /// </summary>
    public string? LastVersion { get; init; }

    /// <summary>Ultimo giorno per cui è partita l'attività giornaliera.</summary>
    public DateOnly? LastActiveDay { get; init; }

    /// <summary>Giorno a cui si riferiscono i comandi e gli errori qui sotto.</summary>
    public DateOnly? Day { get; init; }

    /// <summary>Comandi usati nel giorno, come «comando|origine».</summary>
    public IReadOnlyList<string> CommandsOfDay { get; init; } = [];

    /// <summary>Errori già segnalati nel giorno, come «tipo|origine».</summary>
    public IReadOnlyList<string> ErrorsOfDay { get; init; } = [];

    /// <summary>Ultima versione nuova segnalata, per non ridirla a ogni avvio.</summary>
    public string? ReportedUpdate { get; init; }

    /// <summary>Eventi composti e non ancora accettati dal server, dal più vecchio.</summary>
    public IReadOnlyList<TelemetryEvent> Pending { get; init; } = [];
}

/// <summary>
/// Decide che cosa parte e quando: il diario giornaliero delle statistiche d'uso.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non sa niente di rete e di file</b>, e non ha un orologio suo: riceve il giorno da
/// chi lo chiama. È la parte che si deve poter provare su un mese di giorni in pochi
/// millisecondi — l'uso dei comandi che arriva il giorno dopo, il cambio di data a
/// programma aperto, l'errore che si ripete mille volte e parte una volta sola.
/// </para>
/// <para>
/// <b>I metodi di registrazione sono a buon mercato</b>: un blocco, un insieme, nessuna
/// scrittura. Li chiama il thread dell'interfaccia a ogni comando, e un tasto non deve
/// aspettare le statistiche.
/// </para>
/// </remarks>
public sealed class TelemetryDiary
{
    /// <summary>Quanti eventi tenere in attesa al massimo.</summary>
    /// <remarks>
    /// Una postazione che non raggiunge il server per settimane non deve accumulare un
    /// file che cresce senza fine: oltre questo numero si scartano i più vecchi, che
    /// sono anche quelli che dicono meno.
    /// </remarks>
    public const int MaxPending = 500;

    private readonly object _gate = new();
    private readonly string _version;
    private readonly List<TelemetryEvent> _pending;
    private readonly HashSet<string> _commands;
    private readonly HashSet<string> _errors;
    private TelemetryState _state;

    /// <summary>Riprende un diario salvato, o ne comincia uno.</summary>
    /// <param name="state">Stato salvato; con un identificativo non valido se ne crea uno nuovo.</param>
    /// <param name="version">Versione di BlindPhone in esecuzione.</param>
    public TelemetryDiary(TelemetryState? state, string version)
    {
        _version = version;

        // Un identificativo che non ha la forma attesa non e' di questo programma: si
        // riparte da zero, come una postazione nuova, invece di mandare al server un
        // testo che qualcuno ha scritto nel file.
        _state = state is not null && IsInstallationId(state.InstallationId)
            ? state
            : new TelemetryState { InstallationId = NewInstallationId() };

        _pending = [.. _state.Pending.Where(e => TelemetryCatalog.Check(e).Count == 0)];
        _commands = new HashSet<string>(_state.CommandsOfDay, StringComparer.Ordinal);
        _errors = new HashSet<string>(_state.ErrorsOfDay, StringComparer.Ordinal);
    }

    /// <summary>Identificativo anonimo dell'installazione.</summary>
    public string InstallationId => _state.InstallationId;

    /// <summary>Quanti eventi aspettano di partire.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate) return _pending.Count;
        }
    }

    /// <summary>
    /// Crea un identificativo d'installazione: 128 bit casuali, scritti con sole lettere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Casuale e basta: non deriva dal nome della macchina, dall'utente, dall'indirizzo
    /// della scheda di rete o da qualunque altra cosa si possa ricalcolare. Chi ha in
    /// mano il server non ha modo di risalire a quale postazione sia.
    /// </para>
    /// <para>
    /// <b>Senza cifre</b>, ed è l'unica stranezza: ogni mezzo byte diventa una lettera
    /// fra la a e la p. Con le cifre esadecimali di un GUID capita circa una volta su
    /// quaranta di avere otto cifre di fila, che è la forma di un numero di telefono — e la regola
    /// «nel pacchetto non compaiono sei cifre di fila» deve valere senza eccezioni per
    /// essere una prova.
    /// </para>
    /// </remarks>
    public static string NewInstallationId()
    {
        Span<byte> casuali = stackalloc byte[16];
        RandomNumberGenerator.Fill(casuali);

        return string.Create(32, casuali.ToArray(), static (lettere, byteCasuali) =>
        {
            for (var i = 0; i < byteCasuali.Length; i++)
            {
                lettere[2 * i] = (char)('a' + (byteCasuali[i] >> 4));
                lettere[(2 * i) + 1] = (char)('a' + (byteCasuali[i] & 0x0F));
            }
        });
    }

    /// <summary>Indica se un testo ha la forma di un identificativo d'installazione.</summary>
    /// <param name="value">Testo da verificare.</param>
    public static bool IsInstallationId(string? value)
        => value is { Length: 32 } && value.All(c => c is >= 'a' and <= 'p');

    /// <summary>
    /// Le statistiche cominciano: all'avvio del programma, o quando vengono accese.
    /// </summary>
    /// <param name="today">Giorno corrente.</param>
    /// <param name="facts">Stato della postazione.</param>
    /// <remarks>
    /// L'ordine degli eventi è quello in cui le cose sono successe: prima la nascita
    /// dell'installazione o il cambio di versione, poi i comandi dei giorni passati, poi
    /// l'avvio di oggi.
    /// </remarks>
    public void Begin(DateOnly today, TelemetryFacts facts)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_state.LastVersion))
            {
                Add(TelemetryCatalog.Installation(today, facts));
            }
            else if (!string.Equals(_state.LastVersion, _version, StringComparison.Ordinal))
            {
                Add(TelemetryCatalog.UpdateInstalled(today, _version, _state.LastVersion));
            }

            _state = _state with { LastVersion = _version };

            CloseDayIfOver(today);
            Add(TelemetryCatalog.Startup(today, facts));
            MarkActive(today, facts);
        }
    }

    /// <summary>
    /// Il tempo passa: chiude il giorno finito e segna quello nuovo come attivo.
    /// </summary>
    /// <param name="today">Giorno corrente.</param>
    /// <param name="facts">Chi compone lo stato della postazione, chiamato solo se serve.</param>
    /// <remarks>
    /// Serve al programma che resta aperto per settimane, che è il caso normale su un
    /// posto operatore: senza, una postazione accesa dal lunedì risulterebbe attiva solo
    /// il lunedì.
    /// </remarks>
    public void Advance(DateOnly today, Func<TelemetryFacts> facts)
    {
        lock (_gate)
        {
            CloseDayIfOver(today);

            // Lo stato si chiede solo quando serve: chi lo compone interroga lo screen
            // reader, e farlo a ogni giro del ciclo per non usarlo sarebbe uno spreco.
            if (_state.LastActiveDay != today) MarkActive(today, facts());
        }
    }

    /// <summary>Annota un comando dato dall'operatore.</summary>
    /// <param name="today">Giorno corrente.</param>
    /// <param name="command">Comando.</param>
    /// <param name="origin">Da dove è partito.</param>
    /// <remarks>
    /// Non compone niente: il comando diventa un evento solo quando il giorno è finito,
    /// una volta per coppia comando e origine, senza l'ora e senza il numero di volte.
    /// </remarks>
    public void RecordCommand(DateOnly today, HotkeyCommand command, CommandOrigin origin)
    {
        lock (_gate)
        {
            CloseDayIfOver(today);
            _commands.Add($"{command}|{origin}");
        }
    }

    /// <summary>Annota un errore non gestito.</summary>
    /// <param name="today">Giorno corrente.</param>
    /// <param name="typeName">Tipo dell'eccezione, da <see cref="TelemetryCatalog.TypeNameOf"/>.</param>
    /// <param name="origin">Da dove è arrivato.</param>
    /// <param name="fatal">Se ha terminato il processo.</param>
    /// <returns><c>true</c> se l'errore diventa un evento, <c>false</c> se era già stato detto oggi.</returns>
    /// <remarks>
    /// Un errore nel calcolo del layout si ripete a ogni ridisegno: il registro ne ha
    /// contati milleduecento in pochi secondi. Al server ne arriva uno al giorno per tipo
    /// e origine, che è quello che serve a sapere che c'è.
    /// </remarks>
    public bool RecordError(DateOnly today, string typeName, ErrorOrigin origin, bool fatal)
    {
        lock (_gate)
        {
            CloseDayIfOver(today);

            if (!_errors.Add($"{typeName}|{origin}")) return false;

            Add(TelemetryCatalog.Error(today, _version, typeName, origin, fatal));
            return true;
        }
    }

    /// <summary>Annota una versione nuova trovata sul server degli aggiornamenti.</summary>
    /// <param name="today">Giorno corrente.</param>
    /// <param name="newVersion">Versione trovata.</param>
    /// <remarks>
    /// Una volta per versione: il controllo si ripete a ogni avvio e trova la stessa
    /// versione finché qualcuno non la installa.
    /// </remarks>
    public void RecordUpdateFound(DateOnly today, string newVersion)
    {
        lock (_gate)
        {
            if (string.Equals(_state.ReportedUpdate, newVersion, StringComparison.Ordinal)) return;

            _state = _state with { ReportedUpdate = newVersion };
            Add(TelemetryCatalog.UpdateFound(today, _version, newVersion));
        }
    }

    /// <summary>I primi eventi in attesa, senza toglierli.</summary>
    /// <param name="max">Quanti al massimo.</param>
    public IReadOnlyList<TelemetryEvent> PeekBatch(int max)
    {
        lock (_gate) return [.. _pending.Take(max)];
    }

    /// <summary>Toglie i primi eventi, dopo che il server li ha accettati o rifiutati per sempre.</summary>
    /// <param name="count">Quanti.</param>
    public void Remove(int count)
    {
        lock (_gate) _pending.RemoveRange(0, Math.Min(count, _pending.Count));
    }

    /// <summary>Fotografia da salvare.</summary>
    public TelemetryState Snapshot()
    {
        lock (_gate)
        {
            return _state with
            {
                CommandsOfDay = [.. _commands.Order(StringComparer.Ordinal)],
                ErrorsOfDay = [.. _errors.Order(StringComparer.Ordinal)],
                Pending = [.. _pending],
            };
        }
    }

    /// <summary>
    /// Se il giorno dei comandi annotati è finito, li trasforma in eventi datati a quel
    /// giorno e ricomincia.
    /// </summary>
    private void CloseDayIfOver(DateOnly today)
    {
        if (_state.Day == today) return;

        if (_state.Day is { } giorno)
        {
            foreach (var voce in _commands.Order(StringComparer.Ordinal))
            {
                var parti = voce.Split('|');

                // Una voce che non si rilegge viene dal file, non da questo programma:
                // si lascia cadere.
                if (parti.Length != 2
                    || !Enum.TryParse<HotkeyCommand>(parti[0], out var comando)
                    || !Enum.TryParse<CommandOrigin>(parti[1], out var origine))
                {
                    continue;
                }

                Add(TelemetryCatalog.CommandUsed(giorno, _version, comando, origine));
            }
        }

        _commands.Clear();
        _errors.Clear();
        _state = _state with { Day = today };
    }

    private void MarkActive(DateOnly today, TelemetryFacts facts)
    {
        if (_state.LastActiveDay == today) return;

        _state = _state with { LastActiveDay = today };
        Add(TelemetryCatalog.DailyActivity(today, facts));
    }

    private void Add(TelemetryEvent telemetryEvent)
    {
        _pending.Add(telemetryEvent);

        if (_pending.Count > MaxPending) _pending.RemoveRange(0, _pending.Count - MaxPending);
    }
}
