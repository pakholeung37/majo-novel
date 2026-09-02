#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Played;
using Majo.Runtime.Save;
using Majo.Runtime.Script;
using Majo.Runtime.Settings;
using Majo.Runtime.State;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.UI.Settings;

namespace Majo.Demo;

public partial class RuntimeDemoController : Node
{
    public override void _Ready()
    {
        RunDemoAsync();
    }

    private async void RunDemoAsync()
    {
        RuntimeDemoHud? hud = null;
        var automatic = OS.GetCmdlineUserArgs().Contains(
            "--demo-auto", StringComparer.Ordinal);
        try
        {
            var configuration = RuntimeDemoComposition.CreateConfiguration();
            configuration.Presentation!.Enabled = !automatic;

            RuntimeDialogueProbe? dialoguePresenter = null;
            if (automatic)
            {
                dialoguePresenter = new RuntimeDialogueProbe
                {
                    Name = "DemoAutomaticPresenter",
                    PreferredOptionIndex = 0,
                };
            }

            if (dialoguePresenter is not null)
            {
                AddChild(dialoguePresenter);
            }

            var runtime = new MajoRuntimeBootstrap
            {
                Name = "DemoRuntime",
                Configuration = configuration,
            };
            if (dialoguePresenter is not null)
            {
                runtime.ConfigureDialoguePresenters += presenters =>
                    presenters.Register(dialoguePresenter);
            }
            RuntimeDemoComposition.ConfigureRuntime(
                runtime,
                useFakeAudio: automatic);

            AddChild(runtime);
            await runtime.InitializationCompletion;
            var services = runtime.Host!.Services;
            var dialogue = services.Get<IDialoguePlayer>();
            var scheduler = services.Get<IOperationScheduler>();
            var state = services.Get<IStateManager>();
            if (!automatic)
            {
                hud = new RuntimeDemoHud { Name = "RuntimeDemoHud" };
                AddChild(hud);
                hud.Bind(
                    services.Get<IPlaybackModeManager>(),
                    services.Get<IHistoryManager>(),
                    state,
                    services.Get<IScriptPlayback>(),
                    dialogue,
                    scheduler,
                    services.Get<IInputManager>(),
                    services.Get<IFrameClock>(),
                    services.Get<IRuntimeDiagnostics>(),
                    services.Get<IStateEligibility>(),
                    services.Get<IGameSessionCoordinator>(),
                    services.Get<ISaveSlotManager>(),
                    services.Get<ISaveProductManager>(),
                    services.Get<ISettingsManager>(),
                    services.Get<IGlobalStateManager>(),
                    services.Get<IPlayedRegister>(),
                    services.Get<ISaveLoadOverlay>(),
                    services.Get<ISettingsOverlay>(),
                    () => runtime.Host?.SessionId ?? "<none>",
                    runtime.DebugOverlay);
            }

            await dialogue.StartAsync();
            await WaitForNaturalCompletionAsync(dialogue, state);

            var caseService = services.Get<DemoSessionService>();
            var route = caseService.SelectedRoute;
            if (route is null || scheduler.ActiveOperations.Count != 0)
            {
                throw new InvalidOperationException(
                    "Runtime demo did not finish with a selected route and zero active operations.");
            }

            if (automatic)
            {
                await runtime.RequestShutdownAsync();
                if (caseService.ShutdownCount != 1 ||
                    !caseService.RuntimeWasCancelledAtShutdown)
                {
                    throw new InvalidOperationException(
                        "Demo custom service did not complete its shutdown contract.");
                }
                dialoguePresenter!.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                GD.Print($"Runtime visible-scene smoke test passed: route={route}.");
                GetTree().Quit(0);
            }
            else
            {
                hud!.SetStatus(
                    $"M11-M15 flow completed · route={route} · Runtime remains live for save/load, New Game and Skip validation.");
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"Runtime demo failed: {exception}");
            hud?.SetStatus($"Runtime demo failed · {exception.Message}");
            if (automatic)
            {
                GetTree().Quit(1);
            }
        }
    }

    private async Task WaitForNaturalCompletionAsync(
        IDialoguePlayer dialogue,
        IStateManager state)
    {
        while (true)
        {
            var completion = await dialogue.Completion;
            if (completion.Kind == DialogueCompletionKind.Completed)
            {
                return;
            }

            var startedAt = Time.GetTicksMsec();
            while (Time.GetTicksMsec() - startedAt < 2_000)
            {
                if (state.Status == StateManagerStatus.Idle &&
                    dialogue.State == DialogueSessionState.Running)
                {
                    break;
                }

                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            if (state.Status != StateManagerStatus.Idle ||
                dialogue.State != DialogueSessionState.Running)
            {
                throw new InvalidOperationException(
                    "Runtime demo dialogue stopped outside a completed State restore.");
            }
        }
    }
}
