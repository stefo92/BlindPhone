using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Il gate di accessibilità più importante del progetto.
/// </summary>
/// <remarks>
/// <para>
/// Verifica che <b>ogni</b> transizione della macchina a stati della chiamata
/// produca un annuncio per l'operatore. Non un campione, non quelle a cui si è
/// pensato: tutte quelle dichiarate in <see cref="CallStateMachine.Transitions"/>,
/// enumerate dal test stesso.
/// </para>
/// <para>
/// Perché è un test e non una linea guida: una transizione muta è invisibile a chi
/// scrive il codice, che vede lo schermo aggiornarsi, ed è invece un guasto totale
/// per chi non vede. Aggiungere una transizione senza mapparne l'annuncio è un
/// errore facile da commettere e impossibile da notare in revisione. Questo test lo
/// trasforma in una build rossa.
/// </para>
/// </remarks>
public sealed class AnnouncementCoverageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 10, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void OgniTransizioneDiStatoProduceUnAnnuncio()
    {
        var mapper = new CallAnnouncementMapper();
        var mancanti = new List<string>();

        foreach (var transition in CallStateMachine.Transitions)
        {
            var call = BuildCallInState(transition.From);
            var announcement = mapper.Map(transition, call, Now);

            if (string.IsNullOrWhiteSpace(announcement.Text)) mancanti.Add(transition.ToString());
        }

        Assert.True(
            mancanti.Count == 0,
            "Transizioni senza annuncio, l'operatore non vedente non saprebbe che sono avvenute:\n"
            + string.Join('\n', mancanti));
    }

    [Fact]
    public void GliEventiCriticiSonoAnnunciatiConPrioritaCritica()
    {
        var mapper = new CallAnnouncementMapper();

        // Chiamata entrante: se l'operatore non la sente, la perde.
        var incoming = mapper.Map(
            new CallTransition(CallState.Idle, CallTrigger.IncomingCall, CallState.Alerting),
            BuildCallInState(CallState.Idle),
            Now);

        Assert.Equal(Core.Accessibility.AnnouncementPriority.Critical, incoming.Priority);

        // Caduta della chiamata: nessuno ha riagganciato, e senza annuncio
        // l'operatore continuerebbe a parlare nel vuoto.
        var failed = mapper.Map(
            new CallTransition(CallState.Active, CallTrigger.Failed, CallState.Terminated),
            BuildCallInState(CallState.Active),
            Now);

        Assert.Equal(Core.Accessibility.AnnouncementPriority.Critical, failed.Priority);
    }

    [Fact]
    public void GliEventiDiChiamataPortanoUnEarcon()
    {
        var mapper = new CallAnnouncementMapper();
        var senzaEarcon = new List<string>();

        // Gli eventi che cambiano la disponibilita' della linea devono avere un
        // segnale acustico: e' il canale che sopravvive alla soppressione della voce
        // durante la conversazione.
        CallTrigger[] richiedonoEarcon =
        [
            CallTrigger.IncomingCall,
            CallTrigger.Answer,
            CallTrigger.RemoteAnswered,
            CallTrigger.Hold,
            CallTrigger.Resume,
            CallTrigger.RemoteHangUp,
            CallTrigger.HangUp,
        ];

        foreach (var transition in CallStateMachine.Transitions)
        {
            if (!richiedonoEarcon.Contains(transition.Trigger)) continue;
            if (transition.Trigger == CallTrigger.HangUp && transition.From is CallState.Alerting or CallState.Calling)
                continue;

            var announcement = mapper.Map(transition, BuildCallInState(transition.From), Now);

            if (announcement.Earcon == Core.Accessibility.EarconKind.None)
                senzaEarcon.Add(transition.ToString());
        }

        Assert.True(
            senzaEarcon.Count == 0,
            "Transizioni senza earcon, non percepibili quando la voce e' soppressa:\n"
            + string.Join('\n', senzaEarcon));
    }

    [Fact]
    public void LaTabellaDelleTransizioniNonHaDuplicati()
    {
        // Due transizioni con la stessa coppia stato/evento renderebbero il
        // comportamento dipendente dall'ordine di dichiarazione.
        var duplicati = CallStateMachine.Transitions
            .GroupBy(t => (t.From, t.Trigger))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.From} con {g.Key.Trigger}")
            .ToList();

        Assert.True(duplicati.Count == 0, "Transizioni ambigue: " + string.Join(", ", duplicati));
    }

    /// <summary>
    /// Costruisce una chiamata portandola nello stato richiesto attraverso
    /// transizioni lecite.
    /// </summary>
    private static Call BuildCallInState(CallState target)
    {
        var call = new Call("test-1", CallDirection.Inbound, new CallParty("0212345678", "Mario Rossi"), Now);

        switch (target)
        {
            case CallState.Idle:
                break;

            case CallState.Alerting:
                call.TryApply(CallTrigger.IncomingCall, Now, out _);
                break;

            case CallState.Calling:
                call.TryApply(CallTrigger.OutgoingCall, Now, out _);
                break;

            case CallState.Active:
                call.TryApply(CallTrigger.IncomingCall, Now, out _);
                call.TryApply(CallTrigger.Answer, Now, out _);
                break;

            case CallState.Held:
                call.TryApply(CallTrigger.IncomingCall, Now, out _);
                call.TryApply(CallTrigger.Answer, Now, out _);
                call.TryApply(CallTrigger.Hold, Now, out _);
                break;

            case CallState.Transferring:
                call.TryApply(CallTrigger.IncomingCall, Now, out _);
                call.TryApply(CallTrigger.Answer, Now, out _);
                call.TryApply(CallTrigger.TransferStarted, Now, out _);
                break;

            case CallState.Conferenced:
                call.TryApply(CallTrigger.IncomingCall, Now, out _);
                call.TryApply(CallTrigger.Answer, Now, out _);
                call.TryApply(CallTrigger.ConferenceJoined, Now, out _);
                break;

            case CallState.Terminated:
                call.TryApply(CallTrigger.IncomingCall, Now, out _);
                call.TryApply(CallTrigger.HangUp, Now, out _);
                break;
        }

        Assert.Equal(target, call.State);
        return call;
    }
}
