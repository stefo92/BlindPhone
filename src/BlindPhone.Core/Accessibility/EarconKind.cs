namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Segnali acustici non vocali associati agli eventi.
/// </summary>
/// <remarks>
/// <para>
/// Per un operatore esperto un suono di 120 millisecondi comunica più in fretta di
/// una frase, e soprattutto non impedisce di continuare a parlare al telefono. Gli
/// earcon non sono un abbellimento: sono il canale che permette di rispettare il
/// principio "silenzio durante la conversazione" senza rinunciare a informare.
/// </para>
/// <para>
/// Il panning stereo raggruppa gli eventi per famiglia (coda a sinistra, chiamata
/// al centro, controllo linea a destra) e costruisce una mappa spaziale che
/// l'operatore interiorizza in pochi giorni. Il panning è definito in
/// <see cref="EarconCategoryExtensions"/> e resta configurabile.
/// </para>
/// </remarks>
public enum EarconKind
{
    /// <summary>Nessun suono.</summary>
    None = 0,

    /// <summary>Una nuova chiamata è entrata in coda.</summary>
    QueueCallArrived,

    /// <summary>La coda si è svuotata.</summary>
    QueueEmptied,

    /// <summary>Un chiamante attende oltre la soglia configurata.</summary>
    QueueWaitThresholdExceeded,

    /// <summary>Chiamata entrante in squillo sul posto operatore.</summary>
    IncomingCall,

    /// <summary>
    /// Tono di libero: la destinazione di una chiamata uscente sta squillando.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non è una notifica ma <b>audio di avanzamento chiamata</b>, e la differenza
    /// ha una conseguenza pratica. Chiamando un numero esterno il tono arriva dalla
    /// rete come audio in banda, quindi si sente; chiamando un interno il centralino
    /// spesso non lo manda, e senza generarlo qui l'operatore resta in un silenzio
    /// indistinguibile da una chiamata che non è partita.
    /// </para>
    /// <para>
    /// Si genera solo quando la rete non lo fornisce: sovrapporlo a quello del
    /// gestore darebbe due toni sfasati.
    /// </para>
    /// </remarks>
    RemoteRinging,

    /// <summary>Chiamata risposta.</summary>
    CallAnswered,

    /// <summary>Chiamata terminata.</summary>
    CallEnded,

    /// <summary>Chiamata messa in attesa.</summary>
    CallHeld,

    /// <summary>Chiamata ripresa dall'attesa.</summary>
    CallResumed,

    /// <summary>Promemoria periodico di chiamata dimenticata in attesa.</summary>
    HoldReminder,

    /// <summary>La conferenza a tre è stata aperta.</summary>
    ConferenceStarted,

    /// <summary>La conferenza a tre è stata sciolta.</summary>
    ConferenceEnded,

    /// <summary>Trasferimento completato.</summary>
    TransferSucceeded,

    /// <summary>Trasferimento fallito o annullato.</summary>
    TransferFailed,

    /// <summary>Comando non valido nello stato corrente.</summary>
    CommandRejected,

    /// <summary>
    /// Tono di occupato: la destinazione è al telefono.
    /// </summary>
    /// <remarks>
    /// È il segnale che qualunque telefono produce da sempre, e che l'orecchio
    /// riconosce prima di capire le parole. In questo prodotto va generato in locale
    /// perché il centralino, quando rifiuta con un codice di errore, non manda alcun
    /// audio: la linea resta muta e per chi non vede il silenzio è indistinguibile da
    /// un guasto.
    /// </remarks>
    LineBusy,

    /// <summary>
    /// Tono di congestione: la destinazione non è raggiungibile o non esiste.
    /// </summary>
    /// <remarks>
    /// Il tono rapido, quello che si sente chiamando un numero fuori servizio. Si
    /// distingue dall'occupato per la cadenza, ed è una distinzione che l'operatore
    /// conosce già: «richiamo fra poco» contro «questo numero non va».
    /// </remarks>
    LineUnavailable,

    /// <summary>Registrazione SIP persa: allarme ripetuto finché non è risolto.</summary>
    RegistrationLost,

    /// <summary>Registrazione SIP ripristinata.</summary>
    RegistrationRestored,

    /// <summary>Il collegamento con il centralino si è degradato: i dati di coda non sono aggiornati.</summary>
    PbxLinkDegraded,

    /// <summary>Il collegamento con il centralino è tornato disponibile.</summary>
    PbxLinkRestored,
}

/// <summary>Famiglie di earcon, usate per il posizionamento stereo.</summary>
public enum EarconCategory
{
    /// <summary>Eventi di coda. Posizionati a sinistra.</summary>
    Queue,

    /// <summary>Eventi della chiamata in corso. Posizionati al centro.</summary>
    Call,

    /// <summary>Eventi di controllo linea e trasferimento. Posizionati a destra.</summary>
    LineControl,

    /// <summary>Allarmi di sistema. Al centro e su entrambi i dispositivi di uscita.</summary>
    SystemAlert,
}

/// <summary>Classificazione degli earcon per famiglia.</summary>
public static class EarconCategoryExtensions
{
    /// <summary>Restituisce la famiglia a cui l'earcon appartiene.</summary>
    /// <param name="kind">Earcon da classificare.</param>
    public static EarconCategory Category(this EarconKind kind) => kind switch
    {
        EarconKind.QueueCallArrived
            or EarconKind.QueueEmptied
            or EarconKind.QueueWaitThresholdExceeded => EarconCategory.Queue,

        // Il tono di libero sta con la famiglia della chiamata, quindi al centro
        // dello spazio stereo e sul dispositivo della conversazione: e' li' che
        // l'operatore aspetta di sentire l'andamento della chiamata che ha appena
        // fatto partire.
        EarconKind.IncomingCall
            or EarconKind.RemoteRinging
            or EarconKind.CallAnswered
            or EarconKind.CallEnded
            or EarconKind.LineBusy
            or EarconKind.LineUnavailable => EarconCategory.Call,

        EarconKind.CallHeld
            or EarconKind.CallResumed
            or EarconKind.HoldReminder
            or EarconKind.ConferenceStarted
            or EarconKind.ConferenceEnded
            or EarconKind.TransferSucceeded
            or EarconKind.TransferFailed => EarconCategory.LineControl,

        EarconKind.RegistrationLost
            or EarconKind.RegistrationRestored
            or EarconKind.PbxLinkDegraded
            or EarconKind.PbxLinkRestored
            or EarconKind.CommandRejected => EarconCategory.SystemAlert,

        _ => EarconCategory.Call,
    };

    /// <summary>
    /// Posizione stereo suggerita: -1 tutto a sinistra, 0 centro, +1 tutto a destra.
    /// </summary>
    /// <param name="category">Famiglia di earcon.</param>
    public static float SuggestedPan(this EarconCategory category) => category switch
    {
        EarconCategory.Queue => -0.7f,
        EarconCategory.LineControl => 0.7f,
        _ => 0f,
    };
}
