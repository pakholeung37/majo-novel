#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;

namespace Majo.Runtime.RenderingTests;

public partial class ResolutionProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            await RunAsync();
            GD.Print("RESOLUTION_OK: native raster size, stable layout, live source resize, mipmaps.");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private async Task FramesAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }

    private async Task RunAsync()
    {
        var window = GetWindow();
        window.ContentScaleSize = new Vector2I(640, 360);
        window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        window.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
        window.Size = new Vector2I(640, 360);
        await FramesAsync();
        using var host = new GodotDirectionRenderHost(this, new Vector2I(640, 360));
        await host.InitializeAsync(CancellationToken.None);
        var marker = new ColorRect { Position = new Vector2(190, 90), Size = new Vector2(20, 20), Color = Colors.Red };
        host.Actors.AddChild(marker);
        GodotDirectionRenderHost.AssignVisibilityLayer(marker, GodotDirectionRenderHost.SubjectVisibilityLayer);
        var source = host.CreateLiveViewSource(ViewId.Main, host.PublishedView!);
        foreach (var size in new[] { new Vector2I(1280, 720), new Vector2I(960, 720), new Vector2I(640, 360) })
        {
            window.Size = size;
            await FramesAsync();
            var content = host.RequireView(ViewId.Main).ContentViewport;
            Require(host.StageViewport.Size == new Vector2I(640, 360), "Logical Stage changed size.");
            Require(host.ScreenViewport.Size == window.Size, "Screen is not rendering at window resolution.");
            Require(content.Size.Y == window.Size.Y, "View density does not match window.");
            Require(source.ContentViewport.Size == content.Size, "Live source failed to resize.");
            Require(host.ViewTransform.Transform.IsEqualApprox(Transform2D.Identity), "Logical camera pose changed.");
            var point = content.CanvasTransform * new Vector2(200, 100);
            using var image = content.GetTexture().GetImage();
            Require(image.GetPixel((int)point.X, (int)point.Y).R > 0.9f, "Actor moved relative to its camera.");
            GD.Print($"RESOLUTION_SAMPLE: window={window.Size} stage={host.StageViewport.Size} view={content.Size}");
        }
        host.ReleaseLiveViewSource(source);
        var texture = GD.Load<Texture2D>("res://demos/live2d/assets/characters/mao/runtime/mao_pro.4096/texture_00.png");
        using var textureImage = texture.GetImage();
        Require(textureImage.HasMipmaps(), "Live2D texture was imported without mipmaps.");
        await host.ShutdownAsync(CancellationToken.None);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
