using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Le chiamate di sistema con cui si parla a una cuffia: <c>hid.dll</c> per i rapporti
/// HID, CfgMgr32 per trovare i dispositivi e sapere quando arrivano o se ne vanno.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché queste e non <c>Windows.Devices.HumanInterfaceDevice</c>.</b> L'interfaccia
/// WinRT richiede di dichiarare nel progetto una versione dell'SDK di Windows — un
/// target diverso da quello di tutta la soluzione, che <c>BandaDotNetTests</c> tiene
/// fermo — e porta con sé la proiezione WinRT, qualche decina di megabyte nel
/// pacchetto. In cambio dà un accesso pensato per le applicazioni impacchettate, che in
/// un programma desktop come questo funziona solo in parte e senza dire perché quando
/// non funziona. Queste funzioni ci sono su ogni Windows, sono quelle che usano i
/// programmi di telefonia da vent'anni, e non hanno bisogno di niente.
/// </para>
/// <para>
/// <b>Perché non gli SDK di Jabra o di Poly.</b> Funzionano solo con le cuffie della
/// loro marca, chiedono un accordo di licenza e una libreria nativa da distribuire, e
/// un ente pubblico compra le cuffie a gara: la marca di domani non è quella di oggi.
/// Lo standard HID è ciò che quelle cuffie parlano comunque.
/// </para>
/// <para>
/// <b>CfgMgr32 e non SetupAPI.</b> È lo strato su cui SetupAPI è costruita: per
/// elencare i dispositivi basta una chiamata invece di tre, e offre la notifica di
/// collegamento senza bisogno di una finestra che riceva i messaggi.
/// </para>
/// <para>
/// Le strutture si leggono per posizione dentro memoria non gestita invece di essere
/// dichiarate: una struttura di <c>hid.dll</c> dichiarata con un campo fuori posto
/// produce valori plausibili e sbagliati, mentre le posizioni scritte qui si
/// confrontano una per una con l'intestazione del sistema.
/// </para>
/// </remarks>
internal static class HidNative
{
    public const int ReportInput = 0;
    public const int ReportOutput = 1;

    /// <summary>HIDP_STATUS_SUCCESS.</summary>
    public const int StatusSuccess = 0x00110000;

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint ShareReadWrite = 0x00000003;
    public const uint OpenExisting = 3;
    public const uint FlagOverlapped = 0x40000000;

    public const int ErrorIoPending = 997;
    public const int ErrorOperationAborted = 995;

    /// <summary>CR_SUCCESS.</summary>
    public const int ConfigRetSuccess = 0;

    /// <summary>CR_BUFFER_SMALL: l'elenco è cresciuto fra le due chiamate.</summary>
    public const int ConfigRetBufferSmall = 0x1A;

    /// <summary>CM_GET_DEVICE_INTERFACE_LIST_PRESENT.</summary>
    public const uint InterfaceListPresent = 0;

    /// <summary>Dimensione di HIDP_CAPS.</summary>
    public const int CapsSize = 64;

    /// <summary>Dimensione di HIDP_BUTTON_CAPS.</summary>
    public const int ButtonCapsSize = 72;

    /// <summary>
    /// Dimensione di CM_NOTIFY_FILTER: quattro campi da quattro byte e un'unione il cui
    /// membro più grande è un identificativo di istanza di 200 caratteri.
    /// </summary>
    public const int NotifyFilterSize = 416;

    /// <summary>Un campo pulsante dei rapporti di una cuffia.</summary>
    /// <param name="UsagePage">Pagina dell'usage.</param>
    /// <param name="ReportId">Rapporto che lo contiene.</param>
    /// <param name="UsageMin">Primo usage.</param>
    /// <param name="UsageMax">Ultimo usage: uguale al primo se il campo non è un intervallo.</param>
    /// <param name="IsAbsolute">Il valore è uno stato e non un impulso.</param>
    public readonly record struct ButtonField(ushort UsagePage, byte ReportId, ushort UsageMin, ushort UsageMax, bool IsAbsolute)
    {
        /// <summary>Gli usage del campo, uno per uno.</summary>
        public IEnumerable<ushort> Usages()
        {
            for (int u = UsageMin; u <= UsageMax; u++) yield return (ushort)u;
        }
    }

    /// <summary>Le grandezze di una collezione HID che servono per leggerla e scriverla.</summary>
    /// <param name="UsagePage">Pagina della collezione principale.</param>
    /// <param name="Usage">Usage della collezione principale.</param>
    /// <param name="InputLength">Lunghezza di un rapporto di ingresso, identificativo compreso.</param>
    /// <param name="OutputLength">Lunghezza di un rapporto di uscita, identificativo compreso.</param>
    /// <param name="InputButtonCaps">Quanti campi pulsante in ingresso.</param>
    /// <param name="OutputButtonCaps">Quanti campi pulsante in uscita.</param>
    public readonly record struct Caps(
        ushort UsagePage, ushort Usage, ushort InputLength, ushort OutputLength,
        ushort InputButtonCaps, ushort OutputButtonCaps);

    /// <summary>Legge le grandezze della collezione.</summary>
    public static Caps? GetCaps(nint preparsed)
    {
        var buffer = Marshal.AllocHGlobal(CapsSize);
        try
        {
            if (HidP_GetCaps(preparsed, buffer) != StatusSuccess) return null;

            return new Caps(
                UsagePage: U16(buffer, 2),
                Usage: U16(buffer, 0),
                InputLength: U16(buffer, 4),
                OutputLength: U16(buffer, 6),
                InputButtonCaps: U16(buffer, 46),
                OutputButtonCaps: U16(buffer, 52));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Legge i campi pulsante di un tipo di rapporto.</summary>
    public static IReadOnlyList<ButtonField> GetButtonFields(int reportType, ushort count, nint preparsed)
    {
        if (count == 0) return [];

        var buffer = Marshal.AllocHGlobal(ButtonCapsSize * count);
        try
        {
            var length = count;
            if (HidP_GetButtonCaps(reportType, buffer, ref length, preparsed) != StatusSuccess) return [];

            var campi = new List<ButtonField>(length);

            for (var i = 0; i < length; i++)
            {
                var voce = buffer + (i * ButtonCapsSize);

                // HIDP_BUTTON_CAPS: UsagePage a 0, ReportID a 2, IsRange a 12,
                // IsAbsolute a 15; l'unione Range/NotRange comincia a 56, con
                // UsageMin (o Usage) a 56 e UsageMax a 58.
                var isRange = Marshal.ReadByte(voce, 12) != 0;
                var primo = U16(voce, 56);

                campi.Add(new ButtonField(
                    UsagePage: U16(voce, 0),
                    ReportId: Marshal.ReadByte(voce, 2),
                    UsageMin: primo,
                    UsageMax: isRange ? U16(voce, 58) : primo,
                    IsAbsolute: Marshal.ReadByte(voce, 15) != 0));
            }

            return campi;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Percorsi delle interfacce HID presenti adesso.</summary>
    public static IReadOnlyList<string> PresentHidInterfaces()
    {
        HidD_GetHidGuid(out var guid);

        // L'elenco puo' crescere fra la richiesta della dimensione e quella del
        // contenuto, se un dispositivo arriva proprio in quel momento: si riprova.
        for (var tentativo = 0; tentativo < 3; tentativo++)
        {
            if (CM_Get_Device_Interface_List_Size(out var length, ref guid, null, InterfaceListPresent) != ConfigRetSuccess)
            {
                return [];
            }

            if (length <= 1) return [];

            var buffer = new char[length];
            var esito = CM_Get_Device_Interface_List(ref guid, null, buffer, length, InterfaceListPresent);

            if (esito == ConfigRetBufferSmall) continue;
            if (esito != ConfigRetSuccess) return [];

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        return [];
    }

    private static ushort U16(nint ptr, int offset) => unchecked((ushort)Marshal.ReadInt16(ptr, offset));

    // DllImport e non LibraryImport, come il resto dei P/Invoke del prodotto.

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    // CharSet.Unicode non e' un dettaglio: la stringa del produttore e' in UTF-16, e
    // con il marshaling predefinito ogni carattere arrivava spezzato in due, «S e n n».
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetProductString(SafeFileHandle device, [Out] char[] buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(nint preparsedData, nint capabilities);

    [DllImport("hid.dll")]
    public static extern int HidP_GetButtonCaps(int reportType, nint buttonCaps, ref ushort length, nint preparsedData);

    [DllImport("hid.dll")]
    public static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, nint preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(
        int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usageList,
        ref uint usageLength, nint preparsedData, nint report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_SetUsages(
        int reportType, ushort usagePage, ushort linkCollection, [In, Out] ushort[] usageList,
        ref uint usageLength, nint preparsedData, nint report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_InitializeReportForID(
        int reportType, byte reportId, nint preparsedData, nint report, uint reportLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle file, nint buffer, uint toRead, nint read, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle file, nint buffer, uint toWrite, nint written, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(SafeFileHandle file, nint overlapped, out uint transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle file, nint overlapped);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    public static extern int CM_Get_Device_Interface_List_Size(
        out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    public static extern int CM_Get_Device_Interface_List(
        ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    /// <summary>Richiamo della notifica di collegamento, su un thread del sistema.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int NotifyCallback(nint notify, nint context, int action, nint eventData, int eventDataSize);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Register_Notification(
        nint filter, nint context, NotifyCallback callback, out nint notifyContext);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Unregister_Notification(nint notifyContext);

    /// <summary>HIDD_ATTRIBUTES.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }
}
