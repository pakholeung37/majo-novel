using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class BackgroundContentTests
{
    [Fact]
    public void ClosingOwnerRejectsNewLeasesButPreservesExistingConsumers()
    {
        var releases = 0;
        using var lifetime = new BackgroundContentLifetime(() => releases++);
        var first = lifetime.Retain();
        var second = lifetime.Retain();
        lifetime.Dispose();
        lifetime.Dispose();
        Assert.Equal(0, releases);
        Assert.Throws<ObjectDisposedException>(() => lifetime.Retain());
        first.Dispose();
        first.Dispose();
        Assert.Equal(0, releases);
        second.Dispose();
        second.Dispose();
        Assert.Equal(1, releases);
    }

    [Fact]
    public void ConcurrentLeaseReleaseRunsCleanupOnce()
    {
        var releases = 0;
        using var lifetime = new BackgroundContentLifetime(() => Interlocked.Increment(ref releases));
        var leases = Enumerable.Range(0, 100).Select(_ => lifetime.Retain()).ToArray();
        lifetime.Dispose();
        Parallel.ForEach(leases, lease => { lease.Dispose(); lease.Dispose(); });
        Assert.Equal(1, releases);
    }

    [Fact]
    public void ThrowingCleanupIsNotRetried()
    {
        var calls = 0;
        var lifetime = new BackgroundContentLifetime(() => { calls++; throw new InvalidOperationException(); });
        Assert.Throws<InvalidOperationException>(lifetime.Dispose);
        lifetime.Dispose();
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(BackdropFit.Cover, -100, 0, 400, 200)]
    [InlineData(BackdropFit.Contain, 0, 50, 200, 100)]
    [InlineData(BackdropFit.Stretch, 0, 0, 200, 200)]
    public void FitUsesNativeBoundsIncludingNegativeOrigin(BackdropFit fit, float x, float y, float w, float h)
    {
        var bounds = new Rect2(-30, 15, 100, 50);
        var transform = DirectionLayoutCalculator.CalculateBackgroundTransform(new Vector2(200, 200), bounds, fit);
        var mapped = transform * bounds;
        Assert.True(mapped.IsEqualApprox(new Rect2(x, y, w, h)), mapped.ToString());
    }

    [Fact]
    public void ExplicitPlacementAnchorsNativeBoundsInStageCoordinates()
    {
        var bounds = new Rect2(-20, -10, 100, 50);
        var placement = new BackdropPlacement(new DirectionPoint(25, 75), new DirectionPoint(.5f, 1), 2);
        var transform = DirectionLayoutCalculator.CalculateBackgroundTransform(new Vector2(800, 600), bounds,
            BackdropFit.Cover, placement);
        Assert.Equal(new Vector2(200, 150), transform * (bounds.Position + bounds.Size * new Vector2(.5f, 1)));
        Assert.Equal(new Rect2(100, 50, 200, 100), transform * bounds);
    }

    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, -1)]
    [InlineData(float.NaN, 0, 1, 1)]
    [InlineData(0, float.PositiveInfinity, 1, 1)]
    [InlineData(0, 0, float.PositiveInfinity, 1)]
    [InlineData(float.MaxValue, 0, float.MaxValue, 1)]
    public void InvalidNativeBoundsFailBeforeContentIsMounted(float x, float y, float w, float h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GodotBackgroundContent.ValidateBounds(new Rect2(x, y, w, h)));
}
