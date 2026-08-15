namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Priorità di un annuncio, secondo il modello di ANALISI-BLINDPHONE.md §4.3.
/// </summary>
/// <remarks>
/// <para>
/// La priorità non decide solo l'ordine: decide se un annuncio può interrompere la
/// voce in corso, se può essere accorpato ad altri, e se viene pronunciato durante
/// una conversazione o soppresso a favore del solo earcon.
/// </para>
/// <para>
/// L'errore da evitare è la generosità: se tutto è importante, l'operatore smette
/// di ascoltare e la voce diventa rumore di fondo su cui parlare al telefono. Un
/// annuncio sale a <see cref="Critical"/> solo se il suo mancato ascolto comporta
/// un danno operativo, non se è semplicemente interessante.
/// </para>
/// </remarks>
public enum AnnouncementPriority
{
    /// <summary>
    /// Informativo. Pronunciato solo se la coda annunci è vuota, mai durante una
    /// chiamata. Esempi: sincronizzazione rubrica completata, suggerimenti.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Normale. Accorpabile, soppresso durante la conversazione se la relativa
    /// impostazione è attiva. Esempi: variazione del conteggio coda, cambio di
    /// presenza di un contatto.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// Alto. Non interrompe un annuncio critico ma ha precedenza sul resto.
    /// Esempi: chiamata risposta o terminata, esito del trasferimento, messa in
    /// attesa, interno occupato.
    /// </summary>
    High = 2,

    /// <summary>
    /// Critico. Interrompe la voce in corso e attenua l'audio della chiamata.
    /// Esempi: chiamata entrante, caduta della linea, perdita della registrazione
    /// SIP, pausa automatica imposta dal centralino.
    /// </summary>
    Critical = 3,
}
