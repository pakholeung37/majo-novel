using System;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateSceneEntryBarrierAsync()
    {
        foreach (var body in new[]
        {
            "<<set $entered = true>>\nHello #line:entry_line",
            "<<jump Next>>",
            "Hello #line:entry_line",
            "-> First #line:entry_option\n    Hello #line:entry_line",
        })
        {
            var source = $"title: Start\nscene: first\n---\n{body}\n===\ntitle: Next\nscene: second\n---\nNext #line:entry_next\n===";
            var snapshot = new YarnSourceSnapshot("entry", "entry.yarnproject", 3, "en",
                new[] { new YarnSourceFileSnapshot("entry.yarn", "entry.yarn", source, "hash") }, "hash");
            var compilation = await new YarnRuntimeCompiler().CompileAsync(snapshot);
            Assert(!compilation.HasErrors, "Scene barrier fixture must compile.");
            var version = YarnProjectMaterializer.Materialize(1, compilation);
            var runner = new DialogueRunner { Name = "SceneEntryProbe", autoStart = false };
            AddChild(runner);
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            var calls = 0;
            runner.AddCommandHandler(YarnNodeEntryTransform.CommandName, (Func<Task>)(async () =>
            {
                calls++;
                entered.TrySetResult();
                await release.Task;
            }));
            runner.SetProject(version.Project);
            await runner.StartDialogue("Start");
            await WaitForConditionAsync(() => entered.Task.IsCompleted, 2);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert(calls == 1 && runner.Dialogue.CurrentNode == "Start",
                "A pending entry command must block even an immediate jump.");
            Assert(!runner.VariableStorage.TryGetValue<bool>("$entered", out var value) || !value,
                "A pending entry command must block the first set instruction.");
            // Observe body callbacks directly after proving the real Runner's async dispatch is pending.
            var bodyReached = false;
            runner.Dialogue.LineHandler = _ => bodyReached = true;
            runner.Dialogue.OptionsHandler = _ => bodyReached = true;
            release.TrySetResult();
            await WaitForConditionAsync(() => bodyReached, 2);
            Assert(bodyReached, "Releasing the real Runner barrier must permit authored line/options.");
            await runner.Stop();
            runner.QueueFree();
            version.Project.baseLocalization.Dispose();
            version.Project.Dispose();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
