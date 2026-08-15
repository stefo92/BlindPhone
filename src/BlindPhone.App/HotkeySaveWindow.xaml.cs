using System.Windows;
using BlindPhone.Core.Accessibility;

namespace BlindPhone.App;

/// <summary>Destinazione scelta per il salvataggio della configurazione dei tasti.</summary>
public enum HotkeySaveKind
{
    /// <summary>Aggiorna il profilo corrente e lo applica subito.</summary>
    UpdateCurrent,

    /// <summary>Crea un nuovo profilo con le modifiche e lo attiva.</summary>
    SaveAsNew,

    /// <summary>Scrive la configurazione su un file trasferibile.</summary>
    ExportProfile,

    /// <summary>Scrive la scheda dei tasti in testo semplice.</summary>
    ExportCard,
}

/// <summary>
/// Finestra del pulsante di salvataggio della configurazione dei tasti.
/// </summary>
/// <remarks>
/// Raccoglie soltanto la scelta: l'esecuzione — scrittura del profilo, finestra di
/// scelta del file, annuncio dell'esito — resta al pannello che l'ha aperta. La
/// separazione serve a poter cambiare il modo in cui si sceglie senza toccare il
/// modo in cui si salva.
/// </remarks>
public partial class HotkeySaveWindow : Window
{
    private readonly IAnnouncer _announcer;
    private readonly string _currentProfileName;
    private readonly bool _currentIsBuiltIn;

    /// <summary>Crea la finestra.</summary>
    /// <param name="currentProfileName">Nome del profilo in modifica.</param>
    /// <param name="currentIsBuiltIn">
    /// <c>true</c> se il profilo in modifica è fornito con il prodotto e non è
    /// sovrascrivibile.
    /// </param>
    /// <param name="proposedName">Nome proposto per un nuovo profilo.</param>
    /// <param name="changeCount">Numero di assegnazioni modificate rispetto al profilo salvato.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    public HotkeySaveWindow(
        string currentProfileName,
        bool currentIsBuiltIn,
        string proposedName,
        int changeCount,
        IAnnouncer announcer)
    {
        _announcer = announcer;
        _currentProfileName = currentProfileName;
        _currentIsBuiltIn = currentIsBuiltIn;

        InitializeComponent();

        ProfileNameBox.Text = proposedName;

        var changes = changeCount switch
        {
            0 => "nessuna modifica rispetto all'ultimo salvataggio",
            1 => "una assegnazione modificata",
            _ => $"{changeCount} assegnazioni modificate",
        };

        SummaryText.Text = $"Profilo {currentProfileName}: {changes}.";

        if (currentIsBuiltIn)
        {
            // I profili forniti con il prodotto restano intatti: sono il punto di
            // ritorno quando una configurazione personale si rivela sbagliata durante
            // un turno. Si guida verso la copia invece di limitarsi a rifiutare.
            UpdateCurrentOption.IsEnabled = false;
            UpdateCurrentHint.Text =
                $"Il profilo {currentProfileName} e' fornito con BlindPhone e resta invariato: "
                + "salva una copia con un nome tuo.";

            SaveAsNewOption.IsChecked = true;
        }
        else
        {
            UpdateCurrentHint.Text = $"Le combinazioni del profilo {currentProfileName} vengono sostituite.";
        }

        Loaded += (_, _) =>
        {
            (UpdateCurrentOption.IsEnabled ? UpdateCurrentOption : SaveAsNewOption).Focus();
            UpdateEffect();

            _announcer.Announce(Announcement.CommandResponse(
                $"Salvataggio della configurazione dei tasti. {SummaryText.Text} "
                + "Scegli con le frecce fra quattro possibilita', poi Invio",
                DateTimeOffset.Now));
        };
    }

    /// <summary>Destinazione scelta.</summary>
    public HotkeySaveKind Kind { get; private set; } = HotkeySaveKind.UpdateCurrent;

    /// <summary>Nome del profilo da creare, valido solo per <see cref="HotkeySaveKind.SaveAsNew"/>.</summary>
    public string NewProfileName { get; private set; } = string.Empty;

    private void OnOptionChanged(object sender, RoutedEventArgs e) => UpdateEffect();

    /// <summary>
    /// Descrive a parole l'effetto della scelta corrente.
    /// </summary>
    /// <remarks>
    /// L'etichetta dice cosa si sceglie, questa riga dice cosa succederà: sono due
    /// informazioni diverse, e la seconda è quella che evita di sovrascrivere per
    /// sbaglio la configurazione di un collega.
    /// </remarks>
    private void UpdateEffect()
    {
        // Chiamato anche dai gestori Checked durante InitializeComponent, quando i
        // campi della finestra non esistono ancora.
        if (EffectText is null) return;

        var text = SelectedKind() switch
        {
            HotkeySaveKind.UpdateCurrent =>
                $"Il profilo {_currentProfileName} viene aggiornato e le nuove combinazioni funzionano subito.",
            HotkeySaveKind.SaveAsNew =>
                "Viene creato un profilo nuovo con le modifiche e diventa quello attivo. "
                + $"Il profilo {_currentProfileName} resta come e' adesso.",
            HotkeySaveKind.ExportProfile =>
                "Viene scritto un file da importare su un'altra postazione. La configurazione in uso non cambia.",
            _ => "Viene scritto un file di testo con l'elenco dei comandi e delle combinazioni. "
                 + "La configurazione in uso non cambia.",
        };

        LiveRegion.Update(EffectText, text);
    }

    private HotkeySaveKind SelectedKind()
    {
        if (SaveAsNewOption.IsChecked == true) return HotkeySaveKind.SaveAsNew;
        if (ExportProfileOption.IsChecked == true) return HotkeySaveKind.ExportProfile;
        if (ExportCardOption.IsChecked == true) return HotkeySaveKind.ExportCard;

        return HotkeySaveKind.UpdateCurrent;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Kind = SelectedKind();

        if (Kind == HotkeySaveKind.UpdateCurrent && _currentIsBuiltIn)
        {
            // Non dovrebbe accadere, l'opzione e' disattivata: resta come rete perche'
            // sovrascrivere un profilo predefinito toglierebbe il punto di ritorno.
            Kind = HotkeySaveKind.SaveAsNew;
        }

        if (Kind == HotkeySaveKind.SaveAsNew)
        {
            var name = ProfileNameBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                LiveRegion.Update(EffectText, "Manca il nome del nuovo profilo.");
                _announcer.Announce(Announcement.CommandResponse(
                    "Scrivi un nome per il nuovo profilo", DateTimeOffset.Now));
                ProfileNameBox.Focus();
                return;
            }

            NewProfileName = name;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
