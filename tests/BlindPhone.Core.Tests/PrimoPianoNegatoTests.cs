using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica l'avviso dato quando Windows nega il primo piano alla finestra.
/// </summary>
/// <remarks>
/// La finestra compare ma i tasti restano al programma di prima: chi guarda non se ne
/// accorge, chi non vede nemmeno. È il caso in cui F2 premuto per rispondere finisce
/// dentro Esplora file e rinomina un file. L'avviso deve dire che cosa è successo e
/// quale tasto funziona lo stesso.
/// </remarks>
public sealed class PrimoPianoNegatoTests
{
    [Fact]
    public void DiceQualeTastoFunzionaComunque()
    {
        var profilo = HotkeyProfile.CreateDefault();

        var avviso = ForegroundDeniedNotice.Build(profilo);
        var globale = profilo.Get(HotkeyCommand.Answer, HotkeyScope.Global)!.SpeechText;

        Assert.Contains(globale, avviso, StringComparison.Ordinal);
    }

    [Fact]
    public void NonRimandaAlTastoLocaleCheEProprioQuelloCheNonArriva()
    {
        // Il tasto locale e' quello che l'operatore ha appena premuto senza effetto:
        // ripeterglielo sarebbe il consiglio peggiore possibile.
        var profilo = HotkeyProfile.CreateDefault();
        var avviso = ForegroundDeniedNotice.Build(profilo);

        var locale = profilo.Get(HotkeyCommand.Answer, HotkeyScope.Local)!.SpeechText;
        var globale = profilo.Get(HotkeyCommand.Answer, HotkeyScope.Global)!.SpeechText;

        // Il nome del tasto locale compare dentro quello globale ("Control Maiusc F2"
        // contiene "F2"): si verifica che non compaia da solo.
        var senzaGlobale = avviso.Replace(globale, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(locale, senzaGlobale, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaScorciatoiaGlobaleDiceDiAssegnarla()
    {
        // Senza una scorciatoia globale non resta nessun tasto che funzioni: l'unica
        // cosa utile e' mandare l'operatore ad assegnarla, non lasciarlo premere.
        var profilo = HotkeyProfile.CreateDefault();
        profilo.Remove(HotkeyCommand.Answer, HotkeyScope.Global);

        var avviso = ForegroundDeniedNotice.Build(profilo);

        Assert.Contains("Assegna", avviso, StringComparison.Ordinal);
        Assert.Contains("tasti rapidi", avviso, StringComparison.Ordinal);
    }

    [Fact]
    public void SeguelLaRimappaturaDellOperatore()
    {
        // La combinazione citata deve essere quella configurata adesso: un avviso che
        // nomina il tasto predefinito manda a premere qualcosa che non fa niente.
        var profilo = HotkeyProfile.CreateDefault();
        profilo.Set(new HotkeyBinding(
            HotkeyCommand.Answer, "F4", HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyScope.Global));

        var avviso = ForegroundDeniedNotice.Build(profilo);

        Assert.Contains(
            profilo.Get(HotkeyCommand.Answer, HotkeyScope.Global)!.SpeechText,
            avviso,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiceCheECambiatoIlPrimoPianoENonCheLaChiamataEPersa()
    {
        // L'avviso arriva mentre il telefono squilla: deve spiegare il fatto - i tasti
        // vanno altrove - senza far credere che la chiamata sia caduta.
        var avviso = ForegroundDeniedNotice.Build(HotkeyProfile.CreateDefault());

        Assert.Contains("primo piano", avviso, StringComparison.Ordinal);
        Assert.DoesNotContain("persa", avviso, StringComparison.OrdinalIgnoreCase);
    }
}
