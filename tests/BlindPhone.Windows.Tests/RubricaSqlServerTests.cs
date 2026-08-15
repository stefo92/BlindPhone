using BlindPhone.App;
using BlindPhone.Contacts.SqlServer;
using BlindPhone.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Collegamento della rubrica a Microsoft SQL Server.
/// </summary>
/// <remarks>
/// Si verifica ciò che non richiede un server acceso: come viene composto il
/// collegamento, che cosa esce dal deposito protetto e come vengono tradotti gli
/// errori. La lettura vera di una tabella si collauda sul campo, contro il server
/// dell'ente.
/// </remarks>
public sealed class RubricaSqlServerTests
{
    private static SqlServerOptions Opzioni(bool windows = true) => new()
    {
        Server = "sqlsrv.ente.local\\GESTIONALE",
        Database = "Rubrica",
        Authentication = windows ? SqlServerAuthentication.Windows : SqlServerAuthentication.SqlLogin,
        Username = windows ? null : "blindphone",
        Table = "dbo.Contatti",
    };

    [Fact]
    public void IlTrafficoVerraCifratoAncheSuReteInterna()
    {
        // Fra postazione e server passano nomi, interni e reparti di persone
        // identificabili: e' un trattamento di dati personali, e la cifratura non
        // dev'essere una scelta da ricordarsi di fare.
        var builder = new SqlConnectionStringBuilder(Opzioni().BuildConnectionString(null));

        Assert.True(builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
    }

    [Fact]
    public void IlCollegamentoSiDichiaraDiSolaLettura()
    {
        // Su un gruppo di disponibilita' AlwaysOn permette al server di mandarci su
        // una replica secondaria invece che sul nodo che regge il gestionale.
        var builder = new SqlConnectionStringBuilder(Opzioni().BuildConnectionString(null));

        Assert.Equal(ApplicationIntent.ReadOnly, builder.ApplicationIntent);
        Assert.Equal("BlindPhone", builder.ApplicationName);
    }

    [Fact]
    public void ConLUtenzaDiWindowsNonViaggiaNessunaPassword()
    {
        var stringa = Opzioni().BuildConnectionString("questa-non-serve");
        var builder = new SqlConnectionStringBuilder(stringa);

        Assert.True(builder.IntegratedSecurity);
        Assert.Empty(builder.Password);
    }

    [Fact]
    public void LaDescrizioneNonContieneLaPassword()
    {
        // La descrizione finisce nel registro diagnostico, che viaggia nel pacchetto
        // di assistenza: la stringa di collegamento intera non ci deve mai arrivare.
        var descrizione = Opzioni(windows: false).Describe();

        Assert.DoesNotContain("segretissima", descrizione);
        Assert.Contains("blindphone", descrizione);
        Assert.Contains("Rubrica", descrizione);
    }

    [Fact]
    public void UnCollegamentoIncompletoNonSiTenta()
    {
        Assert.False(new ContactsSqlServerSettings { Server = "srv" }.IsConfigured);
        Assert.False(new ContactsSqlServerSettings { Server = "srv", Database = "db" }.IsConfigured);

        Assert.True(new ContactsSqlServerSettings
        {
            Server = "srv", Database = "db", Table = "dbo.Rubrica",
        }.IsConfigured);
    }

    [Fact]
    public void LeImpostazioniSalvateDiventanoIParametriDelCollegamento()
    {
        var salvate = new ContactsSqlServerSettings
        {
            Server = "srv",
            Database = "db",
            UseWindowsAuthentication = false,
            Username = "utente",
            Table = "dbo.Rubrica",
            TrustServerCertificate = true,
            Mapping = new ContactsColumnMapping
            {
                Extension = "numero",
                Category = "descrizione",
                FirstName = "nome",
                LastName = "cognome",
                Department = "reparto",
                Building = "padiglione",
                Floor = "piano",
                RelatedTo = "relativo",
            },
        };

        var opzioni = ContactsSyncService.ToOptions(salvate);

        Assert.Equal(SqlServerAuthentication.SqlLogin, opzioni.Authentication);
        Assert.True(opzioni.TrustServerCertificate);

        // Le otto voci sono le stesse della procedura guidata dei file: chi ha
        // imparato a importare un file sa gia' collegare un server.
        Assert.Equal("descrizione", opzioni.Mapping.Category);
        Assert.Equal("padiglione", opzioni.Mapping.Building);
        Assert.Equal("relativo", opzioni.Mapping.RelatedTo);
    }

    [Theory]
    [InlineData(18456, "rifiutato l'utenza")]
    [InlineData(4060, "non ha accesso")]
    [InlineData(229, "permesso di leggere")]
    [InlineData(53, "non risponde")]
    [InlineData(-2, "tempo previsto")]
    public void GliErroriDelServerVengonoDettiInItaliano(int numero, string atteso)
    {
        // «Login failed for user» non dice niente a chi lo ascolta una volta sola.
        // Il messaggio deve permettergli di chiamare l'amministratore sapendo cosa
        // riferire.
        var frase = SqlServerContactSource.DescribeSqlFailure(numero);

        Assert.Contains(atteso, frase);
    }

    [Fact]
    public void IlCertificatoNonVerificabileIndicaLOpzioneDaAttivare()
    {
        // E' il caso piu' comune sulle installazioni interne, ed e' l'unico in cui la
        // mossa successiva sta dentro le impostazioni di BlindPhone.
        Assert.Contains("certificato", SqlServerContactSource.DescribeSqlFailure(20));
    }

}
