using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Operations;
using Majo.Runtime.Stage;
using YarnSpinnerGodot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateYarnCommandAdapterAsync()
    {
        var presenter = new HeadlessDialoguePresenter { Name = "ProbePresenter" };
        AddChild(presenter);

        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Resources.BackgroundsPath = "headless/fixtures";
        configuration.Dialogue!.DefaultProjectId = "M4Commands";
        configuration.Presentation!.Enabled = false;
        configuration.Actors!.ReferenceResolution = new Vector2I(1280, 720);
        configuration.Actors!.LeftPosition = new Vector2(100, 200);
        configuration.Actors.CenterPosition = new Vector2(200, 200);
        configuration.Actors.RightPosition = new Vector2(300, 200);
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "hero",
            ActorFactoryIds.Character,
            CharacterPositionPreset.Center,
            ("red", "res://tests/headless/fixtures/character_red.svg"),
            ("blue", "res://tests/headless/fixtures/character_blue.svg")));
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "backdrop",
            ActorFactoryIds.Background,
            CharacterPositionPreset.Center,
            ("dark", "res://tests/headless/fixtures/background_dark.svg"),
            ("light", "res://tests/headless/fixtures/background_light.svg")));

        var customCommandRan = false;
        var customCommandService = new HeadlessLifecycleService();
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "M4Runtime",
            Configuration = configuration,
            ConfigureServices = services => services.AddSingleton<HeadlessLifecycleService>(
                _ => customCommandService),
            ConfigureCommands = commands => commands.Add(
                "majo_probe",
                services => new HeadlessProbeCommand(
                    services.Get<HeadlessLifecycleService>(),
                    () => customCommandRan = true),
                "test",
                "1.0"),
        };
        bootstrap.ConfigureDialoguePresenters += presenters =>
            presenters.Register(presenter);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        var yarnBackend = bootstrap.Host!.Services.Get<IYarnDialogueBackend>();
        var dialogue = bootstrap.Host.Services.Get<IDialoguePlayer>();
        var runner = yarnBackend.Runner;

        var adapter = bootstrap.Host.Services.Get<IYarnCommandAdapter>();

        var unhandledCommand = string.Empty;
        runner.Connect(
            DialogueRunner.SignalName.onUnhandledCommand,
            Callable.From<string>(command => unhandledCommand = command));
        await dialogue.StartAsync(new DialogueStartRequest(StartNode: "UnknownCommand"));
        await WaitForConditionAsync(() => unhandledCommand.Length > 0, 2);
        Assert(unhandledCommand == "majo_unknown detail",
            "Yarn did not preserve the unhandled command diagnostic text.");
        var unknownResult = await dialogue.Completion;
        Assert(unknownResult.Kind == DialogueCompletionKind.Completed,
            "Unknown Yarn command did not allow the dialogue to complete.");
        Assert(adapter.LastFailure?.CommandName == "majo_unknown",
            "Unknown Yarn command was not exposed through Runtime diagnostics.");
        Assert(presenter.Lines.Count > 0 && presenter.Lines[^1] == "after unknown command",
            "Yarn did not advance after an unknown command.");

        await dialogue.StartAsync(new DialogueStartRequest(StartNode: "CustomCommand"));
        await dialogue.Completion;
        Assert(customCommandRan,
            "Public Runtime command registry did not invoke the custom command.");
        Assert(presenter.Lines[^1] == "custom command completed",
            "Yarn did not continue after the custom Runtime command.");

        var direction = bootstrap.Host.Services.Get<IDirectionTransactionCoordinator>();
        var directionHost = bootstrap.Host.Services.Get<GodotDirectionRenderHost>();
        var scopes = bootstrap.Host.Services.Get<IDirectionScopeResolver>();
        var illustrationStage = scopes.ResolveStage("DirectionCommands", "illustration");
        var illustrationView = new ViewId(illustrationStage.Value);
        var directionBeatCommitted = false;
        var directionActorMoved = false;
        var directionIllustrationShown = false;
        var directionIllustrationReset = false;
        var directionStageRestored = false;
        var directionActorLeft = false;
        presenter.LinePresented += line =>
        {
            var state = direction.State;
            state.Stages.TryGetValue(StageId.Main, out var mainStage);
            switch (line.Id)
            {
                case "line:m4-direction-beat":
                    directionBeatCommitted =
                        state.Revision == 1 &&
                        mainStage?.Backdrop?.Resource ==
                            new Majo.Runtime.Resources.ResourceKey(
                                "headless/fixtures/background_dark") &&
                        mainStage.Actors.TryGetValue(new ActorId("hero"), out var placed) &&
                        placed.Appearance == "red" && placed.Z == 5 &&
                        state.Views[illustrationView].StageId == illustrationStage &&
                        state.Views[illustrationView].Pose == new ViewPose(
                            new DirectionPoint(44.438f, 71.950f), 3, 10) &&
                        state.Screen.Bindings[LayoutSlotId.Main] == illustrationView &&
                        directionHost.PublishedScreen.Bindings[LayoutSlotId.Main] == illustrationView &&
                        directionHost.PublishedBackdrop is not null;
                    break;
                case "line:m4-direction-move":
                    directionActorMoved =
                        state.Revision == 2 &&
                        mainStage?.Actors[new ActorId("hero")].Appearance == "blue" &&
                        mainStage.Actors[new ActorId("hero")].Position ==
                            new DirectionPoint(
                                300f / configuration.Actors.ReferenceResolution.X * 100f,
                                (1f - 200f / configuration.Actors.ReferenceResolution.Y) * 100f);
                    break;
                case "line:m4-direction-illustration":
                    directionIllustrationShown =
                        state.Revision == 3 &&
                        state.Screen.Bindings[LayoutSlotId.Main] == illustrationView &&
                        state.Views[illustrationView].Pose == new ViewPose(
                            new DirectionPoint(40, 60), 1.5f, 10) &&
                        mainStage?.Actors.ContainsKey(new ActorId("hero")) == true;
                    break;
                case "line:m4-direction-stage":
                    directionStageRestored =
                        state.Revision == 5 &&
                        state.Screen.Bindings[LayoutSlotId.Main] == ViewId.Main &&
                        state.Views[ViewId.Main].StageId == StageId.Main &&
                        state.Views[ViewId.Main].Pose == ViewPose.Default;
                    break;
                case "line:m4-direction-reset":
                    directionIllustrationReset =
                        state.Revision == 4 &&
                        state.Screen.Bindings[LayoutSlotId.Main] == illustrationView &&
                        state.Views[illustrationView].Pose == ViewPose.Default &&
                        directionHost.PublishedScreen.Bindings[LayoutSlotId.Main] == illustrationView;
                    break;
                case "line:m4-direction-leave":
                    directionActorLeft =
                        state.Revision == 6 &&
                        mainStage?.Actors.Count == 0;
                    break;
            }
        };
        await dialogue.StartAsync(new DialogueStartRequest(StartNode: "DirectionCommands"));
        await dialogue.Completion;
        Assert(directionBeatCommitted,
            "Explicit Yarn Direction Beat did not atomically publish its directives.");
        Assert(directionActorMoved,
            "Yarn character did not resolve a configured mark or settle its move.");
        Assert(directionIllustrationShown,
            "Yarn illustration Stage did not remain independently composed and visible.");
        Assert(directionIllustrationReset,
            "Yarn camera reset changed the Screen binding instead of only resetting the illustration View Pose.");
        Assert(directionStageRestored,
            "Yarn stage show main did not restore the main Stage View.");
        Assert(directionActorLeft,
            "Yarn leave did not settle the target Actor state.");

        var broadcastStage = scopes.ResolveStage("DirectionMultiStage", "broadcast");
        var broadcastView = new ViewId(broadcastStage.Value);
        var directionBroadcastShown = false;
        var directionMainReturned = false;
        var directionSplitShown = false;
        presenter.LinePresented += line =>
        {
            var state = direction.State;
            switch (line.Id)
            {
                case "line:m4-direction-broadcast":
                    directionBroadcastShown =
                        state.Revision == 7 &&
                        state.Stages[StageId.Main].Actors[new ActorId("hero")].Appearance == "red" &&
                        state.Stages[broadcastStage].Actors[new ActorId("hero")].Appearance == "blue" &&
                        state.Views[broadcastView].StageId == broadcastStage &&
                        state.Screen.Bindings[LayoutSlotId.Main] == broadcastView &&
                        directionHost.StageIds.Contains(broadcastStage);
                    break;
                case "line:m4-direction-main-return":
                    directionMainReturned =
                        state.Revision == 8 &&
                        state.Screen.Bindings[LayoutSlotId.Main] == ViewId.Main &&
                        state.Stages[broadcastStage].Actors[new ActorId("hero")].Appearance == "blue";
                    break;
                case "line:m4-direction-split":
                    directionSplitShown =
                        state.Revision == 10 &&
                        state.Screen.Layout == new LayoutId("split") &&
                        state.Screen.Bindings.TryGetValue(new LayoutSlotId("inset"), out var insetView) &&
                        insetView == scopes.ResolveView("DirectionMultiStage", "reaction") &&
                        directionHost.ViewIds.Contains(insetView);
                    break;
            }
        };
        await dialogue.StartAsync(new DialogueStartRequest(StartNode: "DirectionMultiStage"));
        await dialogue.Completion;
        Assert(directionBroadcastShown,
            "Yarn did not create and show a node-local broadcast Stage atomically.");
        Assert(directionMainReturned,
            "Returning main View to main Stage did not preserve the dormant broadcast Stage.");
        Assert(directionSplitShown,
            "Yarn layout did not bind a second View into the split Screen.");

        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(presenter.Lines.Count(line => line == "custom command completed") == 1,
            "A stopped Yarn command continuation escaped into a later session.");
        Assert(!IsInstanceValid(bootstrap), "M4 Runtime node leaked after shutdown.");
        Assert(!IsInstanceValid(runner), "M4 DialogueRunner leaked after cleanup.");
    }
}

[Command("majo_probe", Category = "Test")]
public sealed partial class HeadlessProbeCommand : RuntimeCommand<HeadlessProbeCommand.Arguments>
{
    private readonly HeadlessLifecycleService service;
    private readonly Action onRan;

    public HeadlessProbeCommand(HeadlessLifecycleService service, Action onRan)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.onRan = onRan ?? throw new ArgumentNullException(nameof(onRan));
    }

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        if (arguments.Value != "alpha")
        {
            throw new InvalidOperationException("Custom command received incorrect arguments.");
        }
        if (service is null)
        {
            throw new InvalidOperationException("Custom command could not resolve a game extension service.");
        }
        onRan();
        return ValueTask.CompletedTask;
    }

    public sealed record Arguments([Primary] string Value);
}
