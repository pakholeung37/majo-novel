#nullable enable

using System;
using Majo.Runtime.Configuration;
using System.Threading.Tasks;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Audio;

public enum AudioCategory
{
    Bgm,
    Sfx,
    Voice,
}

public enum AudioPlaybackState
{
    Playing,
    Stopped,
    Finished,
}

public sealed record AudioSettings(
    float MasterVolume = 1,
    float BgmVolume = 1,
    float SfxVolume = 1,
    float VoiceVolume = 1,
    bool MasterMuted = false,
    bool BgmMuted = false,
    bool SfxMuted = false,
    bool VoiceMuted = false,
    string VoiceLocale = "en",
    bool FailOnMissingClips = false);

public readonly record struct AudioFade(
    double DurationSeconds = RuntimeDefaults.ImmediateDurationSeconds)
{
    public AudioFade Validate()
    {
        if (!double.IsFinite(DurationSeconds) || DurationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DurationSeconds), "Fade duration must be finite and non-negative.");
        }

        return this;
    }
}

public sealed record AudioPlaybackResult(
    IAudioPlayback Playback,
    IOperationHandle FadeOperation,
    bool IsSkipped = false)
{
    internal static AudioPlaybackResult Skipped(
        AudioCategory category,
        string clipId,
        string instanceId) => new(
            new SkippedAudioPlayback(category, clipId, instanceId),
            CompletedAudioOperation.Instance,
            true);
}

internal sealed class SkippedAudioPlayback : IAudioPlayback
{
    public SkippedAudioPlayback(AudioCategory category, string clipId, string instanceId)
    {
        Category = category;
        ClipId = clipId;
        InstanceId = instanceId;
    }

    public AudioCategory Category { get; }

    public string ClipId { get; }

    public string InstanceId { get; }

    public bool IsLooping => false;

    public float Volume => 0;

    public AudioPlaybackState State => AudioPlaybackState.Finished;

    public Task Completion => Task.CompletedTask;
}

internal sealed class CompletedAudioOperation : IOperationHandle
{
    public static CompletedAudioOperation Instance { get; } = new();

    public OperationState State => OperationState.Completed;

    public Task Completion => Task.CompletedTask;

    public void Cancel()
    {
    }

    public void Finish()
    {
    }
}

public interface IAudioPlayback
{
    AudioCategory Category { get; }

    string ClipId { get; }

    string InstanceId { get; }

    bool IsLooping { get; }

    float Volume { get; }

    float BaseVolume => Volume;

    AudioPlaybackState State { get; }

    Task Completion { get; }
}

public class AudioException : Exception
{
    public AudioException(string message)
        : base(message)
    {
    }

    public AudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DuplicateAudioInstanceException : AudioException
{
    public DuplicateAudioInstanceException(string instanceId)
        : base($"Audio instance '{instanceId}' is already playing.")
    {
    }
}
