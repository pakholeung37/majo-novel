#nullable enable

using System;
using Godot;
using Majo.Demos.SGRE.Emote;
using Majo.Runtime;
using Majo.Runtime.Audio;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;

namespace Majo.Demos.SGRE;

public partial class SGREDemoController : Node
{
    public override void _Ready() => RunAsync();

    private async void RunAsync()
    {
        var automatic = Array.IndexOf(OS.GetCmdlineUserArgs(), "--sgre-auto") >= 0;
        var recording = Array.IndexOf(OS.GetCmdlineUserArgs(), "--sgre-record") >= 0;
        try
        {
            var settings = MajoProjectConfiguration.CreateDefault();
            settings.Resources!.RootPath = "res://demos/sgre/assets";
            settings.Localization!.DefaultLocale = "zh-Hans";
            settings.Localization.SupportedLocales = ["zh-Hans"];
            settings.Settings!.DialogueLocale = "zh-Hans";
            settings.State!.StorageNamespace = "majo/sgre-demo";
            settings.State.GameBuildId = "sgre-demo-v1";
            settings.Save!.Enabled = true;
            settings.Presentation!.LettersPerSecond = automatic ? 240 : 28;
            settings.Presentation.PresenterScene = ResourceLoader.Load<PackedScene>(
                "res://demos/sgre/SGREPresentation.tscn") ??
                throw new InvalidOperationException("SGRE presentation scene is missing.");
            settings.Diagnostics!.EnableOverlay = !automatic;
            settings.Diagnostics.defaultOverlayVisible = false;
            settings.Diagnostics.ShowFpsOverlay = false;
            settings.Animation!.DefaultCharacterAppearanceAnimationId = "cut";
            settings.Actors!.ReferenceResolution = new Vector2I(1920, 1080);
            settings.Actors.CenterPosition = new Vector2(1100, 1120);

            var registry = new EmoteRegistry();
            var probe = automatic ? new SGREDialogueProbe
            {
                Capture = Array.IndexOf(OS.GetCmdlineUserArgs(), "--sgre-capture") >= 0,
            } : null;
            if (probe is not null) AddChild(probe);
            var recorder = recording ? new SGRERecordingPresenter() : null;
            if (recorder is not null) AddChild(recorder);
            var runtime = new MajoRuntimeBootstrap
            {
                Name = "SGRERuntime",
                Configuration = settings,
                ConfigureDialoguePresenters = presenters =>
                {
                    if (probe is not null) presenters.Register(probe);
                    if (recorder is not null) presenters.Register(recorder);
                },
            };
            runtime.AddPlugin(new EmoteCharacterPlugin(registry));
            AddChild(runtime);
            await runtime.InitializationCompletion;
            var audio = runtime.Host!.Services.Get<IAudioManager>();
            void VoiceStarted(IAudioPlayback playback)
            {
                if (playback.Category == AudioCategory.Voice && playback.ClipId.Contains("_krs", StringComparison.OrdinalIgnoreCase))
                    registry.SetTalking("红莉栖", true);
            }
            void VoiceStopped(IAudioPlayback playback)
            {
                if (playback.Category == AudioCategory.Voice && playback.ClipId.Contains("_krs", StringComparison.OrdinalIgnoreCase))
                    registry.SetTalking("红莉栖", false);
            }
            audio.PlaybackStarted += VoiceStarted;
            audio.PlaybackStopped += VoiceStopped;
            var dialogue = runtime.Host.Services.Get<IDialoguePlayer>();
            await dialogue.StartAsync();
            if (automatic || recording)
            {
                var result = await dialogue.Completion;
                var lines = recording ? recorder!.LineCount : probe!.LineCount;
                if (result.Kind != DialogueCompletionKind.Completed || lines != 18)
                    throw new InvalidOperationException($"SGRE demo did not complete: {result.Kind}, {lines} lines.");
                var actor = registry.Find("红莉栖") ?? throw new InvalidOperationException("The live E-mote actor was not created.");
                if (actor.Rig.EvaluatedFrames < 10 || actor.Rig.LastPartCount == 0 || actor.Rig.ChangedFrames < 10 ||
                    actor.Rig.MotionCount < 3 || actor.Rig.TalkingStarts == 0 || actor.Rig.LastMaskCount == 0)
                    throw new InvalidOperationException("The E-mote rig did not evaluate the expected live animation, masks, motions and speech.");
                GD.Print($"SGRE_DEMO_OK: {lines} lines; E-mote {actor.Rig.EvaluatedFrames} frames, " +
                    $"{actor.Rig.ChangedFrames} changes, {actor.Rig.MotionCount} motions, {actor.Rig.TalkingStarts} voice starts, " +
                    $"{actor.Rig.LastPartCount} parts, {actor.Rig.LastMaskCount} masks, " +
                    $"{actor.Rig.EvaluatedFps:F1} fps, {actor.Rig.AverageEvaluationMilliseconds:F1} ms/frame CPU.");
                if (recording) await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
                audio.PlaybackStarted -= VoiceStarted;
                audio.PlaybackStopped -= VoiceStopped;
                await runtime.RequestShutdownAsync();
                probe?.QueueFree();
                recorder?.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                GetTree().Quit(0);
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"SGRE demo failed: {exception}");
            if (automatic || recording) GetTree().Quit(1);
        }
    }
}
