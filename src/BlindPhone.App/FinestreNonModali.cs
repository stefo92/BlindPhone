using System.Windows;

namespace BlindPhone.App;

/// <summary>
/// Apre le finestre di lavoro senza bloccare la finestra principale, e ne tiene una
/// sola per tipo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché non modali.</b> Guida, rubrica ed elenco delle ultime chiamate sono
/// finestre in cui l'operatore si ferma a consultare qualcosa <i>mentre</i> lavora: una
/// guida che impedisce di usare il programma di cui spiega l'uso serve a poco, e
/// l'elenco delle chiamate ha comandi — richiama, usa per il trasferimento — che hanno
/// senso proprio con una chiamata in corso. Da modali quei comandi non erano nemmeno
/// raggiungibili: il trasferimento non si può avviare mentre un dialogo modale tiene
/// disabilitata la finestra principale.
/// </para>
/// <para>
/// <b>Perché una sola per tipo.</b> Premere due volte il tasto della rubrica apriva due
/// rubriche identiche. Chi guarda se ne accorge e chiude quella di troppo; chi non
/// guarda si ritrova due finestre con lo stesso nome nell'elenco di ALT+TAB, e nessun
/// modo di sapere quale contenga la ricerca appena fatta. Alla seconda pressione si
/// riporta avanti quella che c'è già.
/// </para>
/// <para>
/// <b>La proprietaria resta dichiarata</b>, e non è un dettaglio: una finestra di
/// proprietà si chiude insieme a chi la possiede — quindi nessuna resta orfana quando
/// la postazione si chiude — e resta davanti a lei invece di finirle sotto. Il prezzo
/// è che va anche dichiarato <c>ShowInTaskbar</c>, altrimenti sparisce da ALT+TAB:
/// vedi FinestreInAltTabTests.
/// </para>
/// <para>
/// Vive tutto sul thread dell'interfaccia, che è l'unico da cui si aprono finestre: per
/// questo il registro non ha bisogno di sincronizzazione.
/// </para>
/// </remarks>
internal static class FinestreNonModali
{
    private static readonly Dictionary<Type, Window> Aperte = [];

    /// <summary>
    /// Mostra la finestra del tipo richiesto, oppure riporta avanti quella già aperta.
    /// </summary>
    /// <typeparam name="T">Tipo della finestra.</typeparam>
    /// <param name="crea">Come costruirla, se non c'è.</param>
    /// <param name="proprietaria">Finestra proprietaria.</param>
    /// <param name="prepara">
    /// Cosa fare sulla finestra prima di mostrarla, e anche quando esisteva già: è il
    /// punto in cui una ricerca digitata nel campo di composizione arriva alla rubrica,
    /// o un argomento della guida diventa quello mostrato.
    /// </param>
    /// <returns>La finestra mostrata.</returns>
    public static T MostraOAttiva<T>(Func<T> crea, Window proprietaria, Action<T>? prepara = null)
        where T : Window
    {
        if (Aperte.TryGetValue(typeof(T), out var nota) && nota is T esistente)
        {
            prepara?.Invoke(esistente);

            // Ridotta a icona non si attiva da sola: resterebbe una finestra che
            // risponde ai comandi e non si vede da nessuna parte.
            if (esistente.WindowState == WindowState.Minimized)
            {
                esistente.WindowState = WindowState.Normal;
            }

            esistente.Activate();
            return esistente;
        }

        var finestra = crea();
        finestra.Owner = proprietaria;

        // Si dimentica alla chiusura, altrimenti la pressione successiva proverebbe a
        // riattivare una finestra chiusa: mostrarla di nuovo solleverebbe un'eccezione.
        finestra.Closed += (_, _) => Aperte.Remove(typeof(T));

        Aperte[typeof(T)] = finestra;
        prepara?.Invoke(finestra);
        finestra.Show();

        return finestra;
    }
}
