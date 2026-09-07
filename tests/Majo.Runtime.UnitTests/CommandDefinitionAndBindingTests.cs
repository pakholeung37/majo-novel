#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class CommandDefinitionAndBindingTests
{
    private sealed record TestSampleArguments(
        string Target,
        CommandValue<int> Count,
        CommandValue<double> Speed);

    private sealed class TestSampleCommand : RuntimeCommand<TestSampleArguments>
    {
        private readonly CommandDefinition<TestSampleArguments> definition;

        public TestSampleCommand(CommandDefinition<TestSampleArguments> definition)
        {
            this.definition = definition;
        }

        public override CommandDefinition<TestSampleArguments> Definition => definition;

        public TestSampleArguments? LastExecutedArguments { get; private set; }

        protected override ValueTask ExecuteAsync(
            TestSampleArguments arguments,
            CommandExecutionContext context)
        {
            LastExecutedArguments = arguments;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void SameDefinitionCanBeUsedInTwoCatalogsWithDifferentConverters()
    {
        var definition = CommandDefinition<TestSampleArguments>.Create("sample")
            .Primary<string>("target")
            .Optional<int>("count")
            .Optional<double>("speed")
            .Build(context =>
            {
                var targetSlot = context.GetSlot("target");
                var countSlot = context.GetSlot("count");
                var speedSlot = context.GetSlot("speed");
                return reader => new TestSampleArguments(
                    reader.ReadRequired<string>(targetSlot),
                    reader.ReadOptional<int>(countSlot),
                    reader.ReadOptional<double>(speedSlot));
            });

        // Catalog 1: standard int converter
        var catalog1 = new TestCatalogBuilder()
            .Add("sample", _ => new TestSampleCommand(definition))
            .Build();

        // Catalog 2: custom int converter that multiplies by 10
        var catalog2 = new TestCatalogBuilder()
            .ConfigureConverters(converters =>
            {
                converters.ReplaceDefault<int>(
                    _ => new MultiplyingIntConverter(10),
                    "test",
                    "core");
            })
            .Add("sample", _ => new TestSampleCommand(definition))
            .Build();

        var bound1 = catalog1.Get<TestSampleArguments>("sample");
        var bound2 = catalog2.Get<TestSampleArguments>("sample");

        var input = new CommandInput("sample", new[] { "player", "count:5" });
        var args1 = bound1.Bind(input);
        var args2 = bound2.Bind(input);

        Assert.Equal(5, args1.Count.Value);
        Assert.Equal(50, args2.Count.Value);
    }

    [Fact]
    public void BuildCatalogFailureLeavesNoPartialState()
    {
        var def1 = CommandDefinition<WaitArguments>.Create("cmd1")
            .Optional<bool>("wait")
            .Build(context =>
            {
                var slot = context.GetSlot("wait");
                return reader => new WaitArguments(reader.ReadOptional<bool>(slot));
            });

        var def2 = CommandDefinition<WaitArguments>.Create("cmd2")
            .Optional<CustomNonConvertibleType>("bad")
            .Build(context =>
            {
                var slot = context.GetSlot("bad");
                return reader => new WaitArguments(CommandValue<bool>.Unspecified());
            });

        var cmd1 = new DefinitionHoldingCommand(def1);
        var cmd2 = new DefinitionHoldingCommand(def2);

        var registry = new RuntimeCommandRegistry();
        registry.Add("cmd1", _ => cmd1, "test");
        registry.Add("cmd2", _ => cmd2, "test");

        var ex = Assert.Throws<RuntimeCommandException>(() => registry.BuildCatalog());
        Assert.Equal(CommandFailurePhase.Schema, ex.FailurePhase);
        Assert.False(registry.IsSealed);
    }

    [Fact]
    public void ParameterInGroupIsParsedOnlyOnce()
    {
        var spyConverter = new CountingIntConverter();

        var groupParams = new[]
        {
            new CommandParameterDeclaration(
                Name: "count",
                MemberName: "Count",
                Order: 1,
                Primary: false,
                Required: false,
                ValueType: typeof(int),
                IsCommandValue: true,
                ValueHint: "num")
        };

        var groupDecl = new CommandGroupDeclaration(
            Name: "metrics",
            MemberName: "Metrics",
            Order: 1,
            GroupType: typeof(MetricsGroup),
            Parameters: groupParams);

        var definition = CommandDefinition<GroupHoldingArguments>.Create("track")
            .Group("metrics", "Metrics", typeof(MetricsGroup), groupParameters: groupParams)
            .Build(context =>
            {
                var slot = context.GetSlot("count");
                return reader => new GroupHoldingArguments(
                    new MetricsGroup(reader.ReadOptional<int>(slot)));
            });

        var catalog = new TestCatalogBuilder()
            .ConfigureConverters(converters =>
            {
                converters.ReplaceDefault<int>(
                    _ => spyConverter,
                    "spy",
                    "core");
            })
            .Add("track", _ => new GroupHoldingCommand(definition))
            .Build();

        var bound = catalog.Get<GroupHoldingArguments>("track");
        var result = bound.Bind(new CommandInput("track", new[] { "count:42" }));

        Assert.Equal(42, result.Metrics.Count.Value);
        Assert.Equal(1, spyConverter.ParseCallCount);
    }

    [Fact]
    public void InvalidContractDefaultFailsAtCatalogBuildTime()
    {
        var definition = CommandDefinition<TestSampleArguments>.Create("invalid_default")
            .Primary<string>("target")
            .Optional<int>("count", defaultText: "not_a_number")
            .Build(context => reader => new TestSampleArguments("t", CommandValue<int>.Unspecified(), CommandValue<double>.Unspecified()));

        var builder = new TestCatalogBuilder()
            .Add("invalid_default", _ => new TestSampleCommand(definition));

        var ex = Assert.Throws<RuntimeCommandException>(() => builder.Build());
        Assert.Equal(CommandFailurePhase.Schema, ex.FailurePhase);
        Assert.Contains("not_a_number", ex.Message);
    }

    [Fact]
    public void ContractDefaultViolatingMinimumFailsAtCatalogBuildTime()
    {
        var definition = CommandDefinition<TestSampleArguments>.Create("minimum_default")
            .Primary<string>("target")
            .Optional<int>("count", defaultText: "-5", minimum: 0)
            .Build(context => reader => new TestSampleArguments("t", CommandValue<int>.Unspecified(), CommandValue<double>.Unspecified()));

        var builder = new TestCatalogBuilder()
            .Add("minimum_default", _ => new TestSampleCommand(definition));

        var ex = Assert.Throws<RuntimeCommandException>(() => builder.Build());
        Assert.Equal(CommandFailurePhase.Schema, ex.FailurePhase);
        Assert.Contains("must be at least 0", ex.Message);
    }

    private sealed class MultiplyingIntConverter : ICommandValueConverter<int>
    {
        private readonly int factor;

        public MultiplyingIntConverter(int factor) => this.factor = factor;

        public int Parse(string text) => int.Parse(text) * factor;
    }

    private sealed class CountingIntConverter : ICommandValueConverter<int>
    {
        public int ParseCallCount { get; private set; }

        public int Parse(string text)
        {
            ParseCallCount++;
            return int.Parse(text);
        }
    }

    private sealed class CustomNonConvertibleType
    {
    }

    private sealed class DefinitionHoldingCommand : RuntimeCommand<WaitArguments>
    {
        public DefinitionHoldingCommand(CommandDefinition<WaitArguments> definition) => Definition = definition;

        public override CommandDefinition<WaitArguments> Definition { get; }

        protected override ValueTask ExecuteAsync(WaitArguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;
    }

    private sealed record MetricsGroup(CommandValue<int> Count);

    private sealed record GroupHoldingArguments(MetricsGroup Metrics);

    private sealed class GroupHoldingCommand : RuntimeCommand<GroupHoldingArguments>
    {
        private readonly CommandDefinition<GroupHoldingArguments> definition;

        public GroupHoldingCommand(CommandDefinition<GroupHoldingArguments> definition)
        {
            this.definition = definition;
        }

        public override CommandDefinition<GroupHoldingArguments> Definition => definition;

        protected override ValueTask ExecuteAsync(GroupHoldingArguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;
    }
}
