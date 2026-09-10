using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class CameraProjectionTests
{
    [Fact]
    public void CourtPullbackShrinksBothPlanesWithoutBackgroundMagnificationReversal()
    {
        var lastSubject = float.PositiveInfinity;
        var lastBackground = float.PositiveInfinity;
        for (var step = 0; step <= 100; step++)
        {
            var t = step / 100f;
            var pose = new ViewPose(new(35.1562f * (1 - t), 71 - 21 * t),
                18f / 23f + (.75f - 18f / 23f) * t, 0)
            { Dolly = 18f / 23f + (2f / 7f - 18f / 23f) * t };
            var projection = CameraProjectionCalculator.Calculate(pose,
                new ViewParallaxState(1, new(35.1562f * (1 - t), 46), new(2, 50)),
                ViewIdleState.Off, 6, Size);
            Assert.True(projection.Subject.M11 <= lastSubject);
            Assert.True(projection.Background.M11 <= lastBackground);
            lastSubject = projection.Subject.M11;
            lastBackground = projection.Background.M11;
        }
        Assert.Equal(1.05f, lastSubject, 4);
        Assert.Equal(.7875f, lastBackground, 4);
    }

    [Fact]
    public void CloseDollyPreservesSubjectFramingWithoutMagnifyingTheWall()
    {
        var pose = new ViewPose(new DirectionPoint(241.406f, 78), 18f / 23f, 0) { Dolly = 18f / 23f };
        var projected = CameraProjectionCalculator.Calculate(pose,
            new ViewParallaxState(1, new(241.406f, 46), new(2, 50)), ViewIdleState.Off, 6, Size);
        Assert.Equal(3.6f, projected.Subject.M11, 4);
        Assert.Equal(.9f, projected.Background.M11, 4);
        Assert.Equal(0.85f, (ViewPose.Default with { Dolly = .85f }).Validate().Dolly);
        Assert.Throws<System.ArgumentOutOfRangeException>(() => (ViewPose.Default with { Dolly = .851f }).Validate());
        Assert.Throws<System.ArgumentOutOfRangeException>(() => (ViewPose.Default with { Dolly = 1 }).Validate());
    }

    private static readonly DirectionPoint Size = new(1280, 720);

    [Fact]
    public void NeutralOrDisabledParallaxMatchesTheLegacyProjection()
    {
        var pose = new ViewPose(new DirectionPoint(35, 65), 1.7f, 12);
        var projection = CameraProjectionCalculator.Calculate(
            pose, ViewParallaxState.Off, ViewIdleState.Off, 6, Size);

        Assert.Equal(projection.Subject, projection.Background);
        Assert.Equal(1, projection.SubjectScale);
        Assert.Equal(1, projection.BackgroundScale);
    }

    [Fact]
    public void DollyBackShrinksSubjectMoreThanFarBackground()
    {
        var pose = ViewPose.Default with { Dolly = -0.25f };
        var projection = CameraProjectionCalculator.Calculate(
            pose,
            new ViewParallaxState(1, new DirectionPoint(50, 50), new DirectionPoint(6, 4)),
            ViewIdleState.Off,
            6,
            Size);

        Assert.Equal(0.8f, projection.SubjectScale, 5);
        Assert.Equal(0.96f, projection.BackgroundScale, 5);
        Assert.True(projection.BackgroundScale > projection.SubjectScale);
    }

    [Fact]
    public void ZoomAloneDoesNotCreateDepthDifference()
    {
        var projection = CameraProjectionCalculator.Calculate(
            ViewPose.Default with { Zoom = 2 },
            new ViewParallaxState(1, new DirectionPoint(50, 50), new DirectionPoint(6, 4)),
            ViewIdleState.Off,
            6,
            Size);

        Assert.Equal(projection.Subject, projection.Background);
    }

    [Fact]
    public void PercentageConfigurationScalesWithReferenceResolution()
    {
        var parallax = new ViewParallaxState(0.5f, new DirectionPoint(50, 50), new DirectionPoint(3, 2));
        var pose = new ViewPose(new DirectionPoint(20, 70), 1, 0) { Dolly = -0.25f };
        var small = CameraProjectionCalculator.Calculate(pose, parallax, ViewIdleState.Off, 6, new(100, 100));
        var large = CameraProjectionCalculator.Calculate(pose, parallax, ViewIdleState.Off, 6, new(200, 200));
        var sample = new DirectionPoint(70, 40);

        var smallPoint = small.Background.Transform(sample);
        var largePoint = large.Background.Transform(new DirectionPoint(sample.X * 2, sample.Y * 2));
        Assert.Equal(smallPoint.X * 2, largePoint.X, 4);
        Assert.Equal(smallPoint.Y * 2, largePoint.Y, 4);
    }

    [Fact]
    public void SameInputsAreHistoryIndependentAndFinite()
    {
        var pose = ViewPose.Default with { Dolly = 0.5f };
        var parallax = new ViewParallaxState(1, new DirectionPoint(20, 80), new DirectionPoint(6, 4));
        var idle = new ViewIdleState(new DirectionPoint(0.15f, 0.08f), 12, 1.25);
        var first = CameraProjectionCalculator.Calculate(pose, parallax, idle, 100, Size, 123.5);
        var second = CameraProjectionCalculator.Calculate(pose, parallax, idle, 100, Size, 123.5);

        Assert.Equal(first, second);
        Assert.True(float.IsFinite(first.Background.TX));
        Assert.True(float.IsFinite(first.Subject.TY));
    }

    [Fact]
    public void CoverageDetectsProjectedEdgesWithoutMutatingProjection()
    {
        var identity = new DirectionAffineTransform(1, 0, 0, 1, 0, 0);
        var covered = CameraCoverageCalculator.Evaluate(
            new DirectionPoint(-10, -10), new DirectionPoint(120, 120), identity,
            new DirectionPoint(100, 100));
        var clipped = CameraCoverageCalculator.Evaluate(
            new DirectionPoint(10, 0), new DirectionPoint(100, 100), identity,
            new DirectionPoint(100, 100));

        Assert.True(covered.IsCovered);
        Assert.False(clipped.IsCovered);
        Assert.True(clipped.UncoveredEdges.HasFlag(CameraCoverageEdges.Left));
    }

    [Fact]
    public void CoverageUsesVisiblePolygonForSplitViews()
    {
        var identity = new DirectionAffineTransform(1, 0, 0, 1, 0, 0);
        var leftHalf = new[]
        {
            new DirectionPoint(0, 0), new DirectionPoint(50, 0),
            new DirectionPoint(50, 100), new DirectionPoint(0, 100),
        };
        var result = CameraCoverageCalculator.Evaluate(
            new DirectionPoint(0, 0), new DirectionPoint(55, 100), identity,
            new DirectionPoint(100, 100), leftHalf);

        Assert.True(result.IsCovered);
    }
}
