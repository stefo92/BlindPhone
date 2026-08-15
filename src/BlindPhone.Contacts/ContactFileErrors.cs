namespace BlindPhone.Contacts;

/// <summary>
/// Traduce un guasto di lettura di un file di rubrica in una frase che dice
/// all'operatore cosa fare.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nasce da un difetto vero, del 10 agosto 2026.</b> La lettura delle intestazioni
/// catturava qualunque eccezione e restituiva un elenco vuoto, e la procedura guidata
/// concludeva «il file non contiene una riga di intestazione riconoscibile». Il file
/// era semplicemente aperto in Excel. L'operatore ha passato il tempo a cercare un
/// problema di formato che non esisteva, mentre bastava chiudere una finestra.
/// </para>
/// <para>
/// <b>Un messaggio che indica la causa sbagliata è peggio di nessun messaggio</b>, e
/// su un'interfaccia sonora molto peggio: chi non vede non può dare un'occhiata al
/// file per smentirlo, quindi la frase che sente è tutto ciò che ha, e se è falsa
/// manda a cercare dalla parte opposta.
/// </para>
/// </remarks>
public static class ContactFileErrors
{
    // I due codici con cui Windows dice "qualcun altro ce l'ha aperto".
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    /// <summary>Descrive il guasto in italiano, pronto per essere pronunciato.</summary>
    /// <param name="exception">Errore incontrato leggendo il file.</param>
    public static string Describe(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException
            => "Il file non esiste piu': controlla il percorso",

        UnauthorizedAccessException
            => "Windows non concede l'accesso a questo file. Se sta su una cartella di rete, "
               + "verifica di avere i permessi di lettura",

        IOException io when IsLocked(io)
            => "Il file e' aperto in un altro programma, per esempio Excel. Chiudilo e riprova: "
               + "finche' resta aperto nessun altro programma puo' leggerlo",

        IOException
            => "Il file non si lascia leggere. Se sta su una cartella di rete o su una chiavetta, "
               + "copialo prima sul disco locale",

        _ => "Il file non e' leggibile. Verifica che sia un CSV o un backup di banca dati",
    };

    /// <summary>Il file è tenuto aperto da un altro processo.</summary>
    /// <param name="exception">Errore di ingresso e uscita.</param>
    /// <remarks>
    /// Il codice sta negli ultimi sedici bit di <c>HResult</c>: è la forma in cui gli
    /// errori di Windows arrivano dentro un'eccezione gestita.
    /// </remarks>
    public static bool IsLocked(IOException exception)
        => (exception.HResult & 0xFFFF) is SharingViolation or LockViolation;
}
