using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Canale con cui un secondo avvio consegna un link di chiamata alla copia di
/// BlindPhone già in esecuzione.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché un canale e non il messaggio di finestra.</b> Quando l'operatore clicca
/// un link <c>tel:</c> in una pagina o in un gestionale, Windows avvia
/// <c>BlindPhone.App.exe</c> con il link sulla riga di comando, e quasi sempre
/// BlindPhone è già acceso. Il secondo avvio deve passare il link alla prima copia e
/// uscire. Il messaggio registrato di <see cref="SingleInstanceGuard"/> basta per dire
/// «fatti vedere», ma non porta testo: <c>WM_COPYDATA</c> lo porterebbe, ma va
/// mandato a una finestra precisa e chi lo riceve non sa chi l'ha mandato. Una pipe
/// con nome porta il testo e lascia decidere a Windows chi può scriverci.
/// </para>
/// <para>
/// <b>Una pipe per sessione, e solo per lo stesso utente.</b> Il nome porta il numero
/// della sessione di Windows, come il mutex dell'istanza unica è locale alla sessione:
/// su un terminal server ogni operatore ha la sua copia di BlindPhone, e il link
/// cliccato da uno non deve finire nel campo di composizione dell'altro.
/// <see cref="PipeOptions.CurrentUserOnly"/> fa il resto, da entrambe le parti: la pipe
/// accetta solo processi dello stesso utente, e chi scrive verifica che a riceverla
/// sia un processo dello stesso utente, non uno che si è preso il nome per primo.
/// </para>
/// <para>
/// <b>Quello che passa è solo testo da verificare.</b> Il canale consegna il link così
/// com'era sulla riga di comando, e l'interpretazione la fa la copia attiva con
/// <see cref="CallLinkParser"/>: è il solo punto in cui si decide che cosa è un
/// numero, e il solo che può dire all'operatore perché un link è stato scartato.
/// </para>
/// </remarks>
public sealed class CallLinkChannel : IDisposable
{
    /// <summary>
    /// Radice del nome della pipe. Il numero di versione ha lo stesso scopo di quello
    /// del mutex: cambiare il protocollo senza che due versioni si parlino.
    /// </summary>
    public const string PipeNamePrefix = "BlindPhone.LinkChiamata.v1";

    /// <summary>
    /// Byte al massimo per messaggio: il link più lungo che il parser esamina, più un
    /// carattere che gli permette di accorgersi che era più lungo, nel caso peggiore
    /// di quattro byte per carattere.
    /// </summary>
    private const int MaxBytes = (CallLinkParser.MaxLength + 1) * 4;

    /// <summary>
    /// Tempo concesso a chi si collega per scrivere il link.
    /// </summary>
    /// <remarks>
    /// La pipe ha un solo posto: un processo che si collega e poi tace la terrebbe
    /// occupata per sempre, e i link successivi non arriverebbero più.
    /// </remarks>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly Action<string> _onLink;
    private readonly ILogger? _log;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private CallLinkChannel(string pipeName, Action<string> onLink, ILogger? log)
    {
        _pipeName = pipeName;
        _onLink = onLink;
        _log = log;
    }

    /// <summary>Nome della pipe per la sessione di Windows corrente.</summary>
    public static string PipeNameForCurrentSession()
    {
        using var processo = Process.GetCurrentProcess();
        return $"{PipeNamePrefix}.{processo.SessionId}";
    }

    /// <summary>
    /// Comincia ad ascoltare i link mandati dai secondi avvii.
    /// </summary>
    /// <param name="onLink">
    /// Chiamata per ogni link ricevuto, <b>da un thread di sfondo</b>: chi la fornisce
    /// deve riportarsi sul thread dell'interfaccia.
    /// </param>
    /// <param name="log">Registro diagnostico.</param>
    /// <param name="pipeName">Nome della pipe; quello della sessione se assente.</param>
    /// <returns>Il canale in ascolto. Va smaltito alla chiusura.</returns>
    public static CallLinkChannel Listen(Action<string> onLink, ILogger? log = null, string? pipeName = null)
    {
        var canale = new CallLinkChannel(pipeName ?? PipeNameForCurrentSession(), onLink, log);
        canale._loop = Task.Run(() => canale.ListenLoopAsync(canale._stop.Token));
        return canale;
    }

    /// <summary>
    /// Consegna un link alla copia in esecuzione.
    /// </summary>
    /// <param name="link">Link come è arrivato sulla riga di comando.</param>
    /// <param name="timeout">Quanto aspettare che la copia attiva risponda.</param>
    /// <param name="pipeName">Nome della pipe; quello della sessione se assente.</param>
    /// <returns><c>true</c> se il link è stato consegnato.</returns>
    /// <remarks>
    /// <para>
    /// <b>Prima di scrivere, cede il primo piano.</b> Chi è stato avviato dal clic
    /// dell'operatore ha il diritto di portare una finestra davanti; la copia attiva,
    /// che riceve il link dalla rete di un'altra applicazione, no. Con
    /// <c>AllowSetForegroundWindow</c> il diritto passa al processo che sta dall'altra
    /// parte della pipe, ed è la via prevista da Windows: senza, la finestra
    /// comparirebbe davanti ma i tasti — Invio compreso — resterebbero al browser.
    /// </para>
    /// <para>
    /// Un link più lungo di quanto il parser accetti si tronca a un carattere oltre il
    /// limite: arriva comunque, e il parser lo rifiuta dicendo perché, invece di
    /// sparire senza spiegazione.
    /// </para>
    /// </remarks>
    public static bool TrySend(string link, TimeSpan timeout, string? pipeName = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName ?? PipeNameForCurrentSession(),
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);

            client.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));

            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid))
            {
                AllowSetForegroundWindow(pid);
            }

            var testo = link.Length > CallLinkParser.MaxLength ? link[..(CallLinkParser.MaxLength + 1)] : link;
            var byteDaScrivere = Encoding.UTF8.GetBytes(testo);

            client.Write(byteDaScrivere, 0, byteDaScrivere.Length);
            client.Flush();
            client.WaitForPipeDrain();

            return true;
        }
        catch (Exception)
        {
            // Nessuna copia in ascolto, una copia di una versione che il canale non lo
            // ha, oppure una pipe dello stesso nome che appartiene a un altro utente.
            // In tutti i casi chi chiama ripiega sul messaggio di finestra.
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;

        _stop.Cancel();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // In chiusura non c'e' nessuno a cui dirlo.
        }

        _stop.Dispose();
    }

    private async Task ListenLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;

            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex)
            {
                // Il nome e' gia' preso. Con il mutex dell'istanza unica non dovrebbe
                // succedere; se succede, i link portano comunque la finestra davanti
                // con il messaggio registrato, e la riga di registro spiega perche' il
                // numero non arriva.
                _log?.LogWarning(ex, "Canale dei link di chiamata non aperto: i link non arriveranno al campo di composizione");
                return;
            }

            await using (server.ConfigureAwait(false))
            {
                try
                {
                    await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log?.LogDebug(ex, "Collegamento al canale dei link non riuscito");
                    continue;
                }

                var link = await ReadAsync(server, stop).ConfigureAwait(false);
                if (link is null) continue;

                try
                {
                    _onLink(link);
                }
                catch (Exception ex)
                {
                    // Un difetto nel gestore non deve chiudere il canale: il link
                    // successivo deve poter arrivare.
                    _log?.LogError(ex, "Gestione del link di chiamata ricevuto non riuscita");
                }
            }
        }
    }

    /// <summary>Legge il link, entro il limite di dimensione e di tempo.</summary>
    /// <returns>Il testo ricevuto, oppure <c>null</c> se va scartato.</returns>
    private async Task<string?> ReadAsync(NamedPipeServerStream server, CancellationToken stop)
    {
        using var tempo = CancellationTokenSource.CreateLinkedTokenSource(stop);
        tempo.CancelAfter(ReadTimeout);

        var buffer = new byte[MaxBytes + 1];
        var letti = 0;

        try
        {
            while (letti < buffer.Length)
            {
                var n = await server.ReadAsync(buffer.AsMemory(letti), tempo.Token).ConfigureAwait(false);
                if (n == 0) break;
                letti += n;
            }
        }
        catch (OperationCanceledException)
        {
            _log?.LogWarning("Link di chiamata scartato: chi si era collegato non lo ha mandato in tempo");
            return null;
        }
        catch (IOException ex)
        {
            _log?.LogDebug(ex, "Lettura dal canale dei link interrotta");
            return null;
        }

        if (letti > MaxBytes)
        {
            _log?.LogWarning("Link di chiamata scartato: piu' lungo di {Max} byte", MaxBytes);
            return null;
        }

        if (letti == 0) return null;

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer, 0, letti);
        }
        catch (DecoderFallbackException)
        {
            _log?.LogWarning("Link di chiamata scartato: il testo ricevuto non e' UTF-8 valido");
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
