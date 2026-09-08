using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.History;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Script;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateAutoAndHistoryRuntimeAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "AutoAndHistory";
        configuration.Presentation!.Enabled = true;
        configuration.Presentation.LettersPerSecond = 10_000;
        configuration.Presentation.UseFadeEffect = false;
        configuration.Auto!.ExtraDelaySeconds = 0.03;
        configuration.History!.ShowDefaultUi = true;

        var audioBackend = new FakeAudioBackend();
        var audioLoader = new FakeAudioClipLoader("headless/audio")
            .Register("en/auto-history-voice");
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "AutoHistoryRuntime",
            Configuration = configuration,
        };
        bootstrap.ConfigureServices = services => services.ReplaceSingleton<IAudioManager>(
            resolver => new AudioManager(
                audioBackend,
                audioLoader,
                audioLoader,
                audioLoader,
                new AudioSettings()));
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var services = bootstrap.Host!.Services;
        var dialogue = services.Get<IDialoguePlayer>();
        var presentation = services.Get<IDialoguePresentation>();
        var script = services.Get<IScriptPlayback>();
        var playback = services.Get<IPlaybackModeManager>();
        var history = services.Get<IHistoryManager>();
        var audio = services.Get<IAudioManager>();
        var clock = services.Get<IFrameClock>();
        var input = services.Get<IInputManager>();
        var historyOverlay = bootstrap.FindChild(
            "MajoHistoryOverlay",
            recursive: true,
            owned: false) as Control;
        Assert(historyOverlay is not null && !historyOverlay.Visible,
            "Auto/history runtime did not create the configured default History overlay.");
        Assert(historyOverlay.GetParent() is CanvasLayer { Layer: 40 },
            "History overlay was not placed above the Demo HUD and dialogue layers.");
        Assert(input.ActiveHandleCount == 3,
            "Runtime did not establish the Auto, History, and Settings global input scopes.");

        await dialogue.StartAsync();
        var completion = dialogue.Completion;
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:auto-history-first" &&
                presentation.IsLineDisplayComplete,
            2);
        Assert(script.Phase == ScriptPlaybackPhase.AwaitingAdvance &&
                history.Entries.Count == 1 &&
                history.Entries[0].LineId == "line:auto-history-first",
            "Auto/history runtime did not record the first line at its completed display boundary.");
        await clock.DelayAsync(TimeSpan.FromSeconds(0.08), FrameTimeMode.Unscaled);
        Assert(dialogue.CurrentLineId == "line:auto-history-first",
            "Manual mode advanced without player input.");

        playback.SetMode(PlaybackMode.Auto);
        history.Open();
        Assert(playback.IsPaused && historyOverlay.Visible,
            "History modal did not pause Auto and reveal the default overlay.");
        await clock.DelayAsync(TimeSpan.FromSeconds(0.08), FrameTimeMode.Unscaled);
        Assert(dialogue.CurrentLineId == "line:auto-history-first",
            "Auto deadline advanced while History was open.");
        history.Close();
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:auto-history-second" &&
                presentation.IsLineDisplayComplete &&
                audio.CurrentVoice?.State == AudioPlaybackState.Playing,
            2);

        await clock.DelayAsync(TimeSpan.FromSeconds(0.08), FrameTimeMode.Unscaled);
        Assert(dialogue.CurrentLineId == "line:auto-history-second",
            "Auto/history runtime advanced before the active Voice completed.");
        audioBackend.Players.Last(player =>
            player.Category == AudioCategory.Voice && player.IsPlaying).CompletePlayback();
        await WaitForConditionAsync(
            () => script.Phase == ScriptPlaybackPhase.PresentingOptions &&
                presentation.IsPresentingOptions,
            2);
        Assert(playback.IsPaused,
            "Auto did not pause while Yarn options were presented.");

        await InjectKeyAsync(Key.Enter);
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:auto-history-final" &&
                presentation.IsLineDisplayComplete,
            2);
        Assert(!playback.IsPaused,
            "Auto did not resume after the option was selected.");
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(result.Kind == DialogueCompletionKind.Completed,
            "Auto playback did not complete the real Yarn dialogue.");

        history.Open();
        var replayButton = historyOverlay.FindChildren(
                "*",
                "Button",
                recursive: true,
                owned: false)
            .OfType<Button>()
            .Single(button => button.TooltipText == "Replay voice: auto-history-voice");
        replayButton.EmitSignal(BaseButton.SignalName.Pressed);
        await WaitForConditionAsync(
            () => audio.CurrentVoice?.ClipId == "en/auto-history-voice",
            2);
        Assert(audio.CurrentVoice?.State == AudioPlaybackState.Playing,
            "Auto/history runtime default History UI could not replay the retained Voice entry.");
        await bootstrap.ResetRuntimeAsync();
        Assert(!historyOverlay.Visible &&
                input.ActiveHandleCount == 3 &&
                !services.Get<IRuntimeActivityObserver>()
                    .HasActiveActivities(RuntimeActivityOwner.Other),
            "Reset did not close History UI or release and rebind Runtime work/input.");

        var overlayNode = historyOverlay;
        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(overlayNode) && !IsInstanceValid(bootstrap),
            "Auto/History Runtime nodes leaked after shutdown.");

        async Task InjectKeyAsync(Key key)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Godot.Input.ParseInputEvent(new InputEventKey
            {
                PhysicalKeycode = key,
                Pressed = true,
            });
            Godot.Input.ParseInputEvent(new InputEventKey
            {
                PhysicalKeycode = key,
                Pressed = false,
            });
        }
    }
}
