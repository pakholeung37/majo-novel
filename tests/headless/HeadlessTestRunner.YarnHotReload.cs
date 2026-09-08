using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Script;
using Majo.Runtime.Dialogue.Yarn;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateYarnHotReloadAsync()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            "majo-yarn-hot-reload",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "YarnVmBridge.yarnproject"), """
                {
                  "projectFileVersion": 3,
                  "sourceFiles": ["YarnVmBridge.yarn"],
                  "excludeFiles": [],
                  "baseLanguage": "en"
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "YarnVmBridge.yarn"), """
                title: Start
                ---
                Updated current line. #line:bridge-headless-first
                Updated next line. #line:bridge-headless-second
                ===

                """);

            var presenter = new HotReloadPresenter();
            var configuration = CreateHeadlessConfiguration();
            configuration.Resources!.RootPath = "res://tests";
            configuration.Resources.ScriptsPath = "headless";
            configuration.Dialogue!.DefaultProjectId = "YarnVmBridge";
            configuration.Dialogue.HotReload.Enabled = true;
            configuration.Dialogue.HotReload.SourceRootOverride = sourceRoot;
            configuration.Dialogue.HotReload.ConsistencyScanMilliseconds = 30_000;
            configuration.Presentation!.Enabled = true;
            configuration.Presentation.LettersPerSecond = 10_000;
            configuration.Presentation.UseFadeEffect = false;

            var bootstrap = new MajoRuntimeBootstrap
            {
                Name = "YarnHotReloadRuntime",
                Configuration = configuration,
            };
            bootstrap.ConfigureDialoguePresenters += presenters => presenters.Register(presenter);
            AddChild(bootstrap);
            await bootstrap.InitializationCompletion;

            var services = bootstrap.Host!.Services;
            var dialogue = services.Get<IDialoguePlayer>();
            var hotReload = services.Get<IYarnHotReloadService>();
            var script = services.Get<IScriptPlayback>();
            var presentation = services.Get<IDialoguePresentation>();
            var stopped = 0;
            var completed = 0;
            dialogue.Stopped += _ => stopped++;
            dialogue.Completed += _ => completed++;

            var runner = services.Get<IYarnDialogueBackend>().Runner;
            var delayedPresenter = new DelayedDismissalPresenter();
            runner.AddChild(delayedPresenter);
            services.Get<IYarnDialogueBackend>().RegisterNativePresenter(delayedPresenter);
            await dialogue.StartAsync();
            await WaitForConditionAsync(() => presenter.Lines.Count == 1, 2);
            var sessionId = ((DialoguePlayer)dialogue).CurrentDialogueSessionId;
            delayedPresenter.DelayNextDismissal = true;
            await hotReload.RequestReloadAsync("YarnVmBridge");
            await WaitForConditionAsync(
                () => hotReload.Status.Message?.Contains("was applied", StringComparison.Ordinal) == true,
                3);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            Assert(!delayedPresenter.OverlappedDismissal && delayedPresenter.DismissalCompleted,
                "Yarn hot reload replayed before the previous line finished dismissing.");
            var linePresenter = runner.FindChildren("*", nameof(LinePresenter), recursive: true, owned: false).OfType<LinePresenter>().Single();
            Assert(linePresenter.presenterControl!.Visible &&
                    linePresenter.presenterControl.Modulate.A > 0,
                "Yarn hot reload left the line presenter hidden.");
            Assert(presenter.Lines.Count >= 2 &&
                    presenter.Lines[^1].Id == "line:bridge-headless-first" &&
                    presenter.Lines[^1].Text.Text == "Updated current line." &&
                    presenter.Lines[^1].Origin == DialoguePresentationOrigin.HotReloadReplay,
                "Yarn hot reload did not replay the updated current line.");
            Assert(script.Phase == ScriptPlaybackPhase.AwaitingAdvance &&
                    script.CurrentSpot?.LineId == "line:bridge-headless-first" &&
                    presentation.IsLineActive && presentation.IsLineDisplayComplete,
                "Yarn hot reload did not leave the replayed line at a stable advance boundary.");
            Assert(((DialoguePlayer)dialogue).CurrentDialogueSessionId == sessionId &&
                    stopped == 0 && completed == 0,
                "Yarn hot reload leaked an internal stop/start through DialoguePlayer.");

            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "YarnVmBridge.yarn"),
                "title: Start\n---\n<<if true>>\nBroken\n===\n");
            await WaitForConditionAsync(
                () => hotReload.Status.Message?.Contains(
                    "last-known-good", StringComparison.Ordinal) == true,
                3);
            Assert(dialogue.State == DialogueSessionState.Running &&
                    dialogue.CurrentLineId == "line:bridge-headless-first" &&
                    hotReload.Status.Diagnostics.Any(diagnostic =>
                        diagnostic.Severity == "Error"),
                "A failed Yarn compilation disturbed the running last-known-good session.");

            dialogue.RequestNextLine();
            await WaitForConditionAsync(
                () => presenter.Lines.Any(line =>
                    line.Id == "line:bridge-headless-second" &&
                    line.Text.Text == "Updated next line."),
                2);
            dialogue.RequestNextLine();
            await dialogue.Completion;
            Assert(completed == 1 && stopped == 0,
                "Hot-reloaded dialogue did not complete as one continuous session.");

            await bootstrap.RequestShutdownAsync();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        finally
        {
            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, recursive: true);
            }
        }
    }

    private sealed partial class DelayedDismissalPresenter : Node, DialoguePresenterBase
    {
        public List<IActionMarkupHandler> ActionMarkupHandlers { get; } = new();
        public bool DelayNextDismissal { get; set; }
        public bool DismissalCompleted { get; private set; }
        public bool OverlappedDismissal { get; private set; }
        private bool dismissing;

        public async YarnTask RunLineAsync(LocalizedLine line, LineCancellationToken token)
        {
            OverlappedDismissal |= dismissing;
            await YarnTask.WaitUntilCanceled(token.NextLineToken).SuppressCancellationThrow();
            if (DelayNextDismissal)
            {
                DelayNextDismissal = false;
                dismissing = true;
                await YarnTask.Delay(100);
                dismissing = false;
                DismissalCompleted = true;
            }
        }
    }

    private sealed class HotReloadPresenter : IDialoguePresenter
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
            catch (OperationCanceledException) when (context.NextLineToken.IsCancellationRequested)
            {
            }
        }
    }
}
