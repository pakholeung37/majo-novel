#nullable enable

using Majo.Runtime.Dialogue.Commands;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class GeneratedCommandIntegrationTests
{
    [Fact]
    public void GeneratedDefinitionBindsThroughCatalogWithNestedGroup()
    {
        var registry = new RuntimeCommandRegistry();
        registry.Add("generated probe", _ => new GeneratedProbeCommand(), "test");

        var command = registry.BuildCatalog().Get<GeneratedProbeCommand.Arguments>("generated probe");
        var arguments = command.Bind(new CommandInput("generated probe", new[] { "alpha", "count:7", "wait:true" }));

        Assert.Equal("alpha", arguments.Target);
        Assert.Equal(7, arguments.Options.Count.Value);
        Assert.True(arguments.Options.Wait.Value);
        Assert.Equal(CommandValueOrigin.Explicit, arguments.Options.Wait.Origin);
    }
}

[Command("generated probe")]
public sealed partial class GeneratedProbeCommand : RuntimeCommand<GeneratedProbeCommand.Arguments>
{
    public sealed record Options(
        [Option] CommandValue<int> Count,
        [Option] CommandValue<bool> Wait);

    public sealed record Arguments(
        [Primary] string Target,
        [Group] Options Options);

    protected override ValueTask ExecuteAsync(
        Arguments arguments,
        CommandExecutionContext context) =>
        System.Threading.Tasks.ValueTask.CompletedTask;
}
