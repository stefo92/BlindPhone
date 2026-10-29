using System.Security.Cryptography;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Verifica la firma applicativa dei pacchetti di aggiornamento.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché non basta l'impronta SHA-256 nelle note.</b> Quell'impronta sta in un
/// file di testo scaricato dallo stesso server, sullo stesso canale del pacchetto:
/// chi può sostituire il pacchetto può sostituire anche l'impronta, e il confronto
/// torna. Garantisce l'integrità del trasferimento — il file arrivato a metà, quello
/// sovrascritto durante il caricamento — e <b>non</b> la provenienza. Quello che
/// arriva da qui verrà eseguito con i privilegi di amministratore, e per quello
/// serve una firma che il server non sia in grado di produrre.
/// </para>
/// <para>
/// <b>Come funziona.</b> Accanto a <c>BlindPhone-1.2.0-x64.msi</c> viene pubblicato
/// <c>BlindPhone-1.2.0-x64.msi.sig</c>, che è la firma ECDSA P-256 del pacchetto
/// calcolata sulla macchina di build con una chiave privata che non entra nel
/// repository. La chiave <b>pubblica</b> è incorporata qui sotto, dentro il binario:
/// per far accettare un pacchetto contraffatto non basta più controllare il server,
/// serve la chiave privata.
/// </para>
/// <para>
/// <b>Non dipende da un'autorità di certificazione</b>, e questo è il motivo per cui
/// esiste accanto alla firma Authenticode e non al suo posto: funziona anche prima
/// che un certificato di firma del codice sia disponibile, che è la situazione in cui
/// questo prodotto si trova oggi. Vedi <see cref="AuthenticodeVerifier"/> per il
/// controllo complementare, quello che rende verificabile l'editore anche a UAC e a
/// SmartScreen.
/// </para>
/// <para>
/// <b>Non ha un modo di dire «sì» per sbaglio.</b> Senza chiave configurata, senza
/// firma, con una firma di un'altra chiave o con un solo byte diverso nel pacchetto,
/// la risposta è <c>false</c>. L'unico esito positivo passa da una verifica
/// crittografica riuscita.
/// </para>
/// </remarks>
public static class UpdateSignature
{
    /// <summary>
    /// Chiave pubblica di firma degli aggiornamenti, in formato
    /// SubjectPublicKeyInfo codificato Base64.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>QUESTA È LA RIGA DA RIEMPIRE AL PRIMO RILASCIO FIRMATO.</b> Finché resta
    /// vuota nessun aggiornamento automatico viene installato: non è un ripiego
    /// prudente ma il comportamento corretto, perché l'alternativa — accettare un
    /// pacchetto non firmato — è esattamente la falla che questa classe chiude. Su una
    /// postazione in quello stato l'aggiornamento si fa a mano, scaricando il
    /// pacchetto per una via di cui si conosca la provenienza.
    /// </para>
    /// <para>
    /// La coppia di chiavi si genera <b>una volta sola</b>, e la privata non entra nel
    /// repository né nel pacchetto:
    /// </para>
    /// <code>
    /// openssl ecparam -name prime256v1 -genkey -noout -out blindphone-update.key
    /// openssl ec -in blindphone-update.key -pubout -outform DER | openssl base64 -A
    /// </code>
    /// <para>
    /// L'uscita del secondo comando è il valore da incollare qui. La procedura
    /// completa, con la firma a ogni rilascio, sta in <c>docs/INSTALLAZIONE.md</c>
    /// §10.
    /// </para>
    /// <para>
    /// <b>Sostituirla è un cambio di fiducia, non una correzione.</b> Da quel momento i
    /// pacchetti firmati con la chiave precedente non vengono più accettati, quindi la
    /// sostituzione va fatta sapendo che le postazioni ancora alla versione vecchia
    /// vanno aggiornate a mano una volta.
    /// </para>
    /// </remarks>
    public const string PublicKeySpki = "";

    /// <summary>Estensione del file che porta la firma di un pacchetto.</summary>
    public const string SignatureExtension = ".sig";

    /// <summary>
    /// Dimensione massima accettata per un file di firma.
    /// </summary>
    /// <remarks>
    /// Una firma ECDSA P-256 in forma DER sta in settantadue byte. Il limite serve
    /// perché il file arriva da un server che potrebbe non essere quello giusto: senza
    /// un tetto, «scarica la firma» è un modo per far leggere alla postazione un file
    /// grande quanto l'attaccante decide.
    /// </remarks>
    public const int MaxSignatureBytes = 1024;

    /// <summary>
    /// I formati in cui una firma ECDSA può presentarsi, nell'ordine in cui vengono
    /// provati.
    /// </summary>
    /// <remarks>
    /// <c>openssl dgst -sign</c> produce la forma DER (una sequenza ASN.1 di r e s),
    /// che è quella documentata per il rilascio. La forma IEEE P1363 — r e s
    /// concatenati senza involucro — è quella prodotta dalle API .NET, e si accetta
    /// perché una firma generata con uno strumento invece dell'altro non è un
    /// pacchetto meno autentico. Provarle entrambe non allenta nulla: cambia la
    /// lettura dei byte della firma, non la chiave con cui si verifica.
    /// </remarks>
    private static readonly DSASignatureFormat[] Formats =
    [
        DSASignatureFormat.Rfc3279DerSequence,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation,
    ];

    /// <summary>
    /// Se questa copia del programma porta una chiave pubblica con cui verificare gli
    /// aggiornamenti.
    /// </summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKeySpki);

    /// <summary>Nome del file di firma che accompagna un pacchetto.</summary>
    /// <param name="installerFileName">Nome del pacchetto.</param>
    public static string SignatureFileNameFor(string installerFileName)
        => installerFileName + SignatureExtension;

    /// <summary>
    /// Verifica la firma di un pacchetto su disco.
    /// </summary>
    /// <param name="packagePath">Percorso del pacchetto.</param>
    /// <param name="signature">Contenuto del file <c>.sig</c>.</param>
    /// <returns><c>true</c> solo se la firma è valida per la chiave incorporata.</returns>
    /// <exception cref="InvalidOperationException">
    /// La chiave incorporata è presente ma non è una chiave pubblica leggibile: è un
    /// errore di rilascio, e va distinto da un pacchetto rifiutato.
    /// </exception>
    public static bool Verify(string packagePath, byte[]? signature)
    {
        using var flusso = File.OpenRead(packagePath);
        return Verify(flusso, signature);
    }

    /// <summary>
    /// Verifica la firma di un pacchetto già aperto.
    /// </summary>
    /// <param name="package">
    /// Flusso posizionabile sul contenuto del pacchetto. Viene riportato all'inizio
    /// prima di ogni tentativo.
    /// </param>
    /// <param name="signature">Contenuto del file <c>.sig</c>.</param>
    /// <returns><c>true</c> solo se la firma è valida per la chiave incorporata.</returns>
    /// <remarks>
    /// La versione che prende un flusso esiste per il momento dell'installazione: lì il pacchetto
    /// è tenuto aperto per impedire che qualcuno lo sostituisca, e riverificarlo
    /// riaprendo il percorso vanificherebbe il blocco. Si verifica <b>quei</b> byte,
    /// non quelli che il percorso restituirà la prossima volta.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// La chiave incorporata è presente ma non è leggibile.
    /// </exception>
    public static bool Verify(Stream package, byte[]? signature)
        => VerifyWith(PublicKeySpki, package, signature);

    /// <summary>
    /// Verifica una firma contro una chiave pubblica indicata.
    /// </summary>
    /// <param name="publicKeySpkiBase64">
    /// Chiave pubblica in formato SubjectPublicKeyInfo, codificata Base64.
    /// </param>
    /// <param name="package">Flusso posizionabile sul contenuto firmato.</param>
    /// <param name="signature">Firma da verificare.</param>
    /// <returns><c>true</c> solo se la firma è valida per quella chiave.</returns>
    /// <remarks>
    /// Esiste per i test, che devono poter generare una coppia di chiavi propria: la
    /// chiave di rilascio non sta in un file di prova, e una verifica collaudata su
    /// una chiave diversa da quella usata in esercizio è comunque la stessa verifica.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="publicKeySpkiBase64"/> non è una chiave pubblica leggibile.
    /// </exception>
    public static bool VerifyWith(string? publicKeySpkiBase64, Stream package, byte[]? signature)
    {
        ArgumentNullException.ThrowIfNull(package);

        // Nessuna chiave, nessuna firma, una firma assurdamente grande: tre modi di
        // non essere un pacchetto firmato, e la risposta e' la stessa.
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64)) return false;
        if (signature is null || signature.Length == 0) return false;
        if (signature.Length > MaxSignatureBytes) return false;

        if (!package.CanSeek)
        {
            throw new ArgumentException(
                "Il flusso del pacchetto deve essere posizionabile: la firma si verifica dall'inizio del file",
                nameof(package));
        }

        using var ecdsa = ECDsa.Create();

        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64.Trim()), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // UNA CHIAVE ROTTA NON E' UN PACCHETTO RIFIUTATO.
            //
            // Restituire false qui farebbe apparire «pacchetto non firmato da
            // BlindPhone» su ogni aggiornamento, e chi legge quel messaggio
            // andrebbe a cercare un attacco invece di un errore di rilascio. Il
            // guasto e' nella costante incorporata, e va detto come tale.
            throw new InvalidOperationException(
                "La chiave pubblica di firma degli aggiornamenti incorporata in questa copia di BlindPhone "
                + "non e' leggibile. Non e' un problema del pacchetto scaricato: va corretta nel rilascio.",
                ex);
        }

        foreach (var formato in Formats)
        {
            package.Position = 0;

            try
            {
                if (ecdsa.VerifyData(package, signature, HashAlgorithmName.SHA256, formato)) return true;
            }
            catch (CryptographicException)
            {
                // La firma non e' leggibile in questo formato: si prova l'altro. Se
                // nessuno dei due la interpreta, non e' una firma valida.
            }
        }

        return false;
    }
}
