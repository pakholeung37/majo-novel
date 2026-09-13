#nullable enable

using System.Reflection;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Commands.Audio;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Operations;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeCommandTests
{
    [Fact]
    public async Task NewAppearanceWithPositionUsesVisibilityFadeByDefault()
    {
        var beats = new FakeDirectionBeatRuntime();
        var command = new CharacterCommand(beats,
            new DirectionScopeResolver(), new DelegateDialogueNodeAccessor(() => "Start"),
            new DirectionCommandPolicy(beats));
        var bound = TestCatalogBuilder.CreateBound(command);
        await bound.ExecuteAsync(new CommandInput("character",
            new[] { "hero", "appearance:blue", "at:left" }), new CommandExecutionContext("default-appearance"));
        var patch = Assert.IsType<ActorPatch>(beats.LastSubmitted);
        Assert.Equal(DirectionTransitionId.Fade, patch.Transition.Kind);
        Assert.True(patch.Position.HasValue);
    }

    [Fact]
    public void BinderConvertsTypedArgumentsAndIgnoresNamedArgumentOrder()
    {
        var beats = new FakeDirectionBeatRuntime();
        var nodeAccessor = new DelegateDialogueNodeAccessor(() => "node");
        var command = new CharacterCommand(
            beats,
            new DirectionScopeResolver(),
            nodeAccessor,
            new DirectionCommandPolicy(beats, DirectionAnimationDefaults.FromConfiguration(null)));

        var bound = TestCatalogBuilder.CreateBound(command);
        var character = bound.Bind(
            new RawCommandArguments("maya", new Dictionary<string, string>
            {
                ["wait"] = "true",
                ["at"] = "right",
                ["duration"] = "0.5",
                ["appearance"] = "happy",
                ["scale"] = "0.75",
                ["z"] = "12",
            }));

        Assert.Equal("maya", character.Actor);
        Assert.Equal("happy", character.Appearance.Value);
        Assert.Equal(new PositionRef.Named("right"), character.At.Value);
        Assert.Equal(0.5, character.Transition.Duration.Value);
        Assert.True(character.Transition.Wait.Value);
        Assert.Equal(0.75f, character.Scale.Value);
        Assert.Equal(12, character.Z.Value);
    }

    [Fact]
    public void BinderUsesConfiguredAndLanguageDefaults()
    {
        var beats = new FakeDirectionBeatRuntime();
        var nodeAccessor = new DelegateDialogueNodeAccessor(() => "node");
        var policy = new DirectionCommandPolicy(beats, DirectionAnimationDefaults.FromConfiguration(null));
        var backdropCommand = new BackdropCommand(
            beats,
            new DirectionScopeResolver(),
            nodeAccessor,
            policy);

        var backdropBound = TestCatalogBuilder.CreateBound(backdropCommand);
        var backdrop = backdropBound.Bind(
            new RawCommandArguments("courtroom", new Dictionary<string, string>()));

        var characterCommand = new CharacterCommand(
            beats,
            new DirectionScopeResolver(),
            nodeAccessor,
            policy);

        var characterBound = TestCatalogBuilder.CreateBound(characterCommand);
        var character = characterBound.Bind(
            new RawCommandArguments("maya", new Dictionary<string, string>
            {
                ["appearance"] = "happy",
                ["at"] = "center",
            }));

        Assert.Equal("courtroom", backdrop.Definition);
        Assert.False(backdrop.Transition.Wait.Value);
        Assert.Equal(new PositionRef.Named("center"), character.At.Value);
        Assert.False(character.Transition.Wait.Value);

        var constantCommand = new ConstantDefaultCommand();
        var constantBound = TestCatalogBuilder.CreateBound(constantCommand);
        var constant = constantBound.Bind(
            new RawCommandArguments(null, new Dictionary<string, string>()));
        Assert.True(constant.Enabled.Value);
        Assert.False(constant.Enabled.WasProvided);
        Assert.Equal(CommandValueOrigin.ContractDefault, constant.Enabled.Origin);
    }

    [Theory]
    [InlineData("Unknown", "character hero unexpected:true")]
    [InlineData("integer", "character hero appearance:happy z:1.5")]
    [InlineData("duration", "backdrop dark duration:NaN")]
    [InlineData("at least 0", "backdrop dark duration:-0.1")]
    [InlineData("duplicated", "character hero appearance:happy wait:false WAIT:true")]
    [InlineData("unknown parameter", "backdrop dark time:1")]
    [InlineData("unknown parameter", "backdrop dark over:0.3")]
    [InlineData("unknown parameter", "character hero appearance:happy until:settle")]
    public void BinderRejectsInvalidArgumentsWithUsage(
        string expectedDiagnostic,
        string commandText)
    {
        var pieces = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var beats = new FakeDirectionBeatRuntime();
        var nodeAccessor = new DelegateDialogueNodeAccessor(() => "node");
        var policy = new DirectionCommandPolicy(beats, DirectionAnimationDefaults.FromConfiguration(null));

        var exception = Assert.Throws<RuntimeCommandException>(() =>
        {
            switch (pieces[0])
            {
                case "character":
                    {
                        var cmd = new CharacterCommand(
                            beats,
                            new DirectionScopeResolver(),
                            nodeAccessor,
                            policy);
                        var input = new CommandInput("character", pieces[1..]);
                        var bound = TestCatalogBuilder.CreateBound(cmd);
                        bound.Bind(input);
                        break;
                    }
                case "backdrop":
                    {
                        var cmd = new BackdropCommand(
                            beats,
                            new DirectionScopeResolver(),
                            nodeAccessor,
                            policy);
                        var input = new CommandInput("backdrop", pieces[1..]);
                        var bound = TestCatalogBuilder.CreateBound(cmd);
                        bound.Bind(input);
                        break;
                    }
            }
        });

        Assert.Contains(expectedDiagnostic, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Usage:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogIsTheSingleSourceOfBuiltInCommandNames()
    {
        var commands = new RuntimeCommandRegistry();
        commands.RegisterModule(new BuiltInCommandModule());
        commands.SealRegistrations();
        var catalog = commands.BuildCatalog();

        Assert.Equal(
            ["save", "autoSave"],
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
        commands.RegisterModule(new BuiltInCommandModule());

        var duplicate = Assert.Throws<RuntimeCommandException>(() =>
            commands.Add("save", _ => new DuplicateCommand(), "test"));
        Assert.Contains("already registered", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        var reserved = Assert.Throws<RuntimeCommandException>(() =>
            new RuntimeCommandRegistry().Add("WAIT", _ => new ReservedWaitCommand(), "test"));
        Assert.Contains("Yarn Spinner", reserved.Message, StringComparison.Ordinal);

        commands.SealRegistrations();
        var late = Assert.Throws<RuntimeCommandException>(() =>
            commands.Add("probe", _ => new ProbeCommand(), "test"));
        Assert.Contains("before Runtime initialization", late.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedCommandModuleRegistrationRollsBackPartialModule()
    {
        var commands = new RuntimeCommandRegistry();
        commands.RegisterModule(new BuiltInCommandModule());

        Assert.Throws<RuntimeCommandException>(() =>
            commands.RegisterModule(new FailingCommandModule()));

        Assert.DoesNotContain(
            commands.Registrations,
            registration => registration.Name == "probe");
        Assert.Equal(2, commands.Registrations.Count);
    }

    [Fact]
    public void BuiltInCommandModuleRegistersExpectedCommandsThroughModuleInterface()
    {
        var commands = new RuntimeCommandRegistry();
        commands.RegisterModule(new BuiltInCommandModule());
        commands.SealRegistrations();
        var catalog = commands.BuildCatalog();

        Assert.Equal(
            ["save", "autoSave"],
            catalog.Commands.Select(command => command.Name));
    }

    [Fact]
    public void BuiltInAudioAndDirectionModulesRegisterThroughModuleInterface()
    {
        var commands = new RuntimeCommandRegistry();
        commands.RegisterModule(new BuiltInCommandModule());
        commands.RegisterModule(new AudioCommandModule());
        commands.RegisterModule(new DirectionCommandModule());

        var registeredNames = commands.Registrations.Select(c => c.Name).ToList();
        Assert.Contains("save", registeredNames);
        Assert.Contains("autoSave", registeredNames);
        Assert.Contains("bgm", registeredNames);
        Assert.Contains("voice", registeredNames);
        Assert.Contains("camera move", registeredNames);
    }

    [Fact]
    public void BinderUsesRegisteredCustomValueConverter()
    {
        var bound = TestCatalogBuilder.CreateBound(
            new CustomValueCommand(),
            configureConverters: c => c.AddDefault(value => new CustomCommandValue(value.ToUpperInvariant())));

        var arguments = (CustomValueCommand.Arguments)bound.BindUntyped(
            new RawCommandArguments("sample", new Dictionary<string, string>()));

        Assert.Equal("SAMPLE", arguments.Value.Text);
    }

    [Fact]
    public void BinderValidationRejectsMissingConverterBeforeCommandExecution()
    {
        var exception = Assert.Throws<RuntimeCommandException>(() =>
            TestCatalogBuilder.CreateCatalog(new CustomValueCommand()));

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
        HierarchicalProbeCommand.Arguments? received = null;
        var adapter = CreateAdapter(
            registry,
            out var catalog,
            configure: commands => commands.Add(
                "camera probe",
                _ => new HierarchicalProbeCommand((arguments, _) =>
                {
                    received = arguments;
                    return Task.CompletedTask;
                }),
                "test"));

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
        var beats = new FakeDirectionBeatRuntime();
        var nodeAccessor = new DelegateDialogueNodeAccessor(() => "node");
        var policy = new DirectionCommandPolicy(beats, DirectionAnimationDefaults.FromConfiguration(null));
        var services = new ServiceResolverBuilder()
            .Add<IDirectionBeatRuntime>(beats)
            .Add<IDialogueNodeAccessor>(nodeAccessor)
            .Add<IDirectionScopeResolver>(new DirectionScopeResolver())
            .Add<IDirectionCommandPolicy>(policy)
            .Build();

        var adapter = CreateAdapter(
            registry,
            out var catalog,
            logger: logger,
            services: services,
            configure: commands =>
            {
                DirectionCommandModule.RegisterConverters(commands.Converters);
                commands.Add(
                    "character",
                    s => new CharacterCommand(
                        s.Get<IDirectionBeatRuntime>(),
                        s.Get<IDirectionScopeResolver>(),
                        s.Get<IDialogueNodeAccessor>(),
                        s.Get<IDirectionCommandPolicy>()),
                    "test");
            });
        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["character"]);

        var exception = await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            handler(["hero", "unexpected:true"]));

        Assert.Same(exception, adapter.LastFailure);
        Assert.Contains("unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            catalog.Get("character").Usage,
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
        var beats = new FakeDirectionBeatRuntime();
        var nodeAccessor = new DelegateDialogueNodeAccessor(() => "node");
        var policy = new DirectionCommandPolicy(beats, DirectionAnimationDefaults.FromConfiguration(null));
        var services = new ServiceResolverBuilder()
            .Add<IDirectionBeatRuntime>(beats)
            .Add<IDialogueNodeAccessor>(nodeAccessor)
            .Add<IDirectionScopeResolver>(new DirectionScopeResolver())
            .Add<IDirectionCommandPolicy>(policy)
            .Build();

        var adapter = CreateAdapter(
            registry,
            out _,
            logger: logger,
            services: services,
            configure: commands =>
            {
                DirectionCommandModule.RegisterConverters(commands.Converters);
                commands.Add(
                    "character",
                    s => new CharacterCommand(
                        s.Get<IDirectionBeatRuntime>(),
                        s.Get<IDirectionScopeResolver>(),
                        s.Get<IDialogueNodeAccessor>(),
                        s.Get<IDirectionCommandPolicy>()),
                    "test");
            },
            abortOnRecoverableFailure: false);
        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["character"]);

        await handler(["hero", "unexpected:true"]);

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
            configure: commands => commands.Add(
                "probe",
                _ => new ProbeCommand((_, _) => throw new InvalidOperationException("engine bug")),
                "test"),
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
    public async Task ExtensionCommandUsesPublicRegistryAndRuntimeContext()
    {
        var yarnRegistry = new FakeCommandRegistry();
        ProbeCommand.Arguments? received = null;
        var adapter = CreateAdapter(
            yarnRegistry,
            out _,
            configure: commands => commands.Add(
                "probe",
                _ => new ProbeCommand((arguments, context) =>
                {
                    received = arguments;
                    Assert.NotNull(context);
                    Assert.False(string.IsNullOrEmpty(context.ExecutionId));
                    return Task.CompletedTask;
                }),
                "test"));

        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["probe"]);
        await handler(["alpha", "detail:beta"]);

        Assert.Equal("alpha", received!.Value);
        Assert.Equal("beta", received.Detail);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task CommandsDispatchedAfterDialogueCancellationAreIgnored()
    {
        var yarnRegistry = new FakeCommandRegistry();
        var adapter = CreateAdapter(
            yarnRegistry,
            out _,
            configure: commands => commands.Add(
                "probe",
                _ => new ProbeCommand(),
                "test"));
        await adapter.InitializeAsync(CancellationToken.None);

        var handler = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["probe"]);
        adapter.CancelDialogue();
        await handler(["ignored", "detail:value"]);

        Assert.Null(adapter.LastFailure);
        Assert.Equal(0, yarnRegistry.AbortCount);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task CommandActivityRegistersWithActivityRegistryAndReachesQuiescence()
    {
        var registry = new RuntimeActivityRegistry();
        var yarnRegistry = new FakeCommandRegistry();
        var cmdTcs = new TaskCompletionSource();
        var allowExitTcs = new TaskCompletionSource();

        var commands = new RuntimeCommandRegistry();
        commands.Add("probe", _ => new ProbeCommand(async (args, ctx) =>
        {
            cmdTcs.SetResult();
            await allowExitTcs.Task;
        }), "test");
        commands.SealRegistrations();
        var catalog = commands.BuildCatalog();
        var dispatcher = new RuntimeCommandDispatcher(
            activities: registry);
        var adapter = new YarnCommandAdapter(yarnRegistry, catalog, dispatcher);

        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["probe"]);
        var runTask = handler(["test", "detail:active"]);

        await cmdTcs.Task;
        Assert.True(adapter.HasActiveCommands);
        Assert.True(registry.HasActiveActivities(RuntimeActivityOwner.Dialogue));

        var quiescenceTask = adapter.WaitForQuiescenceAsync(CancellationToken.None);
        Assert.False(quiescenceTask.IsCompleted);

        allowExitTcs.SetResult();
        await runTask;
        await quiescenceTask;

        Assert.False(adapter.HasActiveCommands);
        Assert.False(registry.HasActiveActivities(RuntimeActivityOwner.Dialogue));
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task CommandExecutionDirectlyBindsToSessionCancellation()
    {
        var yarnRegistry = new FakeCommandRegistry();
        var cmdStartedTcs = new TaskCompletionSource();
        var cmdCancelledTcs = new TaskCompletionSource();

        var commands = new RuntimeCommandRegistry();
        commands.Add("probe", _ => new ProbeCommand(async (args, ctx) =>
        {
            cmdStartedTcs.SetResult();
            try
            {
                await Task.Delay(10_000, ctx.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                cmdCancelledTcs.SetResult();
                throw;
            }
        }), "test");
        commands.SealRegistrations();
        var catalog = commands.BuildCatalog();
        var dispatcher = new RuntimeCommandDispatcher();
        var adapter = new YarnCommandAdapter(yarnRegistry, catalog, dispatcher);

        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(yarnRegistry.Handlers["probe"]);
        var runTask = handler(["test", "detail:cancelled"]);

        await cmdStartedTcs.Task;
        Assert.True(adapter.HasActiveCommands);

        yarnRegistry.CancelDialogue();
        await runTask;
        await cmdCancelledTcs.Task;

        await adapter.WaitForQuiescenceAsync(CancellationToken.None);
        Assert.False(adapter.HasActiveCommands);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public void Manifest_GeneratesAndMergesCleanly()
    {
        var commands = new RuntimeCommandRegistry();
        commands.RegisterModule(new BuiltInCommandModule());
        commands.SealRegistrations();
        var catalog = commands.BuildCatalog();

        var manifest1 = RuntimeCommandManifest.FromCatalog(catalog, "provider1", "1.0");
        Assert.Equal("provider1", manifest1.ProviderId);
        Assert.Equal("1.0", manifest1.Version);
        Assert.Contains(manifest1.Commands, c => c.Name == "save");
        Assert.Contains(manifest1.Commands, c => c.Name == "autoSave");

        var json = manifest1.ToJson();
        var roundTrip = RuntimeCommandManifest.FromJson(json);
        Assert.Equal(manifest1.ProviderId, roundTrip.ProviderId);
        Assert.Equal(manifest1.Commands.Count, roundTrip.Commands.Count);

        var customCommands = new RuntimeCommandRegistry();
        customCommands.Add("probe", _ => new ProbeCommand(), "provider2", "2.0");
        customCommands.SealRegistrations();
        var catalog2 = customCommands.BuildCatalog();
        var manifest2 = RuntimeCommandManifest.FromCatalog(catalog2, "provider2", "2.0");

        var merged = RuntimeCommandManifestMerger.Merge("merged_provider", "1.0", new[] { manifest1, manifest2 });
        Assert.Equal(3, merged.Commands.Count);
        Assert.Contains(merged.Commands, c => c.Name == "save");
        Assert.Contains(merged.Commands, c => c.Name == "probe");
        Assert.Equal("provider2", manifest2.Commands.Single(c => c.Name == "probe").ProviderId);
        Assert.Equal("2.0", manifest2.Commands.Single(c => c.Name == "probe").Version);
    }

    [Fact]
    public void DefinitionRejectsDuplicateParametersAndMultiplePrimaryParameters()
    {
        Assert.Throws<RuntimeCommandException>(() =>
            CommandDefinition<WaitArguments>
                .Create("duplicate")
                .Optional<bool>("value")
                .Optional<bool>("VALUE")
                .Build(static context => reader => new WaitArguments(CommandValue<bool>.Unspecified())));

        Assert.Throws<RuntimeCommandException>(() =>
            CommandDefinition<WaitArguments>
                .Create("multiple-primary")
                .Primary<string>("first")
                .Primary<string>("second")
                .Build(static context => reader => new WaitArguments(CommandValue<bool>.Unspecified())));

        Assert.Throws<RuntimeCommandException>(() =>
            CommandDefinition<WaitArguments>
                .Create("unknown-set")
                .Optional<bool>("value")
                .ParameterSet(CommandParameterSetRule.ExactlyOne, "value", "missing")
                .Build(static context => reader => new WaitArguments(CommandValue<bool>.Unspecified())));
    }

    [Fact]
    public async Task CommandCannotExecuteBeforeCatalogAttachment()
    {
        var command = new ProbeCommand();
        var exception = await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            command.ExecuteAsync(
                new CommandInput("probe"),
                new CommandExecutionContext("unattached")).AsTask());

        Assert.Equal(CommandFailurePhase.Schema, exception.FailurePhase);
    }

    [Fact]
    public void ParameterGroups_CanBeReusedAcrossDefinitions()
    {
        var def1 = CommandDefinition<WaitArguments>
            .Create("cmd1")
            .Group("wait", "Wait", typeof(WaitArguments), typeof(BlockingWait), () => new BlockingWait(), new[]
            {
                new CommandParameterDeclaration("wait", "Wait", 20, false, false, typeof(bool), true, "bool", HasDefault: true, DefaultText: "false"),
            })
            .Build(static context =>
            {
                var slot = context.GetSlot("wait.wait");
                return reader => new WaitArguments(reader.ReadOptional<bool>(slot));
            });

        var cmd1 = new TestDefinitionCommand(def1);
        var bound1 = TestCatalogBuilder.CreateBound(cmd1);

        var res1 = bound1.Bind(new RawCommandArguments(null, new Dictionary<string, string>()));
        Assert.True(res1.Wait.Value);
        Assert.False(res1.Wait.WasProvided);
        Assert.Equal(CommandValueOrigin.ContractDefault, res1.Wait.Origin);

        var res2 = bound1.Bind(new RawCommandArguments(null, new Dictionary<string, string> { ["wait"] = "false" }));
        Assert.False(res2.Wait.Value);
        Assert.True(res2.Wait.WasProvided);
        Assert.Equal(CommandValueOrigin.Explicit, res2.Wait.Origin);
    }

    private sealed record TargetGroupArguments(CommandValue<string> Target);

    [Fact]
    public void RegisteredParameterGroupAndManifestMetadataArePreserved()
    {
        var def = CommandDefinition<WaitArguments>
            .Create("plugin command")
            .Optional<string>("other")
            .Group("plugin-target", "Target", typeof(TargetGroupArguments), groupParameters: new[]
            {
                new CommandParameterDeclaration("target", "Target", 0, false, false, typeof(string), true, "value"),
            })
            .ParameterSet(CommandParameterSetRule.AtLeastOne, "target", "other")
            .Build(static context => reader => new WaitArguments(CommandValue<bool>.Unspecified()));

        var registry = new RuntimeCommandRegistry();
        registry.Add("plugin command", _ => new TestDefinitionCommand(def), "plugin", "9.0");
        var catalog = registry.BuildCatalog();
        var manifest = RuntimeCommandManifest.FromCatalog(catalog, "fallback", "1.0");
        var entry = Assert.Single(manifest.Commands);

        Assert.Equal("plugin", entry.ProviderId);
        Assert.Equal("9.0", entry.Version);
        Assert.Contains(entry.ParameterGroups, group => group.Id == "plugin-target");
        Assert.Contains(entry.ParameterSets, set => set.Rule == CommandParameterSetRule.AtLeastOne);
    }

    [Fact(Timeout = 5_000)]
    public async Task PreloadBarrierIsAnActiveCommandAndCancellationPreventsExecution()
    {
        var registry = new FakeCommandRegistry();
        var executed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = CreateAdapter(registry, out _, configure: commands => commands.Add(
            "probe", _ => new ProbeCommand((_, _) => { executed = true; return Task.CompletedTask; }), "test"));
        adapter.PrepareCommandAsync = async (root, arguments, token) =>
        {
            Assert.Equal("probe", root);
            Assert.Equal("test", arguments[0]);
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        await adapter.InitializeAsync(CancellationToken.None);
        var handler = Assert.IsType<Func<string[], Task>>(registry.Handlers["probe"]);
        var running = handler(["test", "detail:ready"]);
        await entered.Task;
        Assert.True(adapter.HasActiveCommands);
        Assert.False(adapter.WaitForQuiescenceAsync().IsCompleted);
        Assert.False(executed);
        adapter.CancelDialogue();
        await running;
        Assert.False(executed);
        Assert.Null(adapter.LastFailure);
        await adapter.ShutdownAsync(CancellationToken.None);
    }

    private static YarnCommandAdapter CreateAdapter(
        FakeCommandRegistry yarnRegistry,
        out RuntimeCommandCatalog catalog,
        IActorManager? actors = null,
        IOperationRunner? operations = null,
        Action<RuntimeCommandRegistry>? configure = null,
        IRuntimeLogger? logger = null,
        bool abortOnRecoverableFailure = true,
        IServiceResolver? services = null)
    {
        var commands = new RuntimeCommandRegistry();
        commands.RegisterModule(new BuiltInCommandModule());
        configure?.Invoke(commands);
        commands.SealRegistrations();
        catalog = commands.BuildCatalog(services);
        var dispatcher = new RuntimeCommandDispatcher(
            services,
            logger: logger,
            abortOnRecoverableFailure: abortOnRecoverableFailure);
        return new YarnCommandAdapter(
            yarnRegistry,
            catalog,
            dispatcher);
    }

    private sealed class TestDefinitionCommand : RuntimeCommand<WaitArguments>
    {
        public TestDefinitionCommand(CommandDefinition<WaitArguments> definition) => Definition = definition;

        public override CommandDefinition<WaitArguments> Definition { get; }

        protected override ValueTask ExecuteAsync(WaitArguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;
    }

    private sealed class ProbeCommand : RuntimeCommand<ProbeCommand.Arguments>
    {
        private readonly Func<Arguments, CommandExecutionContext, Task>? execution;

        public ProbeCommand(Func<Arguments, CommandExecutionContext, Task>? execution = null)
        {
            this.execution = execution;
        }

        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>
                .Create("probe", category: "Test", usage: "probe <value> detail:<value>")
                .Primary<string>("value")
                .Required<string>("detail")
                .Build(static context =>
                {
                    var valSlot = context.GetSlot("value");
                    var detSlot = context.GetSlot("detail");
                    return reader => new Arguments(
                        reader.ReadRequired<string>(valSlot),
                        reader.ReadRequired<string>(detSlot));
                });

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
        {
            if (execution is not null)
            {
                await execution(arguments, context);
            }
        }

        public sealed record Arguments(string Value, string Detail);
    }

    private sealed class HierarchicalProbeCommand : RuntimeCommand<HierarchicalProbeCommand.Arguments>
    {
        private readonly Func<Arguments, CommandExecutionContext, Task>? execution;

        public HierarchicalProbeCommand(Func<Arguments, CommandExecutionContext, Task>? execution = null)
        {
            this.execution = execution;
        }

        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>
                .Create("camera probe", category: "Test", usage: "camera probe <value> detail:<value>")
                .Primary<string>("value")
                .Required<string>("detail")
                .Build(static context =>
                {
                    var valSlot = context.GetSlot("value");
                    var detSlot = context.GetSlot("detail");
                    return reader => new Arguments(
                        reader.ReadRequired<string>(valSlot),
                        reader.ReadRequired<string>(detSlot));
                });

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
        {
            if (execution is not null)
            {
                await execution(arguments, context);
            }
        }

        public sealed record Arguments(string Value, string Detail);
    }

    private sealed class DuplicateCommand : RuntimeCommand<DuplicateCommand.Arguments>
    {
        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>.Create("save").Build(_ => _ => new Arguments());

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;

        public sealed record Arguments;
    }

    private sealed class ReservedWaitCommand : RuntimeCommand<ReservedWaitCommand.Arguments>
    {
        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>.Create("WAIT").Build(_ => _ => new Arguments());

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;

        public sealed record Arguments;
    }

    private sealed class FailingCommandModule : IRuntimeCommandModule
    {
        public void Register(CommandRegistrationContext context)
        {
            context.Commands.Add("probe", _ => new ProbeCommand(), "test");
            context.Commands.Add("save", _ => new DuplicateCommand(), "test");
        }
    }

    private readonly record struct CustomCommandValue(string Text);

    private sealed class CustomValueCommand : RuntimeCommand<CustomValueCommand.Arguments>
    {
        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>
                .Create("customValue")
                .Primary<CustomCommandValue>("value")
                .Build(static context =>
                {
                    var valSlot = context.GetSlot("value");
                    return reader => new Arguments(reader.ReadRequired<CustomCommandValue>(valSlot));
                });

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;

        public sealed record Arguments(CustomCommandValue Value);
    }

    private sealed class ConstantDefaultCommand : RuntimeCommand<ConstantDefaultCommand.Arguments>
    {
        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>.Create("constantDefault")
                .Optional<bool>("enabled", defaultText: "true")
                .Build(context =>
                {
                    var slot = context.GetSlot("enabled");
                    return reader => new Arguments(reader.ReadOptional<bool>(slot));
                });

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;

        public sealed record Arguments(CommandValue<bool> Enabled);
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
}
