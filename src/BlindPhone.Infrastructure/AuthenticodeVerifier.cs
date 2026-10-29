using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Verifica la firma Authenticode di un file e l'identità di chi l'ha firmato.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché serve anche avendo la firma applicativa.</b>
/// <see cref="UpdateSignature"/> chiude l'attacco — un pacchetto non firmato da
/// BlindPhone non viene installato — ma lo chiude <i>dentro</i> BlindPhone. All'ultimo
/// passo c'è la richiesta di consenso di Windows, e quella mostra «Editore
/// sconosciuto» sia per il pacchetto vero sia per uno contraffatto: l'operatore, che
/// in quel momento sta decidendo se dare i privilegi di amministratore, non ha modo
/// di distinguerli. Con la firma Authenticode il nome dell'editore compare
/// verificato, e SmartScreen smette di avvisare.
/// </para>
/// <para>
/// <b>Il confronto non è sul nome ma sull'impronta.</b> «Firmato da qualcuno» è un
/// controllo che passa anche con un certificato comprato da chiunque: quello che si
/// verifica è che il certificato sia <b>quello</b>, con l'impronta SHA-256 dichiarata
/// in <see cref="ExpectedPublisherThumbprints"/>.
/// </para>
/// <para>
/// <b>Si attiva quando il certificato esiste.</b> Finché
/// <see cref="ExpectedPublisherThumbprints"/> è vuoto il controllo non viene imposto,
/// perché imporlo oggi — con un prodotto che non ha ancora un certificato di firma
/// del codice — bloccherebbe ogni aggiornamento senza aggiungere sicurezza a quella
/// che la firma applicativa già dà. Appena il certificato c'è si riempie quella
/// costante, e da quel momento i due controlli sono entrambi obbligatori.
/// </para>
/// </remarks>
public static class AuthenticodeVerifier
{
    /// <summary>
    /// Impronte SHA-256 dei certificati con cui i pacchetti di BlindPhone vengono
    /// firmati, in esadecimale e senza separatori.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Vuoto significa «nessun certificato di firma del codice, ancora».</b> Appena
    /// ce n'è uno va aggiunta qui la sua impronta, che si legge così:
    /// </para>
    /// <code>
    /// certutil -hashfile certificato.cer SHA256
    /// </code>
    /// <para>
    /// L'elenco ne accetta più di una perché un rinnovo cambia l'impronta: durante il
    /// passaggio stanno dentro sia il certificato uscente sia quello entrante, e il
    /// vecchio si toglie quando non ci sono più pacchetti in circolazione firmati con
    /// quello.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> ExpectedPublisherThumbprints = [];

    /// <summary>Se questa copia del programma sa quale editore attendersi.</summary>
    public static bool IsConfigured => ExpectedPublisherThumbprints.Count > 0;

    private static readonly Guid WintrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x00000080;

    /// <summary>
    /// Vero se il file ha una firma Authenticode valida e il certificato
    /// dell'editore ha una delle impronte attese.
    /// </summary>
    /// <param name="path">Percorso del file da verificare.</param>
    /// <param name="expectedSha256Thumbprints">
    /// Impronte SHA-256 ammesse. Un elenco vuoto fa fallire la verifica: chi non sa
    /// quale editore attendersi non ha niente da confrontare, e deve decidere
    /// <b>prima</b> se il controllo si applica — vedi <see cref="IsConfigured"/>.
    /// </param>
    /// <param name="reason">Perché la verifica non è passata, quando non passa.</param>
    public static bool IsSignedBy(
        string path,
        IReadOnlyCollection<string> expectedSha256Thumbprints,
        out string reason)
    {
        if (expectedSha256Thumbprints.Count == 0)
        {
            reason = "nessuna impronta di editore attesa: il controllo Authenticode non e' configurato";
            return false;
        }

        var stato = VerifyTrust(path);

        if (stato != 0)
        {
            reason = $"firma Authenticode assente o non valida (stato 0x{stato:X8})";
            return false;
        }

        string impronta;

        try
        {
            // Non c'e' un sostituto gestito per leggere il certificato di un file
            // firmato: X509CertificateLoader legge i byte di un certificato, non li
            // estrae da un PE o da un MSI. Si prende da qui e si carica con l'API
            // nuova, cosi' l'unica chiamata obsoleta e' quella che non si puo'
            // evitare.
#pragma warning disable SYSLIB0057
            var firmatario = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057

            using var certificato = X509CertificateLoader.LoadCertificate(firmatario.GetRawCertData());
            impronta = Convert.ToHexString(certificato.GetCertHash(HashAlgorithmName.SHA256));
        }
        catch (CryptographicException ex)
        {
            reason = $"certificato dell'editore non leggibile ({ex.Message})";
            return false;
        }

        if (!expectedSha256Thumbprints.Contains(impronta, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"il pacchetto e' firmato da un editore diverso da quello atteso (impronta {impronta})";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Chiede a Windows se la firma del file è valida.
    /// </summary>
    /// <returns>Zero se la firma è valida, altrimenti il codice di stato di WinVerifyTrust.</returns>
    /// <remarks>
    /// <para>
    /// La revoca si controlla sull'intera catena <b>escluso il certificato radice</b>:
    /// la radice è fidata per inclusione nell'archivio del sistema, non per un elenco
    /// di revoca, e chiederlo produrrebbe soltanto un giro di rete che non decide
    /// niente.
    /// </para>
    /// <para>
    /// Nessuna finestra: <c>WTD_UI_NONE</c>. La domanda all'operatore la fa già la
    /// richiesta di consenso di Windows all'avvio dell'installazione, e una seconda
    /// finestra in mezzo a questa verifica sarebbe una finestra che chiede di
    /// approvare ciò che si sta appunto controllando.
    /// </para>
    /// </remarks>
    private static int VerifyTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());

        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeWholeChain,
                dwUnionChoice = WtdChoiceFile,
                pFile = pFile,
                dwStateAction = WtdStateActionIgnore,
                dwProvFlags = WtdRevocationCheckChainExcludeRoot,
            };

            var azione = WintrustActionGenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref azione, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    // DllImport e non LibraryImport, come il resto dei P/Invoke del prodotto: la
    // struttura passata per riferimento contiene stringhe e handle, e il generatore
    // non la marshalla senza riscriverla a mano.
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
