using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue.Yarn.Bridges;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.YarnBridge;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateYarnBridgeIsolationAsync()
    {
        Assert(typeof(DialogueRunner).Assembly.GetType("YarnSpinnerGodot.YarnProjectEditorUtility")?
                    .GetProperty("CompilationPipeline") is null &&
                typeof(LinePresenter).GetMethod("ResetVisibility") is null,
            "Bridge regressions must run against the unmodified Yarn Godot API.");
        const string source = "title: Late\n---\n<<bridge_late>>\nOld. #line:bridge-old\n===\n" +
            "title: Hold\n---\nNew. #line:bridge-new\nNext. #line:bridge-next\n===\n";
        var snapshot = new YarnSourceSnapshot("bridge", "bridge.yarnproject", 3, "en",
            new[] { new YarnSourceFileSnapshot("bridge.yarn", "bridge.yarn", source, "hash") }, "hash");
        var compilation = await new YarnRuntimeCompiler().CompileAsync(snapshot);
        var version = YarnProjectMaterializer.Materialize(1, compilation);
        var presenter = new BridgeIsolationPresenter();
        var runner = new DialogueRunner { autoStart = false };
        runner.SetProject(version.Project);
        runner.AddChild(presenter);
        runner.dialoguePresenters = new Godot.Collections.Array<Node> { presenter };
        runner.AddCommandHandler(YarnProgramBridge.NodeEntryCommand, (Action)(() => { }));
        var commandEntered = new TaskCompletionSource();
        var commandRelease = new TaskCompletionSource();
        runner.AddCommandHandler("bridge_late", (Func<Task>)(async () =>
        {
            commandEntered.TrySetResult();
            // Deliberately ignore cancellation, as third-party Yarn handlers can do.
            await commandRelease.Task;
        }));
        using var bridge = new YarnDialogueRunnerBridge(runner);
        AddChild(runner);
        try
        {
            await runner.StartDialogue("Late");
            await WaitForConditionAsync(() => commandEntered.Task.IsCompleted, 2);
            await runner.Stop();
            await runner.StartDialogue("Hold");
            await WaitForConditionAsync(() => presenter.Lines.Count == 1, 2);
            commandRelease.TrySetResult();
            for (var frame = 0; frame < 4; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert(runner.IsDialogueRunning && presenter.Lines.Count == 1 &&
                    presenter.Lines[0] == "line:bridge-new",
                "A late command advanced the replacement dialogue through the Runner bridge.");

            presenter.DelayDismissal = true;
            Task stopping = runner.Stop();
            await WaitForConditionAsync(() => presenter.DismissalStarted, 2);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert(!stopping.IsCompleted, "Runner Stop completed before its cancelled line retired.");
            presenter.DismissalRelease.TrySetResult();
            await stopping;
            await runner.StartDialogue("Hold");
            await WaitForConditionAsync(() => presenter.Lines.Count == 2, 2);
            Assert(!presenter.OverlappedDismissal, "Replacement dialogue overlapped the old line dismissal.");
            await runner.Stop();
        }
        finally
        {
            commandRelease.TrySetResult();
            presenter.DismissalRelease.TrySetResult();
            bridge.Dispose();
            runner.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            version.Project.baseLocalization.Dispose();
            version.Project.Dispose();
        }
    }

    private sealed partial class BridgeIsolationPresenter : Node, DialoguePresenterBase
    {
        public List<IActionMarkupHandler> ActionMarkupHandlers { get; } = new();
        public List<string> Lines { get; } = new();
        public bool DelayDismissal { get; set; }
        public bool DismissalStarted { get; private set; }
        public bool OverlappedDismissal { get; private set; }
        public TaskCompletionSource DismissalRelease { get; } = new();

        public async YarnTask RunLineAsync(LocalizedLine line, LineCancellationToken token)
        {
            OverlappedDismissal |= DismissalStarted && !DismissalRelease.Task.IsCompleted;
            Lines.Add(line.TextID);
            await YarnTask.WaitUntilCanceled(token.NextLineToken).SuppressCancellationThrow();
            if (DelayDismissal)
            {
                DelayDismissal = false;
                DismissalStarted = true;
                await DismissalRelease.Task;
            }
        }
    }
}
