using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Pbx.Yeastar;

/// <summary>
/// Client REST per l'Open API Yeastar P-Series, con gestione autonoma del ciclo di
/// vita dei token.
/// </summary>
/// <remarks>
/// <para>
/// La gestione dei token è la parte che merita attenzione, perché l'API impone tre
/// vincoli che, presi insieme, rendono l'approccio ingenuo inaffidabile:
/// </para>
/// <list type="bullet">
///   <item><description>l'access token scade dopo 30 minuti;</description></item>
///   <item><description>il refresh token scade dopo 24 ore, quindi non basta rinnovare all'infinito;</description></item>
///   <item><description>ogni applicazione può avere al massimo otto token validi contemporaneamente.</description></item>
/// </list>
/// <para>
/// Il client rinnova <b>proattivamente</b> cinque minuti prima della scadenza, e
/// non al primo 401. La differenza conta: scoprire la scadenza da un errore
/// significa che quell'errore è capitato durante un'operazione reale, e su un posto
/// operatore quell'operazione può essere un trasferimento a metà.
/// </para>
/// </remarks>
public sealed class YeastarApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly YeastarOptions _options;
    private readonly ILogger<YeastarApiClient> _log;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly bool _ownsHttpClient;
    private readonly CertificateTrustService? _certificateTrust;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _accessTokenExpiresAt;
    private DateTimeOffset _refreshTokenExpiresAt;

    /// <summary>Crea il client.</summary>
    /// <param name="options">Parametri di collegamento.</param>
    /// <param name="httpClient">
    /// Client HTTP da riusare; se omesso ne viene creato uno dedicato, configurato
    /// secondo <paramref name="options"/>.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="certificateTrust">
    /// Politica di fiducia sui certificati. Se presente ha la precedenza su
    /// <see cref="YeastarOptions.AllowUntrustedCertificate"/>: un certificato non
    /// verificabile viene sottoposto all'operatore invece di essere accettato o
    /// rifiutato in silenzio.
    /// </param>
    public YeastarApiClient(
        YeastarOptions options,
        HttpClient? httpClient = null,
        ILogger<YeastarApiClient>? logger = null,
        CertificateTrustService? certificateTrust = null)
    {
        _options = options;
        _log = logger ?? NullLogger<YeastarApiClient>.Instance;
        _certificateTrust = certificateTrust;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new HttpClientHandler();

            if (certificateTrust is not null)
            {
                // La via di mezzo fra «verifica tutto» e «accetta qualunque cosa»:
                // un certificato che il sistema non riconosce viene mostrato
                // all'operatore, che decide. Vedi CertificateTrustService.
                handler.ServerCertificateCustomValidationCallback =
                    (_, certificate, _, errors) => ValidateCertificate(certificate, errors);
            }
            else if (options.AllowUntrustedCertificate)
            {
                // Percorso residuo, per gli strumenti a riga di comando e per le
                // postazioni che l'avevano gia' attivato: nessuna verifica.
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            _http = new HttpClient(handler) { Timeout = options.RequestTimeout };
            _ownsHttpClient = true;
        }

        // L'API Yeastar richiede questa intestazione su ogni richiesta.
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(YeastarProtocol.RequiredUserAgent))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", YeastarProtocol.RequiredUserAgent);
        }
    }

    /// <summary>
    /// Decide se accettare il certificato del centralino.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Esposto come metodo pubblico perché lo usa anche il flusso eventi: il
    /// WebSocket si collega alla stessa macchina, sulla stessa porta, e deve applicare
    /// la stessa decisione. Duplicare la politica su due percorsi significherebbe
    /// chiedere due volte la stessa cosa, che è il modo più rapido per insegnare a
    /// rispondere di sì senza ascoltare.
    /// </para>
    /// </remarks>
    /// <param name="certificate">Certificato presentato.</param>
    /// <param name="errors">Errori rilevati dalla verifica di sistema.</param>
    public bool ValidateCertificate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;

        if (_certificateTrust is null) return _options.AllowUntrustedCertificate;

        var description = CertificateDescriptionFactory.Create(
            _options.Host, "al centralino", certificate, errors);

        var verdict = _certificateTrust.Evaluate(description);

        if (!verdict.Accepted)
        {
            _log.LogWarning(
                "Collegamento all'API del centralino rifiutato: {Reason}", verdict.Reason);
        }

        return verdict.Accepted;
    }

    /// <summary>Indica se il client dispone di un token valido.</summary>
    public bool IsAuthenticated =>
        _accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt;

    /// <summary>
    /// Access token corrente, necessario per costruire l'URL del WebSocket.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
        return _accessToken ?? throw new InvalidOperationException("Token non disponibile");
    }

    // -----------------------------------------------------------------------
    // Autenticazione
    // -----------------------------------------------------------------------

    /// <summary>
    /// Garantisce la presenza di un access token valido, rinnovandolo o
    /// riautenticandosi secondo necessità.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <exception cref="YeastarApiException">Se l'autenticazione fallisce.</exception>
    public async Task EnsureTokenAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (_accessToken is not null && now < _accessTokenExpiresAt - _options.TokenRenewalMargin)
            return;

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Ricontrollo dopo l'acquisizione: un'altra richiesta concorrente
            // potrebbe aver gia' rinnovato mentre si attendeva il semaforo.
            now = DateTimeOffset.UtcNow;
            if (_accessToken is not null && now < _accessTokenExpiresAt - _options.TokenRenewalMargin)
                return;

            // Il refresh token vale 24 ore: se e' ancora buono si rinnova, altrimenti
            // si rifa' l'autenticazione completa. Il secondo caso capita almeno una
            // volta al giorno su una postazione sempre accesa, quindi non e' un
            // percorso eccezionale e va gestito in silenzio.
            var canRefresh = _refreshToken is not null && now < _refreshTokenExpiresAt;

            var response = canRefresh
                ? await RefreshTokenAsync(cancellationToken).ConfigureAwait(false)
                : null;

            response ??= await AuthenticateAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess || string.IsNullOrEmpty(response.AccessToken))
            {
                throw new YeastarApiException(
                    response.ErrCode,
                    TranslateAuthError(response.ErrCode, response.ErrMsg));
            }

            _accessToken = response.AccessToken;
            _refreshToken = response.RefreshToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.AccessTokenExpireSeconds);
            _refreshTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.RefreshTokenExpireSeconds);

            _log.LogInformation(
                "Token Yeastar ottenuto, valido fino alle {ExpiresAt:HH:mm:ss}",
                _accessTokenExpiresAt.ToLocalTime());
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<YeastarTokenResponse> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var payload = new { username = _options.ClientId, password = _options.ClientSecret };

        using var response = await _http
            .PostAsJsonAsync($"{_options.BaseUrl}/get_token", payload, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return await ReadTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<YeastarTokenResponse?> RefreshTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var payload = new { refresh_token = _refreshToken };

            using var response = await _http
                .PostAsJsonAsync($"{_options.BaseUrl}/refresh_token", payload, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var token = await ReadTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
            return token.IsSuccess ? token : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Il rinnovo fallito non e' fatale: si ricade sull'autenticazione
            // completa, che ha piu' probabilita' di riuscire.
            _log.LogWarning(ex, "Rinnovo del token non riuscito, si procede con una nuova autenticazione");
            return null;
        }
    }

    private static async Task<YeastarTokenResponse> ReadTokenResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new YeastarTokenResponse
            {
                ErrCode = (int)response.StatusCode,
                ErrMsg = response.ReasonPhrase,
            };
        }

        var token = await response.Content
            .ReadFromJsonAsync<YeastarTokenResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return token ?? new YeastarTokenResponse { ErrCode = -1, ErrMsg = "Risposta non leggibile" };
    }

    /// <summary>
    /// Traduce un codice di errore di autenticazione in un messaggio comprensibile.
    /// </summary>
    /// <remarks>
    /// I messaggi finiscono in un annuncio vocale che l'operatore ascolta una volta
    /// sola. "Credenziali API rifiutate dal centralino" gli permette di chiamare
    /// l'assistenza sapendo cosa dire; "errcode 40001" no.
    /// </remarks>
    private static string TranslateAuthError(int code, string? message) => code switch
    {
        401 or 403 => "Credenziali API rifiutate dal centralino",
        404 => "Servizio API non trovato sul centralino: verificare che la funzione sia abilitata",
        >= 500 => "Il centralino ha risposto con un errore interno",
        -1 => "Risposta del centralino non comprensibile",
        _ when !string.IsNullOrWhiteSpace(message) => $"Il centralino ha rifiutato le credenziali: {message}",
        _ => "Autenticazione al centralino non riuscita",
    };

    // -----------------------------------------------------------------------
    // Chiamate all'API
    // -----------------------------------------------------------------------

    /// <summary>Esegue una GET autenticata e deserializza la risposta.</summary>
    /// <typeparam name="T">Tipo della risposta attesa.</typeparam>
    /// <param name="path">Percorso relativo, per esempio <c>queue/call_status</c>.</param>
    /// <param name="query">Parametri di query, esclusi il token.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <exception cref="YeastarApiException">Se il centralino risponde con un errore.</exception>
    public async Task<T> GetAsync<T>(
        string path,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken cancellationToken)
        where T : YeastarResponse
    {
        await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);

        var url = BuildUrl(path, query);

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Esegue una POST autenticata e deserializza la risposta.</summary>
    /// <typeparam name="T">Tipo della risposta attesa.</typeparam>
    /// <param name="path">Percorso relativo.</param>
    /// <param name="body">Corpo della richiesta.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <exception cref="YeastarApiException">Se il centralino risponde con un errore.</exception>
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
        where T : YeastarResponse
    {
        await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);

        var url = BuildUrl(path, null);

        using var response = await _http
            .PostAsJsonAsync(url, body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<T>(response, path, cancellationToken).ConfigureAwait(false);
    }

    private string BuildUrl(string path, IReadOnlyDictionary<string, string>? query)
    {
        var parameters = new List<string> { $"access_token={Uri.EscapeDataString(_accessToken ?? string.Empty)}" };

        if (query is not null)
        {
            parameters.AddRange(query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        }

        return $"{_options.BaseUrl}/{path.TrimStart('/')}?{string.Join('&', parameters)}";
    }

    private async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        string path,
        CancellationToken cancellationToken)
        where T : YeastarResponse
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new YeastarApiException(
                (int)response.StatusCode,
                $"Il centralino ha risposto {(int)response.StatusCode} alla richiesta {path}");
        }

        var result = await response.Content
            .ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            throw new YeastarApiException(-1, $"Risposta non leggibile dalla richiesta {path}");
        }

        if (!result.IsSuccess)
        {
            throw new YeastarApiException(
                result.ErrCode,
                $"Il centralino ha rifiutato la richiesta {path}: {result.ErrMsg ?? "motivo non indicato"}");
        }

        return result;
    }

    /// <summary>
    /// Rilascia il token sul centralino.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// Va invocato alla chiusura dell'applicazione. Non è una cortesia: con il
    /// limite di otto token per applicazione, i token abbandonati da chiusure
    /// precedenti restano validi per mezz'ora e riducono quelli disponibili alle
    /// altre postazioni.
    /// </remarks>
    public async Task ReleaseTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is null) return;

        try
        {
            await PostAsync<YeastarResponse>("del_token", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Rilascio del token non riuscito");
        }
        finally
        {
            _accessToken = null;
            _refreshToken = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _tokenGate.Dispose();
        if (_ownsHttpClient) _http.Dispose();
    }
}

/// <summary>Errore restituito dall'Open API Yeastar.</summary>
public sealed class YeastarApiException : Exception
{
    /// <summary>Crea l'eccezione.</summary>
    /// <param name="errorCode">Codice di errore restituito dal centralino.</param>
    /// <param name="message">
    /// Messaggio in linguaggio naturale, già pronto per essere pronunciato.
    /// </param>
    public YeastarApiException(int errorCode, string message) : base(message) => ErrorCode = errorCode;

    /// <summary>Codice di errore del centralino, per il log e la diagnostica.</summary>
    public int ErrorCode { get; }
}
