using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Audio;

/// <summary>
/// Ponte audio della conferenza a tre: mette in comunicazione fra loro le gambe di
/// due o più chiamate contemporanee.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il missaggio è locale.</b> Il posto operatore fa da centralino: riceve la voce
/// di ciascun partecipante e la rimanda a tutti gli altri, sommata alla propria. È
/// la scelta che funziona su qualunque PBX, perché non chiede nulla oltre a due
/// chiamate contemporanee — cosa che il prodotto sa già fare per il trasferimento
/// con annuncio. L'alternativa, una sala conferenze lato centralino, richiede
/// configurazione, codici di servizio diversi per ogni marca, e un piano di licenza
/// che non è detto ci sia.
/// </para>
/// <para>
/// <b>La regola è N meno uno.</b> Ogni gamba riceve tutto tranne se stessa. È
/// l'unica riga di logica che conta in questa classe, ed è anche l'unico modo di
/// sbagliarla: rimandare a una linea il proprio audio produce un'eco ritardata di
/// un tempo pari al giro di rete, che è il difetto classico delle conferenze fatte
/// in casa e rende la conversazione impraticabile in pochi secondi.
/// </para>
/// <para>
/// <b>Quello che il ponte non fa.</b> Non tocca la riproduzione verso l'operatore:
/// ogni gamba riproduce già per conto proprio sul dispositivo della conversazione,
/// e WASAPI in modalità condivisa somma i due flussi nel suo mixer. Sommarli anche
/// qui li farebbe sentire due volte.
/// </para>
/// </remarks>
public sealed class ConferenceBridge : IDisposable
{
    private readonly List<SipAudioEndPoint> _legs;
    private readonly ILogger _log;
    private bool _disposed;

    /// <summary>Apre il ponte fra le gambe indicate.</summary>
    /// <param name="legs">
    /// Percorsi audio delle chiamate da unire. Ne servono almeno due; le chiamate
    /// devono essere già instaurate e fuori dall'attesa.
    /// </param>
    /// <param name="logger">Log diagnostico.</param>
    /// <exception cref="ArgumentException">Se le gambe sono meno di due.</exception>
    public ConferenceBridge(IEnumerable<SipAudioEndPoint> legs, ILogger? logger = null)
    {
        _legs = [.. legs];
        _log = logger ?? NullLogger.Instance;

        if (_legs.Count < 2)
        {
            throw new ArgumentException(
                "Una conferenza richiede almeno due percorsi audio", nameof(legs));
        }

        foreach (var leg in _legs)
        {
            leg.BeginConference();
            leg.DecodedAudioReady += OnDecodedAudio;
        }

        _log.LogInformation("Ponte audio della conferenza aperto su {Count} gambe", _legs.Count);
    }

    /// <summary>Numero di gambe collegate dal ponte.</summary>
    public int LegCount => _legs.Count;

    /// <summary>
    /// Indica se una gamba fa parte di questo ponte.
    /// </summary>
    /// <param name="endPoint">Percorso audio da cercare.</param>
    /// <remarks>
    /// Serve al motore telefonico per accorgersi che la chiamata che sta chiudendo
    /// era in conferenza, e smontare il ponte invece di lasciarlo puntato a un
    /// endpoint smaltito.
    /// </remarks>
    public bool Contains(SipAudioEndPoint endPoint) => _legs.Contains(endPoint);

    /// <summary>
    /// Distribuisce alla altre gambe la voce appena arrivata su una di esse.
    /// </summary>
    /// <remarks>
    /// Gira sul thread che riceve i pacchetti RTP della gamba di origine: si limita a
    /// consegnare, e la consegna si limita ad accodare. Il vettore viene condiviso in
    /// sola lettura fra tutti i destinatari, che ne copiano il contenuto nella propria
    /// coda.
    /// </remarks>
    private void OnDecodedAudio(SipAudioEndPoint source, short[] pcm, int clockRate)
    {
        if (_disposed) return;

        foreach (var leg in _legs)
        {
            // N meno uno: a nessuno torna indietro la propria voce.
            if (ReferenceEquals(leg, source)) continue;

            leg.SubmitConferenceAudio(pcm, clockRate);
        }
    }

    /// <summary>Smonta il ponte, lasciando le chiamate aperte e indipendenti.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var leg in _legs)
        {
            leg.DecodedAudioReady -= OnDecodedAudio;
            leg.EndConference();
        }

        _log.LogInformation(
            "Ponte audio della conferenza smontato: {Summary}",
            string.Join(
                "; ",
                _legs.Select(l => $"{l.ConferenceFramesMixed} missati, {l.ConferenceFramesStarved} a vuoto")));
    }
}
