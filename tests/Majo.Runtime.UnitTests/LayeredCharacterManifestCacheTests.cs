using Majo.Demos.Manosaba.Actors.Layered;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class LayeredCharacterManifestCacheTests
{
    [Fact]
    public async Task SharedLoadsSurviveCallerCancellationAndSceneReset()
    {
        var count = 0;
        var loaded = new TaskCompletionSource<LayeredCharacterManifest>();
        var cache = new LayeredCharacterManifestCache(_ => { count++; return loaded.Task; });
        using var cancellation = new CancellationTokenSource();
        var first = cache.GetAsync("Ema", cancellation.Token);
        var second = cache.GetAsync("Ema", default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var manifest = new LayeredCharacterManifest();
        loaded.SetResult(manifest);
        Assert.Same(manifest, await second);
        await cache.ResetAsync(default);
        Assert.Same(manifest, cache.Get("Ema"));
        Assert.Equal(1, count);
        await cache.ShutdownAsync(default);
        Assert.Same(manifest, await cache.GetAsync("Ema", default));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task FailedMetadataLoadCanBeRetried()
    {
        var count = 0;
        var cache = new LayeredCharacterManifestCache(_ => ++count == 1
            ? Task.FromException<LayeredCharacterManifest>(new IOException("load failed"))
            : Task.FromResult(new LayeredCharacterManifest()));
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync("Ema", default));
        Assert.NotNull(await cache.GetAsync("Ema", default));
        Assert.Equal(2, count);
    }
}
