using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Resources.Godot;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class GodotResourceProviderTests
{

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
}
