namespace BlindPhone.Core.Accessibility;

/// <summary>Riproduttore dei segnali acustici non vocali.</summary>
public interface IEarconPlayer
{
    /// <summary>Riproduce un earcon.</summary>
    /// <param name="kind">Segnale da riprodurre.</param>
    /// <remarks>
    /// L'implementazione instrada il suono sul dispositivo corretto secondo la
    /// famiglia dell'earcon: gli eventi di coda escono sul dispositivo della
    /// suoneria (tipicamente gli altoparlanti), quelli di chiamata sul dispositivo
    /// della conversazione. Così un evento di coda non arriva nell'orecchio
    /// dell'operatore mentre parla.
    /// </remarks>
    void Play(EarconKind kind);

    /// <summary>Avvia la ripetizione periodica di un earcon di allarme.</summary>
    /// <param name="kind">Segnale da ripetere.</param>
    /// <param name="interval">Intervallo fra una ripetizione e la successiva.</param>
    /// <param name="initialDelay">
    /// Attesa prima della prima ripetizione. Serve quando il segnale è già stato
    /// suonato una volta da qualcun altro — la suoneria della chiamata entrante esce
    /// dall'annuncio di arrivo — e farlo ripartire subito lo sovrapporrebbe a sé
    /// stesso. Se omessa la prima ripetizione parte immediatamente.
    /// </param>
    /// <remarks>
    /// Riservato alle condizioni che l'operatore non deve poter ignorare: perdita
    /// della registrazione SIP, chiamata dimenticata in attesa, chiamata entrante che
    /// squilla. Un allarme che suona una volta sola in un ufficio rumoroso non è un
    /// allarme.
    /// </remarks>
    void StartRepeating(EarconKind kind, TimeSpan interval, TimeSpan? initialDelay = null);

    /// <summary>Interrompe la ripetizione periodica di un earcon.</summary>
    /// <param name="kind">Segnale da fermare.</param>
    void StopRepeating(EarconKind kind);
}
