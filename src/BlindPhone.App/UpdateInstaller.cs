using System.Diagnostics;
using System.IO;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Avvia l'installatore di Windows su un pacchetto di aggiornamento verificato.
/// </summary>
/// <remarks>
/// <para>
/// Sta fuori da <see cref="UpdateWindow"/> perché i pacchetti da installare arrivano
/// da due strade: quello appena scaricato con «Scarica e installa ora», e quello
/// scaricato in un turno precedente con «Scarica e installa successivamente». La
/// seconda è la più delicata — il pacchetto ha passato una notte in una cartella che
/// l'utente può scrivere — e deve passare dagli stessi controlli della prima, non da
/// una copia che prima o poi resta indietro.
/// </para>
/// </remarks>
internal static class UpdateInstaller
{
    /// <summary>
    /// Riverifica il pacchetto e avvia l'installatore di Windows.
    /// </summary>
    /// <param name="packagePath">Percorso del pacchetto, con la firma accanto.</param>
    /// <param name="log">Log diagnostico.</param>
    /// <remarks>
    /// <para>
    /// <b>Chi chiama chiude l'applicazione subito dopo</b>, e in quest'ordine: un
    /// pacchetto che deve sostituire i file di un programma in esecuzione chiederebbe
    /// un riavvio della macchina, e su una postazione di servizio quello è un costo che
    /// si può evitare uscendo prima.
    /// </para>
    /// <para>
    /// <b>Fra la verifica e l'avvio non c'è una finestra da sfruttare.</b> Prima c'era:
    /// il pacchetto restava in una cartella su cui l'utente ha pieno controllo senza che
    /// nessuno lo trattenesse, e fra il controllo dell'impronta e l'avvio di
    /// <c>msiexec</c> un altro processo poteva sostituirlo. Quello che partiva era
    /// l'altro, con i privilegi di amministratore. Ora il file viene aperto in modo da
    /// negarne la scrittura e la cancellazione, la firma si riverifica <b>su quel
    /// flusso</b> e il blocco resta fino a quando <c>msiexec</c> non ha preso in mano il
    /// pacchetto.
    /// </para>
    /// <para>
    /// L'installazione <b>non</b> è silenziosa. L'operatore vede la stessa procedura
    /// della prima installazione, con la richiesta di consenso di Windows: è quello che
    /// permette di fermarsi a metà se qualcosa non torna, ed è l'ultima occasione in
    /// cui un pacchetto sbagliato può ancora essere rifiutato.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Il pacchetto non è più quello verificato, oppure la sua firma non si trova:
    /// l'installazione non parte e il motivo va detto all'operatore.
    /// </exception>
    public static void Launch(string packagePath, ILogger log)
    {
        FileStream blocco;

        try
        {
            // La lettura resta concessa: msiexec deve poter aprire il pacchetto. Quello
            // che il blocco nega e' scriverlo e cancellarlo.
            blocco = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Pacchetto {Pacchetto} non apribile al momento dell'installazione", packagePath);

            throw new InvalidOperationException(
                "Il pacchetto scaricato non si trova piu' o non si riesce ad aprire: l'installazione non parte.",
                ex);
        }

        using (blocco)
        {
            var firma = ReadSignature(packagePath, log);

            // Si verificano i byte TENUTI APERTI, non quelli che il percorso
            // restituirebbe riaprendolo: riaprire il file vanificherebbe il blocco,
            // perche' fra le due aperture ci sarebbe di nuovo spazio per una sostituzione.
            if (!UpdateSignature.Verify(blocco, firma))
            {
                log.LogError(
                    "Firma del pacchetto {Pacchetto} non valida al momento dell'installazione: annullata",
                    packagePath);

                throw new InvalidOperationException(
                    "Il pacchetto e' cambiato dopo la verifica: l'installazione e' stata annullata. "
                    + "Riprova, e se il problema resta avvisa chi amministra la postazione.");
            }

            var avvio = new ProcessStartInfo
            {
                // PERCORSO ASSOLUTO, E NON "msiexec.exe".
                //
                // Con il solo nome, la risoluzione passa per le regole di ricerca di
                // Windows: la cartella di lavoro e le voci di PATH vengono guardate, e
                // una cartella scrivibile dall'utente che contenga un file con quel nome
                // vincerebbe sul programma di sistema. Si chiede quello in
                // %SystemRoot%\System32, che e' l'unico che si intende avviare.
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                Arguments = $"/i \"{packagePath}\"",
                UseShellExecute = true,
            };

            log.LogInformation("Avvio dell'installazione di {Pacchetto}", packagePath);

            using var processo = Process.Start(avvio);

            // Si attende che msiexec sia pronto, così il blocco cade quando il pacchetto
            // è già nelle sue mani. Non tutte le configurazioni hanno una finestra da
            // attendere — un'installazione senza interfaccia non ne ha — e un'attesa
            // impossibile non è un guasto: il pacchetto è stato verificato un istante
            // prima e il processo è già partito.
            //
            // L'attesa e' breve di proposito. Blocca il thread dell'interfaccia, cioe'
            // anche la coda degli annunci, e su un posto operatore un programma muto per
            // dieci secondi e' indistinguibile da un programma bloccato. msiexec apre il
            // pacchetto nei primi istanti, molto prima di mostrare la sua prima finestra:
            // cinque secondi sono un tetto, non un'attesa prevista.
            try
            {
                processo?.WaitForInputIdle(5_000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.LogDebug(ex, "Attesa dell'avvio di msiexec non disponibile: si prosegue");
            }
        }
    }

    /// <summary>Legge la firma pubblicata accanto al pacchetto scaricato.</summary>
    /// <exception cref="InvalidOperationException">La firma non è leggibile.</exception>
    private static byte[] ReadSignature(string packagePath, ILogger log)
    {
        var percorso = packagePath + UpdateSignature.SignatureExtension;

        try
        {
            return File.ReadAllBytes(percorso);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Firma {Percorso} non leggibile al momento dell'installazione", percorso);

            throw new InvalidOperationException(
                "La firma del pacchetto non si trova piu': l'installazione non parte. "
                + "Riprova a scaricare l'aggiornamento.",
                ex);
        }
    }
}
