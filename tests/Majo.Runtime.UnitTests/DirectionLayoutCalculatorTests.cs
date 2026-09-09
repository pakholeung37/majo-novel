#nullable enable

using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionLayoutCalculatorTests
{
    [Fact]
    public void CalculateSlotRect_DefaultLayout_ReturnsFullViewport()
    {
        var viewport = new Vector2I(1920, 1080);
        var rect = DirectionLayoutCalculator.CalculateSlotRect(new LayoutId("single"), new LayoutSlotId("main"), viewport);

        Assert.Equal(Vector2.Zero, rect.Position);
        Assert.Equal(new Vector2(1920, 1080), rect.Size);
    }

    [Fact]
    public void CalculateSlotRect_SplitLayout_DividesScreenHorizontally()
    {
        var viewport = new Vector2I(1920, 1080);
        var left = DirectionLayoutCalculator.CalculateSlotRect(new LayoutId("split"), new LayoutSlotId("main"), viewport);
        var right = DirectionLayoutCalculator.CalculateSlotRect(new LayoutId("split"), new LayoutSlotId("inset"), viewport);

        Assert.Equal(new Rect2(0, 0, 960, 1080), left);
        Assert.Equal(new Rect2(960, 0, 960, 1080), right);
    }

    [Fact]
    public void CalculateSlotRect_PipLayout_InsetsCorrectly()
    {
        var viewport = new Vector2I(1000, 1000);
        var inset = DirectionLayoutCalculator.CalculateSlotRect(new LayoutId("pip"), new LayoutSlotId("inset"), viewport);

        Assert.Equal(new Rect2(700, 60, 250, 250), inset);
    }

    [Fact]
    public void CalculateBackdropBounds_NullPlacement_ReturnsFullViewport()
    {
        var viewport = new Vector2I(1920, 1080);
        var bounds = DirectionLayoutCalculator.CalculateBackdropBounds(viewport, null, new Vector2(500, 500));

        Assert.Equal(new Rect2(0, 0, 1920, 1080), bounds);
    }

    [Fact]
    public void CalculateBackdropBounds_WithPlacement_ScalesAndOffsetsCorrectly()
    {
        var viewport = new Vector2I(1920, 1080);
        var placement = new BackdropPlacement(
            At: new DirectionPoint(50f, 50f),
            Origin: new DirectionPoint(0.5f, 0.5f),
            Scale: 1.0f);
        var bounds = DirectionLayoutCalculator.CalculateBackdropBounds(viewport, placement, new Vector2(800, 600));

        Assert.Equal(new Vector2(800, 600), bounds.Size);
        Assert.Equal(new Vector2(1920 / 2 - 400, 1080 / 2 - 300), bounds.Position);
    }

    [Fact]
    public void ResolveBackdropStretchMode_MapsCorrectly()
    {
        Assert.Equal(TextureRect.StretchModeEnum.KeepAspectCovered, DirectionLayoutCalculator.ResolveBackdropStretchMode(BackdropFit.Cover));
        Assert.Equal(TextureRect.StretchModeEnum.KeepAspectCentered, DirectionLayoutCalculator.ResolveBackdropStretchMode(BackdropFit.Contain));
        Assert.Equal(TextureRect.StretchModeEnum.Scale, DirectionLayoutCalculator.ResolveBackdropStretchMode(BackdropFit.Stretch));
    }
}
