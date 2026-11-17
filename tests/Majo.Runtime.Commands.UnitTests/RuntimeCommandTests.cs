#nullable enable

using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Commands.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.Commands.UnitTests;

public sealed class RuntimeCommandTests
{

    [Fact]
    public void RegistryRejectsReservedDuplicateAndLateRegistrations()
    {
        var commands = new RuntimeCommandRegistry();
        commands.RegisterModule(new SampleCommandModule());

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
        commands.RegisterModule(new SampleCommandModule());

        Assert.Throws<RuntimeCommandException>(() =>
            commands.RegisterModule(new FailingCommandModule()));

        Assert.DoesNotContain(
            commands.Registrations,
            registration => registration.Name == "probe");
        Assert.Equal(2, commands.Registrations.Count);
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

    [Fact]
    public void Manifest_GeneratesAndMergesCleanly()
    {
        var commands = new RuntimeCommandRegistry();
        commands.RegisterModule(new SampleCommandModule());
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
    private sealed class SampleCommandModule : IRuntimeCommandModule
    {
        public void Register(CommandRegistrationContext context)
        {
            foreach (var name in new[] { "save", "autoSave" })
                context.Commands.Add(name, _ => new EmptyCommand(name), "sample");
        }
    }

    private sealed class EmptyCommand : RuntimeCommand<EmptyCommand.Arguments>
    {
        public EmptyCommand(string name) => Definition =
            CommandDefinition<Arguments>.Create(name).Build(_ => _ => new Arguments());

        public override CommandDefinition<Arguments> Definition { get; }

        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;

        public sealed record Arguments;
    }
}
