namespace BlindPhone.App;

/// <summary>
/// Riconosce le combinazioni premute che non sono mai arrivate alla finestra.
/// </summary>
/// <remarks>
/// <para>
/// Una combinazione registrata come scorciatoia da un altro programma, o tenuta da
/// Windows, viene consegnata a quello: della pressione la finestra in primo piano non
/// vede nulla, del rilascio sì. Un rilascio senza la sua pressione è quindi il segno
/// che la combinazione è presa altrove.
/// </para>
/// <para>
/// La regola ha due eccezioni, ed entrambe nascono da come si apre la finestra di
/// cattura. Invio, se la si apre dalla tabella, e Alt A, se la si apre dal pulsante
/// Riassegna, sono ancora giù quando la finestra compare: il loro rilascio arriva qui
/// senza pressione, ma non è una combinazione mancata. Per questo si annotano i tasti
/// giù all'apertura, e il loro primo rilascio si ignora.
/// </para>
/// <para>
/// Sta in una classe a sé perché la regola si possa provare senza una tastiera: dentro
/// la finestra dipenderebbe da eventi che solo una pressione vera produce.
/// </para>
/// </remarks>
internal sealed class PressioniMancate
{
    private readonly HashSet<string> _downAtOpening = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pressed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Annota i tasti già giù quando la finestra si apre.</summary>
    /// <param name="keys">Nomi dei tasti giù in quel momento.</param>
    public void Opened(IEnumerable<string> keys) => _downAtOpening.UnionWith(keys);

    /// <summary>Annota la pressione di un tasto arrivata alla finestra.</summary>
    /// <param name="key">Nome del tasto.</param>
    public void Pressed(string key)
    {
        _pressed.Add(key);

        // Da qui in poi il suo rilascio e' quello di una pressione vista: non serve
        // piu' ricordare che era giu' all'apertura.
        _downAtOpening.Remove(key);
    }

    /// <summary>Registra il rilascio di un tasto e dice se la sua pressione è mancata.</summary>
    /// <param name="key">Nome del tasto.</param>
    /// <returns>
    /// <c>true</c> se la finestra non ha mai visto la pressione di questo tasto: la
    /// combinazione è stata consegnata a qualcun altro.
    /// </returns>
    public bool ReleasedWithoutPress(string key)
    {
        if (_pressed.Remove(key)) return false;
        if (_downAtOpening.Remove(key)) return false;

        return true;
    }
}
