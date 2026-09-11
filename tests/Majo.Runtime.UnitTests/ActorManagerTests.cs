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
    public async Task ExistingAliasPreloadUsesItsDefinitionWhenRequestOmitsAsset()
    {
        var factory = new DefinitionPreloadFactory();
        using var manager = new ActorManager([CreateDefinition("Ema")], [factory]);
        await manager.InitializeAsync(default);
        await manager.GetOrCreateAsync(new ActorSpawnRequest("ema_left", "test", "happy") { DefinitionId = "Ema" });
        var entry = Assert.Single(manager.GetPreloadResources(new ActorSpawnRequest("ema_left", "test", "happy")));
        Assert.Equal("Ema/happy", entry.Key.Path);
        Assert.Throws<ActorDefinitionException>(() => manager.GetPreloadResources(
            new ActorSpawnRequest("ema_left", "test", "happy") { DefinitionId = "other" }));
        await manager.ShutdownAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCreationCannotDestroyAnAlreadyOwnedActor(bool otherManager)
    {
        var factory = new ReusingActorFactory();
        using var owner = new ActorManager([CreateDefinition("hero")], [factory]);
        using var second = new ActorManager([CreateDefinition("hero")], [factory]);
        await owner.InitializeAsync(default);
        await second.InitializeAsync(default);
        var original = (FakeActor)await owner.CreateAsync("hero");
        var target = otherManager ? second : owner;
        await Assert.ThrowsAsync<ActorException>(() => target.PrepareReplacementAsync(
            [new ActorSpawnRequest("hero", "test", "happy")], default));
        Assert.Same(original, owner.GetActor<FakeActor>("hero"));
        Assert.False(original.IsDestroyed);
        if (otherManager)
        {
            await Assert.ThrowsAsync<ActorException>(() => second.CreateAsync("hero"));
            Assert.False(original.IsDestroyed);
        }
        owner.BindStateMutationGate(new Majo.Runtime.State.StateMutationGate());
        Assert.False(original.IsDestroyed);
        await second.ShutdownAsync(default);
        await owner.ShutdownAsync(default);
        Assert.True(original.IsDestroyed);
    }

    [Fact]
    public async Task DefinitionValidationIsLimitedToRequestedResources()
    {
        var factory = new DefinitionPreloadFactory();
        using var manager = new ActorManager([CreateDefinition("hero"), CreateDefinition("unused")], [factory]);
        await manager.InitializeAsync(default);
        Assert.Empty(factory.Validated);
        await manager.CreateAsync("hero");
        Assert.Equal(["hero"], factory.Validated);
        manager.GetPreloadResources(new ActorSpawnRequest("hero", "test", "happy"));
        Assert.DoesNotContain("unused", factory.Validated);
        await manager.ShutdownAsync(default);
    }

    private sealed class DefinitionPreloadFactory : FakeActorFactory, IActorFactory
    {
        public List<string> Validated { get; } = [];
        public override void ValidateDefinition(ActorDefinition definition) => Validated.Add(definition.DefinitionId);
        public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance) =>
            [ResourcePreloadEntry.Create<object>(new ResourceKey($"{definition.DefinitionId}/{appearance}"))];
    }

    private sealed class ReusingActorFactory : FakeActorFactory
    {
        private IActor? actor;
        public override async Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token) =>
            actor ??= await base.CreateAsync(definition, mode, token);
    }

    private const string SpecialFactoryId = "test-special";
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppearancePreloadRespectsResourcesOwnedByExistingActor(bool preloadExisting)
    {
        var factory = new PreloadActorFactory(preloadExisting);
        using var manager = new ActorManager([CreateDefinition("hero")], [factory]);
        await manager.InitializeAsync(CancellationToken.None);
        var request = new ActorSpawnRequest("hero", "test", "happy");
        Assert.Single(manager.GetPreloadResources(request));
        Assert.Empty(manager.Actors);
        await manager.CreateAsync("hero");
        Assert.Equal(preloadExisting ? 1 : 0, manager.GetPreloadResources(request).Count);
        Assert.Throws<ActorFactoryConflictException>(() => manager.GetPreloadResources(
            new ActorSpawnRequest("hero", "test", "happy", requiredFactoryId: "other")));
        manager.Remove("hero");
        Assert.Single(manager.GetPreloadResources(request));
        await manager.ShutdownAsync(CancellationToken.None);
    }

    private sealed class PreloadActorFactory(bool preloadExisting) : IActorFactory
    {
        public string FactoryId => "test";
        public bool PreloadExistingAppearances => preloadExisting;
        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token) =>
            Task.FromResult<IActor>(new FakeActor(definition.Id, "happy", FactoryId, definition.DefinitionId));
        public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance) =>
            new[] { ResourcePreloadEntry.Create<object>(new ResourceKey("atlas")) };
    }

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

        var actor = await manager.CreateAsync("hero");

        Assert.Same(actor, manager.GetActor<FakeActor>("hero"));
        Assert.True(manager.TryGet("hero", out var queried));
        Assert.Same(actor, queried);
        Assert.Single(manager.Actors);
        await Assert.ThrowsAsync<DuplicateActorIdException>(() => manager.CreateAsync("hero"));

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

        await manager.CreateAsync("stills");
        await manager.CreateAsync("scenes");
        await manager.CreateAsync("hero");

        Assert.Equal(["stills", "scenes", "hero"], manager.Actors.Select(a => a.Id));

        manager.Remove("scenes");
        Assert.Equal(["stills", "hero"], manager.Actors.Select(a => a.Id));

        await manager.CreateAsync("scenes");
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
            new TextureActorDefinitionData("hero", "test", "default",
                [new KeyValuePair<string, string>("default", "")])));
        Assert.Throws<ActorDefinitionException>(() => new ActorDefinition(
            "hero",
            "test",
            new TextureActorDefinitionData("hero", "test", "default", [
                new KeyValuePair<string, string>("default", "res://a.svg"),
                new KeyValuePair<string, string>("default", "res://b.svg"),
            ])));

        var definition = CreateDefinition("hero");
        var data = definition.GetFactoryData<TextureActorDefinitionData>();
        Assert.Equal("res://hero.svg", data.GetAppearancePath(definition.DefinitionId, "default"));
        Assert.Equal("test/hero/unknown", data.GetAppearancePath(definition.DefinitionId, "unknown"));
        Assert.Equal(
            new ResourceKey("test/hero/unknown"),
            data.GetAppearanceResourceKey(definition.DefinitionId, "unknown"));
        Assert.Null(data.GetAppearanceOverride("unknown"));

        var conventional = new ActorDefinition(
            "hero",
            ActorFactoryIds.Character,
            new TextureActorDefinitionData("hero", "characters", "calm", []));
        Assert.Equal(
            "characters/hero/calm",
            conventional.GetFactoryData<TextureActorDefinitionData>()
                .GetAppearanceResourceKey(conventional.DefinitionId, "calm").Path);
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
        var actor = (FakeActor)await manager.CreateAsync("hero");
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
        await manager.CreateAsync("hero");

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
        var actor = await manager.CreateAsync("custom");

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
    public async Task OneDefinitionCreatesIndependentAliasedInstancesAndRejectsIdentityChanges()
    {
        var factory = new FakeActorFactory("test");
        using var manager = new ActorManager([CreateDefinition("Ema")], [factory]);
        await manager.InitializeAsync(default);
        var left = await manager.GetOrCreateAsync(new ActorSpawnRequest("ema_left", "test", "default")
        { DefinitionId = "Ema" });
        var right = await manager.GetOrCreateAsync(new ActorSpawnRequest("ema_right", "test", "default")
        { DefinitionId = "Ema" });

        Assert.NotSame(left, right);
        Assert.Equal("Ema", left.DefinitionId);
        Assert.Equal("Ema", right.DefinitionId);
        Assert.Equal(2, factory.CreateCount);
        await Assert.ThrowsAsync<ActorDefinitionException>(() => manager.GetOrCreateAsync(
            new ActorSpawnRequest("ema_left", "test", "default") { DefinitionId = "Other" }));
    }

    [Theory(Timeout = 5_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingCreationEnforcesFactoryConstraintAcrossBothEntryPoints(bool spawnFirst)
    {
        var factory = new DeferredActorFactory();
        using var manager = new ActorManager([CreateDefinition("hero")], [factory]);
        await manager.InitializeAsync(default);
        var request = new ActorSpawnRequest("hero", "test", "default", requiredFactoryId: "test");
        var first = spawnFirst ? manager.GetOrCreateAsync(request) : manager.CreateAsync("hero");

        await Assert.ThrowsAsync<ActorFactoryConflictException>(() => manager.GetOrCreateAsync(
            new ActorSpawnRequest("hero", "other", "default", requiredFactoryId: "other")));
        var matching = manager.GetOrCreateAsync(request);
        var unconstrained = manager.CreateAsync("hero");
        Assert.Equal(1, factory.CreateCount);
        factory.Complete(new FakeActor("hero", "default", "test"));
        Assert.Same(await first, await matching);
        Assert.Same(await first, await unconstrained);
        await manager.ShutdownAsync(default);
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
        var data = factory.LastDefinition.GetFactoryData<TextureActorDefinitionData>();
        Assert.Equal("happy", data.DefaultAppearance);
        Assert.Equal(CharacterPositionPreset.Left, data.InitialPosition);
        Assert.Equal(
            "portraits/new-hero/happy",
            data.GetAppearanceResourceKey(factory.LastDefinition.DefinitionId, "happy").Path);
        Assert.True(manager.CanCreate(new ActorSpawnRequest("another-hero", ActorFactoryIds.Character, "Default")));
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

        Assert.True(manager.CanCreate(new ActorSpawnRequest("Ema", SpecialFactoryId, "Default")));
        Assert.True(manager.CanCreate(new ActorSpawnRequest("Ema", ActorFactoryIds.Character, "Default", requiredFactoryId: ActorFactoryIds.Character)));
        Assert.True(manager.CanCreate(new ActorSpawnRequest("Other", ActorFactoryIds.Character, "Default")));

        var actor = await manager.GetOrCreateAsync(new ActorSpawnRequest(
            "Ema",
            ActorFactoryIds.Character,
            "Surprised1",
            CharacterPositionPreset.Right));

        Assert.Equal(SpecialFactoryId, actor.FactoryId);
        Assert.NotNull(layeredFactory.LastDefinition);
        Assert.Equal(
            CharacterPositionPreset.Right,
            layeredFactory.LastDefinition.GetFactoryData<TextureActorDefinitionData>().InitialPosition);
        Assert.Null(characterFactory.LastDefinition);

        Assert.True(manager.Remove("Ema"));
        var restored = await manager.GetOrCreateAsync(new ActorSpawnRequest(
            "Ema",
            SpecialFactoryId,
            "Surprised1",
            requiredFactoryId: SpecialFactoryId));
        Assert.Equal(SpecialFactoryId, restored.FactoryId);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ExplicitDefinitionRejectsConflictingSpawnFactory()
    {
        var definition = new ActorDefinition(
            "stage",
            ActorFactoryIds.Background,
            new TextureActorDefinitionData("stage", "backgrounds", "courtroom", []));
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
            new TextureActorDefinitionData("hero", "portraits", "calm", []));
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

    [Fact(Timeout = 5_000)]
    public async Task SameActorInstanceCannotBeManagedByTwoManagers()
    {
        var sharedActor = new FakeActor("hero", "default", "test");
        var factory = new SharedActorFactory(sharedActor);
        using var first = new ActorManager([CreateDefinition("hero")], [factory]);
        using var second = new ActorManager([CreateDefinition("hero")], [factory]);
        await first.InitializeAsync(default);
        await second.InitializeAsync(default);

        Assert.Same(sharedActor, await first.CreateAsync("hero"));
        var exception = await Assert.ThrowsAsync<ActorException>(() => second.CreateAsync("hero"));

        Assert.Contains("already managed", exception.Message, StringComparison.Ordinal);
        Assert.False(sharedActor.IsDestroyed);
        Assert.Same(sharedActor, first.GetActor<IActor>("hero"));
        await second.ShutdownAsync(default);
        await first.ShutdownAsync(default);
        Assert.True(sharedActor.IsDestroyed);
    }

    private static ActorDefinition CreateDefinition(string id) => new(
        id,
        "test",
        new TextureActorDefinitionData(id, "test", "default",
            [new KeyValuePair<string, string>("default", $"res://{id}.svg")]));



    private sealed class CapturingActorFactory : IActorFactory
    {
        public CapturingActorFactory(string factoryId) => FactoryId = factoryId;

        public string FactoryId { get; }

        public ActorDefinition? LastDefinition { get; private set; }

        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token)
        {
            LastDefinition = definition;
            return Task.FromResult<IActor>(new FakeActor(
                definition.Id,
                definition.GetFactoryData<TextureActorDefinitionData>().DefaultAppearance,
                definition.FactoryId,
                definition.DefinitionId));
        }
    }

    private sealed class ConventionActorFactory : IConventionActorFactory
    {
        private readonly string conventionalActorId;

        public ConventionActorFactory(string conventionalActorId) =>
            this.conventionalActorId = conventionalActorId;

        public string FactoryId => SpecialFactoryId;

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
                    not SpecialFactoryId)
            {
                definition = null!;
                return false;
            }

            definition = new ActorDefinition(
                request.ActorId,
                FactoryId,
                new TextureActorDefinitionData(request.DefinitionId ?? request.ActorId, "layered",
                    request.InitialAppearance, [],
                    request.InitialPosition ?? CharacterPositionPreset.Center),
                request.DefinitionId);
            return true;
        }

        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token)
        {
            LastDefinition = definition;
            return Task.FromResult<IActor>(new FakeActor(
                definition.Id,
                definition.GetFactoryData<TextureActorDefinitionData>().DefaultAppearance,
                definition.FactoryId,
                definition.DefinitionId));
        }
    }

    private sealed record CustomActorPayload(string Value);

    private sealed class PayloadActorFactory : IActorFactory
    {
        public string FactoryId => "payload";

        public CustomActorPayload? ValidatedPayload { get; private set; }

        public void ValidateDefinition(ActorDefinition definition) =>
            ValidatedPayload = definition.GetFactoryData<CustomActorPayload>();

        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token) =>
            Task.FromResult<IActor>(new FakeActor(definition.Id, "custom", FactoryId, definition.DefinitionId));
    }

    private sealed class DeferredActorFactory : IActorFactory
    {
        private readonly TaskCompletionSource<IActor> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string FactoryId => "test";

        public int CreateCount { get; private set; }

        public async Task<IActor> CreateAsync(
            ActorDefinition definition,
            ActorCreationMode mode,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return await completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(IActor actor) => completion.SetResult(actor);
    }

    private sealed class SharedActorFactory(IActor actor) : IActorFactory
    {
        public string FactoryId => "test";

        public Task<IActor> CreateAsync(
            ActorDefinition definition,
            ActorCreationMode mode,
            CancellationToken cancellationToken) => Task.FromResult(actor);
    }

}
