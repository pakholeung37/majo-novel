using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Demo;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Operations;
using Majo.Runtime.Played;
using Majo.Runtime.Resources;
using Majo.Runtime.Save;
using Majo.Runtime.Settings;
using Majo.Runtime.State;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.UI.Settings;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private static readonly string[] StartedAndCompletedEvents = ["started", "completed"];
    private static readonly string[] StoppedSessionEvents = ["started", "completed", "started", "stopped"];
    private static readonly string[] ExpectedPlayableDemoLines =
    [
        "line:demo-welcome",
        "line:demo-parallel-ready",
        "line:demo-m17-intro",
        "line:demo-m17-voice",
        "line:demo-m18-preload",
        "line:demo-m19-stage",
        "line:demo-m17-m19-complete",
        "line:demo-audio-ready",
        "line:demo-operation-wait",
        "line:demo-operation-complete",
        "line:demo-dialogue-facade",
        "line:demo-input-guide",
        "line:demo-markup",
        "line:demo-auto-history-guide",
        "line:demo-diagnostics-guide",
        "line:demo-state-checkpoint",
        "line:demo-state-mutated",
        "line:demo-save-guide",
        "line:demo-settings-global-guide",
        "line:demo-played-skip-guide",
        "line:demo-option-guide",
        "line:demo-branch-evidence",
        "line:demo-resource-summary",
        "line:demo-finished",
    ];

    private async Task ValidatePlayableDemoAsync()
    {
        var presenter = new RuntimeDialogueProbe
        {
            Name = "RuntimeDialogueProbe",
            PreferredOptionIndex = 1,
        };
        AddChild(presenter);
        var configuration = RuntimeDemoComposition.CreateConfiguration();
        configuration.Presentation!.Enabled = false;
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "DemoRuntime",
            Configuration = configuration,
        };
        bootstrap.ConfigureDialoguePresenters += presenters =>
            presenters.Register(presenter);
        RuntimeDemoComposition.ConfigureRuntime(
            bootstrap,
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
        var scheduler = bootstrap.Host.Services.Get<IOperationRunner>();
        var caseService = bootstrap.Host.Services.Get<DemoSessionService>();
        var dialogue = bootstrap.Host.Services.Get<IDialoguePlayer>();
        var variables = bootstrap.Host.Services.Get<IDialogueVariables>();
        var ownedRunner = bootstrap.Host.Services.Get<IYarnDialogueBackend>().Runner;
        var direction = bootstrap.Host.Services.Get<IDirectionTransactionCoordinator>();
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
                var stage = direction.State.Stages[StageId.Main];
                var hero = stage.Actors[new ActorId("hero")];
                joinedAtFinalState =
                    hero.Appearance == "calm" &&
                    hero.Position.X < 50 &&
                    stage.Backdrop?.Resource.ToString().EndsWith(
                        "courtroom", StringComparison.Ordinal) == true &&
                    scheduler.ActiveOperations.Count == 0;
            }
            else if (line.Id == "line:demo-operation-wait")
            {
                var hero = direction.State.Stages[StageId.Main].Actors[new ActorId("hero")];
                waitedAtFinalState =
                    hero.Appearance == "determined" &&
                    Mathf.IsEqualApprox(hero.Position.X, 50);
            }
        };

        variables.SetString("headlessProbe", "ready");
        Assert(variables.GetValue("$headlessProbe").StringValue == "ready",
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
        await dialogue.Completion;

        Assert(dialogue.State == DialogueSessionState.Idle,
            "Naturally completed M6 dialogue did not return to Idle.");
        Assert(sessionEvents.SequenceEqual(StartedAndCompletedEvents),
            "M6 natural dialogue session events were out of order.");
        Assert(joinedAtFinalState,
            "Runtime Demo parallel animation join did not expose both final Actor states.");
        Assert(waitedAtFinalState,
            "Runtime Demo wait:true animation did not finish before the next line.");
        Assert(presenter.Lines.Select(line => line.Id).SequenceEqual(ExpectedPlayableDemoLines),
            "Runtime Demo automatic option selection or long-form flow was incorrect: " +
            string.Join(", ", presenter.Lines.Select(line => line.Id)));
        Assert(presenter.Lines[0].Speaker == "Narrator" &&
                !presenter.Lines[0].Text.Text.StartsWith("Narrator:", StringComparison.Ordinal),
            "M6 line projection did not separate the speaker from dialogue text.");
        Assert(dialogue.CurrentLineId == "line:demo-finished",
            "M6 dialogue did not retain the most recently presented line ID.");
        Assert(caseService.SelectedRoute == "evidence",
            "Demo custom command did not update the custom service.");
        Assert(caseService.VoiceLocale == "zh-CN" && caseService.PreloadGroup is null,
            "M17/M18 Demo commands did not switch voice locale or release the preload group.");
        Assert(caseService.InitializeCount == 1,
            "Demo custom service was not initialized exactly once.");
        Assert(presenter.Events
                .Where(entry => entry.Kind == "dialogue")
                .Select(entry => entry.Value)
                .SequenceEqual(StartedAndCompletedEvents),
            "Demo Presenter probe did not observe the complete dialogue lifecycle.");
        Assert(presenter.Events.Any(entry => entry.Kind == "option"),
            "Demo Presenter probe did not observe an option selection.");
        Assert(scheduler.ActiveOperations.Count == 0,
            "Runtime Demo ended with active operations.");

        var diagnostics = bootstrap.Host.Services.Get<IRuntimeDiagnostics>();
        var diagnosticSnapshot = diagnostics.CaptureSnapshot();
        var played = bootstrap.Host.Services.Get<IPlayedRegister>();
        var slots = bootstrap.Host.Services.Get<ISaveSlotManager>();
        var settings = bootstrap.Host.Services.Get<ISettingsManager>();
        var global = bootstrap.Host.Services.Get<IGlobalStateManager>();
        Assert(
            diagnosticSnapshot.Dialogue?.LineId == "line:demo-finished" &&
            diagnosticSnapshot.RecentLogs?.Count > 0,
            "M11 Demo did not expose its current line and structured event buffer.");
        Assert(
            configuration.State!.StorageNamespace == "majo/runtime-demo" &&
            bootstrap.Host.Services.Get<IStateEligibility>() is not null,
            "M12 Demo did not use its isolated durable state and eligibility composition.");
        Assert(
            slots.Capacities[SaveSlotKind.Manual] == 3 &&
            slots.Capacities[SaveSlotKind.Quick] == 2 &&
            slots.Capacities[SaveSlotKind.Auto] == 2 &&
            bootstrap.Host.Services.Get<ISaveProductManager>() is not null &&
            bootstrap.Host.Services.Get<ISaveLoadOverlay>() is not null,
            "M13 Demo did not compose Manual/Quick/Auto slots and the default Save UI.");
        Assert(
            settings.Current.SkipDelaySeconds == 0.08 &&
            global.RecoveryStatus.Kind != ScopedStateRecoveryKind.StrictFailure &&
            bootstrap.Host.Services.Get<ISettingsOverlay>() is not null,
            "M14 Demo did not compose projected Settings, Global State, and default Settings UI.");
        Assert(
            played.LocalePolicy == PlayedLocalePolicy.Shared &&
            configuration.GlobalState!.Enabled &&
            bootstrap.Host.Services.Get<Majo.Runtime.Auto.IPlaybackModeManager>() is not null,
            "M15 Demo did not compose Global Played state and unified playback modes.");

        var missingProject = await AssertThrowsAsync<DialogueProjectException>(
            () => dialogue.StartAsync(new DialogueStartRequest("missing", "Start")));
        Assert(missingProject.ProjectId == "missing",
            "Dialogue project failure did not retain its logical project ID.");
        Assert(dialogue.State == DialogueSessionState.Faulted,
            "Dialogue project load failure did not enter Faulted.");
        Assert(!ownedRunner.IsDialogueRunning && scheduler.ActiveOperations.Count == 0,
            "Dialogue project load failure leaked Runner or Operation work.");

        var lineCountBeforeStop = presenter.Lines.Count;
        var revisionBeforeStopRun = direction.State.Revision;
        await dialogue.StartAsync();
        var stoppedCompletion = dialogue.Completion;
        await WaitForConditionAsync(
            () => presenter.Lines.Count == lineCountBeforeStop + 1 &&
                presenter.Lines[^1].Id == "line:demo-welcome",
            2);
        await WaitForConditionAsync(
            () => direction.State.Revision > revisionBeforeStopRun,
            2);
        await dialogue.StopAsync();
        var stopped = await stoppedCompletion;
        Assert(stopped.Kind == DialogueCompletionKind.Stopped,
            "M6 active stop did not produce a Stopped completion.");
        Assert(dialogue.State == DialogueSessionState.Idle,
            "M6 active stop did not return to Idle.");
        Assert(sessionEvents.SequenceEqual(StoppedSessionEvents),
            "M6 stopped dialogue session events were out of order.");
        Assert(presenter.Lines.Count == lineCountBeforeStop + 1,
            "M6 stop allowed a line after the expected welcome boundary to escape.");
        await WaitForConditionAsync(() => scheduler.ActiveOperations.Count == 0, 2);
        Assert(scheduler.ActiveOperations.Count == 0,
            "M6 stop left dialogue-owned operations active.");

        Assert(resources.LoadedResourceCount >= 3,
            "Convention-based Actor and Yarn resources did not enter the Runtime cache.");
        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert(caseService.ShutdownCount == 1,
            "Demo custom service was not shut down exactly once.");
        Assert(caseService.RuntimeWasCancelledAtShutdown,
            "Demo custom service did not observe Runtime cancellation during shutdown.");
        Assert(!IsInstanceValid(bootstrap), "Demo Runtime node leaked after shutdown.");
        Assert(!IsInstanceValid(ownedRunner), "Demo DialogueRunner leaked after cleanup.");
        Assert(resources.LoadedResourceCount == 0,
            "Runtime resource cache survived shutdown.");
    }
}
