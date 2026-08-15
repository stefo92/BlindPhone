namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Canale di comunicazione diretta con uno screen reader.
/// </summary>
/// <remarks>
/// <para>
/// È un <b>potenziamento</b>, non il canale principale. Il canale principale resta
/// UI Automation, che è standard, non richiede dipendenze e funziona con qualunque
/// tecnologia assistiva conforme. Se nessun bridge diretto è disponibile,
/// l'applicazione deve restare pienamente utilizzabile.
/// </para>
/// <para>
/// Il bridge diretto serve dove UIA non arriva bene: controllo fine
/// sull'interruzione del parlato e scrittura sulla riga braille.
/// </para>
/// </remarks>
public interface IScreenReaderBridge
{
    /// <summary>Nome dello screen reader servito da questo bridge, per la diagnostica.</summary>
    string Name { get; }

    /// <summary>
    /// Indica se il bridge è utilizzabile adesso, cioè se lo screen reader è in
    /// esecuzione e le sue API rispondono.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Pronuncia un testo.</summary>
    /// <param name="text">Testo da pronunciare.</param>
    /// <param name="interrupt">Se interrompere il parlato in corso.</param>
    /// <returns><c>true</c> se lo screen reader ha accettato il messaggio.</returns>
    bool Speak(string text, bool interrupt);

    /// <summary>Scrive un messaggio sul display braille.</summary>
    /// <param name="text">Testo compatto da mostrare.</param>
    /// <returns><c>true</c> se il messaggio è stato accettato.</returns>
    bool Braille(string text);

    /// <summary>Interrompe il parlato in corso.</summary>
    /// <returns><c>true</c> se il comando è stato accettato.</returns>
    bool StopSpeech();
}
