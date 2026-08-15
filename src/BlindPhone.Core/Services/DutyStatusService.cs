using BlindPhone.Core.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Tiene il conto del turno: da quanto la postazione è in servizio, e da quanto è
/// in pausa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché un operatore non vedente ha bisogno di un orologio del turno.</b> Chi
/// vede lo schermo ha l'ora davanti tutto il giorno e ricostruisce a colpo d'occhio
/// da quanto è seduto. Senza vista, l'unico modo è chiedere l'ora al sistema e fare
/// una sottrazione a mente, ricordandosi a che ora si era cominciato: è un calcolo
/// che nessuno fa venti volte al giorno, e infatti non lo fa nessuno. Il risultato è
/// che le pause si allungano senza che ce ne si accorga, o che non si prendono
/// affatto.
/// </para>
/// <para>
/// <b>Il tempo in servizio è al netto delle pause</b>, e la scelta ha una
/// conseguenza pratica: durante la pausa il primo orologio si ferma e parte il
/// secondo. È il comportamento che rende il numero utilizzabile per quello che serve
/// davvero — sapere quanto si è effettivamente stati alla postazione — invece di
/// misurare da quando il programma è acceso, che è un dato di cui non se ne fa nulla
/// nessuno.
/// </para>
/// <para>
/// Il servizio non possiede timer: come tutto il core, riceve l'istante da chi lo
/// interroga. È ciò che rende verificabile un comportamento che altrimenti si
/// potrebbe collaudare solo aspettando davvero un'ora.
/// </para>
/// </remarks>
public sealed class DutyStatusService
{
    private readonly IClock _clock;
    private readonly Lock _gate = new();

    private DateTimeOffset _serviceStartedAt;
    private DateTimeOffset? _pausedSince;
    private TimeSpan _pausedTotal;

    /// <summary>Crea il servizio.</summary>
    /// <param name="clock">Sorgente del tempo.</param>
    public DutyStatusService(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _serviceStartedAt = _clock.Now;
    }

    /// <summary>Sollevato a ogni entrata o uscita dalla pausa.</summary>
    /// <remarks>
    /// Porta il nuovo stato: <c>true</c> se la postazione è passata in pausa.
    /// </remarks>
    public event EventHandler<bool>? PauseChanged;

    /// <summary>Istante in cui la postazione è entrata in servizio.</summary>
    public DateTimeOffset ServiceStartedAt
    {
        get { lock (_gate) return _serviceStartedAt; }
    }

    /// <summary><c>true</c> se la postazione è in pausa.</summary>
    public bool IsPaused
    {
        get { lock (_gate) return _pausedSince is not null; }
    }

    /// <summary>Fa ripartire il conteggio del turno.</summary>
    /// <param name="now">Istante corrente.</param>
    /// <remarks>
    /// Azzera anche le pause accumulate: è l'inizio di un turno nuovo, non la ripresa
    /// di quello di prima.
    /// </remarks>
    public void Restart(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            _serviceStartedAt = now ?? _clock.Now;
            _pausedSince = null;
            _pausedTotal = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Tempo passato alla postazione, al netto delle pause.
    /// </summary>
    /// <param name="now">Istante corrente.</param>
    /// <remarks>
    /// Durante la pausa resta fermo al valore raggiunto: è quello che rende il numero
    /// un tempo di servizio e non un tempo di accensione.
    /// </remarks>
    public TimeSpan InService(DateTimeOffset? now = null)
    {
        var istante = now ?? _clock.Now;

        lock (_gate)
        {
            var totale = istante - _serviceStartedAt - _pausedTotal;
            if (_pausedSince is { } inizio) totale -= istante - inizio;

            return totale < TimeSpan.Zero ? TimeSpan.Zero : totale;
        }
    }

    /// <summary>
    /// Da quanto dura la pausa in corso, oppure zero se la postazione è in servizio.
    /// </summary>
    /// <param name="now">Istante corrente.</param>
    public TimeSpan CurrentPause(DateTimeOffset? now = null)
    {
        var istante = now ?? _clock.Now;

        lock (_gate)
        {
            if (_pausedSince is not { } inizio) return TimeSpan.Zero;

            var durata = istante - inizio;
            return durata < TimeSpan.Zero ? TimeSpan.Zero : durata;
        }
    }

    /// <summary>Tempo complessivo passato in pausa dall'inizio del turno.</summary>
    /// <param name="now">Istante corrente.</param>
    public TimeSpan TotalPaused(DateTimeOffset? now = null)
    {
        var istante = now ?? _clock.Now;

        lock (_gate)
        {
            var totale = _pausedTotal;
            if (_pausedSince is { } inizio) totale += istante - inizio;

            return totale < TimeSpan.Zero ? TimeSpan.Zero : totale;
        }
    }

    /// <summary>Mette la postazione in pausa.</summary>
    /// <param name="now">Istante corrente.</param>
    /// <returns><c>false</c> se era già in pausa e non è cambiato nulla.</returns>
    public bool Pause(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (_pausedSince is not null) return false;
            _pausedSince = now ?? _clock.Now;
        }

        PauseChanged?.Invoke(this, true);
        return true;
    }

    /// <summary>Riporta la postazione in servizio.</summary>
    /// <param name="now">Istante corrente.</param>
    /// <returns><c>false</c> se era già in servizio e non è cambiato nulla.</returns>
    public bool Resume(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (_pausedSince is not { } inizio) return false;

            var durata = (now ?? _clock.Now) - inizio;
            if (durata > TimeSpan.Zero) _pausedTotal += durata;

            _pausedSince = null;
        }

        PauseChanged?.Invoke(this, false);
        return true;
    }
}
