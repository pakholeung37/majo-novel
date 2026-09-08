using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Audio.Godot;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Input;
using Majo.Runtime.History;
using Majo.Runtime.Operations;
using Majo.Runtime.Save;
using Majo.Runtime.Save.Godot;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.Travel;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateScriptAndStateRuntimeAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "ScriptAndState";
        configuration.Presentation!.Enabled = true;
        configuration.Diagnostics!.MinimumLevel = RuntimeLogLevel.Debug;
        configuration.Presentation.LettersPerSecond = 10_000;
        configuration.Presentation.UseFadeEffect = false;
        configuration.Save!.Enabled = true;
        configuration.Save.CaptureThumbnails = false;
        configuration.Save.ShowDefaultUi = true;
        configuration.Save.ManualSlotCapacity = 3;
        configuration.State!.DialogueTravel.Enabled = true;
        configuration.State.DialogueTravel.ShowDefaultUi = true;
        configuration.Actors!.Actors.Add(CreateActorDefinition(
            "state-hero",
            ActorFactoryIds.Character,
            CharacterPositionPreset.Left,
            ("red", "res://tests/headless/fixtures/character_red.svg"),
            ("blue", "res://tests/headless/fixtures/character_blue.svg")));
        configuration.Actors.Actors.Add(CreateActorDefinition(
            "state-room",
            ActorFactoryIds.Background,
            CharacterPositionPreset.Center,
            ("dark", "res://tests/headless/fixtures/background_dark.svg"),
            ("light", "res://tests/headless/fixtures/background_light.svg")));
        var loader = new HeadlessAudioClipLoader()
            .Register("calm")
            .Register("battle")
            .Register("hit")
            .Register("en/voice");
        var lineProbe = new StateLineProbePresenter();
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "ScriptStateRuntime",
            Configuration = configuration,
        };
        bootstrap.ConfigureDialoguePresenters += presenters => presenters.Register(lineProbe);
        bootstrap.ConfigureServices = services => services.ReplaceSingleton<IAudioManager>(
            resolver => new AudioManager(
                new GodotAudioBackend(bootstrap),
                loader,
                loader,
                loader,
                new AudioSettings()));
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var services = bootstrap.Host!.Services;
        var dialogue = services.Get<IDialoguePlayer>();
        var yarnBackend = services.Get<IYarnDialogueBackend>();
        var presentation = services.Get<IDialoguePresentation>();
        var scriptManager = services.Get<IScriptManager>();
        var script = services.Get<IScriptPlayback>();
        var state = services.Get<IStateManager>();
        var variables = services.Get<IDialogueVariables>();
        var actors = services.Get<IActorManager>();
        var audio = services.Get<IAudioManager>();
        var direction = services.Get<IDirectionTransactionCoordinator>();
        var input = services.Get<IInputManager>();
        var operations = services.Get<IOperationRunner>();
        var activities = services.Get<IRuntimeActivityObserver>();
        var serializer = services.Get<IStateSerializer>();
        var storage = services.Get<IStateStorage>();
        var sessions = services.Get<IGameSessionCoordinator>();
        var playback = services.Get<IPlaybackModeManager>();
        var history = services.Get<IHistoryManager>();
        var slots = services.Get<ISaveSlotManager>();
        var saveLoadOverlay = services.Get<ISaveLoadOverlay>();
        var travel = services.Get<IDialogueTravelManager>();

        foreach (var existingSlot in await slots.ListAsync())
        {
            await slots.DeleteAsync(existingSlot.Id);
        }

        var baseInputHandles = input.ActiveHandleCount;
        Assert(bootstrap.FindChild(
                    "MajoDialoguePresentationLayer",
                    recursive: true,
                    owned: false) is CanvasLayer { Layer: 20 } &&
                bootstrap.FindChild(
                    "MajoDirectionScreen",
                    recursive: true,
                    owned: false) is CanvasLayer { Layer: 5 },
            "Dialogue and direction screen layers were not created.");
        Assert(bootstrap.FindChild(
                    "MajoDialogueTravelOverlay",
                    recursive: true,
                    owned: false) is Control { Visible: false },
            "Dialogue Travel did not create its editor-only default overlay.");
        Assert(bootstrap.FindChild(
                "MajoSaveLoadOverlay",
                recursive: true,
                owned: false) is Control { Visible: false },
            "Script/state runtime did not create the configured default Save/Load overlay.");
        saveLoadOverlay.Open();
        Assert(saveLoadOverlay.IsOpen && input.ActiveHandleCount == baseInputHandles + 1,
            "Save/Load UI did not acquire exactly one modal input scope.");
        saveLoadOverlay.Close();
        Assert(!saveLoadOverlay.IsOpen && input.ActiveHandleCount == baseInputHandles,
            "Save/Load UI did not release its modal input scope.");

        var descriptor = await scriptManager.ResolveAsync(
            new ScriptId("ScriptAndState", "Start"));
        Assert(descriptor.LineIds.SequenceEqual(
                ["line:state-current", "line:state-after"]) &&
                descriptor.ProgramFingerprint.Length == 64,
            "ScriptManager did not resolve stable line metadata and fingerprint.");

        variables.SetString("name", "Maya");
        variables.SetNumber("score", 7);
        var hero = actors.Create("state-hero");
        hero.SetAppearance("red");
        hero.SetPosition(new Vector2(123, 234));
        hero.SetTint(new Color(0.2f, 0.4f, 0.8f));
        hero.SetOpacity(0.65f);
        hero.SetVisible(true);
        var room = actors.Create("state-room");
        room.SetAppearance("dark");
        await audio.PlayBgmAsync("calm", "music", volume: 0.7f);

        await dialogue.StartAsync();
        await WaitForConditionAsync(() => presentation.IsLineActive, 2);
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);
        Assert(script.Phase == ScriptPlaybackPhase.AwaitingAdvance &&
                script.CanCaptureCheckpoint &&
                script.CurrentSpot?.LineId == "line:state-current",
            "ScriptPlayback did not expose the safe line checkpoint boundary.");
        await WaitForConditionAsync(
            () => travel.Entries.Count == 1 && travel.CurrentIndex == 0,
            2);

        var capturedCheckpoint = script.CaptureCheckpoint();
        var changedProgramCheckpoint = new ScriptCheckpoint(
            capturedCheckpoint.FormatVersion,
            new string('0', 64),
            capturedCheckpoint.Spot,
            capturedCheckpoint.ResolvedSubstitutions);
        await yarnBackend.ValidateCheckpointAsync(changedProgramCheckpoint);
        Assert(bootstrap.RingBuffer!.RecentEvents.Any(logEvent =>
                logEvent.EventId == "script.checkpoint_program_changed"),
            "A changed Yarn program fingerprint should warn while preserving a valid line checkpoint.");

        var incompatibleSubstitutions = new ScriptCheckpoint(
            capturedCheckpoint.FormatVersion,
            capturedCheckpoint.ProgramFingerprint,
            capturedCheckpoint.Spot,
            Array.Empty<string>());
        var rejectedIncompatibleSubstitutions = false;
        try
        {
            await yarnBackend.ValidateCheckpointAsync(incompatibleSubstitutions);
        }
        catch (ScriptCheckpointException)
        {
            rejectedIncompatibleSubstitutions = true;
        }
        Assert(rejectedIncompatibleSubstitutions,
            "Checkpoint validation should reject a changed Yarn line substitution shape.");

        var snapshot = await state.CaptureGameAsync();
        Assert(snapshot.Records.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
                [
                    BuiltInStateKeys.DialogueVariables,
                    BuiltInStateKeys.Actors,
                    BuiltInStateKeys.AudioBgm,
                    BuiltInStateKeys.Direction,
                    BuiltInStateKeys.ScriptPlayback,
                ]),
            "StateManager did not capture all built-in Game contributors.");
        var envelope = state.CreateEnvelope(
            snapshot,
            StateScope.Game,
            MajoRuntimeMetadata.PackageVersion,
            configuration.State!.GameBuildId,
            configuration.State.StorageNamespace);
        var canonicalBytes = serializer.Serialize(envelope);
        await storage.WriteAsync("headless/state-roundtrip", canonicalBytes);
        var durableEnvelope = serializer.Deserialize(
            await storage.ReadAsync("headless/state-roundtrip"));
        Assert(durableEnvelope.Script?.LineId == "line:state-current",
            "Stored Runtime snapshot lost its current Yarn line.");
        await storage.DeleteAsync("headless/state-roundtrip");

        variables.SetString("name", "Changed");
        variables.SetBoolean("extra", true);
        hero.SetAppearance("blue");
        hero.SetPosition(new Vector2(500, 600));
        hero.SetOpacity(0.1f);
        room.SetAppearance("light");
        await audio.PlayBgmAsync("battle", "music", volume: 0.2f);
        await audio.PlaySfxAsync("hit", "transient-sfx");
        await audio.PlayVoiceAsync("voice");

        var previousSessionId = bootstrap.Host.SessionId;
        await sessions.LoadGameAsync(durableEnvelope.Snapshot);
        Assert(bootstrap.Host.SessionId != previousSessionId &&
                playback.Mode == PlaybackMode.Manual &&
                history.Entries.Count == 1 &&
                history.Entries[0].LineId == "line:state-current" &&
                dialogue.CurrentLineId == "line:state-current" &&
                presentation.IsLineDisplayComplete &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            "Load Game did not return at the restored current-line boundary: " +
            $"sessionChanged={bootstrap.Host.SessionId != previousSessionId}, " +
            $"mode={playback.Mode}, history={history.Entries.Count}, " +
            $"line={dialogue.CurrentLineId ?? "<none>"}, " +
            $"displayComplete={presentation.IsLineDisplayComplete}, phase={script.Phase}.");
        await WaitForConditionAsync(
            () => lineProbe.Lines.Count >= 2 && presentation.IsLineActive,
            2);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);
        await WaitForConditionAsync(
            () => script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            2);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            "Script/state runtime restored checkpoint did not settle at a stable save boundary.");
        await WaitForConditionAsync(
            () => travel.Entries.Count == 1 &&
                travel.Entries[0].Spot.LineId == "line:state-current",
            2);

        var restoredHero = actors.GetActor<IActor>("state-hero");
        var restoredRoom = actors.GetActor<IActor>("state-room");
        Assert(variables.GetValue("name").StringValue == "Maya" &&
                Mathf.IsEqualApprox((float)variables.GetValue("score").NumberValue, 7) &&
                !variables.Contains("extra"),
            "Script/state runtime dialogue variables did not replace the mutated store exactly.");
        Assert(restoredHero.Appearance == "red" &&
                restoredHero.Position.IsEqualApprox(new Vector2(123, 234)) &&
                restoredHero.Tint.IsEqualApprox(new Color(0.2f, 0.4f, 0.8f)) &&
                Mathf.IsEqualApprox(restoredHero.Opacity, 0.65f) &&
                restoredHero.Visible &&
                restoredRoom.Appearance == "dark",
            "Script/state runtime actor/background state did not round-trip.");
        Assert(direction.State.Stages.Count == 1 &&
                direction.State.Stages.ContainsKey(StageId.Main) &&
                direction.State.Stages[StageId.Main].Backdrop is null &&
                direction.State.Views.Count == 1 &&
                direction.State.Views.ContainsKey(ViewId.Main),
            "Direction state did not round-trip as the single staging snapshot.");
        Assert(audio.BgmTracks.Count == 1 &&
                audio.BgmTracks.Single().ClipId == "calm" &&
                audio.BgmTracks.Single().InstanceId == "music" &&
                Mathf.IsEqualApprox(audio.BgmTracks.Single().BaseVolume, 0.7f) &&
                audio.SfxInstances.Count == 0 &&
                audio.CurrentVoice is null,
            "BGM restore or transient SFX/Voice exclusion was incorrect.");
        Assert(lineProbe.Lines.Count >= 2 &&
                lineProbe.Lines[0].Id == "line:state-current" &&
                lineProbe.Lines[1].Id == "line:state-current" &&
                lineProbe.Lines[0].Text.Text == lineProbe.Lines[1].Text.Text &&
                lineProbe.Lines[1].Text.Text.Contains("Maya", StringComparison.Ordinal) &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance &&
                input.ActiveHandleCount == baseInputHandles + 1,
            "Script/state runtime restore did not replay the captured resolved line exactly once: " +
            $"lines={lineProbe.Lines.Count}, current={dialogue.CurrentLineId}, " +
            $"phase={script.Phase}, handles={input.ActiveHandleCount}/{baseInputHandles}, " +
            $"text0={lineProbe.Lines[0].Text.Text}, text1={lineProbe.Lines[1].Text.Text}.");

        var linesBeforeAdvance = lineProbe.Lines.Count;
        dialogue.RequestNextLine();
        await WaitForConditionAsync(
            () => lineProbe.Lines.Count == linesBeforeAdvance + 1 &&
                dialogue.CurrentLineId == "line:state-after" &&
                presentation.IsLineDisplayComplete &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            2);
        Assert(lineProbe.Lines[^1].Id == "line:state-after",
            "Script/state runtime first advance after restore did not present the next line exactly once.");
        await WaitForConditionAsync(
            () => travel.Entries.Count == 2 &&
                travel.Entries[^1].Spot.LineId == "line:state-after",
            2);
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Left,
            Pressed = true,
        });
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:state-current" &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            2);
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Right,
            Pressed = true,
        });
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:state-after" &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            2);

        // Fast mode retains both the UI load and a repeated API load.
        var loadRounds = OS.GetCmdlineUserArgs().Contains("--majo-fast", StringComparer.Ordinal) ? 2 : 3;
        for (var round = 1; round <= loadRounds; round++)
        {
            await dialogue.StopAsync();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            variables.SetNumber("round", round);
            var roundHero = actors.GetActor<IActor>("state-hero");
            roundHero.SetAppearance("red");
            await audio.PlayBgmAsync("calm", "music", volume: 0.7f);
            var linesBeforeStart = lineProbe.Lines.Count;
            await dialogue.StartAsync();
            await WaitForConditionAsync(
                () => lineProbe.Lines.Count == linesBeforeStart + 1 &&
                    dialogue.CurrentLineId == "line:state-current" &&
                    presentation.IsLineDisplayComplete &&
                    script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
                2);

            var savedLineId = script.CurrentSpot?.LineId;
            var slot = new SaveSlotId(SaveSlotKind.Manual, round);
            var saved = await slots.SaveAsync(
                slot,
                new SaveSlotSaveRequest($"Round {round}", CaptureThumbnail: false));
            Assert(saved.Id == slot &&
                    saved.ProjectId == "ScriptAndState" &&
                    saved.NodeName == "Start" &&
                    saved.LineId == savedLineId &&
                    !string.IsNullOrWhiteSpace(saved.TextSummary),
                $"Script/state runtime slot metadata was incomplete in round {round}: " +
                $"project={saved.ProjectId}, node={saved.NodeName}, line={saved.LineId}, " +
                $"text={saved.TextSummary ?? "<null>"}.");

            variables.SetNumber("round", -round);
            roundHero.SetAppearance("blue");
            await audio.PlayBgmAsync("battle", "music", volume: 0.2f);
            await audio.PlaySfxAsync("hit", $"round-sfx-{round}");
            await audio.PlayVoiceAsync("voice");
            await WaitForConditionAsync(
                () => script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
                2);
            var sessionBeforeLoad = bootstrap.Host.SessionId;
            var linesBeforeLoad = lineProbe.Lines.Count;

            if (round == 1)
            {
                saveLoadOverlay.Open();
                await WaitForConditionAsync(
                    () => bootstrap.FindChildren(
                            "*",
                            nameof(Button),
                            recursive: true,
                            owned: false)
                        .OfType<Button>()
                        .Any(button => button.Text == "Load"),
                    2);
                var loadButton = bootstrap.FindChildren(
                        "*",
                        nameof(Button),
                        recursive: true,
                        owned: false)
                    .OfType<Button>()
                    .First(button => button.Text == "Load");
                loadButton.EmitSignal(BaseButton.SignalName.Pressed);
                await WaitForConditionAsync(
                    () => bootstrap.Host.SessionId != sessionBeforeLoad &&
                        slots.Status == SaveSlotManagerStatus.Idle,
                    2);
            }
            else
            {
                await slots.LoadAsync(slot);
            }
            Assert(bootstrap.Host.SessionId != sessionBeforeLoad &&
                    playback.Mode == PlaybackMode.Manual &&
                    history.Entries.Count == 1 &&
                    history.Entries[0].LineId == savedLineId,
                $"Load did not apply Session/History/Playback policy in round {round}.");
            await WaitForConditionAsync(
                () => lineProbe.Lines.Count == linesBeforeLoad + 1 &&
                    dialogue.CurrentLineId == savedLineId,
                2);
            if (round == 1)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Assert(dialogue.CurrentLineId == savedLineId,
                    "Save/Load UI advanced past the line restored by its Load button.");
            }
            Assert(Mathf.IsEqualApprox(
                        (float)variables.GetValue("round").NumberValue,
                        round) &&
                    actors.GetActor<IActor>("state-hero").Appearance == "red" &&
                    audio.BgmTracks.Single().ClipId == "calm" &&
                    audio.SfxInstances.Count == 0 &&
                    audio.CurrentVoice is null &&
                    !activities.HasActiveActivities(RuntimeActivityOwner.Other),
                $"Save -> mutate -> Load round {round} did not restore durable state cleanly.");
        }

        var slotMetadata = await slots.ListAsync(SaveSlotKind.Manual);
        Assert(slotMetadata.Count == loadRounds &&
                slotMetadata.All(item => item.Compatibility == SaveSlotCompatibility.Compatible),
            "Script/state runtime did not list the committed Manual slots deterministically.");

        // Regression: Rapidly loading a save while a background operation is running must not deadlock in StopAsync
        var loadTask = slots.LoadAsync(new SaveSlotId(SaveSlotKind.Manual, 1));
        var completedLoad = await Task.WhenAny(
            loadTask,
            Task.Delay(TimeSpan.FromSeconds(3)));
        Assert(ReferenceEquals(completedLoad, loadTask),
            "Loading a save while background operations are active deadlocked in StopAsync.");
        await loadTask;

        await dialogue.StopAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!activities.HasActiveActivities(RuntimeActivityOwner.Other) &&
                input.ActiveHandleCount == baseInputHandles,
            "Script/state runtime headless cycles left active Operations or transient input ownership.");

        foreach (var slot in await slots.ListAsync())
        {
            await slots.DeleteAsync(slot.Id);
        }

        var actorRoot = bootstrap.GetNode<CanvasLayer>("MajoStageViewport/MajoActors");
        var audioHost = bootstrap.GetNode("MajoAudioHost");
        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(bootstrap) &&
                !IsInstanceValid(actorRoot) &&
                !IsInstanceValid(audioHost),
            "Runtime nodes leaked after shutdown.");
    }

    private sealed class StateLineProbePresenter : IDialoguePresenter
    {
        public List<DialogueLine> Lines { get; } = new();

        public Task PresentLineAsync(
            DialogueLine line,
            DialoguePresentationContext context)
        {
            Lines.Add(line);
            return Task.CompletedTask;
        }
    }
}
