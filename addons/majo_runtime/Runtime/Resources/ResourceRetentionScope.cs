#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Resources;

/// <summary>Persistent ownership for one render tree; cached objects remain shared across scopes.</summary>
public sealed class ResourceRetentionScope : IDisposable
{
    private readonly IResourceManager resources;
    private readonly object gate = new();
    private readonly Dictionary<(Type Type, ResourceKey Key, string? Override), IResourceLease> leases = new();
    private bool disposed;

    public ResourceRetentionScope(IResourceManager resources) =>
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));

    public async Task<TResource> AcquireAsync<TResource>(ResourceKey key, string? overridePath = null,
        CancellationToken cancellationToken = default) where TResource : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = (typeof(TResource), key, overridePath);
        IResourceLease<TResource> lease;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!leases.TryGetValue(identity, out var existing))
            {
                existing = resources.LeaseAsync<TResource>(key, overridePath, pinned: true, cancellationToken: CancellationToken.None);
                leases.Add(identity, existing);
            }
            lease = (IResourceLease<TResource>)existing;
        }
        try
        {
            await lease.Completion.WaitAsync(cancellationToken);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return lease.Resource ?? throw new InvalidOperationException($"Resource '{key}' completed without a value.");
            }
        }
        catch
        {
            // A caller cancellation must not cancel another consumer of this
            // scope. Failed provider acquisitions may be retried on the next use.
            if (lease.Completion.IsFaulted || lease.Completion.IsCanceled)
            {
                lock (gate)
                {
                    if (leases.TryGetValue(identity, out var current) && ReferenceEquals(current, lease))
                        leases.Remove(identity);
                }
                lease.Release();
            }
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var lease in leases.Values) lease.Release();
            leases.Clear();
        }
    }
}
