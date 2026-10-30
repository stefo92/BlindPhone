namespace BlindPhone.Core.Domain;

/// <summary>I pulsanti di una cuffia da ufficio che BlindPhone sa ascoltare.</summary>
/// <remarks>
/// Sono pochi di proposito. Una cuffia ne ha spesso altri — volume, assistente vocale,
/// accoppiamento — ma quelli li gestisce la cuffia da sé o il sistema operativo, e
/// BlindPhone non deve rubarli. Questi sono i soli che la pagina Telefonia dello
/// standard HID dichiara per il controllo della chiamata.
/// </remarks>
public enum HeadsetButton
{
    /// <summary>
    /// Il pulsante della chiamata, «Hook Switch» nello standard: è il gancio della
    /// cornetta, alzato per rispondere e abbassato per chiudere.
    /// </summary>
    HookSwitch,

    /// <summary>Il tasto del microfono, «Phone Mute».</summary>
    PhoneMute,

    /// <summary>
    /// Il tasto «Flash»: il colpetto sul gancio con cui i telefoni da tavolo passano
    /// alla seconda chiamata.
    /// </summary>
    Flash,

    /// <summary>Il tasto di richiamata, «Redial».</summary>
    Redial,

    /// <summary>
    /// Il tasto Play e pausa, per le cuffie che non hanno i pulsanti telefonici e
    /// mandano soltanto quello.
    /// </summary>
    /// <remarks>
    /// Non arriva dal canale telefonico ma dalla tastiera multimediale del sistema, e
    /// lo usano anche i lettori musicali e video: per questo si ascolta solo se
    /// l'operatore lo chiede, e solo mentre una chiamata squilla o è in corso. Vedi
    /// <c>HeadsetSettings.MediaKeyFallback</c>.
    /// </remarks>
    PlayPause,
}

/// <summary>
/// Le spie della cuffia, cioè quello che la cuffia mostra dello stato della chiamata.
/// </summary>
/// <param name="Ring">Una chiamata sta squillando.</param>
/// <param name="OffHook">
/// L'operatore è su una linea: in conversazione, in una chiamata che sta partendo o
/// dentro un trasferimento. È la cornetta alzata.
/// </param>
/// <param name="Mute">Il microfono è silenziato, durante una chiamata.</param>
/// <param name="Hold">C'è almeno una chiamata in attesa.</param>
/// <remarks>
/// <para>
/// <b>Non sono solo luci.</b> Sulle cuffie da ufficio la spia della cornetta alzata
/// è anche lo stato del pulsante: la cuffia si considera «in linea» finché quella
/// spia è accesa, e alla pressione successiva manda «riagganciato». Una cuffia che
/// non viene tenuta allineata resta convinta di essere a riposo mentre l'operatore
/// sta parlando, e il primo tocco sul pulsante, invece di chiudere, non fa niente.
/// </para>
/// <para>
/// Il valore predefinito, tutto spento, è lo stato a riposo.
/// </para>
/// </remarks>
public readonly record struct HeadsetIndicators(
    bool Ring = false,
    bool OffHook = false,
    bool Mute = false,
    bool Hold = false)
{
    /// <summary>Nessuna spia accesa: la cuffia a riposo.</summary>
    public bool IsIdle => !Ring && !OffHook && !Mute && !Hold;
}

/// <summary>
/// Che cosa ha una cuffia: quali pulsanti manda e quali spie accetta.
/// </summary>
/// <param name="Buttons">Pulsanti dichiarati dalla cuffia.</param>
/// <param name="HookSwitchIsAbsolute">
/// Il pulsante della chiamata è dichiarato come stato — alzato o abbassato — e non
/// come impulso. È il caso di quasi tutte le cuffie da ufficio, ed è ciò che rende
/// necessario tenerle allineate: vedi <see cref="Services.HeadsetSignalInterpreter"/>.
/// </param>
/// <param name="Leds">Usage delle spie che la cuffia accetta, nella pagina LED.</param>
public sealed record HeadsetCapabilities(
    IReadOnlySet<HeadsetButton> Buttons,
    bool HookSwitchIsAbsolute,
    IReadOnlySet<ushort> Leds)
{
    /// <summary>La cuffia ha il pulsante con cui rispondere e chiudere.</summary>
    public bool CanAnswer => Buttons.Contains(HeadsetButton.HookSwitch);

    /// <summary>
    /// La cuffia accetta la spia della cornetta alzata, quindi BlindPhone può dirle in
    /// che stato si trova la chiamata.
    /// </summary>
    public bool FollowsOffHook => Leds.Contains(HidTelephonyUsages.OffHookLed);

    /// <summary>
    /// Che cosa sa fare la cuffia, in una frase da ascoltare.
    /// </summary>
    /// <remarks>
    /// Serve al pannello delle impostazioni, ed è lì che l'operatore scopre se la sua
    /// cuffia ha il pulsante che gli serve. Una cuffia che ha soltanto il tasto del
    /// microfono non risponde alle chiamate, e saperlo prima evita di premerlo con il
    /// telefono che squilla.
    /// </remarks>
    public string Describe()
    {
        var pulsanti = new List<string>();
        if (Buttons.Contains(HeadsetButton.HookSwitch)) pulsanti.Add("risposta e chiusura");
        if (Buttons.Contains(HeadsetButton.PhoneMute)) pulsanti.Add("microfono");
        if (Buttons.Contains(HeadsetButton.Flash)) pulsanti.Add("attesa");
        if (Buttons.Contains(HeadsetButton.Redial)) pulsanti.Add("richiamata");

        var spie = new List<string>();
        if (Leds.Contains(HidTelephonyUsages.RingLed)) spie.Add("squillo");
        if (Leds.Contains(HidTelephonyUsages.OffHookLed)) spie.Add("linea");
        if (Leds.Contains(HidTelephonyUsages.MuteLed)) spie.Add("microfono muto");
        if (Leds.Contains(HidTelephonyUsages.HoldLed)) spie.Add("attesa");

        var testo = pulsanti.Count == 0
            ? "nessun pulsante telefonico"
            : "pulsanti: " + string.Join(", ", pulsanti);

        return spie.Count == 0
            ? testo + "; nessuna spia comandabile"
            : testo + "; spie: " + string.Join(", ", spie);
    }
}

/// <summary>
/// I codici con cui lo standard HID nomina i pulsanti e le spie di una cuffia.
/// </summary>
/// <remarks>
/// <para>
/// Vengono dalle tabelle degli usage HID: pagina Telefonia (0x0B) per i pulsanti,
/// pagina LED (0x08) per le spie, pagina Consumer (0x0C) per il tasto multimediale.
/// Sono gli stessi che usano tutti i programmi di telefonia certificati per le cuffie
/// da ufficio, ed è questo che permette di non dipendere da nessun produttore: una
/// cuffia che li rispetta funziona, qualunque marca porti.
/// </para>
/// <para>
/// La traduzione da codice a pulsante sta qui, nel core, e non accanto all'accesso
/// al dispositivo: così si prova senza una cuffia collegata, e chi legge il codice di
/// sistema trova solo byte che vanno e vengono.
/// </para>
/// </remarks>
public static class HidTelephonyUsages
{
    /// <summary>Pagina Telefonia.</summary>
    public const ushort TelephonyPage = 0x0B;

    /// <summary>Pagina LED, quella delle spie.</summary>
    public const ushort LedPage = 0x08;

    /// <summary>Pagina Consumer, quella dei tasti multimediali.</summary>
    public const ushort ConsumerPage = 0x0C;

    /// <summary>Pulsante della chiamata, il gancio.</summary>
    public const ushort HookSwitch = 0x20;

    /// <summary>Colpetto sul gancio.</summary>
    public const ushort Flash = 0x21;

    /// <summary>Richiamata.</summary>
    public const ushort Redial = 0x24;

    /// <summary>Microfono muto.</summary>
    public const ushort PhoneMute = 0x2F;

    /// <summary>Spia del microfono muto.</summary>
    public const ushort MuteLed = 0x09;

    /// <summary>Spia della cornetta alzata.</summary>
    public const ushort OffHookLed = 0x17;

    /// <summary>Spia dello squillo.</summary>
    public const ushort RingLed = 0x18;

    /// <summary>Spia dell'attesa.</summary>
    public const ushort HoldLed = 0x20;

    /// <summary>Tasto Play e pausa.</summary>
    public const ushort PlayPause = 0xCD;

    /// <summary>Le spie che BlindPhone sa comandare.</summary>
    public static IReadOnlySet<ushort> KnownLeds { get; } =
        new HashSet<ushort> { MuteLed, OffHookLed, RingLed, HoldLed };

    /// <summary>
    /// Il pulsante che corrisponde a un usage, se è uno di quelli che BlindPhone
    /// ascolta.
    /// </summary>
    /// <param name="usagePage">Pagina dell'usage.</param>
    /// <param name="usage">Usage dentro la pagina.</param>
    /// <remarks>
    /// Tutto il resto restituisce <c>null</c>, e va ignorato: la pagina Telefonia ha
    /// anche un tastierino, i tasti di altoparlante e di conferenza, che su una cuffia
    /// non ci sono e che nessuna cuffia manda per dire «rispondi».
    /// </remarks>
    public static HeadsetButton? ButtonFor(ushort usagePage, ushort usage) => (usagePage, usage) switch
    {
        (TelephonyPage, HookSwitch) => HeadsetButton.HookSwitch,
        (TelephonyPage, PhoneMute) => HeadsetButton.PhoneMute,
        (TelephonyPage, Flash) => HeadsetButton.Flash,
        (TelephonyPage, Redial) => HeadsetButton.Redial,
        (ConsumerPage, PlayPause) => HeadsetButton.PlayPause,
        _ => null,
    };

    /// <summary>
    /// Le spie da accendere per mostrare uno stato, fra quelle che la cuffia ha.
    /// </summary>
    /// <param name="indicators">Stato da mostrare.</param>
    /// <param name="available">Spie dichiarate dalla cuffia.</param>
    /// <remarks>
    /// Quelle che non compaiono nel risultato vanno spente: un rapporto di uscita HID
    /// porta tutte le spie insieme, e quella che non si accende si spegne.
    /// </remarks>
    public static IReadOnlyList<ushort> LitLeds(HeadsetIndicators indicators, IReadOnlySet<ushort> available)
    {
        var accese = new List<ushort>(4);

        if (indicators.Ring && available.Contains(RingLed)) accese.Add(RingLed);
        if (indicators.OffHook && available.Contains(OffHookLed)) accese.Add(OffHookLed);
        if (indicators.Mute && available.Contains(MuteLed)) accese.Add(MuteLed);
        if (indicators.Hold && available.Contains(HoldLed)) accese.Add(HoldLed);

        return accese;
    }
}
