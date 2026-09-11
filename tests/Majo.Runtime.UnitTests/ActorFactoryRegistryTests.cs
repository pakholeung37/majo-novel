using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ActorFactoryRegistryTests
{
    [Fact]
    public void RegistryRejectsDuplicatesAndMutationAfterSeal()
    {
        var registry = new ActorFactoryRegistry();
        registry.Register("custom", ActorFactoryDomains.Runtime, _ => new FakeActorFactory("custom"));
        Assert.Throws<DuplicateActorFactoryException>(() =>
            registry.Register("custom", ActorFactoryDomains.Runtime, _ => new FakeActorFactory("custom")));
        registry.Replace("custom", ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
            _ => new FakeActorFactory("custom"));
        registry.Seal();
        Assert.True(registry.IsSealed);
        Assert.Throws<ActorException>(() =>
            registry.Replace("custom", ActorFactoryDomains.Runtime, _ => new FakeActorFactory("custom")));
    }

    [Fact]
    public void RegistryRejectsManagerBackendDependenciesAndEmptyDomain()
    {
        var registry = new ActorFactoryRegistry();
        foreach (var dependency in new[]
                 {
                     typeof(IActorManager),
                     typeof(IDirectionTransactionCoordinator),
                     typeof(IDirectionPreparationBackend),
                 })
        {
            Assert.Throws<ActorException>(() => registry.Register(
                $"cycle-{dependency.Name}", ActorFactoryDomains.Runtime,
                _ => new FakeActorFactory("cycle"), null, dependency));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Register(
            "none", ActorFactoryDomains.None, _ => new FakeActorFactory("none")));
    }
}
