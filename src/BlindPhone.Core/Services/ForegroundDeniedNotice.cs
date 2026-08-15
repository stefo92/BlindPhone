using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Che cosa dire all'operatore quando la finestra torna davanti ma Windows non le
/// concede il primo piano.
/// </summary>
/// <remarks>
/// <para>
/// <b>È il caso peggiore che possa capitare in silenzio.</b> La finestra si vede,
/// quindi chi guarda pensa che sia tutto a posto; i tasti però continuano ad andare
/// al programma di prima. Un operatore che preme F2 per rispondere lo sta premendo
/// dentro Esplora file, dove F2 rinomina il file selezionato, o dentro un foglio di
/// calcolo, dove apre la cella in modifica. Non solo la chiamata non viene presa: si
/// combina un guaio altrove, senza accorgersene.
/// </para>
/// <para>
/// Per questo la frase esiste come testo verificabile e non come stringa infilata
/// nell'interfaccia: dice che cosa è successo e dà il tasto che <b>funziona
/// comunque</b>, cioè la scorciatoia globale, che Windows consegna a BlindPhone
/// qualunque finestra sia davanti.
/// </para>
/// </remarks>
public static class ForegroundDeniedNotice
{
    /// <summary>
    /// Compone l'avviso sulla base delle combinazioni assegnate adesso.
    /// </summary>
    /// <param name="profile">Profilo di scorciatoie attivo.</param>
    /// <remarks>
    /// Se «Rispondi» non ha una scorciatoia globale non resta nessun tasto che
    /// funzioni, e dirlo è l'unica cosa utile: l'operatore deve assegnarla, non
    /// continuare a premere un tasto che finisce altrove.
    /// </remarks>
    public static string Build(HotkeyProfile profile)
    {
        var answer = profile.Get(HotkeyCommand.Answer, HotkeyScope.Global)?.SpeechText;

        return answer is null
            ? "Windows non ha concesso il primo piano a BlindPhone: i tasti vanno ancora al programma di prima. "
              + "Assegna una scorciatoia globale a Rispondi dalle impostazioni dei tasti rapidi."
            : $"Windows non ha concesso il primo piano a BlindPhone: rispondi con {answer}, "
              + "che funziona da qualunque finestra.";
    }
}
