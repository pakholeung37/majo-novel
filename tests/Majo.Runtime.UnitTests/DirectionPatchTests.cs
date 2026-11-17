#nullable enable

using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Resources;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionPatchTests
{
    [Fact]
    public void BeatBeginAllowsAnonymousBeats()
    {
        var command = new BeatBeginCommand(
            new FakeDirectionBeatRuntime(),
            new DelegateDialogueNodeAccessor(() => "node"));
        var bound = TestCatalogBuilder.CreateBound(command);
        var arguments = bound.Bind(new CommandInput("beat begin", Array.Empty<string>()));

        Assert.False(arguments.Id.WasProvided);
    }

}
