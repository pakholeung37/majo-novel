using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
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
        configuration.Dialogue!.DefaultProjectId = "M4Commands";
        configuration.Presentation!.Enabled = false;
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
            ConfigureCommands = commands => commands.Register<HeadlessProbeCommandArguments>(
                (arguments, context) =>
                {
                    Assert(arguments.Value == "alpha",
                        "Custom command received incorrect arguments.");
                    Assert(context.Actors is not null,
                        "Custom command did not receive ActorManager context.");
                    Assert(context.Services?.Get<HeadlessLifecycleService>() ==
                        customCommandService,
                        "Custom command could not resolve a game extension service.");
                    customCommandRan = true;
                    return Task.CompletedTask;
                }),
        };
        bootstrap.ConfigureDialoguePresenters += presenters =>
            presenters.Register(presenter);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        var yarnBackend = bootstrap.Host!.Services.Get<IYarnDialogueBackend>();
        var dialogue = bootstrap.Host.Services.Get<IDialoguePlayer>();
        var runner = yarnBackend.Runner;

        var manager = bootstrap.Host!.Services.Get<IActorManager>();
        var curtain = bootstrap.Host.Services.Get<IStageCurtainManager>();
        var scheduler = bootstrap.Host.Services.Get<IOperationScheduler>();
        var adapter = bootstrap.Host.Services.Get<IYarnCommandAdapter>();
        var joinedAtFinalState = false;
        var waitedAtFinalState = false;
        var cancelKeptCharacterVisible = false;
        var completeAppliedHiddenState = false;
        var curtainClosedAtFinalState = false;
        var curtainOpenedAtFinalState = false;
        var curtainCancelKeptOpenState = false;
        var curtainCompleteAppliedClosedState = false;
        presenter.LinePresented += line =>
        {
            var character = manager.Get<ICharacterActor>("hero");
            switch (line.Id)
            {
                case "line:m4-after-join":
                    var background = manager.Get<IBackgroundActor>("backdrop");
                    joinedAtFinalState =
                        character.Position.IsEqualApprox(configuration.Actors.LeftPosition) &&
                        character.ZIndex == 20 &&
                        character.View.ZIndex == 20 &&
                        background.Appearance == "dark" &&
                        scheduler.ActiveOperations.Count == 0;
                    break;
                case "line:m4-after-wait":
                    waitedAtFinalState =
                        character.Position.IsEqualApprox(configuration.Actors.RightPosition) &&
                        character.Appearance == "blue";
                    break;
                case "line:m4-after-cancel":
                    cancelKeptCharacterVisible = character.Visible && character.Opacity > 0;
                    break;
                case "line:m4-after-complete":
                    completeAppliedHiddenState =
                        !character.Visible && Mathf.IsZeroApprox(character.Opacity);
                    break;
                case "line:m4-after-curtain-close":
                    curtainClosedAtFinalState =
                        Mathf.IsEqualApprox(curtain.State.Opacity, 1) &&
                        scheduler.ActiveOperations.Count == 0;
                    break;
                case "line:m4-after-curtain-open":
                    curtainOpenedAtFinalState =
                        Mathf.IsZeroApprox(curtain.State.Opacity) &&
                        scheduler.ActiveOperations.Count == 0;
                    break;
                case "line:m4-after-curtain-cancel":
                    curtainCancelKeptOpenState =
                        Mathf.IsZeroApprox(curtain.State.Opacity);
                    break;
                case "line:m4-after-curtain-complete":
                    curtainCompleteAppliedClosedState =
                        Mathf.IsEqualApprox(curtain.State.Opacity, 1);
                    break;
            }
        };

        await dialogue.StartAsync();
        await WaitForConditionAsync(
            () => scheduler.ActiveOperations.Count >= 2,
            2);
        Assert(presenter.Lines.Count == 0,
            "Yarn advanced past await while grouped animations were active.");
        await dialogue.Completion;

        Assert(presenter.Lines.SequenceEqual(new[]
            {
                "after join",
                "after wait",
                "after cancel",
                "after complete",
                "after curtain close",
                "after curtain open",
                "after curtain cancel",
                "after curtain complete",
            }),
            "Yarn command test lines were delivered out of order.");
        Assert(joinedAtFinalState,
            "Yarn await did not join both non-waiting grouped animations.");
        Assert(waitedAtFinalState,
            "wait=true allowed Yarn to present the next line before the move completed.");
        Assert(cancelKeptCharacterVisible,
            "cancel incorrectly applied the hide operation's final state.");
        Assert(completeAppliedHiddenState,
            "await complete did not apply the hide operation's final state.");
        Assert(curtainClosedAtFinalState,
            "Waiting curtain close did not reach its stable opaque state.");
        Assert(curtainOpenedAtFinalState,
            "Waiting curtain open did not reach its stable transparent state.");
        Assert(curtainCancelKeptOpenState,
            "Cancelling curtain close did not preserve the previous open state.");
        Assert(curtainCompleteAppliedClosedState,
            "Completing curtain close did not apply the opaque target state.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "Completed Yarn dialogue left active operations.");

        var commandFailureSignal = string.Empty;
        bootstrap.Connect(
            MajoRuntimeBootstrap.SignalName.RuntimeCommandFailed,
            Callable.From<string, string>((command, message) =>
                commandFailureSignal = $"{command}:{message}"));
        var badArgumentsStart = dialogue.StartAsync(
            new DialogueStartRequest(StartNode: "BadArguments"));
        var badArgumentsCompletion = dialogue.Completion;
        await badArgumentsStart;
        await WaitForConditionAsync(() => adapter.LastFailure is not null, 2);
        Assert(adapter.LastFailure!.CommandName == "char",
            "Bad Yarn arguments were attributed to the wrong command.");
        Assert(adapter.LastFailure.Message.Contains(
                "Usage: char <actor>",
                StringComparison.Ordinal),
            "Bad Yarn arguments did not include command usage.");
        Assert(commandFailureSignal.StartsWith("char:", StringComparison.Ordinal),
            "Command failure was not exposed through the Runtime signal.");
        var badArgumentsResult = await badArgumentsCompletion;
        Assert(badArgumentsResult.Kind == DialogueCompletionKind.Completed,
            "Recoverable Yarn argument failure did not allow the dialogue to complete.");
        Assert(dialogue.State == DialogueSessionState.Idle,
            "Recoverable Yarn argument failure left the dialogue in a faulted state.");
        Assert(presenter.Lines.LastOrDefault() == "after bad command",
            "Yarn did not advance after a recoverable command failure.");

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
        Assert(presenter.Lines.LastOrDefault() == "after unknown command",
            "Yarn did not advance after an unknown command.");

        var lineCountBeforeStop = presenter.Lines.Count;
        await dialogue.StartAsync(new DialogueStartRequest(StartNode: "StopCancellation"));
        await WaitForConditionAsync(
            () => scheduler.ActiveOperations.Any(
                operation => operation.Id == "stop-cancelled"),
            2);
        var dialogueScopedOperation = scheduler.ActiveOperations.Single(
            operation => operation.Id == "stop-cancelled");
        await dialogue.StopAsync();
        await WaitForConditionAsync(
            () => dialogueScopedOperation.State == OperationState.Cancelled,
            2);
        Assert(presenter.Lines.Count == lineCountBeforeStop,
            "Yarn presented a line after dialogue-scoped command cancellation.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "Stopping Yarn dialogue left a dialogue-scoped operation active.");

        await dialogue.StartAsync(new DialogueStartRequest(StartNode: "CustomCommand"));
        await dialogue.Completion;
        Assert(customCommandRan,
            "Public Runtime command registry did not invoke the custom command.");
        Assert(presenter.Lines[^1] == "custom command completed",
            "Yarn did not continue after the custom Runtime command.");

        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(presenter.Lines.Count(line => line == "custom command completed") == 1,
            "A stopped Yarn command continuation escaped into a later session.");
        Assert(!IsInstanceValid(bootstrap), "M4 Runtime node leaked after shutdown.");
        Assert(!IsInstanceValid(runner), "M4 DialogueRunner leaked after cleanup.");
    }

    [RuntimeCommand(
        "majo_probe",
        Category = "Test",
        Usage = "majo_probe <value>")]
    private sealed class HeadlessProbeCommandArguments
    {
        [CommandParameter("value", 0, Primary = true, Required = true)]
        public string Value { get; set; } = string.Empty;
    }
}
