namespace BlindPhone.Core.Domain;

/// <summary>Come l'operatore comanda il posto operatore.</summary>
/// <remarks>
/// <para>
/// Non è una scelta di aspetto ma di <b>gesti</b>: cambia quali tasti governano la
/// chiamata, non come si vede la finestra.
/// </para>
/// </remarks>
public enum InteractionMode
{
    /// <summary>
    /// Le scorciatoie complete: un tasto per funzione, rimappabili dal pannello.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// Tre tasti del tastierino numerico governano l'intera chiamata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nasce per chi sta al centralino senza avere il tempo — o la voglia — di
    /// imparare quattordici scorciatoie: <c>+</c> risponde e mette in pausa,
    /// <c>/</c> riaggancia, <c>Invio</c> trasferisce. Tutto il resto continua a
    /// funzionare come prima; questa modalità aggiunge una via breve, non ne toglie
    /// nessuna.
    /// </para>
    /// <para>
    /// In questa modalità <b>la rimappatura dei tasti è bloccata</b>. La via breve ha
    /// senso se è identica su tutte le postazioni: un tastierino che su una macchina
    /// fa una cosa e su un'altra ne fa un'altra è peggio di nessuna via breve, perché
    /// chi sostituisce un collega per un turno si porta dietro l'abitudine sbagliata.
    /// </para>
    /// </remarks>
    Simplified = 1,
}
