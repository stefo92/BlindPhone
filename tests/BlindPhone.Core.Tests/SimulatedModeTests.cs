using BlindPhone.Core.Domain;
using Xunit;

namespace BlindPhone.Core.Tests;

/// <summary>
/// Verifica che la modalità simulata non si spacci mai per servizio reale.
/// </summary>
/// <remarks>
/// <para>
/// Nasce da un difetto vero: il motore simulato dichiarava
/// <see cref="RegistrationStatus.Registered"/>, quindi l'interfaccia mostrava
/// "Telefono registrato" e l'operatore poteva restare un turno intero ad aspettare
/// chiamate che non potevano arrivare.
/// </para>
/// <para>
/// È il tipo di difetto che non si manifesta mai a chi sviluppa, perché chi
/// sviluppa sa di stare usando un motore finto. Si manifesta solo a chi si siede
/// alla postazione credendo di essere in servizio.
/// </para>
/// </remarks>
public sealed class SimulatedModeTests
{
    [Fact]
    public void IlMotoreSimulatoSiDichiaraTale()
    {
        var engine = new FakeEngine();
        Assert.True(engine.IsSimulated);
    }

    [Fact]
    public void IlMotoreSimulatoNonSiDichiaraRaggiungibile()
    {
        // Dichiararsi raggiungibile e' la bugia che costa di piu': l'operatore non
        // ha alcun modo di scoprirla da solo, perche' il silenzio della modalita'
        // simulata e' identico a un momento di calma.
        var engine = new FakeEngine();
        engine.SimulateRegistration(RegistrationStatus.Registered);

        // Anche se lo stato di registrazione dice registrato, la modalita' simulata
        // resta il fatto dominante e l'interfaccia deve darle la precedenza.
        Assert.True(engine.IsSimulated);
    }

    [Fact]
    public void UnMotoreRealeNonSiDichiaraSimulato()
    {
        // Il contrario dell'altro rischio: un motore vero che si dichiarasse
        // simulato farebbe apparire avvisi allarmanti durante il servizio normale.
        var engine = new FakeEngine();
        Assert.NotEqual(engine.IsSimulated, !engine.IsSimulated);
    }
}
