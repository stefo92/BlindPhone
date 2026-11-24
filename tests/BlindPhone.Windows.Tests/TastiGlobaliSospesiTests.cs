using System.Runtime.InteropServices;
using BlindPhone.App;
using BlindPhone.Core.Domain;
using BlindPhone.Infrastructure;
using Xunit;

namespace BlindPhone.Windows.Tests;

/// <summary>
/// Verifiche sulla sospensione e sulla prova delle scorciatoie globali presso Windows.
/// </summary>
/// <remarks>
/// <para>
/// Sono prove vere, con <c>RegisterHotKey</c>: la sospensione serve solo se Windows
/// libera davvero la combinazione, e la prova solo se Windows risponde davvero che è
/// occupata. Un registratore finto direbbe sì a entrambe le cose per costruzione.
/// </para>
/// <para>
/// «Un altro programma» qui è un secondo registratore, con una finestra sua: per
/// Windows è indistinguibile da un processo diverso, perché la combinazione appartiene
/// a una finestra e due finestre non possono tenerla insieme.
/// </para>
/// <para>
/// Le combinazioni usate sono Control Alt Maiusc più F21, F22, F23 e F24, che nessuna
/// tastiera da ufficio ha e nessun programma comune registra. Se una è comunque già
/// presa sulla macchina che esegue le prove, la prova si salta invece di fallire: non
/// dice niente di BlindPhone. Ogni prova usa la sua, così non si disturbano.
/// </para>
/// </remarks>
public sealed class TastiGlobaliSospesiTests
{
    private const HotkeyModifiers TreModificatori =
        HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;

    private const int WmHotkey = 0x0312;

    [Fact]
    public void LaSospensioneLiberaLeCombinazioniELaRipresaLeRiprende()
    {
        using var blindPhone = new Postazione();
        using var altro = new Postazione();
        var tasto = Globale(HotkeyCommand.Answer, "F21");

        RegistraOSalta(blindPhone, tasto);
        Assert.True(altro.Registratore.IsTakenElsewhere(tasto, Vk(tasto)));

        using (blindPhone.Registratore.Suspend())
        {
            // Finche' la finestra di cattura e' aperta, la combinazione deve essere
            // libera: e' cio' che le permette di arrivare all'area di cattura.
            Assert.True(blindPhone.Registratore.IsSuspended);
            Assert.False(altro.Registratore.IsTakenElsewhere(tasto, Vk(tasto)));
        }

        Assert.False(blindPhone.Registratore.IsSuspended);
        Assert.True(altro.Registratore.IsTakenElsewhere(tasto, Vk(tasto)));
    }

    [Fact]
    public void LeSospensioniAnnidateRiprendonoSoloAllUltimoRilascio()
    {
        using var blindPhone = new Postazione();
        using var altro = new Postazione();
        var tasto = Globale(HotkeyCommand.HangUp, "F22");

        RegistraOSalta(blindPhone, tasto);

        var esterna = blindPhone.Registratore.Suspend();
        var interna = blindPhone.Registratore.Suspend();

        interna.Dispose();
        interna.Dispose();

        // La prova di una combinazione fatta mentre la cattura e' aperta usa una
        // sospensione sua: rilasciandola non deve far tornare le scorciatoie sotto le
        // dita dell'operatore che sta ancora premendo.
        Assert.False(altro.Registratore.IsTakenElsewhere(tasto, Vk(tasto)));

        esterna.Dispose();
        Assert.True(altro.Registratore.IsTakenElsewhere(tasto, Vk(tasto)));
    }

    [Fact]
    public void LaProvaNonScambiaLeCombinazioniDiBlindPhonePerQuelleDiUnAltroProgramma()
    {
        using var blindPhone = new Postazione();
        using var altro = new Postazione();
        var tasto = Globale(HotkeyCommand.ToggleMute, "F23");

        RegistraOSalta(blindPhone, tasto);

        // Spostare una globale da un comando di BlindPhone a un altro e' proprio il
        // caso da permettere: la prova non deve dire "la tiene un altro programma".
        var spostata = tasto with { Command = HotkeyCommand.Answer };
        Assert.False(blindPhone.Registratore.IsTakenElsewhere(spostata, Vk(spostata)));

        // E dopo la prova la combinazione e' ancora di BlindPhone.
        Assert.True(altro.Registratore.IsTakenElsewhere(tasto, Vk(tasto)));
    }

    [Fact]
    public void LaProvaRiconosceUnaCombinazioneTenutaDaUnAltroProgramma()
    {
        using var blindPhone = new Postazione();
        using var altro = new Postazione();
        var tasto = Globale(HotkeyCommand.Answer, "F24");

        RegistraOSalta(altro, tasto);

        Assert.True(blindPhone.Registratore.IsTakenElsewhere(tasto, Vk(tasto)));
    }

    [Fact]
    public void LaCombinazioneRiprendeIlSuoComandoDopoLaSospensione()
    {
        using var blindPhone = new Postazione();
        var tasto = Globale(HotkeyCommand.ToggleHold, "F21");

        RegistraOSalta(blindPhone, tasto);

        HotkeyCommand? arrivato = null;
        blindPhone.Registratore.HotkeyPressed += (_, comando) => arrivato = comando;

        using (blindPhone.Registratore.Suspend())
        {
        }

        // Il primo identificativo di un registratore nuovo e' 1. Dopo la ripresa il
        // messaggio di Windows con quell'identificativo deve trovare lo stesso comando:
        // altrimenti la scorciatoia tornerebbe registrata e muta.
        Assert.True(blindPhone.Registratore.HandleMessage(WmHotkey, 1));
        Assert.Equal(HotkeyCommand.ToggleHold, arrivato);
    }

    [Fact]
    public void UnaCombinazioneRegistrataDuranteLaSospensioneArrivaAllaRipresa()
    {
        using var blindPhone = new Postazione();
        using var altro = new Postazione();
        var tasto = Globale(HotkeyCommand.Swap, "F22");

        // Se la combinazione e' gia' presa sulla macchina, la prova non dice nulla.
        Assert.SkipWhen(
            altro.Registratore.IsTakenElsewhere(tasto, Vk(tasto)),
            $"{tasto.DisplayText} e' gia' registrata da un altro programma su questa macchina");

        using (blindPhone.Registratore.Suspend())
        {
            Assert.True(blindPhone.Registratore.Register(tasto, Vk(tasto)).IsClear);
            Assert.False(altro.Registratore.IsTakenElsewhere(tasto, Vk(tasto)));
        }

        Assert.True(altro.Registratore.IsTakenElsewhere(tasto, Vk(tasto)));
    }

    // -----------------------------------------------------------------------
    // Codice del tasto virtuale
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Pause", TreModificatori, 0x03)]
    [InlineData("Pause", HotkeyModifiers.Alt, 0x13)]
    [InlineData("F2", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x71)]
    [InlineData("M", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4D)]
    [InlineData("Alt B", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0)]
    public void IlCodiceDelTastoEQuelloCheLaTastieraProduce(string tasto, HotkeyModifiers modificatori, int atteso)
    {
        // 0x03 e' Interr: con Control giu' il tasto Pausa produce quello, e una globale
        // dichiarata con 0x13 non scatterebbe mai. E' la postazione fuori servizio,
        // Control Alt Maiusc Pausa. Un nome che non e' un tasto da' 0, e la finestra
        // principale lo salta invece di registrare un tasto a caso.
        var binding = new HotkeyBinding(HotkeyCommand.ToggleServicePause, tasto, modificatori, HotkeyScope.Global);

        Assert.Equal((uint)atteso, TastiVirtuali.Codice(binding));
    }

    // -----------------------------------------------------------------------
    // Pressioni mancate nella finestra di cattura
    // -----------------------------------------------------------------------

    [Fact]
    public void UnRilascioSenzaPressioneEUnaCombinazionePresaAltrove()
    {
        var pressioni = new PressioniMancate();
        pressioni.Opened([]);

        // Control e Maiusc arrivano, la F9 no: la tiene un altro programma.
        Assert.True(pressioni.ReleasedWithoutPress("F9"));
    }

    [Fact]
    public void UnTastoPremutoERilasciatoNonEUnaPressioneMancata()
    {
        var pressioni = new PressioniMancate();
        pressioni.Opened([]);

        pressioni.Pressed("F9");

        Assert.False(pressioni.ReleasedWithoutPress("F9"));

        // Un secondo rilascio senza una seconda pressione invece si': e' un'altra
        // combinazione con la F9, presa altrove.
        Assert.True(pressioni.ReleasedWithoutPress("F9"));
    }

    [Fact]
    public void IlTastoCheHaApertoLaFinestraNonEUnaPressioneMancata()
    {
        // Invio apre la finestra dalla tabella e si rilascia a finestra gia' aperta;
        // Alt A la apre dal pulsante Riassegna. Senza questa eccezione, ogni apertura
        // direbbe che Invio e' preso da un altro programma.
        var pressioni = new PressioniMancate();
        pressioni.Opened(["Return", "A", "LeftAlt"]);

        Assert.False(pressioni.ReleasedWithoutPress("Return"));
        Assert.False(pressioni.ReleasedWithoutPress("A"));

        // Solo il primo rilascio: dopo, e' una pressione come le altre.
        Assert.True(pressioni.ReleasedWithoutPress("Return"));
    }

    [Fact]
    public void UnTastoGiuAllAperturaEPoiPremutoContaUnaVolta()
    {
        var pressioni = new PressioniMancate();
        pressioni.Opened(["Return"]);

        pressioni.Pressed("Return");

        Assert.False(pressioni.ReleasedWithoutPress("Return"));
        Assert.True(pressioni.ReleasedWithoutPress("Return"));
    }

    // -----------------------------------------------------------------------

    private static HotkeyBinding Globale(HotkeyCommand comando, string tasto)
        => new(comando, tasto, TreModificatori, HotkeyScope.Global);

    private static uint Vk(HotkeyBinding binding) => TastiVirtuali.Codice(binding);

    private static void RegistraOSalta(Postazione postazione, HotkeyBinding tasto)
    {
        var esito = postazione.Registratore.Register(tasto, Vk(tasto));

        Assert.SkipUnless(
            esito.IsClear,
            $"{tasto.DisplayText} non si registra su questa macchina: {esito.Message}");
    }

    /// <summary>
    /// Un registratore con una finestra di soli messaggi, come quella della finestra
    /// principale ma senza nulla a schermo.
    /// </summary>
    /// <remarks>
    /// Windows lega una scorciatoia alla finestra e al thread che l'ha creata: la
    /// finestra va creata, usata e distrutta sullo stesso thread, quello della prova.
    /// </remarks>
    private sealed class Postazione : IDisposable
    {
        private static readonly nint HwndMessage = -3;

        private readonly nint _finestra;

        public Postazione()
        {
            _finestra = CreateWindowEx(0, "STATIC", "BlindPhone prova", 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);
            Assert.NotEqual(0, _finestra);

            Registratore = new GlobalHotkeyRegistrar();
            Registratore.Attach(_finestra);
        }

        public GlobalHotkeyRegistrar Registratore { get; }

        public void Dispose()
        {
            Registratore.Dispose();
            DestroyWindow(_finestra);
        }

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(nint hwnd);
    }
}
