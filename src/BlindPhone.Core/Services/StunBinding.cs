using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BlindPhone.Core.Services;

/// <summary>Come è andata un'interrogazione STUN.</summary>
public enum StunOutcome
{
    /// <summary>Il server ha detto da quale indirizzo e porta vede arrivare la richiesta.</summary>
    Mapped,

    /// <summary>Il server ha risposto con un errore.</summary>
    ServerError,

    /// <summary>Risposta di successo che non contiene nessun indirizzo.</summary>
    NoAddress,

    /// <summary>Il pacchetto appartiene a un'altra transazione: non è la risposta attesa.</summary>
    OtherTransaction,

    /// <summary>Il pacchetto non è un messaggio STUN leggibile.</summary>
    Malformed,

    /// <summary>Nessuna risposta nel tempo concesso, oppure server non risolto.</summary>
    NoResponse,
}

/// <summary>
/// Esito di una richiesta STUN Binding.
/// </summary>
/// <param name="Outcome">Come è andata.</param>
/// <param name="Mapped">
/// Indirizzo e porta da cui il server ha visto arrivare la richiesta, quando c'è.
/// </param>
/// <param name="Detail">Precisazione per il registro e la diagnostica, già in italiano.</param>
public sealed record StunBindingResult(StunOutcome Outcome, IPEndPoint? Mapped = null, string Detail = "")
{
    /// <summary>Nessuna risposta dal server.</summary>
    /// <param name="detail">Che cosa è mancato: la risposta, la risoluzione del nome.</param>
    public static StunBindingResult NoResponse(string detail) => new(StunOutcome.NoResponse, null, detail);

    /// <summary>
    /// L'esito detto per esteso, così come si legge nella scheda Diagnostica.
    /// </summary>
    public string Describe() => Outcome switch
    {
        StunOutcome.Mapped when Mapped is not null
            => $"il server STUN vede la postazione come {MediaAddressPolicy.Describe(Mapped)}",
        StunOutcome.ServerError => $"il server STUN ha risposto con un errore{Precisa()}",
        StunOutcome.NoAddress => "il server STUN ha risposto senza indicare nessun indirizzo",
        StunOutcome.Malformed => $"la risposta del server STUN non si legge{Precisa()}",
        StunOutcome.OtherTransaction => "è arrivata una risposta STUN che non era per questa richiesta",
        _ => $"il server STUN non ha risposto{Precisa()}",
    };

    private string Precisa() => string.IsNullOrWhiteSpace(Detail) ? string.Empty : $", {Detail}";
}

/// <summary>
/// Richiesta e risposta STUN Binding, RFC 5389.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché un'implementazione propria quando la libreria SIP ne ha una.</b> Quella di
/// SIPSorcery manda una richiesta sola e prende per buona la prima risposta STUN che
/// arriva sul socket, di chiunque sia. Su una rete domestica il primo problema vale
/// una chiamata ogni tanto senza indirizzo pubblico, perché basta un pacchetto perso
/// sul Wi-Fi; il secondo è peggio, perché sullo stesso socket arrivano anche i
/// pacchetti di chi è all'altro capo della chiamata. Qui la risposta si riconosce
/// dall'identificativo di transazione, come vuole la §7.3, e i tentativi li decide chi
/// interroga.
/// </para>
/// <para>
/// <b>Niente credenziali, niente integrità.</b> Una Binding senza credenziali è
/// l'unica che serve per sapere l'indirizzo pubblico, ed è quella che ogni server STUN
/// accetta. Gli attributi <c>MESSAGE-INTEGRITY</c> e <c>FINGERPRINT</c> di una
/// risposta si saltano come tutti gli altri attributi non necessari: senza una
/// password condivisa non c'è niente con cui verificarli.
/// </para>
/// <para>
/// Sta nel core perché è un'analisi di byte senza rete: si collauda con i messaggi di
/// esempio della RFC 5769, che è il modo più sicuro di sapere che un indirizzo cifrato
/// con lo XOR torna quello giusto.
/// </para>
/// </remarks>
public static class StunBinding
{
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const ushort BindingError = 0x0111;

    private const uint MagicCookie = 0x2112A442;

    private const ushort MappedAddress = 0x0001;
    private const ushort ErrorCode = 0x0009;
    private const ushort XorMappedAddress = 0x0020;

    /// <summary>
    /// Il numero con cui le bozze precedenti alla RFC 5389 indicavano
    /// <c>XOR-MAPPED-ADDRESS</c>.
    /// </summary>
    /// <remarks>
    /// Qualche server ancora in servizio lo usa. Ha lo stesso contenuto e si legge allo
    /// stesso modo: rifiutarlo significherebbe dire «nessun indirizzo» quando
    /// l'indirizzo c'è.
    /// </remarks>
    private const ushort XorMappedAddressDraft = 0x8020;

    private const int HeaderLength = 20;
    private const int TransactionIdLength = 12;

    /// <summary>Un identificativo di transazione nuovo, casuale come chiede la §6.</summary>
    public static byte[] NewTransactionId() => RandomNumberGenerator.GetBytes(TransactionIdLength);

    /// <summary>
    /// Costruisce una richiesta Binding senza attributi.
    /// </summary>
    /// <param name="transactionId">Dodici byte, da <see cref="NewTransactionId"/>.</param>
    /// <returns>I venti byte da spedire.</returns>
    public static byte[] CreateRequest(ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != TransactionIdLength)
            throw new ArgumentException("L'identificativo di transazione STUN e' di dodici byte", nameof(transactionId));

        var request = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(request, BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);
        transactionId.CopyTo(request.AsSpan(8));

        return request;
    }

    /// <summary>
    /// Legge un pacchetto arrivato in risposta a una richiesta.
    /// </summary>
    /// <param name="datagram">Il pacchetto così come è arrivato.</param>
    /// <param name="expectedTransactionId">Identificativo della richiesta mandata.</param>
    public static StunBindingResult Parse(ReadOnlySpan<byte> datagram, ReadOnlySpan<byte> expectedTransactionId)
    {
        if (datagram.Length < HeaderLength)
            return new StunBindingResult(StunOutcome.Malformed, Detail: "pacchetto troppo corto");

        // I due bit alti a zero sono il segno che distingue STUN da RTP sullo stesso
        // socket (§6): un pacchetto audio comincia con la versione 2, cioe' 10 in binario.
        if ((datagram[0] & 0xC0) != 0)
            return new StunBindingResult(StunOutcome.Malformed, Detail: "non è un messaggio STUN");

        var tipo = BinaryPrimitives.ReadUInt16BigEndian(datagram);
        var lunghezza = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);

        if (BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]) != MagicCookie)
            return new StunBindingResult(StunOutcome.Malformed, Detail: "manca il valore fisso della RFC 5389");

        if (lunghezza % 4 != 0 || HeaderLength + lunghezza > datagram.Length)
            return new StunBindingResult(StunOutcome.Malformed, Detail: "lunghezza dichiarata incoerente");

        var attributi = new List<(ushort Type, byte[] Value)>();
        var corpo = datagram.Slice(HeaderLength, lunghezza);

        while (corpo.Length >= 4)
        {
            var tipoAttributo = BinaryPrimitives.ReadUInt16BigEndian(corpo);
            var lunghezzaAttributo = BinaryPrimitives.ReadUInt16BigEndian(corpo[2..]);

            if (4 + lunghezzaAttributo > corpo.Length)
                return new StunBindingResult(StunOutcome.Malformed, Detail: "attributo tronco");

            attributi.Add((tipoAttributo, corpo.Slice(4, lunghezzaAttributo).ToArray()));

            // Ogni attributo e' allineato a quattro byte: il riempimento non e' contato
            // nella lunghezza dell'attributo ma lo e' in quella del messaggio.
            var passo = 4 + ((lunghezzaAttributo + 3) & ~3);
            corpo = corpo[Math.Min(passo, corpo.Length)..];
        }

        return Interpret(tipo, datagram.Slice(8, TransactionIdLength), attributi, expectedTransactionId);
    }

    /// <summary>
    /// Interpreta una risposta già divisa in intestazione e attributi.
    /// </summary>
    /// <param name="messageType">Tipo del messaggio.</param>
    /// <param name="transactionId">Identificativo di transazione della risposta.</param>
    /// <param name="attributes">Attributi con il loro valore grezzo, nell'ordine di arrivo.</param>
    /// <param name="expectedTransactionId">Identificativo della richiesta mandata.</param>
    /// <remarks>
    /// Esiste a parte da <see cref="Parse"/> perché la libreria SIP i pacchetti STUN che
    /// arrivano sul socket audio li ha già divisi in attributi prima di consegnarli: si
    /// riparte da lì invece di ricomporre i byte, con la stessa regola per tutti e due i
    /// casi.
    /// </remarks>
    public static StunBindingResult Interpret(
        ushort messageType,
        ReadOnlySpan<byte> transactionId,
        IEnumerable<(ushort Type, byte[] Value)> attributes,
        ReadOnlySpan<byte> expectedTransactionId)
    {
        if (!transactionId.SequenceEqual(expectedTransactionId))
            return new StunBindingResult(StunOutcome.OtherTransaction);

        var tid = transactionId.ToArray();
        var elenco = attributes.ToList();

        if (messageType == BindingError)
        {
            var errore = elenco.FirstOrDefault(a => a.Type == ErrorCode).Value;
            return new StunBindingResult(StunOutcome.ServerError, Detail: DescribeError(errore));
        }

        if (messageType != BindingSuccess)
            return new StunBindingResult(StunOutcome.Malformed, Detail: "non è una risposta di Binding");

        // L'indirizzo cifrato con lo XOR ha la precedenza su quello in chiaro, ed e' la
        // ragione per cui esiste: i router con un ALG che riconoscono un indirizzo
        // privato nei pacchetti lo riscrivono, e l'indirizzo in chiaro arriva sbagliato.
        foreach (var (tipo, valore) in elenco)
        {
            if ((tipo is XorMappedAddress or XorMappedAddressDraft) && TryReadAddress(valore, tid, out var xor))
                return new StunBindingResult(StunOutcome.Mapped, xor);
        }

        foreach (var (tipo, valore) in elenco)
        {
            if (tipo == MappedAddress && TryReadAddress(valore, null, out var chiaro))
                return new StunBindingResult(StunOutcome.Mapped, chiaro);
        }

        return new StunBindingResult(StunOutcome.NoAddress);
    }

    /// <summary>
    /// Legge un attributo di indirizzo, in chiaro o cifrato con lo XOR.
    /// </summary>
    /// <param name="value">Valore dell'attributo.</param>
    /// <param name="transactionId">
    /// Identificativo di transazione per gli indirizzi cifrati, <c>null</c> per quelli
    /// in chiaro.
    /// </param>
    /// <param name="endPoint">Indirizzo letto.</param>
    private static bool TryReadAddress(byte[] value, byte[]? transactionId, out IPEndPoint endPoint)
    {
        endPoint = null!;
        if (value.Length < 8) return false;

        var famiglia = value[1];
        var lunghezza = famiglia switch { 0x01 => 4, 0x02 => 16, _ => 0 };
        if (lunghezza == 0 || value.Length < 4 + lunghezza) return false;

        var porta = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(2));
        var indirizzo = value.AsSpan(4, lunghezza).ToArray();

        if (transactionId is not null)
        {
            // §15.2: la porta si combina con i sedici bit alti del valore fisso,
            // l'indirizzo con il valore fisso e, per IPv6, con l'identificativo di
            // transazione che lo segue.
            porta ^= (ushort)(MagicCookie >> 16);

            Span<byte> chiave = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(chiave, MagicCookie);
            transactionId.CopyTo(chiave[4..]);

            for (var i = 0; i < lunghezza; i++) indirizzo[i] ^= chiave[i];
        }

        endPoint = new IPEndPoint(new IPAddress(indirizzo), porta);
        return true;
    }

    /// <summary>Il codice di errore di una risposta negativa, detto per intero.</summary>
    private static string DescribeError(byte[]? value)
    {
        if (value is null || value.Length < 4) return "senza codice";

        var codice = (value[2] & 0x07) * 100 + value[3];
        var motivo = value.Length > 4 ? Encoding.UTF8.GetString(value, 4, value.Length - 4).Trim('\0', ' ') : string.Empty;

        return motivo.Length == 0
            ? $"codice {codice.ToString(CultureInfo.InvariantCulture)}"
            : $"codice {codice.ToString(CultureInfo.InvariantCulture)}, {motivo}";
    }
}
