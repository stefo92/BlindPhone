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

    /// <summary>
    /// Per quanto, dopo un cambio della spia, un valore uguale alla spia di prima si
    /// riconosce ancora come un rapporto partito prima del cambio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Il rapporto che arriva dopo il cambio può essere stato scritto prima.</b> La
    /// cuffia manda un rapporto ogni volta che cambia uno qualunque dei suoi valori, e
    /// ognuno porta anche il pulsante della chiamata. Se uno parte mentre la spia
    /// nuova è ancora in viaggio, porta lo stato vecchio. Due casi visti sul campo:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <b>La spia che cambia due volte.</b> All'avvio del trasferimento il chiamante
    ///   passa in attesa un istante prima che la procedura esista, e la linea va da
    ///   «alzata» ad «abbassata» e di nuovo ad «alzata» in pochi millisecondi. La
    ///   conferma di «abbassata» arrivava quando lo stato atteso era già tornato
    ///   «alzata», e veniva presa per una pressione: il chiamante cadeva nel momento
    ///   in cui l'operatore cominciava a passarlo.
    ///   </description></item>
    ///   <item><description>
    ///   <b>La spia mostrata a lungo.</b> La Jabra Evolve 30 II, seicento millisecondi
    ///   dopo ogni cambio della linea, manda un secondo rapporto per un altro dei suoi
    ///   valori. Il 26 novembre 2026 una chiamata è stata chiusa proprio in quel
    ///   momento: il secondo rapporto è arrivato quattro millisecondi dopo lo
    ///   spegnimento della spia, ancora con la cornetta alzata. La linea era rimasta
    ///   accesa più di mezzo secondo, quindi il valore non era riconosciuto come eco e
    ///   contava come pressione. Il ritorno subito dopo faceva credere la cuffia a
    ///   impulsi, e da lì l'eco di ogni risposta chiudeva la chiamata appena presa,
    ///   fino al riavvio.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Per questo vale per qualunque cambio, comunque fosse lunga la spia di prima.
    /// Mezzo secondo copre la cuffia senza fili, che segue la spia in quattro o cinque
    /// decimi (la Jabra Link 380 dello stesso registro), e resta sotto
    /// <see cref="ReleaseWindow"/>. Una pressione vera così vicina a un cambio della
    /// spia è chi risponde e chiude nello stesso istante, cioè il guasto da evitare: si
    /// perde solo quella, e la successiva conta.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(500);

    private readonly HeadsetCapabilities _capabilities;
    private readonly Dictionary<HeadsetButton, bool> _last = [];

    private bool _hookIsMomentary;
    private DateTimeOffset? _lastHookPressAt;
    private bool _valueBeforeHookPress;
    private bool _expectedOffHook;
    private DateTimeOffset _expectedOffHookSince = DateTimeOffset.MinValue;
    private bool _replacedOffHook;
    private DateTimeOffset _replacedOffHookUntil = DateTimeOffset.MinValue;

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

        if (button != HeadsetButton.HookSwitch) return value;

        if (_hookIsMomentary)
        {
            // L'ECO DELLA LINEA ACCESA NON E' UN DITO, NEMMENO LETTA A IMPULSI.
            //
            // Una cuffia che segue la spia ma e' stata creduta a impulsi alza il gancio
            // da sola appena la linea si accende: letto come appoggio del dito, chiude
            // la chiamata nell'istante in cui si e' risposto. E' il seguito del guasto
            // descritto in EchoWindow, e se un'altra strada riportasse qui la stessa
            // cuffia la conseguenza sarebbe la stessa. Una cuffia davvero a impulsi
            // perde solo la pressione data in quel mezzo secondo.
            if (value && _capabilities.FollowsOffHook && _expectedOffHook && at - _expectedOffHookSince < EchoWindow)
            {
                return false;
            }

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

        if (_capabilities.FollowsOffHook)
        {
            // L'eco dell'allineamento: la cuffia ha seguito la spia impostata da
            // BlindPhone.
            if (value == _expectedOffHook) return false;

            // Un rapporto partito prima che la spia nuova arrivasse: vedi EchoWindow.
            // Non dice dove si trova la cuffia adesso, quindi non entra nemmeno
            // nell'ultimo valore conosciuto. Se la cuffia poi segue la spia senza
            // dirlo, la pressione successiva deve trovare lo stato della spia, non
            // quello vecchio: altrimenti risulterebbe uguale e andrebbe persa.
            if (value == _replacedOffHook && at < _replacedOffHookUntil)
            {
                _last[button] = _expectedOffHook;
                return false;
            }
        }

        _lastHookPressAt = at;
        _valueBeforeHookPress = previous;
        return true;
    }

    /// <summary>
    /// Registra lo stato che la cuffia sta mostrando, appena BlindPhone gliel'ha
    /// mandato.
    /// </summary>
    /// <param name="indicators">Spie appena impostate.</param>
    /// <param name="at">Istante in cui sono state scritte, sullo stesso orologio delle letture.</param>
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
    public void Reflect(HeadsetIndicators indicators, DateTimeOffset at)
    {
        if (indicators.OffHook != _expectedOffHook)
        {
            // Qualunque stato, breve o lungo, puo' avere un rapporto ancora in viaggio:
            // vedi EchoWindow.
            _replacedOffHook = _expectedOffHook;
            _replacedOffHookUntil = at + EchoWindow;

            _expectedOffHook = indicators.OffHook;
            _expectedOffHookSince = at;
        }

        if (_capabilities.FollowsOffHook && !_hookIsMomentary)
        {
            _last[HeadsetButton.HookSwitch] = indicators.OffHook;
        }
    }
}
