using BlindPhone.Contacts;
using BlindPhone.Contacts.SqlServer;
using BlindPhone.Core.Abstractions;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>Esito di un riallineamento della rubrica dal server.</summary>
/// <param name="Success">Se il riallineamento è riuscito.</param>
/// <param name="Message">Messaggio in linguaggio naturale, pronunciabile.</param>
/// <param name="Written">Contatti scritti nella copia locale.</param>
public sealed record ContactsSyncResult(bool Success, string Message, int Written = 0);

/// <summary>
/// Tiene allineata la copia locale della rubrica con la tabella sul server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Un riallineamento fallito non è un guasto della postazione.</b> Se il server non
/// risponde la rubrica resta quella dell'ultimo allineamento riuscito, e il
/// centralinista continua a lavorare: è tutto il motivo per cui la copia locale
/// esiste. Il fallimento finisce nel registro e nella scheda delle impostazioni, non
/// in un allarme che interrompe il turno.
/// </para>
/// <para>
/// <b>Non si riallinea durante una chiamata.</b> Riversare qualche migliaio di
/// contatti nell'archivio cifrato impegna il disco, e farlo mentre l'operatore sta
/// parlando è il modo per introdurre un'esitazione nell'audio proprio quando non
/// serve. Se c'è una chiamata in corso il turno successivo se ne riparla.
/// </para>
/// </remarks>
public sealed class ContactsSyncService : IDisposable
{
    private readonly IContactRepository _contacts;
    private readonly SecretStore _secrets;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _isBusyWithCall;
    private readonly ILogger<ContactsSyncService> _log;
    private readonly ILogger<SqlServerContactSource> _sourceLog;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;

    /// <summary>Crea il servizio.</summary>
    /// <param name="contacts">Archivio locale da riallineare.</param>
    /// <param name="secrets">Deposito protetto da cui leggere la password.</param>
    /// <param name="settings">Impostazioni correnti, rilette a ogni giro.</param>
    /// <param name="isBusyWithCall">Dice se c'è una chiamata in corso.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <param name="sourceLogger">Log della sorgente.</param>
    public ContactsSyncService(
        IContactRepository contacts,
        SecretStore secrets,
        Func<AppSettings> settings,
        Func<bool> isBusyWithCall,
        ILogger<ContactsSyncService> logger,
        ILogger<SqlServerContactSource> sourceLogger)
    {
        _contacts = contacts;
        _secrets = secrets;
        _settings = settings;
        _isBusyWithCall = isBusyWithCall;
        _log = logger;
        _sourceLog = sourceLogger;
    }

    /// <summary>Avvia il riallineamento periodico, se configurato.</summary>
    public void Start()
    {
        var impostazioni = _settings().Contacts;

        if (impostazioni.Mode != ContactsMode.SqlServer || !impostazioni.SqlServer.IsConfigured)
        {
            _log.LogInformation("Rubrica locale: nessun riallineamento da server");
            return;
        }

        var minuti = impostazioni.SqlServer.SyncIntervalMinutes;

        // Il primo allineamento parte dopo mezzo minuto e non subito: all'avvio la
        // postazione sta gia' registrandosi al centralino e aprendo i dispositivi
        // audio, e quelle due cose vengono prima.
        _timer = new Timer(
            async _ => await SyncAsync(CancellationToken.None).ConfigureAwait(false),
            null,
            TimeSpan.FromSeconds(30),
            minuti > 0 ? TimeSpan.FromMinutes(minuti) : Timeout.InfiniteTimeSpan);

        _log.LogInformation(
            "Rubrica collegata a {Server}, riallineamento ogni {Minuti} minuti",
            impostazioni.SqlServer.Server, minuti);
    }

    /// <summary>
    /// Riallinea la copia locale adesso.
    /// </summary>
    /// <param name="cancellationToken">Token di annullamento.</param>
    public async Task<ContactsSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var impostazioni = _settings().Contacts;

        if (impostazioni.Mode != ContactsMode.SqlServer)
            return new ContactsSyncResult(false, "La rubrica non e' collegata a un server");

        if (!impostazioni.SqlServer.IsConfigured)
            return new ContactsSyncResult(false, "Il collegamento al server non e' completo");

        if (_isBusyWithCall())
        {
            _log.LogInformation("Riallineamento rimandato: chiamata in corso");
            return new ContactsSyncResult(false, "Riallineamento rimandato: c'e' una chiamata in corso");
        }

        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
            return new ContactsSyncResult(false, "Un riallineamento e' gia' in corso");

        try
        {
            var sorgente = BuildSource(impostazioni.SqlServer);

            var contatti = await sorgente.LoadAsync(cancellationToken).ConfigureAwait(false);

            if (contatti.Count == 0)
            {
                // Non si svuota la rubrica su una lettura vuota: una tabella
                // temporaneamente vuota, o un permesso revocato che restituisce zero
                // righe, lascerebbe il centralino senza rubrica per un guasto altrui.
                _log.LogWarning("Il server ha restituito zero contatti: la copia locale resta invariata");
                return new ContactsSyncResult(false, "Il server non ha restituito contatti: la rubrica resta quella di prima");
            }

            await _contacts.ClearAsync(sorgente.Name, cancellationToken).ConfigureAwait(false);
            var scritti = await _contacts.UpsertAsync(contatti, sorgente.Name, cancellationToken).ConfigureAwait(false);

            _log.LogInformation("Rubrica riallineata dal server: {Conteggio} contatti", scritti);

            return new ContactsSyncResult(true, $"Rubrica riallineata: {scritti} contatti", scritti);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Riallineamento della rubrica non riuscito");
            return new ContactsSyncResult(false, "Riallineamento non riuscito: la rubrica resta quella di prima");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Costruisce la sorgente dalle impostazioni salvate.</summary>
    /// <param name="settings">Collegamento configurato.</param>
    public SqlServerContactSource BuildSource(ContactsSqlServerSettings settings)
    {
        var password = settings.UseWindowsAuthentication
            ? null
            : _secrets.Get(settings.PasswordSecretKey);

        return new SqlServerContactSource(ToOptions(settings), password, _sourceLog);
    }

    /// <summary>Traduce le impostazioni salvate nei parametri del collegamento.</summary>
    /// <param name="settings">Collegamento configurato.</param>
    public static SqlServerOptions ToOptions(ContactsSqlServerSettings settings) => new()
    {
        Server = settings.Server,
        Database = settings.Database,
        Authentication = settings.UseWindowsAuthentication
            ? SqlServerAuthentication.Windows
            : SqlServerAuthentication.SqlLogin,
        Username = settings.Username,
        PasswordSecretKey = settings.PasswordSecretKey,
        Table = settings.Table,
        TrustServerCertificate = settings.TrustServerCertificate,
        SyncIntervalMinutes = settings.SyncIntervalMinutes,
        Mapping = new CsvColumnMapping
        {
            Extension = settings.Mapping.Extension,
            RelatedTo = settings.Mapping.RelatedTo,
            Category = settings.Mapping.Category,
            FirstName = settings.Mapping.FirstName,
            LastName = settings.Mapping.LastName,
            Department = settings.Mapping.Department,
            Building = settings.Mapping.Building,
            Floor = settings.Mapping.Floor,
        },
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }
}
