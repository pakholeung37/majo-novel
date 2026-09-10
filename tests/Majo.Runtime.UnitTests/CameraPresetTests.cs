using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class CameraPresetTests
{
    [Fact]
    public async Task InheritedFullPresetRestoresAndAppliesAtomicallyWithoutEnvironmentCommands()
    {
        const string source = "title: Court\n---\n<<camera preset define ema extends:base center:35,71 parallaxAnchor:35,46>>\n<<camera preset define base zoom:0.7826087 dolly:0.7826087 parallax:strong parallaxLimit:2,50>>\n===\n";
        var snapshot = new YarnSourceSnapshot("test", "test.yarnproject", 1, "en",
            [new YarnSourceFileSnapshot("main.yarn", "main.yarn", source, "hash")], "fingerprint");
        var module = Assert.Single(NodeDirectionCompiler.Compile(snapshot));
        Assert.Empty(module.Diagnostics);
        var scopes = new DirectionScopeResolver();
        scopes.ApplyModule(Assert.Single(YarnProjectDirectionMetadata.Deserialize(YarnProjectDirectionMetadata.Serialize([module]))));
        var nodes = new DelegateDialogueNodeAccessor(() => "Court");
        var define = TestCatalogBuilder.CreateBound(new CameraPresetDefineCommand(scopes, nodes));
        await define.ExecuteAsync(new CommandInput("camera preset define",
            ["ema", "extends:base", "center:35,71", "parallaxAnchor:35,46"]), new("replay-definition"));
        var beats = new FakeDirectionBeatRuntime();
        var command = TestCatalogBuilder.CreateBound(new CameraMoveCommand(beats, scopes, nodes, new DirectionCommandPolicy(beats)));
        await command.ExecuteAsync(new CommandInput("camera move", ["preset:ema", "duration:8"]), new("move"));
        var patch = Assert.IsType<ViewPosePatch>(beats.LastSubmitted);
        Assert.Equal(new DirectionPoint(35, 71), patch.Center.Value);
        Assert.Equal(.7826087f, patch.Dolly.Value);
        Assert.Equal(new ViewParallaxState(1, new(35, 46), new(2, 50)), patch.Parallax);
        var plan = new Majo.Runtime.Direction.Execution.DirectionPlanner().PlanDomain(DirectionState.CreateDefault(), [patch]);
        Assert.Equal(patch.Parallax, plan.TargetState.Views[ViewId.Main].Parallax);
        Assert.Equal(2, plan.Transitions.Count);
        Assert.All(plan.Transitions, transition => Assert.Equal(8, transition.Spec.DurationSeconds));
        await command.ExecuteAsync(new CommandInput("camera move", ["zoom:1", "duration:1"]), new("partial"));
        Assert.Null(Assert.IsType<ViewPosePatch>(beats.LastSubmitted).Parallax);
    }

    [Theory]
    [InlineData("<<camera preset define a extends:b>>\n<<camera preset define b extends:a>>")]
    [InlineData("<<camera preset define a extends:missing>>")]
    public void InvalidInheritanceIsRejectedAtNodeEntry(string definitions)
    {
        var snapshot = new YarnSourceSnapshot("test", "test.yarnproject", 1, "en",
            [new YarnSourceFileSnapshot("main.yarn", "main.yarn", $"title: Court\n---\n{definitions}\n===\n", "hash")], "fingerprint");
        var module = Assert.Single(NodeDirectionCompiler.Compile(snapshot));
        Assert.Throws<DirectionReferenceException>(() => new DirectionScopeResolver().ApplyModule(module));
    }

    [Fact]
    public async Task PresetDefinitionsArePureAndRestoredWithoutExecutingThePrologue()
    {
        const string source = "title: Court\n---\n<<mark leia at:250,60>>\n<<camera preset define close center:leia zoom:2>>\n===\n";
        var snapshot = new YarnSourceSnapshot("test", "test.yarnproject", 1, "en",
            [new YarnSourceFileSnapshot("main.yarn", "main.yarn", source, "hash")], "fingerprint");
        var module = Assert.Single(NodeDirectionCompiler.Compile(snapshot));
        Assert.Empty(module.Diagnostics);
        var modules = YarnProjectDirectionMetadata.Deserialize(YarnProjectDirectionMetadata.Serialize([module]));
        var scopes = new DirectionScopeResolver();
        scopes.ApplyModule(Assert.Single(modules));
        var beats = new FakeDirectionBeatRuntime();
        var nodes = new DelegateDialogueNodeAccessor(() => "Court");
        var define = TestCatalogBuilder.CreateBound(new CameraPresetDefineCommand(scopes, nodes));
        await define.ExecuteAsync(new CommandInput("camera preset define", ["close", "center:leia", "zoom:2"]), new CommandExecutionContext("define"));
        Assert.Null(beats.LastSubmitted);
        var policy = new DirectionCommandPolicy(beats);
        var camera = TestCatalogBuilder.CreateBound(new CameraCommand(beats, scopes, nodes, policy));
        await camera.ExecuteAsync(new CommandInput("camera", ["preset:close"]), new CommandExecutionContext("cut"));
        var cut = Assert.IsType<ViewPosePatch>(beats.LastSubmitted);
        Assert.Equal(new DirectionPoint(250, 60), cut.Center.Value);
        Assert.Equal(2, cut.Zoom.Value);
        Assert.Equal(0, cut.RotationDegrees.Value);
        Assert.True(cut.Dolly.HasValue);
        Assert.Equal(0, cut.Dolly.Value);
        Assert.Equal(ViewParallaxState.Off, cut.Parallax);
        var move = TestCatalogBuilder.CreateBound(new CameraMoveCommand(beats, scopes, nodes, policy));
        await move.ExecuteAsync(new CommandInput("camera move", ["preset:close", "zoom:3", "duration:2"]), new CommandExecutionContext("move"));
        var motion = Assert.IsType<ViewPosePatch>(beats.LastSubmitted);
        Assert.Equal(new DirectionPoint(250, 60), motion.Center.Value);
        Assert.Equal(3, motion.Zoom.Value);
        Assert.Equal(2, motion.Transition.DurationSeconds);
        Assert.Equal(0, motion.Dolly.Value);
        Assert.Equal(ViewParallaxState.Off, motion.Parallax);
    }

    [Fact]
    public async Task DollyPresetIsAbsoluteAndExplicitArgumentsOverrideCompositionPreset()
    {
        var scopes = new DirectionScopeResolver();
        scopes.EnterNode("Court");
        var beats = new FakeDirectionBeatRuntime();
        var nodes = new DelegateDialogueNodeAccessor(() => "Court");
        var policy = new DirectionCommandPolicy(beats);
        var define = TestCatalogBuilder.CreateBound(new CameraPresetDefineCommand(scopes, nodes));
        await define.ExecuteAsync(new CommandInput("camera preset define", ["pair", "zoom:1.1", "dolly:back"]), new("define"));
        var move = TestCatalogBuilder.CreateBound(new CameraMoveCommand(beats, scopes, nodes, policy));

        await move.ExecuteAsync(new CommandInput("camera move", ["preset:pair", "dolly:near", "duration:1"]), new("move"));
        var patch = Assert.IsType<ViewPosePatch>(beats.LastSubmitted);
        Assert.Equal(0.15f, patch.Dolly.Value);
        Assert.Equal(1.1f, patch.Zoom.Value);
    }
}
