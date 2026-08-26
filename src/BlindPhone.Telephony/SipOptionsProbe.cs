using System.Net.Sockets;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.SIP;

namespace BlindPhone.Telephony;

/// <summary>
/// Chiede al centralino se un interno è raggiungibile, con la stessa domanda che il
/// centralino gira a noi.
/// </summary>
/// <remarks>
/// <para>
/// <b>È il <i>qualify</i> alla rovescia.</b> Il PBX manda a questa postazione un
/// <c>OPTIONS</c> ogni due minuti per sapere se è viva, e chi non risponde smette di
/// ricevere chiamate. Girata verso l'interno di un collega, la stessa domanda risponde
/// a «il suo telefono è acceso e registrato?».
/// </para>
/// <para>
/// <b>Perché questa strada esiste accanto alle altre due.</b> L'API del centralino
/// richiede un piano di licenza; le sottoscrizioni al pacchetto <c>dialog</c> richiedono
/// che l'impianto le serva, e sul centralino su cui questo prodotto è in esercizio non
/// le serve — ottantuno sottoscrizioni inviate, ottantuno rifiutate con <c>404</c>,
/// nessuna accettata, misurato nel registro del 17 agosto 2026. Il metodo
/// <c>OPTIONS</c> invece <b>ogni</b> impianto SIP deve trattarlo: glielo impone
/// l'RFC 3261 §11, ed è lo stesso obbligo per cui questa postazione risponde al
/// sondaggio del centralino.
/// </para>
/// <para>
/// <b>Non lascia niente dietro di sé.</b> Nessuna sottoscrizione da rinnovare, nessuno
/// stato che il PBX debba tenere in memoria per noi, nessun limite di sottoscrizioni
/// concorrenti da superare: una domanda, una risposta, fine. È il motivo per cui si può
/// permettere di guardare l'intera squadra e non solo i cinque nomi che l'operatore ha
/// sotto gli occhi.
/// </para>
/// <para>
/// <b>Quello che non sa.</b> Se il collega stia parlando. La risposta dice che il
/// telefono c'è, non che sia libero, e chi la usa lo dichiara: la colonna dice «in
/// turno», non «disponibile». Per il dettaglio servono le altre due strade.
/// </para>
/// <para>
/// <b>Un sondaggio non fa mai fallire niente.</b> Qualunque cosa vada storta — rete,
/// libreria, indirizzo malformato — produce «non lo so» e non un'eccezione: il quadro
/// dei turni è un di più, e non deve poter disturbare il telefono.
/// </para>
/// </remarks>
public sealed class SipOptionsProbe : IReachabilityProbe
{
    private readonly SIPTransport _transport;
    private readonly SipAccountOptions _account;
    private readonly ILogger<SipOptionsProbe> _log;

    /// <summary>Crea il sondaggio.</summary>
    /// <param name="transport">Trasporto SIP già avviato dal motore telefonico.</param>
    /// <param name="account">Credenziali e dominio dell'account.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SipOptionsProbe(
        SIPTransport transport,
        SipAccountOptions account,
        ILogger<SipOptionsProbe>? logger = null)
    {
        _transport = transport;
        _account = account;
        _log = logger ?? NullLogger<SipOptionsProbe>.Instance;
    }

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_account.Domain);

    /// <inheritdoc />
    public string Domain => _account.Domain;

    /// <inheritdoc />
    public async Task<ReachabilityOutcome> ProbeAsync(
        string target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(target)) return ReachabilityOutcome.Unknown;

        try
        {
            var risposta = await SendAsync(target, digest: null, timeout, cancellationToken)
                .ConfigureAwait(false);

            if (risposta is null) return ReachabilityOutcome.Unknown;

            // UNA SFIDA DI AUTENTICAZIONE NON E' UN RIFIUTO.
            //
            // Alcuni impianti chiedono le credenziali anche per OPTIONS. Tradurre la
            // sfida in «non disponibile» spegnerebbe la funzione proprio dove basta
            // rispondere, che e' quello che il codice della registrazione fa da sempre
            // per il REGISTER.
            if (risposta.Status is SIPResponseStatusCodesEnum.Unauthorised
                or SIPResponseStatusCodesEnum.ProxyAuthenticationRequired)
            {
                var seconda = await RetryWithCredentialsAsync(target, risposta, timeout, cancellationToken)
                    .ConfigureAwait(false);

                if (seconda is null) return ReachabilityOutcome.Unknown;

                risposta = seconda;
            }

            var esito = ReachabilityRules.FromStatus((int)risposta.Status);

            _log.LogDebug(
                "Sondaggio su {Bersaglio}: {Codice} {Motivo} letto come {Esito}",
                target, (int)risposta.Status, risposta.ReasonPhrase, esito);

            return esito;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Sondaggio su {Bersaglio} non riuscito", target);
            return ReachabilityOutcome.Unknown;
        }
    }

    /// <summary>Rimanda la domanda firmandola con le credenziali dell'account.</summary>
    private async Task<SIPResponse?> RetryWithCredentialsAsync(
        string target,
        SIPResponse challenge,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var sfida = challenge.Header.AuthenticationHeaders?.FirstOrDefault()?.SIPDigest;
        if (sfida is null) return null;

        var digest = sfida.CopyOf();
        digest.SetCredentials(
            _account.AuthId ?? _account.Username,
            _account.Password,
            BuildUri(target).ToString(),
            SIPMethodsEnum.OPTIONS.ToString());

        return await SendAsync(target, digest, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Manda un <c>OPTIONS</c> e attende la risposta definitiva.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si usa una transazione non-INVITE invece di scrivere direttamente sul trasporto:
    /// è lei che si occupa delle ritrasmissioni e che riconosce la risposta come propria.
    /// Scrivendo a mano bisognerebbe smistare le risposte in arrivo, che è esattamente il
    /// compito che la libreria esiste per svolgere.
    /// </para>
    /// <para>
    /// La scadenza è nostra e non della transazione: quella del protocollo è di
    /// trentadue secondi, cioè mezzo minuto per riga su un elenco che l'operatore sta
    /// guardando adesso.
    /// </para>
    /// </remarks>
    private async Task<SIPResponse?> SendAsync(
        string target,
        SIPAuthorisationDigest? digest,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(target);

        // Il mittente e' l'account di questa postazione: un OPTIONS che arriva da un
        // indirizzo sconosciuto e' la prima cosa che un centralino rifiuta.
        var da = new SIPFromHeader(
            null,
            SIPURI.ParseSIPURIRelaxed($"{_account.Username}@{_account.Domain}"),
            CallProperties.CreateNewTag());

        var a = new SIPToHeader(null, uri.CopyOf(), null);

        var richiesta = SIPRequest.GetRequest(SIPMethodsEnum.OPTIONS, uri, a, da);

        if (digest is not null)
        {
            richiesta.Header.AuthenticationHeaders = [new SIPAuthenticationHeader(digest)];
        }

        var attesa = new TaskCompletionSource<SIPResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transazione = new SIPNonInviteTransaction(_transport, richiesta, null);

        transazione.NonInviteTransactionFinalResponseReceived += (_, _, _, risposta) =>
        {
            attesa.TrySetResult(risposta);
            return Task.FromResult(SocketError.Success);
        };

        transazione.NonInviteTransactionFailed += (_, _) => attesa.TrySetResult(null);

        transazione.SendRequest();

        using var scadenza = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var completata = await Task.WhenAny(attesa.Task, Task.Delay(timeout, scadenza.Token))
            .ConfigureAwait(false);

        await scadenza.CancelAsync().ConfigureAwait(false);

        // Nessuna risposta entro il tempo concesso: si dice «non lo so». Dedurne che il
        // collega non ci sia sarebbe dichiarare assente un ufficio per un pacchetto
        // perso.
        return completata == attesa.Task ? await attesa.Task.ConfigureAwait(false) : null;
    }

    /// <summary>Compone l'indirizzo da sondare.</summary>
    /// <remarks>
    /// Il parametro di trasporto va dichiarato anche qui. Senza, la libreria sceglie il
    /// valore predefinito — UDP in chiaro — e la domanda parte da un canale diverso da
    /// quello su cui l'account è registrato: il centralino la rifiuta, ed è lo stesso
    /// difetto che sulle sottoscrizioni costò 1.589 richieste respinte contro 57
    /// accettate.
    /// </remarks>
    private SIPURI BuildUri(string target)
        => SIPURI.ParseSIPURIRelaxed($"{target}@{_account.Domain}{_account.TransportParameter}");
}
