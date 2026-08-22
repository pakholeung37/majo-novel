using Majo.Runtime.Core;
using Majo.Runtime.Resources;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ResourceManagerTests
{
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
            .Add("memory://dialogue/main", asset)
            .PauseAsyncLoads();
        var manager = new ResourceManager([provider], lifetime);
        await manager.InitializeAsync(CancellationToken.None);

        var first = manager.LoadAsync<TestAsset>(new ResourceKey("dialogue/main"));
        var second = manager.LoadAsync<TestAsset>(new ResourceKey("dialogue/main"));
        Assert.Equal(1, provider.AsyncLoadCount);
        provider.ReleaseAsyncLoads();

        Assert.Same(asset, await first);
        Assert.Same(asset, await second);
        Assert.Equal(1, provider.AsyncLoadCount);
        Assert.Equal(1, manager.LoadedResourceCount);
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
            .Add("memory://dialogue/main", new object());
        var manager = new ResourceManager([provider], lifetime);
        await manager.InitializeAsync(CancellationToken.None);

        Assert.Throws<ResourceTypeMismatchException>(() =>
            manager.Load<TestAsset>(new ResourceKey("dialogue/main")));
        await Assert.ThrowsAsync<ResourceTypeMismatchException>(() =>
            manager.LoadAsync<TestAsset>(new ResourceKey("dialogue/main")));
        Assert.Equal(0, manager.LoadedResourceCount);
        await manager.ShutdownAsync(CancellationToken.None);
    }

    private sealed record TestAsset(string Name);

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
        private readonly CancellationTokenSource session = new();

        public CancellationToken RuntimeCancellationToken => runtime.Token;

        public CancellationToken SessionCancellationToken => session.Token;

        public void Dispose()
        {
            runtime.Dispose();
            session.Dispose();
        }
    }
}
