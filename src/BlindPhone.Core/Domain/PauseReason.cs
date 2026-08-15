namespace BlindPhone.Core.Domain;

/// <summary>
/// Motivo di pausa selezionabile dall'operatore, così come definito sul PBX.
/// </summary>
/// <param name="Code">Codice tecnico, usato nelle chiamate all'API.</param>
/// <param name="Label">Etichetta leggibile e pronunciabile.</param>
/// <remarks>
/// I motivi si leggono dal centralino invece di configurarli localmente. Così
/// l'operatore sceglie fra le stesse voci che i colleghi vedenti vedono sul
/// pannello, e le sue pause finiscono nella reportistica del servizio con lo
/// stesso codice. Un elenco locale divergente produrrebbe statistiche in cui il
/// lavoro dell'operatore non vedente risulta anomalo.
/// </remarks>
public sealed record PauseReason(string Code, string Label);
