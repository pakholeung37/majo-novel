#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Audio;

internal sealed class AudioTransition : IDisposable
{
    private readonly CancellationTokenSource cts = new();
    private readonly TaskCompletionSource settlementTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDisposable? activityLease;
    private readonly IReadOnlyList<AudioFadeTarget> targets;
    private readonly Action? onComplete;
    private readonly Action? onCancel;
    private int finalized;

    public AudioTransition(
        IReadOnlyList<AudioFadeTarget> targets,
        AudioFade fade,
        IRuntimeActivityRegistry? activities,
        Action? onComplete,
        Action? onCancel)
    {
        this.targets = targets ?? Array.Empty<AudioFadeTarget>();
        this.onComplete = onComplete;
        this.onCancel = onCancel;

        activityLease = activities?.BeginActivity(
            RuntimeActivityOwner.Audio,
            "Fade",
            this.targets.Count > 0 ? this.targets[0].Playback.ClipId : null,
            isCancelable: true,
            isFinishable: true);

        _ = RunFadeAsync(fade);
    }

    public Task Settlement => settlementTcs.Task;

    public event Action? Settled;

    public void Finish() => CompleteOnce(isFinished: true);

    public void Cancel() => CancelOnce();

    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        Finish();
        try
        {
            await Settlement.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        Cancel();
        try
        {
            await Settlement.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunFadeAsync(AudioFade fade)
    {
        try
        {
            await Task.WhenAll(targets.Select(target =>
                target.Playback.Backend.FadeVolumeAsync(
                    target.Volume,
                    TimeSpan.FromSeconds(fade.DurationSeconds),
                    cts.Token)));
            CompleteOnce(isFinished: false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            CancelOnce();
        }
        catch (Exception ex)
        {
            FailOnce(ex);
        }
    }

    private void CompleteOnce(bool isFinished)
    {
        if (Interlocked.Exchange(ref finalized, 1) != 0)
        {
            return;
        }

        try
        {
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        }
        catch
        {
        }

        activityLease?.Dispose();

        if (isFinished)
        {
            foreach (var target in targets)
            {
                target.Playback.Backend.Volume = target.Volume;
            }
        }

        try
        {
            onComplete?.Invoke();
        }
        catch
        {
        }

        try
        {
            Settled?.Invoke();
        }
        catch
        {
        }

        settlementTcs.TrySetResult();
    }

    private void CancelOnce()
    {
        if (Interlocked.Exchange(ref finalized, 1) != 0)
        {
            return;
        }

        try
        {
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        }
        catch
        {
        }

        activityLease?.Dispose();

        try
        {
            onCancel?.Invoke();
        }
        catch
        {
        }

        try
        {
            Settled?.Invoke();
        }
        catch
        {
        }

        settlementTcs.TrySetCanceled();
    }

    private void FailOnce(Exception ex)
    {
        if (Interlocked.Exchange(ref finalized, 1) != 0)
        {
            return;
        }

        activityLease?.Dispose();

        try
        {
            Settled?.Invoke();
        }
        catch
        {
        }

        settlementTcs.TrySetException(ex);
    }

    public void Dispose()
    {
        Cancel();
        cts.Dispose();
    }
}

internal sealed record AudioFadeTarget(AudioPlayback Playback, float Volume);

internal sealed class AudioPlayback : IAudioPlayback
{
    private readonly TaskCompletionSource completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public AudioPlayback(
        AudioCategory category,
        string clipId,
        string instanceId,
        bool looping,
        float baseVolume,
        IAudioBackendPlayer backend)
    {
        Category = category;
        ClipId = clipId;
        InstanceId = instanceId;
        IsLooping = looping;
        BaseVolume = baseVolume;
        Backend = backend;
    }

    public AudioCategory Category { get; }

    public string ClipId { get; }

    public string InstanceId { get; }

    public bool IsLooping { get; set; }

    public float Volume => Backend.Volume;

    public AudioPlaybackState State { get; set; } = AudioPlaybackState.Playing;

    public Task Completion => completion.Task;

    public float BaseVolume { get; set; }

    public IAudioBackendPlayer Backend { get; }

    public void Complete() => completion.TrySetResult();

    public void Fail(Exception exception) => completion.TrySetException(exception);
}
