using System.Runtime.InteropServices;

namespace BlindPhone.App;

/// <summary>
/// Impedisce che di BlindPhone girino due copie contemporaneamente.
/// </summary>
/// <remarks>
/// <para>
/// Da quando la chiusura della finestra ripone il programma nell'area di notifica, il
/// caso è diventato normale: l'operatore non vede più la finestra, riapre il
/// collegamento sul desktop e senza questo controllo parte un secondo processo.
/// </para>
/// <para>
/// <b>Due copie non sono soltanto disordine.</b> Registrano lo <b>stesso interno</b>
/// presso il centralino, e con due registrazioni concorrenti la chiamata entrante
/// arriva a una sola delle due — non sempre la stessa. Si contendono le scorciatoie
/// globali, che Windows concede alla prima che le chiede, per cui i tasti smettono di
/// rispondere nella finestra che l'operatore sta guardando. Scrivono nello stesso file
/// di registro e nella stessa configurazione, e l'ultima che chiude sovrascrive le
/// modifiche dell'altra. Per chi non vede, il sintomo è un telefono che risponde a
/// intermittenza senza alcuna spiegazione.
/// </para>
/// <para>
/// Il meccanismo è in due parti. Un <b>mutex con nome</b> dice se c'è già una copia:
/// il nome non ha prefisso, quindi è locale alla sessione di Windows, ed è la scelta
/// giusta perché su un terminal server due operatori diversi devono poter usare
/// BlindPhone contemporaneamente, ciascuno con il proprio interno. Un <b>messaggio di
/// finestra registrato</b> serve alla seconda copia per chiedere alla prima di farsi
/// vedere prima di togliersi di mezzo: <c>RegisterWindowMessage</c> restituisce lo
/// stesso identificativo in tutti i processi che passano la stessa stringa, ed è il
/// modo previsto da Windows per farsi riconoscere fra processi senza inventare un
/// canale di comunicazione.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// Nome del mutex. Il numero di versione consente di cambiare il protocollo in
    /// futuro senza che una versione vecchia e una nuova si scambino messaggi.
    /// </summary>
    private const string MutexName = "BlindPhone.IstanzaUnica.v1";

    private const int HwndBroadcast = 0xFFFF;

    private readonly Mutex? _mutex;
    private bool _disposed;

    /// <summary>
    /// Verifica se questa è la prima copia e, nel caso, prenota il posto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La sequenza è in tre passi, e il terzo esiste per un caso concreto: chiudere
    /// BlindPhone e riaprirlo subito. Lo smontaggio dei servizi arresta il connettore
    /// del centralino e il motore telefonico, cioè attese di rete che possono durare
    /// un secondo o due, e in quella finestra il posto risulta ancora occupato. Senza
    /// il terzo passo il nuovo avvio uscirebbe in silenzio, e l'operatore vedrebbe un
    /// programma che semplicemente non parte.
    /// </para>
    /// <para>
    /// La segnalazione parte <b>prima</b> dell'attesa, non dopo: nel caso normale — la
    /// copia attiva è viva e riposta nell'area di notifica — la finestra torna davanti
    /// subito, e l'attesa che segue non la vede nessuno perché avviene in un processo
    /// che sta per chiudersi comunque.
    /// </para>
    /// </remarks>
    public SingleInstanceGuard()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName);

            // 1. Tentativo immediato: e' il caso di gran lunga piu' frequente, cioe'
            //    l'avvio normale con nessun'altra copia in giro.
            if (_mutex.WaitOne(TimeSpan.Zero))
            {
                IsFirstInstance = true;
                return;
            }

            // 2. C'e' gia' qualcuno: gli si chiede di mostrarsi.
            SignalExistingInstance();

            // 3. Se entro il tempo di cortesia il posto si libera, quella copia stava
            //    chiudendo: si prende il suo posto e si parte normalmente.
            IsFirstInstance = _mutex.WaitOne(TimeSpan.FromMilliseconds(1500));
        }
        catch (AbandonedMutexException)
        {
            // La copia precedente e' terminata di colpo senza rilasciare il posto.
            // Il mutex e' nostro: si prosegue, ed e' esattamente il comportamento che
            // serve dopo un arresto anomalo.
            IsFirstInstance = true;
        }
        catch (Exception)
        {
            // Se il mutex non si puo' creare — criteri di sicurezza restrittivi, nomi
            // vietati da una policy — si preferisce partire comunque: un posto
            // operatore che non si avvia e' peggio di due copie aperte.
            IsFirstInstance = true;
        }
    }

    /// <summary>Identificativo del messaggio con cui si chiede alla copia attiva di mostrarsi.</summary>
    public static int ShowWindowMessage { get; } = RegisterWindowMessage("BlindPhone.MostraFinestra.v1");

    /// <summary><c>true</c> se questa è la sola copia in esecuzione.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>
    /// Chiede alla copia già in esecuzione di portarsi in primo piano.
    /// </summary>
    /// <remarks>
    /// Il messaggio va a tutte le finestre di primo livello, comprese quelle
    /// invisibili: è ciò che permette di raggiungere una finestra riposta nell'area di
    /// notifica, che è esattamente il caso per cui questo esiste.
    /// </remarks>
    public static void SignalExistingInstance()
    {
        try
        {
            PostMessage(HwndBroadcast, ShowWindowMessage, nint.Zero, nint.Zero);
        }
        catch (Exception)
        {
            // Senza segnalazione la copia attiva resta dov'e'. Non c'e' modo di dirlo
            // all'operatore da qui: questo processo sta gia' uscendo.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (IsFirstInstance) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch (Exception)
        {
            // In chiusura non c'e' piu' nessuno a cui segnalarlo.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, int message, nint wParam, nint lParam);
}
