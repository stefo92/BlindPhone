using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Composizione verso il gateway di riunione Zoom (Cloud Room Connector).
/// </summary>
/// <remarks>
/// <para>
/// <b>Che cos'è questa strada.</b> Il gateway non è un'API: è una destinazione da
/// chiamare. Si compone un indirizzo SIP che porta dentro di sé l'identificativo
/// della riunione e il codice d'accesso, e da lì in poi la riunione è <b>una
/// telefonata come tutte le altre</b> — attesa, conferenza, toni e riaggancio
/// funzionano con i tasti che l'operatore già conosce.
/// </para>
/// <para>
/// <b>Perché vale la pena, e non è la qualità audio.</b> Alla stessa riunione si
/// arriva anche componendo il numero telefonico pubblicato nell'invito: si chiama, si
/// ascolta un messaggio registrato, si digita l'identificativo, cancelletto, si
/// aspetta, si digita il codice, cancelletto. È un percorso a ostacoli <b>a tempo e
/// tutto in ascolto</b>, dove un tono digitato troppo presto si perde e non c'è modo
/// di sapere a che punto si è. Di qui si entra con <b>una composizione sola</b>.
/// Togliere di mezzo quel risponditore automatico è la funzione; la banda larga è un
/// premio.
/// </para>
/// <para>
/// <b>La composizione è posizionale, e sbagliare i punti non dà errore.</b> Il
/// formato è
/// <c>[ID].[Codice].[Comando].[Chiave host].[Riservato].[Codice di composizione]@host</c>:
/// i campi vuoti si saltano lasciando il punto. Un punto di troppo manda in una
/// riunione che non esiste, con un messaggio che non lo spiega. Per questo la stringa
/// la costruisce il programma e non la si chiede mai all'operatore.
/// </para>
/// </remarks>
public static class ZoomRoomConnector
{
    /// <summary>Gateway pubblico di Zoom.</summary>
    /// <remarks>
    /// Alcuni enti compongono invece l'indirizzo numerico di un nodo regionale, che
    /// l'IT fornisce: per questo il gateway è un parametro e non una costante
    /// incastonata nella composizione.
    /// </remarks>
    public const string DefaultGateway = "zoomcrc.com";

    /// <summary>Esito della costruzione di una composizione.</summary>
    /// <param name="Dial">Indirizzo da comporre, completo di codice.</param>
    /// <param name="Spoken">
    /// Forma <b>priva del codice d'accesso</b>, da usare negli annunci, nel registro
    /// delle chiamate e ovunque il valore possa essere riletto.
    /// </param>
    public readonly record struct MeetingDial(string Dial, string Spoken);

    /// <summary>
    /// Costruisce la composizione verso una riunione.
    /// </summary>
    /// <param name="meetingId">Identificativo numerico della riunione.</param>
    /// <param name="passcode">Codice d'accesso numerico, se la riunione ne ha uno.</param>
    /// <param name="gateway">Gateway da raggiungere.</param>
    /// <param name="dial">Composizione costruita, se i dati sono validi.</param>
    /// <param name="error">Motivo del rifiuto, già pronunciabile.</param>
    /// <returns><c>true</c> se la composizione è stata costruita.</returns>
    /// <remarks>
    /// I rifiuti sono in italiano corrente e dicono <b>che cosa correggere</b>: sono
    /// errori che si sistemano in dieci secondi se si sa quale, e che altrimenti
    /// lasciano a riprovare a caso mentre una riunione è già cominciata.
    /// </remarks>
    public static bool TryBuild(
        string? meetingId,
        string? passcode,
        string? gateway,
        out MeetingDial dial,
        out string? error)
    {
        dial = default;

        var id = Compatta(meetingId);

        if (id.Length == 0)
        {
            error = "Manca l'identificativo della riunione";
            return false;
        }

        if (!id.All(char.IsAsciiDigit))
        {
            error = "L'identificativo della riunione e' fatto solo di cifre";
            return false;
        }

        if (id.Length is < MinIdLength or > MaxIdLength)
        {
            error = $"L'identificativo della riunione ha da {MinIdLength} a {MaxIdLength} cifre, "
                    + $"questo ne ha {id.Length}";
            return false;
        }

        var codice = Compatta(passcode);

        // IL CODICE NUMERICO NON E' QUELLO SCRITTO NELL'INVITO, ed e' la trappola
        // pratica di questa funzione. Zoom tiene due codici per la stessa riunione:
        // quello web, che puo' contenere lettere, e quello per telefono e apparati
        // SIP, che e' numerico. Chi copia dall'invito prende il primo, non entra mai,
        // e il gateway non glielo spiega.
        if (codice.Length > 0 && !codice.All(char.IsAsciiDigit))
        {
            error = "Il codice d'accesso da comporre e' solo numerico: quello con le lettere "
                    + "vale per il collegamento sul sito, non per chi entra per telefono";
            return false;
        }

        var host = Compatta(gateway);
        if (host.Length == 0) host = DefaultGateway;

        var utente = codice.Length > 0 ? $"{id}.{codice}" : id;

        dial = new MeetingDial($"{utente}@{host}", $"{id}@{host}");
        error = null;

        return true;
    }

    /// <summary>
    /// Separa l'identificativo della riunione dal codice d'accesso, come li scrive
    /// l'operatore nel campo di composizione.
    /// </summary>
    /// <param name="typed">Testo digitato.</param>
    /// <remarks>
    /// <para>
    /// I separatori ammessi sono <c>.</c>, <c>*</c> e <c>,</c>: quelli che si digitano
    /// senza modificatori e che si trovano al buio sul tastierino numerico.
    /// </para>
    /// <para>
    /// <b>Lo spazio non separa</b>, e non è una svista. Gli inviti scrivono
    /// l'identificativo a gruppi — «812 3456 7890» — e chi lo detta a voce lo
    /// scandisce allo stesso modo. Trattare lo spazio come separatore trasformerebbe
    /// l'ultimo gruppo in un codice d'accesso, e il rifiuto parlerebbe di un codice
    /// che nessuno ha scritto: un messaggio incomprensibile su un errore inventato.
    /// </para>
    /// </remarks>
    public static (string Id, string? Passcode) Split(string? typed)
    {
        var testo = typed?.Trim() ?? string.Empty;
        var taglio = testo.IndexOfAny(['.', '*', ',']);

        return taglio < 0
            ? (testo, null)
            : (testo[..taglio], testo[(taglio + 1)..]);
    }

    /// <summary>
    /// Indica se una destinazione è un indirizzo di riunione Zoom.
    /// </summary>
    /// <param name="destination">Destinazione come verrebbe composta.</param>
    /// <remarks>
    /// Serve a due cose che non c'entrano fra loro: scegliere i codec giusti per
    /// questa chiamata, e tenere il codice d'accesso fuori da quello che si pronuncia
    /// e si conserva.
    /// </remarks>
    public static bool IsMeetingAddress(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;

        var chiocciola = destination.LastIndexOf('@');
        if (chiocciola <= 0) return false;

        var host = destination[(chiocciola + 1)..].Trim();

        return host.Equals(DefaultGateway, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith("." + DefaultGateway, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Toglie il codice d'accesso da una composizione, lasciandola riconoscibile.
    /// </summary>
    /// <param name="destination">Composizione, eventualmente con il codice.</param>
    /// <returns>La stessa destinazione senza il codice.</returns>
    /// <remarks>
    /// <para>
    /// <b>È il presidio di riservatezza di questa funzione.</b> Il codice d'accesso
    /// viaggia dentro il numero composto, quindi finirebbe nel registro delle
    /// chiamate, nella ricomposizione dell'ultimo numero e — questo è il punto —
    /// <b>letto a voce alta</b> in uno stanzone dove ci sono altre persone. L'archivio
    /// è cifrato e il file non è il problema: il problema è la voce. Il codice
    /// d'accesso di una giunta comunale pronunciato in un centralino è un codice
    /// diffuso.
    /// </para>
    /// <para>
    /// Su tutto ciò che non è un indirizzo di riunione non tocca niente: un numero di
    /// telefono con un punto dentro resta com'è.
    /// </para>
    /// </remarks>
    public static string Redact(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return string.Empty;
        if (!IsMeetingAddress(destination)) return destination;

        var chiocciola = destination.LastIndexOf('@');
        var utente = destination[..chiocciola];
        var punto = utente.IndexOf('.');

        return punto < 0 ? destination : utente[..punto] + destination[chiocciola..];
    }

    /// <summary>
    /// Estrae l'identificativo della riunione da una composizione.
    /// </summary>
    /// <param name="destination">Composizione verso il gateway.</param>
    /// <returns>L'identificativo, oppure <c>null</c> se non è un indirizzo di riunione.</returns>
    public static string? MeetingIdOf(string? destination)
    {
        if (destination is null || !IsMeetingAddress(destination)) return null;

        var utente = destination[..destination.LastIndexOf('@')];
        var punto = utente.IndexOf('.');
        var id = punto < 0 ? utente : utente[..punto];

        return id.Length > 0 && id.All(char.IsAsciiDigit) ? id : null;
    }

    /// <summary>
    /// Descrive una destinazione di riunione in forma pronunciabile.
    /// </summary>
    /// <param name="destination">Composizione verso il gateway.</param>
    /// <returns>La descrizione, oppure <c>null</c> se non è un indirizzo di riunione.</returns>
    /// <remarks>
    /// Non pronuncia mai il gateway: «chiocciola zoomcrc punto com» non dice niente
    /// all'operatore e occupa il tempo in cui vorrebbe sentire il resto.
    /// </remarks>
    public static string? Describe(string? destination)
        => MeetingIdOf(destination) is { } id ? $"riunione Zoom {id}" : null;

    /// <summary>
    /// I soli codec che il gateway accetta, nell'ordine in cui vanno offerti.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zoom dichiara per il gateway <b>G.711, G.722 e Opus</b> (Opus solo su SIP).
    /// Sono già tutti nel prodotto tranne Opus, che non vale il prezzo: lo stack SIP
    /// in uso implementa quattro codec e basta, e il guadagno rispetto a G.722 lo
    /// appiattisce comunque il mixer della riunione.
    /// </para>
    /// <para>
    /// <b>G.722 per primo</b> perché qui non c'è nessun trunk che imponga la
    /// compressione, e perché è il codec a banda larga che soddisfa EN 301 549 §6.1:
    /// su questa chiamata si può negoziare il livello di qualità che la norma chiede.
    /// G.711 A-law resta come riserva.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AudioCodec> Codecs { get; } = [AudioCodec.G722, AudioCodec.PCMA];

    /// <summary>
    /// Sceglie i codec da offrire, secondo la destinazione.
    /// </summary>
    /// <param name="destination">Destinazione della chiamata.</param>
    /// <param name="configured">Preferenza configurata sull'account.</param>
    /// <returns>I codec da offrire su questa chiamata.</returns>
    /// <remarks>
    /// <para>
    /// <b>Serve perché la preferenza predefinita è sbagliata per Zoom, e in un modo
    /// che non si vede.</b> L'ordine predefinito del prodotto mette <b>G.729 per
    /// primo</b> — scelta giusta, perché è quello che i centralini italiani di
    /// riferimento negoziano nella pratica — ma il gateway di Zoom <b>non elenca
    /// G.729 fra i codec supportati</b>.
    /// </para>
    /// <para>
    /// Nel caso migliore si spreca un giro di trattativa e si ripiega su G.722. Nel
    /// caso peggiore — una postazione dove l'amministratore ha ristretto l'elenco al
    /// solo G.729, che su certi trunk è una configurazione legittima — <b>la chiamata
    /// verso la riunione non si instaura affatto</b>, e il sintomo è una chiamata che
    /// non parte senza spiegazioni.
    /// </para>
    /// <para>
    /// Su ogni altra destinazione restituisce la preferenza configurata senza
    /// toccarla: questa funzione non deve cambiare il comportamento di una sola
    /// telefonata ordinaria.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AudioCodec> CodecPreferenceFor(
        string? destination,
        IReadOnlyList<AudioCodec> configured)
        => IsMeetingAddress(destination) ? Codecs : configured;

    private const int MinIdLength = 9;
    private const int MaxIdLength = 11;

    /// <summary>
    /// Toglie spazi e separatori d'uso corrente.
    /// </summary>
    /// <remarks>
    /// Gli inviti scrivono l'identificativo a gruppi — «812 3456 7890» — e chi lo
    /// detta a voce lo scandisce allo stesso modo. Rifiutarlo per gli spazi sarebbe
    /// pedanteria a carico di chi digita a orecchio.
    /// </remarks>
    private static string Compatta(string? valore)
        => valore is null
            ? string.Empty
            : new string(valore.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
}
