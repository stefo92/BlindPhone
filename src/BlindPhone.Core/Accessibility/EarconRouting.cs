using BlindPhone.Core.Abstractions;

namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Decide da quale dispositivo audio esce ciascun segnale acustico.
/// </summary>
/// <remarks>
/// <para>
/// È la regola che rende utile l'audio a quattro vie: gli avvisi che arrivano
/// <b>mentre l'operatore parla</b> non devono suonargli in un orecchio, e vanno sugli
/// altoparlanti; i riscontri di ciò che l'operatore <b>ha appena fatto</b> restano
/// dove sta la conversazione, perché sono la risposta a un suo gesto.
/// </para>
/// <para>
/// <b>La suoneria sta con la suoneria.</b> Il segnale di chiamata in arrivo esce dal
/// dispositivo configurato come suoneria, che è quello che l'interfaccia promette
/// chiamandolo così. Prima usciva dalla conversazione, con la motivazione che chi
/// riceve una chiamata non sta parlando: ma non è vero su un posto operatore, dove la
/// chiamata successiva arriva mentre la precedente è ancora in corso — ed è
/// esattamente il momento in cui uno squillo nell'orecchio copre la voce
/// dell'interlocutore.
/// </para>
/// <para>
/// Sta nel core e non nel riproduttore per poterla verificare: è una tabella di
/// decisioni, e una decisione sbagliata qui si manifesta come un suono che arriva
/// dalla parte sbagliata della scrivania, che nessun test di compilazione può notare.
/// </para>
/// </remarks>
public static class EarconRouting
{
    /// <summary>Via audio da cui esce un segnale acustico.</summary>
    /// <param name="kind">Segnale da riprodurre.</param>
    public static AudioRoute RouteFor(EarconKind kind) => kind switch
    {
        // La suoneria: dal dispositivo della suoneria, che tipicamente e' un
        // altoparlante lontano dall'orecchio impegnato.
        EarconKind.IncomingCall => AudioRoute.Ringer,

        // Coda e allarmi di sistema: arrivano senza che l'operatore abbia fatto
        // nulla, e spesso mentre sta parlando.
        _ => kind.Category() switch
        {
            EarconCategory.Queue => AudioRoute.Ringer,
            EarconCategory.SystemAlert => AudioRoute.Ringer,

            // Risposta, chiusura, attesa, trasferimento: sono il riscontro di un
            // gesto appena compiuto, e vanno dove l'operatore sta ascoltando.
            _ => AudioRoute.Conversation,
        },
    };
}
