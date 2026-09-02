using System.Reflection;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Operations;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeCommandTests
{
    [Fact]
    public void BinderConvertsTypedArgumentsAndIgnoresNamedArgumentOrder()
    {
        var character = Bind<CharacterDirectionCommandArguments>(
            [
                "maya",
                "wait:true",
                "at:right",
                "duration:0.5",
                "appearance:happy",
                "scale:0.75",
                "z:12",
            ]);

        Assert.Equal("maya", character.Actor);
        Assert.Equal("happy", character.Appearance);
        Assert.Equal("right", character.At);
        Assert.Equal(0.5, character.Duration);
        Assert.True(character.Wait);
        Assert.Equal(0.75, character.Scale);
        Assert.Equal(12, character.Z);
    }

    [Fact]
    public void BinderUsesConfiguredAndLanguageDefaults()
    {
        var backdrop = Bind<BackdropDirectionCommandArguments>(["courtroom"]);
        var character = Bind<CharacterDirectionCommandArguments>(
            ["maya", "appearance:happy", "at:center"]);

        Assert.Equal("courtroom", backdrop.Resource);
        Assert.False(backdrop.Wait);
        Assert.Equal("center", character.At);
        Assert.False(character.Wait);

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
    [InlineData("at least one", "character hero")]
    [InlineData("integer", "character hero appearance:happy z:1.5")]
    [InlineData("duration", "backdrop dark duration:NaN")]
    [InlineData("at least 0", "backdrop dark duration:-0.1")]
    [InlineData("mutually exclusive", "await group:setup operation:move")]
    [InlineData("duplicated", "character hero appearance:happy wait:false WAIT:true")]
    [InlineData("unknown parameter", "backdrop dark time:1")]
    [InlineData("unknown parameter", "backdrop dark over:0.3")]
    [InlineData("unknown parameter", "character hero appearance:happy until:settle")]
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
                case "character":
                    Bind<CharacterDirectionCommandArguments>(pieces[1..]);
                    break;
                case "backdrop":
                    Bind<BackdropDirectionCommandArguments>(pieces[1..]);
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

    [Fact]
    public void CatalogIsTheSingleSourceOfBuiltInCommandNames()
    {
        var commands = new RuntimeCommandRegistry();
        BuiltInRuntimeCommands.Register(commands);
        var catalog = commands.BuildCatalog();

        Assert.Equal(
            ["await", "endAwait", "cancel", "save", "autoSave"],
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
        BuiltInRuntimeCommands.Register(commands);

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
        BuiltInRuntimeCommands.Register(commands);

        Assert.Throws<RuntimeCommandException>(() =>
            commands.RegisterModule(new FailingCommandModule()));

        Assert.DoesNotContain(
            commands.Registrations,
            registration => registration.Name == "probe");
        Assert.Equal(5, commands.Registrations.Count);
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
        var adapter = CreateAdapter(registry, out var catalog);

        await adapter.InitializeAsync(CancellationToken.None);

        Assert.Equal(
            catalog.Commands.Select(command => command.RootName).Distinct(
                StringComparer.OrdinalIgnoreCase),
            registry.Handlers.Keys);
        Assert.DoesNotContain("wait", registry.Handlers.Keys);
        Assert.DoesNotContain("stop", registry.Handlers.Keys);
        Assert.All(registry.Handlers.Values, handler =>
        {
            var parameter = Assert.Single(handler.Method.GetParameters());
            Assert.NotNull(parameter.GetCustomAttribute<ParamArrayAttribute>());
        });
        await adapter.ShutdownAsync(CancellationToken.None);
        Assert.Empty(registry.Handlers);
        Assert.Equal(
            catalog.Commands.Select(command => command.RootName).Distinct(
                StringComparer.OrdinalIgnoreCase).Reverse(),
            registry.RemovedCommands);
    }

    [Fact(Timeout = 5_000)]
    public async Task AdapterDispatchesHierarchicalCommandToItsOwnDescriptor()
    {
        var registry = new FakeCommandRegistry();
        HierarchicalProbeArguments? received = null;
        var adapter = CreateAdapter(
            registry,
            out var catalog,
            configure: commands => commands.Register<HierarchicalProbeArguments>(
                (arguments, _) =>
                {
                    received = arguments;
                    return Task.CompletedTask;
                }));

        await adapter.InitializeAsync(CancellationToken.None);

        Assert.Equal("camera probe", catalog.Get("camera probe").Name);
        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["camera"]);
        await handler(["probe", "sample", "detail:value"]);

        Assert.Equal("sample", received?.Value);
        Assert.Equal("value", received?.Detail);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task AdapterCapturesClearArgumentFailure()
    {
        var registry = new FakeCommandRegistry();
        var logs = new RecordingLogSink();
        var logger = new StructuredRuntimeLogger(logs, RuntimeLogLevel.Debug);
        var adapter = CreateAdapter(
            registry,
            out var catalog,
            logger: logger,
            configure: commands => commands.Register<CharacterDirectionCommandArguments>(
                (_, _) => Task.CompletedTask));
        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["character"]);

        var exception = await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            handler(["hero"]));

        Assert.Same(exception, adapter.LastFailure);
        Assert.Contains("at least one", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            catalog.Get("character").Descriptor.Usage,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, registry.AbortCount);
        var error = Assert.Single(
            logs.Events,
            log => log.EventId == "command.faulted");
        Assert.Equal(RuntimeLogLevel.Error, error.Level);
        Assert.Equal("abort", error.Properties["disposition"]);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task LenientAdapterReportsAndSkipsRecoverableArgumentFailure()
    {
        var registry = new FakeCommandRegistry();
        var logs = new RecordingLogSink();
        var logger = new StructuredRuntimeLogger(logs, RuntimeLogLevel.Debug);
        var adapter = CreateAdapter(
            registry,
            out _,
            logger: logger,
            configure: commands => commands.Register<CharacterDirectionCommandArguments>(
                (_, _) => Task.CompletedTask),
            abortOnRecoverableFailure: false);
        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["character"]);

        await handler(["hero"]);

        Assert.NotNull(adapter.LastFailure);
        Assert.True(adapter.LastFailure!.IsRecoverable);
        Assert.Equal(0, registry.AbortCount);
        var warning = Assert.Single(
            logs.Events,
            log => log.EventId == "command.rejected");
        Assert.Equal(RuntimeLogLevel.Warning, warning.Level);
        Assert.Equal("skip", warning.Properties["disposition"]);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task LenientAdapterReportsAndSkipsUnknownCommand()
    {
        var registry = new FakeCommandRegistry();
        var logs = new RecordingLogSink();
        var logger = new StructuredRuntimeLogger(logs, RuntimeLogLevel.Debug);
        var adapter = CreateAdapter(
            registry,
            out _,
            logger: logger,
            abortOnRecoverableFailure: false);
        await adapter.InitializeAsync(CancellationToken.None);

        registry.ReportUnhandledCommand("missing detail:value");

        Assert.Equal("missing", adapter.LastFailure?.CommandName);
        Assert.True(adapter.LastFailure?.IsRecoverable);
        Assert.Equal(0, registry.AbortCount);
        var warning = Assert.Single(
            logs.Events,
            log => log.EventId == "command.unknown");
        Assert.Equal(RuntimeLogLevel.Warning, warning.Level);
        Assert.Equal("skip", warning.Properties["disposition"]);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task LenientAdapterStillAbortsForUnexpectedRuntimeFailure()
    {
        var registry = new FakeCommandRegistry();
        var logs = new RecordingLogSink();
        var logger = new StructuredRuntimeLogger(logs, RuntimeLogLevel.Debug);
        var adapter = CreateAdapter(
            registry,
            out _,
            logger: logger,
            configure: commands => commands.Register<ProbeCommandArguments>(
                (_, _) => throw new InvalidOperationException("engine bug")),
            abortOnRecoverableFailure: false);
        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["probe"]);

        var failure = await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            handler(["value", "detail:detail"]));

        Assert.False(failure.IsRecoverable);
        Assert.Equal(1, registry.AbortCount);
        var error = Assert.Single(
            logs.Events,
            log => log.EventId == "command.faulted");
        Assert.Equal(RuntimeLogLevel.Error, error.Level);
        Assert.Equal("abort", error.Properties["disposition"]);
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
    public async Task UnmatchedEndAwaitAbortsDialogueWithClearFailure()
    {
        var registry = new FakeCommandRegistry();
        var adapter = CreateAdapter(
            registry,
            out _);
        await adapter.InitializeAsync(CancellationToken.None);

        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["endAwait"]);
        var exception = await Assert.ThrowsAsync<RuntimeCommandException>(() => handler([]));

        Assert.Same(exception, adapter.LastFailure);
        Assert.Contains("matching await block", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, registry.AbortCount);
        await adapter.ShutdownAsync(CancellationToken.None);
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
    public async Task CommandsDispatchedAfterDialogueCancellationAreIgnored()
    {
        var yarnRegistry = new FakeCommandRegistry();
        var adapter = CreateAdapter(
            yarnRegistry,
            out _);
        await adapter.InitializeAsync(CancellationToken.None);
        var end = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["endAwait"]);

        adapter.CancelDialogue();
        await end([]);

        Assert.Null(adapter.LastFailure);
        Assert.Equal(0, yarnRegistry.AbortCount);
        await adapter.ShutdownAsync(CancellationToken.None);
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
        out RuntimeCommandCatalog catalog,
        IActorManager? actors = null,
        IOperationScheduler? operations = null,
        Action<RuntimeCommandRegistry>? configure = null,
        IRuntimeLogger? logger = null,
        bool abortOnRecoverableFailure = true)
    {
        var commands = new RuntimeCommandRegistry();
        BuiltInRuntimeCommands.Register(commands);
        configure?.Invoke(commands);
        catalog = commands.BuildCatalog();
        var dispatcher = new RuntimeCommandDispatcher(
            new CommandBinder(new CommandBindingContext(0.35)),
            actors,
            operations,
            logger: logger,
            abortOnRecoverableFailure: abortOnRecoverableFailure);
        return new YarnCommandAdapter(
            yarnRegistry,
            catalog,
            dispatcher);
    }

    [RuntimeCommand("probe", Category = "Test", Usage = "probe <value> detail:<value>")]
    private sealed class ProbeCommandArguments
    {
        [CommandParameter("value", 0, Primary = true, Required = true)]
        public string Value { get; set; } = string.Empty;

        [CommandParameter("detail", 1, Required = true)]
        public string Detail { get; set; } = string.Empty;
    }

    [RuntimeCommand("camera", "probe", Category = "Test")]
    private sealed class HierarchicalProbeArguments
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

        public event Action<string>? UnhandledCommand;

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

        public void ReportUnhandledCommand(string commandText) =>
            UnhandledCommand?.Invoke(commandText);

        public void Dispose() => dialogueCancellation.Dispose();
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

        public IActor GetOrCreate(ActorSpawnRequest request)
        {
            if (actors.TryGetValue(request.ActorId, out var existing))
            {
                return existing;
            }

            IActor actor = request.ActorId == "broken"
                ? new FailingAppearanceActor(request.ActorId, request.InitialAppearance)
                : request.FallbackFactoryId switch
                {
                    ActorFactoryIds.Background =>
                        new FakeBackgroundActor(request.ActorId, request.InitialAppearance),
                    ActorFactoryIds.Character =>
                        new FakeCharacterActor(request.ActorId, request.InitialAppearance),
                    _ => throw new ActorFactoryNotFoundException(
                        request.FallbackFactoryId,
                        request.ActorId),
                };
            actors.Add(request.ActorId, actor);
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


    private class FakeCharacterActor : FakeActor, ICharacterActor
    {
        public FakeCharacterActor(string id, string appearance)
            : base(id, appearance)
        {
        }

        public Sprite2D View => null!;

        public Vector2 Scale { get; private set; } = Vector2.One;

        public CharacterPositionPreset PositionPreset { get; private set; }

        public void SetScale(Vector2 scale) => Scale = scale;

        public void SetScenePosition(Vector2 scenePosition) => Position = scenePosition;

        public void SetPosition(CharacterPositionPreset preset)
        {
            PositionPreset = preset;
        }

        public IOperationHandle ChangeAppearance(
            string appearance,
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Appearance = appearance;
            return LastOperation = new FakeOperationHandle(operationId, groupId);
        }

        public IOperationHandle ChangeScenePosition(
            Vector2 scenePosition,
            double durationSeconds,
            string? operationId = null,
            string? groupId = null)
        {
            Position = scenePosition;
            return LastOperation = new FakeOperationHandle(operationId, groupId);
        }

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

        public string Slot { get; private set; } = BackgroundSlotIds.Main;

        public float StartOpacity { get; private set; } = 1;

        public void SetSlot(string slot) => Slot = BackgroundSlotIds.Normalize(slot);

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
