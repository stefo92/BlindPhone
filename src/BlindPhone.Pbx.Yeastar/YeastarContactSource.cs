using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Pbx.Yeastar;

/// <summary>
/// Rubrica letta direttamente dal centralino: interni e contatti aziendali.
/// </summary>
/// <remarks>
/// <para>
/// Con Yeastar come PBX di riferimento questa diventa la sorgente <b>primaria</b>
/// della rubrica, e per una ragione pratica prima che tecnica: è già configurata,
/// si mantiene aggiornata da sola, e gli interni che restituisce sono esattamente
/// quelli di cui il connettore di coda conosce la presenza. Le sorgenti CSV e LDAP
/// previste dal capitolato restano implementate e servono per i contatti esterni al
/// centralino, ma smettono di essere il percorso principale.
/// </para>
/// <para>
/// Beneficio meno ovvio: azzera il lavoro ricorrente dell'ufficio IT. Una rubrica
/// CSV va riesportata e reimportata a ogni cambio di organico, e nella pratica
/// smette di essere aggiornata dopo pochi mesi. Un operatore con una rubrica vecchia
/// trasferisce chiamate a interni che non esistono più.
/// </para>
/// </remarks>
public sealed class YeastarContactSource : IContactSource
{
    private const int PageSize = 100;

    private readonly YeastarApiClient _api;
    private readonly ILogger<YeastarContactSource> _log;

    /// <summary>Crea la sorgente.</summary>
    /// <param name="api">Client REST verso il centralino.</param>
    /// <param name="logger">Log diagnostico.</param>
    public YeastarContactSource(YeastarApiClient api, ILogger<YeastarContactSource>? logger = null)
    {
        _api = api;
        _log = logger ?? NullLogger<YeastarContactSource>.Instance;
    }

    /// <inheritdoc />
    public string Name => "Rubrica del centralino";

    /// <inheritdoc />
    public async Task<IReadOnlyList<Contact>> LoadAsync(CancellationToken cancellationToken)
    {
        var contacts = new List<Contact>();

        contacts.AddRange(await LoadExtensionsAsync(cancellationToken).ConfigureAwait(false));
        contacts.AddRange(await LoadCompanyContactsAsync(cancellationToken).ConfigureAwait(false));

        _log.LogInformation("Rubrica del centralino: {Count} voci caricate", contacts.Count);
        return contacts;
    }

    /// <summary>
    /// Carica gli interni del centralino.
    /// </summary>
    /// <remarks>
    /// Gli interni sono la parte più usata della rubrica su un posto operatore:
    /// sono le destinazioni dei trasferimenti. Vengono contrassegnati come recapiti
    /// di tipo <see cref="PhoneNumberKind.Extension"/>, che è ciò che permette al
    /// trasferimento di riconoscerli come destinazioni interne e di non chiedere la
    /// conferma prevista per i numeri esterni.
    /// </remarks>
    private async Task<IReadOnlyList<Contact>> LoadExtensionsAsync(CancellationToken cancellationToken)
    {
        var results = new List<Contact>();
        var page = 1;

        while (true)
        {
            var response = await _api.GetAsync<YeastarExtensionListResponse>(
                "extension/list",
                new Dictionary<string, string>
                {
                    ["page"] = page.ToString(),
                    ["page_size"] = PageSize.ToString(),
                },
                cancellationToken).ConfigureAwait(false);

            if (response.Data.Count == 0) break;

            foreach (var ext in response.Data)
            {
                if (string.IsNullOrWhiteSpace(ext.Number)) continue;

                var numbers = new List<PhoneNumber>
                {
                    new(ext.Number, PhoneNumberKind.Extension, IsPrimary: true),
                };

                if (!string.IsNullOrWhiteSpace(ext.MobileNumber))
                    numbers.Add(new PhoneNumber(ext.MobileNumber, PhoneNumberKind.Mobile));

                results.Add(new Contact
                {
                    Id = DeterministicId("ext", ext.Number),
                    FirstName = ext.FirstName,
                    LastName = ext.LastName,
                    Email = ext.Email,
                    Numbers = numbers,
                    Source = Name,
                    UpdatedAt = DateTimeOffset.Now,
                });
            }

            if (results.Count >= response.TotalNumber) break;
            page++;
        }

        return results;
    }

    private async Task<IReadOnlyList<Contact>> LoadCompanyContactsAsync(CancellationToken cancellationToken)
    {
        var results = new List<Contact>();
        var page = 1;

        while (true)
        {
            YeastarContactListResponse response;

            try
            {
                response = await _api.GetAsync<YeastarContactListResponse>(
                    "company_contact/list",
                    new Dictionary<string, string>
                    {
                        ["page"] = page.ToString(),
                        ["page_size"] = PageSize.ToString(),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (YeastarApiException ex)
            {
                // La rubrica aziendale puo' non essere configurata: non e' un errore
                // del prodotto, e gli interni gia' caricati restano validi.
                _log.LogInformation(ex, "Rubrica aziendale non disponibile sul centralino");
                break;
            }

            if (response.Data.Count == 0) break;

            foreach (var contact in response.Data)
            {
                var numbers = new List<PhoneNumber>();

                if (!string.IsNullOrWhiteSpace(contact.BusinessNumber))
                    numbers.Add(new PhoneNumber(contact.BusinessNumber, PhoneNumberKind.Work, IsPrimary: true));

                if (!string.IsNullOrWhiteSpace(contact.MobileNumber))
                    numbers.Add(new PhoneNumber(contact.MobileNumber, PhoneNumberKind.Mobile));

                if (!string.IsNullOrWhiteSpace(contact.HomeNumber))
                    numbers.Add(new PhoneNumber(contact.HomeNumber, PhoneNumberKind.Home));

                // Un contatto senza recapiti telefonici non serve a un centralino:
                // occuperebbe posizioni nell'elenco dei risultati di ricerca, e su
                // un'interfaccia sonora ogni voce inutile costa tempo di ascolto.
                if (numbers.Count == 0) continue;

                results.Add(new Contact
                {
                    Id = DeterministicId("contact", contact.Id.ToString()),
                    FirstName = contact.FirstName,
                    LastName = contact.LastName,
                    Email = contact.Email,
                    Organization = contact.Company,
                    Department = contact.Department,
                    JobTitle = contact.JobTitle,
                    Numbers = numbers,
                    Source = Name,
                    UpdatedAt = DateTimeOffset.Now,
                });
            }

            if (results.Count >= response.TotalNumber) break;
            page++;
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<SourceTestResult> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _api.GetAsync<YeastarExtensionListResponse>(
                "extension/list",
                new Dictionary<string, string> { ["page"] = "1", ["page_size"] = "1" },
                cancellationToken).ConfigureAwait(false);

            return new SourceTestResult(
                true,
                $"Centralino raggiungibile, {response.TotalNumber} interni disponibili",
                response.TotalNumber);
        }
        catch (YeastarApiException ex)
        {
            // Il messaggio dell'eccezione e' gia' in linguaggio naturale e viene
            // pronunciato cosi' com'e': l'operatore lo ascolta una volta sola e deve
            // poterlo riferire all'assistenza.
            return new SourceTestResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Verifica della rubrica del centralino non riuscita");
            return new SourceTestResult(false, "Centralino non raggiungibile");
        }
    }

    /// <summary>
    /// Genera un identificativo stabile a partire dalla chiave della sorgente.
    /// </summary>
    /// <remarks>
    /// Deve essere deterministico: a ogni sincronizzazione lo stesso interno deve
    /// produrre lo stesso identificativo, altrimenti l'archivio locale accumula
    /// duplicati e i preferiti e i numeri rapidi assegnati dall'operatore si
    /// staccano dai contatti a cui si riferivano.
    /// </remarks>
    private static Guid DeterministicId(string prefix, string key)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"yeastar:{prefix}:{key}"));

        return new Guid(bytes);
    }
}
