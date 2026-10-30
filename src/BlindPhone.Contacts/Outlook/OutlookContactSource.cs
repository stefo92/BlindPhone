using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Contacts.Outlook;

/// <summary>
/// Importa i contatti dalle cartelle Contatti di Outlook classico sulla postazione.
/// </summary>
/// <remarks>
/// <para>
/// <b>È la rubrica personale di chi siede alla postazione</b>, e in un centralino
/// pesa più di quanto sembri: i numeri che il centralinista usa davvero — il tecnico
/// della caldaia, la segreteria del sindaco, il medico reperibile — spesso non stanno
/// nel gestionale dell'ente ma in Outlook, inseriti negli anni da chi c'era prima.
/// </para>
/// <para>
/// Come per le altre sorgenti, i contatti finiscono nella copia locale cifrata: è lì
/// che si cerca mentre il telefono squilla, non in Outlook.
/// </para>
/// </remarks>
public sealed class OutlookContactSource : IContactSource
{
    private readonly Func<int?, CancellationToken, Task<IReadOnlyList<OutlookContactData>>> _read;
    private readonly ILogger<OutlookContactSource> _log;

    /// <summary>Crea la sorgente che legge da Outlook.</summary>
    /// <param name="includeSubfolders">Leggere anche le sottocartelle delle cartelle Contatti.</param>
    /// <param name="logger">Log diagnostico.</param>
    public OutlookContactSource(bool includeSubfolders, ILogger<OutlookContactSource>? logger = null)
        : this(
            (limite, token) => OutlookContactReader.ReadAsync(includeSubfolders, limite, Timeout, token),
            logger)
    {
    }

    /// <summary>Crea la sorgente con un fornitore di contatti qualsiasi.</summary>
    /// <param name="read">Legge i contatti, al massimo quanti indicati.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <remarks>Serve a provare l'abbinamento dei campi senza Outlook.</remarks>
    public OutlookContactSource(
        Func<int?, CancellationToken, Task<IReadOnlyList<OutlookContactData>>> read,
        ILogger<OutlookContactSource>? logger = null)
    {
        _read = read;
        _log = logger ?? NullLogger<OutlookContactSource>.Instance;
    }

    /// <summary>
    /// Tempo concesso a Outlook per una lettura completa.
    /// </summary>
    /// <remarks>
    /// Largo, perché un Outlook che si avvia in quel momento deve prima aprire il
    /// profilo; ma non infinito, perché l'unica ragione per superarlo è una finestra
    /// di Outlook che aspetta una risposta.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Nome con cui i contatti vengono archiviati e annunciati.</summary>
    public const string SourceName = "Contatti di Outlook";

    /// <inheritdoc />
    public string Name => SourceName;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Contact>> LoadAsync(CancellationToken cancellationToken)
        => Build(await _read(null, cancellationToken).ConfigureAwait(false));

    /// <summary>Legge i primi contatti, per l'anteprima nelle impostazioni.</summary>
    /// <param name="rows">Contatti da leggere.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    public async Task<IReadOnlyList<Contact>> PreviewAsync(int rows, CancellationToken cancellationToken)
        => Build(await _read(rows, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<SourceTestResult> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var contatti = await LoadAsync(cancellationToken).ConfigureAwait(false);

            return contatti.Count == 0
                ? new SourceTestResult(false, "Outlook risponde, ma nelle cartelle Contatti non c'e' nessuno con un numero di telefono", 0)
                : new SourceTestResult(
                    true,
                    $"Trovati {contatti.Count} contatti con almeno un numero nelle cartelle Contatti di Outlook",
                    contatti.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lettura dei contatti di Outlook non riuscita");
            return new SourceTestResult(false, DescribeFailure(ex));
        }
    }

    private List<Contact> Build(IReadOnlyList<OutlookContactData> letti)
    {
        var contatti = new List<Contact>(letti.Count);

        foreach (var voce in letti)
        {
            if (BuildContact(voce, Name) is { } contatto) contatti.Add(contatto);
        }

        var senzaNumero = letti.Count - contatti.Count;
        if (senzaNumero > 0)
            _log.LogInformation("{Sorgente}: {Conteggio} contatti ignorati perche' privi di numeri", Name, senzaNumero);

        return contatti;
    }

    /// <summary>
    /// Trasforma un contatto di Outlook in una voce di rubrica.
    /// </summary>
    /// <param name="data">Campi letti da Outlook.</param>
    /// <param name="sourceName">Nome della sorgente.</param>
    /// <returns>La voce, oppure <c>null</c> se il contatto non ha numeri componibili.</returns>
    /// <remarks>
    /// <para>
    /// <b>Outlook non ha un campo per l'interno</b>, e chi lo scrive lo accoda al
    /// numero di ufficio: «0521 703111 int. 4021». Viene riconosciuto e diventa il
    /// recapito predefinito, per la stessa ragione che vale per la directory: è quello
    /// che regge un trasferimento senza uscire dal centralino.
    /// </para>
    /// <para>
    /// Il numero lo formatta Outlook a modo suo — «+39 (0521) 703111» — e viene
    /// ridotto alla forma componibile da <see cref="DirectoryNumber"/>.
    /// </para>
    /// </remarks>
    public static Contact? BuildContact(OutlookContactData data, string sourceName)
    {
        var numeri = new List<PhoneNumber>();
        var scartati = new List<string>();

        Aggiungi(data.BusinessPhone, PhoneNumberKind.Work);
        Aggiungi(data.BusinessPhone2, PhoneNumberKind.Work);
        Aggiungi(data.CompanyMainPhone, PhoneNumberKind.Work);
        Aggiungi(data.MobilePhone, PhoneNumberKind.Mobile);

        void Aggiungi(string? valore, PhoneNumberKind tipo)
        {
            if (string.IsNullOrWhiteSpace(valore)) return;

            if (DirectoryNumber.Parse(valore) is not { } letto)
            {
                scartati.Add(valore.Trim());
                return;
            }

            if (letto.Extension is { } interno) Inserisci(interno, PhoneNumberKind.Extension);
            if (letto.Number is { } numero) Inserisci(numero, tipo);
        }

        void Inserisci(string numero, PhoneNumberKind tipo)
        {
            if (numeri.Any(n => string.Equals(n.Number, numero, StringComparison.Ordinal))) return;

            // L'interno passa davanti anche quando arriva dopo il numero da cui e'
            // stato ricavato: e' il recapito predefinito.
            if (tipo == PhoneNumberKind.Extension && numeri.All(n => n.Kind != PhoneNumberKind.Extension))
            {
                numeri.Insert(0, new PhoneNumber(numero, tipo));
                return;
            }

            numeri.Add(new PhoneNumber(numero, tipo));
        }

        if (numeri.Count == 0) return null;

        // Il primo e' il predefinito, comunque ci sia arrivato.
        numeri = [.. numeri.Select((n, i) => n with { IsPrimary = i == 0 })];

        var haPersona = !string.IsNullOrWhiteSpace(data.FirstName) || !string.IsNullOrWhiteSpace(data.LastName);

        // UN CONTATTO DI OUTLOOK PUO' ESSERE UNA SOCIETA'.
        //
        // In quel caso nome e cognome sono vuoti e il nominativo e' la societa':
        // senza, la voce finirebbe in elenco con il solo numero, cioe' con
        // l'informazione che l'operatore ha gia' davanti e non gli dice chi e'.
        var visualizzato = data.FullName ?? (haPersona ? null : data.CompanyName ?? data.FileAs);

        return new Contact
        {
            Id = BuildId(data, numeri[0].Number),
            FirstName = data.FirstName,
            LastName = data.LastName,
            DisplayName = visualizzato,
            Organization = data.CompanyName,
            Department = data.Department,
            JobTitle = data.JobTitle,
            Building = data.OfficeLocation,
            Notes = scartati.Count == 0 ? null : string.Join("; ", scartati),
            Numbers = numeri,
            Kind = ContactKindRules.Derive(
                hasPersonName: haPersona,
                isShortCode: false,
                description: haPersona ? null : visualizzato),
            Source = sourceName,
            UpdatedAt = data.LastModified ?? DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Compone l'identificativo stabile della voce.
    /// </summary>
    /// <remarks>
    /// L'identificativo di Outlook resta lo stesso finché il contatto non viene
    /// spostato in un altro archivio, e tiene preferiti e numeri rapidi attaccati alla
    /// persona giusta anche se ne cambia il numero. Il ripiego su nome e primo numero
    /// vale per i contatti che non ne hanno uno, che in pratica non esistono.
    /// </remarks>
    private static Guid BuildId(OutlookContactData data, string primoNumero)
    {
        var chiave = data.EntryId is { } id
            ? $"outlook:#{id}"
            : $"outlook:{data.FirstName}|{data.LastName}|{data.FullName}|{primoNumero}";

        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(chiave)));
    }

    /// <summary>
    /// Traduce un errore della lettura in una frase che dice cosa fare.
    /// </summary>
    /// <param name="exception">Errore ricevuto.</param>
    public static string DescribeFailure(Exception exception) => exception switch
    {
        OutlookUnavailableException propria => propria.Message,
        OperationCanceledException => "Lettura dei contatti di Outlook interrotta",
        COMException => "Outlook ha interrotto la lettura dei contatti. Se Outlook mostra una finestra, chiudila e riprova",
        _ => "Lettura dei contatti di Outlook non riuscita",
    };
}
