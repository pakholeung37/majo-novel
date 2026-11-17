using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Script;
using Majo.Runtime.State;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateNestedDetourStateAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "YarnDetours";
        configuration.Presentation!.LettersPerSecond = 10000;
        configuration.Presentation.UseFadeEffect = false;
        var presenter = new BlockingBridgePresenter();
        var runtime = new MajoRuntimeBootstrap { Configuration = configuration };
        runtime.ConfigureDialoguePresenters = registry => registry.Register(presenter);
        AddChild(runtime);
        await runtime.InitializationCompletion;
        var services = runtime.Host!.Services;
        var dialogue = services.Get<IDialoguePlayer>();
        var script = services.Get<IScriptPlayback>();
        var backend = services.Get<IYarnDialogueBackend>();
        var state = services.Get<IStateManager>();
        var serializer = services.Get<IStateSerializer>();
        await dialogue.StartAsync();
        await WaitForConditionAsync(() => dialogue.CurrentLineId == "line:detour-inner" &&
            script.CanCaptureCheckpoint, 2);
        var checkpoint = script.CaptureCheckpoint();
        Assert(checkpoint.CallStack.Count == 2 && checkpoint.OperandStack.Count > 0,
            "Nested dynamic detour did not capture both return frames and its operand.");
        var snapshot = await state.CaptureGameAsync();
        var payload = snapshot.Records[BuiltInStateKeys.ScriptPlayback].Payload;
        Assert(payload.GetProperty("callStack").GetArrayLength() == 2 &&
                payload.GetProperty("operandStack").GetArrayLength() > 0,
            "Durable script state omitted its VM stacks.");
        var bytes = serializer.Serialize(state.CreateEnvelope(snapshot, StateScope.Game,
            MajoRuntimeMetadata.PackageVersion, configuration.State!.GameBuildId, configuration.State.StorageNamespace));
        var durable = serializer.Deserialize(bytes);
        var incompatible = new ScriptCheckpoint(checkpoint.FormatVersion, new string('0', 64),
            checkpoint.Spot, checkpoint.ResolvedSubstitutions, checkpoint.CallStack, checkpoint.OperandStack);
        var rejected = false;
        try { await backend.ValidateCheckpointAsync(incompatible); }
        catch (ScriptCheckpointException) { rejected = true; }
        Assert(rejected && dialogue.CurrentLineId == "line:detour-inner",
            "A nested checkpoint accepted a different program or disturbed live playback during preflight.");

        await dialogue.StopAsync();
        await services.Get<IGameSessionCoordinator>().LoadGameAsync(durable.Snapshot);
        await WaitForConditionAsync(() => presenter.Lines.Count == 2 && script.CanCaptureCheckpoint, 2);
        Assert(presenter.Lines[^1].Id == "line:detour-inner", "Loading did not replay the saved detour line.");
        var versions = services.Get<IYarnProjectVersionStore>();
        var running = versions.GetRunningVersion("YarnDetours")!;
        versions.StageForNextSession(running with { Generation = 100 });
        Assert(versions.GetActiveVersion("YarnDetours")!.Generation == running.Generation,
            "A next-session candidate replaced the running nested program.");
        dialogue.RequestNextLine();
        await WaitForConditionAsync(() => dialogue.CurrentLineId == "line:detour-outer" && script.CanCaptureCheckpoint, 2);
        dialogue.RequestNextLine();
        await WaitForConditionAsync(() => dialogue.CurrentLineId == "line:detour-parent" && script.CanCaptureCheckpoint, 2);
        Assert(string.Join(",", presenter.Lines.Skip(2).Select(line => line.Id)) == "line:detour-outer,line:detour-parent" &&
                presenter.Lines[^1].Text.Text.Contains('1'),
            "Loaded detour lost its return order or replayed the caller's earlier commands.");
        dialogue.RequestNextLine();
        await dialogue.Completion;
        Assert(versions.GetRunningVersion("YarnDetours") is null &&
                versions.GetActiveVersion("YarnDetours")!.Generation == 100,
            "A deferred candidate was not activated after dialogue completion.");
        await runtime.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
