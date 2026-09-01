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
using Majo.Runtime.Stage;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.Travel;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateScriptAndStateRuntimeAsync()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "M95State";
        configuration.Dialogue.HotReload.Enabled = false;
        configuration.Presentation!.Enabled = true;
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
                resolver.Get<IOperationScheduler>(),
                loader,
                loader,
                loader,
                new AudioSettings()),
            typeof(IOperationScheduler));
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
        var camera = services.Get<ICameraManager>();
        var curtain = services.Get<IStageCurtainManager>();
        var effects = services.Get<IScreenEffectManager>();
        var transitions = services.Get<IStageTransitionManager>();
        var direction = services.Get<IDirectionTransactionCoordinator>();
        var input = services.Get<IInputManager>();
        var operations = services.Get<IOperationScheduler>();
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
                    "MajoStageCurtain",
                    recursive: true,
                    owned: false) is CanvasLayer { Layer: 10 } &&
                bootstrap.FindChild(
                    "MajoDialoguePresentationLayer",
                    recursive: true,
                    owned: false) is CanvasLayer { Layer: 20 } &&
                bootstrap.FindChild(
                    "MajoStageEffects",
                    recursive: true,
                    owned: false) is CanvasLayer { Layer: 90 },
            "Stage curtain must render above actors and below dialogue/full-screen effects.");
        Assert(bootstrap.FindChild(
                    "MajoDialogueTravelOverlay",
                    recursive: true,
                    owned: false) is Control { Visible: false },
            "Dialogue Travel did not create its editor-only default overlay.");
        var curtainRect = bootstrap.FindChild(
            "Curtain",
            recursive: true,
            owned: false) as ColorRect;
        Assert(curtainRect is not null,
            "Stage curtain did not create its full-screen ColorRect.");
        curtain.ApplyStableState(new StageCurtainState(Colors.Black, 1));
        var curtainFade = curtain.SetCurtain(
            Colors.Black,
            0,
            new StageOperationSpec(0.2, "curtain-interpolation-probe", Skippable: false));
        await WaitForConditionAsync(
            () => curtainRect.Color.A is > 0.05f and < 0.95f,
            1);
        Assert(curtainRect.Color.A is > 0.05f and < 0.95f,
            "Stage curtain fade did not render an intermediate alpha value.");
        await curtainFade.Completion;
        Assert(Mathf.IsZeroApprox(curtainRect.Color.A),
            "Stage curtain fade did not finish transparent.");
        Assert(bootstrap.FindChild(
                "MajoSaveLoadOverlay",
                recursive: true,
                owned: false) is Control { Visible: false },
            "M13 did not create the configured default Save/Load overlay.");
        saveLoadOverlay.Open();
        Assert(saveLoadOverlay.IsOpen && input.ActiveHandleCount == baseInputHandles + 1,
            "M13 Save/Load UI did not acquire exactly one modal input scope.");
        saveLoadOverlay.Close();
        Assert(!saveLoadOverlay.IsOpen && input.ActiveHandleCount == baseInputHandles,
            "M13 Save/Load UI did not release its modal input scope.");

        var descriptor = await scriptManager.ResolveAsync(
            new ScriptId("M95State", "Start"));
        Assert(descriptor.LineIds.SequenceEqual(
                ["line:m95-state-current", "line:m95-state-after"]) &&
                descriptor.ProgramFingerprint.Length == 64,
            "M9.5 ScriptManager did not resolve stable line metadata and fingerprint.");

        await dialogue.StartAsync();
        await WaitForConditionAsync(() => presentation.IsLineActive, 2);
        await WaitForConditionAsync(() => presentation.IsLineDisplayComplete, 2);
        Assert(script.Phase == ScriptPlaybackPhase.AwaitingAdvance &&
                script.CanCaptureCheckpoint &&
                script.CurrentSpot?.LineId == "line:m95-state-current",
            "M9.5 ScriptPlayback did not expose the safe line checkpoint boundary.");
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
        camera.ApplyStableState(new CameraState(new Vector2(123, 234), 1.25f, 7));
        curtain.ApplyStableState(new StageCurtainState(Colors.Black, 0.75f));
        effects.ApplyStableState(new ScreenEffectState(new Color(0.1f, 0.2f, 0.3f, 1), 0.4f, 0.15f));
        transitions.ApplyStableAppearance("courtroom");

        var snapshot = await state.CaptureGameAsync();
        Assert(snapshot.Records.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
                [
                    BuiltInStateKeys.DialogueVariables,
                    BuiltInStateKeys.Actors,
                    BuiltInStateKeys.AudioBgm,
                    BuiltInStateKeys.Direction,
                    BuiltInStateKeys.ScriptPlayback,
                ]),
            "M9.6 StateManager did not capture all built-in Game contributors.");
        var envelope = state.CreateEnvelope(
            snapshot,
            StateScope.Game,
            MajoRuntimeMetadata.PackageVersion,
            configuration.State!.GameBuildId,
            configuration.State.StorageNamespace);
        var canonicalBytes = serializer.Serialize(envelope);
        Assert(canonicalBytes.SequenceEqual(serializer.Serialize(envelope)),
            "M12 StateEnvelope serialization was not deterministic.");
        await storage.WriteAsync("headless/m12-roundtrip", canonicalBytes);
        var durableEnvelope = serializer.Deserialize(
            await storage.ReadAsync("headless/m12-roundtrip"));
        Assert(canonicalBytes.SequenceEqual(serializer.Serialize(durableEnvelope)) &&
                durableEnvelope.Script?.LineId == "line:m95-state-current",
            "M12 durable Envelope/Storage round-trip was not canonical.");
        await storage.DeleteAsync("headless/m12-roundtrip");

        variables.SetString("name", "Changed");
        variables.SetBoolean("extra", true);
        hero.SetAppearance("blue");
        hero.SetPosition(new Vector2(500, 600));
        hero.SetOpacity(0.1f);
        room.SetAppearance("light");
        await audio.PlayBgmAsync("battle", "music", volume: 0.2f);
        await audio.PlaySfxAsync("hit", "transient-sfx");
        await audio.PlayVoiceAsync("voice");
        camera.ApplyStableState(CameraState.Default);
        curtain.ApplyStableState(StageCurtainState.Default);
        effects.ApplyStableState(ScreenEffectState.Default);
        transitions.ApplyStableAppearance(null);

        var previousSessionId = bootstrap.Host.SessionId;
        await sessions.LoadGameAsync(durableEnvelope.Snapshot);
        Assert(bootstrap.Host.SessionId != previousSessionId &&
                playback.Mode == PlaybackMode.Manual &&
                history.Entries.Count == 1 &&
                history.Entries[0].LineId == "line:m95-state-current" &&
                dialogue.CurrentLineId == "line:m95-state-current" &&
                presentation.IsLineDisplayComplete &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            "M12 Load Game did not return at the restored current-line boundary: " +
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
            "M12 restored checkpoint did not settle at a stable save boundary.");
        await WaitForConditionAsync(
            () => travel.Entries.Count == 1 &&
                travel.Entries[0].Spot.LineId == "line:m95-state-current",
            2);

        var restoredHero = actors.Get<IActor>("state-hero");
        var restoredRoom = actors.Get<IActor>("state-room");
        Assert(variables.Get("name").StringValue == "Maya" &&
                Mathf.IsEqualApprox((float)variables.Get("score").NumberValue, 7) &&
                !variables.Contains("extra"),
            "M9.6 dialogue variables did not replace the mutated store exactly.");
        Assert(restoredHero.Appearance == "red" &&
                restoredHero.Position.IsEqualApprox(new Vector2(123, 234)) &&
                restoredHero.Tint.IsEqualApprox(new Color(0.2f, 0.4f, 0.8f)) &&
                Mathf.IsEqualApprox(restoredHero.Opacity, 0.65f) &&
                restoredHero.Visible &&
                restoredRoom.Appearance == "dark",
            "M9.6 actor/background state did not round-trip.");
        Assert(direction.State.Stages.Count == 1 &&
                direction.State.Stages.ContainsKey(StageId.Main) &&
                direction.State.Stages[StageId.Main].Backdrop is null &&
                direction.State.Views.Count == 1 &&
                direction.State.Views.ContainsKey(ViewId.Main),
            "M19 Direction state did not round-trip as the single staging snapshot.");
        Assert(audio.BgmTracks.Count == 1 &&
                audio.BgmTracks.Single().ClipId == "calm" &&
                audio.BgmTracks.Single().InstanceId == "music" &&
                Mathf.IsEqualApprox(audio.BgmTracks.Single().BaseVolume, 0.7f) &&
                audio.SfxInstances.Count == 0 &&
                audio.CurrentVoice is null,
            "M9.6 BGM restore or transient SFX/Voice exclusion was incorrect.");
        Assert(lineProbe.Lines.Count >= 2 &&
                lineProbe.Lines[0].Id == "line:m95-state-current" &&
                lineProbe.Lines[1].Id == "line:m95-state-current" &&
                lineProbe.Lines[0].Text.Text == lineProbe.Lines[1].Text.Text &&
                lineProbe.Lines[1].Text.Text.Contains("Maya", StringComparison.Ordinal) &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance &&
                input.ActiveHandleCount == baseInputHandles + 1,
            "M9.5 restore did not replay the captured resolved line exactly once: " +
            $"lines={lineProbe.Lines.Count}, current={dialogue.CurrentLineId}, " +
            $"phase={script.Phase}, handles={input.ActiveHandleCount}/{baseInputHandles}, " +
            $"text0={lineProbe.Lines[0].Text.Text}, text1={lineProbe.Lines[1].Text.Text}.");

        var linesBeforeAdvance = lineProbe.Lines.Count;
        dialogue.RequestNextLine();
        await WaitForConditionAsync(
            () => lineProbe.Lines.Count == linesBeforeAdvance + 1 &&
                dialogue.CurrentLineId == "line:m95-state-after" &&
                presentation.IsLineDisplayComplete &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            2);
        Assert(lineProbe.Lines[^1].Id == "line:m95-state-after",
            "M12 first advance after restore did not present the next line exactly once.");
        await WaitForConditionAsync(
            () => travel.Entries.Count == 2 &&
                travel.Entries[^1].Spot.LineId == "line:m95-state-after",
            2);
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Left,
            Pressed = true,
        });
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:m95-state-current" &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            2);
        Godot.Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = Key.Right,
            Pressed = true,
        });
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:m95-state-after" &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
            2);

        for (var round = 1; round <= 3; round++)
        {
            await dialogue.StopAsync();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var linesBeforeStart = lineProbe.Lines.Count;
            await dialogue.StartAsync();
            await WaitForConditionAsync(
                () => lineProbe.Lines.Count == linesBeforeStart + 1 &&
                    dialogue.CurrentLineId == "line:m95-state-current" &&
                    presentation.IsLineDisplayComplete &&
                    script.Phase == ScriptPlaybackPhase.AwaitingAdvance,
                2);

            variables.SetNumber("round", round);
            var roundHero = actors.Get<IActor>("state-hero");
            roundHero.SetAppearance("red");
            await audio.PlayBgmAsync("calm", "music", volume: 0.7f);
            var savedLineId = script.CurrentSpot?.LineId;
            var slot = new SaveSlotId(SaveSlotKind.Manual, round);
            var saved = await slots.SaveAsync(
                slot,
                new SaveSlotSaveRequest($"Round {round}", CaptureThumbnail: false));
            Assert(saved.Id == slot &&
                    saved.ProjectId == "M95State" &&
                    saved.NodeName == "Start" &&
                    saved.LineId == savedLineId &&
                    !string.IsNullOrWhiteSpace(saved.TextSummary),
                $"M13 slot metadata was incomplete in round {round}: " +
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
                $"M13 Load did not apply Session/History/Playback policy in round {round}.");
            await WaitForConditionAsync(
                () => lineProbe.Lines.Count == linesBeforeLoad + 1 &&
                    dialogue.CurrentLineId == savedLineId,
                2);
            if (round == 1)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Assert(dialogue.CurrentLineId == savedLineId,
                    "M13 Save/Load UI advanced past the line restored by its Load button.");
            }
            Assert(Mathf.IsEqualApprox(
                        (float)variables.Get("round").NumberValue,
                        round) &&
                    actors.Get<IActor>("state-hero").Appearance == "red" &&
                    audio.BgmTracks.Single().ClipId == "calm" &&
                    audio.SfxInstances.Count == 0 &&
                    audio.CurrentVoice is null &&
                    operations.ActiveOperations.Count == 0,
                $"M13 Save -> mutate -> Load round {round} did not restore durable state cleanly.");
        }

        var slotMetadata = await slots.ListAsync(SaveSlotKind.Manual);
        Assert(slotMetadata.Count == 3 &&
                slotMetadata.All(item => item.Compatibility == SaveSlotCompatibility.Compatible),
            "M13 did not list the three committed Manual slots deterministically.");

        await dialogue.StopAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(operations.ActiveOperations.Count == 0 &&
                input.ActiveHandleCount == baseInputHandles,
            "M13 headless cycles left active Operations or transient input ownership.");

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
            "M9.5/M9.6 Runtime nodes leaked after shutdown.");
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
