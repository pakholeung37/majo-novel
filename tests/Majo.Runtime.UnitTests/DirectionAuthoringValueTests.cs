using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Resources;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionAuthoringValueTests
{
    [Theory]
    [InlineData("0,100", 0, 100)]
    [InlineData(" 30.5, 0 ", 30.5, 0)]
    [InlineData("101,0", 101, 0)]
    [InlineData("-1,0", -1, 0)]
    public void CoordinatesRoundTrip(string text, float x, float y)
    {
        var converter = PositionRefConverter.Instance;
        var value = converter.Parse(text);
        Assert.Equal(new PositionRef.Literal(new DirectionPoint(x, y)), value);
        Assert.Equal(value, converter.Parse(converter.Format(value)));
    }

    [Theory]
    [InlineData("30,abc")]
    [InlineData("NaN,0")]
    [InlineData("0,Infinity")]
    [InlineData("1,2,3")]
    public void CompilerAndConverterRejectTheSameInvalidCoordinates(string text)
    {
        Assert.Throws<CommandValueFormatException>(() => PositionRefConverter.Instance.Parse(text));
        var snapshot = new YarnSourceSnapshot("test", "test.yarnproject", 1, "en",
            [new YarnSourceFileSnapshot("main.yarn", "main.yarn",
                $"title: Opening\n---\n<<mark focus at:{text}>>\n===\n", "hash")], "fingerprint");
        var module = Assert.Single(NodeDirectionCompiler.Compile(snapshot));
        var diagnostic = Assert.Single(module.Diagnostics);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal(3, diagnostic.StartLine);
        Assert.Empty(module.MarkDefinitions);
    }

    [Fact]
    public void CompilerDefersDynamicCoordinatesToRuntimeBinding()
    {
        var snapshot = new YarnSourceSnapshot("test", "test.yarnproject", 1, "en",
            [new YarnSourceFileSnapshot("main.yarn", "main.yarn",
                "title: Opening\n---\n<<mark focus at:{$x},0>>\n===\n", "hash")], "fingerprint");
        var module = Assert.Single(NodeDirectionCompiler.Compile(snapshot));
        Assert.Empty(module.Diagnostics);
        Assert.Empty(module.MarkDefinitions);
        Assert.Contains("focus", module.Symbols.Marks);
    }

    [Fact]
    public async Task BoundPositionUsesTheExecutingNodeAndOmittedPositionDoesNotPatch()
    {
        var scopes = new DirectionScopeResolver(new Dictionary<string, DirectionPoint>
        {
            ["left"] = new(20, 0),
        });
        var node = "first";
        var beats = new FakeDirectionBeatRuntime();
        var command = new CharacterCommand(beats, scopes,
            new DelegateDialogueNodeAccessor(() => node), new DirectionCommandPolicy(beats));
        var bound = TestCatalogBuilder.CreateBound(command);
        var input = new CommandInput("character", ["Ema", "at:left"]);
        scopes.EnterNode(node);
        scopes.DefineMark(node, "left", new DirectionPoint(35, 0));
        await bound.ExecuteAsync(input, new CommandExecutionContext("first"));
        Assert.Equal(new DirectionPoint(35, 0), Assert.IsType<ActorPatch>(beats.LastSubmitted).Position.Value);

        node = "second";
        scopes.EnterNode(node);
        await bound.ExecuteAsync(input, new CommandExecutionContext("second"));
        Assert.Equal(new DirectionPoint(20, 0), Assert.IsType<ActorPatch>(beats.LastSubmitted).Position.Value);
        await bound.ExecuteAsync(new CommandInput("character", ["Ema", "appearance:happy"]),
            new CommandExecutionContext("appearance"));
        Assert.False(Assert.IsType<ActorPatch>(beats.LastSubmitted).Position.HasValue);

        await scopes.ResetAsync(default);
        Assert.Equal(new DirectionPoint(20, 0), scopes.ResolveMark(node, "LEFT"));
        Assert.Throws<DirectionReferenceException>(() => scopes.ResolveMark(node, "missing"));
    }

    [Fact]
    public async Task MarkCanUseProjectDefaultAndBeExecutedAgain()
    {
        var scopes = new DirectionScopeResolver();
        var bound = TestCatalogBuilder.CreateBound(new DirectionMarkCommand(scopes,
            new DelegateDialogueNodeAccessor(() => "Opening")));
        var input = new CommandInput("mark", ["witness", "at:left"]);
        await bound.ExecuteAsync(input, new CommandExecutionContext("mark"));
        await bound.ExecuteAsync(input, new CommandExecutionContext("mark-again"));
        Assert.Equal(scopes.ResolveMark("Opening", "left"), scopes.ResolveMark("Opening", "witness"));
    }

    [Theory]
    [InlineData("room", "backgrounds", "backgrounds/room")]
    [InlineData("backgrounds/room", "backgrounds", "backgrounds/room")]
    [InlineData(" /art\\backgrounds/room/ ", "art/backgrounds", "art/backgrounds/room")]
    [InlineData("room", "custom/bg", "custom/bg/room")]
    public async Task BackdropCommandUsesTheSharedResourceKeyRule(string id, string root, string expected)
    {
        var beats = new FakeDirectionBeatRuntime();
        var command = new BackdropCommand(beats, new DirectionScopeResolver(),
            new DelegateDialogueNodeAccessor(() => "Opening"), new DirectionCommandPolicy(beats), root);
        var bound = TestCatalogBuilder.CreateBound(command);
        await bound.ExecuteAsync(new CommandInput("backdrop", [id]), new CommandExecutionContext("backdrop"));
        var patch = Assert.IsType<BackdropPatch>(beats.LastSubmitted);
        Assert.Equal(new ResourceKey(expected), new BackdropId(id).ToResourceKey(root));
        Assert.Equal(new ResourceKey(expected), patch.Backdrop.Resource);
    }
}
