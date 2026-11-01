using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Un server PostHog finto, in HTTPS vero, sull'interfaccia di loopback.
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTPS e non HTTP</b>, perché il client rifiuta qualunque altro schema e una prova
/// che lo aggirasse proverebbe un client diverso da quello che gira sulle postazioni. Il
/// certificato è autofirmato e nasce con il server; il gestore che le prove passano al
/// client si fida di lui e di nessun altro.
/// </para>
/// <para>
/// Conta le connessioni accettate, non solo le richieste: «nessuna chiamata di rete» vuol
/// dire che il client non ha nemmeno aperto il collegamento, e una richiesta contata
/// dopo la stretta di mano TLS non lo direbbe.
/// </para>
/// </remarks>
internal sealed class ServerPostHogFinto : IAsyncDisposable
{
    private readonly TcpListener _ascolto;
    private readonly X509Certificate2 _certificato;
    private readonly CancellationTokenSource _fine = new();
    private readonly Task _accettazione;
    private int _connessioni;

    public ServerPostHogFinto()
    {
        _certificato = CreaCertificato();
        _ascolto = new TcpListener(IPAddress.Loopback, 0);
        _ascolto.Start();
        Porta = ((IPEndPoint)_ascolto.LocalEndpoint).Port;
        _accettazione = AccettaAsync();
    }

    /// <summary>Porta su cui ascolta.</summary>
    public int Porta { get; }

    /// <summary>Indirizzo da scrivere nella configurazione.</summary>
    public string Indirizzo => $"https://127.0.0.1:{Porta}/";

    /// <summary>Connessioni accettate.</summary>
    public int Connessioni => Volatile.Read(ref _connessioni);

    /// <summary>Richieste ricevute: metodo, percorso e corpo.</summary>
    public ConcurrentQueue<(string Metodo, string Percorso, string Corpo)> Richieste { get; } = new();

    /// <summary>Codice con cui rispondere.</summary>
    public HttpStatusCode Risposta { get; set; } = HttpStatusCode.OK;

    /// <summary>Un gestore che si fida del certificato di questo server, e di nessun altro.</summary>
    public HttpMessageHandler GestoreCheSiFida()
    {
        var impronta = _certificato.GetCertHashString();

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificato, _, _) =>
                    certificato is not null
                    && string.Equals(certificato.GetCertHashString(), impronta, StringComparison.OrdinalIgnoreCase),
            },
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _fine.CancelAsync();
        _ascolto.Stop();

        try
        {
            await _accettazione;
        }
        catch (Exception)
        {
            // Il ciclo di accettazione esce con l'eccezione dell'ascolto fermato.
        }

        _certificato.Dispose();
        _fine.Dispose();
    }

    private async Task AccettaAsync()
    {
        while (!_fine.IsCancellationRequested)
        {
            TcpClient cliente;

            try
            {
                cliente = await _ascolto.AcceptTcpClientAsync(_fine.Token);
            }
            catch (Exception)
            {
                return;
            }

            Interlocked.Increment(ref _connessioni);
            _ = Task.Run(() => ServiAsync(cliente));
        }
    }

    private async Task ServiAsync(TcpClient cliente)
    {
        using var _ = cliente;

        try
        {
            await using var tls = new SslStream(cliente.GetStream());
            await tls.AuthenticateAsServerAsync(_certificato);

            // Piu' richieste sulla stessa connessione: il client tiene vivo il
            // collegamento fra un lotto e l'altro.
            while (await LeggiRichiestaAsync(tls) is { } richiesta)
            {
                Richieste.Enqueue(richiesta);

                var corpo = Risposta == HttpStatusCode.OK ? """{"status":"Ok"}""" : string.Empty;
                var testa = $"HTTP/1.1 {(int)Risposta} {Risposta}\r\n"
                            + "Content-Type: application/json\r\n"
                            + $"Content-Length: {Encoding.UTF8.GetByteCount(corpo)}\r\n\r\n";

                await tls.WriteAsync(Encoding.UTF8.GetBytes(testa + corpo));
                await tls.FlushAsync();
            }
        }
        catch (Exception)
        {
            // Una connessione chiusa a meta' dal client e' normale: per esempio quando
            // la prova annulla un invio.
        }
    }

    private static async Task<(string, string, string)?> LeggiRichiestaAsync(Stream flusso)
    {
        var intestazione = new StringBuilder();
        var unByte = new byte[1];

        while (!intestazione.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await flusso.ReadAsync(unByte) == 0) return null;
            intestazione.Append((char)unByte[0]);
        }

        var righe = intestazione.ToString().Split("\r\n");
        var primaRiga = righe[0].Split(' ');

        var lunghezza = righe
            .Where(r => r.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(r => int.Parse(r["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

        var corpo = new byte[lunghezza];
        await flusso.ReadExactlyAsync(corpo);

        return (primaRiga[0], primaRiga[1], Encoding.UTF8.GetString(corpo));
    }

    /// <summary>
    /// Certificato autofirmato per localhost, nato e morto con il server.
    /// </summary>
    /// <remarks>
    /// Si esporta e si rilegge come PFX perché Windows non accetta per un server TLS una
    /// chiave che vive solo in memoria: con il certificato appena creato la stretta di
    /// mano fallisce con «le credenziali fornite non sono state riconosciute».
    /// </remarks>
    private static X509Certificate2 CreaCertificato()
    {
        using var chiave = RSA.Create(2048);

        var richiesta = new CertificateRequest("CN=localhost", chiave, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        richiesta.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        richiesta.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var nomi = new SubjectAlternativeNameBuilder();
        nomi.AddDnsName("localhost");
        nomi.AddIpAddress(IPAddress.Loopback);
        richiesta.CertificateExtensions.Add(nomi.Build());

        using var appenaCreato = richiesta.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        return X509CertificateLoader.LoadPkcs12(appenaCreato.Export(X509ContentType.Pfx), password: null);
    }
}
