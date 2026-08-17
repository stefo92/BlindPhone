namespace BlindPhone.Core.Services;

/// <summary>Cosa fare dopo che il centralino ha rifiutato una sottoscrizione.</summary>
public enum PresenceSubscriptionAction
{
    /// <summary>Errore passeggero: la sottoscrizione resta e riproverà da sé.</summary>
    KeepTrying,

    /// <summary>Si smette di sorvegliare quel numero, gli altri continuano.</summary>
    StopThisExtension,

    /// <summary>Si smette di sorvegliare, e non solo quel numero: la funzione si spegne.</summary>
    GiveUp,
}

/// <summary>
/// Decide quanto insistere con un centralino che rifiuta le sottoscrizioni allo stato
/// degli interni.
/// </summary>
/// <remarks>
/// <para>
/// <b>La domanda a cui risponde è una sola: questo rifiuto riguarda un numero o tutto
/// l'impianto?</b> Le due situazioni si somigliano — una risposta negativa a una
/// richiesta — e vanno gestite in modo opposto. Un numero rifiutato è normale: una
/// rubrica di ente contiene recapiti esterni, code, gruppi di squillo, risponditori
/// automatici, e per tutti quelli il centralino non ha uno stato da riportare.
/// Un impianto che rifiuta <i>tutto</i> è un'altra cosa, e insistere con lui produce
/// una richiesta per ogni nome cercato, per tutto il turno.
/// </para>
/// <para>
/// <b>Perché non basta contare i rifiuti.</b> Il conto da solo direbbe «tanti rifiuti,
/// quindi la funzione non c'è», e sarebbe sbagliato su un centralino che funziona ma di
/// cui l'operatore sta cercando venti numeri di cellulare. Il criterio è più stretto:
/// tanti rifiuti <b>e mai una sola accettazione</b>. Una sottoscrizione accettata
/// dimostra che l'impianto la funzione la serve, e da quel momento i rifiuti tornano a
/// essere quello che sono, cioè notizie sul singolo numero.
/// </para>
/// <para>
/// <b>Vive nel core e non accanto al codice SIP</b> per la ragione di sempre: è una
/// regola, e le regole si verificano caso per caso senza avere un centralino a
/// disposizione. La misura sul campo che l'ha resa necessaria è nel registro del
/// 17 agosto 2026: 81 sottoscrizioni inviate, 81 rifiutate con <c>404</c>, nessuna
/// accettata, e la funzione che continuava a riprovare a ogni ricerca.
/// </para>
/// </remarks>
public sealed class PresenceSubscriptionPolicy
{
    /// <summary>
    /// Quanti rifiuti di fila, senza una sola accettazione, prima di rinunciare.
    /// </summary>
    /// <remarks>
    /// Dodici, e la soglia è alta di proposito: siccome un'accettazione azzera il
    /// conto, arrivarci significa non aver mai visto una sottoscrizione riuscire. Su un
    /// centralino che le serve, un posto operatore ne ottiene decine in pochi minuti.
    /// </remarks>
    public const int MaxRefusalsWithoutSuccess = 12;

    private readonly Lock _gate = new();

    private int _accepted;
    private int _refusals;

    /// <summary>Se si è concluso che questo centralino non serve la funzione.</summary>
    public bool GaveUp { get; private set; }

    /// <summary>Rifiuti consecutivi, azzerati dalla prima accettazione.</summary>
    public int ConsecutiveRefusals
    {
        get { lock (_gate) return _refusals; }
    }

    /// <summary>Sottoscrizioni accettate dal centralino.</summary>
    public int Accepted
    {
        get { lock (_gate) return _accepted; }
    }

    /// <summary>Registra una sottoscrizione accettata.</summary>
    /// <remarks>
    /// È il fatto che cambia il significato di tutti i rifiuti successivi, e per questo
    /// azzera il conto invece di limitarsi a incrementare il proprio.
    /// </remarks>
    public void Accept()
    {
        lock (_gate)
        {
            _accepted++;
            _refusals = 0;
        }
    }

    /// <summary>Registra un rifiuto e dice cosa farne.</summary>
    /// <param name="nature">Natura del rifiuto, secondo il codice di risposta SIP.</param>
    public PresenceSubscriptionAction Refuse(SubscriptionRefusal nature)
    {
        lock (_gate)
        {
            _refusals++;

            if (GaveUp) return PresenceSubscriptionAction.GiveUp;

            // Il pacchetto di eventi non servito si riconosce da una risposta sola e non
            // serve attendere la soglia: il centralino ha appena detto che quella
            // funzione non la fa, per nessun interno.
            if (nature == SubscriptionRefusal.EventPackage)
            {
                GaveUp = true;
                return PresenceSubscriptionAction.GiveUp;
            }

            if (_accepted == 0 && _refusals >= MaxRefusalsWithoutSuccess)
            {
                GaveUp = true;
                return PresenceSubscriptionAction.GiveUp;
            }

            return nature == SubscriptionRefusal.Transient
                ? PresenceSubscriptionAction.KeepTrying
                : PresenceSubscriptionAction.StopThisExtension;
        }
    }
}
