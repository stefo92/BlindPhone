using System.Globalization;
using BlindPhone.Core.Abstractions;
using BlindPhone.Core.Accessibility;
using BlindPhone.Core.Domain;
using BlindPhone.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlindPhone.Core.Services;

/// <summary>
/// La regola che riconosce l'audio a senso unico, senza stato e senza orologio.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sei secondi e cento pacchetti, e nemmeno un pacchetto in arrivo.</b> Sei secondi
/// perché i primi istanti di una chiamata sono pieni di casi legittimi di silenzio: il
/// centralino che apre il flusso dopo la risposta, il gestore che lo sposta su un altro
/// apparato. Cento pacchetti sono due secondi di voce partita davvero, e servono a non
/// scambiare per un guasto di rete una postazione che non trasmette nemmeno lei.
/// </para>
/// <para>
/// <b>Zero, non «pochi».</b> Anche un interlocutore che tace manda pacchetti: di
/// silenzio, oppure i pacchetti di rumore di fondo dei codec che sospendono la voce
/// nelle pause. Nessun pacchetto in sei secondi non è un interlocutore riservato, è un
/// flusso che non arriva.
/// </para>
/// </remarks>
public static class OneWayAudio
{
    /// <summary>Da quanto la chiamata deve essere in conversazione prima di giudicare.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(6);

    /// <summary>Pacchetti partiti che servono a dire che la postazione sta trasmettendo.</summary>
    public const long MinimumSent = 100;

    /// <summary>
    /// Dice se, da <paramref name="start"/> a <paramref name="now"/>, la voce è partita
    /// e non ne è arrivata nessuna.
    /// </summary>
    /// <param name="start">Contatori all'inizio del tratto in conversazione.</param>
    /// <param name="now">Contatori adesso.</param>
    /// <param name="elapsed">Durata del tratto in conversazione.</param>
    public static bool IsOneWay(MediaFlowSnapshot start, MediaFlowSnapshot now, TimeSpan elapsed)
        => elapsed >= Grace
           && now.PacketsSent - start.PacketsSent >= MinimumSent
           && now.PacketsReceived == start.PacketsReceived;

    /// <summary>
    /// Il verso dell'audio di una chiamata, detto per la scheda Diagnostica.
    /// </summary>
    /// <param name="flow">Contatori dall'inizio della chiamata.</param>
    public static string Describe(MediaFlowSnapshot flow)
    {
        var inviati = flow.PacketsSent.ToString("N0", CultureInfo.CurrentCulture);
        var ricevuti = flow.PacketsReceived.ToString("N0", CultureInfo.CurrentCulture);

        return (flow.PacketsSent, flow.PacketsReceived) switch
        {
            (0, 0) => "nessun pacchetto in nessuna delle due direzioni",
            (> 0, 0) => $"SOLO IN USCITA: {inviati} pacchetti partiti e nessuno arrivato. "
                        + "Di solito è il NAT del router o un firewall sulle porte audio",
            (0, > 0) => $"solo in ingresso: {ricevuti} pacchetti arrivati e nessuno partito. "
                        + "Di solito è il microfono",
            _ => "in entrambe le direzioni",
        };
    }
}

/// <summary>
/// Avvisa l'operatore quando la sua voce parte e quella dell'interlocutore non arriva.
/// </summary>
/// <remarks>
/// <para>
/// <b>È il guasto tipico del lavoro agile, ed è muto due volte.</b> Dietro il router di
/// casa il centralino manda la voce a un indirizzo che non si raggiunge: l'operatore
/// sente silenzio e pensa che l'interlocutore non abbia ancora parlato, l'interlocutore
/// sente l'operatore e non capisce perché non gli risponda. Nessuno dei due riceve un
/// segnale, e la chiamata si chiude con un «non si sente» che non lascia traccia di
/// niente. Detto dopo sei secondi, invece, l'operatore sa che deve richiamare da un
/// altro telefono e che il problema va segnalato, con i dati già pronti nella scheda
/// Diagnostica.
/// </para>
/// <para>
/// <b>Una volta per chiamata.</b> Ripeterlo non aggiungerebbe niente e coprirebbe la
/// voce dell'interlocutore il giorno in cui il flusso comincia ad arrivare in ritardo.
/// Ogni tratto in conversazione si giudica da capo, perché dopo un'attesa il centralino
/// può aver spostato il flusso, ma l'avviso parte al più una volta.
/// </para>
/// <para>
/// Gira nel ciclo unico del programma, come gli altri servizi temporizzati: nessun timer
/// proprio che possa restare acceso per conto suo.
/// </para>
/// </remarks>
public sealed class OneWayAudioMonitor
{
    private readonly ITelephonyEngine _telephony;
    private readonly CallSessionManager _calls;
    private readonly IAnnouncer _announcer;
    private readonly ILogger<OneWayAudioMonitor> _log;

    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);

    /// <summary>Crea il servizio.</summary>
    /// <param name="telephony">Motore da cui leggere i contatori dei pacchetti.</param>
    /// <param name="calls">Gestore delle chiamate, per sapere quali sono in conversazione.</param>
    /// <param name="announcer">Canale degli annunci.</param>
    /// <param name="logger">Log diagnostico.</param>
    public OneWayAudioMonitor(
        ITelephonyEngine telephony,
        CallSessionManager calls,
        IAnnouncer announcer,
        ILogger<OneWayAudioMonitor>? logger = null)
    {
        _telephony = telephony;
        _calls = calls;
        _announcer = announcer;
        _log = logger ?? NullLogger<OneWayAudioMonitor>.Instance;
    }

    /// <summary>Guarda le chiamate in conversazione.</summary>
    /// <param name="now">Istante corrente.</param>
    public void Pump(DateTimeOffset now)
    {
        var vive = _calls.LiveCalls;

        // Le chiamate chiuse escono dall'elenco: un identificativo che il motore
        // riusasse non deve ereditare l'avviso gia' dato.
        foreach (var id in _watches.Keys.Where(id => vive.All(c => c.Id != id)).ToList())
        {
            _watches.Remove(id);
        }

        foreach (var call in vive)
        {
            var watch = _watches.TryGetValue(call.Id, out var esistente) ? esistente : null;

            if (call.State is not (CallState.Active or CallState.Conferenced))
            {
                // Attesa, squillo, trasferimento: il silenzio e' legittimo. Il tratto in
                // conversazione si chiude e alla ripresa se ne apre uno nuovo.
                if (watch is not null) watch.Since = null;
                continue;
            }

            if (_telephony.GetMediaFlow(call.Id) is not { } flow) continue;

            if (watch is null)
            {
                watch = new Watch();
                _watches[call.Id] = watch;
            }

            if (watch.Since is null)
            {
                watch.Since = now;
                watch.Baseline = flow;
                continue;
            }

            if (watch.Warned) continue;
            if (!OneWayAudio.IsOneWay(watch.Baseline!, flow, now - watch.Since.Value)) continue;

            watch.Warned = true;

            _log.LogWarning(
                "Chiamata {CallId}: audio in una sola direzione, {Inviati} pacchetti partiti e nessuno arrivato "
                + "in {Secondi} secondi di conversazione",
                call.Id,
                flow.PacketsSent - watch.Baseline!.PacketsSent,
                (int)(now - watch.Since.Value).TotalSeconds);

            // Deve arrivare durante la conversazione, che e' esattamente il momento in cui
            // gli annunci normali tacciono: e' la conversazione stessa a non funzionare.
            _announcer.Announce(Announcement.High(
                AnnouncementTexts.OneWayAudio,
                now,
                EarconKind.PbxLinkDegraded,
                coalesceKey: $"audio:senso-unico:{call.Id}") with { BypassConversationSilence = true });
        }
    }

    /// <summary>Il tratto in conversazione di una chiamata.</summary>
    private sealed class Watch
    {
        /// <summary>Inizio del tratto, <c>null</c> se la chiamata non è in conversazione.</summary>
        public DateTimeOffset? Since { get; set; }

        /// <summary>Contatori all'inizio del tratto.</summary>
        public MediaFlowSnapshot? Baseline { get; set; }

        /// <summary>Se l'avviso per questa chiamata è già stato dato.</summary>
        public bool Warned { get; set; }
    }
}
