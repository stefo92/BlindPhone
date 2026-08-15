namespace BlindPhone.Core.Domain;

/// <summary>
/// Un dato diagnostico, già in forma leggibile.
/// </summary>
/// <param name="Label">Che cosa misura, in parole.</param>
/// <param name="Value">Valore, con l'unità di misura quando ne ha una.</param>
/// <remarks>
/// <para>
/// L'etichetta è in italiano e per esteso perché finisce in due posti dove non c'è
/// spazio per interpretarla: il pannello di diagnostica, letto a voce da un operatore
/// che sta cercando di capire un guasto, e il riepilogo che quell'operatore incolla in
/// una segnalazione all'assistenza.
/// </para>
/// <para>
/// Il valore è una stringa e non un numero: "1.608 pacchetti" e "20 ms" si leggono
/// bene, mentre un numero nudo costringerebbe chi legge a ricordarsi l'unità di
/// misura di ogni riga.
/// </para>
/// </remarks>
public sealed record DiagnosticCounter(string Label, string Value);
