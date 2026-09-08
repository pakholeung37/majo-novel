using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Audio.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateAudioRuntimeAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        var loader = new HeadlessAudioClipLoader()
            .Register("calm")
            .Register("battle")
            .Register("hit")
            .Register("en/line-1")
            .Register("en/line-2");
        var bootstrap = new MajoRuntimeBootstrap
        {
            Name = "AudioRuntime",
            Configuration = configuration,
        };
        bootstrap.ConfigureServices = services => services.ReplaceSingleton<IAudioManager>(
            resolver => new AudioManager(
                new GodotAudioBackend(bootstrap),
                loader,
                loader,
                loader,
                new AudioSettings()));
        AddChild(bootstrap);
        await bootstrap.InitializationCompletion;

        var audio = bootstrap.Host!.Services.Get<IAudioManager>();
        var activities = bootstrap.Host.Services.Get<IRuntimeActivityRegistry>();
        var audioHost = bootstrap.GetNode("MajoAudioHost");
        await audio.PlayBgmAsync("calm", "music");
        var crossfade = await audio.PlayBgmAsync(
            "battle",
            "music",
            fade: new AudioFade(0.08));
        Assert(audioHost.GetChildCount() == 2,
            "M9 BGM crossfade did not keep old and new Godot players concurrently.");
        await crossfade.Settlement;
        await WaitForConditionAsync(() => audioHost.GetChildCount() == 1, 2);
        Assert(audioHost.GetChildCount() == 1 &&
                audio.BgmTracks.Single().ClipId == "battle",
            "M9 BGM crossfade did not release the old Godot player.");

        await audio.PlaySfxAsync("hit", "left");
        await audio.PlaySfxAsync("hit", "right");
        Assert(audio.SfxInstances.Count == 2 && audioHost.GetChildCount() == 3,
            "M9 named SFX instances did not overlap in the Godot backend.");

        var firstVoice = await audio.PlayVoiceAsync("line-1");
        var secondVoice = await audio.PlayVoiceAsync("line-2");
        Assert(firstVoice.Playback.State == AudioPlaybackState.Stopped &&
                ReferenceEquals(audio.CurrentVoice, secondVoice.Playback),
            "M9 single-channel Voice did not preempt the previous voice.");

        var cancelled = await audio.PlayBgmAsync(
            "calm",
            "music",
            fade: new AudioFade(1));
        await audio.CancelTransitionsAsync("music");
        try { await cancelled.Settlement; } catch (OperationCanceledException) { }
        Assert(cancelled.Playback.State == AudioPlaybackState.Stopped &&
                audio.BgmTracks.Single().ClipId == "battle",
            "M9 cancel did not restore the pre-crossfade BGM terminal state.");

        var completed = await audio.PlayBgmAsync(
            "calm",
            "music",
            fade: new AudioFade(1));
        await audio.FinishTransitionsAsync("music");
        await completed.Settlement;
        Assert(completed.Playback.State == AudioPlaybackState.Playing &&
                audio.BgmTracks.Single().ClipId == "calm",
            "M9 complete did not force the BGM crossfade target state.");

        await bootstrap.ResetRuntimeAsync();
        await WaitForConditionAsync(() => audioHost.GetChildCount() == 0, 2);
        Assert(audioHost.GetChildCount() == 0 &&
                audio.BgmTracks.Count == 0 &&
                audio.SfxInstances.Count == 0 &&
                audio.CurrentVoice is null &&
                !activities.HasActiveActivities(RuntimeActivityOwner.Audio),
            $"M9 Reset left audio players or Activities alive: nodes=" +
            $"{audioHost.GetChildCount()}, bgm={audio.BgmTracks.Count}, " +
            $"sfx={audio.SfxInstances.Count}, voice={audio.CurrentVoice is not null}, " +
            $"activities={activities.HasActiveActivities(RuntimeActivityOwner.Audio)}.");

        await bootstrap.RequestShutdownAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert(!IsInstanceValid(bootstrap),
            "M9 shutdown left the Runtime or audio host in the scene tree.");
    }

    private sealed class HeadlessAudioClipLoader : IAudioClipLoader
    {
        private readonly Dictionary<string, IAudioClip> clips =
            new(StringComparer.Ordinal);

        public string PathPrefix => "headless/audio";

        public HeadlessAudioClipLoader Register(string clipId)
        {
            clips.Add(clipId, new GodotAudioClip(
                clipId,
                new AudioStreamGenerator
                {
                    MixRate = 22_050,
                    BufferLength = 0.5f,
                }));
            return this;
        }

        public bool Exists(string clipId, string overridePath = null) =>
            clips.ContainsKey(overridePath ?? clipId);

        public Task<IAudioClip> LoadAsync(
            string clipId,
            string overridePath = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return clips.TryGetValue(overridePath ?? clipId, out var clip)
                ? Task.FromResult(clip)
                : Task.FromException<IAudioClip>(
                    new AudioException($"Headless audio clip '{clipId}' is missing."));
        }
    }
}
