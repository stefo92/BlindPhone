using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using BlindPhone.Core.Accessibility;
using BlindPhone.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BlindPhone.App;

/// <summary>
/// Elenco degli account SIP configurati, con il cambio rapido.
/// </summary>
/// <remarks>
/// <para>
/// Risponde a un caso concreto: un centralinista che copre due enti, o una
/// postazione condivisa a turni fra persone con interni diversi. Prima l'unico modo
/// di cambiare era riscrivere quattro campi e una password nel pannello delle
/// impostazioni, con la postazione fuori servizio nel frattempo e nessun modo di
/// tornare indietro se non riscrivendo tutto di nuovo.
/// </para>
/// <para>
/// <b>«In uso» e «all'avvio» sono due colonne diverse</b>, e la distinzione non è
/// pedanteria: si può lavorare un pomeriggio sull'account di un secondo ente senza
/// volerlo ritrovare domani mattina. Attivare e rendere predefinito restano due
/// gesti separati.
/// </para>
/// </remarks>
public partial class SipAccountsWindow : Window
{
    private readonly SipAccountSwitcher _switcher;
    private readonly IAnnouncer _announcer;
    private readonly ILogger<SipAccountsWindow> _log;
    private readonly ObservableCollection<AccountRow> _rows = [];

    /// <summary>Crea la finestra.</summary>
    /// <param name="switcher">Servizio di cambio account.</param>
    /// <param name="announcer">Pipeline di annuncio.</param>
    /// <param name="logger">Log diagnostico.</param>
    public SipAccountsWindow(
        SipAccountSwitcher switcher,
        IAnnouncer announcer,
        ILogger<SipAccountsWindow> logger)
    {
        InitializeComponent();

        _switcher = switcher;
        _announcer = announcer;
        _log = logger;

        AccountsGrid.ItemsSource = _rows;

        Loaded += (_, _) =>
        {
            Refresh();

            // Il cursore va sull'account attivo e non sulla prima riga: e' il punto da
            // cui l'operatore si orienta, e trovarcisi gia' sopra gli risparmia di
            // percorrere l'elenco per capire dov'e'.
            var indice = Math.Max(0, _rows.ToList().FindIndex(r => r.IsActive));
            GridNavigation.FocusCell(AccountsGrid, indice);

            // Esplicito e non lasciato all'evento di selezione: se il cursore fosse
            // gia' sulla riga giusta l'evento non scatterebbe, e la finestra si
            // aprirebbe senza nessuna riga scelta.
            Choose(indice, announce: false);

            _announcer.Announce(Announcement.CommandResponse(HeaderText.Text, DateTimeOffset.Now));
        };
    }

    /// <summary>Indica che l'operatore ha chiesto di aprire le impostazioni.</summary>
    public bool OpenSettingsRequested { get; private set; }

    private void Refresh()
    {
        _rows.Clear();

        var attivo = _switcher.ActiveAccountId;
        var predefinito = _switcher.DefaultAccountId;

        foreach (var account in _switcher.Accounts)
        {
            _rows.Add(new AccountRow(account, account.Id == attivo, account.Id == predefinito));
        }

        HeaderText.Text = _rows.Count switch
        {
            0 => "Nessun account SIP configurato. Aprilo dalle impostazioni, scheda Account SIP.",
            1 => "Un solo account configurato. Per aggiungerne altri: impostazioni, scheda Account SIP.",
            _ => $"{_rows.Count} account configurati. Frecce per scorrerli, barra spaziatrice per scegliere, "
                 + "Invio per attivare. La colonna Scelto dice su quale account agiscono i pulsanti.",
        };
    }

    /// <summary>
    /// L'account su cui agiscono i pulsanti.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si legge dalla colonna della scelta e <b>non da dove si trova il cursore</b>, ed
    /// è la differenza che rendeva la finestra inutilizzabile con la sola tastiera:
    /// premendo Tab per raggiungere il pulsante, la tabella perde il fuoco e con esso
    /// la nozione di cella corrente. Il pulsante rispondeva «nessun account
    /// selezionato» proprio nel momento in cui veniva premuto, cioè sempre.
    /// </para>
    /// <para>
    /// La scelta invece è un dato delle righe, non della tastiera: sopravvive a
    /// qualunque spostamento del cursore, dentro o fuori dalla tabella.
    /// </para>
    /// </remarks>
    private AccountRow? Selected() => _rows.FirstOrDefault(r => r.IsChosen);

    /// <summary>
    /// Sposta la scelta sulla riga indicata, ed è l'unico punto in cui la scelta
    /// cambia.
    /// </summary>
    /// <param name="index">Riga da scegliere.</param>
    /// <param name="announce">Se dirlo. Vale <c>false</c> quando la finestra si apre.</param>
    /// <remarks>
    /// La scelta è unica per costruzione: si spegne dappertutto e si riaccende su una
    /// riga sola. Due caselle segnate contemporaneamente in un elenco dove si può
    /// attivare un account solo sarebbero una domanda senza risposta.
    /// </remarks>
    private void Choose(int index, bool announce = true)
    {
        if (index < 0 || index >= _rows.Count) return;

        for (var i = 0; i < _rows.Count; i++) _rows[i].IsChosen = i == index;

        if (!announce) return;

        _announcer.Announce(Announcement.CommandResponse(
            $"{_rows[index].Name} scelto, interno {_rows[index].Username}", DateTimeOffset.Now));
    }

    /// <summary>
    /// Tiene la scelta allineata al cursore mentre l'operatore percorre l'elenco.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Senza questo allineamento le due nozioni potrebbero divergere, e la divergenza
    /// qui è pericolosa: l'operatore scende su un account, preme il pulsante, e la
    /// registrazione passa a un altro — quello scelto tempo prima e dimenticato.
    /// Cambiare account chiude la registrazione in corso, quindi non è un errore che
    /// si annulla.
    /// </para>
    /// <para>
    /// La barra spaziatrice resta perché è il gesto che si cerca su una casella di
    /// controllo, e perché conferma a voce quale account è scelto senza dover
    /// ripercorrere la riga.
    /// </para>
    /// </remarks>
    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => Choose(AccountsGrid.SelectedIndex, announce: false);

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            Choose(GridNavigation.CurrentRowIndex(AccountsGrid));
            return;
        }

        if (e.Key != Key.Enter) return;

        e.Handled = true;
        _ = ActivateAsync();
    }

    private void OnActivateClick(object sender, RoutedEventArgs e) => _ = ActivateAsync();

    private async Task ActivateAsync()
    {
        if (Selected() is not { } row)
        {
            Report("Nessun account selezionato");
            return;
        }

        // Il pulsante si disabilita durante il passaggio: il cambio chiude una
        // registrazione e ne apre un'altra, e due richieste sovrapposte lascerebbero
        // il motore in uno stato che nessuno sa raccontare.
        ActivateButton.IsEnabled = false;

        try
        {
            var esito = await _switcher.SwitchToAsync(row.Id).ConfigureAwait(true);

            Report(esito.Message);
            Refresh();

            var indice = Math.Max(0, _rows.ToList().FindIndex(r => r.Id == row.Id));
            GridNavigation.FocusCell(AccountsGrid, indice);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Attivazione dell'account non riuscita");
            Report("Attivazione non riuscita");
        }
        finally
        {
            ActivateButton.IsEnabled = true;
        }
    }

    private void OnDefaultClick(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row)
        {
            Report("Nessun account selezionato");
            return;
        }

        Report(_switcher.SetDefault(row.Id).Message);
        Refresh();

        var indice = Math.Max(0, _rows.ToList().FindIndex(r => r.Id == row.Id));
        GridNavigation.FocusCell(AccountsGrid, indice);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Mostra e pronuncia l'esito di un'operazione.</summary>
    /// <remarks>
    /// Entrambe le cose: la riga a schermo resta rileggibile con il cursore virtuale,
    /// l'annuncio arriva senza che l'operatore debba andarla a cercare.
    /// </remarks>
    private void Report(string message)
    {
        StatusText.Text = message;
        _announcer.Announce(Announcement.CommandResponse(message, DateTimeOffset.Now));
    }

    /// <summary>Riga dell'elenco.</summary>
    /// <remarks>
    /// «Si» e «no» al posto di caselle di spunta: una casella in una tabella viene
    /// letta come «casella di controllo, selezionata» in mezzo a celle di testo, e
    /// interrompe il ritmo della lettura per dire meno di quanto direbbe una parola.
    /// </remarks>
    private sealed class AccountRow(SipSettings account, bool isActive, bool isDefault)
        : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isChosen;

        /// <inheritdoc />
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Se è questo l'account su cui agiscono i pulsanti.
        /// </summary>
        /// <remarks>
        /// Notifica il cambiamento perché la casella di controllo a schermo lo segua:
        /// senza, la colonna mostrerebbe per sempre la scelta fatta all'apertura, cioè
        /// direbbe una cosa mentre i pulsanti ne fanno un'altra.
        /// </remarks>
        public bool IsChosen
        {
            get => _isChosen;
            set
            {
                if (_isChosen == value) return;

                _isChosen = value;
                PropertyChanged?.Invoke(
                    this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChosen)));
            }
        }

        public string Id { get; } = account.Id;

        public string Name { get; } = account.Name;

        public string Username { get; } = string.IsNullOrWhiteSpace(account.Username)
            ? "non configurato"
            : account.Username;

        public string Domain { get; } = string.IsNullOrWhiteSpace(account.Domain)
            ? "non configurato"
            : account.Domain;

        public string Transport { get; } = account.Transport.Equals("Tls", StringComparison.OrdinalIgnoreCase)
            ? "TLS cifrato"
            : account.Transport;

        public bool IsActive { get; } = isActive;

        public string ActiveText { get; } = isActive ? "si" : "no";

        public string DefaultText { get; } = isDefault ? "si" : "no";
    }
}
