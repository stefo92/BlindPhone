using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>Come è andato l'invio di un lotto.</summary>
public enum PostHogOutcome
{
    /// <summary>Il server l'ha accettato.</summary>
    Sent,

    /// <summary>
    /// Il server l'ha rifiutato in un modo che non cambierà riprovando: il lotto si
    /// scarta.
    /// </summary>
    Rejected,

    /// <summary>Il server non ha risposto o era in difficoltà: il lotto resta e si riprova.</summary>
    Failed,
}

/// <summary>
/// Manda a PostHog gli eventi delle statistiche d'uso, a lotti.
/// </summary>
/// <remarks>
/// <para>
/// <b>PERCHÉ UN CLIENT PROPRIO E NON L'SDK UFFICIALE.</b> PostHog pubblica un SDK per
/// .NET, il pacchetto <c>PostHog</c>, e la scelta ovvia sarebbe stata usarlo. È stato
/// valutato e scartato per tre ragioni, in ordine di peso.
/// </para>
/// <list type="number">
///   <item><description>
///   <b>L'elenco chiuso deve valere sui byte che escono, non sulle intenzioni.</b> L'SDK
///   compone il corpo da sé, con le proprietà che decide lui — il nome e la versione
///   della libreria, per cominciare — e quello che aggiunge può cambiare a ogni sua
///   versione. Con un client proprio il corpo lo scrive <see cref="Serialize"/>, che le
///   prove leggono campo per campo; con l'SDK la garanzia andrebbe riverificata a ogni
///   aggiornamento della dipendenza, e nessuno lo farebbe.
///   </description></item>
///   <item><description>
///   <b>È un SDK pensato per i server</b>: valutazione dei contrassegni delle funzioni,
///   cache in memoria e fabbrica dei client HTTP fra le dipendenze, e un ciclo di
///   rilascio fitto. Ogni sua versione nuova sarebbe una dipendenza da collaudare in un
///   prodotto che si firma e si distribuisce a enti pubblici.
///   </description></item>
///   <item><description>
///   <b>Quello che serve è una sola chiamata documentata</b>: una POST in JSON su
///   <c>/batch/</c>, con la chiave del progetto e l'elenco degli eventi. Sono poche
///   decine di righe, che qui sotto si leggono per intero.
///   </description></item>
/// </list>
/// <para>
/// <b>I rinvii non si seguono.</b> Un 301 da un server HTTPS verso un indirizzo HTTP, o
/// verso un altro dominio, manderebbe chiave e identificativo dove nessuno li ha
/// configurati: il lotto si scarta e il registro dice perché.
/// </para>
/// <para>
/// <b>Gli errori restano nel registro.</b> Un server delle statistiche irraggiungibile
/// non è un problema dell'operatore, e non c'è niente che lui possa farci: nessun
/// annuncio, nessuna finestra.
/// </para>
/// </remarks>
public sealed class PostHogClient : IDisposable
{
    /// <summary>Quanti eventi al massimo in un lotto.</summary>
    public const int MaxBatchSize = 50;

    private readonly HttpClient _http;
    private readonly ILogger<PostHogClient> _log;

    /// <summary>Crea il client.</summary>
    /// <param name="handler">
    /// Gestore delle connessioni; omesso se ne crea uno che non segue i rinvii. Le prove
    /// ne passano uno che si fida del certificato del server finto.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    /// <remarks>
    /// Crearlo non apre connessioni: la prima parte con il primo lotto.
    /// </remarks>
    public PostHogClient(HttpMessageHandler? handler = null, ILogger<PostHogClient>? logger = null)
    {
        _log = logger ?? NullLogger<PostHogClient>.Instance;

        // Venti secondi bastano a un server che sta bene. Di piu' sarebbe una richiesta
        // appesa sullo sfondo durante una chiamata, per un dato che puo' aspettare il
        // giro successivo.
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    /// <summary>Il gestore delle connessioni predefinito: niente rinvii, tempi brevi.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>Manda un lotto.</summary>
    /// <param name="endpoint">Indirizzo dei lotti, da <see cref="TelemetrySettings.BatchEndpoint"/>.</param>
    /// <param name="projectKey">Chiave del progetto.</param>
    /// <param name="installationId">Identificativo anonimo dell'installazione.</param>
    /// <param name="batch">Eventi da mandare.</param>
    /// <param name="cancellationToken">Annullamento: arriva con la chiusura del programma.</param>
    /// <returns>Come è andata. Non solleva eccezioni, salvo l'annullamento.</returns>
    public async Task<PostHogOutcome> SendAsync(
        Uri endpoint,
        string projectKey,
        string installationId,
        IReadOnlyList<TelemetryEvent> batch,
        CancellationToken cancellationToken)
    {
        // Ripetuto qui anche se l'indirizzo arriva gia' controllato: e' l'ultima riga
        // prima della rete, e un client che si fidasse di chi lo chiama manderebbe in
        // chiaro la prima volta che qualcuno lo chiama in un modo nuovo.
        if (!endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("Statistiche d'uso: indirizzo {Scheme} rifiutato, serve https", endpoint.Scheme);
            return PostHogOutcome.Rejected;
        }

        var corpo = Serialize(projectKey, installationId, batch, out var scartati);

        if (scartati > 0)
        {
            _log.LogWarning(
                "Statistiche d'uso: {Scartati} eventi fuori dal catalogo tolti dal lotto prima dell'invio", scartati);
        }

        if (corpo is null) return PostHogOutcome.Rejected;

        try
        {
            using var contenuto = new ByteArrayContent(corpo);
            contenuto.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using var risposta = await _http.PostAsync(endpoint, contenuto, cancellationToken).ConfigureAwait(false);
            var codice = (int)risposta.StatusCode;

            if (risposta.IsSuccessStatusCode)
            {
                _log.LogDebug("Statistiche d'uso: lotto di {Eventi} eventi accettato", batch.Count - scartati);
                return PostHogOutcome.Sent;
            }

            // Si riprova solo quando ha senso: il server e' occupato, o e' in
            // difficolta'. Un 400 o un 401 resteranno tali, e riprovarli ogni pochi
            // minuti per settimane sarebbe solo traffico.
            var riprovare = risposta.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                            || codice >= 500;

            _log.LogWarning(
                "Statistiche d'uso: il server ha risposto {Codice}, lotto {Esito}",
                codice,
                riprovare ? "da riprovare" : codice is >= 300 and < 400 ? "scartato: i rinvii non si seguono" : "scartato");

            return riprovare ? PostHogOutcome.Failed : PostHogOutcome.Rejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Statistiche d'uso: invio non riuscito, si riprova piu' tardi");
            return PostHogOutcome.Failed;
        }
    }

    /// <summary>
    /// Il corpo esatto di una richiesta: tutto quello che esce dalla postazione.
    /// </summary>
    /// <param name="projectKey">Chiave del progetto.</param>
    /// <param name="installationId">Identificativo anonimo dell'installazione.</param>
    /// <param name="batch">Eventi.</param>
    /// <param name="discarded">Quanti eventi sono stati tolti perché fuori dal catalogo.</param>
    /// <returns>Il JSON in UTF-8, oppure <c>null</c> se non resta niente da mandare.</returns>
    /// <remarks>
    /// <para>
    /// Oltre alle proprietà del catalogo ne aggiunge due, uguali per ogni evento:
    /// <c>$process_person_profile</c> a <c>false</c>, perché PostHog non costruisca un
    /// profilo di persona attorno all'identificativo, e <c>$geoip_disable</c> a
    /// <c>true</c>, perché non ricavi dall'indirizzo IP la città da cui arriva.
    /// </para>
    /// <para>
    /// L'ora dell'evento è mezzogiorno del giorno a cui si riferisce, in UTC. Non è un
    /// orario: è il modo di dire a PostHog un giorno senza dirgli l'ora. Vedi
    /// <see cref="TelemetryCatalog"/>.
    /// </para>
    /// </remarks>
    public static byte[]? Serialize(
        string projectKey,
        string installationId,
        IReadOnlyList<TelemetryEvent> batch,
        out int discarded)
    {
        var validi = batch.Where(e => TelemetryCatalog.Check(e).Count == 0).ToList();
        discarded = batch.Count - validi.Count;

        if (validi.Count == 0) return null;

        using var flusso = new MemoryStream();

        using (var json = new Utf8JsonWriter(flusso))
        {
            json.WriteStartObject();
            json.WriteString("api_key", projectKey);
            json.WriteBoolean("historical_migration", false);
            json.WriteStartArray("batch");

            foreach (var evento in validi)
            {
                json.WriteStartObject();
                json.WriteString("event", evento.Name);
                json.WriteString("distinct_id", installationId);
                json.WriteString(
                    "timestamp",
                    evento.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T12:00:00Z");

                json.WriteStartObject("properties");

                foreach (var (nome, valore) in evento.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (valore is bool b) json.WriteBoolean(nome, b);
                    else if (valore is string s) json.WriteString(nome, s);
                }

                json.WriteBoolean("$process_person_profile", false);
                json.WriteBoolean("$geoip_disable", true);
                json.WriteEndObject();

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return flusso.ToArray();
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
