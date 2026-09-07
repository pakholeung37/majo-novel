#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Preloading;

/// <summary>One provider acquisition at a time, bounded speculative retention, latest window wins.</summary>
public sealed class ResourceLookAheadWindow : IDisposable
{
    private readonly IResourceManager resources;
    private readonly IRuntimeLogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ResourcePreloadEntry, IResourceGroupLease> held = new();
    private long generation;
    private bool disposed;

    public ResourceLookAheadWindow(IResourceManager resources, IRuntimeLogger? logger = null)
    {
        this.resources = resources;
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public Task MoveAsync(
        IReadOnlyList<ResourcePreloadEntry> required,
        IReadOnlyList<ResourcePreloadEntry> ahead,
        long budgetBytes,
        int maxResources,
        bool waitForAhead = false,
        CancellationToken cancellationToken = default) =>
        MoveCoreAsync(required, ahead, budgetBytes, maxResources, waitForAhead, cancellationToken)
            .WaitAsync(cancellationToken);

    private async Task MoveCoreAsync(IReadOnlyList<ResourcePreloadEntry> required,
        IReadOnlyList<ResourcePreloadEntry> ahead, long budgetBytes, int maxResources,
        bool waitForAhead, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var version = Interlocked.Increment(ref generation);
        var selected = required.Distinct().ToList();
        var bytes = selected.Sum(entry => Math.Max(1, entry.EstimatedBytes));
        foreach (var entry in ahead.Distinct())
        {
            if (selected.Contains(entry)) continue;
            var estimate = Math.Max(1, entry.EstimatedBytes);
            if (selected.Count >= maxResources || estimate > budgetBytes - bytes) continue;
            selected.Add(entry);
            bytes += estimate;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (version != generation || disposed) return;
            foreach (var entry in held.Keys.Except(selected).ToArray())
            {
                held[entry].Release();
                held.Remove(entry);
            }
            if (bytes > budgetBytes)
                logger.Warning("preload", "preload.required_over_budget",
                    $"Required resources exceed the look-ahead budget ({bytes}/{budgetBytes} bytes).");
            foreach (var entry in required.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AcquireAsync(entry, isRequired: true);
                cancellationToken.ThrowIfCancellationRequested();
                if (version != generation || disposed) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var lease in held.Values) lease.Release();
            held.Clear();
            throw;
        }
        finally
        {
            gate.Release();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (waitForAhead) await FillAsync(selected, version, cancellationToken);
        else _ = FillAsync(selected, version, cancellationToken);
    }

    private async Task AcquireAsync(ResourcePreloadEntry entry, bool isRequired = false)
    {
        if (held.ContainsKey(entry))
        {
            if (isRequired) logger.Debug("preload", "preload.required_ready", $"Resource '{entry.Key}' was ready before use.");
            return;
        }
        var lease = resources.PreloadAsync(new ResourcePreloadManifest("yarn-look-ahead", new[] { entry }));
        try
        {
            // Do not cancel the provider wait when a window expires: the shared
            // provider operation may continue anyway. Keep our single-flight gate
            // until it really finishes, then release obsolete retention.
            var waiting = !lease.Completion.IsCompletedSuccessfully;
            var started = Stopwatch.GetTimestamp();
            if (isRequired)
                logger.Debug("preload", waiting ? "preload.required_wait" : "preload.required_ready",
                    $"Resource '{entry.Key}' {(waiting ? "requires a load before use" : "was ready before use")}.");
            await lease.Completion;
            if (isRequired && waiting)
                logger.Debug("preload", "preload.required_wait_completed",
                    $"Resource '{entry.Key}' became ready after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
            if (disposed) lease.Release();
            else held.Add(entry, lease);
        }
        catch
        {
            lease.Release();
            throw;
        }
    }

    private async Task FillAsync(IReadOnlyList<ResourcePreloadEntry> entries, long version, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var entry in entries)
            {
                if (version != generation || disposed || cancellationToken.IsCancellationRequested) break;
                try { await AcquireAsync(entry); }
                catch (Exception exception)
                {
                    logger.Warning("preload", "preload.look_ahead_failed",
                        $"Could not prepare '{entry.Key}': {exception.Message}");
                }
            }
            if (cancellationToken.IsCancellationRequested && version == generation)
            {
                foreach (var lease in held.Values) lease.Release();
                held.Clear();
            }
        }
        finally { gate.Release(); }
    }

    public void Clear()
    {
        Interlocked.Increment(ref generation);
        _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var lease in held.Values) lease.Release();
            held.Clear();
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Clear();
        // Pending provider completions still use the gate to release their leases.
    }
}
