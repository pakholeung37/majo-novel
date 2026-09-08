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
        var move = TestCatalogBuilder.CreateBound(new CameraMoveCommand(beats, scopes, nodes, policy));
        await move.ExecuteAsync(new CommandInput("camera move", ["preset:close", "zoom:3", "duration:2"]), new CommandExecutionContext("move"));
        var motion = Assert.IsType<ViewPosePatch>(beats.LastSubmitted);
        Assert.Equal(new DirectionPoint(250, 60), motion.Center.Value);
        Assert.Equal(3, motion.Zoom.Value);
        Assert.Equal(2, motion.Transition.DurationSeconds);
    }
}
