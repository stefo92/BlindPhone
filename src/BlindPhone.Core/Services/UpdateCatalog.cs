
namespace BlindPhone.Core.Services;

/// <summary>Un aggiornamento trovato sul server, non ancora scaricato.</summary>
/// <param name="Version">Versione numerica, ricavata dal nome del file.</param>
/// <param name="InstallerFileName">Nome del pacchetto <c>.msi</c> sul server.</param>
/// <param name="NotesFileName">Nome del file di testo con le novità.</param>
public sealed record AvailableUpdate(Version Version, string InstallerFileName, string NotesFileName)
{
    /// <summary>Versione nella forma in cui si mostra e si pronuncia.</summary>
    public string DisplayVersion => UpdateCatalog.Describe(Version);
}

/// <summary>Contenuto del file di testo che accompagna un pacchetto.</summary>
/// <param name="Features">Le novità introdotte, una per riga.</param>
/// <param name="Sha256">
/// Impronta del pacchetto dichiarata dal file delle novità, se c'è.
/// </param>
public sealed record UpdateNotes(IReadOnlyList<string> Features, string? Sha256 = null)
{
    /// <summary>Nessuna nota disponibile.</summary>
    public static readonly UpdateNotes Empty = new([]);

    /// <summary>
    /// Legge il file delle novità.
    /// </summary>
    /// <param name="text">Contenuto del file di testo.</param>
    /// <remarks>
    /// <para>
    /// Il formato è deliberatamente povero, perché lo scrive una persona a mano prima
    /// di caricare il pacchetto: una riga per novità, con o senza trattino davanti. Le
    /// righe vuote si saltano, e una riga che comincia con <c>#</c> è un commento che
    /// non compare a schermo.
    /// </para>
    /// <para>
    /// L'unica riga con un significato speciale è <c>SHA256:</c> seguita
    /// dall'impronta del pacchetto. Non è obbligatoria, ma quando c'è viene
    /// confrontata con il file scaricato prima di proporne l'installazione: è ciò che
    /// distingue un pacchetto arrivato intero da uno arrivato a metà o cambiato per
    /// strada.
    /// </para>
    /// </remarks>
    public static UpdateNotes Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;

        var features = new List<string>();
        string? sha = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r').Trim();

            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            {
                sha = line["SHA256:".Length..].Trim();
                continue;
            }

            // Il trattino o il punto elenco davanti alla riga e' come si scrive un
            // elenco in un file di testo: a schermo lo mette gia' l'interfaccia, e
            // lasciarlo produrrebbe due segni uno dietro l'altro.
            features.Add(line.TrimStart('-', '*', '•', ' ').Trim());
        }

        return new UpdateNotes(features, string.IsNullOrWhiteSpace(sha) ? null : sha);
    }

    /// <summary>
    /// Scrive le novità nello stesso formato del file pubblicato sul server.
    /// </summary>
    /// <returns>Una novità per riga con il trattino davanti, e la riga <c>SHA256:</c> se c'è.</returns>
    /// <remarks>
    /// Serve a conservarle accanto al pacchetto scaricato, così che la domanda al
    /// riavvio possa dire che cosa porta anche senza rete. Rilette con
    /// <see cref="Parse"/> tornano identiche.
    /// </remarks>
    public string Format()
    {
        var righe = Features.Select(f => $"- {f}").ToList();
        if (Sha256 is not null) righe.Add($"SHA256: {Sha256}");

        return string.Join("\n", righe) + (righe.Count > 0 ? "\n" : string.Empty);
    }
}

/// <summary>
/// Riconosce i pacchetti di aggiornamento fra i file di una cartella remota.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il legame fra pacchetto e novità è il nome.</b> Sul server stanno accanto
/// <c>BlindPhone-1.2.0-x64.msi</c> e <c>BlindPhone-1.2.0-x64.txt</c>: stesso nome,
/// estensione diversa. Nessun indice da mantenere, nessun formato da rispettare, e
/// caricare una versione nuova è copiare due file in una cartella.
/// </para>
/// <para>
/// <b>La versione si legge dal nome del file e non da dentro il pacchetto.</b> Aprire
/// un MSI per leggerne la versione significherebbe scaricarlo prima di sapere se
/// serve, cioè scaricare qualche decina di megabyte a ogni controllo. Il prezzo è che
/// un pacchetto rinominato a mano viene interpretato per come si chiama: è la
/// convenzione che l'installatore già produce da sé, e va rispettata.
/// </para>
/// <para>
/// Vive nel core e non accanto al codice di rete perché è la parte che decide
/// <b>se</b> aggiornare, e va verificata con i test: sbagliare il confronto fra due
/// versioni significa proporre in eterno un aggiornamento già installato, oppure non
/// proporne mai nessuno.
/// </para>
/// </remarks>
public static class UpdateCatalog
{
    /// <summary>Prefisso dei pacchetti prodotti dall'installatore.</summary>
    public const string InstallerPrefix = "BlindPhone-";

    /// <summary>Estensione del pacchetto.</summary>
    public const string InstallerExtension = ".msi";

    /// <summary>Estensione del file delle novità.</summary>
    public const string NotesExtension = ".txt";

    /// <summary>
    /// Sceglie fra i file remoti il pacchetto più recente, se è più nuovo di quello
    /// installato.
    /// </summary>
    /// <param name="remoteFiles">Nomi dei file presenti nella cartella remota.</param>
    /// <param name="installed">Versione attualmente installata.</param>
    /// <param name="architecture">
    /// Processore del pacchetto installato, <c>x64</c> o <c>arm64</c>. Se indicato, i
    /// pacchetti dichiarati per un altro processore si scartano.
    /// </param>
    /// <returns>L'aggiornamento da proporre, oppure <c>null</c> se non ce n'è.</returns>
    /// <remarks>
    /// <para>
    /// Si guarda il <b>più recente</b> e non il primo trovato: una cartella che
    /// conserva lo storico dei pacchetti è la norma, ed è anche ciò che permette di
    /// tornare indietro reinstallando a mano una versione precedente.
    /// </para>
    /// <para>
    /// Il confronto è <c>&gt;</c> e non <c>!=</c>: con una versione di prova installata
    /// sulla postazione — più recente di quella pubblicata — proporre il «ritorno»
    /// alla versione del server significherebbe proporre di disinstallare la prova in
    /// corso, e l'installatore di Windows la rifiuterebbe comunque.
    /// </para>
    /// <para>
    /// <b>Il processore si guarda, e prima no.</b> Con <c>BlindPhone-1.0.21-arm64.msi</c>
    /// e <c>BlindPhone-1.0.21-x64.msi</c> nella stessa cartella vinceva il primo
    /// dell'elenco, che in ordine alfabetico è l'arm64: a una postazione x64 si
    /// proponeva un pacchetto che Windows Installer rifiuta. Per questo i documenti
    /// chiedevano due cartelle distinte; il portale dei download le tiene insieme, ed
    /// è giusto che possa farlo. Un pacchetto senza processore nel nome — la forma
    /// dei primi rilasci — vale per tutti, come valeva prima.
    /// </para>
    /// </remarks>
    public static AvailableUpdate? FindNewer(
        IEnumerable<string> remoteFiles, Version installed, string? architecture = null)
    {
        AvailableUpdate? migliore = null;

        foreach (var file in remoteFiles)
        {
            var nome = FileNameOnly(file);
            if (ParseVersion(nome) is not { } versione) continue;
            if (!IsForArchitecture(nome, architecture)) continue;
            if (versione <= installed) continue;
            if (migliore is not null && versione <= migliore.Version) continue;

            migliore = new AvailableUpdate(versione, nome, NotesFileNameFor(nome));
        }

        return migliore;
    }

    /// <summary>
    /// Ricava la versione dal nome di un pacchetto.
    /// </summary>
    /// <param name="fileName">Per esempio <c>BlindPhone-1.2.0-x64.msi</c>.</param>
    /// <returns>La versione, oppure <c>null</c> se il nome non è di un pacchetto.</returns>
    /// <remarks>
    /// Si accettano due, tre o quattro numeri separati da punti, ed è quanto basta a
    /// coprire sia <c>1.2</c> sia <c>1.2.0</c> sia <c>1.2.0.3</c>. Quello che segue —
    /// l'architettura, un'etichetta, qualunque cosa — non viene interpretato: serve
    /// solo a distinguere due pacchetti della stessa versione.
    /// </remarks>
    public static Version? ParseVersion(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (!fileName.EndsWith(InstallerExtension, StringComparison.OrdinalIgnoreCase)) return null;
        if (!fileName.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)) return null;

        var corpo = fileName[InstallerPrefix.Length..^InstallerExtension.Length];

        // Il numero di versione e' la prima sequenza di cifre e punti: quello che
        // segue e' il trattino dell'architettura o un'etichetta qualsiasi.
        var fine = 0;
        while (fine < corpo.Length && (char.IsAsciiDigit(corpo[fine]) || corpo[fine] == '.')) fine++;

        var numeri = corpo[..fine].TrimEnd('.');
        if (numeri.Length == 0) return null;

        var parti = numeri.Split('.');
        if (parti.Length is < 2 or > 4) return null;

        return Version.TryParse(numeri, out var versione) ? versione : null;
    }

    /// <summary>
    /// Ricava dal nome di un pacchetto il processore per cui è costruito.
    /// </summary>
    /// <param name="fileName">Per esempio <c>BlindPhone-1.2.0-arm64.msi</c>.</param>
    /// <returns>
    /// <c>x64</c>, <c>arm64</c> o quello che il nome dichiara, in minuscolo; <c>null</c>
    /// se il nome non è di un pacchetto o non dichiara un processore.
    /// </returns>
    /// <remarks>
    /// È la parola subito dopo il numero di versione, quella che
    /// <c>tools\crea-installatore.ps1</c> aggiunge con <c>-Architettura</c>. Quello che
    /// eventualmente segue un altro trattino è un'etichetta e non si interpreta.
    /// </remarks>
    public static string? ParseArchitecture(string fileName)
    {
        if (ParseVersion(fileName) is null) return null;

        var corpo = fileName[InstallerPrefix.Length..^InstallerExtension.Length];

        var fine = 0;
        while (fine < corpo.Length && (char.IsAsciiDigit(corpo[fine]) || corpo[fine] == '.')) fine++;

        var resto = corpo[fine..].TrimStart('-');
        var taglio = resto.IndexOf('-');
        var processore = taglio >= 0 ? resto[..taglio] : resto;

        return processore.Length == 0 ? null : processore.ToLowerInvariant();
    }

    /// <summary>
    /// Se un pacchetto va bene per il processore indicato.
    /// </summary>
    /// <param name="fileName">Nome del pacchetto.</param>
    /// <param name="architecture">Processore richiesto; <c>null</c> accetta tutto.</param>
    public static bool IsForArchitecture(string fileName, string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture)) return true;

        return ParseArchitecture(FileNameOnly(fileName)) is not { } dichiarato
               || string.Equals(dichiarato, architecture.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Versione nella forma in cui si mostra e si pronuncia.
    /// </summary>
    /// <param name="version">Versione da descrivere.</param>
    /// <returns>
    /// <c>1.2</c>, <c>1.2.3</c> oppure <c>1.2.3.4</c>: gli zeri in coda non si dicono.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Il quarto numero si dice quando c'è.</b> Una versione come <c>1.0.20.1</c> è
    /// una correzione della <c>1.0.20</c>, e tagliarla a tre numeri farebbe leggere
    /// «installata 1.0.20, disponibile 1.0.20» proprio nella finestra che chiede se
    /// aggiornare: due versioni uguali, e nessuna ragione apparente per farlo.
    /// </para>
    /// <para>
    /// Sta qui e non in ogni finestra perché la versione installata e quella trovata
    /// sul server si leggono una accanto all'altra, e devono essere dette nello stesso
    /// modo.
    /// </para>
    /// </remarks>
    public static string Describe(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (version.Revision > 0)
        {
            return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}.{version.Revision}";
        }

        return version.Build > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    /// <summary>Nome del file delle novità che accompagna un pacchetto.</summary>
    /// <param name="installerFileName">Nome del pacchetto.</param>
    public static string NotesFileNameFor(string installerFileName)
        => string.Concat(installerFileName.AsSpan(0, installerFileName.Length - InstallerExtension.Length), NotesExtension);

    /// <summary>
    /// Tiene il solo nome del file, scartando il percorso che alcuni server
    /// antepongono nell'elenco.
    /// </summary>
    /// <remarks>
    /// L'elenco di una cartella FTP non ha un formato solo: c'è chi risponde con i
    /// soli nomi e chi con il percorso completo. Normalizzare qui evita di doverlo
    /// fare in ogni punto che confronta un nome.
    /// </remarks>
    private static string FileNameOnly(string entry)
    {
        var pulito = entry.Trim().TrimEnd('\r');
        var taglio = pulito.LastIndexOfAny(['/', '\\']);

        return taglio >= 0 ? pulito[(taglio + 1)..] : pulito;
    }
}
