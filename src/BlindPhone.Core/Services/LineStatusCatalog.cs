using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;

namespace BlindPhone.Core.Services;

/// <summary>Stato della linea, come lo mostra la fascia in cima alla scheda della chiamata.</summary>
public enum LineStatus
{
    /// <summary>Nessuna chiamata: la postazione è pronta.</summary>
    Free,

    /// <summary>Una chiamata entrante sta squillando.</summary>
    Ringing,

    /// <summary>L'operatore è in linea con qualcuno, o lo sta chiamando.</summary>
    Talking,

    /// <summary>Nessuno in linea, ma qualcuno aspetta in attesa.</summary>
    OnHold,

    /// <summary>La postazione è in pausa e non riceve chiamate.</summary>
    OutOfService,
}

/// <summary>
/// Fotografia dello stato da cui dipende la fascia.
/// </summary>
/// <param name="HasRingingCall">C'è una chiamata entrante che squilla.</param>
/// <param name="IsTalking">C'è qualcuno in linea che sente l'operatore, conferenza compresa.</param>
/// <param name="HasOutgoingCall">
/// C'è una chiamata uscente che squilla dall'altra parte: la linea è impegnata anche se
/// nessuno ha ancora risposto.
/// </param>
/// <param name="WaitingCount">
/// Quante persone aspettano: le chiamate in attesa più il chiamante parcheggiato dentro
/// un trasferimento. È <c>CallSessionManager.WaitingCalls</c> e non <c>HeldCalls</c>,
/// per la ragione scritta lì: a chi aspetta la differenza non arriva.
/// </param>
/// <param name="IsServicePaused">La postazione è in pausa.</param>
public readonly record struct LineStatusContext(
    bool HasRingingCall = false,
    bool IsTalking = false,
    bool HasOutgoingCall = false,
    int WaitingCount = 0,
    bool IsServicePaused = false);

/// <summary>Come si presenta uno stato nella fascia.</summary>
/// <param name="Status">Lo stato.</param>
/// <param name="Word">La parola in maiuscolo, che porta l'informazione da sola.</param>
/// <param name="Icon">
/// Pittogramma di Segoe Fluent Icons che la accompagna. Decorativo: chi lo mostra gli
/// toglie il nome accessibile, come ai pittogrammi dei pulsanti.
/// </param>
/// <param name="AccessibleName">
/// Quello che legge lo screen reader passando sulla parola con il cursore virtuale:
/// la parola in minuscolo, perché alcune sintesi compitano le maiuscole lettera per
/// lettera e «IN» diventerebbe «i enne».
/// </param>
/// <param name="Flashes">
/// Se la fascia lampeggia. Solo dove il lampeggio c'era già e per la stessa ragione:
/// una chiamata che squilla ha una scadenza, una postazione fuori servizio cambia il
/// significato di tutto il resto della finestra.
/// </param>
public readonly record struct LineStatusFace(
    LineStatus Status,
    string Word,
    string Icon,
    string AccessibleName,
    bool Flashes);

/// <summary>
/// Decide che cosa dice la fascia di stato della scheda della chiamata.
/// </summary>
/// <remarks>
/// <para>
/// <b>La fascia sostituisce i tre avvisi a tutta larghezza</b> — fuori servizio,
/// chiamata entrante, conversazione in corso — che comparivano in cima alla finestra e
/// spingevano giù tutto il resto a ogni chiamata. Per chi lavora con l'ingrandimento
/// era la cosa peggiore che la finestra potesse fare: il punto che stava guardando se
/// ne andava proprio quando succedeva qualcosa. La fascia c'è sempre e ha sempre la
/// stessa altezza; cambiano parola, colore e cronometro.
/// </para>
/// <para>
/// <b>Uno stato solo alla volta, con una precedenza dichiarata.</b> Squilla prima di
/// tutto, perché è l'unica cosa con una scadenza; poi la conversazione, perché è quella
/// che l'operatore ha in mano; poi l'attesa, perché qualcuno sta contando i minuti; poi
/// la pausa, che riguarda le chiamate che verranno e non quelle che ci sono; libero per
/// ultimo. Le chiamate in corso durante una pausa restano tali: mettersi in pausa vuol
/// dire non prenderne di nuove, non far cadere quella in mano.
/// </para>
/// <para>
/// <b>Le cinque parole sono cinque e non di più.</b> La chiamata uscente che squilla
/// ancora dall'altra parte conta come «in linea»: la linea è impegnata e il nome
/// sotto è quello di chi si sta chiamando. «Libero» sarebbe falso, «squilla» si
/// confonderebbe con una chiamata da prendere. La didascalia sopra il nome dice che non
/// ha ancora risposto, e il cronometro parte solo quando risponde.
/// </para>
/// <para>
/// Vive nel core per la stessa ragione di <see cref="CallActionCatalog"/>: è una regola
/// sul comportamento visibile e si collauda stato per stato, non a occhio. Colori e
/// lampeggio veri restano nella finestra, che è la sola a sapere che cos'è un pennello.
/// </para>
/// </remarks>
public static class LineStatusCatalog
{
    /// <summary>Lo stato da mostrare, secondo la precedenza.</summary>
    /// <param name="context">Fotografia della postazione.</param>
    public static LineStatus Resolve(LineStatusContext context)
    {
        if (context.HasRingingCall) return LineStatus.Ringing;
        if (context.IsTalking || context.HasOutgoingCall) return LineStatus.Talking;
        if (context.WaitingCount > 0) return LineStatus.OnHold;

        return context.IsServicePaused ? LineStatus.OutOfService : LineStatus.Free;
    }

    /// <summary>Parola, pittogramma e lampeggio di uno stato.</summary>
    /// <param name="status">Lo stato.</param>
    /// <remarks>
    /// I pittogrammi sono verificati su Segoe Fluent Icons e su Segoe MDL2 Assets, il
    /// ripiego di Windows 10: il segno di spunta, la campana, la cornetta, le due barre
    /// della pausa e il cerchio barrato. Il cerchio barrato è <c>F140</c> e non
    /// <c>E733</c>, che nel font di Windows 10 non esiste.
    /// </remarks>
    public static LineStatusFace Face(LineStatus status) => status switch
    {
        LineStatus.Ringing => new(status, "SQUILLA", "", "Stato della linea: squilla", Flashes: true),
        LineStatus.Talking => new(status, "IN LINEA", "", "Stato della linea: in linea", Flashes: false),
        LineStatus.OnHold => new(status, "IN ATTESA", "", "Stato della linea: in attesa", Flashes: false),
        LineStatus.OutOfService => new(status, "FUORI SERVIZIO", "", "Stato della linea: fuori servizio", Flashes: true),
        _ => new(LineStatus.Free, "LIBERO", "", "Stato della linea: libero", Flashes: false),
    };

    /// <summary>
    /// Il cronometro della fascia: da quanto dura lo stato mostrato.
    /// </summary>
    /// <param name="status">Lo stato mostrato.</param>
    /// <param name="now">Istante corrente.</param>
    /// <param name="ringing">Chiamata che squilla, se c'è.</param>
    /// <param name="active">Chiamata in conversazione, se c'è.</param>
    /// <param name="conferenced">Partecipanti alla conferenza a tre, se aperta.</param>
    /// <param name="waiting">Chi aspetta: chiamate in attesa e chiamante parcheggiato.</param>
    /// <param name="currentPause">Durata della pausa in corso, zero se in servizio.</param>
    /// <returns>Per esempio "0:12" o "1:02:07"; stringa vuota quando non c'è niente da misurare.</returns>
    /// <remarks>
    /// <para>
    /// Ogni stato misura la sua cosa: lo squillo da quando è cominciato, la
    /// conversazione dalla risposta, l'attesa di chi aspetta da più tempo — è quello che
    /// si rischia di dimenticare, come nella casella delle attese — e la pausa da quando
    /// è iniziata. In conferenza vale la gamba più lunga, cioè da quanto l'operatore è al
    /// telefono.
    /// </para>
    /// <para>
    /// <b>Libero non ha cronometro, e nemmeno la chiamata uscente non ancora risposta.</b>
    /// Il primo non misura niente; per la seconda «durata della conversazione» sarebbe
    /// falso, e un tempo dello squillo accanto alla parola «in linea» si leggerebbe come
    /// una conversazione già cominciata.
    /// </para>
    /// </remarks>
    public static string Elapsed(
        LineStatus status,
        DateTimeOffset now,
        Call? ringing = null,
        Call? active = null,
        IReadOnlyList<Call>? conferenced = null,
        IReadOnlyList<Call>? waiting = null,
        TimeSpan currentPause = default)
    {
        switch (status)
        {
            case LineStatus.Ringing when ringing is not null:
                return CallBoardTexts.ShortDuration(now - ringing.CreatedAt);

            case LineStatus.Talking when active is not null:
                return CallBoardTexts.BandDuration(active.TalkDuration(now));

            case LineStatus.Talking when conferenced is { Count: > 0 }:
                return CallBoardTexts.BandDuration(conferenced.Max(c => c.TalkDuration(now)));

            case LineStatus.OnHold when waiting is { Count: > 0 }:
                return CallBoardTexts.BandDuration(waiting.Max(c => c.HoldDuration(now)));

            case LineStatus.OutOfService:
                return CallBoardTexts.BandDuration(currentPause);

            default:
                return string.Empty;
        }
    }
}
