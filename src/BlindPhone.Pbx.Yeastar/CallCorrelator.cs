using BlindPhone.Core.Abstractions;

namespace BlindPhone.Pbx.Yeastar;

/// <summary>Livello di fiducia con cui una correlazione è stata stabilita.</summary>
public enum CorrelationConfidence
{
    /// <summary>Nessuna corrispondenza trovata.</summary>
    None,

    /// <summary>
    /// Corrispondenza plausibile ma non univoca: più candidati compatibili.
    /// </summary>
    Ambiguous,

    /// <summary>Corrispondenza univoca.</summary>
    Certain,
}

/// <summary>Esito di un tentativo di correlazione.</summary>
/// <param name="LocalCallId">Identificativo locale della chiamata, se determinato.</param>
/// <param name="Confidence">Fiducia con cui la corrispondenza è stata stabilita.</param>
public readonly record struct CorrelationResult(string? LocalCallId, CorrelationConfidence Confidence)
{
    /// <summary>
    /// Indica se il risultato è utilizzabile per attribuire un annuncio a una
    /// chiamata specifica.
    /// </summary>
    /// <remarks>
    /// Solo <see cref="CorrelationConfidence.Certain"/> è utilizzabile. Una
    /// corrispondenza ambigua viene trattata come assente: è la regola di degrado
    /// sicuro descritta più sotto.
    /// </remarks>
    public bool IsUsable => Confidence == CorrelationConfidence.Certain && LocalCallId is not null;
}

/// <summary>
/// Correla le chiamate viste dallo stack SIP locale con quelle riportate
/// dall'Open API del centralino.
/// </summary>
/// <remarks>
/// <para>
/// BlindPhone vede la stessa chiamata da due angolazioni con identificativi
/// diversi: il <c>Call-ID</c> dell'header SIP e il <c>call_id</c> con il
/// <c>channel_id</c> di Yeastar. <b>Non esiste un campo che li leghi
/// direttamente.</b> Senza una correlazione affidabile si producono difetti
/// insidiosi: l'annuncio "trasferimento completato" riferito alla chiamata
/// sbagliata quando ce ne sono due in corso, o il pannello coda che non si aggiorna
/// dopo la risposta.
/// </para>
/// <para>
/// La strategia è a tre livelli, in ordine di affidabilità decrescente:
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Interno più controparte più finestra temporale.</b> L'evento 30008 è già
///   filtrato sull'interno dell'operatore, quindi i candidati sono pochi.
///   </description></item>
///   <item><description>
///   <b>Formato del channel_id.</b> Yeastar usa <c>PJSIP/&lt;interno&gt;-&lt;esadecimale&gt;</c>:
///   il numero dell'interno è dentro l'identificativo e restringe ulteriormente.
///   </description></item>
///   <item><description>
///   <b>Ordine temporale.</b> Con due chiamate contemporanee verso lo stesso numero
///   si applica l'ordine di arrivo.
///   </description></item>
/// </list>
/// <para>
/// <b>Regola di degrado sicuro.</b> Se dopo i tre livelli resta ambiguità, la
/// correlazione viene dichiarata inutilizzabile e l'annuncio omette il riferimento
/// specifico: "Trasferimento completato" invece di "Trasferimento di Mario Rossi
/// completato". Per un operatore che non può verificare a schermo, un'informazione
/// sbagliata è peggio di un'informazione mancante.
/// </para>
/// </remarks>
public sealed class CallCorrelator
{
    /// <summary>Finestra entro cui due eventi possono riferirsi alla stessa chiamata.</summary>
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly List<CandidateCall> _candidates = [];
    private readonly IClock _clock;

    /// <summary>Crea il correlatore.</summary>
    /// <param name="clock">Sorgente del tempo.</param>
    public CallCorrelator(IClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    /// <summary>
    /// Registra una chiamata vista dallo stack SIP locale, rendendola candidata alla
    /// correlazione.
    /// </summary>
    /// <param name="localCallId">Identificativo locale.</param>
    /// <param name="remoteNumber">Numero della controparte.</param>
    /// <param name="observedAt">Istante di osservazione.</param>
    public void RegisterLocalCall(string localCallId, string? remoteNumber, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            PruneExpired(observedAt);
            _candidates.Add(new CandidateCall(localCallId, Normalize(remoteNumber), observedAt, null));
        }
    }

    /// <summary>Rimuove una chiamata terminata dai candidati.</summary>
    /// <param name="localCallId">Identificativo locale.</param>
    public void ForgetLocalCall(string localCallId)
    {
        lock (_gate) _candidates.RemoveAll(c => c.LocalCallId == localCallId);
    }

    /// <summary>
    /// Tenta di correlare un evento del centralino con una chiamata locale.
    /// </summary>
    /// <param name="pbxCallId">Identificativo della chiamata lato PBX.</param>
    /// <param name="channelId">Channel ID Yeastar, nella forma <c>PJSIP/interno-esadecimale</c>.</param>
    /// <param name="remoteNumber">Numero della controparte secondo il centralino.</param>
    /// <param name="extension">Interno dell'operatore.</param>
    /// <param name="eventAt">Istante dell'evento.</param>
    /// <returns>
    /// Esito della correlazione. Va usato solo se
    /// <see cref="CorrelationResult.IsUsable"/> è <c>true</c>.
    /// </returns>
    public CorrelationResult Correlate(
        string? pbxCallId,
        string? channelId,
        string? remoteNumber,
        string extension,
        DateTimeOffset eventAt)
    {
        lock (_gate)
        {
            PruneExpired(eventAt);

            // Livello 0: se questo call_id e' gia' stato associato, la risposta e'
            // immediata e certa. E' il caso piu' frequente dopo il primo evento di
            // una chiamata.
            var known = _candidates.FirstOrDefault(c => c.PbxCallId is not null && c.PbxCallId == pbxCallId);
            if (known is not null) return new CorrelationResult(known.LocalCallId, CorrelationConfidence.Certain);

            // Livello 1: controparte piu' finestra temporale.
            var normalized = Normalize(remoteNumber);
            var byNumber = _candidates
                .Where(c => c.PbxCallId is null)
                .Where(c => normalized is not null && c.RemoteNumber == normalized)
                .Where(c => Math.Abs((c.ObservedAt - eventAt).TotalSeconds) <= MatchWindow.TotalSeconds)
                .ToList();

            if (byNumber.Count == 1)
            {
                Associate(byNumber[0], pbxCallId);
                return new CorrelationResult(byNumber[0].LocalCallId, CorrelationConfidence.Certain);
            }

            // Livello 2: il channel_id contiene il numero dell'interno. Serve a
            // scartare gli eventi che riguardano altri interni, non a scegliere fra
            // due chiamate del nostro.
            if (channelId is not null && !ChannelBelongsToExtension(channelId, extension))
            {
                return new CorrelationResult(null, CorrelationConfidence.None);
            }

            // Livello 3: se resta un solo candidato non ancora associato, e' lui.
            var unassociated = _candidates.Where(c => c.PbxCallId is null).ToList();
            if (unassociated.Count == 1)
            {
                Associate(unassociated[0], pbxCallId);
                return new CorrelationResult(unassociated[0].LocalCallId, CorrelationConfidence.Certain);
            }

            // Ambiguita' residua: si dichiara e non si sceglie. Scegliere il piu'
            // probabile darebbe ragione nella maggior parte dei casi, e proprio per
            // questo renderebbe l'errore raro e quindi impossibile da diagnosticare.
            return byNumber.Count > 1 || unassociated.Count > 1
                ? new CorrelationResult(null, CorrelationConfidence.Ambiguous)
                : new CorrelationResult(null, CorrelationConfidence.None);
        }
    }

    private void Associate(CandidateCall candidate, string? pbxCallId)
    {
        if (pbxCallId is null) return;

        var index = _candidates.IndexOf(candidate);
        if (index >= 0) _candidates[index] = candidate with { PbxCallId = pbxCallId };
    }

    /// <summary>
    /// Verifica che un channel ID Yeastar appartenga all'interno indicato.
    /// </summary>
    /// <remarks>
    /// Il formato osservato nella documentazione è <c>PJSIP/2002-0000004f</c>, dove
    /// la parte fra la barra e il trattino è il numero dell'interno. Il controllo è
    /// volutamente tollerante: se il formato non corrisponde a quello atteso si
    /// restituisce <c>true</c> e si lascia decidere agli altri livelli, invece di
    /// scartare l'evento. Un cambio di formato in un firmware futuro deve degradare
    /// la precisione della correlazione, non azzerarla.
    /// </remarks>
    private static bool ChannelBelongsToExtension(string channelId, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return true;

        var slash = channelId.IndexOf('/');
        if (slash < 0 || slash == channelId.Length - 1) return true;

        var afterSlash = channelId[(slash + 1)..];
        var dash = afterSlash.LastIndexOf('-');
        var candidate = dash > 0 ? afterSlash[..dash] : afterSlash;

        return candidate.Equals(extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Riduce un numero alla forma confrontabile: sole cifre, ultime nove.
    /// </summary>
    /// <remarks>
    /// Il confronto avviene sulle ultime cifre perché lo stesso chiamante può
    /// presentarsi in forme diverse ai due livelli: con o senza prefisso di uscita,
    /// in formato internazionale o nazionale. Nove cifre sono sufficienti a rendere
    /// improbabile una collisione senza essere così tante da far fallire il
    /// confronto sui numeri brevi.
    /// </remarks>
    private static string? Normalize(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;

        var digits = new string([.. number.Where(char.IsDigit)]);
        if (digits.Length == 0) return null;

        return digits.Length <= 9 ? digits : digits[^9..];
    }

    private void PruneExpired(DateTimeOffset now)
    {
        // I candidati vivono al massimo cinque minuti: oltre, una chiamata di quella
        // durata ha gia' avuto molti eventi e sarebbe gia' stata associata.
        _candidates.RemoveAll(c => now - c.ObservedAt > TimeSpan.FromMinutes(5));
    }

    private sealed record CandidateCall(
        string LocalCallId,
        string? RemoteNumber,
        DateTimeOffset ObservedAt,
        string? PbxCallId);
}
