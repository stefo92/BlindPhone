using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Una cartella dei download finta, in HTTPS vero, sull'interfaccia di loopback.
/// </summary>
/// <remarks>
/// <para>
/// Risponde come il portale di BlindPhone: <c>/download/?per=x64</c> con un rimando al
/// pacchetto più recente, i file pubblicati con il loro contenuto, tutto il resto con
/// 404. Le risposte le decide la prova, una per percorso, e ogni richiesta ricevuta
/// resta scritta con le sue intestazioni.
/// </para>
/// <para>
/// Una richiesta per connessione, chiusa dopo la risposta: il client la riapre, e la
/// prova non deve occuparsi di come il client tiene vivi i collegamenti.
/// </para>
/// </remarks>
internal sealed class ServerAggiornamentiFinto : IAsyncDisposable
{
    private readonly TcpListener _ascolto;
    private readonly X509Certificate2 _certificato;
    private readonly CancellationTokenSource _fine = new();
    private readonly Task _accettazione;

    public ServerAggiornamentiFinto()
    {
        _certificato = ServerPostHogFinto.CreaCertificato();
        _ascolto = new TcpListener(IPAddress.Loopback, 0);
        _ascolto.Start();
        Porta = ((IPEndPoint)_ascolto.LocalEndpoint).Port;
        _accettazione = AccettaAsync();
    }

    /// <summary>Una risposta preparata dalla prova.</summary>
    /// <param name="Codice">Codice HTTP.</param>
    /// <param name="Rimando">Valore dell'intestazione <c>Location</c>, se c'è.</param>
    /// <param name="Corpo">Contenuto da mandare.</param>
    public sealed record Risposta(int Codice, string? Rimando = null, byte[]? Corpo = null);

    /// <summary>Una richiesta ricevuta.</summary>
    /// <param name="Percorso">Il percorso chiesto, con la parte dopo il punto interrogativo.</param>
    /// <param name="Intestazioni">Le intestazioni, per nome senza distinzione di maiuscole.</param>
    public sealed record Richiesta(string Percorso, IReadOnlyDictionary<string, string> Intestazioni);

    /// <summary>Porta su cui ascolta.</summary>
    public int Porta { get; }

    /// <summary>Indirizzo della cartella, da scrivere nelle impostazioni.</summary>
    public string Cartella => $"https://127.0.0.1:{Porta}/download/";

    /// <summary>Le risposte, per percorso chiesto.</summary>
    public ConcurrentDictionary<string, Risposta> Risposte { get; } = new(StringComparer.Ordinal);

    /// <summary>Le richieste ricevute, nell'ordine di arrivo.</summary>
    public ConcurrentQueue<Richiesta> Richieste { get; } = new();

    /// <summary>I soli percorsi chiesti.</summary>
    public IEnumerable<string> Percorsi => Richieste.Select(r => r.Percorso);

    /// <summary>La cartella risponde a <c>?per=</c> con un rimando.</summary>
    public void Rimanda(string processore, string destinazione)
        => Risposte[$"/download/?per={processore}"] = new Risposta(302, destinazione);

    /// <summary>Un file pubblicato nella cartella.</summary>
    public void Pubblica(string nome, byte[] contenuto)
        => Risposte[$"/download/{nome}"] = new Risposta(200, Corpo: contenuto);

    /// <summary>Un file di testo pubblicato nella cartella.</summary>
    public void Pubblica(string nome, string testo) => Pubblica(nome, Encoding.UTF8.GetBytes(testo));

    /// <summary>Un gestore che si fida del certificato di questo server, e di nessun altro.</summary>
    /// <remarks>
    /// Senza rimandi automatici, come il gestore vero del servizio: se li seguisse, la
    /// prova sul rimando non vedrebbe mai la risposta che il servizio legge.
    /// </remarks>
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

            if (await LeggiRichiestaAsync(tls) is not { } richiesta) return;
            Richieste.Enqueue(richiesta);

            var risposta = Risposte.TryGetValue(richiesta.Percorso, out var preparata)
                ? preparata
                : new Risposta(404, Corpo: Encoding.UTF8.GetBytes("<html>Non trovato</html>"));

            var corpo = risposta.Corpo ?? [];
            var testa = new StringBuilder()
                .Append($"HTTP/1.1 {risposta.Codice} Risposta\r\n")
                .Append("Content-Type: application/octet-stream\r\n")
                .Append($"Content-Length: {corpo.Length}\r\n")
                .Append("Connection: close\r\n");

            if (risposta.Rimando is { } rimando) testa.Append($"Location: {rimando}\r\n");
            testa.Append("\r\n");

            await tls.WriteAsync(Encoding.ASCII.GetBytes(testa.ToString()));
            await tls.WriteAsync(corpo);
            await tls.FlushAsync();
        }
        catch (Exception)
        {
            // Una connessione chiusa a meta' dal client e' normale: per esempio quando
            // il servizio rifiuta un file troppo grande e smette di leggerlo.
        }
    }

    private static async Task<Richiesta?> LeggiRichiestaAsync(Stream flusso)
    {
        var intestazione = new StringBuilder();
        var unByte = new byte[1];

        while (!intestazione.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await flusso.ReadAsync(unByte) == 0) return null;
            intestazione.Append((char)unByte[0]);
        }

        var righe = intestazione.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var percorso = righe[0].Split(' ')[1];

        var intestazioni = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var riga in righe.Skip(1))
        {
            var due = riga.IndexOf(':');
            if (due > 0) intestazioni[riga[..due].Trim()] = riga[(due + 1)..].Trim();
        }

        return new Richiesta(percorso, intestazioni);
    }
}
