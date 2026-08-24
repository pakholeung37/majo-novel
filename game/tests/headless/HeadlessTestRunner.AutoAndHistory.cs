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
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "M10AutoHistory";
        configuration.Presentation!.Enabled = true;
        configuration.Presentation.LettersPerSecond = 10_000;
        configuration.Presentation.UseFadeEffect = false;
        configuration.Auto!.ExtraDelaySeconds = 0.03;
        configuration.History!.Capacity = 2;
        configuration.History.ShowDefaultUi = true;

        var audioBackend = new FakeAudioBackend();
        var audioLoader = new FakeAudioClipLoader("headless/audio")
            .Register("en/m10-voice");
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "AutoHistoryRuntime",
            Configuration = configuration,
        };
        bootstrap.ConfigureServices = services => services.ReplaceSingleton<IAudioManager>(
            resolver => new AudioManager(
                audioBackend,
                resolver.Get<IOperationScheduler>(),
                audioLoader,
                audioLoader,
                audioLoader,
                new AudioSettings()),
            typeof(IOperationScheduler));
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var services = bootstrap.Host!.Services;
        var dialogue = services.Get<IDialoguePlayer>();
        var presentation = services.Get<IDialoguePresentation>();
        var script = services.Get<IScriptPlayback>();
        var auto = services.Get<IAutoManager>();
        var history = services.Get<IHistoryManager>();
        var audio = services.Get<IAudioManager>();
        var clock = services.Get<IFrameClock>();
        var input = services.Get<IInputManager>();
        var operations = services.Get<IOperationScheduler>();
        var historyOverlay = bootstrap.FindChild(
            "MajoHistoryOverlay",
            recursive: true,
            owned: false) as Control;
        Assert(historyOverlay is not null && !historyOverlay.Visible,
            "M10 did not create the configured default History overlay.");
        Assert(historyOverlay.GetParent() is CanvasLayer { Layer: 40 },
            "History overlay was not placed above the Demo HUD and dialogue layers.");
        Assert(input.ActiveHandleCount == 2,
            "M10 did not establish exactly the Auto and History global input scopes.");

        await dialogue.StartAsync();
        var completion = dialogue.Completion;
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:m10-first" &&
                presentation.IsLineDisplayComplete,
            2);
        Assert(script.Phase == ScriptPlaybackPhase.AwaitingAdvance &&
                history.Entries.Count == 1 &&
                history.Entries[0].LineId == "line:m10-first",
            "M10 did not record the first line at its completed display boundary.");
        await clock.DelayAsync(TimeSpan.FromSeconds(0.08), FrameTimeMode.Unscaled);
        Assert(dialogue.CurrentLineId == "line:m10-first",
            "M10 Manual mode advanced without player input.");

        auto.SetMode(AutoMode.Auto);
        history.Open();
        Assert(auto.IsPaused && historyOverlay.Visible,
            "M10 History modal did not pause Auto and reveal the default overlay.");
        await clock.DelayAsync(TimeSpan.FromSeconds(0.08), FrameTimeMode.Unscaled);
        Assert(dialogue.CurrentLineId == "line:m10-first",
            "M10 Auto deadline advanced while History was open.");
        history.Close();
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:m10-second" &&
                presentation.IsLineDisplayComplete &&
                audio.CurrentVoice?.State == AudioPlaybackState.Playing,
            2);
        Assert(!auto.IsPaused &&
                history.Entries.Count == 2 &&
                history.Entries[1].VoiceId == "m10-voice",
            "M10 did not resume Auto or retain the resolved Voice ID in History.");

        await clock.DelayAsync(TimeSpan.FromSeconds(0.08), FrameTimeMode.Unscaled);
        Assert(dialogue.CurrentLineId == "line:m10-second",
            "M10 advanced before the active Voice completed.");
        audioBackend.Players.Last(player =>
            player.Category == AudioCategory.Voice && player.IsPlaying).CompletePlayback();
        await WaitForConditionAsync(
            () => script.Phase == ScriptPlaybackPhase.PresentingOptions &&
                presentation.IsPresentingOptions,
            2);
        Assert(auto.IsPaused,
            "M10 Auto did not pause while Yarn options were presented.");

        await InjectKeyAsync(Key.Enter);
        await WaitForConditionAsync(
            () => dialogue.CurrentLineId == "line:m10-final" &&
                presentation.IsLineDisplayComplete,
            2);
        Assert(!auto.IsPaused,
            "M10 Auto did not resume after the option was selected.");
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(result.Kind == DialogueCompletionKind.Completed &&
                history.Entries.Select(entry => entry.LineId).SequenceEqual(
                    ["line:m10-second", "line:m10-final"]) &&
                history.Entries[0].VoiceId == "m10-voice" &&
                history.Entries[1].Text == "Final visible line.",
            "M10 History capacity/order or final visible text was incorrect.");

        history.Open();
        var replayButton = historyOverlay.FindChildren(
                "*",
                "Button",
                recursive: true,
                owned: false)
            .OfType<Button>()
            .Single(button => button.TooltipText == "Replay voice: m10-voice");
        replayButton.EmitSignal(BaseButton.SignalName.Pressed);
        await WaitForConditionAsync(
            () => audio.CurrentVoice?.ClipId == "m10-voice",
            2);
        Assert(audio.CurrentVoice?.State == AudioPlaybackState.Playing,
            "M10 default History UI could not replay the retained Voice entry.");
        await bootstrap.ResetRuntimeAsync();
        Assert(history.Entries.Count == 0 &&
                !history.IsOpen &&
                !historyOverlay.Visible &&
                auto.Mode == AutoMode.Manual &&
                !auto.IsPaused &&
                input.ActiveHandleCount == 2 &&
                operations.ActiveOperations.Count == 0,
            "M10 Reset did not clear Session History, restore Manual mode, or rebind input.");

        var overlayNode = historyOverlay;
        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(overlayNode) && !IsInstanceValid(bootstrap),
            "M10 Auto/History Runtime nodes leaked after shutdown.");

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
