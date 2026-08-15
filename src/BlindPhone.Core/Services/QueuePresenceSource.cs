using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Domain;

namespace BlindPhone.Core.Services;

/// <summary>
/// Sorgente di presenza basata sull'API del centralino.
/// </summary>
/// <remarks>
/// È la strada preferibile quando c'è: il centralino manda gli eventi da solo, per
/// tutti gli interni, senza che il posto operatore debba sottoscrivere nulla e senza
/// consumare risorse del PBX per ogni interno guardato. Richiede però un piano di
/// licenza con le API abilitate, che non tutte le installazioni hanno.
/// </remarks>
public sealed class QueuePresenceSource : IPresenceSource
{
    private readonly IQueueProvider _provider;
    private bool _started;

    /// <summary>Crea la sorgente.</summary>
    /// <param name="provider">Connettore verso il centralino.</param>
    public QueuePresenceSource(IQueueProvider provider) => _provider = provider;

    /// <inheritdoc />
    public string Name => "API del centralino";

    /// <inheritdoc />
    public bool IsAvailable => _provider.Capabilities.CanReadPresence;

    /// <inheritdoc />
    /// <remarks>
    /// L'API riferisce di tutti gli interni senza che nessuno lo chieda: non c'è nulla
    /// da dichiarare.
    /// </remarks>
    public bool RequiresExplicitWatch => false;

    /// <inheritdoc />
    public event EventHandler<PresenceChangedEventArgs>? PresenceChanged;

    /// <inheritdoc />
    public void Start()
    {
        if (_started) return;

        _started = true;
        _provider.PresenceChanged += OnProviderPresenceChanged;
    }

    /// <inheritdoc />
    public Task WatchAsync(IReadOnlyCollection<string> extensions, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// L'API del centralino dice <i>se</i> un interno è occupato, non <i>con chi</i>:
    /// non porta gli identificativi del dialogo, che sono l'unica cosa con cui si può
    /// chiedere di entrare in una conversazione. Su una postazione servita solo da
    /// questa sorgente l'inclusione si dichiara non disponibile, invece di tentare un
    /// invito che il centralino non potrebbe collegare a niente.
    /// </remarks>
    public DialogReference? ActiveDialog(string extension) => null;

    private void OnProviderPresenceChanged(object? sender, PresenceChangedEventArgs e)
        => PresenceChanged?.Invoke(this, e);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_started)
        {
            _provider.PresenceChanged -= OnProviderPresenceChanged;
            _started = false;
        }

        return ValueTask.CompletedTask;
    }
}
