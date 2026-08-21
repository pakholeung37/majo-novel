using System.Reflection;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Operations;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeCommandTests
{
    [Fact]
    public void ParserConvertsTypedArgumentsAndExplicitTargets()
    {
        var parser = new RuntimeCommandParser(0.35);

        var character = parser.ParseCharacter(
            ["maya", "happy", "RIGHT", "0.5", "true", "setup", "maya-move"]);
        var awaitCommand = parser.ParseAwait(["maya-move", "true", "id"]);
        var cancelCommand = parser.ParseCancel(["setup"]);

        Assert.Equal("maya", character.ActorId);
        Assert.Equal("happy", character.Appearance);
        Assert.Equal(CharacterPositionPreset.Right, character.Position);
        Assert.Equal(0.5, character.DurationSeconds);
        Assert.True(character.Wait);
        Assert.Equal("setup", character.GroupId);
        Assert.Equal("maya-move", character.OperationId);
        Assert.True(awaitCommand.Complete);
        Assert.Equal(OperationTargetKind.Id, awaitCommand.Target.Kind);
        Assert.Equal("maya-move", awaitCommand.Target.Name);
        Assert.Equal(OperationTargetKind.Group, cancelCommand.Target.Kind);
        Assert.Equal("setup", cancelCommand.Target.Name);
    }

    [Fact]
    public void ParserUsesDefaultsAndDashClearsOptionalBindings()
    {
        var parser = new RuntimeCommandParser(0.35);

        var background = parser.ParseBack(["backdrop", "courtroom"]);
        var hidden = parser.ParseHide(["maya", "0", "false", "-", "hide-maya"]);

        Assert.Equal(0.35, background.DurationSeconds);
        Assert.False(background.Wait);
        Assert.Null(background.GroupId);
        Assert.Null(background.OperationId);
        Assert.Equal(0, hidden.DurationSeconds);
        Assert.Null(hidden.GroupId);
        Assert.Equal("hide-maya", hidden.OperationId);
    }

    [Theory]
    [InlineData("expected", "char")]
    [InlineData("position", "char hero happy diagonal")]
    [InlineData("duration", "back backdrop dark NaN")]
    [InlineData("wait", "hide hero 0.2 sometimes")]
    [InlineData("target kind", "await setup false actor")]
    public void ParserRejectsInvalidArgumentsWithUsage(
        string expectedDiagnostic,
        string commandText)
    {
        var parser = new RuntimeCommandParser(0.35);
        var pieces = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var exception = Assert.Throws<RuntimeCommandException>(() =>
        {
            switch (pieces[0])
            {
                case "char":
                    parser.ParseCharacter(pieces[1..]);
                    break;
                case "back":
                    parser.ParseBack(pieces[1..]);
                    break;
                case "hide":
                    parser.ParseHide(pieces[1..]);
                    break;
                case "await":
                    parser.ParseAwait(pieces[1..]);
                    break;
            }
        });

        Assert.Contains(expectedDiagnostic, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Usage:", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 5_000)]
    public async Task NonWaitingCharacterCommandReturnsWhileOperationRemainsActive()
    {
        var actor = new FakeCharacterActor("hero", "idle");
        var service = CreateService(actor, out _);

        var task = service.CharacterAsync(new CharacterCommand(
            "hero", "happy", CharacterPositionPreset.Left, 1, false, "setup", "move"));

        Assert.True(task.IsCompletedSuccessfully);
        Assert.False(actor.LastOperation.Completion.IsCompleted);
        Assert.Equal("happy", actor.Appearance);
        Assert.True(actor.Visible);
        Assert.Equal(CharacterPositionPreset.Left, actor.PositionPreset);
        Assert.Equal("setup", actor.LastOperation.GroupId);
        Assert.Equal("move", actor.LastOperation.Id);
        actor.LastOperation.Complete();
        await actor.LastOperation.Completion;
    }

    [Fact(Timeout = 5_000)]
    public async Task WaitingHideCommandDoesNotFinishBeforeActorOperation()
    {
        var actor = new FakeCharacterActor("hero", "idle");
        var service = CreateService(actor, out _);

        var task = service.HideAsync(new HideCommand(
            "hero", 1, true, null, "hide-hero"));

        Assert.False(task.IsCompleted);
        actor.LastOperation.Complete();
        await task;
        Assert.False(actor.Visible);
    }

    [Fact(Timeout = 5_000)]
    public async Task BackCommandCreatesBackgroundAndUsesFadeOperation()
    {
        var manager = new FakeActorManager();
        var scheduler = new FakeOperationScheduler();
        var service = new RuntimeCommandService(manager, scheduler);

        await service.BackAsync(new BackCommand(
            "backdrop", "courtroom", 0.5, false, "setup", "background"));

        var background = manager.Get<FakeBackgroundActor>("backdrop");
        Assert.Equal("courtroom", background.Appearance);
        Assert.True(background.Visible);
        Assert.Equal(0, background.StartOpacity);
        Assert.Equal("setup", background.LastOperation.GroupId);
        Assert.Equal("background", background.LastOperation.Id);
        background.LastOperation.Complete();
    }

    [Fact(Timeout = 5_000)]
    public async Task AwaitCancelAndCompleteRouteToTypedSchedulerTargets()
    {
        var actor = new FakeCharacterActor("hero", "idle");
        var service = CreateService(actor, out var scheduler);
        var group = OperationTarget.ForGroup("setup");
        var operation = OperationTarget.ForId("hero-move");

        await service.AwaitAsync(new AwaitCommand(group, false));
        await service.AwaitAsync(new AwaitCommand(operation, true));
        await service.CancelAsync(new CancelCommand(operation));

        Assert.Equal(("wait", group), scheduler.Calls[0]);
        Assert.Equal(("complete", operation), scheduler.Calls[1]);
        Assert.Equal(("cancel", operation), scheduler.Calls[2]);
    }

    [Fact(Timeout = 5_000)]
    public async Task AdapterRegistersOnlyMajoNamesAndUnregistersInReverseOrder()
    {
        var registry = new FakeCommandRegistry();
        var service = new RecordingCommandService();
        var adapter = new YarnCommandAdapter(
            registry,
            service,
            new RuntimeCommandParser(0.35));

        await adapter.InitializeAsync(CancellationToken.None);

        Assert.Equal(YarnCommandAdapter.CommandNames, registry.Handlers.Keys);
        Assert.DoesNotContain("wait", registry.Handlers.Keys);
        Assert.DoesNotContain("stop", registry.Handlers.Keys);
        var characterHandler = Assert.IsType<Func<string[], Task>>(registry.Handlers["char"]);
        await characterHandler(["hero", "happy", "center", "0.2", "false", "setup"]);
        Assert.Equal("hero", service.Character!.ActorId);
        Assert.Equal(CharacterPositionPreset.Center, service.Character.Position);

        await adapter.ShutdownAsync(CancellationToken.None);
        Assert.Empty(registry.Handlers);
        Assert.Equal(
            YarnCommandAdapter.CommandNames.Reverse(),
            registry.RemovedCommands);
    }

    [Fact(Timeout = 5_000)]
    public async Task AdapterCapturesClearArgumentFailure()
    {
        var registry = new FakeCommandRegistry();
        var adapter = new YarnCommandAdapter(
            registry,
            new RecordingCommandService(),
            new RuntimeCommandParser(0.35));
        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["char"]);

        var exception = await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            handler(["hero", "happy", "somewhere"]));

        Assert.Same(exception, adapter.LastFailure);
        Assert.Contains("position", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(RuntimeCommandParser.CharacterUsage, exception.Message, StringComparison.Ordinal);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public void CommandCoreConstructorDependsOnlyOnRuntimeInterfaces()
    {
        var constructor = Assert.Single(typeof(RuntimeCommandService).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Equal([typeof(IActorManager), typeof(IOperationScheduler)],
            parameters.Select(parameter => parameter.ParameterType));
        Assert.All(parameters, parameter => Assert.True(parameter.ParameterType.IsInterface));
    }

    private static RuntimeCommandService CreateService(
        FakeCharacterActor actor,
        out FakeOperationScheduler scheduler)
    {
        var manager = new FakeActorManager(actor);
        scheduler = new FakeOperationScheduler();
        return new RuntimeCommandService(manager, scheduler);
    }

    private sealed class FakeCommandRegistry : IYarnCommandRegistry
    {
        public Dictionary<string, Delegate> Handlers { get; } = new(StringComparer.Ordinal);

        public List<string> RemovedCommands { get; } = new();

        public void AddCommandHandler(string commandName, Delegate handler) =>
            Handlers.Add(commandName, handler);

        public void RemoveCommandHandler(string commandName)
        {
            if (!Handlers.Remove(commandName))
            {
                throw new InvalidOperationException($"Handler '{commandName}' was not registered.");
            }

            RemovedCommands.Add(commandName);
        }
    }

    private sealed class RecordingCommandService : IRuntimeCommandService
    {
        public CharacterCommand? Character { get; private set; }

        public Task BackAsync(BackCommand command) => Task.CompletedTask;

        public Task CharacterAsync(CharacterCommand command)
        {
            Character = command;
            return Task.CompletedTask;
        }

        public Task HideAsync(HideCommand command) => Task.CompletedTask;

        public Task AwaitAsync(AwaitCommand command) => Task.CompletedTask;

        public Task CancelAsync(CancelCommand command) => Task.CompletedTask;
    }

    private sealed class FakeActorManager : IActorManager
    {
        private readonly Dictionary<string, IActor> actors = new(StringComparer.Ordinal);

        public FakeActorManager(params IActor[] actors)
        {
            foreach (var actor in actors)
            {
                this.actors.Add(actor.Id, actor);
            }
        }

        public IReadOnlyCollection<IActor> Actors => actors.Values;

        public IActor Create(string actorId)
        {
            IActor actor = actorId == "backdrop"
                ? new FakeBackgroundActor(actorId, "default")
                : new FakeCharacterActor(actorId, "default");
            actors.Add(actorId, actor);
            return actor;
        }

        public TActor Get<TActor>(string actorId) where TActor : class, IActor =>
            actors.TryGetValue(actorId, out var actor) && actor is TActor typed
                ? typed
                : throw new ActorException($"Actor '{actorId}' has the wrong type.");

        public bool TryGet(string actorId, out IActor? actor) =>
            actors.TryGetValue(actorId, out actor);

        public bool Remove(string actorId) => actors.Remove(actorId);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private abstract class FakeActor : IActor
    {
        protected FakeActor(string id, string appearance)
        {
            Id = id;
            Appearance = appearance;
        }

        public string Id { get; }

        public string Appearance { get; protected set; }

        public bool Visible { get; protected set; } = true;

        public Vector2 Position { get; protected set; }

        public Color Tint { get; private set; } = Colors.White;

        public float Opacity { get; protected set; } = 1;

        public Node SceneNode => null!;

        public FakeOperationHandle LastOperation { get; protected set; } = new();

        public void SetAppearance(string appearance) => Appearance = appearance;

        public void SetVisible(bool visible) => Visible = visible;

        public void SetPosition(Vector2 position) => Position = position;

        public void SetTint(Color tint) => Tint = tint;

        public virtual void SetOpacity(float opacity) => Opacity = opacity;

        public IOperationHandle ChangePosition(
            Vector2 position,
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Position = position;
            return LastOperation = new FakeOperationHandle(operationId, groupId);
        }

        public IOperationHandle ChangeOpacity(
            float opacity,
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Opacity = opacity;
            return LastOperation = new FakeOperationHandle(operationId, groupId);
        }

        public IOperationHandle Show(
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Visible = true;
            return LastOperation = new FakeOperationHandle(operationId, groupId);
        }

        public IOperationHandle Hide(
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Visible = false;
            return LastOperation = new FakeOperationHandle(operationId, groupId);
        }

        public void Destroy()
        {
        }
    }

    private sealed class FakeCharacterActor : FakeActor, ICharacterActor
    {
        public FakeCharacterActor(string id, string appearance)
            : base(id, appearance)
        {
        }

        public Sprite2D View => null!;

        public CharacterPositionPreset PositionPreset { get; private set; }

        public IOperationHandle ChangePosition(
            CharacterPositionPreset preset,
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            PositionPreset = preset;
            return LastOperation = new FakeOperationHandle(operationId, groupId);
        }
    }

    private sealed class FakeBackgroundActor : FakeActor, IBackgroundActor
    {
        public FakeBackgroundActor(string id, string appearance)
            : base(id, appearance)
        {
        }

        public TextureRect CurrentView => null!;

        public float StartOpacity { get; private set; } = 1;

        public override void SetOpacity(float opacity)
        {
            StartOpacity = opacity;
            base.SetOpacity(opacity);
        }

        public IOperationHandle CrossFadeAppearance(
            string appearance,
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Appearance = appearance;
            return LastOperation = new FakeOperationHandle(operationId, groupId);
        }
    }

    private sealed class FakeOperationHandle : IOperationHandle
    {
        private readonly TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeOperationHandle(string? id = null, string? groupId = null)
        {
            Id = id;
            GroupId = groupId;
        }

        public string? Id { get; }

        public string? GroupId { get; }

        public OperationState State { get; private set; } = OperationState.Running;

        public Task Completion => completion.Task;

        public void Cancel()
        {
            State = OperationState.Cancelled;
            completion.TrySetCanceled();
        }

        public void Complete()
        {
            State = OperationState.Completed;
            completion.TrySetResult();
        }
    }

    private sealed class FakeOperationScheduler : IOperationScheduler
    {
        public List<(string Action, OperationTarget Target)> Calls { get; } = new();

        public IReadOnlyCollection<IOperationHandle> ActiveOperations => [];

        public IOperationHandle Start(OperationDefinition definition) =>
            throw new NotSupportedException();

        public Task WaitAsync(
            OperationTarget target,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(("wait", target));
            return Task.CompletedTask;
        }

        public Task CancelAsync(
            OperationTarget target,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(("cancel", target));
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            OperationTarget target,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(("complete", target));
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
