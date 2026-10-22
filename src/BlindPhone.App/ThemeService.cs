using System.Windows;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace BlindPhone.App;

/// <summary>
/// Applica il tema dei colori a tutte le finestre aperte.
/// </summary>
/// <remarks>
/// <para>
/// I colori del prodotto vivono in tre dizionari — <c>Temi\Chiaro.xaml</c>,
/// <c>Temi\Scuro.xaml</c>, <c>Temi\Sistema.xaml</c> — che dichiarano le stesse
/// chiavi. Cambiare tema significa sostituire il dizionario in testa alle risorse
/// dell'applicazione: le finestre che richiamano i pennelli con
/// <c>DynamicResource</c> si ridipingono da sole, senza riavviare e senza perdere una
/// chiamata in corso.
/// </para>
/// <para>
/// <b>Il contrasto elevato vince sulla scelta dell'utente, sempre.</b> Non è una
/// svista di priorità: chi lo usa lo ha configurato in Windows su misura per la
/// propria vista, e un tema scritto qui — per quanto verificato — sarebbe l'unica
/// finestra della macchina a non rispettarlo. Quando Windows è in contrasto elevato
/// si carica il tema di sistema qualunque cosa dicano le impostazioni, e non si
/// chiede conferma.
/// </para>
/// <para>
/// <b>La barra del titolo segue il tema a parte.</b> Non è contenuto della finestra ma
/// cornice disegnata da Windows, e un dizionario non la raggiunge: vedi
/// <see cref="TitleBarTheme"/>.
/// </para>
/// </remarks>
public sealed class ThemeService : IDisposable
{
    private const string ChiaveTemaWindows =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly ILogger<ThemeService> _log;
    private readonly ResourceDictionary _resources;

    private AppTheme _scelta = AppTheme.FollowSystem;
    private string? _applicato;
    private bool _disposed;

    /// <summary>Crea il servizio.</summary>
    /// <param name="resources">
    /// Risorse dell'applicazione in cui innestare il tema; normalmente
    /// <c>Application.Current.Resources</c>.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    public ThemeService(ResourceDictionary resources, ILogger<ThemeService>? logger = null)
    {
        _resources = resources;
        _log = logger ?? NullLogger<ThemeService>.Instance;

        // Windows avvisa quando l'utente cambia tema o accende il contrasto elevato.
        // Senza questo, chi passa al tema scuro di sera si ritrova l'unica finestra
        // rimasta chiara e deve riavviare il posto operatore per allinearla — cioe'
        // chiudere il telefono mentre e' in servizio.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Nome del dizionario attualmente in vigore.</summary>
    public string? Current => _applicato;

    /// <summary>Se Windows è in modalità contrasto elevato.</summary>
    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>Applica un tema.</summary>
    /// <param name="theme">Scelta dell'utente.</param>
    public void Apply(AppTheme theme)
    {
        _scelta = theme;
        ApplyResolved();
    }

    private void ApplyResolved()
    {
        var nome = Resolve(_scelta);

        // Rimettere lo stesso dizionario farebbe ricalcolare ogni DynamicResource di
        // ogni finestra aperta per nulla, e durante una conversazione l'interfaccia
        // deve stare ferma.
        if (string.Equals(nome, _applicato, StringComparison.Ordinal)) return;

        var dizionario = new ResourceDictionary
        {
            Source = new Uri($"Temi/{nome}.xaml", UriKind.Relative),
        };

        // SI CERCA IL DIZIONARIO PER NOME, non per cartella.
        //
        // In Temi\ vivono anche gli stili dei controlli, che non sono un tema e non
        // vanno mai tolti: un confronto sul solo prefisso della cartella li avrebbe
        // presi per primi, e a ogni cambio di tema la finestra avrebbe perso tutti i
        // modelli dei controlli insieme ai colori.
        var precedente = _resources.MergedDictionaries.FirstOrDefault(IsTheme);

        if (precedente is not null) _resources.MergedDictionaries.Remove(precedente);

        _resources.MergedDictionaries.Insert(0, dizionario);
        _applicato = nome;

        // La barra del titolo la disegna Windows e i dizionari non la raggiungono: il
        // suo colore si chiede a parte, altrimenti resta chiara sopra il tema scuro.
        TitleBarTheme.Set(string.Equals(nome, "Scuro", StringComparison.Ordinal));

        _log.LogInformation(
            "Tema dei colori: {Tema} (scelta {Scelta}, contrasto elevato {Contrasto})",
            nome, _scelta, HighContrast);
    }

    /// <summary>Nomi dei tre dizionari dei colori, gli unici sostituibili.</summary>
    private static readonly string[] Dizionari = ["Chiaro", "Scuro", "Sistema"];

    private static bool IsTheme(ResourceDictionary dictionary)
    {
        var sorgente = dictionary.Source?.OriginalString;
        if (sorgente is null) return false;

        return Dizionari.Any(nome =>
            sorgente.EndsWith($"Temi/{nome}.xaml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Decide quale dizionario serve, data la scelta e lo stato di Windows.</summary>
    /// <param name="theme">Scelta dell'utente.</param>
    private static string Resolve(AppTheme theme)
    {
        if (HighContrast) return "Sistema";

        return theme switch
        {
            AppTheme.Light => "Chiaro",
            AppTheme.Dark => "Scuro",
            _ => WindowsPrefersDark() ? "Scuro" : "Chiaro",
        };
    }

    /// <summary>
    /// Legge dal registro se Windows è impostato sul tema scuro per le applicazioni.
    /// </summary>
    /// <remarks>
    /// WPF non espone questa preferenza: <c>SystemColors</c> segue i colori classici
    /// di Win32, che restano chiari anche con Windows in modalità scura. È il motivo
    /// per cui un'applicazione WPF non aggiornata resta bianca in mezzo a un sistema
    /// tutto scuro, e il motivo per cui questa lettura esiste.
    /// </remarks>
    private static bool WindowsPrefersDark()
    {
        try
        {
            using var chiave = Registry.CurrentUser.OpenSubKey(ChiaveTemaWindows);

            // Assente su installazioni vecchie o irrigidite da criteri: in dubbio si
            // resta chiari, che e' il comportamento di sempre.
            return chiave?.GetValue("AppsUseLightTheme") is int valore && valore == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility))
        {
            return;
        }

        // L'avviso arriva da un thread di sistema: toccare le risorse
        // dell'applicazione da li' significa toccare l'interfaccia fuori dal suo
        // thread, che e' un guasto che si presenta una volta su venti.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            try
            {
                ApplyResolved();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Riapplicazione del tema dopo il cambio di Windows non riuscita");
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
