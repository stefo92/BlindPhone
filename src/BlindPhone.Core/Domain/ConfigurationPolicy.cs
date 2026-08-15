namespace BlindPhone.Core.Domain;

/// <summary>
/// Sezioni della configurazione che si possono bloccare separatamente.
/// </summary>
/// <remarks>
/// La granularità per sezione esiste perché il caso frequente non è «blocca tutto»:
/// è «l'account SIP e il centralino li decide l'ufficio IT, il resto lo regola
/// l'operatore». Voce, velocità della sintesi, dispositivi audio e tasti rapidi sono
/// la parte più personale del prodotto, e bloccarli insieme ai parametri di rete
/// costringerebbe a chiamare l'assistenza per alzare il volume di un earcon.
/// </remarks>
public enum SettingsSection
{
    /// <summary>Account SIP: interni, password, dominio, trasporto, codec.</summary>
    SipAccounts,

    /// <summary>Collegamento all'API del centralino.</summary>
    Pbx,

    /// <summary>Dispositivi audio, volumi e regolazione del buffer.</summary>
    Audio,

    /// <summary>Voce, earcon, braille, verbosità.</summary>
    Accessibility,

    /// <summary>Comportamento di chiamate e trasferimento.</summary>
    Calls,

    /// <summary>Tasti rapidi e profili.</summary>
    Hotkeys,

    /// <summary>Rubrica e importazione.</summary>
    Contacts,

    /// <summary>Registro diagnostico, recapiti dell'assistenza, certificati.</summary>
    Diagnostics,

    /// <summary>Finestra e area di notifica.</summary>
    Tray,
}

/// <summary>Quanto è accessibile il pannello delle impostazioni.</summary>
public enum PanelAccess
{
    /// <summary>Aperto: l'operatore modifica quello che vuole.</summary>
    Open = 0,

    /// <summary>
    /// Consultabile ma non modificabile: i campi si leggono, il salvataggio è
    /// rifiutato.
    /// </summary>
    /// <remarks>
    /// È la modalità che serve davvero nella maggior parte delle installazioni. Un
    /// pannello che non si apre affatto toglie all'operatore anche la possibilità di
    /// <b>leggere</b> come è configurata la sua postazione — e quando chiama
    /// l'assistenza, la prima domanda che gli viene fatta è esattamente quella.
    /// </remarks>
    ReadOnly = 1,

    /// <summary>Chiuso: il pannello non si apre senza la password dell'amministratore.</summary>
    Locked = 2,
}

/// <summary>Da dove arriva un criterio di blocco.</summary>
/// <remarks>
/// Conta perché determina se il criterio è aggirabile, e questo va detto con
/// onestà a chi lo configura: vedi <see cref="ConfigurationPolicy"/>.
/// </remarks>
public enum PolicyOrigin
{
    /// <summary>Nessun criterio: il pannello è aperto.</summary>
    None = 0,

    /// <summary>
    /// Criterio scritto nella configurazione dell'utente.
    /// </summary>
    /// <remarks>
    /// Sta in un file che l'utente può modificare, quindi <b>protegge dagli errori,
    /// non dalla volontà</b>.
    /// </remarks>
    User = 1,

    /// <summary>
    /// Criterio scritto nella parte di sistema del registro di Windows.
    /// </summary>
    /// <remarks>
    /// Un utente senza privilegi amministrativi non può cambiarlo: è l'unico che
    /// costituisca un vincolo vero.
    /// </remarks>
    Machine = 2,
}

/// <summary>
/// Che cosa l'operatore può cambiare nelle impostazioni.
/// </summary>
/// <remarks>
/// <para>
/// <b>Due livelli, e la differenza va detta a chi configura.</b> Un criterio scritto
/// nella configurazione dell'utente vive in <c>%APPDATA%</c>, cartella su cui
/// l'utente ha pieno controllo: chi sa dov'è può cancellare tre righe e sbloccare
/// tutto. Protegge dalla modifica accidentale — che è il caso reale nel 99 per cento
/// delle installazioni — e non dalla volontà.
/// </para>
/// <para>
/// Il vincolo vero è quello di postazione, scritto nella parte di sistema del
/// registro di Windows, dove un utente senza privilegi amministrativi non arriva.
/// Quando c'è, ha la precedenza su qualunque cosa dica il file dell'utente.
/// </para>
/// <para>
/// Fingere che il primo livello sia una barriera di sicurezza sarebbe peggio che non
/// averlo: chi amministra la rete prenderebbe una decisione basandosi su una
/// garanzia che non esiste.
/// </para>
/// </remarks>
public sealed record ConfigurationPolicy
{
    /// <summary>Accessibilità del pannello nel suo insieme.</summary>
    public PanelAccess Access { get; init; } = PanelAccess.Open;

    /// <summary>Sezioni bloccate, quando il pannello resta consultabile.</summary>
    /// <remarks>
    /// Vuoto con <see cref="PanelAccess.ReadOnly"/> significa «tutte»: chi imposta
    /// la sola modalità senza elencare le sezioni intende bloccare tutto, ed è
    /// l'interpretazione meno sorprendente.
    /// </remarks>
    public IReadOnlyList<SettingsSection> LockedSections { get; init; } = [];

    /// <summary>
    /// Messaggio che l'amministratore lascia all'operatore.
    /// </summary>
    /// <remarks>
    /// Va compilato. «Impostazione bloccata» lascia l'operatore senza sapere a chi
    /// rivolgersi; «Configurazione gestita dall'ufficio IT, interno 4000» gli dice
    /// cosa fare. È l'unica parte del criterio che l'operatore sente pronunciare, e
    /// per un operatore non vedente è tutto quello che c'è.
    /// </remarks>
    public string? Note { get; init; }

    /// <summary>Impronta della password di sblocco, in esadecimale.</summary>
    public string? PasswordHash { get; init; }

    /// <summary>Sale della password, in base 64.</summary>
    public string? PasswordSalt { get; init; }

    /// <summary>Da dove arriva questo criterio.</summary>
    public PolicyOrigin Origin { get; init; } = PolicyOrigin.None;

    /// <summary>Criterio assente: tutto aperto.</summary>
    public static ConfigurationPolicy Unrestricted { get; } = new();

    /// <summary>Indica se esiste una password con cui sbloccare.</summary>
    public bool HasPassword =>
        !string.IsNullOrWhiteSpace(PasswordHash) && !string.IsNullOrWhiteSpace(PasswordSalt);

    /// <summary>Indica se il criterio limita qualcosa.</summary>
    public bool IsRestrictive => Access != PanelAccess.Open || LockedSections.Count > 0;

    /// <summary>Indica se una sezione è modificabile.</summary>
    /// <param name="section">Sezione da verificare.</param>
    /// <remarks>
    /// Con il pannello chiuso la risposta è no per tutto: chi è arrivato a leggere i
    /// campi lo ha fatto sbloccando, e in quel caso il criterio non si applica più.
    /// </remarks>
    public bool CanEdit(SettingsSection section) => Access switch
    {
        PanelAccess.Locked => false,
        PanelAccess.ReadOnly => LockedSections.Count > 0 && !LockedSections.Contains(section),
        _ => LockedSections.Count == 0 || !LockedSections.Contains(section),
    };

    /// <summary>Indica se il pannello si apre senza password.</summary>
    public bool CanOpenPanel => Access != PanelAccess.Locked;

    /// <summary>Descrizione pronunciabile del criterio in vigore.</summary>
    /// <remarks>
    /// Comprende il messaggio dell'amministratore quando c'è: è la parte che dice
    /// all'operatore a chi rivolgersi, e senza di essa il blocco è solo un ostacolo
    /// senza spiegazione.
    /// </remarks>
    public string Describe()
    {
        if (!IsRestrictive) return "Le impostazioni sono modificabili.";

        var testo = Access switch
        {
            PanelAccess.Locked => "Il pannello delle impostazioni e' bloccato dall'amministratore.",
            PanelAccess.ReadOnly when LockedSections.Count == 0
                => "Le impostazioni si possono leggere ma non modificare.",
            PanelAccess.ReadOnly
                => $"Alcune sezioni si possono leggere ma non modificare: {DescribeSections()}.",
            _ => $"Alcune sezioni sono bloccate: {DescribeSections()}.",
        };

        if (!string.IsNullOrWhiteSpace(Note)) testo += $" {Note.Trim()}";

        return testo;
    }

    /// <summary>Elenco pronunciabile delle sezioni bloccate.</summary>
    public string DescribeSections() =>
        LockedSections.Count == 0
            ? "tutte"
            : string.Join(", ", LockedSections.Select(DescribeSection));

    /// <summary>Nome pronunciabile di una sezione.</summary>
    /// <param name="section">Sezione da nominare.</param>
    public static string DescribeSection(SettingsSection section) => section switch
    {
        SettingsSection.SipAccounts => "account SIP",
        SettingsSection.Pbx => "centralino",
        SettingsSection.Audio => "audio",
        SettingsSection.Accessibility => "accessibilita'",
        SettingsSection.Calls => "chiamate",
        SettingsSection.Hotkeys => "tasti rapidi",
        SettingsSection.Contacts => "rubrica",
        SettingsSection.Diagnostics => "diagnostica",
        SettingsSection.Tray => "finestra e area di notifica",
        _ => section.ToString(),
    };
}
