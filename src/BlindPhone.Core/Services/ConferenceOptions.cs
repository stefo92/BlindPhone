namespace BlindPhone.Core.Services;

/// <summary>Parametri della conferenza a tre.</summary>
/// <remarks>
/// <para>
/// Contiene soltanto nomi di tasti, e non è una svista: la conferenza non ha
/// parametri di comportamento da regolare — o le due linee sono unite o non lo
/// sono. Ha però il problema che hanno tutte le modalità di questo prodotto,
/// cioè che entrarci cambia il significato dei tasti, e che quel cambiamento va
/// detto nominando le combinazioni <b>configurate</b>.
/// </para>
/// <para>
/// Le proprietà sono modificabili dopo la costruzione per la stessa ragione per
/// cui lo è <see cref="TransferOptions.TransferKeyName"/>: il profilo dei tasti si
/// cambia a caldo, mentre queste opzioni si compongono all'avvio. Un'istruzione
/// vocale che nomina un tasto non più assegnato manda a premere qualcosa che non
/// fa nulla, e lo fa proprio mentre due persone sono in linea.
/// </para>
/// </remarks>
public sealed record ConferenceOptions
{
    /// <summary>Nome pronunciabile del tasto che apre e scioglie la conferenza.</summary>
    public string ConferenceKeyName { get; set; } = "F7";

    /// <summary>Nome pronunciabile del tasto di attesa.</summary>
    /// <remarks>
    /// Compare nel rifiuto del comando: la conferenza richiede due chiamate, e
    /// mettere in attesa la prima è il primo dei due gesti che servono a ottenerle.
    /// </remarks>
    public string HoldKeyName { get; set; } = "F4";

    /// <summary>Nome pronunciabile del tasto che porta al campo di composizione.</summary>
    /// <remarks>
    /// Secondo gesto del rifiuto: chiamare il secondo interlocutore.
    /// </remarks>
    public string DialerKeyName { get; set; } = "F8";

    /// <summary>Impostazioni predefinite.</summary>
    public static ConferenceOptions Default { get; } = new();
}
