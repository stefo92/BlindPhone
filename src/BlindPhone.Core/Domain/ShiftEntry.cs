namespace BlindPhone.Core.Domain;

/// <summary>Se un collega è alla sua postazione, per quanto se ne può sapere.</summary>
/// <remarks>
/// Volutamente più grossolano di <see cref="PresenceState"/>. La domanda a cui questo
/// elenco risponde è «chi c'è adesso», non «chi è libero adesso»: sono due decisioni
/// diverse, e chi guarda il turno non vuole vedere un collega sparire dall'elenco
/// perché in quel momento sta parlando al telefono.
/// </remarks>
public enum DutyState
{
    /// <summary>Non si sa: il centralino non ha risposto, o non ha ancora risposto.</summary>
    Unknown,

    /// <summary>Il telefono è registrato: qualcuno è alla postazione.</summary>
    OnDuty,

    /// <summary>Il telefono non risponde: spento, scollegato, o nessuno al suo posto.</summary>
    OffDuty,

    /// <summary>Il centralino non conosce quell'interno: c'è un errore nell'elenco.</summary>
    NotConfigured,
}

/// <summary>
/// Una riga dell'elenco dei colleghi: chi è, com'è messo, e da quando.
/// </summary>
/// <param name="Number">Interno, nella forma da mostrare.</param>
/// <param name="Name">
/// Nominativo. Quello scritto nelle impostazioni, altrimenti quello trovato in rubrica,
/// altrimenti stringa vuota.
/// </param>
/// <param name="Duty">Se è alla postazione.</param>
/// <param name="Detail">
/// Stato di dettaglio, quando la sorgente lo conosce: libero, occupato, che squilla.
/// Vale <see cref="PresenceState.Unknown"/> quando l'informazione arriva dal sondaggio,
/// che sa soltanto se il telefono c'è.
/// </param>
/// <param name="Since">
/// Da quando lo stato è quello, oppure <c>null</c> se non è mai cambiato da quando la
/// postazione è accesa.
/// </param>
/// <param name="Source">Da quale strada arriva l'informazione, per la diagnostica.</param>
public sealed record ShiftEntry(
    string Number,
    string Name,
    DutyState Duty,
    PresenceState Detail,
    DateTimeOffset? Since,
    string Source)
{
    /// <summary>Nome se c'è, altrimenti il numero: quello che si dice per identificarlo.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Number : Name;
}
