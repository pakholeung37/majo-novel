using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Operations;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ActorManagerTests
{
    [Fact(Timeout = 5_000)]
    public async Task ManagerCreatesQueriesRemovesAndEnforcesUniqueActiveIds()
    {
        var factory = new FakeActorFactory("test");
        var manager = new ActorManager([CreateDefinition("hero")], [factory]);
        await manager.InitializeAsync(CancellationToken.None);

        var actor = manager.Create("hero");

        Assert.Same(actor, manager.Get<FakeActor>("hero"));
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

    [Fact]
    public void ManagerRejectsDuplicateDefinitions()
    {
        Assert.Throws<DuplicateActorIdException>(() => new ActorManager(
            [CreateDefinition("hero"), CreateDefinition("hero")]));
    }

    [Fact]
    public void DefinitionRejectsInvalidAppearancesAndUnknownAppearanceLookup()
    {
        Assert.Throws<ActorDefinitionException>(() => new ActorDefinition(
            "hero",
            "test",
            "missing",
            [new KeyValuePair<string, string>("default", "res://hero.svg")]));
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
        Assert.Throws<UnknownActorAppearanceException>(() =>
            definition.GetAppearancePath("unknown"));
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

    private static ActorDefinition CreateDefinition(string id) => new(
        id,
        "test",
        "default",
        [new KeyValuePair<string, string>("default", $"res://{id}.svg")]);

    private sealed class FakeActorFactory : IActorFactory
    {
        public FakeActorFactory(string factoryId)
        {
            FactoryId = factoryId;
        }

        public string FactoryId { get; }

        public int CreateCount { get; private set; }

        public IActor Create(ActorDefinition definition)
        {
            CreateCount++;
            return new FakeActor(definition.Id, definition.DefaultAppearance);
        }
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id, string appearance)
        {
            Id = id;
            Appearance = appearance;
        }

        public string Id { get; }

        public string Appearance { get; private set; }

        public bool Visible { get; private set; } = true;

        public Vector2 Position { get; private set; }

        public Color Tint { get; private set; } = Colors.White;

        public float Opacity { get; private set; } = 1;

        public Node SceneNode => null!;

        public bool IsDestroyed { get; private set; }

        public FakeOperationHandle ActiveOperation { get; set; } = new();

        public void SetAppearance(string appearance) => Appearance = appearance;

        public void SetVisible(bool visible) => Visible = visible;

        public void SetPosition(Vector2 position) => Position = position;

        public void SetTint(Color tint) => Tint = tint;

        public void SetOpacity(float opacity) => Opacity = opacity;

        public IOperationHandle ChangePosition(
            Vector2 position,
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Position = position;
            return new FakeOperationHandle(OperationState.Completed);
        }

        public IOperationHandle ChangeOpacity(
            float opacity,
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Opacity = opacity;
            return new FakeOperationHandle(OperationState.Completed);
        }

        public IOperationHandle Show(
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Visible = true;
            return new FakeOperationHandle(OperationState.Completed);
        }

        public IOperationHandle Hide(
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Visible = false;
            return new FakeOperationHandle(OperationState.Completed);
        }

        public void Destroy()
        {
            IsDestroyed = true;
            ActiveOperation.Cancel();
        }
    }

    private sealed class FakeOperationHandle : IOperationHandle
    {
        private readonly TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeOperationHandle(OperationState initialState = OperationState.Running)
        {
            State = initialState;
            if (initialState == OperationState.Completed)
            {
                completion.SetResult();
            }
        }

        public string? Id => null;

        public string? GroupId => null;

        public OperationState State { get; private set; }

        public Task Completion => completion.Task;

        public void Cancel()
        {
            if (State == OperationState.Completed)
            {
                return;
            }

            State = OperationState.Cancelled;
            completion.TrySetCanceled();
            _ = completion.Task.Exception;
        }

        public void Complete()
        {
            State = OperationState.Completed;
            completion.TrySetResult();
        }
    }
}
