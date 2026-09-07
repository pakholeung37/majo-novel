using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Timing;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ActorManagerTests
{
    [Fact]
    public void AnimationSpecValidatesDurationAndPreservesConflictPolicy()
    {
        var animation = new AnimationSpec(
            0.35,
            EasingId.CubicOut,
            AnimationTimeMode.Unscaled,
            AnimationConflictPolicy.CancelPrevious);

        Assert.Equal(0.35, animation.DurationSeconds);
        Assert.Equal(EasingId.CubicOut, animation.Easing);
        Assert.Equal(AnimationTimeMode.Unscaled, animation.TimeMode);
        Assert.Equal(AnimationConflictPolicy.CancelPrevious, animation.ConflictPolicy);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnimationSpec(double.NaN));
    }


    [Fact(Timeout = 5_000)]
    public async Task ManagerCreatesQueriesRemovesAndEnforcesUniqueActiveIds()
    {
        var factory = new FakeActorFactory("test");
        var manager = new ActorManager([CreateDefinition("hero")], [factory]);
        await manager.InitializeAsync(CancellationToken.None);

        var actor = manager.Create("hero");

        Assert.Same(actor, manager.GetActor<FakeActor>("hero"));
        Assert.True(manager.TryGet("hero", out var queried));
        Assert.Same(actor, queried);
        Assert.Single(manager.Actors);
        Assert.Throws<DuplicateActorIdException>(() => manager.Create("hero"));

        Assert.True(manager.Remove("hero"));
        Assert.False(manager.Remove("hero"));
        Assert.True(((FakeActor)actor).IsDestroyed);
        Assert.Empty(manager.Actors);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ManagerPreservesExactActorCreationOrderAcrossCreationsAndRemovals()
    {
        var factory = new FakeActorFactory("test");
        var manager = new ActorManager(
            [CreateDefinition("stills"), CreateDefinition("scenes"), CreateDefinition("hero")],
            [factory]);
        await manager.InitializeAsync(CancellationToken.None);

        manager.Create("stills");
        manager.Create("scenes");
        manager.Create("hero");

        Assert.Equal(["stills", "scenes", "hero"], manager.Actors.Select(a => a.Id));

        manager.Remove("scenes");
        Assert.Equal(["stills", "hero"], manager.Actors.Select(a => a.Id));

        manager.Create("scenes");
        Assert.Equal(["stills", "hero", "scenes"], manager.Actors.Select(a => a.Id));

        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public void ManagerRejectsDuplicateDefinitions()
    {
        Assert.Throws<DuplicateActorIdException>(() => new ActorManager(
            [CreateDefinition("hero"), CreateDefinition("hero")]));
    }

    [Fact]
    public void DefinitionValidatesOverridesAndFallsBackToConventionKeys()
    {
        Assert.Throws<ActorDefinitionException>(() => new ActorDefinition(
            "hero",
            "test",
            "default",
            [new KeyValuePair<string, string>("default", "")]));
        Assert.Throws<ActorDefinitionException>(() => new ActorDefinition(
            "hero",
            "test",
            "default",
            [
                new KeyValuePair<string, string>("default", "res://a.svg"),
                new KeyValuePair<string, string>("default", "res://b.svg"),
            ]));

        var definition = CreateDefinition("hero");
        Assert.Equal("res://hero.svg", definition.GetAppearancePath("default"));
        Assert.Equal("test/hero/unknown", definition.GetAppearancePath("unknown"));
        Assert.Equal(
            new ResourceKey("test/hero/unknown"),
            definition.GetAppearanceResourceKey("unknown"));
        Assert.Null(definition.GetAppearanceOverride("unknown"));

        var conventional = new ActorDefinition(
            "hero",
            ActorFactoryIds.Character,
            "calm",
            []);
        Assert.Equal(
            "characters/hero/calm",
            conventional.GetAppearanceResourceKey("calm").Path);
    }

    [Fact(Timeout = 5_000)]
    public async Task InitializationRejectsMissingFactoryWithActorContext()
    {
        var manager = new ActorManager([CreateDefinition("hero")]);

        var exception = await Assert.ThrowsAsync<ActorFactoryNotFoundException>(() =>
            manager.InitializeAsync(CancellationToken.None));

        Assert.Contains("hero", exception.Message, StringComparison.Ordinal);
        Assert.Contains("test", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 5_000)]
    public async Task ResetDestroysActorsAndCancelsTheirOwnedOperations()
    {
        var factory = new FakeActorFactory("test");
        var manager = new ActorManager([CreateDefinition("hero")], [factory]);
        await manager.InitializeAsync(CancellationToken.None);
        var actor = (FakeActor)manager.Create("hero");
        actor.ActiveOperation = new FakeOperationHandle();

        await manager.ResetAsync(CancellationToken.None);

        Assert.True(actor.IsDestroyed);
        Assert.Equal(OperationState.Cancelled, actor.ActiveOperation.State);
        Assert.Empty(manager.Actors);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ThirdPartyFactoryCanBeRegisteredAndReplacedBeforeInitialization()
    {
        var original = new FakeActorFactory("test");
        var replacement = new FakeActorFactory("test");
        var manager = new ActorManager([CreateDefinition("hero")]);
        manager.RegisterFactory(original);
        Assert.Throws<DuplicateActorFactoryException>(() =>
            manager.RegisterFactory(replacement));
        manager.RegisterFactory(replacement, replace: true);

        await manager.InitializeAsync(CancellationToken.None);
        manager.Create("hero");

        Assert.Equal(0, original.CreateCount);
        Assert.Equal(1, replacement.CreateCount);
        Assert.Throws<ActorException>(() =>
            manager.RegisterFactory(new FakeActorFactory("late")));
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task CustomFactoryOwnsItsDefinitionPayloadWithoutTextureSchema()
    {
        var payload = new CustomActorPayload("custom-value");
        var definition = new ActorDefinition("custom", "payload", payload);
        var factory = new PayloadActorFactory();
        var manager = new ActorManager([definition], [factory]);

        await manager.InitializeAsync(CancellationToken.None);
        var actor = manager.Create("custom");

        Assert.Same(payload, factory.ValidatedPayload);
        Assert.Equal("custom", actor.Id);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ConcurrentAsyncCreationSharesOneFactoryOperation()
    {
        var factory = new DeferredActorFactory();
        var manager = new ActorManager([CreateDefinition("hero")], [factory]);
        await manager.InitializeAsync(CancellationToken.None);

        var first = manager.CreateAsync("hero");
        var second = manager.CreateAsync("hero");
        Assert.Equal(1, factory.CreateCount);
        factory.Complete(new FakeActor("hero", "default"));

        Assert.Same(await first, await second);
        Assert.Single(manager.Actors);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task MissingBuiltInDefinitionUsesSpawnRequestAndResourceConventions()
    {
        var factory = new CapturingActorFactory(ActorFactoryIds.Character);
        var manager = new ActorManager(
            [],
            [factory],
            conventions: new ActorCreationConventions(
                "portraits",
                "scenes"));
        await manager.InitializeAsync(CancellationToken.None);

        var actor = await manager.GetOrCreateAsync(new ActorSpawnRequest(
            "new-hero",
            ActorFactoryIds.Character,
            "happy",
            CharacterPositionPreset.Left));

        Assert.Equal("new-hero", actor.Id);
        Assert.NotNull(factory.LastDefinition);
        Assert.Equal("happy", factory.LastDefinition.DefaultAppearance);
        Assert.Equal(CharacterPositionPreset.Left, factory.LastDefinition.InitialPosition);
        Assert.Equal(
            "portraits/new-hero/happy",
            factory.LastDefinition.GetAppearanceResourceKey("happy").Path);
        Assert.True(manager.CanCreate("another-hero", ActorFactoryIds.Character));
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task DormantSpawnBuildsActorWithoutMakingItVisible()
    {
        var factory = new FakeActorFactory("test");
        var manager = new ActorManager([CreateDefinition("hero")], [factory]);
        await manager.InitializeAsync(CancellationToken.None);

        var actor = await manager.GetOrCreateAsync(new ActorSpawnRequest(
            "hero",
            "test",
            "default",
            creationMode: ActorCreationMode.Dormant));

        Assert.False(actor.Visible);
        Assert.Same(actor, manager.GetActor<IActor>("hero"));
        Assert.Equal(1, factory.CreateCount);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task SpecializedConventionFactoryOverridesImplicitCharacterFallback()
    {
        var characterFactory = new CapturingActorFactory(ActorFactoryIds.Character);
        var layeredFactory = new ConventionActorFactory("Ema");
        var manager = new ActorManager([], [characterFactory, layeredFactory]);
        await manager.InitializeAsync(CancellationToken.None);

        Assert.True(manager.CanCreate("Ema", ActorFactoryIds.LayeredCharacter));
        Assert.False(manager.CanCreate("Ema", ActorFactoryIds.Character));
        Assert.True(manager.CanCreate("Other", ActorFactoryIds.Character));

        var actor = await manager.GetOrCreateAsync(new ActorSpawnRequest(
            "Ema",
            ActorFactoryIds.Character,
            "Surprised1",
            CharacterPositionPreset.Right));

        Assert.Equal(ActorFactoryIds.LayeredCharacter, actor.FactoryId);
        Assert.NotNull(layeredFactory.LastDefinition);
        Assert.Equal(
            CharacterPositionPreset.Right,
            layeredFactory.LastDefinition.InitialPosition);
        Assert.Null(characterFactory.LastDefinition);

        Assert.True(manager.Remove("Ema"));
        var restored = await manager.GetOrCreateAsync(new ActorSpawnRequest(
            "Ema",
            ActorFactoryIds.LayeredCharacter,
            "Surprised1",
            requiredFactoryId: ActorFactoryIds.LayeredCharacter));
        Assert.Equal(ActorFactoryIds.LayeredCharacter, restored.FactoryId);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ExplicitDefinitionRejectsConflictingSpawnFactory()
    {
        var definition = new ActorDefinition(
            "stage",
            ActorFactoryIds.Background,
            "courtroom",
            []);
        var manager = new ActorManager(
            [definition],
            [
                new CapturingActorFactory(ActorFactoryIds.Background),
                new CapturingActorFactory(ActorFactoryIds.Character),
            ]);
        await manager.InitializeAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ActorFactoryConflictException>(() =>
            manager.GetOrCreateAsync(new ActorSpawnRequest(
                "stage",
                ActorFactoryIds.Character,
                "normal",
                requiredFactoryId: ActorFactoryIds.Character)));

        Assert.Empty(manager.Actors);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ExplicitCustomFactoryOverridesCommandFallbackFactory()
    {
        var factory = new CapturingActorFactory("portrait");
        var definition = new ActorDefinition(
            "hero",
            "portrait",
            "portraits",
            "calm",
            []);
        var manager = new ActorManager([definition], [factory]);
        await manager.InitializeAsync(CancellationToken.None);

        var actor = await manager.GetOrCreateAsync(new ActorSpawnRequest(
            "hero",
            ActorFactoryIds.Character,
            "happy"));

        Assert.Equal("portrait", actor.FactoryId);
        Assert.Same(definition, factory.LastDefinition);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    private static ActorDefinition CreateDefinition(string id) => new(
        id,
        "test",
        "default",
        [new KeyValuePair<string, string>("default", $"res://{id}.svg")]);



    private sealed class CapturingActorFactory : IActorFactory
    {
        public CapturingActorFactory(string factoryId) => FactoryId = factoryId;

        public string FactoryId { get; }

        public ActorDefinition? LastDefinition { get; private set; }

        public IActor Create(ActorDefinition definition)
        {
            LastDefinition = definition;
            return new FakeActor(
                definition.Id,
                definition.DefaultAppearance,
                definition.FactoryId);
        }
    }

    private sealed class ConventionActorFactory : IConventionActorFactory
    {
        private readonly string conventionalActorId;

        public ConventionActorFactory(string conventionalActorId) =>
            this.conventionalActorId = conventionalActorId;

        public string FactoryId => ActorFactoryIds.LayeredCharacter;

        public ActorDefinition? LastDefinition { get; private set; }

        public bool TryResolveDefinition(
            ActorSpawnRequest request,
            out ActorDefinition definition)
        {
            if (!string.Equals(
                    request.ActorId,
                    conventionalActorId,
                    StringComparison.Ordinal) ||
                request.FallbackFactoryId is not ActorFactoryIds.Character and
                    not ActorFactoryIds.LayeredCharacter)
            {
                definition = null!;
                return false;
            }

            definition = new ActorDefinition(
                request.ActorId,
                FactoryId,
                "layered",
                request.InitialAppearance,
                [],
                request.InitialPosition ?? CharacterPositionPreset.Center);
            return true;
        }

        public IActor Create(ActorDefinition definition)
        {
            LastDefinition = definition;
            return new FakeActor(
                definition.Id,
                definition.DefaultAppearance,
                definition.FactoryId);
        }
    }

    private sealed record CustomActorPayload(string Value);

    private sealed class PayloadActorFactory : IActorFactory
    {
        public string FactoryId => "payload";

        public CustomActorPayload? ValidatedPayload { get; private set; }

        public void ValidateDefinition(ActorDefinition definition) =>
            ValidatedPayload = definition.GetFactoryData<CustomActorPayload>();

        public IActor Create(ActorDefinition definition) =>
            new FakeActor(definition.Id, "custom");
    }

    private sealed class DeferredActorFactory : IActorFactory
    {
        private readonly TaskCompletionSource<IActor> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string FactoryId => "test";

        public int CreateCount { get; private set; }

        public IActor Create(ActorDefinition definition) =>
            throw new NotSupportedException();

        public async Task<IActor> CreateAsync(
            ActorDefinition definition,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return await completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(IActor actor) => completion.SetResult(actor);
    }

}
