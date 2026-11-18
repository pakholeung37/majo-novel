#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

// Owns native subscriptions and asynchronous scans; stopping it drains both before returning.
internal sealed class YarnSourceMonitor : IAsyncDisposable, IDisposable
{
    private const string Category = "script.hot_reload";
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken token;
    private readonly FileSystemWatcher watcher;
    private readonly Timer timer;
    private readonly Func<CancellationToken, Task<string>> readFingerprint;
    private readonly Action<string> establishBaseline;
    private readonly Action changed;
    private readonly IRuntimeLogger logger;
    private string? fingerprint;
    private long revision;
    private bool disposed;
    private Task scan = Task.CompletedTask;
    private Task? stop;
    private int notifications;
    private TaskCompletionSource? notificationsDrained;

    public YarnSourceMonitor(string root, int? consistencyMilliseconds,
        Func<CancellationToken, Task<string>> readFingerprint, Action<string> establishBaseline,
        Action changed, IRuntimeLogger logger)
    {
        if (consistencyMilliseconds is { } interval) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        this.readFingerprint = readFingerprint;
        this.establishBaseline = establishBaseline;
        this.changed = changed;
        this.logger = logger;
        token = lifetime.Token;
        watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
        };
        timer = new Timer(_ => _ = ScanAsync(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            watcher.Changed += HandleSourceEvent;
            watcher.Created += HandleSourceEvent;
            watcher.Deleted += HandleSourceEvent;
            watcher.Renamed += HandleSourceEvent;
            watcher.Error += HandleError;
            watcher.EnableRaisingEvents = true;
            if (consistencyMilliseconds is { } period) timer.Change(period, period);
        }
        catch
        {
            timer.Dispose();
            watcher.Dispose();
            lifetime.Dispose();
            throw;
        }
    }

    public void AcceptFingerprint(string value)
    {
        lock (gate)
        {
            if (disposed) return;
            fingerprint = value;
            revision++; // A compile or reset supersedes a scan still reading old files.
        }
    }
    public void ResetBaseline()
    {
        lock (gate)
        {
            if (disposed) return;
            fingerprint = null;
            revision++;
        }
    }

    public Task ScanAsync()
    {
        TaskCompletionSource completion;
        long currentRevision;
        lock (gate)
        {
            if (disposed) return Task.CompletedTask;
            if (!scan.IsCompleted) return scan;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scan = completion.Task;
            currentRevision = revision;
        }
        _ = ScanCoreAsync(completion, currentRevision);
        return completion.Task;
    }

    private async Task ScanCoreAsync(TaskCompletionSource completion, long currentRevision)
    {
        try
        {
            var current = await readFingerprint(token).ConfigureAwait(false);
            bool baseline;
            bool different;
            lock (gate)
            {
                if (disposed || revision != currentRevision) return;
                baseline = fingerprint is null;
                different = !string.Equals(fingerprint, current, StringComparison.Ordinal);
                fingerprint = current;
                revision++;
            }
            if (baseline) Notify(() => establishBaseline(current));
            else if (different) Notify(changed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.Warning(Category, "script.hot_reload.consistency_scan_failed", "Yarn consistency scan failed.", exception);
        }
        finally { completion.TrySetResult(); }
    }

    private void HandleSourceEvent(object sender, FileSystemEventArgs args)
    {
        if (IsYarnPath(args.FullPath)) Notify(changed);
    }
    private void HandleError(object sender, ErrorEventArgs args) => Notify(() =>
    {
        logger.Warning(Category, "script.hot_reload.watcher_overflow",
            "The Yarn source watcher lost events; a consistency scan was scheduled.", args.GetException());
        changed();
    });

    private void Notify(Action notification)
    {
        lock (gate)
        {
            if (disposed) return;
            if (notifications++ == 0) notificationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { notification(); }
        finally
        {
            lock (gate)
                if (--notifications == 0) notificationsDrained!.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task pendingScan;
        Task pendingNotifications;
        lock (gate)
        {
            if (stop is not null) return new ValueTask(stop);
            disposed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stop = completion.Task;
            pendingScan = scan;
            pendingNotifications = notificationsDrained?.Task ?? Task.CompletedTask;
        }
        _ = StopCoreAsync(completion, pendingScan, pendingNotifications);
        return new ValueTask(completion.Task);
    }
    private async Task StopCoreAsync(TaskCompletionSource completion, Task pendingScan, Task pendingNotifications)
    {
        var errors = new List<Exception>();
        try { lifetime.Cancel(); }
        catch (Exception exception) { errors.Add(exception); }
        try { watcher.Dispose(); }
        catch (Exception exception) { errors.Add(exception); }
        try { await timer.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { errors.Add(exception); }
        try { await Task.WhenAll(pendingScan, pendingNotifications).ConfigureAwait(false); }
        catch (Exception exception) { errors.Add(exception); }
        try { lifetime.Dispose(); }
        catch (Exception exception) { errors.Add(exception); }
        if (errors.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Yarn source monitor cleanup failed.", errors));
    }
    public void Dispose() => _ = ObserveStopAsync();
    private async Task ObserveStopAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            logger.Warning(Category, "script.hot_reload.monitor_stop_failed", "Yarn source monitor cleanup failed.", exception);
        }
    }
    private static bool IsYarnPath(string path) =>
        path.EndsWith(".yarn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(YarnSpinnerGodot.YarnProject.YARN_PROJECT_EXTENSION, StringComparison.OrdinalIgnoreCase);
}
