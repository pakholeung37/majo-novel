#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Resources;

internal sealed class ResourceGroupLease : IResourceGroupLease
{
    private readonly object gate = new();
    private readonly IRuntimeLogger logger;
    private ResourceLeaseState state = ResourceLeaseState.Loading;

    public ResourceGroupLease(
        string groupId,
        bool isPinned,
        IReadOnlyList<IResourceLease> resources,
        IRuntimeLogger logger)
    {
        GroupId = groupId;
        IsPinned = isPinned;
        Resources = resources;
        this.logger = logger;
        Completion = ObserveAsync();
    }

    public string LeaseId { get; } = Guid.NewGuid().ToString("N");
    public string GroupId { get; }
    public bool IsPinned { get; }
    public ResourceLeaseState State { get { lock (gate) return state; } }
    public double Progress => Resources.Count == 0 ? 1 :
        (double)Resources.Count(resource => resource.State != ResourceLeaseState.Loading) / Resources.Count;
    public IReadOnlyList<IResourceLease> Resources { get; }
    public Task Completion { get; }

    public void Cancel()
    {
        lock (gate)
        {
            if (state is ResourceLeaseState.Released or ResourceLeaseState.Cancelled) return;
            state = ResourceLeaseState.Cancelled;
        }
        foreach (var resource in Resources) resource.Cancel();
    }

    public void Release()
    {
        lock (gate)
        {
            if (state == ResourceLeaseState.Released) return;
            state = ResourceLeaseState.Released;
        }
        foreach (var resource in Resources) resource.Release();
        logger.Debug("resource", "resource.group_released",
            $"Preload group '{GroupId}' released its Runtime retention intent.");
    }

    public void Dispose() => Release();

    private async Task ObserveAsync()
    {
        try
        {
            await Task.WhenAll(Resources.Select(resource => resource.Completion));
            lock (gate) if (state == ResourceLeaseState.Loading) state = ResourceLeaseState.Ready;
            logger.Info("resource", "resource.group_completed", $"Preload group '{GroupId}' completed.");
        }
        catch (OperationCanceledException)
        {
            lock (gate) if (state == ResourceLeaseState.Loading) state = ResourceLeaseState.Cancelled;
            throw;
        }
        catch
        {
            lock (gate) state = ResourceLeaseState.Failed;
            throw;
        }
    }
}
