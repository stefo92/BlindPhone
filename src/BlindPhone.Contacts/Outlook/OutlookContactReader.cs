using System.Runtime.InteropServices;

namespace BlindPhone.Contacts.Outlook;

/// <summary>
/// I campi di un contatto di Outlook che servono a un centralino, già letti.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mancano di proposito la posta elettronica, il numero di casa e le note.</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///   Gli indirizzi di posta e le note sono protetti dal controllo di sicurezza del
///   modello a oggetti di Outlook: leggerli da un altro programma può far comparire
///   un avviso dentro Outlook, che chiede di consentire l'accesso. Per chi non vede
///   è una finestra comparsa altrove, durante un riallineamento che non ha chiesto, e
///   il riallineamento resterebbe fermo finché qualcuno non la trova.
///   </description></item>
///   <item><description>
///   Il numero di casa è un dato privato della persona, che a un posto operatore non
///   serve per passare una chiamata: per il principio di minimizzazione non arriva
///   nemmeno nella copia locale.
///   </description></item>
/// </list>
/// </remarks>
public sealed record OutlookContactData
{
    /// <summary>Identificativo di Outlook, stabile finché il contatto resta nella stessa cartella.</summary>
    public string? EntryId { get; init; }

    /// <summary>Nominativo per esteso.</summary>
    public string? FullName { get; init; }

    /// <summary>Nome.</summary>
    public string? FirstName { get; init; }

    /// <summary>Cognome.</summary>
    public string? LastName { get; init; }

    /// <summary>Come Outlook archivia il contatto, per esempio «Rossi, Mario».</summary>
    public string? FileAs { get; init; }

    /// <summary>Società o ente.</summary>
    public string? CompanyName { get; init; }

    /// <summary>Reparto.</summary>
    public string? Department { get; init; }

    /// <summary>Qualifica.</summary>
    public string? JobTitle { get; init; }

    /// <summary>Ufficio, cioè la collocazione.</summary>
    public string? OfficeLocation { get; init; }

    /// <summary>Telefono di ufficio.</summary>
    public string? BusinessPhone { get; init; }

    /// <summary>Secondo telefono di ufficio.</summary>
    public string? BusinessPhone2 { get; init; }

    /// <summary>Numero principale della società.</summary>
    public string? CompanyMainPhone { get; init; }

    /// <summary>Cellulare.</summary>
    public string? MobilePhone { get; init; }

    /// <summary>Ultima modifica del contatto in Outlook.</summary>
    public DateTimeOffset? LastModified { get; init; }
}

/// <summary>Che cosa c'è di Outlook su questa postazione.</summary>
public enum OutlookAvailability
{
    /// <summary>Outlook classico, che si lascia leggere da altri programmi.</summary>
    Classic,

    /// <summary>Solo il nuovo Outlook, che non si lascia leggere da altri programmi.</summary>
    OnlyNewOutlook,

    /// <summary>Nessun Outlook.</summary>
    NotInstalled,
}

/// <summary>
/// Un problema con Outlook, già detto in una frase che si può pronunciare.
/// </summary>
/// <param name="speech">Che cosa è successo e che cosa fare.</param>
/// <param name="inner">Errore originale, per il registro.</param>
public sealed class OutlookUnavailableException(string speech, Exception? inner = null) : Exception(speech, inner);

/// <summary>
/// Legge i contatti dalle cartelle Contatti del profilo di Outlook classico.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il collegamento è tardivo, con <c>dynamic</c>, e non usa gli assembly di
/// interoperabilità di Office.</b> Quelli vanno installati insieme a Office e legano
/// il programma a una versione; il collegamento tardivo funziona con qualunque
/// Outlook classico dal 2010 in poi, e su una postazione senza Outlook non impedisce
/// a BlindPhone di avviarsi.
/// </para>
/// <para>
/// <b>Il nuovo Outlook non espone questa interfaccia</b>, e non c'è modo di leggerne i
/// contatti dalla postazione: vivono nella casella sul server, e per arrivarci serve
/// Microsoft Graph con un'applicazione registrata presso l'ente. Qui ci si limita a
/// riconoscerlo e a dirlo.
/// </para>
/// <para>
/// <b>Outlook non viene mai chiuso.</b> Se era aperto è dell'operatore, e chiuderlo
/// interromperebbe il suo lavoro; se l'ha avviato questa lettura, si chiude da solo
/// quando non resta nessun riferimento — ed è il motivo per cui ogni oggetto viene
/// rilasciato appena letto.
/// </para>
/// </remarks>
public static class OutlookContactReader
{
    /// <summary>Cartella predefinita dei contatti, <c>olFolderContacts</c>.</summary>
    private const int FolderContacts = 10;

    /// <summary>Elementi di tipo contatto, <c>olContactItem</c>.</summary>
    private const int ContactItemType = 2;

    /// <summary>Classe di un contatto, <c>olContact</c>: le liste di distribuzione hanno un'altra classe.</summary>
    private const int ContactClass = 40;

    /// <summary>Il server COM non si è avviato, <c>CO_E_SERVER_EXEC_FAILURE</c>.</summary>
    private const int ServerExecFailure = unchecked((int)0x80080005);

    /// <summary>Che cosa c'è di Outlook su questa postazione.</summary>
    public static OutlookAvailability Probe()
        => Decide(
            classicRegistered: Type.GetTypeFromProgID("Outlook.Application", throwOnError: false) is not null,
            newOutlookPresent: IsNewOutlookPresent());

    /// <summary>
    /// Decide la disponibilità da quello che si è trovato.
    /// </summary>
    /// <param name="classicRegistered">L'interfaccia di Outlook classico è registrata.</param>
    /// <param name="newOutlookPresent">Il nuovo Outlook è installato per questo utente.</param>
    /// <remarks>
    /// Con entrambi installati vince il classico, che resta leggibile anche quando
    /// l'utente ha scelto di aprire il nuovo: è l'interfaccia del classico che si usa,
    /// non la finestra che l'utente tiene aperta.
    /// </remarks>
    public static OutlookAvailability Decide(bool classicRegistered, bool newOutlookPresent)
    {
        if (classicRegistered) return OutlookAvailability.Classic;

        return newOutlookPresent ? OutlookAvailability.OnlyNewOutlook : OutlookAvailability.NotInstalled;
    }

    /// <summary>Che cosa dire all'operatore quando Outlook classico non c'è.</summary>
    /// <param name="availability">Che cosa si è trovato.</param>
    /// <returns>La frase, oppure <c>null</c> se Outlook classico c'è.</returns>
    public static string? Explain(OutlookAvailability availability) => availability switch
    {
        OutlookAvailability.OnlyNewOutlook =>
            "Su questa postazione c'e' solo il nuovo Outlook, che non permette ad altri programmi di leggere i contatti. "
            + "Serve Outlook classico; in alternativa esporta i contatti in un file CSV da Outlook sul web, "
            + "alla voce Contatti, e importa quel file.",

        OutlookAvailability.NotInstalled =>
            "Outlook non e' installato su questa postazione: non ci sono contatti di Outlook da leggere.",

        _ => null,
    };

    /// <summary>
    /// Il nuovo Outlook è installato per l'utente corrente.
    /// </summary>
    /// <remarks>
    /// È un pacchetto dello Store e non si registra fra i programmi COM: lo si
    /// riconosce dalla cartella del pacchetto o dall'alias con cui si avvia. Sbagliare
    /// questa risposta cambia solo la frase detta all'operatore, non quello che
    /// BlindPhone riesce a fare.
    /// </remarks>
    private static bool IsNewOutlookPresent()
    {
        try
        {
            var locale = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Directory.Exists(Path.Combine(locale, "Packages", "Microsoft.OutlookForWindows_8wekyb3d8bbwe"))
                   || File.Exists(Path.Combine(locale, "Microsoft", "WindowsApps", "olk.exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Legge i contatti da tutte le cartelle Contatti del profilo.
    /// </summary>
    /// <param name="includeSubfolders">Leggere anche le sottocartelle delle cartelle Contatti.</param>
    /// <param name="limit">Contatti da leggere al massimo; <c>null</c> per tutti.</param>
    /// <param name="timeout">Tempo massimo concesso a Outlook.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <exception cref="OutlookUnavailableException">Outlook non c'è o non risponde.</exception>
    /// <remarks>
    /// <para>
    /// <b>Gira su un filo a sé, in un appartamento a filo singolo.</b> È il modello
    /// per cui Outlook è scritto, e il solo che non dipende da come Office ha
    /// registrato le proprie interfacce sulla postazione.
    /// </para>
    /// <para>
    /// <b>Il tempo massimo conta più del solito.</b> Outlook può fermarsi su una
    /// finestra — la scelta del profilo, un avviso di sicurezza, la richiesta di una
    /// password — e chi non vede non sa che è comparsa. Allo scadere la lettura viene
    /// abbandonata e la frase dice dove guardare.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<OutlookContactData>> ReadAsync(
        bool includeSubfolders,
        int? limit,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (Explain(Probe()) is { } assente) throw new OutlookUnavailableException(assente);

        var esito = new TaskCompletionSource<IReadOnlyList<OutlookContactData>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var filo = new Thread(() =>
        {
            try
            {
                esito.TrySetResult(ReadAll(includeSubfolders, limit, cancellationToken));
            }
            catch (Exception ex)
            {
                esito.TrySetException(ex);
            }
        })
        {
            // Un filo rimasto appeso a Outlook non deve impedire a BlindPhone di
            // chiudersi.
            IsBackground = true,
            Name = "Lettura dei contatti di Outlook",
        };

        filo.SetApartmentState(ApartmentState.STA);
        filo.Start();

        try
        {
            return await esito.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new OutlookUnavailableException(
                "Outlook non ha risposto entro il tempo previsto. Se sullo schermo c'e' una finestra di Outlook, "
                + "per esempio la scelta del profilo o un avviso di sicurezza, chiudila e riprova.",
                ex);
        }
    }

    private static List<OutlookContactData> ReadAll(bool includeSubfolders, int? limit, CancellationToken cancellationToken)
    {
        var tipo = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false)
                   ?? throw new OutlookUnavailableException(Explain(OutlookAvailability.NotInstalled)!);

        dynamic outlook;

        try
        {
            outlook = Activator.CreateInstance(tipo)!;
        }
        catch (COMException ex) when (ex.HResult == ServerExecFailure)
        {
            // Il caso tipico e' Outlook aperto come amministratore mentre BlindPhone
            // non lo e', o il contrario: Windows non lascia parlare due programmi con
            // privilegi diversi, e l'errore non lo dice.
            throw new OutlookUnavailableException(
                "Outlook non si e' lasciato avviare. Se e' aperto come amministratore, chiudilo e riaprilo "
                + "normalmente, poi riprova.",
                ex);
        }

        var risultato = new List<OutlookContactData>();
        dynamic? sessione = null;

        try
        {
            sessione = outlook.GetNamespace("MAPI");

            try
            {
                // Nessuna finestra: senza un profilo predefinito Outlook chiederebbe
                // quale usare, e la domanda comparirebbe dove l'operatore non la cerca.
                sessione.Logon(Type.Missing, Type.Missing, false, false);
            }
            catch (COMException ex)
            {
                throw new OutlookUnavailableException(
                    "Outlook non ha un profilo di posta pronto su questa postazione: aprilo una volta, "
                    + "configura l'account e riprova.",
                    ex);
            }

            // Ogni archivio del profilo ha la propria cartella Contatti: la casella
            // personale, una casella condivisa aggiunta come account, un file di dati.
            // Leggere solo quella predefinita lascerebbe fuori la rubrica di
            // segreteria che sta nella casella dell'ufficio.
            dynamic archivi = sessione.Stores;
            int quanti = archivi.Count;

            for (var i = 1; i <= quanti && !Full(); i++)
            {
                dynamic archivio = archivi.Item(i);

                try
                {
                    dynamic cartella;

                    try
                    {
                        cartella = archivio.GetDefaultFolder(FolderContacts);
                    }
                    catch (COMException)
                    {
                        // Archivi senza contatti: cartelle pubbliche, caselle
                        // condivise non memorizzate sulla postazione.
                        continue;
                    }

                    Visit(cartella, includeSubfolders, risultato, limit, cancellationToken);
                    Release(cartella);
                }
                finally
                {
                    Release(archivio);
                }
            }

            Release(archivi);
        }
        finally
        {
            Release(sessione);
            Release(outlook);
        }

        return risultato;

        bool Full() => limit is { } massimo && risultato.Count >= massimo;
    }

    private static void Visit(
        dynamic cartella,
        bool includeSubfolders,
        List<OutlookContactData> risultato,
        int? limit,
        CancellationToken cancellationToken)
    {
        dynamic elementi = cartella.Items;
        int quanti = elementi.Count;

        try
        {
            for (var i = 1; i <= quanti; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (limit is { } massimo && risultato.Count >= massimo) return;

                dynamic elemento = elementi.Item(i);

                try
                {
                    // Le liste di distribuzione stanno nella stessa cartella ma non
                    // hanno numeri: sono un elenco di altri contatti.
                    if ((int)elemento.Class != ContactClass) continue;

                    risultato.Add(ReadContact(elemento));
                }
                finally
                {
                    Release(elemento);
                }
            }
        }
        finally
        {
            Release(elementi);
        }

        if (!includeSubfolders) return;

        dynamic sottocartelle = cartella.Folders;
        int quante = sottocartelle.Count;

        try
        {
            for (var i = 1; i <= quante; i++)
            {
                dynamic sottocartella = sottocartelle.Item(i);

                try
                {
                    if ((int)sottocartella.DefaultItemType == ContactItemType)
                        Visit(sottocartella, includeSubfolders, risultato, limit, cancellationToken);
                }
                finally
                {
                    Release(sottocartella);
                }
            }
        }
        finally
        {
            Release(sottocartelle);
        }
    }

    private static OutlookContactData ReadContact(dynamic contatto)
    {
        DateTimeOffset? modificato = null;

        try
        {
            modificato = new DateTimeOffset((DateTime)contatto.LastModificationTime);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentOutOfRangeException)
        {
            // Una data che non si legge non deve far perdere il contatto.
        }

        return new OutlookContactData
        {
            EntryId = Text(contatto.EntryID),
            FullName = Text(contatto.FullName),
            FirstName = Text(contatto.FirstName),
            LastName = Text(contatto.LastName),
            FileAs = Text(contatto.FileAs),
            CompanyName = Text(contatto.CompanyName),
            Department = Text(contatto.Department),
            JobTitle = Text(contatto.JobTitle),
            OfficeLocation = Text(contatto.OfficeLocation),
            BusinessPhone = Text(contatto.BusinessTelephoneNumber),
            BusinessPhone2 = Text(contatto.Business2TelephoneNumber),
            CompanyMainPhone = Text(contatto.CompanyMainTelephoneNumber),
            MobilePhone = Text(contatto.MobileTelephoneNumber),
            LastModified = modificato,
        };
    }

    private static string? Text(object? valore)
        => valore is string testo && !string.IsNullOrWhiteSpace(testo) ? testo.Trim() : null;

    private static void Release(object? oggetto)
    {
        if (oggetto is not null && Marshal.IsComObject(oggetto)) Marshal.ReleaseComObject(oggetto);
    }
}
