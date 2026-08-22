#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using YarnSpinnerGodot;

namespace Majo.Demo;

public partial class M5DemoController : Node
{
    public override void _Ready()
    {
        RunDemoAsync();
    }

    private async void RunDemoAsync()
    {
        M5DemoPresenter? presenter = null;
        var automatic = OS.GetCmdlineUserArgs().Contains(
            "--m5-auto", StringComparer.Ordinal);
        try
        {
            var configuration = M5DemoComposition.CreateConfiguration();

            var portraitLayer = new CanvasLayer { Name = "M5PortraitLayer", Layer = 5 };
            var portraitHost = new Node2D { Name = "Portraits" };
            portraitLayer.AddChild(portraitHost);
            AddChild(portraitLayer);

            Node dialoguePresenter;
            if (automatic)
            {
                dialoguePresenter = new M5DialogueProbe
                {
                    Name = "M5AutomaticPresenter",
                    PreferredOptionIndex = 0,
                };
            }
            else
            {
                presenter = new M5DemoPresenter { Name = "M5DemoPresenter" };
                dialoguePresenter = presenter;
            }

            AddChild(dialoguePresenter);

            var runner = new DialogueRunner
            {
                Name = "M5DialogueRunner",
                dialoguePresenters = new Godot.Collections.Array<Node?> { dialoguePresenter },
            };
            var runtime = new MajoRuntimeBootstrap
            {
                Name = "M5Runtime",
                Configuration = configuration,
                YarnDialogueRunner = runner,
            };
            M5DemoComposition.ConfigureRuntime(
                runtime,
                portraitHost,
                configuration.Actors!);

            AddChild(runtime);
            await runtime.InitializationCompletion;
            var resources = runtime.Host!.Services.Get<IResourceManager>();
            var yarnProject = await resources
                .CreateLoader<YarnProject>(configuration.Resources!.ScriptsPath)
                .LoadAsync(M5DemoComposition.DialogueProjectId);
            runner.SetProject(yarnProject);
            AddChild(runner);
            await runner.StartDialogue("Start");
            await runner.DialogueTask;

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
            runner.QueueFree();
            portraitLayer.QueueFree();
            if (caseService.ShutdownCount != 1 ||
                !caseService.RuntimeWasCancelledAtShutdown)
            {
                throw new InvalidOperationException(
                    "M5 custom service did not complete its shutdown contract.");
            }

            if (automatic)
            {
                dialoguePresenter.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                GD.Print($"M5 visible-scene smoke test passed: route={route}.");
                GetTree().Quit(0);
            }
            else
            {
                presenter!.SetStatus($"M5 completed · route={route} · Runtime clean");
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"M5 demo failed: {exception}");
            presenter?.SetStatus($"M5 failed · {exception.Message}");
            if (automatic)
            {
                GetTree().Quit(1);
            }
        }
    }
}
