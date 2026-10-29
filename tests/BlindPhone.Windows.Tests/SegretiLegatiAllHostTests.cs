using System.Security.Cryptography;
using System.Text;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica che una credenziale non parta verso un server diverso da quello per cui
/// è stata inserita.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il caso che questi test proteggono.</b> DPAPI risponde a «chi può leggere il
/// segreto sul disco». Fino alla 1.0.16 la seconda domanda non era coperta: <b>a chi
/// il programma lo manda.</b> Il Client Secret dell'API stava sotto una chiave fissa
/// e la password SIP sotto una chiave che nominava l'account, quindi bastava cambiare
/// l'indirizzo del centralino — in <c>settings.json</c>, che l'operatore può aprire
/// con un editor di testo, oppure con un pacchetto di configurazione distribuito —
/// perché la credenziale partisse verso l'indirizzo nuovo senza che nessuno venisse
/// avvisato.
/// </para>
/// <para>
/// Nel caso Yeastar quel segreto viaggia nel corpo JSON di <c>get_token</c>: il nuovo
/// host lo riceve per intero, e da quel momento ha le credenziali dell'API del
/// centralino vero.
/// </para>
/// <para>
/// La prova che conta è <see cref="UnSegretoNonPartePerUnHostDiverso"/>. Le altre
/// coprono i modi in cui la protezione potrebbe rompere qualcosa che funzionava: una
/// postazione aggiornata dalla 1.0.16 non deve chiedere di reinserire niente, e un
/// indirizzo scritto in una forma diversa ma equivalente non deve contare come un
/// cambio di server.
/// </para>
/// </remarks>
public sealed class SegretiLegatiAllHostTests : IDisposable
{
    private readonly string _cartella = Path.Combine(
        Path.GetTempPath(), "BlindPhone.Test", $"segreti-{Guid.NewGuid():N}");

    public SegretiLegatiAllHostTests() => Directory.CreateDirectory(_cartella);

    private SecretStore Deposito() => new(_cartella);

    [Fact]
    public void UnSegretoTornaPerIlProprioHost()
    {
        var deposito = Deposito();

        deposito.SetForHost(SecretStore.PbxClientSecretKey, "segreto-vero", "pbx.ente.it");

        Assert.Equal(
            "segreto-vero",
            deposito.GetForHost(SecretStore.PbxClientSecretKey, "pbx.ente.it", "il Client Secret"));
    }

    [Fact]
    public void UnSegretoNonPartePerUnHostDiverso()
    {
        var deposito = Deposito();

        deposito.SetForHost(SecretStore.PbxClientSecretKey, "segreto-vero", "pbx.ente.it");

        // L'host e' stato cambiato da qualcun altro: un file di testo, un pacchetto
        // distribuito. Il segreto non esce.
        Assert.Equal(
            string.Empty,
            deposito.GetForHost(SecretStore.PbxClientSecretKey, "pbx.attaccante.it", "il Client Secret"));

        // E l'operatore deve poterlo sentire: senza la frase, il sintomo e' soltanto
        // "collegamento al centralino interrotto", che e' lo stesso di un centralino
        // spento e di una rete guasta.
        var negato = Assert.Single(deposito.Rejected);

        Assert.Equal(SecretStore.PbxClientSecretKey, negato.Key);
        Assert.Equal("pbx.ente.it", negato.BoundHost);
        Assert.Equal("pbx.attaccante.it", negato.RequestedHost);
        Assert.Contains("pbx.attaccante.it", negato.Speech, StringComparison.Ordinal);
        Assert.Contains("il Client Secret", negato.Speech, StringComparison.Ordinal);
    }

    [Fact]
    public void UnSegretoNegatoNonVieneCancellato()
    {
        // Rimettere l'indirizzo di prima deve far tornare le cose a posto: cancellare
        // il segreto renderebbe definitivo un cambio fatto per sbaglio.
        var deposito = Deposito();

        deposito.SetForHost(SecretStore.PbxClientSecretKey, "segreto-vero", "pbx.ente.it");

        Assert.Equal(
            string.Empty,
            deposito.GetForHost(SecretStore.PbxClientSecretKey, "altro.host", "il Client Secret"));

        Assert.Equal(
            "segreto-vero",
            deposito.GetForHost(SecretStore.PbxClientSecretKey, "pbx.ente.it", "il Client Secret"));
    }

    [Fact]
    public void UnaStessaChiaveNegataDueVolteSiAnnunciaUnaVolta()
    {
        // I segreti si leggono piu' volte durante l'avvio, una per servizio.
        // L'operatore deve sentirlo una volta, non una per servizio.
        var deposito = Deposito();

        deposito.SetForHost(SecretStore.SipPasswordKey, "password", "registrar.ente.it");

        deposito.GetForHost(SecretStore.SipPasswordKey, "altro", "la password SIP");
        deposito.GetForHost(SecretStore.SipPasswordKey, "altro", "la password SIP");

        Assert.Single(deposito.Rejected);
    }

    [Fact]
    public void UnIndirizzoCompletoSiRiduceAlSuoHost()
    {
        // Cambiare cartella sul server degli aggiornamenti non cambia a chi si manda
        // la password, e non deve costare un reinserimento.
        var deposito = Deposito();

        deposito.SetForHost("update.password", "password", "ftps://aggiornamenti.ente.it/blindphone/");

        Assert.Equal(
            "password",
            deposito.GetForHost("update.password", "ftps://aggiornamenti.ente.it/altra-cartella/", "la password"));

        // Un server diverso invece lo e'.
        Assert.Equal(
            string.Empty,
            deposito.GetForHost("update.password", "ftps://aggiornamenti.altrove.it/blindphone/", "la password"));
    }

    [Fact]
    public void IlConfrontoNonBadaAMaiuscoleEPuntoFinale()
    {
        var deposito = Deposito();

        deposito.SetForHost(SecretStore.PbxClientSecretKey, "segreto", "PBX.Ente.IT.");

        Assert.Equal(
            "segreto",
            deposito.GetForHost(SecretStore.PbxClientSecretKey, "pbx.ente.it", "il Client Secret"));
    }

    [Fact]
    public void UnaVoceSenzaHostVieneLegataAlPrimoUtilizzo()
    {
        // E' la migrazione di una postazione aggiornata dalla 1.0.16: la credenziale
        // c'e' ma non dice a chi appartiene. La si lega all'host configurato in quel
        // momento, cosi' la postazione continua a funzionare senza reinserire niente.
        var deposito = Deposito();

        deposito.Set(SecretStore.PbxClientSecretKey, "segreto-della-1.0.16");

        Assert.Null(deposito.BoundHostOf(SecretStore.PbxClientSecretKey));

        Assert.Equal(
            "segreto-della-1.0.16",
            deposito.GetForHost(SecretStore.PbxClientSecretKey, "pbx.ente.it", "il Client Secret"));

        Assert.Equal("pbx.ente.it", deposito.BoundHostOf(SecretStore.PbxClientSecretKey));

        // Da qui in avanti vale la regola nuova.
        Assert.Equal(
            string.Empty,
            deposito.GetForHost(SecretStore.PbxClientSecretKey, "altro.host", "il Client Secret"));
    }

    [Fact]
    public void UnDepositoScrittoDallaVersionePrecedenteSiLegge()
    {
        // Il formato di prima era {"chiave": "valore"}, senza l'host. Se non si
        // leggesse, l'aggiornamento costerebbe il reinserimento di tutte le
        // credenziali su ogni postazione — e una postazione senza password SIP e' un
        // posto operatore muto.
        var entropia = Encoding.UTF8.GetBytes("BlindPhone.SecretStore.v1");
        var vecchio = Encoding.UTF8.GetBytes("""{"sip.password":"password-storica"}""");

        File.WriteAllBytes(
            Path.Combine(_cartella, "secrets.dat"),
            ProtectedData.Protect(vecchio, entropia, DataProtectionScope.CurrentUser));

        var deposito = Deposito();

        Assert.Equal("password-storica", deposito.Get(SecretStore.SipPasswordKey));

        Assert.Equal(
            "password-storica",
            deposito.GetForHost(SecretStore.SipPasswordKey, "registrar.ente.it", "la password SIP"));
    }

    [Fact]
    public void IlLegameSopravviveAlRiavvio()
    {
        // Il deposito e' un file: se il legame vivesse solo in memoria, basterebbe
        // riavviare BlindPhone per riaprire la falla.
        Deposito().SetForHost(SecretStore.PbxClientSecretKey, "segreto", "pbx.ente.it");

        var dopoRiavvio = Deposito();

        Assert.Equal(
            "segreto",
            dopoRiavvio.GetForHost(SecretStore.PbxClientSecretKey, "pbx.ente.it", "il Client Secret"));

        Assert.Equal(
            string.Empty,
            dopoRiavvio.GetForHost(SecretStore.PbxClientSecretKey, "altro.host", "il Client Secret"));
    }

    [Fact]
    public void UnValoreVuotoRimuoveLaCredenzialeDelProprioServer()
    {
        // "Nessuna password" e "password vuota per questo server" si comporterebbero
        // allo stesso modo, e la prima e' quella che si intende.
        var deposito = Deposito();

        deposito.SetForHost(SecretStore.PbxClientSecretKey, "segreto", "pbx.ente.it");
        deposito.SetForHost(SecretStore.PbxClientSecretKey, string.Empty, "pbx.ente.it");

        Assert.Equal(string.Empty, deposito.Get(SecretStore.PbxClientSecretKey));
        Assert.Null(deposito.BoundHostOf(SecretStore.PbxClientSecretKey));
    }

    [Fact]
    public void UnValoreVuotoNonCancellaLaCredenzialeDiUnAltroServer()
    {
        // IL CASO CHE SI ROMPEREBBE DA SOLO.
        //
        // Se qualcuno sposta l'host nella configurazione, il pannello mostra il campo
        // della password vuoto — la credenziale vale per un altro server. Salvando la
        // scheda senza toccare quel campo, un "valore vuoto cancella" distruggerebbe
        // la credenziale e con essa la via d'uscita, perche' rimettere l'indirizzo di
        // prima non la farebbe piu' tornare.
        var deposito = Deposito();

        deposito.SetForHost(SecretStore.PbxClientSecretKey, "segreto", "pbx.ente.it");
        deposito.SetForHost(SecretStore.PbxClientSecretKey, string.Empty, "pbx.attaccante.it");

        Assert.Equal("pbx.ente.it", deposito.BoundHostOf(SecretStore.PbxClientSecretKey));

        Assert.Equal(
            "segreto",
            deposito.GetForHost(SecretStore.PbxClientSecretKey, "pbx.ente.it", "il Client Secret"));
    }

    [Fact]
    public void ReinserireLaCredenzialeToglieLAvviso()
    {
        var deposito = Deposito();

        deposito.SetForHost(SecretStore.PbxClientSecretKey, "segreto", "pbx.ente.it");
        deposito.GetForHost(SecretStore.PbxClientSecretKey, "pbx.nuovo.it", "il Client Secret");

        Assert.Single(deposito.Rejected);

        // Il pannello scrive la credenziale per il server nuovo: la richiesta di
        // reinserirla non ha piu' ragione di esistere.
        deposito.SetForHost(SecretStore.PbxClientSecretKey, "segreto-nuovo", "pbx.nuovo.it");

        Assert.Empty(deposito.Rejected);

        Assert.Equal(
            "segreto-nuovo",
            deposito.GetForHost(SecretStore.PbxClientSecretKey, "pbx.nuovo.it", "il Client Secret"));
    }

    [Fact]
    public void LaChiaveDegliArchiviNonEUnaCredenzialeDiRete()
    {
        // Non viene inviata a nessuno: legarla a un host non proteggerebbe niente e
        // renderebbe illeggibili rubrica e storico al primo cambio di centralino.
        var deposito = Deposito();

        var chiave = deposito.GetOrCreateDatabaseKey();

        Assert.False(string.IsNullOrWhiteSpace(chiave));
        Assert.Equal(chiave, Deposito().GetOrCreateDatabaseKey());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cartella)) Directory.Delete(_cartella, recursive: true);
        }
        catch (IOException)
        {
            // La pulizia del profilo ci arriva comunque.
        }
    }
}
