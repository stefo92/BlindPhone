namespace BlindPhone.Core.Abstractions;

/// <summary>
/// Uscita della voce propria di BlindPhone sul dispositivo scelto per gli annunci.
/// </summary>
/// <remarks>
/// <para>
/// <b>Esiste perché la sintesi di sistema non sa scegliere il dispositivo.</b> La voce
/// interna usciva sempre dal dispositivo predefinito di Windows, qualunque cosa fosse
/// scelta in «Voce degli annunci»: su un posto operatore con la cuffia come predefinito
/// finiva nello stesso orecchio dell'interlocutore, cioè proprio dove la quarta via
/// esisteva per non farla arrivare.
/// </para>
/// <para>
/// Ora la frase si sintetizza in memoria e la suona il motore audio, che i dispositivi
/// li sa scegliere. Il volume resta quello della sintesi: questa uscita suona i campioni
/// così come arrivano, perché lo stesso cursore deve dare lo stesso volume sia sul
/// dispositivo scelto sia sul predefinito.
/// </para>
/// </remarks>
public interface IAnnouncementAudioOutput
{
    /// <summary>
    /// <c>true</c> se agli annunci è assegnato un dispositivo preciso.
    /// </summary>
    /// <remarks>
    /// Senza un dispositivo preciso la voce esce dal predefinito di sistema come ha
    /// sempre fatto. Non è la stessa cosa del predefinito per le comunicazioni che usa
    /// il motore audio, e scambiarli sposterebbe la voce in cuffia a chi non ha mai
    /// toccato questa impostazione.
    /// </remarks>
    bool HasDedicatedDevice { get; }

    /// <summary>
    /// Riproduce una frase già sintetizzata sul dispositivo degli annunci, attendendone
    /// la fine.
    /// </summary>
    /// <param name="pcm">Campioni a 16 bit con segno, monofonici, little endian.</param>
    /// <param name="sampleRate">Frequenza di campionamento dei campioni.</param>
    /// <param name="cancellationToken">Interrompe la riproduzione, per un annuncio più urgente.</param>
    /// <returns>
    /// <c>true</c> se la frase è stata riprodotta, o interrotta di proposito;
    /// <c>false</c> se il dispositivo non ha suonato, e la frase va detta altrove.
    /// </returns>
    Task<bool> PlaySpeechAsync(byte[] pcm, int sampleRate, CancellationToken cancellationToken);
}
