using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;

namespace BlindPhone.Core.Services;

/// <summary>Da quale chiamata viene il numero copiato.</summary>
public enum CallerNumberSource
{
    /// <summary>La chiamata in conversazione, o una delle parti della conferenza.</summary>
    InConversation,

    /// <summary>La chiamata che sta squillando.</summary>
    Ringing,

    /// <summary>La chiamata uscente che non ha ancora risposto.</summary>
    Outgoing,

    /// <summary>Una chiamata in attesa o in trasferimento.</summary>
    Waiting,

    /// <summary>L'ultima chiamata ricevuta, già terminata: risposta o persa.</summary>
    LastReceived,
}

/// <summary>La chiamata scelta per la copia del numero, e da dove viene.</summary>
/// <param name="Call">Chiamata scelta.</param>
/// <param name="Source">Situazione in cui si trova.</param>
public sealed record CallerNumberCandidate(Call Call, CallerNumberSource Source);

/// <summary>
/// Sceglie di quale chiamata copiare il numero negli appunti, e come dirlo.
/// </summary>
/// <remarks>
/// <para>
/// Il bisogno è quello di ogni centralino: annotare il numero di chi ha chiamato in un
/// altro programma — il gestionale delle segnalazioni, un messaggio a un collega, un
/// foglio di calcolo. Chi vede lo copia dalla scheda della chiamata con il mouse; chi
/// non vede lo dovrebbe ascoltare e riscrivere a mano, cifra per cifra, con il rischio
/// di sbagliarne una proprio nel dato che serve a richiamare.
/// </para>
/// <para>
/// <b>Una regola sola, sempre nello stesso ordine</b>, perché chi non vede lo schermo
/// non può controllare quale chiamata è «selezionata»: deve poter prevedere quale
/// numero finisce negli appunti, e sentirselo confermare. L'ordine va da ciò che è più
/// vicino all'orecchio a ciò che è appena passato:
/// </para>
/// <list type="number">
///   <item><description>chi è in linea, cioè la conversazione in corso;</description></item>
///   <item><description>chi sta chiamando, se il telefono squilla;</description></item>
///   <item><description>chi si sta chiamando, finché non risponde;</description></item>
///   <item><description>la chiamata in attesa messa in attesa per ultima;</description></item>
///   <item><description>l'ultima chiamata ricevuta, risposta o persa.</description></item>
/// </list>
/// <para>
/// L'ultimo gradino è quello che si usa di più: si riaggancia, e poi si annota. Conta
/// solo le chiamate <b>ricevute</b>: il comando copia il numero del chiamante, e quello
/// di una chiamata fatta dall'operatore lo conosce già chi l'ha composto, e lo ritrova
/// con Richiama. Conta anche le perse, che sono proprio quelle da richiamare.
/// </para>
/// <para>
/// Dentro uno stesso gradino, con due parti in conferenza o due chiamate in attesa, si
/// preferisce chi ha chiamato a chi è stato chiamato, e poi la più recente. Una
/// chiamata con il numero nascosto non fa scendere al gradino successivo: copiare il
/// numero di un'altra persona senza che l'operatore se lo aspetti sarebbe peggio che
/// dirgli che quel numero non c'è.
/// </para>
/// </remarks>
public static class CallerNumberCopy
{
    /// <summary>Sceglie la chiamata da cui copiare il numero.</summary>
    /// <param name="inConversation">Chiamate in conversazione: quella attiva, o le parti della conferenza.</param>
    /// <param name="ringing">Chiamata che squilla.</param>
    /// <param name="outgoing">Chiamata uscente senza risposta.</param>
    /// <param name="waiting">Chiamate in attesa o in trasferimento.</param>
    /// <param name="lastReceived">Ultima chiamata ricevuta e già terminata.</param>
    /// <returns>La chiamata scelta, oppure <c>null</c> se non ce n'è nessuna.</returns>
    public static CallerNumberCandidate? Choose(
        IReadOnlyList<Call> inConversation,
        Call? ringing,
        Call? outgoing,
        IReadOnlyList<Call> waiting,
        Call? lastReceived)
    {
        if (Preferred(inConversation, c => c.AnsweredAt ?? c.CreatedAt) is { } talking)
        {
            return new CallerNumberCandidate(talking, CallerNumberSource.InConversation);
        }

        if (ringing is not null) return new CallerNumberCandidate(ringing, CallerNumberSource.Ringing);
        if (outgoing is not null) return new CallerNumberCandidate(outgoing, CallerNumberSource.Outgoing);

        if (Preferred(waiting, c => c.HeldAt ?? c.CreatedAt) is { } held)
        {
            return new CallerNumberCandidate(held, CallerNumberSource.Waiting);
        }

        return lastReceived is not null
            ? new CallerNumberCandidate(lastReceived, CallerNumberSource.LastReceived)
            : null;
    }

    /// <summary>
    /// Il testo da mettere negli appunti per una controparte.
    /// </summary>
    /// <param name="party">Controparte della chiamata.</param>
    /// <returns>Il numero, oppure <c>null</c> se non ce n'è uno da copiare.</returns>
    /// <remarks>
    /// <para>
    /// Si copia la forma che si compone: le sole cifre, con il più iniziale se c'era
    /// (<see cref="CallerIdentity.Normalize"/>). È quella che si incolla senza ritocchi
    /// nel campo di composizione, in una rubrica o in un modulo che accetta solo cifre;
    /// spazi e trattini della segnalazione andrebbero tolti a mano.
    /// </para>
    /// <para>
    /// Un chiamante con il numero nascosto arriva come «anonymous», o senza nulla: non
    /// ha cifre, e non si copia. Mettere negli appunti la parola «anonymous» farebbe
    /// incollare nel gestionale qualcosa che sembra un dato e non lo è.
    /// </para>
    /// </remarks>
    public static string? CopyText(CallParty party)
    {
        var number = CallerIdentity.Normalize(party.Number);
        return number.Length == 0 ? null : number;
    }

    /// <summary>Conferma da pronunciare dopo la copia.</summary>
    /// <param name="candidate">Chiamata da cui è stato copiato il numero.</param>
    /// <param name="copied">Testo messo negli appunti.</param>
    /// <param name="grouping">Raggruppamento delle cifre scelto nelle impostazioni.</param>
    /// <remarks>
    /// Il numero si rilegge per intero, cifra per cifra: è l'unico modo, per chi non
    /// vede, di sapere che cosa incollerà. E si dice di chi è, perché la regola che
    /// sceglie la chiamata è prevedibile ma non per questo sempre presente alla mente.
    /// </remarks>
    public static string Confirmation(CallerNumberCandidate candidate, string copied, DigitGrouping grouping)
    {
        var party = candidate.Call.RemoteParty;
        var who = party.HasName ? $" di {party.SpeechName}" : string.Empty;
        var digits = SpeechFormatter.Digits(copied, grouping);

        return candidate.Source switch
        {
            CallerNumberSource.Ringing => $"Copiato il numero{who}, che sta chiamando: {digits}",
            CallerNumberSource.Outgoing => $"Copiato il numero{who}, che stai chiamando: {digits}",
            CallerNumberSource.Waiting => $"Copiato il numero{who}, in attesa: {digits}",
            CallerNumberSource.LastReceived => party.HasName
                ? $"Copiato il numero dell'ultima chiamata ricevuta, {party.SpeechName}: {digits}"
                : $"Copiato il numero dell'ultima chiamata ricevuta: {digits}",
            _ => $"Copiato il numero{who}: {digits}",
        };
    }

    /// <summary>Risposta quando la chiamata scelta non ha un numero da copiare.</summary>
    /// <param name="candidate">Chiamata scelta.</param>
    public static string HiddenNumber(CallerNumberCandidate candidate)
    {
        var party = candidate.Call.RemoteParty;
        var who = party.HasName ? $", {party.SpeechName}," : string.Empty;

        var subject = candidate.Source switch
        {
            CallerNumberSource.Ringing => $"Chi sta chiamando{who}",
            CallerNumberSource.Outgoing => $"La chiamata in uscita{who}",
            CallerNumberSource.Waiting => $"La chiamata in attesa{who}",
            CallerNumberSource.LastReceived => $"L'ultima chiamata ricevuta{who}",
            _ => $"Chi e' in linea{who}",
        };

        return $"{subject} non ha un numero da copiare: il numero e' nascosto. Gli appunti non sono cambiati";
    }

    /// <summary>Risposta quando non c'è nessuna chiamata da cui copiare.</summary>
    public const string NothingToCopy =
        "Nessun numero da copiare: non ci sono chiamate in corso e non ne hai ancora ricevute";

    private static Call? Preferred(IReadOnlyList<Call> calls, Func<Call, DateTimeOffset> recency)
        => calls
            .OrderByDescending(c => c.Direction == CallDirection.Inbound)
            .ThenByDescending(recency)
            .FirstOrDefault();
}
