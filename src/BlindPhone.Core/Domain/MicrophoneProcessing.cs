using System.Text.Json.Serialization;

namespace BlindPhone.Core.Domain;

/// <summary>
/// Quanto deve intervenire la riduzione del rumore ambientale sul microfono.
/// </summary>
/// <remarks>
/// <para>
/// <b>I livelli non sono una scala di qualità, sono una scala di compromesso.</b>
/// Togliere rumore a un segnale che contiene anche la voce significa deformare un po'
/// la voce: più si toglie, più si deforma. La riduzione agisce soprattutto <i>fra una
/// parola e l'altra</i>, dove c'è solo rumore e niente da rovinare; mentre si parla il
/// rumore resta e la voce, a livelli alti, comincia a suonare «metallica».
/// </para>
/// <para>
/// Per questo il valore predefinito è <see cref="Off"/> e <see cref="Strong"/> non è
/// presentato come «il migliore»: serve in una stanza molto rumorosa, dove la voce
/// deformata si capisce meglio del rumore.
/// </para>
/// </remarks>
public enum NoiseSuppressionLevel
{
    /// <summary>Nessun intervento: il microfono passa com'è.</summary>
    Off = 0,

    /// <summary>
    /// Leggera: attenua il rumore costante — ventola, ronzio, aria condizionata — senza
    /// toccare quasi la voce. È il livello da provare per primo.
    /// </summary>
    Light = 1,

    /// <summary>Media: attenua anche il rumore d'ufficio più presente, con qualche effetto sulla voce.</summary>
    Medium = 2,

    /// <summary>Forte: per ambienti molto rumorosi. La voce si fa sensibilmente meno naturale.</summary>
    Strong = 3,
}

/// <summary>
/// Elaborazione automatica applicata al microfono prima di inviare la voce.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perché esiste.</b> Un microfono tenuto a distanza diversa, o una cuffia con un
/// guadagno basso, producono una voce che l'interlocutore sente troppo debole oppure
/// a livelli che cambiano da una chiamata all'altra. Con il microfono di una postazione
/// misurata il picco arrivava a -23 dBFS, cioè usava un quattordicesimo della dinamica
/// disponibile: è quello che il controllo automatico del livello (AGC) corregge, senza
/// costringere l'operatore a regolare i volumi di Windows.
/// </para>
/// <para>
/// <b>Entrambe le funzioni sono spente per impostazione predefinita</b>, e non per
/// prudenza di principio: aggiungono un'elaborazione sul percorso della voce che
/// l'operatore non ha scelto, e la riduzione del rumore aggiunge circa 16 millisecondi
/// di ritardo in trasmissione. Chi le vuole le accende, e sente subito la differenza.
/// </para>
/// </remarks>
public sealed record MicrophoneProcessing
{
    /// <summary>
    /// Se regolare automaticamente il livello della voce verso un valore costante.
    /// </summary>
    /// <remarks>
    /// Alza la voce debole e abbassa quella forte. Durante le pause il guadagno resta
    /// fermo, quindi non «tira su» il rumore di fondo quando nessuno parla.
    /// </remarks>
    public bool AutoGain { get; init; }

    /// <summary>Quanto ridurre il rumore ambientale.</summary>
    public NoiseSuppressionLevel NoiseSuppression { get; init; } = NoiseSuppressionLevel.Off;

    /// <summary>Se almeno una delle due funzioni è attiva.</summary>
    /// <remarks>
    /// Escluso dal file di configurazione: è un valore derivato, e scriverlo accanto
    /// ai due veri lascerebbe a chi modifica il file a mano l'impressione di poterlo
    /// cambiare.
    /// </remarks>
    [JsonIgnore]
    public bool IsActive => AutoGain || Level != NoiseSuppressionLevel.Off;

    /// <summary>Nessuna elaborazione: la voce passa com'è.</summary>
    public static MicrophoneProcessing None { get; } = new();

    /// <summary>
    /// Il livello di riduzione del rumore, ricondotto a un valore che esiste.
    /// </summary>
    /// <remarks>
    /// Un file di configurazione modificato a mano può contenere un numero che non
    /// corrisponde a nessun livello. Lo si tratta come «spento» invece di lasciare che
    /// il motore ne tragga una conclusione qualsiasi: sul percorso della voce è meglio
    /// non fare niente che fare qualcosa di imprevisto.
    /// </remarks>
    [JsonIgnore]
    public NoiseSuppressionLevel Level
        => Enum.IsDefined(NoiseSuppression) ? NoiseSuppression : NoiseSuppressionLevel.Off;

    /// <summary>Una copia con il livello di riduzione del rumore normalizzato.</summary>
    public MicrophoneProcessing Sanitized() => this with { NoiseSuppression = Level };
}
