using Majo.Runtime.Dialogue.Commands;
using Xunit;

namespace Majo.Runtime.Commands.UnitTests;

public sealed class CommandResourceContractTests
{
    [Fact]
    public async Task GeneratedResourceProviderUsesBoundDefaultsAndExplicitValuesWithoutExecuting()
    {
        var plugin = new ResourceProbeCommand();
        var registry = new RuntimeCommandRegistry();
        registry.Add("plugin show", _ => plugin, "plugin");
        var catalog = registry.BuildCatalog();

        Assert.Equal([new CommandResourceReference("sfx", "clip-42-calm")],
            catalog.DescribeResources(["PLUGIN", "SHOW", "42"]));
        Assert.Equal([new CommandResourceReference("sfx", "clip-43-Angry")],
            catalog.DescribeResources(["plugin", "show", "STYLE:Angry", "43"]));
        Assert.Equal(0, plugin.Executions);

        var command = catalog.Resolve(["PLUGIN", "SHOW", "STYLE:Angry", "43"], out var input);
        Assert.NotNull(command);
        Assert.Equal("plugin show", input!.CommandName);
        await command.ExecuteAsync(input, new CommandExecutionContext("plugin-execution"));
        Assert.Equal(1, plugin.Executions);
        Assert.Equal(43, plugin.LastArguments!.Id);
        Assert.Equal("Angry", plugin.LastArguments.Style.Value);
        Assert.Equal(CommandValueOrigin.Explicit, plugin.LastArguments.Style.Origin);
    }

    [Theory]
    [InlineData("invalid", "style:calm")]
    [InlineData("42", "unknown:calm")]
    public void InvalidArgumentsFailBeforeResourceDescription(string id, string option)
    {
        var plugin = new ResourceProbeCommand();
        var registry = new RuntimeCommandRegistry();
        registry.Add("plugin show", _ => plugin, "plugin");
        var catalog = registry.BuildCatalog();

        var failure = Assert.Throws<RuntimeCommandException>(() =>
            catalog.DescribeResources(["plugin", "show", id, option]));

        Assert.Equal(CommandFailurePhase.Binding, failure.FailurePhase);
        Assert.Equal(0, plugin.Descriptions);
        Assert.Equal(0, plugin.Executions);
    }

    [Theory]
    [InlineData("plugin", "show", "plugin show", "sfx", "clip-42-calm")]
    [InlineData("PLUGIN", "SHOW", "plugin show", "sfx", "clip-42-calm")]
    [InlineData("plugin", "Showcase", "plugin", "backdrop", "Showcase")]
    public void CatalogSelectsLongestCompletePathAndPreservesResourceIdCase(
        string root, string child, string expectedCommand, string kind, string id)
    {
        var registry = new RuntimeCommandRegistry();
        registry.Add("plugin", _ => new RootResourceProbeCommand(), "plugin");
        registry.Add("plugin show", _ => new ResourceProbeCommand(), "plugin");
        var catalog = registry.BuildCatalog();
        string[] tokens = expectedCommand == "plugin" ? [root, child] : [root, child, "42"];

        Assert.Equal(expectedCommand, catalog.Resolve(tokens, out _)!.Name);
        Assert.Equal([new CommandResourceReference(kind, id)], catalog.DescribeResources(tokens));
        Assert.Null(catalog.Resolve(["unknown", "show"], out var unknownInput));
        Assert.Null(unknownInput);
        Assert.Empty(catalog.DescribeResources(["unknown", "show"]));
    }
}

[Command("plugin show")]
public sealed partial class ResourceProbeCommand : RuntimeCommand<ResourceProbeCommand.Arguments>,
    ICommandResourceProvider<ResourceProbeCommand.Arguments>
{
    public sealed record Arguments(
        [Primary] int Id,
        [Option(DefaultText = "calm")] CommandValue<string> Style);

    public int Executions { get; private set; }
    public int Descriptions { get; private set; }
    public Arguments? LastArguments { get; private set; }

    public IReadOnlyList<CommandResourceReference> DescribeResources(Arguments arguments)
    {
        Descriptions++;
        return [new("sfx", $"clip-{arguments.Id}-{arguments.Style.Value}")];
    }

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        Executions++;
        LastArguments = arguments;
        return ValueTask.CompletedTask;
    }
}

[Command("plugin")]
public sealed partial class RootResourceProbeCommand : RuntimeCommand<RootResourceProbeCommand.Arguments>,
    ICommandResourceProvider<RootResourceProbeCommand.Arguments>
{
    public sealed record Arguments([Primary] string Id);

    public IReadOnlyList<CommandResourceReference> DescribeResources(Arguments arguments) =>
        [new("backdrop", arguments.Id)];

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context) =>
        ValueTask.CompletedTask;
}
