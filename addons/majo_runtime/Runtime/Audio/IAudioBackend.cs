#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Audio;

public interface IAudioClip
{
    string Id { get; }

    TimeSpan? Duration { get; }
}

public interface IAudioClipLoader
{
    string PathPrefix { get; }

    bool Exists(string clipId, string? overridePath = null);

    Task<IAudioClip> LoadAsync(
        string clipId,
        string? overridePath = null,
        CancellationToken cancellationToken = default);
}

public interface IAudioBackendPlayer : IDisposable
{
    string InstanceId { get; }

    bool IsLooping { get; set; }

    float Volume { get; set; }

    bool IsPlaying { get; }

    Task Completion { get; }

    void Play();

    void StopPlayback();

    Task FadeVolumeAsync(
        float targetVolume,
        TimeSpan duration,
        CancellationToken cancellationToken);
}

public interface IAudioBackend
{
    IReadOnlyCollection<IAudioBackendPlayer> ActivePlayers { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    IAudioBackendPlayer CreatePlayer(
        AudioCategory category,
        IAudioClip clip,
        string instanceId);

    Task ResetAsync(CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken);
}
