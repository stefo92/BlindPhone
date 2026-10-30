namespace BlindPhone.Core.Domain;

/// <summary>
/// Pacchetti audio di una chiamata, contati dall'inizio, nelle due direzioni.
/// </summary>
/// <param name="PacketsSent">Pacchetti partiti dalla postazione verso la controparte.</param>
/// <param name="PacketsReceived">Pacchetti arrivati dalla controparte e consegnati alla riproduzione.</param>
/// <remarks>
/// Sono i due numeri che separano un problema della postazione da un problema della
/// rete: se partono e non arrivano, il microfono e il codec fanno il loro lavoro e la
/// voce dell'interlocutore si perde per strada.
/// </remarks>
public sealed record MediaFlowSnapshot(long PacketsSent, long PacketsReceived);
