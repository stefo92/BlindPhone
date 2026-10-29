using System.Security.Cryptography;
using System.Text;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la firma applicativa dei pacchetti di aggiornamento.
/// </summary>
/// <remarks>
/// <para>
/// È il controllo che decide se un file scaricato da un server FTP viene eseguito con
/// i privilegi di amministratore. Prima non esisteva: si confrontava l'impronta
/// SHA-256 dichiarata in un file di testo pubblicato <b>sullo stesso server</b>, che
/// garantisce l'integrità del trasferimento e non la provenienza — chi può sostituire
/// il pacchetto può sostituire anche l'impronta.
/// </para>
/// <para>
/// Quello che va protetto qui è che <see cref="UpdateSignature"/> non abbia nessun
/// modo di dire «sì» per sbaglio. Ogni prova negativa di questa classe è un modo in
/// cui un pacchetto contraffatto potrebbe passare, e la risposta deve essere la stessa
/// per tutti: <c>false</c>.
/// </para>
/// <para>
/// I test usano una coppia di chiavi generata al volo. La chiave di rilascio non sta
/// in un file di prova — la sua privata non entra nel repository — e una verifica
/// collaudata su un'altra chiave è comunque la stessa verifica: cambia il valore della
/// costante incorporata, non il codice che la usa.
/// </para>
/// </remarks>
public sealed class FirmaAggiornamentoTests
{
    private static readonly byte[] Pacchetto =
        Encoding.UTF8.GetBytes("Non e' un MSI, ma e' una sequenza di byte firmabile come un MSI.");

    /// <summary>Una chiave usa e getta sulla curva del rilascio.</summary>
    private static ECDsa NuovaChiave() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>La pubblica di una chiave, nella forma in cui si incolla nella costante.</summary>
    private static string Pubblica(ECDsa chiave)
        => Convert.ToBase64String(chiave.ExportSubjectPublicKeyInfo());

    /// <summary>Firma in formato DER, quello che produce openssl.</summary>
    private static byte[] FirmaDer(ECDsa chiave, byte[] contenuto)
        => chiave.SignData(contenuto, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    private static MemoryStream Flusso(byte[] contenuto) => new(contenuto, writable: false);

    [Fact]
    public void UnaFirmaInFormatoDerVieneAccettata()
    {
        // E' il formato che produce "openssl dgst -sha256 -sign", cioe' quello
        // documentato per il rilascio in docs/INSTALLAZIONE.md §10.
        using var chiave = NuovaChiave();
        var pubblica = Pubblica(chiave);

        var firma = FirmaDer(chiave, Pacchetto);

        Assert.True(UpdateSignature.VerifyWith(pubblica, Flusso(Pacchetto), firma));
    }

    [Fact]
    public void UnaFirmaInFormatoP1363VieneAccettata()
    {
        // E' il formato che producono le API .NET. Accettarlo non allenta niente:
        // cambia la lettura dei byte della firma, non la chiave con cui si verifica.
        using var chiave = NuovaChiave();
        var pubblica = Pubblica(chiave);

        var firma = chiave.SignData(
            Pacchetto, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.True(UpdateSignature.VerifyWith(pubblica, Flusso(Pacchetto), firma));
    }

    [Fact]
    public void UnPacchettoCambiatoDiUnByteVieneRifiutato()
    {
        using var chiave = NuovaChiave();
        var pubblica = Pubblica(chiave);

        var firma = FirmaDer(chiave, Pacchetto);

        var manomesso = (byte[])Pacchetto.Clone();
        manomesso[0] ^= 0x01;

        Assert.False(UpdateSignature.VerifyWith(pubblica, Flusso(manomesso), firma));
    }

    [Fact]
    public void UnaFirmaAlterataVieneRifiutata()
    {
        using var chiave = NuovaChiave();
        var pubblica = Pubblica(chiave);

        var firma = FirmaDer(chiave, Pacchetto);

        firma[^1] ^= 0x01;

        Assert.False(UpdateSignature.VerifyWith(pubblica, Flusso(Pacchetto), firma));
    }

    [Fact]
    public void UnaFirmaDiUnAltraChiaveVieneRifiutata()
    {
        // E' il caso che conta davvero: l'attaccante ha il pacchetto, sa come si
        // firma, e ha una chiave sua perche' la nostra privata non e' sul server.
        using var nostra = NuovaChiave();
        using var altra = NuovaChiave();

        Assert.False(UpdateSignature.VerifyWith(Pubblica(nostra), Flusso(Pacchetto), FirmaDer(altra, Pacchetto)));
    }

    [Fact]
    public void SenzaChiaveIncorporataNessunPacchettoVieneAccettato()
    {
        // Il comportamento con la costante PublicKeySpki vuota, che e' lo stato in cui
        // si trova una copia compilata prima del primo rilascio firmato: nessun
        // aggiornamento automatico, non "tutti gli aggiornamenti".
        using var chiave = NuovaChiave();

        var firma = FirmaDer(chiave, Pacchetto);

        Assert.False(UpdateSignature.VerifyWith(null, Flusso(Pacchetto), firma));
        Assert.False(UpdateSignature.VerifyWith(string.Empty, Flusso(Pacchetto), firma));
        Assert.False(UpdateSignature.VerifyWith("   ", Flusso(Pacchetto), firma));
    }

    [Fact]
    public void UnaFirmaAssenteOVuotaVieneRifiutata()
    {
        using var chiave = NuovaChiave();
        var pubblica = Pubblica(chiave);

        Assert.False(UpdateSignature.VerifyWith(pubblica, Flusso(Pacchetto), null));
        Assert.False(UpdateSignature.VerifyWith(pubblica, Flusso(Pacchetto), []));
    }

    [Fact]
    public void UnaFirmaPiuGrandeDelLimiteVieneRifiutataSenzaLeggerla()
    {
        // Il file di firma arriva da un server che potrebbe non essere quello giusto:
        // senza un tetto, "scarica la firma" diventa un modo per far leggere alla
        // postazione un file grande quanto l'attaccante decide.
        using var chiave = NuovaChiave();
        var pubblica = Pubblica(chiave);

        var enorme = new byte[UpdateSignature.MaxSignatureBytes + 1];

        Assert.False(UpdateSignature.VerifyWith(pubblica, Flusso(Pacchetto), enorme));
    }

    [Fact]
    public void UnaChiaveIncorporataIlleggibileNonSiConfondeConUnPacchettoRifiutato()
    {
        // Un errore di rilascio e un attacco non devono dare lo stesso messaggio: chi
        // legge "pacchetto non firmato da BlindPhone" andrebbe a cercare un attacco
        // invece di una costante incollata male.
        using var chiave = NuovaChiave();

        var firma = FirmaDer(chiave, Pacchetto);

        Assert.Throws<InvalidOperationException>(
            () => UpdateSignature.VerifyWith("non-e'-base64-valido!!", Flusso(Pacchetto), firma));

        Assert.Throws<InvalidOperationException>(
            () => UpdateSignature.VerifyWith(Convert.ToBase64String([1, 2, 3]), Flusso(Pacchetto), firma));
    }

    [Fact]
    public void LaFirmaSiVerificaAncheSulFileSuDisco()
    {
        // Il percorso usato da UpdateService dopo lo scaricamento. Il flusso e' la via
        // usata al momento dell'installazione, dove il file e' tenuto aperto perche'
        // nessuno lo sostituisca.
        using var chiave = NuovaChiave();
        var pubblica = Pubblica(chiave);

        var firma = FirmaDer(chiave, Pacchetto);

        var percorso = Path.Combine(Path.GetTempPath(), $"blindphone-firma-{Guid.NewGuid():N}.bin");

        try
        {
            File.WriteAllBytes(percorso, Pacchetto);

            using var apertura = File.OpenRead(percorso);
            Assert.True(UpdateSignature.VerifyWith(pubblica, apertura, firma));
        }
        finally
        {
            File.Delete(percorso);
        }
    }

    [Fact]
    public void IlNomeDelFileDiFirmaSegueQuelloDelPacchetto()
    {
        // Il legame fra pacchetto e firma e' il nome e nient'altro, come per il file
        // delle novita': pubblicare una versione resta copiare dei file in una
        // cartella.
        Assert.Equal(
            "BlindPhone-1.2.0-x64.msi.sig",
            UpdateSignature.SignatureFileNameFor("BlindPhone-1.2.0-x64.msi"));
    }

    [Fact]
    public void UnFlussoNonPosizionabileEUnErroreDiChiChiama()
    {
        // La firma si verifica dall'inizio del file, e su un flusso che non si puo'
        // riportare all'inizio la risposta "false" sarebbe indistinguibile da un
        // pacchetto contraffatto.
        using var chiave = NuovaChiave();
        var pubblica = Pubblica(chiave);

        var firma = FirmaDer(chiave, Pacchetto);

        using var nonPosizionabile = new FlussoSenzaRicerca(Pacchetto);

        Assert.Throws<ArgumentException>(
            () => UpdateSignature.VerifyWith(pubblica, nonPosizionabile, firma));
    }

    [Fact]
    public void SenzaImprontaAttesaIlControlloAuthenticodeNonSiApplica()
    {
        // Finche' non esiste un certificato di firma del codice non c'e' niente da
        // confrontare, e UpdateService salta il controllo: la risposta "false" qui
        // dice "non configurato", non "pacchetto rifiutato". Le due cose le distingue
        // AuthenticodeVerifier.IsConfigured, e confonderle bloccherebbe ogni
        // aggiornamento.
        Assert.False(AuthenticodeVerifier.IsConfigured);

        Assert.False(AuthenticodeVerifier.IsSignedBy("qualunque.msi", [], out var motivo));
        Assert.Contains("non e' configurato", motivo, StringComparison.Ordinal);
    }

    [Fact]
    public void UnFileSenzaFirmaAuthenticodeVieneRifiutato()
    {
        // Esercita la chiamata a WinVerifyTrust per intero. Serve: un errore
        // nell'impaginazione delle strutture passate a Windows non si vede leggendo
        // il codice, e si manifesterebbe come un rifiuto di ogni pacchetto — oppure,
        // peggio, come un blocco del processo.
        var percorso = Path.Combine(Path.GetTempPath(), $"blindphone-nonfirmato-{Guid.NewGuid():N}.msi");

        try
        {
            File.WriteAllBytes(percorso, Pacchetto);

            Assert.False(
                AuthenticodeVerifier.IsSignedBy(percorso, ["0123456789ABCDEF"], out var motivo));

            // Non deve dire "editore diverso": non c'e' nessun editore da confrontare.
            Assert.Contains("Authenticode", motivo, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(percorso);
        }
    }

    /// <summary>Un flusso leggibile che non sa tornare indietro, come quello di una rete.</summary>
    private sealed class FlussoSenzaRicerca(byte[] contenuto) : Stream
    {
        private readonly MemoryStream _interno = new(contenuto, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => _interno.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _interno.Dispose();
            base.Dispose(disposing);
        }
    }
}
