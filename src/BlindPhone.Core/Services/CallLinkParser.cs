using System.Text;

namespace BlindPhone.Core.Services;

/// <summary>Schema di un link di chiamata.</summary>
public enum CallLinkScheme
{
    /// <summary><c>tel:</c>, RFC 3966: un numero di telefono.</summary>
    Tel,

    /// <summary><c>sip:</c>, RFC 3261: un indirizzo SIP.</summary>
    Sip,

    /// <summary><c>sips:</c>, RFC 3261: un indirizzo SIP da raggiungere cifrato.</summary>
    Sips,

    /// <summary><c>callto:</c>, lo schema di NetMeeting e Skype che i gestionali usano ancora.</summary>
    Callto,
}

/// <summary>
/// Destinazione ricavata da un link di chiamata, già ripulita e verificata.
/// </summary>
/// <param name="Scheme">Schema del link da cui viene.</param>
/// <param name="User">
/// Numero di telefono — solo cifre, con il più iniziale se c'era — oppure nome
/// utente SIP.
/// </param>
/// <param name="Host">
/// Dominio dell'indirizzo SIP, in minuscolo e con la porta se il link la indicava;
/// <c>null</c> per un numero di telefono.
/// </param>
/// <param name="IsPhoneNumber"><see cref="User"/> è un numero di telefono.</param>
/// <param name="Extension">
/// Interno indicato con <c>;ext=</c>. Non si compone: va chiesto al centralino che
/// risponde, quindi si dice all'operatore e basta.
/// </param>
public sealed record CallLink(
    CallLinkScheme Scheme,
    string User,
    string? Host,
    bool IsPhoneNumber,
    string? Extension)
{
    /// <summary>
    /// Che cosa scrivere nel campo di composizione.
    /// </summary>
    /// <param name="ownDomain">
    /// Dominio dell'account SIP in uso: un indirizzo verso il proprio centralino si
    /// riduce al solo numero o nome.
    /// </param>
    /// <param name="homeCountryCode">Prefisso internazionale del paese della postazione, senza il più.</param>
    /// <returns>Il numero, oppure <c>utente@dominio</c> se l'indirizzo porta altrove.</returns>
    /// <remarks>
    /// <para>
    /// <b>Verso il proprio centralino resta il solo numero</b>, ed è quello che
    /// l'operatore avrebbe scritto a mano: «sip:204@centralino.ente.local» diventa 204,
    /// la rubrica lo riconosce, la voce lo legge in quattro sillabe invece di
    /// pronunciare un dominio intero. La composizione aggiunge il dominio da sé.
    /// </para>
    /// <para>
    /// <b>Un numero italiano in forma internazionale torna nazionale</b>: +39 06 1234
    /// diventa 06 1234. È il numero che chiunque in Italia compone, e passa da
    /// qualunque centralino; il più, invece, lo accettano solo i piani di numerazione
    /// che qualcuno ha configurato per riceverlo. In Italia la conversione è una
    /// semplice cancellazione del prefisso, perché lo zero delle numerazioni fisse fa
    /// parte del numero e resta anche nella forma internazionale.
    /// </para>
    /// <para>
    /// Verso un dominio diverso l'indirizzo resta intero, numero compreso così come
    /// il link lo scriveva: un fornitore esterno può volere la forma internazionale, e
    /// riscriverla sarebbe decidere al posto suo.
    /// </para>
    /// </remarks>
    public string Dial(string? ownDomain, string homeCountryCode = CallLinkParser.DefaultCountryCode)
        => Dial([ownDomain], homeCountryCode);

    /// <summary>
    /// Che cosa scrivere nel campo di composizione, quando il proprio centralino ha
    /// più di un nome.
    /// </summary>
    /// <param name="ownHosts">
    /// Nomi con cui il link può indicare il proprio centralino: il dominio
    /// dell'account e il server di registrazione, che spesso non coincidono.
    /// </param>
    /// <param name="homeCountryCode">Prefisso internazionale del paese della postazione, senza il più.</param>
    /// <returns>Il numero, oppure <c>utente@dominio</c> se l'indirizzo porta altrove.</returns>
    public string Dial(IEnumerable<string?> ownHosts, string homeCountryCode = CallLinkParser.DefaultCountryCode)
    {
        if (Host is null || ownHosts.Any(h => CallLinkParser.IsSameHost(Host, h)))
        {
            return IsPhoneNumber ? CallLinkParser.ToNational(User, homeCountryCode) : User;
        }

        return $"{User}@{Host}";
    }
}

/// <summary>
/// Interpreta i link <c>tel:</c>, <c>sip:</c>, <c>sips:</c> e <c>callto:</c> con cui
/// un gestionale, una pagina web o un messaggio di posta propongono una chiamata.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il link arriva da fuori, e va trattato come tale.</b> Chiunque può scrivere una
/// pagina con un link di chiamata, e il link finisce dentro un'intestazione SIP. Per
/// questo il parser non si limita a estrarre un numero: rifiuta tutto quello che non
/// sa spiegare. Caratteri di controllo — un a capo dentro un'intestazione SIP ne
/// inventa una nuova —, codifiche malformate, codici di servizio del centralino,
/// intestazioni e parametri SIP che deciderebbero al posto dell'operatore dove e come
/// far passare la chiamata.
/// </para>
/// <para>
/// <b>Il link non compone mai.</b> Il parser produce una destinazione da mettere nel
/// campo di composizione, e il resto lo decide l'operatore con Invio. La ragione per
/// cui il resto della classe può permettersi di essere tollerante con le abitudini
/// dei gestionali è questa: la tolleranza produce al massimo un numero letto ad alta
/// voce e non chiamato.
/// </para>
/// <para>
/// <b>Rigore sulla sicurezza, tolleranza sulla forma.</b> I gestionali scrivono link
/// che le RFC non ammettono — «tel:06/1234567», «callto://0612345678/», un numero
/// locale senza contesto — e rifiutarli significherebbe rifiutare quasi tutti i link
/// che un centralino della PA incontra davvero. La tolleranza riguarda solo la forma
/// del numero, mai quello che il numero fa.
/// </para>
/// <para>
/// I rifiuti sono frasi in italiano corrente che dicono <b>perché</b>: vengono lette
/// all'operatore dopo «Link di chiamata non usato», e gli servono a capire se il
/// problema è nel link o nel programma.
/// </para>
/// </remarks>
public static class CallLinkParser
{
    /// <summary>Prefisso internazionale dell'Italia, senza il più.</summary>
    public const string DefaultCountryCode = "39";

    /// <summary>
    /// Lunghezza massima del link intero.
    /// </summary>
    /// <remarks>
    /// Un link di chiamata reale sta in poche decine di caratteri; anche un indirizzo
    /// SIP con parametri e intestazioni resta sotto le duecento. Il limite è largo di
    /// proposito, e serve a una cosa sola: che una riga di comando costruita apposta
    /// non diventi lavoro da fare, né testo da tenere in memoria o da scrivere nel
    /// registro.
    /// </remarks>
    public const int MaxLength = 1024;

    /// <summary>
    /// Cifre al massimo in un numero locale: il più lungo che serve è un prefisso
    /// internazionale con lo 00 davanti, e non arriva a venti.
    /// </summary>
    private const int MaxLocalDigits = 20;

    /// <summary>Cifre al massimo in un numero internazionale, prefisso compreso (E.164).</summary>
    private const int MaxGlobalDigits = 15;

    /// <summary>
    /// Cifre al minimo in un numero internazionale: sotto non esiste un numero E.164
    /// vero, e un «+39» da solo è un prefisso, non un numero.
    /// </summary>
    private const int MinGlobalDigits = 5;

    /// <summary>Cifre al massimo nell'interno indicato con <c>;ext=</c>.</summary>
    private const int MaxExtensionDigits = 10;

    /// <summary>Lunghezza massima del nome utente in un indirizzo SIP.</summary>
    private const int MaxUserLength = 64;

    /// <summary>Lunghezza massima di un nome di dominio (RFC 1035).</summary>
    private const int MaxHostLength = 253;

    private static readonly (string Prefix, CallLinkScheme Scheme)[] Schemes =
    [
        ("tel:", CallLinkScheme.Tel),
        ("sips:", CallLinkScheme.Sips),
        ("sip:", CallLinkScheme.Sip),
        ("callto:", CallLinkScheme.Callto),
    ];

    /// <summary>
    /// Dice se un testo comincia con uno degli schemi gestiti.
    /// </summary>
    /// <param name="text">Argomento della riga di comando o testo da esaminare.</param>
    /// <remarks>
    /// Serve a riconoscere il link sulla riga di comando senza ancora fidarsene: la
    /// verifica vera la fa <see cref="TryParse(string?, out CallLink?, out string?)"/>,
    /// e un argomento che comincia bene ma è malformato deve arrivare fino a lì,
    /// perché è lì che si spiega all'operatore che cosa non va.
    /// </remarks>
    public static bool LooksLikeCallLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.TrimStart();
        return Schemes.Any(s => trimmed.StartsWith(s.Prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Interpreta un link di chiamata, con l'Italia come paese della postazione.
    /// </summary>
    /// <param name="text">Link come arriva dalla riga di comando.</param>
    /// <param name="link">Destinazione ricavata, se il link è valido.</param>
    /// <param name="error">Motivo del rifiuto, già pronunciabile.</param>
    /// <returns><c>true</c> se il link è stato accettato.</returns>
    public static bool TryParse(string? text, out CallLink? link, out string? error)
        => TryParse(text, DefaultCountryCode, out link, out error);

    /// <summary>
    /// Interpreta un link di chiamata.
    /// </summary>
    /// <param name="text">Link come arriva dalla riga di comando.</param>
    /// <param name="homeCountryCode">Prefisso internazionale del paese della postazione, senza il più.</param>
    /// <param name="link">Destinazione ricavata, se il link è valido.</param>
    /// <param name="error">Motivo del rifiuto, già pronunciabile.</param>
    /// <returns><c>true</c> se il link è stato accettato.</returns>
    public static bool TryParse(string? text, string homeCountryCode, out CallLink? link, out string? error)
    {
        link = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "il link e' vuoto";
            return false;
        }

        // PRIMA LA LUNGHEZZA, POI TUTTO IL RESTO: ogni controllo successivo costa in
        // proporzione al testo, e un testo enorme non ha alcuna possibilita' di essere
        // un numero di telefono.
        if (text.Length > MaxLength)
        {
            error = "il link e' troppo lungo per essere un numero di telefono";
            return false;
        }

        // Il controllo sui caratteri di controllo si ripete dopo la decodifica, ma va
        // fatto anche qui: un a capo letterale e' il modo piu' diretto di inventare
        // un'intestazione SIP, e non deve arrivare nemmeno al punto in cui si spezza
        // il link nei suoi pezzi.
        if (text.Any(char.IsControl))
        {
            error = "il link contiene caratteri non ammessi";
            return false;
        }

        var trimmed = text.Trim();
        var schema = Schemes.FirstOrDefault(s => trimmed.StartsWith(s.Prefix, StringComparison.OrdinalIgnoreCase));

        if (schema.Prefix is null)
        {
            error = "non e' un link di chiamata";
            return false;
        }

        var body = StripAuthoritySlashes(trimmed[schema.Prefix.Length..]);

        return schema.Scheme switch
        {
            CallLinkScheme.Tel => TryParseTel(body, homeCountryCode, out link, out error),
            CallLinkScheme.Callto => TryParseCallto(body, homeCountryCode, out link, out error),
            _ => TryParseSip(body, schema.Scheme, homeCountryCode, out link, out error),
        };
    }

    /// <summary>
    /// Riporta un numero italiano in forma internazionale alla forma nazionale.
    /// </summary>
    /// <param name="number">Numero già ripulito.</param>
    /// <param name="homeCountryCode">Prefisso del paese della postazione, senza il più.</param>
    /// <returns>Il numero senza prefisso del paese, oppure il numero com'era.</returns>
    /// <remarks>
    /// Il confronto sul solo inizio è sicuro perché i prefissi internazionali non sono
    /// mai l'uno l'inizio dell'altro: dopo +39 non comincia nessun altro paese. San
    /// Marino è +378, e infatti non comincia con +39.
    /// </remarks>
    public static string ToNational(string number, string homeCountryCode = DefaultCountryCode)
    {
        var prefisso = "+" + homeCountryCode;

        return number.StartsWith(prefisso, StringComparison.Ordinal) && number.Length > prefisso.Length
            ? number[prefisso.Length..]
            : number;
    }

    /// <summary>
    /// Dice se il dominio di un indirizzo è quello del proprio account.
    /// </summary>
    /// <param name="host">Dominio dell'indirizzo, eventualmente con la porta.</param>
    /// <param name="ownDomain">Dominio dell'account, eventualmente con la porta.</param>
    /// <remarks>
    /// La porta non conta: chi scrive il link nel gestionale indica il centralino, e
    /// la porta su cui il centralino ascolta la sa già la configurazione dell'account.
    /// </remarks>
    public static bool IsSameHost(string host, string? ownDomain)
    {
        if (string.IsNullOrWhiteSpace(ownDomain)) return false;

        return string.Equals(HostWithoutPort(host), HostWithoutPort(ownDomain.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // tel: (RFC 3966)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Interpreta il corpo di un link <c>tel:</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Un <c>tel:</c> non ha intestazioni, ma qualche gestionale ci appende comunque
    /// un <c>?</c> con dei parametri suoi: si ignorano, come si ignorano tutti i
    /// parametri che la RFC prevede e che a un posto operatore non dicono niente
    /// (<c>isub</c>, <c>tgrp</c>, <c>npdi</c> e simili).
    /// </para>
    /// <para>
    /// Due parametri invece contano. <c>ext</c> è l'interno da chiedere dopo la
    /// risposta. <c>phone-context</c> dice dentro quale paese o quale organizzazione
    /// un numero locale ha senso, e serve a rifiutare il caso in cui comporlo da qui
    /// chiamerebbe qualcun altro.
    /// </para>
    /// </remarks>
    private static bool TryParseTel(string body, string homeCountryCode, out CallLink? link, out string? error)
    {
        link = null;

        var pezzi = CutAt(body, '?').Split(';');

        if (!TryDecode(pezzi[0], out var numero))
        {
            error = InvalidEncoding;
            return false;
        }

        if (!TryReadNumberParameters(pezzi.AsSpan(1), out var parametri, out error)) return false;

        if (!TryBuildPhoneNumber(numero, parametri, homeCountryCode, out var cifre, out error)) return false;

        link = new CallLink(CallLinkScheme.Tel, cifre, null, IsPhoneNumber: true, parametri.Extension);
        return true;
    }

    /// <summary>
    /// Interpreta il corpo di un link <c>callto:</c>.
    /// </summary>
    /// <remarks>
    /// Lo schema non ha una specifica: era quello di NetMeeting, poi di Skype, e i
    /// gestionali lo usano ancora per dire «chiama questo numero». Dentro ci si trova
    /// un numero, e allora vale come <c>tel:</c>, oppure un indirizzo con la
    /// chiocciola, e allora vale come <c>sip:</c>. Un nome Skype senza chiocciola non
    /// è niente che un centralino sappia chiamare.
    /// </remarks>
    private static bool TryParseCallto(string body, string homeCountryCode, out CallLink? link, out string? error)
    {
        link = null;

        var senzaDomanda = CutAt(body, '?');

        if (senzaDomanda.Contains('@'))
        {
            if (!TryParseSip(senzaDomanda, CallLinkScheme.Callto, homeCountryCode, out link, out error)) return false;
            return true;
        }

        if (!TryParseTel(senzaDomanda, homeCountryCode, out var tel, out error))
        {
            return false;
        }

        link = tel! with { Scheme = CallLinkScheme.Callto };
        return true;
    }

    // -----------------------------------------------------------------------
    // sip: e sips: (RFC 3261, paragrafo 19.1)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Interpreta il corpo di un link <c>sip:</c> o <c>sips:</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Del link si tengono utente e dominio, e nient'altro.</b> Le intestazioni
    /// dopo il punto interrogativo sono la parte pericolosa: un <c>Replaces</c> si
    /// inserisce in una conversazione altrui, un <c>body</c> mette contenuto nella
    /// richiesta. I parametri dopo il dominio non sono innocui per un'altra ragione:
    /// <c>maddr</c> sceglie a quale server mandare la chiamata, <c>transport</c> e
    /// <c>method</c> come e con che richiesta. Sono tutte decisioni che spettano alla
    /// configurazione dell'account, non a chi ha scritto la pagina.
    /// </para>
    /// <para>
    /// La password che la sintassi ammette prima della chiocciola si scarta senza
    /// commenti: la RFC la sconsiglia, e una password scritta in una pagina web non è
    /// una password.
    /// </para>
    /// <para>
    /// <b>Lo schema <c>sips:</c> non cambia la composizione.</b> La cifratura della
    /// tratta fra postazione e centralino la decide il trasporto configurato
    /// sull'account, e lo stack non usa <c>sips:</c> nemmeno per le chiamate digitate:
    /// la ragione è nel motore telefonico, alla voce del parametro di trasporto.
    /// </para>
    /// </remarks>
    private static bool TryParseSip(
        string body,
        CallLinkScheme scheme,
        string homeCountryCode,
        out CallLink? link,
        out string? error)
    {
        link = null;

        var indirizzo = CutAt(body, '?');
        var chiocciola = indirizzo.IndexOf('@');

        if (chiocciola >= 0 && indirizzo.IndexOf('@', chiocciola + 1) >= 0)
        {
            error = "l'indirizzo contiene due chiocciole";
            return false;
        }

        // SENZA CHIOCCIOLA C'E' SOLO UN SERVER, OPPURE UN GESTIONALE SBRIGATIVO.
        //
        // Per la RFC «sip:pbx.ente.it» e' un indirizzo valido, ma non dice chi
        // chiamare. «sip:204», invece, e' quello che scrivono i gestionali quando
        // intendono l'interno 204 del proprio centralino: formalmente 204 sarebbe il
        // nome di un server, ma nessun server si chiama cosi'.
        if (chiocciola < 0)
        {
            var soloUtente = indirizzo.Split(';');

            if (!TryDecode(soloUtente[0], out var nudo))
            {
                error = InvalidEncoding;
                return false;
            }

            if (!LooksLikePhoneNumber(nudo))
            {
                error = "manca il numero o il nome da chiamare: c'e' solo l'indirizzo di un server";
                return false;
            }

            if (!TryBuildPhoneNumber(nudo, NumberParameters.None, homeCountryCode, out var cifre, out error)) return false;

            link = new CallLink(scheme, cifre, null, IsPhoneNumber: true, Extension: null);
            return true;
        }

        // La password, se c'e', sta dopo i due punti della parte utente.
        var utenteGrezzo = CutAt(indirizzo[..chiocciola], ':');
        var pezziDominio = indirizzo[(chiocciola + 1)..].Split(';');

        if (!TryParseHost(pezziDominio[0], out var dominio, out error)) return false;

        // Dei parametri dopo il dominio se ne legge uno solo, e non per obbedirgli:
        // «user=phone» dice che la parte utente e' un numero di telefono, e quindi
        // che i suoi trattini e i suoi punti sono separatori da togliere.
        var utenteTelefono = pezziDominio.Skip(1).Any(p =>
            TryDecode(p, out var parametro)
            && parametro.Trim().Equals("user=phone", StringComparison.OrdinalIgnoreCase));

        // La parte utente puo' essere un numero con i suoi parametri — «+39 06
        // 1234;ext=12», il «telephone-subscriber» della RFC — oppure un nome.
        var pezziUtente = utenteGrezzo.Split(';');

        if (!TryDecode(pezziUtente[0], out var utente))
        {
            error = InvalidEncoding;
            return false;
        }

        if (utente.Trim().Length == 0)
        {
            error = "manca il numero o il nome da chiamare prima della chiocciola";
            return false;
        }

        // QUANDO LA PARTE UTENTE E' UN NUMERO, E QUANDO E' SOLO FATTA DI CIFRE.
        //
        // Per la RFC la parte utente di un indirizzo SIP e' opaca: «812345678.4321»
        // non e' un numero con un punto in mezzo ma un nome, e il gateway di Zoom ci
        // legge dentro identificativo e codice d'accesso proprio grazie a quel punto.
        // Toglierlo come separatore manderebbe in una riunione che non esiste. Si
        // tratta come numero solo cio' che lo dichiara — «user=phone», il piu'
        // iniziale — oppure cio' che non puo' essere altro, le sole cifre.
        var numero = LooksLikePhoneNumber(utente)
                     && (utenteTelefono || utente.TrimStart().StartsWith('+') || utente.All(char.IsAsciiDigit));

        if (numero)
        {
            if (!TryReadNumberParameters(pezziUtente.AsSpan(1), out var parametri, out error)) return false;
            if (!TryBuildPhoneNumber(utente, parametri, homeCountryCode, out var cifre, out error)) return false;

            link = new CallLink(scheme, cifre, dominio, IsPhoneNumber: true, parametri.Extension);
            return true;
        }

        if (!TryValidateSipUser(utente, out error)) return false;

        link = new CallLink(scheme, utente, dominio, IsPhoneNumber: false, Extension: null);
        return true;
    }

    /// <summary>
    /// Verifica il nome utente di un indirizzo SIP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La RFC ammette nel nome utente parecchia punteggiatura. Qui se ne ammette
    /// pochissima — lettere, cifre, punto, trattino, trattino basso — perché il nome
    /// finisce in un'intestazione SIP e prima ancora nel campo di composizione, dove
    /// l'operatore lo sente leggere: «mario.rossi» si legge e si capisce, un nome con
    /// virgolette, parentesi angolari o spazi non è un nome ma un tentativo.
    /// </para>
    /// <para>
    /// Asterisco e cancelletto hanno un rifiuto tutto loro: sono i codici di servizio
    /// del centralino, e il motivo è spiegato dove si costruiscono i numeri.
    /// </para>
    /// </remarks>
    private static bool TryValidateSipUser(string user, out string? error)
    {
        if (user.Contains('*') || user.Contains('#'))
        {
            error = ServiceCodes;
            return false;
        }

        if (user.Length > MaxUserLength)
        {
            error = "il nome da chiamare e' troppo lungo";
            return false;
        }

        if (!user.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            error = "il nome da chiamare contiene caratteri non ammessi in un indirizzo";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Verifica il dominio di un indirizzo SIP, con la porta se c'è.
    /// </summary>
    /// <param name="raw">Dominio come scritto nel link, già privato dei parametri.</param>
    /// <param name="host">Dominio in minuscolo, con la porta se indicata.</param>
    /// <param name="error">Motivo del rifiuto.</param>
    /// <remarks>
    /// Tre forme e basta: un nome di dominio, un indirizzo IPv4, un indirizzo IPv6
    /// fra parentesi quadre. Il dominio non si decodifica: la RFC non ammette
    /// codifiche percentuali lì, e un dominio che ne contiene una non è un dominio.
    /// </remarks>
    private static bool TryParseHost(string raw, out string? host, out string? error)
    {
        host = null;
        var testo = raw.Trim();

        if (testo.Length == 0)
        {
            error = "manca il dominio dopo la chiocciola";
            return false;
        }

        string nome;
        string? porta = null;

        if (testo.StartsWith('['))
        {
            var chiusa = testo.IndexOf(']');
            if (chiusa < 0)
            {
                error = InvalidHost;
                return false;
            }

            nome = testo[..(chiusa + 1)];
            var resto = testo[(chiusa + 1)..];

            if (resto.Length > 0)
            {
                if (!resto.StartsWith(':'))
                {
                    error = InvalidHost;
                    return false;
                }

                porta = resto[1..];
            }

            if (!IsIPv6Literal(nome[1..^1]))
            {
                error = InvalidHost;
                return false;
            }
        }
        else
        {
            var duePunti = testo.IndexOf(':');
            nome = duePunti < 0 ? testo : testo[..duePunti];
            porta = duePunti < 0 ? null : testo[(duePunti + 1)..];

            if (!IsHostName(nome))
            {
                error = InvalidHost;
                return false;
            }
        }

        if (porta is not null && !IsPort(porta))
        {
            error = "la porta indicata dopo il dominio non e' valida";
            return false;
        }

        host = (porta is null ? nome : $"{nome}:{porta}").ToLowerInvariant();
        error = null;
        return true;
    }

    /// <summary>Nome di dominio o indirizzo IPv4, secondo RFC 1035 e RFC 3261.</summary>
    private static bool IsHostName(string name)
    {
        if (name.Length is 0 or > MaxHostLength) return false;

        // Un punto finale e' ammesso dalla sintassi dei nomi di dominio assoluti.
        var etichette = name.TrimEnd('.').Split('.');

        return etichette.All(e =>
            e.Length is > 0 and <= 63
            && e.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            && e[0] != '-'
            && e[^1] != '-');
    }

    /// <summary>Indirizzo IPv6, senza le parentesi quadre.</summary>
    private static bool IsIPv6Literal(string text)
        => text.Length > 0
           && text.Contains(':')
           && text.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.')
           && System.Net.IPAddress.TryParse(text, out var indirizzo)
           && indirizzo.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    private static bool IsPort(string text)
        => text.Length is > 0 and <= 5
           && text.All(char.IsAsciiDigit)
           && int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) is > 0 and <= 65535;

    private static string HostWithoutPort(string host)
    {
        // Il registrar si trova scritto anche come indirizzo intero, «sip:pbx.ente.it»:
        // lo schema non fa parte del nome del server.
        foreach (var schema in (string[])["sips:", "sip:"])
        {
            if (host.StartsWith(schema, StringComparison.OrdinalIgnoreCase)) host = host[schema.Length..];
        }

        if (host.StartsWith('['))
        {
            var chiusa = host.IndexOf(']');
            return chiusa < 0 ? host : host[..(chiusa + 1)];
        }

        var duePunti = host.IndexOf(':');
        return (duePunti < 0 ? host : host[..duePunti]).TrimEnd('.');
    }

    // -----------------------------------------------------------------------
    // Numeri
    // -----------------------------------------------------------------------

    /// <summary>I due parametri di un numero che il posto operatore usa.</summary>
    private readonly record struct NumberParameters(string? Extension, string? PhoneContext)
    {
        public static NumberParameters None => default;
    }

    /// <summary>
    /// Legge i parametri che seguono un numero: <c>;ext=</c> e <c>;phone-context=</c>.
    /// </summary>
    /// <remarks>
    /// Gli altri si scartano senza guardarli, ma solo dopo averli decodificati: un
    /// parametro con una codifica malformata dice che il link è stato costruito male
    /// o costruito apposta, e in entrambi i casi non va preso per buono a metà.
    /// </remarks>
    private static bool TryReadNumberParameters(
        ReadOnlySpan<string> raw,
        out NumberParameters parameters,
        out string? error)
    {
        parameters = NumberParameters.None;
        string? interno = null;
        string? contesto = null;

        foreach (var pezzo in raw)
        {
            var uguale = pezzo.IndexOf('=');
            var nomeGrezzo = uguale < 0 ? pezzo : pezzo[..uguale];
            var valoreGrezzo = uguale < 0 ? string.Empty : pezzo[(uguale + 1)..];

            if (!TryDecode(nomeGrezzo, out var nome) || !TryDecode(valoreGrezzo, out var valore))
            {
                error = InvalidEncoding;
                return false;
            }

            switch (nome.Trim().ToLowerInvariant())
            {
                case "ext":
                    var cifre = new string([.. valore.Where(c => !IsVisualSeparator(c))]);

                    if (cifre.Length is 0 or > MaxExtensionDigits || !cifre.All(char.IsAsciiDigit))
                    {
                        error = "l'interno indicato dopo il numero non e' valido";
                        return false;
                    }

                    interno = cifre;
                    break;

                case "phone-context":
                    contesto = valore.Trim();
                    break;
            }
        }

        parameters = new NumberParameters(interno, contesto);
        error = null;
        return true;
    }

    /// <summary>
    /// Ripulisce un numero di telefono e verifica che si possa comporre da qui.
    /// </summary>
    /// <param name="text">Numero già decodificato.</param>
    /// <param name="parameters">Parametri letti dopo il numero.</param>
    /// <param name="homeCountryCode">Prefisso del paese della postazione.</param>
    /// <param name="number">Solo cifre, con il più iniziale se c'era.</param>
    /// <param name="error">Motivo del rifiuto.</param>
    /// <remarks>
    /// <para>
    /// <b>Separatori.</b> La RFC ammette trattino, punto e parentesi. Si accettano
    /// anche lo spazio e la barra, perché in Italia i numeri si scrivono «06 1234567»
    /// e «06/1234567» e i gestionali li copiano così come sono nell'anagrafica, e le
    /// varianti tipografiche del trattino e dello spazio che un elaboratore di testi
    /// inserisce da sé.
    /// </para>
    /// <para>
    /// <b>Lo «(0)» dopo il prefisso internazionale</b> è la convenzione con cui, in
    /// molti paesi, si indica lo zero da comporre solo dall'interno: +44 (0)20 si
    /// compone +44 20. In Italia lo zero fa parte del numero, quindi +39 (0)6 si
    /// compone +39 06, e lo zero resta. Senza questa regola le firme di posta dei
    /// colleghi esteri produrrebbero numeri con uno zero di troppo.
    /// </para>
    /// <para>
    /// <b>Asterisco e cancelletto si rifiutano</b>, anche se la RFC li ammette nei
    /// numeri locali. Sono i codici con cui il centralino attiva i servizi — la
    /// deviazione di tutte le chiamate verso un numero esterno si attiva con un
    /// asterisco, qualche cifra e un cancelletto — e una pagina web non ha alcuna
    /// ragione legittima per proporne uno. Letto ad alta voce, «asterisco due uno
    /// asterisco» non dice a nessuno che cosa farebbe premendo Invio.
    /// </para>
    /// <para>
    /// <b>Il contesto dei numeri locali.</b> La RFC vuole che un numero locale dica
    /// dove vale; i gestionali non lo fanno quasi mai, e il numero si prende per
    /// buono. Quando lo dicono e il contesto è un altro paese, comporre quel numero da
    /// qui chiamerebbe un altro abbonato, e si rifiuta. Un contesto che è un nome di
    /// dominio indica un'organizzazione — gli interni di un ente — e va bene.
    /// </para>
    /// </remarks>
    private static bool TryBuildPhoneNumber(
        string text,
        NumberParameters parameters,
        string homeCountryCode,
        out string number,
        out string? error)
    {
        number = string.Empty;

        if (!TryCleanNumber(text, homeCountryCode, out var pulito, out error)) return false;

        var internazionale = pulito.StartsWith('+');
        var cifre = internazionale ? pulito.Length - 1 : pulito.Length;

        if (internazionale)
        {
            if (cifre < MinGlobalDigits)
            {
                error = "il numero internazionale e' troppo corto";
                return false;
            }

            if (cifre > MaxGlobalDigits)
            {
                error = "il numero internazionale e' troppo lungo";
                return false;
            }

            if (pulito[1] == '0')
            {
                error = "il prefisso internazionale non puo' cominciare con zero";
                return false;
            }
        }
        else if (cifre > MaxLocalDigits)
        {
            error = "il numero e' troppo lungo";
            return false;
        }

        // Il contesto vale solo per i numeri locali: un numero internazionale vale
        // ovunque per definizione, e la RFC non gli consente di portarne uno.
        if (!internazionale && parameters.PhoneContext is { Length: > 0 } contesto
            && !TryAcceptContext(contesto, homeCountryCode, out error))
        {
            return false;
        }

        number = pulito;
        error = null;
        return true;
    }

    /// <summary>Toglie i separatori e verifica che resti un numero.</summary>
    private static bool TryCleanNumber(string text, string homeCountryCode, out string number, out string? error)
    {
        number = string.Empty;
        var testo = DropTrunkZero(text.Trim(), homeCountryCode);

        if (testo.Length == 0 || !testo.Any(char.IsAsciiDigit))
        {
            // Senza cifre ma con lettere e' un nome — un nome Skype in un callto:, una
            // parola in un tel: — e dirlo spiega il rifiuto meglio di «manca il numero».
            error = testo.Any(char.IsLetter) ? "il numero contiene lettere" : "nel link manca il numero";
            return false;
        }

        var risultato = new StringBuilder(testo.Length);

        for (var i = 0; i < testo.Length; i++)
        {
            var c = testo[i];

            if (char.IsAsciiDigit(c))
            {
                risultato.Append(c);
            }
            else if (c == '+')
            {
                if (i != 0)
                {
                    error = "il segno piu' puo' stare solo all'inizio del numero";
                    return false;
                }

                risultato.Append(c);
            }
            else if (IsVisualSeparator(c))
            {
                // Si salta: e' li' solo per l'occhio.
            }
            else if (c is '*' or '#')
            {
                error = ServiceCodes;
                return false;
            }
            else if (char.IsLetter(c))
            {
                error = "il numero contiene lettere";
                return false;
            }
            else
            {
                error = "il numero contiene caratteri che non fanno parte di un numero di telefono";
                return false;
            }
        }

        number = risultato.ToString();
        error = null;
        return true;
    }

    /// <summary>
    /// Toglie lo «(0)» scritto dopo un prefisso internazionale che non è il nostro.
    /// </summary>
    private static string DropTrunkZero(string text, string homeCountryCode)
    {
        if (!text.StartsWith('+')) return text;

        // Si cerca lo «(0)» subito dopo il primo gruppo di cifre, ignorando gli
        // spazi: «+44 (0)20», «+44(0) 20», «+44 ( 0 ) 20».
        var i = 1;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;

        var prefisso = text[1..i];
        if (prefisso.Length is 0 or > 3) return text;

        var j = SkipSpaces(text, i);
        if (j >= text.Length || text[j] != '(') return text;

        var k = SkipSpaces(text, j + 1);
        if (k >= text.Length || text[k] != '0') return text;

        var l = SkipSpaces(text, k + 1);
        if (l >= text.Length || text[l] != ')') return text;

        // In Italia lo zero resta: fa parte del numero anche dall'estero. Si tolgono
        // solo le parentesi, che altrove sono separatori come gli altri.
        return prefisso == homeCountryCode
            ? text[..i] + "0" + text[(l + 1)..]
            : text[..i] + text[(l + 1)..];
    }

    private static int SkipSpaces(string text, int from)
    {
        while (from < text.Length && (text[from] == ' ' || text[from] == ' ')) from++;
        return from;
    }

    /// <summary>Verifica che un numero locale abbia senso da qui.</summary>
    private static bool TryAcceptContext(string context, string homeCountryCode, out string? error)
    {
        if (context.StartsWith('+'))
        {
            if (!TryCleanNumber(context, homeCountryCode, out var prefisso, out _) || prefisso.Length < 2)
            {
                error = "il prefisso che accompagna il numero non e' valido";
                return false;
            }

            if (!prefisso[1..].StartsWith(homeCountryCode, StringComparison.Ordinal))
            {
                error = $"il numero vale solo all'interno del prefisso piu' {Spaced(prefisso[1..])}, "
                        + "e composto da qui chiamerebbe un altro numero";
                return false;
            }

            error = null;
            return true;
        }

        if (!IsHostName(context))
        {
            error = "il contesto che accompagna il numero non e' valido";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Dice se un testo, già decodificato, ha la forma di un numero di telefono.
    /// </summary>
    /// <remarks>
    /// Basta che non ci siano lettere e ci sia almeno una cifra: il resto lo verifica
    /// la pulizia del numero, che sa dire che cosa non va. Asterisco e cancelletto
    /// fanno parte della forma — un codice di servizio è fatto di cifre — proprio
    /// perché il rifiuto arrivi con la spiegazione giusta.
    /// </remarks>
    private static bool LooksLikePhoneNumber(string text)
        => text.Any(char.IsAsciiDigit)
           && text.All(c => char.IsAsciiDigit(c) || c is '+' or '*' or '#' || IsVisualSeparator(c));

    /// <summary>Separatori che servono solo all'occhio.</summary>
    private static bool IsVisualSeparator(char c)
        => c is '-' or '.' or '(' or ')' or ' ' or '/'
            or ' '   // spazio unificatore
            or ' '   // spazio unificatore stretto
            or '‐'   // trattino
            or '‑'   // trattino unificatore
            or '‒'   // trattino delle cifre
            or '–';  // lineetta breve

    private static string Spaced(string digits) => string.Join(' ', digits.ToCharArray());

    // -----------------------------------------------------------------------
    // Utilità
    // -----------------------------------------------------------------------

    private const string InvalidEncoding = "il link contiene una codifica non valida";

    private const string InvalidHost = "il dominio dopo la chiocciola non e' valido";

    private const string ServiceCodes =
        "contiene asterisco o cancelletto, cioe' un codice di servizio del centralino, "
        + "e un link non puo' proporne";

    /// <summary>
    /// Toglie le barre che alcuni gestionali mettono dopo lo schema.
    /// </summary>
    /// <remarks>
    /// «callto://0612345678» è una forma diffusissima, e alcuni programmi la chiudono
    /// con una barra in più — «callto://0612345678/» — perché la trattano come un
    /// indirizzo web. Nessuno dei quattro schemi prevede le barre, quindi toglierle
    /// non cambia mai il significato di un link scritto bene.
    /// </remarks>
    private static string StripAuthoritySlashes(string body)
    {
        var senza = body.StartsWith("//", StringComparison.Ordinal) ? body[2..] : body;
        return senza.EndsWith('/') ? senza[..^1] : senza;
    }

    private static string CutAt(string text, char separator)
    {
        var posizione = text.IndexOf(separator);
        return posizione < 0 ? text : text[..posizione];
    }

    /// <summary>
    /// Decodifica le sequenze percentuali, rifiutando quelle malformate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Uri.UnescapeDataString(string)"/> non va bene, ed è una scelta: una
    /// sequenza malformata la lascia com'è invece di segnalarla, e un byte che non è
    /// UTF-8 valido lo sostituisce in silenzio. Qui entrambi i casi sono un rifiuto.
    /// </para>
    /// <para>
    /// Si decodifica una volta sola. Un «%2541» diventa «%41» e si ferma lì: il
    /// segno di percentuale rimasto non è ammesso da nessuna parte, e il rifiuto
    /// arriva dalla verifica successiva.
    /// </para>
    /// <para>
    /// Il controllo sui caratteri di controllo si rifà dopo la decodifica, perché
    /// «%0D%0A» è un a capo esattamente come quello scritto per esteso.
    /// </para>
    /// </remarks>
    private static bool TryDecode(string text, out string decoded)
    {
        decoded = string.Empty;

        if (!text.Contains('%'))
        {
            decoded = text;
            return !text.Any(char.IsControl);
        }

        var byteDecodificati = new List<byte>(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c != '%')
            {
                // I caratteri non ASCII scritti per esteso passano per la loro forma
                // UTF-8, cosi' si mescolano correttamente con quelli codificati.
                byteDecodificati.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            if (i + 2 >= text.Length
                || !char.IsAsciiHexDigit(text[i + 1])
                || !char.IsAsciiHexDigit(text[i + 2]))
            {
                return false;
            }

            byteDecodificati.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
            i += 2;
        }

        try
        {
            decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString([.. byteDecodificati]);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return !decoded.Any(char.IsControl);
    }
}
