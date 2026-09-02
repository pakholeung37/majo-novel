#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Audio;

public sealed record FakeAudioClip(string Id, TimeSpan? Duration = null) : IAudioClip;

public sealed class FakeAudioClipLoader : IAudioClipLoader
{
    private readonly Dictionary<string, IAudioClip> clips = new(StringComparer.Ordinal);

    public FakeAudioClipLoader(string pathPrefix)
    {
        PathPrefix = pathPrefix;
    }

    public string PathPrefix { get; }

    public FakeAudioClipLoader Register(string clipId, TimeSpan? duration = null)
    {
        clips[clipId] = new FakeAudioClip(clipId, duration);
        return this;
    }

    public bool Exists(string clipId, string? overridePath = null) =>
        clips.ContainsKey(overridePath ?? clipId);

    public Task<IAudioClip> LoadAsync(
        string clipId,
        string? overridePath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = overridePath ?? clipId;
        return clips.TryGetValue(key, out var clip)
            ? Task.FromResult(clip)
            : Task.FromException<IAudioClip>(
                new AudioException($"Fake audio clip '{key}' does not exist."));
    }
}

public sealed class FakeAudioBackend : IAudioBackend
{
    private readonly List<FakeAudioBackendPlayer> players = new();
    private bool isInitialized;

    public bool AutoCompleteFades { get; set; } = true;

    public IReadOnlyCollection<IAudioBackendPlayer> ActivePlayers =>
        players.Where(player => player.IsPlaying).Cast<IAudioBackendPlayer>().ToArray();

    public IReadOnlyList<FakeAudioBackendPlayer> Players => players;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public IAudioBackendPlayer CreatePlayer(
        AudioCategory category,
        IAudioClip clip,
        string instanceId)
    {
        if (!isInitialized)
        {
            throw new AudioException("Fake audio backend is not initialized.");
        }

        var player = new FakeAudioBackendPlayer(
            category,
            clip,
            instanceId,
            () => AutoCompleteFades);
        players.Add(player);
        return player;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopAll();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopAll();
        isInitialized = false;
        return Task.CompletedTask;
    }

    public void CompleteFades()
    {
        foreach (var player in players)
        {
            player.CompleteFades();
        }
    }

    private void StopAll()
    {
        foreach (var player in players)
        {
            player.StopPlayback();
        }
    }
}

public sealed class FakeAudioBackendPlayer : IAudioBackendPlayer
{
    private readonly Func<bool> autoCompleteFades;
    private readonly List<PendingFade> fades = new();
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool isDisposed;

    internal FakeAudioBackendPlayer(
        AudioCategory category,
        IAudioClip clip,
        string instanceId,
        Func<bool> autoCompleteFades)
    {
        Category = category;
        Clip = clip;
        InstanceId = instanceId;
        this.autoCompleteFades = autoCompleteFades;
    }

    public AudioCategory Category { get; }

    public IAudioClip Clip { get; }

    public string InstanceId { get; }

    public bool IsLooping { get; set; }

    public float Volume { get; set; } = 1;

    public bool IsPlaying { get; private set; }

    public Task Completion => completion.Task;

    public void Play()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        IsPlaying = true;
    }

    public void StopPlayback()
    {
        if (isDisposed)
        {
            return;
        }

        IsPlaying = false;
        isDisposed = true;
        CancelFades();
        completion.TrySetResult();
    }

    public Task FadeVolumeAsync(
        float targetVolume,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero || autoCompleteFades())
        {
            Volume = targetVolume;
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new PendingFade(targetVolume, completion);
        fade.Registration = cancellationToken.Register(() =>
        {
            fades.Remove(fade);
            completion.TrySetCanceled(cancellationToken);
        });
        fades.Add(fade);
        return completion.Task;
    }

    public void CompletePlayback()
    {
        if (!IsLooping)
        {
            CompleteFades();
            IsPlaying = false;
            isDisposed = true;
            completion.TrySetResult();
        }
    }

    public void CompleteFades()
    {
        foreach (var fade in fades.ToArray())
        {
            Volume = fade.Target;
            fade.Registration.Dispose();
            fade.Completion.TrySetResult();
            fades.Remove(fade);
        }
    }

    public void Dispose() => StopPlayback();

    private void CancelFades()
    {
        foreach (var fade in fades.ToArray())
        {
            fade.Registration.Dispose();
            fade.Completion.TrySetCanceled();
            fades.Remove(fade);
        }
    }

    private sealed class PendingFade
    {
        public PendingFade(float target, TaskCompletionSource completion)
        {
            Target = target;
            Completion = completion;
        }

        public float Target { get; }

        public TaskCompletionSource Completion { get; }

        public CancellationTokenRegistration Registration { get; set; }
    }
}
