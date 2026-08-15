namespace BlindPhone.Core.Domain;

/// <summary>Politica di cifratura del flusso audio.</summary>
/// <remarks>
/// Vive nel dominio e non nel livello telefonico perché è una decisione dell'ente,
/// non un dettaglio di protocollo: dice se una conversazione può viaggiare in chiaro,
/// che è una domanda a cui risponde chi amministra il servizio.
/// </remarks>
public enum SrtpPolicy
{
    /// <summary>Nessuna cifratura: l'audio viaggia in chiaro.</summary>
    Disabled,

    /// <summary>
    /// Cifratura se la controparte la supporta, altrimenti chiamata in chiaro.
    /// </summary>
    /// <remarks>
    /// È il compromesso praticabile su una rete mista: si ottiene la cifratura dove
    /// c'è senza perdere le chiamate verso chi non la offre. Il ripiego <b>viene
    /// detto</b> all'operatore, perché una protezione che si spegne in silenzio è
    /// peggio di una protezione assente.
    /// </remarks>
    Preferred,

    /// <summary>
    /// Cifratura obbligatoria: senza, la chiamata non viene instaurata.
    /// </summary>
    /// <remarks>
    /// La chiamata che fallisce <b>è</b> il comportamento voluto: dove questa politica
    /// è scelta, una conversazione in chiaro è un incidente peggiore di una
    /// telefonata non fatta.
    /// </remarks>
    Required,
}

/// <summary>Esito della decisione su come instaurare il flusso audio.</summary>
public enum SrtpOutcome
{
    /// <summary>Si procede con l'audio cifrato.</summary>
    Secure,

    /// <summary>Si procede con l'audio in chiaro.</summary>
    Clear,

    /// <summary>Non si procede: la politica impone la cifratura e non è disponibile.</summary>
    Reject,
}

/// <summary>
/// Decide se una chiamata va instaurata cifrata, in chiaro o non va instaurata.
/// </summary>
/// <remarks>
/// <para>
/// È una funzione pura, e sta separata dal codice che apre le chiamate per la stessa
/// ragione per cui lo è la regola che instrada la voce degli annunci: i suoi modi di
/// sbagliare non si vedono leggendo il codice. Una politica <c>Required</c> che
/// ripiega in chiaro produce una conversazione che sembra protetta e non lo è, e
/// nessuno se ne accorge finché non se ne accorge qualcun altro.
/// </para>
/// </remarks>
public static class SrtpNegotiation
{
    /// <summary>
    /// Decide come instaurare il flusso audio.
    /// </summary>
    /// <param name="policy">Politica configurata sull'account.</param>
    /// <param name="remoteSupportsSrtp">
    /// Se la controparte ha offerto o accettato la cifratura. Su una chiamata uscente
    /// si conosce solo dopo la risposta, quindi la decisione si prende due volte:
    /// prima per costruire l'offerta, poi sull'esito.
    /// </param>
    public static SrtpOutcome Decide(SrtpPolicy policy, bool remoteSupportsSrtp) => policy switch
    {
        SrtpPolicy.Disabled => SrtpOutcome.Clear,
        SrtpPolicy.Required => remoteSupportsSrtp ? SrtpOutcome.Secure : SrtpOutcome.Reject,
        SrtpPolicy.Preferred => remoteSupportsSrtp ? SrtpOutcome.Secure : SrtpOutcome.Clear,
        _ => SrtpOutcome.Clear,
    };

    /// <summary>
    /// Indica se l'offerta uscente deve proporre la cifratura.
    /// </summary>
    /// <param name="policy">Politica configurata.</param>
    /// <remarks>
    /// Con <see cref="SrtpPolicy.Preferred"/> si propone comunque: è l'unico modo per
    /// scoprire se la controparte la supporta. Il ripiego, se serve, arriva dopo.
    /// </remarks>
    public static bool ShouldOffer(SrtpPolicy policy) => policy is SrtpPolicy.Preferred or SrtpPolicy.Required;

    /// <summary>
    /// Indica se un rifiuto del centralino è compatibile con un secondo tentativo in
    /// chiaro.
    /// </summary>
    /// <param name="statusCode">Codice di stato SIP della risposta di rifiuto.</param>
    /// <remarks>
    /// <para>
    /// Sono i tre modi in cui un apparato dice «questa descrizione di sessione non mi
    /// va bene»: 488 <i>Not Acceptable Here</i> è quello previsto dalla RFC 3261 per
    /// un'offerta media non accettabile, 606 <i>Not Acceptable</i> è la sua variante
    /// globale, 415 <i>Unsupported Media Type</i> lo usano alcuni centralini più
    /// vecchi al posto del 488.
    /// </para>
    /// <para>
    /// <b>Solo questi tre.</b> Un 486 «occupato» o un 404 «non esiste» non hanno nulla
    /// a che vedere con la cifratura, e ritentare su quelli significherebbe chiamare
    /// due volte chi ha appena rifiutato.
    /// </para>
    /// </remarks>
    public static bool LooksLikeMediaRejection(int statusCode) => statusCode is 488 or 606 or 415;
}
