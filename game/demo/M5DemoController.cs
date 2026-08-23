#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Operations;

namespace Majo.Demo;

public partial class M5DemoController : Node
{
    public override void _Ready()
    {
        RunDemoAsync();
    }

    private async void RunDemoAsync()
    {
        M10DemoHud? hud = null;
        var automatic = OS.GetCmdlineUserArgs().Contains(
            "--m5-auto", StringComparer.Ordinal);
        try
        {
            var configuration = M5DemoComposition.CreateConfiguration();
            configuration.Presentation!.Enabled = !automatic;

            var portraitLayer = new CanvasLayer { Name = "M5PortraitLayer", Layer = 5 };
            var portraitHost = new Node2D { Name = "Portraits" };
            portraitLayer.AddChild(portraitHost);
            AddChild(portraitLayer);

            M5DialogueProbe? dialoguePresenter = null;
            if (automatic)
            {
                dialoguePresenter = new M5DialogueProbe
                {
                    Name = "M5AutomaticPresenter",
                    PreferredOptionIndex = 0,
                };
            }

            if (dialoguePresenter is not null)
            {
                AddChild(dialoguePresenter);
            }

            var runtime = new MajoRuntimeBootstrap
            {
                Name = "M5Runtime",
                Configuration = configuration,
            };
            if (dialoguePresenter is not null)
            {
                runtime.ConfigureDialoguePresenters += presenters =>
                    presenters.Register(dialoguePresenter);
            }
            M5DemoComposition.ConfigureRuntime(
                runtime,
                portraitHost,
                configuration.Actors!);

            AddChild(runtime);
            await runtime.InitializationCompletion;
            if (!automatic)
            {
                hud = new M10DemoHud { Name = "M10DemoHud" };
                AddChild(hud);
                hud.Bind(
                    runtime.Host!.Services.Get<IAutoManager>(),
                    runtime.Host.Services.Get<IHistoryManager>());
            }

            var dialogue = runtime.Host!.Services.Get<IDialoguePlayer>();
            await dialogue.StartAsync();
            await dialogue.Completion;

            var services = runtime.Host!.Services;
            var scheduler = services.Get<IOperationScheduler>();
            var caseService = services.Get<M5CaseService>();
            var route = caseService.SelectedRoute;
            if (route is null || scheduler.ActiveOperations.Count != 0)
            {
                throw new InvalidOperationException(
                    "M5 demo did not finish with a selected route and zero active operations.");
            }

            await runtime.RequestShutdownAsync();
            portraitLayer.QueueFree();
            if (caseService.ShutdownCount != 1 ||
                !caseService.RuntimeWasCancelledAtShutdown)
            {
                throw new InvalidOperationException(
                    "M5 custom service did not complete its shutdown contract.");
            }

            if (automatic)
            {
                dialoguePresenter!.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                GD.Print($"M5 visible-scene smoke test passed: route={route}.");
                GetTree().Quit(0);
            }
            else
            {
                hud!.SetStatus($"M10 demo completed · route={route} · Runtime clean");
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"M5 demo failed: {exception}");
            hud?.SetStatus($"M10 demo failed · {exception.Message}");
            if (automatic)
            {
                GetTree().Quit(1);
            }
        }
    }
}
