using System.Text.Json.Serialization;

namespace BlindPhone.Pbx.Yeastar;

/// <summary>
/// Modello di trasporto delle risposte dell'Open API Yeastar P-Series.
/// </summary>
/// <remarks>
/// <para>
/// I nomi dei campi rispecchiano esattamente quelli documentati da Yeastar, in
/// <c>snake_case</c>. Non vengono rinominati in stile .NET: quando si legge un log
/// o si confronta con la documentazione ufficiale, la corrispondenza uno a uno vale
/// più dell'aderenza alle convenzioni di nomenclatura. La traduzione verso il
/// dominio avviene in <see cref="YeastarQueueProvider"/>, che è il confine giusto.
/// </para>
/// </remarks>
public static class YeastarProtocol
{
    /// <summary>Codice di esito che indica successo.</summary>
    public const int SuccessCode = 0;

    /// <summary>Intestazione richiesta dall'API Yeastar su ogni richiesta.</summary>
    public const string RequiredUserAgent = "OpenAPI";
}

/// <summary>Campi comuni a tutte le risposte dell'API.</summary>
public record YeastarResponse
{
    /// <summary>Codice di esito: 0 indica successo.</summary>
    [JsonPropertyName("errcode")]
    public int ErrCode { get; init; }

    /// <summary>Messaggio di esito.</summary>
    [JsonPropertyName("errmsg")]
    public string? ErrMsg { get; init; }

    /// <summary>Indica se la richiesta è andata a buon fine.</summary>
    [JsonIgnore]
    public bool IsSuccess => ErrCode == YeastarProtocol.SuccessCode;
}

/// <summary>Risposta di <c>get_token</c> e <c>refresh_token</c>.</summary>
public sealed record YeastarTokenResponse : YeastarResponse
{
    /// <summary>Token da usare nelle chiamate successive.</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    /// <summary>Durata dell'access token in secondi. Yeastar restituisce 1800.</summary>
    [JsonPropertyName("access_token_expire_time")]
    public int AccessTokenExpireSeconds { get; init; }

    /// <summary>Token per il rinnovo senza ricredenziarsi.</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    /// <summary>Durata del refresh token in secondi. Yeastar restituisce 86400.</summary>
    [JsonPropertyName("refresh_token_expire_time")]
    public int RefreshTokenExpireSeconds { get; init; }
}

/// <summary>
/// Una chiamata riportata da <c>queue/call_status</c>, in attesa, in squillo o in
/// conversazione.
/// </summary>
public sealed record YeastarQueueCall
{
    /// <summary>Identificativo della chiamata lato PBX.</summary>
    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }

    /// <summary>Numero del chiamante.</summary>
    [JsonPropertyName("caller_number")]
    public string? CallerNumber { get; init; }

    /// <summary>Nome del chiamante, quando il PBX lo conosce.</summary>
    [JsonPropertyName("caller_display_name")]
    public string? CallerDisplayName { get; init; }

    /// <summary>Numero del chiamato, tipicamente quello della coda.</summary>
    [JsonPropertyName("callee_number")]
    public string? CalleeNumber { get; init; }

    /// <summary>Nome del chiamato, tipicamente il nome della coda.</summary>
    [JsonPropertyName("callee_display_name")]
    public string? CalleeDisplayName { get; init; }

    /// <summary>
    /// Durata in secondi.
    /// </summary>
    /// <remarks>
    /// Per le voci di <c>waiting_list</c> si assume rappresenti il tempo di attesa
    /// in coda. <b>Punto da verificare su PBX reale</b>: se rappresentasse la
    /// durata totale della chiamata, i tempi annunciati sarebbero sistematicamente
    /// sovrastimati (YEASTAR-INTEGRAZIONE.md §13, voce 8).
    /// </remarks>
    [JsonPropertyName("duration")]
    public int Duration { get; init; }
}

/// <summary>Risposta di <c>queue/call_status</c>.</summary>
public sealed record YeastarQueueCallStatusResponse : YeastarResponse
{
    /// <summary>Numero di chiamate in attesa. È il dato che risponde al tasto di conteggio.</summary>
    [JsonPropertyName("waiting_calls")]
    public int WaitingCalls { get; init; }

    /// <summary>Elenco dei chiamanti in attesa.</summary>
    [JsonPropertyName("waiting_list")]
    public IReadOnlyList<YeastarQueueCall> WaitingList { get; init; } = [];

    /// <summary>Numero di chiamate già distribuite e in squillo.</summary>
    [JsonPropertyName("ringing_calls")]
    public int RingingCalls { get; init; }

    /// <summary>Elenco delle chiamate in squillo.</summary>
    [JsonPropertyName("ringing_list")]
    public IReadOnlyList<YeastarQueueCall> RingingList { get; init; } = [];

    /// <summary>Numero di chiamate di coda in conversazione.</summary>
    [JsonPropertyName("active_calls")]
    public int ActiveCalls { get; init; }

    /// <summary>Elenco delle chiamate in conversazione.</summary>
    [JsonPropertyName("active_list")]
    public IReadOnlyList<YeastarQueueCall> ActiveList { get; init; } = [];
}

/// <summary>Un motivo di pausa configurato sul PBX.</summary>
public sealed record YeastarPauseReason
{
    /// <summary>Identificativo del motivo.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Etichetta leggibile.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>Risposta di <c>queue_pause_reason/list</c>.</summary>
public sealed record YeastarPauseReasonListResponse : YeastarResponse
{
    /// <summary>Motivi di pausa disponibili.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<YeastarPauseReason> Data { get; init; } = [];
}

/// <summary>Un contatto della rubrica aziendale del PBX.</summary>
public sealed record YeastarContact
{
    /// <summary>Identificativo del contatto.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Nome.</summary>
    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    /// <summary>Cognome.</summary>
    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }

    /// <summary>Indirizzo di posta elettronica.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary>Telefono di ufficio.</summary>
    [JsonPropertyName("business_number")]
    public string? BusinessNumber { get; init; }

    /// <summary>Telefono cellulare.</summary>
    [JsonPropertyName("mobile_number")]
    public string? MobileNumber { get; init; }

    /// <summary>Telefono di casa.</summary>
    [JsonPropertyName("home_number")]
    public string? HomeNumber { get; init; }

    /// <summary>Organizzazione.</summary>
    [JsonPropertyName("company")]
    public string? Company { get; init; }

    /// <summary>Reparto.</summary>
    [JsonPropertyName("department")]
    public string? Department { get; init; }

    /// <summary>Qualifica.</summary>
    [JsonPropertyName("job_title")]
    public string? JobTitle { get; init; }
}

/// <summary>Risposta delle interrogazioni sulla rubrica aziendale.</summary>
public sealed record YeastarContactListResponse : YeastarResponse
{
    /// <summary>Numero totale di contatti disponibili.</summary>
    [JsonPropertyName("total_number")]
    public int TotalNumber { get; init; }

    /// <summary>Contatti restituiti in questa pagina.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<YeastarContact> Data { get; init; } = [];
}

/// <summary>Un interno configurato sul PBX.</summary>
public sealed record YeastarExtension
{
    /// <summary>Numero dell'interno.</summary>
    [JsonPropertyName("number")]
    public string? Number { get; init; }

    /// <summary>Nome.</summary>
    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    /// <summary>Cognome.</summary>
    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }

    /// <summary>Indirizzo di posta elettronica.</summary>
    [JsonPropertyName("email_addr")]
    public string? Email { get; init; }

    /// <summary>Cellulare associato.</summary>
    [JsonPropertyName("mobile_number")]
    public string? MobileNumber { get; init; }
}

/// <summary>Risposta di <c>extension/list</c>.</summary>
public sealed record YeastarExtensionListResponse : YeastarResponse
{
    /// <summary>Numero totale di interni.</summary>
    [JsonPropertyName("total_number")]
    public int TotalNumber { get; init; }

    /// <summary>Interni restituiti.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<YeastarExtension> Data { get; init; } = [];
}
