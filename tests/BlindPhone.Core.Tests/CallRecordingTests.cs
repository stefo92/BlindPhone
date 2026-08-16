using BlindPhone.Core.Domain;
using BlindPhone.Core.Services;
using BlindPhone.Core.Speech;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica dove finiscono le registrazioni e che cosa sente chi è in linea.
/// </summary>
/// <remarks>
/// L'avviso all'interlocutore non è una cortesia: informare l'interessato prima che
/// la raccolta cominci è ciò che rende lecita la registrazione. Le sue parole si
/// collaudano come si collauda un requisito, non come si rilegge un'etichetta.
/// </remarks>
public sealed class CallRecordingTests
{
    private static readonly DateTimeOffset Quando = new(2026, 8, 8, 9, 14, 32, TimeSpan.FromHours(2));

    [Fact]
    public void LaCartellaStaDentroDocumenti()
    {
        var cartella = CallRecordingPaths.Folder(@"C:\Utenti\mario\Documenti");

        Assert.Equal(@"C:\Utenti\mario\Documenti\Registrazione Chiamate", cartella);
    }

    [Fact]
    public void IlNomeDelFileCominciaConLaDataEOraOrdinabile()
    {
        // L'ordine alfabetico della cartella coincide cosi' con quello cronologico:
        // e' l'unico modo per ritrovare "quella di stamattina" senza aprire i file.
        var nome = CallRecordingPaths.FileName(new CallParty("4021", "Mario Rossi"), Quando);

        Assert.StartsWith("2026-08-08 09-14-32 ", nome, StringComparison.Ordinal);
        Assert.EndsWith(".mp3", nome, StringComparison.Ordinal);
    }

    [Fact]
    public void IlNomeDelFilePortaNomeENumeroDellInterlocutore()
    {
        var nome = CallRecordingPaths.FileName(new CallParty("4021", "Mario Rossi"), Quando);

        Assert.Equal("2026-08-08 09-14-32 Mario Rossi 4021.mp3", nome);
    }

    [Fact]
    public void SenzaNomeRestaIlNumero()
    {
        var nome = CallRecordingPaths.FileName(new CallParty("3491234567"), Quando);

        Assert.Equal("2026-08-08 09-14-32 3491234567.mp3", nome);
    }

    [Fact]
    public void SenzaControparteRestaLaSolaDataEOra()
    {
        Assert.Equal("2026-08-08 09-14-32.mp3", CallRecordingPaths.FileName(null, Quando));
    }

    [Fact]
    public void ICaratteriVietatiDiventanoSpaziInvecheDiSparire()
    {
        // Un nome che arriva dall'intestazione SIP puo' contenere qualunque cosa.
        // Togliendo i separatori si incollerebbero fra loro parole che erano distinte.
        var nome = CallRecordingPaths.FileName(new CallParty("4021", "Ufficio Tecnico / Reparto A"), Quando);

        Assert.DoesNotContain("/", nome, StringComparison.Ordinal);
        Assert.Contains("Ufficio Tecnico Reparto A", nome, StringComparison.Ordinal);
    }

    [Fact]
    public void UnNomeLunghissimoVieneAccorciato()
    {
        // Il percorso completo ha un tetto: un nome smisurato farebbe fallire la
        // creazione del file mentre la conversazione e' in corso, cioe' quando non
        // c'e' modo di rimediare.
        var lunghissimo = new string('A', 300);
        var nome = CallRecordingPaths.FileName(new CallParty("4021", lunghissimo), Quando);

        Assert.True(nome.Length < 100, $"nome lungo {nome.Length} caratteri");
    }

    [Fact]
    public void IlPercorsoCompletoMetteInsiemeCartellaENome()
    {
        var percorso = CallRecordingPaths.FileFor(
            @"C:\Utenti\mario\Documenti", new CallParty("4021", "Mario Rossi"), Quando);

        Assert.Equal(
            @"C:\Utenti\mario\Documenti\Registrazione Chiamate\2026-08-08 09-14-32 Mario Rossi 4021.mp3",
            percorso);
    }

    // -----------------------------------------------------------------------
    // Che cosa sente chi è in linea
    // -----------------------------------------------------------------------

    [Fact]
    public void LAvvisoDiAvvioEBreveENonNominaIlProgramma()
    {
        // Chi lo sente sta parlando con un ufficio, non con un softphone: nessun
        // riferimento a tasti, a BlindPhone o a come funziona il centralino.
        var testo = AnnouncementTexts.RecordingStartedForRemote;

        Assert.Equal("Avvio della registrazione", testo);
        Assert.DoesNotContain("BlindPhone", testo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LAvvisoDiFineDiceQuantoEStatoRegistrato()
    {
        // Senza la durata, "si stava registrando" resta un'informazione senza confini.
        var testo = AnnouncementTexts.RecordingStoppedForRemote(TimeSpan.FromSeconds(95));

        Assert.Equal("Registrazione interrotta, 1 minuto e 35", testo);
    }

    [Fact]
    public void SenzaAvvisoLOperatoreVieneAvvertitoCheTocaALui()
    {
        var con = AnnouncementTexts.RecordingStartedForOperator(true, "Alt R");
        var senza = AnnouncementTexts.RecordingStartedForOperator(false, "Alt R");

        Assert.Contains("e' stato avvisato", con, StringComparison.Ordinal);
        Assert.Contains("SENZA avviso", senza, StringComparison.Ordinal);
        Assert.Contains("informalo tu", senza, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EntrambiGliAnnunciCitanoIlTastoPerFermare()
    {
        Assert.Contains("Alt R", AnnouncementTexts.RecordingStartedForOperator(true, "Alt R"), StringComparison.Ordinal);
        Assert.Contains("Alt R", AnnouncementTexts.RecordingStartedForOperator(false, "Alt R"), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Il tasto
    // -----------------------------------------------------------------------

    [Fact]
    public void LaRegistrazioneEAssegnataAdAltR()
    {
        var profilo = HotkeyProfile.CreateDefault();
        var binding = profilo.Get(HotkeyCommand.ToggleCallRecording, HotkeyScope.Local);

        Assert.NotNull(binding);
        Assert.Equal("R", binding.Key);
        Assert.Equal(HotkeyModifiers.Alt, binding.Modifiers);
    }

    [Fact]
    public void AltRNonSiSovrapponeARichiamaLUltimoNumero()
    {
        var profilo = HotkeyProfile.CreateDefault();

        var registra = profilo.Get(HotkeyCommand.ToggleCallRecording, HotkeyScope.Local);
        var richiama = profilo.Get(HotkeyCommand.RedialLast, HotkeyScope.Local);

        Assert.NotNull(registra);
        Assert.NotNull(richiama);
        Assert.NotEqual(registra.ConflictKey, richiama.ConflictKey);
    }
}
