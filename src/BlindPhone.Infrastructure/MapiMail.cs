using System.Runtime.InteropServices;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Prepara un messaggio di posta con allegato nel programma predefinito.
/// </summary>
/// <remarks>
/// <para>
/// Serve una cosa sola che <c>mailto:</c> non sa fare: <b>allegare un file</b>. È la
/// differenza fra un'operazione sola e una sequenza — salva, ricorda dove, apri la
/// posta, scrivi, cerca il file, allega — che su una postazione usata senza vista
/// non si porta a termine mentre si è già in difficoltà.
/// </para>
/// <para>
/// L'interfaccia usata è MAPI semplice, la stessa che sta dietro al comando «Invia a,
/// destinatario posta» di Esplora risorse. È vecchia e questo è il suo pregio: la
/// implementano Outlook e i client aziendali, e non richiede nulla di installato
/// oltre al programma di posta che l'ente usa già.
/// </para>
/// <para>
/// <b>Il messaggio non parte da solo.</b> Si passa il flag che chiede di mostrare
/// l'interfaccia: il messaggio si apre compilato e l'invio resta un gesto
/// dell'operatore. Un allegato con dati di traffico che esce dall'ente senza che
/// nessuno prema «invia» sarebbe una comunicazione di dati personali fatta dal
/// software.
/// </para>
/// <para>
/// Dove MAPI non c'è — postazioni con sola posta web — la chiamata fallisce in modo
/// pulito e chi chiama ripiega su <c>mailto:</c> senza allegato.
/// </para>
/// </remarks>
internal static class MapiMail
{
    /// <summary>Mostra la finestra di composizione invece di inviare in silenzio.</summary>
    private const int MapiDialog = 0x00000008;

    /// <summary>Esito di successo.</summary>
    private const int MapiSuccess = 0;

    /// <summary>L'operatore ha chiuso la finestra senza inviare. Non è un errore.</summary>
    private const int MapiUserAbort = 1;

    /// <summary>Destinatario: «a».</summary>
    private const int MapiTo = 1;

    /// <summary>
    /// Prova a preparare il messaggio.
    /// </summary>
    /// <param name="address">Destinatario.</param>
    /// <param name="subject">Oggetto.</param>
    /// <param name="body">Testo.</param>
    /// <param name="attachmentPath">File da allegare.</param>
    /// <param name="error">Motivo del fallimento, per il registro.</param>
    /// <returns><c>true</c> se il messaggio è stato aperto o inviato.</returns>
    public static bool TrySend(
        string address,
        string subject,
        string body,
        string attachmentPath,
        out string error)
    {
        var recipientPtr = nint.Zero;
        var filePtr = nint.Zero;

        try
        {
            var recipient = new MapiRecipDesc
            {
                RecipClass = MapiTo,
                Name = address,
                Address = "SMTP:" + address,
            };

            recipientPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MapiRecipDesc>());
            Marshal.StructureToPtr(recipient, recipientPtr, fDeleteOld: false);

            var file = new MapiFileDesc
            {
                Position = -1,
                Path = attachmentPath,
                Name = Path.GetFileName(attachmentPath),
            };

            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MapiFileDesc>());
            Marshal.StructureToPtr(file, filePtr, fDeleteOld: false);

            var message = new MapiMessage
            {
                Subject = subject,
                NoteText = body,
                RecipCount = 1,
                Recips = recipientPtr,
                FileCount = 1,
                Files = filePtr,
            };

            var result = MAPISendMail(nint.Zero, nint.Zero, message, MapiDialog, 0);

            if (result is MapiSuccess or MapiUserAbort)
            {
                error = string.Empty;
                return true;
            }

            error = $"MAPISendMail ha restituito {result}";
            return false;
        }
        catch (Exception ex)
        {
            // Tipicamente MAPI32.DLL non c'e', o non c'e' un client di posta
            // registrato: e' una configurazione legittima, non un guasto.
            error = ex.Message;
            return false;
        }
        finally
        {
            if (recipientPtr != nint.Zero) Marshal.FreeHGlobal(recipientPtr);
            if (filePtr != nint.Zero) Marshal.FreeHGlobal(filePtr);
        }
    }

    // DllImport e non LibraryImport: il generatore di P/Invoke non sa gestire il
    // marshalling automatico di una classe con campi stringa, che e' esattamente la
    // forma che MAPI richiede. E' lo stesso motivo per cui gli altri P/Invoke del
    // prodotto usano DllImport.
    [DllImport("MAPI32.DLL", EntryPoint = "MAPISendMail", CharSet = CharSet.Ansi)]
    private static extern int MAPISendMail(
        nint session,
        nint uiParam,
        MapiMessage message,
        int flags,
        int reserved);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class MapiMessage
    {
        public int Reserved;
        public string? Subject;
        public string? NoteText;
        public string? MessageType;
        public string? DateReceived;
        public string? ConversationID;
        public int Flags;
        public nint Originator;
        public int RecipCount;
        public nint Recips;
        public int FileCount;
        public nint Files;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MapiRecipDesc
    {
        public int Reserved;
        public int RecipClass;
        public string? Name;
        public string? Address;
        public int EIDSize;
        public nint EntryID;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MapiFileDesc
    {
        public int Reserved;
        public int Flags;
        public int Position;
        public string? Path;
        public string? Name;
        public nint FileType;
    }
}
