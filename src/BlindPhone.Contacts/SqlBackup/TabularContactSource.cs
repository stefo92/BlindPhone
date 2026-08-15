using System.Security.Cryptography;
using System.Text;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Contacts.SqlBackup;

/// <summary>
/// Costruisce contatti a partire da una tabella, qualunque cosa la fornisca.
/// </summary>
/// <remarks>
/// <para>
/// <b>Un backup su disco e un server sono la stessa cosa vista da qui:</b> righe con
/// colonne. Tenere una sola implementazione significa che la regola su cosa scartare,
/// il modo di comporre l'identificativo e l'anteprima restano identici fra le due
/// procedure — e che una correzione fatta per una vale per l'altra senza doversene
/// ricordare.
/// </para>
/// </remarks>
public abstract class TabularContactSource : IContactSource
{
    private readonly CsvColumnMapping _mapping;
    private readonly string _idPrefix;
    private readonly ILogger _log;

    /// <summary>Crea la sorgente.</summary>
    /// <param name="table">Tabella da leggere.</param>
    /// <param name="mapping">Corrispondenza fra colonne e campi del contatto.</param>
    /// <param name="idPrefix">
    /// Spazio dei nomi dell'identificativo: distingue la provenienza, così la stessa
    /// persona presente in due archivi resta due voci invece di fondersi in silenzio.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    protected TabularContactSource(
        string table,
        CsvColumnMapping mapping,
        string idPrefix,
        ILogger? logger)
    {
        Table = table;
        _mapping = mapping;
        _idPrefix = idPrefix;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Tabella da cui si leggono i contatti.</summary>
    protected string Table { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>Apre il lettore. Ogni chiamata ne restituisce uno nuovo.</summary>
    protected abstract ISqlBackupReader OpenReader();

    /// <inheritdoc />
    public Task<IReadOnlyList<Contact>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(Read(limit: null, cancellationToken));

    /// <summary>
    /// Legge le prime righe, per l'anteprima della procedura guidata.
    /// </summary>
    /// <param name="rows">Righe da leggere.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <remarks>
    /// L'anteprima è indispensabile in un'interfaccia sonora: chi non vede non può
    /// controllare a colpo d'occhio se le colonne sono finite nei campi giusti, e una
    /// mappatura sbagliata si scoprirebbe altrimenti trasferendo una chiamata alla
    /// persona sbagliata.
    /// </remarks>
    public Task<IReadOnlyList<Contact>> PreviewAsync(int rows, CancellationToken cancellationToken)
        => Task.FromResult(Read(rows, cancellationToken));

    /// <inheritdoc />
    public abstract Task<SourceTestResult> TestAsync(CancellationToken cancellationToken);

    /// <summary>Legge la tabella e ne ricava i contatti.</summary>
    /// <param name="limit">Righe da leggere al massimo; <c>null</c> per tutte.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    protected IReadOnlyList<Contact> Read(int? limit, CancellationToken cancellationToken)
    {
        using var lettore = OpenReader();

        var contatti = new List<Contact>();
        var scartate = 0;

        foreach (var riga in lettore.ReadRows(Table, limit, cancellationToken))
        {
            var contatto = Build(riga);

            // Una riga senza recapiti telefonici non serve a un centralino e
            // occuperebbe posizioni nell'elenco dei risultati: su un'interfaccia
            // sonora ogni voce inutile costa tempo di ascolto.
            if (contatto is null)
            {
                scartate++;
                continue;
            }

            contatti.Add(contatto);
        }

        if (scartate > 0)
            _log.LogInformation("{Sorgente}: {Conteggio} righe ignorate perche' prive di numeri", Name, scartate);

        return contatti;
    }

    private Contact? Build(IReadOnlyDictionary<string, string?> riga)
    {
        string? Leggi(string? colonna)
        {
            if (string.IsNullOrWhiteSpace(colonna)) return null;
            if (!riga.TryGetValue(colonna, out var valore)) return null;

            return string.IsNullOrWhiteSpace(valore) ? null : valore.Trim();
        }

        var numeri = new List<PhoneNumber>();

        Aggiungi(Leggi(_mapping.Extension), PhoneNumberKind.Extension);
        Aggiungi(Leggi(_mapping.WorkPhone), PhoneNumberKind.Work);
        Aggiungi(Leggi(_mapping.MobilePhone), PhoneNumberKind.Mobile);

        void Aggiungi(string? valore, PhoneNumberKind tipo)
        {
            if (string.IsNullOrWhiteSpace(valore)) return;
            numeri.Add(new PhoneNumber(valore.Trim(), tipo, numeri.Count == 0));
        }

        if (numeri.Count == 0) return null;

        var nome = Leggi(_mapping.FirstName);
        var cognome = Leggi(_mapping.LastName);
        var visualizzato = Leggi(_mapping.DisplayName);

        // L'identificativo e' deterministico: risincronizzare aggiorna i contatti
        // esistenti invece di duplicarli, e i preferiti e i numeri rapidi assegnati
        // dall'operatore restano attaccati alla persona giusta.
        var chiave = $"{nome}|{cognome}|{visualizzato}|{numeri[0].Number}";

        return new Contact
        {
            Id = DeterministicId($"{_idPrefix}:{Table}:{chiave}"),
            FirstName = nome,
            LastName = cognome,
            DisplayName = visualizzato,
            PhoneticName = Leggi(_mapping.PhoneticName),
            Email = Leggi(_mapping.Email),
            Organization = Leggi(_mapping.Organization),
            Department = Leggi(_mapping.Department),
            JobTitle = Leggi(_mapping.JobTitle),
            Category = Leggi(_mapping.Category),
            RelatedTo = Leggi(_mapping.RelatedTo),
            Building = Leggi(_mapping.Building),
            Floor = Leggi(_mapping.Floor),
            Numbers = numeri,
            Source = Name,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private static Guid DeterministicId(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
