namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Punto di ingresso con cui i servizi applicativi comunicano con l'operatore.
/// </summary>
/// <remarks>
/// I servizi del core dipendono da questa interfaccia e non dai bridge concreti.
/// Nei test si sostituisce con un raccoglitore in memoria, ed è così che il gate di
/// copertura degli annunci può verificare che ogni transizione di stato produca
/// effettivamente un messaggio.
/// </remarks>
public interface IAnnouncer
{
    /// <summary>Accoda un annuncio per l'operatore.</summary>
    /// <param name="announcement">Annuncio da pronunciare.</param>
    void Announce(Announcement announcement);

    /// <summary>Riproduce un earcon senza alcun testo associato.</summary>
    /// <param name="earcon">Segnale acustico da riprodurre.</param>
    /// <remarks>
    /// Usato quando il principio di silenzio durante la conversazione sopprime la
    /// voce ma l'evento va comunque segnalato.
    /// </remarks>
    void PlayEarcon(EarconKind earcon);

    /// <summary>Interrompe immediatamente la voce in corso e svuota la coda annunci.</summary>
    /// <remarks>
    /// Comando esplicito dell'operatore, non un'operazione automatica: serve quando
    /// una raffica di eventi ha riempito la coda e si vuole tornare al silenzio per
    /// poter parlare.
    /// </remarks>
    void Silence();

    /// <summary>Ripete l'ultimo annuncio pronunciato.</summary>
    void RepeatLast();

    /// <summary>Storico degli annunci recenti, dal più recente al più vecchio.</summary>
    IReadOnlyList<Announcement> History { get; }
}
