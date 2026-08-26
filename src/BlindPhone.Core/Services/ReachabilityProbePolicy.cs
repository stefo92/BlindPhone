using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Decide se ai sondaggi di raggiungibilità si possa credere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il pericolo di questa strada è che risponda sempre di sì.</b> Un centralino, o
/// più spesso un controllore di bordo davanti a lui, può rispondere <c>200</c> a
/// qualunque <c>OPTIONS</c> senza chiedersi a chi fosse indirizzato: risponde per sé,
/// non per l'interno. Il risultato sarebbe un elenco in cui tutti risultano in turno
/// sempre, compreso chi ha il telefono spento da una settimana — cioè l'informazione
/// sbagliata al posto di nessuna informazione, che su un posto operatore è il guasto
/// peggiore. Il centralinista passerebbe le chiamate a scrivanie vuote fidandosi di
/// quello che legge.
/// </para>
/// <para>
/// <b>Come si scopre.</b> Insieme agli interni veri si sonda un bersaglio che non può
/// esistere: un indirizzo la cui parte utente contiene lettere, quindi impossibile da
/// comporre e impossibile da configurare come interno. Se il centralino risponde
/// «raggiungibile» anche a quello, allora sta rispondendo per sé, e i suoi «sì» non
/// dicono niente su nessuno.
/// </para>
/// <para>
/// <b>Due giri prima di rinunciare, e non uno.</b> Una singola risposta anomala può
/// venire da un rimbalzo di rete o da un apparato che si stava riavviando; due giri di
/// seguito no. Ed è la stessa prudenza di
/// <see cref="PresenceSubscriptionPolicy"/>: dichiarare assente una funzione è una
/// decisione che vale per tutto il turno, quindi va presa su una prova e non su un
/// indizio.
/// </para>
/// <para>
/// <b>Si può tornare indietro.</b> Se il bersaglio di controllo ricomincia a essere
/// rifiutato — l'amministratore ha corretto la configurazione, il controllore di bordo
/// è stato aggiornato — la funzione torna disponibile senza che si debba riavviare la
/// postazione. È il contrario della rinuncia sulle sottoscrizioni, che vale per la
/// sessione: lì a costare era ritentare, qui il sondaggio si fa comunque.
/// </para>
/// </remarks>
public sealed class ReachabilityProbePolicy
{
    /// <summary>Giri consecutivi con il bersaglio di controllo raggiungibile prima di rinunciare.</summary>
    public const int RoundsBeforeGivingUp = 2;

    private readonly Lock _gate = new();

    private int _controlAnswered;
    private bool _methodRefused;

    /// <summary>
    /// Se in questo momento agli esiti si possa credere.
    /// </summary>
    /// <remarks>
    /// Quando è falso l'elenco dei colleghi non mostra stati inventati: dichiara che il
    /// centralino non è in grado di distinguere, e spiega cosa si può fare.
    /// </remarks>
    public bool CanTrustResults
    {
        get { lock (_gate) return !_methodRefused && _controlAnswered < RoundsBeforeGivingUp; }
    }

    /// <summary>Se il centralino ha dichiarato di non servire il metodo.</summary>
    public bool MethodRefused
    {
        get { lock (_gate) return _methodRefused; }
    }

    /// <summary>Se il centralino risponde «raggiungibile» anche a un indirizzo inesistente.</summary>
    public bool AnswersForAnyone
    {
        get { lock (_gate) return !_methodRefused && _controlAnswered >= RoundsBeforeGivingUp; }
    }

    /// <summary>Registra l'esito del bersaglio di controllo, alla fine di ogni giro.</summary>
    /// <param name="outcome">Cosa ha risposto il centralino all'indirizzo inesistente.</param>
    /// <returns><c>true</c> se da adesso agli esiti si può credere.</returns>
    public bool ObserveControl(ReachabilityOutcome outcome)
    {
        lock (_gate)
        {
            switch (outcome)
            {
                // La risposta giusta a un indirizzo inesistente: il centralino guarda
                // davvero a chi e' indirizzata la domanda.
                case ReachabilityOutcome.NotConfigured:
                case ReachabilityOutcome.Unreachable:
                    _controlAnswered = 0;
                    _methodRefused = false;
                    break;

                case ReachabilityOutcome.Reachable:
                    if (_controlAnswered < RoundsBeforeGivingUp) _controlAnswered++;
                    break;

                case ReachabilityOutcome.Refused:
                    _methodRefused = true;
                    break;

                // Nessuna risposta: non dice niente ne' a favore ne' contro, e il conto
                // resta dov'e'. Una rete che non risponde spegnerebbe altrimenti la
                // funzione per un disservizio passeggero.
                default:
                    break;
            }

            return !_methodRefused && _controlAnswered < RoundsBeforeGivingUp;
        }
    }

    /// <summary>
    /// Registra l'esito di un interno vero, per riconoscere il rifiuto del metodo.
    /// </summary>
    /// <param name="outcome">Esito del sondaggio su un interno dell'elenco.</param>
    /// <remarks>
    /// Il rifiuto del metodo si riconosce da una risposta sola e non ha bisogno di
    /// attendere il giro successivo: il centralino ha appena detto che quella domanda
    /// non la tratta, e non la trattera' per nessun altro interno.
    /// </remarks>
    public void ObserveExtension(ReachabilityOutcome outcome)
    {
        if (outcome != ReachabilityOutcome.Refused) return;

        lock (_gate) _methodRefused = true;
    }
}
