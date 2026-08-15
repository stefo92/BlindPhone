using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Dove finiscono le registrazioni delle conversazioni, e con quale nome.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nella cartella Documenti dell'utente e non nei dati dell'applicazione.</b> Una
/// registrazione è un documento dell'operatore: va ritrovata, riascoltata, inoltrata a
/// chi la chiede e cancellata quando non serve più. In
/// <c>%APPDATA%</c> sarebbe in un posto che nessuno apre, e per chi naviga con lo
/// screen reader raggiungerla vorrebbe dire percorrere un albero di cartelle nascoste.
/// </para>
/// <para>
/// <b>Il nome del file è pensato per essere letto a voce.</b> Data e ora in forma
/// ordinabile, poi chi era in linea: così l'elenco della cartella si presenta in
/// ordine cronologico da sé, e l'operatore che cerca «quella di stamattina con
/// l'ufficio tecnico» la riconosce sentendo il nome invece di aprire i file uno per
/// uno.
/// </para>
/// </remarks>
public static class CallRecordingPaths
{
    /// <summary>Nome della cartella dentro Documenti.</summary>
    public const string FolderName = "Registrazione Chiamate";

    /// <summary>
    /// Cartella in cui salvare le registrazioni, creata se non esiste.
    /// </summary>
    /// <param name="documentsPath">
    /// Percorso della cartella Documenti. Iniettato invece di essere letto qui, così
    /// le prove non scrivono fra i documenti di chi le esegue.
    /// </param>
    public static string Folder(string documentsPath) => Path.Combine(documentsPath, FolderName);

    /// <summary>
    /// Compone il percorso del file per una conversazione.
    /// </summary>
    /// <param name="documentsPath">Percorso della cartella Documenti.</param>
    /// <param name="party">Controparte della chiamata.</param>
    /// <param name="startedAt">Istante di inizio della registrazione.</param>
    /// <returns>Percorso completo del file MP3 da creare.</returns>
    public static string FileFor(string documentsPath, CallParty? party, DateTimeOffset startedAt)
        => Path.Combine(Folder(documentsPath), FileName(party, startedAt));

    /// <summary>
    /// Nome del file per una conversazione.
    /// </summary>
    /// <param name="party">Controparte della chiamata.</param>
    /// <param name="startedAt">Istante di inizio della registrazione.</param>
    /// <returns>Per esempio "2026-08-08 09-14-32 Mario Rossi 4021.mp3".</returns>
    public static string FileName(CallParty? party, DateTimeOffset startedAt)
    {
        var quando = startedAt.ToString("yyyy-MM-dd HH-mm-ss");
        var chi = Describe(party);

        return string.IsNullOrEmpty(chi) ? $"{quando}.mp3" : $"{quando} {chi}.mp3";
    }

    /// <summary>Nome e numero della controparte, ridotti a caratteri ammessi in un nome di file.</summary>
    private static string Describe(CallParty? party)
    {
        if (party is null) return string.Empty;

        var pezzi = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(party.DisplayName)) pezzi.Add(party.DisplayName);
        if (!string.IsNullOrWhiteSpace(party.Number)) pezzi.Add(party.Number);

        return Sanitize(string.Join(' ', pezzi));
    }

    /// <summary>
    /// Rende una stringa utilizzabile come nome di file.
    /// </summary>
    /// <param name="value">Testo di partenza.</param>
    /// <remarks>
    /// <para>
    /// I caratteri vietati da Windows si sostituiscono con uno spazio invece di essere
    /// tolti: un nome che arriva dalla segnalazione SIP può contenere qualunque cosa, e
    /// togliere i separatori incollerebbe fra loro parole che erano distinte.
    /// </para>
    /// <para>
    /// La lunghezza si limita perché il percorso completo ha un tetto: un nome
    /// visualizzato lunghissimo — capita con le intestazioni SIP di alcuni centralini —
    /// farebbe fallire la creazione del file proprio mentre la conversazione è in corso,
    /// cioè quando non c'è modo di rimediare.
    /// </para>
    /// </remarks>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var vietati = Path.GetInvalidFileNameChars();
        var pulito = new string([.. value.Select(c => vietati.Contains(c) ? ' ' : c)]);

        // Spazi multipli accorpati: sostituendo i caratteri vietati con spazi se ne
        // creano facilmente due o tre di seguito.
        while (pulito.Contains("  ", StringComparison.Ordinal))
        {
            pulito = pulito.Replace("  ", " ", StringComparison.Ordinal);
        }

        pulito = pulito.Trim();

        return pulito.Length <= 60 ? pulito : pulito[..60].TrimEnd();
    }
}
