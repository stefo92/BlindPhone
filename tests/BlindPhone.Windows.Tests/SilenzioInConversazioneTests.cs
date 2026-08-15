using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifica la migrazione che toglie il silenzio durante la conversazione.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da una segnalazione dal campo: <i>«gli annunci cessano da quando squilla la
/// chiamata a quando termina»</i>. Fra la risposta e la chiusura l'operatore non
/// vedente restava senza nessuna notizia, e sono proprio i minuti in cui le notizie
/// contano di più — una seconda chiamata che entra, un trasferimento che fallisce, la
/// registrazione che si interrompe.
/// </para>
/// <para>
/// Cambiare il solo valore predefinito non sarebbe bastato: una postazione già in uso
/// ha <c>settings.json</c> scritto con dentro il vecchio <c>true</c>, e avrebbe
/// continuato a tacere. Ma quel <c>true</c> non è quasi mai una scelta: è il valore
/// che il prodotto ci ha messo da solo quando era l'impostazione predefinita.
/// </para>
/// </remarks>
public sealed class SilenzioInConversazioneTests
{
    private static AppSettings ConfigurazioneEsistente(int schema, bool silenzio) => new()
    {
        SchemaVersion = schema,
        Accessibility = new AccessibilitySettings { SilenceDuringConversation = silenzio },
        SipAccounts = [new SipSettings { Id = "a", Username = "200", Domain = "pbx" }],
        DefaultSipAccountId = "a",
    };

    [Fact]
    public void UnaPostazioneGiaInUsoSmetteDiTacereDuranteLaConversazione()
    {
        var migrata = ConfigurazioneEsistente(schema: 3, silenzio: true).Migrate();

        Assert.False(migrata.Accessibility.SilenceDuringConversation);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrata.SchemaVersion);
    }

    [Fact]
    public void UnaConfigurazioneNuovaNasceGiaSenzaSilenzio()
    {
        Assert.False(new AppSettings().Accessibility.SilenceDuringConversation);
    }

    [Fact]
    public void ChiRiattivaIlSilenzioSeLoTiene()
    {
        // Dopo il primo salvataggio la versione di schema e' quella corrente: la
        // migrazione non deve trasformarsi in una preferenza che torna da sola a ogni
        // avvio, togliendo la scelta a chi l'ha fatta davvero.
        var scelta = ConfigurazioneEsistente(AppSettings.CurrentSchemaVersion, silenzio: true);

        Assert.True(scelta.Migrate().Accessibility.SilenceDuringConversation);
    }

    [Fact]
    public void LaMigrazioneEIdempotente()
    {
        var una = ConfigurazioneEsistente(schema: 2, silenzio: true).Migrate();
        var due = una.Migrate();

        Assert.False(due.Accessibility.SilenceDuringConversation);
        Assert.Equal(una.SchemaVersion, due.SchemaVersion);
    }
}
