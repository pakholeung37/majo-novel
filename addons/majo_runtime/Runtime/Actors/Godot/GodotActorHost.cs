using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;

namespace Majo.Runtime.Actors.Godot;

internal sealed class GodotActorHost : IRuntimeService
{
    private readonly Node owner;
    private readonly Vector2I referenceResolution;
    private bool isInitialized;

    public GodotActorHost(Node owner, ActorConfiguration configuration)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(configuration);
        referenceResolution = configuration.ReferenceResolution;
        StageViewport = new SubViewport
        {
            Name = "MajoStageViewport",
            Disable3D = true,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            HandleInputLocally = false,
            GuiDisableInput = true,
        };
        StageBackdropLayer = new CanvasLayer
        {
            Name = "MajoStageBackdrop",
            Layer = -100,
        };
        StageBackdrop = new ColorRect
        {
            Name = "Black",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        StageBackdropLayer.AddChild(StageBackdrop);
        Root = new CanvasLayer { Name = "MajoActors" };
        BackgroundLayer = new Control
        {
            Name = "Backgrounds",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        CharacterLayer = new Node2D { Name = "Characters" };
        Root.AddChild(BackgroundLayer);
        Root.AddChild(CharacterLayer);
        StageViewport.AddChild(StageBackdropLayer);
        StageViewport.AddChild(Root);

        OutputLayer = new CanvasLayer { Name = "MajoCameraOutput" };
        LiveOutput = CreateOutputView("LiveOutput");
        TransitionSource = CreateOutputView("TransitionSource");
        TransitionSource.Visible = false;
        OutputLayer.AddChild(LiveOutput);
        OutputLayer.AddChild(TransitionSource);
    }

    public SubViewport StageViewport { get; }

    public CanvasLayer StageBackdropLayer { get; }

    public ColorRect StageBackdrop { get; }

    public CanvasLayer Root { get; }

    public CanvasLayer OutputLayer { get; }

    public TextureRect LiveOutput { get; }

    public TextureRect TransitionSource { get; }

    public Control BackgroundLayer { get; }

    public Node2D CharacterLayer { get; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        owner.AddChild(StageViewport);
        owner.AddChild(OutputLayer);
        ResizeOutput();
        LiveOutput.Texture = StageViewport.GetTexture();
        StageBackdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        BackgroundLayer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        owner.GetViewport().SizeChanged += ResizeOutput;
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized && GodotObject.IsInstanceValid(owner))
        {
            owner.GetViewport().SizeChanged -= ResizeOutput;
        }

        if (isInitialized && GodotObject.IsInstanceValid(OutputLayer))
        {
            OutputLayer.QueueFree();
        }

        if (isInitialized && GodotObject.IsInstanceValid(StageViewport))
        {
            StageViewport.QueueFree();
        }

        isInitialized = false;
        return Task.CompletedTask;
    }

    private void ResizeOutput()
    {
        var visibleSize = owner.GetViewport().GetVisibleRect().Size;
        StageViewport.Size = referenceResolution.X > 0 && referenceResolution.Y > 0
            ? referenceResolution
            : new Vector2I(
                Math.Max(1, Mathf.RoundToInt(visibleSize.X)),
                Math.Max(1, Mathf.RoundToInt(visibleSize.Y)));
        LiveOutput.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        TransitionSource.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    private static TextureRect CreateOutputView(string name) => new()
    {
        Name = name,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.Scale,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };
}
