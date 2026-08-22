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
    public void BinderConvertsTypedArgumentsAndIgnoresNamedArgumentOrder()
    {
        var character = Bind<CharacterCommandArguments>(
            [
                "maya",
                "operation:maya-move",
                "WAIT:true",
                "position:RIGHT",
                "duration:0.5",
                "appearance:happy",
                "group:setup",
            ]);

        Assert.Equal("maya", character.Actor);
        Assert.Equal("happy", character.Appearance);
        Assert.Equal(CharacterPositionPreset.Right, character.Position);
        Assert.Equal(0.5, character.Duration);
        Assert.True(character.Wait);
        Assert.Equal("setup", character.Group);
        Assert.Equal("maya-move", character.Operation);
    }

    [Fact]
    public void BinderUsesConfiguredAndLanguageDefaults()
    {
        var background = Bind<BackCommandArguments>(
            ["backdrop", "appearance:courtroom"]);
        var hidden = Bind<HideCommandArguments>(
            ["maya", "operation:hide-maya", "duration:0"]);

        Assert.Equal(0.35, background.Duration);
        Assert.False(background.Wait);
        Assert.Null(background.Group);
        Assert.Null(background.Operation);
        Assert.Equal(0, hidden.Duration);
        Assert.Null(hidden.Group);
        Assert.Equal("hide-maya", hidden.Operation);

        var constant = Bind<ConstantDefaultCommandArguments>([]);
        Assert.True(constant.Enabled);
    }

    [Fact]
    public void BinderSupportsAwaitBlockAndTargetForms()
    {
        var block = Bind<AwaitCommandArguments>([]);
        var completeBlock = Bind<AwaitCommandArguments>(["complete:true"]);
        var target = Bind<AwaitCommandArguments>(["group:opening"]);

        Assert.False(block.Complete);
        Assert.Null(block.Group);
        Assert.True(completeBlock.Complete);
        Assert.Equal("opening", target.Group);
        Assert.Null(target.Operation);
        _ = Bind<EndAwaitCommandArguments>([]);
    }

    [Theory]
    [InlineData("appearance", "char hero position:left")]
    [InlineData("position", "char hero appearance:happy position:diagonal")]
    [InlineData("duration", "back backdrop appearance:dark duration:NaN")]
    [InlineData("at least 0", "back backdrop appearance:dark duration:-0.1")]
    [InlineData("wait", "hide hero duration:0.2 wait:sometimes")]
    [InlineData("mutually exclusive", "await group:setup operation:move")]
    [InlineData("unexpected positional", "back backdrop dark")]
    [InlineData("duplicated", "hide hero wait:true WAIT:false")]
    [InlineData("unknown parameter", "back backdrop appearance:dark time:1")]
    [InlineData("unexpected positional", "endAwait now")]
    [InlineData("either group or operation", "cancel")]
    public void BinderRejectsInvalidArgumentsWithUsage(
        string expectedDiagnostic,
        string commandText)
    {
        var pieces = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var exception = Assert.Throws<RuntimeCommandException>(() =>
        {
            switch (pieces[0])
            {
                case "char":
                    Bind<CharacterCommandArguments>(pieces[1..]);
                    break;
                case "back":
                    Bind<BackCommandArguments>(pieces[1..]);
                    break;
                case "hide":
                    Bind<HideCommandArguments>(pieces[1..]);
                    break;
                case "await":
                    Bind<AwaitCommandArguments>(pieces[1..]);
                    break;
                case "endAwait":
                    Bind<EndAwaitCommandArguments>(pieces[1..]);
                    break;
                case "cancel":
                    Bind<CancelCommandArguments>(pieces[1..]);
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
        var service = CreateService(actor);

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
        var service = CreateService(actor);

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
        var service = new ActorCommandService(manager);

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

    [Fact]
    public void CatalogIsTheSingleSourceOfBuiltInCommandNames()
    {
        var commands = new RuntimeCommandRegistry();
        BuiltInRuntimeCommands.Register(commands, new RecordingCommandService());
        var catalog = commands.BuildCatalog();

        Assert.Equal(
            ["back", "char", "hide", "await", "endAwait", "cancel"],
            catalog.Commands.Select(command => command.Name));
        Assert.Equal(
            catalog.Commands.Count,
            catalog.Commands.Select(command => command.Name).Distinct(
                StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void RegistryRejectsReservedDuplicateAndLateRegistrations()
    {
        var commands = new RuntimeCommandRegistry();
        BuiltInRuntimeCommands.Register(commands, new RecordingCommandService());

        var duplicate = Assert.Throws<RuntimeCommandException>(() =>
            commands.Register<DuplicateEndAwaitArguments>((_, _) => Task.CompletedTask));
        Assert.Contains("already registered", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        var reserved = Assert.Throws<RuntimeCommandException>(() =>
            new RuntimeCommandRegistry().Register<ReservedWaitArguments>(
                (_, _) => Task.CompletedTask));
        Assert.Contains("Yarn Spinner", reserved.Message, StringComparison.Ordinal);

        _ = commands.BuildCatalog();
        var late = Assert.Throws<RuntimeCommandException>(() =>
            commands.Register<ProbeCommandArguments>((_, _) => Task.CompletedTask));
        Assert.Contains("before Runtime initialization", late.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryRejectsWritableArgumentsWithoutParameterMetadata()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new RuntimeCommandRegistry().Register<IncompleteMetadataArguments>(
                (_, _) => Task.CompletedTask));

        Assert.Contains(
            nameof(CommandParameterAttribute),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("MissingMetadata", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedCommandModuleRegistrationRollsBackPartialModule()
    {
        var commands = new RuntimeCommandRegistry();
        BuiltInRuntimeCommands.Register(commands, new RecordingCommandService());

        Assert.Throws<RuntimeCommandException>(() =>
            commands.RegisterModule(new FailingCommandModule()));

        Assert.DoesNotContain(
            commands.Registrations,
            registration => registration.Name == "probe");
        Assert.Equal(6, commands.Registrations.Count);
    }

    [Fact]
    public void BinderUsesRegisteredCustomValueConverter()
    {
        var commands = new RuntimeCommandRegistry();
        commands.Register<CustomValueCommandArguments>((_, _) => Task.CompletedTask);
        var descriptor = Assert.Single(commands.Registrations).Descriptor;
        var converters = new CommandValueConverterRegistry()
            .Register(value => new CustomCommandValue(value.ToUpperInvariant()));
        var binder = new CommandBinder(new CommandBindingContext(0.35), converters);

        var arguments = binder.Bind<CustomValueCommandArguments>(descriptor, ["sample"]);

        Assert.Equal("SAMPLE", arguments.Value.Text);
    }

    [Fact]
    public void BinderValidationRejectsMissingConverterBeforeCommandExecution()
    {
        var commands = new RuntimeCommandRegistry();
        commands.Register<CustomValueCommandArguments>((_, _) => Task.CompletedTask);
        var descriptor = Assert.Single(commands.Registrations).Descriptor;
        var binder = new CommandBinder(new CommandBindingContext(0.35));

        var exception = Assert.Throws<RuntimeCommandException>(() =>
            binder.Validate(descriptor));

        Assert.Contains("converter", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("value", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 5_000)]
    public async Task AdapterRegistersOnlyMajoNamesAndUnregistersInReverseOrder()
    {
        var registry = new FakeCommandRegistry();
        var service = new RecordingCommandService();
        var adapter = CreateAdapter(registry, service, out var catalog);

        await adapter.InitializeAsync(CancellationToken.None);

        Assert.Equal(catalog.Commands.Select(command => command.Name), registry.Handlers.Keys);
        Assert.DoesNotContain("wait", registry.Handlers.Keys);
        Assert.DoesNotContain("stop", registry.Handlers.Keys);
        Assert.All(registry.Handlers.Values, handler =>
        {
            var parameter = Assert.Single(handler.Method.GetParameters());
            Assert.NotNull(parameter.GetCustomAttribute<ParamArrayAttribute>());
        });
        var characterHandler = Assert.IsType<Func<string[], Task>>(registry.Handlers["char"]);
        await characterHandler(
            ["hero", "group:setup", "position:center", "appearance:happy", "duration:0.2"]);
        Assert.Equal("hero", service.Character!.ActorId);
        Assert.Equal(CharacterPositionPreset.Center, service.Character.Position);

        await adapter.ShutdownAsync(CancellationToken.None);
        Assert.Empty(registry.Handlers);
        Assert.Equal(
            catalog.Commands.Select(command => command.Name).Reverse(),
            registry.RemovedCommands);
    }

    [Fact(Timeout = 5_000)]
    public async Task AdapterCapturesClearArgumentFailure()
    {
        var registry = new FakeCommandRegistry();
        var adapter = CreateAdapter(
            registry,
            new RecordingCommandService(),
            out var catalog);
        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["char"]);

        var exception = await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            handler(["hero", "appearance:happy", "position:somewhere"]));

        Assert.Same(exception, adapter.LastFailure);
        Assert.Contains("position", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            catalog.Get("char").Descriptor.Usage,
            exception.Message,
            StringComparison.Ordinal);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task TargetedAwaitAndCancelUseTypedSchedulerTargets()
    {
        var registry = new FakeCommandRegistry();
        var actors = new FakeActorManager();
        var scheduler = new FakeOperationScheduler();
        var adapter = CreateAdapter(
            registry,
            new ActorCommandService(actors),
            out _,
            actors,
            scheduler);
        await adapter.InitializeAsync(CancellationToken.None);
        var awaitHandler = Assert.IsType<Func<string[], Task>>(registry.Handlers["await"]);
        var cancelHandler = Assert.IsType<Func<string[], Task>>(registry.Handlers["cancel"]);

        await awaitHandler(["group:setup"]);
        await awaitHandler(["operation:hero-move", "complete:true"]);
        await cancelHandler(["operation:hero-move"]);

        Assert.Equal(("wait", OperationTarget.ForGroup("setup")), scheduler.Calls[0]);
        Assert.Equal(("complete", OperationTarget.ForId("hero-move")), scheduler.Calls[1]);
        Assert.Equal(("cancel", OperationTarget.ForId("hero-move")), scheduler.Calls[2]);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task AwaitBlockWaitsForAllTrackedOperations()
    {
        var registry = new FakeCommandRegistry();
        var hero = new FakeCharacterActor("hero", "idle");
        var actors = new FakeActorManager(hero);
        var scheduler = new FakeOperationScheduler();
        var adapter = CreateAdapter(
            registry,
            new ActorCommandService(actors),
            out _,
            actors,
            scheduler);
        await adapter.InitializeAsync(CancellationToken.None);

        await Handler("await")([]);
        await Handler("back")(
            ["backdrop", "appearance:courtroom", "duration:1"]);
        await Handler("char")(
            ["hero", "appearance:happy", "position:left", "duration:1"]);
        var backdrop = actors.Get<FakeBackgroundActor>("backdrop");

        var endTask = Handler("endAwait")([]);
        Assert.False(endTask.IsCompleted);
        backdrop.LastOperation.Complete();
        await Task.Yield();
        Assert.False(endTask.IsCompleted);
        hero.LastOperation.Complete();
        await endTask;

        await adapter.ShutdownAsync(CancellationToken.None);

        Func<string[], Task> Handler(string name) =>
            Assert.IsType<Func<string[], Task>>(registry.Handlers[name]);
    }

    [Fact(Timeout = 5_000)]
    public async Task AwaitBlockCompleteFinishesTrackedOperationsAtBoundary()
    {
        var registry = new FakeCommandRegistry();
        var hero = new FakeCharacterActor("hero", "idle");
        var actors = new FakeActorManager(hero);
        var scheduler = new FakeOperationScheduler();
        var adapter = CreateAdapter(
            registry,
            new ActorCommandService(actors),
            out _,
            actors,
            scheduler);
        await adapter.InitializeAsync(CancellationToken.None);

        await Handler("await")(["complete:true"]);
        await Handler("char")(
            ["hero", "appearance:happy", "position:right", "duration:10"]);
        await Handler("endAwait")([]);

        Assert.Equal(OperationState.Completed, hero.LastOperation.State);
        await adapter.ShutdownAsync(CancellationToken.None);

        Func<string[], Task> Handler(string name) =>
            Assert.IsType<Func<string[], Task>>(registry.Handlers[name]);
    }

    [Fact(Timeout = 5_000)]
    public async Task NestedAwaitBlocksTrackInnerOperationsInBothScopes()
    {
        var registry = new FakeCommandRegistry();
        var hero = new FakeCharacterActor("hero", "idle");
        var actors = new FakeActorManager(hero);
        var scheduler = new FakeOperationScheduler();
        var adapter = CreateAdapter(
            registry,
            new ActorCommandService(actors),
            out _,
            actors,
            scheduler);
        await adapter.InitializeAsync(CancellationToken.None);

        await Handler("await")([]);
        await Handler("char")(
            ["hero", "appearance:happy", "position:left", "duration:1"]);
        await Handler("await")([]);
        await Handler("back")(
            ["backdrop", "appearance:courtroom", "duration:1"]);
        var backdrop = actors.Get<FakeBackgroundActor>("backdrop");

        var innerEnd = Handler("endAwait")([]);
        Assert.False(innerEnd.IsCompleted);
        backdrop.LastOperation.Complete();
        await innerEnd;

        var outerEnd = Handler("endAwait")([]);
        Assert.False(outerEnd.IsCompleted);
        hero.LastOperation.Complete();
        await outerEnd;

        await adapter.ShutdownAsync(CancellationToken.None);

        Func<string[], Task> Handler(string name) =>
            Assert.IsType<Func<string[], Task>>(registry.Handlers[name]);
    }

    [Fact(Timeout = 5_000)]
    public async Task UnmatchedEndAwaitAbortsDialogueWithClearFailure()
    {
        var registry = new FakeCommandRegistry();
        var adapter = CreateAdapter(
            registry,
            new RecordingCommandService(),
            out _);
        await adapter.InitializeAsync(CancellationToken.None);

        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["endAwait"]);
        var exception = await Assert.ThrowsAsync<RuntimeCommandException>(() => handler([]));

        Assert.Same(exception, adapter.LastFailure);
        Assert.Contains("matching await block", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, registry.AbortCount);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public void ActorCommandServiceDependsOnlyOnActorManagerInterface()
    {
        var constructor = Assert.Single(typeof(ActorCommandService).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Equal([typeof(IActorManager)],
            parameters.Select(parameter => parameter.ParameterType));
        Assert.All(parameters, parameter => Assert.True(parameter.ParameterType.IsInterface));
    }

    [Fact(Timeout = 5_000)]
    public async Task ExtensionCommandUsesPublicRegistryAndRuntimeContext()
    {
        var yarnRegistry = new FakeCommandRegistry();
        var actorManager = new FakeActorManager();
        var scheduler = new FakeOperationScheduler();
        var operation = new FakeOperationHandle();
        ProbeCommandArguments? received = null;
        var adapter = CreateAdapter(
            yarnRegistry,
            new ActorCommandService(actorManager),
            out _,
            actorManager,
            scheduler,
            commands => commands.Register<ProbeCommandArguments>((arguments, context) =>
            {
                received = arguments;
                Assert.Same(actorManager, context.Actors);
                Assert.Same(scheduler, context.Operations);
                context.Track(operation);
                return Task.CompletedTask;
            }));

        await adapter.InitializeAsync(CancellationToken.None);
        var begin = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["await"]);
        var handler = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["probe"]);
        var end = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["endAwait"]);
        await begin([]);
        await handler(["alpha", "detail:beta"]);
        var endTask = end([]);

        Assert.Equal("alpha", received!.Value);
        Assert.Equal("beta", received.Detail);
        Assert.False(endTask.IsCompleted);
        operation.Complete();
        await endTask;
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueCancellationCancelsTrackedNonWaitingOperation()
    {
        var yarnRegistry = new FakeCommandRegistry();
        var actor = new FakeCharacterActor("hero", "idle");
        var actorManager = new FakeActorManager(actor);
        var scheduler = new FakeOperationScheduler();
        var adapter = CreateAdapter(
            yarnRegistry,
            new ActorCommandService(actorManager),
            out _,
            actorManager,
            scheduler);
        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["char"]);

        await handler(
            ["hero", "appearance:happy", "position:center", "duration:10"]);
        Assert.Equal(OperationState.Running, actor.LastOperation.State);
        yarnRegistry.CancelDialogue();

        Assert.Equal(OperationState.Cancelled, actor.LastOperation.State);
        Assert.Null(adapter.LastFailure);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueCancellationClearsOpenAwaitBlocks()
    {
        var yarnRegistry = new FakeCommandRegistry();
        var actor = new FakeCharacterActor("hero", "idle");
        var actorManager = new FakeActorManager(actor);
        var scheduler = new FakeOperationScheduler();
        var adapter = CreateAdapter(
            yarnRegistry,
            new ActorCommandService(actorManager),
            out _,
            actorManager,
            scheduler);
        await adapter.InitializeAsync(CancellationToken.None);
        var begin = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["await"]);
        var character = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["char"]);
        var end = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["endAwait"]);

        await begin([]);
        await character(
            ["hero", "appearance:happy", "position:center", "duration:10"]);
        yarnRegistry.CancelDialogue();
        yarnRegistry.BeginDialogue();

        var exception = await Assert.ThrowsAsync<RuntimeCommandException>(() => end([]));
        Assert.Contains("matching await block", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OperationState.Cancelled, actor.LastOperation.State);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task FailedInitialModificationRollsBackNewActor()
    {
        var manager = new FakeActorManager();
        var service = new ActorCommandService(manager);

        await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            service.CharacterAsync(new CharacterCommand(
                "broken", "invalid", CharacterPositionPreset.Center, 0, false, null, null)));

        Assert.False(manager.TryGet("broken", out _));
    }

    private static ActorCommandService CreateService(FakeCharacterActor actor)
    {
        var manager = new FakeActorManager(actor);
        return new ActorCommandService(manager);
    }

    private static TArguments Bind<TArguments>(
        IReadOnlyList<string> arguments,
        double defaultDuration = 0.35)
        where TArguments : class, new()
    {
        var commands = new RuntimeCommandRegistry();
        commands.Register<TArguments>((_, _) => Task.CompletedTask);
        var descriptor = Assert.Single(commands.Registrations).Descriptor;
        return new CommandBinder(new CommandBindingContext(defaultDuration))
            .Bind<TArguments>(descriptor, arguments);
    }

    private static YarnCommandAdapter CreateAdapter(
        FakeCommandRegistry yarnRegistry,
        IActorCommandService actorCommands,
        out RuntimeCommandCatalog catalog,
        IActorManager? actors = null,
        IOperationScheduler? operations = null,
        Action<RuntimeCommandRegistry>? configure = null)
    {
        var commands = new RuntimeCommandRegistry();
        BuiltInRuntimeCommands.Register(commands, actorCommands);
        configure?.Invoke(commands);
        catalog = commands.BuildCatalog();
        var dispatcher = new RuntimeCommandDispatcher(
            new CommandBinder(new CommandBindingContext(0.35)),
            actors,
            operations);
        return new YarnCommandAdapter(yarnRegistry, catalog, dispatcher);
    }

    [RuntimeCommand("probe", Category = "Test", Usage = "probe <value> detail:<value>")]
    private sealed class ProbeCommandArguments
    {
        [CommandParameter("value", 0, Primary = true, Required = true)]
        public string Value { get; set; } = string.Empty;

        [CommandParameter("detail", 1, Required = true)]
        public string Detail { get; set; } = string.Empty;
    }

    [RuntimeCommand("endAwait")]
    private sealed class DuplicateEndAwaitArguments
    {
    }

    [RuntimeCommand("WAIT")]
    private sealed class ReservedWaitArguments
    {
    }

    [RuntimeCommand("incomplete")]
    private sealed class IncompleteMetadataArguments
    {
        public string MissingMetadata { get; set; } = string.Empty;
    }

    private sealed class FailingCommandModule : IRuntimeCommandModule
    {
        public void Register(IRuntimeCommandRegistrar commands)
        {
            commands.Register<ProbeCommandArguments>((_, _) => Task.CompletedTask);
            commands.Register<DuplicateEndAwaitArguments>((_, _) => Task.CompletedTask);
        }
    }

    private readonly record struct CustomCommandValue(string Text);

    [RuntimeCommand("customValue")]
    private sealed class CustomValueCommandArguments
    {
        [CommandParameter("value", 0, Primary = true, Required = true)]
        public CustomCommandValue Value { get; set; }
    }

    [RuntimeCommand("constantDefault")]
    private sealed class ConstantDefaultCommandArguments
    {
        [CommandParameter("enabled", 0, DefaultValue = "true")]
        public bool Enabled { get; set; }
    }

    private sealed class FakeCommandRegistry : IYarnCommandRegistry
    {
        private CancellationTokenSource dialogueCancellation = new();

        public CancellationToken DialogueCancellationToken => dialogueCancellation.Token;

        public Dictionary<string, Delegate> Handlers { get; } = new(StringComparer.Ordinal);

        public List<string> RemovedCommands { get; } = new();

        public int AbortCount { get; private set; }

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

        public void CancelDialogue() => dialogueCancellation.Cancel();

        public void BeginDialogue()
        {
            dialogueCancellation.Dispose();
            dialogueCancellation = new CancellationTokenSource();
        }

        public void AbortDialogue() => AbortCount++;

        public void Dispose() => dialogueCancellation.Dispose();
    }

    private sealed class RecordingCommandService : IActorCommandService
    {
        public CharacterCommand? Character { get; private set; }

        public Task BackAsync(BackCommand command) => Task.CompletedTask;

        public Task CharacterAsync(CharacterCommand command)
        {
            Character = command;
            return Task.CompletedTask;
        }

        public Task HideAsync(HideCommand command) => Task.CompletedTask;

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
            IActor actor = actorId switch
            {
                "backdrop" => new FakeBackgroundActor(actorId, "default"),
                "broken" => new FailingAppearanceActor(actorId, "default"),
                _ => new FakeCharacterActor(actorId, "default"),
            };
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

        public virtual void SetAppearance(string appearance) => Appearance = appearance;

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

    private class FakeCharacterActor : FakeActor, ICharacterActor
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

    private sealed class FailingAppearanceActor : FakeCharacterActor
    {
        public FailingAppearanceActor(string id, string appearance)
            : base(id, appearance)
        {
        }

        public override void SetAppearance(string appearance) =>
            throw new UnknownActorAppearanceException(Id, appearance);
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
