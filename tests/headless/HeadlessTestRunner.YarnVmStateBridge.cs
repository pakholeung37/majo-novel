using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Yarn;
using Majo.YarnBridge;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateYarnVmStateBridgeAsync()
    {
        var presenter = new BlockingBridgePresenter();
        var configuration = CreateHeadlessConfiguration();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "YarnVmBridge";
        configuration.Presentation!.Enabled = false;

        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "YarnVmBridgeRuntime",
            Configuration = configuration,
        };
        bootstrap.ConfigureDialoguePresenters += presenters =>
            presenters.Register(presenter);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var dialogue = bootstrap.Host!.Services.Get<IDialoguePlayer>();
        var backend = bootstrap.Host.Services.Get<IYarnDialogueBackend>();
        var runner = backend.Runner;
        var bridge = new YarnVmStateBridge();

        await dialogue.StartAsync();
        await WaitForConditionAsync(() => presenter.Lines.Count == 1, 2);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var snapshot = bridge.CaptureAtLine(
            runner.Dialogue,
            "line:bridge-headless-first");
        Assert(snapshot.ExecutionState == "WaitingForContinue" &&
                snapshot.OperandStackDepth == 0 &&
                snapshot.CallStackDepth == 0 &&
                snapshot.PendingOptionCount == 0,
            "Yarn VM Bridge captured an unsafe Godot DialogueRunner boundary.");

        await dialogue.StopAsync();
        var injectionCount = 0;
        var injectReplay = Callable.From(() =>
        {
            var project = runner.YarnProject ?? throw new InvalidOperationException(
                "DialogueRunner had no YarnProject during replay injection.");
            bridge.InjectReplayAfterNodeSelected(
                runner.Dialogue,
                project.Program,
                snapshot);
            injectionCount++;
        });
        runner.Connect(DialogueRunner.SignalName.onDialogueStart, injectReplay);

        await dialogue.StartAsync();
        var replayCompletion = dialogue.Completion;
        await WaitForConditionAsync(() => presenter.Lines.Count == 2, 2);
        Assert(injectionCount == 1 &&
                presenter.Lines[0].Id == "line:bridge-headless-first" &&
                presenter.Lines[1].Id == "line:bridge-headless-first",
            "DialogueRunner did not replay the captured line after VM state injection.");

        dialogue.RequestNextLine();
        await WaitForConditionAsync(() => presenter.Lines.Count == 3, 2);
        Assert(presenter.Lines[2].Id == "line:bridge-headless-second",
            "DialogueRunner did not continue to the line after the restored snapshot.");

        dialogue.RequestNextLine();
        await replayCompletion;
        Assert(dialogue.State == DialogueSessionState.Idle,
            "Restored DialogueRunner session did not complete normally.");

        runner.Disconnect(DialogueRunner.SignalName.onDialogueStart, injectReplay);
        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(bootstrap) && !IsInstanceValid(runner),
            "Yarn VM Bridge headless validation leaked Runtime nodes.");
    }

    private sealed class BlockingBridgePresenter : IDialoguePresenter
    {
        public List<DialogueLine> Lines { get; } = new();

        public async Task PresentLineAsync(
            DialogueLine line,
            DialoguePresentationContext context)
        {
            Lines.Add(line);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.NextLineToken);
            }
            catch (OperationCanceledException)
                when (context.NextLineToken.IsCancellationRequested)
            {
            }
        }
    }
}
