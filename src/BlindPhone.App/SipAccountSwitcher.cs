using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;
using BlindPhone.Infrastructure;
using BlindPhone.Telephony;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>Esito di un cambio di account.</summary>
/// <param name="Success">Se il cambio è avvenuto.</param>
/// <param name="Message">Messaggio in linguaggio naturale, già pronunciabile.</param>
public sealed record AccountSwitchResult(bool Success, string Message);

/// <summary>
/// Governa il passaggio da un account SIP a un altro.
/// </summary>
/// <remarks>
/// <para>
/// Il cambio non è una semplice riscrittura di impostazioni: chiude la registrazione
/// presso un centralino e ne apre un'altra altrove, e con essa cade qualunque
/// conversazione in corso. Da qui le due regole che questa classe applica e che
/// nessuna interfaccia deve poter aggirare.
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Mai con una linea occupata.</b> Il comando viene rifiutato, non chiesto in
///   conferma: chi preme il tasto per abitudine mentre parla con qualcuno non deve
///   poter far cadere quella conversazione con un secondo tasto premuto altrettanto
///   per abitudine.
///   </description></item>
///   <item><description>
///   <b>Mai verso un account inutilizzabile.</b> Passare a un account senza interno
///   o senza dominio porterebbe il posto operatore in modalità simulata, cioè in uno
///   stato in cui sembra funzionare e non riceve chiamate — il modo peggiore di
///   sbagliare su un telefono di servizio.
///   </description></item>
/// </list>
/// </remarks>
public sealed class SipAccountSwitcher
{
    private readonly SettingsService _settingsService;
    private readonly SwitchableTelephonyEngine _engine;
    private readonly CallSessionManager _calls;
    private readonly IAnnouncer _announcer;
    private readonly ILogger<SipAccountSwitcher> _log;

    /// <summary>Crea il servizio.</summary>
    /// <param name="settingsService">Configurazione, per gli account e le password.</param>
    /// <param name="engine">Motore telefonico commutabile.</param>
    /// <param name="calls">Gestore delle chiamate, per sapere se la linea è occupata.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SipAccountSwitcher(
        SettingsService settingsService,
        SwitchableTelephonyEngine engine,
        CallSessionManager calls,
        IAnnouncer announcer,
        ILogger<SipAccountSwitcher> logger)
    {
        _settingsService = settingsService;
        _engine = engine;
        _calls = calls;
        _announcer = announcer;
        _log = logger;
    }

    /// <summary>Account configurati, nell'ordine in cui compaiono nell'elenco.</summary>
    public IReadOnlyList<SipSettings> Accounts => _settingsService.Current.SipAccounts;

    /// <summary>Identificativo dell'account attivo in questo momento.</summary>
    /// <remarks>
    /// Viene dal motore e non dalla configurazione, ed è la differenza che conta: la
    /// configurazione dice quale account si userà al prossimo avvio, il motore dice
    /// con quale si sta lavorando adesso. Confonderli farebbe dire all'elenco che è
    /// attivo un account su cui l'operatore non è registrato.
    /// </remarks>
    public string? ActiveAccountId => _engine.CurrentAccount is { } account
        ? Accounts.FirstOrDefault(a => a.Name == account.Name && a.Username == account.Username)?.Id
        : null;

    /// <summary>Identificativo dell'account usato all'avvio.</summary>
    public string? DefaultAccountId => _settingsService.Current.DefaultSipAccountId;

    /// <summary>
    /// Passa all'account indicato.
    /// </summary>
    /// <param name="accountId">Identificativo dell'account.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    public async Task<AccountSwitchResult> SwitchToAsync(
        string? accountId,
        CancellationToken cancellationToken = default)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);

        if (account is null)
        {
            return new AccountSwitchResult(false, "Account non trovato");
        }

        if (!account.IsUsable)
        {
            // Passare qui porterebbe in modalita' simulata: sembra funzionare e non
            // riceve chiamate, ed e' il modo peggiore di sbagliare su un telefono.
            return new AccountSwitchResult(
                false,
                $"L'account {account.Name} non e' completo: mancano interno o centralino. "
                + "Completalo nelle impostazioni");
        }

        if (_calls.LiveCalls.Count > 0)
        {
            // Rifiuto e non richiesta di conferma: vedi il commento della classe.
            return new AccountSwitchResult(
                false,
                "Non si cambia account con una chiamata in corso: chiudi prima la conversazione");
        }

        if (account.Id == ActiveAccountId)
        {
            return new AccountSwitchResult(false, $"{account.Name} e' gia' l'account in uso");
        }

        _announcer.Announce(Announcement.High(
            $"Passaggio all'account {account.Name}, interno {account.Username}",
            DateTimeOffset.Now,
            coalesceKey: "sip:account"));

        var options = App.BuildAccountOptions(account, _settingsService);
        var ok = await _engine.SwitchAsync(options, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            _log.LogError("Passaggio all'account {Name} non riuscito", account.Name);

            return new AccountSwitchResult(
                false,
                $"Passaggio a {account.Name} non riuscito: si resta sull'account precedente");
        }

        _log.LogInformation("Account SIP attivo: {Name} ({Username})", account.Name, account.Username);

        return new AccountSwitchResult(
            true,
            $"Account {account.Name} attivo, interno {account.Username}. "
            + "La registrazione al centralino richiede qualche secondo");
    }

    /// <summary>
    /// Rende predefinito un account, senza attivarlo.
    /// </summary>
    /// <param name="accountId">Identificativo dell'account.</param>
    /// <remarks>
    /// Attivare e rendere predefinito sono due gesti distinti, e vanno tenuti tali:
    /// un centralinista che copre un secondo ente per un pomeriggio attiva quel
    /// conto senza volerlo trovare anche domani mattina.
    /// </remarks>
    public AccountSwitchResult SetDefault(string? accountId)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);

        if (account is null) return new AccountSwitchResult(false, "Account non trovato");

        var aggiornate = _settingsService.Current with { DefaultSipAccountId = account.Id };

        if (!_settingsService.Save(aggiornate))
        {
            return new AccountSwitchResult(false, "Salvataggio della configurazione non riuscito");
        }

        _log.LogInformation("Account predefinito: {Name}", account.Name);

        return new AccountSwitchResult(
            true, $"{account.Name} sara' l'account usato all'avvio di BlindPhone");
    }
}
