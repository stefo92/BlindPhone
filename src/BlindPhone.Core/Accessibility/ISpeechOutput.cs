namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Destinazione finale della voce: screen reader, voce interna o entrambi.
/// </summary>
/// <remarks>
/// Il core non sa e non deve sapere se dietro c'è NVDA, JAWS, una notifica UI
/// Automation o il motore SAPI su un dispositivo audio dedicato. Decide cosa dire e
/// quando; il come è responsabilità dell'infrastruttura, che sceglie il canale in
/// base a cosa è disponibile e alle preferenze dell'operatore.
/// </remarks>
public interface ISpeechOutput
{
    /// <summary>Pronuncia un testo.</summary>
    /// <param name="text">Testo da pronunciare.</param>
    /// <param name="interrupt">Se interrompere il parlato in corso.</param>
    void Speak(string text, bool interrupt);

    /// <summary>Interrompe il parlato in corso.</summary>
    void Stop();

    /// <summary>Aggiorna la riga di stato del display braille.</summary>
    /// <param name="text">Testo compatto.</param>
    void Braille(string text);
}

/// <summary>
/// Contesto operativo che influenza le regole di soppressione degli annunci.
/// </summary>
/// <remarks>
/// Volutamente mutabile e con proprietà semplici: viene aggiornato dai servizi
/// applicativi a ogni cambio di stato ed è letto dalla pipeline a ogni annuncio.
/// Un'astrazione più elaborata non aggiungerebbe nulla e renderebbe più difficile
/// capire, leggendo un test, perché un annuncio è stato soppresso.
/// </remarks>
public sealed class AnnouncementContext
{
    /// <summary>
    /// <c>true</c> quando è in corso una conversazione con audio attivo.
    /// </summary>
    public bool IsConversationActive { get; set; }

    /// <summary>
    /// <c>true</c> mentre l'operatore sta digitando un numero o un testo di ricerca.
    /// </summary>
    public bool IsOperatorTyping { get; set; }
}
