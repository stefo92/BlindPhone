namespace BlindPhone.Core.Accessibility;

/// <summary>
/// Un messaggio destinato all'operatore, con tutto ciò che serve per deciderne
/// l'instradamento, la priorità e il trattamento nella coda annunci.
/// </summary>
/// <remarks>
/// <para>
/// L'annuncio è un oggetto di dominio, non una stringa passata a una funzione di
/// sintesi. La differenza conta: significa che la decisione su cosa dire è presa
/// nel core, testabile, e separata dalla decisione su come dirlo (screen reader,
/// voce interna, braille, earcon), che vive nell'infrastruttura.
/// </para>
/// </remarks>
public sealed record Announcement
{
    /// <summary>Testo da pronunciare.</summary>
    public required string Text { get; init; }

    /// <summary>Priorità, che governa interruzione, accorpamento e soppressione.</summary>
    public AnnouncementPriority Priority { get; init; } = AnnouncementPriority.Normal;

    /// <summary>Earcon da riprodurre insieme all'annuncio, se previsto.</summary>
    public EarconKind Earcon { get; init; } = EarconKind.None;

    /// <summary>
    /// Chiave di raggruppamento per l'accorpamento e l'anti-ripetizione.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Due annunci con la stessa chiave sono considerati "lo stesso tipo di
    /// notizia": se arrivano ravvicinati, vince l'ultimo e i precedenti vengono
    /// scartati. È il meccanismo che evita che tre chiamate entrate in mezzo
    /// secondo producano tre annunci consecutivi invece di uno solo aggiornato.
    /// </para>
    /// <para>
    /// Se la chiave è <c>null</c> l'annuncio non viene mai accorpato né soppresso
    /// per ripetizione: si usa per i messaggi che devono essere pronunciati sempre,
    /// anche identici (per esempio le conferme di un comando premuto due volte).
    /// </para>
    /// </remarks>
    public string? CoalesceKey { get; init; }

    /// <summary>
    /// Testo compatto per il display braille. Se <c>null</c> la riga braille non
    /// viene aggiornata da questo annuncio.
    /// </summary>
    public string? BrailleText { get; init; }

    /// <summary>
    /// Se <c>true</c>, l'annuncio viene pronunciato anche durante una conversazione
    /// attiva, in deroga al principio di silenzio.
    /// </summary>
    /// <remarks>
    /// Gli annunci a priorità <see cref="AnnouncementPriority.Critical"/> hanno
    /// sempre questo comportamento. Il flag serve per i pochi casi a priorità
    /// inferiore che l'operatore ha esplicitamente richiesto, tipicamente premendo
    /// un tasto: se chiede il conteggio della coda mentre parla, la risposta va
    /// data, non soppressa.
    /// </remarks>
    public bool BypassConversationSilence { get; init; }

    /// <summary>Istante di creazione, usato per la finestra di accorpamento.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Crea un annuncio critico, che interrompe la voce in corso.
    /// </summary>
    /// <param name="text">Testo da pronunciare.</param>
    /// <param name="now">Istante corrente.</param>
    /// <param name="earcon">Earcon associato.</param>
    /// <param name="coalesceKey">Chiave di raggruppamento.</param>
    /// <param name="braille">Testo compatto per il braille.</param>
    public static Announcement Critical(
        string text,
        DateTimeOffset now,
        EarconKind earcon = EarconKind.None,
        string? coalesceKey = null,
        string? braille = null) => new()
        {
            Text = text,
            Priority = AnnouncementPriority.Critical,
            Earcon = earcon,
            CoalesceKey = coalesceKey,
            BrailleText = braille,
            BypassConversationSilence = true,
            CreatedAt = now,
        };

    /// <summary>Crea un annuncio ad alta priorità.</summary>
    /// <param name="text">Testo da pronunciare.</param>
    /// <param name="now">Istante corrente.</param>
    /// <param name="earcon">Earcon associato.</param>
    /// <param name="coalesceKey">Chiave di raggruppamento.</param>
    /// <param name="braille">Testo compatto per il braille.</param>
    public static Announcement High(
        string text,
        DateTimeOffset now,
        EarconKind earcon = EarconKind.None,
        string? coalesceKey = null,
        string? braille = null) => new()
        {
            Text = text,
            Priority = AnnouncementPriority.High,
            Earcon = earcon,
            CoalesceKey = coalesceKey,
            BrailleText = braille,
            CreatedAt = now,
        };

    /// <summary>Crea un annuncio a priorità normale.</summary>
    /// <param name="text">Testo da pronunciare.</param>
    /// <param name="now">Istante corrente.</param>
    /// <param name="earcon">Earcon associato.</param>
    /// <param name="coalesceKey">Chiave di raggruppamento.</param>
    /// <param name="braille">Testo compatto per il braille.</param>
    public static Announcement Normal(
        string text,
        DateTimeOffset now,
        EarconKind earcon = EarconKind.None,
        string? coalesceKey = null,
        string? braille = null) => new()
        {
            Text = text,
            Priority = AnnouncementPriority.Normal,
            Earcon = earcon,
            CoalesceKey = coalesceKey,
            BrailleText = braille,
            CreatedAt = now,
        };

    /// <summary>
    /// Crea la risposta a un comando premuto dall'operatore: viene sempre
    /// pronunciata, anche durante una conversazione.
    /// </summary>
    /// <param name="text">Testo da pronunciare.</param>
    /// <param name="now">Istante corrente.</param>
    /// <param name="braille">Testo compatto per il braille.</param>
    /// <remarks>
    /// Un comando che non risponde è indistinguibile da un comando che non ha
    /// funzionato. Per un utente che non può guardare lo schermo per verificare,
    /// questa ambiguità porta a premere di nuovo, spesso peggiorando la situazione.
    /// </remarks>
    public static Announcement CommandResponse(string text, DateTimeOffset now, string? braille = null) => new()
    {
        Text = text,
        Priority = AnnouncementPriority.High,
        CoalesceKey = null,
        BrailleText = braille,
        BypassConversationSilence = true,
        CreatedAt = now,
    };
}
