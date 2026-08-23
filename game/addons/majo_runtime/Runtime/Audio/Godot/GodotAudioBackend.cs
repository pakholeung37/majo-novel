#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Audio.Godot;

public sealed class GodotAudioBackend : IAudioBackend
{
    private readonly Node owner;
    private readonly HashSet<GodotAudioBackendPlayer> players = new();
    private Node? host;

    public GodotAudioBackend(Node owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public IReadOnlyCollection<IAudioBackendPlayer> ActivePlayers =>
        players.Cast<IAudioBackendPlayer>().ToArray();

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is not null)
        {
            return Task.CompletedTask;
        }

        host = new Node { Name = "MajoAudioHost" };
        owner.AddChild(host);
        return Task.CompletedTask;
    }

    public IAudioBackendPlayer CreatePlayer(
        AudioCategory category,
        IAudioClip clip,
        string instanceId)
    {
        if (host is null)
        {
            throw new AudioException("Godot audio backend is not initialized.");
        }

        if (clip is not GodotAudioClip godotClip)
        {
            throw new AudioException(
                $"Godot audio backend cannot play clip type '{clip.GetType().FullName}'.");
        }

        var player = new GodotAudioBackendPlayer(
            host,
            category,
            godotClip,
            instanceId,
            RemovePlayer);
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
        if (GodotObject.IsInstanceValid(host))
        {
            host!.QueueFree();
        }

        host = null;
        return Task.CompletedTask;
    }

    private void StopAll()
    {
        foreach (var player in players.ToArray())
        {
            player.Dispose();
        }

        players.Clear();
    }

    private void RemovePlayer(GodotAudioBackendPlayer player) => players.Remove(player);
}

internal sealed class GodotAudioBackendPlayer : IAudioBackendPlayer
{
    private readonly AudioStreamPlayer player;
    private readonly Action<GodotAudioBackendPlayer> released;
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Tween? fadeTween;
    private TaskCompletionSource? fadeCompletion;
    private bool isDisposed;
    private bool loop;
    private float volume = 1;

    public GodotAudioBackendPlayer(
        Node host,
        AudioCategory category,
        GodotAudioClip clip,
        string instanceId,
        Action<GodotAudioBackendPlayer> released)
    {
        this.released = released;
        InstanceId = instanceId;
        player = new AudioStreamPlayer
        {
            Name = $"{category}_{SanitizeNodeName(instanceId)}",
            Stream = clip.Stream,
        };
        player.Finished += HandleFinished;
        host.AddChild(player);
    }

    public string InstanceId { get; }

    public bool Loop
    {
        get => loop;
        set => loop = value;
    }

    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0, 1);
            if (GodotObject.IsInstanceValid(player))
            {
                player.VolumeDb = volume <= 0 ? -80 : Mathf.LinearToDb(volume);
            }
        }
    }

    public bool IsPlaying => !isDisposed && player.Playing;

    public Task Completion => completion.Task;

    public void Play()
    {
        ThrowIfDisposed();
        player.Play();
    }

    public void Stop()
    {
        if (isDisposed)
        {
            return;
        }

        player.Stop();
        Release();
    }

    public async Task FadeVolumeAsync(
        float targetVolume,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        targetVolume = Math.Clamp(targetVolume, 0, 1);
        if (duration <= TimeSpan.Zero)
        {
            Volume = targetVolume;
            return;
        }

        KillFade();
        var targetDb = targetVolume <= 0 ? -80 : Mathf.LinearToDb(targetVolume);
        fadeTween = player.CreateTween();
        fadeTween.SetTrans(Tween.TransitionType.Linear);
        fadeTween.SetEase(Tween.EaseType.InOut);
        fadeTween.TweenProperty(player, "volume_db", targetDb, duration.TotalSeconds);
        var finished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fadeCompletion = finished;
        void OnFinished() => finished.TrySetResult();
        fadeTween.Finished += OnFinished;
        using var registration = cancellationToken.Register(() =>
        {
            KillFade();
            finished.TrySetCanceled(cancellationToken);
        });
        try
        {
            await finished.Task;
            volume = targetVolume;
        }
        finally
        {
            if (GodotObject.IsInstanceValid(fadeTween))
            {
                fadeTween!.Finished -= OnFinished;
            }

            if (ReferenceEquals(fadeCompletion, finished))
            {
                fadeCompletion = null;
                fadeTween = null;
            }
        }
    }

    public void Dispose() => Stop();

    private void HandleFinished()
    {
        if (loop && !isDisposed)
        {
            player.Play();
            return;
        }

        Release(natural: true);
    }

    private void Release(bool natural = false)
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        KillFade();
        fadeCompletion?.TrySetResult();
        player.Finished -= HandleFinished;
        if (GodotObject.IsInstanceValid(player))
        {
            // Release the native playback/stream reference synchronously. Waiting
            // for QueueFree alone can leave a WAV playback alive when the scene
            // exits in the same frame as natural audio completion.
            player.Stream = null;
            player.QueueFree();
        }

        released(this);
        completion.TrySetResult();
    }

    private void KillFade()
    {
        if (GodotObject.IsInstanceValid(fadeTween))
        {
            fadeTween!.Kill();
        }
    }

    private void ThrowIfDisposed()
    {
        if (isDisposed)
        {
            throw new ObjectDisposedException(nameof(GodotAudioBackendPlayer));
        }
    }

    private static string SanitizeNodeName(string value) => value
        .Replace('/', '_')
        .Replace(':', '_')
        .Replace('@', '_');
}
