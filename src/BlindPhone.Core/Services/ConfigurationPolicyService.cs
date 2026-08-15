using System.Security.Cryptography;
using System.Text;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// Decide che cosa l'operatore può modificare, e verifica la password di sblocco.
/// </summary>
/// <remarks>
/// <para>
/// Tiene insieme i due livelli di criterio: quello di postazione, che un utente
/// senza privilegi amministrativi non può cambiare, e quello scritto nella
/// configurazione dell'utente. <b>Il primo vince sempre</b>, e non solo quando è più
/// restrittivo: un criterio di postazione che dichiara il pannello aperto è una
/// decisione presa da chi amministra la rete, e un file nella cartella dell'utente
/// non deve poterla contraddire in nessuna direzione.
/// </para>
/// <para>
/// Lo sblocco vale per la <b>sessione corrente</b> e non viene scritto da nessuna
/// parte. Chi sblocca per correggere un parametro non deve lasciare la postazione
/// sbloccata per sempre, e soprattutto non deve doverla ribloccare ricordandosene.
/// </para>
/// </remarks>
public sealed class ConfigurationPolicyService
{
    /// <summary>Iterazioni della derivazione della password.</summary>
    /// <remarks>
    /// Duecentomila con SHA-256: il costo è di poche decine di millisecondi su una
    /// postazione da ufficio, cioè invisibile su un'operazione che si fa una volta
    /// ogni tanto, e altrettante volte più caro per chi provasse a indovinare la
    /// password dal file. Il valore accompagna l'impronta perché possa crescere in
    /// futuro senza invalidare quelle già scritte.
    /// </remarks>
    private const int Iterations = 200_000;

    /// <summary>Lunghezza dell'impronta e del sale, in byte.</summary>
    private const int KeyLength = 32;

    private readonly ILogger<ConfigurationPolicyService> _log;
    private readonly Lock _gate = new();

    private ConfigurationPolicy _machine = ConfigurationPolicy.Unrestricted;
    private ConfigurationPolicy _user = ConfigurationPolicy.Unrestricted;
    private bool _unlockedForSession;

    /// <summary>Crea il servizio.</summary>
    /// <param name="logger">Log diagnostico.</param>
    public ConfigurationPolicyService(ILogger<ConfigurationPolicyService>? logger = null)
        => _log = logger ?? NullLogger<ConfigurationPolicyService>.Instance;

    /// <summary>Criterio in vigore, dopo la combinazione dei due livelli.</summary>
    public ConfigurationPolicy Effective
    {
        get
        {
            lock (_gate)
            {
                // Sbloccato per questa sessione: il criterio resta noto — serve a
                // dire all'operatore che la postazione e' gestita — ma non limita
                // piu' nulla.
                if (_unlockedForSession) return _machine with { Access = PanelAccess.Open, LockedSections = [] };

                return _machine.Origin == PolicyOrigin.Machine ? _machine : _user;
            }
        }
    }

    /// <summary>Indica se l'operatore ha sbloccato le impostazioni in questa sessione.</summary>
    public bool IsUnlockedForSession
    {
        get { lock (_gate) return _unlockedForSession; }
    }

    /// <summary>Criterio di postazione, quello non aggirabile.</summary>
    public ConfigurationPolicy MachinePolicy
    {
        get { lock (_gate) return _machine; }
    }

    /// <summary>Assegna i due criteri letti dalle rispettive sorgenti.</summary>
    /// <param name="machine">Criterio di postazione, oppure <c>null</c> se assente.</param>
    /// <param name="user">Criterio scritto nella configurazione dell'utente.</param>
    public void Load(ConfigurationPolicy? machine, ConfigurationPolicy? user)
    {
        lock (_gate)
        {
            _machine = machine ?? ConfigurationPolicy.Unrestricted;
            _user = user ?? ConfigurationPolicy.Unrestricted;
        }

        var vigente = Effective;

        if (vigente.IsRestrictive)
        {
            _log.LogInformation(
                "Criterio di configurazione in vigore ({Origine}): pannello {Accesso}, sezioni bloccate {Sezioni}",
                vigente.Origin, vigente.Access, vigente.DescribeSections());
        }
    }

    /// <summary>Indica se una sezione è modificabile adesso.</summary>
    /// <param name="section">Sezione da verificare.</param>
    public bool CanEdit(SettingsSection section) => Effective.CanEdit(section);

    /// <summary>Indica se il pannello si può aprire senza sbloccare.</summary>
    public bool CanOpenPanel => Effective.CanOpenPanel;

    /// <summary>
    /// Verifica la password e, se corretta, sblocca per la sessione corrente.
    /// </summary>
    /// <param name="password">Password digitata dall'operatore.</param>
    /// <returns><c>true</c> se lo sblocco è avvenuto.</returns>
    /// <remarks>
    /// <b>Senza password configurata non si sblocca.</b> Sarebbe la scappatoia più
    /// ovvia: bloccare il pannello dimenticando la password lo renderebbe apribile da
    /// chiunque premesse Invio su un campo vuoto. Chi si trova in quella condizione
    /// deve passare dal criterio di postazione o dal file di configurazione, che è
    /// scomodo ed è giusto che lo sia.
    /// </remarks>
    public bool Unlock(string? password)
    {
        ConfigurationPolicy criterio;
        lock (_gate) criterio = _machine.Origin == PolicyOrigin.Machine ? _machine : _user;

        if (!criterio.HasPassword)
        {
            _log.LogWarning("Sblocco richiesto ma nessuna password e' configurata nel criterio");
            return false;
        }

        if (!Verify(password, criterio.PasswordHash!, criterio.PasswordSalt!))
        {
            _log.LogWarning("Sblocco delle impostazioni rifiutato: password errata");
            return false;
        }

        lock (_gate) _unlockedForSession = true;

        _log.LogWarning("Impostazioni sbloccate dall'amministratore per la sessione corrente");
        return true;
    }

    /// <summary>Riblocca subito, senza aspettare la chiusura del programma.</summary>
    public void Relock()
    {
        lock (_gate)
        {
            if (!_unlockedForSession) return;
            _unlockedForSession = false;
        }

        _log.LogInformation("Impostazioni ribloccate");
    }

    // -----------------------------------------------------------------------
    // Password
    // -----------------------------------------------------------------------

    /// <summary>
    /// Calcola impronta e sale di una password nuova.
    /// </summary>
    /// <param name="password">Password scelta dall'amministratore.</param>
    /// <returns>Impronta in esadecimale e sale in base 64.</returns>
    /// <remarks>
    /// La password non viene conservata da nessuna parte, nemmeno cifrata: si scrive
    /// solo di che cosa è l'impronta. Il file di configurazione è esportabile e
    /// finisce nei backup, e una password recuperabile da lì aprirebbe tutte le
    /// postazioni configurate con lo stesso pacchetto.
    /// </remarks>
    public static (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(KeyLength);
        var hash = Derive(password, salt);

        return (Convert.ToHexString(hash), Convert.ToBase64String(salt));
    }

    /// <summary>Verifica una password contro impronta e sale.</summary>
    /// <param name="password">Password digitata.</param>
    /// <param name="hash">Impronta attesa, in esadecimale.</param>
    /// <param name="salt">Sale, in base 64.</param>
    public static bool Verify(string? password, string hash, string salt)
    {
        if (string.IsNullOrEmpty(password)) return false;

        try
        {
            var atteso = Convert.FromHexString(hash);
            var calcolato = Derive(password, Convert.FromBase64String(salt));

            // Confronto a tempo costante: un confronto normale si ferma al primo byte
            // diverso, e la differenza di tempo racconta quanti byte erano giusti.
            return CryptographicOperations.FixedTimeEquals(atteso, calcolato);
        }
        catch (FormatException)
        {
            // Impronta o sale scritti male nel file: si rifiuta, invece di sollevare
            // un errore che bloccherebbe l'apertura del pannello.
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
}
