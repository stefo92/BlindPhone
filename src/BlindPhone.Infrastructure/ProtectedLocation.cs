using System.Security.AccessControl;
using System.Security.Principal;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Verifica che un file e la cartella che lo contiene siano modificabili solo da
/// amministratori e da SYSTEM.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cosa serve.</b> Un pacchetto di configurazione distribuito vale per tutti i
/// profili della postazione: cambia l'indirizzo del centralino, il registrar SIP,
/// l'indirizzo a cui vanno i pacchetti di assistenza, e può installare un criterio
/// con password che impedisce all'operatore di rimettere le cose a posto. Un oggetto
/// con quei poteri deve arrivare da chi amministra la macchina, e l'unico modo di
/// saperlo è guardare chi può scriverlo.
/// </para>
/// <para>
/// <b>Perché non basta la cartella giusta.</b> Con i permessi predefiniti di
/// <c>%ProgramData%</c> un utente standard può creare la sottocartella se non esiste
/// — e ne diventa proprietario — oppure scriverci dentro se l'ha creata chi non ha
/// interrotto l'ereditarietà. Su una postazione condivisa fra turni, questo basta a
/// un operatore qualsiasi per distribuire una configurazione a tutti gli altri. La
/// cartella viene protetta dall'installatore, ma questa verifica deve esserci
/// comunque: copre le installazioni già fatte e le cartelle i cui permessi sono
/// stati cambiati dopo.
/// </para>
/// <para>
/// <b>Il proprietario conta quanto la lista dei permessi.</b> Chi possiede un
/// oggetto in Windows può riscriverne i permessi anche quando la lista attuale non
/// glielo concede: un file o una cartella di proprietà di un utente standard è
/// modificabile da quell'utente, qualunque cosa dica la lista nel momento in cui si
/// guarda.
/// </para>
/// <para>
/// <b>In caso di dubbio risponde «non protetto».</b> Un permesso che non si riesce a
/// leggere, una regola scritta in una forma non prevista, una cartella che non
/// esiste: sono tutte situazioni in cui non si può affermare che soltanto un
/// amministratore possa scrivere lì, e la risposta corretta è la stessa che si dà a
/// una cartella aperta.
/// </para>
/// </remarks>
public static class ProtectedLocation
{
    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>
    /// <c>NT SERVICE\TrustedInstaller</c>, l'identità con cui l'installatore di
    /// Windows scrive.
    /// </summary>
    /// <remarks>
    /// Non ha un valore in <see cref="WellKnownSidType"/> e si scrive per esteso. Va
    /// considerata fidata perché è l'identità che compare come proprietaria dei file
    /// scritti da un pacchetto MSI: senza, la cartella creata dal nostro stesso
    /// installatore risulterebbe non protetta.
    /// </remarks>
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly SecurityIdentifier TrustedInstaller = new(TrustedInstallerSid);

    /// <summary>
    /// I diritti che permettono di sostituire il contenuto di un file o di una
    /// cartella, o di cambiarne i permessi per poterlo fare.
    /// </summary>
    /// <remarks>
    /// <c>ChangePermissions</c> e <c>TakeOwnership</c> stanno nell'elenco perché sono
    /// un permesso di scrittura con un passaggio in più: chi può riscrivere la lista
    /// dei permessi si concede tutto il resto da sé.
    /// </remarks>
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData |                        // = CreateFiles
        FileSystemRights.AppendData |                       // = CreateDirectories
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    /// <summary>
    /// I diritti generici, che <see cref="FileSystemRights"/> non nomina.
    /// </summary>
    /// <remarks>
    /// Una regola scritta in SDDL può concedere <c>GA</c> o <c>GW</c>: il nucleo li
    /// traduce nei diritti specifici al momento del controllo d'accesso, ma qui
    /// arrivano come bit grezzi. Senza questo controllo una lista che concede «tutto»
    /// in quella forma risulterebbe innocua.
    /// </remarks>
    private const int GenericWriteBits = 0x50000000; // GENERIC_WRITE | GENERIC_ALL

    private const AccessControlSections Sections =
        AccessControlSections.Owner | AccessControlSections.Access;

    /// <summary>
    /// Se un file è scrivibile soltanto da amministratori, da SYSTEM e
    /// dall'installatore di Windows.
    /// </summary>
    /// <param name="filePath">Percorso del file da verificare.</param>
    /// <param name="reason">
    /// Perché la posizione non è protetta, quando non lo è. Stringa vuota quando lo è.
    /// </param>
    /// <returns><c>true</c> solo se nessun altro può scrivere lì.</returns>
    /// <remarks>
    /// Si guardano quattro cose: il proprietario del file, il proprietario della
    /// cartella, i permessi del file e i permessi della cartella. La cartella conta
    /// quanto il file: chi può creare e cancellare al suo interno sostituisce il file
    /// senza mai averne avuto il permesso di scrittura.
    /// </remarks>
    public static bool IsAdminOnly(string filePath, out string reason)
    {
        try
        {
            var file = new FileInfo(filePath);
            var cartella = file.Directory;

            if (cartella is null)
            {
                reason = "il percorso non ha una cartella che lo contenga";
                return false;
            }

            var permessiFile = new FileSecurity(file.FullName, Sections);
            var permessiCartella = new DirectorySecurity(cartella.FullName, Sections);

            if (!HasTrustedOwner(permessiFile, "file", out reason)) return false;
            if (!HasTrustedOwner(permessiCartella, "cartella", out reason)) return false;
            if (!OnlyTrustedCanWrite(permessiFile, "il file", out reason)) return false;
            if (!OnlyTrustedCanWrite(permessiCartella, "la cartella", out reason)) return false;

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or PrivilegeNotHeldException
                                      or ArgumentException
                                      or NotSupportedException
                                      or PlatformNotSupportedException)
        {
            // Non aver potuto leggere i permessi non e' una prova che siano stretti.
            reason = $"i permessi non sono leggibili ({ex.Message})";
            return false;
        }
    }

    /// <summary>Se un identificativo è fra quelli che possono scrivere legittimamente.</summary>
    private static bool IsTrusted(IdentityReference? sid) =>
        sid is not null
        && (sid.Equals(Administrators) || sid.Equals(LocalSystem) || sid.Equals(TrustedInstaller));

    private static bool HasTrustedOwner(FileSystemSecurity acl, string what, out string reason)
    {
        var proprietario = acl.GetOwner(typeof(SecurityIdentifier));

        if (IsTrusted(proprietario))
        {
            reason = string.Empty;
            return true;
        }

        reason = $"il proprietario del {what} non e' un amministratore ({Describe(proprietario)})";
        return false;
    }

    private static bool OnlyTrustedCanWrite(FileSystemSecurity acl, string what, out string reason)
    {
        var regole = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule regola in regole)
        {
            // Le regole di negazione si saltano: una negazione non concede niente, e
            // interpretare l'intreccio fra permessi e negazioni qui significherebbe
            // riscrivere il controllo d'accesso di Windows. Il risultato e' che una
            // lista che concede a tutti e poi nega a qualcuno viene dichiarata non
            // protetta: la risposta prudente, e la cartella va comunque sistemata.
            if (regola.AccessControlType != AccessControlType.Allow) continue;

            // LE REGOLE DI SOLA EREDITA' NON RIGUARDANO QUESTO OGGETTO.
            //
            // Una regola marcata InheritOnly descrive che permessi avranno gli oggetti
            // creati dentro la cartella, non chi puo' toccare la cartella adesso.
            // Contarla come un permesso di scrittura farebbe risultare non protetta
            // anche C:\Windows\System32, che porta una regola di quel tipo per
            // CREATOR OWNER: un controllo che rifiuta tutto non protegge niente,
            // perche' chi lo subisce lo disattiva.
            //
            // Cio' che quelle regole producono davvero — un file scrivibile da chi lo
            // ha creato — viene colto dal controllo sui permessi del file, che si fa
            // sul file vero e non su quello che la cartella promette.
            if ((regola.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;

            var diritti = regola.FileSystemRights;
            var scrittura = (diritti & WriteRights) != 0 || ((int)diritti & GenericWriteBits) != 0;

            if (!scrittura) continue;
            if (IsTrusted(regola.IdentityReference)) continue;

            reason = $"{what} e' modificabile da {Describe(regola.IdentityReference)}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Nome leggibile di un identificativo, con il SID come riserva.
    /// </summary>
    /// <remarks>
    /// Il nome serve a chi legge il registro: «modificabile da BUILTIN\Users» si
    /// capisce, «modificabile da S-1-5-32-545» va cercato. La traduzione può non
    /// riuscire — un account di un dominio non raggiungibile, un SID orfano — e in
    /// quel caso il SID è comunque meglio di niente.
    /// </remarks>
    private static string Describe(IdentityReference? sid)
    {
        if (sid is null) return "identita' sconosciuta";

        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException)
        {
            // IdentityNotMappedException quando il SID non ha un nome; le altre
            // quando la traduzione non puo' nemmeno essere tentata.
            return sid.Value;
        }
    }

    /// <summary>
    /// Se un percorso indica un file locale, e non una cartella di rete.
    /// </summary>
    /// <param name="path">Percorso da esaminare. Vuoto o assente conta come locale.</param>
    /// <returns><c>false</c> per i percorsi di rete e per quelli non interpretabili.</returns>
    /// <remarks>
    /// <para>
    /// <b>Perché un percorso di rete in una configurazione è un problema.</b> Un
    /// valore come <c>\\host\share\suoneria.wav</c> non è soltanto un file che
    /// potrebbe non esserci: aprirlo fa partire un'autenticazione verso
    /// <c>host</c>, che riceve così un tentativo di accesso con l'identità
    /// dell'operatore. Chi controlla quell'host lo raccoglie senza dover fare altro,
    /// e gli è bastato scrivere una riga in un file di configurazione.
    /// </para>
    /// <para>
    /// Si riconoscono quattro travestimenti della stessa cosa: <c>\\host\share</c>,
    /// la forma con barre normali, i percorsi con prefisso di dispositivo
    /// (<c>\\?\UNC\...</c>) e le lettere di unità che sono cartelle di rete mappate.
    /// Un indirizzo con uno schema — <c>http://</c>, <c>file://host/</c> — non è un
    /// percorso locale e viene rifiutato allo stesso modo.
    /// </para>
    /// </remarks>
    public static bool IsLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        var pulito = path.Trim();

        if (pulito.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (pulito.StartsWith("//", StringComparison.Ordinal)) return false;

        if (Uri.TryCreate(pulito, UriKind.Absolute, out var uri))
        {
            // Un indirizzo con uno schema non e' un percorso locale, con un'unica
            // eccezione: file:// senza host indica un file su questo disco. Da li' si
            // continua sul percorso che l'indirizzo indica, non sulla sua forma
            // scritta, perche' le due non sono interscambiabili.
            if (!uri.IsFile || uri.IsUnc) return false;

            pulito = uri.LocalPath;
        }

        try
        {
            var completo = Path.GetFullPath(pulito);

            // Un percorso relativo puo' risolversi su una cartella di rete: si
            // guarda la forma risolta, non quella scritta.
            if (completo.StartsWith(@"\\", StringComparison.Ordinal)) return false;

            var radice = Path.GetPathRoot(completo);
            if (string.IsNullOrEmpty(radice)) return false;

            return new DriveInfo(radice).DriveType != DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException
                                      or IOException
                                      or System.Security.SecurityException)
        {
            // Un percorso che non si riesce nemmeno a normalizzare non e' un percorso
            // locale di cui ci si possa fidare.
            return false;
        }
    }
}
