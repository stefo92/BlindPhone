using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica il criterio che limita ciò che l'operatore può modificare.
/// </summary>
/// <remarks>
/// <para>
/// La regola che questi test proteggono è la precedenza: il criterio di postazione
/// vince sempre su quello dell'utente, <b>anche quando è meno restrittivo</b>. È il
/// caso che verrebbe scritto male per primo — sembra prudente far vincere il più
/// severo — ma un criterio di postazione che dichiara il pannello aperto è una
/// decisione presa da chi amministra la rete, e un file nella cartella dell'utente
/// non deve poterla contraddire in nessuna direzione.
/// </para>
/// <para>
/// L'altra è che senza password configurata non si sblocchi: sarebbe la scappatoia
/// più ovvia, e la si trova premendo Invio su un campo vuoto.
/// </para>
/// </remarks>
public sealed class ConfigurationPolicyTests
{
    private static ConfigurationPolicy Bloccato(
        PanelAccess accesso = PanelAccess.ReadOnly,
        PolicyOrigin origine = PolicyOrigin.User,
        params SettingsSection[] sezioni) =>
        new()
        {
            Access = accesso,
            LockedSections = sezioni,
            Origin = origine,
        };

    [Fact]
    public void SenzaCriterioTuttoEModificabile()
    {
        var servizio = new ConfigurationPolicyService();
        servizio.Load(null, null);

        Assert.True(servizio.CanOpenPanel);
        Assert.All(Enum.GetValues<SettingsSection>(), s => Assert.True(servizio.CanEdit(s)));
    }

    [Fact]
    public void IlCriterioDiPostazioneVinceAnchheSeMenoRestrittivo()
    {
        // E' il caso che si scriverebbe male per primo: sembra prudente far vincere
        // il piu' severo, ma la decisione di chi amministra la rete non si contraddice
        // con un file nella cartella dell'utente.
        var servizio = new ConfigurationPolicyService();

        servizio.Load(
            machine: Bloccato(PanelAccess.Open, PolicyOrigin.Machine),
            user: Bloccato(PanelAccess.Locked, PolicyOrigin.User));

        Assert.True(servizio.CanOpenPanel);
    }

    [Fact]
    public void SenzaCriterioDiPostazioneValeQuelloDellUtente()
    {
        var servizio = new ConfigurationPolicyService();
        servizio.Load(machine: null, user: Bloccato(PanelAccess.Locked));

        Assert.False(servizio.CanOpenPanel);
    }

    [Fact]
    public void LeSezioniNonElencateRestanoModificabili()
    {
        // E' il caso frequente: l'account SIP lo decide l'ufficio IT, voce e tasti
        // rapidi restano all'operatore.
        var servizio = new ConfigurationPolicyService();

        servizio.Load(
            null,
            Bloccato(PanelAccess.ReadOnly, PolicyOrigin.User, SettingsSection.SipAccounts, SettingsSection.Pbx));

        Assert.False(servizio.CanEdit(SettingsSection.SipAccounts));
        Assert.False(servizio.CanEdit(SettingsSection.Pbx));
        Assert.True(servizio.CanEdit(SettingsSection.Accessibility));
        Assert.True(servizio.CanEdit(SettingsSection.Hotkeys));
    }

    [Fact]
    public void SolaLetturaSenzaElencoBloccaTutto()
    {
        // Chi imposta la modalita' senza elencare le sezioni intende bloccare tutto:
        // e' l'interpretazione meno sorprendente.
        var servizio = new ConfigurationPolicyService();
        servizio.Load(null, Bloccato(PanelAccess.ReadOnly));

        Assert.All(Enum.GetValues<SettingsSection>(), s => Assert.False(servizio.CanEdit(s)));
    }

    [Fact]
    public void ConIlPannelloChiusoNullaEModificabile()
    {
        var servizio = new ConfigurationPolicyService();
        servizio.Load(null, Bloccato(PanelAccess.Locked));

        Assert.All(Enum.GetValues<SettingsSection>(), s => Assert.False(servizio.CanEdit(s)));
    }

    // -------------------------------------------------------------------
    // Sblocco
    // -------------------------------------------------------------------

    [Fact]
    public void LaPasswordGiustaSbloccaPerLaSessione()
    {
        var (hash, salt) = ConfigurationPolicyService.HashPassword("Segreta.2026");

        var servizio = new ConfigurationPolicyService();
        servizio.Load(null, Bloccato(PanelAccess.Locked) with { PasswordHash = hash, PasswordSalt = salt });

        Assert.False(servizio.CanOpenPanel);
        Assert.True(servizio.Unlock("Segreta.2026"));

        Assert.True(servizio.CanOpenPanel);
        Assert.True(servizio.CanEdit(SettingsSection.SipAccounts));
        Assert.True(servizio.IsUnlockedForSession);
    }

    [Fact]
    public void LaPasswordSbagliataNonSblocca()
    {
        var (hash, salt) = ConfigurationPolicyService.HashPassword("Segreta.2026");

        var servizio = new ConfigurationPolicyService();
        servizio.Load(null, Bloccato(PanelAccess.Locked) with { PasswordHash = hash, PasswordSalt = salt });

        Assert.False(servizio.Unlock("segreta.2026"));
        Assert.False(servizio.Unlock(string.Empty));
        Assert.False(servizio.Unlock(null));
        Assert.False(servizio.CanOpenPanel);
    }

    [Fact]
    public void SenzaPasswordConfigurataNonSiSblocca()
    {
        // Sarebbe la scappatoia piu' ovvia: bloccare dimenticando la password
        // renderebbe il pannello apribile da chiunque premesse Invio su un campo vuoto.
        var servizio = new ConfigurationPolicyService();
        servizio.Load(null, Bloccato(PanelAccess.Locked));

        Assert.False(servizio.Unlock(string.Empty));
        Assert.False(servizio.Unlock("qualunque cosa"));
        Assert.False(servizio.CanOpenPanel);
    }

    [Fact]
    public void SiPuoRibloccareSenzaChiudereIlProgramma()
    {
        var (hash, salt) = ConfigurationPolicyService.HashPassword("Segreta.2026");

        var servizio = new ConfigurationPolicyService();
        servizio.Load(null, Bloccato(PanelAccess.Locked) with { PasswordHash = hash, PasswordSalt = salt });

        servizio.Unlock("Segreta.2026");
        servizio.Relock();

        Assert.False(servizio.CanOpenPanel);
        Assert.False(servizio.IsUnlockedForSession);
    }

    [Fact]
    public void LaPasswordDelCriterioDiPostazioneEQuellaCheConta()
    {
        // Con un criterio di postazione attivo, la password del file dell'utente non
        // deve sbloccare niente: altrimenti basterebbe scriversene una propria.
        var postazione = ConfigurationPolicyService.HashPassword("PasswordDelReparto");
        var utente = ConfigurationPolicyService.HashPassword("MeLaSonoScrittaIo");

        var servizio = new ConfigurationPolicyService();

        servizio.Load(
            Bloccato(PanelAccess.Locked, PolicyOrigin.Machine)
                with { PasswordHash = postazione.Hash, PasswordSalt = postazione.Salt },
            Bloccato(PanelAccess.Locked)
                with { PasswordHash = utente.Hash, PasswordSalt = utente.Salt });

        Assert.False(servizio.Unlock("MeLaSonoScrittaIo"));
        Assert.True(servizio.Unlock("PasswordDelReparto"));
    }

    [Fact]
    public void DueImpronteDellaStessaPasswordSonoDiverse()
    {
        // Il sale e' casuale: due postazioni con la stessa password non producono la
        // stessa impronta, quindi un elenco precalcolato non serve a nulla.
        var una = ConfigurationPolicyService.HashPassword("Segreta.2026");
        var due = ConfigurationPolicyService.HashPassword("Segreta.2026");

        Assert.NotEqual(una.Hash, due.Hash);
        Assert.NotEqual(una.Salt, due.Salt);

        Assert.True(ConfigurationPolicyService.Verify("Segreta.2026", una.Hash, una.Salt));
        Assert.True(ConfigurationPolicyService.Verify("Segreta.2026", due.Hash, due.Salt));
    }

    [Fact]
    public void UnaImprontaScrittaMaleNonFaCadereIlPannello()
    {
        // Un file modificato a mano non deve produrre un errore che impedisce di
        // aprire le impostazioni: si rifiuta lo sblocco e basta.
        Assert.False(ConfigurationPolicyService.Verify("qualcosa", "non esadecimale", "non base 64"));
    }

    // -------------------------------------------------------------------
    // Testi
    // -------------------------------------------------------------------

    [Fact]
    public void LaDescrizioneComprendeIlMessaggioDellAmministratore()
    {
        // E' l'unica parte del blocco che l'operatore sente: senza, resta un ostacolo
        // senza spiegazione e senza nessuno a cui rivolgersi.
        var criterio = Bloccato(PanelAccess.Locked) with
        {
            Note = "Configurazione gestita dall'ufficio IT, interno 4000",
        };

        Assert.Contains("interno 4000", criterio.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void LeSezioniSiNominanoInItaliano()
    {
        var criterio = Bloccato(
            PanelAccess.ReadOnly, PolicyOrigin.User, SettingsSection.SipAccounts, SettingsSection.Pbx);

        var testo = criterio.Describe();

        Assert.Contains("account SIP", testo, StringComparison.Ordinal);
        Assert.Contains("centralino", testo, StringComparison.Ordinal);
        Assert.DoesNotContain("SipAccounts", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaLimitiLaDescrizioneLoDice()
        => Assert.Contains("modificabili", ConfigurationPolicy.Unrestricted.Describe(), StringComparison.Ordinal);
}
