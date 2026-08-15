using System.Windows;
using System.Windows.Input;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Services;

namespace BlindPhone.App;

/// <summary>
/// Chiede la password dell'amministratore per sbloccare le impostazioni.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il messaggio dell'amministratore viene prima del campo della password</b>, e
/// non è una scelta grafica. Nel caso normale chi arriva qui non ha la password e
/// non l'avrà mai: quello che gli serve è sapere a chi rivolgersi. Un messaggio
/// messo dopo un campo che non può compilare sarebbe come non averlo scritto.
/// </para>
/// <para>
/// Lo sblocco vale per la <b>sessione corrente</b> e non viene scritto da nessuna
/// parte: chi sblocca per correggere un parametro non lascia la postazione sbloccata
/// per sempre, e soprattutto non deve doverla ribloccare ricordandosene.
/// </para>
/// </remarks>
public partial class UnlockSettingsWindow : Window
{
    private readonly ConfigurationPolicyService _policy;
    private readonly IAnnouncer _announcer;

    /// <summary>Crea la finestra.</summary>
    /// <param name="policy">Servizio che verifica la password.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    public UnlockSettingsWindow(ConfigurationPolicyService policy, IAnnouncer announcer)
    {
        InitializeComponent();

        _policy = policy;
        _announcer = announcer;

        var criterio = policy.Effective;
        var postazione = policy.MachinePolicy;

        TitleText.Text = criterio.Access switch
        {
            Core.Domain.PanelAccess.Locked => "Le impostazioni sono bloccate dall'amministratore",
            Core.Domain.PanelAccess.ReadOnly => "Le impostazioni si possono leggere ma non modificare",
            _ => "Alcune impostazioni sono bloccate",
        };

        var testo = criterio.Describe();

        // Va detto da dove arriva il blocco: se e' di postazione, l'operatore non lo
        // aggira nemmeno modificando i propri file, e insistere gli farebbe perdere
        // tempo. Se e' dell'utente, l'assistenza puo' rimuoverlo da remoto.
        testo += postazione.Origin == Core.Domain.PolicyOrigin.Machine
            ? "\n\nIl blocco e' impostato sulla postazione e vale per tutti gli utenti: "
              + "solo un amministratore del computer puo' rimuoverlo."
            : "\n\nIl blocco e' impostato su questo profilo utente.";

        testo += criterio.HasPassword
            ? "\n\nCon la password dell'amministratore puoi sbloccare per questa sessione: "
              + "alla chiusura di BlindPhone le impostazioni tornano bloccate."
            : "\n\nNessuna password di sblocco e' configurata: le impostazioni non si possono "
              + "sbloccare da qui.";

        NoticeText.Text = HelpTextLayout.Wrap(testo);

        // Senza password configurata il campo non serve a niente: lasciarlo attivo
        // inviterebbe a provare combinazioni su una porta che non ha serratura.
        if (!criterio.HasPassword)
        {
            PasswordBox.IsEnabled = false;
            UnlockButton.IsEnabled = false;
            PasswordLabel.Content = "Password dell'amministratore (non configurata)";
        }

        Loaded += (_, _) =>
        {
            if (criterio.HasPassword) PasswordBox.Focus();
            else CancelButton.Focus();

            _announcer.Announce(Announcement.High(
                $"{TitleText.Text}. {testo.Replace("\n", " ", StringComparison.Ordinal)}",
                DateTimeOffset.Now,
                EarconKind.CommandRejected));
        };
    }

    /// <summary>Indica se lo sblocco è avvenuto.</summary>
    public bool Unlocked { get; private set; }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        TryUnlock();
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e) => TryUnlock();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void TryUnlock()
    {
        if (_policy.Unlock(PasswordBox.Password))
        {
            Unlocked = true;
            DialogResult = true;

            _announcer.Announce(Announcement.CommandResponse(
                "Impostazioni sbloccate per questa sessione", DateTimeOffset.Now));

            Close();
            return;
        }

        // Il campo si svuota e riprende il fuoco: chi non vede lo schermo altrimenti
        // ricomincia a digitare in coda a quello che aveva gia' scritto.
        PasswordBox.Clear();
        PasswordBox.Focus();

        StatusText.Text = "Password errata";

        _announcer.Announce(Announcement.High(
            "Password errata. Le impostazioni restano bloccate",
            DateTimeOffset.Now,
            EarconKind.CommandRejected));
    }
}
