#nullable enable

using System.IO;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Resources.Godot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateRepeatedWallAsync()
    {
        Assert(DisplayServer.GetName() != "headless", "Repeated wall pixel tests require a real renderer.");
        using var tileImage = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                tileImage.SetPixel(x, y, new Color(x / 7f, y / 7f, (x + y) % 2, 1));
        using var tile = ImageTexture.CreateFromImage(tileImage);
        using var repeated = new HorizontalRepeatTexture2D(tile, new Vector2I(8, 8), 3);
        using var referenceImage = Image.CreateEmpty(24, 8, false, Image.Format.Rgba8);
        for (var i = 0; i < 3; i++)
            referenceImage.BlitRect(tileImage, new Rect2I(0, 0, 8, 8), new Vector2I(i * 8, 0));
        using var reference = ImageTexture.CreateFromImage(referenceImage);
        Assert(repeated.GetSize() == new Vector2(24, 8), "Repeated wall changed its logical extent.");
        Assert(repeated.GetRid() == tile.GetRid(), "Repeated wall allocated a separate texture RID.");

        var actualViewport = new SubViewport
        {
            Size = new Vector2I(128, 96),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var referenceViewport = new SubViewport
        {
            Size = actualViewport.Size,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(actualViewport);
        AddChild(referenceViewport);
        static TextureRect Display(Texture2D texture) => new()
        {
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            Position = new Vector2(30.25f, 25.5f),
            Size = new Vector2(72, 24),
        };
        var actual = Display(repeated);
        var expected = Display(reference);
        actualViewport.AddChild(actual);
        referenceViewport.AddChild(expected);
        var evidence = ProjectSettings.GlobalizePath("res://build/wall-repetition-evidence");
        Directory.CreateDirectory(evidence);
        try
        {
            foreach (var mode in new[] { TextureRect.StretchModeEnum.Scale, TextureRect.StretchModeEnum.KeepAspectCovered, TextureRect.StretchModeEnum.Tile })
            {
                actual.StretchMode = expected.StretchMode = mode;
                actual.Size = expected.Size = mode == TextureRect.StretchModeEnum.KeepAspectCovered
                    ? new Vector2(60, 40) : new Vector2(72, 24);
                foreach (var angle in new[] { 0f, -8f, 15f })
                {
                    actual.RotationDegrees = expected.RotationDegrees = angle;
                    foreach (var flip in new[] { false, true })
                    {
                        actual.FlipH = expected.FlipH = flip;
                        actual.FlipV = expected.FlipV = flip;
                        using var a = await ReadTransitionFrameAsync(actualViewport);
                        using var b = await ReadTransitionFrameAsync(referenceViewport);
                        AssertLayeredImagesClose(b, a, $"Repeated wall {mode}/{angle}/{flip}");
                        if (mode == TextureRect.StretchModeEnum.Scale && angle == 0 && !flip)
                        {
                            for (var x = 32; x < 100; x++)
                                Assert(a.GetPixel(x, 36).A > .99f, "Repeated panel seams exposed the clear color.");
                            a.SavePng(Path.Combine(evidence, "repeated.png"));
                            b.SavePng(Path.Combine(evidence, "reference.png"));
                        }
                    }
                }
            }
            // A logical tile can be smaller than its resident source without
            // allocating a resized image (the court uses 2048 -> 1024).
            using var scaled = new HorizontalRepeatTexture2D(tile, new Vector2I(4, 4), 15);
            var sampler = new Majo.Runtime.UI.Diagnostics.ResourceMemorySampler();
            using var wide = new HorizontalRepeatTexture2D(tile, new Vector2I(1024, 1024), 15);
            var sampled = sampler.Sample(new[]
            {
                new Majo.Runtime.Resources.ResourceDiagnosticEntry(wide, 1, 0),
                new Majo.Runtime.Resources.ResourceDiagnosticEntry(tile, 1, 0),
            });
            Assert(sampled.Contains("Tex ≈   0M", System.StringComparison.Ordinal),
                "Resource diagnostics must count shared source pixels, not the virtual panorama.");
            Assert(scaled.GetSize() == new Vector2(60, 4) && scaled.GetRid() == tile.GetRid(),
                "Logical tile scaling must preserve the original backing texture.");
        }
        finally { actualViewport.QueueFree(); referenceViewport.QueueFree(); }
        GD.Print($"Repeated wall pixel regressions passed. Evidence: {evidence}");
    }
}
