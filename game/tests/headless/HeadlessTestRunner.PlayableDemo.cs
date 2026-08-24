using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Demo;
using Majo.Runtime.Actors;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidatePlayableDemoAsync()
    {
        var presenter = new RuntimeDialogueProbe
        {
            Name = "RuntimeDialogueProbe",
            PreferredOptionIndex = 1,
        };
        AddChild(presenter);
        var portraitHost = new Node2D { Name = "DemoPortraitHost" };
        AddChild(portraitHost);
        var configuration = RuntimeDemoComposition.CreateConfiguration();
        configuration.Presentation!.Enabled = false;
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "DemoRuntime",
            Configuration = configuration,
        };
        bootstrap.ConfigureDialoguePresenters += presenters =>
            presenters.Register(presenter);
        var customFactory = RuntimeDemoComposition.ConfigureRuntime(
            bootstrap,
            portraitHost,
            configuration.Actors!,
            useFakeAudio: true);
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;
        var resources = bootstrap.Host!.Services.Get<IResourceManager>();
        var bgmResources = resources.CreateLoader<AudioStream>(configuration.Audio!.BgmPath);
        var sfxResources = resources.CreateLoader<AudioStream>(configuration.Audio.SfxPath);
        var voiceResources = resources.CreateLoader<AudioStream>(configuration.Audio.VoicePath);
        var theme = await bgmResources.LoadAsync("demo-theme");
        var tension = await bgmResources.LoadAsync("demo-tension");
        var chime = await sfxResources.LoadAsync("demo-chime");
        var narratorVoice = await voiceResources.LoadAsync("en/narrator-welcome");
        var mayaVoice = await voiceResources.LoadAsync("en/maya-ready");
        Assert(
            theme is AudioStreamOggVorbis { Loop: true } &&
            tension is AudioStreamOggVorbis { Loop: true } &&
            chime is AudioStreamOggVorbis { Loop: false } &&
            narratorVoice is AudioStreamOggVorbis { Loop: false } &&
            mayaVoice is AudioStreamOggVorbis { Loop: false } &&
            chime.GetLength() > 1 &&
            narratorVoice.GetLength() > 10 &&
            mayaVoice.GetLength() > 5,
            "Runtime Demo audio assets were missing, invalid, or configured with unsafe loops.");
        var manager = bootstrap.Host!.Services.Get<IActorManager>();
        var scheduler = bootstrap.Host.Services.Get<IOperationScheduler>();
        var caseService = bootstrap.Host.Services.Get<DemoSessionService>();
        var dialogue = bootstrap.Host.Services.Get<IDialoguePlayer>();
        var variables = bootstrap.Host.Services.Get<IDialogueVariables>();
        var ownedRunner = bootstrap.Host.Services.Get<IYarnDialogueBackend>().Runner;
        var sessionEvents = new List<string>();
        dialogue.Started += _ => sessionEvents.Add("started");
        dialogue.Completed += _ => sessionEvents.Add("completed");
        dialogue.Stopped += _ => sessionEvents.Add("stopped");
        var joinedAtFinalState = false;
        var waitedAtFinalState = false;
        presenter.LinePresented += line =>
        {
            if (line.Id == "line:demo-parallel-ready")
            {
                var hero = manager.Get<ICharacterActor>("hero");
                var backdrop = manager.Get<IBackgroundActor>("backdrop");
                joinedAtFinalState =
                    hero.Appearance == "calm" &&
                    hero.Position.IsEqualApprox(configuration.Actors.LeftPosition) &&
                    backdrop.Appearance == "courtroom" &&
                    scheduler.ActiveOperations.Count == 0;
            }
            else if (line.Id == "line:demo-operation-wait")
            {
                var hero = manager.Get<ICharacterActor>("hero");
                waitedAtFinalState =
                    hero.Appearance == "determined" &&
                    hero.Position.IsEqualApprox(configuration.Actors.CenterPosition);
            }
        };

        variables.SetString("headlessProbe", "ready");
        Assert(variables.Get("$headlessProbe").StringValue == "ready",
            "Yarn-free dialogue variables did not preserve a string value.");
        Assert(ownedRunner.VariableStorage.TryGetValue<string>(
                "$headlessProbe",
                out var yarnVariable) && yarnVariable == "ready",
            "Yarn-free dialogue variables were not bridged into Yarn storage.");

        await dialogue.StartAsync();
        Assert(dialogue.State == DialogueSessionState.Running,
            "M6 StartAsync returned before the dialogue entered Running.");
        Assert(dialogue.CurrentProjectId == "main" && dialogue.CurrentNode == "Start",
            "M6 dialogue did not expose its resolved project and node.");
        await WaitForConditionAsync(() => scheduler.ActiveOperations.Count >= 2, 2);
        Assert(presenter.Lines.Select(line => line.Id).SequenceEqual(
                ["line:demo-welcome"]),
            "Runtime Demo advanced past its parallel opening animations before joining.");
        await dialogue.Completion;

        Assert(dialogue.State == DialogueSessionState.Idle,
            "Naturally completed M6 dialogue did not return to Idle.");
        Assert(sessionEvents.SequenceEqual(new[] { "started", "completed" }),
            "M6 natural dialogue session events were out of order.");
        Assert(joinedAtFinalState,
            "Runtime Demo parallel animation join did not expose both final Actor states.");
        Assert(waitedAtFinalState,
            "Runtime Demo wait=true animation did not finish before the next line.");
        Assert(presenter.Lines.Select(line => line.Id).SequenceEqual(new[]
            {
                "line:demo-welcome",
                "line:demo-parallel-ready",
                "line:demo-audio-ready",
                "line:demo-operation-wait",
                "line:demo-operation-cancel",
                "line:demo-operation-complete",
                "line:demo-dialogue-facade",
                "line:demo-input-guide",
                "line:demo-markup",
                "line:demo-auto-history-guide",
                "line:demo-state-checkpoint",
                "line:demo-state-mutated",
                "line:demo-option-guide",
                "line:demo-branch-evidence",
                "line:demo-resource-summary",
                "line:demo-finished",
            }),
            "Runtime Demo automatic option selection or long-form flow was incorrect.");
        Assert(presenter.Lines[0].Speaker == "Narrator" &&
                !presenter.Lines[0].Text.Text.StartsWith("Narrator:", StringComparison.Ordinal),
            "M6 line projection did not separate the speaker from dialogue text.");
        Assert(dialogue.CurrentLineId == "line:demo-finished",
            "M6 dialogue did not retain the most recently presented line ID.");
        Assert(caseService.SelectedRoute == "evidence",
            "Demo custom command did not update the custom service.");
        Assert(caseService.InitializeCount == 1,
            "Demo custom service was not initialized exactly once.");
        Assert(customFactory.CreateCount == 1,
            "Demo custom Actor factory did not create the hero exactly once.");
        Assert(manager.Get<ICharacterActor>("hero").SceneNode.GetParent() == portraitHost,
            "Demo custom Actor factory did not use its external host.");
        Assert(presenter.Events
                .Where(entry => entry.Kind == "dialogue")
                .Select(entry => entry.Value)
                .SequenceEqual(new[] { "started", "completed" }),
            "Demo Presenter probe did not observe the complete dialogue lifecycle.");
        Assert(presenter.Events.Any(entry => entry.Kind == "option"),
            "Demo Presenter probe did not observe an option selection.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "Runtime Demo ended with active operations.");

        var missingProject = await AssertThrowsAsync<DialogueProjectException>(
            () => dialogue.StartAsync(new DialogueStartRequest("missing", "Start")));
        Assert(missingProject.ProjectId == "missing",
            "Dialogue project failure did not retain its logical project ID.");
        Assert(dialogue.State == DialogueSessionState.Faulted,
            "Dialogue project load failure did not enter Faulted.");
        Assert(!ownedRunner.IsDialogueRunning && scheduler.ActiveOperations.Count == 0,
            "Dialogue project load failure leaked Runner or Operation work.");

        var lineCountBeforeStop = presenter.Lines.Count;
        await dialogue.StartAsync();
        var stoppedCompletion = dialogue.Completion;
        await WaitForConditionAsync(
            () => presenter.Lines.Count == lineCountBeforeStop + 1 &&
                presenter.Lines[^1].Id == "line:demo-welcome",
            2);
        await WaitForConditionAsync(() => scheduler.ActiveOperations.Count >= 2, 2);
        await dialogue.StopAsync();
        var stopped = await stoppedCompletion;
        Assert(stopped.Kind == DialogueCompletionKind.Stopped,
            "M6 active stop did not produce a Stopped completion.");
        Assert(dialogue.State == DialogueSessionState.Idle,
            "M6 active stop did not return to Idle.");
        Assert(sessionEvents.SequenceEqual(new[]
            {
                "started",
                "completed",
                "started",
                "stopped",
            }),
            "M6 stopped dialogue session events were out of order.");
        Assert(presenter.Lines.Count == lineCountBeforeStop + 1,
            "M6 stop allowed a line after the expected welcome boundary to escape.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "M6 stop left dialogue-owned operations active.");

        var heroNode = manager.Get<ICharacterActor>("hero").SceneNode;
        Assert(resources.LoadedResourceCount >= 3,
            "Convention-based Actor and Yarn resources did not enter the Runtime cache.");
        await bootstrap.RequestShutdownAsync();
        portraitHost.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(caseService.ShutdownCount == 1,
            "Demo custom service was not shut down exactly once.");
        Assert(caseService.RuntimeWasCancelledAtShutdown,
            "Demo custom service did not observe Runtime cancellation during shutdown.");
        Assert(!IsInstanceValid(heroNode),
            "Demo custom Actor node survived Runtime shutdown.");
        Assert(!IsInstanceValid(bootstrap), "Demo Runtime node leaked after shutdown.");
        Assert(!IsInstanceValid(ownedRunner), "Demo DialogueRunner leaked after cleanup.");
        Assert(!IsInstanceValid(portraitHost), "Demo custom Actor host leaked after cleanup.");
        Assert(resources.LoadedResourceCount == 0,
            "Runtime resource cache survived shutdown.");
    }
}
