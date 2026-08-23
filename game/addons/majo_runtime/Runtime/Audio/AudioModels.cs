#nullable enable

using System;
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
    string VoiceLocale = "en");

public readonly record struct AudioFade(
    double DurationSeconds = 0,
    string? OperationId = null,
    string? GroupId = null)
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
    IOperationHandle FadeOperation);

public interface IAudioPlayback
{
    AudioCategory Category { get; }

    string ClipId { get; }

    string InstanceId { get; }

    bool Loop { get; }

    float Volume { get; }

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
