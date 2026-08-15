using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Deposito su file delle decisioni di fiducia sui certificati.
/// </summary>
/// <remarks>
/// <para>
/// Vive in <c>certificati-fidati.json</c>, accanto alla configurazione ma
/// <b>separato da essa</b>. Il motivo è che <c>settings.json</c> è documentato come
/// esportabile e condivisibile fra postazioni: una decisione di fiducia che
/// viaggiasse con la configurazione verrebbe applicata su macchine il cui operatore
/// non l'ha mai presa, ed è esattamente il rilievo che un collaudo di sicurezza
/// presso una PA solleva.
/// </para>
/// <para>
/// Il file non è cifrato e non deve esserlo: contiene impronte pubbliche, non
/// segreti. Deve però essere <b>leggibile</b>, perché la domanda «di quali
/// certificati si fida questa postazione» va potuta rispondere senza aprire il
/// programma.
/// </para>
/// </remarks>
public sealed class CertificateTrustStore : ICertificateTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<CertificateTrustStore> _log;
    private readonly Lock _gate = new();

    private Dictionary<string, CertificateDescription> _trusted = [];

    /// <summary>Crea il deposito.</summary>
    /// <param name="directory">Cartella dati dell'applicazione.</param>
    /// <param name="logger">Log diagnostico.</param>
    public CertificateTrustStore(string directory, ILogger<CertificateTrustStore>? logger = null)
    {
        _path = Path.Combine(directory, "certificati-fidati.json");
        _log = logger ?? NullLogger<CertificateTrustStore>.Instance;
        Load();
    }

    /// <summary>Percorso del file, per la diagnostica.</summary>
    public string Path_ => _path;

    /// <inheritdoc />
    public IReadOnlyList<CertificateDescription> Trusted
    {
        get { lock (_gate) return [.. _trusted.Values]; }
    }

    /// <inheritdoc />
    public bool IsTrusted(string trustKey)
    {
        lock (_gate) return _trusted.ContainsKey(trustKey);
    }

    /// <inheritdoc />
    public void Trust(CertificateDescription certificate)
    {
        lock (_gate) _trusted[certificate.TrustKey] = certificate;
        Save();
    }

    /// <inheritdoc />
    public void Revoke(string trustKey)
    {
        bool removed;
        lock (_gate) removed = _trusted.Remove(trustKey);

        if (removed) Save();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var voci = JsonSerializer.Deserialize<List<CertificateDescription>>(json, JsonOptions) ?? [];

            _trusted = voci.ToDictionary(v => v.TrustKey, v => v);

            _log.LogInformation(
                "Certificati approvati dall'operatore su questa postazione: {Count}", _trusted.Count);
        }
        catch (Exception ex)
        {
            // Un file illeggibile si comporta come un file vuoto: si torna a chiedere.
            // Ripartire da zero su una decisione di sicurezza e' la scelta prudente —
            // l'alternativa sarebbe indovinare quali decisioni erano state prese.
            _log.LogWarning(ex, "Elenco dei certificati approvati non leggibile: le decisioni verranno richieste di nuovo");
            _trusted = [];
        }
    }

    private void Save()
    {
        try
        {
            List<CertificateDescription> voci;
            lock (_gate) voci = [.. _trusted.Values];

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(voci, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Il salvataggio fallito non impedisce di proseguire: la decisione vale
            // comunque per la sessione. Va detto, perche' la differenza si manifesta
            // solo al riavvio successivo, quando la domanda ricompare.
            _log.LogError(ex, "Salvataggio dei certificati approvati non riuscito: la decisione varra' solo per questa sessione");
        }
    }
}

