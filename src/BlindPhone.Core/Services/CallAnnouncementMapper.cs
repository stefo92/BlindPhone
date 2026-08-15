using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;

namespace BlindPhone.Core.Services;

/// <summary>
/// Traduce le transizioni di stato della chiamata in annunci per l'operatore.
/// </summary>
/// <remarks>
/// <para>
/// L'esistenza di questa classe è una scelta di accessibilità con conseguenze
/// architetturali. Se gli annunci fossero sparsi nei gestori di evento — una
/// chiamata a <c>Speak()</c> dove capita — prima o poi un ramo di codice ne
/// resterebbe privo, e nessuno se ne accorgerebbe fino a quando un operatore non
/// vedente si trova davanti a un cambio di stato silenzioso. Concentrando la
/// mappatura in un unico punto <b>esaustivo</b>, l'assenza di un annuncio diventa
/// verificabile automaticamente.
/// </para>
/// <para>
/// Il test <c>AnnouncementCoverageTests</c> enumera
/// <see cref="CallStateMachine.Transitions"/> e verifica che
/// <see cref="Map"/> restituisca un annuncio per ognuna. Una transizione muta
/// ferma la build.
/// </para>
/// </remarks>
public sealed class CallAnnouncementMapper
{
    private readonly DigitGrouping _grouping;
    private readonly VerbosityLevel _verbosity;

    /// <summary>Crea il mappatore.</summary>
    /// <param name="grouping">Come leggere i numeri telefonici.</param>
    /// <param name="verbosity">Livello di dettaglio degli annunci.</param>
    public CallAnnouncementMapper(
        DigitGrouping grouping = DigitGrouping.Single,
        VerbosityLevel verbosity = VerbosityLevel.Standard)
    {
        _grouping = grouping;
        _verbosity = verbosity;
    }

    /// <summary>
    /// Produce l'annuncio corrispondente a una transizione.
    /// </summary>
    /// <param name="transition">Transizione avvenuta.</param>
    /// <param name="call">Chiamata che l'ha subita, con lo stato già aggiornato.</param>
    /// <param name="now">Istante corrente.</param>
    /// <returns>
    /// L'annuncio da pronunciare. Non restituisce mai <c>null</c> per una
    /// transizione presente in <see cref="CallStateMachine.Transitions"/>.
    /// </returns>
    public Announcement Map(CallTransition transition, Call call, DateTimeOffset now)
    {
        var party = call.RemoteParty;

        return (transition.From, transition.Trigger) switch
        {
            // Chiamata entrante: critico, perche' e' l'evento su cui si fonda il
            // lavoro dell'operatore e non deve mai essere perso.
            (CallState.Idle, CallTrigger.IncomingCall) => Announcement.Critical(
                AnnouncementTexts.IncomingCall(party, call.QueueName, _grouping),
                now,
                EarconKind.IncomingCall,
                coalesceKey: null,
                braille: BrailleIncoming(party)),

            (CallState.Idle, CallTrigger.OutgoingCall) => Announcement.High(
                AnnouncementTexts.Dialing(SpeechFormatter.Party(party.DisplayName, party.Number, _grouping)),
                now,
                coalesceKey: $"call:{call.Id}:progress"),

            (CallState.Alerting, CallTrigger.Answer) => Announcement.High(
                AnnouncementTexts.CallAnswered(party),
                now,
                EarconKind.CallAnswered,
                coalesceKey: $"call:{call.Id}:progress",
                braille: BrailleActive(party)),

            (CallState.Calling, CallTrigger.RemoteAnswered) => Announcement.High(
                AnnouncementTexts.CallAnswered(party),
                now,
                EarconKind.CallAnswered,
                coalesceKey: $"call:{call.Id}:progress",
                braille: BrailleActive(party)),

            // Abbandono del chiamante prima della risposta: e' un dato di servizio,
            // non un evento neutro. L'operatore deve sapere di aver perso una
            // chiamata, altrimenti non puo' rendersene conto in alcun modo.
            //
            // Si identifica il chiamante nello stesso modo in cui lo si e' annunciato
            // all'arrivo: e' la stessa persona, e sentirla nominare in due modi diversi
            // a quindici secondi di distanza costringerebbe a chiedersi se sono due.
            // Con il numero letto per intero, per giunta, non si potrebbe nemmeno
            // richiamarla, che e' l'unica cosa da fare dopo una chiamata persa.
            (CallState.Alerting, CallTrigger.RemoteHangUp) => Announcement.High(
                $"Chiamata persa da {AnnouncementTexts.CallerIdentity(party, _grouping)}",
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end"),

            (CallState.Alerting, CallTrigger.HangUp) => Announcement.High(
                "Chiamata rifiutata",
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end"),

            (CallState.Calling, CallTrigger.HangUp) => Announcement.High(
                "Chiamata annullata",
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end"),

            (CallState.Calling, CallTrigger.RemoteRejected) => Announcement.High(
                AnnouncementTexts.CallEnded(party, call.EndReason, TimeSpan.Zero),
                now,
                ToneForFailure(call.EndReason),
                coalesceKey: $"call:{call.Id}:end"),

            (CallState.Calling, CallTrigger.RemoteHangUp) => Announcement.High(
                AnnouncementTexts.CallEnded(party, call.EndReason, TimeSpan.Zero),
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end"),

            (CallState.Active, CallTrigger.Hold) => Announcement.High(
                AnnouncementTexts.CallHeld(party),
                now,
                EarconKind.CallHeld,
                coalesceKey: $"call:{call.Id}:hold",
                braille: BrailleHeld(party)),

            (CallState.Held, CallTrigger.Resume) => Announcement.High(
                AnnouncementTexts.CallResumed(party),
                now,
                EarconKind.CallResumed,
                coalesceKey: $"call:{call.Id}:hold",
                braille: BrailleActive(party)),

            // Conferenza a tre. Come per il trasferimento, questi annunci sono la rete
            // di sicurezza: nell'uso normale CallSessionManager li sopprime e ne emette
            // uno solo che nomina entrambi i partecipanti, perche' le due gambe entrano
            // e escono insieme e due frasi consecutive direbbero la stessa cosa due
            // volte. Restano qui perche' nessuna transizione possa essere muta.
            (CallState.Active, CallTrigger.ConferenceJoined) or
            (CallState.Held, CallTrigger.ConferenceJoined) => Announcement.High(
                $"{party.SpeechName} in conferenza",
                now,
                EarconKind.ConferenceStarted,
                coalesceKey: $"call:{call.Id}:conference",
                braille: BrailleConference(party)),

            (CallState.Conferenced, CallTrigger.ConferenceLeft) => Announcement.High(
                AnnouncementTexts.CallResumed(party),
                now,
                EarconKind.ConferenceEnded,
                coalesceKey: $"call:{call.Id}:conference",
                braille: BrailleActive(party)),

            (CallState.Conferenced, CallTrigger.Hold) => Announcement.High(
                AnnouncementTexts.CallHeld(party),
                now,
                EarconKind.ConferenceEnded,
                coalesceKey: $"call:{call.Id}:conference",
                braille: BrailleHeld(party)),

            (CallState.Conferenced, CallTrigger.HangUp) => Announcement.High(
                AnnouncementTexts.CallEnded(party, CallEndReason.LocalHangUp, call.TalkDuration(now)),
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end",
                braille: "libero"),

            // Un partecipante che chiude scioglie la conferenza: e' un evento subito,
            // non provocato, e va detto per intero anche se poi il gestore aggiunge
            // con chi si resta.
            (CallState.Conferenced, CallTrigger.RemoteHangUp) => Announcement.High(
                AnnouncementTexts.CallEnded(party, CallEndReason.RemoteHangUp, call.TalkDuration(now)),
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end",
                braille: "libero"),

            // Le transizioni di trasferimento producono un annuncio di base, che il
            // TransferOrchestrator sostituisce con uno piu' ricco quando conosce la
            // destinazione. Restano qui perche' nessuna transizione possa essere muta
            // anche se l'orchestratore non e' in gioco (per esempio un trasferimento
            // avviato dal centralino).
            (CallState.Active, CallTrigger.TransferStarted) or
            (CallState.Held, CallTrigger.TransferStarted) => Announcement.High(
                "Trasferimento in corso",
                now,
                coalesceKey: $"call:{call.Id}:transfer"),

            (CallState.Transferring, CallTrigger.TransferCompleted) => Announcement.High(
                "Trasferimento completato",
                now,
                EarconKind.TransferSucceeded,
                coalesceKey: $"call:{call.Id}:transfer"),

            // Il chiamante parcheggiato torna in linea mentre il trasferimento resta
            // aperto. Stesso testo e stesso earcon della ripresa da attesa: per
            // l'operatore e' lo stesso fatto — "adesso parlo di nuovo con lui" — e
            // parole diverse per lo stesso esito si dovrebbero imparare due volte.
            // Nell'uso normale l'orchestratore lo sopprime e ne emette uno solo che
            // nomina anche chi resta in attesa e il tasto che completa.
            (CallState.Transferring, CallTrigger.Resume) => Announcement.High(
                AnnouncementTexts.CallResumed(party),
                now,
                EarconKind.CallResumed,
                coalesceKey: $"call:{call.Id}:hold",
                braille: BrailleActive(party)),

            (CallState.Transferring, CallTrigger.TransferAborted) => Announcement.High(
                AnnouncementTexts.CallResumed(party),
                now,
                EarconKind.TransferFailed,
                coalesceKey: $"call:{call.Id}:transfer",
                braille: BrailleActive(party)),

            (CallState.Transferring, CallTrigger.RemoteHangUp) => Announcement.High(
                "Il chiamante ha riagganciato durante il trasferimento",
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end"),

            (CallState.Transferring, CallTrigger.HangUp) => Announcement.High(
                "Trasferimento interrotto, chiamata chiusa",
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end"),

            (CallState.Active, CallTrigger.HangUp) or
            (CallState.Held, CallTrigger.HangUp) => Announcement.High(
                AnnouncementTexts.CallEnded(party, CallEndReason.LocalHangUp, call.TalkDuration(now)),
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end",
                braille: "libero"),

            (CallState.Active, CallTrigger.RemoteHangUp) or
            (CallState.Held, CallTrigger.RemoteHangUp) => Announcement.High(
                AnnouncementTexts.CallEnded(party, CallEndReason.RemoteHangUp, call.TalkDuration(now)),
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end",
                braille: "libero"),

            // I guasti sono critici: la chiamata e' caduta senza che nessuno abbia
            // riagganciato, e l'operatore deve saperlo subito per poter richiamare.
            (_, CallTrigger.Failed) => Announcement.Critical(
                party.IsAnonymous
                    ? "Chiamata caduta per problema di rete"
                    : $"Chiamata con {party.SpeechName} caduta per problema di rete",
                now,
                EarconKind.CallEnded,
                coalesceKey: $"call:{call.Id}:end",
                braille: "caduta"),

            // Rete di sicurezza. Se si aggiunge una transizione alla tabella senza
            // mapparla qui, il test di copertura fallisce; questo ramo esiste solo
            // perche' il compilatore richiede l'esaustivita' dello switch, e produce
            // comunque un annuncio invece di restare muto.
            _ => Announcement.High(
                $"Stato chiamata: {DescribeState(transition.To)}",
                now,
                coalesceKey: $"call:{call.Id}:progress"),
        };
    }

    private static string DescribeState(CallState state) => state switch
    {
        CallState.Idle => "libera",
        CallState.Alerting => "in arrivo",
        CallState.Calling => "in chiamata",
        CallState.Active => "in conversazione",
        CallState.Held => "in attesa",
        CallState.Transferring => "in trasferimento",
        CallState.Conferenced => "in conferenza",
        CallState.Terminated => "terminata",
        _ => "sconosciuto",
    };

    // La riga braille e' volutamente telegrafica: su un display a 40 celle lo spazio
    // e' la risorsa scarsa, e l'operatore la legge con le dita mentre fa altro.
    /// <summary>
    /// Sceglie il tono da mandare in cuffia quando una chiamata non va a buon fine.
    /// </summary>
    /// <param name="reason">Motivo per cui la chiamata non è andata a buon fine.</param>
    /// <remarks>
    /// <para>
    /// <b>Nasce da una segnalazione dal campo.</b> Chiamando un numero fuori servizio
    /// l'operatore non sentiva nulla in cuffia, e restava disorientato: il centralino
    /// rifiuta con un codice di errore e <c>Content-Length: 0</c>, cioè senza mandare
    /// alcun audio, e quindi non c'era proprio niente da riprodurre. Su un telefono
    /// tradizionale quel tono lo genera l'apparecchio, non la rete.
    /// </para>
    /// <para>
    /// <b>Due toni e non uno</b>, perché la distinzione la fa già l'orecchio di
    /// chiunque abbia usato un telefono: l'occupato lento dice «richiamo fra poco», il
    /// rapido dice «questo numero non va». Riconoscerli non richiede di aspettare la
    /// frase che li accompagna, ed è la differenza fra sapere subito e sapere dopo.
    /// </para>
    /// <para>
    /// La frase resta e dice il motivo per esteso: il tono è il primo riscontro, non
    /// l'unico. Ma il tono esce sul dispositivo della conversazione, cioè in cuffia,
    /// dove l'operatore sta ascoltando la chiamata che ha appena fatto partire.
    /// </para>
    /// </remarks>
    private static EarconKind ToneForFailure(CallEndReason? reason) => reason switch
    {
        CallEndReason.Busy => EarconKind.LineBusy,

        // Tutto il resto e' un numero che non risponde come dovrebbe: inesistente,
        // non raggiungibile, non consentito, caduto per la rete. Per l'operatore la
        // conseguenza e' la stessa — su questo numero adesso non si passa — e il
        // dettaglio glielo dice la frase.
        _ => EarconKind.LineUnavailable,
    };

    private static string BrailleIncoming(CallParty party) => $">{Short(party)}";

    private static string BrailleActive(CallParty party) => $"={Short(party)}";

    private static string BrailleHeld(CallParty party) => $"~{Short(party)}";

    private static string BrailleConference(CallParty party) => $"+{Short(party)}";

    private static string Short(CallParty party)
    {
        var text = party.DisplayName ?? party.Number;
        if (string.IsNullOrWhiteSpace(text)) return "?";
        return text.Length <= 16 ? text : text[..16];
    }
}
