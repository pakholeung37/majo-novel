using Majo.Runtime.Actors;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ActorStateContributorTests
{
    [Fact(Timeout = 5_000)]
    public async Task ActorStateContributorPreservesVisualCreationOrder()
    {
        var factory = new FakeActorFactory("test");
        var definitions = new[]
        {
            new ActorDefinition("stills", "test", new TextureActorDefinitionData("stills", "test", "default", [])),
            new ActorDefinition("scenes", "test", new TextureActorDefinitionData("scenes", "test", "default", [])),
            new ActorDefinition("hero", "test", new TextureActorDefinitionData("hero", "test", "default", [])),
        };
        var manager = new ActorManager(definitions, [factory]);
        await manager.InitializeAsync(CancellationToken.None);

        (await manager.CreateAsync("stills")).SetZIndex(-10);
        await manager.CreateAsync("scenes");
        (await manager.CreateAsync("hero")).SetZIndex(20);

        var contributor = new ActorStateContributor(manager);
        var record = await contributor.CaptureAsync(CancellationToken.None);

        // Reset and verify order is restored as stills -> scenes -> hero (not alphabetical scenes -> stills -> hero)
        await manager.ResetAsync(CancellationToken.None);
        Assert.Empty(manager.Actors);

        await contributor.CommitPreparedAsync(record, CancellationToken.None);
        Assert.Equal(["stills", "scenes", "hero"], manager.Actors.Select(a => a.Id));
        Assert.Equal([-10, 0, 20], manager.Actors.Select(a => a.ZIndex));

        await manager.ShutdownAsync(CancellationToken.None);
    }


}
