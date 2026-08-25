#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Majo.Runtime.Resources;

public enum ResourceLeaseState
{
    Loading,
    Ready,
    Failed,
    Cancelled,
    Released,
}

public enum ResourceLocaleChannel
{
    None,
    Dialogue,
    Voice,
}

public sealed record ResourcePreloadContext(
    string DialogueLocale,
    string VoiceLocale,
    IReadOnlyList<string> DialogueFallbackChain,
    IReadOnlyList<string> VoiceFallbackChain)
{
    public static ResourcePreloadContext Unlocalized { get; } = new(
        string.Empty,
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<string>());
}

public sealed record ResourcePreloadEntry(
    ResourceKey Key,
    Type ResourceType,
    string? OverridePath = null,
    long EstimatedBytes = 0,
    string? LocalizedRoot = null,
    ResourceLocaleChannel LocaleChannel = ResourceLocaleChannel.None)
{
    public static ResourcePreloadEntry Create<TResource>(
        ResourceKey key,
        string? overridePath = null,
        long estimatedBytes = 0,
        string? localizedRoot = null,
        ResourceLocaleChannel localeChannel = ResourceLocaleChannel.None)
        where TResource : class => new(
            key,
            typeof(TResource),
            overridePath,
            estimatedBytes,
            localizedRoot,
            localeChannel);

    public ResourcePreloadEntry Validate()
    {
        ArgumentNullException.ThrowIfNull(ResourceType);
        if (!ResourceType.IsClass)
        {
            throw new ArgumentException("Preloaded resource types must be reference types.");
        }
        if (EstimatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EstimatedBytes), "Estimated bytes cannot be negative.");
        }
        if (LocaleChannel != ResourceLocaleChannel.None &&
            string.IsNullOrWhiteSpace(LocalizedRoot))
        {
            throw new ArgumentException(
                "Localized preload entries require a localized root.");
        }
        return this;
    }
}

public sealed record ResourcePreloadManifest
{
    public ResourcePreloadManifest(
        string id,
        IEnumerable<ResourcePreloadEntry> entries,
        bool pinned = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(entries);
        Id = id;
        Entries = entries.Select(entry => entry.Validate()).ToArray();
        if (Entries.Count == 0)
        {
            throw new ArgumentException(
                "A preload manifest requires at least one entry.", nameof(entries));
        }
        Pinned = pinned;
    }

    public string Id { get; }

    public IReadOnlyList<ResourcePreloadEntry> Entries { get; }

    public bool Pinned { get; }
}

public interface IResourceLease : IDisposable
{
    string LeaseId { get; }

    ResourceKey LogicalKey { get; }

    string? ResolvedPath { get; }

    Type ResourceType { get; }

    ResourceLeaseState State { get; }

    object? Resource { get; }

    Exception? Failure { get; }

    Task Completion { get; }

    void Cancel();

    void Release();
}

public interface IResourceLease<out TResource> : IResourceLease
    where TResource : class
{
    new TResource? Resource { get; }
}

public interface IResourceGroupLease : IDisposable
{
    string LeaseId { get; }

    string GroupId { get; }

    bool IsPinned { get; }

    ResourceLeaseState State { get; }

    double Progress { get; }

    IReadOnlyList<IResourceLease> Resources { get; }

    Task Completion { get; }

    void Cancel();

    void Release();
}

public sealed record ResourceCacheEntrySnapshot(
    string ResourceType,
    string ProviderId,
    string ResolvedPath,
    long EstimatedBytes,
    int LeaseCount,
    int PinnedLeaseCount,
    DateTimeOffset LastAccessedAt);

public sealed record ResourceCacheSnapshot(
    int LoadedCount,
    int PendingCount,
    int ActiveLeaseCount,
    long EstimatedBytes,
    long BudgetBytes,
    long CacheHits,
    long CacheMisses,
    long EvictionCount,
    IReadOnlyList<ResourceCacheEntrySnapshot> Entries)
{
    public bool IsOverBudget => BudgetBytes > 0 && EstimatedBytes > BudgetBytes;
}
