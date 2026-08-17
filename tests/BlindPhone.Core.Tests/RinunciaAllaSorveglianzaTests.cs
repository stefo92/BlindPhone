using BlindPhone.Core.Services;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica quando si smette di chiedere lo stato degli interni a un centralino che
/// lo rifiuta.
/// </summary>
/// <remarks>
/// <para>
/// La misura che ha reso necessaria questa regola sta nel registro del 17 agosto 2026:
/// <b>81 sottoscrizioni inviate, 81 rifiutate con <c>404</c>, nessuna accettata</b>, e
/// la funzione che ripartiva a ogni ricerca in rubrica per tutta la mattina.
/// </para>
/// <para>
/// Il punto delicato non è smettere: è <b>non smettere quando non si deve</b>. Una
/// rubrica di ente è piena di numeri per cui il rifiuto è la risposta corretta —
/// recapiti esterni, code, gruppi di squillo — e spegnere la sorveglianza per quelli
/// significherebbe perdere lo stato degli interni veri.
/// </para>
/// </remarks>
public sealed class RinunciaAllaSorveglianzaTests
{
    [Fact]
    public void AllInizioSiSorveglia()
    {
        var regola = new PresenceSubscriptionPolicy();

        Assert.False(regola.GaveUp);
        Assert.Equal(0, regola.ConsecutiveRefusals);
    }

    [Fact]
    public void IlPacchettoNonServitoFermaLaFunzioneAlPrimoRifiuto()
    {
        // Non c'e' soglia da attendere: il centralino ha appena detto che quella
        // funzione non la fa, e non la fara' per nessun interno.
        var regola = new PresenceSubscriptionPolicy();

        Assert.Equal(
            PresenceSubscriptionAction.GiveUp,
            regola.Refuse(SubscriptionRefusal.EventPackage));

        Assert.True(regola.GaveUp);
    }

    [Fact]
    public void UnNumeroRifiutatoFermaSoloQuelNumero()
    {
        var regola = new PresenceSubscriptionPolicy();

        Assert.Equal(
            PresenceSubscriptionAction.StopThisExtension,
            regola.Refuse(SubscriptionRefusal.Resource));

        Assert.False(regola.GaveUp);
    }

    [Fact]
    public void UnRifiutoPerMancataAutorizzazioneFermaSoloQuelNumero()
    {
        // Ripetere la stessa richiesta con le stesse credenziali dara' la stessa
        // risposta, ma non dice niente sugli altri interni.
        var regola = new PresenceSubscriptionPolicy();

        Assert.Equal(
            PresenceSubscriptionAction.StopThisExtension,
            regola.Refuse(SubscriptionRefusal.Authorisation));

        Assert.False(regola.GaveUp);
    }

    [Fact]
    public void UnErrorePasseggeroLasciaLaSottoscrizioneAlSuoPosto()
    {
        // La rete torna, il centralino finisce di riavviarsi: la libreria riprova da
        // se' ed e' il comportamento giusto.
        var regola = new PresenceSubscriptionPolicy();

        Assert.Equal(
            PresenceSubscriptionAction.KeepTrying,
            regola.Refuse(SubscriptionRefusal.Transient));
    }

    [Fact]
    public void SiRinunciaSoloAllaSoglia()
    {
        var regola = new PresenceSubscriptionPolicy();

        for (var i = 1; i < PresenceSubscriptionPolicy.MaxRefusalsWithoutSuccess; i++)
        {
            Assert.Equal(
                PresenceSubscriptionAction.StopThisExtension,
                regola.Refuse(SubscriptionRefusal.Resource));

            Assert.False(regola.GaveUp);
        }

        Assert.Equal(
            PresenceSubscriptionAction.GiveUp,
            regola.Refuse(SubscriptionRefusal.Resource));

        Assert.True(regola.GaveUp);
    }

    [Fact]
    public void UnaSolaAccettazioneAzzeraIlConto()
    {
        // E' la protezione che rende praticabile la soglia. Una sottoscrizione accettata
        // dimostra che l'impianto la funzione la serve: da quel momento i rifiuti
        // tornano a essere notizie sul singolo numero, e venti cellulari cercati di
        // fila non devono spegnere lo stato degli interni.
        var regola = new PresenceSubscriptionPolicy();

        for (var i = 1; i < PresenceSubscriptionPolicy.MaxRefusalsWithoutSuccess; i++)
        {
            regola.Refuse(SubscriptionRefusal.Resource);
        }

        regola.Accept();
        Assert.Equal(0, regola.ConsecutiveRefusals);

        for (var i = 0; i < PresenceSubscriptionPolicy.MaxRefusalsWithoutSuccess * 3; i++)
        {
            Assert.Equal(
                PresenceSubscriptionAction.StopThisExtension,
                regola.Refuse(SubscriptionRefusal.Resource));
        }

        Assert.False(regola.GaveUp);
    }

    [Fact]
    public void IlCasoDelRegistroDelDiciassetteAgosto()
    {
        // 81 rifiuti con 404 e nessuna accettazione. La funzione va spenta, e va spenta
        // molto prima dell'ottantunesimo: altrimenti la postazione manda una richiesta
        // per ogni nome cercato per tutto il turno, e verso un apparato di frontiera una
        // raffica del genere somiglia a un attacco.
        var regola = new PresenceSubscriptionPolicy();
        var rinunciaAl = 0;

        for (var i = 1; i <= 81; i++)
        {
            var azione = regola.Refuse(SipDialogPresence.Classify(404));

            if (azione == PresenceSubscriptionAction.GiveUp && rinunciaAl == 0) rinunciaAl = i;
        }

        Assert.Equal(PresenceSubscriptionPolicy.MaxRefusalsWithoutSuccess, rinunciaAl);
        Assert.True(regola.GaveUp);
    }

    [Fact]
    public void DopoLaRinunciaOgniAltroRifiutoResta()
    {
        // I rifiuti in volo continuano ad arrivare: non devono riaprire nulla.
        var regola = new PresenceSubscriptionPolicy();

        regola.Refuse(SubscriptionRefusal.EventPackage);

        Assert.Equal(
            PresenceSubscriptionAction.GiveUp,
            regola.Refuse(SubscriptionRefusal.Transient));

        Assert.True(regola.GaveUp);
    }
}
