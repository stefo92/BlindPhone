namespace BlindPhone.Core.Domain;

/// <summary>
/// Codec audio negoziabili per la conversazione.
/// </summary>
/// <remarks>
/// L'elenco corrisponde a quelli che lo stack SIP implementa davvero, verificati
/// sulla libreria e non desunti dalla documentazione: <c>AudioEncoder</c> dichiara
/// molti valori nell'enumerazione ma ne codifica solo quattro.
/// </remarks>
public enum AudioCodec
{
    /// <summary>
    /// G.722, banda larga a 16 kHz.
    /// </summary>
    /// <remarks>
    /// È l'unico dei quattro che soddisfa EN 301 549 §6.1, il quale richiede, per
    /// l'ICT con comunicazione vocale bidirezionale, il supporto di una banda audio
    /// con limite superiore di almeno 7000 Hz. Sugli interni di un centralino
    /// moderno è disponibile praticamente sempre.
    /// </remarks>
    G722,

    /// <summary>
    /// G.711 A-law, banda stretta a 8 kHz.
    /// </summary>
    /// <remarks>
    /// È la variante europea di G.711 ed è il minimo comune denominatore di
    /// qualunque apparato: se la negoziazione fallisce con tutto il resto, questo
    /// funziona. Va sempre lasciato attivo come rete di sicurezza.
    /// </remarks>
    PCMA,

    /// <summary>
    /// G.729, banda stretta compressa a 8 kbit/s.
    /// </summary>
    /// <remarks>
    /// Serve dove la banda è scarsa: sedi collegate con linee lente, o trunk verso
    /// operatori che lo impongono. Costa qualità: la voce risulta più metallica e
    /// i toni DTMF inband non sopravvivono alla compressione, quindi va usato solo
    /// con DTMF RFC 4733. I brevetti sono scaduti, non ci sono più vincoli di
    /// licenza.
    /// </remarks>
    G729,

    /// <summary>
    /// G.711 μ-law, banda stretta a 8 kHz.
    /// </summary>
    /// <remarks>
    /// È la variante nordamericana e giapponese di G.711. In Europa non serve, e
    /// offrirla prima di A-law porta alcuni apparati a negoziarla producendo una
    /// resa peggiore senza alcun vantaggio. Resta selezionabile per gli scenari di
    /// interconnessione internazionale, ma è esclusa dalla configurazione
    /// predefinita.
    /// </remarks>
    PCMU,
}

/// <summary>Descrizione di un codec, per l'interfaccia e per gli annunci.</summary>
/// <param name="Codec">Codec descritto.</param>
/// <param name="ShortName">Nome tecnico, come compare in SDP.</param>
/// <param name="Label">Etichetta leggibile e pronunciabile.</param>
/// <param name="Description">Spiegazione di quando conviene usarlo.</param>
/// <param name="SampleRateHz">Frequenza di campionamento.</param>
/// <param name="BitrateKbps">Banda occupata, in kilobit al secondo.</param>
public sealed record AudioCodecInfo(
    AudioCodec Codec,
    string ShortName,
    string Label,
    string Description,
    int SampleRateHz,
    int BitrateKbps)
{
    /// <summary>
    /// Indica se il codec è a banda larga secondo il criterio di EN 301 549 §6.1.
    /// </summary>
    /// <remarks>
    /// Il criterio della norma è il limite superiore della banda utile, almeno
    /// 7000 Hz. Si usa la frequenza di campionamento come discriminante: da 16 kHz
    /// in su la banda utile supera i 7 kHz.
    /// </remarks>
    public bool IsWideband => SampleRateHz >= 16000;
}

/// <summary>
/// Catalogo dei codec disponibili.
/// </summary>
/// <remarks>
/// <para>
/// Sorgente di verità unica per l'interfaccia, per il motore telefonico e per gli
/// annunci. Aggiungere un codec qui lo rende automaticamente configurabile, senza
/// altre modifiche.
/// </para>
/// </remarks>
public static class AudioCodecCatalog
{
    private static readonly AudioCodecInfo[] Catalog =
    [
        new(AudioCodec.G722, "G722", "G.722 banda larga",
            "Qualita' migliore, voce piu' naturale e faticosa da confondere. Richiede circa la stessa banda del G.711. "
            + "E' l'unico che soddisfa il requisito di banda larga della norma europea sull'accessibilita'.",
            SampleRateHz: 16000, BitrateKbps: 64),

        new(AudioCodec.PCMA, "PCMA", "G.711 A-law",
            "Lo standard europeo, compatibile con qualunque apparato. Da tenere sempre attivo come riserva: "
            + "se la negoziazione fallisce con gli altri, questo funziona.",
            SampleRateHz: 8000, BitrateKbps: 64),

        new(AudioCodec.G729, "G729", "G.729 compresso",
            "Occupa circa un ottavo della banda degli altri. Utile su collegamenti lenti o quando lo impone "
            + "l'operatore telefonico. La voce risulta piu' metallica.",
            SampleRateHz: 8000, BitrateKbps: 8),

        new(AudioCodec.PCMU, "PCMU", "G.711 mu-law",
            "Variante nordamericana del G.711. In Europa non serve: da attivare solo per interconnessioni "
            + "internazionali che lo richiedano espressamente.",
            SampleRateHz: 8000, BitrateKbps: 64),
    ];

    /// <summary>Tutti i codec supportati dallo stack.</summary>
    public static IReadOnlyList<AudioCodecInfo> All => Catalog;

    /// <summary>
    /// I tre codec di uso corrente in Europa.
    /// </summary>
    public static IReadOnlyList<AudioCodecInfo> European =>
        [Catalog[0], Catalog[1], Catalog[2]];

    /// <summary>
    /// Ordine di preferenza predefinito: G.729, poi G.722, poi G.711 A-law.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>G.729 in testa</b> perché è quello che i centralini di riferimento
    /// negoziano nella pratica: diversi PBX, e i trunk di parecchi operatori
    /// italiani, offrono solo quello. Metterlo primo evita una negoziazione che
    /// ripiega comunque su di lui dopo aver proposto alternative non supportate.
    /// </para>
    /// <para>
    /// <b>G.722 resta nell'elenco</b>, e questo è il punto che va compreso per non
    /// prendere una decisione sbagliata in futuro. EN 301 549 §6.1 richiede che
    /// l'apparato <i>sia in grado</i> di codificare e decodificare la banda larga,
    /// non che la negozi per prima. Finché G.722 è offerto la conformità è
    /// soddisfatta e dimostrabile; <b>toglierlo dall'elenco la farebbe perdere</b>.
    /// La conseguenza pratica dell'ordine attuale è solo che la conversazione
    /// avverrà in banda stretta quando il centralino non offre di meglio.
    /// </para>
    /// <para>
    /// <b>G.711 A-law in coda</b> come riserva universale: se la negoziazione
    /// fallisce con gli altri due, questo funziona con qualunque apparato.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AudioCodec> DefaultPreference =>
        [AudioCodec.G729, AudioCodec.G722, AudioCodec.PCMA];

    /// <summary>Cerca la descrizione di un codec.</summary>
    /// <param name="codec">Codec cercato.</param>
    public static AudioCodecInfo Describe(AudioCodec codec)
        => Catalog.First(c => c.Codec == codec);

    /// <summary>
    /// Interpreta un nome tecnico, tollerando le forme in cui viene scritto.
    /// </summary>
    /// <param name="name">Nome del codec, per esempio "G722", "g.722", "PCMA".</param>
    /// <returns>Il codec corrispondente, oppure <c>null</c> se non riconosciuto.</returns>
    /// <remarks>
    /// La tolleranza serve perché questi nomi vengono anche scritti a mano nel file
    /// di configurazione da un amministratore, che può ragionevolmente digitare
    /// "G.711A" o "alaw" invece di "PCMA".
    /// </remarks>
    public static AudioCodec? Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var normalized = name.Trim().ToUpperInvariant().Replace(".", string.Empty).Replace("-", string.Empty);

        return normalized switch
        {
            "G722" or "G722WB" => AudioCodec.G722,
            "PCMA" or "G711A" or "G711ALAW" or "ALAW" => AudioCodec.PCMA,
            "G729" or "G729A" or "G729AB" => AudioCodec.G729,
            "PCMU" or "G711U" or "G711ULAW" or "ULAW" or "MULAW" => AudioCodec.PCMU,
            _ => null,
        };
    }

    /// <summary>
    /// Converte un elenco di nomi in codec, scartando quelli non riconosciuti.
    /// </summary>
    /// <param name="names">Nomi dei codec, nell'ordine di preferenza.</param>
    /// <returns>
    /// I codec riconosciuti. Se nessun nome è valido restituisce
    /// <see cref="DefaultPreference"/>, perché un elenco vuoto renderebbe
    /// impossibile qualunque chiamata.
    /// </returns>
    public static IReadOnlyList<AudioCodec> ParseList(IEnumerable<string>? names)
    {
        var parsed = (names ?? [])
            .Select(Parse)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .Distinct()
            .ToList();

        return parsed.Count > 0 ? parsed : DefaultPreference;
    }

    /// <summary>
    /// Indica se un elenco di codec include almeno un codec a banda larga.
    /// </summary>
    /// <param name="codecs">Codec selezionati.</param>
    /// <remarks>
    /// Serve all'avviso di conformità: disattivando ogni codec a banda larga il
    /// prodotto smette di soddisfare EN 301 549 §6.1, che nel mercato della PA è un
    /// requisito contrattuale e non una raccomandazione. L'operatore può comunque
    /// farlo — ci sono centralini che non offrono G.722 — ma deve saperlo.
    /// </remarks>
    public static bool IncludesWideband(IEnumerable<AudioCodec> codecs)
        => codecs.Any(c => Describe(c).IsWideband);
}
