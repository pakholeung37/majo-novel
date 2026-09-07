using System.Threading.Channels;
using Majo.Runtime.Preloading;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ResourceLookAheadWindowTests
{
    [Fact(Timeout = 5000)]
    public async Task RepeatedOversizedSetWarnsOnceButNewBudgetAndResourcesRemainVisible()
    {
        var resources = new FakeResources { AutoComplete = true };
        var logger = new CaptureLogger();
        using var window = new ResourceLookAheadWindow(resources, logger);
        for (var i = 0; i < 25; i++)
            await window.MoveAsync(new[] { Entry("ema-atlas", 341) }, Array.Empty<ResourcePreloadEntry>(), 96, 12);
        Assert.Single(logger.Warnings);
        Assert.Contains("ema-atlas", logger.Warnings[0].Message);
        Assert.Contains("not measured", logger.Warnings[0].Message);
        Assert.Single(resources.Loaded);
        await window.MoveAsync(new[] { Entry("ema-atlas", 341) }, Array.Empty<ResourcePreloadEntry>(), 128, 12);
        await window.MoveAsync(new[] { Entry("other-atlas", 341) }, Array.Empty<ResourcePreloadEntry>(), 128, 12);
        Assert.Equal(3, logger.Warnings.Count);
    }

    private sealed class CaptureLogger : IRuntimeLogger
    {
        public List<RuntimeLogEvent> Warnings { get; } = new();
        public bool IsEnabled(string category, RuntimeLogLevel level) => true;
        public void Log(RuntimeLogEvent logEvent)
        {
            if (logEvent.Level == RuntimeLogLevel.Warning) Warnings.Add(logEvent);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task BudgetAndCountLimitSpeculationButRequiredResourceIsAlwaysPrepared()
    {
        var resources = new FakeResources { AutoComplete = true };
        using var window = new ResourceLookAheadWindow(resources);
        await window.MoveAsync(new[] { Entry("required", 20) },
            new[] { Entry("large", 90), Entry("small", 30), Entry("extra", 10) }, 80, 2,
            waitForAhead: true);
        Assert.Equal(new[] { "required", "small" }, resources.Loaded.Select(item => item.GroupId));
        await window.MoveAsync(new[] { Entry("oversized", 200) }, new[] { Entry("extra", 10) }, 80, 2,
            waitForAhead: true);
        Assert.Equal("oversized", resources.Loaded.Last().GroupId);
        Assert.All(resources.Loaded.Take(2), lease => Assert.True(lease.Released));
    }

    [Fact(Timeout = 5000)]
    public async Task SharedResourceIsRetainedAndExpiredResourceReleased()
    {
        var resources = new FakeResources { AutoComplete = true };
        using var window = new ResourceLookAheadWindow(resources);
        var shared = Entry("shared", 10);
        await window.MoveAsync(new[] { shared }, new[] { Entry("old", 10) }, 100, 10, waitForAhead: true);
        await window.MoveAsync(new[] { shared }, new[] { Entry("new", 10) }, 100, 10, waitForAhead: true);
        Assert.Equal(3, resources.Loaded.Count);
        Assert.False(resources.Loaded[0].Released);
        Assert.True(resources.Loaded[1].Released);
    }

    [Fact(Timeout = 5000)]
    public async Task ReplacingWindowWaitsForActualProviderCompletionAndDiscardsOldQueue()
    {
        var resources = new FakeResources();
        using var window = new ResourceLookAheadWindow(resources);
        await window.MoveAsync(Array.Empty<ResourcePreloadEntry>(), new[] { Entry("old", 10), Entry("never", 10) }, 100, 10);
        var old = await resources.Started.Reader.ReadAsync();
        var move = window.MoveAsync(new[] { Entry("current", 10) }, Array.Empty<ResourcePreloadEntry>(), 100, 10);
        Assert.False(move.IsCompleted);
        Assert.Single(resources.Loaded);
        old.Complete();
        var current = await resources.Started.Reader.ReadAsync();
        Assert.Equal("current", current.GroupId);
        Assert.True(old.Released);
        current.Complete();
        await move;
        Assert.Equal(2, resources.Loaded.Count);
    }

    [Fact(Timeout = 5000)]
    public async Task CancellationReturnsPromptlyAndClearReleasesInFlightAcquisition()
    {
        var resources = new FakeResources();
        using var window = new ResourceLookAheadWindow(resources);
        using var cancellation = new CancellationTokenSource();
        var move = window.MoveAsync(new[] { Entry("pending", 10) }, Array.Empty<ResourcePreloadEntry>(), 100, 10, cancellationToken: cancellation.Token);
        var pending = await resources.Started.Reader.ReadAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        window.Clear();
        pending.Complete();
        await pending.ReleaseCompletion.Task;
        Assert.True(pending.Released);
    }

    [Fact(Timeout = 5000)]
    public async Task CancelledOpeningWindowStopsAfterInFlightLoadWithoutStartingTheRest()
    {
        var resources = new FakeResources();
        using var window = new ResourceLookAheadWindow(resources);
        using var cancellation = new CancellationTokenSource();
        var start = window.MoveAsync(Array.Empty<ResourcePreloadEntry>(),
            new[] { Entry("in-flight", 10), Entry("never", 10) }, 100, 10,
            waitForAhead: true, cancellationToken: cancellation.Token);
        var pending = await resources.Started.Reader.ReadAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        pending.Complete();
        await pending.ReleaseCompletion.Task;
        Assert.Single(resources.Loaded);
    }

    [Fact(Timeout = 5000)]
    public async Task FailedRequiredAcquisitionReleasesLeaseAndCanRetry()
    {
        var resources = new FakeResources();
        using var window = new ResourceLookAheadWindow(resources);
        var entry = Entry("retry", 10);
        var move = window.MoveAsync(new[] { entry }, Array.Empty<ResourcePreloadEntry>(), 100, 10);
        var failed = await resources.Started.Reader.ReadAsync();
        failed.Fail();
        await Assert.ThrowsAsync<InvalidOperationException>(() => move);
        Assert.True(failed.Released);
        resources.AutoComplete = true;
        await window.MoveAsync(new[] { entry }, Array.Empty<ResourcePreloadEntry>(), 100, 10);
        Assert.Equal(2, resources.Loaded.Count);
    }

    private static ResourcePreloadEntry Entry(string name, long bytes) =>
        ResourcePreloadEntry.Create<object>(new ResourceKey(name), estimatedBytes: bytes);

    private sealed class FakeResources : IResourceManager
    {
        public bool AutoComplete { get; set; }
        public List<FakeGroup> Loaded { get; } = new();
        public Channel<FakeGroup> Started { get; } = Channel.CreateUnbounded<FakeGroup>();
        public int LoadedResourceCount => Loaded.Count;
        public IResourceGroupLease PreloadAsync(ResourcePreloadManifest manifest, ResourcePreloadContext? context = null,
            CancellationToken cancellationToken = default)
        {
            var group = new FakeGroup(manifest.Entries.Single().Key.Path);
            Loaded.Add(group);
            if (AutoComplete) group.Complete();
            Started.Writer.TryWrite(group);
            return group;
        }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public TResource Load<TResource>(ResourceKey key, string? overridePath = null) where TResource : class => throw new NotSupportedException();
        public Task<TResource> LoadAsync<TResource>(ResourceKey key, string? overridePath = null,
            CancellationToken cancellationToken = default) where TResource : class => throw new NotSupportedException();
        public bool Exists<TResource>(ResourceKey key, string? overridePath = null) where TResource : class => true;
        public IResourceLoader<TResource> CreateLoader<TResource>(string pathPrefix) where TResource : class => throw new NotSupportedException();
    }

    private sealed class FakeGroup(string id) : IResourceGroupLease
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released { get; private set; }
        public string LeaseId => GroupId;
        public string GroupId => id;
        public bool IsPinned => false;
        public ResourceLeaseState State => Released ? ResourceLeaseState.Released : ResourceLeaseState.Loading;
        public double Progress => ready.Task.IsCompletedSuccessfully ? 1 : 0;
        public IReadOnlyList<IResourceLease> Resources => Array.Empty<IResourceLease>();
        public Task Completion => ready.Task;
        public void Complete() => ready.TrySetResult();
        public void Fail() => ready.TrySetException(new InvalidOperationException("load failed"));
        public void Cancel() => ready.TrySetCanceled();
        public void Release() { Released = true; ReleaseCompletion.TrySetResult(); }
        public void Dispose() => Release();
    }
}
