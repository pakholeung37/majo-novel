using Majo.Runtime.Core;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Resources.Godot;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ResourceManagerTests
{
    [Fact(Timeout = 5000)]
    public async Task RegisteredResourcesSurviveEvictionAndResetButCannotBeRemovedWhileLeased()
    {
        using var lifetime = new TestRuntimeLifetime();
        var manager = new ResourceManager([], lifetime, releaseOnLastLease: true);
        await manager.InitializeAsync(default);
        var key = new ResourceKey("backgrounds/generated");
        var asset = new TestAsset("generated");
        var released = 0;
        manager.Register(key, asset, _ => released++);
        Assert.Throws<MajoResourceException>(() => manager.Register(key, asset));
        var lease = manager.LeaseAsync<object>(key);
        await lease.Completion;
        Assert.Same(asset, lease.Resource);
        Assert.Throws<MajoResourceException>(() => manager.Unregister(key));
        lease.Release();
        Assert.Equal(0, manager.LoadedResourceCount);
        Assert.Same(asset, await manager.LoadAsync<TestAsset>(key));
        await manager.ResetAsync(default);
        Assert.Same(asset, manager.Load<TestAsset>(key));
        Assert.Equal(0, released);
        Assert.True(manager.Unregister(key));
        Assert.False(manager.Exists<TestAsset>(key));
        Assert.False(manager.Unregister(key));
        Assert.Equal(1, released);
        manager.Register(key, asset, _ => released++);
        await manager.ShutdownAsync(default);
        Assert.Equal(2, released);
    }

    [Fact(Timeout = 5_000)]
    public async Task OwnershipModeEvictsOnlyAfterLastSharedLeaseWithoutByteBudget()
    {
        using var lifetime = new TestRuntimeLifetime();
        var provider = new MemoryProvider("memory").Add("memory://atlas", new TestAsset("atlas"));
        var manager = new ResourceManager([provider], lifetime, releaseOnLastLease: true);
        await manager.InitializeAsync(default);
        var first = manager.LeaseAsync<TestAsset>(new ResourceKey("atlas"), estimatedBytes: 1_000_000_000, pinned: true);
        await first.Completion;
        var second = manager.LeaseAsync<TestAsset>(new ResourceKey("atlas"), pinned: true);
        await second.Completion;
        first.Release();
        Assert.Equal(1, manager.LoadedResourceCount);
        Assert.False(manager.CaptureCacheSnapshot().IsOverBudget);
        second.Release();
        Assert.Equal(0, manager.LoadedResourceCount);
        Assert.Equal(1, provider.AsyncLoadCount);
        await manager.ShutdownAsync(default);
    }

    [Fact(Timeout = 5_000)]
    public async Task BudgetPressureReportsGrowthAndRecoveryWithoutLeaseChurnSpam()
    {
        using var lifetime = new TestRuntimeLifetime();
        var logger = new PressureLogger();
        var provider = new MemoryProvider("memory")
            .Add("memory://atlas", new TestAsset("atlas"))
            .Add("memory://extra", new TestAsset("extra"));
        var manager = new ResourceManager([provider], lifetime, logger, memoryBudgetBytes: 100);
        await manager.InitializeAsync(default);
        var owner = manager.LeaseAsync<TestAsset>(new ResourceKey("atlas"), estimatedBytes: 120, pinned: true);
        await owner.Completion;
        for (var i = 0; i < 17; i++)
        {
            var replacement = manager.LeaseAsync<TestAsset>(new ResourceKey("atlas"), estimatedBytes: 120, pinned: true);
            await replacement.Completion;
            replacement.Release();
        }
        Assert.Single(logger.Messages);
        Assert.Contains("memory://atlas", logger.Messages[0]);
        var extra = manager.LeaseAsync<TestAsset>(new ResourceKey("extra"), estimatedBytes: 20);
        await extra.Completion;
        Assert.Equal(2, logger.Messages.Count);
        extra.Release();
        owner.Release();
        Assert.False(manager.CaptureCacheSnapshot().IsOverBudget);
        var again = manager.LeaseAsync<TestAsset>(new ResourceKey("atlas"), estimatedBytes: 120);
        await again.Completion;
        Assert.Equal(3, logger.Messages.Count);
        again.Release();
        await manager.ShutdownAsync(default);
    }

    private sealed class PressureLogger : IRuntimeLogger
    {
        public List<string> Messages { get; } = new();
        public bool IsEnabled(string category, RuntimeLogLevel level) => true;
        public void Log(RuntimeLogEvent entry)
        {
            if (entry.EventId == "resource.budget_pressure") Messages.Add(entry.Message);
        }
    }

    [Fact(Timeout = 5_000)]
    public async Task CancellingOneTreeAcquisitionDoesNotCancelSharedProviderWork()
    {
        using var lifetime = new TestRuntimeLifetime();
        var asset = new TestAsset("atlas");
        var provider = new MemoryProvider("memory").Add("memory://atlas", asset).PauseAsyncLoads();
        var manager = new ResourceManager([provider], lifetime);
        await manager.InitializeAsync(CancellationToken.None);
        using var cancelledTree = new ResourceRetentionScope(manager);
        using var survivingTree = new ResourceRetentionScope(manager);
        using var cancellation = new CancellationTokenSource();
        var key = new ResourceKey("atlas");
        var first = cancelledTree.AcquireAsync<TestAsset>(key, cancellationToken: cancellation.Token);
        var second = survivingTree.AcquireAsync<TestAsset>(key);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        cancelledTree.Dispose();
        provider.ReleaseAsyncLoads();
        Assert.Same(asset, await second);
        Assert.Equal(1, provider.AsyncLoadCount);
        Assert.Equal(1, manager.CaptureCacheSnapshot().Entries.Single().PinnedLeaseCount);
        survivingTree.Dispose();
        Assert.Equal(0, manager.CaptureCacheSnapshot().ActiveLeaseCount);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task RenderTreesShareResourceAndKeepItPinnedUntilLastOwnerExits()
    {
        using var lifetime = new TestRuntimeLifetime();
        var asset = new TestAsset("atlas");
        var provider = new MemoryProvider("memory").Add("memory://atlas", asset);
        var manager = new ResourceManager([provider], lifetime, memoryBudgetBytes: 1, defaultResourceEstimateBytes: 100);
        await manager.InitializeAsync(CancellationToken.None);
        using var current = new ResourceRetentionScope(manager);
        using var candidate = new ResourceRetentionScope(manager);
        var key = new ResourceKey("atlas");
        Assert.Same(asset, await current.AcquireAsync<TestAsset>(key));
        Assert.Same(asset, await current.AcquireAsync<TestAsset>(key));
        Assert.Same(asset, await candidate.AcquireAsync<TestAsset>(key));
        Assert.Equal(1, provider.AsyncLoadCount);
        Assert.Equal(2, manager.CaptureCacheSnapshot().Entries.Single().PinnedLeaseCount);
        Assert.Equal(100, manager.CaptureCacheSnapshot().EstimatedBytes);
        Assert.True(manager.IsPinned<TestAsset>(key));
        Assert.Equal(0, manager.TrimCache());
        candidate.Dispose(); // rollback candidate
        Assert.True(manager.IsPinned<TestAsset>(key));
        Assert.Same(asset, await manager.LoadAsync<TestAsset>(key));
        current.Dispose();
        Assert.False(manager.IsPinned<TestAsset>(key));
        Assert.Equal(0, manager.CaptureCacheSnapshot().ActiveLeaseCount);
        Assert.Equal(0, manager.LoadedResourceCount);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public void ResourceKeyNormalizesSeparatorsAndRejectsPhysicalPaths()
    {
        Assert.Equal("characters/hero/calm", new ResourceKey("/characters\\hero/calm/").Path);
        Assert.Equal(
            "characters/hero/calm",
            ResourceKey.Combine("characters", "hero", "calm").Path);
        Assert.Throws<ArgumentException>(() => new ResourceKey("res://assets/hero.png"));
        Assert.Throws<ArgumentException>(() => new ResourceKey("characters/../secret"));
    }

    [Fact]
    public void ProviderRegistryOrdersByPriorityAndRejectsDuplicateIds()
    {
        var low = new MemoryProvider("low");
        var high = new MemoryProvider("high");
        var registry = new ResourceProviderRegistry()
            .Register(low, priority: -1)
            .Register(high, priority: 10);

        Assert.Equal([high, low], registry.Providers);
        Assert.Throws<DuplicateResourceProviderException>(() => registry.Register(high));
        Assert.Same(
            low,
            registry.Register(low, priority: 20, replace: true).Providers[0]);
    }

    [Fact]
    public void GodotProviderAcceptsCustomResourcePathResolver()
    {
        var resolver = new TestPathResolver();
        var provider = new GodotProjectResourceProvider(resolver, new UnusedFrameClock());
        var request = new ResourceRequest(
            new ResourceKey("characters/hero/calm"),
            typeof(TestAsset));

        Assert.Equal(
            ["res://my-layout/TestAsset/characters/hero/calm.asset"],
            provider.GetCandidatePaths(request));
        Assert.Same(request, resolver.LastRequest);
    }

    [Fact(Timeout = 5_000)]
    public async Task ScopedLoaderUsesPriorityExactOverridesAndCache()
    {
        using var lifetime = new TestRuntimeLifetime();
        var lowAsset = new TestAsset("low");
        var highAsset = new TestAsset("high");
        var overrideAsset = new TestAsset("override");
        var low = new MemoryProvider("low")
            .Add("low://characters/hero/calm", lowAsset)
            .Add("res://special/hero.png", overrideAsset);
        var high = new MemoryProvider("high")
            .Add("high://characters/hero/calm", highAsset);
        var manager = new ResourceManager(
            new ResourceProviderRegistry()
                .Register(low)
                .Register(high, priority: 1)
                .Providers,
            lifetime);
        await manager.InitializeAsync(CancellationToken.None);
        var loader = manager.CreateLoader<TestAsset>("characters/hero");

        Assert.Same(highAsset, loader.Load("calm"));
        Assert.Same(highAsset, loader.Load("calm"));
        Assert.Equal(1, high.SyncLoadCount);
        Assert.Same(overrideAsset, loader.Load("calm", "res://special/hero.png"));
        Assert.Equal(2, manager.LoadedResourceCount);

        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ConcurrentAsyncLoadsShareOneProviderOperation()
    {
        using var lifetime = new TestRuntimeLifetime();
        var asset = new TestAsset("shared");
        var provider = new MemoryProvider("memory")
            .Add("memory://scripts/main", asset)
            .PauseAsyncLoads();
        var manager = new ResourceManager([provider], lifetime);
        await manager.InitializeAsync(CancellationToken.None);

        var first = manager.LoadAsync<TestAsset>(new ResourceKey("scripts/main"));
        var second = manager.LoadAsync<TestAsset>(new ResourceKey("scripts/main"));
        Assert.Equal(1, provider.AsyncLoadCount);
        provider.ReleaseAsyncLoads();

        Assert.Same(asset, await first);
        Assert.Same(asset, await second);
        Assert.Equal(1, provider.AsyncLoadCount);
        Assert.Equal(1, manager.LoadedResourceCount);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task SessionReplacementDoesNotCancelSubsequentResourceLoads()
    {
        using var lifetime = new TestRuntimeLifetime();
        var asset = new TestAsset("new-session");
        var provider = new MemoryProvider("memory")
            .Add("memory://audio/bgm", asset);
        var manager = new ResourceManager([provider], lifetime);
        await manager.InitializeAsync(CancellationToken.None);

        lifetime.BeginSession();

        var loaded = await manager.LoadAsync<TestAsset>(new ResourceKey("audio/bgm"));
        Assert.Same(asset, loaded);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ConcurrentLeasesDeduplicateAndCallerCancellationIsIndependent()
    {
        using var lifetime = new TestRuntimeLifetime();
        var asset = new TestAsset("leased");
        var provider = new MemoryProvider("memory")
            .Add("memory://characters/hero/calm", asset)
            .PauseAsyncLoads();
        var manager = new ResourceManager([provider], lifetime);
        await manager.InitializeAsync(CancellationToken.None);

        var first = manager.LeaseAsync<TestAsset>(
            new ResourceKey("characters/hero/calm"), estimatedBytes: 64);
        var second = manager.LeaseAsync<TestAsset>(
            new ResourceKey("characters/hero/calm"), estimatedBytes: 64);
        first.Cancel();
        provider.ReleaseAsyncLoads();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Completion);
        await second.Completion;
        Assert.Equal(ResourceLeaseState.Cancelled, first.State);
        Assert.Equal(ResourceLeaseState.Ready, second.State);
        Assert.Same(asset, second.Resource);
        Assert.Equal(1, provider.AsyncLoadCount);
        Assert.Equal(1, manager.CaptureCacheSnapshot().ActiveLeaseCount);

        second.Release();
        Assert.Equal(0, manager.CaptureCacheSnapshot().ActiveLeaseCount);
        Assert.Equal(1, manager.TrimCache());
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task BudgetEvictsOnlyUnleasedEntriesAndRetainsPinnedGroups()
    {
        using var lifetime = new TestRuntimeLifetime();
        var provider = new MemoryProvider("memory")
            .Add("memory://backgrounds/a", new TestAsset("a"))
            .Add("memory://backgrounds/b", new TestAsset("b"));
        var manager = new ResourceManager(
            [provider], lifetime, memoryBudgetBytes: 100);
        await manager.InitializeAsync(CancellationToken.None);

        var pinned = manager.PreloadAsync(new ResourcePreloadManifest(
            "chapter",
            [ResourcePreloadEntry.Create<TestAsset>(
                new ResourceKey("backgrounds/a"), estimatedBytes: 80)],
            pinned: true));
        await pinned.Completion;
        var transient = manager.LeaseAsync<TestAsset>(
            new ResourceKey("backgrounds/b"), estimatedBytes: 80);
        await transient.Completion;

        Assert.True(manager.CaptureCacheSnapshot().IsOverBudget);
        transient.Release();
        var pressured = manager.CaptureCacheSnapshot();
        Assert.False(pressured.IsOverBudget);
        Assert.Single(pressured.Entries);
        Assert.Equal("memory://backgrounds/a", pressured.Entries[0].ResolvedPath);
        Assert.Equal(1, pressured.Entries[0].PinnedLeaseCount);

        pinned.Release();
        Assert.Equal(1, manager.TrimCache());
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task PreloadManifestResolvesLocaleFallbackBeforeFlatResource()
    {
        using var lifetime = new TestRuntimeLifetime();
        var localized = new TestAsset("zh");
        var provider = new MemoryProvider("memory")
            .Add("memory://voice/zh/line-1", localized)
            .Add("memory://voice/line-1", new TestAsset("flat"));
        var manager = new ResourceManager([provider], lifetime);
        await manager.InitializeAsync(CancellationToken.None);
        var manifest = new ResourcePreloadManifest(
            "localized",
            [ResourcePreloadEntry.Create<TestAsset>(
                new ResourceKey("voice/line-1"),
                localizedRoot: "voice",
                localeChannel: ResourceLocaleChannel.Voice)]);

        var group = manager.PreloadAsync(manifest, new ResourcePreloadContext(
            "en",
            "zh-CN",
            ["en"],
            ["zh-CN", "zh", "en"]));
        await group.Completion;

        Assert.Same(localized, group.Resources[0].Resource);
        Assert.Equal("memory://voice/zh/line-1", group.Resources[0].ResolvedPath);
        group.Release();
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ResetClearsCacheAndMissingErrorsListCandidates()
    {
        using var lifetime = new TestRuntimeLifetime();
        var asset = new TestAsset("cached");
        var provider = new MemoryProvider("memory")
            .Add("memory://characters/hero/calm", asset);
        var manager = new ResourceManager([provider], lifetime);
        await manager.InitializeAsync(CancellationToken.None);

        Assert.Same(asset, manager.Load<TestAsset>(new ResourceKey("characters/hero/calm")));
        await manager.ResetAsync(CancellationToken.None);
        Assert.Equal(0, manager.LoadedResourceCount);
        Assert.Same(asset, manager.Load<TestAsset>(new ResourceKey("characters/hero/calm")));
        Assert.Equal(2, provider.SyncLoadCount);

        var exception = Assert.Throws<ResourceNotFoundException>(() =>
            manager.Load<TestAsset>(new ResourceKey("characters/missing/default")));
        Assert.Contains(
            "memory://characters/missing/default",
            exception.Candidates);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ManagerRejectsProviderTypeMismatches()
    {
        using var lifetime = new TestRuntimeLifetime();
        var provider = new MemoryProvider("memory")
            .Add("memory://scripts/main", new object());
        var manager = new ResourceManager([provider], lifetime);
        await manager.InitializeAsync(CancellationToken.None);

        Assert.Throws<ResourceTypeMismatchException>(() =>
            manager.Load<TestAsset>(new ResourceKey("scripts/main")));
        await Assert.ThrowsAsync<ResourceTypeMismatchException>(() =>
            manager.LoadAsync<TestAsset>(new ResourceKey("scripts/main")));
        Assert.Equal(0, manager.LoadedResourceCount);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    private sealed record TestAsset(string Name);

    private sealed class TestPathResolver : IResourcePathResolver
    {
        public ResourceRequest? LastRequest { get; private set; }

        public IReadOnlyList<string> GetCandidatePaths(ResourceRequest request)
        {
            LastRequest = request;
            return [$"res://my-layout/{request.ResourceType.Name}/{request.Key.Path}.asset"];
        }
    }

    private sealed class UnusedFrameClock : IFrameClock
    {
        public Task NextFrameAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemoryProvider : IResourceProvider
    {
        private readonly Dictionary<string, object> resources = new(StringComparer.Ordinal);
        private TaskCompletionSource? asyncRelease;
        private bool isInitialized;

        public MemoryProvider(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public int SyncLoadCount { get; private set; }

        public int AsyncLoadCount { get; private set; }

        public MemoryProvider Add(string path, object resource)
        {
            resources.Add(path, resource);
            return this;
        }

        public MemoryProvider PauseAsyncLoads()
        {
            asyncRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return this;
        }

        public void ReleaseAsyncLoads() => asyncRelease?.TrySetResult();

        public IReadOnlyList<string> GetCandidatePaths(ResourceRequest request) =>
            [request.OverridePath ?? $"{Id}://{request.Key.Path}"];

        public bool TryResolve(ResourceRequest request, out ResourceLocation? location)
        {
            EnsureInitialized();
            var candidate = GetCandidatePaths(request)[0];
            location = resources.ContainsKey(candidate)
                ? new ResourceLocation(Id, candidate)
                : null;
            return location is not null;
        }

        public object Load(ResourceLocation location, Type resourceType)
        {
            EnsureInitialized();
            SyncLoadCount++;
            return resources[location.Path];
        }

        public async Task<object> LoadAsync(
            ResourceLocation location,
            Type resourceType,
            CancellationToken cancellationToken)
        {
            EnsureInitialized();
            AsyncLoadCount++;
            if (asyncRelease is not null)
            {
                await asyncRelease.Task.WaitAsync(cancellationToken);
            }

            return resources[location.Path];
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            isInitialized = true;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            isInitialized = false;
            return Task.CompletedTask;
        }

        private void EnsureInitialized()
        {
            if (!isInitialized)
            {
                throw new InvalidOperationException("Memory provider is not initialized.");
            }
        }
    }

    private sealed class TestRuntimeLifetime : IRuntimeLifetime, IDisposable
    {
        private readonly CancellationTokenSource runtime = new();
        private CancellationTokenSource session = new();

        public CancellationToken RuntimeCancellationToken => runtime.Token;

        public CancellationToken SessionCancellationToken => session.Token;

        public void BeginSession()
        {
            var previous = session;
            session = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
        }

        public void Dispose()
        {
            runtime.Dispose();
            session.Dispose();
        }
    }
}
