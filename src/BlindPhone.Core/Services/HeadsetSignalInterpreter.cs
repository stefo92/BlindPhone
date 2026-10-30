using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Distingue, nei valori che una cuffia manda, le pressioni vere dell'operatore.
/// </summary>
/// <remarks>
/// <para>
/// <b>Una cuffia non manda «pressioni»: manda stati.</b> Ogni rapporto HID porta il
/// valore di tutti i pulsanti insieme, quindi premendo il tasto del microfono arriva
/// anche il valore del pulsante della chiamata, uguale a prima. Reagire ai valori
/// invece che ai cambiamenti significherebbe rispondere a ogni rapporto, e chiudere
/// la chiamata nell'istante in cui l'operatore si silenzia.
/// </para>
/// <para>
/// <b>Il pulsante della chiamata è il caso difficile, e ha due nature.</b> Sulle
/// cuffie da ufficio è uno stato, la cornetta alzata o abbassata: vale 1 per tutta la
/// conversazione e torna a 0 quando si chiude. Su altre è un impulso: vale 1 finché
/// il dito è sul tasto e 0 appena lo si toglie. Letto come stato, l'impulso
/// risponderebbe alla pressione e chiuderebbe al rilascio, un quarto di secondo dopo.
/// La regola che li tiene insieme:
/// </para>
/// <list type="bullet">
///   <item><description>
///   gli altri pulsanti si leggono sempre come impulsi: conta solo il passaggio da 0
///   a 1, e tenerli premuti non ripete niente;
///   </description></item>
///   <item><description>
///   il pulsante della chiamata dichiarato come stato conta a ogni cambiamento, in
///   tutte e due le direzioni;
///   </description></item>
///   <item><description>
///   se dopo una pressione torna indietro entro <see cref="ReleaseWindow"/>, non era
///   uno stato ma un impulso dichiarato male: il ritorno non conta, e da lì in poi lo
///   si legge come impulso. Nessuno risponde e chiude in meno di un secondo.
///   </description></item>
/// </list>
/// <para>
/// <b>Quando la cuffia ha la spia della cornetta alzata, BlindPhone la tiene allineata
/// e la cuffia la segue</b>: se si risponde da tastiera la cuffia passa «in linea» da
/// sola, e il suo pulsante, la volta dopo, manda «riagganciato». Un valore che
/// coincide con lo stato appena impostato è l'eco di quell'allineamento, non un dito
/// sul tasto, e non conta: è la riga che impedisce alla cuffia di chiudere la
/// chiamata a cui si è appena risposto dalla tastiera.
/// </para>
/// <para>
/// Una istanza per cuffia collegata, usata da un thread solo: è il thread che legge
/// quella cuffia. Non conserva nulla fra un collegamento e l'altro, perché una cuffia
/// ricollegata può essere un'altra cuffia.
/// </para>
/// </remarks>
public sealed class HeadsetSignalInterpreter
{
    /// <summary>
    /// Entro quanto un ritorno del pulsante della chiamata è il rilascio di un
    /// impulso e non una seconda pressione.
    /// </summary>
    /// <remarks>
    /// Una pressione decisa dura un quarto di secondo, una lenta mezzo. Rispondere e
    /// chiudere apposta in meno di così non capita, mentre la stessa finestra non può
    /// essere molto più lunga: chi tiene il dito sul tasto oltre questo tempo, su una
    /// cuffia a impulsi non ancora riconosciuta, chiuderebbe al rilascio.
    /// </remarks>
    public static readonly TimeSpan ReleaseWindow = TimeSpan.FromMilliseconds(700);

    private readonly HeadsetCapabilities _capabilities;
    private readonly Dictionary<HeadsetButton, bool> _last = [];

    private bool _hookIsMomentary;
    private DateTimeOffset? _lastHookPressAt;
    private bool _valueBeforeHookPress;
    private bool _expectedOffHook;

    /// <summary>Crea l'interprete per una cuffia.</summary>
    /// <param name="capabilities">Pulsanti e spie dichiarati dalla cuffia.</param>
    public HeadsetSignalInterpreter(HeadsetCapabilities capabilities)
    {
        _capabilities = capabilities;
        _hookIsMomentary = !capabilities.HookSwitchIsAbsolute;
    }

    /// <summary>
    /// <c>true</c> se il pulsante della chiamata si legge come impulso: perché è
    /// dichiarato così, o perché si è comportato così.
    /// </summary>
    public bool HookSwitchIsMomentary => _hookIsMomentary;

    /// <summary>
    /// Registra un valore letto dalla cuffia.
    /// </summary>
    /// <param name="button">Pulsante a cui si riferisce.</param>
    /// <param name="value">Valore letto: premuto, o cornetta alzata.</param>
    /// <param name="at">Istante della lettura.</param>
    /// <returns><c>true</c> se è una pressione dell'operatore, da tradurre in un comando.</returns>
    /// <remarks>
    /// Il valore di partenza di ogni pulsante è 0, cioè rilasciato e cornetta
    /// abbassata. È lo stato in cui una cuffia si trova quasi sempre, e lo standard
    /// non dà un modo affidabile di chiederglielo prima che mandi qualcosa: molte
    /// rifiutano la richiesta, e aspettare il primo rapporto per stabilirlo
    /// significherebbe perdere la prima pressione, cioè proprio la risposta.
    /// </remarks>
    public bool Feed(HeadsetButton button, bool value, DateTimeOffset at)
    {
        var previous = _last.GetValueOrDefault(button);
        if (previous == value) return false;

        _last[button] = value;

        if (button != HeadsetButton.HookSwitch || _hookIsMomentary)
        {
            // Impulso: conta l'appoggio del dito, non il rilascio.
            return value;
        }

        // IL RITORNO DI UN IMPULSO DICHIARATO COME STATO.
        //
        // Va guardato prima dell'eco: il rilascio puo' arrivare quando la spia e' gia'
        // stata aggiornata, e allora non coinciderebbe con lo stato atteso e verrebbe
        // preso per una seconda pressione — quella che chiude la chiamata appena
        // risposta.
        if (_lastHookPressAt is { } ultima && at - ultima < ReleaseWindow && value == _valueBeforeHookPress)
        {
            _hookIsMomentary = true;
            return false;
        }

        // L'eco dell'allineamento: la cuffia ha seguito la spia impostata da BlindPhone.
        if (_capabilities.FollowsOffHook && value == _expectedOffHook) return false;

        _lastHookPressAt = at;
        _valueBeforeHookPress = previous;
        return true;
    }

    /// <summary>
    /// Registra lo stato che la cuffia sta mostrando, appena BlindPhone gliel'ha
    /// mandato.
    /// </summary>
    /// <param name="indicators">Spie appena impostate.</param>
    /// <remarks>
    /// <para>
    /// Una cuffia che segue la spia della cornetta alzata cambia stato senza che
    /// nessuno abbia toccato il pulsante, e non è detto che lo dica: molte non mandano
    /// nessun rapporto. Senza aggiornare qui l'ultimo valore conosciuto, la pressione
    /// successiva — quella che porta la cuffia da «in linea» a «riagganciato» —
    /// risulterebbe uguale al valore vecchio e andrebbe persa.
    /// </para>
    /// <para>
    /// Sulle cuffie a impulsi la spia è solo una luce, e il valore del pulsante non
    /// cambia: lì non si tocca niente.
    /// </para>
    /// </remarks>
    public void Reflect(HeadsetIndicators indicators)
    {
        _expectedOffHook = indicators.OffHook;

        if (_capabilities.FollowsOffHook && !_hookIsMomentary)
        {
            _last[HeadsetButton.HookSwitch] = indicators.OffHook;
        }
    }
}
