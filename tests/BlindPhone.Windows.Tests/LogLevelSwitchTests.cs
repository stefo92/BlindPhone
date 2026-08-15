using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifiche sull'interruttore del livello di registro.
/// </summary>
/// <remarks>
/// È ciò che permette di alzare il dettaglio mentre il guasto si sta manifestando,
/// senza riavviare e senza far cadere le chiamate in corso. Se sbagliasse verso, il
/// registro tacerebbe proprio quando serve, oppure resterebbe dettagliato per sempre
/// accumulando dati di traffico che nessuno ha chiesto di conservare.
/// </remarks>
public sealed class LogLevelSwitchTests
{
    [Fact]
    public void IlLivelloPredefinitoENormale()
        => Assert.Equal(LogLevel.Information, new LogLevelSwitch().Minimum);

    [Fact]
    public void AlzandoIlDettaglioPassanoAncheLeRigheDiDebug()
    {
        var interruttore = new LogLevelSwitch();

        Assert.False(interruttore.IsEnabled(LogLevel.Debug));

        interruttore.Minimum = LogLevel.Debug;

        Assert.True(interruttore.IsEnabled(LogLevel.Debug));
        Assert.True(interruttore.IsEnabled(LogLevel.Information));
        Assert.False(interruttore.IsEnabled(LogLevel.Trace));
    }

    [Fact]
    public void GliErroriPassanoSempre()
    {
        var interruttore = new LogLevelSwitch(LogLevel.Error);

        Assert.True(interruttore.IsEnabled(LogLevel.Error));
        Assert.True(interruttore.IsEnabled(LogLevel.Critical));
        Assert.False(interruttore.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void IlLivelloNoneNonScriveMaiNulla()
        => Assert.False(new LogLevelSwitch(LogLevel.Trace).IsEnabled(LogLevel.None));

    [Theory]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("scritto male", LogLevel.Information)]
    [InlineData("", LogLevel.Information)]
    [InlineData(null, LogLevel.Information)]
    public void UnNomeScrittoMaleNonSpegneIlRegistro(string? nome, LogLevel atteso)
        => Assert.Equal(atteso, LogLevelSwitch.Parse(nome));

    [Fact]
    public void OgniLivelloHaUnaDescrizioneInItaliano()
    {
        foreach (var livello in new[]
                 {
                     LogLevel.Trace, LogLevel.Debug, LogLevel.Information,
                     LogLevel.Warning, LogLevel.Error,
                 })
        {
            var testo = LogLevelSwitch.Describe(livello);

            Assert.False(string.IsNullOrWhiteSpace(testo));
            Assert.NotEqual(livello.ToString(), testo);
        }
    }
}
