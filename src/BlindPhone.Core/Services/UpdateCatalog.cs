
namespace BlindPhone.Core.Services;

/// <summary>Un aggiornamento trovato sul server, non ancora scaricato.</summary>
/// <param name="Version">Versione numerica, ricavata dal nome del file.</param>
/// <param name="InstallerFileName">Nome del pacchetto <c>.msi</c> sul server.</param>
/// <param name="NotesFileName">Nome del file di testo con le novità.</param>
public sealed record AvailableUpdate(Version Version, string InstallerFileName, string NotesFileName)
{
    /// <summary>Versione nella forma in cui si mostra e si pronuncia.</summary>
    public string DisplayVersion => Version.Build > 0
        ? $"{Version.Major}.{Version.Minor}.{Version.Build}"
        : $"{Version.Major}.{Version.Minor}";
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
    /// </remarks>
    public static AvailableUpdate? FindNewer(IEnumerable<string> remoteFiles, Version installed)
    {
        AvailableUpdate? migliore = null;

        foreach (var file in remoteFiles)
        {
            var nome = FileNameOnly(file);
            if (ParseVersion(nome) is not { } versione) continue;
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
