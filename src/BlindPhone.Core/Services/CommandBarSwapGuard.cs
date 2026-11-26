namespace BlindPhone.Core.Services;

/// <summary>
/// Trattiene il primo clic su un pulsante che chiude una chiamata, quando ha appena
/// preso il posto di un altro nella barra della chiamata.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il pulsante cambia sotto il dito.</b> Appena si risponde, «Riaggancia» compare
/// esattamente dove un istante prima c'era «Rispondi», e il focus della tastiera passa
/// a lui. Un secondo clic dato per abitudine, o il clic che arriva dopo aver già
/// risposto con il tasto, chiude la chiamata appena presa. Il 26 novembre 2026, su una
/// sola postazione, è successo ventidue volte: i clic arrivavano fra 0,6 e 2,2 secondi
/// dopo la risposta, e il chiamante cadeva senza che l'operatore capisse perché.
/// </para>
/// <para>
/// <b>Vale solo per i pulsanti che chiudono</b> — Riaggancia e Rifiuta — e solo se al
/// loro posto, nella barra di prima, c'era un pulsante diverso. Gli altri scambi non
/// fanno perdere nessuno: chi mette in attesa per sbaglio riprende con un clic. Un
/// pulsante che era già lì, o che compare in un posto prima vuoto, non viene
/// trattenuto. Nemmeno quello che prende il posto di «Componi»: la chiamata in uscita
/// non la fa partire un clic su quel pulsante ma il campo di composizione o la
/// rubrica, e chi la chiude subito ha visto di aver chiamato il numero sbagliato.
/// </para>
/// <para>
/// <b>Si trattiene un clic solo, il primo.</b> Chi voleva davvero chiudere preme di
/// nuovo e chiude: l'avviso gli dice perché il primo non è bastato. I tasti e il
/// pulsante della cuffia non passano di qui, e chiudono subito come sempre.
/// </para>
/// <para>
/// Usata dal thread dell'interfaccia, che è l'unico a ricostruire la barra e a
/// ricevere i clic.
/// </para>
/// </remarks>
public sealed class CommandBarSwapGuard
{
    /// <summary>
    /// Per quanto, dopo essere comparso al posto di un altro, un pulsante che chiude
    /// trattiene il primo clic.
    /// </summary>
    /// <remarks>
    /// Copre i clic sbagliati visti sul campo, l'ultimo a 2,2 secondi, con un margine.
    /// Lo stesso giorno la chiusura voluta più rapida dal pulsante è arrivata dopo
    /// quattro secondi: chi chiude prima di così ha sbagliato numero, e gli costa un
    /// clic in più.
    /// </remarks>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(2500);

    private CallActionButton[] _shown = [];
    private readonly Dictionary<CallAction, Arrival> _arrivals = [];

    /// <summary>
    /// Registra i pulsanti appena mostrati nella barra, nell'ordine in cui compaiono.
    /// </summary>
    /// <param name="buttons">Pulsanti della barra, da sinistra a destra.</param>
    /// <param name="at">Istante della ricostruzione.</param>
    /// <remarks>
    /// Una ricostruzione che lascia un pulsante al suo posto non ne rinnova l'arrivo:
    /// la barra si rifà anche quando cambia solo un'etichetta o un pulsante si spegne,
    /// e il tempo va contato da quando il pulsante è comparso, non dall'ultima volta che
    /// è stato ridisegnato.
    /// </remarks>
    public void Shown(IReadOnlyList<CallActionButton> buttons, DateTimeOffset at)
    {
        foreach (var azione in _arrivals.Keys.ToList())
        {
            if (!buttons.Any(b => b.Action == azione)) _arrivals.Remove(azione);
        }

        for (var posto = 0; posto < buttons.Count && posto < _shown.Length; posto++)
        {
            var adesso = buttons[posto];
            var prima = _shown[posto];

            if (!Closes(adesso.Action) || prima.Action == adesso.Action) continue;
            if (prima.Action == CallAction.FocusDialer) continue;

            _arrivals[adesso.Action] = new Arrival(adesso.Label, prima.Label, at);
        }

        _shown = [.. buttons];
    }

    /// <summary>
    /// Decide se un clic va trattenuto.
    /// </summary>
    /// <param name="action">Azione del pulsante premuto.</param>
    /// <param name="at">Istante del clic.</param>
    /// <returns>
    /// Il clic trattenuto, con i nomi dei due pulsanti; <c>null</c> se il clic va
    /// eseguito.
    /// </returns>
    public HeldClick? Intercept(CallAction action, DateTimeOffset at)
    {
        // Il primo clic consuma l'arrivo, trattenuto o no: il secondo passa sempre.
        if (!_arrivals.Remove(action, out var arrivo)) return null;

        var trascorso = at - arrivo.At;
        return trascorso < SettleTime ? new HeldClick(arrivo.Label, arrivo.ReplacedLabel, trascorso) : null;
    }

    /// <summary><c>true</c> per i pulsanti che chiudono una chiamata.</summary>
    public static bool Closes(CallAction action) => action is CallAction.HangUp or CallAction.Reject;

    private readonly record struct Arrival(string Label, string ReplacedLabel, DateTimeOffset At);
}

/// <summary>Un clic trattenuto da <see cref="CommandBarSwapGuard"/>.</summary>
/// <param name="Label">Nome del pulsante premuto.</param>
/// <param name="ReplacedLabel">Nome del pulsante che stava al suo posto.</param>
/// <param name="SinceArrival">Da quanto il pulsante era comparso.</param>
public readonly record struct HeldClick(string Label, string ReplacedLabel, TimeSpan SinceArrival);
