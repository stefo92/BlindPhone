using BlindPhone.Core.Speech;

namespace BlindPhone.Core.Services;

/// <summary>
/// Modalità con cui il tasto unico di trasferimento distingue fra cieco e con
/// annuncio.
/// </summary>
/// <remarks>
/// Il capitolato richiede una sola combinazione per entrambe le modalità:
/// <see cref="ProgressiveDoubleTap"/> è la risposta a quel requisito ed è il valore
/// predefinito. Le altre modalità esistono perché operatori diversi arrivano da
/// abitudini diverse, e imporre un modello a chi ne ha già uno consolidato costa
/// più errori di quanti ne eviti.
/// </remarks>
public enum TransferInteractionMode
{
    /// <summary>
    /// Progressiva con doppia pressione: una pressione avvia la consultazione, due
    /// pressioni rapide eseguono il trasferimento cieco. È la modalità richiesta dal
    /// capitolato.
    /// </summary>
    ProgressiveDoubleTap,

    /// <summary>
    /// Progressiva pura: il tasto avvia sempre la consultazione. Completando mentre
    /// la destinazione squilla si ottiene di fatto un trasferimento semi-cieco.
    /// Nessuna doppia pressione, nessuna attesa.
    /// </summary>
    ProgressiveOnly,

    /// <summary>
    /// Con scelta esplicita: il tasto apre un elenco di due voci navigabile con le
    /// frecce. Più lenta, ma preferita da chi inizia.
    /// </summary>
    ExplicitChoice,
}

/// <summary>Parametri della procedura di trasferimento.</summary>
public sealed record TransferOptions
{
    /// <summary>Modalità di interazione del tasto unico.</summary>
    public TransferInteractionMode Mode { get; init; } = TransferInteractionMode.ProgressiveDoubleTap;

    /// <summary>
    /// Finestra entro cui due pressioni valgono come doppia pressione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Questo valore governa un compromesso reale, che vale la pena esplicitare. In
    /// modalità a doppia pressione, alla prima pressione il sistema <b>non può
    /// sapere</b> se ne seguirà una seconda, quindi attende la scadenza di questa
    /// finestra prima di avviare la consultazione. Il risultato è che il
    /// trasferimento con annuncio, che è il caso più frequente, paga qui una
    /// latenza pari a questo valore.
    /// </para>
    /// <para>
    /// L'alternativa scartata era avviare subito la consultazione e convertirla in
    /// trasferimento cieco se arrivava la seconda pressione: nessuna latenza, ma il
    /// telefono del collega squilla per un attimo e viene chiuso, e la sequenza
    /// CANCEL seguita da REFER apre condizioni di corsa con il centralino. Fra una
    /// latenza di 400 millisecondi e un comportamento non deterministico su una
    /// funzione critica, si è scelta la latenza.
    /// </para>
    /// <para>
    /// Chi non usa mai il trasferimento cieco può passare a
    /// <see cref="TransferInteractionMode.ProgressiveOnly"/> ed eliminare l'attesa.
    /// </para>
    /// </remarks>
    public TimeSpan DoubleTapWindow { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Tempo oltre il quale una consultazione senza risposta viene annullata e il
    /// chiamante originale ripreso.
    /// </summary>
    public TimeSpan ConsultationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Se richiedere una conferma esplicita prima di trasferire verso un numero
    /// esterno.
    /// </summary>
    /// <remarks>
    /// Protezione contro la composizione errata e contro l'uso improprio del
    /// centralino verso numerazioni a valore aggiunto.
    /// </remarks>
    public bool ConfirmExternalDestinations { get; init; } = true;

    /// <summary>
    /// Se avvisare quando la destinazione risulta occupata, in non disturbare o non
    /// registrata.
    /// </summary>
    public bool WarnOnUnavailableDestination { get; init; } = true;

    /// <summary>
    /// Lunghezza massima di un numero perché sia considerato un interno.
    /// </summary>
    public int MaxInternalNumberLength { get; init; } = 5;

    /// <summary>Nome pronunciabile del tasto di trasferimento configurato.</summary>
    /// <remarks>
    /// <para>
    /// Compare negli annunci ("F9 per completare"). Se l'operatore rimappa il tasto
    /// e questo testo non lo segue, gli annunci diventano istruzioni sbagliate: è
    /// aggiornato dal servizio delle scorciatoie a ogni cambio di configurazione.
    /// </para>
    /// <para>
    /// È l'unica proprietà modificabile dopo la costruzione, e la ragione è quella
    /// appena detta: il profilo dei tasti si cambia a caldo, mentre queste opzioni
    /// vengono composte all'avvio. Un'istruzione vocale che nomina un tasto non più
    /// assegnato è peggio di nessuna istruzione, perché manda a premere qualcosa che
    /// non fa nulla proprio mentre un chiamante è in attesa.
    /// </para>
    /// </remarks>
    public string TransferKeyName { get; set; } = "F9";

    /// <summary>Come leggere i numeri negli annunci del trasferimento.</summary>
    /// <remarks>
    /// Serve alla rilettura della destinazione, che è una verifica e non una
    /// descrizione: l'operatore deve poter confrontare cifra per cifra quello che ha
    /// digitato con quello che il chiamante gli ha dettato. Segue la preferenza
    /// impostata nella scheda Accessibilità, così la lettura è la stessa in tutto il
    /// prodotto.
    /// </remarks>
    public DigitGrouping DigitGrouping { get; init; } = DigitGrouping.Single;

    /// <summary>Impostazioni predefinite.</summary>
    public static TransferOptions Default { get; } = new();
}
