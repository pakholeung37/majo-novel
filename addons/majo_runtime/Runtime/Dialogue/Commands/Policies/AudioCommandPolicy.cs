#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Commands;

public enum AudioCommandKind
{
    Bgm,
    Sfx,
    Voice,
}

public sealed record AudioFadeDefaults(
    double DefaultAudioFadeDurationSeconds,
    double DefaultVoiceFadeDurationSeconds)
{
    public static AudioFadeDefaults FromConfiguration(AnimationConfiguration? animation)
    {
        if (animation is null)
        {
            return new AudioFadeDefaults(
                RuntimeDefaults.AnimationDurationSeconds,
                RuntimeDefaults.ImmediateDurationSeconds);
        }

        return new AudioFadeDefaults(
            animation.DefaultAudioFadeDurationSeconds,
            animation.DefaultVoiceFadeDurationSeconds);
    }
}

public interface IAudioCommandPolicy : IRuntimeService
{
    double ResolveFadeDuration(
        AudioFadeArguments input,
        AudioCommandKind kind,
        CommandExecutionContext context);
}

public sealed class AudioCommandPolicy : IAudioCommandPolicy
{
    private readonly AudioFadeDefaults defaults;

    public AudioCommandPolicy(AudioFadeDefaults defaults)
    {
        this.defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public double ResolveFadeDuration(
        AudioFadeArguments input,
        AudioCommandKind kind,
        CommandExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Duration.WasProvided)
        {
            return input.Duration.Value;
        }

        return kind switch
        {
            AudioCommandKind.Voice => defaults.DefaultVoiceFadeDurationSeconds,
            _ => defaults.DefaultAudioFadeDurationSeconds,
        };
    }
}
