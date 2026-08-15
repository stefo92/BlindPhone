using System.Text;
using BlindPhone.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Infrastructure;

/// <summary>
/// Conserva i profili di scorciatoie e decide quale è attivo.
/// </summary>
/// <remarks>
/// <para>
/// I profili vivono in <c>%APPDATA%\BlindPhone\hotkeys.json</c>, accanto alla
/// configurazione e per lo stesso motivo: sono personali, e su una postazione usata a
/// turni alterni ogni operatore deve ritrovare i propri. Stanno in un file separato
/// da <c>settings.json</c> perché si esportano e si importano da soli — la mappa dei
/// tasti si porta su un'altra macchina anche quando l'account SIP è diverso.
/// </para>
/// <para>
/// <b>Il profilo attivo è un'istanza sola, viva per tutta la sessione.</b> La
/// finestra principale, il registratore delle scorciatoie globali e il pannello delle
/// impostazioni ne condividono il riferimento: cambiare configurazione significa
/// riempire quell'istanza con altre assegnazioni, non sostituirla. Se venisse
/// sostituita, chi la tiene da prima continuerebbe a usare la mappa vecchia, e la
/// modifica sembrerebbe salvata ma senza effetto — cioè il peggior esito possibile
/// su una funzione che si verifica solo provandola.
/// </para>
/// <para>
/// Il salvataggio è atomico come quello della configurazione: una mappa dei tasti
/// troncata da un'interruzione di corrente lascerebbe l'operatore senza scorciatoie
/// al riavvio, cioè senza modo di usare il telefono.
/// </para>
/// </remarks>
public sealed class HotkeyProfileStore
{
    private readonly ILogger<HotkeyProfileStore> _log;
    private readonly List<HotkeyProfile> _profiles = [];

    /// <summary>Crea il deposito e carica i profili.</summary>
    /// <param name="directory">Cartella dati dell'applicazione.</param>
    /// <param name="logger">Log diagnostico.</param>
    /// <remarks>
    /// Quale profilo sia attivo lo dice <c>hotkeys.json</c> e nessun altro. Prima
    /// l'informazione stava anche nella configurazione generale, e i due file
    /// finivano per contraddirsi: al riavvio vinceva quello sbagliato e l'operatore si
    /// ritrovava un profilo che non aveva scelto.
    /// </remarks>
    public HotkeyProfileStore(string directory, ILogger<HotkeyProfileStore>? logger = null)
    {
        _log = logger ?? NullLogger<HotkeyProfileStore>.Instance;
        FilePath = Path.Combine(directory, "hotkeys.json");

        Load();
    }

    /// <summary>Percorso del file dei profili.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Profilo attivo. L'istanza non cambia mai: cambia il suo contenuto.
    /// </summary>
    public HotkeyProfile Active { get; } = new("Standard");

    /// <summary>Nomi dei profili disponibili, in ordine alfabetico.</summary>
    public IReadOnlyList<string> ProfileNames =>
        [.. _profiles.Select(p => p.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Assegnazioni scartate all'ultima lettura perché non interpretabili.
    /// </summary>
    public int IgnoredBindings { get; private set; }

    /// <summary>Sollevato quando il profilo attivo cambia contenuto.</summary>
    /// <remarks>
    /// Serve alla finestra principale per registrare di nuovo le scorciatoie globali
    /// presso Windows: quelle locali seguono l'istanza condivisa e cambiano da sole,
    /// le globali no, perché il sistema conosce solo quelle che gli sono state
    /// dichiarate.
    /// </remarks>
    public event EventHandler<HotkeyProfile>? ActiveProfileChanged;

    /// <summary>Restituisce una copia di un profilo, oppure <c>null</c> se non esiste.</summary>
    /// <param name="name">Nome del profilo.</param>
    /// <remarks>
    /// Si restituisce una copia perché il pannello lavora su una bozza: se
    /// consegnassimo l'originale, ogni tasto premuto nell'editor sarebbe già una
    /// modifica applicata, e "Annulla" non avrebbe nulla da annullare.
    /// </remarks>
    public HotkeyProfile? Get(string? name)
        => Find(name)?.Clone();

    /// <summary>Indica se esiste un profilo con questo nome.</summary>
    /// <param name="name">Nome da verificare.</param>
    public bool Exists(string? name) => Find(name) is not null;

    /// <summary>
    /// Registra un profilo e lo rende attivo.
    /// </summary>
    /// <param name="profile">Profilo da salvare, con il nome desiderato.</param>
    /// <returns><c>true</c> se il file è stato scritto.</returns>
    /// <remarks>
    /// L'attivazione avviene anche se la scrittura del file fallisce: l'operatore ha
    /// appena assegnato dei tasti e deve poterli usare subito. Il fallimento viene
    /// riportato a parte, perché riguarda solo la sopravvivenza al riavvio.
    /// </remarks>
    public bool SaveAndActivate(HotkeyProfile profile)
    {
        var stored = profile.Clone();
        var index = _profiles.FindIndex(p => Same(p.Name, stored.Name));

        if (index >= 0) _profiles[index] = stored;
        else _profiles.Add(stored);

        Active.CopyFrom(stored);
        var saved = Persist();

        ActiveProfileChanged?.Invoke(this, Active);
        return saved;
    }

    /// <summary>Rende attivo un profilo esistente.</summary>
    /// <param name="name">Nome del profilo.</param>
    /// <returns><c>true</c> se il profilo esisteva ed è stato attivato.</returns>
    public bool Activate(string? name)
    {
        if (Find(name) is not { } profile) return false;

        Active.CopyFrom(profile);
        Persist();

        ActiveProfileChanged?.Invoke(this, Active);
        return true;
    }

    /// <summary>
    /// Elimina un profilo.
    /// </summary>
    /// <param name="name">Nome del profilo.</param>
    /// <returns>
    /// Messaggio di errore pronto per l'annuncio, oppure <c>null</c> se
    /// l'eliminazione è riuscita.
    /// </returns>
    /// <remarks>
    /// I profili forniti con il prodotto non si eliminano: sono il punto di ritorno
    /// quando una configurazione personale si rivela sbagliata, e serve che ci siano
    /// sempre. Se si elimina quello attivo si torna allo Standard invece di restare
    /// senza scorciatoie.
    /// </remarks>
    public string? Delete(string? name)
    {
        if (Find(name) is not { } profile) return "Il profilo non esiste piu'";

        if (HotkeyProfile.IsBuiltIn(profile.Name))
            return $"Il profilo {profile.Name} e' fornito con il prodotto e non si puo' eliminare";

        _profiles.Remove(profile);

        if (Same(Active.Name, profile.Name))
        {
            var fallback = Find("Standard") ?? _profiles.FirstOrDefault() ?? HotkeyProfile.CreateDefault();
            Active.CopyFrom(fallback);
            ActiveProfileChanged?.Invoke(this, Active);
        }

        Persist();
        return null;
    }

    /// <summary>Esporta un profilo in un file scelto dall'operatore.</summary>
    /// <param name="profile">Profilo da esportare.</param>
    /// <param name="path">Percorso di destinazione.</param>
    /// <returns><c>true</c> se l'esportazione è riuscita.</returns>
    public bool ExportProfile(HotkeyProfile profile, string path)
    {
        try
        {
            File.WriteAllText(path, HotkeyProfileSerializer.SerializeSingle(profile), Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Esportazione del profilo di scorciatoie non riuscita");
            return false;
        }
    }

    /// <summary>
    /// Esporta la scheda dei tasti in testo semplice.
    /// </summary>
    /// <param name="profile">Profilo da descrivere.</param>
    /// <param name="path">Percorso di destinazione.</param>
    /// <param name="generatedAt">Momento della generazione.</param>
    /// <returns><c>true</c> se la scrittura è riuscita.</returns>
    /// <remarks>
    /// La codifica è UTF-8: i traduttori braille e i blocchi note per non vedenti la
    /// leggono, mentre una codifica di sistema renderebbe illeggibili le lettere
    /// accentate su una macchina configurata diversamente.
    /// </remarks>
    public bool ExportCard(HotkeyProfile profile, string path, DateTimeOffset generatedAt)
    {
        try
        {
            File.WriteAllText(path, HotkeyCardWriter.Build(profile, generatedAt), Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Esportazione della scheda dei tasti non riuscita");
            return false;
        }
    }

    /// <summary>
    /// Serializza tutti i profili, per includerli in un pacchetto di configurazione.
    /// </summary>
    /// <remarks>
    /// Restituisce lo stesso formato del file <c>hotkeys.json</c>: un pacchetto che
    /// trasporta i profili trasporta il testo già serializzato invece degli oggetti,
    /// così il formato ha un serializzatore solo e non due che possono divergere.
    /// </remarks>
    public string SerializeAll() => HotkeyProfileSerializer.Serialize(_profiles, Active.Name);

    /// <summary>
    /// Sostituisce i profili con quelli di un pacchetto di configurazione.
    /// </summary>
    /// <param name="json">Profili serializzati.</param>
    /// <param name="activeProfile">Profilo da rendere attivo, se presente nel pacchetto.</param>
    /// <returns>Quanti profili sono stati registrati.</returns>
    /// <remarks>
    /// <para>
    /// <b>Sovrascrive, a differenza di <see cref="Import"/>.</b> Le due operazioni
    /// hanno intenzioni diverse: importare un profilo è un gesto dell'operatore, che
    /// aggiunge il proprio senza toccare quelli già presenti; applicare un pacchetto è
    /// un gesto dell'amministratore, che distribuisce una configurazione decisa
    /// centralmente e si aspetta che sulla postazione ci sia quella.
    /// </para>
    /// <para>
    /// I profili con nomi diversi da quelli del pacchetto <b>restano</b>: un operatore
    /// che si era costruito una mappa propria non se la vede sparire perché l'ufficio
    /// IT ha distribuito la configurazione di reparto.
    /// </para>
    /// </remarks>
    public int ImportAll(string json, string? activeProfile)
    {
        try
        {
            var caricati = HotkeyProfileSerializer.Deserialize(json);
            if (caricati.Profiles.Count == 0) return 0;

            foreach (var profilo in caricati.Profiles)
            {
                var index = _profiles.FindIndex(p => Same(p.Name, profilo.Name));

                if (index >= 0) _profiles[index] = profilo;
                else _profiles.Add(profilo);
            }

            var richiesto = activeProfile ?? caricati.ActiveProfile;

            if (Find(richiesto) is { } attivo)
            {
                Active.CopyFrom(attivo);
                ActiveProfileChanged?.Invoke(this, Active);
            }

            Persist();

            _log.LogInformation(
                "Profili di scorciatoie applicati dal pacchetto: {Count}, attivo {Attivo}",
                caricati.Profiles.Count, Active.Name);

            return caricati.Profiles.Count;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applicazione dei profili di scorciatoie dal pacchetto non riuscita");
            return 0;
        }
    }

    /// <summary>
    /// Importa un profilo da file e lo registra.
    /// </summary>
    /// <param name="path">Percorso del file.</param>
    /// <returns>Il profilo importato, oppure <c>null</c> se il file non è valido.</returns>
    /// <remarks>
    /// Un profilo con un nome già presente non sovrascrive l'esistente: viene
    /// registrato con un nome libero. Sovrascrivere significherebbe cancellare senza
    /// preavviso la configurazione di chi usa la postazione, e l'importazione è
    /// spesso fatta dall'ufficio IT su una macchina altrui.
    /// </remarks>
    public HotkeyProfile? Import(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var imported = HotkeyProfileSerializer.DeserializeSingle(json);

            if (imported is null || imported.Bindings.Count == 0) return null;

            imported.Name = ProposeName(imported.Name);
            _profiles.Add(imported);
            Persist();

            return imported.Clone();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importazione del profilo di scorciatoie non riuscita");
            return null;
        }
    }

    /// <summary>
    /// Propone un nome libero a partire da quello richiesto.
    /// </summary>
    /// <param name="requested">Nome desiderato.</param>
    public string ProposeName(string? requested)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "Profilo" : requested.Trim();

        if (!Exists(baseName)) return baseName;

        for (var index = 2; index < 100; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!Exists(candidate)) return candidate;
        }

        return $"{baseName} {Guid.NewGuid():N}"[..40];
    }

    // -----------------------------------------------------------------------
    // Persistenza
    // -----------------------------------------------------------------------

    private void Load()
    {
        var loadedActive = (string?)null;
        var existed = File.Exists(FilePath);

        if (existed)
        {
            try
            {
                var result = HotkeyProfileSerializer.Deserialize(File.ReadAllText(FilePath, Encoding.UTF8));

                _profiles.AddRange(result.Profiles);
                loadedActive = result.ActiveProfile;
                IgnoredBindings = result.IgnoredBindings;

                if (result.IgnoredBindings > 0)
                {
                    _log.LogWarning(
                        "{Count} assegnazioni di tasti non interpretabili sono state ignorate", result.IgnoredBindings);
                }
            }
            catch (Exception ex)
            {
                // Stessa scelta della configurazione: si conserva l'originale per
                // l'assistenza e si riparte dai profili predefiniti, invece di
                // lasciare la postazione senza tasti.
                _log.LogError(ex, "File dei tasti rapidi non leggibile: si usano i profili predefiniti");
                TryBackupCorruptedFile();
                _profiles.Clear();
            }
        }

        // I profili forniti con il prodotto vengono RIGENERATI dal codice a ogni
        // avvio, non conservati come li aveva scritti la versione precedente.
        //
        // Non sono modificabili dall'interfaccia — chi li cambia salva una copia —
        // quindi non c'e' nulla dell'operatore da perdere. Conservandoli, invece, un
        // comando aggiunto da una versione nuova non sarebbe mai arrivato su una
        // postazione gia' in servizio: e' cosi' che F1 e la guida in linea sono
        // rimasti senza tasto sulle installazioni esistenti.
        foreach (var builtIn in HotkeyProfile.CreateBuiltIn())
        {
            if (Find(builtIn.Name) is { } stale) _profiles.Remove(stale);
            _profiles.Add(builtIn);
        }

        var migrated = MigrateUserProfiles();

        var chosen = Find(loadedActive) ?? Find("Standard") ?? _profiles[0];
        Active.CopyFrom(chosen);

        // Al primo avvio il file viene scritto subito, come per la configurazione e
        // per lo stesso motivo: l'amministratore che prepara la postazione deve poter
        // vedere quali profili esistono e come sono fatti, senza doverli indovinare.
        // Dopo una migrazione si riscrive perche' il lavoro fatto non vada ripetuto a
        // ogni avvio, e perche' resti traccia di cosa e' stato assegnato.
        if (!existed || migrated) Persist();

        _log.LogInformation(
            "Profilo di scorciatoie attivo: {Name} ({Count} assegnazioni, {Total} profili disponibili)",
            Active.Name, Active.Bindings.Count, _profiles.Count);
    }

    /// <summary>
    /// Assegna ai profili personali la combinazione predefinita dei comandi che non
    /// hanno mai conosciuto.
    /// </summary>
    /// <returns><c>true</c> se qualcosa è cambiato e il file va riscritto.</returns>
    /// <remarks>
    /// Riguarda i profili creati dall'operatore, che una versione nuova non può
    /// rigenerare senza distruggere il suo lavoro. Vedi
    /// <see cref="HotkeyProfile.AddMissingDefaults"/> per i tre vincoli che la
    /// migrazione rispetta.
    /// </remarks>
    private bool MigrateUserProfiles()
    {
        var changed = false;

        foreach (var profile in _profiles.Where(p => !HotkeyProfile.IsBuiltIn(p.Name)))
        {
            var added = profile.AddMissingDefaults();
            if (added.Count == 0) continue;

            changed = true;

            _log.LogInformation(
                "Profilo {Name}: assegnata la combinazione predefinita a {Count} comandi mai conosciuti ({Commands})",
                profile.Name,
                added.Count,
                string.Join(", ", added));
        }

        return changed;
    }

    private bool Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var json = HotkeyProfileSerializer.Serialize(_profiles, Active.Name);
            var temporary = FilePath + ".tmp";

            File.WriteAllText(temporary, json, Encoding.UTF8);

            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);

            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Salvataggio dei tasti rapidi non riuscito");
            return false;
        }
    }

    private void TryBackupCorruptedFile()
    {
        try
        {
            var backup = FilePath + $".corrotto-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(FilePath, backup, overwrite: true);
            _log.LogInformation("File dei tasti rapidi precedente conservato in {Path}", backup);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Copia di sicurezza del file dei tasti rapidi non riuscita");
        }
    }

    private HotkeyProfile? Find(string? name)
        => name is null ? null : _profiles.FirstOrDefault(p => Same(p.Name, name));

    private static bool Same(string? left, string? right)
        => string.Equals(left, right, StringComparison.CurrentCultureIgnoreCase);
}
